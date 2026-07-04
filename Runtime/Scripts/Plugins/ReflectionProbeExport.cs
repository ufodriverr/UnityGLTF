using System.Collections.Generic;
using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that includes ReflectionProbes in the exported glTF.
	///
	/// Each probe's cubemap is flattened to an equirectangular LDR PNG (through the regular
	/// texture pipeline, so ExportTextureScale / ExportMaxTextureSize apply) and referenced from
	/// a node-level <see cref="IMMERSION_reflection_probe"/> extension on the probe's node,
	/// together with box projection settings, intensity and blend distance. The panorama can be
	/// used directly with three.js EquirectangularReflectionMapping / PMREMGenerator.
	/// </summary>
	public class ReflectionProbeExport : GLTFExportPlugin
	{
		[SerializeField]
		[Tooltip("Maximum width of the exported equirectangular panorama per probe, in pixels. " +
		         "The natural size (4x the cubemap face size) is used when smaller.")]
		private int maxEquirectWidth = 2048;

		public override string DisplayName => "IMMERSION_reflection_probes";

		public override string Description =>
			"Exports ReflectionProbes as equirectangular PNG panoramas plus probe metadata " +
			"(box projection, intensity, blend distance) on the probe's node.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new ReflectionProbeExportContext(maxEquirectWidth);
		}
	}

	public class ReflectionProbeExportContext : GLTFExportPluginContext
	{
		private readonly int _maxEquirectWidth;
		private readonly List<(ReflectionProbe probe, Node node)> _probes = new List<(ReflectionProbe, Node)>();

		public ReflectionProbeExportContext(int maxEquirectWidth)
		{
			_maxEquirectWidth = Mathf.Max(64, maxEquirectWidth);
		}

		public override void BeforeSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			LightingExportUtils.ReleaseTexturesFromPreviousExports();
		}

		public override void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node)
		{
			if (transform.TryGetComponent<ReflectionProbe>(out var probe) && probe.enabled)
				_probes.Add((probe, node));
		}

		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			if (_probes.Count == 0) return;

			var exportedAny = false;
			foreach (var (probe, node) in _probes)
			{
				var cubemap = probe.texture ? probe.texture : probe.bakedTexture;
				if (!cubemap)
				{
					Debug.LogWarning($"ReflectionProbe '{probe.name}' has no baked texture and was skipped. Bake lighting before exporting.", probe);
					continue;
				}

				var width = Mathf.Min(cubemap.width * 4, _maxEquirectWidth);
				var equirect = LightingExportUtils.CubemapToEquirect(cubemap, probe.textureHDRDecodeValues, width, $"ReflectionProbe-{probe.name}");
				if (equirect == null) continue;
				var id = exporter.ExportTexture(equirect, GLTFSceneExporter.TextureMapType.sRGB, LightingExportUtils.PngExportSettings);
				if (id == null) continue;

				var center = probe.center;
				var size = probe.size;
				node.AddExtension(IMMERSION_reflection_probe.EXTENSION_NAME, new IMMERSION_reflection_probe(new JObject
				{
					["texture"] = id.Id,
					["boxProjection"] = probe.boxProjection,
					// X is mirrored between Unity and glTF, same conversion as mesh/node positions
					["center"] = new JArray(-center.x, center.y, center.z),
					["size"] = new JArray(size.x, size.y, size.z),
					["intensity"] = probe.intensity,
					["blendDistance"] = probe.blendDistance,
					["importance"] = probe.importance,
					["mode"] = probe.mode.ToString(),
				}));
				exportedAny = true;
			}

			if (exportedAny)
				exporter.DeclareExtensionUsage(IMMERSION_reflection_probe.EXTENSION_NAME, false);
		}
	}
}
