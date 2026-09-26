using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Immersion.Export
{
	/// <summary>
	/// Per-frame whole-body trace of logic-scene IK characters (Lisa lower-body question, anim-v2):
	/// world pose of the Animator GameObject and of every body bone (hips, pelvis, legs, feet,
	/// spine, neck, head, arms) at a fixed 60 fps over <c>-seconds</c>, read in LateUpdate (after
	/// the Animator's evaluation, IK pass, retarget and root-motion application).
	///
	/// Edit mode reuses <see cref="IkHandFrameProbe"/>'s scene capture (nothing is saved). Play
	/// mode (empty scene, <c>Time.captureFramerate = 60</c>) rebuilds each captured rig once per
	/// variant at its scene pose, all instances side by side in the same frames:
	/// <list type="bullet">
	/// <item><c>scene</c>: the scene controller, scene <c>applyRootMotion</c>, CharacterIK mirror
	///   (<see cref="IkHandFrameDriver"/>) with the scene targets and weights — what the module shows.</item>
	/// <item><c>ikOff</c>: same, CharacterIK goals zeroed.</item>
	/// <item><c>noRootMotion</c>: same as <c>scene</c> with <c>applyRootMotion = false</c>.</item>
	/// <item><c>playableFootIk</c> / <c>playableNoFootIk</c>: the state's clip through a bare
	///   <c>AnimationClipPlayable</c> (UnityGLTF's bake path) with foot IK on / off, no CharacterIK;
	///   a manual graph evaluated in LateUpdate at the controller variants' clip time (frame / 60 s).</item>
	/// </list>
	/// Writes <c>-out/lower-body-trace.json</c> and exits.
	///
	/// Unity.exe -batchmode -projectPath &lt;proj&gt; -executeMethod Immersion.Export.LowerBodyTraceProbe.Run
	///   -scene "Assets/_Project/Scenes/Logic/Logic_X.unity" -out "C:/dir" [-rigs "Lisa"] [-seconds 7]
	/// (no -quit — the probe exits by itself; exit 0 = written, 1 = failed)
	/// </summary>
	public static class LowerBodyTraceProbe
	{
		private const string Flag = "Immersion.LowerBodyTraceProbe.pending";

		[Serializable]
		public class Pending
		{
			public string outDir;
			public float seconds;
			public IkHandFrameProbe.Capture capture;
		}

		public static void Run()
		{
			var scenePath = CliArgs.Get("-scene");
			var outDir = CliArgs.Get("-out");
			if (string.IsNullOrEmpty(scenePath) || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError("[LowerBodyTraceProbe] usage: -scene <logic scene> -out <dir> [-rigs \"Lisa\"] [-seconds 7]");
				CliArgs.Exit(1);
				return;
			}
			Pending pending;
			try
			{
				var capture = IkHandFrameProbe.CaptureScene(scenePath);
				var names = CliArgs.Split(CliArgs.Get("-rigs")).Where(s => s.Length > 0).ToList();
				if (names.Count > 0) capture.rigs = capture.rigs.Where(r => names.Contains(r.name)).ToList();
				if (capture.rigs.Count == 0) throw new Exception("no matching CharacterIK rig");
				var secondsArg = CliArgs.Get("-seconds");
				pending = new Pending
				{
					outDir = outDir,
					seconds = string.IsNullOrEmpty(secondsArg) ? 7f : float.Parse(secondsArg, CultureInfo.InvariantCulture),
					capture = capture,
				};
			}
			catch (Exception e)
			{
				Debug.LogError("[LowerBodyTraceProbe] FAIL capture: " + e);
				CliArgs.Exit(1);
				return;
			}
			Directory.CreateDirectory(outDir);
			var json = JsonUtility.ToJson(pending, true);
			File.WriteAllText(Path.Combine(outDir, "scene-capture.json"), json);
			SessionState.SetString(Flag, json);
			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); // logic scene closed unsaved
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
				var pending = JsonUtility.FromJson<Pending>(raw);
				var host = new GameObject("LowerBodyTraceProbeHost").AddComponent<LowerBodyTraceProbeRunner>();
				host.Begin(pending);
			};
		}
	}

	// Shares the file with the static entry class on purpose (see IkHandFrameProbe.cs).

	/// <summary>Play-mode half of <see cref="LowerBodyTraceProbe"/>: rebuild variants, trace, dump, exit.</summary>
	public class LowerBodyTraceProbeRunner : MonoBehaviour
	{
		private static readonly Regex BoneFilter = new Regex("BoneRoot|Hip|Pelvis|Thigh|Calf|Knee|Foot|ToeBase$|Waist|Spine|Neck|Head$|Clavicle|Upperarm$|Forearm$|_Hand$", RegexOptions.Compiled);
		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
		private static readonly string[] Variants = { "scene", "ikOff", "noRootMotion", "playableFootIk", "playableNoFootIk" };

		private class Inst
		{
			public string rig;
			public string variant;
			public Animator animator;
			public IkHandFrameDriver driver;
			public PlayableGraph graph;
			public AnimationClipPlayable clipPlayable;
			public bool usesPlayable;
			public List<Transform> bones = new List<Transform>();
			public List<string> boneNames = new List<string>();
			public Dictionary<string, string> human = new Dictionary<string, string>();
			public readonly StringBuilder frames = new StringBuilder();
			public string clipName;
			public float clipLength;
		}

		private LowerBodyTraceProbe.Pending pending;
		private readonly List<Inst> insts = new List<Inst>();
		private readonly StringBuilder meta = new StringBuilder();
		private int frame;
		private int totalFrames;
		private bool done;

		public void Begin(LowerBodyTraceProbe.Pending p)
		{
			pending = p;
			Time.captureFramerate = 60; // fixed dt
			totalFrames = Mathf.CeilToInt(p.seconds * 60f);
			try
			{
				foreach (var rig in p.capture.rigs)
					foreach (var v in Variants)
						insts.Add(Build(rig, v));
			}
			catch (Exception e)
			{
				Debug.LogError("[LowerBodyTraceProbe] FAIL build: " + e);
				done = true;
				EditorApplication.Exit(1);
			}
		}

		private Inst Build(IkHandFrameProbe.Rig rig, string variant)
		{
			var model = AssetDatabase.LoadAssetAtPath<GameObject>(rig.sourcePath);
			if (!model) throw new Exception("source model not found: " + rig.sourcePath);
			var parent = new GameObject("proxy: " + rig.rootParentKey + " [" + variant + "]").transform;
			parent.SetPositionAndRotation(rig.rootParentWorld.pos, rig.rootParentWorld.rot);
			parent.localScale = rig.rootParentWorld.scale;
			var inst = Instantiate(model, parent);
			inst.name = rig.name + " (" + variant + ")";
			inst.transform.localPosition = rig.rootLocal.pos;
			inst.transform.localRotation = rig.rootLocal.rot;
			inst.transform.localScale = rig.rootLocal.scale;
			for (var i = 0; i < rig.bonePaths.Count; i++)
			{
				var t = inst.transform.Find(rig.bonePaths[i]);
				if (!t) continue;
				var l = rig.boneLocals[i];
				t.localPosition = l.pos; t.localRotation = l.rot; t.localScale = l.scale;
			}
			var animator = inst.GetComponent<Animator>();
			if (!animator) throw new Exception("no Animator on model root " + rig.sourcePath);
			var avatar = AssetDatabase.LoadAllAssetsAtPath(rig.avatarPath).OfType<Avatar>().FirstOrDefault(a => a.name == rig.avatarName);
			if (avatar) animator.avatar = avatar;
			var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(rig.controllerPath);
			var result = new Inst { rig = rig.name, variant = variant, animator = animator };
			animator.applyRootMotion = variant == "noRootMotion" ? false : rig.applyRootMotion;
			animator.updateMode = (AnimatorUpdateMode)rig.updateMode;
			animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; // no camera in batch mode
			var clip = controller ? controller.animationClips.FirstOrDefault() : null;
			result.clipName = clip ? clip.name : "";
			result.clipLength = clip ? clip.length : 0f;

			if (variant.StartsWith("playable", StringComparison.Ordinal))
			{
				animator.runtimeAnimatorController = null;
				animator.Rebind();
				result.graph = PlayableGraph.Create("LowerBodyTrace " + inst.name);
				result.clipPlayable = AnimationClipPlayable.Create(result.graph, clip);
				result.clipPlayable.SetApplyFootIK(variant == "playableFootIk");
				var output = AnimationPlayableOutput.Create(result.graph, "Animation", animator);
				output.SetSourcePlayable(result.clipPlayable);
				result.graph.SetTimeUpdateMode(DirectorUpdateMode.Manual); // time-aligned with the controller variants (LateUpdate)
				result.usesPlayable = true;
			}
			else
			{
				animator.runtimeAnimatorController = controller;
				animator.Rebind();
				var driver = inst.AddComponent<IkHandFrameDriver>();
				driver.Setup(animator);
				driver.ikActive = variant != "ikOff" && rig.ikActive;
				foreach (var g in rig.goals)
				{
					var tgo = new GameObject("IK target " + g.goal + " (" + g.targetName + ")");
					Transform tp;
					if (g.parentInAvatar) tp = string.IsNullOrEmpty(g.parentPathInAvatar) ? inst.transform : inst.transform.Find(g.parentPathInAvatar);
					else
					{
						tp = new GameObject("proxy target parent: " + g.parentKey).transform;
						tp.SetPositionAndRotation(g.parentWorld.pos, g.parentWorld.rot);
						tp.localScale = g.parentWorld.scale;
					}
					if (!tp) throw new Exception("target parent not found for " + g.goal);
					tgo.transform.SetParent(tp, false);
					tgo.transform.localPosition = g.local.pos;
					tgo.transform.localRotation = g.local.rot;
					tgo.transform.localScale = g.local.scale;
					driver.goals.Add(new IkHandFrameDriver.Goal
					{
						ikGoal = (AvatarIKGoal)Enum.Parse(typeof(AvatarIKGoal), g.goal),
						target = tgo.transform,
						positionWeight = g.positionWeight,
						rotationWeight = g.rotationWeight,
					});
				}
				result.driver = driver;
			}

			foreach (var t in inst.GetComponentsInChildren<Transform>(true))
			{
				if (t == inst.transform || !BoneFilter.IsMatch(t.name) || t.name.EndsWith("_end", StringComparison.Ordinal)) continue;
				result.bones.Add(t);
				result.boneNames.Add(t.name);
			}
			foreach (HumanBodyBones hb in Enum.GetValues(typeof(HumanBodyBones)))
			{
				if (hb == HumanBodyBones.LastBone) continue;
				var bt = animator.isHuman ? animator.GetBoneTransform(hb) : null;
				if (bt) result.human[hb.ToString()] = bt.name;
			}
			meta.Append($"  {{\"rig\": \"{rig.name}\", \"variant\": \"{variant}\", \"applyRootMotion\": {(animator.applyRootMotion ? "true" : "false")}, \"ik\": {(result.driver != null && result.driver.ikActive ? "true" : "false")}, \"playable\": {(result.usesPlayable ? "true" : "false")}, \"clip\": \"{result.clipName}\", \"clipLength\": {F(result.clipLength)}, \"isHuman\": {(animator.isHuman ? "true" : "false")}, \"rootWorldPos\": {V(inst.transform.position)}, \"rootWorldRot\": {Q(inst.transform.rotation)}, \"rootLossyScale\": {V(inst.transform.lossyScale)}}},\n");
			return result;
		}

		private void LateUpdate()
		{
			if (done) return;
			try
			{
				frame++;
				foreach (var r in insts)
				{
					float clipTime;
					if (r.usesPlayable)
					{
						// same clip time the controller variants show this frame (f / 60 s after the first update)
						r.clipPlayable.SetTime(frame / 60.0);
						r.graph.Evaluate();
						clipTime = (float)r.clipPlayable.GetTime();
					}
					else clipTime = r.animator.GetCurrentAnimatorStateInfo(0).normalizedTime * r.clipLength;
					var root = r.animator.transform;
					var sb = r.frames;
					sb.Append(frame == 1 ? "" : ",\n").Append("{\"f\":").Append(frame).Append(",\"t\":").Append(F(Time.time)).Append(",\"ct\":").Append(F(clipTime))
						.Append(",\"root\":[").Append(Vc(root.position)).Append(',').Append(Qc(root.rotation)).Append(']')
												.Append(",\"bones\":[");
					for (var i = 0; i < r.bones.Count; i++)
					{
						if (i > 0) sb.Append(',');
						var b = r.bones[i];
						sb.Append('[').Append(Vc(b.position)).Append(',').Append(Qc(b.rotation)).Append(']');
					}
					sb.Append("]}");
				}
				if (frame >= totalFrames) Finish();
			}
			catch (Exception e)
			{
				Debug.LogError("[LowerBodyTraceProbe] FAIL trace: " + e);
				done = true;
				EditorApplication.Exit(1);
			}
		}

		private void Finish()
		{
			done = true;
			var sb = new StringBuilder();
			sb.Append("{\n\"scene\": \"").Append(pending.capture.scene).Append("\",\n\"unity\": \"").Append(Application.unityVersion).Append("\",\n\"fps\": 60,\n");
			sb.Append("\"meta\": [\n").Append(TrimComma(meta.ToString())).Append("],\n\"instances\": [\n");
			for (var k = 0; k < insts.Count; k++)
			{
				var r = insts[k];
				sb.Append("{\"rig\": \"").Append(r.rig).Append("\", \"variant\": \"").Append(r.variant).Append("\", \"clip\": \"").Append(r.clipName).Append("\", \"clipLength\": ").Append(F(r.clipLength))
					.Append(", \"boneNames\": [").Append(string.Join(",", r.boneNames.Select(n => "\"" + n + "\""))).Append("]")
					.Append(", \"human\": {").Append(string.Join(",", r.human.Select(kv => "\"" + kv.Key + "\":\"" + kv.Value + "\""))).Append("}")
					.Append(",\n\"frames\": [\n").Append(r.frames).Append("\n]}").Append(k + 1 < insts.Count ? ",\n" : "\n");
				if (r.usesPlayable && r.graph.IsValid()) r.graph.Destroy();
			}
			sb.Append("]\n}\n");
			var path = Path.Combine(pending.outDir, "lower-body-trace.json");
			File.WriteAllText(path, sb.ToString());
			Debug.Log("[LowerBodyTraceProbe] wrote " + path + " (" + insts.Count + " instances x " + frame + " frames)");
			EditorApplication.Exit(0);
		}

		private static string TrimComma(string s)
		{
			var i = s.LastIndexOf(",\n", StringComparison.Ordinal);
			return i >= 0 && i == s.Length - 2 ? s.Substring(0, i) + "\n" : s;
		}

		private static string F(float v) => v.ToString("R", Inv);
		private static string Vc(Vector3 v) => $"{F(v.x)},{F(v.y)},{F(v.z)}";
		private static string Qc(Quaternion q) => $"{F(q.x)},{F(q.y)},{F(q.z)},{F(q.w)}";
		private static string V(Vector3 v) => "[" + Vc(v) + "]";
		private static string Q(Quaternion q) => "[" + Qc(q) + "]";
	}
}
