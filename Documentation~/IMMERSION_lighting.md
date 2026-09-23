# IMMERSION lighting export

Two export plugins (enabled by default, configurable in **Project Settings ▸ UnityGLTF ▸ Export**)
capture Unity's baked lighting so the Immersion web editor / runtime (three.js) can reproduce
the Unity look.

**The primary output is sidecar files written next to the exported `.glb`/`.gltf`** — the web
side loads loose PNGs + JSON, not data embedded inside a GLB:

```
Bank.glb
Bank_Lightmap-0_RGBM8.png       one per baked lightmap page (lossless RGBM8, full bake res)
Bank_Lightmap-1_RGBM8.png
Bank_lightmap_offsets.json      lightmap manifest (web editor schema)
Bank_reflection.png             6x1 cube-face atlas (main probe, or skybox fallback)
```

A scene has exactly **one file per lightmap page**: the RGBM8 sidecar. All sidecar names are
prefixed with the export's base name, so several scenes can coexist in the same folder or
asset store. Upload them to the editor's project assets together with the GLB; Scene Settings ▸
Load picks them up by name (`{scene}_lightmap_offsets.json`, `colorName` matching for lightmap
pages, `*reflection*` for the environment).

| Plugin | Sidecar output | glTF payload |
|---|---|---|
| `Gltf Custom Shaders Export` (`GltfCustomData`) | `<name>_Lightmap-<i>_RGBM8.png` (the lightmap pages) | `extras.customData` on renderer + probe nodes, `extras.customShader` on `Immersion/Web/*` materials, embedded per-probe equirect |
| `IMMERSION_lightmaps` (`LightmapExport`) | `<name>_lightmap_offsets.json` (no page pixels) | `IMMERSION_lightmaps` (root), `IMMERSION_lightmap` (node) |
| `IMMERSION_reflection_probes` (`ReflectionProbeExport`) | `<name>_reflection.png` (6×1 cube atlas) | — |

The two lightmap plugins are split by responsibility: the custom-shaders plugin writes the page
PIXELS, `IMMERSION_lightmaps` writes the page NAMES + tiling (offsets JSON and both extensions).
Their callback order is undefined, so the page file name is *derived* by both from
`ImmersionLightmapPages.PageFileName(i)` rather than handed over. Disabling the custom-shaders
plugin therefore leaves a scene whose extensions name page files that don't exist; disabling
`IMMERSION_lightmaps` leaves the pages declared (minimal root extension, fallback) but drops the
offsets JSON and the per-node tiling.

Nothing here resizes textures. Texture sizes are set after export, in the Editor's Texture
Tools (Apply → Reupload); the RGBM8 pages and the reflection strip are written at bake / probe
resolution (the strip's face size is capped by the plugin's **Max Face Size**, default 512).

## Lightmaps

### RGBM8 pages

The `Gltf Custom Shaders Export` plugin encodes every baked lightmap **unclamped** to RGBM8
(`Hidden/RGBMEncode`, `_MaxRange = 5`, decode `hdr = rgb * a * 5` in linear space) — that is
what the custom `Immersion/Web/*` shaders sample and, decoded to linear HDR, what the vanilla
`material.lightMap` slots get. It is **the only lightmap page a scene exports**, as a loose
sidecar rather than inside the GLB:

- `<name>_Lightmap-<i>_RGBM8.png` — lossless 8-bit RGBA PNG, full bake resolution, no tone
  curve, no rescale. Same pixel/row orientation as every other exported PNG.
- **the `_RGBM8` suffix is part of the contract**: the web runtime detects the encoding by file
  name (`/_?rgbm8?(\.|$)/i`). Any other name is treated as a legacy LDR page (old scenes).
- the GLB keeps a **4×4 black RGBM page** per lightmap under the original texture name
  (`<unityLightmapName>_<i>_RGBM8`), so `extras.customData.lm_index` stays a valid index into
  the model's lightmap page list and page ordering is unchanged. A consumer that ignores the
  sidecars therefore renders visibly unlit rather than subtly wrong.
- the root extension names them (payload **version 2**):

```json
{
  "version": 2,
  "lightmaps": [ { "lightmapIndex": 0, "image": "Bank_Lightmap-0_RGBM8.png" } ]
}
```

`image` is the resolved sidecar file name, fetchable relative to the GLB as-is. Version 1
pointed `image` at a tone-curve LDR page (in `{name}` token form) and listed the RGBM8 pages in
a separate `rgbmPages` array; both are gone.

Set `GltfCustomData.EmbedFullLightmapPages = true` (or tick **Embed Full Lightmap Pages** on the
plugin) to embed the full-resolution RGBM8 pages in the GLB as well; the sidecars are written
either way.

### Per-renderer binding

Every renderer node gets `extras.customData`:

```json
{ "lm_index": 0, "lm_uv_scale_offset": "[sx, sy, ox, oy]", "reflection_probe_texture": "ReflectionProbe-0" }
```

