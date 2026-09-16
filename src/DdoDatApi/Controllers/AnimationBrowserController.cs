using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using VoK.Sdk.Common;

namespace DdoDatApi.Controllers;

/// <summary>
/// Global browser/index for every DbAnimator in client_anim.dat.
/// The index is intentionally lightweight; deep decoder validation is performed on demand
/// by /AnimationBrowser/{id}/preflight so browsing all ~19k records stays responsive.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class AnimationBrowserController : ControllerBase
{
    static readonly Assembly Sdk = typeof(RenderMesh).Assembly;
    static readonly object Gate = new();
    static List<Row>? _rows;

    sealed record Row(uint Id, int BoneCount, float Duration, int ByteLength, string HavokType, string DecodeHint, string? Label);

    [HttpGet("search")]
    public IActionResult Search(
        string? q = null,
        int? boneCount = null,
        float? minDuration = null,
        float? maxDuration = null,
        string? status = null,
        string? havokType = null,
        int page = 1,
        int pageSize = 250,
        bool refresh = false)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 25, 1000);
        List<Row> rows;
        lock (Gate)
        {
            if (_rows == null || refresh) _rows = BuildIndex();
            rows = _rows;
        }

        IEnumerable<Row> query = rows;
        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            query = query.Where(x =>
                $"0x{x.Id:X8}".Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                (x.Label?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                x.HavokType.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                x.DecodeHint.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        if (boneCount.HasValue) query = query.Where(x => x.BoneCount == boneCount.Value);
        if (minDuration.HasValue) query = query.Where(x => x.Duration >= minDuration.Value);
        if (maxDuration.HasValue) query = query.Where(x => x.Duration <= maxDuration.Value);
        if (!string.IsNullOrWhiteSpace(havokType) && !havokType.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.HavokType.Equals(havokType, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("all", StringComparison.OrdinalIgnoreCase))
            query = query.Where(x => x.DecodeHint.Equals(status, StringComparison.OrdinalIgnoreCase));

        var filtered = query.OrderBy(x => x.Id).ToArray();
        var slice = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToArray();

        return Ok(new
        {
            version = "1.7.2",
            totalGameAnimations = rows.Count,
            filtered = filtered.Length,
            page,
            pageSize,
            pages = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)pageSize)),
            rows = slice.Select(x => new
            {
                id = $"0x{x.Id:X8}",
                boneCount = x.BoneCount,
                duration = x.Duration,
                byteLength = x.ByteLength,
                havokType = x.HavokType,
                decodeStatus = x.DecodeHint,
                label = x.Label
            }).ToArray(),
            statusLegend = new
            {
                candidate = "Supported Havok animation class detected; select the row to run decoder preflight.",
                unsupported = "Havok animation data exists but this animation class is not decoded yet.",
                raw = "No supported skeletal animation class was identified by the lightweight index."
            }
        });
    }

    [HttpGet("{id}/preflight")]
    public IActionResult Preflight(string id)
    {
        if (!TryId(id, out var did)) return BadRequest("Invalid animation id.");
        var animator = LoadAnimator(did);
        if (animator == null) return NotFound($"Animator 0x{did:X8} could not be loaded.");
        byte[]? bytes;
        try { bytes = DatSource.AnimDat?.GetFileContents(did); } catch { bytes = null; }
        if (bytes == null) return NotFound($"Animation record 0x{did:X8} could not be read.");

        int bones = ReadInt(animator, "BoneCount");
        float sdkDuration = ReadFloat(animator, "Duration");
        string type = DetectHavokType(bytes);
        var decoded = DdoHavokAnimationDecoder.Decode(bytes, true);

        string state;
        string detail;
        bool finite = true;
        if (!decoded.Success)
        {
            state = (type == "SplineCompressed" || type == "InterleavedUncompressed") ? "decoder-error" : "unsupported-format";
            detail = decoded.Error;
        }
        else if (decoded.NumFrames <= 0)
        {
            state = "no-source-frames";
            detail = $"The source spline header reports NumFrames={decoded.NumFrames}. Duration is still {decoded.Duration:0.###}s.";
        }
        else if (decoded.Frames.Count == 0)
        {
            state = "decode-empty";
            detail = $"The source reports {decoded.NumFrames} frames, but frame expansion returned 0.";
        }
        else if (decoded.TransformTrackCount != bones)
        {
            state = "track-mismatch";
            detail = $"SDK Animator BoneCount={bones}, decoded transform tracks={decoded.TransformTrackCount}.";
        }
        else
        {
            finite = FramesFinite(decoded);
            if (!finite)
            {
                state = "invalid-transforms";
                detail = "At least one decoded translation/rotation/scale component is NaN or infinite.";
            }
            else
            {
                state = "playable";
                detail = $"Decoded {decoded.Frames.Count} frames across {decoded.TransformTrackCount} tracks.";
            }
        }

        return Ok(new
        {
            version = "1.7.2",
            id = $"0x{did:X8}",
            label = (string?)null,
            guess = (string?)null,
            sdk = new { boneCount = bones, duration = sdkDuration },
            havokType = type,
            status = state,
            playable = state == "playable",
            detail,
            decoder = new
            {
                success = decoded.Success,
                error = decoded.Error,
                animationType = decoded.AnimationType,
                duration = decoded.Duration,
                transformTrackCount = decoded.TransformTrackCount,
                floatTrackCount = decoded.FloatTrackCount,
                numFrames = decoded.NumFrames,
                outputFrames = decoded.Frames.Count,
                numBlocks = decoded.NumBlocks,
                maxFramesPerBlock = decoded.MaxFramesPerBlock,
                maskAndQuantizationSize = decoded.MaskAndQuantizationSize,
                frameDuration = decoded.FrameDuration,
                dataLength = decoded.DataLength,
                finiteTransforms = finite
            }
        });
    }

    static List<Row> BuildIndex()
    {
        var ids = DiscoverIds();
        var list = new List<Row>(ids.Count);
        foreach (var id in ids.OrderBy(x => x))
        {
            var a = LoadAnimator(id);
            if (a == null) continue;
            byte[]? bytes;
            try { bytes = DatSource.AnimDat?.GetFileContents(id); } catch { bytes = null; }
            var type = bytes == null ? "Unknown" : DetectHavokType(bytes);
            var hint = (type == "SplineCompressed" || type == "InterleavedUncompressed") ? "candidate" : (type == "Unknown" || type == "HavokAnimation" ? "raw" : "unsupported");
            list.Add(new Row(id, ReadInt(a, "BoneCount"), ReadFloat(a, "Duration"), bytes?.Length ?? 0, type, hint, null));
        }
        return list;
    }

    static string DetectHavokType(byte[] bytes)
    {
        if (ContainsAscii(bytes, "hkaSplineCompressedAnimation")) return "SplineCompressed";
        if (ContainsAscii(bytes, "hkaInterleavedUncompressedAnimation")) return "InterleavedUncompressed";
        if (ContainsAscii(bytes, "hkaDeltaCompressedAnimation")) return "DeltaCompressed";
        if (ContainsAscii(bytes, "hkaWaveletCompressedAnimation")) return "WaveletCompressed";
        if (ContainsAscii(bytes, "hkaQuantizedAnimation")) return "Quantized";
        if (ContainsAscii(bytes, "hkaAnimation")) return "HavokAnimation";
        return "Unknown";
    }

    static bool ContainsAscii(byte[] data, string text)
    {
        var p = Encoding.ASCII.GetBytes(text);
        if (p.Length == 0 || data.Length < p.Length) return false;
        for (int i = 0; i <= data.Length - p.Length; i++)
        {
            int j = 0;
            for (; j < p.Length && data[i + j] == p[j]; j++) { }
            if (j == p.Length) return true;
        }
        return false;
    }

    static bool FramesFinite(DdoHavokAnimationDecoder.DecodeResult d)
    {
        static bool F(float x) => !float.IsNaN(x) && !float.IsInfinity(x);
        foreach (var frame in d.Frames)
        foreach (var t in frame.Transforms)
        {
            if (!t.Translation.All(F) || !t.Rotation.All(F) || !t.Scale.All(F)) return false;
        }
        return true;
    }

    static object? LoadAnimator(uint id)
    {
        try
        {
            if (DatSource.AnimDat == null) return null;
            var t = Sdk.GetType("VoK.Sdk.Common.Animator");
            var m = t?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(x => x.Name == "Load" && x.GetParameters().Length == 2 && x.GetParameters()[1].ParameterType == typeof(uint));
            return m?.Invoke(null, new object?[] { DatSource.AnimDat, id });
        }
        catch { return null; }
    }

    static int ReadInt(object o, string name)
    {
        try { return Convert.ToInt32(o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o) ?? 0); }
        catch { return 0; }
    }

    static float ReadFloat(object o, string name)
    {
        try { return Convert.ToSingle(o.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o) ?? 0f); }
        catch { return 0f; }
    }

    static List<uint> DiscoverIds()
    {
        var found = new HashSet<uint>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Walk(DatSource.AnimDat, found, visited, 0);
        return found.OrderBy(x => x).ToList();
    }

    static void Walk(object? value, HashSet<uint> dst, HashSet<object> visited, int depth)
    {
        if (value == null || depth > 6) return;
        var t = value.GetType();
        if (t.IsPrimitive || value is string || value is byte[] || value is Type) return;
        if (!t.IsValueType && !visited.Add(value)) return;
        if (TryNumeric(value, out var direct)) Add(direct, dst);
        if (value is IDictionary dict)
        {
            int n = 0;
            foreach (DictionaryEntry e in dict)
            {
                if (++n > 3_000_000) break;
                if (TryNumeric(e.Key, out var k)) Add(k, dst);
                Walk(e.Value, dst, visited, depth + 1);
            }
            return;
        }
        if (value is IEnumerable seq)
        {
            int n = 0;
            foreach (var item in seq)
            {
                if (++n > 3_000_000) break;
                if (TryNumeric(item, out var k)) Add(k, dst);
                else if (item != null && depth < 4) Walk(item, dst, visited, depth + 1);
            }
            return;
        }
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            try { Walk(f.GetValue(value), dst, visited, depth + 1); } catch { }
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
            try { Walk(p.GetValue(value), dst, visited, depth + 1); } catch { }
        }
    }

    static void Add(uint id, HashSet<uint> dst)
    {
        if ((id >> 24) != 0x05 || dst.Contains(id)) return;
        try { if (DatSource.AnimDat?.GetFileContents(id) != null) dst.Add(id); } catch { }
    }

    static bool TryNumeric(object? value, out uint result)
    {
        result = 0;
        try
        {
            switch (value)
            {
                case uint u: result = u; return true;
                case int i when i >= 0: result = (uint)i; return true;
                case ushort s: result = s; return true;
                case long l when l >= 0 && l <= uint.MaxValue: result = (uint)l; return true;
                case ulong ul when ul <= uint.MaxValue: result = (uint)ul; return true;
            }
        }
        catch { }
        return false;
    }

    static bool TryId(string text, out uint id)
    {
        id = 0;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out id);
        return uint.TryParse(text, out id);
    }
}
