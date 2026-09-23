// Revolution material export: extras.customShader for the Immersion/Web/* shaders.
// The lighting half of this plugin (lightmap pages, per-renderer customData, probe equirects)
// lives in ../Lighting/GltfCustomData.Lighting.cs (same partial class).

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

public partial class GltfCustomDataExporter : GLTFExportPluginContext
{
    private const float LIGHTMAP_DYNAMIC_RANGE = 5f;
    private const string LIGHTMAP_PACKED_SHADER = "Hidden/RGBMEncode";
    private const string CUBEMAP_PACKED_SHADER = "Hidden/CubemapToEquirect";

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
    }
    
    private void ExportGlassLitGi(GLTFSceneExporter exporter, Material material, JObject extras)
    {
        extras["customShader"] = new JObject
        {
            ["shader"] = "glass",
            ["roughness"] = material.GetFloat("_Roughness"),
            ["reflectionStrength"] = material.GetFloat("_ReflectionStrength"),
        };
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
    }

    /// <summary>
    /// Collects textures by property name, optionally preprocesses them, records their
    /// names under extras["customShader"]["textures"], and exports them (linear).
    /// </summary>
    private void CollectAndExportTextures(
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
            {
                var processed = preprocess(textureName, texture);
                if (processed == null) continue;
                // decoded copies (normal maps) are temporaries: keep them alive until the
                // exporter has encoded them, then destroy with the other temp textures
                if (processed != texture) _tempTexturesToDestroy.Add(processed);
                texture = processed;
            }

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
}
