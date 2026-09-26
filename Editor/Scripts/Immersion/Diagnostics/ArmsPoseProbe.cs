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
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace Immersion.Export
{
	/// <summary>
	/// Full-skeleton + skinned-mesh reference of a logic-scene character playing ONE prompt tag through the
	/// project's own <c>AnimationController</c> (anim-v2 follow-up: Dismisser <c>egocentric-arms-crossed-m</c>
	/// hand/forearm penetration). Same tag path as <see cref="RootMotionTraceProbe"/> (DynamicAnimator + AC on an
	/// inactive host in test mode, the three private layer-loop calls by reflection, fixed 60 fps).
	///
	/// Edit mode: opens the scene, finds the Animator at <c>-path</c>, records every descendant's scene-local TRS
	/// (the prefab instance's overrides included), its components, and the diff against the source model asset.
	/// Play mode: per case builds the rig from the model asset at the scene pose; <c>sceneLocals</c> copies the
	/// scene-local TRS onto it and rebinds the Animator (so non-humanoid bones keep their scene values).
	/// Every <c>sampleEvery</c> frames after the tag (sampled after every LateUpdate) it writes the pose of every
	/// transform relative to the Animator GameObject in glTF convention (rp = mirrorX(inv(R)·(p−P)),
	/// rq = (q'.x,−q'.y,−q'.z,q'.w), q' = inv(R)·q, R/P = the Animator GO's CURRENT world pose, so root motion is
	/// factored out), and every <c>bakeEvery</c> frames <c>SkinnedMeshRenderer.BakeMesh</c> vertices in the same
	/// frame (float32 LE xyz, <c>bakes/&lt;case&gt;_&lt;k&gt;_&lt;smr&gt;.bin</c>). Per SMR once: dominant bone per
	/// vertex + its weight and the triangle list (<c>mesh_&lt;case&gt;_&lt;smr&gt;.json</c>).
	///
	/// Unity.exe -batchmode -nographics -projectPath &lt;proj&gt; -executeMethod Immersion.Export.ArmsPoseProbe.Run
	///   -scene "Assets/…/Logic_X.unity" -path "&gt;&gt;&gt; Avatars &lt;&lt;&lt;/Dismisser" -spec "C:/dir/spec.json" -out "C:/dir"
	/// (no -quit — the probe exits by itself; exit 0 = written, 1 = failed)
	/// </summary>
	public static class ArmsPoseProbe
	{
		private const string Flag = "Immersion.ArmsPoseProbe.pending";

		public static void Run()
		{
			var scene = CliArgs.Get("-scene");
			var path = CliArgs.Get("-path");
			var outDir = CliArgs.Get("-out");
			var specPath = CliArgs.Get("-spec");
			if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(outDir) || string.IsNullOrEmpty(specPath) || !File.Exists(specPath))
			{
				Debug.LogError("[ArmsPoseProbe] usage: -scene <logic.unity> -path <animator hierarchy path> -spec <spec.json> -out <dir>");
				CliArgs.Exit(1);
				return;
			}
			var capture = new AnimatorSettingsProbe.Capture { outDir = outDir, unity = Application.unityVersion, playClips = "" };
			var locals = new SceneLocals();
			try
			{
				AnimatorSettingsProbe.CaptureScene(scene, capture); // opens the scene (Single)
				var animator = SceneManagementFind(path);
				if (!animator) throw new InvalidOperationException("no Animator at " + path);
				locals = RecordSceneLocals(animator);
				Directory.CreateDirectory(outDir);
				File.WriteAllText(Path.Combine(outDir, "scene-locals.json"), JsonUtility.ToJson(locals, true));
			}
			catch (Exception e)
			{
				Debug.LogError("[ArmsPoseProbe] FAIL capture: " + e);
				CliArgs.Exit(1);
				return;
			}
			var pending = new Pending
			{
				outDir = outDir,
				path = path,
				specJson = File.ReadAllText(specPath),
				captureJson = JsonUtility.ToJson(capture),
				localsJson = JsonUtility.ToJson(locals),
			};
			SessionState.SetString(Flag, JsonUtility.ToJson(pending));
			EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single); // closed unsaved
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
				new GameObject("ArmsPoseProbeHost").AddComponent<ArmsPoseProbeRunner>().Begin(JsonUtility.FromJson<Pending>(raw));
			};
		}

		private static Animator SceneManagementFind(string hierarchyPath)
		{
			var scene = EditorSceneManager.GetActiveScene();
			return scene.GetRootGameObjects().SelectMany(r => r.GetComponentsInChildren<Animator>(true))
				.FirstOrDefault(a => IkHandFrameProbe.HierarchyPath(a.transform) == hierarchyPath);
		}

		private static SceneLocals RecordSceneLocals(Animator a)
		{
			var res = new SceneLocals { animatorPath = IkHandFrameProbe.HierarchyPath(a.transform) };
			var source = PrefabUtility.GetCorrespondingObjectFromOriginalSource(a.gameObject);
			foreach (var t in a.GetComponentsInChildren<Transform>(true))
			{
				if (t == a.transform) continue;
				var rel = AnimationUtility.CalculateTransformPath(t, a.transform);
				var e = new LocalEntry
				{
					path = rel,
					pos = t.localPosition,
					rot = t.localRotation,
					scale = t.localScale,
					active = t.gameObject.activeSelf,
					components = string.Join(";", t.GetComponents<Component>().Where(c => !(c is Transform)).Select(c => c ? c.GetType().FullName : "<missing>")),
					addedInScene = !PrefabUtility.IsPartOfPrefabInstance(t) || PrefabUtility.IsAddedGameObjectOverride(t.gameObject),
				};
				if (source)
				{
					var st = source.transform.Find(rel);
					if (st)
					{
						e.inSource = true;
						e.dPos = (st.localPosition - t.localPosition).magnitude;
						e.dRotDeg = Quaternion.Angle(st.localRotation, t.localRotation);
						e.dScale = (st.localScale - t.localScale).magnitude;
					}
				}
				res.entries.Add(e);
			}
			if (a.isHuman)
			{
				for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
				{
					var bt = a.GetBoneTransform(b);
					if (bt) res.humanBones.Add(b + "=" + bt.name);
				}
			}
			return res;
		}

		[Serializable]
		public class Pending
		{
			public string outDir;
			public string path;
			public string specJson;
			public string captureJson;
			public string localsJson;
		}

		[Serializable]
		public class SceneLocals
		{
			public string animatorPath;
			public List<LocalEntry> entries = new List<LocalEntry>();
			public List<string> humanBones = new List<string>();
		}

		[Serializable]
		public class LocalEntry
		{
			public string path;
			public Vector3 pos;
			public Quaternion rot;
			public Vector3 scale;
			public bool active;
			public string components;
			public bool addedInScene;
			public bool inSource;
			public float dPos;
			public float dRotDeg;
			public float dScale;
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
			public bool sceneLocals = true;
			public string layer = "Body";
			public string clip;
			public float weight = 1f;
			public float speed = 1f;
			public float preRollSeconds = 2f;
			public float blendDuration = 1f;
			public int sampleEvery = 6;
			public int bakeEvery = 30;
			public float tailSeconds = 0.5f;
			public float timeoutSeconds = 60f;
		}
	}

	/// <summary>Play-mode half of <see cref="ArmsPoseProbe"/> (same file on purpose, see IkHandFrameProbe.cs).</summary>
	public class ArmsPoseProbeRunner : MonoBehaviour
	{
		private struct PostSample { }

		private class Trace
		{
			public ArmsPoseProbe.CaseDef def;
			public Animator animator;
			public Component ac;
			public AnimationClip clip;
			public int layerIndex = -1;
			public int tagFrame;
			public int endFrame = -1;
			public bool finished;
			public string error = "";
			public Transform[] nodes;
			public string[] nodePaths;
			public SkinnedMeshRenderer[] smrs;
			public Mesh bakeMesh;
			public readonly StringBuilder samples = new StringBuilder(1 << 20);
			public readonly List<string> bakes = new List<string>();
			public int sampleCount;
			public string appliedLocals = "";
		}

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
		private static ArmsPoseProbeRunner active;
		private ArmsPoseProbe.Pending pending;
		private readonly List<Trace> traces = new List<Trace>();
		private int buildFrame;
		private bool done;
		private Type acType;
		private MethodInfo miLayerIndex, miPlayClip, miSetWeight;

		public void Begin(ArmsPoseProbe.Pending p)
		{
			pending = p;
			active = this;
			Time.captureFramerate = 60;
			InstallPlayerLoop();
			var capture = JsonUtility.FromJson<AnimatorSettingsProbe.Capture>(p.captureJson);
			var locals = JsonUtility.FromJson<ArmsPoseProbe.SceneLocals>(p.localsJson);
			var spec = JsonUtility.FromJson<ArmsPoseProbe.Spec>(p.specJson);
			acType = Type.GetType("AnimationController, Assembly-CSharp");
			const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
			if (acType != null)
			{
				miLayerIndex = acType.GetMethod("GetAnimatorLayerIndex", f);
				miPlayClip = acType.GetMethod("PlayAnimationClip", f);
				miSetWeight = acType.GetMethod("SetLayerWeight", f);
			}
			buildFrame = Time.frameCount;
			var rec = capture.recs.FirstOrDefault(r => r.hierarchyPath == p.path);
			foreach (var def in spec.cases)
			{
				var tr = new Trace { def = def };
				traces.Add(tr);
				try
				{
					if (rec == null) throw new InvalidOperationException("no captured Animator " + p.path);
					var animator = AnimatorSettingsProbeRunner.Build(rec, false, out var err);
					if (!animator) throw new InvalidOperationException(err);
					animator.gameObject.name = def.id + " (" + animator.gameObject.name + ")";
					if (def.sceneLocals)
					{
						var n = 0;
						foreach (var e in locals.entries)
						{
							if (!e.inSource) continue;
							var t = animator.transform.Find(e.path);
							if (!t) continue;
							t.localPosition = e.pos;
							t.localRotation = e.rot;
							t.localScale = e.scale;
							n++;
						}
						animator.Rebind();
						tr.appliedLocals = n + " transforms";
					}
					animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
					animator.logWarnings = false;
					tr.animator = animator;
					tr.nodes = animator.GetComponentsInChildren<Transform>(true);
					tr.nodePaths = tr.nodes.Select(t => t == animator.transform ? "" : AnimationUtility.CalculateTransformPath(t, animator.transform)).ToArray();
					tr.smrs = animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
					tr.bakeMesh = new Mesh();
					tr.clip = AnimatorSettingsProbeRunner.LoadClip(def.clip);
					if (!tr.clip) throw new InvalidOperationException("clip not found: " + def.clip);
					if (acType == null || miPlayClip == null || miSetWeight == null || miLayerIndex == null) throw new InvalidOperationException("AnimationController or its private methods not found");
					var host = new GameObject("AnimationController (" + def.id + ")");
					host.SetActive(false);
					var ac = host.AddComponent(acType);
					acType.GetField("animator", f).SetValue(ac, animator);
					acType.GetField("_animationsTestMode", f).SetValue(ac, true);
					acType.GetField("blendDuration", f).SetValue(ac, def.blendDuration);
					host.SetActive(true);
					tr.ac = ac;
					tr.tagFrame = Mathf.RoundToInt(def.preRollSeconds * 60f) + 1;
					WriteMeshInfo(tr);
				}
				catch (Exception e)
				{
					tr.error = e.GetType().Name + ": " + e.Message;
					tr.finished = true;
					Debug.LogError($"[ArmsPoseProbe] case {def.id}: {tr.error}");
				}
			}
			Debug.Log($"[ArmsPoseProbe] play: {traces.Count} cases");
		}

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
				subs[i].subSystemList = list.ToArray();
			}
			loop.subSystemList = subs;
			PlayerLoop.SetPlayerLoop(loop);
		}

		private void Update()
		{
			if (done) return;
			var k = Time.frameCount - buildFrame;
			foreach (var tr in traces)
			{
				if (tr.finished || k != tr.tagFrame) continue;
				try
				{
					tr.layerIndex = (int)miLayerIndex.Invoke(tr.ac, new object[] { tr.def.layer });
					miPlayClip.Invoke(tr.ac, new object[] { tr.def.layer, tr.clip, tr.layerIndex, tr.def.speed });
					miSetWeight.Invoke(tr.ac, new object[] { tr.layerIndex, tr.def.weight });
					Debug.Log($"[ArmsPoseProbe] {tr.def.id}: tag {tr.def.layer}/{tr.clip.name} w{tr.def.weight} s{tr.def.speed} at frame {k} (layer {tr.layerIndex}, locals {tr.appliedLocals})");
				}
				catch (Exception e)
				{
					tr.error += e.GetType().Name + ": " + (e.InnerException ?? e).Message;
					tr.finished = true;
				}
			}
		}

		private void SamplePost()
		{
			if (done) return;
			try
			{
				var k = Time.frameCount - buildFrame;
				var allDone = true;
				foreach (var tr in traces)
				{
					if (tr.finished) continue;
					var rel = k - tr.tagFrame;
					if (rel >= 0 && tr.layerIndex >= 0)
					{
						var a = tr.animator;
						var info = a.GetCurrentAnimatorStateInfo(tr.layerIndex);
						var inTr = a.IsInTransition(tr.layerIndex);
						var next = a.GetNextAnimatorStateInfo(tr.layerIndex);
						var clipInfo = a.GetCurrentAnimatorClipInfo(tr.layerIndex);
						var nextClip = a.GetNextAnimatorClipInfo(tr.layerIndex);
						var bodyIsCur = clipInfo.Length > 0 && clipInfo[0].clip == tr.clip;
						var bodyIsNext = inTr && nextClip.Length > 0 && nextClip[0].clip == tr.clip;
						var nt = bodyIsCur ? info.normalizedTime : bodyIsNext ? next.normalizedTime : -1f;
						var ct = nt < 0 ? -1f : Mathf.Min(nt, 1f) * tr.clip.length;
						if (rel % tr.def.sampleEvery == 0) WriteSample(tr, k, rel, nt, ct, inTr);
						if (rel % tr.def.bakeEvery == 0) Bake(tr, k, ct);
						if (nt >= 1f && tr.endFrame < 0) tr.endFrame = k;
						if (tr.endFrame >= 0 && k >= tr.endFrame + Mathf.RoundToInt(tr.def.tailSeconds * 60f)) tr.finished = true;
						if (rel / 60f > tr.def.timeoutSeconds) { tr.error += "timeout"; tr.finished = true; }
					}
					allDone &= tr.finished;
				}
				if (allDone) Finish();
			}
			catch (Exception e)
			{
				Debug.LogError("[ArmsPoseProbe] FAIL play: " + e);
				done = true;
				EditorApplication.Exit(1);
			}
		}

		private void WriteSample(Trace tr, int k, int rel, float nt, float ct, bool inTr)
		{
			var a = tr.animator;
			var R = a.transform.rotation;
			var P = a.transform.position;
			var inv = Quaternion.Inverse(R);
			var sb = tr.samples;
			sb.Append(tr.sampleCount > 0 ? ",\n" : "\n").Append("{\"k\":").Append(k).Append(",\"t\":").Append(N(rel / 60.0)).Append(",\"nt\":").Append(N(nt)).Append(",\"ct\":").Append(N(ct))
				.Append(",\"inTr\":").Append(inTr ? 1 : 0).Append(",\"w\":[");
			for (var i = 1; i < a.layerCount; i++) sb.Append(i > 1 ? "," : "").Append(N(a.GetLayerWeight(i)));
			sb.Append("],\"go\":{\"p\":").Append(V(P)).Append(",\"q\":").Append(Q(R)).Append("},\"P\":[");
			for (var i = 0; i < tr.nodes.Length; i++)
			{
				var t = tr.nodes[i];
				var ru = inv * (t.position - P);
				sb.Append(i > 0 ? "," : "").Append(N(-ru.x)).Append(',').Append(N(ru.y)).Append(',').Append(N(ru.z));
			}
			sb.Append("],\"Q\":[");
			for (var i = 0; i < tr.nodes.Length; i++)
			{
				var qu = inv * tr.nodes[i].rotation;
				sb.Append(i > 0 ? "," : "").Append(N(qu.x)).Append(',').Append(N(-qu.y)).Append(',').Append(N(-qu.z)).Append(',').Append(N(qu.w));
			}
			sb.Append("]}");
			tr.sampleCount++;
		}

		private void Bake(Trace tr, int k, float ct)
		{
			var a = tr.animator;
			var inv = Quaternion.Inverse(a.transform.rotation);
			var P = a.transform.position;
			var dir = Path.Combine(pending.outDir, "bakes");
			Directory.CreateDirectory(dir);
			var files = new List<string>();
			foreach (var smr in tr.smrs)
			{
				smr.BakeMesh(tr.bakeMesh, true);
				var verts = tr.bakeMesh.vertices;
				var x = smr.transform;
				var bytes = new byte[verts.Length * 12];
				for (var i = 0; i < verts.Length; i++)
				{
					var w = x.position + x.rotation * verts[i]; // useScale: scale already baked in
					var r = inv * (w - P);
					Buffer.BlockCopy(BitConverter.GetBytes(-r.x), 0, bytes, i * 12, 4);
					Buffer.BlockCopy(BitConverter.GetBytes(r.y), 0, bytes, i * 12 + 4, 4);
					Buffer.BlockCopy(BitConverter.GetBytes(r.z), 0, bytes, i * 12 + 8, 4);
				}
				var name = $"{tr.def.id}_{k}_{smr.name}.bin";
				File.WriteAllBytes(Path.Combine(dir, name), bytes);
				files.Add(name);
			}
			tr.bakes.Add("{\"k\":" + k + ",\"t\":" + N((k - tr.tagFrame) / 60.0) + ",\"ct\":" + N(ct) + ",\"files\":[" + string.Join(",", files.Select(S)) + "]}");
		}

		private void WriteMeshInfo(Trace tr)
		{
			foreach (var smr in tr.smrs)
			{
				var mesh = smr.sharedMesh;
				if (!mesh) continue;
				var bones = smr.bones;
				var bw = mesh.boneWeights;
				var sb = new StringBuilder();
				sb.Append("{\"smr\":").Append(S(smr.name)).Append(",\"vertexCount\":").Append(mesh.vertexCount).Append(",\"blendShapes\":").Append(mesh.blendShapeCount)
					.Append(",\"bones\":[").Append(string.Join(",", bones.Select(b => S(b ? b.name : "")))).Append("],\"dom\":[");
				for (var i = 0; i < bw.Length; i++)
				{
					var w = bw[i];
					var bi = w.boneIndex0; var wv = w.weight0;
					if (w.weight1 > wv) { bi = w.boneIndex1; wv = w.weight1; }
					if (w.weight2 > wv) { bi = w.boneIndex2; wv = w.weight2; }
					if (w.weight3 > wv) { bi = w.boneIndex3; wv = w.weight3; }
					sb.Append(i > 0 ? "," : "").Append(bi).Append(',').Append(N(wv));
				}
				sb.Append("],\"tris\":[");
				var tris = mesh.triangles;
				for (var i = 0; i < tris.Length; i++) sb.Append(i > 0 ? "," : "").Append(tris[i]);
				sb.Append("]}");
				Directory.CreateDirectory(pending.outDir);
				File.WriteAllText(Path.Combine(pending.outDir, $"mesh_{tr.def.id}_{smr.name}.json"), sb.ToString());
			}
		}

		private void Finish()
		{
			done = true;
			active = null;
			var sb = new StringBuilder("{\n\"meta\":{\"unity\":").Append(S(Application.unityVersion)).Append(",\"date\":").Append(S(DateTime.UtcNow.ToString("o", Inv)))
				.Append(",\"captureFramerate\":60,\"sample\":\"after every LateUpdate\",\"convention\":\"P/Q: node pose relative to the Animator GO's current world pose, glTF convention: p=mirrorX(inv(R)*(pw-P)), q=(q'.x,-q'.y,-q'.z,q'.w), q'=inv(R)*qw; bakes: BakeMesh(useScale) vertices in the same frame\"},\n\"cases\":[");
			for (var i = 0; i < traces.Count; i++)
			{
				var tr = traces[i];
				sb.Append(i > 0 ? ",\n" : "\n").Append("{\"id\":").Append(S(tr.def.id)).Append(",\"def\":").Append(JsonUtility.ToJson(tr.def)).Append(",\"appliedLocals\":").Append(S(tr.appliedLocals))
					.Append(",\"error\":").Append(S(tr.error)).Append(",\"tagFrame\":").Append(tr.tagFrame).Append(",\"endFrame\":").Append(tr.endFrame)
					.Append(",\"clipLength\":").Append(tr.clip ? N(tr.clip.length) : "null")
					.Append(",\"humanScale\":").Append(tr.animator && tr.animator.isHuman ? N(tr.animator.humanScale) : "null")
					.Append(",\"nodes\":[").Append(tr.nodes != null ? string.Join(",", tr.nodes.Select(t => S(t.name))) : "").Append(']')
					.Append(",\"nodePaths\":[").Append(tr.nodePaths != null ? string.Join(",", tr.nodePaths.Select(S)) : "").Append(']')
					.Append(",\"bakes\":[").Append(string.Join(",", tr.bakes)).Append("],\"samples\":[").Append(tr.samples).Append("\n]}");
			}
			sb.Append("\n]\n}\n");
			var path = Path.Combine(pending.outDir, "unity-arms-pose.json");
			File.WriteAllText(path, sb.ToString());
			var failed = traces.Where(t => t.error.Length > 0).Select(t => t.def.id + ": " + t.error).ToList();
			Debug.Log($"[ArmsPoseProbe] wrote {path}; failed cases: {(failed.Count == 0 ? "none" : string.Join(" | ", failed))}");
			EditorApplication.Exit(failed.Count == 0 ? 0 : 1);
		}

		private static string N(double v)
		{
			if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
			var r = Math.Round(v, 7);
			if (r == 0) r = 0;
			return r.ToString("0.#######", Inv);
		}

		private static string V(Vector3 v) => "[" + N(v.x) + "," + N(v.y) + "," + N(v.z) + "]";
		private static string Q(Quaternion q) => "[" + N(q.x) + "," + N(q.y) + "," + N(q.z) + "," + N(q.w) + "]";

		private static string S(string s)
		{
			if (s == null) return "null";
			var sb = new StringBuilder("\"");
			foreach (var ch in s)
			{
				if (ch == '"') sb.Append("\\\"");
				else if (ch == '\\') sb.Append("\\\\");
				else if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4", Inv));
				else sb.Append(ch);
			}
			return sb.Append('"').ToString();
		}
	}
}
