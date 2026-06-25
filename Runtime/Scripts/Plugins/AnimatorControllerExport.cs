using GLTF.Schema;
using UnityEngine;
#if UNITY_EDITOR
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEditor.Animations;
#endif

namespace UnityGLTF.Plugins
{
	/// <summary>
	/// Export plugin that captures Unity AnimatorController state machines and writes them, as JSON,
	/// into a root-level glTF extension (<see cref="IMMERSION_animator_controller.EXTENSION_NAME"/>),
	/// next to the baked glTF animations.
	///
	/// The exported data describes, for every Animator in the exported hierarchy:
	/// - which parameters exist (name, type, default value),
	/// - the layers and their default state,
	/// - every state (name, speed, motion = clip or blend tree, looping, tag, ...),
	/// - what transitions to what and why (conditions on parameters),
	/// - how fast (transition duration, exit time, offset, fixed vs normalized, interruption rules).
	///
	/// State motions reference the baked animations both by clip name and by their index inside
	/// <c>gltf.animations</c>, so a three.js (or any) runtime can rebuild an AnimationMixer-driven
	/// state machine that matches Unity.
	/// </summary>
	public class AnimatorControllerExport : GLTFExportPlugin
	{
		public override string DisplayName => "IMMERSION_animator_controller";

		public override string Description =>
			"Exports Unity AnimatorController state machines (parameters, states, transitions, conditions, " +
			"blend trees and transition timings) as JSON so the baked animations can be driven like in Unity.";

		public override bool EnabledByDefault => true;

		public override GLTFExportPluginContext CreateInstance(ExportContext context)
		{
#if UNITY_EDITOR
			return new AnimatorControllerExportContext();
#else
			// AnimatorController data is only available in the editor; nothing to do at runtime.
			return null;
#endif
		}
	}

#if UNITY_EDITOR
	public class AnimatorControllerExportContext : GLTFExportPluginContext
	{
		public override void AfterSceneExport(GLTFSceneExporter exporter, GLTFRoot gltfRoot)
		{
			var controllers = new JArray();
			var processed = new HashSet<Animator>();

			if (exporter.RootTransforms != null)
			{
				foreach (var root in exporter.RootTransforms)
				{
					if (!root) continue;
					foreach (var animator in root.GetComponentsInChildren<Animator>(true))
					{
						if (!animator || !processed.Add(animator)) continue;
						var controller = animator.runtimeAnimatorController as AnimatorController;
						if (!controller) continue;

						var json = BuildController(exporter, gltfRoot, animator, controller);
						if (json != null) controllers.Add(json);
					}
				}
			}

			if (controllers.Count == 0) return;

			var data = new JObject
			{
				["version"] = 1,
				["controllers"] = controllers,
			};

			exporter.DeclareExtensionUsage(IMMERSION_animator_controller.EXTENSION_NAME, false);
			gltfRoot.AddExtension(IMMERSION_animator_controller.EXTENSION_NAME, new IMMERSION_animator_controller(data));
		}

		// ---- Controller / layers ------------------------------------------------------------

		private static JObject BuildController(GLTFSceneExporter exporter, GLTFRoot root, Animator animator, AnimatorController controller)
		{
			var layersArr = new JArray();
			foreach (var layer in controller.layers)
				layersArr.Add(SerializeLayer(exporter, root, controller, layer, animator.transform));

			return new JObject
			{
				["name"] = controller.name,
				["nodeIndex"] = exporter.GetTransformIndex(animator.transform),
				["nodeName"] = animator.name,
				["applyRootMotion"] = animator.applyRootMotion,
				["parameters"] = SerializeParameters(controller),
				["layers"] = layersArr,
			};
		}

		private static JObject SerializeLayer(GLTFSceneExporter exporter, GLTFRoot root, AnimatorController controller, AnimatorControllerLayer layer, Transform animatorTransform)
		{
			var sm = layer.stateMachine;

			// Flatten all states (including those inside sub-state-machines) into a stable index list.
			var flatStates = new List<AnimatorState>();
			CollectStates(sm, flatStates);
			var indexOf = new Dictionary<AnimatorState, int>();
			for (var i = 0; i < flatStates.Count; i++) indexOf[flatStates[i]] = i;

			var statesArr = new JArray();
			foreach (var state in flatStates)
				statesArr.Add(SerializeState(exporter, root, controller, state, animatorTransform, indexOf));

			var anyArr = new JArray();
			foreach (var tr in GetAnyStateTransitionsRecursive(sm))
				anyArr.Add(SerializeStateTransition(tr, indexOf));

			var entryArr = new JArray();
			if (sm)
				foreach (var entry in sm.entryTransitions)
					entryArr.Add(SerializeEntryTransition(entry, indexOf));

			return new JObject
			{
				["name"] = layer.name,
				["defaultWeight"] = layer.defaultWeight,
				["blendingMode"] = layer.blendingMode == AnimatorLayerBlendingMode.Additive ? "additive" : "override",
				["ikPass"] = layer.iKPass,
				["defaultStateIndex"] = (sm && sm.defaultState && indexOf.TryGetValue(sm.defaultState, out var di)) ? di : -1,
				["states"] = statesArr,
				["anyStateTransitions"] = anyArr,
				["entryTransitions"] = entryArr,
			};
		}

