using DdoDatApi.Converters;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using VoK.Sdk.Common;

namespace DdoDatApi.Controllers;

/// <summary>
/// 1.7.2 NPC / wearable appearance resolver.
///
/// DDO character records use Appearance_ClassList selectors that point at 0x20
/// AppearanceTable/APR records.  Each selector chooses an AppearanceKey and an
/// intensity/modifier.  The selected APR part can replace RenderMeshes, append
/// material modifiers, or replace a Setup.  This endpoint decodes that structure
/// into a deterministic composition plan that the GLB exporter can apply using
/// RenderMesh.MeshTypeId and MaterialInstance.MaterialTypeId.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class NpcAppearanceController : ControllerBase
{
    static readonly Assembly Sdk = typeof(RenderMesh).Assembly;
    const int MaxNodes = 420;
    const int MaxDepth = 5;

    sealed record RefHit(uint Id, int Offset, string Type, string Dat, string SourcePath, bool Structured);
    sealed record Pending(uint Id, int Depth, uint Parent, string ParentType, string Path, bool Structured);
    sealed record RawRefDto(uint id, string hex, int offset, string hexOffset, string type);

    sealed class SetupInfo
    {
        public uint Id { get; init; }
        public int JointCount { get; init; }
        public uint[] Meshes { get; init; } = Array.Empty<uint>();
        public int Depth { get; init; }
        public string Path { get; init; } = "";
        public bool Structured { get; init; }
        public string ParentType { get; init; } = "";
        public int Score { get; set; }
        public string[] Reasons { get; set; } = Array.Empty<string>();
    }

    sealed class AppearanceSelectionInfo
    {
        public uint AprFile { get; init; }
        public uint Key { get; init; }
        public string KeyName { get; init; } = "";
        public double Modifier { get; init; }
        public string Path { get; init; } = "";
        public int SourceRank { get; init; }
    }

    sealed class AprMaterialMod
    {
        public double Priority { get; init; }
        public uint TemplateDid { get; init; }
        public uint ModifierDid { get; init; }
        public uint MeshType { get; init; }
        public uint MaterialType { get; init; }
    }

    sealed class AprMeshReplacement
    {
        public double Priority { get; init; }
        public uint MeshType { get; init; }
        public uint MeshDid { get; init; }
    }

    sealed class AprSetupReplacement
    {
        public double Priority { get; init; }
        public uint SetupDid { get; init; }
    }

    sealed class AprPart
    {
        public double Intensity { get; init; }
        public List<AprMaterialMod> MaterialMods { get; } = new();
        public List<AprMeshReplacement> MeshReplacements { get; } = new();
        public List<AprSetupReplacement> SetupReplacements { get; } = new();
    }

    sealed class AprModification
    {
        public uint Key { get; init; }
        public List<AprPart> Parts { get; } = new();
    }

    sealed class AprTableData
    {
        public uint Did { get; init; }
        public long Consumed { get; init; }
        public List<AprModification> Modifications { get; } = new();
        public List<(uint Key, int Value)> HashMap { get; } = new();
    }

    sealed class SelectionMatch
    {
        public AppearanceSelectionInfo Selection { get; init; } = new();
        public bool KeyFound { get; init; }
        public bool ExactIntensityMatch { get; init; }
        public List<AprPart> SelectedParts { get; } = new();
    }

    sealed class AprRecordData
    {
        public uint Id { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
        public AprTableData? Table { get; init; }
        public string? Error { get; init; }
        public List<SelectionMatch> SelectionMatches { get; } = new();
    }

    sealed class ResolvedAppearanceData
    {
        public List<SelectionMatch> SelectedParts { get; } = new();
        public List<AprMeshReplacement> MeshReplacements { get; } = new();
        public List<AprMaterialMod> MaterialMods { get; } = new();
        public List<AprSetupReplacement> SetupReplacements { get; } = new();
        public List<string> Errors { get; } = new();
    }

    [HttpGet("{dbId}")]
    public IActionResult Resolve(string dbId, string? baseSetup = null, string? visual = null, string? physObj = null, string? equipment = null)
    {
        if (!TryId(dbId, out var root) || root < 0x78000000 || root > 0x7FFFFFFF)
            return BadRequest("Expected a DbProperties ID in the 0x78xxxxxx-0x7Fxxxxxx range.");

        TryId(baseSetup ?? "", out var selectedSetup);
        TryId(visual ?? "", out var selectedVisual);
        TryId(physObj ?? "", out var selectedPhysObj);

        object? rootProperties = null;
        JsonElement? rootPropertyJson = null;
        try
        {
            rootProperties = DatSource.PropertyMaster.GetPropertyCollection(root);
            if (rootProperties != null)
                rootPropertyJson = SerializeWithPropertyConverter(rootProperties);
        }
        catch { }

        // Prefer the exact PhysObj resolved by the desktop catalog.  If it was not supplied,
        // recover it from the parsed DbProperties property collection by name.
        if (selectedPhysObj == 0 && rootPropertyJson.HasValue)
            selectedPhysObj = FindFirstNamedId(rootPropertyJson.Value, "PhysObj", 0x47000000, 0x47FFFFFF);

        JsonElement? entityJson = null;
        object? entitySummary = null;
        if (selectedPhysObj != 0)
        {
            try
            {
                var entity = EntityDesc.Load(DatSource.GameLogicDat, selectedPhysObj, DatSource.PropertyMaster);
                if (entity != null)
                {
                    entityJson = SerializeWithPropertyConverter(entity);
                    entitySummary = new
                    {
                        id = selectedPhysObj,
                        hex = $"0x{selectedPhysObj:X8}",
                        visualDescId = FindAnyIdByJsonName(entityJson.Value, "visualDescId", 0x1F000000, 0x1FFFFFFF),
                        relevantProperties = ExtractRelevantPropertyNodes(entityJson.Value)
                    };
                }
            }
            catch (Exception ex)
            {
                entitySummary = new { id = selectedPhysObj, hex = $"0x{selectedPhysObj:X8}", error = ex.GetBaseException().Message };
            }
        }

        // Extract the exact Appearance_KeyInfo tuples.  Keep root and EntityDescription
        // entries in diagnostics, but prefer the DbProperties selector if both sources
        // request the same APR/key pair.
        var appearanceSelections = new List<AppearanceSelectionInfo>();
        if (rootPropertyJson.HasValue)
            appearanceSelections.AddRange(ExtractAppearanceSelections(rootPropertyJson.Value, "$db", 0));
        if (entityJson.HasValue)
            appearanceSelections.AddRange(ExtractAppearanceSelections(entityJson.Value, "$entity", 1));

        // Dressing Room equipment selectors override the character defaults for the same
        // APR/key pair. This is the same composition mechanism DDO uses for modular body
        // parts; no mesh is fabricated when an equipment record has no appearance selectors.
        var equipmentSummaries = new List<object>();
        if (!string.IsNullOrWhiteSpace(equipment))
        {
            int eqIndex = 0;
            foreach (var token in equipment.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var tokenParts = token.Split('|', StringSplitOptions.TrimEntries);
                string idText = tokenParts.Length > 0 ? tokenParts[0] : token;
                string slotHint = tokenParts.Length > 1 ? tokenParts[1] : "";
                uint appearanceHint = 0;
                if (tokenParts.Length > 2) TryId(tokenParts[2], out appearanceHint);
                if (!TryId(idText, out var equipmentId) || equipmentId < 0x78000000 || equipmentId > 0x7FFFFFFF) continue;
                try
                {
                    var equipmentProperties = DatSource.PropertyMaster.GetPropertyCollection(equipmentId);
                    if (equipmentProperties == null) continue;
                    var equipmentJson = SerializeWithPropertyConverter(equipmentProperties);
                    var explicitSelections = ExtractAppearanceSelections(equipmentJson, $"$equipment[{eqIndex}]", -10 - eqIndex);
                    var selections = explicitSelections;
                    string resolver = "Appearance_ClassList";
                    string? implicitNote = null;
                    var baseSelectorState = appearanceSelections.Where(x => x.SourceRank >= 0).ToArray();
                    var discoveredAprHints = new List<uint>();
                    if (appearanceHint is >= 0x20000000 and <= 0x20FFFFFF) discoveredAprHints.Add(appearanceHint);
                    discoveredAprHints.AddRange(FindWearableAppearanceHints(equipmentId));
                    discoveredAprHints = discoveredAprHints.Where(x => x is >= 0x20000000 and <= 0x20FFFFFF).Distinct().ToList();

                    if (selections.Length == 0 && discoveredAprHints.Count > 0)
                    {
                        var implicitSelections = new List<AppearanceSelectionInfo>();
                        var notes = new List<string>();
                        foreach (var aprHint in discoveredAprHints)
                        {
                            var one = BuildImplicitWearableSelections(aprHint, slotHint, $"$equipment[{eqIndex}].appearanceHint[0x{aprHint:X8}]", -10 - eqIndex, baseSelectorState, out var oneNote);
                            implicitSelections.AddRange(one);
                            if (!string.IsNullOrWhiteSpace(oneNote)) notes.Add(oneNote!);
                        }
                        selections = implicitSelections
                            .GroupBy(x => (x.AprFile, x.Key))
                            .Select(g => g.OrderBy(x => x.SourceRank).First())
                            .ToArray();
                        implicitNote = string.Join(" ", notes);
                        resolver = selections.Length > 0 ? "base-selector-matched APR/WState" : "unresolved APR/WState";
                    }
                    appearanceSelections.AddRange(selections);
                    equipmentSummaries.Add(new
                    {
                        id = equipmentId,
                        hex = $"0x{equipmentId:X8}",
                        slotHint,
                        appearanceHint = appearanceHint == 0 ? null : $"0x{appearanceHint:X8}",
                        discoveredAppearanceHints = discoveredAprHints.Select(x => $"0x{x:X8}").ToArray(),
                        selectorCount = selections.Length,
                        explicitSelectorCount = explicitSelections.Length,
                        resolver,
                        implicitNote,
                        relevantProperties = ExtractRelevantPropertyNodes(equipmentJson)
                    });
                    eqIndex++;
                }
                catch (Exception ex)
                {
                    equipmentSummaries.Add(new { id = equipmentId, hex = $"0x{equipmentId:X8}", slotHint, selectorCount = 0, error = ex.GetBaseException().Message });
                }
            }
        }

        var effectiveSelections = appearanceSelections
            .GroupBy(x => (x.AprFile, x.Key))
            .Select(g => g.OrderBy(x => x.SourceRank).ThenBy(x => x.Path, StringComparer.Ordinal).First())
            .OrderBy(x => x.AprFile)
            .ThenBy(x => x.Key)
            .ToArray();

        var aprIds = effectiveSelections.Select(x => x.AprFile).Where(x => x is >= 0x20000000 and <= 0x20FFFFFF).Distinct().ToHashSet();

        var baseInfo = selectedSetup != 0 ? InspectSetup(selectedSetup, 0, "$baseSetup", true, "Selected") : null;
        int baseJoints = baseInfo?.JointCount ?? 0;
        var baseMeshes = baseInfo?.Meshes?.ToHashSet() ?? new HashSet<uint>();

        // Retain the conservative local graph as comparison/debug evidence.  It remains
        // useful for non-APR creatures and for spotting setup-replacement records.
        var queue = new Queue<Pending>();
        var seen = new HashSet<uint>();
        var diagnostics = new List<object>();
        var candidates = new Dictionary<uint, SetupInfo>();

        queue.Enqueue(new Pending(root, 0, 0, "", "$db", true));
        if (selectedPhysObj != 0)
            queue.Enqueue(new Pending(selectedPhysObj, 1, root, "DbProperties", "$physObj", true));
        if (selectedVisual != 0)
            queue.Enqueue(new Pending(selectedVisual, 1, root, "DbProperties", "$selectedVisual", true));
        foreach (var aprId in aprIds)
            queue.Enqueue(new Pending(aprId, 1, selectedPhysObj != 0 ? selectedPhysObj : root, selectedPhysObj != 0 ? "EntityDescription" : "DbProperties", "$appearanceAprFile", true));

        foreach (var r in ScanStructuredPropertyReferences(root))
        {
            if (ShouldFollow(r.Id, r.Type, "DbProperties", 1))
                queue.Enqueue(new Pending(r.Id, 1, root, "DbProperties", r.SourcePath, true));
        }

        while (queue.Count > 0 && seen.Count < MaxNodes)
        {
            var item = queue.Dequeue();
            if (item.Depth > MaxDepth || !seen.Add(item.Id)) continue;
            if (!TryLoad(item.Id, out var dat, out var bytes) || bytes == null) continue;

            string type = TypeName(item.Id) ?? "Unknown";
            diagnostics.Add(new
            {
                id = $"0x{item.Id:X8}", type, dat, item.Depth, item.Path,
                structured = item.Structured,
                parent = item.Parent == 0 ? null : $"0x{item.Parent:X8}",
                parentType = item.ParentType,
                byteLength = bytes.Length
            });

            if ((item.Id >> 24) == 0x04 || type.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            {
                if (item.Id != selectedSetup)
                {
                    var si = InspectSetup(item.Id, item.Depth, item.Path, item.Structured, item.ParentType);
                    if (si != null)
                    {
                        ScoreCandidate(si, baseJoints, baseMeshes);
                        if (si.Score > 0 && (!candidates.TryGetValue(si.Id, out var old) || si.Score > old.Score))
                            candidates[si.Id] = si;
                    }
                }
                continue;
            }

            if (item.Depth >= MaxDepth) continue;
            bool unaligned = ShouldScanUnaligned(type);
            foreach (var r in ScanLoadableReferences(bytes, item.Id, unaligned).Take(192))
            {
                if (!ShouldFollow(r.Id, r.Type, type, item.Depth + 1)) continue;
                string path = item.Path + " -> " + r.Type + "(0x" + r.Id.ToString("X8") + ")";
                queue.Enqueue(new Pending(r.Id, item.Depth + 1, item.Id, type, path, item.Structured));
            }
        }

        var ranked = candidates.Values
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Depth)
            .ThenBy(x => x.Id)
            .Take(12)
            .Select(x => new
            {
                id = x.Id,
                hex = $"0x{x.Id:X8}",
                x.JointCount,
                meshCount = x.Meshes.Length,
                meshes = x.Meshes.Select(m => $"0x{m:X8}").ToArray(),
                x.Depth,
                x.Score,
                x.Path,
                x.Structured,
                x.ParentType,
                x.Reasons,
                recommended = x.Score >= 500
            })
            .ToArray();

        var rootRelevant = rootPropertyJson.HasValue ? ExtractRelevantPropertyNodes(rootPropertyJson.Value) : Array.Empty<object>();
        var entityRelevant = entityJson.HasValue ? ExtractRelevantPropertyNodes(entityJson.Value) : Array.Empty<object>();

        var aprRecords = aprIds
            .OrderBy(x => x)
            .Select(id => InspectAppearanceApr(id, appearanceSelections.Where(s => s.AprFile == id).ToArray()))
            .ToArray();
        var resolvedAppearance = ResolveAppearance(effectiveSelections, aprRecords);

        return Ok(new
        {
            format = "DDO Studio NPC Appearance Composition",
            resolverVersion = 6,
            appearanceMode = "APR typed composition",
            generatedUtc = DateTime.UtcNow,
            dbId = root,
            dbHex = $"0x{root:X8}",
            physObj = selectedPhysObj == 0 ? null : $"0x{selectedPhysObj:X8}",
            baseSetup = selectedSetup == 0 ? null : $"0x{selectedSetup:X8}",
            baseJointCount = baseJoints,
            baseMeshes = baseMeshes.Select(m => $"0x{m:X8}").OrderBy(x => x).ToArray(),
            selectedVisual = selectedVisual == 0 ? null : $"0x{selectedVisual:X8}",
            rootAppearanceProperties = rootRelevant,
            entityDescription = entitySummary,
            entityAppearanceProperties = entityRelevant,
            dressingRoomEquipment = equipmentSummaries.ToArray(),
            appearanceSelections = appearanceSelections.Select(SelectionDto).ToArray(),
            appearanceAprFiles = aprIds.Select(id => $"0x{id:X8}").OrderBy(x => x).ToArray(),
            appearanceAprRecords = aprRecords.Select(AprRecordDto).ToArray(),
            resolvedAppearance = ResolvedAppearanceDto(resolvedAppearance),
            scannedNodes = seen.Count,
            truncated = queue.Count > 0,
            components = ranked,
            recommendedCount = ranked.Count(x => x.recommended),
            graph = diagnostics.Take(420).ToArray(),
            note = "Wearable composition reuses the loaded character's APR selector intensities for matching AppearanceKeys, then applies equipment overrides through MeshTypeId/MaterialTypeId composition. Direct WState -> Appearance references are included as wearable candidates."
        });
    }

    static JsonElement SerializeWithPropertyConverter(object value)
    {
        var settings = new Newtonsoft.Json.JsonSerializerSettings
        {
            NullValueHandling = Newtonsoft.Json.NullValueHandling.Include
        };
        settings.Converters.Add(new IPropertyJsonConverter());
        string json = Newtonsoft.Json.JsonConvert.SerializeObject(value, settings);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    static object SelectionDto(AppearanceSelectionInfo s) => new
    {
        aprFile = $"0x{s.AprFile:X8}",
        key = $"0x{s.Key:X8}",
        keyName = s.KeyName,
        modifier = s.Modifier,
        path = s.Path
    };

    static AppearanceSelectionInfo[] ExtractAppearanceSelections(JsonElement root, string rootPath, int sourceRank)
    {
        var list = new List<AppearanceSelectionInfo>();
        WalkAppearanceSelections(root, rootPath, sourceRank, list, 0);
        return list.ToArray();
    }

    static void WalkAppearanceSelections(JsonElement e, string path, int sourceRank, List<AppearanceSelectionInfo> list, int depth)
    {
        if (depth > 20 || list.Count >= 256) return;
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyCI(e, "propertyName", out var pn) &&
                string.Equals(pn.GetString(), "Appearance_KeyInfo", StringComparison.OrdinalIgnoreCase))
            {
                uint apr = FindChildPropertyId(e, "Appearance_AprFile");
                uint key = FindChildPropertyId(e, "Appearance_Key");
                double mod = FindChildPropertyDouble(e, "Appearance_Modifier");
                string keyName = FindChildPropertyString(e, "Appearance_Key", "enumValue") ?? "";
                if (apr is >= 0x20000000 and <= 0x20FFFFFF && key != 0)
                {
                    list.Add(new AppearanceSelectionInfo
                    {
                        AprFile = apr,
                        Key = key,
                        KeyName = keyName,
                        Modifier = mod,
                        Path = path,
                        SourceRank = sourceRank
                    });
                }
            }

            foreach (var p in e.EnumerateObject())
                WalkAppearanceSelections(p.Value, path + "." + p.Name, sourceRank, list, depth + 1);
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var v in e.EnumerateArray())
                WalkAppearanceSelections(v, path + "[" + (i++) + "]", sourceRank, list, depth + 1);
        }
    }

    static uint FindChildPropertyId(JsonElement e, string propertyName)
    {
        if (e.ValueKind != JsonValueKind.Object) return 0;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in p.Value.EnumerateArray())
                {
                    if (child.ValueKind != JsonValueKind.Object) continue;
                    if (!TryGetPropertyCI(child, "propertyName", out var pn) || !string.Equals(pn.GetString(), propertyName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (TryGetPropertyCI(child, "value", out var val) && TryJsonUInt(val, out uint n)) return n;
                }
            }
        }
        return 0;
    }

    static double FindChildPropertyDouble(JsonElement e, string propertyName)
    {
        if (e.ValueKind != JsonValueKind.Object) return 0;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var child in p.Value.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetPropertyCI(child, "propertyName", out var pn) || !string.Equals(pn.GetString(), propertyName, StringComparison.OrdinalIgnoreCase)) continue;
                if (!TryGetPropertyCI(child, "value", out var val)) continue;
                if (val.ValueKind == JsonValueKind.Number && val.TryGetDouble(out double d)) return d;
                if (val.ValueKind == JsonValueKind.String && double.TryParse(val.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            }
        }
        return 0;
    }

    static string? FindChildPropertyString(JsonElement e, string propertyName, string field)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Array) continue;
            foreach (var child in p.Value.EnumerateArray())
            {
                if (child.ValueKind != JsonValueKind.Object) continue;
                if (!TryGetPropertyCI(child, "propertyName", out var pn) || !string.Equals(pn.GetString(), propertyName, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryGetPropertyCI(child, field, out var val) && val.ValueKind == JsonValueKind.String) return val.GetString();
            }
        }
        return null;
    }

    static AppearanceSelectionInfo[] BuildImplicitWearableSelections(uint aprId, string slotHint, string path, int sourceRank, IReadOnlyList<AppearanceSelectionInfo> baseSelectorState, out string? note)
    {
        note = null;
        try
        {
            var data = DatSource.GeneralDat?.GetFileContents(aprId);
            if (data == null) { note = "APR record not found in General DAT."; return Array.Empty<AppearanceSelectionInfo>(); }
            var table = DecodeAppearanceTable(data, aprId);
            if (table.Modifications.Count == 0) { note = "APR decoded but contains no modifications."; return Array.Empty<AppearanceSelectionInfo>(); }

            const uint Worn = 0x10000007;
            const uint HeadMesh = 0x1000000B;
            const uint FaceTexture = 0x10000011;
            const uint CrestMesh = 0x10000027;
            const uint DyeMapColor = 0x10000068;
            const uint DyeMapColor2 = 0x100003A4;

            bool head = string.Equals(slotHint, "Head", StringComparison.OrdinalIgnoreCase);
            bool body = string.Equals(slotHint, "Armor", StringComparison.OrdinalIgnoreCase) || string.Equals(slotHint, "Cloak", StringComparison.OrdinalIgnoreCase);
            uint[] preferred = head
                ? new[] { HeadMesh, CrestMesh, Worn, FaceTexture, DyeMapColor, DyeMapColor2 }
                : body
                    ? new[] { Worn, DyeMapColor, DyeMapColor2 }
                    : new[] { Worn, HeadMesh, CrestMesh, FaceTexture, DyeMapColor, DyeMapColor2 };

            var mods = table.Modifications
                .OrderBy(m => { int i = Array.IndexOf(preferred, m.Key); return i < 0 ? 1000 : i; })
                .ThenBy(m => m.Key)
                .ToArray();

            var selected = new List<AppearanceSelectionInfo>();
            foreach (var mod in mods)
            {
                if (!preferred.Contains(mod.Key) && (head || body)) continue;
                if (mod.Parts.Count == 0) continue;

                // Do not invent a race/body variant when an APR contains multiple genuinely
                // different choices. We only auto-select an unambiguous part, or a 1.0/default
                // part when every alternative carries the same effective mesh targets.
                AprPart? part = null;
                string selectionReason = "";
                var baseState = baseSelectorState
                    .Where(x => x.Key == mod.Key)
                    .OrderBy(x => x.SourceRank)
                    .FirstOrDefault();
                if (baseState != null && mod.Parts.Count > 0)
                {
                    // For equipment composition, copy the character's own selector state for
                    // the same AppearanceKey and choose the wearable part nearest that intensity.
                    // Race/body variants therefore follow the loaded character instead of
                    // inventing a generic 1.0 choice.
                    double best = mod.Parts.Min(p => Math.Abs(p.Intensity - baseState.Modifier));
                    part = mod.Parts
                        .Where(p => Math.Abs(Math.Abs(p.Intensity - baseState.Modifier) - best) <= 0.0000001)
                        .OrderBy(p => p.Intensity)
                        .FirstOrDefault();
                    selectionReason = $"matched base {AppearanceKeyName(mod.Key)}={baseState.Modifier:0.#####}";
                }
                else if (mod.Parts.Count == 1)
                {
                    part = mod.Parts[0];
                    selectionReason = "single wearable variant";
                }
                else
                {
                    var signatures = mod.Parts.Select(PartSignature).Distinct(StringComparer.Ordinal).ToArray();
                    if (signatures.Length == 1)
                    {
                        part = mod.Parts.OrderBy(p => Math.Abs(p.Intensity - 1.0)).ThenBy(p => p.Intensity).First();
                        selectionReason = "equivalent wearable variants";
                    }
                    else if (!string.IsNullOrWhiteSpace(slotHint))
                    {
                        // 1.7.2 standalone wearable preview:
                        // APR intensities are not universally "strength" values. Many wearable
                        // tables use tiny values (0.00, 0.01, 0.02, ...) as variant selectors.
                        // Some Worn tables use selector values rather than literal blend weights; choosing the value
                        // nearest 1.0 selected the wrong helmet variant.
                        //
                        // Preserve the conventional Worn=1.0 case when it actually exists;
                        // otherwise prefer an explicit zero/default variant, then the lowest
                        // available intensity. A loaded character still uses baseState above.
                        if (mod.Key == Worn)
                        {
                            part = mod.Parts.FirstOrDefault(p => Math.Abs(p.Intensity - 1.0) <= 0.00001)
                                ?? mod.Parts.FirstOrDefault(p => Math.Abs(p.Intensity) <= 0.00001)
                                ?? mod.Parts.OrderBy(p => p.Intensity).First();
                        }
                        else
                        {
                            part = mod.Parts.FirstOrDefault(p => Math.Abs(p.Intensity) <= 0.00001)
                                ?? mod.Parts.FirstOrDefault(p => Math.Abs(p.Intensity - 1.0) <= 0.00001)
                                ?? mod.Parts.OrderBy(p => p.Intensity).First();
                        }
                        selectionReason = $"standalone wearable default intensity {part.Intensity:0.#####}";
                    }
                }
                if (part == null) continue;

                selected.Add(new AppearanceSelectionInfo
                {
                    AprFile = aprId,
                    Key = mod.Key,
                    KeyName = AppearanceKeyName(mod.Key),
                    Modifier = part.Intensity,
                    Path = path + ".implicit[0x" + mod.Key.ToString("X8") + "]:" + selectionReason,
                    SourceRank = sourceRank
                });
            }

            if (selected.Count == 0)
            {
                note = $"APR 0x{aprId:X8} has no unambiguous {slotHint} selector. Multiple variants were left unresolved instead of guessing.";
                return Array.Empty<AppearanceSelectionInfo>();
            }
            note = $"Synthesized {selected.Count} conservative selector(s) from direct APR 0x{aprId:X8} for slot '{slotHint}'.";
            return selected.ToArray();
        }
        catch (Exception ex)
        {
            note = ex.GetBaseException().Message;
            return Array.Empty<AppearanceSelectionInfo>();
        }
    }

    static IEnumerable<uint> FindWearableAppearanceHints(uint equipmentId)
    {
        // Some wearable DbProperties records point at
        // WState records which in turn carry the Appearance/APR references used while worn.
        // Keep this deliberately shallow: item -> direct WState -> direct Appearance.
        var result = new HashSet<uint>();
        byte[]? rootBytes = null;
        try { rootBytes = DatSource.GameLogicDat?.GetFileContents(equipmentId); } catch { }
        if (rootBytes == null) return result;

        foreach (var wstate in ScanAlignedIds(rootBytes, 0x70))
        {
            byte[]? stateBytes = null;
            try { stateBytes = DatSource.GameLogicDat?.GetFileContents(wstate); } catch { }
            if (stateBytes == null) continue;
            foreach (var apr in ScanAlignedIds(stateBytes, 0x20))
                result.Add(apr);
        }
        return result;
    }

    static IEnumerable<uint> ScanAlignedIds(byte[] bytes, byte prefix)
    {
        var seen = new HashSet<uint>();
        for (int o = 0; o + 4 <= bytes.Length; o += 4)
        {
            uint id = BitConverter.ToUInt32(bytes, o);
            if ((byte)(id >> 24) != prefix || id == 0) continue;
            if (seen.Add(id)) yield return id;
        }
    }

    static string PartSignature(AprPart p)
    {
        var meshes = p.MeshReplacements.Select(x => $"M:{x.MeshType:X8}:{x.MeshDid:X8}").OrderBy(x => x);
        var mats = p.MaterialMods.Select(x => $"T:{x.MeshType:X8}:{x.MaterialType:X8}:{x.ModifierDid:X8}").OrderBy(x => x);
        var setups = p.SetupReplacements.Select(x => $"S:{x.SetupDid:X8}").OrderBy(x => x);
        return string.Join("|", meshes.Concat(mats).Concat(setups));
    }

    static string AppearanceKeyName(uint key) => key switch
    {
        0x10000007 => "Worn",
        0x1000000B => "HeadMesh",
        0x10000011 => "FaceTexture",
        0x10000027 => "CrestMesh",
        0x10000068 => "DyemapColor",
        0x100003A4 => "DyemapColor2",
        _ => $"0x{key:X8}"
    };

    static AprRecordData InspectAppearanceApr(uint id, AppearanceSelectionInfo[] selections)
    {
        try
        {
            var data = DatSource.GeneralDat?.GetFileContents(id);
            if (data == null)
                return new AprRecordData { Id = id, Error = "Appearance record not found in General DAT." };

            AprTableData? table = null;
            string? error = null;
            try { table = DecodeAppearanceTable(data, id); }
            catch (Exception ex) { error = ex.GetBaseException().Message; }

            var result = new AprRecordData { Id = id, Data = data, Table = table, Error = error };
            if (table != null)
            {
                foreach (var selection in selections)
                    result.SelectionMatches.Add(MatchSelection(table, selection));
            }
            return result;
        }
        catch (Exception ex)
        {
            return new AprRecordData { Id = id, Error = ex.GetBaseException().Message };
        }
    }

    static AprTableData DecodeAppearanceTable(byte[] data, uint expectedId)
    {
        var type = Sdk.GetType("VoK.Sdk.Common.AppearanceTable") ?? throw new Exception("VoK.Sdk.Common.AppearanceTable is not available in this SDK build.");
        using var br = new BinaryReader(new MemoryStream(data, false));
        var parsed = Construct(type, br);

        uint parsedDid = ReadNamedUInt(parsed, "Did", "DID", "Id", "ID");
        var table = new AprTableData
        {
            Did = parsedDid is >= 0x20000000 and <= 0x20FFFFFF ? parsedDid : expectedId,
            Consumed = br.BaseStream.Position
        };

        var hashContainer = GetLoose(parsed, "HashMap", "Hash", "Lookup", "Map");
        foreach (var (keyObj, valueObj) in MapEntries(hashContainer))
        {
            uint key = UIntValue(keyObj);
            int value = IntValue(valueObj);
            if (key != 0) table.HashMap.Add((key, value));
        }

        var modsContainer = GetLoose(parsed, "Modifications", "ModificationList", "AppearanceModifications", "Mods");
        if (modsContainer == null)
            modsContainer = FindEnumerableMember(parsed, n => n.Contains("modif", StringComparison.OrdinalIgnoreCase));

        foreach (var (entryKey, entryValue) in MapEntriesOrItems(modsContainer))
        {
            object modObj = entryValue ?? entryKey ?? new object();
            uint key = UIntValue(entryKey);
            if (key == 0) key = ReadNamedUInt(modObj, "Key", "AppearanceKey", "DidKey", "m_eKey");
            if (key == 0)
            {
                key = FindScalarUInt(modObj, n => n.Contains("key", StringComparison.OrdinalIgnoreCase), 0x10000000, 0x10FFFFFF);
            }
            if (key == 0) continue;

            var mod = new AprModification { Key = key };
            var partsContainer = GetLoose(modObj, "Parts", "AppearanceParts", "Values", "Entries", "Modifications");
            if (partsContainer == null)
                partsContainer = FindEnumerableMember(modObj, n => n.Contains("part", StringComparison.OrdinalIgnoreCase) || n.Contains("value", StringComparison.OrdinalIgnoreCase));

            foreach (var (_, partValue) in MapEntriesOrItems(partsContainer))
            {
                if (partValue == null) continue;
                var part = DecodeAppearancePart(partValue);
                if (part != null) mod.Parts.Add(part);
            }

            // Some SDK collection wrappers enumerate the parts directly and expose the key
            // on the parent.  Fall back to looking for part-like descendants if needed.
            if (mod.Parts.Count == 0)
            {
                foreach (var partObj in FindDescendants(modObj, o => o.GetType().Name.Contains("Part", StringComparison.OrdinalIgnoreCase), 4).Take(256))
                {
                    var part = DecodeAppearancePart(partObj);
                    if (part != null) mod.Parts.Add(part);
                }
            }

            if (mod.Parts.Count > 0)
                table.Modifications.Add(mod);
        }

        // Structural fallback: locate modification-like objects anywhere in the parsed table.
        if (table.Modifications.Count == 0)
        {
            foreach (var modObj in FindDescendants(parsed, o => o.GetType().Name.Contains("Modification", StringComparison.OrdinalIgnoreCase), 5).Take(256))
            {
                uint key = ReadNamedUInt(modObj, "Key", "AppearanceKey", "DidKey", "m_eKey");
                if (key == 0) key = FindScalarUInt(modObj, n => n.Contains("key", StringComparison.OrdinalIgnoreCase), 0x10000000, 0x10FFFFFF);
                if (key == 0) continue;
                var mod = new AprModification { Key = key };
                foreach (var partObj in FindDescendants(modObj, o => o.GetType().Name.Contains("Part", StringComparison.OrdinalIgnoreCase), 3).Take(256))
                {
                    var part = DecodeAppearancePart(partObj);
                    if (part != null) mod.Parts.Add(part);
                }
                if (mod.Parts.Count > 0) table.Modifications.Add(mod);
            }
        }

        table.Modifications.Sort((a, b) => a.Key.CompareTo(b.Key));
        foreach (var m in table.Modifications)
            m.Parts.Sort((a, b) => a.Intensity.CompareTo(b.Intensity));
        return table;
    }

    static AprPart? DecodeAppearancePart(object partObj)
    {
        double intensity = ReadNamedDouble(partObj, "Intensity", "Modifier", "Value", "m_fIntensity");
        var part = new AprPart { Intensity = intensity };

        var materialContainer = GetLoose(partObj, "MaterialMods", "MaterialModifiers", "Materials", "MaterialModifications");
        foreach (var op in EnumerateOperationObjects(materialContainer, partObj, "Material"))
        {
            uint modifierDid = ReadNamedUInt(op, "ModifierDid", "MaterialModifierDid", "Modifier", "Did", "DID");
            if (modifierDid < 0x30000000 || modifierDid > 0x30FFFFFF)
                modifierDid = FindScalarUInt(op, n => n.Contains("modifier", StringComparison.OrdinalIgnoreCase) || n.Contains("did", StringComparison.OrdinalIgnoreCase), 0x30000000, 0x30FFFFFF);
            if (modifierDid == 0) continue;

            uint templateDid = ReadNamedUInt(op, "TemplateDid", "MaterialTemplateDid", "Template");
            uint meshType = ReadNamedUInt(op, "MeshType", "RenderMeshTypeId", "MeshTypeId");
            uint materialType = ReadNamedUInt(op, "MaterialType", "MaterialTypeId", "RenderMaterialTypeId");
            double priority = ReadNamedDouble(op, "Priority", "m_fPriority");
            part.MaterialMods.Add(new AprMaterialMod
            {
                Priority = priority,
                TemplateDid = templateDid,
                ModifierDid = modifierDid,
                MeshType = meshType,
                MaterialType = materialType
            });
        }

        var meshContainer = GetLoose(partObj, "MeshReplacements", "Meshes", "RenderMeshReplacements", "MeshReplacement");
        foreach (var op in EnumerateOperationObjects(meshContainer, partObj, "Mesh"))
        {
            uint meshDid = ReadNamedUInt(op, "MeshDid", "RenderMeshDid", "Mesh", "Did", "DID");
            if (meshDid < 0x06000000 || meshDid > 0x06FFFFFF)
                meshDid = FindScalarUInt(op, n => n.Contains("mesh", StringComparison.OrdinalIgnoreCase) || n.Contains("did", StringComparison.OrdinalIgnoreCase), 0x06000000, 0x06FFFFFF);
            if (meshDid == 0) continue;
            uint meshType = ReadNamedUInt(op, "MeshType", "MeshTypeId", "RenderMeshTypeId");
            double priority = ReadNamedDouble(op, "Priority", "m_fPriority");
            part.MeshReplacements.Add(new AprMeshReplacement { Priority = priority, MeshType = meshType, MeshDid = meshDid });
        }

        var setupContainer = GetLoose(partObj, "SetupReplacements", "Setups", "SetupReplacement");
        foreach (var op in EnumerateOperationObjects(setupContainer, partObj, "Setup"))
        {
            uint setupDid = ReadNamedUInt(op, "SetupDid", "Setup", "Did", "DID");
            if (setupDid < 0x04000000 || setupDid > 0x04FFFFFF)
                setupDid = FindScalarUInt(op, n => n.Contains("setup", StringComparison.OrdinalIgnoreCase) || n.Contains("did", StringComparison.OrdinalIgnoreCase), 0x04000000, 0x04FFFFFF);
            if (setupDid == 0) continue;
            double priority = ReadNamedDouble(op, "Priority", "m_fPriority");
            part.SetupReplacements.Add(new AprSetupReplacement { Priority = priority, SetupDid = setupDid });
        }

        if (part.MaterialMods.Count == 0 && part.MeshReplacements.Count == 0 && part.SetupReplacements.Count == 0)
            return null;
        return part;
    }

    static IEnumerable<object> EnumerateOperationObjects(object? preferredContainer, object partObj, string typeToken)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        if (preferredContainer != null)
        {
            foreach (var (_, value) in MapEntriesOrItems(preferredContainer))
                if (value != null && seen.Add(value)) yield return value;
        }
        if (seen.Count == 0)
        {
            foreach (var o in FindDescendants(partObj, o => o.GetType().Name.Contains(typeToken, StringComparison.OrdinalIgnoreCase), 3))
                if (seen.Add(o)) yield return o;
        }
    }

    static SelectionMatch MatchSelection(AprTableData table, AppearanceSelectionInfo selection)
    {
        var match = new SelectionMatch { Selection = selection };
        var mod = table.Modifications.FirstOrDefault(m => m.Key == selection.Key);
        if (mod == null || mod.Parts.Count == 0) return match;

        bool exact = false;
        var exactParts = mod.Parts.Where(p => Math.Abs(p.Intensity - selection.Modifier) <= 0.00001).ToArray();
        if (exactParts.Length > 0)
        {
            exact = true;
            match.SelectedParts.AddRange(exactParts);
        }
        else if (mod.Parts.Count == 1)
        {
            match.SelectedParts.Add(mod.Parts[0]);
        }
        else
        {
            double best = mod.Parts.Min(p => Math.Abs(p.Intensity - selection.Modifier));
            match.SelectedParts.AddRange(mod.Parts.Where(p => Math.Abs(Math.Abs(p.Intensity - selection.Modifier) - best) <= 0.0000001));
        }

        var result = new SelectionMatch
        {
            Selection = selection,
            KeyFound = true,
            ExactIntensityMatch = exact
        };
        result.SelectedParts.AddRange(match.SelectedParts);
        return result;
    }

    static ResolvedAppearanceData ResolveAppearance(AppearanceSelectionInfo[] effectiveSelections, AprRecordData[] records)
    {
        var result = new ResolvedAppearanceData();
        foreach (var selection in effectiveSelections)
        {
            var record = records.FirstOrDefault(r => r.Id == selection.AprFile);
            if (record == null)
            {
                result.Errors.Add($"APR 0x{selection.AprFile:X8} was not inspected.");
                continue;
            }
            if (record.Table == null)
            {
                result.Errors.Add($"APR 0x{selection.AprFile:X8}: {record.Error ?? "AppearanceTable parse failed."}");
                continue;
            }

            var match = MatchSelection(record.Table, selection);
            if (!match.KeyFound || match.SelectedParts.Count == 0)
            {
                result.Errors.Add($"APR 0x{selection.AprFile:X8} does not contain a usable 0x{selection.Key:X8} selector.");
                continue;
            }

            result.SelectedParts.Add(match);
            foreach (var p in match.SelectedParts)
            {
                result.MeshReplacements.AddRange(p.MeshReplacements);
                result.MaterialMods.AddRange(p.MaterialMods);
                result.SetupReplacements.AddRange(p.SetupReplacements);
            }
        }
        return result;
    }

    static object ResolvedAppearanceDto(ResolvedAppearanceData r) => new
    {
        selectorCount = r.SelectedParts.Count,
        selectedParts = r.SelectedParts.Select(SelectionMatchDto).ToArray(),
        meshReplacements = r.MeshReplacements.Select(MeshReplacementDto).ToArray(),
        materialMods = r.MaterialMods.Select(MaterialModDto).ToArray(),
        setupReplacements = r.SetupReplacements.Select(SetupReplacementDto).ToArray(),
        errors = r.Errors.ToArray()
    };

    static object SelectionMatchDto(SelectionMatch m) => new
    {
        aprFile = $"0x{m.Selection.AprFile:X8}",
        key = $"0x{m.Selection.Key:X8}",
        keyName = m.Selection.KeyName,
        requestedModifier = m.Selection.Modifier,
        keyFound = m.KeyFound,
        exactIntensityMatch = m.ExactIntensityMatch,
        selectedIntensity = m.SelectedParts.Count > 0 ? m.SelectedParts[0].Intensity : (double?)null,
        source = m.Selection.Path,
        selectedParts = m.SelectedParts.Select(PartDto).ToArray()
    };

    static object PartDto(AprPart p) => new
    {
        intensity = p.Intensity,
        materialMods = p.MaterialMods.Select(MaterialModDto).ToArray(),
        meshReplacements = p.MeshReplacements.Select(MeshReplacementDto).ToArray(),
        setupReplacements = p.SetupReplacements.Select(SetupReplacementDto).ToArray()
    };

    static object MaterialModDto(AprMaterialMod m) => new
    {
        priority = m.Priority,
        templateDid = $"0x{m.TemplateDid:X8}",
        modifierDid = $"0x{m.ModifierDid:X8}",
        meshType = $"0x{m.MeshType:X8}",
        materialType = $"0x{m.MaterialType:X8}"
    };

    static object MeshReplacementDto(AprMeshReplacement m) => new
    {
        priority = m.Priority,
        meshType = $"0x{m.MeshType:X8}",
        meshDid = $"0x{m.MeshDid:X8}"
    };

    static object SetupReplacementDto(AprSetupReplacement m) => new
    {
        priority = m.Priority,
        setupDid = $"0x{m.SetupDid:X8}"
    };

    static object AprRecordDto(AprRecordData record)
    {
        if (record.Data.Length == 0)
            return new { id = record.Id, hex = $"0x{record.Id:X8}", error = record.Error ?? "Appearance record unavailable." };

        int rawLimit = Math.Min(record.Data.Length, 262144);
        object sdk;
        if (record.Table == null)
        {
            sdk = new
            {
                candidateTypes = new[] { "VoK.Sdk.Common.AppearanceTable" },
                attempts = new object[] { new { type = "VoK.Sdk.Common.AppearanceTable", success = false, error = record.Error ?? "AppearanceTable parse failed." } }
            };
        }
        else
        {
            sdk = new
            {
                candidateTypes = new[] { "VoK.Sdk.Common.AppearanceTable" },
                attempts = new object[]
                {
                    new
                    {
                        type = "VoK.Sdk.Common.AppearanceTable",
                        success = true,
                        consumed = record.Table.Consumed,
                        complete = record.Table.Consumed == record.Data.Length,
                        members = new
                        {
                            did = $"0x{record.Table.Did:X8}",
                            modifications = record.Table.Modifications.Select(m => new
                            {
                                key = $"0x{m.Key:X8}",
                                parts = m.Parts.Select(PartDto).ToArray()
                            }).ToArray(),
                            hashMap = record.Table.HashMap.Select(x => new { key = $"0x{x.Key:X8}", value = x.Value }).ToArray(),
                            selectionMatches = record.SelectionMatches.Select(SelectionMatchDto).ToArray()
                        }
                    }
                }
            };
        }

        return new
        {
            id = record.Id,
            hex = $"0x{record.Id:X8}",
            byteLength = record.Data.Length,
            sha1 = Convert.ToHexString(SHA1.HashData(record.Data)),
            firstBytes = Convert.ToHexString(record.Data.AsSpan(0, Math.Min(512, record.Data.Length))),
            rawHex = Convert.ToHexString(record.Data.AsSpan(0, rawLimit)),
            rawHexTruncated = rawLimit < record.Data.Length,
            alignedReferences = ScanReferenceDtos(record.Data, 4).Take(512).ToArray(),
            unalignedReferences = ScanReferenceDtos(record.Data, 1).Where(x => x.offset % 4 != 0).Take(256).ToArray(),
            interestingDwords = ScanInterestingDwords(record.Data).Take(2048).ToArray(),
            sdk
        };
    }

    static object? GetLoose(object? o, params string[] aliases)
    {
        if (o == null) return null;
        var members = EnumerateMembers(o).ToArray();
        foreach (var alias in aliases)
        {
            string n = NormalizeMemberName(alias);
            var exact = members.FirstOrDefault(m => NormalizeMemberName(m.Name) == n);
            if (!string.IsNullOrEmpty(exact.Name)) return exact.Value;
        }
        foreach (var alias in aliases)
        {
            string n = NormalizeMemberName(alias);
            var fuzzy = members.FirstOrDefault(m => NormalizeMemberName(m.Name).Contains(n, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(fuzzy.Name)) return fuzzy.Value;
        }
        return null;
    }

    static string NormalizeMemberName(string name)
    {
        var chars = name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
        return new string(chars).Replace("kbackingfield", "", StringComparison.Ordinal);
    }

    static IEnumerable<(string Name, object? Value)> EnumerateMembers(object o)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var t = o.GetType();
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (p.GetIndexParameters().Length != 0 || !seen.Add(p.Name)) continue;
            object? v = null; try { v = p.GetValue(o); } catch { }
            yield return (p.Name, v);
        }
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!seen.Add(f.Name)) continue;
            object? v = null; try { v = f.GetValue(o); } catch { }
            yield return (f.Name, v);
        }
    }

    static object? FindEnumerableMember(object o, Func<string, bool> namePredicate)
    {
        foreach (var m in EnumerateMembers(o))
            if (m.Value is IEnumerable && m.Value is not string && namePredicate(m.Name)) return m.Value;
        return null;
    }

    static IEnumerable<(object? Key, object? Value)> MapEntries(object? container)
    {
        if (container == null) yield break;
        if (container is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict) yield return (e.Key, e.Value);
            yield break;
        }
        foreach (var x in Items(container))
        {
            var key = GetLoose(x, "Key", "Item1");
            var value = GetLoose(x, "Value", "Item2");
            if (key != null || value != null) yield return (key, value);
        }
    }

    static IEnumerable<(object? Key, object? Value)> MapEntriesOrItems(object? container)
    {
        if (container == null) yield break;
        bool yielded = false;
        foreach (var e in MapEntries(container))
        {
            yielded = true;
            yield return e;
        }
        if (yielded) yield break;
        foreach (var x in Items(container)) yield return (null, x);
    }

    static IEnumerable<object> FindDescendants(object root, Func<object, bool> predicate, int maxDepth)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<(object Value, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0)
        {
            var (value, depth) = queue.Dequeue();
            if (value == null || depth > maxDepth || !seen.Add(value)) continue;
            if (depth > 0 && predicate(value)) yield return value;
            if (depth >= maxDepth) continue;
            var t = value.GetType();
            if (t.IsPrimitive || t.IsEnum || value is string || value is decimal || value is byte[]) continue;
            if (value is IEnumerable en)
            {
                foreach (var x in en) if (x != null) queue.Enqueue((x, depth + 1));
                continue;
            }
            foreach (var m in EnumerateMembers(value))
                if (m.Value != null) queue.Enqueue((m.Value, depth + 1));
        }
    }

    static uint ReadNamedUInt(object? o, params string[] aliases)
    {
        var v = GetLoose(o, aliases);
        return UIntValue(v);
    }

    static double ReadNamedDouble(object? o, params string[] aliases)
    {
        var v = GetLoose(o, aliases);
        try { return Convert.ToDouble(v ?? 0, CultureInfo.InvariantCulture); } catch { return 0; }
    }

    static int IntValue(object? o)
    {
        try { return Convert.ToInt32(o ?? 0, CultureInfo.InvariantCulture); } catch { return 0; }
    }

    static uint FindScalarUInt(object o, Func<string, bool> namePredicate, uint min, uint max)
    {
        foreach (var m in EnumerateMembers(o))
        {
            if (!namePredicate(m.Name) || m.Value == null || m.Value is IEnumerable && m.Value is not string) continue;
            uint v = UIntValue(m.Value);
            if (v >= min && v <= max) return v;
        }
        return 0;
    }

    static object[] ExtractRelevantPropertyNodes(JsonElement root)
    {
        var list = new List<object>();
        WalkRelevant(root, "$", list, 0);
        return list.Take(256).ToArray();
    }

    static void WalkRelevant(JsonElement e, string path, List<object> list, int depth)
    {
        if (depth > 18 || list.Count >= 256) return;
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyCI(e, "propertyName", out var pn) && pn.ValueKind == JsonValueKind.String)
            {
                string name = pn.GetString() ?? "";
                if (IsRelevantPropertyName(name))
                {
                    list.Add(new
                    {
                        path,
                        propertyName = name,
                        propertyId = Stringish(e, "propertyId"),
                        propertyType = Stringish(e, "propertyType"),
                        value = Stringish(e, "value"),
                        byteValue = Stringish(e, "byteValue"),
                        enumValue = Stringish(e, "enumValue"),
                        enumType = Stringish(e, "enumType"),
                        key = Stringish(e, "key"),
                        reference = Stringish(e, "reference"),
                        text = Stringish(e, "text"),
                        node = e.Clone()
                    });
                }
            }
            foreach (var p in e.EnumerateObject()) WalkRelevant(p.Value, path + "." + p.Name, list, depth + 1);
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            int i = 0;
            foreach (var v in e.EnumerateArray()) WalkRelevant(v, path + "[" + (i++) + "]", list, depth + 1);
        }
    }

    static bool IsRelevantPropertyName(string name)
    {
        string[] tokens = { "Appearance", "Worn", "Wear", "Outfit", "Armor", "Hair", "Head", "Face", "Skin", "Body", "Race", "Species", "Gender", "Sex", "Material", "Texture", "Visual", "PhysObj", "Entity_Class", "Render_LOD" };
        return tokens.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    static string? Stringish(JsonElement obj, string name)
    {
        if (!TryGetPropertyCI(obj, name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => v.GetRawText()
        };
    }

    static IEnumerable<uint> FindNamedIds(JsonElement root, string propertyName, uint min, uint max)
    {
        var found = new HashSet<uint>();
        WalkNamedIds(root, propertyName, min, max, found, 0);
        return found.OrderBy(x => x);
    }

    static uint FindFirstNamedId(JsonElement root, string propertyName, uint min, uint max)
        => FindNamedIds(root, propertyName, min, max).FirstOrDefault();

    static void WalkNamedIds(JsonElement e, string propertyName, uint min, uint max, HashSet<uint> found, int depth)
    {
        if (depth > 18) return;
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyCI(e, "propertyName", out var pn) && pn.ValueKind == JsonValueKind.String &&
                string.Equals(pn.GetString(), propertyName, StringComparison.OrdinalIgnoreCase) &&
                TryGetPropertyCI(e, "value", out var value) && TryJsonUInt(value, out uint id) && id >= min && id <= max)
                found.Add(id);
            foreach (var p in e.EnumerateObject()) WalkNamedIds(p.Value, propertyName, min, max, found, depth + 1);
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in e.EnumerateArray()) WalkNamedIds(v, propertyName, min, max, found, depth + 1);
        }
    }

    static string? FindAnyIdByJsonName(JsonElement e, string jsonName, uint min, uint max)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (string.Equals(p.Name, jsonName, StringComparison.OrdinalIgnoreCase) && TryJsonUInt(p.Value, out uint n) && n >= min && n <= max)
                    return $"0x{n:X8}";
                var nested = FindAnyIdByJsonName(p.Value, jsonName, min, max);
                if (nested != null) return nested;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in e.EnumerateArray())
            {
                var nested = FindAnyIdByJsonName(v, jsonName, min, max);
                if (nested != null) return nested;
            }
        }
        return null;
    }

    static bool TryGetPropertyCI(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }
        return false;
    }

    static bool TryJsonUInt(JsonElement value, out uint id)
    {
        id = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetUInt32(out id);
        if (value.ValueKind != JsonValueKind.String) return false;
        string s = (value.GetString() ?? "").Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
        return uint.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    static RawRefDto[] ScanReferenceDtos(byte[] data, int step)
    {
        var result = new List<RawRefDto>();
        var seen = new HashSet<(uint, int)>();
        for (int o = 0; o + 4 <= data.Length && result.Count < 1024; o += step)
        {
            uint id = BitConverter.ToUInt32(data, o);
            string? type = TypeName(id);
            if (type == null || !seen.Add((id, o))) continue;
            result.Add(new RawRefDto(id, $"0x{id:X8}", o, $"0x{o:X}", type));
        }
        return result.ToArray();
    }

    static object[] ScanInterestingDwords(byte[] data)
    {
        var result = new List<object>();
        for (int o = 0; o + 4 <= data.Length && result.Count < 4096; o += 4)
        {
            uint v = BitConverter.ToUInt32(data, o);
            if (v == 0) continue;
            string? type = TypeName(v);
            byte p = (byte)(v >> 24);
            bool selectorLike = p == 0x10 || v <= 0x00010000;
            if (type == null && !selectorLike) continue;
            result.Add(new { offset = o, hexOffset = $"0x{o:X}", value = v, hex = $"0x{v:X8}", type, selectorLike });
        }
        return result.ToArray();
    }
    static void ScoreCandidate(SetupInfo c, int baseJoints, HashSet<uint> baseMeshes)
    {
        var reasons = new List<string>();
        int score = 0;
        score += Math.Max(0, 420 - c.Depth * 75);
        if (c.Structured) { score += 260; reasons.Add("structured DbProperties path"); }
        if (c.ParentType.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase)) { score += 230; reasons.Add("visual-description child"); }
        if (c.ParentType.Contains("Appearance", StringComparison.OrdinalIgnoreCase)) { score += 260; reasons.Add("appearance child"); }
        if (c.ParentType.Contains("EntityDescription", StringComparison.OrdinalIgnoreCase)) { score += 120; reasons.Add("entity-description child"); }
        if (c.Path.Contains("Appearance", StringComparison.OrdinalIgnoreCase)) { score += 110; reasons.Add("appearance branch"); }
        if (c.Path.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase)) { score += 80; reasons.Add("visual branch"); }

        if (c.Meshes.Length == 0) { c.Score = 0; c.Reasons = new[] { "no render meshes" }; return; }
        if (baseMeshes.Count > 0 && c.Meshes.All(baseMeshes.Contains) && c.Meshes.Length == baseMeshes.Count)
        { c.Score = 0; c.Reasons = new[] { "duplicates base Setup meshes" }; return; }

        if (baseJoints > 0)
        {
            if (c.JointCount == baseJoints) { score += 420; reasons.Add("same skeleton joint count"); }
            else if (c.JointCount == 0)
            {
                if (c.ParentType.Contains("Visual", StringComparison.OrdinalIgnoreCase) || c.ParentType.Contains("Appearance", StringComparison.OrdinalIgnoreCase) || c.Path.Contains("Appearance", StringComparison.OrdinalIgnoreCase))
                { score += 70; reasons.Add("rigid visual accessory"); }
                else { score = 0; reasons.Add("unrigged Setup without appearance provenance"); }
            }
            else { score = 0; reasons.Add($"different skeleton family ({c.JointCount} vs {baseJoints})"); }
        }
        score += Math.Min(120, c.Meshes.Length * 20);
        c.Score = Math.Max(0, score);
        c.Reasons = reasons.ToArray();
    }

    static SetupInfo? InspectSetup(uint id, int depth, string path, bool structured, string parentType)
    {
        try
        {
            var bytes = DatSource.GeneralDat?.GetFileContents(id);
            if (bytes == null) return null;
            var t = Sdk.GetType("VoK.Sdk.Common.Setup");
            if (t == null) return null;
            object setup;
            using var ms = new MemoryStream(bytes, false);
            using var br = new BinaryReader(ms);
            try { setup = Construct(t, br, null); }
            catch { ms.Position = 0; setup = Construct(t, br); }

            var meshes = Items(Get(setup, "RenderMeshIds")).Select(UIntValue).Where(x => x >= 0x06000000 && x <= 0x06FFFFFF).Distinct().ToArray();
            int jointCount = 0;
            var havok = Get(setup, "HavokSetup");
            if (havok != null)
            {
                int names = Items(Get(havok, "BoneNames")).Count();
                int parents = Items(Get(havok, "ParentIndices")).Count();
                int bones = Items(Get(havok, "Bones")).Count() / 2;
                jointCount = Math.Min(names, Math.Min(parents, bones));
            }
            return new SetupInfo { Id = id, JointCount = jointCount, Meshes = meshes, Depth = depth, Path = path, Structured = structured, ParentType = parentType };
        }
        catch { return null; }
    }

    static IEnumerable<RefHit> ScanStructuredPropertyReferences(uint root)
    {
        object? properties = null;
        try { properties = DatSource.PropertyMaster.GetPropertyCollection(root); } catch { }
        if (properties == null) yield break;
        JsonElement element;
        try { element = SerializeWithPropertyConverter(properties); }
        catch { yield break; }
        var found = new Dictionary<uint, string>();
        WalkJson(element, "$properties", found, 0);
        foreach (var kv in found.OrderBy(x => x.Key))
        {
            var type = TypeName(kv.Key);
            if (type == null) continue;
            if (!TryLoad(kv.Key, out var dat, out var bytes) || bytes == null) continue;
            yield return new RefHit(kv.Key, -2, type, dat, kv.Value, true);
        }
    }

    static void WalkJson(JsonElement e, string path, Dictionary<uint, string> found, int depth)
    {
        if (depth > 12) return;
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) WalkJson(p.Value, path + "." + p.Name, found, depth + 1);
                break;
            case JsonValueKind.Array:
                int i = 0; foreach (var v in e.EnumerateArray()) WalkJson(v, path + "[" + (i++) + "]", found, depth + 1); break;
            case JsonValueKind.Number:
                if (e.TryGetUInt32(out var n) && TypeName(n) != null && !found.ContainsKey(n)) found[n] = path; break;
            case JsonValueKind.String:
                var s = e.GetString(); if (string.IsNullOrWhiteSpace(s)) break; s = s.Trim();
                uint n2; bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n2) : uint.TryParse(s, out n2);
                if (ok && TypeName(n2) != null && !found.ContainsKey(n2)) found[n2] = path; break;
        }
    }

    static bool ShouldFollow(uint id, string type, string currentType, int depth)
    {
        if (id == 0 || depth > MaxDepth || string.IsNullOrWhiteSpace(type)) return false;
        byte p = (byte)(id >> 24);
        if (p == 0x04 || type.Contains("Setup", StringComparison.OrdinalIgnoreCase)) return true;
        if (p == 0x47 || type.Contains("EntityDescription", StringComparison.OrdinalIgnoreCase)) return true;
        if (p == 0x1F || type.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase)) return true;
        if (p == 0x20 || type.Contains("Appearance", StringComparison.OrdinalIgnoreCase)) return true;
        if (p == 0x02 || type.Equals("Scene", StringComparison.OrdinalIgnoreCase))
            return depth <= 2 && (currentType.Contains("DbProperties", StringComparison.OrdinalIgnoreCase) || currentType.Contains("EntityDescription", StringComparison.OrdinalIgnoreCase) || currentType.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase));
        if (p is 0x23 or 0x28 || type.Contains("Mapper", StringComparison.OrdinalIgnoreCase))
            return depth <= 2 && (currentType.Contains("DbProperties", StringComparison.OrdinalIgnoreCase) || currentType.Contains("Appearance", StringComparison.OrdinalIgnoreCase) || currentType.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    static bool ShouldScanUnaligned(string type)
        => type.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase) || type.Contains("EntityDescription", StringComparison.OrdinalIgnoreCase) || type.Contains("Appearance", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<RefHit> ScanLoadableReferences(byte[] bytes, uint self, bool unaligned)
    {
        var seen = new HashSet<uint>(); int step = unaligned ? 1 : 4;
        for (int o = 0; o + 4 <= bytes.Length; o += step)
        {
            uint id = BitConverter.ToUInt32(bytes, o);
            if (id == 0 || id == self || !seen.Add(id)) continue;
            var type = TypeName(id); if (type == null) continue;
            if (!TryLoad(id, out var dat, out var child) || child == null) continue;
            yield return new RefHit(id, o, type, dat, "$raw+0x" + o.ToString("X"), false);
        }
    }

    static string? TypeName(uint id)
    {
        foreach (var r in DatSource.IdRanges)
            if (id >= r.Minimum && id <= r.Maximum)
                return string.IsNullOrWhiteSpace(r.Name) ? r.Description : r.Name;
        return null;
    }

    static bool TryLoad(uint id, out string dat, out byte[]? bytes)
    {
        dat = ""; bytes = null;
        try
        {
            byte p = (byte)(id >> 24);
            if (p == 0x47 || p >= 0x78) { bytes = DatSource.GameLogicDat?.GetFileContents(id); if (bytes != null) { dat = "GameLogic"; return true; } }
            if (p == 0x06) { bytes = DatSource.Mesh?.GetFileContents(id); if (bytes != null) { dat = "Mesh"; return true; } }
            bytes = DatSource.GeneralDat?.GetFileContents(id); if (bytes != null) { dat = "General"; return true; }
            bytes = DatSource.GameLogicDat?.GetFileContents(id); if (bytes != null) { dat = "GameLogic"; return true; }
        }
        catch { }
        return false;
    }

    static object Construct(Type t, params object?[] args)
    {
        foreach (var c in t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var p = c.GetParameters(); if (p.Length != args.Length) continue;
            bool ok = true;
            for (int i = 0; i < p.Length; i++) { if (args[i] == null) continue; if (!p[i].ParameterType.IsAssignableFrom(args[i]!.GetType())) { ok = false; break; } }
            if (ok) return c.Invoke(args);
        }
        throw new MissingMethodException(t.FullName);
    }

    static object? Get(object? o, string name)
    {
        if (o == null) return null; var t = o.GetType();
        try { return t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o) ?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o); }
        catch { return null; }
    }

    static IEnumerable<object> Items(object? o)
    {
        if (o is not IEnumerable e) yield break; foreach (var x in e) if (x != null) yield return x;
    }

    static uint UIntValue(object? o) { try { return Convert.ToUInt32(o, CultureInfo.InvariantCulture); } catch { return 0; } }

    static bool TryId(string s, out uint id)
    {
        id = 0; if (string.IsNullOrWhiteSpace(s)) return false; s = s.Trim(); if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }
}
