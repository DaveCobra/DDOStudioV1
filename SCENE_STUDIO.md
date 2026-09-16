# Scene Studio - DDO Studio 1.7.2

The embedded viewer is designed around a neutral scene that can be customized locally without bundling environment artwork.

## Environment

Users can load:

- standard equirectangular sky images
- HDR equirectangular environments
- six-face cubemaps

Environment files stay on the user's machine and are not copied into the source or release package.

## Model transform

The loaded model has independent controls for:

- Position X
- Position Y
- Position Z
- Rotation X
- Rotation Y
- Rotation Z

The pedestal is a separate scene object with its own height and scale. Model Position Y does not move the pedestal. Weapon records (`WeenieType 0x00020081`) load at Position Y `1.40x`, Rotate X `90°`, with Position X/Z and Rotation Y/Z at zero. Reset Model Transform returns the current model to its class defaults.

## Inspection

The viewer can toggle skeleton, vertex, normal, and wireframe overlays. Camera framing is based on the loaded model's bounds.

## Animation

Only validated compatible animations are offered in the per-model animation panel. The global animation browser remains available for inspection of the Anim DAT catalog. Weapon models prefer compatible `0x05005943` and automatically loop it; if unavailable they fall back to compatible `0x05000440`. Other models prefer compatible `0x05000440`. Curated built-in labels are loaded from `viewer/animation-names.json`, with LocalAppData aliases acting as user overrides.
