# Weapon Visual Effect Rendering Implementation Plan

> **Status (2026-09-17): executed.** All eight tasks are implemented and everything verifiable without
> a screen has been verified — backend resolution against Sireth and against unimbued weapons, the
> waveform/particle simulator headless against the real payload, and clean builds of all three
> projects. What remains is the on-screen check in Task 8 Step 4, which needs human eyes.
> Deviations from the plan as written: the DTO is `ResolvedWeaponEffects` (not `…Plan`) with
> `WaveformData` (not `WaveformDto`), the endpoint is `WeaponEffect/resolve` (matching the existing
> `VisualEffect/resolve`), and node types are matched via the SDK's public `ScriptNode.Type` string
> because `ScriptNodeType` is internal. Task 2's three API fixes were the only 5.0 breakages.

> **For agentic workers:** steps use checkbox (`- [ ]`) syntax for tracking. Execute tasks in order;
> each ends with something you can look at and judge. This plan deliberately contains **no
> test-first steps and no commit steps** — that is the repository owner's standing preference.

**Goal:** Render a weapon's real imbue/alignment visual effects in DDO Studio — the aura shell and the
sprite-sheet particle emitters — driven by the game's own data instead of texture guesswork.

**Architecture:** "Backend" here means DDO Studio's own bundled dat service, `src/DdoDatApi` — an
ASP.NET Core app the WinForms shell launches as `backend/DdoDatApi.exe` on `127.0.0.1:<dynamic port>`
and talks to over HTTP. It is not the standalone ddonexus DatApi, though the endpoint shape is
familiar (`DbProperties`, `EntityDesc`, `Image`, `RawDat`, …). It gains one new endpoint that walks the actual effect chain (item effects →
imbue properties → class script tables → particle systems + aura appearance) and returns a typed
"effect plan". The viewer gains a small particle simulator that consumes that plan, evaluates DDO
Waveforms per frame, and draws sprite-sheet particles at the weapon's own holding-location anchors.
The existing heuristic `VisualEffect/resolve` path stays as the fallback for assets with no plan.

**Tech Stack:** .NET 10 (WinForms shell, ASP.NET Core loopback backend, console exporter), VoK.Sdk 5.0,
three.js r128 global build in a single-page viewer, WebView2.

**Spec:** `C:\dev\ddonexus\weapon-visual-effects.md` — the reverse-engineered chain and binary formats.
Read it first; every id, enum and field name below comes from it.

## Global Constraints

- Windows x64, .NET 10 SDK.
- No telemetry; backend binds loopback only; all game data read from the user's local install.
- No DDO content is redistributed — textures and meshes are resolved at runtime from the user's dats.
- three.js is **r128, global build** (`THREE.*`, no ES modules). `viewer/` is copied wholesale by
  `build-preview-and-run.cmd`, so new viewer files need only a `<script>` tag.
- VoK.Sdk 5.0 or newer is required: it has `PSDescription`/`ParticleEmitterDesc`/`ParticleKeyframeDesc`/
  `Waveform` and the `WaveFormProperty` field-order fix. 4.3.5 has neither.
- DDO is Z-up, glTF is Y-up. The exporter applies a -90° X conversion at the scene root, so any
  position or direction taken from Setup/PSDescription data must be converted with
  `(x, y, z)_ddo → (x, z, -y)_gltf` before use in viewer space.
- Match the surrounding code style. `viewer/index.html` is written as very dense one-liners; the new
  viewer module may be normally formatted, but keep the existing file's style when editing it.

---

## File Structure

**New files**

| File | Responsibility |
|---|---|
| `src/DdoDatApi/Models/WeaponEffectPlan.cs` | DTOs for the effect plan (the backend↔viewer contract) |
| `src/DdoDatApi/Controllers/WeaponEffectController.cs` | `GET WeaponEffect/plan` endpoint |
| `src/DdoDatApi/Controllers/ImbueStateResolver.cs` | item + creation effects → current `Weapon_Imbue_*` values |
| `src/DdoDatApi/Controllers/ScriptGraphWalker.cs` | include-tree flattening + switch evaluation over ScriptTable data |
| `src/DDOAssetStudio/viewer/vfx-particles.js` | Waveform evaluator + particle simulator + aura shell material |

**Modified files**

| File | Change |
|---|---|
| `C:\dev\dh5\vok.sdk\src\VoK.Sdk\Common\NodePort.cs` | typed switch-port data so consumers can evaluate switches |
| `src/DdoDatApi/DdoDatApi.csproj`, `src/DDOExporter/DDOGlbExporter.csproj` | VoK.Sdk 4.3.5 → 5.0 |
| `src/DDOAssetStudio/viewer/index.html` | load the new module; route plans to it; keep the legacy path |
| `src/DDOAssetStudio/Program.cs` | fetch the plan, export the shell GLB, hand both to the viewer |
| `VFX_RENDERING_NOTES.md`, `docs/TECHNICAL_ARCHITECTURE.md` | document the data-driven pipeline |