(`lm_index` is -1 for unlit renderers; the scale/offset is the raw Unity
`Renderer.lightmapScaleOffset`; the probe is the one with the highest blend weight for that
renderer.) One `customData` per renderer transform — nothing is propagated to child nodes.

### Offsets manifest

`<name>_lightmap_offsets.json` uses the web editor's existing schema
(`packages/shared/src/scene-core.ts` — `buildOffsetLookup` / `applyLightmapOffsetsToScene`):

```json
{
  "lightmaps": [
    { "index": 0, "colorName": "Bank_Lightmap-0" }
  ],
  "renderers": [
    {
      "path": "Building/Wall",
      "lightmapIndex": 0,
      "tilingX": 1.0, "tilingY": 1.0,
      "offsetX": 0.0, "offsetY": 0.0
    }
  ]
}
```

- `colorName` is matched case-insensitively against uploaded file names (without extension),
  exact match first, then prefix. It stays `<name>_Lightmap-<i>` — the *prefix* of the RGBM8
  page file (`Bank_Lightmap-0` → `Bank_Lightmap-0_RGBM8.png`). Deliberately unchanged so
  existing projects and uploads keep matching. (Caveat of prefix matching: a scene with ≥11
  pages could resolve `…_Lightmap-1` to `…_Lightmap-10_RGBM8.png` depending on file order;
  real enviro scenes have one or two pages.)
- `path` is the node name path from the export root (the editor matches full path first, then
  leaf name).
- `tilingX/Y`, `offsetX/Y` are the **raw Unity** `Renderer.lightmapScaleOffset` values — the
  editor applies the bottom-left-origin V adjustment itself (`1 - tilingY - offsetY`).
- Lightmap UVs are the mesh UV2, exported as glTF `TEXCOORD_1`.

The glTF node extension `IMMERSION_lightmap` carries the same data in-file:

```json
{
  "lightmapIndex": 0,
  "image": "Bank_Lightmap-0_RGBM8.png",
  "scaleOffset": [sx, sy, ox, oy],
  "scaleOffsetGltf": [sx, sy, ox, oy]
}
```

**Name form — one rule everywhere:** every lightmap file name that reaches an output file is
**resolved**, with no `{name}` token left in it. The token only lives inside the exporter
(`GLTFSceneExporter.SidecarNameToken`): sidecar file names and text sidecars are substituted on
write, and both lightmap extensions substitute it themselves via
`GLTFSceneExporter.SidecarBaseName` before serialization.

(`scaleOffsetGltf` is pre-converted for glTF TEXCOORD_1 with flipY=false textures:
`uv * xy + zw`.)

## Reflection probes

Two consumers, two outputs:

1. **Revolution shaders (per renderer)** — `Gltf Custom Shaders Export` embeds one equirect per
   probe in the GLB (`<probeTexture>_Equirect`, linear, divided by `_DynamicRange = 5`,
   `Hidden/CubemapToEquirect` with the probe's own HDR decode values — `unity_SpecCube0_HDR`
   is zero in an editor blit, which used to export all-black probes) and writes
   `extras.customData { rp_intensity, box_projection, bounds, texture }` on the probe node.
   Renderers point at their probe through `reflection_probe_texture` (above).
2. **Web editor scene environment** — `IMMERSION_reflection_probes` writes the **main probe**
   (highest `importance`, then largest volume) as `<name>_reflection.png`, a horizontal 6×1
   cube-face atlas (width = 6 × height, square faces, order +X, −X, +Y, −Y, +Z, −Z in three.js
   `CubeTexture` orientation, sRGB, face size capped by **Max Face Size**). If the export has
   **no baked probe but the scene has a skybox**, the skybox is baked into the strip instead —
   unless the exporter opted out (`ReflectionProbeExport.skyboxFallback`; the avatar batch
   exporter does, so an avatar GLB is not accompanied by the empty scene's procedural sky).

## Gotchas

- **Bake first.** Lightmaps and probes must be baked (Lighting window) before export; unbaked
  probes are skipped with a console warning.
- **Sidecar files need a real file export** — they're written by `SaveGLB` / `SaveGLTFandBin`
  (the editor menu does this). Stream/byte-array exports have no output folder, so no sidecars.
- **git-URL package consumers:** every file needs a committed `.meta` — Unity silently ignores
  files without one inside immutable packages.
- **Directional lightmaps / shadowmasks** are not exported (color lightmap only).
- **URP only:** the encode shaders (`Hidden/RGBMEncode`, `Hidden/CubemapToEquirect`,
  `Hidden/NormalDecodeBlit`, `Hidden/IMMERSION_ChannelPack`) include the URP shader library.
- **Scriptable render pipelines:** the skybox fallback uses `Camera.RenderToCubemap`, which can
  be limited in URP/HDRP; if the bake fails or comes out black, the fallback is skipped with a
  warning. Baked reflection probes and lightmaps are unaffected.
