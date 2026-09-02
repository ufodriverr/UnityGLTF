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
	///   LDR with the Photopea curve — see UnityGLTFLightmapDecode.shader — matching the
	///   reference manual .hdr-in-Photopea-to-PNG conversion, NOT the exact sRGB formula),
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
		[Range(0.01f, 1f)]
		[Tooltip("Resolution scale for the exported lightmap PNGs (1 = full bake resolution). Lightmaps use these settings INSTEAD of the global Export Texture Scale / Export Max Texture Size.")]
		private float lightmapTextureScale = 1f;

		[SerializeField]
		[Tooltip("Optional hard cap on the largest exported lightmap dimension, in pixels. 0 = no cap. Independent of the global Export Max Texture Size.")]
		private int lightmapMaxTextureSize = 0;

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
			return new LightmapExportContext(context, embedTexturesInGlb, lightmapTextureScale, lightmapMaxTextureSize);
		}
	}

	/// <summary>
	/// Cross-plugin hand-off for the EXTERNAL RGBM8 lightmap pages.
	///
	/// The Immersion custom-data export plugin (<c>GltfCustomDataExporter</c>, Editor assembly)
	/// writes the full-precision RGBM8 lightmap pages as <c>&lt;name&gt;_Lightmap-&lt;i&gt;_RGBM8.png</c>
	/// sidecars, but the file names belong in the root <see cref="IMMERSION_lightmaps"/> extension
	/// that <see cref="LightmapExportContext"/> builds. Plugin callbacks fire in the order of
	/// <c>GLTFSettings.ExportPlugins</c>, so neither plugin can assume it runs first: both call
	/// <see cref="ApplyToRoot"/> and the call is idempotent.
	/// </summary>
	public static class ImmersionLightmapPages
	{
		/// <summary>Root-extension key holding the sidecar file names, in lightmap-page order.</summary>
		public const string RgbmPagesKey = "rgbmPages";

		private static readonly List<string> _rgbmPages = new List<string>();

		/// <summary>Registered RGBM8 sidecar names (still carrying the sidecar name token).</summary>
		public static IReadOnlyList<string> RgbmPages => _rgbmPages;

		/// <summary>Drops the previous export's page list. Call from BeforeSceneExport.</summary>
		public static void Reset()
		{
			_rgbmPages.Clear();
		}

		/// <summary>Records the RGBM8 sidecar file names, in lightmap-page (lm_index) order.</summary>
		public static void SetRgbmPages(IEnumerable<string> fileNames)
		{
			_rgbmPages.Clear();
			if (fileNames != null) _rgbmPages.AddRange(fileNames);
		}

		/// <summary>
		/// Adds/refreshes <c>rgbmPages</c> on the root <see cref="IMMERSION_lightmaps"/> extension,
		/// creating the extension if no plugin has added it yet. No-op when nothing was registered.
		/// </summary>
		public static void ApplyToRoot(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			if (gltfRoot == null || _rgbmPages.Count == 0) return;

			// The GLB/glTF JSON is not run through the sidecar name-token replacement (only file
			// names and text sidecars are), so resolve the token here against the exported scene
			// name — which is the GLB base name SaveGLB was given.
			var baseName = ResolveExportName(gltfRoot);
			var pages = new JArray();
			foreach (var fileName in _rgbmPages)
			{
				pages.Add(string.IsNullOrEmpty(baseName)
					? fileName
					: fileName.Replace(GLTFSceneExporter.SidecarNameToken, baseName));
			}

			GetOrCreateRootExtensionData(exporter, gltfRoot)[RgbmPagesKey] = pages;
		}

		/// <summary>
		/// Returns the root <see cref="IMMERSION_lightmaps"/> payload, creating (and declaring) the
		/// extension on first use. Both plugins write into the same object, so neither may call
		/// <c>GLTFRoot.AddExtension</c> directly — that throws when the extension already exists.
		/// </summary>
		public static JObject GetOrCreateRootExtensionData(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			if (gltfRoot.Extensions != null
				&& gltfRoot.Extensions.TryGetValue(IMMERSION_lightmaps.EXTENSION_NAME, out var existing)
				&& existing is IMMERSION_lightmaps lightmaps)
			{
				if (lightmaps.data == null) lightmaps.data = new JObject();
				if (lightmaps.data["version"] == null) lightmaps.data["version"] = 1;
				return lightmaps.data;
			}

			var data = new JObject { ["version"] = 1 };
			gltfRoot.AddExtension(IMMERSION_lightmaps.EXTENSION_NAME, new IMMERSION_lightmaps(data));
			exporter?.DeclareExtensionUsage(IMMERSION_lightmaps.EXTENSION_NAME, false);
			return data;
		}

		private static string ResolveExportName(GLTFRoot gltfRoot)
		{
			var scenes = gltfRoot.Scenes;
			if (scenes == null || scenes.Count == 0) return null;
			var index = gltfRoot.Scene != null ? gltfRoot.Scene.Id : 0;
			if (index < 0 || index >= scenes.Count) index = 0;
			return scenes[index]?.Name;
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

		private readonly bool _embedTextures;
		private readonly float _textureScale;
		private readonly int _maxTextureSize;
		private readonly List<LightmappedNode> _nodes = new List<LightmappedNode>();
		private readonly HashSet<int> _usedIndices = new HashSet<int>();
		// LightmapSettings.lightmaps returns a fresh array copy on every access, so cache it
		private LightmapData[] _lightmaps;
		private LightmapData[] Lightmaps => _lightmaps ?? (_lightmaps = LightmapSettings.lightmaps);

		public LightmapExportContext(ExportContext context, bool embedTextures, float textureScale, int maxTextureSize)
		{
			_embedTextures = embedTextures;
			_textureScale = textureScale;
			_maxTextureSize = maxTextureSize;
		}

		public override void BeforeSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			LightingExportUtils.ReleaseTexturesFromPreviousExports();
			// Also reset here (not just in the custom-data plugin) so a disabled custom-data plugin
			// can't leak a previous export's RGBM page list into this one.
			ImmersionLightmapPages.Reset();
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
				// lightmaps have their own resolution settings (plugin fields), independent of
				// the global texture scale/cap — baked lighting usually deserves the full bake
				var ldr = LightingExportUtils.DecodeLightmapToLDR(lightmaps[index].lightmapColor, baseName, _textureScale, _maxTextureSize);
				if (ldr == null) continue;

				// prefix with the export name so lightmaps from different scenes can coexist in
				// the same folder / asset store (e.g. "Bank_Lightmap-0.png")
				var fileBase = GLTFSceneExporter.SidecarNameToken + "_" + baseName;
				var fileName = fileBase + ".png";
				exporter.AddSidecarFile(fileName, ldr.EncodeToPNG());
				fileNames[index] = fileName;
				manifestLightmaps.Add(new JObject
				{
					// the editor matches colorName against uploaded file names (case-insensitive,
					// no extension); the {name} token is resolved when the manifest is written
					["index"] = index,
					["colorName"] = fileBase,
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
				Encoding.UTF8.GetBytes(manifest.ToString()), replaceTokenInContent: true);

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
			// Merge into the shared payload — the custom-data plugin may already have created the
			// extension for its "rgbmPages" list (plugin callback order is not defined).
			ImmersionLightmapPages.GetOrCreateRootExtensionData(exporter, gltfRoot)["lightmaps"] = lightmapsArr;

			// "rgbmPages": the external RGBM8 pages written by the Immersion custom-data plugin.
			// Called from both plugins because callback order isn't guaranteed; idempotent.
			ImmersionLightmapPages.ApplyToRoot(exporter, gltfRoot);
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
