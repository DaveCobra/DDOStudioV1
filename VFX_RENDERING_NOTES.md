# DDO Studio 1.7.2 - Visual Effect Reconstruction

DDO visual effects can be distributed across particle descriptions, texture/surface branches, appearance data, and scene-level modifiers rather than living inside a single RenderMesh.

The backend therefore returns effect evidence separately from the base model. The viewer can use texture/mask information to build additive sprites or glows while keeping ambiguous auxiliary geometry diagnostic-only.

## Weapon imbue effects are data-driven, not inferred

Weapon imbue/alignment auras no longer go through evidence reconstruction. `WeaponEffect/resolve`
walks the real chain and the viewer renders exactly what the data says:

1. An item's `Effect_OnCreationEffects` are applied to find its live `Weapon_Imbue_Type` and
   `Weapon_Imbue_Alignment`. Unimbued weapons resolve to nothing, which is the correct answer.
2. The weapon entity's class script tables are walked, taking only the Switch branches those two
   property values select.
3. The `MeshFX` branch gives an aura shell entity whose script applies an `AppearanceKey.Weaponaura_*`
   key. That key's material modifier overrides a RenderMaterial template, and the merged result carries
   the tint, opacity, depth flags and both animated texture layers.
4. The `Weapon_Imbue_Powerup` branch gives `Particle` nodes, each with a PSDescription id and a
   holding location.

**Two layers, and the base tint.** The aura material blends two independently transformed copies of its
texture (`DiffuseMap`/`DiffuseMap2`), each with its own `UTranslate`/`VTranslate`/`UScale`/`VScale`/
`UVRotate` waveform. A single draw samples *both* layers - and the client then issues that draw twice
per frame, which is where most of the effect's brightness comes from (see below).
Separately, an imbued weapon usually carries `Render_Color`, which the client uses as the base mesh's
`c_MaterialDiffuseColor` - Sireth's `0xFF4C99FF` is why its metal reads blue in game (confirmed in the
capture; see below). The viewer applies that tint to the loaded model and restores the original colours
when effects are cleared.

**Anchors matter.** Particles are emitted only at holding locations the weapon's own Setup defines.
The shared script fires nine weapon points (Tip, Mid1-Mid7, ArrowTip) but no weapon defines all nine;
the client resolves the point first and creates nothing when it is missing, so the resolver drops
those nodes too. A quarterstaff typically anchors six of the nine.

Resolution lives in VoK.Sdk 5.0 (`WeaponEffectResolver`, `ScriptGraph`, `PSDescription`, `Waveform`),
not in this backend; the controller is a thin adapter that maps surface ids to `Image/` URLs and enum
values to names.

`viewer/vfx-particles.js` evaluates the Waveforms per frame and drives pooled additive sprites,
converting DDO Z-up data with `(x, y, z) -> (x, z, -y)` to match the exporter's scene root. The aura is
one mesh with a two-layer shader, each layer sampled through its own UV transform matrix, added to the
scene twice so it renders in two additive passes like the client's.

**The viewer works in display space, end to end, because the client does.** The captured shaders
contain no gamma conversion anywhere: the client samples a texture's stored bytes, multiplies by its
light constants and writes the result. three.js normally decodes colour textures to linear, lights
there, and re-encodes on output. Both pipelines are self-consistent and they do not produce the same
picture - for total light `L`, the client gives `t*L` where three gives `t*L^(1/2.2)`, which is about
1.6x too bright in shadow, 0.7x too dark in light, and never blows out to white where the game does.
Sireth's blade is blown out in game and was not here.

So `outputEncoding` is `LinearEncoding`, effect and particle textures are left undecoded, and
`applyDisplaySpaceTextures()` undoes the `sRGBEncoding` that GLTFLoader forces onto `map` and
`emissiveMap` after every model load. The aura's raw `ShaderMaterial` already received none of three's
decode/encode injection, so it needed no change - it was the only thing in the viewer that was already
right.

The aura shader is a **direct port of the client's own**, disassembled out of RenderMaterial
`0x2B000325` (a RenderMaterial record embeds its compiled D3D9 and DXBC shaders; `fxc /dumpbin` reads
them):

```
colour.rgb = (Texture_Color(uv0) + Texture_Color2(uv1)) * c_MaterialDiffuseColor.rgb
colour.a   = c_MaterialDiffuseColor.a          // a constant, 0.6 for Weaponaura_Good
```

The render state around it comes from a **RenderDoc capture of the client**, not from inference. The
draw using this shader - identified by SHA1 against the bytecode extracted from the dat, so there is no
doubt which draw it is - blends `SrcBlend=SRC_ALPHA, DestBlend=ONE, BlendOp=ADD` with alpha
`ONE/ZERO/ADD`. That is exactly three's `AdditiveBlending`.

**And the mesh is drawn twice.** The capture shows two draws 21 chunks apart with identical state -
same vertex and pixel shader, same texture view, same depth and raster state, same constant buffer.
The material carries two layers and each gets its own draw. This is the single largest contributor to
the effect's brightness and it cannot be faked by scaling the shader's output, because each pass clamps
to 1 before blending: two 0.6-weighted passes reach ~1.2x where one 2x pass reaches 1.0x. Two passes
predict highlights of rgb(238,255,255) against the game's measured rgb(220,253,254).

The capture also confirms the resolved constants exactly - `c_MaterialDiffuseColor` is
`[0.4353, 0.7176, 0.8863, 0.6]`, matching what the resolver computes from the Appearance chain.

Before the capture, this file documented `One/One` on the strength of a table of colours measured off
an in-game screenshot. That reasoning was sound and the answer was wrong: screenshot-fitting cannot see
a second draw, so it attributed the missing brightness to the blend. Fit colours to *choose between*
candidates, never to conclude - capture the frame.

