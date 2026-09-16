using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Numerics;
using VoK.Sdk.Common;

namespace DdoDatApi.Controllers;

/// <summary>
/// 1.4.1.32: matches Animator bone counts and prefilters skeleton families against actual Setup skeleton joint counts.
/// This is intentionally empirical: it inventories real local DAT records and reports
/// exact-count candidates without claiming ownership until a relationship path is proven.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class AnimationSkeletonMatcherController : ControllerBase
{
    static readonly Assembly Sdk = typeof(RenderMesh).Assembly;
    static readonly object Gate = new();
    static List<uint>? _generalIds;
    static List<uint>? _animIds;
    static List<AnimatorSummary>? _animatorInventory;
    static readonly Dictionary<string, FamilyMatch> FamilyCache = new(StringComparer.Ordinal);


    /// <summary>
    /// 1.4.1.13: returns Animator records whose SDK BoneCount exactly matches the selected Setup's
    /// parsed Havok skeleton joint count. The 58-joint humanoid family is playback-validated;
    /// other exact-count families are surfaced as experimental until independently validated.
    /// </summary>
    [HttpGet("compatible/{setupId}")]
    public IActionResult Compatible(string setupId, int limit = 5000, bool refresh = false, bool familyFilter = true)
    {
        if (!TryId(setupId, out var setup)) return BadRequest("Invalid Setup id.");
        limit = Math.Clamp(limit, 1, 10000);

        var setupBytes = SafeGetGeneral(setup);
        if (setupBytes == null) return NotFound($"Setup 0x{setup:X8} was not found in General DAT.");
        var skeleton = TryReadSkeletonSummary(setupBytes);
        if (skeleton == null || skeleton.JointCount <= 0)
            return UnprocessableEntity(new { setup = $"0x{setup:X8}", error = "No Havok skeleton was parsed from this Setup." });

        List<AnimatorSummary> inventory;
        lock (Gate)
        {
            if (_animIds == null || refresh)
                _animIds = DiscoverIds(DatSource.AnimDat, id => ((id >> 24) == 0x05) && SafeGetAnim(id) != null);
            if (_animatorInventory == null || refresh)
            {
                var rows = new List<AnimatorSummary>(_animIds.Count);
                foreach (var id in _animIds.OrderBy(x => x))
                {
                    var a = LoadAnimator(id);
                    if (a == null) continue;
                    var bytes = SafeGetAnim(id);
                    rows.Add(new AnimatorSummary(id, ReadInt(a, "BoneCount"), ReadFloat(a, "Duration"), bytes?.Length ?? 0));
                }
                _animatorInventory = rows;
            }
            if (refresh) FamilyCache.Clear();
            inventory = _animatorInventory.ToList();
        }

        var allCountMatches = inventory.Where(x => x.BoneCount == skeleton.JointCount).OrderBy(x => x.Id).ToArray();
        var countMatches = allCountMatches.Take(limit).ToArray();
        var familyRows = new List<(AnimatorSummary Anim, FamilyMatch Match)>();
        int decoded = 0, decodeFailed = 0, hiddenWrongFamily = 0;

        foreach (var anim in countMatches)
        {
            FamilyMatch match;
            var cacheKey = $"{setup:X8}:{anim.Id:X8}";
            lock (Gate)
            {
                if (FamilyCache.TryGetValue(cacheKey, out var cached)) { match = cached; familyRows.Add((anim, match)); continue; }
            }

            var bytes = SafeGetAnim(anim.Id);
            if (bytes == null) { match = new FamilyMatch("decode-failed", 0, 0, 0, 0); decodeFailed++; }
            else
            {
                var dec = DdoHavokAnimationDecoder.Decode(bytes, true);
                if (!dec.Success || dec.TransformTrackCount != skeleton.JointCount || dec.Frames.Count == 0)
                {
                    match = new FamilyMatch("decode-failed", 0, 0, 0, 0);
                    decodeFailed++;
                }
                else
                {
                    decoded++;
                    match = EvaluateFamily(dec, skeleton.Translations);
                    if (match.Tier == "wrong-family") hiddenWrongFamily++;
                }
            }
            lock (Gate) FamilyCache[cacheKey] = match;
            familyRows.Add((anim, match));
        }

        var visible = familyFilter
            ? familyRows.Where(x => x.Match.Tier != "wrong-family" && x.Match.Tier != "decode-failed").ToArray()
            : familyRows.ToArray();

        return Ok(new
        {
            version = "1.4.1.32",
            setup = $"0x{setup:X8}",
            jointCount = skeleton.JointCount,
            skeletonNames = skeleton.Names.Take(128).ToArray(),
            playbackBinding = skeleton.JointCount == 58 ? "validated-direct-index" : "family-filtered-direct-index",
            totalBoneCountMatches = allCountMatches.Length,
            totalCompatible = visible.Length,
            familyFilter,
            decodedForFamilyCheck = decoded,
            decodeFailed,
            hiddenWrongFamily,
            returned = Math.Min(limit, visible.Length),
            truncated = allCountMatches.Length > countMatches.Length,
            animations = visible.Take(limit).Select(x => new
            {
                id = $"0x{x.Anim.Id:X8}",
                boneCount = x.Anim.BoneCount,
                duration = x.Anim.Duration,
                byteLength = x.Anim.ByteLength,
                label = (string?)null,
                linked = true,
                compatible = x.Match.Tier != "wrong-family" && x.Match.Tier != "decode-failed",
                validatedFamily = x.Match.Tier is "exact-likely" or "likely",
                familyTier = x.Match.Tier,
                familyScore = x.Match.Score,
                familyMatched = x.Match.Matched,
                familyEligible = x.Match.Eligible
            }).ToArray(),
            note = $"Pre-decoded {countMatches.Length:N0} exact BoneCount candidates (of {allCountMatches.Length:N0}) and hid {hiddenWrongFamily:N0} wrong-family animations. Decode failures are also hidden from the playable list. Results are cached for this Studio session."
        });
    }

    [HttpGet("match/{animationId}")]
    public IActionResult Match(string animationId = "0x05000051", int radius = 64, bool scanAllAnimators = true, bool refresh = false)
    {
        if (!TryId(animationId, out var targetId)) return BadRequest("Invalid animation id.");
        radius = Math.Clamp(radius, 1, 2048);

        var target = LoadAnimator(targetId);
        if (target == null) return NotFound($"Animator 0x{targetId:X8} could not be loaded from client_anim.dat.");
        int targetBoneCount = ReadInt(target, "BoneCount");
        float targetDuration = ReadFloat(target, "Duration");

        List<uint> generalIds, animIds;
        lock (Gate)
        {
            if (_generalIds == null || refresh)
                _generalIds = DiscoverIds(DatSource.GeneralDat, id => ((id >> 24) == 0x04) && SafeGetGeneral(id) != null);
            if (_animIds == null || refresh)
                _animIds = DiscoverIds(DatSource.AnimDat, id => ((id >> 24) == 0x05) && SafeGetAnim(id) != null);
            generalIds = _generalIds;
            animIds = _animIds;
        }

        var setupMatches = new List<object>();
        var setupHistogram = new Dictionary<int, int>();
        int setupParsed = 0;
        foreach (var id in generalIds.Where(x => (x >> 24) == 0x04))
        {
            var bytes = SafeGetGeneral(id);
            if (bytes == null) continue;
            var skel = TryReadSkeletonSummary(bytes);
            if (skel == null) continue;
            setupParsed++;
            setupHistogram[skel.JointCount] = setupHistogram.GetValueOrDefault(skel.JointCount) + 1;
            if (skel.JointCount == targetBoneCount)
            {
                setupMatches.Add(new
                {
                    setup = $"0x{id:X8}",
                    jointCount = skel.JointCount,
                    boneNames = skel.Names.Take(96).ToArray(),
                    parents = skel.Parents.Take(96).ToArray(),
                    byteLength = bytes.Length
                });
            }
        }

        var neighborhood = new List<object>();
        uint lo = targetId > (uint)radius ? targetId - (uint)radius : 0;
        uint hi = targetId + (uint)radius;
        foreach (var id in animIds.Where(x => x >= lo && x <= hi).OrderBy(x => x))
        {
            var a = LoadAnimator(id);
            if (a == null) continue;
            neighborhood.Add(AnimatorRow(id, a));
        }

        var matchingAnimators = new List<object>();
        var animatorHistogram = new Dictionary<int, int>();
        int animatorParsed = 0;
        if (scanAllAnimators)
        {
            foreach (var id in animIds.OrderBy(x => x))
            {
                var a = LoadAnimator(id);
                if (a == null) continue;
                int bones = ReadInt(a, "BoneCount");
                float dur = ReadFloat(a, "Duration");
                animatorParsed++;
                animatorHistogram[bones] = animatorHistogram.GetValueOrDefault(bones) + 1;
                if (bones == targetBoneCount)
                    matchingAnimators.Add(new { id = $"0x{id:X8}", boneCount = bones, duration = dur });
            }
        }

        return Ok(new
        {
            version = "1.4.1.32",
            targetAnimator = new
            {
                id = $"0x{targetId:X8}",
                boneCount = targetBoneCount,
                duration = targetDuration
            },
            inventory = new
            {
                discoveredGeneralIds = generalIds.Count,
                parsedSetups = setupParsed,
                discoveredAnimIds = animIds.Count,
                parsedAnimators = animatorParsed
            },
            exactSkeletonCandidates = setupMatches,
            nearbyAnimators = neighborhood,
            exactBoneCountAnimators = matchingAnimators.Take(5000).ToArray(),
            setupJointCountHistogram = setupHistogram.OrderBy(x => x.Key).Select(x => new { jointCount = x.Key, count = x.Value }).ToArray(),
            animatorBoneCountHistogram = animatorHistogram.OrderBy(x => x.Key).Select(x => new { boneCount = x.Key, count = x.Value }).ToArray(),
            interpretation = new
            {
                rule = "A matching count is evidence of compatibility, not proof of ownership. Ownership still requires a Scriptlet/ScriptTable/WState/DbProperties relationship or successful playback.",
                target = $"Animator 0x{targetId:X8} reports {targetBoneCount} bones. Every Setup listed in exactSkeletonCandidates has exactly {targetBoneCount} parsed joints.",
                next = "Prioritize exact-count Setups that are referenced by the same state/script graph as the target Animator. If one candidate validates in playback, reuse its track order as a hypothesis across Animators with the same BoneCount and skeleton family."
            }
        });
    }

    static object AnimatorRow(uint id, object a) => new
    {
        id = $"0x{id:X8}",
        boneCount = ReadInt(a, "BoneCount"),
        duration = ReadFloat(a, "Duration")
    };

    static object? LoadAnimator(uint id)
    {
        try
        {
            if (DatSource.AnimDat == null) return null;
            var t = Sdk.GetType("VoK.Sdk.Common.Animator");
            if (t == null) return null;
            var m = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(x => x.Name == "Load" && x.GetParameters().Length == 2 && x.GetParameters()[1].ParameterType == typeof(uint));
            if (m == null) return null;
            return m.Invoke(null, new object?[] { DatSource.AnimDat, id });
        }
        catch { return null; }
    }

    static SkeletonSummary? TryReadSkeletonSummary(byte[] setupBytes)
    {
        try
        {
            var t = Sdk.GetType("VoK.Sdk.Common.Setup");
            if (t == null) return null;
            object setup;
            using var ms = new MemoryStream(setupBytes, false);
            using var br = new BinaryReader(ms);
            try { setup = Construct(t, br, null); }
            catch { ms.Position = 0; setup = Construct(t, br); }
            var havok = Get(setup, "HavokSetup");
            if (havok == null) return null;
            var names = Items(Get(havok, "BoneNames")).Select(x => x.ToString() ?? "").ToList();
            var rawParents = Items(Get(havok, "ParentIndices")).Select(IntValue).ToList();
            var bones = Items(Get(havok, "Bones")).ToList();
            int count = Math.Min(names.Count, Math.Min(rawParents.Count, bones.Count / 2));
            if (count <= 0) return null;
            var parents = new int[count];
            var translations = new Vector3[count];
            for (int i = 0; i < count; i++)
            {
                int raw = rawParents[i];
                int parent = (raw == 3 || (raw & 1) != 0) ? -1 : raw / 2;
                if (parent < -1 || parent >= count || parent == i) parent = -1;
                parents[i] = parent;
                var a = bones[i * 2];
                translations[i] = Vec3(Get(a, "V1"));
            }
            return new SkeletonSummary(count, names.Take(count).ToArray(), parents, translations);
        }
        catch { return null; }
    }

    static FamilyMatch EvaluateFamily(DdoHavokAnimationDecoder.DecodeResult dec, Vector3[] bind)
    {
        int count = dec.TransformTrackCount;
        if (dec.Frames.Count == 0 || bind.Length != count) return new FamilyMatch("unknown", 0, 0, 0, 0);
        int sampleCount = Math.Min(dec.Frames.Count, 64);
        var samples = new List<DdoHavokAnimationDecoder.FrameSample>(sampleCount);
        for (int s = 0; s < sampleCount; s++)
        {
            int ix = (int)MathF.Round(s * (dec.Frames.Count - 1f) / Math.Max(1, sampleCount - 1));
            samples.Add(dec.Frames[Math.Clamp(ix, 0, dec.Frames.Count - 1)]);
        }
        int eligible = 0, matched = 0, strongMismatch = 0;
        for (int i = 1; i < count; i++)
        {
            var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
            foreach (var f in samples)
            {
                var t = f.Transforms.FirstOrDefault(x => x.Track == i);
                if (t?.Translation == null || t.Translation.Length < 3) continue;
                xs.Add(t.Translation[0]); ys.Add(t.Translation[1]); zs.Add(t.Translation[2]);
            }
            if (xs.Count < 2) continue;
            float mx = Median(xs), my = Median(ys), mz = Median(zs);
            double varsum = 0;
            for (int k = 0; k < xs.Count; k++)
                varsum += Math.Pow(xs[k] - mx, 2) + Math.Pow(ys[k] - my, 2) + Math.Pow(zs[k] - mz, 2);
            float std = (float)Math.Sqrt(varsum / xs.Count);
            float bn = bind[i].Length();
            float scale = Math.Max(bn, .05f);
            float stableLimit = Math.Max(.003f, .06f * scale);
            if (std > stableLimit) continue;
            eligible++;
            float dist = Vector3.Distance(new Vector3(mx, my, mz), bind[i]);
            float tol = Math.Max(.02f, .18f * scale);
            if (dist <= tol) matched++;
            if (dist > Math.Max(.06f, .5f * scale)) strongMismatch++;
        }
        float score = eligible > 0 ? (float)matched / eligible : 0f;
        string tier = "count-only";
        if (eligible >= Math.Max(6, (int)MathF.Floor(count * .3f)))
        {
            if (score >= .82f && strongMismatch <= 1) tier = "exact-likely";
            else if (score >= .58f) tier = "likely";
            else if (score < .38f || strongMismatch >= Math.Max(4, (int)MathF.Floor(eligible * .35f))) tier = "wrong-family";
        }
        return new FamilyMatch(tier, score, eligible, matched, strongMismatch);
    }

    static float Median(List<float> values)
    {
        values.Sort();
        int m = values.Count / 2;
        return (values.Count & 1) == 1 ? values[m] : (values[m - 1] + values[m]) * .5f;
    }

    static Vector3 Vec3(object? o)
    {
        if (o == null) return Vector3.Zero;
        try
        {
            float x = Convert.ToSingle(Get(o, "X") ?? 0, CultureInfo.InvariantCulture);
            float y = Convert.ToSingle(Get(o, "Y") ?? 0, CultureInfo.InvariantCulture);
            float z = Convert.ToSingle(Get(o, "Z") ?? 0, CultureInfo.InvariantCulture);
            return new Vector3(x, y, z);
        }
        catch { return Vector3.Zero; }
    }

    static List<uint> DiscoverIds(object? root, Func<uint, bool> validator)
    {
        var found = new HashSet<uint>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(root, found, visited, 0, validator);
        return found.OrderBy(x => x).ToList();
    }

    static void Walk(object? value, HashSet<uint> dst, HashSet<object> visited, int depth, Func<uint, bool> validator)
    {
        if (value == null || depth > 6) return;
        var t = value.GetType();
        if (t.IsPrimitive || value is string || value is byte[] || value is Type) return;
        if (!t.IsValueType && !visited.Add(value)) return;
        if (TryNumeric(value, out var direct)) AddIfValid(direct, dst, validator);
        if (value is IDictionary dict)
        {
            int n = 0;
            foreach (DictionaryEntry e in dict)
            {
                if (++n > 3_000_000) break;
                if (TryNumeric(e.Key, out var k)) AddIfValid(k, dst, validator);
                Walk(e.Value, dst, visited, depth + 1, validator);
            }
            return;
        }
        if (value is IEnumerable seq)
        {
            int n = 0;
            foreach (var item in seq)
            {
                if (++n > 3_000_000) break;
                if (TryNumeric(item, out var k)) AddIfValid(k, dst, validator);
                else if (item != null)
                {
                    foreach (var name in new[] { "Id", "ID", "Did", "DID", "FileId", "FileID", "Key" })
                        if (TryNumeric(Get(item, name), out var mid)) AddIfValid(mid, dst, validator);
                    if (depth < 4) Walk(item, dst, visited, depth + 1, validator);
                }
            }
            return;
        }
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            try { Walk(f.GetValue(value), dst, visited, depth + 1, validator); } catch { }
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
            try { Walk(p.GetValue(value), dst, visited, depth + 1, validator); } catch { }
        }
    }

    static void AddIfValid(uint id, HashSet<uint> dst, Func<uint, bool> validator)
    {
        if (id == 0 || dst.Contains(id)) return;
        try { if (validator(id)) dst.Add(id); } catch { }
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

    static int ReadInt(object o, string name) { try { return Convert.ToInt32(Get(o, name), CultureInfo.InvariantCulture); } catch { return 0; } }
    static float ReadFloat(object o, string name) { try { return Convert.ToSingle(Get(o, name), CultureInfo.InvariantCulture); } catch { return 0f; } }
    static int IntValue(object? o) { try { return Convert.ToInt32(o, CultureInfo.InvariantCulture); } catch { return 0; } }

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

    static byte[]? SafeGetGeneral(uint id) { try { return DatSource.GeneralDat?.GetFileContents(id); } catch { return null; } }
    static byte[]? SafeGetAnim(uint id) { try { return DatSource.AnimDat?.GetFileContents(id); } catch { return null; } }
    static bool TryId(string s, out uint id) { s = s.Trim(); if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..]; return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id); }

    sealed record SkeletonSummary(int JointCount, string[] Names, int[] Parents, Vector3[] Translations);
    sealed record FamilyMatch(string Tier, float Score, int Eligible, int Matched, int StrongMismatch);
    sealed record AnimatorSummary(uint Id, int BoneCount, float Duration, int ByteLength);
}
