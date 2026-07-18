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

			// Fresh in-memory settings with every plugin registered at its EnabledByDefault state.
			// The persisted project settings asset can lose its plugin sub-assets in batch mode
			// (saved with empty ImportPlugins/ExportPlugins lists), which silently disables ALL
			// export plugins on the next run — GetDefaultSettings() sidesteps that entirely.
			var settings = GLTFSettings.GetDefaultSettings();
			settings.ExportAnimations = true;

			// DefaultPoseExport re-poses skeletons to the BIND pose (T-pose, splayed hands)
			// before export. Bones a clip doesn't drive (fingers in most clips, the whole body
			// in face-capture clips) bake static tracks from the CURRENT pose, so with the
			// plugin on they freeze in T-pose — visibly broken hands/arms on the web. We pose
			// each avatar into its controller's default state instead (below), so the plugin
			// must not undo that.
			foreach (var plugin in settings.ExportPlugins)
				if (plugin is DefaultPoseExport) plugin.Enabled = false;

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

					PoseToDefaultState(instance);

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

		// Pose the instance into its AnimatorController's base-layer default state at t=0 (the
		// natural pose the module shows). The exported rest pose AND every static baked track
		// (fingers, face-clip body holds) then match the manual in-scene exports instead of the
		// raw T-pose prefab. Sampled via AnimationMode (same mechanism UnityGLTF's own humanoid
		// bake uses), snapshotted, and re-applied after AnimationMode restores the original.
		private static void PoseToDefaultState(GameObject instance)
		{
			var animator = instance.GetComponentInChildren<Animator>(true);
			var controller = animator ? animator.runtimeAnimatorController as AnimatorController : null;
			var defaultState = controller && controller.layers.Length > 0 && controller.layers[0].stateMachine
				? controller.layers[0].stateMachine.defaultState
				: null;
			var clip = defaultState ? defaultState.motion as AnimationClip : null;
			if (!clip)
			{
				Debug.LogWarning("[AvatarBatchExporter] no default-state clip to pose '" + instance.name + "' — exporting the prefab pose.");
				return;
			}

			UnityEditor.AnimationMode.StartAnimationMode();
			try
			{
				UnityEditor.AnimationMode.BeginSampling();
				UnityEditor.AnimationMode.SampleAnimationClip(instance, clip, 0f);
				UnityEditor.AnimationMode.EndSampling();

				var bones = instance.GetComponentsInChildren<Transform>(true);
				var pose = new (Transform t, Vector3 p, Quaternion r, Vector3 s)[bones.Length];
				for (var b = 0; b < bones.Length; b++)
					pose[b] = (bones[b], bones[b].localPosition, bones[b].localRotation, bones[b].localScale);

				UnityEditor.AnimationMode.StopAnimationMode(); // restores the live pose…
				foreach (var (t, p, r, s) in pose)             // …so re-apply the sampled one
				{
					if (!t) continue;
					t.localPosition = p;
					t.localRotation = r;
					t.localScale = s;
				}
				Debug.Log("[AvatarBatchExporter] posed '" + instance.name + "' to default state '" + defaultState.name + "' (clip '" + clip.name + "' @0s).");
			}
			finally
			{
				if (UnityEditor.AnimationMode.InAnimationMode()) UnityEditor.AnimationMode.StopAnimationMode();
			}
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
