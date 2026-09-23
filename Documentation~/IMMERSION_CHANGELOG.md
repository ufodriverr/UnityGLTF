# IMMERSION fork changelog

Fork-specific history (upstream's `CHANGELOG.md` is left untouched so upstream merges stay
clean). Current layout and concern map: `IMMERSION.md`; lighting contract: `IMMERSION_lighting.md`.

## 2026-09-23 — cleanup wave

- **Layout by concern.** All Immersion code moved under `Editor/Scripts/Immersion/{Avatar,
  Materials, Lighting, Cli, Diagnostics}`; the former Runtime plugins (`LightmapExport`,
  `ReflectionProbeExport`, `LightingExportUtils`, `AnimatorExtrasExport`) now live in the Editor
  assembly. `GltfCustomData` is a partial class split into its material and lighting halves.
  Encode shaders under `Editor/Shaders/Immersion/`. Shared CLI helpers in `Cli/CliArgs.cs`.
- **`AvatarMaterialMaps` replaces `AvatarMaterialFixExport`.** One declarative per-shader table
  (SG_FakeSSSSkin / SG_FakeSSS / SG_HairAnisoFakeSSS / SG_AvatarsClothes / RL_CorneaShaderBasic /
  RL_TearlineShader graphs + Reallusion Amplify skin/hair/teeth/tongue/cornea) exports base
  colour, normal, alpha, culling and a GPU-packed glTF ORM texture (`Hidden/IMMERSION_ChannelPack`)
  straight from the material. Skin/teeth/clothes gain occlusion + per-texel roughness/metallic
  (the old plugin stripped AO and left roughness 1), hair clips at `_AlphaClip` (the property the
  graph actually uses) and follows the material's `_Cull`, the orphan mask images are gone from
  the GLB.
- **Transmission is configuration.** `MaterialExtensionsExport.KHR_materials_transmission`
  defaults to off (tagged upstream hunk) and both batch exporters force it off via
  `ImmersionExportSettings`; the "preserve specular" path made URP transparent shells invisible
  glass on the web.
- **`ReflectionProbeExport`** writes only the `<name>_reflection.png` strip; the unread
  `IMMERSION_reflection_probe` node extension, the embed toggle and the texture-scale hooks are
  gone. The skybox fallback is opt-out (`skyboxFallback`) and off for avatar exports.
- **`GltfCustomData`**: `extras.customData` is written once per renderer node. The old loop also
  stamped the parent's lightmap index/tiling onto every child node after the children's own
  callbacks, so nested renderers lost their own binding and probe. Debug.Log noise removed;
  decoded normal-map temporaries are now destroyed after the export.
- **Removed** (no consumer on the web side): `DefaultPoseExport` (re-posed skinned meshes to bind
  pose on every export, including scene batches — default-state posing in `AvatarBatchExporter`
  is the one pose path), `AnimatorControllerExport` + `IMMERSION_animator_controller` (the
  flattened `scenes[].extras.IMMERSION_animator` is the contract), `SceneSettingsExport` +
  `IMMERSION_scene_settings`, the LDR/Photopea lightmap decode (`UnityGLTFLightmapDecode.shader`,
  `LightmapExport` embed/scale/cap — superseded on 2026-09-02 by the lossless RGBM8 pages),
  `UnityGLTFCubemapToEquirect.shader`, `ExportEnviroProbes` (531 one-off), the global
  *Export Texture Scale / Max Size* mechanism (`GLTFSettings`, `ExporterTextures`,
  `UniqueTexture.Scale` back to upstream — texture sizing happens after export in the Editor's
  Texture Tools), the Reallusion-specific albedo name list in `ExporterMaterials` (the generic
  Albedo/Main/Color/Base/Diffuse matcher stays), the `Invisible.mat` serializer bump and the
  fork's hijack of upstream's `CHANGELOG.md`.
- Upstream-file hunks are tagged `// IMMERSION:` (`ExporterAnimation`, `ExporterMaterials`,
  `GLTFSceneExporter` sidecar API, `MaterialExtensionsExport`).

## 2026-09-02

- Lightmap pages ship as lossless RGBM8 sidecars (`<name>_Lightmap-<i>_RGBM8.png`), the GLB keeps
  4×4 black placeholders; `IMMERSION_lightmaps` payload version 2 (resolved names, no `rgbmPages`);
  `GLTFSceneExporter.SidecarBaseName`. Decoded normal maps keep a mip chain.

## 2026-08-29

- The Revolution export pipeline moved from the app repo into the package (`GltfCustomData`,
  encode shaders, `ExportEnviroProbes`, `SceneStatistics`).

## 2026-07 … 2026-08

- `AvatarBatchExporter` (default-state posing via `Animator.Play` + `Update`, prefab unpack /
  activate, fresh settings), `SceneBatchExporter`, `TwistProbe` (proved Unity's runtime leaves
  unmapped `*Twist*` bones at bind rotation — no twist solve is applied anywhere),
  `AnimatorExtrasExport`, RL material fallbacks in `ExporterMaterials`, metallic 0 / roughness 1
  for unknown shaders, spec-gloss → metallic-roughness fallback.

## 2026-06-25

- `IMMERSION_animator_controller` (since removed), blend-tree / sub-state-machine clip baking in
  `ExporterAnimation`, texture downscale settings (since removed).
