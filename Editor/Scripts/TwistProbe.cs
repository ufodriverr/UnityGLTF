using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Immersion.Export
{
	/// <summary>
	/// Ground-truth probe for the humanoid TWIST solve: enters PLAY MODE in batch, spawns
	/// each -avatars prefab with its -controller override, lets the Animator run a few frames
	/// (the twist distribution only runs in the player loop — no edit-mode sampling path
	/// writes sibling *Twist* bones), then dumps every *Twist0N bone's localRotation to
	/// -out/twist-pose.json and exits.
	///
	/// Unity.exe -batchmode -projectPath &lt;proj&gt; -executeMethod Immersion.Export.TwistProbe.Run
	///   -avatars "a.prefab;b.prefab" -controller "a.controller;b.controller" -out "C:/dir"
	/// (no -quit — the probe exits by itself)
	/// </summary>
	public static class TwistProbe
	{
		private const string Flag = "Immersion.TwistProbe.pending";

		public static void Run()
		{
			SessionState.SetString(Flag, JsonUtility.ToJson(new Args
			{
				avatars = GetArg("-avatars"),
				controllers = GetArg("-controller"),
				outDir = GetArg("-out"),
			}));
			EditorApplication.EnterPlaymode();
		}

		[Serializable]
		private class Args { public string avatars; public string controllers; public string outDir; }

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
				var args = JsonUtility.FromJson<Args>(raw);
				var host = new GameObject("TwistProbeHost").AddComponent<TwistProbeRunner>();
				host.Begin(args.avatars, args.controllers, args.outDir);
			};
		}

		private static string GetArg(string flag)
		{
			var args = Environment.GetCommandLineArgs();
			for (var i = 0; i < args.Length - 1; i++)
				if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
					return args[i + 1];
			return null;
		}
	}

	public class TwistProbeRunner : MonoBehaviour
	{
		public void Begin(string avatars, string controllers, string outDir)
		{
			StartCoroutine(Probe(avatars.Split(';'), (controllers ?? "").Split(';'), outDir));
		}

		private IEnumerator Probe(string[] avatars, string[] controllers, string outDir)
		{
			var sb = new System.Text.StringBuilder();
			sb.Append("{\n");
			for (var i = 0; i < avatars.Length; i++)
			{
				var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(avatars[i].Trim().Trim('"'));
				if (!prefab) { Debug.LogError("[TwistProbe] prefab not found: " + avatars[i]); continue; }
				var instance = Instantiate(prefab);
				var animator = instance.GetComponentInChildren<Animator>(true);
				if (i < controllers.Length && !string.IsNullOrEmpty(controllers[i]))
				{
					var c = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllers[i].Trim().Trim('"'));
					if (c) animator.runtimeAnimatorController = c;
				}
				animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
				for (var f = 0; f < 10; f++) yield return null; // let the player-loop twist solve run

				var rows = new List<string>();
				foreach (var t in instance.GetComponentsInChildren<Transform>(true))
				{
					if (!t.name.Contains("Twist0")) continue;
					var q = t.localRotation;
					rows.Add($"  \"{t.name}\": [{q.x:R}, {q.y:R}, {q.z:R}, {q.w:R}]");
				}
				sb.Append($" \"{Path.GetFileNameWithoutExtension(avatars[i])}\": {{\n{string.Join(",\n", rows)}\n }}");
				sb.Append(i < avatars.Length - 1 ? ",\n" : "\n");
				Debug.Log("[TwistProbe] captured " + rows.Count + " twist bones for " + prefab.name);
				Destroy(instance);
			}
			sb.Append("}\n");
			Directory.CreateDirectory(outDir);
			File.WriteAllText(Path.Combine(outDir, "twist-pose.json"), sb.ToString());
			Debug.Log("[TwistProbe] wrote " + Path.Combine(outDir, "twist-pose.json"));
			EditorApplication.Exit(0);
		}
	}
}
