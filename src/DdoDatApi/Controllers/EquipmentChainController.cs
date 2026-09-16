using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace DdoDatApi.Controllers;

/// <summary>
/// Recursively walks every loadable DAT reference reachable from an equipment/item DbProperties record.
/// This intentionally does not assume which record is the inventory model or worn appearance.
/// The resulting graph is meant to expose the real equipment-composition path empirically.
/// </summary>
[ApiController]
[Route("[controller]")]
public class EquipmentChainController : ControllerBase
{
    const int MaxDepthLimit = 8;
    const int MaxNodesLimit = 1200;
    const int MaxRefsPerNode = 512;

    [HttpGet("{dbId}")]
    public IActionResult Get(string dbId, int depth = 5, int maxNodes = 600)
    {
        if (!TryId(dbId, out var root) || root < 0x78000000 || root > 0x7FFFFFFF)
            return BadRequest("Expected a DbProperties ID in the 0x78xxxxxx-0x7Fxxxxxx range.");

        depth = Math.Clamp(depth, 1, MaxDepthLimit);
        maxNodes = Math.Clamp(maxNodes, 50, MaxNodesLimit);

        if (!TryLoad(root, out var rootDat, out var rootBytes) || rootBytes == null)
            return NotFound($"DbProperties record 0x{root:X8} was not found in local DAT files.");

        var propertyRefs = ScanStructuredPropertyReferences(root).ToArray();

        var queue = new Queue<(uint Id, int Depth, uint? Parent, int? Offset)>();
        var visited = new HashSet<uint>();
        var nodes = new List<NodeDto>();
        var edges = new List<EdgeDto>();
        queue.Enqueue((root, 0, null, null));
        foreach (var pr in propertyRefs)
            queue.Enqueue((pr.Id, 1, root, -2));

        while (queue.Count > 0 && nodes.Count < maxNodes)
        {
            var item = queue.Dequeue();
            if (!visited.Add(item.Id))
            {
                if (item.Parent.HasValue)
                    edges.Add(new EdgeDto(item.Parent.Value, item.Id, item.Offset ?? -1, item.Depth, true));
                continue;
            }

            if (!TryLoad(item.Id, out var dat, out var bytes) || bytes == null)
                continue;

            var refs = ScanLoadableReferences(bytes, item.Id).Take(MaxRefsPerNode).ToArray();
            var type = TypeName(item.Id) ?? "Unknown";
            var node = new NodeDto
            {
                Id = item.Id,
                Hex = $"0x{item.Id:X8}",
                Type = type,
                Dat = dat,
                Depth = item.Depth,
                ByteLength = bytes.Length,
                Sha1 = Convert.ToHexString(SHA1.HashData(bytes)),
                FirstBytes = Convert.ToHexString(bytes.AsSpan(0, Math.Min(256, bytes.Length))),
                AsciiStrings = ScanAscii(bytes).Take(48).ToArray(),
                References = refs.Select(r => new RefDto
                {
                    Id = r.Id,
                    Hex = $"0x{r.Id:X8}",
                    Type = r.Type,
                    Offset = r.Offset,
                    HexOffset = $"0x{r.Offset:X4}",
                    Dat = r.Dat
                }).ToArray()
            };
            nodes.Add(node);

            if (item.Parent.HasValue)
                edges.Add(new EdgeDto(item.Parent.Value, item.Id, item.Offset ?? -1, item.Depth, false));

            if (item.Depth >= depth) continue;
            foreach (var r in refs)
            {
                edges.Add(new EdgeDto(item.Id, r.Id, r.Offset, item.Depth + 1, visited.Contains(r.Id)));
                if (!visited.Contains(r.Id) && queue.Count + nodes.Count < maxNodes * 2)
                    queue.Enqueue((r.Id, item.Depth + 1, item.Id, r.Offset));
            }
        }

        var uniqueNodes = nodes.GroupBy(n => n.Id).Select(g => g.First()).OrderBy(n => n.Depth).ThenBy(n => n.Id).ToArray();
        var counts = uniqueNodes.GroupBy(n => n.Type).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
            .Select(g => new { type = g.Key, count = g.Count() }).ToArray();

        var setupIds = uniqueNodes.Where(n => IsPrefix(n.Id, 0x04)).Select(n => n.Hex).Distinct().ToArray();
        var meshIds = uniqueNodes.Where(n => IsPrefix(n.Id, 0x06)).Select(n => n.Hex).Distinct().ToArray();
        var visualIds = uniqueNodes.Where(n => IsPrefix(n.Id, 0x1F)).Select(n => n.Hex).Distinct().ToArray();
        var appearanceIds = uniqueNodes.Where(n => IsPrefix(n.Id, 0x20)).Select(n => n.Hex).Distinct().ToArray();
        var materialIds = uniqueNodes.Where(n => IsPrefix(n.Id, 0x2B) || IsPrefix(n.Id, 0x30) || IsPrefix(n.Id, 0x31)).Select(n => n.Hex).Distinct().ToArray();
        var textureIds = uniqueNodes.Where(n => IsPrefix(n.Id, 0x40) || IsPrefix(n.Id, 0x41)).Select(n => n.Hex).Distinct().ToArray();

        // Interesting branches are shallow non-root records that themselves fan out into renderable assets.
        var interesting = uniqueNodes
            .Where(n => n.Depth > 0)
            .Select(n => new
            {
                n.Id,
                n.Hex,
                n.Type,
                n.Dat,
                n.Depth,
                setupRefs = n.References.Count(r => IsPrefix(r.Id, 0x04)),
                meshRefs = n.References.Count(r => IsPrefix(r.Id, 0x06)),
                visualRefs = n.References.Count(r => IsPrefix(r.Id, 0x1F)),
                appearanceRefs = n.References.Count(r => IsPrefix(r.Id, 0x20)),
                materialRefs = n.References.Count(r => IsPrefix(r.Id, 0x2B) || IsPrefix(r.Id, 0x30) || IsPrefix(r.Id, 0x31)),
                textureRefs = n.References.Count(r => IsPrefix(r.Id, 0x40) || IsPrefix(r.Id, 0x41)),
                totalRefs = n.References.Length
            })
            .Where(x => x.setupRefs + x.meshRefs + x.visualRefs + x.appearanceRefs + x.materialRefs + x.textureRefs > 0)
            .OrderBy(x => x.Depth)
            .ThenByDescending(x => x.setupRefs + x.meshRefs + x.visualRefs + x.appearanceRefs + x.materialRefs + x.textureRefs)
            .Take(128)
            .ToArray();

        return Ok(new
        {
            format = "DDO Studio Equipment Chain Diagnostic",
            diagnosticVersion = 1,
            generatedUtc = DateTime.UtcNow,
            root = new { id = root, hex = $"0x{root:X8}", dat = rootDat, byteLength = rootBytes.Length },
            structuredPropertyReferences = propertyRefs.Select(r => new { id = r.Id, hex = $"0x{r.Id:X8}", type = r.Type, dat = r.Dat, source = "DbProperties parsed object" }).ToArray(),
            limits = new { requestedDepth = depth, requestedMaxNodes = maxNodes, nodeCount = uniqueNodes.Length, truncated = nodes.Count >= maxNodes || queue.Count > 0 },
            summary = new
            {
                typeCounts = counts,
                setups = setupIds,
                renderMeshes = meshIds,
                visualDescriptions = visualIds,
                appearances = appearanceIds,
                materials = materialIds,
                texturesAndSurfaces = textureIds,
                interestingBranches = interesting
            },
            nodes = uniqueNodes,
            edges = edges.Distinct().ToArray()
        });
    }

