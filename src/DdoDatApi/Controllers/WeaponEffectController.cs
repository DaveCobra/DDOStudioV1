using DdoDatApi.Models;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using VoK.Sdk.Common;
using VoK.Sdk.Ddo.Enums;

namespace DdoDatApi.Controllers;

/// <summary>
/// Serves a weapon's imbue/alignment visual effects for the viewer.
///
/// All resolution lives in VoK.Sdk (<see cref="WeaponEffectResolver"/>) because it is game truth, not an
/// API concern. This controller only adapts the SDK result to what a browser can consume: surface ids
/// become Image URLs, ids become hex strings, and enum values become their names.
/// </summary>
[ApiController]
[Route("[controller]")]
public sealed class WeaponEffectController : ControllerBase
{
    [HttpGet("resolve")]
    [ProducesResponseType<ResolvedWeaponEffects>(200)]
    [ProducesResponseType(400)]
    public IActionResult Resolve([FromQuery] string db = null)
    {
        if (string.IsNullOrWhiteSpace(db)) return BadRequest("Provide the item's DbProperties id as db.");
        if (!db.IsValid(out var dbId, out var error)) return error;

        var effects = WeaponEffectResolver.Resolve(DatSource.GameLogicDat, DatSource.GeneralDat,
                                                   DatSource.PropertyMaster, dbId);

        var result = new ResolvedWeaponEffects
        {
            ItemId = Hex(effects.ItemId),
            ImbueType = EnumName(typeof(WeaponImbueType), effects.Imbue.Type),
            ImbueAlignment = EnumName(typeof(WeaponImbueType2), effects.Imbue.Alignment),
            ImbueSources = effects.Imbue.Sources,
            BaseTint = Rgba(effects.RenderColor),
            Notes = effects.Notes,
        };

        if (effects.StreakColor != 0 || effects.StreakMaterialDid != 0)
            result.Streak = new WeaponEffectStreak
            {
                Color = $"{effects.StreakColor:X8}",
                MaterialId = Hex(effects.StreakMaterialDid),
            };

        foreach (var spawn in effects.Spawns)
            result.Spawns.Add(new WeaponEffectSpawn
            {
                Anchor = EnumName(typeof(VoK.Sdk.Ddo.Enums.HoldingLocation), spawn.HoldingLocation),
                Position = new[] { spawn.Position.X, spawn.Position.Y, spawn.Position.Z },
                ParticleSystem = Hex(spawn.ParticleId),
            });

        foreach (var system in effects.ParticleSystems)
            result.ParticleSystems[Hex(system.ParticleId)] = new ResolvedParticleSystem
            {
                Id = Hex(system.ParticleId),
                TextureUrl = ImageUrl(system.SurfaceDid),
                StartFade = system.Description.StartFadeDistance,
                StopFade = system.Description.StopFadeDistance,
                InheritOpacity = system.Description.InheritOpacity,
                Emitters = system.Description.Emitters.Where(x => x.Active).ToList(),
            };

        if (effects.Aura != null)
            result.Shell = new WeaponEffectShell
            {
                EntityId = Hex(effects.Aura.EntityId),
                SetupId = Hex(effects.Aura.SetupId),
                AppearanceId = Hex(effects.Aura.AppearanceId),
                AppearanceKey = EnumName(typeof(AppearanceKey), effects.Aura.AppearanceKey),
                Tint = effects.Aura.Tint,
                Opacity = effects.Aura.Opacity,
                DepthWrite = effects.Aura.DepthWrite,
                DepthTest = effects.Aura.DepthTest,
                DoubleSided = effects.Aura.DoubleSided,
                Layers = effects.Aura.Layers.Select(layer => new WeaponEffectShellLayer
                {
                    TextureUrl = ImageUrl(layer.SurfaceDid),
                    UTranslate = layer.UTranslate,
                    VTranslate = layer.VTranslate,
                    UScale = layer.UScale,
                    VScale = layer.VScale,
                    UVRotate = layer.UVRotate,
                }).ToList(),
            };

        return Ok(result);
    }

    private static string Hex(uint value) => $"0x{value:X8}";

    private static string ImageUrl(uint surfaceDid) => surfaceDid == 0 ? null : $"Image/0x{surfaceDid:X8}";

    private static string EnumName(Type enumType, uint value) => Enum.GetName(enumType, value) ?? $"0x{value:X8}";

    /// <summary>Render_Color is 0xAARRGGBB; the viewer wants RGBA 0-1.</summary>
    private static float[] Rgba(uint? argb)
    {
        if (argb == null || argb.Value == 0 || argb.Value == 0xFFFFFFFF) return null;
        return new[]
        {
            ((argb.Value >> 16) & 0xFF) / 255f,
            ((argb.Value >> 8) & 0xFF) / 255f,
            (argb.Value & 0xFF) / 255f,
            ((argb.Value >> 24) & 0xFF) / 255f,
        };
    }
}
