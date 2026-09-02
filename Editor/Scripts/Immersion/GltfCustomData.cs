
using System;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGLTF;
using UnityGLTF.Plugins;

public class GltfCustomData : GLTFExportPlugin
{
    /// <summary>
    /// Global override for the serialized <c>embedFullLightmapPages</c> field, for batch/CLI
    /// exports that run on <c>GLTFSettings.GetDefaultSettings()</c> (e.g.
    /// <c>Immersion.Export.SceneBatchExporter</c>) and therefore never see the settings asset.
    /// Set it to true to restore the pre-sidecar behaviour.
    /// </summary>
    public static bool EmbedFullLightmapPages = false;

    [SerializeField]
    [Tooltip("Embed the full-resolution RGBM8 lightmap pages inside the GLB (legacy behaviour). " +
             "Off (default): every page ships as a lossless '<name>_Lightmap-<i>_RGBM8.png' sidecar " +
             "next to the GLB and only a 4x4 black placeholder page stays in the GLB — so lm_index " +
             "and the lightmap page order/names are unchanged, but the GLB no longer carries the pixels.")]
    private bool embedFullLightmapPages = false;

    public override string DisplayName => "Gltf Custom Shaders Export";
    public override string Description => "Exports custom shaders and textures to glTF";
    public override bool EnabledByDefault => true;
    public override bool AlwaysEnabled => false;

    public override GLTFExportPluginContext CreateInstance(ExportContext context)
    {
        return new GltfCustomDataExporter(EmbedFullLightmapPages || embedFullLightmapPages);
    }
}

public class GltfCustomDataExporter : GLTFExportPluginContext
{
    private const float LIGHTMAP_DYNAMIC_RANGE = 5f;
    private const string LIGHTMAP_PACKED_SHADER = "Hidden/RGBMEncode";
    private const string CUBEMAP_PACKED_SHADER = "Hidden/CubemapToEquirect";

    // Sidecar file name pattern for the external RGBM8 lightmap pages. The "{name}" token is
    // replaced with the exported file's base name when the sidecars are written, so this matches
    // the LDR sidecars written by UnityGLTF.Plugins.LightmapExport ("<name>_Lightmap-<i>.png").
    private const string LIGHTMAP_SIDECAR_SUFFIX = "_RGBM8.png";

    // Size of the black stand-in page embedded in the GLB when the real pages ship as sidecars.
    private const int LIGHTMAP_PLACEHOLDER_SIZE = 4;

    // Texture slot that maps to { linear = true, alphaMode = Always } in
    // GLTFSceneExporter.GetExportSettingsForSlot — RGBM is linear data and MUST keep its alpha
    // (the multiplier channel), and unlike "linearWithAlpha" this slot skips the TextureImporter
    // sRGB probe that only makes sense for on-disk assets.
    private const string RGBM_TEXTURE_SLOT = "rgbm";

    private readonly List<Texture2D> _tempTexturesToDestroy = new();
    private readonly bool _embedFullLightmapPages;

    public GltfCustomDataExporter(bool embedFullLightmapPages = false)
    {
        _embedFullLightmapPages = embedFullLightmapPages;
    }

    // ───────────────────────── Export hooks ─────────────────────────

    public override void AfterTextureExport(
        GLTFSceneExporter exporter, GLTFSceneExporter.UniqueTexture texture,
        int index, GLTFTexture tex)
    {
        CleanupTempTexturesDelayed();
        base.AfterTextureExport(exporter, texture, index, tex);
    }

    // Exports custom material properties
    public override void AfterMaterialExport(
        GLTFSceneExporter exporter, GLTFRoot gltfRoot,
        Material material, GLTFMaterial materialNode)
    {
        var extras = materialNode.Extras as JObject ?? new JObject();

        switch (material.shader.name)
        {
            case "Immersion/Web/SimpleLitGi":
                ExportSimpleLitGi(exporter, material, extras);
                break;
            case "Immersion/Web/GlassLitGi":
                ExportGlassLitGi(exporter, material, extras);
                break;
            case "Immersion/Web/HairShader":
                ExportHairShader(exporter, material, extras);
                break;
        }

        materialNode.Extras = extras;
        base.AfterMaterialExport(exporter, gltfRoot, material, materialNode);
    }