The particle shader is `tex.rgb * vertexColour.rgb` with `alpha = vertexColour.a` (the texture's own
alpha is ignored), and its six draws in the capture use the same additive blend. There is nothing to
tune about either shader: earlier builds guessed at density masks, luminance alphas, hot thresholds,
gain and multiply-vs-add, and all of it was wrong.

The effect targets a **material slot**, not the whole model: the appearance mod's `MaterialType`
selects `MaterialInstance.MaterialTypeId`, and for weapons that is `0x1000005C weapon_effect`. Sireth's
shell mesh `0x0600338A` carries exactly that slot (and already ships with the aura RenderMaterial),
while the base staff mesh `0x06003398` is type 2 and is never touched.

Still inferred: **`UVRotate` is degrees per second** (radians would blur rather than drift), and
**Perlin and Fractal waveforms** are approximated with a sine (nothing in the imbue path uses them).

## The base mesh: `Render_Color`, MODULATE2X, and no tone mapping

`Render_Color` is **confirmed**, not inferred. Searching every constant buffer in the capture for its
literal float triple - `0xFF4C99FF` is (0.298, 0.600, 1.000) - finds it live in exactly two draws, both
2238 indices with the same textures: the base staff mesh, drawn once per pass. `fxc /dumpbin` on those
shaders names the slot it occupies: `c_MaterialDiffuseColor`. It is also `c_MaterialSpecularColor` in
the base pass, and it arrives **pre-multiplied into every light constant**
(`c_Light0_DiffuseColorTimesMatColor`, `c_MaterialDiffuseTimesAmbientLight`), so the tint scales ambient,
diffuse and specular alike rather than being a flat multiply over the final colour.

That pre-multiplication is also a free self-check on any value read this way:
`c_Light0_DiffuseColorAndSquaredRange` appears raw *and* pre-multiplied in the same buffer, and dividing
one by the other returns the tint exactly.

**The client's mesh shading is MODULATE2X.** Both base-mesh shaders end the same way:

```
add  r1.xyz, r4.xyzx, r1.xyzx              // ambient*matColor + sum of lights*NdotL*matColor
mul  r0.xyz, r0.xyzx, r1.xyzx              // * texture
mad  r0.xyz, r0.xyzx, l(2,2,2), r2.xyzx    // *2, then + specular
```

The diffuse term is **doubled**. three.js has no such factor, so for years' worth of light-slider
twiddling the viewer was rendering the weapon at half the client's brightness for identical lights.

**And the client has no tone mapping at all.** The captured frame contains zero non-indexed draws -
no fullscreen pass exists to tone map with - and the mesh shaders write final colour directly after a
fog lerp. The viewer was applying `ACESFilmicToneMapping`, a filmic S-curve that compresses highlights
and desaturates hard as it approaches white. That is what turned the aura into "flat dark cyan" no
matter which blend it used, and it is why raising ambient and exposure always helped a little and never
enough: ACES re-compressed whatever it was given. The viewer now uses `LinearToneMapping`, which is
`saturate(exposure * colour)` - the client's clamp, with the exposure slider still meaningful at a
default of 1.0.

The viewer's lights are now the capture's own values, doubled for MODULATE2X: ambient
(0.154, 0.190, 0.202), dominant point light (0.759, 0.707, 0.656), a second dim warm light
(0.102, 0.071, 0.039). The hemispheric ambient constant was ~0, so the studio hemisphere light is flat.
These are one tavern's lights rather than a universal answer, but they are measured, and the sliders
remain for inspection.

**Do not nudge the aura shell's transform.** An earlier build scaled it by 1.01 "so the surfaces do not
z-fight". Scaling happens about the model *origin*, not the mesh centre, so every vertex slides outward
in proportion to its distance from that origin - on a staff, whose origin sits at the handle, that walks
the entire shell up the shaft and leaves base geometry poking through the bottom of each blade. The
client applies no such nudge; `DepthWrite=false` on the aura material is what keeps the two surfaces
from fighting, and the shell is separate geometry in the first place.

## Comparing against a screenshot

A player's own graphics settings change the reference. `AmbientLightBoost` in `UserPreferences.ini`
maps to the client cvar `Render.AmbientLightBoost` ("sets the base level of ambient light to use"), so a
player running 2.00 is lit with double the default ambient, and `Gamma` shifts it further. The viewer's
defaults now sit nearer that: Ambient 1.8, Exposure 1.12, plus a generated studio environment so PBR
materials get image-based light without importing a sky.

Do not confuse that preference with `AmbientLightBoostEntityVFX`, a same-named `EntityVFX` type that
adds an RGB offset to one entity's ambient and clamps to 0-1. It appears on the newer `*VFX` imbue
branches.

If translucent at a sane glow still does not match, the remaining suspect is geometry rather than
shading - whether the shell mesh covers the blade as fully in game as the exported Setup suggests.

The formats and the client functions they were derived from are documented in
`weapon-visual-effects.md` in the ddonexus repository.

## Texture and mask handling

Grayscale or low-saturation textures can represent alpha/luminance masks rather than visible color. The viewer analyzes loaded effect textures and can combine a colored source with a separate mask using additive blending. This avoids treating a mask as an ordinary opaque square.

## Anchoring

Effect placement is derived from the loaded model's local geometry bounds and effect metadata. Model-space anchors are transformed with the model root, so user position/rotation changes keep the effect aligned with the model.

## Conservative reconstruction

Effect graphs can contain unrelated decorative or shared resources. DDO Studio does not assume every referenced mesh belongs in the final effect. Proven base geometry stays in the primary GLB while effect-only evidence is resolved separately.
