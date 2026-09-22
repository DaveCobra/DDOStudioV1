# DDO Studio 1.7.2 - Technical Architecture

## 1. Process layout

DDO Studio is split into three local components:

1. **DDOAssetStudio** - WinForms desktop shell and WebView2 host.
2. **DdoDatApi** - loopback ASP.NET Core backend that reads and interprets the local DDO DAT files.
3. **DDOGlbExporter** - command-line exporter that writes the resolved model to glTF 2.0 / GLB.

The desktop chooses an available loopback port at startup and launches the backend on `127.0.0.1`. No remote service is required for asset access.

## 2. Search/index boundary

The backend builds a local schema-versioned index from GameLogic records. The model-name index is intentionally narrower than the complete DAT catalog: it contains named records intended for direct model browsing. Standalone weapons and shields remain searchable because they render correctly on their own. Worn/composed equipment classes are not published into the model-name index. Other metadata can remain available to internal resolvers without being exposed through the model browser.

Schema version 7 rebuilds older local indexes so the current model-library boundary is applied consistently. The public name index includes non-equippable renderable records plus standalone Weapon and Shield records; worn/composed equipment classes remain internal-only.

## 3. Model resolution

A selected row is resolved through typed DDO relationships rather than by treating arbitrary 32-bit values as model IDs. The primary path is:

```text
DbProperties
  -> PhysObj
  -> VisualDescription
  -> Setup
  -> RenderMesh
  -> MaterialInstance / RenderSurface / RenderTexture
```

Direct relationships take precedence over unrelated nodes discovered deeper in a graph. Records that do not resolve to a nonzero Setup are not displayed as renderable models.

## 4. Geometry and coordinate conversion

DDO geometry is reconstructed from RenderMesh vertex/index data. DDO uses a Z-up coordinate system while glTF uses Y-up, so exported scene roots apply a -90 degree X-axis conversion. The skeleton and skinned meshes remain under the same conversion root so skinning relationships are preserved.

The GLB exporter writes positions, normals, UVs, indices, skin joints/weights, inverse-bind matrices, materials, embedded textures, and animation tracks when validated.

## 5. Materials and textures

MaterialInstance data is resolved into material properties and texture/surface chains. Known diffuse/base-color and normal-map roles are emitted into standard glTF material slots. Additional discovered texture references remain available as diagnostic metadata when their semantic role is not yet proven.

Texture decoding includes the DXT formats used by the game data. Material opacity remains conservative unless the source semantics justify transparency.

## 6. Appearance composition

Character/object appearance data can reference typed Appearance tables that contain mesh replacement, material modification, or Setup replacement operations. The resolver parses these operations into a deterministic composition plan before GLB generation so the preview and manual export use the same result.

Composition is data-driven by Appearance keys, mesh-type IDs, material-type IDs, selector values, and direct relationship evidence. The exporter applies the plan before the final scene is written.

## 7. Animation discovery and decoding

Animation records live in the Anim DAT and use older Havok animation structures. DDO Studio separates three questions:

- does an animation record decode?
- does its transform-track count match the model skeleton?
- does the available family/binding evidence support playback on this rig?

Supported decoded paths include spline-compressed and interleaved uncompressed transform animations. Candidate clips that fail compatibility or family checks are kept out of the model's playable animation list.

The preferred animation is chosen only from the current model's validated compatible list. Weapon records (`WeenieType 0x00020081`) prefer `0x05005943` and force looping when available, then fall back to compatible `0x05000440`. Other models prefer compatible `0x05000440`. Curated built-in labels come from `src/DDOAssetStudio/viewer/animation-names.json`; LocalAppData aliases can override those names without changing the shipped file.

## 8. Viewer transform model

The Three.js viewer uses separate scene roots for the loaded model and the pedestal. The model root owns user Position X/Y/Z and Rotation X/Y/Z values. Most models default to zero on all six axes. Weapon records (`WeenieType 0x00020081`) default to Position Y `1.40x` and Rotation X `90°`, with all other position/rotation axes at zero. Reset Model Transform restores the defaults for the current model classification. The pedestal has its own independent height and scale controls, and model Position Y never moves the pedestal.

Inspection overlays and the SkeletonHelper follow the model root. Camera framing is recalculated from the model bounds after load.

## 9. Visual effects

Weapon imbue/alignment effects are resolved from the game's own effect chain by `WeaponEffect/resolve`, not inferred from texture references. An item's `Effect_OnCreationEffects` are applied to find its live `Weapon_Imbue_Type` / `Weapon_Imbue_Alignment`; the weapon entity's class script tables are then walked, following only the Switch branches those values select. That yields two layers:

- **Aura shell** - the `MeshFX` node's entity, whose own script table applies an `AppearanceKey.Weaponaura_*` key into an Appearance table. The key's material modifier supplies the tint, opacity, diffuse texture and UV scroll rates, and the shell mesh is exported to GLB like any other Setup.
- **Particles** - the `Weapon_Imbue_Powerup` script's `Particle` nodes, each carrying a PSDescription id and a holding location. One particle system is emitted per holding location **the weapon's Setup actually defines**; the client skips the rest, and so does the resolver. No weapon defines all nine weapon points.

PSDescription records decode through VoK.Sdk into emitters and per-particle keyframes (Waveform-driven velocity, lifespan, scale and rotation; keyframed colour and size; sprite-sheet frame counts). The viewer's `vfx-particles.js` evaluates those Waveforms per frame and drives pooled additive sprites, converting DDO's Z-up positions and directions with `(x, y, z) -> (x, z, -y)` to match the exporter's scene root.

Assets with no resolved effects fall back to the older heuristic resolver (`VisualEffect/resolve`), which discovers particle descriptions, texture/mask branches, scene-level modifiers and auxiliary geometry, favors evidence-backed texture/mask reconstruction, and treats ambiguous auxiliary geometry conservatively.

All resolution lives in VoK.Sdk (`WeaponEffectResolver`, `ScriptGraph`, `PSDescription`, `Waveform`) rather than in this backend, so the same chain is available to any SDK consumer; the controller only turns surface ids into `Image/` URLs and enum values into names. The reverse-engineered formats behind it - the script chain, the aura key tables, and the PSDescription/Waveform binary layouts - are documented in `weapon-visual-effects.md` in the ddonexus repository.

## 10. Local storage and privacy

Generated state is kept under `%LOCALAPPDATA%\DDO Asset Studio`, including:

- backend index/cache
- preview GLBs
- diagnostics
- curated built-in animation names and user animation-alias overrides

The source and release assembly scripts do not package those directories. Release output removes PDB files to avoid leaking local source/build paths.
