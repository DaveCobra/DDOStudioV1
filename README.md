# DDO Studio 1.7.2

DDO Studio is a local Windows model viewer and GLB exporter for Dungeons & Dragons Online data. It reads the user's own installed DAT files, resolves renderable model chains, reconstructs materials and textures, exposes validated skeleton/animation data, and previews the result in an embedded Three.js viewer.

## Current application

The desktop application centers on one searchable model library and one 3D viewer. Name search and **View All** operate on a renderable model index. Standalone weapons and shields are included in normal search because they render correctly as independent models. Worn/composed equipment classes such as armor, clothing, and jewelry are kept out of the public model browser when they are not reliable standalone assets.

Selecting a row resolves the model's `DbProperties -> PhysObj -> VisualDescription -> Setup -> RenderMesh` chain and builds a preview GLB from local game data.

The viewer supports:

- Position X / Y / Z
- Rotation X / Y / Z
- independent pedestal height and scale
- skeleton, vertices, normals, and wireframe inspection
- compatible animation browsing and playback
- class-based viewer defaults: Weapon records use Position Y `1.40x`, Rotate X `90°`, with the other transform axes at zero
- Weapon previews prefer looping animation `0x05005943` when it is compatible; other models prefer `0x05000440`, and Weapons fall back to `0x05000440` when the spinning clip is unavailable
- neutral scene lighting over a generated studio environment, plus optional user-selected equirectangular sky/HDR or six-face cubemap imagery
- GLB export using the same model/material/appearance pipeline as preview

Model transforms and pedestal transforms are separate scene objects. Moving or rotating a model does not move the pedestal.

## Data pipeline

DDO Studio does not redistribute DDO DAT content. At runtime it reads the user's local installation and resolves typed relationships through the bundled loopback backend. The exporter writes glTF 2.0 / GLB with geometry, materials, textures, skinning data, and validated animation clips when available.

The local model-search index contains named records intended for direct model browsing, including standalone weapon/shield records. Internal metadata needed by render, appearance, animation, and effect-resolution code stays separate from that public name index.

## Privacy and local data

DDO Studio has no telemetry or account integration. The application backend listens only on a dynamically selected loopback address (`127.0.0.1`). Generated caches, diagnostics, preview GLBs, and user animation aliases are stored under the current Windows user's LocalAppData folder and are not part of the source package.

Release builds delete PDB debug symbols because they can embed local build paths. The distributed source and update packages are assembled without local logs, preview caches, user aliases, machine-specific paths, or personal identifiers.

## Animation aliases

User-renamed animation labels are stored at:

```text
%LOCALAPPDATA%\DDO Asset Studio\animation-aliases.json
```

DDO Studio 1.7.2 also ships a curated built-in name map at `src/DDOAssetStudio/viewer/animation-names.json`. The LocalAppData file is a user override layer: custom names replace built-in labels for matching IDs without modifying the shipped source data.

## Build from source

Requirements:

- Windows x64
- .NET 10 SDK
- Dungeons & Dragons Online installed locally for runtime use
- Internet access during the first release build so the script can stage the Three.js runtime files and WebView2 standalone installer

For a development preview:

```bat
build-preview-and-run.cmd
```

For the self-contained end-user release and installer:

```bat
build-release.cmd
```

The installer is written to:

```text
release\installer\DDOStudio-Setup-1.7.2.exe
```

See `docs/DDO_Studio_Technical_Documentation_1.7.2.pdf`, `docs/DDO_Studio_Technical_Documentation_1.7.2.docx`, `docs/DDO_Studio_Program_Overview_1.7.2.pdf`, `docs/TECHNICAL_ARCHITECTURE.md`, `docs/PROJECT_HISTORY.md`, `docs/BUILDING.md`, `SCENE_STUDIO.md`, and `VFX_RENDERING_NOTES.md` for additional detail.
