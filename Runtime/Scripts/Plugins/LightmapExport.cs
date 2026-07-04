using System.Collections.Generic;
using System.Linq;
using System.Text;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that exports the scene's baked lightmaps alongside the glTF, in the format
	/// the Immersion web editor consumes:
	///
	/// - one PNG per lightmap page, named <c>Lightmap-&lt;index&gt;.png</c> (HDR/RGBM decoded to
	///   LDR sRGB, same look as manually converting the lightmap EXR to PNG),
	/// - a <c>&lt;exportName&gt;_lightmap_offsets.json</c> manifest with the editor's schema:
	///   <c>lightmaps: [{ index, colorName }]</c> and
	///   <c>renderers: [{ path, lightmapIndex, tilingX, tilingY, offsetX, offsetY }]</c>
	///   (raw Unity Renderer.lightmapScaleOffset values; the editor applies the V-flip itself).
	///
	/// Both are written next to the exported .glb/.gltf. The global ExportTextureScale /
	/// ExportMaxTextureSize settings are applied to the PNGs.
	///
	/// Additionally, node-level <see cref="IMMERSION_lightmap"/> and root-level
	/// <see cref="IMMERSION_lightmaps"/> extensions carry the same information inside the glTF.
	/// With <see cref="embedTexturesInGlb"/> enabled the lightmap PNGs are also embedded as
	/// regular glTF textures (bigger files; the web editor doesn't read embedded ones).
	/// </summary>
	public class LightmapExport : GLTFExportPlugin
	{
		[SerializeField]
		[Tooltip("Also embed the lightmap PNGs as glTF textures inside the exported file. Increases file size; the Immersion web editor only reads the sidecar PNGs.")]
		private bool embedTexturesInGlb = false;

		public override string DisplayName => "IMMERSION_lightmaps";

		public override string Description =>
			"Exports baked lightmaps as PNG files plus a <name>_lightmap_offsets.json manifest " +
			"(Immersion web editor format) next to the exported file, and adds lightmap info " +
			"extensions to the glTF.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new LightmapExportContext(context, embedTexturesInGlb);
		}
	}

	public class LightmapExportContext : GLTFExportPluginContext
	{
		private struct LightmappedNode
		{
			public Node Node;
			public string Path;
			public int LightmapIndex;
			public Vector4 ScaleOffset;
		}

		private readonly ExportContext _context;
		private readonly bool _embedTextures;
		private readonly List<LightmappedNode> _nodes = new List<LightmappedNode>();
		private readonly HashSet<int> _usedIndices = new HashSet<int>();
		// LightmapSettings.lightmaps returns a fresh array copy on every access, so cache it
		private LightmapData[] _lightmaps;
		private LightmapData[] Lightmaps => _lightmaps ?? (_lightmaps = LightmapSettings.lightmaps);

		public LightmapExportContext(ExportContext context, bool embedTextures)
		{
			_context = context;
			_embedTextures = embedTextures;
		}

		public override void BeforeSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			LightingExportUtils.ReleaseTexturesFromPreviousExports();
		}

		public override void BeforeTextureExport(GLTFSceneExporter exporter, ref GLTFSceneExporter.UniqueTexture texture, string textureSlot)
		{
			// lighting textures are pre-scaled to the target resolution; don't scale them twice
			if (LightingExportUtils.IsLightingTexture(texture.Texture))
			{
				texture.Scale = 1f;
				texture.MaxSize = 0;
			}
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

			_nodes.Add(new LightmappedNode
			{
				Node = node,
				Path = GetNodePath(transform, exporter.RootTransforms),
				LightmapIndex = index,
				ScaleOffset = renderer.lightmapScaleOffset,
			});
			_usedIndices.Add(index);
		}

		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			if (_nodes.Count == 0) return;

			var lightmaps = Lightmaps;
			var textureIds = new Dictionary<int, int>();  // lightmapIndex -> glTF texture (when embedding)
			var fileNames = new Dictionary<int, string>(); // lightmapIndex -> sidecar PNG name
			var lightmapsArr = new JArray();               // for the glTF root extension
			var manifestLightmaps = new JArray();          // for the sidecar offsets JSON

			foreach (var index in _usedIndices.OrderBy(i => i))
			{
				var baseName = $"Lightmap-{index}";
				var ldr = LightingExportUtils.DecodeLightmapToLDR(lightmaps[index].lightmapColor, baseName, _context.settings);
				if (ldr == null) continue;

				var fileName = baseName + ".png";
				exporter.AddSidecarFile(fileName, ldr.EncodeToPNG());
				fileNames[index] = fileName;
				manifestLightmaps.Add(new JObject
				{
					// the editor matches colorName against uploaded file names (case-insensitive, no extension)
					["index"] = index,
					["colorName"] = baseName,
				});

				var entry = new JObject { ["lightmapIndex"] = index, ["image"] = fileName };
				if (_embedTextures)
				{
					var id = exporter.ExportTexture(ldr, GLTFSceneExporter.TextureMapType.sRGB, LightingExportUtils.PngExportSettings);
					if (id != null)
					{
						textureIds[index] = id.Id;
						entry["texture"] = id.Id;
					}
				}
				lightmapsArr.Add(entry);
			}

			if (fileNames.Count == 0) return;

			// sidecar offsets JSON in the Immersion web editor schema (raw Unity tiling/offset;
			// the editor applies the glTF V-flip itself)
			var renderers = new JArray();
			foreach (var entry in _nodes)
			{
				if (!fileNames.ContainsKey(entry.LightmapIndex)) continue;
				var so = entry.ScaleOffset;
				renderers.Add(new JObject
				{
					["path"] = entry.Path,
					["lightmapIndex"] = entry.LightmapIndex,
					["tilingX"] = so.x,
					["tilingY"] = so.y,
					["offsetX"] = so.z,
					["offsetY"] = so.w,
				});
			}

			var manifest = new JObject
			{
				["lightmaps"] = manifestLightmaps,
				["renderers"] = renderers,
			};
			exporter.AddSidecarFile(GLTFSceneExporter.SidecarNameToken + "_lightmap_offsets.json",
				Encoding.UTF8.GetBytes(manifest.ToString()));

			// glTF extensions with the same data (node extension + root list)
			foreach (var entry in _nodes)
			{
				if (!fileNames.TryGetValue(entry.LightmapIndex, out var fileName)) continue;
				var so = entry.ScaleOffset;
				var ext = new JObject
				{
					["lightmapIndex"] = entry.LightmapIndex,
					["image"] = fileName,
					["scaleOffset"] = new JArray(so.x, so.y, so.z, so.w),
					// glTF TEXCOORD_1 has V flipped vs. Unity's UV2; this variant bakes that flip
					// in so the sample coordinate is just: uv * xy + zw (with flipY = false textures)
					["scaleOffsetGltf"] = new JArray(so.x, so.y, so.z, 1f - so.y - so.w),
				};
				if (textureIds.TryGetValue(entry.LightmapIndex, out var texId))
					ext["texture"] = texId;
				entry.Node.AddExtension(IMMERSION_lightmap.EXTENSION_NAME, new IMMERSION_lightmap(ext));
			}

			exporter.DeclareExtensionUsage(IMMERSION_lightmap.EXTENSION_NAME, false);
			exporter.DeclareExtensionUsage(IMMERSION_lightmaps.EXTENSION_NAME, false);
			gltfRoot.AddExtension(IMMERSION_lightmaps.EXTENSION_NAME, new IMMERSION_lightmaps(new JObject
			{
				["version"] = 1,
				["lightmaps"] = lightmapsArr,
			}));
		}

		// Node name path from the export root down to this transform (inclusive), e.g.
		// "Building/Wall" — the editor matches by full path first, then by leaf name.
		private static string GetNodePath(Transform transform, IReadOnlyList<Transform> roots)
		{
			var names = new List<string>();
			var current = transform;
			while (current != null)
			{
				names.Add(current.name);
				if (roots != null && roots.Contains(current)) break;
				current = current.parent;
			}
			names.Reverse();
			return string.Join("/", names);
		}
	}
}
