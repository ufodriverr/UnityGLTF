using UnityGLTF;
using UnityGLTF.Plugins;

namespace Immersion.Export
{
	/// <summary>
	/// The IMMERSION export defaults that live on plugin instances rather than on
	/// <see cref="GLTFSettings"/> itself. Batch exports build their settings with
	/// <c>GLTFSettings.GetDefaultSettings()</c> (the persisted settings asset can lose its plugin
	/// sub-assets in batch mode), so every value that must hold for a web export is applied here
	/// explicitly instead of relying on what a project's settings asset happens to contain.
	/// </summary>
	public static class ImmersionExportSettings
	{
		/// <param name="settings">Fresh or persisted settings; plugin instances are edited in place.</param>
		/// <param name="isObjectExport">
		/// True for a single-object export (an avatar prefab), false for a whole scene. Object
		/// exports get no skybox-fallback reflection strip: the empty export scene's procedural
		/// sky is not part of the object.
		/// </param>
		public static void ApplyDefaults(GLTFSettings settings, bool isObjectExport)
		{
			foreach (var plugin in settings.ExportPlugins)
			{
				switch (plugin)
				{
					// URP transparent materials with "preserve specular" would otherwise export
					// transmissionFactor = 1 - alpha and render as invisible glass on the web.
					case MaterialExtensionsExport materialExtensions:
						materialExtensions.KHR_materials_transmission = false;
						break;
					case ReflectionProbeExport reflectionProbes:
						reflectionProbes.skyboxFallback = !isObjectExport;
						break;
				}
			}
		}
	}
}
