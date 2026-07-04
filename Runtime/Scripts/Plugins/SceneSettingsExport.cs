using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that captures Unity scene-level look settings — skybox, ambient lighting and
	/// fog — into a root-level <see cref="IMMERSION_scene_settings"/> extension.
	///
	/// The skybox is baked with a temporary camera to a cubemap and flattened to an
	/// equirectangular LDR PNG (through the regular texture pipeline, so ExportTextureScale /
	/// ExportMaxTextureSize apply). This works for any skybox type (6-sided, cubemap, procedural).
	/// In three.js the panorama can be assigned as scene.background / scene.environment via
	/// EquirectangularReflectionMapping.
	/// </summary>
	public class SceneSettingsExport : GLTFExportPlugin
	{
		[SerializeField]
		[Tooltip("Cubemap face size used when baking the skybox. The exported equirectangular panorama is 4x this wide (e.g. 256 -> 1024x512).")]
		private int skyboxBakeFaceSize = 256;

		public override string DisplayName => "IMMERSION_scene_settings";

		public override string Description =>
			"Exports the scene's skybox (as an equirectangular PNG panorama), ambient lighting and " +
			"fog settings, so a web renderer can reproduce the overall scene look.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new SceneSettingsExportContext(skyboxBakeFaceSize);
		}
	}

	public class SceneSettingsExportContext : GLTFExportPluginContext
	{
		private readonly int _skyboxBakeFaceSize;

		public SceneSettingsExportContext(int skyboxBakeFaceSize)
		{
			_skyboxBakeFaceSize = Mathf.Clamp(Mathf.NextPowerOfTwo(skyboxBakeFaceSize), 16, 2048);
		}

		public override void BeforeSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			LightingExportUtils.ReleaseTexturesFromPreviousExports();
		}

		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			var data = new JObject { ["version"] = 1 };

			var skybox = ExportSkybox(exporter);
			if (skybox != null) data["skybox"] = skybox;
			data["ambient"] = ExportAmbient();
			data["fog"] = ExportFog();
			data["reflectionIntensity"] = RenderSettings.reflectionIntensity;

			exporter.DeclareExtensionUsage(IMMERSION_scene_settings.EXTENSION_NAME, false);
			gltfRoot.AddExtension(IMMERSION_scene_settings.EXTENSION_NAME, new IMMERSION_scene_settings(data));
		}

		private JObject ExportSkybox(GLTFSceneExporter exporter)
		{
			if (!RenderSettings.skybox) return null;

			// Camera.RenderToCubemap uses the built-in render path; under URP/HDRP it can succeed
			// but produce empty faces. Warn so a black skybox in the output is explainable.
			if (GraphicsSettings.currentRenderPipeline != null)
				Debug.LogWarning("UnityGLTF: skybox bake uses Camera.RenderToCubemap, which is not fully supported on scriptable render pipelines (URP/HDRP). Verify the exported skybox, or disable the IMMERSION_scene_settings plugin.");

			var cubeRT = new RenderTexture(_skyboxBakeFaceSize, _skyboxBakeFaceSize, 16, RenderTextureFormat.ARGBHalf)
			{
				dimension = TextureDimension.Cube,
				hideFlags = HideFlags.HideAndDontSave,
			};

			var go = new GameObject("UnityGLTF Skybox Capture") { hideFlags = HideFlags.HideAndDontSave };
			try
			{
				var cam = go.AddComponent<Camera>();
				cam.enabled = false;
				cam.clearFlags = CameraClearFlags.Skybox;
				cam.cullingMask = 0; // skybox only, no scene geometry
				cam.allowHDR = true;
				cam.nearClipPlane = 0.1f;
				cam.farClipPlane = 100f;
				cam.transform.position = Vector3.zero;

				if (!cam.RenderToCubemap(cubeRT))
				{
					Debug.LogWarning("UnityGLTF: could not bake the skybox to a cubemap; skybox export skipped.");
					return null;
				}

				// the bake target is a raw linear HDR render texture, so no RGBM/HDR decode needed
				var equirect = LightingExportUtils.CubemapToEquirect(cubeRT, new Vector4(1, 1, 0, 0), _skyboxBakeFaceSize * 4, "Skybox");
				if (equirect == null) return null;
				var id = exporter.ExportTexture(equirect, GLTFSceneExporter.TextureMapType.sRGB, LightingExportUtils.PngExportSettings);
				if (id == null) return null;

				return new JObject { ["texture"] = id.Id };
			}
			finally
			{
				Object.DestroyImmediate(go);
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
