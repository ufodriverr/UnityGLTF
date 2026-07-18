using GLTF.Schema;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that writes a caller-provided animator JSON block into the exported scene's
	/// <c>extras.IMMERSION_animator</c>.
	///
	/// This is the flattened, web-runtime-ready animator description (masks + controller in the
	/// Immersion editor's <c>AnimSettingsJson</c> shape) — distinct from
	/// <see cref="AnimatorControllerExport"/>, which dumps the full Unity state-machine graph as a
	/// root-level extension. three.js's GLTFLoader copies scene extras onto
	/// <c>gltf.scene.userData</c> automatically, so the web side picks
	/// <c>userData.IMMERSION_animator</c> up with zero loader changes.
	///
	/// The payload is deliberately NOT derived from the AnimatorController: flattening Unity's
	/// bool/int/exit-time graphs down to the web's trigger-only model is a per-avatar authoring
	/// decision. Callers (e.g. <c>Immersion.Export.AvatarBatchExporter</c>) set
	/// <see cref="PayloadJson"/> before running the export; when it is null or empty this plugin
	/// does nothing.
	/// </summary>
	public class AnimatorExtrasExport : GLTFExportPlugin
	{
		/// <summary>
		/// JSON string for the next export's <c>scenes[n].extras.IMMERSION_animator</c>.
		/// Expected shape: <c>{ "masks": {...}, "clips": {...}, "controller": {...} }</c>
		/// (any subset of the Immersion AnimSettingsJson keys). Set to null/empty to emit nothing.
		/// </summary>
		public static string PayloadJson;

		public override string DisplayName => "IMMERSION_animator (scene extras)";

		public override string Description =>
			"Writes a caller-provided flattened animator block (masks + trigger controller, Immersion " +
			"AnimSettingsJson shape) into the exported scene's extras.IMMERSION_animator, where the " +
			"Immersion web editor/runtime adopts it on load. No-op unless a payload is set " +
			"programmatically before export.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new AnimatorExtrasExportContext();
		}
	}

	public class AnimatorExtrasExportContext : GLTFExportPluginContext
	{
		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			var payload = AnimatorExtrasExport.PayloadJson;
			if (string.IsNullOrWhiteSpace(payload)) return;
			if (gltfRoot.Scenes == null || gltfRoot.Scenes.Count == 0)
			{
				Debug.LogWarning("AnimatorExtrasExport: no scene in the exported glTF, IMMERSION_animator not written.");
				return;
			}

			JObject data;
			try
			{
				data = JObject.Parse(payload);
			}
			catch (System.Exception e)
			{
				Debug.LogError("AnimatorExtrasExport: PayloadJson is not valid JSON, IMMERSION_animator not written. " + e.Message);
				return;
			}

			var scene = gltfRoot.Scene != null ? gltfRoot.Scene.Value : gltfRoot.Scenes[0];
			if (scene == null) scene = gltfRoot.Scenes[0];

			if (!(scene.Extras is JObject extras))
			{
				extras = new JObject();
				scene.Extras = extras;
			}
			extras["IMMERSION_animator"] = data;

			Debug.Log("AnimatorExtrasExport: wrote scenes.extras.IMMERSION_animator (" + payload.Length + " chars).");
		}
	}
}
