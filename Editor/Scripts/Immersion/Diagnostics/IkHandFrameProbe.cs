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

namespace Immersion.Export
{
	/// <summary>
	/// Ground truth for the humanoid IK HAND FRAME: how Unity maps an IK goal rotation
	/// (<c>SetIKRotation</c>, world) to the mapped hand bone's world rotation after IK at
	/// position/rotation weight 1. Output feeds the web runtime's per-goal
	/// <c>rotationOffset</c> (<c>q_handWorld = q_targetWorld · rotationOffset</c>).
	///
	/// Edit mode: opens <c>-scene</c> (a logic scene), finds every <c>CharacterIK</c> (by type
	/// name, via reflection — the template's Assembly-CSharp is not referenced), and records
	/// the host Animator's source model, controller, avatar, the world/local TRS of the avatar
	/// root, of every transform under it and of each hand target (+ its parent). Nothing is
	/// saved: the scene is replaced by an empty one before play mode.
	///
	/// Play mode: rebuilds each rig from its source model at the recorded transforms, adds
	/// <see cref="IkHandFrameDriver"/> (a mirror of CharacterIK.OnAnimatorIK) and samples, per
	/// clip time, an IK-off frame plus IK-on frames with the scene target rotation and with
	/// perturbed target rotations. Hand/arm bones are read in LateUpdate (after the Animator's
	/// IK pass and retarget). Writes <c>-out/ik-hand-frame.json</c> and exits.
	///
	/// Unity.exe -batchmode -projectPath &lt;proj&gt; -executeMethod Immersion.Export.IkHandFrameProbe.Run
	///   -scene "Assets/_Project/Scenes/Logic/Logic_X.unity" -out "C:/dir" [-times "0.13;0.47;0.81"] [-settle 4]
	/// (no -quit — the probe exits by itself; exit 0 = written, 1 = failed)
	/// </summary>
	public static class IkHandFrameProbe
	{
		private const string Flag = "Immersion.IkHandFrameProbe.pending";

		public static void Run()
		{
			var scenePath = CliArgs.Get("-scene");
			var outDir = CliArgs.Get("-out");
			if (string.IsNullOrEmpty(scenePath) || string.IsNullOrEmpty(outDir))
			{
				Debug.LogError("[IkHandFrameProbe] usage: -scene <logic scene> -out <dir> [-times \"0.13;0.47;0.81\"] [-settle 4]");
				CliArgs.Exit(1);
				return;
			}
			Capture capture;
			try
			{
				capture = CaptureScene(scenePath);
			}
			catch (Exception e)
			{
				Debug.LogError("[IkHandFrameProbe] FAIL capture: " + e);
				CliArgs.Exit(1);
				return;
			}
			capture.outDir = outDir;
			capture.times = CliArgs.Get("-times") ?? "0.13;0.47;0.81";
			capture.settle = CliArgs.GetInt("-settle", 4);
			Directory.CreateDirectory(outDir);
			var json = JsonUtility.ToJson(capture, true);
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
				var capture = JsonUtility.FromJson<Capture>(raw);
				var host = new GameObject("IkHandFrameProbeHost").AddComponent<IkHandFrameProbeRunner>();
				host.Begin(capture);
			};
		}

		// ---------------------------------------------------------------- edit-mode capture

