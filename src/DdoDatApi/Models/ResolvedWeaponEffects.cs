using System.Collections.Generic;
using VoK.Sdk.Common;

namespace DdoDatApi.Models;

/// <summary>
/// The viewer-facing shape of VoK.Sdk's <see cref="VoK.Sdk.Common.WeaponEffects"/>: the same data with
/// surface ids turned into Image URLs and enum values into names. Emitters, keyframes and waveforms are
/// the SDK's own types, serialized as they are.
/// </summary>
public sealed class ResolvedWeaponEffects
{
    public string ItemId { get; set; } = "";

    public string ImbueType { get; set; } = "Invalid";

    public string ImbueAlignment { get; set; } = "Invalid";

    public List<string> ImbueSources { get; set; } = new();

    /// <summary>The weenie's Render_Color as RGBA 0-1; the client tints the base mesh with it.</summary>
    public float[] BaseTint { get; set; }

    public WeaponEffectShell Shell { get; set; }

    public List<WeaponEffectSpawn> Spawns { get; set; } = new();

    /// <summary>Keyed by PSDescription id, e.g. "0x39000243".</summary>
    public Dictionary<string, ResolvedParticleSystem> ParticleSystems { get; set; } = new();

    public WeaponEffectStreak Streak { get; set; }

    public List<string> Notes { get; set; } = new();
}

/// <summary>The translucent aura mesh laid over the weapon.</summary>
public sealed class WeaponEffectShell
{
    public string EntityId { get; set; } = "";

    public string SetupId { get; set; } = "";

    public string AppearanceId { get; set; } = "";

    public string AppearanceKey { get; set; } = "";

    public float[] Tint { get; set; } = new[] { 1f, 1f, 1f };

    public float Opacity { get; set; } = 1f;

    public bool DepthWrite { get; set; }

    public bool DepthTest { get; set; } = true;

    public bool DoubleSided { get; set; }

    public List<WeaponEffectShellLayer> Layers { get; set; } = new();

    /// <summary>Filled in by the desktop shell once it has exported the shell mesh.</summary>
    public string GlbUrl { get; set; }
}

/// <summary>One texture layer of the aura material; each transform is a waveform sampled per frame.</summary>
public sealed class WeaponEffectShellLayer
{
    public string TextureUrl { get; set; }

    public Waveform UTranslate { get; set; }

    public Waveform VTranslate { get; set; }

    public Waveform UScale { get; set; }

    public Waveform VScale { get; set; }

    public Waveform UVRotate { get; set; }
}

/// <summary>One particle system placed at one of the weapon's holding locations.</summary>
public sealed class WeaponEffectSpawn
{
    public string Anchor { get; set; } = "";

    /// <summary>Anchor position in DDO model space (Z-up); the viewer converts to Y-up.</summary>
    public float[] Position { get; set; } = new[] { 0f, 0f, 0f };

    public string ParticleSystem { get; set; } = "";
}

public sealed class ResolvedParticleSystem
{
    public string Id { get; set; } = "";

    public string TextureUrl { get; set; }

    public float StartFade { get; set; }

    public float StopFade { get; set; }

    public bool InheritOpacity { get; set; }

    public List<ParticleEmitterDesc> Emitters { get; set; } = new();
}

/// <summary>The swing-trail override the imbue script applies. Carried for the viewer; not drawn yet.</summary>
public sealed class WeaponEffectStreak
{
    public string Color { get; set; } = "";

    public string MaterialId { get; set; } = "";
}
