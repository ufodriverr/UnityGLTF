# IMMERSION lighting export

Three export plugins (enabled by default, configurable in **Project Settings ▸ UnityGLTF ▸ Export**)
capture Unity's baked/scene lighting so the Immersion web editor (three.js) can reproduce the
Unity look.

**The primary output is sidecar files written next to the exported `.glb`/`.gltf`** — the web
editor loads loose PNGs + JSON, not data embedded inside a GLB:

```
Bank.glb
Bank_Lightmap-0.png             one per baked lightmap page (LDR sRGB)
Bank_Lightmap-1.png
Bank_lightmap_offsets.json      lightmap manifest (web editor schema)
Bank_reflection.png             6x1 cube-face atlas (main probe, or skybox fallback)
```

All sidecar names are prefixed with the export's base name, so several scenes can coexist in
the same folder or asset store.

Upload these to the editor's project assets together with the GLB; Scene Settings ▸ Load picks
them up by name (`{scene}_lightmap_offsets.json`, `colorName` matching for lightmap pages,
`*reflection*` for the environment).

| Plugin | Sidecar output | glTF extension(s) |
|---|---|---|
| `IMMERSION_lightmaps` | `Lightmap-<i>.png` + `<name>_lightmap_offsets.json` | `IMMERSION_lightmaps` (root), `IMMERSION_lightmap` (node) |
| `IMMERSION_reflection_probes` | `<name>_reflection.png` (6×1 cube atlas) | `IMMERSION_reflection_probe` (node) |
| `IMMERSION_scene_settings` | — | `IMMERSION_scene_settings` (root: ambient, fog) |

Each texture plugin also has an **Embed Textures In Glb** toggle (off by default) that
additionally embeds the PNGs as regular glTF textures for non-Immersion consumers.

The global **Export Texture Scale** / **Export Max Texture Size** settings are applied to the
sidecar PNGs (per cube face for the reflection atlas).

## HDR → PNG

Unity stores lightmaps, probes and skyboxes in HDR. These plugins decode the Unity encoding
(BC6H/float raw HDR, RGBM, or dLDR) to linear color, clamp to LDR and write standard sRGB PNGs —
the same result as manually converting a Unity lightmap EXR to PNG in an image editor. Light
intensity above 1.0 is clamped.

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

- `colorName` is matched case-insensitively against uploaded file names (without extension) —
  it always equals the exported PNG's base name.
- `path` is the node name path from the export root (the editor matches full path first, then
  leaf name).
- `tilingX/Y`, `offsetX/Y` are the **raw Unity** `Renderer.lightmapScaleOffset` values — the
  editor applies the bottom-left-origin V adjustment itself (`1 - tilingY - offsetY`).
- Lightmap UVs are the mesh UV2, exported as glTF `TEXCOORD_1`.

The glTF node extension `IMMERSION_lightmap` carries the same data in-file:

```json
{
  "lightmapIndex": 0,
  "image": "{name}_Lightmap-0.png",
  "scaleOffset": [sx, sy, ox, oy],
  "scaleOffsetGltf": [sx, sy, ox, oy],
  "texture": 3
}
```

(In extension payloads `{name}` stays literal — extensions are serialized into the glTF before
the output file name is known; substitute the export's base name when resolving. Sidecar file
names and the offsets JSON have it already resolved.)

(`scaleOffsetGltf` is pre-converted for glTF TEXCOORD_1 with flipY=false textures:
`uv * xy + zw`. `texture` only exists when embedding is enabled.)

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
