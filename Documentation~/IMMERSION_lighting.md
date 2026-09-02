# IMMERSION lighting export

Three export plugins (enabled by default, configurable in **Project Settings ▸ UnityGLTF ▸ Export**)
capture Unity's baked/scene lighting so the Immersion web editor (three.js) can reproduce the
Unity look.

**The primary output is sidecar files written next to the exported `.glb`/`.gltf`** — the web
editor loads loose PNGs + JSON, not data embedded inside a GLB:

```
Bank.glb
Bank_Lightmap-0_RGBM8.png       one per baked lightmap page (lossless RGBM8, full bake res)
Bank_Lightmap-1_RGBM8.png
Bank_lightmap_offsets.json      lightmap manifest (web editor schema)
Bank_reflection.png             6x1 cube-face atlas (main probe, or skybox fallback)
```

A scene has exactly **one file per lightmap page**: the RGBM8 sidecar. The old tone-curve LDR
page (`Bank_Lightmap-0.png`) is no longer written — see [RGBM8 pages](#rgbm8-pages-custom-immersion-shaders).

All sidecar names are prefixed with the export's base name, so several scenes can coexist in
the same folder or asset store.

Upload these to the editor's project assets together with the GLB; Scene Settings ▸ Load picks
them up by name (`{scene}_lightmap_offsets.json`, `colorName` matching for lightmap pages,
`*reflection*` for the environment).

| Plugin | Sidecar output | glTF extension(s) |
|---|---|---|
| `IMMERSION_lightmaps` | `<name>_lightmap_offsets.json` (no page pixels) | `IMMERSION_lightmaps` (root), `IMMERSION_lightmap` (node) |
| `Gltf Custom Shaders Export` | `<name>_Lightmap-<i>_RGBM8.png` (the lightmap pages) | `extras.customData` (node) |
| `IMMERSION_reflection_probes` | `<name>_reflection.png` (6×1 cube atlas) | `IMMERSION_reflection_probe` (node) |
| `IMMERSION_scene_settings` | — | `IMMERSION_scene_settings` (root: ambient, fog) |

The two lightmap plugins are split by responsibility: the custom-shaders plugin writes the page
PIXELS, the `IMMERSION_lightmaps` plugin writes the page NAMES + tiling (offsets JSON and both
extensions). Their callback order is undefined, so the page file name is *derived* by both from
`ImmersionLightmapPages.PageFileName(i)` rather than handed over. Disabling the custom-shaders
plugin therefore leaves a scene whose extensions name page files that don't exist; disabling
`IMMERSION_lightmaps` leaves the pages declared (minimal root extension, fallback) but drops the
offsets JSON and the per-node tiling.

Each texture plugin also has an **Embed Textures In Glb** toggle (off by default) that
additionally embeds the PNGs as regular glTF textures for non-Immersion consumers. For
`IMMERSION_lightmaps` that copy is the clamped LDR decode (see [HDR → PNG](#hdr--png)) and it is
the ONLY thing the plugin's **Lightmap Texture Scale** / **Lightmap Max Texture Size** settings
still affect — the RGBM8 sidecars are always full bake resolution, uncapped and unscaled.

The global **Export Texture Scale** / **Export Max Texture Size** settings are applied to the
reflection atlas (per cube face) and the embedded skybox, never to lightmaps.

## HDR → PNG

Unity stores lightmaps, probes and skyboxes in HDR. For **probes and the skybox** these plugins
decode the Unity encoding (BC6H/float raw HDR, RGBM, or dLDR) to linear color, clamp to LDR and
write standard sRGB PNGs — the same result as manually converting a Unity lightmap EXR to PNG in
an image editor. Light intensity above 1.0 is clamped.

**Lightmaps no longer take that path**: their pages ship unclamped as RGBM8 (below). The clamped
LDR decode is only produced for the optional embedded copy (`IMMERSION_lightmaps` ▸ *Embed
Textures In Glb*).

## Lightmaps

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
  exact match first, then prefix. It stays `<name>_Lightmap-<i>` — i.e. the *prefix* of the
  RGBM8 page file, which is what it now resolves to (`Bank_Lightmap-0` →
  `Bank_Lightmap-0_RGBM8.png`). Deliberately unchanged so existing projects and uploads keep
  matching. (Caveat of prefix matching: a scene with ≥11 pages could resolve `…_Lightmap-1` to
  `…_Lightmap-10_RGBM8.png` depending on file order; real enviro scenes have one or two pages.)
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
  "scaleOffsetGltf": [sx, sy, ox, oy],
  "texture": 3
}
```

**Name form — one rule everywhere:** every lightmap file name that reaches an output file is
**resolved**, with no `{name}` token left in it. The token only lives inside the exporter
(`GLTFSceneExporter.SidecarNameToken`): sidecar file names and text sidecars are substituted on
write, and both lightmap extensions substitute it themselves via
`GLTFSceneExporter.SidecarBaseName` before serialization. (Until 2026-09 the extension payloads
shipped the literal `{name}` token and consumers had to substitute it — that is gone.)

(`scaleOffsetGltf` is pre-converted for glTF TEXCOORD_1 with flipY=false textures:
`uv * xy + zw`. `texture` only exists when embedding is enabled.)

### RGBM8 pages (custom Immersion shaders)

The `Gltf Custom Shaders Export` plugin encodes every baked lightmap **unclamped** to RGBM8
(`Hidden/RGBMEncode`, `_MaxRange = 5`, decode `hdr = rgb * a * 5` in linear space) — that is what
the custom `Immersion/Web/*` shaders sample, and since 2026-09 it is **the only lightmap page a
scene exports**, as a loose sidecar rather than inside the GLB:

- `<name>_Lightmap-<i>_RGBM8.png` — lossless 8-bit RGBA PNG, full bake resolution, no tone
  curve, no rescale. Same pixel/row orientation as every other exported PNG.
- **the `_RGBM8` suffix is part of the contract**: the web runtime detects the encoding by file
  name (`/_?rgbm8?(\.|$)/i` → bind raw to the Revolution `uLightmap`, or decode `rgb * a * 5`
  for a vanilla lightmap slot). Any other name is treated as a legacy LDR page.
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

  `image` is the resolved sidecar file name, fetchable relative to the GLB as-is; `texture` is
  added next to it only when `IMMERSION_lightmaps`' **Embed Textures In Glb** put an LDR copy in
  the file. Version 1 pointed `image` at the removed LDR page (in `{name}` token form) and listed
  the RGBM8 pages in a separate `rgbmPages` array; that array is gone — read `lightmaps[].image`.

Set `GltfCustomData.EmbedFullLightmapPages = true` (or tick **Embed Full Lightmap Pages** on the
plugin) to embed the full-resolution pages again; the sidecars are written either way.

## Reflection probes

The web editor applies a single environment map per scene, expected as a **horizontal 6×1
cube-face atlas** (width = 6 × height, square faces, order +X, −X, +Y, −Y, +Z, −Z in
three.js `CubeTexture` orientation, sRGB).

- The **main probe** (highest `importance`, then largest volume) is written as
  `<name>_reflection.png`.
- If the scene has **no baked probes but has a skybox**, the skybox is baked into
  `<name>_reflection.png` instead, so the scene still gets an environment.
- Every probe node gets an `IMMERSION_reflection_probe` extension:

```json
{
  "layout": "cubeStrip",
  "main": true,
  "image": "{name}_reflection.png",
  "boxProjection": true,
  "center": [0, 1, 0],
  "size": [10, 4, 10],
  "intensity": 1.0,
  "blendDistance": 1.0,
  "importance": 1,
  "mode": "Baked"
}
```

`center`/`size` are local to the node, converted to glTF's coordinate system (X mirrored vs.
Unity). `{name}` in `image` stands for the export's base file name. The atlas face size is
capped by the plugin's **Max Face Size** setting (default 512) and the global texture scale/cap.

## Scene settings

Root extension `IMMERSION_scene_settings` (ambient + fog; the web editor doesn't consume these
yet, but the data rides along for future use):

```json
{
  "version": 1,
  "ambient": { "mode": "Trilight", "intensity": 1.0, "color": [r, g, b],
               "skyColor": [...], "equatorColor": [...], "groundColor": [...] },
  "fog": { "enabled": true, "mode": "ExponentialSquared", "color": [r, g, b],
           "density": 0.01, "startDistance": 0, "endDistance": 300 },
  "reflectionIntensity": 1.0,
  "skybox": { "texture": 7 }
}
```

Colors are raw Unity inspector values (sRGB). `skybox` (an embedded equirectangular texture)
only exists when the plugin's **Embed Skybox In Glb** toggle is enabled.

## Gotchas

- **Bake first.** Lightmaps and probes must be baked (Lighting window) before export; unbaked
  probes are skipped with a console warning.
- **Sidecar files need a real file export** — they're written by `SaveGLB` / `SaveGLTFandBin`
  (the editor menu does this). Stream/byte-array exports have no output folder, so no sidecars.
- **git-URL package consumers:** every file needs a committed `.meta` — Unity silently ignores
  files without one inside immutable packages.
- **Directional lightmaps / shadowmasks** are not exported (color lightmap only).
- **Lightmap encoding** is detected from the texture format: raw HDR (High Quality/BC6H),
  RGBM (Normal Quality, format with alpha) or dLDR (Low Quality, format without alpha). A
  dLDR lightmap stored in an alpha-bearing uncompressed format would be mis-detected as RGBM —
  use Normal or High Quality lightmap encoding to be safe.
- **Scriptable render pipelines:** the skybox bake uses `Camera.RenderToCubemap`, which can be
  limited in URP/HDRP; if the bake fails or comes out black, the skybox fallback is skipped
  with a warning. Baked reflection probes and lightmaps are unaffected.
