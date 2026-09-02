using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
	/// <summary>
	/// Root-level glTF extension that lists the baked Unity lightmaps exported with the asset.
	/// Each entry maps a Unity lightmap index to the loose page file that carries its pixels.
	///
	/// Payload shape (version 2):
	/// {
	///   "version": 2,
	///   "lightmaps": [ { "lightmapIndex": 0, "image": "Bank_Lightmap-0_RGBM8.png", "texture": 3 }, ... ]
	/// }
	///
	/// <c>image</c> is the RESOLVED file name (the <c>{name}</c> sidecar token is already
	/// substituted) of the page's lossless RGBM8 sidecar PNG — decode <c>hdr = rgb * a * 5</c> in
	/// linear space; the <c>_RGBM8</c> suffix is how consumers recognise the encoding. Entry order
	/// follows <c>lightmapIndex</c>, which is also the nodes' <c>extras.customData.lm_index</c>.
	/// By default the GLB only carries a 4x4 black placeholder page per lightmap (see
	/// <c>GltfCustomDataExporter</c>), so a consumer that ignores the sidecars renders unlit.
	/// <c>texture</c> is optional and only present when the IMMERSION_lightmaps plugin's
	/// "Embed Textures In Glb" toggle added a clamped LDR copy for non-Immersion consumers.
	///
	/// Version 1 (pre-2026-09) instead pointed <c>image</c> at a tone-curve LDR sidecar
	/// (<c>Bank_Lightmap-0.png</c>, in unresolved <c>{name}</c> token form) and listed the RGBM8
	/// pages in a separate <c>rgbmPages</c> array. That LDR sidecar is no longer written.
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
	///   "image": "Bank_Lightmap-0_RGBM8.png", // resolved page file name, as in the root list
	///   "texture": 3,                // optional: embedded LDR copy, only with Embed Textures In Glb
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
