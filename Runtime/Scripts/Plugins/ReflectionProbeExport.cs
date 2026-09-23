using System.Collections.Generic;
using System.Linq;
using GLTF.Schema;
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
	/// The per-renderer probe binding and the per-probe equirect the Revolution shaders use come
	/// from the Gltf Custom Shaders Export plugin (<c>extras.customData</c>), not from here.
	/// </summary>
	public class ReflectionProbeExport : GLTFExportPlugin
	{
		[SerializeField]
		[Tooltip("Maximum face size of the exported cube atlas, in pixels (atlas width = 6x this). The probe's own resolution is used when smaller.")]
		private int maxFaceSize = 512;

		/// <summary>
		/// Write the skybox into <c>&lt;name&gt;_reflection.png</c> when the export has no usable
		/// baked probe. Scene exports want this; object exports (avatars) do not — the
		/// <c>Immersion.Export.AvatarBatchExporter</c> turns it off so an avatar GLB is not
		/// accompanied by a strip of the empty scene's procedural sky.
		/// </summary>
		public bool skyboxFallback = true;

		public override string DisplayName => "IMMERSION_reflection_probes";

		public override string Description =>
			"Exports the main ReflectionProbe (or the skybox as fallback) as a 6x1 cube-face " +
			"atlas PNG next to the exported file (Immersion web editor format).";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new ReflectionProbeExportContext(maxFaceSize, skyboxFallback);
		}
	}

	public class ReflectionProbeExportContext : GLTFExportPluginContext
	{
		private const string SidecarFileName = GLTFSceneExporter.SidecarNameToken + "_reflection.png";

		private readonly int _maxFaceSize;
		private readonly bool _skyboxFallback;
		private readonly List<ReflectionProbe> _probes = new List<ReflectionProbe>();

		public ReflectionProbeExportContext(int maxFaceSize, bool skyboxFallback)
		{
			_maxFaceSize = Mathf.Max(16, maxFaceSize);
			_skyboxFallback = skyboxFallback;
		}

		public override void BeforeSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			LightingExportUtils.ReleaseTexturesFromPreviousExports();
		}

		public override void AfterNodeExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot, Transform transform, Node node)
		{
			if (transform.TryGetComponent<ReflectionProbe>(out var probe) && probe.enabled)
				_probes.Add(probe);
		}

		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			foreach (var probe in _probes)
			{
				if (!GetProbeTexture(probe))
					Debug.LogWarning($"ReflectionProbe '{probe.name}' has no baked texture and was skipped. Bake lighting before exporting.", probe);
			}

			// the editor uses a single environment map; the most important probe wins the sidecar
			var mainProbe = _probes
				.Where(p => GetProbeTexture(p))
				.OrderByDescending(p => p.importance)
				.ThenByDescending(p => p.size.x * p.size.y * p.size.z)
				.FirstOrDefault();

			if (mainProbe != null)
			{
				var cubemap = GetProbeTexture(mainProbe);
				var atlas = LightingExportUtils.CubemapToFaceAtlas(cubemap, mainProbe.textureHDRDecodeValues, GetFaceSize(cubemap), $"Reflection-{mainProbe.name}");
				if (atlas != null)
					exporter.AddSidecarFile(SidecarFileName, atlas.EncodeToPNG());
			}
			else if (_skyboxFallback)
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
			return Mathf.Min(cubemap.width, _maxFaceSize);
		}
	}
}
