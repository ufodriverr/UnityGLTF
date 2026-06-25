# `IMMERSION_animator_controller` glTF extension

This is a **non-standard, export-only** glTF extension added by UnityGLTF. It lets you export a
Unity **AnimatorController** (the state machine that drives a Skinned Mesh / Avatar) together with
the GLB so a runtime such as **three.js** can reproduce the exact same animation behaviour:

- which baked animations exist,
- which **parameters** exist (name, type, default value),
- the **states** and the **default state** of each layer,
- **what transitions to what and why** (conditions on parameters),
- **how fast** (transition duration, exit time, offset, fixed vs. normalized timing),
- **blend trees** (1D / 2D / direct) with thresholds and positions.

The baked clips are exported exactly as before (standard `animations` in the GLB). This extension
only *adds* the controller graph as data; nothing about the existing export changes.

## How to export

1. Select your Avatar / Skinned model (the GameObject that has the `Animator` with an
   `AnimatorController` assigned).
2. `Assets ▸ UnityGLTF ▸ Export selected` (or the GameObject context menu), exactly as before.

The plugin **"IMMERSION_animator_controller"** is enabled by default. You can toggle it in
`Project Settings ▸ UnityGLTF ▸ Export`. It works for any number of `Animator`s in the exported
hierarchy.

> Only `AnimatorController` assets are read. A plain `AnimationController override` or an
> `Animation` component has no state machine, so only the baked clips are exported for those.

## Where it lives in the file

Root-level extension. With the three.js `GLTFLoader` you read it from
`gltf.parser.json.extensions['IMMERSION_animator_controller']`.

```jsonc
{
  "extensionsUsed": ["IMMERSION_animator_controller"],
  "animations": [ /* baked clips, referenced by name + index below */ ],
  "extensions": {
    "IMMERSION_animator_controller": {
      "version": 1,
      "controllers": [ /* one per Animator that has an AnimatorController */ ]
    }
  }
}
```

## Schema

All property names are **camelCase**. Times/values use Unity's semantics.

### Controller
| field | type | notes |
|---|---|---|
| `name` | string | AnimatorController asset name |
| `nodeIndex` | int | glTF node index of the Animator's transform (`-1` if not exported) |
| `nodeName` | string | name of that GameObject |
| `applyRootMotion` | bool | |
| `parameters` | Parameter[] | |
| `layers` | Layer[] | |

### Parameter
| field | type | notes |
|---|---|---|
| `name` | string | |
| `type` | string | `"float"` \| `"int"` \| `"bool"` \| `"trigger"` |
| `defaultFloat` | number | present when `type == "float"` |
| `defaultInt` | int | present when `type == "int"` |
| `defaultBool` | bool | present when `type == "bool"` or `"trigger"` |

### Layer
| field | type | notes |
|---|---|---|
| `name` | string | |
| `defaultWeight` | number | |
| `blendingMode` | string | `"override"` \| `"additive"` |
| `ikPass` | bool | |
| `defaultStateIndex` | int | index into `states` (the entry/default state), `-1` if none |
| `states` | State[] | **flattened**: includes states from nested sub-state-machines |
| `anyStateTransitions` | StateTransition[] | "Any State" transitions (recursively gathered) |
| `entryTransitions` | EntryTransition[] | conditional Entry transitions of the root state machine |

> States from sub-state-machines are flattened into the single `states` array. All transition
> `destinationStateIndex` values index into that flat array.

### State
| field | type | notes |
|---|---|---|
| `name` | string | |
| `index` | int | its own index in `states` (for convenience) |
| `speed` | number | |
| `speedParameterActive` | bool | |
| `speedParameter` | string\|null | parameter that scales speed, when active |
| `cycleOffset` | number | |
| `mirror` | bool | |
| `writeDefaultValues` | bool | |
| `iKOnFeet` | bool | |
| `tag` | string | |
| `motion` | Motion\|null | clip or blend tree (see below) |
| `transitions` | StateTransition[] | outgoing transitions from this state |

### Motion — clip
| field | type | notes |
|---|---|---|
| `type` | string | `"clip"` |
| `clip` | string | Unity clip name |
| `animationIndex` | int | index into the GLB `animations` array (`-1` if not resolvable) |
| `animationName` | string | name of that glTF animation (matches `THREE.AnimationClip.name`) |
| `isLooping` | bool | |
| `length` | number | clip length in seconds |

### Motion — blend tree (recursive)
| field | type | notes |
|---|---|---|
| `type` | string | `"blendTree"` |
| `name` | string | |
| `blendType` | string | `"simple1D"` \| `"freeformDirectional2D"` \| `"freeformCartesian2D"` \| `"direct"` |
| `blendParameter` | string | X parameter |
| `blendParameterY` | string | Y parameter (2D only) |
| `children` | BlendChild[] | |

**BlendChild**: `threshold` (number), `positionX` / `positionY` (number, 2D), `timeScale` (number),
`cycleOffset` (number), `directBlendParameter` (string, direct only), `mirror` (bool),
`motion` (Motion — clip or nested blend tree).