---

### Task 1: Typed switch-port data in the SDK

The walker in Task 3 must read which property value selects each Switch branch. Today
`NodePort.NodeData` holds an **anonymous** object for `SwitchValue`/`WhenTest` ports, which is
unreadable to consumers without reflection.

**Files:**
- Modify: `C:\dev\dh5\vok.sdk\src\VoK.Sdk\Common\NodePort.cs` (`ReadSwitchValueData`, `ReadSwitchTransitionData`, the `When`/`WhenTest` case)
- Create: `C:\dev\dh5\vok.sdk\src\VoK.Sdk\Common\SwitchPortData.cs`

**Interfaces:**
- Produces: `SwitchValueData { IReadOnlyList<IProperty> Properties; IReadOnlyList<uint> Ints; uint Operator; float Unknown1; float Unknown2; }`,
  `SwitchTransitionData { IProperty Property1; IProperty Property2; byte Bool1; byte Bool2; float Float1; byte Bool3; float Float2; float Float3; float Float4; }`,
  `WhenTestData { IProperty WhenProperty; uint[] WhenData; }`

- [ ] **Step 1: Add the typed classes**

`SwitchPortData.cs`, following the file conventions of `Common/MaterialMod.cs` (UTF-8 BOM, LF, spaces,
`/** \addtogroup generic_stuff Dat Objects @{ */` banner, no tuples):

```csharp
public class SwitchValueData
{
    /// <summary>The property values that select this branch; the switch's own property id is on the Switch node.</summary>
    public IReadOnlyList<IProperty> Properties { get; internal set; }

    public IReadOnlyList<uint> Ints { get; internal set; }

    /// <summary>Comparison operator; 2 is the only value seen in weapon-imbue scripts (equality).</summary>
    public uint Operator { get; internal set; }

    public float Unknown1 { get; internal set; }

    public float Unknown2 { get; internal set; }
}
```

- [ ] **Step 2: Return the typed classes from NodePort**

Replace the anonymous returns in `ReadSwitchValueData` / `ReadSwitchTransitionData` and the
`(When, WhenTest)` case with the new types. Read order must not change — only the container type.

- [ ] **Step 3: Build and spot-check a known table**

Run: `dotnet build C:\dev\dh5\vok.sdk\src\VoK.Sdk\VoK.Sdk.csproj -c Debug`
Then dump `0x0700022C` with any harness and confirm the Good branch still reports
`Weapon_Imbue_Alignment=Good` and `AppearanceScript_Key=Weaponaura_Good`.

---

### Task 2: Move DDO Studio onto VoK.Sdk 5.0

**Files:**
- Modify: `src/DdoDatApi/DdoDatApi.csproj:16`, `src/DDOExporter/DDOGlbExporter.csproj:14`
- Create: `NuGet.config` (only if no local feed is configured yet)

- [ ] **Step 1: Pack the SDK to a local feed**

```bat
dotnet pack C:\dev\dh5\vok.sdk\src\VoK.Sdk\VoK.Sdk.csproj -c Release -o C:\dev\.local-nuget
```

If the repo has no local feed source, add `NuGet.config` at the repo root:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="local-vok" value="C:\dev\.local-nuget" />
  </packageSources>
</configuration>
```

- [ ] **Step 2: Bump both project references**

Change `Version="4.3.5"` to the packed 5.0 version in both csproj files.

- [ ] **Step 3: Apply the three known API fixes**

Both projects were compiled against VoK.Sdk 5.0.0 out-of-tree on 2026-09-17 to find this list.
`DDOGlbExporter` **compiles clean**. `DdoDatApi` has exactly three breakages, all unrelated to the
WSL network-parser removal (that removal only dropped wire-format readers, which DDO Studio never
used — it reads dat WStates, and does so by reflection over type names):

1. `src/DdoDatApi/Converters/IPropertyJsonConverter.cs:45` — `IUInt64Property` no longer exists; 5.0
   keeps only `IInt64Property` and `IUInt32Property`. Delete the arm:

```csharp
            IUInt64Property   => "UInt64",
```

2. `src/DdoDatApi/Caching/IndexLoader.cs:112` and `:193` — `IPropertyCollection.Properties` is now a
   list rather than a dictionary, so drop `.Values`:

```csharp
CollectTreasureItems(dbp.Properties, items, new HashSet<uint> { id });
```

3. `src/DdoDatApi/Controllers/EffectResolver.cs:264` — `IStringInfoProperty.GetStringEntry` is gone.
   The file's own comment explains why it matters: it wants the raw template with `{0}`/`{1}`
   placeholders intact, which `GetText(…, null, null)` drops. `IPropertyMaster.GetStringEntry` still
   exists in 5.0 and the StringInfo carries its own `Key`/`Table`, so the equivalent is:

```csharp
            var entry = prop.Key != null && prop.Table != null
                ? DatSource.PropertyMaster.GetStringEntry(prop.Key.Value, prop.Table.Value)
                : null;
            raw = entry?.Value ?? prop.GetText(DatSource.PropertyMaster, null, null);
