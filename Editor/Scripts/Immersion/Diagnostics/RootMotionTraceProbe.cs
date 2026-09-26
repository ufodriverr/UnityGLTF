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
using UnityEngine.LowLevel;
using UnityEngine.Playables;
using UnityEngine.PlayerLoop;

namespace Immersion.Export
{
	/// <summary>
	/// Per-frame root-motion reference of logic-scene characters (anim-v2 T018c): the Animator
	/// GameObject's world pose every frame at a fixed 60 fps, sampled twice per frame — right after
	/// the animation update (root motion applied, before ANY LateUpdate, so before
	/// <c>KeepPositionRotation</c>) and after every LateUpdate (after KeepPositionRotation). Both
	/// sample points are PlayerLoop systems inserted around <c>PreLateUpdate.ScriptRunBehaviourLateUpdate</c>,
	/// so they do not depend on script execution order.
	///
	/// Edit mode reuses <see cref="AnimatorSettingsProbe"/>'s scene capture (nothing is saved); play
	/// mode (empty scene, <c>Time.captureFramerate = 60</c>) rebuilds one rig per case with
	/// <see cref="AnimatorSettingsProbeRunner.Build"/> at its scene pose and runs the case:
	/// <list type="bullet">
	/// <item><c>clip</c>: T018b's replay — at frame <c>startFrame</c> back to the scene pose, culling
	///   AlwaysAnimate, <c>AnimationPlayableUtilities.PlayClip</c>; recorded until clip end + <c>tailSeconds</c>.</item>
	/// <item><c>tag</c>: the real tag path — the rig's controller plus the project's own
	///   <c>AnimationController</c> (added on an inactive host with <c>animator</c>,
	///   <c>_animationsTestMode = true</c>, <c>blendDuration</c> set before its Awake), then per tag the
	///   same calls as <c>AnimationController.PlayAnimation</c>'s layer loop (<c>GetAnimatorLayerIndex</c>,
	///   <c>PlayAnimationClip</c>, <c>SetLayerWeight</c>, by reflection: they are private), clip asset
	///   loaded directly instead of through the bundle manager. Recorded until <c>tailSeconds</c> after
	///   the last tag's layer weight is back at 0.</item>
	/// <item><c>idle</c>: controller + AnimationController, no tag, <c>durationSeconds</c>.</item>
	/// </list>
	/// Edit mode also writes <c>-out/root-curves.json</c> (the clips' humanoid root curves on a 60 Hz grid).
	/// Writes <c>-out/unity-reference.json</c> (plus <c>-out/cases/&lt;id&gt;.json</c> when the single file
	/// would exceed 15 MB) and exits.
	///
	/// Unity.exe -batchmode -nographics -projectPath &lt;proj&gt; -executeMethod Immersion.Export.RootMotionTraceProbe.Run
	///   -scenes "Assets/…/Logic_X.unity;Assets/…/Logic_Y.unity" -spec "C:/dir/cases.json" -out "C:/dir"
	/// (no -quit — the probe exits by itself; exit 0 = written, 1 = failed)
	/// </summary>
	public static class RootMotionTraceProbe
	{
		private const string Flag = "Immersion.RootMotionTraceProbe.pending";

		public static void Run()
		{
			var scenes = CliArgs.Split(CliArgs.Get("-scenes")).Where(s => s.Length > 0).ToList();
			var outDir = CliArgs.Get("-out");
			var specPath = CliArgs.Get("-spec");
			if (scenes.Count == 0 || string.IsNullOrEmpty(outDir) || string.IsNullOrEmpty(specPath) || !File.Exists(specPath))
			{
				Debug.LogError("[RootMotionTraceProbe] usage: -scenes \"a.unity;b.unity\" -spec <cases.json> -out <dir>");
				CliArgs.Exit(1);
				return;
			}
			var capture = new AnimatorSettingsProbe.Capture { outDir = outDir, unity = Application.unityVersion, playClips = "" };
			try
			{
				foreach (var s in scenes) AnimatorSettingsProbe.CaptureScene(s, capture);
			}
			catch (Exception e)
			{
				Debug.LogError("[RootMotionTraceProbe] FAIL capture: " + e);
				CliArgs.Exit(1);
				return;
			}
			try
			{
				DumpRootCurves(JsonUtility.FromJson<Spec>(File.ReadAllText(specPath)), outDir);
			}
			catch (Exception e)
			{
				Debug.LogError("[RootMotionTraceProbe] FAIL root curves: " + e);
				CliArgs.Exit(1);
				return;
			}
			var pending = new Pending
			{
				outDir = outDir,
				specPath = specPath,
				specJson = File.ReadAllText(specPath),
				captureJson = JsonUtility.ToJson(capture),
			};
			SessionState.SetString(Flag, JsonUtility.ToJson(pending));
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
				new GameObject("RootMotionTraceProbeHost").AddComponent<RootMotionTraceProbeRunner>().Begin(JsonUtility.FromJson<Pending>(raw));
			};
		}

