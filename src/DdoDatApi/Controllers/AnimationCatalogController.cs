using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace DdoDatApi.Controllers;

/// <summary>
/// Enumerates client_anim.dat and exposes raw Havok records plus conservative model-link diagnostics.
/// Unknown records remain available as raw bytes so later Havok decoders can consume them losslessly.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class AnimationCatalogController : ControllerBase
{
    static readonly object Gate = new();
    static List<uint>? _ids;

    [HttpGet("ids")]
    public IActionResult Ids(bool refresh = false)
    {
        lock (Gate)
        {
            if (_ids == null || refresh) _ids = DiscoverIds();
            return Ok(new { dat = "Anim", count = _ids.Count, ids = _ids.Select(x => $"0x{x:X8}").ToArray() });
        }
    }

    [HttpGet("havok")]
    public IActionResult HavokCandidates(bool refresh = false, int limit = 50000)
    {
        lock (Gate)
        {
            if (_ids == null || refresh) _ids = DiscoverIds();
            limit = Math.Clamp(limit, 1, 50000);
            var hits = new List<object>();
            foreach (var did in _ids.Take(limit))
            {
                byte[]? bytes;
                try { bytes = DatSource.AnimDat?.GetFileContents(did); } catch { bytes = null; }
                if (bytes == null) continue;
                var ascii = ScanAscii(bytes).ToArray();
                var classes = ascii.Where(x => x.StartsWith("hk", StringComparison.Ordinal)).Distinct().Take(32).ToArray();
                var chunks = ScanChunkTags(bytes).Where(x => x is "TAG0" or "SDKV" or "DATA" or "TYPE" or "INDX" or "ITEM").Distinct().ToArray();
                if (classes.Length == 0 && chunks.Length == 0) continue;
                hits.Add(new { id = $"0x{did:X8}", byteLength = bytes.Length, sha1 = Convert.ToHexString(SHA1.HashData(bytes)), havokClasses = classes, chunkTags = chunks });
            }
            return Ok(new { dat = "Anim", scanned = Math.Min(limit, _ids.Count), totalRecords = _ids.Count, havokCandidates = hits.Count, entries = hits });
        }
    }

    /// <summary>
    /// Conservative first-pass resolver for a renderable Setup. It never claims a clip is compatible
    /// unless the record has a direct typed-looking relationship (Setup references Anim, or Anim references Setup).
    /// This gives the UI real candidate records while Havok Tagfile track decoding is being integrated.
    /// </summary>
    [HttpGet("model/{setupId}")]
    public IActionResult ModelCandidates(string setupId, int limit = 50000)
    {
        if (!TryId(setupId, out var setup)) return BadRequest("Invalid Setup id.");
        byte[]? setupBytes;
        try { setupBytes = DatSource.GeneralDat?.GetFileContents(setup); } catch { setupBytes = null; }
        if (setupBytes == null) return NotFound($"Setup 0x{setup:X8} was not found in General DAT.");

        lock (Gate)
        {
            if (_ids == null) _ids = DiscoverIds();
            limit = Math.Clamp(limit, 1, Math.Min(50000, _ids.Count));

            var directFromSetup = new HashSet<uint>();
            foreach (var (_, id) in ScanIds(setupBytes))
            {
                if (_ids.BinarySearch(id) >= 0) directFromSetup.Add(id);
            }

            var reverse = new List<object>();
            int scanned = 0;
            foreach (var animId in _ids.Take(limit))
            {
                scanned++;
                byte[]? bytes;
                try { bytes = DatSource.AnimDat?.GetFileContents(animId); } catch { bytes = null; }
                if (bytes == null) continue;

                int setupRefs = CountAlignedId(bytes, setup);
                bool direct = directFromSetup.Contains(animId);
                if (!direct && setupRefs == 0) continue;

                var classes = ScanAscii(bytes)
                    .Where(x => x.StartsWith("hk", StringComparison.Ordinal))
                    .Distinct().Take(12).ToArray();
                int score = (direct ? 1000 : 0) + setupRefs * 100;
                reverse.Add(new
                {
                    id = $"0x{animId:X8}",
                    score,
                    directFromSetup = direct,
                    reverseSetupReferences = setupRefs,
                    byteLength = bytes.Length,
                    havokClasses = classes
                });
            }

            var ordered = reverse
                .OrderByDescending(x => (int)x.GetType().GetProperty("score")!.GetValue(x)!)
                .Take(512)
                .ToArray();

            return Ok(new
            {
                setup = $"0x{setup:X8}",
                scanned,
                totalAnimationRecords = _ids.Count,
                directAnimationIdsInSetup = directFromSetup.Select(x => $"0x{x:X8}").ToArray(),
                candidates = ordered,
                note = "Candidates are relationship evidence, not yet decoded playable clips. Havok Tagfile tracks must still be converted to glTF animation channels."
            });
        }
    }

    [HttpGet("{id}/inspect")]
    public IActionResult Inspect(string id)
    {
        if (!TryId(id, out var did)) return BadRequest("Invalid DAT id.");
        byte[]? bytes;
        try { bytes = DatSource.AnimDat?.GetFileContents(did); } catch { bytes = null; }
        if (bytes == null) return NotFound();

        var ascii = ScanAscii(bytes).Take(256).ToArray();
        var classes = ascii.Where(s => s.StartsWith("hk", StringComparison.Ordinal) || s.StartsWith("hka", StringComparison.Ordinal))
            .Distinct().Take(128).ToArray();
        var chunks = ScanChunkTags(bytes).Distinct().Take(128).ToArray();
        var refs = ScanIds(bytes).Take(256).Select(x => new { offset = x.Offset, id = $"0x{x.Id:X8}", type = TypeName(x.Id) }).ToArray();

        return Ok(new
        {
            id = $"0x{did:X8}",
            dat = "Anim",
            byteLength = bytes.Length,
            sha1 = Convert.ToHexString(SHA1.HashData(bytes)),
            looksLikeHavok = classes.Length > 0 || chunks.Any(x => x is "TAG0" or "SDKV" or "DATA" or "TYPE"),
            havokClasses = classes,
            chunkTags = chunks,
            asciiStrings = ascii,
            references = refs,
            firstBytes = Convert.ToHexString(bytes.AsSpan(0, Math.Min(512, bytes.Length)))
        });
    }

    [HttpGet("{id}/decode")]
    public IActionResult Decode(string id, bool frames = true)
    {
        if (!TryId(id, out var did)) return BadRequest("Invalid DAT id.");
        byte[]? bytes;
        try { bytes = DatSource.AnimDat?.GetFileContents(did); } catch { bytes = null; }
        if (bytes == null) return NotFound();

        var decoded = DdoHavokAnimationDecoder.Decode(bytes, frames);
        if (!decoded.Success)
            return UnprocessableEntity(new { id = $"0x{did:X8}", decoded.Success, decoded.Error, decoded.Note });

        return Ok(new
        {
            id = $"0x{did:X8}",
            dat = "Anim",
            sha1 = Convert.ToHexString(SHA1.HashData(bytes)),
            guess = (string?)null,
            decoded
        });
    }

    [HttpGet("{id}/decoded-file")]
    public IActionResult DecodedFile(string id)
    {
        if (!TryId(id, out var did)) return BadRequest("Invalid DAT id.");
        byte[]? bytes;
        try { bytes = DatSource.AnimDat?.GetFileContents(did); } catch { bytes = null; }
        if (bytes == null) return NotFound();
        var decoded = DdoHavokAnimationDecoder.Decode(bytes, true);
        if (!decoded.Success) return UnprocessableEntity(decoded);
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = $"0x{did:X8}",
            dat = "Anim",
            sha1 = Convert.ToHexString(SHA1.HashData(bytes)),
            guess = (string?)null,
            decoded
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"anim_{did:X8}.decoded.json");
    }

    [HttpGet("{id}/raw")]
    public IActionResult Raw(string id)
    {
        if (!TryId(id, out var did)) return BadRequest("Invalid DAT id.");
        byte[]? bytes;
        try { bytes = DatSource.AnimDat?.GetFileContents(did); } catch { bytes = null; }
        if (bytes == null) return NotFound();
        return File(bytes, "application/octet-stream", $"anim_{did:X8}.hkx");
    }

    static int CountAlignedId(byte[] bytes, uint wanted)
    {
        int n = 0;
        for (int i = 0; i + 4 <= bytes.Length; i += 4)
            if (BitConverter.ToUInt32(bytes, i) == wanted) n++;
        return n;
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
        if (TryNumeric(value, out var direct)) AddIfAnim(direct, dst);

        if (value is IDictionary dict)
        {
            int n = 0;
            foreach (DictionaryEntry e in dict)
            {
                if (++n > 3_000_000) break;
                if (TryNumeric(e.Key, out var k)) AddIfAnim(k, dst);
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
                if (TryNumeric(item, out var k)) AddIfAnim(k, dst);
                else if (item != null)
                {
                    foreach (var name in new[] { "Id", "ID", "Did", "DID", "FileId", "FileID", "Key" })
                        if (TryNumeric(GetMember(item, name), out var mid)) AddIfAnim(mid, dst);
                    if (depth < 4) Walk(item, dst, visited, depth + 1);
                }
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

    static void AddIfAnim(uint id, HashSet<uint> dst)
    {
        if (id == 0) return;
        try { if (DatSource.AnimDat?.GetFileContents(id) != null) dst.Add(id); } catch { }
    }

    static object? GetMember(object o, string name)
    {
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
        } catch { }
        return false;
    }

    static IEnumerable<string> ScanAscii(byte[] b)
    {
        var sb = new StringBuilder();
        foreach (var x in b)
        {
            if (x >= 32 && x <= 126) sb.Append((char)x);
            else { if (sb.Length >= 4) yield return sb.ToString(); sb.Clear(); }
        }
        if (sb.Length >= 4) yield return sb.ToString();
    }

    static IEnumerable<string> ScanChunkTags(byte[] b)
    {
        for (int i = 0; i + 4 <= b.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < 4; j++) if (b[i + j] < 'A' || b[i + j] > 'Z') { ok = false; break; }
            if (ok) yield return Encoding.ASCII.GetString(b, i, 4);
        }
    }

    static IEnumerable<(int Offset, uint Id)> ScanIds(byte[] b)
    {
        for (int i = 0; i + 4 <= b.Length; i += 4)
        {
            uint id = BitConverter.ToUInt32(b, i);
            if (TypeName(id) != null) yield return (i, id);
        }
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
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out id);
    }
}
