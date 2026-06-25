using Newtonsoft.Json.Linq;

namespace GLTF.Schema
{
	/// <summary>
	/// Root-level glTF extension that stores Unity AnimatorController state-machine data
	/// (parameters, layers, states, transitions, conditions, blend trees and all timing values)
	/// alongside the baked glTF animations. This lets a runtime (e.g. three.js) reproduce the
	/// same animator behaviour that Unity has: what transitions to what, why (conditions),
	/// which parameters exist, and how fast (transition durations / exit times).
	///
	/// This is an export-only extension; the payload is built by <c>AnimatorControllerExportContext</c>
	/// and serialized verbatim here. A minimal deserializer is provided so the data round-trips
	/// (and isn't dropped) when a file is re-imported.
	/// </summary>
	public class IMMERSION_animator_controller : IExtension
	{
		public const string EXTENSION_NAME = "IMMERSION_animator_controller";

		/// <summary>The raw extension payload (object that contains the "controllers" array).</summary>
		public JObject data;

		public IMMERSION_animator_controller(JObject data)
		{
			this.data = data ?? new JObject();
		}

		public JProperty Serialize()
		{
			return new JProperty(EXTENSION_NAME, data ?? new JObject());
		}

		public IExtension Clone(GLTFRoot root)
		{
			return new IMMERSION_animator_controller(data != null ? (JObject)data.DeepClone() : null);
		}
	}

	public class IMMERSION_animator_controller_Factory : ExtensionFactory
	{
		public IMMERSION_animator_controller_Factory()
		{
			ExtensionName = IMMERSION_animator_controller.EXTENSION_NAME;
		}

		public override IExtension Deserialize(GLTFRoot root, JProperty extensionToken)
		{
			if (extensionToken?.Value is JObject obj)
				return new IMMERSION_animator_controller((JObject)obj.DeepClone());
			return null;
		}
	}
}
