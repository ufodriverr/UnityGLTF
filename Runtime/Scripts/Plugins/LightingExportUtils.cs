using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

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
		private static readonly HashSet<Texture2D> _exportedTextures = new HashSet<Texture2D>();

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

		private static Material _cubemapToFacesMaterial;
		private static Material CubemapToFacesMaterial
		{
			get
			{
				if (!_cubemapToFacesMaterial)
					_cubemapToFacesMaterial = LoadBlitMaterial("UnityGLTFCubemapToFaces");
				return _cubemapToFacesMaterial;
			}
		}

		/// <summary>True for textures created by these helpers during the current export.</summary>
		public static bool IsLightingTexture(Texture texture)
		{
			return texture is Texture2D tex2D && _exportedTextures.Contains(tex2D);
		}

		/// <summary>
		/// Applies the global ExportTextureScale / ExportMaxTextureSize to a dimension pair, same
		/// math as UniqueTexture.ScaledDimension. Lighting textures are pre-scaled with this (and
		/// then excluded from the exporter's own scaling), so the sidecar PNGs match the GLB.
		/// </summary>
		public static Vector2Int ScaledSize(int width, int height, GLTFSettings settings)
		{
			return ScaledSize(width, height, settings.ExportTextureScale, settings.ExportMaxTextureSize);
		}

		/// <summary>Same scaling math with explicit factors (scale 0.01-1, maxSize 0 = no cap).</summary>
		public static Vector2Int ScaledSize(int width, int height, float scale, int maxSize)
		{
			var factor = Mathf.Clamp(scale, 0.01f, 1f);
			var maxDimension = Mathf.Max(width, height);
			if (maxSize > 0 && maxDimension * factor > maxSize)
				factor = maxSize / (float)maxDimension;
			return new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(width * factor)), Mathf.Max(1, Mathf.RoundToInt(height * factor)));
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
		public static Texture2D DecodeLightmapToLDR(Texture2D lightmap, string name, float scale, int maxSize)
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
			var size = ScaledSize(lightmap.width, lightmap.height, scale, maxSize);
			return BlitToSRGBTexture(lightmap, size.x, size.y, mat, name, TextureWrapMode.Clamp);
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

		/// <summary>
		/// Flattens a cubemap into a horizontal 6x1 face strip (+X,-X,+Y,-Y,+Z,-Z, WebGL/three.js
		/// CubeTexture orientation) as a clamped LDR sRGB Texture2D — the format the Immersion web
		/// editor's reflection loader expects (width = 6 x height, square faces).
		/// </summary>
		public static Texture2D CubemapToFaceAtlas(Texture cubemap, Vector4 hdrDecodeValues, int faceSize, string name)
		{
			var mat = CubemapToFacesMaterial;
			if (!mat) return null;
			mat.SetTexture("_CubeTex", cubemap);
			mat.SetVector("_Decode", hdrDecodeValues);
			mat.SetFloat("_UseDecode", 1f);
			faceSize = Mathf.Max(4, faceSize);
			return BlitToSRGBTexture(Texture2D.whiteTexture, faceSize * 6, faceSize, mat, name, TextureWrapMode.Clamp);
		}

		/// <summary>
		/// Renders the scene skybox (any type: 6-sided, cubemap, procedural) into a temporary HDR
		/// cubemap RenderTexture via a throwaway camera. Caller must Release + destroy the result.
		/// Returns null if there is no skybox or the bake fails (e.g. some SRP setups).
		/// </summary>
		public static RenderTexture BakeSkyboxToCubemap(int faceSize)
		{
			if (!RenderSettings.skybox) return null;

			var cubeRT = new RenderTexture(faceSize, faceSize, 16, RenderTextureFormat.ARGBHalf)
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
					cubeRT.Release();
					Object.DestroyImmediate(cubeRT);
					return null;
				}
				return cubeRT;
			}
			finally
			{
				Object.DestroyImmediate(go);
			}
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
