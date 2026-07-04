using System.Collections.Generic;
using System.Linq;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that exports ReflectionProbes alongside the glTF, in the format the
	/// Immersion web editor consumes: a single horizontal 6x1 cube-face atlas PNG
	/// (+X,-X,+Y,-Y,+Z,-Z, three.js CubeTexture orientation) named
	/// <c>&lt;exportName&gt;_reflection.png</c>, written next to the exported .glb/.gltf.
	///
	/// The editor applies one environment map per scene, so the "main" probe (highest importance,
	/// then largest volume) becomes the sidecar PNG. When the scene has no baked probes but does
	/// have a skybox, the skybox is baked into the reflection atlas instead, so the scene still
	/// gets an environment in the editor.
	///
	/// Every probe node also gets a <see cref="IMMERSION_reflection_probe"/> extension with the
	/// probe's metadata (box projection, center/size, intensity, blend distance). With
	/// <see cref="embedTexturesInGlb"/> enabled the atlases are also embedded as glTF textures.
	/// </summary>
	public class ReflectionProbeExport : GLTFExportPlugin
	{
		[SerializeField]
		[Tooltip("Maximum face size of the exported cube atlas, in pixels (atlas width = 6x this). The probe's own resolution is used when smaller.")]
		private int maxFaceSize = 512;

		[SerializeField]
		[Tooltip("Also embed the reflection atlases as glTF textures inside the exported file. Increases file size; the Immersion web editor only reads the sidecar PNG.")]
		private bool embedTexturesInGlb = false;

		public override string DisplayName => "IMMERSION_reflection_probes";

		public override string Description =>
			"Exports the main ReflectionProbe (or the skybox as fallback) as a 6x1 cube-face " +
			"atlas PNG next to the exported file (Immersion web editor format), plus probe " +
			"metadata extensions on the probe nodes.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new ReflectionProbeExportContext(context, maxFaceSize, embedTexturesInGlb);
		}
	}

	public class ReflectionProbeExportContext : GLTFExportPluginContext
	{
		private const string SidecarFileName = GLTFSceneExporter.SidecarNameToken + "_reflection.png";

		private readonly ExportContext _context;
		private readonly int _maxFaceSize;
		private readonly bool _embedTextures;
		private readonly List<(ReflectionProbe probe, Node node)> _probes = new List<(ReflectionProbe, Node)>();

		public ReflectionProbeExportContext(ExportContext context, int maxFaceSize, bool embedTextures)
		{
			_context = context;
			_maxFaceSize = Mathf.Max(16, maxFaceSize);
			_embedTextures = embedTextures;
		}

		public override void BeforeSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			LightingExportUtils.ReleaseTexturesFromPreviousExports();
		}

		public override void BeforeTextureExport(GLTFSceneExporter exporter, ref GLTFSceneExporter.UniqueTexture texture, string textureSlot)
		{
			// lighting textures are pre-scaled to the target resolution; scaling the atlas again
			// would break the editor's width-divisible-by-6 requirement
			if (LightingExportUtils.IsLightingTexture(texture.Texture))
			{
				texture.Scale = 1f;
				texture.MaxSize = 0;
			}
		}

		public override void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node)
		{
			if (transform.TryGetComponent<ReflectionProbe>(out var probe) && probe.enabled)
				_probes.Add((probe, node));
		}

		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			// the editor uses a single environment map; the most important probe wins the sidecar
			var mainProbe = _probes
				.Where(p => GetProbeTexture(p.probe))
				.OrderByDescending(p => p.probe.importance)
				.ThenByDescending(p => p.probe.size.x * p.probe.size.y * p.probe.size.z)
				.Select(p => p.probe)
				.FirstOrDefault();

			var exportedAny = false;
			foreach (var (probe, node) in _probes)
			{
				var cubemap = GetProbeTexture(probe);
				if (!cubemap)
				{
					Debug.LogWarning($"ReflectionProbe '{probe.name}' has no baked texture and was skipped. Bake lighting before exporting.", probe);
					continue;
				}

				// non-main probes only need their atlas when it gets embedded in the glTF
				Texture2D atlas = null;
				if (probe == mainProbe || _embedTextures)
				{
					atlas = LightingExportUtils.CubemapToFaceAtlas(cubemap, probe.textureHDRDecodeValues, GetFaceSize(cubemap), $"Reflection-{probe.name}");
					if (atlas == null) continue;
				}

				if (probe == mainProbe && atlas != null)
					exporter.AddSidecarFile(SidecarFileName, atlas.EncodeToPNG());

				var center = probe.center;
				var size = probe.size;
				var ext = new JObject
				{
					["layout"] = "cubeStrip", // 6x1 horizontal, +X,-X,+Y,-Y,+Z,-Z
					["main"] = probe == mainProbe,
					["boxProjection"] = probe.boxProjection,
					// X is mirrored between Unity and glTF, same conversion as mesh/node positions
					["center"] = new JArray(-center.x, center.y, center.z),
					["size"] = new JArray(size.x, size.y, size.z),
					["intensity"] = probe.intensity,
					["blendDistance"] = probe.blendDistance,
					["importance"] = probe.importance,
					["mode"] = probe.mode.ToString(),
				};
				if (probe == mainProbe)
					ext["image"] = SidecarFileName;
				if (_embedTextures && atlas != null)
				{
					var id = exporter.ExportTexture(atlas, GLTFSceneExporter.TextureMapType.sRGB, LightingExportUtils.PngExportSettings);
					if (id != null) ext["texture"] = id.Id;
				}

				node.AddExtension(IMMERSION_reflection_probe.EXTENSION_NAME, new IMMERSION_reflection_probe(ext));
				exportedAny = true;
			}

			if (exportedAny)
			{
				exporter.DeclareExtensionUsage(IMMERSION_reflection_probe.EXTENSION_NAME, false);
			}
			else
			{
				// no usable probes — bake the skybox into the reflection atlas instead, so the
				// scene still gets an environment map in the web editor
				var cubeRT = LightingExportUtils.BakeSkyboxToCubemap(Mathf.Min(256, _maxFaceSize));
				if (cubeRT != null)
				{
					try
					{
						var atlas = LightingExportUtils.CubemapToFaceAtlas(cubeRT, new Vector4(1, 1, 0, 0), GetFaceSize(cubeRT), "Reflection-Skybox");
						if (atlas != null)
							exporter.AddSidecarFile(SidecarFileName, atlas.EncodeToPNG());
					}
					finally
					{
						cubeRT.Release();
						Object.DestroyImmediate(cubeRT);
					}
				}
			}
		}

		private static Texture GetProbeTexture(ReflectionProbe probe)
		{
			return probe.texture ? probe.texture : probe.bakedTexture;
		}

		private int GetFaceSize(Texture cubemap)
		{
			var face = Mathf.Min(cubemap.width, _maxFaceSize);
			// respect the global texture scale/cap (applied per face; the exporter-side scaling is
			// disabled for these in BeforeTextureExport)
			return LightingExportUtils.ScaledSize(face, face, _context.settings).x;
		}
	}
}
