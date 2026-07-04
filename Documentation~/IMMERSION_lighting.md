# IMMERSION lighting extensions

Three export plugins (enabled by default, toggleable in **Project Settings ▸ UnityGLTF ▸ Export**)
capture Unity's baked/scene lighting so a web renderer (three.js) can reproduce the Unity look:

| Plugin | Extension(s) | What it exports |
|---|---|---|
| `IMMERSION_lightmaps` | `IMMERSION_lightmaps` (root), `IMMERSION_lightmap` (node) | Baked lightmaps as PNG + per-mesh lightmap index & UV tiling |
| `IMMERSION_reflection_probes` | `IMMERSION_reflection_probe` (node) | ReflectionProbe cubemaps as equirectangular PNG + probe metadata |
| `IMMERSION_scene_settings` | `IMMERSION_scene_settings` (root) | Skybox as equirectangular PNG, ambient light, fog |

All textures go through the regular UnityGLTF texture pipeline, so the
**Export Texture Scale** and **Export Max Texture Size** settings apply to them as well
(on GLB export, same as for regular textures).

## HDR → PNG

Unity stores lightmaps, probes and skyboxes in HDR. These plugins decode the Unity encoding
(BC6H/float raw HDR, or RGBM) to linear color, clamp to LDR and write standard sRGB PNGs —
the same result as manually converting a Unity lightmap EXR to PNG in an image editor.
Light intensity above 1.0 is clamped; if washed-out bright spots ever become a problem, an
RGBM-encoded PNG variant can be added later.

## IMMERSION_lightmaps / IMMERSION_lightmap

Root extension:

```json
"extensions": {
  "IMMERSION_lightmaps": {
    "version": 1,
    "lightmaps": [ { "lightmapIndex": 0, "texture": 3 } ]
  }
}
```

Every lightmapped `MeshRenderer`'s node gets:

```json
"extensions": {
  "IMMERSION_lightmap": {
    "lightmapIndex": 0,
    "texture": 3,
    "scaleOffset": [sx, sy, ox, oy],
    "scaleOffsetGltf": [sx, sy, ox, oy]
  }
}
```

- `scaleOffset` is the raw Unity `Renderer.lightmapScaleOffset` (for Unity-style UV2 with V up).
- `scaleOffsetGltf` is the same tiling pre-converted for glTF's `TEXCOORD_1` (V flipped) and
  textures loaded with `flipY = false` (GLTFLoader default). Sampling is then simply:

```glsl
vec2 lmUv = texcoord1 * scaleOffsetGltf.xy + scaleOffsetGltf.zw;
```

three.js example (Lambert/Standard materials support `lightMap` natively; note three.js expects
the tiling baked into `uv2`/`uv1` attribute or handled via a small shader patch):

```js
const ext = gltf.parser.json.extensions?.IMMERSION_lightmaps;
const nodeExt = nodeDef.extensions?.IMMERSION_lightmap;
const lightMap = await gltf.parser.getDependency('texture', nodeExt.texture);
lightMap.channel = 1; // TEXCOORD_1
material.lightMap = lightMap;
// apply nodeExt.scaleOffsetGltf either by transforming the uv1 attribute
// or via lightMap.repeat/offset when the mesh has its own lightmap chart:
lightMap.repeat.set(so[0], so[1]); lightMap.offset.set(so[2], so[3]);
```

The mesh's lightmap UVs are the regular glTF `TEXCOORD_1` accessor (Unity UV2, already V-flipped
by the exporter).

## IMMERSION_reflection_probe

Attached to the node that has the `ReflectionProbe` component (probe position = node position):

```json
"extensions": {
  "IMMERSION_reflection_probe": {
    "texture": 5,
    "boxProjection": true,
    "center": [0, 1, 0],
    "size": [10, 4, 10],
    "intensity": 1.0,
    "blendDistance": 1.0,
    "importance": 1,
    "mode": "Baked"
  }
}
```

`center`/`size` are in the node's local space, already converted to glTF's coordinate system
(X mirrored vs. Unity). The texture is an equirectangular panorama in three.js orientation:

```js
const tex = await gltf.parser.getDependency('texture', probeExt.texture);
tex.mapping = THREE.EquirectangularReflectionMapping;
tex.colorSpace = THREE.SRGBColorSpace;
const envMap = new THREE.PMREMGenerator(renderer).fromEquirectangular(tex).texture;
```

The per-probe panorama width is `4 × cubemap face size`, capped by the plugin's
**Max Equirect Width** setting (default 2048) and the global texture scale/cap.

## IMMERSION_scene_settings

```json
"extensions": {
  "IMMERSION_scene_settings": {
    "version": 1,
    "skybox": { "texture": 7 },
    "ambient": { "mode": "Skybox", "intensity": 1.0, "color": [r, g, b] },
    "fog": { "enabled": true, "mode": "ExponentialSquared", "color": [r, g, b],
             "density": 0.01, "startDistance": 0, "endDistance": 300 },
    "reflectionIntensity": 1.0
  }
}
```

- The skybox is baked with a temporary camera (works for 6-sided, cubemap and procedural
  skyboxes) and exported as an equirectangular PNG. In three.js:

```js
const sky = await gltf.parser.getDependency('texture', ext.skybox.texture);
sky.mapping = THREE.EquirectangularReflectionMapping;
sky.colorSpace = THREE.SRGBColorSpace;
scene.background = sky;
scene.environment = sky; // or use a probe/PMREM instead
```

- `ambient.mode` is `Skybox`, `Trilight` or `Flat`; `Trilight` additionally has
  `skyColor` / `equatorColor` / `groundColor`.
- Colors are raw Unity inspector values (sRGB): `new THREE.Color(...).setRGB(r, g, b, THREE.SRGBColorSpace)`.
- `fog.mode` is `Linear`, `Exponential` or `ExponentialSquared` (maps to `THREE.Fog` /
  `THREE.FogExp2`).

## Gotchas

- **Bake first.** Lightmaps and probes must be baked (Lighting window) before export; unbaked
  probes are skipped with a console warning.
- **Prefab exports** also include the skybox/ambient/fog of the currently open scene (via the
  `IMMERSION_scene_settings` plugin). Disable the plugin in Project Settings if that's unwanted.
- **Directional lightmaps / shadowmasks** are not exported (color lightmap only).
- **Lightmap encoding** is detected from the texture format: raw HDR (High Quality/BC6H),
  RGBM (Normal Quality, format with alpha) or dLDR (Low Quality, format without alpha). A
  dLDR lightmap stored in an alpha-bearing uncompressed format would be mis-detected as RGBM —
  use Normal or High Quality lightmap encoding to be safe.
- **Scriptable render pipelines:** the skybox bake uses `Camera.RenderToCubemap`, which can be
  limited in URP/HDRP; if the bake fails the skybox is skipped with a warning.
