using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityGLTF;
using UnityGLTF.Plugins;

namespace Immersion.Export
{
	/// <summary>
	/// Batch-mode GLB export of avatar prefabs, with an optional flattened animator JSON baked
	/// into each GLB's scene extras (see <see cref="AnimatorExtrasExport"/>).
	///
	/// Usage:
	/// <code>
	/// Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
	///   -executeMethod Immersion.Export.AvatarBatchExporter.ExportAvatars \
	///   -avatars "Assets/A/Foo.prefab;Assets/B/Bar.prefab" \
	///   -animator "C:/exports/foo.animator.json;;" \
	///   -controller "Assets/A/Alt.controller;;" \
	///   -out "C:/exports"
	/// </code>
	///
	/// - <c>-avatars</c>: semicolon-separated prefab asset paths (required).
	/// - <c>-out</c>: output directory for the GLBs (required; created if missing).
	/// - <c>-animator</c>: semicolon-separated absolute paths to per-avatar animator JSON files,
	///   aligned by index with -avatars; empty segment = no animator block for that avatar.
	/// - <c>-controller</c>: semicolon-separated AnimatorController asset paths, aligned by index;
	///   empty segment = keep the prefab's own controller. The controller determines which clips
	///   are baked into the GLB (UnityGLTF exports the clips referenced by the Animator).
	///
	/// Exit code 0 = every avatar exported; 1 = at least one failed (details on stdout, each
	/// avatar logs a line starting with "[AvatarBatchExporter]").
	/// </summary>
	public static class AvatarBatchExporter
	{
		public static void ExportAvatars()
		{
			var avatars = SplitArg(GetArg("-avatars"));
			var animatorJsons = SplitArg(GetArg("-animator"));
			var controllers = SplitArg(GetArg("-controller"));
			var outDir = GetArg("-out");

			if (avatars.Count == 0 || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError("[AvatarBatchExporter] usage: -avatars \"a.prefab;b.prefab\" -out <dir> [-animator \"a.json;b.json\"] [-controller \"a.controller;\"]");
				Exit(1);
				return;
			}

			Directory.CreateDirectory(outDir);
			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

			var settings = GLTFSettings.GetOrCreateSettings();
			settings.ExportAnimations = true;

			var failures = 0;
			for (var i = 0; i < avatars.Count; i++)
			{
				var prefabPath = avatars[i];
				var name = Path.GetFileNameWithoutExtension(prefabPath);
				GameObject instance = null;
				try
				{
					var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
					if (!prefab) throw new Exception("prefab not found at '" + prefabPath + "'");

					instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
					instance.transform.localPosition = Vector3.zero;

					var controllerPath = i < controllers.Count ? controllers[i] : null;
					if (!string.IsNullOrEmpty(controllerPath))
					{
						var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
						if (!controller) throw new Exception("controller not found at '" + controllerPath + "'");
						var animator = instance.GetComponentInChildren<Animator>(true);
						if (!animator) throw new Exception("prefab has no Animator for -controller override");
						animator.runtimeAnimatorController = controller;
					}

					var jsonPath = i < animatorJsons.Count ? animatorJsons[i] : null;
					AnimatorExtrasExport.PayloadJson = string.IsNullOrEmpty(jsonPath) ? null : File.ReadAllText(jsonPath);

					var context = new ExportContext(settings);
					var exporter = new GLTFSceneExporter(new[] { instance.transform }, context);
					exporter.SaveGLB(outDir, name);

					Debug.Log("[AvatarBatchExporter] OK " + name + " -> " + Path.Combine(outDir, name + ".glb")
						+ (AnimatorExtrasExport.PayloadJson != null ? " (with IMMERSION_animator)" : ""));
				}
				catch (Exception e)
				{
					failures++;
					Debug.LogError("[AvatarBatchExporter] FAIL " + name + ": " + e);
				}
				finally
				{
					AnimatorExtrasExport.PayloadJson = null;
					if (instance) UnityEngine.Object.DestroyImmediate(instance);
				}
			}

			Debug.Log("[AvatarBatchExporter] done: " + (avatars.Count - failures) + "/" + avatars.Count + " exported to " + outDir);
			Exit(failures > 0 ? 1 : 0);
		}

		private static string GetArg(string flag)
		{
			var args = Environment.GetCommandLineArgs();
			for (var i = 0; i < args.Length - 1; i++)
				if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
					return args[i + 1];
			return null;
		}

		private static List<string> SplitArg(string value)
		{
			return string.IsNullOrEmpty(value)
				? new List<string>()
				: value.Split(';').Select(s => s.Trim().Trim('"')).ToList();
		}

		private static void Exit(int code)
		{
			if (Application.isBatchMode) EditorApplication.Exit(code);
		}
	}
}
