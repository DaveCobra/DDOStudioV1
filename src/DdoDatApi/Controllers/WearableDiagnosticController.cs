using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Properties;

namespace DdoDatApi.Controllers;

/// <summary>
/// Focused diagnostic for items whose visual only exists while equipped.
/// Unlike EquipmentChainController, this does not recursively walk arbitrary DAT records.
/// It stays anchored to the item's DbProperties record and its directly referenced WState records.
/// </summary>
[ApiController]
[Route("[controller]")]
public class WearableDiagnosticController : ControllerBase
{
    public const uint PurpleBagVisualDescription = 0x1F000013;

    [HttpGet("{dbId}")]
    public IActionResult Get(string dbId)
    {
        if (!TryId(dbId, out var root) || root < 0x78000000 || root > 0x7FFFFFFF)
            return BadRequest("Expected a DbProperties ID in the 0x78xxxxxx-0x7Fxxxxxx range.");

        byte[]? rootBytes = null;
        try { rootBytes = DatSource.GameLogicDat?.GetFileContents(root); } catch { }
        if (rootBytes == null) return NotFound($"DbProperties record 0x{root:X8} was not found.");

        IPropertyCollection? props = null;
        try { props = DatSource.PropertyMaster.GetPropertyCollection(root); } catch { }
        if (props == null) return NotFound($"DbProperties object 0x{root:X8} could not be decoded.");

        uint weenieType = 0;
        string weenieTypeName = "Unknown";
        try
        {
            weenieType = props.GetWeenieType();
            weenieTypeName = Enum.GetName(typeof(WeenieType), weenieType) ?? $"0x{weenieType:X8}";
        }
        catch { }

        JsonElement propertyJson;
        try { propertyJson = JsonSerializer.SerializeToElement(props, props.GetType()); }
        catch { propertyJson = JsonDocument.Parse("{}").RootElement.Clone(); }

        var directRefs = ScanReferences(rootBytes).ToArray();
        var wstates = directRefs.Where(r => (r.Id >> 24) == 0x70)
            .Select(r => LoadWState(r.Id, r.Offset))
            .Where(x => x != null)
            .Cast<object>()
            .ToArray();

        var placeholderHits = directRefs.Where(r => r.Id == PurpleBagVisualDescription).ToArray();

        return Ok(new
        {
            format = "DDO Studio Wearable Diagnostic",
            diagnosticVersion = 1,
            generatedUtc = DateTime.UtcNow,
            root = new
            {
                id = root,
                hex = $"0x{root:X8}",
                byteLength = rootBytes.Length,
                sha1 = Convert.ToHexString(SHA1.HashData(rootBytes)),
                weenieType,
                weenieTypeHex = $"0x{weenieType:X8}",
                weenieTypeName
            },
            knownPlaceholders = new[]
            {
                new { id = PurpleBagVisualDescription, hex = $"0x{PurpleBagVisualDescription:X8}", meaning = "Generic purple bag / inventory placeholder VisualDescription" }
            },
            placeholderSeenDirectly = placeholderHits.Length > 0,
            directReferences = directRefs.Select(r => new { id = r.Id, hex = $"0x{r.Id:X8}", type = TypeName(r.Id), offset = r.Offset, hexOffset = $"0x{r.Offset:X4}" }).ToArray(),
            wstates,
            parsedProperties = propertyJson,
            notes = new[]
            {
                "Wearable items may have no standalone world model; their visible geometry can be applied only when equipped to a character.",
                "This diagnostic deliberately avoids following unrelated shared DAT records.",
                "WState records are preserved as first-class candidates because some wearable DbProperties records reference them directly."
            }
        });
    }

    object? LoadWState(uint id, int rootOffset)
    {
        byte[]? bytes = null;
        try { bytes = DatSource.GameLogicDat?.GetFileContents(id); } catch { }
        if (bytes == null) return null;
        var refs = ScanReferences(bytes).ToArray();
        return new
        {
            id,
            hex = $"0x{id:X8}",
            rootOffset,
            byteLength = bytes.Length,
            sha1 = Convert.ToHexString(SHA1.HashData(bytes)),
            firstBytes = Convert.ToHexString(bytes.AsSpan(0, Math.Min(256, bytes.Length))),
            references = refs.Select(r => new { id = r.Id, hex = $"0x{r.Id:X8}", type = TypeName(r.Id), offset = r.Offset, hexOffset = $"0x{r.Offset:X4}" }).ToArray()
        };
    }

    sealed record RefHit(uint Id, int Offset);

    static IEnumerable<RefHit> ScanReferences(byte[] bytes)
    {
        var seen = new HashSet<(uint, int)>();
        for (int o = 0; o + 4 <= bytes.Length; o += 4)
        {
            uint id = BitConverter.ToUInt32(bytes, o);
            if (id == 0 || TypeName(id) == null) continue;
            if (seen.Add((id, o))) yield return new RefHit(id, o);
        }
    }

    static string? TypeName(uint id)
    {
        foreach (var r in DatSource.IdRanges)
            if (id >= r.Minimum && id <= r.Maximum)
                return string.IsNullOrWhiteSpace(r.Name) ? r.Description : r.Name;
        return null;
    }

    static bool TryId(string text, out uint id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out id);
        return uint.TryParse(text, out id);
    }
}
