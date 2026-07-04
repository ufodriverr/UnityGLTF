using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that captures Unity scene-level look settings — ambient lighting, fog and
	/// (optionally) the skybox — into a root-level <see cref="IMMERSION_scene_settings"/>
	/// extension.
	///
	/// Note: the Immersion web editor takes its environment from the reflection sidecar PNG (see
	/// <see cref="ReflectionProbeExport"/>, which also bakes the skybox as a fallback when the
	/// scene has no probes). Embedding the skybox as an equirectangular glTF texture is therefore
	/// off by default and only useful for other consumers.
	/// </summary>
	public class SceneSettingsExport : GLTFExportPlugin
	{
		[SerializeField]
		[Tooltip("Bake the skybox and embed it as an equirectangular glTF texture. Increases file size; the Immersion web editor doesn't read it (it uses the reflection sidecar PNG instead).")]
		private bool embedSkyboxInGlb = false;

		[SerializeField]
		[Tooltip("Cubemap face size used when baking the skybox for embedding. The equirectangular panorama is 4x this wide (e.g. 256 -> 1024x512).")]
		private int skyboxBakeFaceSize = 256;

		public override string DisplayName => "IMMERSION_scene_settings";

		public override string Description =>
			"Exports the scene's ambient lighting and fog settings (and optionally the skybox as " +
			"an embedded equirectangular texture), so a web renderer can reproduce the overall " +
			"scene look.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new SceneSettingsExportContext(context, embedSkyboxInGlb, skyboxBakeFaceSize);
		}
	}

	public class SceneSettingsExportContext : GLTFExportPluginContext
	{
		private readonly ExportContext _context;
		private readonly bool _embedSkybox;
		private readonly int _skyboxBakeFaceSize;

		public SceneSettingsExportContext(ExportContext context, bool embedSkybox, int skyboxBakeFaceSize)
		{
			_context = context;
			_embedSkybox = embedSkybox;
			_skyboxBakeFaceSize = Mathf.Clamp(Mathf.NextPowerOfTwo(skyboxBakeFaceSize), 16, 2048);
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

		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			var data = new JObject { ["version"] = 1 };

			if (_embedSkybox)
			{
				var skybox = ExportSkybox(exporter);
				if (skybox != null) data["skybox"] = skybox;
			}
			data["ambient"] = ExportAmbient();
			data["fog"] = ExportFog();
			data["reflectionIntensity"] = RenderSettings.reflectionIntensity;

			exporter.DeclareExtensionUsage(IMMERSION_scene_settings.EXTENSION_NAME, false);
			gltfRoot.AddExtension(IMMERSION_scene_settings.EXTENSION_NAME, new IMMERSION_scene_settings(data));
		}

		private JObject ExportSkybox(GLTFSceneExporter exporter)
		{
			// Camera.RenderToCubemap uses the built-in render path; under URP/HDRP it can succeed
			// but produce empty faces. Warn so a black skybox in the output is explainable.
			if (RenderSettings.skybox && GraphicsSettings.currentRenderPipeline != null)
				Debug.LogWarning("UnityGLTF: skybox bake uses Camera.RenderToCubemap, which is not fully supported on scriptable render pipelines (URP/HDRP). Verify the exported skybox, or disable 'Embed Skybox In Glb' on the IMMERSION_scene_settings plugin.");

			var cubeRT = LightingExportUtils.BakeSkyboxToCubemap(_skyboxBakeFaceSize);
			if (cubeRT == null) return null;

			try
			{
				// the bake target is a raw linear HDR render texture, so no RGBM/HDR decode needed;
				// the global texture scale/cap is applied here (the exporter-side scaling is
				// disabled for lighting textures in BeforeTextureExport)
				var width = LightingExportUtils.ScaledSize(_skyboxBakeFaceSize * 4, _skyboxBakeFaceSize * 2, _context.settings).x;
				var equirect = LightingExportUtils.CubemapToEquirect(cubeRT, new Vector4(1, 1, 0, 0), width, "Skybox");
				if (equirect == null) return null;
				var id = exporter.ExportTexture(equirect, GLTFSceneExporter.TextureMapType.sRGB, LightingExportUtils.PngExportSettings);
				if (id == null) return null;

				return new JObject { ["texture"] = id.Id };
			}
			finally
			{
				cubeRT.Release();
				Object.DestroyImmediate(cubeRT);
			}
		}

		private static JObject ExportAmbient()
		{
			var ambient = new JObject
			{
				["mode"] = RenderSettings.ambientMode.ToString(),
				["intensity"] = RenderSettings.ambientIntensity,
				["color"] = ToJson(RenderSettings.ambientLight),
			};
			if (RenderSettings.ambientMode == AmbientMode.Trilight)
			{
				ambient["skyColor"] = ToJson(RenderSettings.ambientSkyColor);
				ambient["equatorColor"] = ToJson(RenderSettings.ambientEquatorColor);
				ambient["groundColor"] = ToJson(RenderSettings.ambientGroundColor);
			}
			return ambient;
		}

		private static JObject ExportFog()
		{
			return new JObject
			{
				["enabled"] = RenderSettings.fog,
				["mode"] = RenderSettings.fogMode.ToString(),
				["color"] = ToJson(RenderSettings.fogColor),
				["density"] = RenderSettings.fogDensity,
				["startDistance"] = RenderSettings.fogStartDistance,
				["endDistance"] = RenderSettings.fogEndDistance,
			};
		}

		private static JArray ToJson(Color color)
		{
			return new JArray(color.r, color.g, color.b);
		}
	}
}