```

Run: `dotnet build src\DdoDatApi\DdoDatApi.csproj -c Debug` and
`dotnet build src\DDOExporter\DDOGlbExporter.csproj -c Debug` to confirm nothing else surfaced.

- [ ] **Step 4: Confirm nothing regressed visually**

Run `build-preview-and-run.cmd`, search for `Sireth`, select it. The staff must still preview exactly
as before. Two things to check specifically because Step 3 touched them:

- The details pane's effect list (from `EffectResolver`) must still read e.g. "+7 Enhancement Bonus",
  with numbers substituted and no stray `{0}` or empty holes.
- The `WaveFormProperty` field-order fix changes every material waveform value, so open a model with
  an animated material (any `*VFX` weapon) and confirm it still renders.

---

### Task 3: Backend — resolve imbue state and the effect plan skeleton

**Files:**
- Create: `src/DdoDatApi/Models/WeaponEffectPlan.cs`
- Create: `src/DdoDatApi/Controllers/ImbueStateResolver.cs`
- Create: `src/DdoDatApi/Controllers/ScriptGraphWalker.cs`
- Create: `src/DdoDatApi/Controllers/WeaponEffectController.cs`

**Interfaces:**
- Consumes: `SwitchValueData` (Task 1); `DatSource.GameLogicDat`, `DatSource.GeneralDat`,
  `DatSource.PropertyMaster`; the existing `EffectContext`/`EffectResolver` in `Controllers/EffectResolver.cs`.
- Produces: `WeaponEffectPlan` (JSON contract used by Tasks 4–7), `ImbueStateResolver.Resolve(uint dbId)`,
  `ScriptGraphWalker.FindScript(uint classScriptTableId, uint fxKey)`,
  `ScriptGraphWalker.Collect(IEnumerable<ScriptNode> nodes, Func<uint, uint?> propertyValue)`

- [ ] **Step 1: Define the plan DTOs**

`WeaponEffectPlan.cs` — this is the whole contract, so write it out fully:

```csharp
namespace DdoDatApi.Models;

public sealed class WeaponEffectPlan
{
    public string ItemId { get; set; } = "";
    public string ImbueType { get; set; } = "Invalid";
    public string ImbueAlignment { get; set; } = "Invalid";
    /// <summary>Effects that set those properties, for the diagnostics pane.</summary>
    public List<string> ImbueSources { get; set; } = new();
    public WeaponEffectShell? Shell { get; set; }
    public List<WeaponEffectSpawn> Spawns { get; set; } = new();
    public Dictionary<string, WeaponParticleSystem> ParticleSystems { get; set; } = new();
    public WeaponEffectStreak? Streak { get; set; }
    public List<string> Notes { get; set; } = new();
}

public sealed class WeaponEffectShell
{
    public string EntityId { get; set; } = "";
    public string SetupId { get; set; } = "";
    public string AppearanceId { get; set; } = "";
    public string AppearanceKey { get; set; } = "";
    public float[] Tint { get; set; } = [1f, 1f, 1f];
    public float Opacity { get; set; } = 1f;
    public string? DiffuseTextureUrl { get; set; }
    /// <summary>UV animation, already reduced to units per second for u and v.</summary>
    public float[] UvScroll { get; set; } = [0f, 0f];
    public string? GlbUrl { get; set; }
}

public sealed class WeaponEffectSpawn
{
    public string Anchor { get; set; } = "";
    /// <summary>Anchor position in DDO model space; the viewer converts to Y-up.</summary>
    public float[] Position { get; set; } = [0f, 0f, 0f];
    public string ParticleSystem { get; set; } = "";
}

public sealed class WeaponParticleSystem
{
    public string Id { get; set; } = "";
    public string? TextureUrl { get; set; }
    public float StartFade { get; set; }
    public float StopFade { get; set; }
    public bool InheritOpacity { get; set; }
    public List<WeaponParticleEmitter> Emitters { get; set; } = new();
}

public sealed class WeaponParticleEmitter
{
    public int Shape { get; set; }
    public float BirthRate { get; set; }
    public uint MaxParticles { get; set; }
    public uint BlastCount { get; set; }
    public float StartTime { get; set; }
    public float TimeLimit { get; set; }
    public float FadeIn { get; set; }
    public float FadeOut { get; set; }
    public uint UFrames { get; set; }
    public uint VFrames { get; set; }
    public uint FramesPerSec { get; set; }
    public bool RandomizeStartFrame { get; set; }
    public WaveformDto Velocity { get; set; } = new();
    public WaveformDto Lifespan { get; set; } = new();
    public WaveformDto Scale { get; set; } = new();
    public WaveformDto ParticleScale { get; set; } = new();
    public WaveformDto MinSpread { get; set; } = new();
    public WaveformDto MaxSpread { get; set; } = new();
    public WaveformDto[] Direction { get; set; } = [];
    public WaveformDto[] OriginOffset { get; set; } = [];
    public WaveformDto[] Rotation { get; set; } = [];
    public WaveformDto[] RotationVelocity { get; set; } = [];
    public List<WeaponParticleKey> Keys { get; set; } = new();
}

