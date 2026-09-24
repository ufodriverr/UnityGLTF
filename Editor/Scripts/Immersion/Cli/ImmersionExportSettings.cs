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
		/// <param name="maxTextureSize">
		/// Texture cap of the Revolution plugin (<see cref="GltfCustomData"/>): 0 = unlimited (the
		/// default — web exports ship full resolution and are sized in the Editor's Texture Tools).
		/// </param>
		/// <param name="maxLightmapSize">Lightmap page cap (HDR resample before the RGBM encode), 0 = unlimited.</param>
		public static void ApplyDefaults(GLTFSettings settings, bool isObjectExport, int maxTextureSize = 0, int maxLightmapSize = 0)
		{
			// The static overrides win over the plugin fields; a previous caller must not leak into
			// this export.
			GltfCustomData.MaxTextureSizeOverride = -1;
			GltfCustomData.MaxLightmapSizeOverride = -1;

			// Lossless PNG for every texture. The JPEG heuristic keys off "does the texture have
			// alpha", which UnityGLTF answers from the IMPORTED format of the active build target
			// (Standalone DXT1 → JPEG, another target's alpha format → PNG) — the same material map
			// came out JPEG here and PNG in the art team's export. Web assets are re-encoded
			// (WebP, sized) afterwards in the Editor's Texture Tools, so the export stays lossless.
			settings.UseTextureFileTypeHeuristic = false;
			// UnityGLTF's persistent image-bytes cache (Temp/UnityGLTF) keys on the texture, NOT on
			// the encode decision above — a JPEG cached by an earlier export was served again after
			// switching to PNG. Batch exports always encode fresh.
			settings.UseCaching = false;

			foreach (var plugin in settings.ExportPlugins)
			{
				switch (plugin)
				{
					case GltfCustomData customData:
						customData.MaxTextureSize = maxTextureSize;
						customData.MaxLightmapSize = maxLightmapSize;
						break;
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
