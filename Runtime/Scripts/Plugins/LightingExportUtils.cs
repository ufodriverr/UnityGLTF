using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Shared helpers for the IMMERSION lighting export plugins (lightmaps, reflection probes,
	/// skybox). Converts Unity's HDR lighting textures to plain LDR sRGB textures so they can be
	/// exported as standard PNGs through the regular texture pipeline — which also means the
	/// global ExportTextureScale / ExportMaxTextureSize settings apply to them.
	///
	/// Note: values above 1.0 (very bright light) are clamped, same as manually converting a
	/// Unity lightmap EXR to PNG in an image editor.
	/// </summary>
	internal static class LightingExportUtils
	{
		private static Material _lightmapDecodeMaterial;
		private static Material _cubemapToEquirectMaterial;

		// The exporter holds references to these textures until the output file is written, and
		// there is no plugin callback after that point — so they're kept alive here and destroyed
		// at the start of the next export (see ReleaseTexturesFromPreviousExports).
		private static readonly List<Texture2D> _exportedTextures = new List<Texture2D>();

		/// <summary>Destroys the temporary LDR textures created during previous exports.</summary>
		public static void ReleaseTexturesFromPreviousExports()
		{
			foreach (var tex in _exportedTextures)
			{
				if (!tex) continue;
				if (Application.isEditor) Object.DestroyImmediate(tex);
				else Object.Destroy(tex);
			}
			_exportedTextures.Clear();
		}

		private static Material LoadBlitMaterial(string shaderName)
		{
			var shader = Resources.Load<Shader>(shaderName);
			if (!shader)
			{
				Debug.LogError($"UnityGLTF: shader '{shaderName}' not found in Resources; lighting texture export will fail.");
				return null;
			}
			return new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
		}

		private static Material LightmapDecodeMaterial
		{
			get
			{
				if (!_lightmapDecodeMaterial)
					_lightmapDecodeMaterial = LoadBlitMaterial("UnityGLTFLightmapDecode");
				return _lightmapDecodeMaterial;
			}
		}

		private static Material CubemapToEquirectMaterial
		{
			get
			{
				if (!_cubemapToEquirectMaterial)
					_cubemapToEquirectMaterial = LoadBlitMaterial("UnityGLTFCubemapToEquirect");
				return _cubemapToEquirectMaterial;
			}
		}

		/// <summary>Texture export settings that force PNG output (alpha kept), sRGB, no channel conversion.</summary>
		public static GLTFSceneExporter.TextureExportSettings PngExportSettings
		{
			get
			{
				var settings = new GLTFSceneExporter.TextureExportSettings();
				settings.isValid = true;
				settings.alphaMode = GLTFSceneExporter.TextureExportSettings.AlphaMode.Always;
				settings.linear = false;
				settings.conversion = GLTFSceneExporter.TextureExportSettings.Conversion.None;
				return settings;
			}
		}

		/// <summary>
		/// Decodes a baked lightmap (raw HDR like BC6H/half-float, or RGBM-encoded) to a clamped
		/// LDR sRGB Texture2D, ready for PNG export.
		/// </summary>
		public static Texture2D DecodeLightmapToLDR(Texture2D lightmap, string name)
		{
			var mat = LightmapDecodeMaterial;
			if (!mat) return null;
			// Raw HDR formats (BC6H / float, "High Quality" encoding) already hold linear radiance.
			// LDR formats with alpha are RGBM ("Normal Quality"), without alpha dLDR ("Low Quality").
			// The decode instructions match Unity's DecodeLightmap for the active color space.
			var isRawHdr = GraphicsFormatUtility.IsHDRFormat(lightmap.graphicsFormat);
			var isRgbm = GraphicsFormatUtility.HasAlphaChannel(lightmap.graphicsFormat);
			var linearSpace = QualitySettings.activeColorSpace == ColorSpace.Linear;
			mat.SetFloat("_Mode", isRawHdr ? 0f : 1f);
			mat.SetVector("_Decode", isRgbm
				? (linearSpace ? new Vector4(34.493242f, 2.2f, 0f, 0f) : new Vector4(5f, 1f, 0f, 0f))   // pow(5, 2.2)
				: (linearSpace ? new Vector4(4.59479f, 1f, 0f, 0f) : new Vector4(2f, 1f, 0f, 0f)));     // pow(2, 2.2)
			return BlitToSRGBTexture(lightmap, lightmap.width, lightmap.height, mat, name, TextureWrapMode.Clamp);
		}

		/// <summary>
		/// Flattens a cubemap into an equirectangular (2:1) clamped LDR sRGB Texture2D, applying
		/// Unity's HDR decode instructions (pass <c>probe.textureHDRDecodeValues</c>, or
		/// <c>Vector4(1,1,0,0)</c> for raw linear render targets).
		/// </summary>
		public static Texture2D CubemapToEquirect(Texture cubemap, Vector4 hdrDecodeValues, int width, string name)
		{
			var mat = CubemapToEquirectMaterial;
			if (!mat) return null;
			mat.SetTexture("_CubeTex", cubemap);
			mat.SetVector("_Decode", hdrDecodeValues);
			mat.SetFloat("_UseDecode", 1f);
			// Clamp: Unity has a single wrap mode per texture, and vertical Repeat would bleed the
			// poles into each other under bilinear/PMREM filtering. The cost is a minor seam at
			// the anti-meridian.
			return BlitToSRGBTexture(Texture2D.whiteTexture, width, Mathf.Max(1, width / 2), mat, name, TextureWrapMode.Clamp);
		}

		private static Texture2D BlitToSRGBTexture(Texture source, int width, int height, Material material, string name, TextureWrapMode wrapMode)
		{
			var rt = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
			var prevActive = RenderTexture.active;
			var prevSRGB = GL.sRGBWrite;
			try
			{
				GL.sRGBWrite = true;
				Graphics.Blit(source, rt, material);

				var tex = new Texture2D(width, height, TextureFormat.ARGB32, false, false);
				tex.name = name;
				// The exporter keeps a reference to this texture until the file is written, so it
				// must stay alive; it gets destroyed at the start of the next export.
				tex.hideFlags = HideFlags.HideAndDontSave;
				tex.wrapMode = wrapMode;
				tex.filterMode = FilterMode.Bilinear;
				RenderTexture.active = rt;
				tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
				tex.Apply();

				_exportedTextures.Add(tex);
				return tex;
			}
			finally
			{
				RenderTexture.active = prevActive;
				GL.sRGBWrite = prevSRGB;
				RenderTexture.ReleaseTemporary(rt);
			}
		}
	}
}