public sealed class WeaponParticleKey
{
    public float Time { get; set; }
    public float ScaleX { get; set; }
    public float ScaleY { get; set; }
    /// <summary>0xAARRGGBB.</summary>
    public string Color { get; set; } = "FFFFFFFF";
}

public sealed class WaveformDto
{
    public string Type { get; set; } = "None";
    public float Base { get; set; }
    public float BaseVelocity { get; set; }
    public float Amplitude { get; set; }
    public float AmplitudeVelocity { get; set; }
    public float Phase { get; set; }
    public float PhaseVelocity { get; set; }
    public float Frequency { get; set; }
    public float FrequencyVelocity { get; set; }
    public float KeyframeDuration { get; set; }
    public bool KeyframeLoops { get; set; }
    public List<float[]> Keyframes { get; set; } = new();
}

public sealed class WeaponEffectStreak
{
    public string Color { get; set; } = "";
    public string MaterialId { get; set; } = "";
}
```

- [ ] **Step 2: Resolve the live imbue state**

`ImbueStateResolver.cs`. The weenie's own `Weapon_Imbue_Type`/`Weapon_Imbue_Alignment` are usually
`Invalid`; the values that matter come from `Effect_OnCreationEffects` mods with
`Mod_Op = Set` writing to `0x10000BC5` / `0x10000C28`. `Controllers/EffectResolver.cs` already walks
those arrays and applies mods — reuse its `EffectContext` rather than re-implementing:

```csharp
internal sealed class ImbueState
{
    public uint Type { get; set; }
    public uint Alignment { get; set; }
    public List<string> Sources { get; } = new();
}
```

Walk `Effect_OnCreationEffects` → each `Effect_Entry`'s `Effect` DID → that effect's `Mod_Array` →
for each `Mod` whose `Mod_Destination` is `Weapon_Imbue_Type` or `Weapon_Imbue_Alignment` and whose
`Mod_Op` is `Set`, take the mod's value for that property id and record the effect's name in `Sources`.
Last writer wins, matching the client's ordered application.

- [ ] **Step 3: Flatten the include tree and evaluate switches**

`ScriptGraphWalker.cs`:

```csharp
// Depth-first over ScriptTable.Includes; returns every script node list registered under fxKey.
public static List<ScriptNode> FindScript(IDatFile gameLogic, IPropertyMaster pm, uint tableId, uint fxKey);
```

`FindScript` loads the table, collects `Scripts[fxKey]` when present, then recurses into `Includes`
(guard against repeats with a `HashSet<uint>`; the shared tables are large and appear more than once).

Then the evaluator. For a `Switch` node, `SwitchScript_Property` gives the property id; each output
port is either a `SwitchValue` (with `SwitchValueData.Properties` listing the values that select it) or
the `Default` port (taken when no `SwitchValue` matched):

```csharp
public static void Collect(IEnumerable<ScriptNode> nodes, Func<uint, uint?> propertyValue, ICollection<ScriptNode> sink)
{
    foreach (var node in nodes)
    {
        if (node == null) continue;
        if (node.TypeId == (byte)ScriptNodeType.Switch)
        {
            var propId = node.Properties.TryGetValue((uint)DdoProperty.SwitchScript_Property, out var p)
                ? p.RawUInt32 : 0u;
            var current = propertyValue(propId);
            NodePort matched = null, fallback = null;
            foreach (var port in node.Outputs ?? new())
            {
                if (port.NodeData is SwitchValueData sv)
                {
                    if (current != null && sv.Properties.Any(x => x.PropertyId == propId && ValueOf(x) == current))
                        matched = port;
                }
                else fallback ??= port;
            }
            var chosen = matched ?? fallback;
            if (chosen != null) Collect(chosen.Nodes, propertyValue, sink);
            continue;
        }

        sink.Add(node);
        foreach (var port in node.Outputs ?? new()) Collect(port.Nodes, propertyValue, sink);
    }
}
```

`ValueOf` reads an `IProperty` as a uint (enum/int/byte). `Collection` and `Run` nodes need no special
casing here: `Collection` just fans out through its ports, and a `Run` node's `RunScript_ID` is followed
by the caller in Step 4.

- [ ] **Step 4: The endpoint**

`WeaponEffectController.cs`, `[HttpGet("plan")]`, query `db` (required), optional `setup`:

1. Resolve the imbue state (Step 2). If both values are `Invalid`, return a plan with empty `Spawns`
   and a note — that is a legitimate "no effect" answer, not an error.
2. `db` → `PhysObj` → EntityDescription → `Entity_ClassScriptTable` → `ScriptTable_DBScriptTable`.
3. `FindScript(classTable, FX.Weapon_Imbue_Powerup)`, `Collect` with the imbue state, and keep the
   `Particle` nodes: each gives `ParticleScript_ParticleID` and `ParticleScript_HoldingLocation`.
   Also keep `SetProperty` nodes writing `Script_OverrideStreakColor` / `Script_OverrideStreakMaterial`
   for `Streak`.
4. Load the weapon's `Setup` and, for every collected Particle node, look up its holding location. **Skip
   nodes whose location the Setup does not define** — that is what the client does. Emit one
   `WeaponEffectSpawn` per surviving node using the holding location's `Transform.Position`.
5. Leave `ParticleSystems` and `Shell` empty for now; fill `Notes` with the resolved FX key names.

- [ ] **Step 5: Verify against Sireth**

Run the backend alone on a fixed port:

```bat
set ASPNETCORE_URLS=http://127.0.0.1:5199
dotnet run --project src\DdoDatApi\DdoDatApi.csproj
```

```bat
curl -s "http://127.0.0.1:5199/WeaponEffect/plan?db=0x7901B9CD"
```

Expected: `ImbueAlignment` = `Good`, `ImbueType` = `Invalid`, `ImbueSources` contains
`Supreme Good`, and **exactly 6 spawns** — `Target_Weapon_Tip`, `Mid1`, `Mid2`, `Mid3`, `Mid4`, `Mid5`
— each with `ParticleSystem` `0x39000243`. `Mid6`, `Mid7` and `ArrowTip` must be absent. `Tip`'s
position must be about `[0.043, 1.668, 0]`. `Streak.Color` must be `99C7F3FC`.

---

### Task 4: Backend — fill in particle systems from PSDescription

**Files:**
- Modify: `src/DdoDatApi/Controllers/WeaponEffectController.cs`

**Interfaces:**
- Consumes: `VoK.Sdk.Common.PSDescription`, `ParticleEmitterDesc`, `ParticleKeyframeDesc`, `Waveform`.
- Produces: populated `WeaponEffectPlan.ParticleSystems`.

- [ ] **Step 1: Map Waveform to WaveformDto**

```csharp
static WaveformDto Dto(Waveform w) => new()
{
    Type = w.Type.ToString(),
    Base = w.Base, BaseVelocity = w.BaseVelocity,
    Amplitude = w.Amplitude, AmplitudeVelocity = w.AmplitudeVelocity,
    Phase = w.Phase, PhaseVelocity = w.PhaseVelocity,
    Frequency = w.Frequency, FrequencyVelocity = w.FrequencyVelocity,
    KeyframeDuration = w.KeyframeDuration, KeyframeLoops = w.KeyframeLoops,
    Keyframes = (w.Keyframes ?? new()).Select(k => new[] { k.Percent, k.Value }).ToList(),
};
```

- [ ] **Step 2: Load each distinct particle id and its texture**

For every distinct `ParticleScript_ParticleID` in the spawns: `PSDescription.Load(DatSource.GeneralDat, id)`,
then resolve the sprite sheet: `PSDescription.MaterialDid` → `MaterialInstance.Load` →
each `Modifiers` entry → `MaterialModifier.Load` → `DiffuseMap.TextureDid` →
`RenderTexture.Load` → `SurfaceDids[0]` (the first surface is the top mip). Set
`TextureUrl = $"Image/0x{surface:X8}"`, which the existing `ImageController` already serves as PNG.

- [ ] **Step 3: Copy the emitter fields into the DTO**

Straight field copy per the spec's PEmitterDesc list, including `Keys` from `ParticleKeyframeDesc`
(`Time`, `ScaleX`, `ScaleY`, `Color` as `$"{key.Color:X8}"`). Skip emitters whose `Active` is false.

- [ ] **Step 4: Verify**

```bat
curl -s "http://127.0.0.1:5199/WeaponEffect/plan?db=0x7901B9CD"
```

Expected for `0x39000243`: 2 emitters; `MaxParticles` 10; `Lifespan` a `None` waveform with base 0.8;
`BirthRate` 0.125 and 0.5; `UFrames` 2, `VFrames` 4, `FramesPerSec` 16; 4 keys per emitter starting at
color `4B6FB7E2` and ending at `006FB7E2`; `TextureUrl` `Image/0x41007BBB`. Open that URL in a browser
and confirm you get the 256x512 sheet of pale flame wisps.

---

### Task 5: Backend — fill in the aura shell

**Files:**
- Modify: `src/DdoDatApi/Controllers/WeaponEffectController.cs`

- [ ] **Step 1: Find the shell entity and its aura key**

From the weapon's class script table, `FindScript(..., FX 0x1B)` and collect `MeshFX` nodes; take
`MeshFXScript_Entity` (the shell EntityDescription) and `MeshFXScript_Behavior` (the FX key to run on
it — `Weapon_Imbue_Effect`). Then load the shell entity's own `Entity_ClassScriptTable`,
`FindScript(shellTable, behaviour)` and `Collect` with the **weapon's** imbue state (the shell script
reads its parent's properties). Keep the `Appearance` node: it gives `AppearanceScript_Appearance` and
`AppearanceScript_Key`.

The gate scriptlet (`0x0C0000FB`) is deliberately not evaluated here: DDO Studio always previews an
"equipped, drawn, not stowed" weapon, which is exactly the branch that emits `weapon_mesh_fx`. Record
that assumption in `Notes`.

- [ ] **Step 2: Resolve the key into renderable material values**

`AppearanceTable.Load(DatSource.GeneralDat, appearanceId)` → `Modifications[key]` → `Parts[0]` →
`MaterialMods[0]` → `ModifierDid` → `MaterialModifier.Load`. From its `MaterialProperties`:

- `DiffuseColor` → `Tint` = RGB, `Opacity` = `ColorAlpha`
- `DiffuseMap` → `TextureDid` → `RenderTexture` → `SurfaceDids[0]` → `DiffuseTextureUrl`
- `UTranslate` / `VTranslate` → `UvScroll`. Both are waveforms; for v1 reduce each to a scalar rate
  using its `Base` (Good is `UTranslate` Sine base 0.3, `VTranslate` Speed base 0.3), and note the
  simplification in `Notes`.

Also resolve the shell's `Setup` for Task 7: shell entity → `VisualDesc` (`0x1F002061`) → its
`SetupInfos[0].SetupDid`.

- [ ] **Step 3: Verify**

Expected for Sireth: `Shell.AppearanceKey` = `Weaponaura_Good`, `AppearanceId` = `0x20000004`,
`EntityId` = `0x470036FF`, `SetupId` = `0x040020CC`, `Tint` ≈ `[0.435, 0.718, 0.886]`,
`Opacity` = 0.6. Sanity-check a second item: any Flaming weapon must come back `Weaponaura_Fire`
with an orange tint.

---

### Task 6: Viewer — the particle simulator

**Files:**
- Create: `src/DDOAssetStudio/viewer/vfx-particles.js`
- Modify: `src/DDOAssetStudio/viewer/index.html` (script tag; `setVisualEffects` routing)

**Interfaces:**
- Consumes: the plan JSON from Task 4.
- Produces: `window.ddoVfx = { createSystems(plan, parent), update(dt), dispose() }`

- [ ] **Step 1: Waveform evaluator**

```js
function evalWaveform(w, t, rng) {
  if (!w) return 0;
  const base = w.base + w.baseVelocity * t;
  const amp = w.amplitude + w.amplitudeVelocity * t;
  const freq = w.frequency + w.frequencyVelocity * t;
  const phase = w.phase * Math.PI + w.phaseVelocity * t;
  switch (w.type) {
    case 'None':     return w.base;
    case 'Speed':    return base + t * freq + phase;
    case 'Noise':    return base + amp * ((rng ? rng() : Math.random()) * 2 - 1);
    case 'Sine':     return base + amp * Math.sin(t * freq + phase);
    case 'Square':   return base + (Math.sin(t * freq + phase) < 0 ? -amp : amp);
    case 'Bounce':   return base + amp * Math.abs(Math.sin(t * freq + phase));
    case 'Perlin':
    case 'Fractal':  return base + amp * (Math.sin(t * freq + phase) * 0.6); // approximation
    case 'Keyframe': return evalKeyframes(w, t);
    default:         return base;
  }
}