		/// <summary>
		/// <c>-out/root-curves.json</c>: every humanoid root / motion curve (<c>RootT.*</c>, <c>RootQ.*</c>,
		/// <c>MotionT.*</c>, <c>MotionQ.*</c>) of the spec's clips (+ Idle_default), sampled with
		/// <c>AnimationCurve.Evaluate</c> on the 60 Hz grid t = i/60, i = 0..ceil(length·60) (last sample
		/// clamped to the length). Values are the clip's own (humanScale-normalised) curve values.
		/// </summary>
		private static void DumpRootCurves(Spec spec, string outDir)
		{
			var paths = spec.cases.Select(c => c.clip).Concat(spec.cases.SelectMany(c => c.tags.Select(t => t.clip)))
				.Append("Assets/_Project/AnimationSystem/AnimatorController/Idle_default.anim")
				.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
			var inv = CultureInfo.InvariantCulture;
			var sb = new StringBuilder("{");
			var first = true;
			foreach (var p in paths)
			{
				var clip = AnimatorSettingsProbeRunner.LoadClip(p);
				if (!clip) continue;
				var bindings = AnimationUtility.GetCurveBindings(clip)
					.Where(b => b.path == "" && (b.propertyName.StartsWith("RootT.", StringComparison.Ordinal) || b.propertyName.StartsWith("RootQ.", StringComparison.Ordinal)
						|| b.propertyName.StartsWith("MotionT.", StringComparison.Ordinal) || b.propertyName.StartsWith("MotionQ.", StringComparison.Ordinal)))
					.OrderBy(b => b.propertyName, StringComparer.Ordinal).ToList();
				var n = Mathf.CeilToInt(clip.length * 60f - 1e-3f);
				sb.Append(first ? "\n" : ",\n").Append('"').Append(clip.name).Append("\":{\"length\":").Append(clip.length.ToString("R", inv)).Append(",\"samples\":").Append(n + 1).Append(",\"curves\":{");
				first = false;
				for (var bi = 0; bi < bindings.Count; bi++)
				{
					var curve = AnimationUtility.GetEditorCurve(clip, bindings[bi]);
					sb.Append(bi > 0 ? "," : "").Append('"').Append(bindings[bi].propertyName).Append("\":{\"keys\":").Append(curve.length).Append(",\"v\":[");
					for (var i = 0; i <= n; i++)
					{
						var t = Mathf.Min(i / 60f, clip.length);
						sb.Append(i > 0 ? "," : "").Append(Math.Round(curve.Evaluate(t), 7).ToString("0.#######", inv));
					}
					sb.Append("]}");
				}
				sb.Append("}}");
			}
			sb.Append("\n}\n");
			Directory.CreateDirectory(outDir);
			File.WriteAllText(Path.Combine(outDir, "root-curves.json"), sb.ToString());
		}

		[Serializable]
		public class Pending
		{
			public string outDir;
			public string specPath;
			public string specJson;
			public string captureJson;
		}

		[Serializable]
		public class Spec
		{
			public List<CaseDef> cases = new List<CaseDef>();
		}

		[Serializable]
		public class CaseDef
		{
			public string id;
			public string kind; // clip | tag | idle
			public string scene; // substring of the logic scene path
			public string path; // Animator hierarchy path in that scene
			public bool kpr;
			public string clip; // kind clip
			public int startFrame = 5; // kind clip
			public float tailSeconds = 0.5f;
			public float durationSeconds; // kind idle
			public float timeoutSeconds = 150f;
			public float blendDuration = 1f;
			public List<TagDef> tags = new List<TagDef>();
		}

