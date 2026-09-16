using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using VoK.Sdk.Common;

namespace DdoDatApi.Controllers;

/// <summary>
/// 1.4.1.13 relationship-hop inspector (carried forward from 1.4.1.8).
/// Starts with small structured records that contain an exact requested animation ID,
/// then follows only validated aligned DDO object references through General/GameLogic.
/// This deliberately excludes megabyte-scale render/material blobs that produced false positives.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class AnimationRelationshipController : ControllerBase
{
    static readonly object Gate = new();
    static List<uint>? _generalIds;
    static List<uint>? _gameLogicIds;

    [HttpGet("setup/{setupId}")]
    public IActionResult Inspect(string setupId, string animationId = "0x05000051", int trackCount = 58, int maxDepth = 3, int maxNodes = 256, bool refresh = false)
    {
        if (!TryId(setupId, out var setup)) return BadRequest("Invalid Setup id.");
        if (!TryId(animationId, out var animation)) return BadRequest("Invalid animation id.");
        maxDepth = Math.Clamp(maxDepth, 1, 5);
        maxNodes = Math.Clamp(maxNodes, 32, 1024);

        List<uint> generalIds, gameLogicIds;
        lock (Gate)
        {
            if (_generalIds == null || refresh) _generalIds = DiscoverIds(DatSource.GeneralDat, SafeGetGeneral);
            if (_gameLogicIds == null || refresh) _gameLogicIds = DiscoverIds(DatSource.GameLogicDat, SafeGetGameLogic);
            generalIds = _generalIds;
            gameLogicIds = _gameLogicIds;
        }
        var generalSet = generalIds.ToHashSet();
        var gameLogicSet = gameLogicIds.ToHashSet();

        var seeds = new List<Node>();
        int scanned = 0;
        ScanSeeds("General", generalIds, SafeGetGeneral);
        ScanSeeds("GameLogic", gameLogicIds, SafeGetGameLogic);

        // Always include the selected Setup as a root/context node.
        var setupBytes = SafeGetGeneral(setup);
        if (setupBytes != null)
            seeds.Add(BuildNode("General", setup, setupBytes, 0, "selected setup"));

        // Deduplicate before BFS.
        var seedKeys = new HashSet<string>();
        seeds = seeds.Where(s => seedKeys.Add(Key(s.Dat, s.IdValue))).ToList();

        var nodes = new Dictionary<string, Node>();
        var edges = new List<Edge>();
        var queue = new Queue<(Node Node, int Depth)>();
        foreach (var s in seeds.OrderByDescending(SeedPriority).Take(64))
        {
            nodes[Key(s.Dat, s.IdValue)] = s;
            queue.Enqueue((s, 0));
        }

        while (queue.Count > 0 && nodes.Count < maxNodes)
        {
            var (node, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;
            var bytes = node.Dat == "General" ? SafeGetGeneral(node.IdValue) : SafeGetGameLogic(node.IdValue);
            if (bytes == null) continue;

            foreach (var r in ExtractValidatedReferences(bytes, generalSet, gameLogicSet, animation).Take(192))
            {
                if (r.Kind == "Anim")
                {
                    edges.Add(new Edge(node.Dat, node.Id, "Anim", $"0x{r.Id:X8}", r.Offset, TypeName(r.Id) ?? "Animation"));
                    continue;
                }
                string targetDat = r.Kind;
                uint targetId = r.Id;
                string k = Key(targetDat, targetId);
                edges.Add(new Edge(node.Dat, node.Id, targetDat, $"0x{targetId:X8}", r.Offset, TypeName(targetId) ?? "Unknown"));
                if (nodes.ContainsKey(k) || nodes.Count >= maxNodes) continue;
                var targetBytes = targetDat == "General" ? SafeGetGeneral(targetId) : SafeGetGameLogic(targetId);
                if (targetBytes == null) continue;

                // Keep the graph useful: follow small/medium metadata records and state/script/animator objects.
                string type = TypeName(targetId) ?? "Unknown";
                if (!ShouldFollow(type, targetBytes.Length, targetId == setup)) continue;
                var child = BuildNode(targetDat, targetId, targetBytes, depth + 1, "relationship hop");
                nodes[k] = child;
                queue.Enqueue((child, depth + 1));
            }
        }

        var paths = FindPaths(nodes.Values.ToList(), edges, setup, animation, maxDepth + 2);
        var strongMaps = nodes.Values
            .SelectMany(n => n.CandidateTrackMaps.Select(m => new { n.Dat, n.Id, n.Type, map = m }))
            .Where(x => x.map.Unique >= Math.Max(24, (int)Math.Ceiling(trackCount * 0.70)))
            .OrderByDescending(x => x.map.Score)
            .Take(64)
            .ToArray();

        return Ok(new
        {
            version = "1.4.1.17",
            setup = $"0x{setup:X8}",
            animation = $"0x{animation:X8}",
            trackCount,
            scan = new { scannedRecords = scanned, seedCount = seeds.Count, graphNodes = nodes.Count, graphEdges = edges.Count, maxDepth },
            seeds = seeds.OrderByDescending(SeedPriority).Take(64).ToArray(),
            nodes = nodes.Values.OrderBy(n => n.Depth).ThenByDescending(SeedPriority).ToArray(),
            edges = edges.Distinct().ToArray(),
            paths,
            strongCandidateTrackMaps = strongMaps,
            guidance = new
            {
                note = "Only exact aligned requested-animation references in small structured metadata records are used as animation seeds. Large RenderMaterial/texture/mesh blobs are excluded from seed selection.",
                target = "Look for a short path connecting ScriptTable/Scriptlet/WState/Animator-like nodes to the selected Setup and animation, then validate any high-uniqueness 58-entry joint map on that path.",
                especiallyInteresting = "Scriptlet 0x0C0000B2 was a high-value 1.4.1.7 lead because the requested animation appeared at the final 4 bytes of a ~156-byte record."
            }
        });

        void ScanSeeds(string dat, List<uint> ids, Func<uint, byte[]?> getter)
        {
            foreach (var id in ids)
            {
                scanned++;
                var b = getter(id);
                if (b == null || b.Length < 4) continue;
                string type = TypeName(id) ?? "Unknown";
                // The core 1.4.1.8 change: do not even consider giant render/material data as relationship seeds.
                if (!IsStructuredSeedType(type) && b.Length > 4096) continue;
                var offsets = FindAlignedUInt32(b, animation).Take(32).ToArray();
                if (offsets.Length == 0) continue;
                if (!IsStructuredSeedType(type) && b.Length > 1024) continue;
                var n = BuildNode(dat, id, b, 0, "exact animation seed");
                n.RequestedAnimationOffsets = offsets;
                seeds.Add(n);
            }
        }

        Node BuildNode(string dat, uint id, byte[] b, int depth, string relation)
        {
            var animOffsets = FindAlignedUInt32(b, animation).Take(32).ToArray();
            var setupOffsets = FindAlignedUInt32(b, setup).Take(32).ToArray();
            return new Node
            {
                Dat = dat,
                IdValue = id,
                Id = $"0x{id:X8}",
                Type = TypeName(id) ?? "Unknown",
                Depth = depth,
                Relation = relation,
                ByteLength = b.Length,
                Sha1 = Convert.ToHexString(SHA1.HashData(b)),
                RequestedAnimationOffsets = animOffsets,
                SetupOffsets = setupOffsets,
                CandidateTrackMaps = FindStrongMaps(b, trackCount, 78).OrderByDescending(m => m.Score).Take(16).ToArray(),
                AnimationContext = animOffsets.Select(o => HexWindow(b, o, 32)).ToArray(),
                FirstBytes = Convert.ToHexString(b.AsSpan(0, Math.Min(256, b.Length)))
            };
        }
    }

    [HttpGet("record/{dat}/{id}")]
    public IActionResult Record(string dat, string id, string animationId = "0x05000051", int trackCount = 58, int jointCount = 78)
    {
        if (!TryId(id, out var did) || !TryId(animationId, out var animation)) return BadRequest("Invalid id.");
        byte[]? b = dat.Equals("GameLogic", StringComparison.OrdinalIgnoreCase) ? SafeGetGameLogic(did) : SafeGetGeneral(did);
        if (b == null) return NotFound();
        var refs = ExtractAllAlignedIds(b).Take(4096).Select(x => new { x.Offset, id = $"0x{x.Id:X8}", type = TypeName(x.Id) }).ToArray();
        return Ok(new
        {
            dat,
            id = $"0x{did:X8}",
            type = TypeName(did),
            byteLength = b.Length,
            requestedAnimationOffsets = FindAlignedUInt32(b, animation).ToArray(),
            candidateTrackMaps = FindStrongMaps(b, trackCount, jointCount).OrderByDescending(m => m.Score).Take(64).ToArray(),
            alignedReferences = refs,
            firstBytes = Convert.ToHexString(b.AsSpan(0, Math.Min(4096, b.Length)))
        });
    }

    static bool IsStructuredSeedType(string type) =>
        type.Contains("Script", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("State", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("Animator", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("Animation", StringComparison.OrdinalIgnoreCase) ||
        type.Contains("Setup", StringComparison.OrdinalIgnoreCase);

    static bool ShouldFollow(string type, int len, bool isSetup) => isSetup || IsStructuredSeedType(type) ||
        (len <= 8192 && !type.Contains("Material", StringComparison.OrdinalIgnoreCase) &&
         !type.Contains("Texture", StringComparison.OrdinalIgnoreCase) &&
         !type.Contains("Mesh", StringComparison.OrdinalIgnoreCase) &&
         !type.Contains("Surface", StringComparison.OrdinalIgnoreCase) &&
         !type.Contains("Image", StringComparison.OrdinalIgnoreCase));

    static int SeedPriority(Node n)
    {
        int p = 0;
        if (n.Type.Contains("Scriptlet", StringComparison.OrdinalIgnoreCase)) p += 500;
        if (n.Type.Contains("ScriptTable", StringComparison.OrdinalIgnoreCase)) p += 450;
        if (n.Type.Contains("Animator", StringComparison.OrdinalIgnoreCase)) p += 700;
        if (n.Type.Contains("State", StringComparison.OrdinalIgnoreCase)) p += 600;
        if (n.Type.Contains("Setup", StringComparison.OrdinalIgnoreCase)) p += 300;
        if (n.ByteLength <= 512) p += 300;
        else if (n.ByteLength <= 4096) p += 150;
        p += n.RequestedAnimationOffsets.Length * 20;
        return p;
    }

    static IEnumerable<ValidatedRef> ExtractValidatedReferences(byte[] b, HashSet<uint> general, HashSet<uint> gameLogic, uint animation)
    {
        var seen = new HashSet<string>();
        for (int off = 0; off + 4 <= b.Length; off += 4)
        {
            uint id = BitConverter.ToUInt32(b, off);
            if (id == 0) continue;
            string? kind = null;
            if (id == animation || ((id >> 24) == 0x05 && SafeGetAnim(id) != null)) kind = "Anim";
            else if (general.Contains(id)) kind = "General";
            else if (gameLogic.Contains(id)) kind = "GameLogic";
            if (kind == null) continue;
            if (seen.Add($"{kind}:{id:X8}")) yield return new ValidatedRef(off, id, kind);
        }
    }

    static IEnumerable<(int Offset, uint Id)> ExtractAllAlignedIds(byte[] b)
    {
        for (int off = 0; off + 4 <= b.Length; off += 4)
        {
            uint id = BitConverter.ToUInt32(b, off);
            if (id != 0 && TypeName(id) != null) yield return (off, id);
        }
    }

    static IEnumerable<int> FindAlignedUInt32(byte[] b, uint wanted)
    {
        for (int off = 0; off + 4 <= b.Length; off += 4)
            if (BitConverter.ToUInt32(b, off) == wanted) yield return off;
    }

    static IEnumerable<MapCandidate> FindStrongMaps(byte[] b, int trackCount, int jointCount)
    {
        var seen = new HashSet<string>();
        foreach (int countWidth in new[] { 4, 2 })
        {
            for (int off = 0; off + countWidth <= b.Length; off++)
            {
                int count = countWidth == 4 ? BitConverter.ToInt32(b, off) : BitConverter.ToUInt16(b, off);
                if (count != trackCount) continue;
                foreach (int width in new[] { 1, 2, 4 })
                {
                    if (countWidth == 2 && width == 4) continue;
                    int start = off + countWidth;
                    if ((long)start + (long)trackCount * width > b.Length) continue;
                    var values = new int[trackCount];
                    bool valid = true;
                    for (int i = 0; i < trackCount; i++)
                    {
                        int p = start + i * width;
                        int v = width == 1 ? b[p] : width == 2 ? BitConverter.ToUInt16(b, p) : BitConverter.ToInt32(b, p);
                        values[i] = v;
                        if (v < 0 || v >= jointCount) { valid = false; break; }
                    }
                    if (!valid) continue;
                    int unique = values.Distinct().Count();
                    // A real one-track-per-bone binding should be mostly unique. This rejects the repeating
                    // 0/16/18/70 byte patterns that polluted 1.4.1.7 RenderMaterial candidates.
                    if (unique < Math.Max(24, (int)Math.Ceiling(trackCount * 0.70))) continue;
                    int seq = 0;
                    for (int i = 1; i < values.Length; i++) if (values[i] == values[i - 1] + 1) seq++;
                    int score = 700 + unique * 8 + seq * 3 + (countWidth == 4 ? 100 : 0);
                    string key = $"{start}:{width}:{string.Join(',', values)}";
                    if (!seen.Add(key)) continue;
                    yield return new MapCandidate(off, start, width, countWidth == 4 ? "int32-count-prefix" : "uint16-count-prefix", values.Length, values.Min(), values.Max(), unique, seq, score, values);
                }
            }
        }
    }

    static string HexWindow(byte[] b, int center, int radius)
    {
        int start = Math.Max(0, center - radius);
        int end = Math.Min(b.Length, center + 4 + radius);
        return $"0x{start:X}:" + Convert.ToHexString(b.AsSpan(start, end - start));
    }

    static object[] FindPaths(List<Node> nodes, List<Edge> edges, uint setup, uint animation, int maxDepth)
    {
        string setupHex = $"0x{setup:X8}";
        string animHex = $"0x{animation:X8}";
        var adj = edges.GroupBy(e => $"{e.FromDat}:{e.FromId}").ToDictionary(g => g.Key, g => g.ToList());
        var results = new List<object>();
        foreach (var n in nodes.Where(n => n.RequestedAnimationOffsets.Length > 0 || n.Id == setupHex))
        {
            var q = new Queue<(string Key, List<string> Path, int Depth)>();
            q.Enqueue(($"{n.Dat}:{n.Id}", new List<string> { $"{n.Dat}:{n.Id}({n.Type})" }, 0));
            var seen = new HashSet<string>();
            while (q.Count > 0 && results.Count < 64)
            {
                var cur = q.Dequeue();
                if (!seen.Add(cur.Key) || cur.Depth >= maxDepth) continue;
                if (!adj.TryGetValue(cur.Key, out var outs)) continue;
                foreach (var e in outs)
                {
                    var p = new List<string>(cur.Path) { $"{e.ToDat}:{e.ToId}({e.ToType})" };
                    if (e.ToId == setupHex || e.ToId == animHex)
                        results.Add(new { start = cur.Path[0], end = e.ToId, path = p.ToArray() });
                    if (e.ToDat != "Anim") q.Enqueue(($"{e.ToDat}:{e.ToId}", p, cur.Depth + 1));
                }
            }
        }
        return results.ToArray();
    }

    static byte[]? SafeGetGeneral(uint id) { try { return DatSource.GeneralDat?.GetFileContents(id); } catch { return null; } }
    static byte[]? SafeGetGameLogic(uint id) { try { return DatSource.GameLogicDat?.GetFileContents(id); } catch { return null; } }
    static byte[]? SafeGetAnim(uint id) { try { return DatSource.AnimDat?.GetFileContents(id); } catch { return null; } }

    static List<uint> DiscoverIds(object? root, Func<uint, byte[]?> getter)
    {
        var found = new HashSet<uint>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(root, found, visited, 0, getter);
        return found.OrderBy(x => x).ToList();
    }

    static void Walk(object? value, HashSet<uint> dst, HashSet<object> visited, int depth, Func<uint, byte[]?> getter)
    {
        if (value == null || depth > 6) return;
        var t = value.GetType();
        if (t.IsPrimitive || value is string || value is byte[] || value is Type) return;
        if (!t.IsValueType && !visited.Add(value)) return;
        if (TryNumeric(value, out var direct)) AddIfValid(direct, dst, getter);
        if (value is IDictionary dict)
        {
            int n = 0;
            foreach (DictionaryEntry e in dict)
            {
                if (++n > 3_000_000) break;
                if (TryNumeric(e.Key, out var k)) AddIfValid(k, dst, getter);
                Walk(e.Value, dst, visited, depth + 1, getter);
            }
            return;
        }
        if (value is IEnumerable seq)
        {
            int n = 0;
            foreach (var item in seq)
            {
                if (++n > 3_000_000) break;
                if (TryNumeric(item, out var k)) AddIfValid(k, dst, getter);
                else if (item != null)
                {
                    foreach (var name in new[] { "Id", "ID", "Did", "DID", "FileId", "FileID", "Key" })
                        if (TryNumeric(Get(item, name), out var mid)) AddIfValid(mid, dst, getter);
                    if (depth < 4) Walk(item, dst, visited, depth + 1, getter);
                }
            }
            return;
        }
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            try { Walk(f.GetValue(value), dst, visited, depth + 1, getter); } catch { }
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
            try { Walk(p.GetValue(value), dst, visited, depth + 1, getter); } catch { }
        }
    }

    static void AddIfValid(uint id, HashSet<uint> dst, Func<uint, byte[]?> getter)
    {
        if (id == 0 || dst.Contains(id)) return;
        if (getter(id) != null) dst.Add(id);
    }

    static object? Get(object? o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        try { return t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o)
            ?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o); }
        catch { return null; }
    }

    static bool TryNumeric(object? o, out uint value)
    {
        value = 0;
        try
        {
            if (o is uint u) { value = u; return true; }
            if (o is int i && i >= 0) { value = (uint)i; return true; }
            if (o is long l && l >= 0 && l <= uint.MaxValue) { value = (uint)l; return true; }
            if (o is ulong ul && ul <= uint.MaxValue) { value = (uint)ul; return true; }
            if (o is ushort us) { value = us; return true; }
        }
        catch { }
        return false;
    }

    static string? TypeName(uint id)
    {
        var r = DatSource.IdRanges.FirstOrDefault(x => id >= x.Minimum && id <= x.Maximum);
        return r?.Name;
    }

    static bool TryId(string s, out uint id)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }

    static string Key(string dat, uint id) => $"{dat}:{id:X8}";

    sealed class Node
    {
        public string Dat { get; set; } = "";
        public uint IdValue { get; set; }
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public int Depth { get; set; }
        public string Relation { get; set; } = "";
        public int ByteLength { get; set; }
        public string Sha1 { get; set; } = "";
        public int[] RequestedAnimationOffsets { get; set; } = Array.Empty<int>();
        public int[] SetupOffsets { get; set; } = Array.Empty<int>();
        public MapCandidate[] CandidateTrackMaps { get; set; } = Array.Empty<MapCandidate>();
        public string[] AnimationContext { get; set; } = Array.Empty<string>();
        public string FirstBytes { get; set; } = "";
    }
    sealed record Edge(string FromDat, string FromId, string ToDat, string ToId, int Offset, string ToType);
    sealed record ValidatedRef(int Offset, uint Id, string Kind);
    sealed record MapCandidate(int CountOffset, int Offset, int ElementWidth, string Evidence, int Count, int Min, int Max, int Unique, int SequentialPairs, int Score, int[] Values);
}
