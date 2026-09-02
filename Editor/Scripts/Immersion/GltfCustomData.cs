
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
    public override string DisplayName => "Gltf Custom Shaders Export";
    public override string Description => "Exports custom shaders and textures to glTF";
    public override bool EnabledByDefault => true;
    public override bool AlwaysEnabled => false;

    public override GLTFExportPluginContext CreateInstance(ExportContext context)
    {
        return new GltfCustomDataExporter();
    }
}

public class GltfCustomDataExporter : GLTFExportPluginContext
{
    private const float LIGHTMAP_DYNAMIC_RANGE = 5f;
    private const string LIGHTMAP_PACKED_SHADER = "Hidden/RGBMEncode";
    private const string CUBEMAP_PACKED_SHADER = "Hidden/CubemapToEquirect";
    private readonly List<Texture2D> _tempTexturesToDestroy = new();

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

    // Exports lightmap textures
    public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
    {
        ExportLightmaps(exporter);
        base.AfterSceneExport(exporter, gltfRoot);
    }

    // ───────────────────── Lightmap export ──────────────────────────

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
        var exportSettings = new GLTFSceneExporter.TextureExportSettings
        {
            linear = true,
            alphaMode = GLTFSceneExporter.TextureExportSettings.AlphaMode.Always,
        };

        for (int i = 0; i < lightmapsCount; i++)
        {
            var src = LightmapSettings.lightmaps[i].lightmapColor;
            if (src == null) continue;

            // Encode HDR -> RGBM(8) into an in-memory Texture2D (RGBA32, linear)
            var rgbmTex = EncodeLightmap(src, matScope.Material, LIGHTMAP_DYNAMIC_RANGE);
            rgbmTex.name = $"{src.name}_{i}_RGBM8";

            exporter.ExportTexture(rgbmTex, rgbmTex.name, exportSettings);
            _tempTexturesToDestroy.Add(rgbmTex);
        }

        Debug.Log($"Exported {lightmapsCount} lightmaps as in-memory RGBM PNG textures into glTF (MaxRange={LIGHTMAP_DYNAMIC_RANGE}).");
    }

    private static Texture2D EncodeLightmap(Texture source, Material rgbmMat, float maxRange)
    {
        rgbmMat.SetFloat("_MaxRange", maxRange);
        return BlitToTexture2D(source, source.width, source.height, rgbmMat,
            RenderTextureFormat.ARGB32, TextureFormat.RGBA32);
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