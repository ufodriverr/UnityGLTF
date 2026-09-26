using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Immersion.Export
{
	/// <summary>
	/// Ground truth for the Animator settings a logic scene runs its characters with: the effective
	/// <c>applyRootMotion</c>, <c>updateMode</c>, <c>cullingMode</c>, <c>isHuman</c>, <c>hasRootMotion</c>,
	/// avatar and controller of every Animator, read through the Animator API (so importer defaults
	/// and prefab overrides are already resolved).
	///
	/// Edit mode: opens each <c>-scenes</c> entry (nothing is saved) and records every Animator
	/// (inactive ones included) with its hierarchy path, GlobalObjectId (source-asset fileID +
	/// prefab-instance fileID, to match the scene YAML's stripped block), source model, the source
	/// model's own Animator values, the prefab-instance overrides that target the Animator, the
	/// ModelImporter's animation type / avatar setup, the GameObject's components and any component
	/// that implements <c>OnAnimatorMove</c> (which would take root motion over).
	///
	/// Play mode (empty scene, fixed 60 fps via <c>Time.captureFramerate</c>): rebuilds each rig from
	/// its nearest prefab asset at its scene world position/rotation with the scene's avatar and
	/// controller, WITHOUT touching <c>applyRootMotion</c>, re-adds a <c>KeepPositionRotation</c> of the
	/// Animator GameObject with its scene values, reads the values again after a few frames and
	/// measures the GameObject's position/yaw drift over <c>-seconds</c> of the default state (culling
	/// forced to AlwaysAnimate after the read: batch mode has no camera).
	/// <c>-playClips "Name=clip.anim;Name=model.fbx::Take"</c> additionally plays each clip once
	/// (Playables, one extra rig per clip, with and without that rig's KeepPositionRotation) and
	/// records the GameObject's visible yaw / XZ travel over the clip — the root motion Unity applies.
	/// Writes <c>-out/animator-settings.json</c> and exits.
	///
	/// Unity.exe -batchmode -projectPath &lt;proj&gt; -executeMethod Immersion.Export.AnimatorSettingsProbe.Run
	///   -scenes "Assets/_Project/Scenes/Logic/Logic_X.unity;Assets/…/Logic_Y.unity" -out "C:/dir" [-seconds 3]
	///   [-playClips "Dismisser=Assets/…/a.anim;Camilla=Assets/…/b.anim"]
	/// (no -quit — the probe exits by itself; exit 0 = written, 1 = failed)
	/// </summary>
	public static class AnimatorSettingsProbe
	{
		private const string Flag = "Immersion.AnimatorSettingsProbe.pending";

		public static void Run()
		{
			var scenes = CliArgs.Split(CliArgs.Get("-scenes")).Where(s => s.Length > 0).ToList();
			var outDir = CliArgs.Get("-out");
			if (scenes.Count == 0 || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError("[AnimatorSettingsProbe] usage: -scenes \"a.unity;b.unity\" -out <dir> [-seconds 3] [-playClips \"Name=clip.anim\"]");
				CliArgs.Exit(1);
				return;
			}
			var capture = new Capture
			{
				outDir = outDir,
				seconds = CliArgs.GetInt("-seconds", 3),
				unity = Application.unityVersion,
				playClips = CliArgs.Get("-playClips") ?? "",
			};
			try
			{
				foreach (var s in scenes) CaptureScene(s, capture);
			}
			catch (Exception e)
			{
				Debug.LogError("[AnimatorSettingsProbe] FAIL capture: " + e);
				CliArgs.Exit(1);
				return;
			}
			Directory.CreateDirectory(outDir);
			var json = JsonUtility.ToJson(capture, true);
			File.WriteAllText(Path.Combine(outDir, "animator-settings.edit.json"), json);
			SessionState.SetString(Flag, json);
			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); // logic scenes closed unsaved
			EditorApplication.EnterPlaymode();
		}

		[InitializeOnLoadMethod]
		private static void Hook()
		{
			if (!Application.isBatchMode) return;
			EditorApplication.playModeStateChanged += state =>
			{
				if (state != PlayModeStateChange.EnteredPlayMode) return;
				var raw = SessionState.GetString(Flag, null);
				if (string.IsNullOrEmpty(raw)) return;
				SessionState.EraseString(Flag);
				var capture = JsonUtility.FromJson<Capture>(raw);
				new GameObject("AnimatorSettingsProbeHost").AddComponent<AnimatorSettingsProbeRunner>().Begin(capture);
			};
		}

		// ---------------------------------------------------------------- edit-mode capture

		internal static void CaptureScene(string scenePath, Capture capture)
		{
			var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
			var animators = scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Animator>(true)).ToList();
			Debug.Log($"[AnimatorSettingsProbe] {scenePath}: {animators.Count} Animators");
			foreach (var a in animators)
			{
				var go = a.gameObject;
				var gid = GlobalObjectId.GetGlobalObjectIdSlow(a);
				var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(a);
				var sourcePath = source ? AssetDatabase.GetAssetPath(source) : "";
				var rec = new Rec
				{
					scene = scenePath,
					name = go.name,
					hierarchyPath = IkHandFrameProbe.HierarchyPath(go.transform),
					globalObjectId = gid.ToString(),
					sourceFileId = gid.targetObjectId.ToString(CultureInfo.InvariantCulture),
					prefabInstanceFileId = gid.targetPrefabId.ToString(CultureInfo.InvariantCulture),
					activeInHierarchy = go.activeInHierarchy,
					enabled = a.enabled,
					isPrefabInstance = PrefabUtility.IsPartOfPrefabInstance(a),
					nearestPrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go) ?? "",
					animatorPathInNearestPrefab = PrefabUtility.GetNearestPrefabInstanceRoot(go) is GameObject nr ? AnimationUtility.CalculateTransformPath(go.transform, nr.transform) : "",
					sourceAssetPath = sourcePath,
					worldPos = go.transform.position,
					worldRot = go.transform.rotation,
					edit = Read(a),
					components = string.Join(";", go.GetComponents<Component>().Select(c => c ? c.GetType().Name : "<missing>")),
					onAnimatorMoveComponents = string.Join(";", go.GetComponents<MonoBehaviour>().Where(HasOnAnimatorMove).Select(c => c.GetType().FullName)),
				};
				var kpr = go.GetComponents<MonoBehaviour>().FirstOrDefault(c => c && c.GetType().Name == "KeepPositionRotation");
				if (kpr)
				{
					rec.kprType = kpr.GetType().AssemblyQualifiedName;
					rec.kprJson = EditorJsonUtility.ToJson(kpr);
				}
				if (source is Animator sa)
				{
					rec.sourceAnimatorApplyRootMotion = sa.applyRootMotion;
					rec.sourceAnimatorUpdateMode = sa.updateMode.ToString();
					rec.sourceAnimatorCullingMode = sa.cullingMode.ToString();
					rec.sourceAnimatorAvatar = sa.avatar ? sa.avatar.name : "";
					rec.sourceAnimatorController = sa.runtimeAnimatorController ? sa.runtimeAnimatorController.name : "";
				}
				if (AssetImporter.GetAtPath(sourcePath) is ModelImporter mi)
					rec.modelImporter = $"animationType={mi.animationType} avatarSetup={mi.avatarSetup} motionNodeName='{mi.motionNodeName}' importAnimation={mi.importAnimation}";
				if (rec.isPrefabInstance)
				{
					var mods = PrefabUtility.GetPropertyModifications(go) ?? new PropertyModification[0];
					rec.animatorOverrides = string.Join(";", mods
						.Where(m => m.target is Animator)
						.Select(m => m.propertyPath + "=" + (m.objectReference ? m.objectReference.name : m.value)));
				}
				capture.recs.Add(rec);
				Debug.Log($"[AnimatorSettingsProbe] {rec.hierarchyPath} (source fileID {rec.sourceFileId}, prefab instance &{rec.prefabInstanceFileId}): applyRootMotion={rec.edit.applyRootMotion} update={rec.edit.updateMode} culling={rec.edit.cullingMode} isHuman={rec.edit.isHuman} hasRootMotion={rec.edit.hasRootMotion} avatar={rec.edit.avatar} controller={rec.edit.controller} source={sourcePath} overrides=[{rec.animatorOverrides}]");
			}
		}

		private static bool HasOnAnimatorMove(MonoBehaviour c) =>
			c && c.GetType().GetMethod("OnAnimatorMove", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;

		internal static Values Read(Animator a) => new Values
		{
			applyRootMotion = a.applyRootMotion,
			updateMode = a.updateMode.ToString(),
			cullingMode = a.cullingMode.ToString(),
			isHuman = a.isHuman,
			hasRootMotion = a.hasRootMotion,
			isInitialized = a.isInitialized,
			avatar = a.avatar ? a.avatar.name : "",
			avatarPath = a.avatar ? AssetDatabase.GetAssetPath(a.avatar) : "",
			avatarValid = a.avatar && a.avatar.isValid,
			controller = a.runtimeAnimatorController ? a.runtimeAnimatorController.name : "",
			controllerPath = a.runtimeAnimatorController ? AssetDatabase.GetAssetPath(a.runtimeAnimatorController) : "",
			humanScale = a.isHuman ? a.humanScale : 0f,
			speed = a.speed,
		};

		// ---------------------------------------------------------------- data

		[Serializable]
		public class Values
		{
			public bool applyRootMotion;
			public string updateMode;
			public string cullingMode;
			public bool isHuman;
			public bool hasRootMotion;
			public bool isInitialized;
			public string avatar;
			public string avatarPath;
			public bool avatarValid;
			public string controller;
			public string controllerPath;
			public float humanScale;
			public float speed;
		}

		[Serializable]
		public class Rec
		{
			public string scene;
			public string name;
			public string hierarchyPath;
			public string globalObjectId;
			public string sourceFileId;
			public string prefabInstanceFileId;
			public bool activeInHierarchy;
			public bool enabled;
			public bool isPrefabInstance;
			public string nearestPrefabPath;
			public string animatorPathInNearestPrefab;
			public string sourceAssetPath;
			public bool sourceAnimatorApplyRootMotion;
			public string sourceAnimatorUpdateMode;
			public string sourceAnimatorCullingMode;
			public string sourceAnimatorAvatar;
			public string sourceAnimatorController;
			public string modelImporter;
			public string animatorOverrides;
			public string components;
			public string onAnimatorMoveComponents;
			public Vector3 worldPos;
			public Quaternion worldRot = Quaternion.identity;
			public string kprType;
			public string kprJson;
			public Values edit;
			public Values play;
			public string playClip;
			public float playDriftXZ_m;
			public float playDriftY_m;
			public float playYawMaxAbs_deg;
			public float playYawEnd_deg;
			public string playError;
		}

		[Serializable]
		public class ClipRun
		{
			public string rig;
			public string clipPath;
			public string clip;
			public float clipLength_s;
			public bool clipLooping;
			public bool withKpr;
			public bool applyRootMotion;
			public float yawMaxAbs_deg;
			public float yawMin_deg;
			public float yawMax_deg;
			public float yawEnd_deg;
			public float travelXZMax_m;
			public float travelXZEnd_m;
			public float driftYMax_m;
			public int frames;
			public string error;
		}

		[Serializable]
		public class Capture
		{
			public string unity;
			public string outDir;
			public int seconds;
			public string playClips;
			public List<Rec> recs = new List<Rec>();
			public List<ClipRun> clipRuns = new List<ClipRun>();
		}
	}

	// Play-mode half; shares the file with the static entry class on purpose (see IkHandFrameProbe.cs:
	// the file's MonoScript binds to the static class, so AddComponent accepts this Editor-folder type).

	/// <summary>Play-mode half of <see cref="AnimatorSettingsProbe"/>: rebuild, read, measure drift, dump, exit.</summary>
	public class AnimatorSettingsProbeRunner : MonoBehaviour
	{
		private class Live
		{
			public AnimatorSettingsProbe.Rec rec;
			public AnimatorSettingsProbe.ClipRun run; // null = default-state rig
			public AnimationClip clip;
			public PlayableGraph graph;
			public Animator animator;
			public Vector3 startPos;
			public float startYaw;
			public bool finished;
		}

		private const int ReadFrame = 5;
		private AnimatorSettingsProbe.Capture capture;
		private readonly List<Live> rigs = new List<Live>();
		private int frame;
		private float measureStart = -1f;
		private bool done;

		public void Begin(AnimatorSettingsProbe.Capture c)
		{
			capture = c;
			Time.captureFramerate = 60; // fixed dt
			foreach (var rec in capture.recs)
			{
				try
				{
					var animator = Build(rec, !string.IsNullOrEmpty(rec.kprJson), out var err);
					if (!animator) { rec.playError = err; continue; }
					rigs.Add(new Live { rec = rec, animator = animator });
				}
				catch (Exception e)
				{
					rec.playError = e.GetType().Name + ": " + e.Message;
				}
			}
			foreach (var entry in CliArgs.Split(capture.playClips).Where(e => e.Contains("=")))
			{
				var name = entry.Substring(0, entry.IndexOf('='));
				var clipSpec = entry.Substring(entry.IndexOf('=') + 1);
				var rec = capture.recs.FirstOrDefault(r => r.name == name);
				var variants = rec != null && !string.IsNullOrEmpty(rec.kprJson) ? new[] { true, false } : new[] { false };
				foreach (var withKpr in variants)
				{
					var run = new AnimatorSettingsProbe.ClipRun { rig = name, clipPath = clipSpec, withKpr = withKpr };
					capture.clipRuns.Add(run);
					try
					{
						if (rec == null) { run.error = "no Animator named " + name; continue; }
						var clip = LoadClip(clipSpec);
						if (!clip) { run.error = "clip not found: " + clipSpec; continue; }
						var animator = Build(rec, withKpr, out var err);
						if (!animator) { run.error = err; continue; }
						run.clip = clip.name;
						run.clipLength_s = clip.length;
						run.clipLooping = clip.isLooping;
						rigs.Add(new Live { rec = rec, run = run, clip = clip, animator = animator });
					}
					catch (Exception e)
					{
						run.error = e.GetType().Name + ": " + e.Message;
					}
				}
			}
			Debug.Log($"[AnimatorSettingsProbe] play: built {rigs.Count} rigs ({capture.recs.Count} scene Animators, {capture.clipRuns.Count} clip runs)");
		}

		internal static AnimationClip LoadClip(string spec)
		{
			var sep = spec.IndexOf("::", StringComparison.Ordinal);
			var path = sep >= 0 ? spec.Substring(0, sep) : spec;
			var take = sep >= 0 ? spec.Substring(sep + 2) : null;
			return AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
				.FirstOrDefault(a => !a.name.StartsWith("__preview__", StringComparison.Ordinal) && (take == null || a.name == take));
		}

		internal static Animator Build(AnimatorSettingsProbe.Rec rec, bool withKpr, out string error)
		{
			error = null;
			// The nearest prefab asset (keeps any prefab / variant-level override); scene-level overrides are in rec.animatorOverrides.
			var assetPath = string.IsNullOrEmpty(rec.nearestPrefabPath) ? rec.sourceAssetPath : rec.nearestPrefabPath;
			var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
			if (!model) { error = "prefab / model not loadable: " + assetPath; return null; }
			var inst = Instantiate(model);
			inst.name = rec.name + " (probe)";
			var host = string.IsNullOrEmpty(rec.animatorPathInNearestPrefab) ? inst.transform : inst.transform.Find(rec.animatorPathInNearestPrefab);
			var animator = host ? host.GetComponent<Animator>() : null;
			if (!animator) { error = "no Animator at '" + rec.animatorPathInNearestPrefab + "' in " + assetPath; return null; }
			// Scene pose: KeepPositionRotation clamps the euler yaw around its start rotation.
			animator.transform.SetPositionAndRotation(rec.worldPos, rec.worldRot);
			// Same avatar + controller as the scene instance; applyRootMotion / modes are left as the prefab gives them.
			var avatar = string.IsNullOrEmpty(rec.edit.avatarPath) ? null : AssetDatabase.LoadAllAssetsAtPath(rec.edit.avatarPath).OfType<Avatar>().FirstOrDefault(a => a.name == rec.edit.avatar);
			if (avatar && animator.avatar != avatar) animator.avatar = avatar;
			var ctrl = string.IsNullOrEmpty(rec.edit.controllerPath) ? null : AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(rec.edit.controllerPath);
			if (ctrl && animator.runtimeAnimatorController != ctrl) animator.runtimeAnimatorController = ctrl;
			if (withKpr && !string.IsNullOrEmpty(rec.kprType))
			{
				var type = Type.GetType(rec.kprType);
				if (type == null) { error = "KeepPositionRotation type not loadable: " + rec.kprType; return null; }
				EditorJsonUtility.FromJsonOverwrite(rec.kprJson, animator.gameObject.AddComponent(type));
			}
			return animator;
		}

		private void Update()
		{
			if (done) return;
			try
			{
				frame++;
				if (frame == ReadFrame)
				{
					foreach (var r in rigs)
					{
						var values = AnimatorSettingsProbe.Read(r.animator);
						if (r.run == null)
						{
							r.rec.play = values;
							var clips = r.animator.runtimeAnimatorController ? r.animator.GetCurrentAnimatorClipInfo(0) : new AnimatorClipInfo[0];
							r.rec.playClip = string.Join(",", clips.Select(ci => ci.clip.name + "@" + ci.weight.ToString("0.###", CultureInfo.InvariantCulture)));
						}
						else
						{
							r.run.applyRootMotion = values.applyRootMotion;
							// Back to the scene pose (KeepPositionRotation's start is already that pose), then play the clip from t = 0.
							r.animator.transform.SetPositionAndRotation(r.rec.worldPos, r.rec.worldRot);
							AnimationPlayableUtilities.PlayClip(r.animator, r.clip, out r.graph);
						}
						r.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; // after the read: no camera in batch mode
						r.startPos = r.animator.transform.position;
						r.startYaw = r.animator.transform.eulerAngles.y;
					}
					measureStart = Time.time;
					return;
				}
				if (measureStart < 0) return;
				var allDone = true;
				foreach (var r in rigs)
				{
					if (r.finished) continue;
					var yaw = Mathf.DeltaAngle(r.startYaw, r.animator.transform.eulerAngles.y);
					var d = r.animator.transform.position - r.startPos;
					var xz = new Vector2(d.x, d.z).magnitude;
					if (r.run == null)
					{
						r.rec.playYawMaxAbs_deg = Mathf.Max(r.rec.playYawMaxAbs_deg, Mathf.Abs(yaw));
						r.rec.playYawEnd_deg = yaw;
						r.rec.playDriftXZ_m = Mathf.Max(r.rec.playDriftXZ_m, xz);
						r.rec.playDriftY_m = Mathf.Max(r.rec.playDriftY_m, Mathf.Abs(d.y));
						if (Time.time - measureStart >= capture.seconds) r.finished = true;
					}
					else
					{
						var run = r.run;
						run.frames++;
						run.yawMaxAbs_deg = Mathf.Max(run.yawMaxAbs_deg, Mathf.Abs(yaw));
						run.yawMin_deg = Mathf.Min(run.yawMin_deg, yaw);
						run.yawMax_deg = Mathf.Max(run.yawMax_deg, yaw);
						run.yawEnd_deg = yaw;
						run.travelXZMax_m = Mathf.Max(run.travelXZMax_m, xz);
						run.travelXZEnd_m = xz;
						run.driftYMax_m = Mathf.Max(run.driftYMax_m, Mathf.Abs(d.y));
						if (r.graph.GetRootPlayable(0).GetTime() >= run.clipLength_s) { r.finished = true; r.graph.Stop(); }
					}
					allDone &= r.finished;
				}
				if (allDone || Time.time - measureStart > capture.seconds + 120) Finish();
			}
			catch (Exception e)
			{
				Debug.LogError("[AnimatorSettingsProbe] FAIL play: " + e);
				done = true;
				EditorApplication.Exit(1);
			}
		}

		private void Finish()
		{
			done = true;
			var path = Path.Combine(capture.outDir, "animator-settings.json");
			File.WriteAllText(path, JsonUtility.ToJson(capture, true));
			var sb = new StringBuilder();
			foreach (var r in capture.recs)
				sb.Append($"\n  {r.hierarchyPath}: edit applyRootMotion={r.edit.applyRootMotion} play applyRootMotion={(r.play != null ? r.play.applyRootMotion.ToString() : "-")} hasRootMotion={(r.play != null ? r.play.hasRootMotion.ToString() : "-")} clip={r.playClip} drift XZ {r.playDriftXZ_m:0.####} m yaw max {r.playYawMaxAbs_deg:0.###} deg {r.playError}");
			foreach (var c in capture.clipRuns)
				sb.Append($"\n  clip {c.rig} {c.clip} ({c.clipLength_s:0.##} s) kpr={c.withKpr} applyRootMotion={c.applyRootMotion}: yaw max|.| {c.yawMaxAbs_deg:0.###} [{c.yawMin_deg:0.###}, {c.yawMax_deg:0.###}] end {c.yawEnd_deg:0.###} deg, XZ max {c.travelXZMax_m:0.####} end {c.travelXZEnd_m:0.####} m, frames {c.frames} {c.error}");
			Debug.Log("[AnimatorSettingsProbe] summary:" + sb);
			Debug.Log("[AnimatorSettingsProbe] wrote " + path);
			EditorApplication.Exit(0);
		}
	}
}
