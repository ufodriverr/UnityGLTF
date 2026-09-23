
using System;
using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGLTF;
using UnityGLTF.Plugins;

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