    // ───────────────────── Material exporters ──────────────────────

    private void ExportSimpleLitGi(GLTFSceneExporter exporter, Material material, JObject extras)
    {
        Vector2 scale = material.mainTextureScale;
        Vector2 offset = material.mainTextureOffset;
        Vector4 rmaMul = material.GetVector("_RMAMul");
        Vector3 emission = material.GetVector("_EmissionColor") * material.GetFloat("_EmissiveMapEnabled");

        extras["customShader"] = new JObject
        {
            ["shader"] = "simpleLit",
            ["roughness"] = rmaMul.x,
            ["metallic"] = rmaMul.y,
            ["ao"] = rmaMul.z,
            ["emission"] = new JObject { ["r"] = emission.x, ["g"] = emission.y, ["b"] = emission.z },
            ["scaleOffset"] = new JObject { ["x"] = scale.x, ["y"] = scale.y, ["z"] = offset.x, ["w"] = offset.y },
        };

        // Unity gates the reflection term by saturate(diffuse + _ReflectionContribution)
        // (GiFragment.hlsl); the web decoder needs the authored value for parity.
        if (material.HasProperty("_ReflectionContribution"))
            extras["customShader"]["reflectionContribution"] = material.GetFloat("_ReflectionContribution");

        // Optional albedo HSL grade (HSV keyword + _Hue vector) — applied by the web shader too.
        if (material.HasProperty("_Hsv") && material.GetFloat("_Hsv") >= 0.5f && material.HasProperty("_Hue"))
        {
            Vector4 hue = material.GetVector("_Hue");
            extras["customShader"]["hsv"] = new JObject { ["x"] = hue.x, ["y"] = hue.y, ["z"] = hue.z };
        }

        // _DETAIL keyword: tiled detail albedo/normal + mask (GiFunctions
        // ApplyDetailAlbedo/ApplyDetailNormal — CC skin pore detail lives here).
        // detailUv = uv * _DetailAlbedoMap_ST.xy + .zw for BOTH detail maps.
        bool detail = material.IsKeywordEnabled("_DETAIL");
        var textureNames = new List<string> { "_BumpMap", "_RMAMap", "_EmissionMap" };
        if (detail)
        {
            Vector2 dScale = material.GetTextureScale("_DetailAlbedoMap");
            Vector2 dOffset = material.GetTextureOffset("_DetailAlbedoMap");
            extras["customShader"]["detailScaleOffset"] = new JObject { ["x"] = dScale.x, ["y"] = dScale.y, ["z"] = dOffset.x, ["w"] = dOffset.y };
            extras["customShader"]["detailAlbedoScale"] = material.GetFloat("_DetailAlbedoMapScale");
            extras["customShader"]["detailNormalScale"] = material.GetFloat("_DetailNormalMapScale");
            textureNames.Add("_DetailMask");
            textureNames.Add("_DetailAlbedoMap");
            textureNames.Add("_DetailNormalMap");
        }

        CollectAndExportTextures(
            exporter, material, extras,
            textureNames.ToArray(),
            (name, tex) => name == "_BumpMap" || name == "_DetailNormalMap"
                ? NormalMapBlitExporter.DecodeNormalToTexture2D(tex, flipGreen: true)
                : tex);

        Debug.Log("Adding custom shader " + material.name);
    }
    
    private void ExportGlassLitGi(GLTFSceneExporter exporter, Material material, JObject extras)
    {
        extras["customShader"] = new JObject
        {
            ["shader"] = "glass",
            ["roughness"] = material.GetFloat("_Roughness"),
            ["reflectionStrength"] = material.GetFloat("_ReflectionStrength"),
            
        };

        Debug.Log("Adding custom shader " + material.name);
    }

