# DDO Studio - How the current architecture was reached

This document records the technical path that produced the current application without relying on one-off test assets or machine-specific data.

## Phase 1 - DAT access and typed IDs

The project began by making the local DDO DAT files reliably readable from a small backend service. Object ID ranges were separated by DAT/type so model discovery would not depend on blind byte scanning alone. DbProperties parsing and name indexing supplied the first useful entry points.

## Phase 2 - Renderable model chains

The next step was establishing a stable relationship chain from named game objects to PhysObj, VisualDescription, Setup, and RenderMesh records. Direct typed references became more important than broad graph proximity. This eliminated many false-positive model candidates and established the current rule that the library only presents records that resolve to a real Setup.

## Phase 3 - GLB reconstruction

Once geometry resolution was reliable, the exporter was built around glTF 2.0. Mesh primitives, materials, UVs, textures, coordinate conversion, skeletons, skin weights, and inverse-bind matrices were moved into a deterministic export pipeline. Preview and manual export now share that exporter rather than using separate rendering logic.

## Phase 4 - Materials, appearance, and composition

MaterialInstance and RenderSurface relationships were decoded far enough to assign base-color and normal textures consistently. Appearance tables were then treated as typed operations instead of loose references, allowing mesh/material/Setup replacements to be composed before export.

## Phase 5 - Havok animation work

Animation discovery was separated from animation playback. The backend first learned to identify Havok-bearing animation records, then decode supported spline-compressed and interleaved transform formats. Compatibility checks were added so a matching track count alone would not automatically make a clip playable on an unrelated skeleton.

## Phase 6 - Visual effects and viewer tooling

Effect data was moved into an evidence-based resolver covering particle descriptions, texture/mask branches, and scene modifiers. In parallel, the Three.js viewer gained model inspection controls, skeleton visualization, animation browsing, scene/environment import, and independent model/pedestal transforms.

## Phase 7 - Final model-library boundary

The final architecture separates the public model-name index from internal metadata. The desktop library focuses on directly renderable models and keeps standalone weapons and shields searchable, while worn/composed equipment records remain outside the public browser. Internal resolver data stays available to the backend where needed. Search, preview, export, animation compatibility, and scene controls now operate around that model-centric contract.

## Phase 8 - Release/privacy hardening

The release pipeline was made self-contained for application components, while keeping game data external in the user's own installation. Runtime caches and user settings live in LocalAppData, debug symbols are excluded from releases, and source/update packages are assembled without personal paths, logs, aliases, screenshots, or machine-specific artifacts.

## Phase 9 - Final presentation defaults and curated animation names

The final viewer behavior moved presentation rules out of one-off asset handling and into classification-based defaults. Weapon records now receive the same Position Y `1.40x` and Rotation X `90°` starting transform, while the pedestal remains independent. Compatible Weapon previews prefer looping `0x05005943`; other models continue to prefer compatible `0x05000440`. Manually identified animation labels were converted into a generic built-in `animation-names.json` database, with user LocalAppData aliases retained only as an optional override layer.
