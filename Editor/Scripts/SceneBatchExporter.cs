using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityGLTF;

namespace Immersion.Export
{
	/// <summary>
	/// Batch-mode GLB export of whole Unity scenes — the scriptable equivalent of the
	/// "UnityGLTF → Export active scene" menu item. Each scene is opened (Single mode, so its
	/// baked LightingData binds), every root GameObject is exported into one GLB named after
	/// the scene, and the IMMERSION export plugins emit their usual sidecars next to it
	/// (lightmap pages + offsets JSON, reflection-probe strip, scene-settings skybox).
	///
	/// Usage:
	/// <code>
	/// Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
	///   -executeMethod Immersion.Export.SceneBatchExporter.ExportScenes \
	///   -scenes "Assets/Scenes/EnvA.unity;Assets/Scenes/EnvB.unity" \
	///   -out "C:/exports"
	/// </code>
	///
	/// - <c>-scenes</c>: semicolon-separated scene asset paths (required).
	/// - <c>-out</c>: output directory for the GLBs (required; created if missing).
	///
	/// Exit code 0 = every scene exported; 1 = at least one failed (details on stdout, each
	/// scene logs a line starting with "[SceneBatchExporter]").
	/// </summary>
	public static class SceneBatchExporter
	{
		public static void ExportScenes()
		{
			var scenePaths = SplitArg(GetArg("-scenes"));
			var outDir = GetArg("-out");

			if (scenePaths.Count == 0 || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError("[SceneBatchExporter] usage: -scenes \"Assets/A.unity;Assets/B.unity\" -out <dir>");
				Exit(1);
				return;
			}

			Directory.CreateDirectory(outDir);

			// Fresh in-memory settings with every plugin registered at its EnabledByDefault state
			// (all IMMERSION plugins are EnabledByDefault). The persisted project settings asset
			// can lose its plugin sub-assets in batch mode, silently disabling every export
			// plugin — GetDefaultSettings() sidesteps that (same as AvatarBatchExporter).
			var settings = GLTFSettings.GetDefaultSettings();

			var failures = 0;
			foreach (var scenePath in scenePaths)
			{
				var name = Path.GetFileNameWithoutExtension(scenePath);
				try
				{
					var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
					if (!scene.IsValid()) throw new Exception("scene not found/openable at '" + scenePath + "'");

					var roots = scene.GetRootGameObjects();
					var transforms = Array.ConvertAll(roots, gameObject => gameObject.transform);
					if (transforms.Length == 0) throw new Exception("scene has no root GameObjects");

					var context = new ExportContext(settings);
					var exporter = new GLTFSceneExporter(transforms, context);
					exporter.SaveGLB(outDir, scene.name);

					var sidecars = Directory.GetFiles(outDir)
						.Select(Path.GetFileName)
						.Where(f => f.StartsWith(scene.name + "_", StringComparison.Ordinal))
						.ToArray();
					Debug.Log("[SceneBatchExporter] OK " + name + " -> " + Path.Combine(outDir, scene.name + ".glb")
						+ (sidecars.Length > 0 ? " (sidecars: " + string.Join(", ", sidecars) + ")" : ""));
				}
				catch (Exception e)
				{
					failures++;
					Debug.LogError("[SceneBatchExporter] FAIL " + name + ": " + e);
				}
			}

			Debug.Log("[SceneBatchExporter] done: " + (scenePaths.Count - failures) + "/" + scenePaths.Count + " exported to " + outDir);
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
