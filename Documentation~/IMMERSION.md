# IMMERSION additions to UnityGLTF — what is where

This fork (`ufodriverr/UnityGLTF`, branch `Immersion_vova_branch`) is upstream UnityGLTF plus
the Unity → web export toolchain of the Immersion MetaCoach project. It is consumed as a UPM
package from the git URL; nothing Immersion-specific should live in a consuming project.

Everything Immersion-specific sits under `Editor/Scripts/Immersion/` (Editor assembly — export
plugins are discovered via `TypeCache` in any assembly, and nobody exports at runtime), grouped
by concern, plus the glTF schema classes next to upstream's and a handful of tagged hunks in
upstream files (`// IMMERSION:`).

```
Editor/Scripts/Immersion/
  Avatar/       AvatarBatchExporter   CLI: avatar prefab -> GLB (poses the rig, bakes clips)
                AvatarMaterialMaps    per-shader export table for the CC/Reallusion avatar shaders
                AnimatorExtrasExport  scenes[].extras.IMMERSION_animator (flattened web animator)
  Materials/    GltfCustomData        Revolution plugin, material half: extras.customShader
                NormalMapBlit         DXT5nm/BC5 normal -> RGB decode blit
  Lighting/     GltfCustomData.Lighting  Revolution plugin, lighting half: customData, RGBM8 pages, probe equirects
                LightmapExport        IMMERSION_lightmaps: offsets JSON + page names/tiling
                ReflectionProbeExport IMMERSION_reflection_probes: <name>_reflection.png strip
                LightingExportUtils   cube -> strip blit, skybox bake, temp-texture pool
  Cli/          SceneBatchExporter    CLI: whole scenes -> GLB + sidecars
                AvatarProbeExporter   CLI: logic scene -> one RGBM equirect PNG per avatar probe + manifest
                ImmersionExportSettings  the plugin defaults every web export needs
                CliArgs               -executeMethod argument helpers
  Diagnostics/  TwistProbe            play-mode twist-bone ground truth (closed investigation)
                SceneStatistics       material/shader audit window (art helper)
Editor/Shaders/Immersion/             RGBMEncode, CubemapToEquirect, NormalDecode, ChannelPack (URP, Shader.Find)
Runtime/Resources/UnityGLTFCubemapToFaces.shader   6x1 strip blit (Resources.Load — must stay here)
Runtime/Plugins/GLTFSerialization/Extensions/IMMERSION_lightmaps.cs   root + node schema
Documentation~/IMMERSION_lighting.md  the lightmap / probe contract
Documentation~/IMMERSION_CHANGELOG.md fork history (upstream's CHANGELOG is left alone)
```

## "I want to change X" → where the core is

Most of the work is done by **upstream** code; the fork hooks into it. Start in the upstream
file, then look for the plugin.

| Concern | Upstream core | Fork hooks |
|---|---|---|
| Mesh, rig, skin, blendshapes | `GLTFSceneExporter.SaveGLB → ExportScene → ExportNode`, `SceneExporter/ExporterMeshes.cs`, `ExporterSkinning.cs` | none — node TRS is captured from the **current pose**, which is why `AvatarBatchExporter` poses first |
| Animation clip baking | `SceneExporter/ExporterAnimation.cs` (`ExportAnimationFromNode → ExportAnimationClips → ConvertClipToGLTFAnimation → GenerateMissingCurves/BakePropertyAnimation`), `ExporterAnimationHumanoid.cs` | two tagged hunks (sub-state-machine + blend-tree clips), `AvatarBatchExporter.PoseToDefaultState` (undriven bones bake CONSTANT curves from the current pose), `AnimatorExtrasExport` (authored flattened animator, set via `-animator`) |
| Standard materials + textures | `SceneExporter/ExporterMaterials.cs` (`ExportMaterial`, `ExportPBRMetallicRoughness`), `ExporterTextures.cs` | tagged hunks: generic name matchers for custom shaders, metallic 0 default, spec-gloss fallback |
| CC / Reallusion avatar materials | (taken over before the generic path) | `Avatar/AvatarMaterialMaps.cs` — the table; `Shaders/Immersion/ChannelPack.shader` packs the mask channels into glTF ORM |
| Revolution shaders (`Immersion/Web/*`) | — | `Materials/GltfCustomData.cs` → `extras.customShader` contract v2 (+ `NormalMapBlit`); key list in `IMMERSION_lighting.md` ▸ Contract v2 |
| Per-avatar baked probes (scene-config channel) | — | `Cli/AvatarProbeExporter.cs` |
| Lightmaps | — | `Lighting/GltfCustomData.Lighting.cs` (RGBM8 pages + `customData.lm_index`), `Lighting/LightmapExport.cs` (offsets JSON + names) — see `IMMERSION_lighting.md` |
| Reflection probes | — | `Lighting/GltfCustomData.Lighting.cs` (per-probe equirect + `customData`), `Lighting/ReflectionProbeExport.cs` (scene strip) |
| Loose files next to the GLB | `GLTFSceneExporter` — `AddSidecarFile`, `SidecarNameToken`, `SidecarBaseName` (tagged) | every lighting plugin |
| Transmission / KHR material extensions | `Plugins/MaterialExtensionsExport.cs` | `KHR_materials_transmission` default off (tagged) + forced off by `ImmersionExportSettings` |

