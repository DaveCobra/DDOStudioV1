using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using VoK.Sdk.Common;

namespace DdoDatApi.Controllers;

[ApiController]
[Route("[controller]")]
public class AppearanceDiagnosticController : ControllerBase
{
    static readonly Assembly Sdk = typeof(RenderMesh).Assembly;

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        if (!TryId(id, out var appearanceId) || (appearanceId >> 24) != 0x20)
            return BadRequest("Expected an Appearance ID in the 0x20xxxxxx range.");

        var data = DatSource.GeneralDat?.GetFileContents(appearanceId);
        if (data == null) return NotFound();

        var refs = ScanReferences(data);
        var ascii = ScanAscii(data);
        var sdk = TrySdkParse(data);

        return Ok(new
        {
            appearanceId,
            byteLength = data.Length,
            sha1 = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(data)),
            firstBytes = Convert.ToHexString(data.AsSpan(0, Math.Min(128, data.Length))),
            references = refs,
            asciiStrings = ascii,
            sdk
        });
    }

    static object[] ScanReferences(byte[] data)
    {
        var hits = new Dictionary<uint, List<int>>();
        for (int o = 0; o + 4 <= data.Length; o += 4)
        {
            uint v = BitConverter.ToUInt32(data, o);
            byte p = (byte)(v >> 24);
            if (!KnownPrefix(p)) continue;
            if (!hits.TryGetValue(v, out var offsets)) hits[v] = offsets = new List<int>();
            if (offsets.Count < 32) offsets.Add(o);
        }

        return hits.OrderBy(kv => kv.Key).Select(kv => (object)new
        {
            id = kv.Key,
            hex = $"0x{kv.Key:X8}",
            type = TypeName((byte)(kv.Key >> 24)),
            count = kv.Value.Count,
            offsets = kv.Value
        }).ToArray();
    }

    static bool KnownPrefix(byte p) => p is
        0x04 or // Setup
        0x06 or // RenderMesh
        0x1F or // VisualDescription
        0x20 or // Appearance
        0x2B or // RenderMaterial
        0x30 or // MaterialModifier
        0x31 or // MaterialInstance
        0x39 or // PSDescription / particle-system description
        0x40 or // RenderTexture
        0x41 or // RenderSurface
        0x47 or // EntityDescription
        0x66;   // PhysicsMesh

    static string TypeName(byte p) => p switch
    {
        0x04 => "Setup",
        0x06 => "RenderMesh",
        0x1F => "VisualDescription",
        0x20 => "Appearance",
        0x2B => "RenderMaterial",
        0x30 => "MaterialModifier",
        0x31 => "MaterialInstance",
        0x39 => "PSDescription",
        0x40 => "RenderTexture",
        0x41 => "RenderSurface",
        0x47 => "EntityDescription",
        0x66 => "PhysicsMesh",
        _ => "Unknown"
    };

    static string[] ScanAscii(byte[] data)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length >= 4 && result.Count < 64) result.Add(sb.ToString());
            sb.Clear();
        }
        foreach (var b in data)
        {
            if (b >= 32 && b <= 126) sb.Append((char)b);
            else Flush();
        }
        Flush();
        return result.Distinct().Take(64).ToArray();
    }

    static object TrySdkParse(byte[] data)
    {
        var candidates = Sdk.GetTypes()
            .Where(t => t.Namespace?.StartsWith("VoK.Sdk", StringComparison.Ordinal) == true)
            .Where(t => t.Name.Contains("Appearance", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.FullName)
            .ToArray();

        var attempts = new List<object>();
        foreach (var t in candidates.Take(24))
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
                // One successful parser is enough; keep candidate list small and deterministic.
                break;
            }
            catch (Exception ex)
            {
                attempts.Add(new { type = t.FullName, success = false, error = ex.GetBaseException().Message });
            }
        }
        return new { candidateTypes = candidates.Select(t => t.FullName).ToArray(), attempts };
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
        throw new MissingMethodException($"No BinaryReader constructor for {t.FullName}");
    }

    static object? Snapshot(object? value, int depth, HashSet<object> visited)
    {
        if (value == null) return null;
        if (depth > 3) return value.ToString();
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
                if (list.Count >= 48) { list.Add("<truncated>"); break; }
                list.Add(Snapshot(x, depth + 1, visited));
            }
            return list;
        }

        var dict = new Dictionary<string, object?>();
        foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (p.GetIndexParameters().Length != 0) continue;
            try { dict[p.Name] = Snapshot(p.GetValue(value), depth + 1, visited); } catch { }
            if (dict.Count >= 64) break;
        }
        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (dict.ContainsKey(f.Name)) continue;
            try { dict[f.Name] = Snapshot(f.GetValue(value), depth + 1, visited); } catch { }
            if (dict.Count >= 64) break;
        }
        return dict;
    }

    static bool TryId(string s, out uint id)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
    }
}
