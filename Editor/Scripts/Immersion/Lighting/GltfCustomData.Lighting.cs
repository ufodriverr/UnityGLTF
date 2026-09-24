
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityGLTF;
using UnityGLTF.Plugins;

// Revolution lighting export half of the Gltf Custom Shaders Export plugin (partial class,
// declared in ../Materials/GltfCustomData.cs): per-renderer extras.customData (lightmap index +
// tiling + probe binding), RGBM lightmap page sidecars with black GLB placeholders, and the
// per-probe equirect the Immersion/Web/* shaders sample. Contract version 2 (see the material
// half): probe binding by uuid, probe `texture` = glTF texture index, ranges 8.
public partial class GltfCustomDataExporter
{
    // Probe uuid per probe (cached: GlobalObjectId.GetGlobalObjectIdSlow is slow and is otherwise
    // asked once per renderer) + the reverse map that keeps the uuids unique within one export.
    private readonly Dictionary<ReflectionProbe, string> _probeUuids = new();
    private readonly Dictionary<string, ReflectionProbe> _uuidOwners = new();

    // Structural probe binding, resolved in AfterSceneExport (a probe node can be exported after
    // the renderers that use it): renderer node -> its probe, probe -> its glTF node.
    private readonly List<(Node node, ReflectionProbe probe)> _rendererProbeBindings = new();
    private readonly Dictionary<ReflectionProbe, Node> _probeNodes = new();