		// ---- States -------------------------------------------------------------------------

		private static JObject SerializeState(GLTFSceneExporter exporter, GLTFRoot root, AnimatorController controller, AnimatorState state, Transform animatorTransform, Dictionary<AnimatorState, int> indexOf)
		{
			var speedParamDefault = state.speedParameterActive ? GetFloatParamDefault(controller, state.speedParameter) : 1f;

			var obj = new JObject
			{
				["name"] = state.name,
				["index"] = indexOf.TryGetValue(state, out var si) ? si : -1,
				["speed"] = state.speed,
				["speedParameterActive"] = state.speedParameterActive,
				["speedParameter"] = state.speedParameterActive ? state.speedParameter : null,
				["cycleOffset"] = state.cycleOffset,
				["mirror"] = state.mirror,
				["writeDefaultValues"] = state.writeDefaultValues,
				["iKOnFeet"] = state.iKOnFeet,
				["tag"] = state.tag,
				["motion"] = SerializeMotion(exporter, root, state.motion, animatorTransform, state.speed, speedParamDefault, state.speedParameterActive),
			};

			var transitions = new JArray();
			foreach (var tr in state.transitions)
				transitions.Add(SerializeStateTransition(tr, indexOf));
			obj["transitions"] = transitions;

			return obj;
		}

		// ---- Motions (clip / blend tree, recursive) -----------------------------------------

		private static JToken SerializeMotion(GLTFSceneExporter exporter, GLTFRoot root, Motion motion, Transform animatorTransform, float stateSpeed, float speedParamDefault, bool speedParamActive)
		{
			if (motion == null) return JValue.CreateNull();

			if (motion is AnimationClip clip)
			{
				var bakedSpeed = exporter.BakeAnimationSpeed ? stateSpeed * (speedParamActive ? speedParamDefault : 1f) : 1f;
				var id = ResolveAnimationId(exporter, clip, animatorTransform, bakedSpeed, stateSpeed, 1f);

				var obj = new JObject
				{
					["type"] = "clip",
					["clip"] = clip.name,
					["animationIndex"] = id,
					["isLooping"] = clip.isLooping,
					["length"] = clip.length,
				};
				if (id >= 0 && root.Animations != null && id < root.Animations.Count)
					obj["animationName"] = root.Animations[id].Name;
				return obj;
			}

			if (motion is BlendTree tree)
			{
				var children = new JArray();
				foreach (var child in tree.children)
				{
					children.Add(new JObject
					{
						["threshold"] = child.threshold,
						["positionX"] = child.position.x,
						["positionY"] = child.position.y,
						["timeScale"] = child.timeScale,
						["cycleOffset"] = child.cycleOffset,
						["directBlendParameter"] = child.directBlendParameter,
						["mirror"] = child.mirror,
						// blend-tree members are baked at speed 1 (see ExporterAnimation.ExportAnimationClips)
						["motion"] = SerializeMotion(exporter, root, child.motion, animatorTransform, 1f, 1f, false),
					});
				}

				return new JObject
				{
					["type"] = "blendTree",
					["name"] = tree.name,
					["blendType"] = BlendTypeToString(tree.blendType),
					["blendParameter"] = tree.blendParameter,
					["blendParameterY"] = tree.blendParameterY,
					["children"] = children,
				};
			}

			return JValue.CreateNull();
		}

		// ---- Transitions --------------------------------------------------------------------

		private static JObject SerializeStateTransition(AnimatorStateTransition tr, Dictionary<AnimatorState, int> indexOf)
		{
			var obj = new JObject
			{
				["name"] = tr.name,
				["hasExitTime"] = tr.hasExitTime,
				["exitTime"] = tr.exitTime,
				["hasFixedDuration"] = tr.hasFixedDuration,
				["duration"] = tr.duration,
				["offset"] = tr.offset,
				["interruptionSource"] = InterruptionToString(tr.interruptionSource),
				["orderedInterruption"] = tr.orderedInterruption,
				["canTransitionToSelf"] = tr.canTransitionToSelf,
				["solo"] = tr.solo,
				["mute"] = tr.mute,
				["isExit"] = tr.isExit,
			};
			FillDestination(obj, tr.destinationState, tr.destinationStateMachine, tr.isExit, indexOf);
			obj["conditions"] = SerializeConditions(tr.conditions);
			return obj;
		}

		private static JObject SerializeEntryTransition(AnimatorTransition tr, Dictionary<AnimatorState, int> indexOf)
		{
			var obj = new JObject { ["name"] = tr.name };
			FillDestination(obj, tr.destinationState, tr.destinationStateMachine, false, indexOf);
			obj["conditions"] = SerializeConditions(tr.conditions);
			return obj;
		}

