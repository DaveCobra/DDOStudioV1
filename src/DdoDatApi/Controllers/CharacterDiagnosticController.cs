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
/// Diagnostic endpoint for reverse engineering DDO character composition.
/// It deliberately does not assume that NPC customization is stored in a 0x20 Appearance object.
/// Instead it captures the complete VisualDescription record and every plausible asset reference.
/// </summary>
[ApiController]
[Route("[controller]")]
public class CharacterDiagnosticController : ControllerBase
{
    static readonly Assembly Sdk = typeof(RenderMesh).Assembly;

    [HttpGet("{visualDescriptionId}")]
    public IActionResult Get(string visualDescriptionId, string? dbId = null, string? physObj = null, string? setup = null)
    {
        if (!TryId(visualDescriptionId, out var visualId) || (visualId >> 24) != 0x1F)
            return BadRequest("Expected a VisualDescription ID in the 0x1Fxxxxxx range.");

        var data = DatSource.GeneralDat?.GetFileContents(visualId);
        if (data == null) return NotFound();

        TryId(dbId ?? "", out var parsedDbId);
        TryId(physObj ?? "", out var parsedPhysObj);
        TryId(setup ?? "", out var parsedSetup);

        var aligned = ScanReferences(data, alignedOnly: true);
        var unaligned = ScanReferences(data, alignedOnly: false)
            .Where(x => x.Offset % 4 != 0)
            .Take(256)
            .ToArray();

        var childIds = aligned.Select(x => x.Id)
            .Concat(unaligned.Select(x => x.Id))
            .Where(x => x != visualId)
            .Distinct()
            .Take(128)
            .ToArray();

        var children = childIds.Select(InspectChild).Where(x => x != null).ToArray();
        var words = Enumerable.Range(0, data.Length / 4)
            .Take(4096)
            .Select(i => new
            {
                offset = i * 4,
                hexOffset = $"0x{i * 4:X4}",
                value = BitConverter.ToUInt32(data, i * 4),
                hex = $"0x{BitConverter.ToUInt32(data, i * 4):X8}"
            }).ToArray();

        return Ok(new
        {
            format = "DDO Studio Character Composition Diagnostic",
            diagnosticVersion = 2,
            generatedUtc = DateTime.UtcNow,
            source = new
            {
                dbId = parsedDbId,
                dbHex = parsedDbId == 0 ? null : $"0x{parsedDbId:X8}",
                physObj = parsedPhysObj,
                physHex = parsedPhysObj == 0 ? null : $"0x{parsedPhysObj:X8}",
                visualDescriptionId = visualId,
                visualHex = $"0x{visualId:X8}",
                setup = parsedSetup,
                setupHex = parsedSetup == 0 ? null : $"0x{parsedSetup:X8}"
            },
            visualDescription = new
            {
                byteLength = data.Length,
                sha1 = Convert.ToHexString(SHA1.HashData(data)),
                rawHex = Convert.ToHexString(data),
                firstBytes = Convert.ToHexString(data.AsSpan(0, Math.Min(256, data.Length))),
                asciiStrings = ScanAscii(data),
                dwords = words,
                alignedReferences = aligned.Select(ToDto).ToArray(),
                unalignedReferences = unaligned.Select(ToDto).ToArray(),
                sdk = TrySdkParse(data)
            },
            referencedRecords = children
        });
    }

    sealed record RefHit(uint Id, int Offset, string Type);

    static IEnumerable<RefHit> ScanReferences(byte[] data, bool alignedOnly)
    {
        int step = alignedOnly ? 4 : 1;
        var seen = new HashSet<(uint, int)>();
        for (int o = 0; o + 4 <= data.Length; o += step)
        {
            uint id = BitConverter.ToUInt32(data, o);
            var type = TypeName(id);
            if (type == null) continue;
            if (seen.Add((id, o))) yield return new RefHit(id, o, type);
        }
    }

    static object ToDto(RefHit x) => new
    {
        id = x.Id,
        hex = $"0x{x.Id:X8}",
        offset = x.Offset,
        hexOffset = $"0x{x.Offset:X4}",
        type = x.Type
    };

    static string? TypeName(uint id)
    {
        foreach (var r in DatSource.IdRanges)
        {
            if (id >= r.Minimum && id <= r.Maximum)
                return string.IsNullOrWhiteSpace(r.Name) ? r.Description : r.Name;
        }
        return null;
    }

    static object? InspectChild(uint id)
    {
        if (!TryLoad(id, out var dat, out var bytes) || bytes == null) return null;
        var refs = ScanReferences(bytes, alignedOnly: true)
            .Where(x => x.Id != id)
            .Take(64)
            .Select(ToDto)
            .ToArray();
        return new
        {
            id,
            hex = $"0x{id:X8}",
            type = TypeName(id) ?? "Unknown",
            dat,
            byteLength = bytes.Length,
            sha1 = Convert.ToHexString(SHA1.HashData(bytes)),
            firstBytes = Convert.ToHexString(bytes.AsSpan(0, Math.Min(256, bytes.Length))),
            references = refs,
            asciiStrings = ScanAscii(bytes).Take(32).ToArray()
        };
    }

