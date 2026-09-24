# IMMERSION fork changelog

Fork-specific history (upstream's `CHANGELOG.md` is left untouched so upstream merges stay
clean). Current layout and concern map: `IMMERSION.md`; lighting contract: `IMMERSION_lighting.md`.

## 2026-09-24 — Revolution exporter contract v2 (art v2 ported as a superset)

- **Contract v2** in `GltfCustomData` (material + lighting halves): every key of the art team's
  Revolution exporter v2 (`Assets/_Project/Revolution` @ `3f86b8dd01`, 09-23) with the same
  name/type/meaning — `customShader.textures` = glTF texture **indices**, `hsv {h,s,v}` + `isHsv`
  always, `uvRotation`, `isPbr`, `alphaTest`, `alphaCutoff`, `skin`, hair `_SpecColor` linear,
  normal maps decoded **without** the green flip, probe binding `reflection_probe` (uuid) on
  renderers and probe nodes, probe `texture` = index, lightmap RGBM range and probe equirect
  range **5 → 8** — plus `"version": 2` on every `customShader` / `customData`, and what Unity
  still renders but art v2 drops: `reflectionContribution`, the `_DETAIL` maps, glass
  `color`/`cullMode`/`_BaseTex`, simpleLit `cullMode` (`_CullMode` 0 → glTF `doubleSided`), the new
  `Immersion/Web/OcclusionMask` shader (`shader: "occlusionMask"`), `textureNames {slot: name}`,
  renderer `lm_scale_offset` (numeric) + `reflection_probe_node`, probe `range` +
  `bounds_center`/`bounds_extents`. `lm_uv_scale_offset` is culture-invariant.
- Probe uuid = `GlobalObjectId.targetObjectId` (as art) with a deterministic FNV-1a fallback for
  unsaved scenes (avatar CLI) / collisions; cached per probe.
- Decoded normal maps are cached per source texture: no duplicate images (the art v2 GLB had 13).
  The generic normal fallback in `ExporterMaterials` skips `Immersion/Web/*` (no extra glTF
  `normalTexture` next to the decoded copy).
- Lightmaps: sidecar names unchanged (`<name>_Lightmap-<i>_RGBM8.png`); the GLB placeholder / full
  page is named `<lm>_<i>_RGBM8lightmap` (art v2 name); `IMMERSION_lightmaps` payload **version
  3** with `rgbmRange: 8` at the root and per page; the offsets JSON carries `rgbmRange` too.
- Optional caps `MaxTextureSize` / `MaxLightmapSize` on the plugin (default 0 = unlimited; CLI
  `-maxTextureSize` / `-maxLightmapSize`, applied by `ImmersionExportSettings.ApplyDefaults`);
  lightmaps are resampled in HDR before the encode. `SceneBatchExporter -embedLightmaps true`.
- New CLI `AvatarProbeExporter.ExportFromScene`: per-avatar baked probes resolved from a logic
  scene (`probeAnchor` → probe → baked `.exr` + intensity) → `exr2equirect.py --rgbm --webready`-
  identical RGBM64 PNGs + `<prefix>_avatar_probes.json`.
- The consuming project must drop its `Assets/_Project/Revolution` copy (same global classes and
  `Hidden/*` shaders).

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
