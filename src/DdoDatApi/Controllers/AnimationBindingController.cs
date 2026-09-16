using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using VoK.Sdk.Common;

namespace DdoDatApi.Controllers;

/// <summary>
/// Diagnostic bridge between DDO render Setups and animation records.
/// This intentionally favors auditable evidence over guesses: exact aligned references,
/// validated Anim-DAT IDs, and track-count-sized integer arrays whose values fit the Setup skeleton.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class AnimationBindingController : ControllerBase
{
    static readonly Assembly Sdk = typeof(RenderMesh).Assembly;
    static readonly object Gate = new();
    static List<uint>? _generalIds;
    static List<uint>? _gameLogicIds;

    [HttpGet("setup/{setupId}")]
    public IActionResult InspectSetup(string setupId, int trackCount = 58, string? animationId = "0x05000051", int maxRecords = 700000, bool refresh = false)
    {
        if (!TryId(setupId, out var setup)) return BadRequest("Invalid Setup id.");
        uint? animation = null;
        if (!string.IsNullOrWhiteSpace(animationId))
        {
            if (!TryId(animationId!, out var aid)) return BadRequest("Invalid animation id.");
            animation = aid;
        }

        byte[]? setupBytes;
        try { setupBytes = DatSource.GeneralDat?.GetFileContents(setup); } catch { setupBytes = null; }
        if (setupBytes == null) return NotFound($"Setup 0x{setup:X8} not found in General DAT.");

        var skeleton = ReadSkeleton(setupBytes);
        int jointCount = skeleton.Count;
        if (jointCount <= 0) return UnprocessableEntity(new { setup = $"0x{setup:X8}", error = "No Havok skeleton was parsed from this Setup." });
        trackCount = Math.Clamp(trackCount, 1, Math.Max(1, jointCount));
        maxRecords = Math.Clamp(maxRecords, 1, 750000);

        List<uint> generalIds;
        List<uint> gameLogicIds;
        lock (Gate)
        {
            if (_generalIds == null || refresh)
                _generalIds = DiscoverIds(DatSource.GeneralDat, id => SafeGetGeneral(id));
            if (_gameLogicIds == null || refresh)
                _gameLogicIds = DiscoverIds(DatSource.GameLogicDat, id => SafeGetGameLogic(id));
            generalIds = _generalIds;
            gameLogicIds = _gameLogicIds;
        }

        // 1.4.1.7 deliberately stops treating every valid-looking 0x05 value as an
        // animation relationship.  We only retain records with exact evidence tied to
        // the selected Setup or requested animation, plus the important same-ID metadata
        // record (DDO commonly uses parallel numeric IDs across DATs).
        var records = new List<RecordEvidence>();
        var linkedAnimations = new HashSet<uint>();
        int scanned = 0;
        int exactSetupRecords = 0;
        int exactAnimationRecords = 0;
        int sameIdRecords = 0;

        ScanDat("General", generalIds, SafeGetGeneral);
        ScanDat("GameLogic", gameLogicIds, SafeGetGameLogic);

        var ordered = records
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Dat)
            .ThenBy(r => r.IdValue)
            .Take(512)
            .ToArray();

        return Ok(new
        {
            setup = $"0x{setup:X8}",
            skeleton = new
            {
                jointCount,
                joints = skeleton.Select((j, i) => new { index = i, j.Name, j.Parent }).ToArray()
            },
            requestedTrackCount = trackCount,
            requestedAnimation = animation.HasValue ? $"0x{animation.Value:X8}" : null,
            strictMode = true,
            scannedRecords = scanned,
            discoveredGeneralRecords = generalIds.Count,
            discoveredGameLogicRecords = gameLogicIds.Count,
            exactSetupRecords,
            exactRequestedAnimationRecords = exactAnimationRecords,
            sameIdMetadataRecords = sameIdRecords,
            linkedAnimationIds = linkedAnimations.OrderBy(x => x).Select(x => $"0x{x:X8}").ToArray(),
            records = ordered,
            guidance = new
            {
                strategy = "1.4.1.7 keeps only exact Setup references, exact requested-animation references, and same-numeric-ID metadata records. Broad 0x05 coincidence scoring is disabled.",
                sameId = animation.HasValue ? $"Inspect General/GameLogic record 0x{animation.Value:X8} first. A parallel DbAnimator/animation metadata record with the same numeric ID is high-value evidence." : "No requested animation supplied.",
                mapValidation = $"A plausible {trackCount}-track map must contain only joint indices 0..{jointCount - 1} and is searched only inside evidence-bearing records.",
                next = "If an exact or same-ID record contains a valid track map, use it directly. Otherwise use the small retained record set as seeds for the next typed relationship hop."
            }
        });

        void ScanDat(string datName, List<uint> ids, Func<uint, byte[]?> getter)
        {
            foreach (var id in ids)
            {
                if (scanned >= maxRecords) break;
                scanned++;

                // Cheap type/name filtering happens before expensive secondary analysis, but
                // every record is still checked for the two exact 32-bit values we care about.
                var bytes = getter(id);
                if (bytes == null || bytes.Length < 4) continue;

                var setupOffsets = FindAlignedUInt32(bytes, setup).Take(64).ToArray();
                var animationOffsets = animation.HasValue ? FindAlignedUInt32(bytes, animation.Value).Take(64).ToArray() : Array.Empty<int>();
                bool sameId = animation.HasValue && id == animation.Value;
                if (setupOffsets.Length == 0 && animationOffsets.Length == 0 && !sameId) continue;

                if (setupOffsets.Length > 0) exactSetupRecords++;
                if (animationOffsets.Length > 0) exactAnimationRecords++;
                if (sameId) sameIdRecords++;

                var type = TypeName(id) ?? "Unknown";
                var maps = FindCountPrefixedMaps(bytes, trackCount, jointCount)
                    .OrderByDescending(x => x.Score)
                    .Take(32)
                    .ToArray();

                // Only discover neighboring Anim IDs inside records already proven relevant.
                // This prevents the 12k+ false-positive list produced by 1.4.1.7.
                var animIds = FindValidatedAnimationIds(bytes)
                    .Where(x => !animation.HasValue || x.Id == animation.Value || setupOffsets.Length > 0 || sameId)
                    .Take(128)
                    .ToArray();
                foreach (var x in animIds) linkedAnimations.Add(x.Id);
                if (animation.HasValue && (animationOffsets.Length > 0 || sameId)) linkedAnimations.Add(animation.Value);

                int score = 0;
                if (sameId) score += 2000;
                score += Math.Min(1500, setupOffsets.Length * 750);
                score += Math.Min(1500, animationOffsets.Length * 750);
                if (type.Contains("Animator", StringComparison.OrdinalIgnoreCase)) score += 1000;
                else if (type.Contains("State", StringComparison.OrdinalIgnoreCase)) score += 500;
                else if (type.Contains("Animation", StringComparison.OrdinalIgnoreCase)) score += 500;
                else if (type.Contains("Script", StringComparison.OrdinalIgnoreCase)) score += 200;
                if (maps.Length > 0) score += maps.Max(x => x.Score);

                string relation = sameId && setupOffsets.Length > 0 ? "same-id + setup-ref"
                    : sameId ? "same-id metadata"
                    : setupOffsets.Length > 0 && animationOffsets.Length > 0 ? "setup + requested-animation ref"
                    : setupOffsets.Length > 0 ? "setup ref"
                    : "requested-animation ref";

                records.Add(new RecordEvidence
                {
                    Dat = datName,
                    IdValue = id,
                    Id = $"0x{id:X8}",
                    Type = type,
                    Relation = relation,
                    ByteLength = bytes.Length,
                    Sha1 = Convert.ToHexString(SHA1.HashData(bytes)),
                    SetupReferenceOffsets = setupOffsets,
                    RequestedAnimationReferenceOffsets = animationOffsets,
                    AnimationReferences = animIds.Select(a => new IdReference(a.Offset, $"0x{a.Id:X8}")).ToArray(),
                    CandidateTrackMaps = maps,
                    Score = score,
                    FirstBytes = Convert.ToHexString(bytes.AsSpan(0, Math.Min(256, bytes.Length))),
                    AsciiPreview = AsciiPreview(bytes, 480)
                });
            }
        }
    }

    [HttpGet("record/{dat}/{id}")]
    public IActionResult InspectRecord(string dat, string id, int trackCount = 58, int jointCount = 78)
    {
        if (!TryId(id, out var did)) return BadRequest("Invalid record id.");
        byte[]? bytes = dat.Equals("GameLogic", StringComparison.OrdinalIgnoreCase) ? SafeGetGameLogic(did) : SafeGetGeneral(did);
        if (bytes == null) return NotFound();
        return Ok(new
        {
            dat,
            id = $"0x{did:X8}",
            type = TypeName(did),
            byteLength = bytes.Length,
            animationReferences = FindValidatedAnimationIds(bytes).Select(a => new IdReference(a.Offset, $"0x{a.Id:X8}")).ToArray(),
            candidateTrackMaps = FindCountPrefixedMaps(bytes, trackCount, jointCount).OrderByDescending(x => x.Score).Take(64).ToArray(),
            firstBytes = Convert.ToHexString(bytes.AsSpan(0, Math.Min(2048, bytes.Length)))
        });
    }

    static byte[]? SafeGetGeneral(uint id) { try { return DatSource.GeneralDat?.GetFileContents(id); } catch { return null; } }
    static byte[]? SafeGetGameLogic(uint id) { try { return DatSource.GameLogicDat?.GetFileContents(id); } catch { return null; } }
    static byte[]? SafeGetAnim(uint id) { try { return DatSource.AnimDat?.GetFileContents(id); } catch { return null; } }

    static List<JointInfo> ReadSkeleton(byte[] setupBytes)
    {
        try
        {
            var t = Sdk.GetType("VoK.Sdk.Common.Setup");
            if (t == null) return new List<JointInfo>();
            using var br = new BinaryReader(new MemoryStream(setupBytes));
            object setup;
            try
            {
                // The Setup constructor used by the working GLB exporter in this VoK SDK
                // takes a BinaryReader plus a nullable parse/context argument.  The previous
                // binding inspector only tried the one-argument constructor, which caused
                // valid rigged Setups to fall through as if
                // they had no Havok skeleton.
                setup = Construct(t, br, null);
            }
            catch
            {
                br.BaseStream.Position = 0;
                setup = Construct(t, br);
            }
            var havok = Get(setup, "HavokSetup");
            if (havok == null) return new List<JointInfo>();
            var names = Items(Get(havok, "BoneNames")).Select(x => x.ToString() ?? "").ToList();
            var parents = Items(Get(havok, "ParentIndices")).Select(IntValue).ToList();
            var bones = Items(Get(havok, "Bones")).ToList();
            int count = Math.Min(names.Count, Math.Min(parents.Count, bones.Count / 2));
            var result = new List<JointInfo>(count);
            for (int i = 0; i < count; i++)
            {
                int raw = parents[i];
                int parent = (raw == 3 || (raw & 1) != 0) ? -1 : raw / 2;
                if (parent < -1 || parent >= count || parent == i) parent = -1;
                result.Add(new JointInfo(string.IsNullOrWhiteSpace(names[i]) ? $"Bone_{i:00}" : names[i], parent));
            }
            return result;
        }
        catch { return new List<JointInfo>(); }
    }

    static IEnumerable<(int Offset, uint Id)> FindValidatedAnimationIds(byte[] bytes)
    {
        for (int i = 0; i + 4 <= bytes.Length; i += 4)
        {
            uint id = BitConverter.ToUInt32(bytes, i);
            if ((id >> 24) != 0x05) continue;
            if (SafeGetAnim(id) != null) yield return (i, id);
        }
    }

    static IEnumerable<int> FindAlignedUInt32(byte[] bytes, uint wanted)
    {
        for (int i = 0; i + 4 <= bytes.Length; i += 4)
            if (BitConverter.ToUInt32(bytes, i) == wanted) yield return i;
    }

    static IEnumerable<MapCandidate> FindCountPrefixedMaps(byte[] b, int trackCount, int jointCount)
    {
        var seen = new HashSet<string>();
        for (int off = 0; off + 4 <= b.Length; off++)
        {
            int count = BitConverter.ToInt32(b, off);
            if (count != trackCount) continue;

            foreach (var width in new[] { 1, 2, 4 })
            {
                int start = off + 4;
                long need = (long)trackCount * width;
                if (start + need > b.Length) continue;
                var values = new int[trackCount];
                bool valid = true;
                for (int i = 0; i < trackCount; i++)
                {
                    int p = start + i * width;
                    int v = width switch
                    {
                        1 => b[p],
                        2 => BitConverter.ToUInt16(b, p),
                        _ => BitConverter.ToInt32(b, p)
                    };
                    values[i] = v;
                    if (v < 0 || v >= jointCount) { valid = false; break; }
                }
                if (!valid) continue;
                var candidate = ScoreMap(off, start, width, values, jointCount, "int32-count-prefix");
                if (candidate != null && seen.Add($"{candidate.Offset}:{candidate.ElementWidth}:{string.Join(',', candidate.Values)}"))
                    yield return candidate;
            }
        }

        // Some DDO arrays use a 16-bit count. Keep this lower-confidence path separate.
        for (int off = 0; off + 2 <= b.Length; off++)
        {
            int count = BitConverter.ToUInt16(b, off);
            if (count != trackCount) continue;
            foreach (var width in new[] { 1, 2 })
            {
                int start = off + 2;
                long need = (long)trackCount * width;
                if (start + need > b.Length) continue;
                var values = new int[trackCount];
                bool valid = true;
                for (int i = 0; i < trackCount; i++)
                {
                    int p = start + i * width;
                    int v = width == 1 ? b[p] : BitConverter.ToUInt16(b, p);
                    values[i] = v;
                    if (v < 0 || v >= jointCount) { valid = false; break; }
                }
                if (!valid) continue;
                var candidate = ScoreMap(off, start, width, values, jointCount, "uint16-count-prefix");
                if (candidate != null && seen.Add($"{candidate.Offset}:{candidate.ElementWidth}:{string.Join(',', candidate.Values)}"))
                    yield return candidate;
            }
        }
    }

    static MapCandidate? ScoreMap(int countOffset, int dataOffset, int width, int[] values, int jointCount, string evidence)
    {
        int unique = values.Distinct().Count();
        if (values.Length >= 8 && unique < Math.Max(4, values.Length / 5)) return null;
        int sequential = 0;
        for (int i = 1; i < values.Length; i++) if (values[i] == values[i - 1] + 1) sequential++;
        int score = evidence.StartsWith("int32", StringComparison.Ordinal) ? 500 : 350;
        score += (int)Math.Round(250.0 * unique / Math.Max(1, values.Length));
        score += Math.Min(100, sequential * 4);
        if (values.All(v => v >= 0 && v < jointCount)) score += 150;
        return new MapCandidate
        {
            CountOffset = countOffset,
            Offset = dataOffset,
            ElementWidth = width,
            Evidence = evidence,
            Count = values.Length,
            Min = values.Min(),
            Max = values.Max(),
            Unique = unique,
            SequentialPairs = sequential,
            Score = score,
            Values = values
        };
    }

    static string AsciiPreview(byte[] b, int max)
    {
        var sb = new StringBuilder();
        int emitted = 0;
        foreach (byte x in b)
        {
            if (emitted >= max) break;
            char c = x >= 32 && x <= 126 ? (char)x : ' ';
            sb.Append(c); emitted++;
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

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

    static object Construct(Type t, params object?[] args)
    {
        foreach (var c in t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var p = c.GetParameters();
            if (p.Length != args.Length) continue;
            bool ok = true;
            for (int i = 0; i < p.Length; i++)
            {
                if (args[i] == null) continue;
                if (!p[i].ParameterType.IsAssignableFrom(args[i]!.GetType())) { ok = false; break; }
            }
            if (ok) return c.Invoke(args);
        }
        throw new MissingMethodException(t.FullName);
    }

    static object? Get(object? o, string name)
    {
        if (o == null) return null;
        var t = o.GetType();
        try { return t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o)
            ?? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o); }
        catch { return null; }
    }

    static IEnumerable<object> Items(object? o)
    {
        if (o is not IEnumerable e) yield break;
        foreach (var x in e) if (x != null) yield return x;
    }

    static int IntValue(object? o)
    {
        try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return 0; }
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

    sealed record JointInfo(string Name, int Parent);
    sealed record IdReference(int Offset, string Id);
    sealed class RecordEvidence
    {
        public string Dat { get; set; } = "";
        public uint IdValue { get; set; }
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string Relation { get; set; } = "";
        public int ByteLength { get; set; }
        public string Sha1 { get; set; } = "";
        public int[] SetupReferenceOffsets { get; set; } = Array.Empty<int>();
        public int[] RequestedAnimationReferenceOffsets { get; set; } = Array.Empty<int>();
        public IdReference[] AnimationReferences { get; set; } = Array.Empty<IdReference>();
        public MapCandidate[] CandidateTrackMaps { get; set; } = Array.Empty<MapCandidate>();
        public int Score { get; set; }
        public string FirstBytes { get; set; } = "";
        public string AsciiPreview { get; set; } = "";
    }
    sealed class MapCandidate
    {
        public int CountOffset { get; set; }
        public int Offset { get; set; }
        public int ElementWidth { get; set; }
        public string Evidence { get; set; } = "";
        public int Count { get; set; }
        public int Min { get; set; }
        public int Max { get; set; }
        public int Unique { get; set; }
        public int SequentialPairs { get; set; }
        public int Score { get; set; }
        public int[] Values { get; set; } = Array.Empty<int>();
    }
}
