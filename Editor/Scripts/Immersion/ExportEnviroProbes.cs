// UNCOMMITTED probe-repair helper (module 531, 2026-08-29).
// The 531 enviro GLB exports shipped NO reflection-probe data: at export time the
// probes' baked cubemaps were not loaded (rp.texture == null), so GltfCustomData's
// probe path silently skipped extras + equirect for every probe, and every mesh's
// reflection_probe_texture came out empty. This tool repairs the DATA side offline:
// for each 531 enviro scene it (re)bakes reflection-probe snapshots when missing,
// then encodes every enabled baked probe with the exporter's own
// Hidden/CubemapToEquirect shader (probe HDR decode + /_DynamicRange 5 — byte-parity
// with GltfCustomData.ExportReflectionProbeCubemap) and writes:
//   <out>/<scene>/<probeTexture>_Equirect.png   (linear, 4w x 2w like the exporter)
//   <out>/<scene>/probes.json                   (node, texture, intensity,
//                                                box_projection, bounds string + numbers)
// A Node-side injector then adds these to the LIVE GLBs (probe nodes + textures),
// preserving the round-5 texture optimization — no re-export needed.
//
//   Unity.exe -batchmode -quit -projectPath <proj> \
//     -executeMethod Immersion.Export531.ExportEnviroProbes.Export -out <dir>
// (must run WITH graphics — the encode is a GPU blit; do NOT pass -nographics)
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Immersion.Export531
{
	public static class ExportEnviroProbes
	{
		private static readonly string[] Scenes =
		{
			"Assets/_Project/Scenes/Environment/SalesForce_Debranded2.unity",
			"Assets/_Project/Scenes/Environment/SalesForce.unity",
			"Assets/_Project/Scenes/Environment/SalesForce_OfficeBuilding.unity",
		};

		private const string EquirectShader = "Hidden/CubemapToEquirect";
		private const float DynamicRange = 5.0f; // exporter parity (UNITY_SHADERS.probeRange)

		// Interactive path — run from the open editor (prompts to save unsaved
		// scene changes first; scenes are opened Single). Output goes to
		// <project>/Temp531Probes.
		[MenuItem("Immersion/531/Export Enviro Probes (bake missing)")]
		public static void ExportFromMenu()
		{
			if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
			var outDir = Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "Temp531Probes");
			ExportTo(outDir);
			EditorUtility.RevealInFinder(outDir);
		}

		public static void Export()
		{
			var outDir = GetArg("-out");
			if (string.IsNullOrEmpty(outDir)) throw new Exception("pass -out <dir>");
			ExportTo(outDir);
		}

		private static void ExportTo(string outDir)
		{
			Directory.CreateDirectory(outDir);

			foreach (var scenePath in Scenes)
			{
				var sceneName = Path.GetFileNameWithoutExtension(scenePath);
				Debug.Log($"[ExportEnviroProbes] ===== {sceneName} =====");
				var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
				if (!scene.IsValid()) throw new Exception($"scene not openable: {scenePath}");

				var probes = CollectBakedProbes();
				if (probes.Count == 0)
				{
					Debug.LogWarning($"[ExportEnviroProbes] {sceneName}: no enabled baked probes — skipped");
					continue;
				}

				if (probes.Exists(p => p.texture == null))
				{
					// Unity 6 made Lightmapping.BakeAllReflectionProbesSnapshots internal;
					// the public per-probe bake writes a standalone .exr next to the scene
					// and assigns it as the probe's baked texture — persists in the scene.
					Debug.Log($"[ExportEnviroProbes] {sceneName}: unbaked probe snapshots — baking…");
					var sceneDir = Path.GetDirectoryName(scenePath)?.Replace('\\', '/');
					var bakeDir = $"{sceneDir}/{sceneName}";
					if (!AssetDatabase.IsValidFolder(bakeDir))
						AssetDatabase.CreateFolder(sceneDir, sceneName);
					for (int i = 0; i < probes.Count; i++)
					{
						var rp = probes[i];
						if (rp.texture != null) continue;
						var assetPath = $"{bakeDir}/ReflectionProbe-{i}.exr";
						if (!Lightmapping.BakeReflectionProbe(rp, assetPath))
							throw new Exception($"{sceneName}: BakeReflectionProbe failed for {rp.gameObject.name}");
					}
					// Persist the bake references so future GLB exports carry probes too.
					EditorSceneManager.SaveScene(scene);
					AssetDatabase.SaveAssets();
					probes = CollectBakedProbes();
				}

				var sceneOut = Path.Combine(outDir, sceneName);
				Directory.CreateDirectory(sceneOut);
				var rows = new JArray();

				foreach (var rp in probes)
				{
					var cube = rp.texture as Cubemap;
					if (cube == null)
					{
						Debug.LogError($"[ExportEnviroProbes] {sceneName}/{rp.gameObject.name}: still no baked cubemap after bake — skipped");
						continue;
					}

					var png = EncodeEquirectPng(cube, rp.textureHDRDecodeValues);
					var pngName = $"{cube.name}_Equirect.png";
					File.WriteAllBytes(Path.Combine(sceneOut, pngName), png);

					var b = rp.bounds;
					rows.Add(new JObject
					{
						["node"] = rp.gameObject.name,
						["texture"] = cube.name,
						["image"] = pngName,
						["rp_intensity"] = rp.intensity,
						["box_projection"] = rp.boxProjection,
						// Same string the exporter writes — the runtime parses this exact format
						// (parseUnityBoundsToBox3 applies the Unity->three -x flip itself).
						["bounds"] = b.ToString(),
						["center"] = new JArray(b.center.x, b.center.y, b.center.z),
						["extents"] = new JArray(b.extents.x, b.extents.y, b.extents.z),
						["importance"] = rp.importance,
						["cubeSize"] = cube.width,
					});
					Debug.Log($"[ExportEnviroProbes] {sceneName}: exported {pngName} bounds={b} intensity={rp.intensity} boxProj={rp.boxProjection}");
				}

				File.WriteAllText(Path.Combine(sceneOut, "probes.json"),
					new JObject { ["scene"] = sceneName, ["probes"] = rows }.ToString());
			}

			Debug.Log("[ExportEnviroProbes] done");
		}

		private static List<ReflectionProbe> CollectBakedProbes()
		{
			var list = new List<ReflectionProbe>();
			foreach (var rp in UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None))
			{
				if (!rp.enabled || !rp.gameObject.activeInHierarchy) continue;
				if (rp.mode != UnityEngine.Rendering.ReflectionProbeMode.Baked) continue;
				list.Add(rp);
			}
			return list;
		}

		// Mirrors GltfCustomData.EncodeReflectionProbe + BlitToTexture2D exactly:
		// linear RT, probe HDR decode passed explicitly, /5 range, 4w x 2w equirect.
		private static byte[] EncodeEquirectPng(Cubemap cube, Vector4 hdrDecodeValues)
		{
			var shader = Shader.Find(EquirectShader);
			if (shader == null) throw new Exception($"shader not found: {EquirectShader}");
			var mat = new Material(shader);
			try
			{
				mat.SetTexture("_Cube", cube);
				mat.SetFloat("_DynamicRange", DynamicRange);
				mat.SetVector("_CubeDecode", hdrDecodeValues);

				int width = cube.width * 4;
				int height = cube.width * 2;
				var desc = new RenderTextureDescriptor(width, height, RenderTextureFormat.ARGBHalf, 0)
				{
					msaaSamples = 1, depthBufferBits = 0, mipCount = 1,
					useMipMap = false, autoGenerateMips = false, sRGB = false,
				};
				var rt = RenderTexture.GetTemporary(desc);
				var prev = RenderTexture.active;
				try
				{
					Graphics.Blit(null, rt, mat, 0);
					RenderTexture.active = rt;
					var half = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true);
					half.ReadPixels(new Rect(0, 0, width, height), 0, 0);
					half.Apply(false, false);

					// 8-bit linear PNG — SetPixels clamps to [0,1], same quantization the
					// exporter's PNG encode applies to its RGBAHalf texture.
					var ldr = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
					ldr.SetPixels(half.GetPixels());
					ldr.Apply(false, false);
					var png = ldr.EncodeToPNG();
					UnityEngine.Object.DestroyImmediate(half);
					UnityEngine.Object.DestroyImmediate(ldr);
					return png;
				}
				finally
				{
					RenderTexture.active = prev;
					RenderTexture.ReleaseTemporary(rt);
				}
			}
			finally
			{
				UnityEngine.Object.DestroyImmediate(mat);
			}
		}

		private static string GetArg(string name)
		{
			var args = Environment.GetCommandLineArgs();
			for (int i = 0; i < args.Length - 1; i++)
				if (args[i] == name) return args[i + 1];
			return null;
		}
	}
}
