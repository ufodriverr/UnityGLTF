using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
	/// <summary>
	/// Root-level glTF extension carrying Unity scene-level look settings that glTF has no
	/// standard place for: the skybox (baked to an equirectangular LDR PNG), ambient lighting
	/// and fog. Lets a three.js runtime reproduce the overall scene look.
	///
	/// Payload shape:
	/// {
	///   "version": 1,
	///   "skybox": { "texture": 7 },                     // equirectangular panorama
	///   "ambient": { "mode": "Skybox", "intensity": 1.0,
	///                "color": [r,g,b], "skyColor": [...], "equatorColor": [...], "groundColor": [...] },
	///   "fog": { "enabled": true, "mode": "ExponentialSquared", "color": [r,g,b],
	///            "density": 0.01, "startDistance": 0, "endDistance": 300 },
	///   "reflectionIntensity": 1.0
	/// }
	///
	/// Colors are the raw Unity inspector values (sRGB). Built by <c>SceneSettingsExportContext</c>.
	/// </summary>
	public class IMMERSION_scene_settings : IExtension
	{
		public const string EXTENSION_NAME = "IMMERSION_scene_settings";

		public JObject data;

		public IMMERSION_scene_settings(JObject data)
		{
			this.data = data ?? new JObject();
		}

		public JProperty Serialize()
		{
			return new JProperty(EXTENSION_NAME, data ?? new JObject());
		}

		public IExtension Clone(GLTFRoot root)
		{
			return new IMMERSION_scene_settings(data != null ? (JObject)data.DeepClone() : null);
		}
	}

	public class IMMERSION_scene_settings_Factory : ExtensionFactory
	{
		public IMMERSION_scene_settings_Factory()
		{
			ExtensionName = IMMERSION_scene_settings.EXTENSION_NAME;
		}

		public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
		{
			if (extensionToken?.Value is JObject obj)
				return new IMMERSION_scene_settings((JObject)obj.DeepClone());
			return null;
		}
	}
}