function evalKeyframes(w, t) {
  const keys = w.keyframes || [];
  if (!keys.length) return w.base;
  const dur = w.keyframeDuration > 0 ? w.keyframeDuration : 1;
  let pct = (w.keyframeLoops ? (t % dur) : Math.min(t, dur)) / dur * 100;
  if (pct <= keys[0][0]) return keys[0][1];
  for (let i = 1; i < keys.length; i++) {
    if (pct <= keys[i][0]) {
      const [p0, v0] = keys[i - 1], [p1, v1] = keys[i];
      const f = p1 === p0 ? 0 : (pct - p0) / (p1 - p0);
      return v0 + (v1 - v0) * f;
    }
  }
  return keys[keys.length - 1][1];
}
```

- [ ] **Step 2: Sprite pool per emitter**

For each spawn × emitter, build a pool of `MaxParticles` `THREE.Sprite`s, each with its own
`SpriteMaterial` and its own `texture.clone()` so frames can differ per particle:

```js
function makeParticlePool(texture, emitter, parent) {
  const pool = [];
  for (let i = 0; i < Math.max(1, emitter.maxParticles); i++) {
    const tex = texture.clone();
    tex.needsUpdate = true;
    tex.repeat.set(1 / Math.max(1, emitter.uFrames), 1 / Math.max(1, emitter.vFrames));
    const mat = new THREE.SpriteMaterial({ map: tex, transparent: true, depthWrite: false,
                                           blending: THREE.AdditiveBlending, color: 0xffffff });
    if ('toneMapped' in mat) mat.toneMapped = false;
    const spr = new THREE.Sprite(mat);
    spr.visible = false;
    spr.userData.ddoVfx = true;
    parent.add(spr);
    pool.push({ sprite: spr, tex, mat, alive: false, age: 0, life: 1, vel: new THREE.Vector3(),
                frame0: 0, size: 1, rot: 0, rotVel: 0 });
  }
  return pool;
}
```

- [ ] **Step 3: Spawn and update**

Convert anchor and direction from DDO Z-up with `(x, y, z) → (x, z, -y)`. Per emitter keep a spawn
accumulator; `BirthRate` is treated as seconds per particle (flagged as an assumption in the spec):

```js
function spawn(p, sys, emitter, anchorPos, time) {
  const dirRaw = new THREE.Vector3(evalWaveform(emitter.direction[0], time),
                                   evalWaveform(emitter.direction[1], time),
                                   evalWaveform(emitter.direction[2], time));
  const dir = ddoToViewer(dirRaw).normalize();
  const minS = evalWaveform(emitter.minSpread, time), maxS = evalWaveform(emitter.maxSpread, time);
  const spread = THREE.MathUtils.degToRad(minS + Math.random() * Math.max(0, maxS - minS));
  const axis = new THREE.Vector3(Math.random() - .5, Math.random() - .5, Math.random() - .5).normalize();
  dir.applyAxisAngle(axis, spread);
  const radius = Math.max(0, evalWaveform(emitter.scale, time));
  p.sprite.position.copy(anchorPos).add(new THREE.Vector3(
      (Math.random() - .5) * radius, (Math.random() - .5) * radius, (Math.random() - .5) * radius));
  p.vel.copy(dir).multiplyScalar(evalWaveform(emitter.velocity, time));
  p.life = Math.max(.05, evalWaveform(emitter.lifespan, time));
  p.size = Math.max(.001, evalWaveform(emitter.particleScale, time));
  p.rot = evalWaveform(emitter.rotation[2], time) * Math.PI / 180;
  p.rotVel = evalWaveform(emitter.rotationVelocity[2], time) * Math.PI / 180;
  p.frame0 = emitter.randomizeStartFrame ? Math.floor(Math.random() * emitter.uFrames * emitter.vFrames) : 0;
  p.age = 0; p.alive = true; p.sprite.visible = true;
}
```

Per frame, for each live particle: advance `age`, integrate position by `vel * dt`, interpolate the
keyframe list by age in seconds for color/alpha/scaleX/scaleY, set `mat.color`, `mat.opacity`,
`mat.rotation`, `sprite.scale`, and set the sheet frame:

```js
const frames = Math.max(1, emitter.uFrames * emitter.vFrames);
const f = (p.frame0 + Math.floor(p.age * emitter.framesPerSec)) % frames;
p.tex.offset.set((f % emitter.uFrames) / emitter.uFrames,
                 1 - (Math.floor(f / emitter.uFrames) + 1) / emitter.vFrames);
