using System.Collections.Generic;
using System.Linq;
using System.Text;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that describes the scene's baked lightmaps alongside the glTF, in the format
	/// the Immersion web editor consumes:
	///
	/// - a <c>&lt;exportName&gt;_lightmap_offsets.json</c> manifest with the editor's schema:
	///   <c>lightmaps: [{ index, colorName }]</c> and
	///   <c>renderers: [{ path, lightmapIndex, tilingX, tilingY, offsetX, offsetY }]</c>
	///   (raw Unity Renderer.lightmapScaleOffset values; the editor applies the V-flip itself),
	/// - node-level <see cref="IMMERSION_lightmap"/> and root-level
	///   <see cref="IMMERSION_lightmaps"/> extensions with the same information inside the glTF.
	///
	/// The lightmap PIXELS are NOT written here: a scene ships exactly ONE file per lightmap page,
	/// the lossless RGBM8 sidecar <c>&lt;exportName&gt;_Lightmap-&lt;i&gt;_RGBM8.png</c> written by
	/// the Immersion custom-data plugin (see <see cref="ImmersionLightmapPages"/>) — that is the
	/// page the <c>Immersion/Web/*</c> shaders sample and the one this plugin's extensions name.
	/// The legacy tone-curve LDR sidecar (<c>&lt;exportName&gt;_Lightmap-&lt;i&gt;.png</c>) is no
	/// longer written; with <see cref="embedTexturesInGlb"/> enabled that clamped LDR decode is
	/// still available as a regular embedded glTF texture for non-Immersion consumers.
	/// </summary>
	public class LightmapExport : GLTFExportPlugin
	{
		[SerializeField]
		[Range(0.01f, 1f)]
		[Tooltip("Resolution scale for the LDR lightmap copies embedded by 'Embed Textures In Glb' (1 = full bake resolution). Only used when that toggle is on - the RGBM8 lightmap sidecars are always full bake resolution.")]
		private float lightmapTextureScale = 1f;

		[SerializeField]
		[Tooltip("Optional hard cap on the largest embedded lightmap dimension, in pixels. 0 = no cap. Only used with 'Embed Textures In Glb'; the RGBM8 sidecars are never capped.")]
		private int lightmapMaxTextureSize = 0;

		[SerializeField]
		[Tooltip("Also embed a clamped LDR copy of every lightmap as a regular glTF texture, for non-Immersion consumers. Increases file size; the Immersion web editor and runtime read the '<name>_Lightmap-<i>_RGBM8.png' sidecars instead.")]
		private bool embedTexturesInGlb = false;

		public override string DisplayName => "IMMERSION_lightmaps";

		public override string Description =>
			"Writes the <name>_lightmap_offsets.json manifest (Immersion web editor format) next " +
			"to the exported file and adds the lightmap info extensions to the glTF; the page " +
			"pixels ship as the RGBM8 sidecars of the Gltf Custom Shaders Export plugin.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new LightmapExportContext(context, embedTexturesInGlb, lightmapTextureScale, lightmapMaxTextureSize);
		}
	}

	/// <summary>
	/// Naming + root-extension helpers shared by the two plugins that describe the lightmap pages.
	///
	/// A scene exports exactly ONE file per lightmap page: the lossless RGBM8 sidecar
	/// <c>&lt;name&gt;_Lightmap-&lt;i&gt;_RGBM8.png</c> (decode <c>hdr = rgb * a * 5</c>, linear),
	/// written by the Immersion custom-data export plugin (<c>GltfCustomDataExporter</c>, Editor
	/// assembly) while the file NAMES are declared by <see cref="LightmapExportContext"/> in the
	/// <see cref="IMMERSION_lightmaps"/> / <see cref="IMMERSION_lightmap"/> extensions. Plugin
	/// callbacks fire in the order of <c>GLTFSettings.ExportPlugins</c>, so neither plugin can
	/// assume it runs first — hence the name is DERIVED, not handed over
	/// (<see cref="PageFileName"/>), and both sides compute the same string.
	/// </summary>
	public static class ImmersionLightmapPages
	{
		/// <summary>
		/// Version of the root <see cref="IMMERSION_lightmaps"/> payload.
		/// 1 = <c>lightmaps[].image</c> was the tone-curve LDR sidecar (token form) and the RGBM8
		/// pages were listed separately in <c>rgbmPages</c>;
		/// 2 = <c>lightmaps[].image</c> IS the RGBM8 page, as a resolved file name, and there is
		/// no <c>rgbmPages</c> array any more.
		/// </summary>
		public const int Version = 2;

		/// <summary>Suffix (including the extension) of every RGBM8 lightmap page sidecar.</summary>
		public const string PageFileSuffix = "_RGBM8.png";

		/// <summary>One registered lightmap page sidecar.</summary>
		public struct Page
		{
			/// <summary>Unity lightmap page index (<c>lm_index</c> / <c>lightmapIndex</c>).</summary>
			public int Index;
			/// <summary>Sidecar file name in <see cref="GLTFSceneExporter.SidecarNameToken"/> form.</summary>
			public string FileName;
		}

		private static readonly List<Page> _pages = new List<Page>();

		/// <summary>RGBM8 sidecars actually written by the running/last export (for logging).</summary>
		public static IReadOnlyList<Page> Pages => _pages;

		/// <summary>
		/// Sidecar file name of one lightmap page, in <see cref="GLTFSceneExporter.SidecarNameToken"/>
		/// form (<c>{name}_Lightmap-&lt;i&gt;_RGBM8.png</c>). The <c>_RGBM8</c> suffix is part of the
		/// runtime contract: the web runtime detects RGBM8 pages BY FILE NAME
		/// (<c>/_?rgbm8?(\.|$)/i</c>) — a page without it is treated as a legacy LDR page.
		/// </summary>
		public static string PageFileName(int lightmapIndex)
		{
			return GLTFSceneExporter.SidecarNameToken + "_Lightmap-" + lightmapIndex + PageFileSuffix;
		}

		/// <summary>Drops the previous export's page list. Call from BeforeSceneExport.</summary>
		public static void Reset()
		{
			_pages.Clear();
		}

		/// <summary>Records a written RGBM8 sidecar (token-form file name) for a lightmap page.</summary>
		public static void RegisterPage(int lightmapIndex, string tokenFileName)
		{
			if (string.IsNullOrEmpty(tokenFileName)) return;
			_pages.Add(new Page { Index = lightmapIndex, FileName = tokenFileName });
		}

		/// <summary>
		/// Resolves <see cref="GLTFSceneExporter.SidecarNameToken"/> in a sidecar file name so it can
		/// go into a glTF extension payload: the glTF JSON is NOT run through the token replacement
		/// (only sidecar file names and text sidecars are). Uses the exporter's own sidecar base name
		/// and falls back to the exported scene name (equal to it for file exports).
		/// </summary>
		public static string ResolveName(GLTFSceneExporter exporter, GLTFRoot gltfRoot, string tokenFileName)
		{
			if (string.IsNullOrEmpty(tokenFileName)) return tokenFileName;
			var baseName = exporter != null ? exporter.SidecarBaseName : null;
			if (string.IsNullOrEmpty(baseName)) baseName = ResolveExportName(gltfRoot);
			return string.IsNullOrEmpty(baseName)
				? tokenFileName
				: tokenFileName.Replace(GLTFSceneExporter.SidecarNameToken, baseName);
		}

		/// <summary>
		/// Fallback for exports that run WITHOUT the <see cref="LightmapExport"/> plugin (it owns the
		/// root <c>lightmaps</c> array): declares the pages the custom-data plugin wrote, so the
		/// sidecars stay discoverable. No-op as soon as something declared a non-empty list, so the
		/// plugin's own array — which only lists pages actually used by exported renderers — wins,
		/// whichever plugin runs first.
		/// </summary>
		public static void EnsurePagesDeclared(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			if (gltfRoot == null || _pages.Count == 0) return;

			var data = GetOrCreateRootExtensionData(exporter, gltfRoot);
			if (data["lightmaps"] is JArray existing && existing.Count > 0) return;

			var lightmaps = new JArray();
			foreach (var page in _pages)
			{
				lightmaps.Add(new JObject
				{
					["lightmapIndex"] = page.Index,
					["image"] = ResolveName(exporter, gltfRoot, page.FileName),
				});
			}

			data["lightmaps"] = lightmaps;
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
				lightmaps.data["version"] = Version;
				return lightmaps.data;
			}

			var data = new JObject { ["version"] = Version };
			gltfRoot.AddExtension(IMMERSION_lightmaps.EXTENSION_NAME, new IMMERSION_lightmaps(data));
			exporter?.DeclareExtensionUsage(IMMERSION_lightmaps.EXTENSION_NAME, false);
			return data;
		}

		private static string ResolveExportName(GLTFRoot gltfRoot)
		{
			var scenes = gltfRoot?.Scenes;
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
			var fileNames = new Dictionary<int, string>(); // lightmapIndex -> RGBM8 page file name (resolved)
			var lightmapsArr = new JArray();               // for the glTF root extension
			var manifestLightmaps = new JArray();          // for the sidecar offsets JSON

			foreach (var index in _usedIndices.OrderBy(i => i))
			{
				var baseName = $"Lightmap-{index}";
				// The one and only page file: the lossless RGBM8 sidecar written by the Immersion
				// custom-data plugin. Its name is derived (not handed over) because plugin callback
				// order is undefined; extension payloads carry it RESOLVED, since the glTF JSON is
				// never run through the sidecar token replacement.
				var fileName = ImmersionLightmapPages.ResolveName(
					exporter, gltfRoot, ImmersionLightmapPages.PageFileName(index));
				fileNames[index] = fileName;
				manifestLightmaps.Add(new JObject
				{
					// the editor matches colorName against uploaded file names (case-insensitively,
					// without extension: exact first, then prefix — so "<name>_Lightmap-<i>" still
					// resolves to "<name>_Lightmap-<i>_RGBM8.png"). Unchanged on purpose: existing
					// projects/uploads keep matching. The {name} token is resolved on write.
					["index"] = index,
					["colorName"] = GLTFSceneExporter.SidecarNameToken + "_" + baseName,
				});

				var entry = new JObject { ["lightmapIndex"] = index, ["image"] = fileName };
				if (_embedTextures)
				{
					// Optional convenience copy for non-Immersion consumers only: the clamped LDR
					// decode (Photopea curve, see UnityGLTFLightmapDecode.shader), scaled/capped by
					// this plugin's own settings. Nothing in the Immersion pipeline reads it.
					var ldr = LightingExportUtils.DecodeLightmapToLDR(lightmaps[index].lightmapColor, baseName, _textureScale, _maxTextureSize);
					var id = ldr == null
						? null
						: exporter.ExportTexture(ldr, GLTFSceneExporter.TextureMapType.sRGB, LightingExportUtils.PngExportSettings);
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
			// extension (plugin callback order is not defined). This list wins over the fallback
			// one that plugin declares: it is scoped to the pages exported renderers actually use.
			ImmersionLightmapPages.GetOrCreateRootExtensionData(exporter, gltfRoot)["lightmaps"] = lightmapsArr;
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