		internal static Capture CaptureScene(string scenePath)
		{
			var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
			var capture = new Capture { scene = scenePath };
			var iks = scene.GetRootGameObjects()
				.SelectMany(r => r.GetComponentsInChildren<MonoBehaviour>(true))
				.Where(c => c != null && c.GetType().Name == "CharacterIK")
				.ToList();
			Debug.Log("[IkHandFrameProbe] CharacterIK components in scene: " + iks.Count);
			foreach (var ik in iks)
			{
				var host = ik.gameObject;
				var animator = host.GetComponent<Animator>();
				if (!animator) { Debug.LogError("[IkHandFrameProbe] CharacterIK without Animator on " + host.name); continue; }
				var rig = new Rig
				{
					name = host.name,
					hierarchyPath = HierarchyPath(host.transform),
					activeInScene = host.activeInHierarchy,
					sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(host) ?? "",
					controllerPath = animator.runtimeAnimatorController ? AssetDatabase.GetAssetPath(animator.runtimeAnimatorController) : "",
					avatarPath = animator.avatar ? AssetDatabase.GetAssetPath(animator.avatar) : "",
					avatarName = animator.avatar ? animator.avatar.name : "",
					applyRootMotion = animator.applyRootMotion,
					cullingMode = (int)animator.cullingMode,
					updateMode = (int)animator.updateMode,
					ikActive = (bool)Field(ik, "ikActive"),
					rootParentKey = animator.transform.parent ? HierarchyPath(animator.transform.parent) : "",
					rootParentWorld = animator.transform.parent ? WorldTrs(animator.transform.parent) : Trs.Identity,
					rootLocal = LocalTrs(animator.transform),
					rootWorld = WorldTrs(animator.transform),
					components = string.Join(";", host.GetComponents<Component>().Select(c => c ? c.GetType().Name : "<missing>")),
				};
				foreach (var t in host.GetComponentsInChildren<Transform>(true))
				{
					if (t == host.transform) continue;
					rig.bonePaths.Add(AnimationUtility.CalculateTransformPath(t, host.transform));
					rig.boneLocals.Add(LocalTrs(t));
				}
				AddGoal(rig, ik, host.transform, "LeftHand", "leftHandTarget", "leftHandPositionWeight", "leftHandRotationWeight");
				AddGoal(rig, ik, host.transform, "RightHand", "rightHandTarget", "rightHandPositionWeight", "rightHandRotationWeight");
				AddGoal(rig, ik, host.transform, "LeftFoot", "leftFootTarget", "leftFootPositionWeight", "leftFootRotationWeight");
				AddGoal(rig, ik, host.transform, "RightFoot", "rightFootTarget", "rightFootPositionWeight", "rightFootRotationWeight");
				capture.rigs.Add(rig);
				Debug.Log($"[IkHandFrameProbe] rig '{rig.name}' src={rig.sourcePath} ctrl={rig.controllerPath} avatar={rig.avatarPath}#{rig.avatarName} goals={rig.goals.Count} components={rig.components}");
			}
			return capture;
		}

		private static void AddGoal(Rig rig, MonoBehaviour ik, Transform host, string goal, string targetField, string pwField, string rwField)
		{
			var target = Field(ik, targetField) as Transform;
			if (!target) return;
			var g = new Goal
			{
				goal = goal,
				targetName = target.name,
				positionWeight = (float)Field(ik, pwField),
				rotationWeight = (float)Field(ik, rwField),
				parentInAvatar = target.parent && (target.parent == host || target.parent.IsChildOf(host)),
				local = LocalTrs(target),
				world = WorldTrs(target),
			};
			if (g.parentInAvatar)
				g.parentPathInAvatar = target.parent == host ? "" : AnimationUtility.CalculateTransformPath(target.parent, host);
			else if (target.parent)
			{
				g.parentKey = HierarchyPath(target.parent);
				g.parentWorld = WorldTrs(target.parent);
			}
			else
				g.parentWorld = Trs.Identity;
			rig.goals.Add(g);
		}

		private static object Field(object o, string name) =>
			o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(o);

		internal static string HierarchyPath(Transform t)
		{
			var parts = new List<string>();
			for (var c = t; c; c = c.parent) parts.Add(c.name);
			parts.Reverse();
			return string.Join("/", parts);
		}

		internal static Trs LocalTrs(Transform t) => new Trs { pos = t.localPosition, rot = t.localRotation, scale = t.localScale };
		internal static Trs WorldTrs(Transform t) => new Trs { pos = t.position, rot = t.rotation, scale = t.lossyScale };

		// ---------------------------------------------------------------- data

		[Serializable]
		public class Trs
		{
			public Vector3 pos;
			public Quaternion rot = Quaternion.identity;
			public Vector3 scale = Vector3.one;
			public static Trs Identity => new Trs();
		}

