using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityGLTF;

namespace Immersion.Export
{
	/// <summary>
	/// Batch-mode export of animation clips, ONE GLB PER CLIP, each baked on a reference rig
	/// (the web "clip pool" / animation-library format: rig node hierarchy + exactly one clip).
	///
	/// Usage:
	/// <code>
	/// Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
	///   -executeMethod Immersion.Export.ClipBatchExporter.ExportClips \
	///   -rig "Assets/A/Avatar.fbx" \
	///   -clips "Assets/Clips/a.anim;Assets/Anims/Take.fbx::Idle01" \
	///   -out "C:/exports/pool" \
	///   [-names "a;Idle01"] [-poseController "Assets/A/Base.controller"] [-keepRenderers 1]
	/// </code>
	///
	/// - <c>-rig</c>: prefab or model (FBX) asset path of the reference rig (required). Must carry
	///   an Animator; humanoid clips need a humanoid Avatar on it.
	/// - <c>-clips</c>: semicolon-separated clip sources (required). A <c>.anim</c> asset path, or
	///   <c>&lt;asset path&gt;::&lt;clip name&gt;</c> for a clip inside a model/other asset.
	/// - <c>-out</c>: output directory (required; created if missing). Writes
	///   <c>&lt;name&gt;.glb</c> + <c>&lt;name&gt;.clip.json</c> (Unity clip facts: length, frame rate,
	///   loop, root-motion import flags) per clip.
	/// - <c>-names</c>: optional output names aligned with -clips; empty segment = the source file
	///   name without extension (<c>.anim</c>) or the clip name (<c>::</c> form).
	/// - <c>-poseController</c>: optional AnimatorController whose base-layer default state at t=0
	///   poses the rig before export (<see cref="AvatarBatchExporter.PoseToDefaultState"/>). The
	///   pose is the GLB's REST pose (node TRS), which the web retarget reads as the rig profile
	///   (hips rest height = stature ratio), so every clip of one rig must share it — pass the
	///   controller the avatar GLB itself was exported with. Default: the rig Animator's own
	///   controller; if it has none, the exported clip's own t=0.
	/// - <c>-keepRenderers 1</c>: export meshes/skins/materials too (debug/comparison). Default: every
	///   Renderer is disabled before export, so the GLB holds the full node hierarchy (every
	///   GameObject stays) and the clip only.
	///
	/// Each clip is played through an IN-MEMORY single-state AnimatorController (never saved as
	/// an asset), so UnityGLTF bakes exactly that clip. The GLB's animation carries Unity's clip
	/// name; callers that need the output name inside the GLB rename it afterwards (HelperTools
	/// anim-library/export-pool-clips.mjs does, while stripping to the library format).
	///
	/// Exit code 0 = every clip exported; 1 = at least one failed. One line per clip:
	/// <c>[ClipBatchExporter] OK|FAIL &lt;name&gt;</c>.
	/// </summary>
	public static class ClipBatchExporter
	{
		private const string Tag = "[ClipBatchExporter]";

