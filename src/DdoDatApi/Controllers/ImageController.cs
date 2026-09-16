using Microsoft.AspNetCore.Mvc;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using VoK.Sdk.Ddo.Enums;
using VoK.Sdk.Enums;
using VoK.Sdk.Properties;

namespace DdoDatApi.Controllers;

/// <summary>
/// Loads images from dat files known to contain images.
/// </summary>
[ApiController]
[Route("[controller]")]
public class ImageController : ControllerBase
{
    // Image IDs for icon underlayer backgrounds (non-magical vs magical items)
    private const uint UnderlayNone = 0x41004735;
    private const uint UnderlayMagic = 0x41004736;
    private const uint IngredientTypeBackground = 0x4101FB3C;

    /// <summary>
    /// gets a singular image from the SDK's ImageExporter. Does not load from the highres dat.
    /// </summary>
    /// <param name="id">the Id to get. Hexadecimal is best and should have a "0x" prefix. Also supports integers (without "0x"), if need be. Values must be in the range
    /// of a UINT, from 0x78000000 to 0x79FFFFFF</param>
    [HttpGet("{id}")]
    [ProducesResponseType<FileContentResult>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.NotFound)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest)]
    public IActionResult Get(string id)
    {
        if (!id.IsValid(out var datId, out var error))
            return error;

        var imgBytes = DatSource.ImageExporter.GetPngImageBytes(datId);
        if (imgBytes == null || imgBytes.Length < 1) return new NotFoundResult();

        return File(imgBytes, "image/png");
    }

    /// <summary>
    /// Generates the composite icon for the item specified by its DbProperties ID.
    /// Layers the underlayer, background, magic border, and item icon into a single PNG.
    /// </summary>
    /// <param name="id">The DbProperties ID of the item. Hexadecimal with "0x" prefix is preferred.</param>
    [HttpGet("Icon/{id}")]
    [ProducesResponseType<FileContentResult>((int)HttpStatusCode.OK)]
    [ProducesResponseType((int)HttpStatusCode.NotFound)]
    [ProducesResponseType((int)HttpStatusCode.BadRequest)]
    public IActionResult GetIcon(string id)
    {
        if (!id.IsValid(out var itemId, out var error))
            return error;

        var props = DatSource.PropertyMaster.GetPropertyCollection(itemId);
        if (props == null) return new NotFoundResult();

        var pngBytes = GenerateIconBytes(props);
        if (pngBytes == null || pngBytes.Length < 1) return new NotFoundResult();

        return File(pngBytes, "image/png", $"0x{itemId:X8}.png");
    }

    private static byte[] GenerateIconBytes(IPropertyCollection props)
    {
        Image underlayer = null;
        Image background = null;
        Image icon = null;
        Image magicLayer = null;

        try
        {
            var treasureTypeProp = props.GetEnumProperty((uint)DdoProperty.Treasure_Type);
            var isMagical = treasureTypeProp != null && treasureTypeProp.UInt32Value != 0;

            var underlayId = props.GetInt32PropertyValue((uint)DdoProperty.Item_UnderlayLayer);
            if (underlayId != null && underlayId != 0)
                underlayer = DatSource.ImageExporter.GetPngImage((uint)underlayId.Value);
            else
                underlayer = DatSource.ImageExporter.GetPngImage(isMagical ? UnderlayMagic : UnderlayNone);

            if (underlayer == null) return null;

            var backgroundId = props.GetInt32PropertyValue((uint)DdoProperty.Item_BackgroundLayer)
                ?? props.GetInt32PropertyValue((uint)DdoProperty.Item_ArmorBackgroundLayer)
                ?? props.GetInt32PropertyValue((uint)DdoProperty.Icon_Target_m_didBackgroundLayer);

            var weenieType = props.GetEnumProperty((uint)DdoProperty.WeenieType);
            var ingredType = props.GetEnumProperty((uint)DdoProperty.Ingredient_IngredientType);
            if (weenieType?.UInt32Value == (uint)WeenieType.Ingredient && ingredType != null)
                background = DatSource.ImageExporter.GetPngImage(IngredientTypeBackground);
            else if (backgroundId != null)
                background = DatSource.ImageExporter.GetPngImage((uint)backgroundId.Value);

            var specialUnderlayId = props.GetInt32PropertyValue((uint)DdoProperty.Icon_Target_m_didUnderLayer);
            if (specialUnderlayId != null && specialUnderlayId != 0)
            {
                using var secondUnderlay = DatSource.ImageExporter.GetPngImage((uint)specialUnderlayId.Value);
                if (secondUnderlay != null && background != null)
                {
                    using var g = Graphics.FromImage(background);
                    g.DrawImage(secondUnderlay, 0, 0);
                }
            }

            var iconId = props.GetInt32PropertyValue((uint)DdoProperty.Item_Icon)
                ?? props.GetInt32PropertyValue((uint)DdoProperty.Icon_Target_m_didImageLayer);
            if (iconId != null)
                icon = DatSource.ImageExporter.GetPngImage((uint)iconId.Value);

            var magicLayerId = props.GetInt32PropertyValue((uint)DdoProperty.Item_MagicalLayer)
                ?? props.GetInt32PropertyValue((uint)DdoProperty.Icon_Target_m_didMagicLayer);
            if (magicLayerId != null && magicLayerId != 0)
                magicLayer = DatSource.ImageExporter.GetPngImage((uint)magicLayerId.Value);

            using var graphics = Graphics.FromImage(underlayer);
            if (background != null) graphics.DrawImage(background, 0, 0);
            if (magicLayer != null) graphics.DrawImage(magicLayer, 0, 0);
            if (icon != null) graphics.DrawImage(icon, 0, 0);

            using var ms = new MemoryStream();
            underlayer.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            underlayer?.Dispose();
            background?.Dispose();
            icon?.Dispose();
            magicLayer?.Dispose();
        }
    }
}