		[Serializable]
		public class Goal
		{
			public string goal;
			public string targetName;
			public float positionWeight;
			public float rotationWeight;
			public bool parentInAvatar;
			public string parentPathInAvatar;
			public string parentKey;
			public Trs parentWorld;
			public Trs local;
			public Trs world;
		}

		[Serializable]
		public class Rig
		{
			public string name;
			public string hierarchyPath;
			public bool activeInScene;
			public string sourcePath;
			public string controllerPath;
			public string avatarPath;
			public string avatarName;
			public bool applyRootMotion;
			public int cullingMode;
			public int updateMode;
			public bool ikActive;
			public string rootParentKey;
			public Trs rootParentWorld;
			public Trs rootLocal;
			public Trs rootWorld;
			public string components;
			public List<string> bonePaths = new List<string>();
			public List<Trs> boneLocals = new List<Trs>();
			public List<Goal> goals = new List<Goal>();
		}

		[Serializable]
		public class Capture
		{
			public string scene;
			public string outDir;
			public string times;
			public int settle;
			public List<Rig> rigs = new List<Rig>();
		}
	}

	// The play-mode MonoBehaviours below share this file on purpose: the file's MonoScript binds to the
	// static IkHandFrameProbe class, so these classes stay unbound and AddComponent accepts them. Unity
	// refuses AddComponent for an Editor-folder MonoBehaviour that owns a MonoScript ("is an editor
	// script"), which includes the first MonoBehaviour of a file whose name matches no class.

	/// <summary>
	/// Play-mode IK driver used by <see cref="IkHandFrameProbe"/>. <c>OnAnimatorIK</c> is a
	/// line-for-line mirror of the template's <c>CharacterIK.OnAnimatorIK</c>
	/// (<c>Assets/_Project/Scripts/Character/CharacterIK.cs:69-99</c>): per goal
	/// SetIKPositionWeight, SetIKRotationWeight, SetIKPosition(target.position),
	/// SetIKRotation(target.rotation) — world space, read live every frame. When
	/// <see cref="ikActive"/> is false it zeroes all four goals and the look-at weight, like the
	/// original's else-branch. Before setting anything it records the Animator's own goal
	/// (<c>GetIKPosition/GetIKRotation</c>) so an IK-off frame yields the animated hand goal.
	/// Must sit on the Animator's GameObject (Unity only sends OnAnimatorIK there).
	/// </summary>
	public class IkHandFrameDriver : MonoBehaviour
	{
		public class Goal
		{
			public AvatarIKGoal ikGoal;
			public Transform target;
			public float positionWeight = 1f;
			public float rotationWeight = 1f;
			// readback at OnAnimatorIK entry (before this frame's Set* calls)
			public Vector3 readbackPos;
			public Quaternion readbackRot;
		}

		public bool ikActive = true;
		public readonly List<Goal> goals = new List<Goal>();
		public int ikCalls;
		public int lastLayer = -1;

		private Animator animator;

		public void Setup(Animator a) { animator = a; }

		private void OnAnimatorIK(int layerIndex)
		{
			if (animator == null) return;
			ikCalls++;
			lastLayer = layerIndex;
			foreach (var g in goals)
			{
				g.readbackPos = animator.GetIKPosition(g.ikGoal);
				g.readbackRot = animator.GetIKRotation(g.ikGoal);
			}

			if (ikActive && goals.Count > 0)
			{
				foreach (var g in goals)
				{
					if (g.target == null) continue;
					animator.SetIKPositionWeight(g.ikGoal, g.positionWeight);
					animator.SetIKRotationWeight(g.ikGoal, g.rotationWeight);
					animator.SetIKPosition(g.ikGoal, g.target.position);
					animator.SetIKRotation(g.ikGoal, g.target.rotation);
				}
			}
			else
			{
				animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, 0);
				animator.SetIKRotationWeight(AvatarIKGoal.LeftHand, 0);
				animator.SetIKPositionWeight(AvatarIKGoal.RightHand, 0);
				animator.SetIKRotationWeight(AvatarIKGoal.RightHand, 0);
				animator.SetIKPositionWeight(AvatarIKGoal.LeftFoot, 0);
				animator.SetIKRotationWeight(AvatarIKGoal.LeftFoot, 0);
				animator.SetIKPositionWeight(AvatarIKGoal.RightFoot, 0);
				animator.SetIKRotationWeight(AvatarIKGoal.RightFoot, 0);
				animator.SetLookAtWeight(0);
			}
		}
	}

	/// <summary>Play-mode half of <see cref="IkHandFrameProbe"/>: rebuild, sample, dump, exit.</summary>
	public class IkHandFrameProbeRunner : MonoBehaviour
	{
		private class Live
		{
			public IkHandFrameProbe.Rig rig;
			public Animator animator;
			public IkHandFrameDriver driver;
			public readonly List<Transform> targets = new List<Transform>();
			public readonly List<Quaternion> baseTargetRot = new List<Quaternion>();
			public int stateHash;
			public float clipLength;
		}

		private class Condition
		{
			public string label;
			public float time;
			public bool ik;
			public Quaternion worldPre = Quaternion.identity; // q' = worldPre · q · localPost
			public Quaternion localPost = Quaternion.identity;
		}

		private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
		private IkHandFrameProbe.Capture capture;
		private readonly List<Live> rigs = new List<Live>();
		private readonly List<Condition> conditions = new List<Condition>();
		private readonly StringBuilder samples = new StringBuilder();
		private readonly StringBuilder meta = new StringBuilder();
		private int frame;
		private int condIndex = -1;
		private int condFrame;
		private const int Warmup = 10;
		private bool done;

		public void Begin(IkHandFrameProbe.Capture c)
		{
			capture = c;
			try
			{
				Build();
				Plan();
			}
			catch (Exception e)
			{
				Debug.LogError("[IkHandFrameProbe] FAIL build: " + e);
				EditorApplication.Exit(1);
			}
		}

		private void Build()
		{
			var proxies = new Dictionary<string, Transform>();
			Transform Proxy(string key, IkHandFrameProbe.Trs world)
			{
				key = string.IsNullOrEmpty(key) ? "<scene root>" : key;
				if (proxies.TryGetValue(key, out var p)) return p;
				var go = new GameObject("proxy: " + key);
				go.transform.SetPositionAndRotation(world.pos, world.rot);
				go.transform.localScale = world.scale;
				proxies[key] = go.transform;
				return go.transform;
			}

			foreach (var rig in capture.rigs)
			{
				var model = AssetDatabase.LoadAssetAtPath<GameObject>(rig.sourcePath);
				if (!model) throw new Exception("source model not found: " + rig.sourcePath);
				var parent = Proxy(rig.rootParentKey, rig.rootParentWorld);
				var inst = Instantiate(model, parent);
				inst.name = rig.name + " (probe)";
				inst.transform.localPosition = rig.rootLocal.pos;
				inst.transform.localRotation = rig.rootLocal.rot;
				inst.transform.localScale = rig.rootLocal.scale;

				int applied = 0, changed = 0, missing = 0;
				for (var i = 0; i < rig.bonePaths.Count; i++)
				{
					var t = inst.transform.Find(rig.bonePaths[i]);
					if (!t) { missing++; continue; }
					var l = rig.boneLocals[i];
					if ((t.localPosition - l.pos).sqrMagnitude > 1e-12f || Quaternion.Angle(t.localRotation, l.rot) > 1e-3f || (t.localScale - l.scale).sqrMagnitude > 1e-12f) changed++;
					t.localPosition = l.pos; t.localRotation = l.rot; t.localScale = l.scale;
					applied++;
				}

				var animator = inst.GetComponent<Animator>();
				if (!animator) throw new Exception("no Animator on model root " + rig.sourcePath);
				var avatar = AssetDatabase.LoadAllAssetsAtPath(rig.avatarPath).OfType<Avatar>().FirstOrDefault(a => a.name == rig.avatarName);
				if (avatar) animator.avatar = avatar;
				animator.runtimeAnimatorController = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(rig.controllerPath);
				animator.applyRootMotion = rig.applyRootMotion;
				animator.updateMode = (AnimatorUpdateMode)rig.updateMode;
				animator.cullingMode = AnimatorCullingMode.AlwaysAnimate; // no camera in batch mode
				animator.Rebind();

				var live = new Live { rig = rig, animator = animator };
				var driver = inst.AddComponent<IkHandFrameDriver>();
				driver.Setup(animator);
				driver.ikActive = rig.ikActive;
				foreach (var g in rig.goals)
				{
					var tgo = new GameObject("IK target " + rig.name + " " + g.goal + " (" + g.targetName + ")");
					var tp = g.parentInAvatar
						? (string.IsNullOrEmpty(g.parentPathInAvatar) ? inst.transform : inst.transform.Find(g.parentPathInAvatar))
						: Proxy(g.parentKey, g.parentWorld);
					if (!tp) throw new Exception("target parent not found for " + g.goal);
					tgo.transform.SetParent(tp, false);
					tgo.transform.localPosition = g.local.pos;
					tgo.transform.localRotation = g.local.rot;
					tgo.transform.localScale = g.local.scale;
					var dPos = (tgo.transform.position - g.world.pos).magnitude;
					var dRot = Quaternion.Angle(tgo.transform.rotation, g.world.rot);
					meta.Append($"  {{\"rig\": \"{rig.name}\", \"goal\": \"{g.goal}\", \"target\": \"{g.targetName}\", \"rebuiltVsSceneWorld_m\": {F(dPos)}, \"rebuiltVsSceneWorld_deg\": {F(dRot)}}},\n");
					driver.goals.Add(new IkHandFrameDriver.Goal
					{
						ikGoal = (AvatarIKGoal)Enum.Parse(typeof(AvatarIKGoal), g.goal),
						target = tgo.transform,
						positionWeight = g.positionWeight,
						rotationWeight = g.rotationWeight,
					});
					live.targets.Add(tgo.transform);
					live.baseTargetRot.Add(tgo.transform.rotation);
				}
				live.driver = driver;
				rigs.Add(live);
				var rootDPos = (inst.transform.position - rig.rootWorld.pos).magnitude;
				var rootDRot = Quaternion.Angle(inst.transform.rotation, rig.rootWorld.rot);
				Debug.Log($"[IkHandFrameProbe] built '{rig.name}': bones applied {applied}, differing from model default {changed}, not in model {missing}; root rebuilt-vs-scene {rootDPos:0.######} m / {rootDRot:0.######} deg; isHuman={animator.isHuman} avatarValid={(animator.avatar ? animator.avatar.isValid : false)}");
				meta.Append($"  {{\"rig\": \"{rig.name}\", \"bonesApplied\": {applied}, \"bonesDifferingFromModelDefault\": {changed}, \"sceneTransformsNotInModel\": {missing}, \"rootRebuiltVsScene_m\": {F(rootDPos)}, \"rootRebuiltVsScene_deg\": {F(rootDRot)}, \"isHuman\": {(animator.isHuman ? "true" : "false")}}},\n");
				AppendAvatarAnalytics(live);
			}
		}

		// Unity's internal Avatar post/pre rotations (reflection; optional cross-check).
		private void AppendAvatarAnalytics(Live live)
		{
			var avatar = live.animator.avatar;
			if (!avatar) return;
			var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
			var names = typeof(Avatar).GetMethods(flags).Where(m => m.DeclaringType == typeof(Avatar)).Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")").Distinct();
			meta.Append($"  {{\"rig\": \"{live.rig.name}\", \"avatarMethods\": \"{string.Join(" ", names)}\"}},\n");
			foreach (var bone in new[] { HumanBodyBones.LeftHand, HumanBodyBones.RightHand, HumanBodyBones.LeftLowerArm, HumanBodyBones.RightLowerArm })
			{
				var parts = new List<string>();
				foreach (var mName in new[] { "GetPostRotation", "GetPreRotation" })
				{
					var m = typeof(Avatar).GetMethod(mName, flags, null, new[] { typeof(int) }, null);
					if (m == null) continue;
					try { parts.Add($"\"{mName}\": {Q((Quaternion)m.Invoke(avatar, new object[] { (int)bone }))}"); }
					catch (Exception e) { parts.Add($"\"{mName}Error\": \"{e.GetType().Name}\""); }
				}
				var ls = typeof(Avatar).GetMethod("GetLimitSign", flags, null, new[] { typeof(int) }, null);
				if (ls != null)
				{
					try { parts.Add($"\"GetLimitSign\": {V((Vector3)ls.Invoke(avatar, new object[] { (int)bone }))}"); }
					catch (Exception e) { parts.Add($"\"GetLimitSignError\": \"{e.GetType().Name}\""); }
				}
				var bt = live.animator.GetBoneTransform(bone);
				meta.Append($"  {{\"rig\": \"{live.rig.name}\", \"humanBone\": \"{bone}\", \"transform\": \"{(bt ? bt.name : "")}\", {string.Join(", ", parts)}}},\n");
			}
		}

		private void Plan()
		{
			var times = CliArgs.Split(capture.times).Select(s => float.Parse(s, Inv)).ToList();
			foreach (var t in times)
			{
				conditions.Add(new Condition { label = "ikOff", time = t, ik = false });
				conditions.Add(new Condition { label = "base", time = t, ik = true });
				conditions.Add(new Condition { label = "localX+30", time = t, ik = true, localPost = Quaternion.AngleAxis(30, Vector3.right) });
				conditions.Add(new Condition { label = "localY+30", time = t, ik = true, localPost = Quaternion.AngleAxis(30, Vector3.up) });
				conditions.Add(new Condition { label = "localZ+30", time = t, ik = true, localPost = Quaternion.AngleAxis(30, Vector3.forward) });
				conditions.Add(new Condition { label = "worldY-45", time = t, ik = true, worldPre = Quaternion.AngleAxis(-45, Vector3.up) });
				conditions.Add(new Condition { label = "localX+20*localZ-35", time = t, ik = true, localPost = Quaternion.AngleAxis(20, Vector3.right) * Quaternion.AngleAxis(-35, Vector3.forward) });
			}
			Debug.Log("[IkHandFrameProbe] planned " + conditions.Count + " conditions x " + rigs.Count + " rigs");
		}

		private void Update()
		{
			if (done) return;
			try
			{
				frame++;
				if (frame <= Warmup)
				{
					if (frame == Warmup)
						foreach (var r in rigs)
						{
							var si = r.animator.GetCurrentAnimatorStateInfo(0);
							r.stateHash = si.fullPathHash;
							r.clipLength = si.length;
							var clips = r.animator.GetCurrentAnimatorClipInfo(0);
							meta.Append($"  {{\"rig\": \"{r.rig.name}\", \"stateFullPathHash\": {si.fullPathHash}, \"stateLength_s\": {F(si.length)}, \"stateSpeed\": {F(si.speed)}, \"speedMultiplier\": {F(si.speedMultiplier)}, \"clips\": \"{string.Join(",", clips.Select(c => c.clip.name + "@" + c.weight.ToString("0.###", Inv)))}\", \"ikCallsDuringWarmup\": {r.driver.ikCalls}, \"layerCount\": {r.animator.layerCount}}},\n");
						}
					return;
				}
				if (condFrame == 0)
				{
					condIndex++;
					if (condIndex >= conditions.Count) { Finish(); return; }
					var c = conditions[condIndex];
					foreach (var r in rigs)
					{
						r.driver.ikActive = c.ik;
						for (var i = 0; i < r.targets.Count; i++)
							r.targets[i].rotation = c.worldPre * r.baseTargetRot[i] * c.localPost;
					}
				}
				var cond = conditions[condIndex];
				foreach (var r in rigs) r.animator.Play(r.stateHash, 0, cond.time);
			}
			catch (Exception e)
			{
				Debug.LogError("[IkHandFrameProbe] FAIL update: " + e);
				done = true;
				EditorApplication.Exit(1);
			}
		}

		private void LateUpdate()
		{
			if (done || frame <= Warmup || condIndex < 0 || condIndex >= conditions.Count) return;
			condFrame++;
			if (condFrame < capture.settle) return;
			condFrame = 0;
			var c = conditions[condIndex];
			foreach (var r in rigs)
			{
				var si = r.animator.GetCurrentAnimatorStateInfo(0);
				for (var i = 0; i < r.driver.goals.Count; i++)
				{
					var g = r.driver.goals[i];
					var isLeft = g.ikGoal == AvatarIKGoal.LeftHand;
					var isHand = g.ikGoal == AvatarIKGoal.LeftHand || g.ikGoal == AvatarIKGoal.RightHand;
					if (!isHand) continue;
					var hand = r.animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
					var fore = r.animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
					var upper = r.animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
					var shoulder = r.animator.GetBoneTransform(isLeft ? HumanBodyBones.LeftShoulder : HumanBodyBones.RightShoulder);
					samples.Append("  {")
						.Append($"\"rig\": \"{r.rig.name}\", \"goal\": \"{g.ikGoal}\", \"cond\": \"{c.label}\", \"ik\": {(c.ik ? "true" : "false")}, ")
						.Append($"\"tReq\": {F(c.time)}, \"tNorm\": {F(si.normalizedTime)}, \"ikCalls\": {r.driver.ikCalls}, \"ikLayer\": {r.driver.lastLayer}, ")
						.Append($"\"targetPos\": {V(g.target.position)}, \"targetRot\": {Q(g.target.rotation)}, ")
						.Append($"\"handName\": \"{hand.name}\", \"handPos\": {V(hand.position)}, \"handRot\": {Q(hand.rotation)}, \"handLocalRot\": {Q(hand.localRotation)}, ")
						.Append($"\"forePos\": {V(fore.position)}, \"foreLocalRot\": {Q(fore.localRotation)}, ")
						.Append($"\"upperPos\": {V(upper.position)}, \"upperLocalRot\": {Q(upper.localRotation)}, ")
						.Append($"\"shoulderName\": \"{(shoulder ? shoulder.name : "")}\", \"shoulderLocalRot\": {(shoulder ? Q(shoulder.localRotation) : "null")}, ")
						.Append($"\"goalReadbackPos\": {V(g.readbackPos)}, \"goalReadbackRot\": {Q(g.readbackRot)}")
						.Append("},\n");
				}
			}
		}

		private void Finish()
		{
			done = true;
			var sb = new StringBuilder();
			sb.Append("{\n\"scene\": \"").Append(capture.scene).Append("\",\n\"unity\": \"").Append(Application.unityVersion).Append("\",\n");
			sb.Append("\"meta\": [\n").Append(TrimComma(meta.ToString())).Append("],\n");
			sb.Append("\"samples\": [\n").Append(TrimComma(samples.ToString())).Append("]\n}\n");
			var path = Path.Combine(capture.outDir, "ik-hand-frame.json");
			File.WriteAllText(path, sb.ToString());
			Debug.Log("[IkHandFrameProbe] wrote " + path);
			EditorApplication.Exit(0);
		}

		private static string TrimComma(string s)
		{
			var i = s.LastIndexOf(",\n", StringComparison.Ordinal);
			return i >= 0 && i == s.Length - 2 ? s.Substring(0, i) + "\n" : s;
		}

		private static string F(float v) => v.ToString("R", Inv);
		private static string V(Vector3 v) => $"[{F(v.x)}, {F(v.y)}, {F(v.z)}]";
		private static string Q(Quaternion q) => $"[{F(q.x)}, {F(q.y)}, {F(q.z)}, {F(q.w)}]";
	}
}