		public static void ExportClips()
		{
			var rigPath = CliArgs.Get("-rig");
			var clipSources = CliArgs.Split(CliArgs.Get("-clips")).Where(s => !string.IsNullOrEmpty(s)).ToList();
			var names = CliArgs.Split(CliArgs.Get("-names"));
			var outDir = CliArgs.Get("-out");
			var poseControllerPath = CliArgs.Get("-poseController");
			var keepRenderers = CliArgs.GetInt("-keepRenderers", 0) != 0;

			if (string.IsNullOrEmpty(rigPath) || clipSources.Count == 0 || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError(Tag + " usage: -rig <prefab|fbx> -clips \"a.anim;b.fbx::Take\" -out <dir> [-names \"a;b\"] [-poseController <controller>] [-keepRenderers 1]");
				CliArgs.Exit(1);
				return;
			}

			var rig = AssetDatabase.LoadAssetAtPath<GameObject>(rigPath);
			if (!rig)
			{
				Debug.LogError(Tag + " FAIL rig not found at '" + rigPath + "'");
				CliArgs.Exit(1);
				return;
			}

			AnimatorController poseController = null;
			if (!string.IsNullOrEmpty(poseControllerPath))
			{
				poseController = AssetDatabase.LoadAssetAtPath<AnimatorController>(poseControllerPath);
				if (!poseController)
				{
					Debug.LogError(Tag + " FAIL pose controller not found at '" + poseControllerPath + "'");
					CliArgs.Exit(1);
					return;
				}
			}

			Directory.CreateDirectory(outDir);
			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

			// Same settings path as AvatarBatchExporter (fresh defaults, see there for why).
			var settings = GLTFSettings.GetDefaultSettings();
			settings.ExportAnimations = true;
			ImmersionExportSettings.ApplyDefaults(settings, isObjectExport: true);
			Debug.Log(Tag + " rig '" + rigPath + "', " + clipSources.Count + " clip(s), pose "
				+ (poseController ? "'" + poseControllerPath + "'" : "rig controller / clip t=0")
				+ (keepRenderers ? ", renderers kept" : ", animation-only (renderers disabled)"));

			var failures = 0;
			for (var i = 0; i < clipSources.Count; i++)
			{
				var source = clipSources[i];
				var name = i < names.Count && !string.IsNullOrEmpty(names[i]) ? names[i] : DefaultName(source);
				GameObject instance = null;
				AnimatorController clipController = null;
				try
				{
					var clip = LoadClip(source);
					if (!clip) throw new Exception("clip not found at '" + source + "'");

					instance = (GameObject)PrefabUtility.InstantiatePrefab(rig);
					instance.transform.localPosition = Vector3.zero;
					// Plain object state from here on: renderer removal and activation must survive
					// the humanoid sampler's undo pair (see AvatarBatchExporter).
					if (PrefabUtility.IsPartOfPrefabInstance(instance))
						PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
					if (!instance.activeSelf) instance.SetActive(true);

					var animator = instance.GetComponentInChildren<Animator>(true);
					if (!animator) throw new Exception("rig has no Animator");
					if (clip.isHumanMotion && (!animator.avatar || !animator.avatar.isHuman))
						throw new Exception("humanoid clip but the rig's Animator has no humanoid Avatar");

					if (!keepRenderers) DisableRenderers(instance);

					clipController = SingleClipController(name, clip);
					var restFrom = PoseRest(instance, animator, poseController, clipController);
					// Swapping the controller rebinds the Animator, which puts the skeleton back to
					// its bind pose: snapshot the posed locals and write them back after the swap.
					var posed = instance.GetComponentsInChildren<Transform>(true)
						.Select(t => (t, t.localPosition, t.localRotation, t.localScale)).ToList();
					animator.runtimeAnimatorController = clipController;
					animator.enabled = false;
					foreach (var (t, p, r, sc) in posed)
					{
						t.localPosition = p;
						t.localRotation = r;
						t.localScale = sc;
					}

					var context = new ExportContext(settings);
					if (!ReferenceEquals(context.settings, settings))
						throw new Exception("export settings were replaced by the project settings asset");
					var exporter = new GLTFSceneExporter(new[] { instance.transform }, context);
					// The sampler's Undo.PerformUndo must only revert its own recording group.
					Undo.IncrementCurrentGroup();
					exporter.SaveGLB(outDir, name);

					var glbPath = Path.Combine(outDir, name + ".glb");
					if (!File.Exists(glbPath)) throw new Exception("exporter wrote no file at '" + glbPath + "'");
					File.WriteAllText(Path.Combine(outDir, name + ".clip.json"), ClipFactsJson(name, source, clip, rigPath, restFrom, animator.avatar), new UTF8Encoding(false));

					Debug.Log(Tag + " OK " + name + " -> " + glbPath + " (unity clip '" + clip.name + "', "
						+ clip.length.ToString("0.###", CultureInfo.InvariantCulture) + " s, rest from " + restFrom + ")");
				}
				catch (Exception e)
				{
					failures++;
					Debug.LogError(Tag + " FAIL " + name + ": " + e);
				}
				finally
				{
					if (instance) UnityEngine.Object.DestroyImmediate(instance);
					DestroyController(clipController);
				}
			}

			Debug.Log(Tag + " done: " + (clipSources.Count - failures) + "/" + clipSources.Count + " exported to " + outDir);
			CliArgs.Exit(failures > 0 ? 1 : 0);
		}

