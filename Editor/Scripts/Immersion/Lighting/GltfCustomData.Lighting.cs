
using System;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGLTF;
using UnityGLTF.Plugins;

// Revolution lighting export half of the Gltf Custom Shaders Export plugin (partial class,
// declared in ../Materials/GltfCustomData.cs): per-renderer extras.customData (lightmap index +
// tiling + probe binding), RGBM8 lightmap page sidecars with black GLB placeholders, and the
// per-probe equirect the Immersion/Web/* shaders sample.
public partial class GltfCustomDataExporter
{
    // Exports lightmap parameters and reflection probes data
    public override void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node)
    {
        Renderer renderer = transform.GetComponent<Renderer>();

        if (renderer != null)
        {
            bool hasLightmap = renderer.lightmapIndex != -1;
            
            int lightmapIndex = -1;
            string lmScaleOffsetJson = "";

            if (hasLightmap)
            {
                lightmapIndex = renderer.lightmapIndex;
                Vector4 lightmapScaleOffset = renderer.lightmapScaleOffset;

                lmScaleOffsetJson = $"[{lightmapScaleOffset.x}, {lightmapScaleOffset.y}, {lightmapScaleOffset.z}, {lightmapScaleOffset.w}]";
            }
            
            ReflectionProbe rendererReflectionProbe = GetMostInfluentialProbe(renderer);
            bool hasReflectionProbe = rendererReflectionProbe != null;
            string reflectionProbeTexture = "";

            if (hasReflectionProbe)
            {
                if (rendererReflectionProbe.texture != null)
                {
                    reflectionProbeTexture = rendererReflectionProbe.texture.name;
                }
            }
            
            // One customData per renderer node. (Until 2026-09 this also stamped the PARENT's
            // lm_index / tiling onto every child node — sharing the parent's extras object and
            // running after the children's own AfterNodeExport, so a nested renderer lost its
            // own lightmap binding and its reflection_probe_texture. Every renderer transform
            // gets its own callback; nothing needs propagating.)
            var extras = node.Extras as JObject ?? new JObject();
            extras["customData"] = new JObject { ["lm_index"] = lightmapIndex, ["lm_uv_scale_offset"] = lmScaleOffsetJson, ["reflection_probe_texture"] = reflectionProbeTexture };

            node.Extras = extras;
        }

        ReflectionProbe rp = transform.GetComponent<ReflectionProbe>();
        if (rp != null)
        {
            var extras = node.Extras as JObject ?? new JObject();
            var tex = rp.texture as Cubemap;

            if (tex == null) return;

            ExportReflectionProbeCubemap(exporter, tex, tex.name, rp.textureHDRDecodeValues);

            extras["customData"] = new JObject
            {
                ["rp_intensity"] = rp.intensity, ["box_projection"] = rp.boxProjection, ["bounds"] = rp.bounds.ToString(), ["texture"] = tex.name,
            };
            node.Extras = extras;
        }

        base.AfterNodeExport(exporter, gltfRoot, transform, node);
    }

    public override void BeforeSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
    {
        // The RGBM page list is static (it has to survive until whichever plugin builds the root
        // IMMERSION_lightmaps extension runs); clear last export's leftovers.
        ImmersionLightmapPages.Reset();
        base.BeforeSceneExport(exporter, gltfRoot);
    }

    // Exports lightmap textures
    public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
    {
        ExportLightmaps(exporter);
        // Fallback only: the IMMERSION_lightmaps plugin owns the root "lightmaps" list and wins
        // whether it runs before or after this one (plugin callback order is just the order of
        // GLTFSettings.ExportPlugins). This keeps the sidecars declared if it is disabled.
        ImmersionLightmapPages.EnsurePagesDeclared(exporter, gltfRoot);
        base.AfterSceneExport(exporter, gltfRoot);
    }

    // ───────────────────── Lightmap export ──────────────────────────

    /// <summary>
    /// Encodes every baked lightmap page to RGBM8 (decode: <c>hdr = rgb * a * 5.0</c>, linear) and
    /// ships it as a lossless <c>&lt;exportName&gt;_Lightmap-&lt;i&gt;_RGBM8.png</c> sidecar next to
    /// the GLB — byte-for-byte the same encode that used to be embedded, with no tone curve and no
    /// resize. This is the ONLY lightmap page file a scene export produces (the legacy tone-curve
    /// LDR sidecar is gone); the names are declared in the root <c>IMMERSION_lightmaps</c>
    /// extension. The GLB itself only gets a 4x4 black placeholder page per lightmap under the SAME
    /// texture name, so <c>lm_index</c> stays a valid index into the model's lightmap page list and
    /// the web runtime's page ordering (name match + glTF texture order) is untouched. Set
    /// <see cref="GltfCustomData.EmbedFullLightmapPages"/> (or the plugin's serialized flag) to
    /// embed the full pages again; the sidecars are written either way.
    /// </summary>
    private void ExportLightmaps(GLTFSceneExporter exporter)
    {
        int lightmapsCount = LightmapSettings.lightmaps.Length;
        if (lightmapsCount == 0) return;

        var shader = Shader.Find(LIGHTMAP_PACKED_SHADER);
        if (shader == null)
        {
            Debug.LogError($"RGBM shader not found: {LIGHTMAP_PACKED_SHADER}");
            return;
        }

        using var matScope = new MaterialScope(shader);
        int exportedPages = 0;

        for (int i = 0; i < lightmapsCount; i++)
        {
            var src = LightmapSettings.lightmaps[i].lightmapColor;
            if (src == null) continue;

            // Encode HDR -> RGBM(8) into an in-memory Texture2D (RGBA32, linear)
            var rgbmTex = EncodeLightmap(src, matScope.Material, LIGHTMAP_DYNAMIC_RANGE);
            var pageName = $"{src.name}_{i}_RGBM8";
            rgbmTex.name = pageName;
            ApplyLightmapPageSampler(rgbmTex);

            // Sidecar: the raw RGBM8 readback straight to PNG (8-bit RGBA, non-premultiplied,
            // lossless). Same row order as the embedded page — both are Unity ReadPixels results
            // run through EncodeToPNG, like every other PNG this pipeline writes. The name comes
            // from the shared helper because IMMERSION_lightmaps derives the very same string for
            // its extension payloads without depending on which plugin runs first; the "_RGBM8"
            // suffix is how the web runtime recognises an RGBM8 page.
            var sidecarName = ImmersionLightmapPages.PageFileName(i);
            exporter.AddSidecarFile(sidecarName, rgbmTex.EncodeToPNG());
            ImmersionLightmapPages.RegisterPage(i, sidecarName);

            if (_embedFullLightmapPages)
            {
                exporter.ExportTexture(rgbmTex, RGBM_TEXTURE_SLOT);
                _tempTexturesToDestroy.Add(rgbmTex);
            }
            else
            {
                // The full-res encode is already on disk; don't keep an RGBA32 copy of every
                // lightmap page alive until the delayed cleanup runs.
                UnityEngine.Object.DestroyImmediate(rgbmTex);

                // Black, not white/neutral: a runtime that ignores the sidecar then renders an
                // obviously unlit scene instead of a subtly wrong one.
                var placeholder = CreateBlackLightmapPage(pageName);
                exporter.ExportTexture(placeholder, RGBM_TEXTURE_SLOT);
                _tempTexturesToDestroy.Add(placeholder);
            }

            exportedPages++;
        }

        Debug.Log(_embedFullLightmapPages
            ? $"Exported {exportedPages} lightmap pages as RGBM8 (MaxRange={LIGHTMAP_DYNAMIC_RANGE}): full pages EMBEDDED in the glTF + '*_RGBM8.png' sidecars."
            : $"Exported {exportedPages} lightmap pages as RGBM8 (MaxRange={LIGHTMAP_DYNAMIC_RANGE}): '*_RGBM8.png' SIDECARS (the only lightmap files) + {LIGHTMAP_PLACEHOLDER_SIZE}x{LIGHTMAP_PLACEHOLDER_SIZE} black placeholder pages in the glTF (set GltfCustomData.EmbedFullLightmapPages to embed the full pages).");
    }

    private static Texture2D EncodeLightmap(Texture source, Material rgbmMat, float maxRange)
    {
        rgbmMat.SetFloat("_MaxRange", maxRange);
        return BlitToTexture2D(source, source.width, source.height, rgbmMat,
            RenderTextureFormat.ARGB32, TextureFormat.RGBA32);
    }

    /// <summary>
    /// A 4x4 RGBM8 page that decodes to linear black: rgb = 0, m = 1 (alpha 255), so
    /// <c>rgb * (a * MaxRange) == 0</c>. Alpha is kept at 255 rather than 0 so nothing in the
    /// export or import chain can mistake the page for a fully transparent texture.
    /// </summary>
    private static Texture2D CreateBlackLightmapPage(string name)
    {
        // linear: true — same flags the real page gets, so no sRGB conversion on export.
        var tex = new Texture2D(LIGHTMAP_PLACEHOLDER_SIZE, LIGHTMAP_PLACEHOLDER_SIZE,
            TextureFormat.RGBA32, false, true);
        var pixels = new Color32[LIGHTMAP_PLACEHOLDER_SIZE * LIGHTMAP_PLACEHOLDER_SIZE];
        for (int p = 0; p < pixels.Length; p++)
            pixels[p] = new Color32(0, 0, 0, 255);
        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        tex.name = name;
        ApplyLightmapPageSampler(tex);
        return tex;
    }

    // Lightmap atlas pages must clamp (Repeat bleeds the opposite edge of the atlas into a chart);
    // no mip chain is built here — the web runtime generates its own mips for the sidecar page.
    private static void ApplyLightmapPageSampler(Texture2D tex)
    {
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
    }

    public static ReflectionProbe GetMostInfluentialProbe(Renderer renderer)
    {
        if (renderer == null) return null;

        var blendInfos = new List<ReflectionProbeBlendInfo>();
        renderer.GetClosestReflectionProbes(blendInfos);

        if (blendInfos.Count == 0) return null;

        // GetClosestReflectionProbes already sorts by descending weight,
        // but we pick the max explicitly to be safe.
        ReflectionProbe best = null;
        float bestWeight = float.NegativeInfinity;

        foreach (var info in blendInfos)
        {
            if (info.probe == null) continue;
            if (info.weight > bestWeight)
            {
                bestWeight = info.weight;
                best = info.probe;
            }
        }

        return best;
    }

    // ───────────────────── Reflection probe export ─────────────────

    private void ExportReflectionProbeCubemap(GLTFSceneExporter exporter, Cubemap cube, string name, Vector4 hdrDecodeValues)
    {
        int size = cube.width * 2;
        // Pick a reasonable size; 512x256 or 1024x512 depending on quality needs.
        var eq = EncodeReflectionProbe(cube, size * 2, size, hdrDecodeValues);
        if (eq == null) return;

        eq.name = $"{name}_Equirect";

        var exportSettings = new GLTFSceneExporter.TextureExportSettings { linear = true };
        exporter.ExportTexture(eq, eq.name, exportSettings);

        // Don't destroy immediately; UnityGLTF may encode later.
        _tempTexturesToDestroy.Add(eq);
    }

    private static Texture2D EncodeReflectionProbe(Cubemap cube, int width, int height, Vector4 hdrDecodeValues)
    {
        var shader = Shader.Find(CUBEMAP_PACKED_SHADER);
        if (shader == null)
        {
            Debug.LogError($"Shader not found: {CUBEMAP_PACKED_SHADER}");
            return null;
        }

        using var matScope = new MaterialScope(shader);
        matScope.Material.SetTexture("_Cube", cube);
        matScope.Material.SetFloat("_DynamicRange", 5.0f);
        // The probe's own HDR decode instructions — unity_SpecCube0_HDR is NOT
        // populated in an editor blit, which used to export all-black equirects.
        matScope.Material.SetVector("_CubeDecode", hdrDecodeValues);

        return BlitToTexture2D(null, width, height, matScope.Material,
            RenderTextureFormat.ARGBHalf, TextureFormat.RGBAHalf);
    }

    // ───────────────────── Shared GPU blit helper ──────────────────

    /// <summary>
    /// Blits <paramref name="source"/> (or null) through <paramref name="blitMaterial"/> into a
    /// new linear Texture2D via a temporary render texture.
    /// </summary>
    private static Texture2D BlitToTexture2D(
        Texture source, int width, int height,
        Material blitMaterial,
        RenderTextureFormat rtFormat, TextureFormat texFormat)
    {
        var desc = new RenderTextureDescriptor(width, height, rtFormat, 0)
        {
            msaaSamples = 1,
            depthBufferBits = 0,
            mipCount = 1,
            useMipMap = false,
            autoGenerateMips = false,
#if UNITY_2021_2_OR_NEWER
            sRGB = false, // keep RT in linear space
#endif
        };

        var rt = RenderTexture.GetTemporary(desc);
        var prev = RenderTexture.active;

        try
        {
            Graphics.Blit(source, rt, blitMaterial, 0);

            RenderTexture.active = rt;

            // 'linear: true' is critical for correct decoding later.
            var tex = new Texture2D(width, height, texFormat, false, true);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply(false, false);
            return tex;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
        }
    }

    // ───────────────────── Utilities ────────────────────────────────

    private sealed class MaterialScope : IDisposable
    {
        public Material Material { get; }

        public MaterialScope(Shader shader) => Material = new Material(shader);

        public void Dispose()
        {
            if (Material != null)
                UnityEngine.Object.DestroyImmediate(Material);
        }
    }

    // Call this after you finish exporting everything (Editor safe delay).
    private void CleanupTempTexturesDelayed()
    {
        UnityEditor.EditorApplication.delayCall += () =>
        {
            foreach (var t in _tempTexturesToDestroy)
                if (t != null)
                    UnityEngine.Object.DestroyImmediate(t);

            _tempTexturesToDestroy.Clear();
        };
    }
}