    sealed class NodeDto
    {
        public uint Id { get; set; }
        public string Hex { get; set; } = "";
        public string Type { get; set; } = "";
        public string Dat { get; set; } = "";
        public int Depth { get; set; }
        public int ByteLength { get; set; }
        public string Sha1 { get; set; } = "";
        public string FirstBytes { get; set; } = "";
        public string[] AsciiStrings { get; set; } = Array.Empty<string>();
        public RefDto[] References { get; set; } = Array.Empty<RefDto>();
    }

    sealed class RefDto
    {
        public uint Id { get; set; }
        public string Hex { get; set; } = "";
        public string Type { get; set; } = "";
        public int Offset { get; set; }
        public string HexOffset { get; set; } = "";
        public string Dat { get; set; } = "";
    }

    sealed record EdgeDto(uint From, uint To, int Offset, int Depth, bool AlreadyVisited);
    sealed record RefHit(uint Id, int Offset, string Type, string Dat);

    static IEnumerable<RefHit> ScanLoadableReferences(byte[] bytes, uint self)
    {
        var seen = new HashSet<(uint, int)>();
        for (int o = 0; o + 4 <= bytes.Length; o += 4)
        {
            uint id = BitConverter.ToUInt32(bytes, o);
            if (id == 0 || id == self) continue;
            var type = TypeName(id);
            if (type == null) continue;
            if (!TryLoad(id, out var dat, out var child) || child == null) continue;
            if (seen.Add((id, o))) yield return new RefHit(id, o, type, dat);
        }
    }

