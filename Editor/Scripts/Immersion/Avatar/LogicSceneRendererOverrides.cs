using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Immersion.Export
{
	/// <summary>
	/// Renderer state of an avatar's INSTANCE in a logic scene (materials, mesh, enabled), captured
	/// so <see cref="AvatarBatchExporter"/> can export the prefab with what the module actually
	/// renders instead of the prefab defaults.
	///
	/// Why: a logic scene often instantiates the avatar's FBX (or the prefab) with per-renderer
	/// overrides — a truncated <c>m_Materials</c> that hides a phantom submesh, a different
	/// material per slot. The ERT Coach prefab pairs the eyes/teeth submesh with <c>T_EyeOcc</c>
	/// (OcclusionMask → black eyes on the web) while the logic scene draws it with the FBX default
	/// <c>T_Organica</c> and leaves the shell submesh undrawn.
	///
	/// Instance resolution, per avatar prefab:
	/// 1. an explicit instance name (<c>-logicInstances</c>, aligned with <c>-avatars</c>): the scene
	///    GameObject carrying an <see cref="Animator"/> with that name (else any GameObject with it);
	/// 2. otherwise the single scene Animator whose prefab instance root has the same ORIGINAL
	///    source asset as the prefab (a prefab variant of an FBX and an FBX instance match).
	/// Renderer matching, per logic-instance renderer: the same original source object (the FBX /
	/// base prefab renderer), else the same hierarchy path below the instance root.
	///
	/// State is stored as <see cref="GlobalObjectId"/> strings, so it survives the switch to the
	/// empty export scene (an asset referenced only from managed code can be unloaded there).
	/// </summary>
	public sealed class LogicSceneRendererOverrides
	{
		private sealed class RendererState
		{
			public string SourceKey;   // GlobalObjectId of the original source renderer, or null
			public string Path;        // hierarchy path below the instance root
			public string MeshId;      // GlobalObjectId of the shared mesh, or null
			public string MeshName;
			public string[] MaterialIds; // GlobalObjectId per slot ("" = empty slot)
			public string[] MaterialNames;
			public bool Enabled;
			public bool Matched;
		}

		public string InstancePath { get; private set; }
		public string ScenePath { get; private set; }
		private readonly List<RendererState> _renderers = new List<RendererState>();

		/// <summary>
		/// Open <paramref name="scenePath"/> and capture the renderer state of each avatar's instance.
		/// The result is aligned with <paramref name="prefabPaths"/>; a null entry = not resolved
		/// (the reason is logged as an error and counted in <paramref name="failures"/>).
		/// </summary>
		public static List<LogicSceneRendererOverrides> Collect(string scenePath, IList<string> prefabPaths, IList<string> instanceNames, out int failures)
		{
			failures = 0;
			var result = new List<LogicSceneRendererOverrides>();
			var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
			if (!scene.IsValid()) throw new Exception("logic scene not found/openable at '" + scenePath + "'");

			var roots = scene.GetRootGameObjects();
			var animators = roots.SelectMany(r => r.GetComponentsInChildren<Animator>(true)).ToArray();

			for (var i = 0; i < prefabPaths.Count; i++)
			{
				var prefabPath = prefabPaths[i];
				var wanted = i < instanceNames.Count ? instanceNames[i] : null;
				try
				{
					var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
					if (!prefab) throw new Exception("prefab not found at '" + prefabPath + "'");
					var instance = ResolveInstance(prefab, wanted, roots, animators);
					var o = new LogicSceneRendererOverrides { ScenePath = scenePath, InstancePath = PathOf(instance.transform, null) };
					foreach (var r in instance.GetComponentsInChildren<Renderer>(true))
						o._renderers.Add(Capture(r, instance.transform));
					Debug.Log("[AvatarBatchExporter] logic instance for '" + prefab.name + "': " + o.InstancePath
						+ " (" + o._renderers.Count + " renderers)\n" + o.Describe());
					result.Add(o);
				}
				catch (Exception e)
				{
					failures++;
					result.Add(null);
					Debug.LogError("[AvatarBatchExporter] FAIL logic instance for '" + prefabPath + "': " + e.Message);
				}
			}
			return result;
		}

		private static GameObject ResolveInstance(GameObject prefab, string wanted, GameObject[] roots, Animator[] animators)
		{
			if (!string.IsNullOrEmpty(wanted))
			{
				var byAnimator = animators.Where(a => a.gameObject.name == wanted).Select(a => a.gameObject).Distinct().ToList();
				if (byAnimator.Count == 1) return InstanceRoot(byAnimator[0]);
				if (byAnimator.Count > 1) throw new Exception(byAnimator.Count + " Animator objects named '" + wanted + "'");
				var any = roots.SelectMany(r => r.GetComponentsInChildren<Transform>(true)).Where(t => t.name == wanted).ToList();
				if (any.Count == 1) return InstanceRoot(any[0].gameObject);
				throw new Exception(any.Count == 0 ? "no object named '" + wanted + "' in the logic scene" : any.Count + " objects named '" + wanted + "'");
			}

			var source = OriginalSourceAsset(prefab);
			var candidates = animators
				.Select(a => InstanceRoot(a.gameObject))
				.Where(g => PrefabUtility.IsPartOfPrefabInstance(g))
				.Distinct()
				.Where(g => OriginalSourceAsset(g) == source)
				.ToList();
			if (candidates.Count == 1) return candidates[0];
			throw new Exception((candidates.Count == 0 ? "no logic-scene instance" : candidates.Count + " logic-scene instances")
				+ " of '" + AssetDatabase.GetAssetPath(source) + "'"
				+ (candidates.Count > 1 ? " (" + string.Join(", ", candidates.Select(c => c.name)) + ")" : "")
				+ " - pass -logicInstances");
		}

		// The prefab instance root an Animator belongs to (the Animator object itself when it is not
		// part of a prefab instance).
		private static GameObject InstanceRoot(GameObject go)
		{
			var root = PrefabUtility.GetNearestPrefabInstanceRoot(go);
			return root ? root : go;
		}

		// The asset at the bottom of the variant / nesting chain (an FBX model or a base prefab).
		private static UnityEngine.Object OriginalSourceAsset(GameObject go)
		{
			var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
			var path = AssetDatabase.GetAssetPath(src ? src : go);
			return string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadMainAssetAtPath(path);
		}

		private static string SourceKey(Renderer r)
		{
			var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(r);
			return src ? GlobalObjectId.GetGlobalObjectIdSlow(src).ToString() : null;
		}

		private static Mesh MeshOf(Renderer r)
		{
			if (r is SkinnedMeshRenderer smr) return smr.sharedMesh;
			var mf = r.GetComponent<MeshFilter>();
			return mf ? mf.sharedMesh : null;
		}

		private static string IdOf(UnityEngine.Object o) => o ? GlobalObjectId.GetGlobalObjectIdSlow(o).ToString() : "";

		private static RendererState Capture(Renderer r, Transform root)
		{
			var mesh = MeshOf(r);
			var mats = r.sharedMaterials;
			return new RendererState
			{
				SourceKey = SourceKey(r),
				Path = PathOf(r.transform, root),
				MeshId = mesh ? IdOf(mesh) : null,
				MeshName = mesh ? mesh.name : null,
				MaterialIds = mats.Select(IdOf).ToArray(),
				MaterialNames = mats.Select(m => m ? m.name : "<none>").ToArray(),
				Enabled = r.enabled,
			};
		}

		/// <summary>
		/// Apply the captured state to the renderers of <paramref name="instance"/> (the prefab
		/// instance about to be exported). Throws when an object cannot be resolved or a mesh swap
		/// would break skinning.
		/// </summary>
		public void Apply(GameObject instance)
		{
			foreach (var s in _renderers) s.Matched = false;
			var log = new StringBuilder();
			var changed = 0;
			var targets = instance.GetComponentsInChildren<Renderer>(true)
				.Select(r => (renderer: r, key: SourceKey(r), path: PathOf(r.transform, instance.transform)))
				.ToList();

			// Unpack BEFORE changing anything: on a prefab instance a scripted change is only an
			// unrecorded override, and the humanoid sampler's Undo register/perform pair re-merges
			// the instance from its prefab mid-export (it dropped SetActive the same way — see
			// AvatarBatchExporter). As plain objects the changes survive.
			if (PrefabUtility.IsPartOfPrefabInstance(instance))
				PrefabUtility.UnpackPrefabInstance(PrefabUtility.GetOutermostPrefabInstanceRoot(instance), PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

			foreach (var (r, key, path) in targets)
			{
				var state = (key != null ? _renderers.FirstOrDefault(s => !s.Matched && s.SourceKey == key) : null)
					?? _renderers.FirstOrDefault(s => !s.Matched && s.Path == path);
				if (state == null)
				{
					log.Append("\n  ").Append(path).Append(": no logic-scene counterpart - prefab state kept");
					continue;
				}
				state.Matched = true;

				var before = Pairing(r);
				var mesh = MeshOf(r);
				if (state.MeshId != null && IdOf(mesh) != state.MeshId)
				{
					var target = Resolve<Mesh>(state.MeshId, state.MeshName);
					if (mesh && (target.vertexCount != mesh.vertexCount || target.bindposes.Length != mesh.bindposes.Length))
						throw new Exception(path + ": logic mesh '" + target.name + "' is not skin-compatible with '" + mesh.name + "'");
					if (r is SkinnedMeshRenderer smr) smr.sharedMesh = target;
					else r.GetComponent<MeshFilter>().sharedMesh = target;
				}

				var mats = new Material[state.MaterialIds.Length];
				for (var k = 0; k < mats.Length; k++)
					mats[k] = state.MaterialIds[k].Length == 0 ? null : Resolve<Material>(state.MaterialIds[k], state.MaterialNames[k]);
				r.sharedMaterials = mats;
				r.enabled = state.Enabled;

				var after = Pairing(r);
				if (after != before) changed++;
				log.Append("\n  ").Append(path).Append(after == before ? " (unchanged): " : ": ").Append(after)
					.Append(after == before ? "" : "   [prefab: " + before + "]");
			}
			foreach (var s in _renderers.Where(s => !s.Matched))
				log.Append("\n  logic-only renderer ").Append(s.Path).Append(" (added in the scene, not exported): ")
					.Append(string.Join(", ", s.MaterialNames));
			Debug.Log("[AvatarBatchExporter] applied logic-scene renderer state of " + InstancePath + " to '" + instance.name
				+ "' (" + changed + " renderer(s) changed):" + log);
		}

		private static T Resolve<T>(string id, string name) where T : UnityEngine.Object
		{
			if (!GlobalObjectId.TryParse(id, out var gid)) throw new Exception("bad GlobalObjectId '" + id + "' (" + name + ")");
			var obj = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(gid) as T;
			if (!obj) throw new Exception("cannot resolve " + typeof(T).Name + " '" + name + "' (" + id + ")");
			return obj;
		}

		// "submesh i [indexCount] -> material" for every submesh, as Unity draws it: slot i draws
		// submesh i; submeshes without a slot are not drawn.
		private static string Pairing(Renderer r)
		{
			var mesh = MeshOf(r);
			var mats = r.sharedMaterials;
			if (!mesh) return "no mesh";
			var parts = new List<string>();
			for (var i = 0; i < mesh.subMeshCount; i++)
				parts.Add(i + "[" + mesh.GetIndexCount(i) + "]->" + (i < mats.Length ? (mats[i] ? mats[i].name : "<none>") : "(not drawn)"));
			return (r.enabled ? "" : "DISABLED ") + mesh.name + " " + string.Join(" ", parts);
		}

		private string Describe()
		{
			var sb = new StringBuilder();
			foreach (var s in _renderers)
				sb.Append("  ").Append(s.Path).Append(": ").Append(s.MeshName ?? "no mesh").Append(" slots [")
					.Append(string.Join(", ", s.MaterialNames)).Append("]").Append(s.Enabled ? "" : " DISABLED").Append('\n');
			return sb.ToString();
		}

		private static string PathOf(Transform t, Transform root)
		{
			var parts = new List<string>();
			for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
			parts.Reverse();
			return parts.Count == 0 ? "." : string.Join("/", parts);
		}
	}
}