### StateTransition
| field | type | notes |
|---|---|---|
| `name` | string | |
| `destinationStateIndex` | int | index into the layer's `states`, `-1` if Exit |
| `destinationStateName` | string | present when resolvable |
| `destinationStateMachine` | string | present if the transition targets a sub-state-machine; `destinationStateIndex` then points at that machine's default state |
| `isExit` | bool | transition goes to Exit |
| `hasExitTime` | bool | |
| `exitTime` | number | normalized [0..1] of the source state |
| `hasFixedDuration` | bool | if true, `duration` is in **seconds**, otherwise **normalized** [0..1] |
| `duration` | number | the transition / blend time |
| `offset` | number | normalized start offset into the destination state |
| `interruptionSource` | string | `"none"` \| `"source"` \| `"destination"` \| `"sourceThenDestination"` \| `"destinationThenSource"` |
| `orderedInterruption` | bool | |
| `canTransitionToSelf` | bool | (Any State transitions) |
| `solo` / `mute` | bool | |
| `conditions` | Condition[] | **all** must be true (AND) for the transition to fire |

### EntryTransition
Like StateTransition but only `name`, destination fields and `conditions` (Entry transitions have
no timing). The first one whose conditions pass selects the entry state; otherwise `defaultStateIndex`.

### Condition
| field | type | notes |
|---|---|---|
| `parameter` | string | |
| `mode` | string | `"if"` (bool true / trigger) \| `"ifNot"` (bool false) \| `"greater"` \| `"less"` \| `"equals"` \| `"notEqual"` |
| `threshold` | number | compared value (ignored for `if` / `ifNot`) |

## Example (trimmed)

```jsonc
{
  "version": 1,
  "controllers": [{
    "name": "PlayerController",
    "nodeIndex": 0,
    "nodeName": "Avatar",
    "applyRootMotion": false,
    "parameters": [
      { "name": "Speed",    "type": "float",   "defaultFloat": 0 },
      { "name": "Jump",     "type": "trigger", "defaultBool": false },
      { "name": "Grounded", "type": "bool",    "defaultBool": true }
    ],
    "layers": [{
      "name": "Base Layer",
      "defaultWeight": 1,
      "blendingMode": "override",
      "ikPass": false,
      "defaultStateIndex": 0,
      "states": [
        {
          "name": "Idle", "index": 0, "speed": 1,
          "motion": { "type": "clip", "clip": "Idle", "animationIndex": 0, "animationName": "Idle", "isLooping": true, "length": 2.0 },
          "transitions": [
            { "destinationStateIndex": 1, "destinationStateName": "Walk",
              "hasExitTime": false, "exitTime": 0, "hasFixedDuration": true, "duration": 0.25, "offset": 0,
              "conditions": [ { "parameter": "Speed", "mode": "greater", "threshold": 0.1 } ] }
          ]
        },
        {
          "name": "Walk", "index": 1, "speed": 1,
          "motion": {
            "type": "blendTree", "blendType": "simple1D", "blendParameter": "Speed",
            "children": [
              { "threshold": 0, "timeScale": 1, "motion": { "type": "clip", "clip": "Walk", "animationIndex": 1, "animationName": "Walk", "isLooping": true, "length": 1.0 } },
              { "threshold": 1, "timeScale": 1, "motion": { "type": "clip", "clip": "Run",  "animationIndex": 2, "animationName": "Run",  "isLooping": true, "length": 0.8 } }
            ]
          },
          "transitions": [
            { "destinationStateIndex": 0, "destinationStateName": "Idle",
              "hasExitTime": false, "hasFixedDuration": true, "duration": 0.25,
              "conditions": [ { "parameter": "Speed", "mode": "less", "threshold": 0.1 } ] }
          ]
        }
      ],
      "anyStateTransitions": [
        { "destinationStateIndex": 0, "destinationStateName": "Idle",
          "hasFixedDuration": true, "duration": 0.1,
          "conditions": [ { "parameter": "Jump", "mode": "if", "threshold": 0 } ] }
      ],
      "entryTransitions": []
    }]
  }]
}
```

## Consuming it in three.js

Animations are loaded as standard `THREE.AnimationClip[]`. Match a state's `motion.animationName`
(or `animationIndex`) to a clip, drive them with an `AnimationMixer`, and use `duration` for
`crossFadeTo`:

```ts
gltfLoader.load(url, (gltf) => {
  const ctrl = gltf.parser?.json?.extensions?.['IMMERSION_animator_controller'];
  if (ctrl) {
    // keep it with the model so the runtime can build a state machine from it
    gltf.scene.userData.animatorController = ctrl;
  }
  // gltf.animations are the baked clips; find one by name:
  // const clip = gltf.animations.find(c => c.name === state.motion.animationName);
});
```

A clip transition becomes: `mixer.clipAction(next).reset().play(); current.crossFadeTo(next, duration, false);`
where `duration` is in seconds when `hasFixedDuration` is true (otherwise multiply by the source clip length).
