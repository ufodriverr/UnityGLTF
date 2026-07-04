using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
	/// <summary>
	/// Node-level glTF extension describing a Unity ReflectionProbe. The probe's cubemap is
	/// exported as an equirectangular LDR PNG (a glTF texture) that can be fed directly into
	/// three.js (EquirectangularReflectionMapping / PMREMGenerator). The probe's position comes
	/// from the node transform this extension is attached to.
	///
	/// Payload shape:
	/// {
	///   "texture": 5,                 // glTF texture index of the equirectangular panorama
	///   "boxProjection": true,
	///   "center": [x,y,z],            // box center, local to the node, glTF coordinate space
	///   "size": [x,y,z],              // box size
	///   "intensity": 1.0,
	///   "blendDistance": 1.0,
	///   "importance": 1,
	///   "mode": "Baked"               // Baked | Realtime | Custom
	/// }
	///
	/// Built by <c>ReflectionProbeExportContext</c>.
	/// </summary>
	public class IMMERSION_reflection_probe : IExtension
	{
		public const string EXTENSION_NAME = "IMMERSION_reflection_probe";

		public JObject data;

		public IMMERSION_reflection_probe(JObject data)
		{
			this.data = data ?? new JObject();
		}

		public JProperty Serialize()
		{
			return new JProperty(EXTENSION_NAME, data ?? new JObject());
		}

		public IExtension Clone(GLTFRoot root)
		{
			return new IMMERSION_reflection_probe(data != null ? (JObject)data.DeepClone() : null);
		}
	}

	public class IMMERSION_reflection_probe_Factory : ExtensionFactory
	{
		public IMMERSION_reflection_probe_Factory()
		{
			ExtensionName = IMMERSION_reflection_probe.EXTENSION_NAME;
		}

		public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
		{
			if (extensionToken?.Value is JObject obj)
				return new IMMERSION_reflection_probe((JObject)obj.DeepClone());
			return null;
		}
	}
}