    private void ExportHairShader(GLTFSceneExporter exporter, Material material, JObject extras)
    {
        Vector4 param = material.GetVector("_Params");
        Color specColor = material.GetColor("_SpecColor");

        extras["customShader"] = new JObject
        {
            ["shader"] = "hair",
            ["_Params"] = new JObject { ["x"] = param.x, ["y"] = param.y, ["z"] = param.z, ["w"] = param.w },
            ["_SpecColor"] = new JObject { ["r"] = specColor.r, ["g"] = specColor.g, ["b"] = specColor.b },
            ["_SpecIntensity"] = material.GetFloat("_SpecIntensity"),
            ["_Roughness"] = material.GetFloat("_Roughness"),
            ["_Anisotropy"] = material.GetFloat("_Anisotropy"),
            ["_SpecShift"] = material.GetFloat("_SpecShift"),
            ["_AO"] = material.GetFloat("_AO"),
        };

        CollectAndExportTextures(
            exporter, material, extras,
            new[] { "_HairIdMap", "_HairAoMap" });

        Debug.Log("Adding custom shader " + material.name);
    }

    /// <summary>
    /// Collects textures by property name, optionally preprocesses them, records their
    /// names under extras["customShader"]["textures"], and exports them (linear).
    /// </summary>
    private static void CollectAndExportTextures(
        GLTFSceneExporter exporter, Material material, JObject extras,
        string[] textureNames,
        Func<string, Texture2D, Texture2D> preprocess = null)
    {
        var shaderTextures = new List<Texture2D>();
        var texturesJson = new JObject();

        foreach (string textureName in textureNames)
        {
            if (!material.HasProperty(textureName)) continue;

            var texture = material.GetTexture(textureName) as Texture2D;
            if (texture == null || shaderTextures.Contains(texture)) continue;

            if (preprocess != null)
                texture = preprocess(textureName, texture);

            shaderTextures.Add(texture);
            texturesJson[textureName] = texture.name;
        }

        extras["customShader"]["textures"] = texturesJson;

        var exportSettings = new GLTFSceneExporter.TextureExportSettings { linear = true };
        foreach (var shaderTexture in shaderTextures)
        {
            exporter.ExportTexture(shaderTexture, shaderTexture.name, exportSettings);
        }
    }