		private static void FillDestination(JObject obj, AnimatorState destState, AnimatorStateMachine destSm, bool isExit, Dictionary<AnimatorState, int> indexOf)
		{
			if (destState && indexOf.TryGetValue(destState, out var di))
			{
				obj["destinationStateIndex"] = di;
				obj["destinationStateName"] = destState.name;
			}
			else if (destSm)
			{
				// Entering a sub-state-machine resolves to that machine's default state.
				obj["destinationStateMachine"] = destSm.name;
				if (destSm.defaultState && indexOf.TryGetValue(destSm.defaultState, out var ddi))
				{
					obj["destinationStateIndex"] = ddi;
					obj["destinationStateName"] = destSm.defaultState.name;
				}
				else
				{
					obj["destinationStateIndex"] = -1;
				}
			}
			else
			{
				obj["destinationStateIndex"] = -1;
				obj["isExit"] = isExit;
			}
		}

		private static JArray SerializeConditions(AnimatorCondition[] conditions)
		{
			var arr = new JArray();
			if (conditions != null)
			{
				foreach (var c in conditions)
				{
					arr.Add(new JObject
					{
						["parameter"] = c.parameter,
						["mode"] = ConditionModeToString(c.mode),
						["threshold"] = c.threshold,
					});
				}
			}
			return arr;
		}

		// ---- Parameters ---------------------------------------------------------------------

		private static JArray SerializeParameters(AnimatorController controller)
		{
			var arr = new JArray();
			foreach (var p in controller.parameters)
			{
				var obj = new JObject
				{
					["name"] = p.name,
					["type"] = ParamTypeToString(p.type),
				};
				switch (p.type)
				{
					case AnimatorControllerParameterType.Float: obj["defaultFloat"] = p.defaultFloat; break;
					case AnimatorControllerParameterType.Int: obj["defaultInt"] = p.defaultInt; break;
					case AnimatorControllerParameterType.Bool:
					case AnimatorControllerParameterType.Trigger: obj["defaultBool"] = p.defaultBool; break;
				}
				arr.Add(obj);
			}
			return arr;
		}

		private static float GetFloatParamDefault(AnimatorController controller, string name)
		{
			if (string.IsNullOrEmpty(name)) return 1f;
			foreach (var p in controller.parameters)
				if (p.name == name && p.type == AnimatorControllerParameterType.Float)
					return p.defaultFloat;
			return 1f;
		}

		// ---- Helpers ------------------------------------------------------------------------

		private static int ResolveAnimationId(GLTFSceneExporter exporter, AnimationClip clip, Transform t, params float[] candidateSpeeds)
		{
			if (!clip || !t) return -1;
			foreach (var s in candidateSpeeds)
			{
				var id = exporter.GetAnimationId(clip, t, s);
				if (id >= 0) return id;
			}
			return -1;
		}

		private static void CollectStates(AnimatorStateMachine sm, List<AnimatorState> list)
		{
			if (!sm) return;
			foreach (var c in sm.states) list.Add(c.state);
			foreach (var csm in sm.stateMachines)
				CollectStates(csm.stateMachine, list);
		}

		private static IEnumerable<AnimatorStateTransition> GetAnyStateTransitionsRecursive(AnimatorStateMachine sm)
		{
			if (!sm) yield break;
			foreach (var t in sm.anyStateTransitions) yield return t;
			foreach (var csm in sm.stateMachines)
			{
				if (!csm.stateMachine) continue;
				foreach (var t in GetAnyStateTransitionsRecursive(csm.stateMachine))
					yield return t;
			}
		}

		private static string ParamTypeToString(AnimatorControllerParameterType type)
		{
			switch (type)
			{
				case AnimatorControllerParameterType.Float: return "float";
				case AnimatorControllerParameterType.Int: return "int";
				case AnimatorControllerParameterType.Bool: return "bool";
				case AnimatorControllerParameterType.Trigger: return "trigger";
				default: return type.ToString().ToLowerInvariant();
			}
		}

		private static string ConditionModeToString(AnimatorConditionMode mode)
		{
			switch (mode)
			{
				case AnimatorConditionMode.If: return "if";
				case AnimatorConditionMode.IfNot: return "ifNot";
				case AnimatorConditionMode.Greater: return "greater";
				case AnimatorConditionMode.Less: return "less";
				case AnimatorConditionMode.Equals: return "equals";
				case AnimatorConditionMode.NotEqual: return "notEqual";
				default: return mode.ToString();
			}
		}

		private static string InterruptionToString(TransitionInterruptionSource source)
		{
			switch (source)
			{
				case TransitionInterruptionSource.None: return "none";
				case TransitionInterruptionSource.Source: return "source";
				case TransitionInterruptionSource.Destination: return "destination";
				case TransitionInterruptionSource.SourceThenDestination: return "sourceThenDestination";
				case TransitionInterruptionSource.DestinationThenSource: return "destinationThenSource";
				default: return source.ToString();
			}
		}

		private static string BlendTypeToString(BlendTreeType type)
		{
			switch (type)
			{
				case BlendTreeType.Simple1D: return "simple1D";
				case BlendTreeType.FreeformDirectional2D: return "freeformDirectional2D";
				case BlendTreeType.FreeformCartesian2D: return "freeformCartesian2D";
				case BlendTreeType.Direct: return "direct";
				default: return type.ToString();
			}
		}
	}
#endif
}