## Command lines

```
Unity.exe -batchmode -quit -projectPath <proj> -executeMethod Immersion.Export.AvatarBatchExporter.ExportAvatars
  -avatars "Assets/A/Foo.prefab;Assets/B/Bar.prefab" -out "C:/exports"
  [-animator "C:/exports/foo.animator.json;;"] [-controller "Assets/A/Alt.controller;;"]

Unity.exe -batchmode -quit -projectPath <proj> -executeMethod Immersion.Export.SceneBatchExporter.ExportScenes
  -scenes "Assets/Scenes/EnvA.unity;Assets/Scenes/EnvB.unity" -out "C:/exports"
  [-embedLightmaps true]

Unity.exe -batchmode -quit -projectPath <proj> -executeMethod Immersion.Export.AvatarProbeExporter.ExportFromScene
  -scene "Assets/_Project/Scenes/Logic/Logic_X.unity" -out "C:/exports"
  [-range 64] [-webready false] [-avatars "MetaCoach;Lisa"] [-prefix Name]

Unity.exe -batchmode -projectPath <proj> -executeMethod Immersion.Export.TwistProbe.Run
  -avatars "a.prefab" -controller "a.controller" -out "C:/dir"        (no -quit)
```

Scene and avatar exporters take optional `-maxTextureSize N` / `-maxLightmapSize N` (longest side
in px) — default 0 = full resolution; web assets are sized after export in the Editor's Texture
Tools. Lists align by index; an empty segment means "none / keep the prefab's own". Both exporters
build their settings from `GLTFSettings.GetDefaultSettings()` (the persisted settings asset can
lose its plugin sub-assets in batch mode) and then apply `ImmersionExportSettings.ApplyDefaults`
— so a project's settings asset never decides what a web export contains. Exit code 0 = all
exported, 1 = at least one failed (per-item lines start with `[AvatarBatchExporter]` /
`[SceneBatchExporter]`).

## Rules

- The consuming project must NOT also contain the art team's Revolution copy
  (`Assets/_Project/Revolution`): it declares the same global classes (`GltfCustomData`,
  `GltfCustomDataExporter`, `NormalMapBlitExporter`) and `Hidden/*` encode shaders — two plugins
  would register and `Shader.Find` becomes ambiguous. Its v2 contract is ported here as a superset.

- Every file needs a committed `.meta` — a git-URL package silently drops files without one.
- After a fork push, consuming projects pin the commit in `Packages/packages-lock.json`: bump via
  Package Manager ▸ Update, or delete the lock entry.
- The three encode shaders under `Editor/Shaders/Immersion/` are URP-only (they include the URP
  shader library) and are resolved with `Shader.Find` at export time.
- Verify avatar exports numerically (quaternion deltas on `*Twist01` bones against a known-good
  GLB), never by eye; an ad-hoc `Export selected` from a T-posed prefab bakes T-pose statics.