    static bool TryLoad(uint id, out string dat, out byte[]? bytes)
    {
        dat = "";
        bytes = null;
        try
        {
            byte prefix = (byte)(id >> 24);
            if (prefix == 0x06)
            {
                bytes = DatSource.Mesh?.GetFileContents(id);
                if (bytes != null) { dat = "Mesh"; return true; }
            }
            if (prefix == 0x47 || prefix >= 0x78)
            {
                bytes = DatSource.GameLogicDat?.GetFileContents(id);
                if (bytes != null) { dat = "GameLogic"; return true; }
            }
            if (prefix == 0x41)
            {
                bytes = DatSource.Highres?.GetFileContents(id);
                if (bytes != null) { dat = "Highres"; return true; }
                bytes = DatSource.SurfaceDat?.GetFileContents(id);
                if (bytes != null) { dat = "Surface"; return true; }
                bytes = DatSource.GeneralDat?.GetFileContents(id);
                if (bytes != null) { dat = "General"; return true; }
            }

            bytes = DatSource.GeneralDat?.GetFileContents(id);
            if (bytes != null) { dat = "General"; return true; }
            bytes = DatSource.GameLogicDat?.GetFileContents(id);
            if (bytes != null) { dat = "GameLogic"; return true; }
            bytes = DatSource.Mesh?.GetFileContents(id);
            if (bytes != null) { dat = "Mesh"; return true; }
        }
        catch { }
        return false;
    }

    static string[] ScanAscii(byte[] data)
    {
        var result = new List<string>();
        var chars = new List<char>();
        void Flush()
        {
            if (chars.Count >= 4 && result.Count < 128)
                result.Add(new string(chars.ToArray()));
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

    static object TrySdkParse(byte[] data)
    {
        Type[] candidates;
        try
        {
            candidates = Sdk.GetTypes()
                .Where(t => t.Namespace?.StartsWith("VoK.Sdk", StringComparison.Ordinal) == true)
                .Where(t =>
                    t.Name.Contains("Visual", StringComparison.OrdinalIgnoreCase) ||
                    t.Name.Contains("ObjectDescription", StringComparison.OrdinalIgnoreCase) ||
                    t.Name.Contains("Appearance", StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.FullName)
                .Take(64)
                .ToArray();
        }
        catch (ReflectionTypeLoadException ex)
        {
            candidates = ex.Types.Where(t => t != null).Cast<Type>()
                .Where(t => t.Name.Contains("Visual", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("Description", StringComparison.OrdinalIgnoreCase))
                .Take(64).ToArray();
        }

        var attempts = new List<object>();
        foreach (var t in candidates)
        {
            try
            {
                using var br = new BinaryReader(new MemoryStream(data));
                var parsed = Construct(t, br);
                attempts.Add(new
                {
                    type = t.FullName,
                    success = true,
                    consumed = br.BaseStream.Position,
                    members = Snapshot(parsed, 0, new HashSet<object>(ReferenceEqualityComparer.Instance))
                });
            }
            catch (Exception ex)
            {
                attempts.Add(new { type = t.FullName, success = false, error = ex.GetBaseException().Message });
            }
        }
        return new { candidateTypes = candidates.Select(t => t.FullName).ToArray(), attempts };
    }

    static object Construct(Type t, BinaryReader br)
    {
        foreach (var c in t.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            var p = c.GetParameters();
            if (p.Length == 1 && p[0].ParameterType.IsAssignableFrom(typeof(BinaryReader)))
                return c.Invoke(new object[] { br });
        }
        throw new MissingMethodException($"No BinaryReader constructor for {t.FullName}");
    }

    static object? Snapshot(object? value, int depth, HashSet<object> visited)
    {
        if (value == null) return null;
        if (depth > 4) return value.ToString();
        var t = value.GetType();
        if (t.IsEnum || t.IsPrimitive || value is decimal || value is string)
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        if (value is byte[] bytes) return $"byte[{bytes.Length}]";
        if (!t.IsValueType && !visited.Add(value)) return "<cycle>";

        if (value is IEnumerable e)
        {
            var list = new List<object?>();
            foreach (var x in e)
            {
                if (list.Count >= 96) { list.Add("<truncated>"); break; }
                list.Add(Snapshot(x, depth + 1, visited));
            }
            return list;
        }

        var dict = new Dictionary<string, object?>();
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            try { dict[p.Name] = Snapshot(p.GetValue(value), depth + 1, visited); } catch { }
            if (dict.Count >= 96) break;
        }
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (dict.ContainsKey(f.Name)) continue;
            try { dict[f.Name] = Snapshot(f.GetValue(value), depth + 1, visited); } catch { }
            if (dict.Count >= 96) break;
        }
        return dict;
    }

    static bool TryId(string s, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }
}
