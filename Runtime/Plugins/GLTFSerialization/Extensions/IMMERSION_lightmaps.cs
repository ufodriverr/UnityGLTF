using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
	/// <summary>
	/// Root-level glTF extension that lists the baked Unity lightmaps exported with the asset.
	/// Each entry maps a Unity lightmap index to a glTF texture (a PNG that has already been
	/// decoded from Unity's HDR/RGBM lightmap encoding to plain LDR color).
	///
	/// Payload shape:
	/// {
	///   "version": 1,
	///   "lightmaps": [ { "lightmapIndex": 0, "image": "Bank_Lightmap-0.png", "texture": 3 }, ... ],
	///   "rgbmPages": [ "Bank_Lightmap-0_RGBM8.png", ... ]
	/// }
	///
	/// <c>lightmaps</c> holds the LDR (Photopea-curve) pages used by the vanilla-material path.
	/// <c>rgbmPages</c> lists the loose RGBM8 sidecar PNGs — full-precision lightmaps for the
	/// custom Immersion web shaders — in lightmap-page order, i.e. entry i belongs to the page the
	/// nodes' <c>extras.customData.lm_index == i</c> points at. By default the GLB only carries a
	/// 4x4 black placeholder for each of those pages (see <c>GltfCustomDataExporter</c>), so a
	/// consumer that ignores <c>rgbmPages</c> renders unlit.
	///
	/// Which mesh uses which lightmap (and with what UV tiling) is stored per node in
	/// <see cref="IMMERSION_lightmap"/>. Built by <c>LightmapExportContext</c> and
	/// <c>GltfCustomDataExporter</c> (both go through <c>ImmersionLightmapPages</c>).
	/// </summary>
	public class IMMERSION_lightmaps : IExtension
	{
		public const string EXTENSION_NAME = "IMMERSION_lightmaps";

		public JObject data;

		public IMMERSION_lightmaps(JObject data)
		{
			this.data = data ?? new JObject();
		}

		public JProperty Serialize()
		{
			return new JProperty(EXTENSION_NAME, data ?? new JObject());
		}

		public IExtension Clone(GLTFRoot root)
		{
			return new IMMERSION_lightmaps(data != null ? (JObject)data.DeepClone() : null);
		}
	}

	public class IMMERSION_lightmaps_Factory : ExtensionFactory
	{
		public IMMERSION_lightmaps_Factory()
		{
			ExtensionName = IMMERSION_lightmaps.EXTENSION_NAME;
		}

		public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
		{
			if (extensionToken?.Value is JObject obj)
				return new IMMERSION_lightmaps((JObject)obj.DeepClone());
			return null;
		}
	}

	/// <summary>
	/// Node-level glTF extension that connects a mesh node to one of the lightmaps listed in
	/// the root-level <see cref="IMMERSION_lightmaps"/> extension.
	///
	/// Payload shape:
	/// {
	///   "lightmapIndex": 0,          // Unity lightmap index (matches the root extension list)
	///   "texture": 3,                // glTF texture index of the lightmap PNG (for convenience)
	///   "scaleOffset": [sx,sy,ox,oy],     // Unity Renderer.lightmapScaleOffset, for Unity-style UV2
	///   "scaleOffsetGltf": [sx,sy,ox,oy]  // same tiling, pre-converted for the glTF TEXCOORD_1
	/// }
	///
	/// glTF flips V compared to Unity, so with a three.js/GLTFLoader setup (flipY = false) the
	/// lightmap sample coordinate is simply: uv = TEXCOORD_1 * scaleOffsetGltf.xy + scaleOffsetGltf.zw
	/// </summary>
	public class IMMERSION_lightmap : IExtension
	{
		public const string EXTENSION_NAME = "IMMERSION_lightmap";

		public JObject data;

		public IMMERSION_lightmap(JObject data)
		{
			this.data = data ?? new JObject();
		}

		public JProperty Serialize()
		{
			return new JProperty(EXTENSION_NAME, data ?? new JObject());
		}

		public IExtension Clone(GLTFRoot root)
		{
			return new IMMERSION_lightmap(data != null ? (JObject)data.DeepClone() : null);
		}
	}

	public class IMMERSION_lightmap_Factory : ExtensionFactory
	{
		public IMMERSION_lightmap_Factory()
		{
			ExtensionName = IMMERSION_lightmap.EXTENSION_NAME;
		}

		public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
		{
			if (extensionToken?.Value is JObject obj)
				return new IMMERSION_lightmap((JObject)obj.DeepClone());
			return null;
		}
	}
}