    static IEnumerable<RefHit> ScanStructuredPropertyReferences(uint root)
    {
        object? properties = null;
        try { properties = DatSource.PropertyMaster.GetPropertyCollection(root); } catch { }
        if (properties == null) yield break;

        JsonElement element;
        try { element = JsonSerializer.SerializeToElement(properties, properties.GetType()); }
        catch { yield break; }

        var candidates = new HashSet<uint>();
        WalkJson(element, candidates);
        foreach (var id in candidates.OrderBy(x => x))
        {
            var type = TypeName(id);
            if (type == null) continue;
            if (!TryLoad(id, out var dat, out var bytes) || bytes == null) continue;
            yield return new RefHit(id, -2, type, dat);
        }
    }

    static void WalkJson(JsonElement element, HashSet<uint> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject()) WalkJson(p.Value, values);
                break;
            case JsonValueKind.Array:
                foreach (var v in element.EnumerateArray()) WalkJson(v, values);
                break;
            case JsonValueKind.Number:
                if (element.TryGetUInt32(out var n) && TypeName(n) != null) values.Add(n);
                break;
            case JsonValueKind.String:
                var text = element.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    text = text.Trim();
                    uint parsedId;
                    bool ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out parsedId)
                        : uint.TryParse(text, out parsedId);
                    if (ok && TypeName(parsedId) != null) values.Add(parsedId);
                }
                break;
        }
    }

    static bool IsPrefix(uint id, byte prefix) => (id >> 24) == prefix;

    static string? TypeName(uint id)
    {
        foreach (var r in DatSource.IdRanges)
            if (id >= r.Minimum && id <= r.Maximum)
                return string.IsNullOrWhiteSpace(r.Name) ? r.Description : r.Name;
        return null;
    }

    static bool TryLoad(uint id, out string dat, out byte[]? bytes)
    {
        dat = "";
        bytes = null;
        // Prefer the DATs known to own the common render/property types, then exhaustively try the rest.
        var sources = new (string Name, VoK.Sdk.Dat.IDatFile? Dat)[]
        {
            ("GameLogic", DatSource.GameLogicDat), ("General", DatSource.GeneralDat), ("Mesh", DatSource.Mesh),
            ("Highres", DatSource.Highres), ("Surface", DatSource.SurfaceDat), ("Anim", DatSource.AnimDat),
            ("LocalEnglish", DatSource.LocalEnglishDat), ("Sound", DatSource.SoundDat),
            ("Cell1", DatSource.Cell1), ("Cell2", DatSource.Cell2), ("Cell3", DatSource.Cell3), ("Cell4", DatSource.Cell4),
            ("Map1", DatSource.Map1), ("Map2", DatSource.Map2), ("Map3", DatSource.Map3), ("Map4", DatSource.Map4)
        };
        foreach (var source in sources)
        {
            try
            {
                bytes = source.Dat?.GetFileContents(id);
                if (bytes != null)
                {
                    dat = source.Name;
                    return true;
                }
            }
            catch { }
        }
        bytes = null;
        return false;
    }

    static string[] ScanAscii(byte[] data)
    {
        var result = new List<string>();
        var chars = new List<char>();
        void Flush()
        {
            if (chars.Count >= 4 && result.Count < 128) result.Add(new string(chars.ToArray()));
            chars.Clear();
        }
        foreach (byte b in data)
        {
            if (b >= 32 && b <= 126) chars.Add((char)b);
            else Flush();
        }
        Flush();
        return result.Distinct().ToArray();
    }

    static bool TryId(string text, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        return uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out id)
            || uint.TryParse(text, out id);
    }
}