		private static string DefaultName(string source)
		{
			var sep = source.IndexOf("::", StringComparison.Ordinal);
			return sep >= 0 ? source.Substring(sep + 2) : Path.GetFileNameWithoutExtension(source);
		}

		/// <summary>A <c>.anim</c> main asset, or <c>path::clipName</c> for a sub-asset clip (e.g. an FBX take).</summary>
		private static AnimationClip LoadClip(string source)
		{
			var sep = source.IndexOf("::", StringComparison.Ordinal);
			if (sep < 0) return AssetDatabase.LoadAssetAtPath<AnimationClip>(source);
			var path = source.Substring(0, sep);
			var clipName = source.Substring(sep + 2);
			return AssetDatabase.LoadAllAssetsAtPath(path)
				.OfType<AnimationClip>()
				.FirstOrDefault(c => c.name == clipName && !c.name.StartsWith("__preview__", StringComparison.Ordinal));
		}

		private static void DisableRenderers(GameObject instance)
		{
			// DISABLED, not destroyed: every GameObject stays (the node hierarchy is exactly the
			// avatar export's) and UnityGLTF exports no mesh/skin/material for a disabled renderer
			// (ContainsValidRenderer), but a clip's blendshape curves still resolve their
			// SkinnedMeshRenderer (GenerateMissingCurves reads sharedMesh — a destroyed renderer
			// throws there). The resulting morph-weight channels target mesh-less nodes; the
			// library post-process drops them (design D8: morphs never portable between rigs).
			foreach (var r in instance.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
		}

		/// <summary>
		/// In-memory controller: one layer, one default state holding <paramref name="clip"/>. Never
		/// saved. Built from plain objects instead of AddLayer/AddState: those register UNDO
		/// creations, and the humanoid sampler's Undo.PerformUndo (ExporterAnimationHumanoid) would
		/// revert them mid-export — the state machine is destroyed and the Animator reports the
		/// controller as invalid.
		/// </summary>
		private static AnimatorController SingleClipController(string name, AnimationClip clip)
		{
			var state = new AnimatorState { name = name, motion = clip, speed = 1f, writeDefaultValues = true, hideFlags = HideFlags.HideInHierarchy };
			var stateMachine = new AnimatorStateMachine { name = "Base Layer", hideFlags = HideFlags.HideInHierarchy };
			stateMachine.states = new[] { new ChildAnimatorState { state = state, position = Vector3.zero } };
			stateMachine.defaultState = state;
			var controller = new AnimatorController { name = name };
			controller.layers = new[] { new AnimatorControllerLayer { name = "Base Layer", stateMachine = stateMachine, defaultWeight = 1f } };
			return controller;
		}

		private static void DestroyController(AnimatorController controller)
		{
			if (!controller) return;
			foreach (var layer in controller.layers)
			{
				if (!layer.stateMachine) continue;
				foreach (var child in layer.stateMachine.states)
					if (child.state) UnityEngine.Object.DestroyImmediate(child.state);
				UnityEngine.Object.DestroyImmediate(layer.stateMachine);
			}
			UnityEngine.Object.DestroyImmediate(controller);
		}

		/// <summary>Poses the rig for the export's rest pose; returns what the pose came from (for the log/sidecar).</summary>
		private static string PoseRest(GameObject instance, Animator animator, AnimatorController poseController, AnimatorController clipController)
		{
			var own = animator.runtimeAnimatorController as AnimatorController;
			var pose = poseController ? poseController : own ? own : clipController;
			animator.runtimeAnimatorController = pose;
			AvatarBatchExporter.PoseToDefaultState(instance);
			var state = pose.layers.Length > 0 && pose.layers[0].stateMachine ? pose.layers[0].stateMachine.defaultState : null;
			var clip = state ? state.motion as AnimationClip : null;
			var from = pose == clipController ? "clip" : pose == poseController ? "poseController" : "rigController";
			return from + ":" + (clip ? clip.name : "<none>") + "@0";
		}

		private static string ClipFactsJson(string name, string source, AnimationClip clip, string rigPath, string restFrom, Avatar avatar)
		{
			var s = AnimationUtility.GetAnimationClipSettings(clip);
			var sourcePath = source.Split(new[] { "::" }, StringSplitOptions.None)[0];
			var inv = CultureInfo.InvariantCulture;
			string B(bool v) => v ? "true" : "false";
			string F(float v) => v.ToString("R", inv);
			string Q(string v) => "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
			var sb = new StringBuilder();
			sb.Append("{\n");
			sb.Append("  \"name\": ").Append(Q(name)).Append(",\n");
			sb.Append("  \"unitySource\": ").Append(Q(source)).Append(",\n");
			sb.Append("  \"unitySourceGuid\": ").Append(Q(AssetDatabase.AssetPathToGUID(sourcePath))).Append(",\n");
			sb.Append("  \"unityClipName\": ").Append(Q(clip.name)).Append(",\n");
			sb.Append("  \"referenceRig\": ").Append(Q(rigPath)).Append(",\n");
			sb.Append("  \"restPose\": ").Append(Q(restFrom)).Append(",\n");
			sb.Append("  \"lengthS\": ").Append(F(clip.length)).Append(",\n");
			sb.Append("  \"frameRate\": ").Append(F(clip.frameRate)).Append(",\n");
			sb.Append("  \"isHumanMotion\": ").Append(B(clip.isHumanMotion)).Append(",\n");
			sb.Append("  \"hasRootCurves\": ").Append(B(clip.hasRootCurves)).Append(",\n");
			sb.Append("  \"hasGenericRootTransform\": ").Append(B(clip.hasGenericRootTransform)).Append(",\n");
			sb.Append("  \"hasMotionCurves\": ").Append(B(clip.hasMotionCurves)).Append(",\n");
			sb.Append("  \"loop\": ").Append(B(s.loopTime)).Append(",\n");
			sb.Append("  \"loopBlend\": ").Append(B(s.loopBlend)).Append(",\n");
			sb.Append("  \"bakeIntoPose\": { \"rotation\": ").Append(B(s.loopBlendOrientation))
				.Append(", \"positionY\": ").Append(B(s.loopBlendPositionY))
				.Append(", \"positionXZ\": ").Append(B(s.loopBlendPositionXZ)).Append(" },\n");
			sb.Append("  \"keepOriginal\": { \"rotation\": ").Append(B(s.keepOriginalOrientation))
				.Append(", \"positionY\": ").Append(B(s.keepOriginalPositionY))
				.Append(", \"positionXZ\": ").Append(B(s.keepOriginalPositionXZ)).Append(" },\n");
			sb.Append("  \"heightFromFeet\": ").Append(B(s.heightFromFeet)).Append(",\n");
			sb.Append("  \"mirror\": ").Append(B(s.mirror)).Append(",\n");
			sb.Append("  \"cycleOffset\": ").Append(F(s.cycleOffset)).Append(",\n");
			sb.Append("  \"orientationOffsetY\": ").Append(F(s.orientationOffsetY)).Append(",\n");
			sb.Append("  \"level\": ").Append(F(s.level)).Append(",\n");
			sb.Append("  \"averageSpeed\": [").Append(F(clip.averageSpeed.x)).Append(", ").Append(F(clip.averageSpeed.y)).Append(", ").Append(F(clip.averageSpeed.z)).Append("],\n");
			sb.Append("  \"events\": ").Append(AnimationUtility.GetAnimationEvents(clip).Length.ToString(inv)).Append(",\n");
			// Humanoid bone map of the rig (human name -> transform name): lets tooling tell
			// muscle-driven bones from unmapped helpers (CC rigs map Neck to CC_Base_NeckTwist01).
			var human = avatar && avatar.isHuman ? avatar.humanDescription.human : new HumanBone[0];
			sb.Append("  \"humanBones\": {");
			for (var h = 0; h < human.Length; h++)
				sb.Append(h == 0 ? "\n    " : ",\n    ").Append(Q(human[h].humanName)).Append(": ").Append(Q(human[h].boneName));
			sb.Append(human.Length > 0 ? "\n  }\n" : "}\n");
			sb.Append("}\n");
			return sb.ToString();
		}
	}
}
