# DDO Studio 1.7.2 - Visual Effect Reconstruction

DDO visual effects can be distributed across particle descriptions, texture/surface branches, appearance data, and scene-level modifiers rather than living inside a single RenderMesh.

The backend therefore returns effect evidence separately from the base model. The viewer can use texture/mask information to build additive sprites or glows while keeping ambiguous auxiliary geometry diagnostic-only.

## Texture and mask handling

Grayscale or low-saturation textures can represent alpha/luminance masks rather than visible color. The viewer analyzes loaded effect textures and can combine a colored source with a separate mask using additive blending. This avoids treating a mask as an ordinary opaque square.

## Anchoring

Effect placement is derived from the loaded model's local geometry bounds and effect metadata. Model-space anchors are transformed with the model root, so user position/rotation changes keep the effect aligned with the model.

## Conservative reconstruction

Effect graphs can contain unrelated decorative or shared resources. DDO Studio does not assume every referenced mesh belongs in the final effect. Proven base geometry stays in the primary GLB while effect-only evidence is resolved separately.
