using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Shared helpers for the IMMERSION reflection-strip export: flattens a cubemap (baked probe
	/// or the skybox) into a clamped LDR sRGB 6x1 face atlas. Lightmap pages do NOT go through
	/// here — they ship unclamped as RGBM8 sidecars (see <c>GltfCustomDataExporter</c>).
	/// </summary>
	internal static class LightingExportUtils
	{
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

		// Linear shader output; the hardware applies the exact sRGB formula on write.
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
