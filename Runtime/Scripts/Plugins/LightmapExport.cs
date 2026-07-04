using System.Collections.Generic;
using System.Linq;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that includes the scene's baked lightmaps in the exported glTF.
	///
	/// The lightmap textures (HDR/RGBM in Unity) are decoded to plain LDR color and exported as
	/// PNG through the regular texture pipeline (so ExportTextureScale / ExportMaxTextureSize
	/// apply). A root-level <see cref="IMMERSION_lightmaps"/> extension lists the exported
	/// lightmaps, and every lightmapped mesh node gets a <see cref="IMMERSION_lightmap"/>
	/// extension with its lightmap index and UV tiling (Renderer.lightmapScaleOffset).
	///
	/// The lightmap UVs themselves are the mesh's UV2, which UnityGLTF already exports as
	/// TEXCOORD_1 (with V flipped, as glTF requires) — the node extension contains a
	/// pre-converted "scaleOffsetGltf" so a three.js renderer can sample directly:
	/// uv = TEXCOORD_1 * scaleOffsetGltf.xy + scaleOffsetGltf.zw
	/// </summary>
	public class LightmapExport : GLTFExportPlugin
	{
		public override string DisplayName => "IMMERSION_lightmaps";

		public override string Description =>
			"Exports baked lightmaps as PNG textures (HDR decoded to LDR) plus per-node lightmap " +
			"index and UV tiling, so a web renderer can reproduce Unity's baked lighting.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new LightmapExportContext();
		}
	}

	public class LightmapExportContext : GLTFExportPluginContext
	{
		private struct LightmappedNode
		{
			public Node Node;
			public int LightmapIndex;
			public Vector4 ScaleOffset;
		}

		private readonly List<LightmappedNode> _nodes = new List<LightmappedNode>();
		private readonly HashSet<int> _usedIndices = new HashSet<int>();
		// LightmapSettings.lightmaps returns a fresh array copy on every access, so cache it
		private LightmapData[] _lightmaps;
		private LightmapData[] Lightmaps => _lightmaps ?? (_lightmaps = LightmapSettings.lightmaps);

		public override void BeforeSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			LightingExportUtils.ReleaseTexturesFromPreviousExports();
		}

		public override void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node)
		{
			// Renderer covers MeshRenderer and (less commonly) lightmapped SkinnedMeshRenderers
			if (!transform.TryGetComponent<Renderer>(out var renderer)) return;

			// lightmapIndex uses sentinel values (0xFFFF = none, 0xFFFE = not baked yet) which are
			// filtered out by the range check against the actual lightmap array.
			var index = renderer.lightmapIndex;
			var lightmaps = Lightmaps;
			if (index < 0 || index >= lightmaps.Length) return;
			if (lightmaps[index] == null || !lightmaps[index].lightmapColor) return;

			_nodes.Add(new LightmappedNode { Node = node, LightmapIndex = index, ScaleOffset = renderer.lightmapScaleOffset });
			_usedIndices.Add(index);
		}

		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			if (_nodes.Count == 0) return;

			var lightmaps = Lightmaps;
			var textureIds = new Dictionary<int, int>();
			var lightmapsArr = new JArray();

			foreach (var index in _usedIndices.OrderBy(i => i))
			{
				var source = lightmaps[index].lightmapColor;
				var ldr = LightingExportUtils.DecodeLightmapToLDR(source, $"Lightmap-{index}");
				if (ldr == null) continue;
				var id = exporter.ExportTexture(ldr, GLTFSceneExporter.TextureMapType.sRGB, LightingExportUtils.PngExportSettings);
				if (id == null) continue;

				textureIds[index] = id.Id;
				lightmapsArr.Add(new JObject
				{
					["lightmapIndex"] = index,
					["texture"] = id.Id,
				});
			}

			if (textureIds.Count == 0) return;

			foreach (var entry in _nodes)
			{
				if (!textureIds.TryGetValue(entry.LightmapIndex, out var textureId)) continue;
				var so = entry.ScaleOffset;
				entry.Node.AddExtension(IMMERSION_lightmap.EXTENSION_NAME, new IMMERSION_lightmap(new JObject
				{
					["lightmapIndex"] = entry.LightmapIndex,
					["texture"] = textureId,
					["scaleOffset"] = new JArray(so.x, so.y, so.z, so.w),
					// glTF TEXCOORD_1 has V flipped vs. Unity's UV2; this variant bakes that flip
					// in so the sample coordinate is just: uv * xy + zw (with flipY = false textures)
					["scaleOffsetGltf"] = new JArray(so.x, so.y, so.z, 1f - so.y - so.w),
				}));
			}

			exporter.DeclareExtensionUsage(IMMERSION_lightmap.EXTENSION_NAME, false);
			exporter.DeclareExtensionUsage(IMMERSION_lightmaps.EXTENSION_NAME, false);
			gltfRoot.AddExtension(IMMERSION_lightmaps.EXTENSION_NAME, new IMMERSION_lightmaps(new JObject
			{
				["version"] = 1,
				["lightmaps"] = lightmapsArr,
			}));
		}
	}
}