```

Retire particles at `age >= life`.

- [ ] **Step 4: Wire into the viewer**

In `index.html`: add `<script src="vfx-particles.js"></script>` after `GLTFLoader.js`. In
`setVisualEffects(data)`, when `data && data.spawns` is present, call `window.ddoVfx.createSystems(data, effectRoot)`
and skip `populateVisualEffects` entirely; otherwise keep today's heuristic path unchanged. Call
`window.ddoVfx.update(dt)` from `animateVisualEffects(dt)`, and `dispose()` from `clearVisualEffects()`.

- [ ] **Step 5: Verify on screen**

Run `build-preview-and-run.cmd`, select Sireth. Expected: small pale blue-white wisps rising from six
points along the staff — a cluster at the tip and five down the shaft, none past the ends. They should
brighten to white mid-life then fade to nothing, each sprite visibly cycling through the flame frames,
about 8–16 alive at a time. Rotate the camera: sprites must stay billboarded and stay attached to the
staff when you change Position/Rotation in the viewer controls.

---

### Task 7: Viewer — the aura shell layer

**Files:**
- Modify: `src/DDOAssetStudio/Program.cs` (export the shell GLB, put its url in the plan)
- Modify: `src/DDOAssetStudio/viewer/vfx-particles.js` (load and skin the shell)

- [ ] **Step 1: Export the shell mesh to GLB**

The exporter already takes a Setup id, so reuse it exactly as the preview path does, with the shell's
`Shell.SetupId`:

```
DDOGlbExporter.exe 0x040020CC <cache>\shell-0x040020CC.glb
```

Cache it beside the existing preview GLBs in LocalAppData and set `Shell.GlbUrl` on the plan before
handing it to the viewer.

- [ ] **Step 2: Render the shell**

In `vfx-particles.js`, when `plan.shell?.glbUrl` is set, load it with the existing `THREE.GLTFLoader`,
add it under the same parent as the particles, and replace every material with an additive, tinted,
scrolling one:

```js
const mat = new THREE.MeshBasicMaterial({
  map: shellTexture, color: new THREE.Color(tint[0], tint[1], tint[2]),
  transparent: true, opacity: shell.opacity, depthWrite: false,
  blending: THREE.AdditiveBlending, side: THREE.DoubleSide });