    // Exports lightmap parameters and reflection probes data
    public override void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node)
    {
        Renderer renderer = transform.GetComponent<Renderer>();

        if (renderer != null)
        {
            int lightmapIndex = renderer.lightmapIndex != -1 ? renderer.lightmapIndex : -1;

            var customData = new JObject
            {
                ["version"] = CONTRACT_VERSION,
                ["lm_index"] = lightmapIndex,
                ["lm_uv_scale_offset"] = "",
            };

            if (lightmapIndex != -1)
            {
                Vector4 so = renderer.lightmapScaleOffset;
                // The string form is art v2's key: raw Unity Renderer.lightmapScaleOffset, formatted
                // like art's $"[{x}, {y}, {z}, {w}]" but culture-invariant (a comma-decimal editor
                // locale produced unparsable content). lm_scale_offset carries the same numbers.
                customData["lm_uv_scale_offset"] = "[" + F(so.x) + ", " + F(so.y) + ", " + F(so.z) + ", " + F(so.w) + "]";
                customData["lm_scale_offset"] = new JArray(so.x, so.y, so.z, so.w);
            }

            ReflectionProbe probe = GetMostInfluentialProbe(renderer);
            customData["reflection_probe"] = probe != null ? GetProbeUuid(probe) : "";
            if (probe != null) _rendererProbeBindings.Add((node, probe));

            // One customData per renderer node. (Until 2026-09 — and still in art v2 — this also
            // stamped the PARENT's lm_index / tiling onto every child node, sharing one JObject and
            // running after the children's own AfterNodeExport, so a nested renderer lost its own
            // lightmap binding and its probe. Every renderer transform gets its own callback.)
            var extras = node.Extras as JObject ?? new JObject();
            extras["customData"] = customData;
            node.Extras = extras;
        }

        ReflectionProbe rp = transform.GetComponent<ReflectionProbe>();
        if (rp != null)
        {
            var tex = rp.texture as Cubemap;
            if (tex == null)
            {
                Debug.LogWarning($"[GltfCustomData] reflection probe '{transform.name}' has no baked cubemap — not exported (bake it first).");
            }
            else
            {
                int textureId = ExportReflectionProbeCubemap(exporter, tex, tex.name, rp.textureHDRDecodeValues);
                var bounds = rp.bounds;

                var extras = node.Extras as JObject ?? new JObject();
                extras["customData"] = new JObject
                {
                    // art v2 keys
                    ["rp_intensity"] = rp.intensity,
                    ["box_projection"] = rp.boxProjection,
                    ["bounds"] = bounds.ToString(),
                    ["texture"] = textureId,
                    ["reflection_probe"] = GetProbeUuid(rp),
                    // fork additions
                    ["version"] = CONTRACT_VERSION,
                    ["range"] = PROBE_RANGE,
                    ["bounds_center"] = new JArray(bounds.center.x, bounds.center.y, bounds.center.z),
                    ["bounds_extents"] = new JArray(bounds.extents.x, bounds.extents.y, bounds.extents.z),
                };
                node.Extras = extras;
                _probeNodes[rp] = node;
            }
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
        ResolveProbeNodeBindings(gltfRoot);
        base.AfterSceneExport(exporter, gltfRoot);
    }

    // renderer customData.reflection_probe_node = glTF node index of its probe, when the probe is
    // part of this export — a structural binding next to the uuid.
    private void ResolveProbeNodeBindings(GLTFRoot gltfRoot)
    {
        if (_rendererProbeBindings.Count == 0 || _probeNodes.Count == 0 || gltfRoot.Nodes == null) return;

        var nodeIndex = new Dictionary<Node, int>();
        for (int i = 0; i < gltfRoot.Nodes.Count; i++) nodeIndex[gltfRoot.Nodes[i]] = i;

        foreach (var (node, probe) in _rendererProbeBindings)
        {
            if (!_probeNodes.TryGetValue(probe, out var probeNode)) continue;
            if (!nodeIndex.TryGetValue(probeNode, out var index)) continue;
            if ((node.Extras as JObject)?["customData"] is JObject customData)
                customData["reflection_probe_node"] = index;
        }
    }

    // ───────────────────── Probe uuid ───────────────────────────────

    /// <summary>
    /// The probe binding id (art v2: <c>GlobalObjectId.targetObjectId</c> as a decimal string).
    /// Saved scenes use exactly that. When it is 0 (objects of an unsaved scene — the avatar CLI
    /// instantiates into one) or already taken by a different probe in this export (only
    /// <c>targetObjectId</c> is kept, not the asset GUID / prefab instance id), a deterministic
    /// fallback is used: FNV-1a 64 of the scene path + the sibling-indexed hierarchy path,
    /// 63-bit, decimal. Cached per probe.
    /// </summary>
    private string GetProbeUuid(ReflectionProbe probe)
    {
        if (_probeUuids.TryGetValue(probe, out var cached)) return cached;

        string uuid = null;
        var scene = probe.gameObject.scene;
        if (scene.IsValid() && !string.IsNullOrEmpty(scene.path))
        {
            var gid = GlobalObjectId.GetGlobalObjectIdSlow(probe.gameObject);
            if (gid.targetObjectId != 0)
                uuid = gid.targetObjectId.ToString(CultureInfo.InvariantCulture);
        }

        if (uuid == null || (_uuidOwners.TryGetValue(uuid, out var owner) && owner != probe))
        {
            ulong hash = Fnv1a64(scene.path + "|" + HierarchyPath(probe.transform)) & 0x7FFFFFFFFFFFFFFFUL;
            if (hash == 0) hash = 1;
            while (_uuidOwners.TryGetValue(hash.ToString(CultureInfo.InvariantCulture), out owner) && owner != probe)
                hash = (hash + 1) & 0x7FFFFFFFFFFFFFFFUL;
            uuid = hash.ToString(CultureInfo.InvariantCulture);
        }

        _probeUuids[probe] = uuid;
        _uuidOwners[uuid] = probe;
        return uuid;
    }

    private static string HierarchyPath(Transform t)
    {
        var parts = new List<string>();
        for (var c = t; c != null; c = c.parent)
            parts.Add(c.name + "[" + c.GetSiblingIndex() + "]");
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static ulong Fnv1a64(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            h ^= b;
            h *= 1099511628211UL;
        }
        return h;
    }

    private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

    // ───────────────────── Lightmap export ──────────────────────────

    /// <summary>
    /// Encodes every baked lightmap page to RGBM (decode: <c>hdr = rgb * a * 8</c>, linear —
    /// <see cref="LIGHTMAP_RGBM_RANGE"/>; the range is also declared per page in the root
    /// <c>IMMERSION_lightmaps</c> extension as <c>rgbmRange</c>) and ships it as a lossless
    /// <c>&lt;exportName&gt;_Lightmap-&lt;i&gt;_RGBM8.png</c> sidecar next to the GLB (the "8" in
    /// the suffix is the 8-bit RGBM ENCODING marker, not the range). The page is resampled in HDR
    /// BEFORE the encode when the lightmap cap is set (0 = full bake resolution). The GLB gets a
    /// 4x4 black placeholder page per lightmap named <c>{unityLightmap}_{i}_RGBM8lightmap</c> (art
    /// v2's embedded page name), so <c>lm_index</c> stays a valid index into the model's lightmap
    /// page list. Set <see cref="GltfCustomData.EmbedFullLightmapPages"/> (or the plugin's
    /// serialized flag) to embed the full pages instead (art-demo parity); the sidecars are
    /// written either way.
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
        var sizes = new List<string>();

        for (int i = 0; i < lightmapsCount; i++)
        {
            var src = LightmapSettings.lightmaps[i].lightmapColor;
            if (src == null) continue;

            // HDR resample (when capped) + encode -> RGBM into an in-memory Texture2D (RGBA32, linear)
            ClampSize(src.width, src.height, _maxLightmapSize, out int w, out int h);
            var rgbmTex = EncodeLightmap(src, matScope.Material, LIGHTMAP_RGBM_RANGE, w, h);
            var pageName = $"{src.name}_{i}_RGBM8lightmap";
            rgbmTex.name = pageName;
            ApplyLightmapPageSampler(rgbmTex);
            sizes.Add(w + "x" + h + (w != src.width || h != src.height ? $" (from {src.width}x{src.height})" : ""));

            // Sidecar: the raw RGBM readback straight to PNG (8-bit RGBA, non-premultiplied,
            // lossless). Same row order as the embedded page — both are Unity ReadPixels results
            // run through EncodeToPNG, like every other PNG this pipeline writes. The name comes
            // from the shared helper because IMMERSION_lightmaps derives the very same string for
            // its extension payloads without depending on which plugin runs first; the "_RGBM8"
            // suffix is how the web runtime recognises an RGBM page file.
            var sidecarName = ImmersionLightmapPages.PageFileName(i);
            exporter.AddSidecarFile(sidecarName, rgbmTex.EncodeToPNG());
            ImmersionLightmapPages.RegisterPage(i, sidecarName);

            if (_embedFullLightmapPages)
            {
                _lightmapTextures.Add(rgbmTex);
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
                _lightmapTextures.Add(placeholder);
                exporter.ExportTexture(placeholder, RGBM_TEXTURE_SLOT);
                _tempTexturesToDestroy.Add(placeholder);
            }

            exportedPages++;
        }

        Debug.Log(_embedFullLightmapPages
            ? $"Exported {exportedPages} lightmap pages as RGBM (range {LIGHTMAP_RGBM_RANGE}, {string.Join(", ", sizes)}): full pages EMBEDDED in the glTF + '*_RGBM8.png' sidecars."
            : $"Exported {exportedPages} lightmap pages as RGBM (range {LIGHTMAP_RGBM_RANGE}, {string.Join(", ", sizes)}): '*_RGBM8.png' SIDECARS (the only lightmap files) + {LIGHTMAP_PLACEHOLDER_SIZE}x{LIGHTMAP_PLACEHOLDER_SIZE} black placeholder pages in the glTF (set GltfCustomData.EmbedFullLightmapPages to embed the full pages).");
    }

    private static Texture2D EncodeLightmap(Texture source, Material rgbmMat, float maxRange, int width, int height)
    {
        rgbmMat.SetFloat("_MaxRange", maxRange);
        return BlitToTexture2D(source, width, height, rgbmMat,
            RenderTextureFormat.ARGB32, TextureFormat.RGBA32);
    }

    /// <summary>
    /// A 4x4 RGBM page that decodes to linear black: rgb = 0, m = 1 (alpha 255), so
    /// <c>rgb * (a * range) == 0</c>. Alpha is kept at 255 rather than 0 so nothing in the
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

    /// <summary>
    /// Embeds the probe as a <c>4w x 2w</c> equirect (capped by the texture cap, aspect kept),
    /// linear, divided by <see cref="PROBE_RANGE"/>, 8-bit PNG with alpha 255 (NOT RGBM). Returns
    /// the glTF texture index, -1 when the encode shader is missing.
    /// </summary>
    private int ExportReflectionProbeCubemap(GLTFSceneExporter exporter, Cubemap cube, string name, Vector4 hdrDecodeValues)
    {
        int size = cube.width * 2;
        ClampSize(size * 2, size, _maxTextureSize, out int width, out int height);
        var eq = EncodeReflectionProbe(cube, width, height, hdrDecodeValues);
        if (eq == null) return -1;

        eq.name = $"{name}_Equirect";

        var exportSettings = new GLTFSceneExporter.TextureExportSettings { linear = true };
        var texId = exporter.ExportTexture(eq, eq.name, exportSettings);

        // Don't destroy immediately; UnityGLTF may encode later.
        _tempTexturesToDestroy.Add(eq);
        return texId.Id;
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
        matScope.Material.SetFloat("_DynamicRange", PROBE_RANGE);
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
            _lightmapTextures.Clear();
            _decodedNormals.Clear();
        };
    }
}