    // Exports lightmap parameters and reflection probes data
    public override void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node)
    {
        Renderer renderer = transform.GetComponent<Renderer>();

        if (node.Mesh != null && renderer == null) Debug.Log("No renderer on " + transform.name);

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
            
            var extras = node.Extras as JObject ?? new JObject();
            extras["customData"] = new JObject { ["lm_index"] = lightmapIndex, ["lm_uv_scale_offset"] = lmScaleOffsetJson, ["reflection_probe_texture"] = reflectionProbeTexture };

            node.Extras = extras;

            var children = node.Children;
            if (children != null) Debug.Log(node.Children.Count);
            children?.ForEach(child =>
            {
                Debug.Log("Adding custom data to child node");
                var childNode = child.Value;
                if (childNode != null)
                {
                    extras = node.Extras as JObject ?? new JObject();


                    extras["customData"] = new JObject { ["lm_index"] = lightmapIndex, ["lm_uv_scale_offset"] = lmScaleOffsetJson, };

                    childNode.Extras = extras;
                }
            });
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
        // Idempotent, and LightmapExportContext calls it too — plugin callback order is just the
        // order of GLTFSettings.ExportPlugins, so neither side can assume it runs first.
        ImmersionLightmapPages.ApplyToRoot(exporter, gltfRoot);
        base.AfterSceneExport(exporter, gltfRoot);
    }

    // ───────────────────── Lightmap export ──────────────────────────

    /// <summary>
    /// Encodes every baked lightmap page to RGBM8 (decode: <c>hdr = rgb * a * 5.0</c>, linear) and
    /// ships it as a lossless <c>&lt;exportName&gt;_Lightmap-&lt;i&gt;_RGBM8.png</c> sidecar next to
    /// the GLB — byte-for-byte the same encode that used to be embedded, with no tone curve and no
    /// resize. The GLB itself only gets a 4x4 black placeholder page per lightmap under the SAME
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
        var sidecarNames = new List<string>();
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
            // run through EncodeToPNG, which is what the LDR sidecar writer does as well.
            var sidecarName = $"{GLTFSceneExporter.SidecarNameToken}_Lightmap-{i}{LIGHTMAP_SIDECAR_SUFFIX}";
            exporter.AddSidecarFile(sidecarName, rgbmTex.EncodeToPNG());
            sidecarNames.Add(sidecarName);

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

        ImmersionLightmapPages.SetRgbmPages(sidecarNames);

        Debug.Log(_embedFullLightmapPages
            ? $"Exported {exportedPages} lightmap pages as RGBM8 (MaxRange={LIGHTMAP_DYNAMIC_RANGE}): full pages EMBEDDED in the glTF + '*_RGBM8.png' sidecars."
            : $"Exported {exportedPages} lightmap pages as RGBM8 (MaxRange={LIGHTMAP_DYNAMIC_RANGE}): '*_RGBM8.png' SIDECARS + {LIGHTMAP_PLACEHOLDER_SIZE}x{LIGHTMAP_PLACEHOLDER_SIZE} black placeholder pages in the glTF (set GltfCustomData.EmbedFullLightmapPages to embed the full pages).");
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

public static class NormalMapBlitExporter
{
    private const string NORMAL_DECODE_SHADER = "Hidden/NormalDecodeBlit";
    private static readonly int MainTexId = Shader.PropertyToID("_MainTex");
    private static readonly int FlipYId = Shader.PropertyToID("_FlipY");

    /// <summary>
    /// Blits a normal map through the decode shader and returns an RGB (0..1) normal map as Texture2D.
    /// </summary>
    public static Texture2D DecodeNormalToTexture2D(Texture normalMap, bool flipGreen, int width = 0, int height = 0)
    {
        if (normalMap == null) return null;

        width = (width > 0) ? width : normalMap.width;
        height = (height > 0) ? height : normalMap.height;

        var shader = Shader.Find(NORMAL_DECODE_SHADER);
        if (shader == null)
        {
            Debug.LogError($"Shader not found: {NORMAL_DECODE_SHADER}");
            return null;
        }

        var mat = new Material(shader);
        mat.SetTexture(MainTexId, normalMap);
        mat.SetFloat(FlipYId, flipGreen ? 1f : 0f);

        // Use an 8-bit RT since we want to export standard RGB.
        // Keep it linear (sRGB off) because normal data is linear.
        var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGB32, 0)
        {
            msaaSamples = 1,
            depthBufferBits = 0,
            sRGB = false,
            useMipMap = false,
            autoGenerateMips = false
        };

        var rt = RenderTexture.GetTemporary(desc);
        var prev = RenderTexture.active;

        try
        {
            Graphics.Blit(null, rt, mat, 0);

            RenderTexture.active = rt;
            // mipChain: TRUE — the exporter derives the glTF sampler's minFilter from
            // Texture2D.mipmapCount; a mip-less decode ships LINEAR (no mipmap) and the
            // web samples mip 0 at every minification. On the 20x-tiled CC skin
            // micro-normal (_DetailNormalMap) that aliased into per-pixel sparkle
            // ("salt desert" skin, 531 r3 QA 2026-09-02). Apply(true) builds the chain.
            var outTex = new Texture2D(width, height, TextureFormat.RGBA32, true, true); // mipChain: true, linear: true
            outTex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            outTex.Apply(true, false);
            outTex.name = normalMap.name;
            return outTex;
        }
        finally
        {
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            UnityEngine.Object.DestroyImmediate(mat);
        }
    }
}