if ('toneMapped' in mat) mat.toneMapped = false;
```

Each frame, advance `shellTexture.offset` by `uvScroll[0] * dt` and `uvScroll[1] * dt` (wrap with
`THREE.RepeatWrapping`). Scale the shell up by a hair (about 1.02) to avoid z-fighting with the base
mesh, and note that the client instead relies on the shell being its own slightly different mesh.

- [ ] **Step 3: Verify on screen**

Select Sireth again. Expected: the staff now carries a soft pale-blue translucent skin over the metal,
with the texture visibly drifting along the weapon, plus the particles from Task 6. Toggle wireframe to
confirm the shell is a separate mesh and that the base staff is unchanged underneath.

---

### Task 8: Wire the plan into the shell app and document it

**Files:**
- Modify: `src/DDOAssetStudio/Program.cs:2687-2790` (`ResolveVisualEffectsAsync`)
- Modify: `VFX_RENDERING_NOTES.md`, `docs/TECHNICAL_ARCHITECTURE.md` (section 9)

- [ ] **Step 1: Request the plan first, fall back second**

In `ResolveVisualEffectsAsync`, before the existing `VisualEffect/resolve` call, request
`WeaponEffect/plan?db=0x{row.DbId:X8}` when `row.DbId != 0`. If it returns a plan with at least one
spawn or a shell, add the shell GLB url (Task 7) and pass that object to
`window.ddoViewer.setVisualEffects`. Otherwise fall through to today's heuristic payload unchanged.
Keep the existing `generation != previewGeneration` guards and the dressing-room slot variant.

- [ ] **Step 2: Surface it in the details pane**

The details text already has a `VISUAL EFFECTS` section. When a plan was used, print the resolved
imbue type/alignment, the aura key, the particle ids, and the anchor names, so a wrong resolution is
visible without opening a debugger.

- [ ] **Step 3: Update the docs**

`VFX_RENDERING_NOTES.md` currently describes the conservative texture/mask reconstruction. Add the
data-driven path: imbue properties select an aura `AppearanceKey` plus PSDescription particle systems;
anchors come from Setup holding locations; missing anchors emit nothing. Point at
`C:\dev\ddonexus\weapon-visual-effects.md` for the formats. In `docs/TECHNICAL_ARCHITECTURE.md`
section 9, replace the "resolved independently from the base mesh" description with the two-layer model.

- [ ] **Step 4: Verify end to end**

Run `build-preview-and-run.cmd`. Check, in order:
1. Sireth — shell + 6 emitters, details pane shows `Good` / `Weaponaura_Good` / `0x39000243`.
2. A plain non-magical weapon — no shell, no particles, no errors in the WebView console.
3. A `*VFX` weapon (Imbue_Type set) — shell resolves against `0x20000D45` with its own key.
4. An armor or other non-weapon asset — the legacy heuristic path still runs as before.
5. Load Sireth, then another model, then Sireth again — no leaked sprites, no growing memory
   (`clearVisualEffects` must dispose pools and cloned textures).

---

## Notes and risks

- **`BirthRate` units are an assumption.** If particle density looks wrong by a large factor, invert the
  interpretation (particles per second rather than seconds per particle) before touching anything else.
- **Per-sprite texture clones** are the simple route to per-particle sheet frames. 6 anchors × 2 emitters
  × 10 particles is 120 sprites, which r128 handles comfortably. If a later effect needs hundreds,
  replace the pool with one instanced `THREE.Points` and a shader that takes frame index per instance.
- **Perlin and Fractal waveforms are approximated** with a sine. Nothing in the weapon-imbue path uses
  them; if a future effect does, port the client's six-octave formula from the spec.
- **The `_sparse` variant** (unequipped weapons) is out of scope; DDO Studio always previews the
  equipped-and-drawn branch.
- **The swing trail** (`Script_OverrideStreakColor` / `Script_OverrideStreakMaterial`) is carried in the
  plan but not rendered. It needs the `Streak`/`StreakInfo` node mechanism, which has not been reversed.
