using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using VoK.Sdk.Dat;

namespace DdoDatApi.Controllers;

/// <summary>
/// Walks the real loadable asset graph for a selected object and isolates particle/VFX branches.
/// The goal is to reconstruct effects separately from the base RenderMesh, starting with
/// PSDescription and the textures/surfaces/materials reachable from it.
///
/// 1.4.1.24 tightens the resolver so it stays on the selected item's actual render/effect chain
/// instead of drifting into unrelated global records (CellMesh, MasterProperty, WState, etc.).
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class VisualEffectController : ControllerBase
{
    sealed record RefHit(uint Id, int Offset, string Type, string Dat);

    [HttpGet("resolve")]
    public IActionResult Resolve(
        [FromQuery] string db = null,
        [FromQuery] string visual = null,
        [FromQuery] string setup = null,
        [FromQuery] string appearance = null,
        [FromQuery] int maxDepth = 9,
        [FromQuery] int maxNodes = 900)
    {
        maxDepth = Math.Clamp(maxDepth, 1, 10);
        maxNodes = Math.Clamp(maxNodes, 64, 2400);

        var seeds = new List<uint>();
        AddSeed(db, seeds); AddSeed(visual, seeds); AddSeed(setup, seeds); AddSeed(appearance, seeds);
        seeds = seeds.Where(x => x != 0).Distinct().ToList();
        if (seeds.Count == 0) return BadRequest("Provide at least one db/visual/setup/appearance ID.");

        var queue = new Queue<(uint Id, int Depth, uint Parent, int Offset)>();
        foreach (var id in seeds) queue.Enqueue((id, 0, 0, -1));

        // DbProperties is parsed by the SDK; pull DIDs out of the structured object as well as raw bytes.
        // Only keep references that look like part of a render/appearance/effect chain.
        foreach (var id in seeds.Where(IsDbProperties))
            foreach (var r in ScanStructuredPropertyReferences(id))
                if (ShouldFollowStructuredSeed(r.Id, r.Type))
                    queue.Enqueue((r.Id, 1, id, -2));

        var seen = new HashSet<uint>();
        var nodeRows = new List<object>();
        var edgeRows = new List<object>();
        var particleIds = new HashSet<uint>();

        // Some persistent model auras are not PSDescription
        // particles at all. They are auxiliary Scene -> EntityDescription -> VisualDescription
        // -> Setup -> RenderMesh branches. Track that exact relationship separately.
        var sceneEntityIds = new HashSet<uint>();
        var auxiliaryVisualIds = new HashSet<uint>();
        var auxiliarySetupIds = new HashSet<uint>();
        var renderableAuxiliarySetupIds = new HashSet<uint>();
        var auxiliaryMeshIds = new HashSet<uint>();
        var auxiliaryMaterialIds = new HashSet<uint>();
        var auxiliarySurfaceIds = new HashSet<uint>();
        var auxiliaryTextureIds = new HashSet<uint>();

        while (queue.Count > 0 && seen.Count < maxNodes)
        {
            var (id, depth, parent, parentOffset) = queue.Dequeue();
            if (!seen.Add(id)) continue;
            if (!TryLoad(id, out var dat, out var bytes) || bytes == null) continue;

            var type = TypeName(id) ?? "Unknown";
            bool particle = IsParticleDescription(id, type);
            if (particle) particleIds.Add(id);

            var refs = ScanLoadableReferences(bytes, id, ShouldScanUnaligned(type) || auxiliarySetupIds.Contains(id))
                .Where(r => ShouldFollowGraphRef(r.Id, r.Type ?? TypeName(r.Id) ?? "Unknown", type))
                .Take(256)
                .ToArray();

            nodeRows.Add(new
            {
                id = $"0x{id:X8}", type, dat, depth,
                byteLength = bytes.Length,
                isParticleDescription = particle,
                referenceCount = refs.Length
            });

            if (parent != 0)
                edgeRows.Add(new { from = $"0x{parent:X8}", to = $"0x{id:X8}", offset = parentOffset, depth, type });

            if (depth >= maxDepth) continue;
            foreach (var r in refs)
            {
                string childType = r.Type ?? TypeName(r.Id) ?? "Unknown";

                if (type.Contains("Scene", StringComparison.OrdinalIgnoreCase)
                    && childType.Contains("EntityDescription", StringComparison.OrdinalIgnoreCase))
                    sceneEntityIds.Add(r.Id);

                if (sceneEntityIds.Contains(id)
                    && childType.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase))
                    auxiliaryVisualIds.Add(r.Id);

                if (auxiliaryVisualIds.Contains(id)
                    && childType.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    auxiliarySetupIds.Add(r.Id);

                if (auxiliarySetupIds.Contains(id)
                    && childType.Contains("RenderMesh", StringComparison.OrdinalIgnoreCase))
                {
                    auxiliaryMeshIds.Add(r.Id);
                    renderableAuxiliarySetupIds.Add(id);
                }

                if (auxiliaryMeshIds.Contains(id)
                    && (childType.Contains("Material", StringComparison.OrdinalIgnoreCase) || ((byte)(r.Id >> 24)) is 0x2B or 0x30 or 0x31))
                    auxiliaryMaterialIds.Add(r.Id);

                if (auxiliaryMaterialIds.Contains(id))
                {
                    byte cp = (byte)(r.Id >> 24);
                    if (cp == 0x41 || childType.Contains("RenderSurface", StringComparison.OrdinalIgnoreCase)) auxiliarySurfaceIds.Add(r.Id);
                    if (cp == 0x40 || childType.Contains("RenderTexture", StringComparison.OrdinalIgnoreCase)) auxiliaryTextureIds.Add(r.Id);
                    if (cp is 0x2B or 0x30 or 0x31 || childType.Contains("Material", StringComparison.OrdinalIgnoreCase)) auxiliaryMaterialIds.Add(r.Id);
                }

                if (auxiliarySurfaceIds.Contains(id)
                    && (((byte)(r.Id >> 24)) == 0x40 || childType.Contains("RenderTexture", StringComparison.OrdinalIgnoreCase)))
                    auxiliaryTextureIds.Add(r.Id);

                edgeRows.Add(new { from = $"0x{id:X8}", to = $"0x{r.Id:X8}", offset = r.Offset, depth = depth + 1, type = r.Type });
                if (!seen.Contains(r.Id)) queue.Enqueue((r.Id, depth + 1, id, r.Offset));
            }
        }

        var particleBranchNodes = new HashSet<uint>(particleIds);
        var particleTextures = new HashSet<uint>();
        var particleSurfaces = new HashSet<uint>();
        var particleMaterials = new HashSet<uint>();
        var particleMeshes = new HashSet<uint>();

        // Follow each PSDescription independently. This prevents ordinary diffuse/model textures
        // from being mistaken for aura/particle textures just because they share the object graph.
        foreach (var ps in particleIds)
        {
            var pq = new Queue<(uint Id, int Depth)>();
            var pv = new HashSet<uint>();
            pq.Enqueue((ps, 0));
            while (pq.Count > 0)
            {
                var (id, depth) = pq.Dequeue();
                if (!pv.Add(id) || depth > 5) continue;
                particleBranchNodes.Add(id);
                if (!TryLoad(id, out _, out var bytes) || bytes == null) continue;

                foreach (var r in ScanLoadableReferences(bytes, id, ShouldScanUnaligned(TypeName(id) ?? "Unknown")))
                {
                    string t = r.Type ?? TypeName(r.Id) ?? "Unknown";
                    byte prefix = (byte)(r.Id >> 24);
                    if (prefix == 0x40 || t.Contains("RenderTexture", StringComparison.OrdinalIgnoreCase)) particleTextures.Add(r.Id);
                    if (prefix == 0x41 || t.Contains("RenderSurface", StringComparison.OrdinalIgnoreCase)) particleSurfaces.Add(r.Id);
                    if (prefix is 0x2B or 0x30 or 0x31 || t.Contains("Material", StringComparison.OrdinalIgnoreCase)) particleMaterials.Add(r.Id);
                    if (prefix == 0x06 || t.Contains("RenderMesh", StringComparison.OrdinalIgnoreCase)) particleMeshes.Add(r.Id);

                    if (depth < 5 && ShouldFollowParticleBranch(r.Id, t)) pq.Enqueue((r.Id, depth + 1));
                }
            }
        }

        // Surface records frequently own the final texture DID.
        foreach (var sid in particleSurfaces.ToArray())
            if (TryLoad(sid, out _, out var bytes) && bytes != null)
                foreach (var r in ScanLoadableReferences(bytes, sid, false))
                    if ((byte)(r.Id >> 24) == 0x40 || r.Type.Contains("RenderTexture", StringComparison.OrdinalIgnoreCase))
                        particleTextures.Add(r.Id);

        foreach (var sid in auxiliarySurfaceIds.ToArray())
            if (TryLoad(sid, out _, out var bytes) && bytes != null)
                foreach (var r in ScanLoadableReferences(bytes, sid, false))
                    if ((byte)(r.Id >> 24) == 0x40 || r.Type.Contains("RenderTexture", StringComparison.OrdinalIgnoreCase))
                        auxiliaryTextureIds.Add(r.Id);

        // Auxiliary meshes discovered through effect graphs can be decorative geometry,
        // not the blue tip aura. Probe the selected item's *base* appearance/surfaces/material
        // modifier for packed render references without recursively trusting those hits yet.
        // This keeps the graph clean while exposing shader/material-driven aura candidates.
        var baseProbeRoots = new HashSet<uint>();
        foreach (var seed in seeds.Where(IsDbProperties))
        {
            if (!TryLoad(seed, out _, out var b) || b == null) continue;
            foreach (var r in ScanLoadableReferences(b, seed, false))
            {
                byte rp = (byte)(r.Id >> 24);
                if (rp is 0x20 or 0x30 or 0x31 or 0x41) baseProbeRoots.Add(r.Id);
                if (r.Type.Contains("Appearance", StringComparison.OrdinalIgnoreCase)
                    || r.Type.Contains("Material", StringComparison.OrdinalIgnoreCase)
                    || r.Type.Contains("RenderSurface", StringComparison.OrdinalIgnoreCase))
                    baseProbeRoots.Add(r.Id);
            }
        }
        // Also include the strict graph's scene-level material modifiers and appearance nodes.
        foreach (var id in seen)
        {
            var t = TypeName(id) ?? "";
            if (t.Contains("Appearance", StringComparison.OrdinalIgnoreCase)
                || t.Contains("MaterialModifier", StringComparison.OrdinalIgnoreCase))
                baseProbeRoots.Add(id);
        }

        var baseMaterialProbe = new List<object>();
        foreach (var rootId in baseProbeRoots.OrderBy(x => x))
        {
            if (!TryLoad(rootId, out var rootDat, out var rootBytes) || rootBytes == null) continue;
            string rootType = TypeName(rootId) ?? "Unknown";
            foreach (var r in ScanLoadableReferences(rootBytes, rootId, true).Take(512))
            {
                byte rp = (byte)(r.Id >> 24);
                string rt = r.Type ?? TypeName(r.Id) ?? "Unknown";
                if (!(rp is 0x06 or 0x20 or 0x2B or 0x30 or 0x31 or 0x39 or 0x40 or 0x41)) continue;
                baseMaterialProbe.Add(new
                {
                    root = $"0x{rootId:X8}", rootType, rootDat,
                    id = $"0x{r.Id:X8}", type = rt, r.Offset
                });
            }
        }


        // 1.4.1.32: focused aura-material traversal. The broad 1.4.1.28 probe proved that
        // generic Appearance 0x20000000 is a global material catalog and therefore noisy.
        // Here we only recurse from small Appearance records that are actually in the verified
        // appearance/effect graphs, plus verified scene-level MaterialModifiers.
        var auraRootIds = new HashSet<uint>();
        foreach (var id in seen)
        {
            var t = TypeName(id) ?? "";
            if (t.Contains("Appearance", StringComparison.OrdinalIgnoreCase))
            {
                if (id == 0x20000000) continue;
                if (TryLoad(id, out _, out var ab) && ab != null && ab.Length <= 4096) auraRootIds.Add(id);
            }
            if (t.Contains("MaterialModifier", StringComparison.OrdinalIgnoreCase)) auraRootIds.Add(id);
        }

        var auraCandidateTextures = new HashSet<uint>();
        var auraChainRows = new List<object>();
        var aq = new Queue<(uint Id, int Depth, uint Parent, int Offset)>();
        foreach (var id in auraRootIds) aq.Enqueue((id, 0, 0, -1));
        var av = new HashSet<uint>();
        while (aq.Count > 0 && av.Count < 256)
        {
            var (id, depth, parent, parentOffset) = aq.Dequeue();
            if (!av.Add(id) || depth > 4) continue;
            if (!TryLoad(id, out var datName, out var bytes2) || bytes2 == null) continue;
            string t = TypeName(id) ?? "Unknown";
            auraChainRows.Add(new { id = $"0x{id:X8}", type = t, dat = datName, depth, parent = parent == 0 ? null : $"0x{parent:X8}", offset = parentOffset, byteLength = bytes2.Length });
            bool packed = t.Contains("Appearance", StringComparison.OrdinalIgnoreCase) || t.Contains("Material", StringComparison.OrdinalIgnoreCase);
            foreach (var r in ScanLoadableReferences(bytes2, id, packed).Take(256))
            {
                byte rp = (byte)(r.Id >> 24);
                string rt = r.Type ?? TypeName(r.Id) ?? "Unknown";
                if (rp == 0x40 || rt.Contains("RenderTexture", StringComparison.OrdinalIgnoreCase))
                {
                    if (r.Id != 0x40000000) auraCandidateTextures.Add(r.Id);
                    continue;
                }
                bool follow = rp is 0x2B or 0x30 or 0x31 or 0x41
                    || rt.Contains("Material", StringComparison.OrdinalIgnoreCase)
                    || rt.Contains("RenderSurface", StringComparison.OrdinalIgnoreCase);
                if (follow && depth < 4) aq.Enqueue((r.Id, depth + 1, id, r.Offset));
            }
        }


        // 1.4.1.32: runtime effect investigator. Static mesh/material probing is no longer
        // treated as the only source of a weapon aura. Starting from the selected object's
        // DbProperties and verified EntityDescription records, follow script/state/event-ish
        // records separately. This intentionally allows ScriptTable/Scriptlet/WState here even
        // though the normal render graph filters them out, preserving provenance and preventing
        // the old global-graph explosion.
        var runtimeRoots = new HashSet<uint>(seeds.Where(IsDbProperties));
        foreach (var id in seen)
        {
            var tt = TypeName(id) ?? "";
            if (tt.Contains("EntityDescription", StringComparison.OrdinalIgnoreCase)) runtimeRoots.Add(id);
        }

        var runtimeRows = new List<object>();
        var runtimeEdges = new List<object>();
        var runtimeScripts = new HashSet<uint>();
        var runtimeStates = new HashSet<uint>();
        var runtimeEffects = new HashSet<uint>();
        var runtimeSetups = new HashSet<uint>();
        var runtimeVisuals = new HashSet<uint>();
        var runtimeTextures = new HashSet<uint>();
        var runtimeMaterials = new HashSet<uint>();
        var runtimeQueue = new Queue<(uint Id, int Depth, uint Parent, int Offset)>();
        foreach (var id in runtimeRoots) runtimeQueue.Enqueue((id, 0, 0, -1));
        var runtimeSeen = new HashSet<uint>();

        while (runtimeQueue.Count > 0 && runtimeSeen.Count < 700)
        {
            var (id, depth, parent, parentOffset) = runtimeQueue.Dequeue();
            if (!runtimeSeen.Add(id) || depth > 7) continue;
            if (!TryLoad(id, out var rd, out var rb) || rb == null) continue;
            var rt = TypeName(id) ?? "Unknown";
            var strings = ExtractAsciiStrings(rb, 18).ToArray();
            var refs = ScanLoadableReferences(rb, id, true)
                .Where(x => IsRuntimeInteresting(x.Id, x.Type ?? TypeName(x.Id) ?? "Unknown"))
                .Take(384).ToArray();

            runtimeRows.Add(new
            {
                id = $"0x{id:X8}", type = rt, dat = rd, depth,
                parent = parent == 0 ? null : $"0x{parent:X8}",
                offset = parentOffset, byteLength = rb.Length,
                strings,
                referenceCount = refs.Length,
                score = RuntimeCandidateScore(id, rt)
            });

            if (parent != 0)
                runtimeEdges.Add(new { from = $"0x{parent:X8}", to = $"0x{id:X8}", offset = parentOffset, depth, type = rt });

            ClassifyRuntime(id, rt, runtimeScripts, runtimeStates, runtimeEffects, runtimeSetups, runtimeVisuals, runtimeTextures, runtimeMaterials);
            if (depth >= 7) continue;

            foreach (var r in refs)
            {
                string ct = r.Type ?? TypeName(r.Id) ?? "Unknown";
                runtimeEdges.Add(new { from = $"0x{id:X8}", to = $"0x{r.Id:X8}", offset = r.Offset, depth = depth + 1, type = ct });
                ClassifyRuntime(r.Id, ct, runtimeScripts, runtimeStates, runtimeEffects, runtimeSetups, runtimeVisuals, runtimeTextures, runtimeMaterials);
                if (!runtimeSeen.Contains(r.Id)) runtimeQueue.Enqueue((r.Id, depth + 1, id, r.Offset));
            }
        }

        // Rank runtime candidates by effect-likeness and proximity to the selected object.
        // The desktop app can preview these immediately, so a user should only need to report
        // what they see instead of repeatedly uploading JSON between each probe iteration.
        var runtimeCandidateRows = runtimeRows
            .Select(x => JsonSerializer.SerializeToElement(x))
            .Where(x => x.TryGetProperty("score", out var sc) && sc.GetInt32() > 0)
            .OrderByDescending(x => x.GetProperty("score").GetInt32())
            .ThenBy(x => x.GetProperty("depth").GetInt32())
            .Take(80)
            .Select(x => new
            {
                id = x.GetProperty("id").GetString(),
                type = x.GetProperty("type").GetString(),
                dat = x.GetProperty("dat").GetString(),
                depth = x.GetProperty("depth").GetInt32(),
                score = x.GetProperty("score").GetInt32(),
                parent = x.TryGetProperty("parent", out var pp) && pp.ValueKind != JsonValueKind.Null ? pp.GetString() : null,
                offset = x.GetProperty("offset").GetInt32()
            }).ToArray();

        // 1.7.2: keep texture candidates in provenance order instead of HashSet/ID order.
        // Persistent auras are more useful when the texture closest
        // to the selected item's runtime/state branch is previewed before wider-graph textures.
        var rankedRuntimeTextureIds = runtimeCandidateRows
            .Where(x => x.id != null && (x.type?.Contains("RenderTexture", StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderBy(x => x.depth)
            .ThenByDescending(x => x.score)
            .Select(x => x.id!)
            .Concat(runtimeTextures.Where(x => x != 0x40000000).OrderBy(x => x).Select(x => $"0x{x:X8}"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Merge runtime textures into the automatic preview pool. Keep static candidates too,
        // but runtime-discovered textures are listed first by the desktop client.
        foreach (var tid in runtimeTextures)
            if (tid != 0x40000000) auraCandidateTextures.Add(tid);

        return Ok(new
        {
            version = "1.7.2",
            packedVisualReferenceScan = true,
            packedAuxiliarySetupScan = true,
            strictGraphFiltering = true,
            renderAuxiliaryModels = false,
            baseMaterialProbeEnabled = true,
            focusedAuraMaterialTraversal = true,
            runtimeEffectInvestigatorEnabled = true,
            selfContainedAuraInvestigation = true,
            runtimeRoots = runtimeRoots.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            runtimeScriptAnchors = runtimeScripts.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            runtimeStateAnchors = runtimeStates.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            runtimeEffectCandidates = runtimeEffects.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            runtimeCandidateSetups = runtimeSetups.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            runtimeCandidateVisuals = runtimeVisuals.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            runtimeCandidateTextures = rankedRuntimeTextureIds,
            runtimeCandidateMaterials = runtimeMaterials.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            runtimeCandidateRanking = runtimeCandidateRows,
            runtimeNodes = runtimeRows,
            runtimeEdges,
            investigationStages = new[]
            {
                "1. Trace selected DbProperties/EntityDescription into ScriptTable/Scriptlet/WState/event records.",
                "2. Rank effect/particle/visual/setup/material/texture records by provenance and distance from the selected object.",
                "3. Auto-preview runtime-discovered texture candidates at the weapon tips.",
                "4. Identify runtime Setup candidates with provenance so renderable ones can be exported without another discovery pass.",
                "5. Preserve a complete local investigation bundle so the next iteration can be driven from one report rather than repeated narrow probes."
            },
            auraRootIds = auraRootIds.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            auraCandidateTextures = auraCandidateTextures.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            auraMaterialChain = auraChainRows,
            baseMaterialProbeRoots = baseProbeRoots.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            baseMaterialProbe,
            seeds = seeds.Select(x => $"0x{x:X8}").ToArray(),
            scannedNodes = seen.Count,
            hasVisualEffects = particleIds.Count > 0 || auxiliaryMeshIds.Count > 0 || auraCandidateTextures.Count > 0 || runtimeTextures.Count > 0,
            effectMode = particleIds.Count > 0 ? "particle" : ((auraCandidateTextures.Count > 0 || runtimeTextures.Count > 0) ? "aura-texture" : (auxiliaryMeshIds.Count > 0 ? "auxiliary-diagnostic-only" : "none")),
            auxiliaryVisualDescriptions = auxiliaryVisualIds.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            auxiliaryReferencedSetups = auxiliarySetupIds.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            auxiliaryEffectSetups = renderableAuxiliarySetupIds.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            auxiliaryEffectMeshes = auxiliaryMeshIds.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            auxiliaryEffectMaterials = auxiliaryMaterialIds.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            auxiliaryEffectSurfaces = auxiliarySurfaceIds.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            auxiliaryEffectTextures = auxiliaryTextureIds.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            particleDescriptions = particleIds.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            particleTextures = particleTextures.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            particleSurfaces = particleSurfaces.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            particleMaterials = particleMaterials.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            particleMeshes = particleMeshes.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            note = auxiliaryMeshIds.Count > 0
                ? "Auxiliary RenderMesh branches were resolved and are exposed alongside texture/runtime aura candidates. They are not assumed to be the entire effect; persistent energy can be texture/mask driven."
                : particleIds.Count > 0
                    ? "Particle-system branch resolved from the selected object's verified render/effect chain, including packed/unaligned VisualDescription references. Global scene/world records are filtered out."
                    : (auraCandidateTextures.Count > 0 || runtimeTextures.Count > 0)
                        ? "Texture/runtime aura candidates were resolved even though no standalone particle or auxiliary RenderMesh branch was proven."
                        : "No verified particle, aura-texture, or auxiliary RenderMesh visual-effect branch was found.",
            nodes = nodeRows,
            edges = edgeRows
        });
    }


    static IEnumerable<string> ExtractAsciiStrings(byte[] bytes, int max)
    {
        if (bytes == null || bytes.Length == 0 || max <= 0) yield break;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        int emitted = 0;
        for (int i = 0; i <= bytes.Length; i++)
        {
            int b = i < bytes.Length ? bytes[i] : 0;
            if (b >= 32 && b <= 126) sb.Append((char)b);
            else
            {
                if (sb.Length >= 4)
                {
                    var text = sb.ToString().Trim();
                    if (text.Length > 96) text = text[..96];
                    if (text.Length >= 4 && seen.Add(text))
                    {
                        yield return text;
                        emitted++;
                        if (emitted >= max) yield break;
                    }
                }
                sb.Clear();
            }
        }
    }

    static bool IsRuntimeInteresting(uint id, string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return false;
        byte p = (byte)(id >> 24);
        if (p is 0x02 or 0x04 or 0x06 or 0x07 or 0x1F or 0x20 or 0x2B or 0x30 or 0x31 or 0x39 or 0x40 or 0x41 or 0x47) return true;
        string[] names = { "Script", "WState", "State", "Event", "Effect", "Particle", "Visual", "Setup", "Entity", "Appearance", "Material", "Texture", "Surface", "RenderMesh" };
        return names.Any(n => type.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    static int RuntimeCandidateScore(uint id, string type)
    {
        byte p = (byte)(id >> 24);
        if (IsParticleDescription(id, type)) return 1000;
        if (type.Contains("Effect", StringComparison.OrdinalIgnoreCase) || type.Contains("Particle", StringComparison.OrdinalIgnoreCase)) return 900;
        if (type.Contains("ScriptTable", StringComparison.OrdinalIgnoreCase) || type.Contains("Scriptlet", StringComparison.OrdinalIgnoreCase)) return 760;
        if (type.Contains("WState", StringComparison.OrdinalIgnoreCase) || type.Contains("State", StringComparison.OrdinalIgnoreCase)) return 700;
        if (p == 0x04 || type.Contains("Setup", StringComparison.OrdinalIgnoreCase)) return 620;
        if (p == 0x1F || type.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase)) return 580;
        if (p == 0x40 || type.Contains("RenderTexture", StringComparison.OrdinalIgnoreCase)) return 540;
        if (p is 0x2B or 0x30 or 0x31 || type.Contains("Material", StringComparison.OrdinalIgnoreCase)) return 460;
        if (p == 0x06 || type.Contains("RenderMesh", StringComparison.OrdinalIgnoreCase)) return 420;
        if (p == 0x47 || type.Contains("EntityDescription", StringComparison.OrdinalIgnoreCase)) return 360;
        return 0;
    }

    static void ClassifyRuntime(uint id, string type,
        HashSet<uint> scripts, HashSet<uint> states, HashSet<uint> effects,
        HashSet<uint> setups, HashSet<uint> visuals, HashSet<uint> textures, HashSet<uint> materials)
    {
        byte p = (byte)(id >> 24);
        if (type.Contains("Script", StringComparison.OrdinalIgnoreCase)) scripts.Add(id);
        if (type.Contains("WState", StringComparison.OrdinalIgnoreCase) || type.Contains("State", StringComparison.OrdinalIgnoreCase)) states.Add(id);
        if (IsParticleDescription(id, type) || type.Contains("Effect", StringComparison.OrdinalIgnoreCase) || type.Contains("Particle", StringComparison.OrdinalIgnoreCase)) effects.Add(id);
        if (p == 0x04 || type.Contains("Setup", StringComparison.OrdinalIgnoreCase)) setups.Add(id);
        if (p == 0x1F || type.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase)) visuals.Add(id);
        if (p == 0x40 || type.Contains("RenderTexture", StringComparison.OrdinalIgnoreCase)) textures.Add(id);
        if (p is 0x2B or 0x30 or 0x31 || type.Contains("Material", StringComparison.OrdinalIgnoreCase)) materials.Add(id);
    }

    static bool IsDbProperties(uint id) => id >= 0x78000000 && id <= 0x7FFFFFFF;
    static bool IsParticleDescription(uint id, string type) => (id >> 24) == 0x39 || type.Contains("PSDescription", StringComparison.OrdinalIgnoreCase);

    static bool ShouldFollowStructuredSeed(uint id, string type)
    {
        if (string.IsNullOrWhiteSpace(type) || IsNoiseType(type)) return false;
        byte p = (byte)(id >> 24);
        return p is 0x02 or 0x04 or 0x06 or 0x1F or 0x20 or 0x2B or 0x30 or 0x31 or 0x39 or 0x40 or 0x41 or 0x47 or 0x66;
    }

    static bool ShouldFollowGraphRef(uint id, string type, string currentType)
    {
        if (string.IsNullOrWhiteSpace(type) || IsNoiseType(type)) return false;
        byte p = (byte)(id >> 24);
        bool renderish = p is 0x02 or 0x04 or 0x06 or 0x1F or 0x20 or 0x2B or 0x30 or 0x31 or 0x39 or 0x40 or 0x41 or 0x47 or 0x66;
        if (!renderish) return false;

        if (currentType.Contains("DbProperties", StringComparison.OrdinalIgnoreCase))
            return p is 0x04 or 0x1F or 0x20 or 0x39 or 0x47 or 0x06 or 0x2B or 0x30 or 0x31 or 0x40 or 0x41 or 0x66;

        if (currentType.Contains("Setup", StringComparison.OrdinalIgnoreCase))
            return p is 0x02 or 0x04 or 0x06 or 0x1F or 0x20 or 0x39 or 0x47 or 0x2B or 0x30 or 0x31 or 0x40 or 0x41 or 0x66;

        if (currentType.Contains("Scene", StringComparison.OrdinalIgnoreCase))
            return p is 0x04 or 0x06 or 0x1F or 0x20 or 0x39 or 0x47 or 0x2B or 0x30 or 0x31 or 0x40 or 0x41;

        if (currentType.Contains("Appearance", StringComparison.OrdinalIgnoreCase)
            || currentType.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase)
            || currentType.Contains("EntityDescription", StringComparison.OrdinalIgnoreCase))
            return p is 0x04 or 0x06 or 0x1F or 0x20 or 0x39 or 0x47 or 0x2B or 0x30 or 0x31 or 0x40 or 0x41 or 0x66;

        if (currentType.Contains("RenderMesh", StringComparison.OrdinalIgnoreCase))
            return p is 0x2B or 0x30 or 0x31 or 0x40 or 0x41;

        if (currentType.Contains("Material", StringComparison.OrdinalIgnoreCase)
            || currentType.Contains("RenderSurface", StringComparison.OrdinalIgnoreCase))
            return p is 0x2B or 0x30 or 0x31 or 0x40 or 0x41;

        if (currentType.Contains("PSDescription", StringComparison.OrdinalIgnoreCase))
            return p is 0x39 or 0x2B or 0x30 or 0x31 or 0x40 or 0x41 or 0x06 or 0x47 or 0x1F or 0x20 or 0x04;

        return false;
    }

    static bool IsNoiseType(string type)
    {
        string[] noisy =
        {
            "CellMesh", "KeyMap", "WState", "Region", "Terrain", "TerrainTypeTable", "SkyDescription",
            "FogDescription", "DayDescription", "SoundInfo", "Sound", "Scriptlet", "ScriptTable",
            "ActionMap", "MasterProperty", "GameTime", "UiLayout", "StringTable", "LandblockData",
            "ComputeShader", "EnumMapper", "DidMapper", "Font", "EncounterDescription"
        };
        return noisy.Any(n => type.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    static bool ShouldFollowParticleBranch(uint id, string type)
    {
        byte p = (byte)(id >> 24);
        return IsParticleDescription(id, type)
            || p is 0x2B or 0x30 or 0x31 or 0x40 or 0x41 or 0x06
            || type.Contains("Material", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Texture", StringComparison.OrdinalIgnoreCase)
            || type.Contains("Surface", StringComparison.OrdinalIgnoreCase)
            || type.Contains("RenderMesh", StringComparison.OrdinalIgnoreCase);
    }

    static IEnumerable<RefHit> ScanStructuredPropertyReferences(uint root)
    {
        object properties = null;
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

    static void WalkJson(JsonElement e, HashSet<uint> values)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) WalkJson(p.Value, values);
                break;
            case JsonValueKind.Array:
                foreach (var v in e.EnumerateArray()) WalkJson(v, values);
                break;
            case JsonValueKind.Number:
                if (e.TryGetUInt32(out var n) && TypeName(n) != null) values.Add(n);
                break;
            case JsonValueKind.String:
                var s = e.GetString();
                if (string.IsNullOrWhiteSpace(s)) break;
                s = s.Trim();
                uint n2;
                bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? uint.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out n2)
                    : uint.TryParse(s, out n2);
                if (ok && TypeName(n2) != null) values.Add(n2);
                break;
        }
    }

    static bool ShouldScanUnaligned(string type)
        => type.Contains("VisualDescription", StringComparison.OrdinalIgnoreCase)
        || type.Contains("EntityDescription", StringComparison.OrdinalIgnoreCase);

    static IEnumerable<RefHit> ScanLoadableReferences(byte[] bytes, uint self, bool unaligned)
    {
        var seen = new HashSet<uint>();
        // Most records use 4-byte aligned DIDs. VisualDescription / EntityDescription are tiny
        // packed records. 1.4.1.28 also checks every byte for Setup records reached specifically
        // through an auxiliary Scene -> EntityDescription -> VisualDescription branch.
        // Every candidate is still required to map to a known DDO record type and load from a DAT.
        int step = unaligned ? 1 : 4;
        for (int o = 0; o + 4 <= bytes.Length; o += step)
        {
            uint id = BitConverter.ToUInt32(bytes, o);
            if (id == 0 || id == self || !seen.Add(id)) continue;
            var type = TypeName(id);
            if (type == null) continue;
            if (!TryLoad(id, out var dat, out var child) || child == null) continue;
            yield return new RefHit(id, o, type, dat);
        }
    }

    static string TypeName(uint id)
    {
        foreach (var r in DatSource.IdRanges)
            if (id >= r.Minimum && id <= r.Maximum)
                return string.IsNullOrWhiteSpace(r.Name) ? r.Description : r.Name;
        return null;
    }

    static bool TryLoad(uint id, out string dat, out byte[] bytes)
    {
        dat = ""; bytes = null;
        var sources = new (string Name, IDatFile Dat)[]
        {
            ("GameLogic", DatSource.GameLogicDat), ("General", DatSource.GeneralDat), ("Mesh", DatSource.Mesh),
            ("Highres", DatSource.Highres), ("Surface", DatSource.SurfaceDat), ("Anim", DatSource.AnimDat),
            ("LocalEnglish", DatSource.LocalEnglishDat), ("Sound", DatSource.SoundDat),
            ("Cell1", DatSource.Cell1), ("Cell2", DatSource.Cell2), ("Cell3", DatSource.Cell3), ("Cell4", DatSource.Cell4),
            ("Map1", DatSource.Map1), ("Map2", DatSource.Map2), ("Map3", DatSource.Map3), ("Map4", DatSource.Map4)
        };
        foreach (var s in sources)
        {
            try
            {
                bytes = s.Dat?.GetFileContents(id);
                if (bytes != null) { dat = s.Name; return true; }
            }
            catch { }
        }
        bytes = null; return false;
    }

    static void AddSeed(string raw, List<uint> ids)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        raw = raw.Trim();
        uint id;
        bool ok = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id)
            : uint.TryParse(raw, out id);
        if (ok) ids.Add(id);
    }
}