		[Serializable]
		public class TagDef
		{
			public string layer;
			public string clip;
			public float weight = 1f;
			public float speed = 1f;
			public string after = "build"; // build | prevTag | prevZero
			public float delaySeconds;
		}
	}

	// Play-mode half; shares the file with the static entry class on purpose (see IkHandFrameProbe.cs:
	// the file's MonoScript binds to the static class, so AddComponent accepts this Editor-folder type).

	/// <summary>Play-mode half of <see cref="RootMotionTraceProbe"/>.</summary>
	public class RootMotionTraceProbeRunner : MonoBehaviour
	{
		private struct PreSample { }
		private struct PostSample { }

		private class TagEvent
		{
			public RootMotionTraceProbe.TagDef def;
			public int layerIndex = -1;
			public int frame = -1;
			public double t;
			public int firstPositiveFrame = -1;
			public int zeroFrame = -1;
			public double zeroT;
			public string error;
		}

		private class Trace
		{
			public RootMotionTraceProbe.CaseDef def;
			public AnimatorSettingsProbe.Rec rec;
			public Animator animator;
			public Component ac;
			public Component kpr;
			public AnimationClip clip;
			public PlayableGraph graph;
			public bool graphStarted;
			public int graphStartFrame = -1;
			public Vector3 p0;
			public Quaternion r0;
			public double t;
			public int k;
			public int frames;
			public readonly StringBuilder sb = new StringBuilder(1 << 20);
			public readonly List<TagEvent> tags = new List<TagEvent>();
			public int nextTag;
			public bool armed;
			public int endFrame = -1;
			public bool finished;
			public string error = "";
			public string humanScale = "null";
			public string animatorValues = "null";
			public string kprState = "null";
			public string controllerAtFrame1 = "";
			public string overridesAtEnd = "";
			// pre-sample stash (written out with the post sample of the same frame)
			public Vector3 preP;
			public Quaternion preQ;
			public string preExtra;
		}

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
		private static RootMotionTraceProbeRunner active;
		private static readonly string[] StateNames = { "Idle_default", "Empty", "Body_0", "Body_1", "Arms_0", "Arms_1", "Face_0", "Face_1", "BlendTree" };
		private static readonly Dictionary<int, string> StateByHash = StateNames.ToDictionary(Animator.StringToHash, n => n);

		private RootMotionTraceProbe.Pending pending;
		private AnimatorSettingsProbe.Capture capture;
		private RootMotionTraceProbe.Spec spec;
		private readonly List<Trace> traces = new List<Trace>();
		private int buildFrameCount;
		private bool done;
		private string playerLoopOrder = "";
		private Type acType;
		private MethodInfo miLayerIndex, miPlayClip, miSetWeight;
		private FieldInfo fiOverride;
		private FieldInfo[] kprFields;

		public void Begin(RootMotionTraceProbe.Pending p)
		{
			pending = p;
			active = this;
			capture = JsonUtility.FromJson<AnimatorSettingsProbe.Capture>(p.captureJson);
			spec = JsonUtility.FromJson<RootMotionTraceProbe.Spec>(p.specJson);
			Time.captureFramerate = 60; // fixed dt
			InstallPlayerLoop();
			acType = Type.GetType("AnimationController, Assembly-CSharp");
			if (acType != null)
			{
				const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
				miLayerIndex = acType.GetMethod("GetAnimatorLayerIndex", f);
				miPlayClip = acType.GetMethod("PlayAnimationClip", f);
				miSetWeight = acType.GetMethod("SetLayerWeight", f);
				fiOverride = acType.GetField("overrideController", f);
			}
			buildFrameCount = Time.frameCount;
			foreach (var def in spec.cases)
			{
				var tr = new Trace { def = def };
				traces.Add(tr);
				try
				{
					BuildCase(tr);
				}
				catch (Exception e)
				{
					tr.error = e.GetType().Name + ": " + e.Message;
					tr.finished = true;
				}
				if (tr.error.Length > 0) Debug.LogError($"[RootMotionTraceProbe] case {def.id}: {tr.error}");
			}
			Debug.Log($"[RootMotionTraceProbe] play: {traces.Count} cases, AnimationController type {(acType != null ? "found" : "MISSING")}, PlayerLoop PreLateUpdate: {playerLoopOrder}");
		}

		private void BuildCase(Trace tr)
		{
			var def = tr.def;
			tr.rec = capture.recs.FirstOrDefault(r => r.scene.Contains(def.scene) && r.hierarchyPath == def.path);
			if (tr.rec == null) { tr.error = $"no Animator '{def.path}' in a scene matching '{def.scene}'"; tr.finished = true; return; }
			var animator = AnimatorSettingsProbeRunner.Build(tr.rec, def.kpr, out var err);
			if (!animator) { tr.error = err; tr.finished = true; return; }
			animator.gameObject.name = def.id + " (" + animator.gameObject.name + ")";
			animator.logWarnings = false; // bodyPosition is read outside OnAnimatorIK on purpose (after the animation update)
			tr.animator = animator;
			tr.p0 = animator.transform.position;
			tr.r0 = animator.transform.rotation;
			tr.kpr = animator.GetComponents<MonoBehaviour>().FirstOrDefault(c => c && c.GetType().Name == "KeepPositionRotation");
			if (tr.kpr && kprFields == null)
			{
				const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
				var kt = tr.kpr.GetType();
				kprFields = new[] { "startPosition", "startRotation", "isLerpingBack", "lerpTarget", "isLerpingRotation", "lerpRotationTarget" }.Select(n => kt.GetField(n, f)).ToArray();
			}
			if (def.kind == "clip")
			{
				tr.clip = AnimatorSettingsProbeRunner.LoadClip(def.clip);
				if (!tr.clip) { tr.error = "clip not found: " + def.clip; tr.finished = true; }
				return; // culling + PlayClip at startFrame, exactly like T018b
			}
			animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; // no camera in batch mode
			if (acType == null || miPlayClip == null || miSetWeight == null || miLayerIndex == null)
			{
				tr.error = "AnimationController (Assembly-CSharp) or its private methods not found";
				tr.finished = true;
				return;
			}
			var host = new GameObject("AnimationController (" + def.id + ")");
			host.SetActive(false);
			var ac = host.AddComponent(acType);
			const BindingFlags bf = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
			acType.GetField("animator", bf).SetValue(ac, animator);
			acType.GetField("_animationsTestMode", bf).SetValue(ac, true);
			acType.GetField("blendDuration", bf).SetValue(ac, def.blendDuration);
			host.SetActive(true); // Awake: test mode → InitializeAnimator() (AnimatorOverrideController) only
			tr.ac = ac;
			foreach (var td in def.tags) tr.tags.Add(new TagEvent { def = td });
		}

		// ---------------------------------------------------------------- player loop

		private void InstallPlayerLoop()
		{
			var loop = PlayerLoop.GetCurrentPlayerLoop();
			var subs = loop.subSystemList;
			for (var i = 0; i < subs.Length; i++)
			{
				if (subs[i].type != typeof(PreLateUpdate)) continue;
				var list = subs[i].subSystemList.ToList();
				var idx = list.FindIndex(s => s.type == typeof(PreLateUpdate.ScriptRunBehaviourLateUpdate));
				if (idx < 0) throw new InvalidOperationException("PreLateUpdate.ScriptRunBehaviourLateUpdate not found");
				list.Insert(idx + 1, new PlayerLoopSystem { type = typeof(PostSample), updateDelegate = () => { if (active) active.SamplePost(); } });
				list.Insert(idx, new PlayerLoopSystem { type = typeof(PreSample), updateDelegate = () => { if (active) active.SamplePre(); } });
				subs[i].subSystemList = list.ToArray();
				playerLoopOrder = string.Join(",", list.Select(s => s.type != null ? s.type.Name : "?"));
			}
			loop.subSystemList = subs;
			PlayerLoop.SetPlayerLoop(loop);
		}

		// ---------------------------------------------------------------- Update: case drivers

		private void Update()
		{
			if (done) return;
			var k = Time.frameCount - buildFrameCount;
			foreach (var tr in traces)
			{
				if (tr.finished) continue;
				try
				{
					if (tr.def.kind == "clip")
					{
						if (!tr.graphStarted && k >= tr.def.startFrame)
						{
							tr.animator.transform.SetPositionAndRotation(tr.p0, tr.r0);
							tr.animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
							AnimationPlayableUtilities.PlayClip(tr.animator, tr.clip, out tr.graph);
							tr.graphStarted = true;
							tr.graphStartFrame = k;
						}
						continue;
					}
					while (tr.nextTag < tr.tags.Count && Due(tr, tr.tags[tr.nextTag], k))
					{
						Fire(tr, tr.tags[tr.nextTag], k);
						tr.nextTag++;
					}
				}
				catch (Exception e)
				{
					tr.error += (tr.error.Length > 0 ? "; " : "") + e.GetType().Name + ": " + (e.InnerException ?? e).Message;
					tr.finished = true;
				}
			}
		}

		private static bool Due(Trace tr, TagEvent te, int k)
		{
			var d = Mathf.RoundToInt(te.def.delaySeconds * 60f);
			switch (te.def.after)
			{
				case "build": return k >= d + 1; // d animation frames of base state before the tag frame
				case "prevTag": return tr.nextTag > 0 && k >= tr.tags[tr.nextTag - 1].frame + d;
				case "prevZero": return tr.nextTag > 0 && tr.tags[tr.nextTag - 1].zeroFrame >= 0 && k >= tr.tags[tr.nextTag - 1].zeroFrame + d;
				default: throw new ArgumentException("unknown tag 'after': " + te.def.after);
			}
		}

		private void Fire(Trace tr, TagEvent te, int k)
		{
			var clip = AnimatorSettingsProbeRunner.LoadClip(te.def.clip);
			te.frame = k;
			te.t = tr.t + Time.deltaTime; // time of this frame's samples
			if (!clip) { te.error = "clip not found: " + te.def.clip; tr.error += te.error; return; }
			// = AnimationController.PlayAnimation's per-layer loop body (tag string parsing + bundle lookup skipped)
			te.layerIndex = (int)miLayerIndex.Invoke(tr.ac, new object[] { te.def.layer });
			miPlayClip.Invoke(tr.ac, new object[] { te.def.layer, clip, te.layerIndex, te.def.speed });
			miSetWeight.Invoke(tr.ac, new object[] { te.layerIndex, te.def.weight });
			tr.armed = false;
			Debug.Log($"[RootMotionTraceProbe] {tr.def.id}: tag {te.def.layer}/{clip.name} w{te.def.weight} s{te.def.speed} at frame {k} (layer {te.layerIndex})");
		}

		// ---------------------------------------------------------------- samples

		private void SamplePre()
		{
			if (done) return;
			var k = Time.frameCount - buildFrameCount;
			foreach (var tr in traces)
			{
				if (tr.finished || !tr.animator) continue;
				if (tr.k == 0 && tr.frames == 0) WriteBuildSample(tr);
				tr.k = k;
				tr.t += Time.deltaTime;
				var a = tr.animator;
				var x = a.transform;
				tr.preP = x.position;
				tr.preQ = x.rotation;
				var sb = new StringBuilder(512);
				sb.Append(",\"dp\":").Append(V(a.deltaPosition)).Append(",\"dq\":").Append(Q(a.deltaRotation));
				if (a.isHuman)
				{
					sb.Append(",\"bp\":").Append(V(a.bodyPosition)).Append(",\"bq\":").Append(Q(a.bodyRotation));
					var hips = a.GetBoneTransform(HumanBodyBones.Hips);
					if (hips) sb.Append(",\"hp\":").Append(V(hips.position)).Append(",\"hq\":").Append(Q(hips.rotation));
					var lf = a.GetBoneTransform(HumanBodyBones.LeftFoot);
					var rf = a.GetBoneTransform(HumanBodyBones.RightFoot);
					if (lf && rf) sb.Append(",\"lf\":").Append(V(lf.position)).Append(",\"rf\":").Append(V(rf.position));
					sb.Append(",\"pv\":").Append(V(a.pivotPosition)).Append(",\"pw\":").Append(N(a.pivotWeight));
				}
				if (tr.def.kind == "clip")
				{
					if (tr.graphStarted && tr.graph.IsValid()) sb.Append(",\"ct\":").Append(N(tr.graph.GetRootPlayable(0).GetTime()));
				}
				else
				{
					sb.Append(",\"w\":[");
					for (var i = 1; i < a.layerCount; i++) sb.Append(i > 1 ? "," : "").Append(N(a.GetLayerWeight(i)));
					sb.Append("],\"L\":[");
					for (var i = 0; i < a.layerCount; i++)
					{
						if (i > 0) sb.Append(',');
						AppendLayer(sb, a, i);
					}
					sb.Append(']');
				}
				tr.preExtra = sb.ToString();
				if (k == 1)
				{
					tr.humanScale = N(a.isHuman ? a.humanScale : 0f);
					tr.animatorValues = JsonUtility.ToJson(AnimatorSettingsProbe.Read(a));
					tr.controllerAtFrame1 = a.runtimeAnimatorController ? a.runtimeAnimatorController.GetType().Name + ":" + a.runtimeAnimatorController.name : "";
				}
			}
		}

		private static void AppendLayer(StringBuilder sb, Animator a, int i)
		{
			var cur = a.GetCurrentAnimatorStateInfo(i);
			var inTr = a.IsInTransition(i);
			var nxt = a.GetNextAnimatorStateInfo(i);
			var trn = a.GetAnimatorTransitionInfo(i);
			var ci = a.GetCurrentAnimatorClipInfo(i);
			var ni = a.GetNextAnimatorClipInfo(i);
			sb.Append('[').Append(S(StateName(cur.shortNameHash))).Append(',').Append(N(cur.normalizedTime)).Append(',').Append(inTr ? 1 : 0)
				.Append(',').Append(S(inTr ? StateName(nxt.shortNameHash) : "")).Append(',').Append(N(inTr ? nxt.normalizedTime : 0f))
				.Append(',').Append(N(inTr ? trn.normalizedTime : 0f))
				.Append(',').Append(S(ClipName(ci))).Append(',').Append(S(ClipName(ni)))
				.Append(',').Append(N(cur.speed * cur.speedMultiplier)).Append(']');
		}

		private static string ClipName(AnimatorClipInfo[] infos) =>
			infos == null || infos.Length == 0 ? "" : string.Join("+", infos.Select(c => (c.clip ? c.clip.name : "?") + (infos.Length > 1 ? "@" + N(c.weight) : "")));

		private static string StateName(int hash) => StateByHash.TryGetValue(hash, out var n) ? n : "#" + hash.ToString(Inv);

		private void WriteBuildSample(Trace tr)
		{
			var pose = PoseJson(tr, tr.p0, tr.r0);
			tr.sb.Append("{\"k\":0,\"t\":0,\"dt\":0,\"pre\":").Append(pose).Append(",\"post\":").Append(pose).Append('}');
			tr.frames++;
		}

		private void SamplePost()
		{
			if (done) return;
			try
			{
				var allDone = true;
				foreach (var tr in traces)
				{
					if (tr.finished || !tr.animator || tr.preExtra == null) { allDone &= tr.finished; continue; }
					var a = tr.animator;
					var x = a.transform;
					var sb = tr.sb;
					sb.Append(",\n{\"k\":").Append(tr.k).Append(",\"t\":").Append(N(tr.t)).Append(",\"dt\":").Append(N(Time.deltaTime))
						.Append(",\"pre\":").Append(PoseJson(tr, tr.preP, tr.preQ))
						.Append(",\"post\":").Append(PoseJson(tr, x.position, x.rotation))
						.Append(tr.preExtra);
					if (tr.kpr && kprFields != null && kprFields[2] != null)
						sb.Append(",\"kpr\":[").Append((bool)kprFields[2].GetValue(tr.kpr) ? 1 : 0).Append(',').Append((bool)kprFields[4].GetValue(tr.kpr) ? 1 : 0).Append(']');
					sb.Append('}');
					tr.frames++;
					tr.preExtra = null;
					if (tr.k == 1 && tr.kpr && kprFields != null && kprFields[0] != null)
						tr.kprState = "{\"serialized\":" + EditorJsonUtility.ToJson(tr.kpr) + ",\"startPosition\":" + V((Vector3)kprFields[0].GetValue(tr.kpr)) + ",\"startRotation\":" + Q((Quaternion)kprFields[1].GetValue(tr.kpr)) + "}";
					CheckEnd(tr);
					allDone &= tr.finished;
				}
				if (allDone) Finish();
			}
			catch (Exception e)
			{
				Debug.LogError("[RootMotionTraceProbe] FAIL play: " + e);
				done = true;
				EditorApplication.Exit(1);
			}
		}

		private void CheckEnd(Trace tr)
		{
			var def = tr.def;
			if (def.kind == "clip")
			{
				if (tr.graphStarted && tr.graph.GetRootPlayable(0).GetTime() >= tr.clip.length + def.tailSeconds) End(tr);
			}
			else if (def.kind == "idle")
			{
				if (tr.t >= def.durationSeconds - 1e-6) End(tr);
			}
			else
			{
				var last = tr.nextTag > 0 ? tr.tags[tr.nextTag - 1] : null;
				if (last != null && last.layerIndex >= 0)
				{
					var w = tr.animator.GetLayerWeight(last.layerIndex);
					if (w > 0f && !tr.armed) { tr.armed = true; if (last.firstPositiveFrame < 0) last.firstPositiveFrame = tr.k; }
					else if (w == 0f && tr.armed && last.zeroFrame < 0) { last.zeroFrame = tr.k; last.zeroT = tr.t; tr.armed = false; }
				}
				if (tr.nextTag == tr.tags.Count && last != null && last.zeroFrame >= 0 && tr.k >= last.zeroFrame + Mathf.RoundToInt(def.tailSeconds * 60f)) End(tr);
			}
			if (!tr.finished && tr.t > def.timeoutSeconds)
			{
				tr.error += (tr.error.Length > 0 ? "; " : "") + "timeout after " + def.timeoutSeconds + " s";
				End(tr);
			}
		}

		private void End(Trace tr)
		{
			tr.finished = true;
			tr.endFrame = tr.k;
			if (tr.ac && fiOverride != null && fiOverride.GetValue(tr.ac) is AnimatorOverrideController oc)
			{
				var list = new List<KeyValuePair<AnimationClip, AnimationClip>>();
				oc.GetOverrides(list);
				tr.overridesAtEnd = string.Join(";", list.Select(p => (p.Key ? p.Key.name : "?") + "=" + (p.Value ? p.Value.name : "")));
			}
			tr.animator.enabled = false; // freeze: the case is complete
			Debug.Log($"[RootMotionTraceProbe] {tr.def.id}: done at frame {tr.k} (t {tr.t:0.###} s), {tr.frames} samples {tr.error}");
		}

		// ---------------------------------------------------------------- output

		private void Finish()
		{
			done = true;
			active = null;
			var outDir = pending.outDir;
			Directory.CreateDirectory(outDir);
			var caseBodies = traces.Select(CaseJson).ToList();
			var total = caseBodies.Sum(b => (long)b.Length);
			var split = total > 15_000_000L;
			var sb = new StringBuilder();
			sb.Append("{\n\"meta\":").Append(MetaJson(split)).Append(",\n\"clips\":").Append(ClipsJson())
				.Append(",\n\"masks\":").Append(MasksJson()).Append(",\n\"cases\":[\n");
			if (split)
			{
				var dir = Path.Combine(outDir, "cases");
				Directory.CreateDirectory(dir);
				for (var i = 0; i < traces.Count; i++)
				{
					File.WriteAllText(Path.Combine(dir, traces[i].def.id + ".json"), caseBodies[i]);
					sb.Append(i > 0 ? ",\n" : "").Append(CaseHeaderJson(traces[i], "cases/" + traces[i].def.id + ".json"));
				}
			}
			else
			{
				for (var i = 0; i < traces.Count; i++) sb.Append(i > 0 ? ",\n" : "").Append(caseBodies[i]);
			}
			sb.Append("\n]\n}\n");
			var path = Path.Combine(outDir, "unity-reference.json");
			File.WriteAllText(path, sb.ToString());
			var failed = traces.Where(t => t.error.Length > 0).Select(t => t.def.id + ": " + t.error).ToList();
			Debug.Log($"[RootMotionTraceProbe] wrote {path} ({(split ? "split into cases/" : "single file")}, {total / 1e6:0.0} MB of frames); failed cases: {(failed.Count == 0 ? "none" : string.Join(" | ", failed))}");
			EditorApplication.Exit(0);
		}

		private string MetaJson(bool split) =>
			"{\"unity\":" + S(Application.unityVersion) + ",\"date\":" + S(DateTime.UtcNow.ToString("o", Inv)) + ",\"spec\":" + S(pending.specPath)
			+ ",\"captureFramerate\":60,\"split\":" + (split ? "true" : "false")
			+ ",\"playerLoopPreLateUpdate\":" + S(playerLoopOrder)
			+ ",\"samplePoints\":{\"pre\":\"PlayerLoop system inserted right BEFORE PreLateUpdate.ScriptRunBehaviourLateUpdate (after the animation update, before any LateUpdate incl. KeepPositionRotation)\",\"post\":\"PlayerLoop system inserted right AFTER PreLateUpdate.ScriptRunBehaviourLateUpdate (after every LateUpdate)\"}"
			+ ",\"convention\":\"rp = mirrorX(inv(R0)*(p-p0)); rq = (q'.x,-q'.y,-q'.z,q'.w) with q' = inv(R0)*q; yaw = atan2(f.x,f.z) deg, f = rq*(0,0,1) (three/glTF convention); p0,R0 = GO world pose when the rig was built (scene pose)\""
			+ ",\"bodySample\":\"bp/bq = Animator.bodyPosition/bodyRotation, hp/hq = Hips bone Transform position/rotation, lf/rf = LeftFoot/RightFoot bone positions, pv = Animator.pivotPosition (all world), pw = Animator.pivotWeight, at the pre sample\""
			+ ",\"layerTuple\":\"[state, normalizedTime, inTransition, nextState, nextNormalizedTime, transitionNormalizedTime, currentClip(s), nextClip(s), state speed*speedMultiplier]\"}";

		private string ClipsJson()
		{
			var paths = spec.cases.Select(c => c.clip).Concat(spec.cases.SelectMany(c => c.tags.Select(t => t.clip)))
				.Append("Assets/_Project/AnimationSystem/AnimatorController/Idle_default.anim")
				.Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
			var sb = new StringBuilder("{");
			var first = true;
			foreach (var p in paths)
			{
				var c = AnimatorSettingsProbeRunner.LoadClip(p);
				if (!c) continue;
				var s = AnimationUtility.GetAnimationClipSettings(c);
				sb.Append(first ? "\n" : ",\n").Append(S(c.name)).Append(":{\"path\":").Append(S(p))
					.Append(",\"length\":").Append(N(c.length)).Append(",\"frameRate\":").Append(N(c.frameRate)).Append(",\"isLooping\":").Append(B(c.isLooping))
					.Append(",\"wrapMode\":").Append(S(c.wrapMode.ToString())).Append(",\"isHumanMotion\":").Append(B(c.isHumanMotion))
					.Append(",\"hasRootCurves\":").Append(B(c.hasRootCurves)).Append(",\"hasMotionCurves\":").Append(B(c.hasMotionCurves))
					.Append(",\"hasGenericRootTransform\":").Append(B(c.hasGenericRootTransform)).Append(",\"hasMotionFloatCurves\":").Append(B(c.hasMotionFloatCurves))
					.Append(",\"averageSpeed\":").Append(V(c.averageSpeed)).Append(",\"averageAngularSpeed\":").Append(N(c.averageAngularSpeed))
					.Append(",\"apparentSpeed\":").Append(N(c.apparentSpeed)).Append(",\"averageDuration\":").Append(N(c.averageDuration))
					.Append(",\"settings\":{\"loopTime\":").Append(B(s.loopTime)).Append(",\"loopBlend\":").Append(B(s.loopBlend))
					.Append(",\"cycleOffset\":").Append(N(s.cycleOffset)).Append(",\"loopBlendOrientation\":").Append(B(s.loopBlendOrientation))
					.Append(",\"loopBlendPositionY\":").Append(B(s.loopBlendPositionY)).Append(",\"loopBlendPositionXZ\":").Append(B(s.loopBlendPositionXZ))
					.Append(",\"keepOriginalOrientation\":").Append(B(s.keepOriginalOrientation)).Append(",\"keepOriginalPositionY\":").Append(B(s.keepOriginalPositionY))
					.Append(",\"keepOriginalPositionXZ\":").Append(B(s.keepOriginalPositionXZ)).Append(",\"heightFromFeet\":").Append(B(s.heightFromFeet))
					.Append(",\"mirror\":").Append(B(s.mirror)).Append(",\"orientationOffsetY\":").Append(N(s.orientationOffsetY)).Append(",\"level\":").Append(N(s.level))
					.Append(",\"startTime\":").Append(N(s.startTime)).Append(",\"stopTime\":").Append(N(s.stopTime)).Append("}}");
				first = false;
			}
			return sb.Append("\n}").ToString();
		}

		private static string MasksJson()
		{
			var sb = new StringBuilder("{");
			var first = true;
			foreach (var name in new[] { "Mask_BodyNoHead", "Mask_Arms", "Mask_Head" })
			{
				var path = "Assets/_Project/AnimationSystem/AnimatorMasks/" + name + ".mask";
				var m = AssetDatabase.LoadAssetAtPath<AvatarMask>(path);
				sb.Append(first ? "\n" : ",\n").Append(S(name)).Append(':');
				first = false;
				if (!m) { sb.Append("null"); continue; }
				sb.Append("{\"path\":").Append(S(path)).Append(",\"parts\":{");
				for (var i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++)
					sb.Append(i > 0 ? "," : "").Append(S(((AvatarMaskBodyPart)i).ToString())).Append(':').Append(B(m.GetHumanoidBodyPartActive((AvatarMaskBodyPart)i)));
				sb.Append("},\"transformCount\":").Append(m.transformCount).Append('}');
			}
			return sb.Append("\n}").ToString();
		}

		private string CaseHeaderJson(Trace tr, string file)
		{
			var d = tr.def;
			var sb = new StringBuilder("{");
			sb.Append("\"id\":").Append(S(d.id)).Append(",\"kind\":").Append(S(d.kind)).Append(",\"scene\":").Append(S(tr.rec != null ? tr.rec.scene : d.scene))
				.Append(",\"path\":").Append(S(d.path)).Append(",\"rigAsset\":").Append(S(tr.rec != null ? tr.rec.nearestPrefabPath : ""))
				.Append(",\"kpr\":").Append(B(d.kpr)).Append(",\"kprPresent\":").Append(B(tr.kpr)).Append(",\"kprState\":").Append(tr.kprState)
				.Append(",\"clip\":").Append(S(tr.clip ? tr.clip.name : "")).Append(",\"startFrame\":").Append(d.kind == "clip" ? tr.graphStartFrame : -1)
				.Append(",\"p0\":").Append(V(tr.p0)).Append(",\"R0\":").Append(Q(tr.r0))
				.Append(",\"humanScale\":").Append(tr.humanScale).Append(",\"stabilizeFeet\":").Append(tr.animator ? B(tr.animator.stabilizeFeet) : "null").Append(",\"animator\":").Append(tr.animatorValues)
				.Append(",\"controllerAtFrame1\":").Append(S(tr.controllerAtFrame1)).Append(",\"overridesAtEnd\":").Append(S(tr.overridesAtEnd))
				.Append(",\"tags\":[");
			for (var i = 0; i < tr.tags.Count; i++)
			{
				var te = tr.tags[i];
				sb.Append(i > 0 ? "," : "").Append("{\"layer\":").Append(S(te.def.layer)).Append(",\"layerIndex\":").Append(te.layerIndex)
					.Append(",\"clip\":").Append(S(Path.GetFileNameWithoutExtension(te.def.clip))).Append(",\"weight\":").Append(N(te.def.weight)).Append(",\"speed\":").Append(N(te.def.speed))
					.Append(",\"after\":").Append(S(te.def.after)).Append(",\"delaySeconds\":").Append(N(te.def.delaySeconds))
					.Append(",\"frame\":").Append(te.frame).Append(",\"t\":").Append(N(te.t)).Append(",\"firstPositiveFrame\":").Append(te.firstPositiveFrame)
					.Append(",\"zeroFrame\":").Append(te.zeroFrame).Append(",\"zeroT\":").Append(N(te.zeroT)).Append(",\"error\":").Append(S(te.error ?? "")).Append('}');
			}
			sb.Append("],\"endFrame\":").Append(tr.endFrame).Append(",\"samples\":").Append(tr.frames).Append(",\"error\":").Append(S(tr.error));
			if (file != null) sb.Append(",\"file\":").Append(S(file)).Append('}');
			return sb.ToString();
		}

		private string CaseJson(Trace tr) => CaseHeaderJson(tr, null) + ",\"frames\":[\n" + tr.sb + "\n]}";

		// ---------------------------------------------------------------- formatting

		private static string PoseJson(Trace tr, Vector3 p, Quaternion q)
		{
			var inv = Quaternion.Inverse(tr.r0);
			var ru = inv * (p - tr.p0);
			var qu = inv * q;
			var rp = new Vector3(-ru.x, ru.y, ru.z);
			var rq = new Quaternion(qu.x, -qu.y, -qu.z, qu.w);
			double fx = 2.0 * (rq.x * rq.z + rq.w * rq.y), fz = 1.0 - 2.0 * (rq.x * rq.x + rq.y * rq.y);
			var yaw = Math.Atan2(fx, fz) * 180.0 / Math.PI;
			return "{\"p\":" + V(p) + ",\"q\":" + Q(q) + ",\"rp\":" + V(rp) + ",\"rq\":" + Q(rq) + ",\"yaw\":" + N(yaw) + "}";
		}

		private static string N(double v)
		{
			if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
			var r = Math.Round(v, 6);
			if (r == 0) r = 0; // no "-0"
			return r.ToString("0.######", Inv);
		}

		private static string V(Vector3 v) => "[" + N(v.x) + "," + N(v.y) + "," + N(v.z) + "]";
		private static string Q(Quaternion q) => "[" + N(q.x) + "," + N(q.y) + "," + N(q.z) + "," + N(q.w) + "]";
		private static string B(bool b) => b ? "true" : "false";

		private static string S(string s)
		{
			if (s == null) return "null";
			var sb = new StringBuilder("\"");
			foreach (var ch in s)
			{
				switch (ch)
				{
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': sb.Append("\\r"); break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", Inv));
						else sb.Append(ch);
						break;
				}
			}
			return sb.Append('"').ToString();
		}
	}
}
