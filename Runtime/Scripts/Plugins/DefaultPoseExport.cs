using System.Collections.Generic;
using System.Linq;
using GLTF.Schema;
using UnityEngine;

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that exports skinned characters in their default (bind/rest) pose instead
	/// of whatever pose the Animator happened to be in at export time.
	///
	/// The exporter captures bone node transforms live from the scene, so an avatar exported
	/// mid-animation is frozen in that pose in the glTF — which makes it awkward to position
	/// in the web editor afterwards. The bind pose is recoverable without any asset references:
	/// mesh.bindposes[i] is the inverse of bone i's rest-pose matrix relative to the renderer,
	/// so restWorld = smr.localToWorld * bindposes[i]^-1.
	///
	/// BeforeSceneExport re-poses every skeleton under the export roots into that rest pose
	/// (which also makes BakeSkinnedMeshes bake the rest pose, not the caught frame);
	/// AfterSceneExport restores the exact live pose so the scene is untouched.
	/// </summary>
	public class DefaultPoseExport : GLTFExportPlugin
	{
		[SerializeField]
		[Tooltip("Also reset all blend shape weights to 0 during export (e.g. faces caught mid-viseme). Leave off if your avatars use non-zero blend shape weights for body/face shaping.")]
		private bool resetBlendShapeWeights = false;

		public override string DisplayName => "IMMERSION_default_pose";

		public override string Description =>
			"Exports skinned meshes in their bind/rest pose (the pose the model has when placed " +
			"in a scene) instead of the animated pose at export time. The scene pose is restored after export.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
			return new DefaultPoseExportContext(resetBlendShapeWeights);
		}
	}

	public class DefaultPoseExportContext : GLTFExportPluginContext
	{
		private readonly bool _resetBlendShapeWeights;

		private struct TransformState
		{
			public Transform Transform;
			public Vector3 LocalPosition;
			public Quaternion LocalRotation;
			public Vector3 LocalScale;
		}

		private readonly List<TransformState> _savedPose = new List<TransformState>();
		private readonly List<(SkinnedMeshRenderer renderer, float[] weights)> _savedBlendShapes =
			new List<(SkinnedMeshRenderer, float[])>();

		public DefaultPoseExportContext(bool resetBlendShapeWeights)
		{
			_resetBlendShapeWeights = resetBlendShapeWeights;
		}

		public override void BeforeSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			_savedPose.Clear();
			_savedBlendShapes.Clear();

			// Desired rest-pose world matrix per bone. Several renderers usually share one
			// skeleton (body/hair/clothes) and agree on the rest pose; first writer wins.
			var restWorld = new Dictionary<Transform, Matrix4x4>();

			foreach (var root in exporter.RootTransforms)
			{
				if (!root) continue;
				foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
				{
					CollectRestPose(smr, restWorld);

					if (_resetBlendShapeWeights && smr.sharedMesh && smr.sharedMesh.blendShapeCount > 0)
					{
						var weights = new float[smr.sharedMesh.blendShapeCount];
						for (int i = 0; i < weights.Length; i++)
							weights[i] = smr.GetBlendShapeWeight(i);
						_savedBlendShapes.Add((smr, weights));
						for (int i = 0; i < weights.Length; i++)
							smr.SetBlendShapeWeight(i, 0f);
					}
				}
			}

			if (restWorld.Count == 0) return;

			// Apply parent-first so each bone's local matrix is computed against its parent's
			// rest world matrix (not the possibly-still-animated live one).
			foreach (var bone in restWorld.Keys.OrderBy(Depth))
			{
				var parent = bone.parent;
				var parentWorld = !parent ? Matrix4x4.identity :
					restWorld.TryGetValue(parent, out var rest) ? rest : parent.localToWorldMatrix;
				var local = parentWorld.inverse * restWorld[bone];

				_savedPose.Add(new TransformState
				{
					Transform = bone,
					LocalPosition = bone.localPosition,
					LocalRotation = bone.localRotation,
					LocalScale = bone.localScale
				});

				bone.localPosition = local.GetColumn(3);
				bone.localRotation = local.rotation;
				bone.localScale = local.lossyScale;
			}
		}

		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			// Locals are independent of application order.
			foreach (var state in _savedPose)
			{
				if (!state.Transform) continue;
				state.Transform.localPosition = state.LocalPosition;
				state.Transform.localRotation = state.LocalRotation;
				state.Transform.localScale = state.LocalScale;
			}
			_savedPose.Clear();

			foreach (var (renderer, weights) in _savedBlendShapes)
			{
				if (!renderer) continue;
				for (int i = 0; i < weights.Length; i++)
					renderer.SetBlendShapeWeight(i, weights[i]);
			}
			_savedBlendShapes.Clear();
		}

		private static void CollectRestPose(SkinnedMeshRenderer smr, Dictionary<Transform, Matrix4x4> restWorld)
		{
			var mesh = smr.sharedMesh;
			var bones = smr.bones;
			if (!mesh || bones == null || bones.Length == 0) return;

			var bindposes = mesh.bindposes;
			if (bindposes == null || bindposes.Length == 0) return;

			var rendererWorld = smr.transform.localToWorldMatrix;
			int count = Mathf.Min(bones.Length, bindposes.Length);
			for (int i = 0; i < count; i++)
			{
				var bone = bones[i];
				if (!bone || restWorld.ContainsKey(bone)) continue;
				restWorld.Add(bone, rendererWorld * bindposes[i].inverse);
			}
		}

		private static int Depth(Transform t)
		{
			int depth = 0;
			while (t.parent)
			{
				depth++;
				t = t.parent;
			}
			return depth;
		}
	}
}
