# DDO GLB Exporter 1.7.2 — Material Pipeline

The exporter converts local DDO Setup/RenderMesh data into GLB and now resolves multiple named material texture roles.

Pipeline:
Setup -> RenderMesh -> MaterialInstance -> MaterialModifier -> named texture property -> RenderTexture -> RenderSurface -> DXT1/DXT3/DXT5 -> embedded PNG -> GLB.

Current glTF mappings:
- `DiffuseMap` -> `pbrMetallicRoughness.baseColorTexture`
- `NormalMap` -> `normalTexture`
- other named DDO texture properties -> `material.extras.ddoTextures` for inspection/future mapping

Important proven behavior:
- UVs are exported as raw `(U, V)`; do not globally flip V.
- materials default to OPAQUE to avoid the ghost-transparency problem seen with BLEND.
- a secondary texture decode failure does not discard a successfully decoded diffuse map.

The desktop app launches this exporter automatically for previews and permanent GLB exports.

## DDO Studio 1.7.2 appearance composition

The exporter accepts an optional resolved NPC appearance composition:

```
DDOGlbExporter.exe 0x0400022C output.glb --appearance "composition.json"
```

The JSON is produced by the local `NpcAppearance` endpoint. Mesh replacements are joined by `RenderMesh.MeshTypeId`, material modifiers by `MaterialInstance.MaterialTypeId`, and any Setup replacement is applied before skeleton/mesh export.
