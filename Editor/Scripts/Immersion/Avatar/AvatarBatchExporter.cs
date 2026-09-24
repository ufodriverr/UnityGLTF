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
	/// - <c>-maxTextureSize N</c> / <c>-maxLightmapSize N</c>: optional caps of the Revolution
	///   plugin (longest side in px); default 0 = full resolution.
	///
	/// Exit code 0 = every avatar exported; 1 = at least one failed (details on stdout, each
	/// avatar logs a line starting with "[AvatarBatchExporter]").
	/// </summary>
	public static class AvatarBatchExporter
	{
		public static void ExportAvatars()
		{
			var avatars = CliArgs.Split(CliArgs.Get("-avatars"));
			var animatorJsons = CliArgs.Split(CliArgs.Get("-animator"));
			var controllers = CliArgs.Split(CliArgs.Get("-controller"));
			var outDir = CliArgs.Get("-out");

			if (avatars.Count == 0 || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError("[AvatarBatchExporter] usage: -avatars \"a.prefab;b.prefab\" -out <dir> [-animator \"a.json;b.json\"] [-controller \"a.controller;\"]");
				CliArgs.Exit(1);
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
			var maxTextureSize = Math.Max(0, CliArgs.GetInt("-maxTextureSize", 0));
			var maxLightmapSize = Math.Max(0, CliArgs.GetInt("-maxLightmapSize", 0));
			ImmersionExportSettings.ApplyDefaults(settings, isObjectExport: true, maxTextureSize, maxLightmapSize);
			Debug.Log("[AvatarBatchExporter] contract v" + GltfCustomDataExporter.CONTRACT_VERSION
				+ ", probe range " + GltfCustomDataExporter.PROBE_RANGE
				+ ", maxTextureSize " + (maxTextureSize > 0 ? maxTextureSize.ToString() : "unlimited"));

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

					// Some configured prefabs ship with an INACTIVE root (the module activates
					// them at runtime — e.g. 531's Lisa.prefab). An inactive root makes the
					// Animator refuse to evaluate (so PoseToDefaultState silently leaves the
					// raw bind/T-pose) and makes UnityGLTF drop every animated curve
					// ("Object X is disabled, not exporting animated curve") — the GLB comes
					// out with ZERO animations. Activate before posing/exporting.
					if (!instance.activeSelf)
					{
						// UNPACK FIRST: SetActive on a prefab INSTANCE is only an override, and the
						// humanoid sampler's Undo.RegisterFullObjectHierarchyUndo/PerformUndo pair
						// (ExporterAnimationHumanoid.CollectClipCurvesBySampling) drops it again
						// mid-export — the rig is inactive by the time the curves are written and
						// every channel is skipped. Unpacking makes the activation plain object
						// state that survives the undo.
						PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
						instance.SetActive(true);
						Debug.Log("[AvatarBatchExporter] unpacked + activated inactive prefab root '" + name + "' before export.");
					}

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
					// a destroyed settings object makes ExportContext fall back to the project asset
					if (!ReferenceEquals(context.settings, settings))
						throw new Exception("export settings were replaced by the project settings asset");
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
			CliArgs.Exit(failures > 0 ? 1 : 0);
		}

		// Pose the instance into its AnimatorController's base-layer default state at t=0 (the
		// natural pose the module shows). The exported rest pose AND every static baked track
		// (bones a clip does not drive — fingers in most clips, the whole body in face-capture
		// clips — are baked as constant curves from the CURRENT pose by UnityGLTF's
		// GenerateMissingCurves) then match the in-scene look instead of the raw T-pose prefab.
		// Humanoid clips carry muscle curves, so plain SampleAnimationClip is a no-op — the
		// Animator itself is driven (Play + Update) and the pose frozen for the export.
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

			// Drive the Animator itself (Play + Update) instead of AnimationMode playable
			// sampling: it poses the muscle-mapped bones to the default state rather than the
			// raw T-pose prefab. Do NOT add any twist-solve handling on top: the TwistProbe
			// play-mode ground truth (Editor/Scripts/TwistProbe.cs, commit 9ff128df) proved
			// Unity's own runtime leaves unmapped *Twist*/ShareBone helper bones frozen at
			// their bind rotation on these rigs, so bind-frozen twist statics in the export
			// are Unity-faithful (worst export-vs-probe delta 2.49°). The LuxMed rigs' ~30°
			// twist values are CC A-pose bind AUTHORING, not a runtime solve.
			try
			{
				animator.enabled = true;
				animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
				animator.Play(Animator.StringToHash(defaultState.name), 0, 0f);
				animator.Update(0f);
				animator.Update(0.000001f); // muscle pose on the mapped bones

				// Round-trip the evaluated pose through HumanPoseHandler: GetHumanPose reads the
				// muscle values off the posed skeleton, SetHumanPose runs the muscle→skeleton
				// writeback for the mapped bones. Per TwistProbe (see above) this does NOT write
				// the unmapped *Twist* helpers — no runtime path does; they stay at bind, which
				// is exactly what Unity's runtime produces.
				if (animator.isHuman && animator.avatar)
				{
					var handler = new HumanPoseHandler(animator.avatar, animator.transform);
					var humanPose = new HumanPose();
					handler.GetHumanPose(ref humanPose);
					handler.SetHumanPose(ref humanPose);
					handler.Dispose();
				}
				Debug.Log("[AvatarBatchExporter] posed '" + instance.name + "' to default state '" + defaultState.name + "' (clip '" + clip.name + "' @0s, HumanPose round-trip).");
			}
			finally
			{
				animator.enabled = false; // freeze the pose for export
			}
		}
	}
}
