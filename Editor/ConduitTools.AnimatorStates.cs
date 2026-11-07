/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Animator states and state machines, including their payload application.
    /// </summary>
    internal static partial class ConduitBridge
    {



        /// <summary>
        /// Adds a state (creating it in the addressed state machine), or updates an existing one,
        /// from a flat JSON payload: motion (clip asset path or null), speed(+speedParameter),
        /// mirror(+mirrorParameter), cycleOffset(+cycleOffsetParameter), timeParameter,
        /// writeDefaultValues, ikOnFeet, tag, position ([x,y]), and makeDefault. Parameter-driven
        /// modifiers validate against the controller's parameter list before anything applies.
        /// </summary>
        /// <param name="args">Pipe-separated controller path, layer, state path, and options JSON.</param>
        /// <returns>A per-property evidence report, or an error with nothing applied.</returns>
        private static string AnimatorStateSetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return "Usage: animator-state-set <controllerPath> | <layerNameOrIndex> | <statePath> [| {\"motion\":\"Assets/....anim\"|null,\"speed\":1,\"speedParameter\":\"...\"|null,\"mirror\":false,\"mirrorParameter\":...,\"cycleOffset\":0,\"cycleOffsetParameter\":...,\"timeParameter\":...,\"writeDefaultValues\":true,\"ikOnFeet\":false,\"tag\":\"...\",\"position\":[x,y],\"makeDefault\":true}]";
            }

            AnimatorController controller;
            string assetPath;
            var error = TryLoadAnimatorController(sections[0], out controller, out assetPath);
            if (error != null)
            {
                return error;
            }

            AnimatorControllerLayer[] layers;
            int layerIndex;
            error = TryResolveAnimatorLayer(controller, sections[1], out layers, out layerIndex);
            if (error != null)
            {
                return error;
            }

            List<KeyValuePair<string, string>> fields;
            error = TryParsePayloadSection(sections.Length > 3 ? sections[3] : string.Empty, out fields);
            if (error != null)
            {
                return error;
            }

            var root = layers[layerIndex].stateMachine;
            AnimatorState state;
            AnimatorStateMachine owner;
            var created = false;
            if (TryResolveStatePath(root, sections[2], out state, out owner) != null)
            {
                // The state does not exist yet: its owning state machine must, so the final path
                // segment becomes the new state's name.
                var trimmed = sections[2].Trim().Trim('"');
                var split = trimmed.LastIndexOf('/');
                var smPath = split < 0 ? string.Empty : trimmed.Substring(0, split);
                var stateName = split < 0 ? trimmed : trimmed.Substring(split + 1);
                if (stateName.Length == 0)
                {
                    return "Missing state name in path: '" + sections[2] + "'.";
                }

                error = TryResolveStateMachinePath(root, smPath, out owner);
                if (error != null)
                {
                    return error + " Create it first with animator-statemachine-set.";
                }

                state = owner.AddState(stateName);
                created = true;
            }

            string report;
            error = ApplyAnimatorStatePayload(controller, owner, state, fields, out report);
            if (error != null)
            {
                return error;
            }

            PersistAnimationAssetEdit(controller);
            return (created ? "Added state '" : "State '") + state.name + "' in layer '" + layers[layerIndex].name +
                "' (state machine '" + owner.name + "') on " + assetPath + "." + report;
        }

        /// <summary>
        /// Applies a validated state options payload to a state, shared by animator-state-set and
        /// animator-blendtree-set. Validates every field first (nothing mutates on a bad payload)
        /// and reports each property as set (old -&gt; new) or already-at-value.
        /// </summary>
        /// <param name="controller">Controller that owns the state (for parameter validation).</param>
        /// <param name="owner">State machine that directly contains the state.</param>
        /// <param name="state">State to configure.</param>
        /// <param name="fields">Parsed payload fields.</param>
        /// <param name="report">The per-property evidence report (leading space included when non-empty).</param>
        /// <returns>An error string with nothing applied, otherwise null.</returns>
        private static string ApplyAnimatorStatePayload(AnimatorController controller, AnimatorStateMachine owner, AnimatorState state, List<KeyValuePair<string, string>> fields, out string report)
        {
            report = string.Empty;
            if (fields.Count == 0)
            {
                return null;
            }

            // Pass 1 - validate everything into staged values.
            var setMotion = false;
            Motion motion = null;
            float? speed = null;
            float? cycleOffset = null;
            bool? mirror = null;
            bool? writeDefaults = null;
            bool? ikOnFeet = null;
            string tag = null;
            bool? makeDefault = null;
            Vector3? position = null;
            var setSpeedParameter = false;      string speedParameter = null;
            var setMirrorParameter = false;     string mirrorParameter = null;
            var setCycleOffsetParameter = false; string cycleOffsetParameter = null;
            var setTimeParameter = false;       string timeParameter = null;
            var unknownFields = new List<string>();

            foreach (var entry in fields)
            {
                switch (entry.Key)
                {
                    case "motion":
                        setMotion = true;
                        var motionToken = JsonTokenToString(entry.Value);
                        if (motionToken != null)
                        {
                            motion = AssetDatabase.LoadAssetAtPath<Motion>(NormalizeAssetPath(motionToken));
                            if (motion == null) return "Motion asset not found: " + motionToken + ". Nothing was applied.";
                        }

                        break;

                    case "speed":
                        float speedValue;
                        if (!TryJsonTokenToFloat(entry.Value, out speedValue)) return "speed '" + entry.Value + "' is not a number. Nothing was applied.";
                        speed = speedValue;
                        break;

                    case "cycleOffset":
                        float cycleValue;
                        if (!TryJsonTokenToFloat(entry.Value, out cycleValue)) return "cycleOffset '" + entry.Value + "' is not a number. Nothing was applied.";
                        cycleOffset = cycleValue;
                        break;

                    case "mirror":
                        bool mirrorValue;
                        if (!TryJsonTokenToBool(entry.Value, out mirrorValue)) return "mirror '" + entry.Value + "' is not a bool. Nothing was applied.";
                        mirror = mirrorValue;
                        break;

                    case "writeDefaultValues":
                        bool wdValue;
                        if (!TryJsonTokenToBool(entry.Value, out wdValue)) return "writeDefaultValues '" + entry.Value + "' is not a bool. Nothing was applied.";
                        writeDefaults = wdValue;
                        break;

                    case "ikOnFeet":
                        bool ikValue;
                        if (!TryJsonTokenToBool(entry.Value, out ikValue)) return "ikOnFeet '" + entry.Value + "' is not a bool. Nothing was applied.";
                        ikOnFeet = ikValue;
                        break;

                    case "tag":
                        tag = JsonTokenToString(entry.Value) ?? string.Empty;
                        break;

                    case "makeDefault":
                        bool defaultValue;
                        if (!TryJsonTokenToBool(entry.Value, out defaultValue)) return "makeDefault '" + entry.Value + "' is not a bool. Nothing was applied.";
                        makeDefault = defaultValue;
                        break;

                    case "position":
                        Vector3 positionValue;
                        if (!TryJsonTokenToPosition(entry.Value, out positionValue)) return "position must be a [x, y] number array. Nothing was applied.";
                        position = positionValue;
                        break;

                    case "speedParameter":
                        setSpeedParameter = true;
                        speedParameter = JsonTokenToString(entry.Value);
                        var speedParamError = ValidateAnimatorParameterReference(controller, speedParameter, AnimatorControllerParameterType.Float, "speedParameter");
                        if (speedParamError != null) return speedParamError;
                        break;

                    case "mirrorParameter":
                        setMirrorParameter = true;
                        mirrorParameter = JsonTokenToString(entry.Value);
                        var mirrorParamError = ValidateAnimatorParameterReference(controller, mirrorParameter, AnimatorControllerParameterType.Bool, "mirrorParameter");
                        if (mirrorParamError != null) return mirrorParamError;
                        break;

                    case "cycleOffsetParameter":
                        setCycleOffsetParameter = true;
                        cycleOffsetParameter = JsonTokenToString(entry.Value);
                        var cycleParamError = ValidateAnimatorParameterReference(controller, cycleOffsetParameter, AnimatorControllerParameterType.Float, "cycleOffsetParameter");
                        if (cycleParamError != null) return cycleParamError;
                        break;

                    case "timeParameter":
                        setTimeParameter = true;
                        timeParameter = JsonTokenToString(entry.Value);
                        var timeParamError = ValidateAnimatorParameterReference(controller, timeParameter, AnimatorControllerParameterType.Float, "timeParameter");
                        if (timeParamError != null) return timeParamError;
                        break;

                    default:
                        unknownFields.Add(entry.Key);
                        break;
                }
            }

            // Pass 2 - apply with per-property evidence.
            var changes = new List<string>();
            if (setMotion)
            {
                var oldName = state.motion == null ? "null" : state.motion.name;
                var newName = motion == null ? "null" : motion.name;
                if (state.motion == motion) changes.Add("motion already " + oldName);
                else { changes.Add("motion " + oldName + " -> " + newName); state.motion = motion; }
            }

            if (speed.HasValue)
            {
                if (Mathf.Approximately(state.speed, speed.Value)) changes.Add("speed already " + JsonNumber(state.speed));
                else { changes.Add("speed " + JsonNumber(state.speed) + " -> " + JsonNumber(speed.Value)); state.speed = speed.Value; }
            }

            if (setSpeedParameter)
            {
                changes.Add(ApplyStateParameterDriver("speedParameter", state.speedParameterActive ? state.speedParameter : null, speedParameter, active => state.speedParameterActive = active, value => state.speedParameter = value));
            }

            if (mirror.HasValue)
            {
                if (state.mirror == mirror.Value) changes.Add("mirror already " + state.mirror.ToString().ToLowerInvariant());
                else { changes.Add("mirror " + state.mirror.ToString().ToLowerInvariant() + " -> " + mirror.Value.ToString().ToLowerInvariant()); state.mirror = mirror.Value; }
            }

            if (setMirrorParameter)
            {
                changes.Add(ApplyStateParameterDriver("mirrorParameter", state.mirrorParameterActive ? state.mirrorParameter : null, mirrorParameter, active => state.mirrorParameterActive = active, value => state.mirrorParameter = value));
            }

            if (cycleOffset.HasValue)
            {
                if (Mathf.Approximately(state.cycleOffset, cycleOffset.Value)) changes.Add("cycleOffset already " + JsonNumber(state.cycleOffset));
                else { changes.Add("cycleOffset " + JsonNumber(state.cycleOffset) + " -> " + JsonNumber(cycleOffset.Value)); state.cycleOffset = cycleOffset.Value; }
            }

            if (setCycleOffsetParameter)
            {
                changes.Add(ApplyStateParameterDriver("cycleOffsetParameter", state.cycleOffsetParameterActive ? state.cycleOffsetParameter : null, cycleOffsetParameter, active => state.cycleOffsetParameterActive = active, value => state.cycleOffsetParameter = value));
            }

            if (setTimeParameter)
            {
                changes.Add(ApplyStateParameterDriver("timeParameter", state.timeParameterActive ? state.timeParameter : null, timeParameter, active => state.timeParameterActive = active, value => state.timeParameter = value));
            }

            if (writeDefaults.HasValue)
            {
                if (state.writeDefaultValues == writeDefaults.Value) changes.Add("writeDefaultValues already " + state.writeDefaultValues.ToString().ToLowerInvariant());
                else { changes.Add("writeDefaultValues " + state.writeDefaultValues.ToString().ToLowerInvariant() + " -> " + writeDefaults.Value.ToString().ToLowerInvariant()); state.writeDefaultValues = writeDefaults.Value; }
            }

            if (ikOnFeet.HasValue)
            {
                if (state.iKOnFeet == ikOnFeet.Value) changes.Add("ikOnFeet already " + state.iKOnFeet.ToString().ToLowerInvariant());
                else { changes.Add("ikOnFeet " + state.iKOnFeet.ToString().ToLowerInvariant() + " -> " + ikOnFeet.Value.ToString().ToLowerInvariant()); state.iKOnFeet = ikOnFeet.Value; }
            }

            if (tag != null)
            {
                if (state.tag == tag) changes.Add("tag already " + (tag.Length == 0 ? "(empty)" : "'" + tag + "'"));
                else { changes.Add("tag '" + state.tag + "' -> '" + tag + "'"); state.tag = tag; }
            }

            if (position.HasValue)
            {
                // The states array is a copy - update the matching child entry and write it back.
                var children = owner.states;
                for (var i = 0; i < children.Length; i++)
                {
                    if (children[i].state != state)
                    {
                        continue;
                    }

                    if (children[i].position == position.Value) changes.Add("position already (" + JsonNumber(position.Value.x) + ", " + JsonNumber(position.Value.y) + ")");
                    else
                    {
                        changes.Add("position (" + JsonNumber(children[i].position.x) + ", " + JsonNumber(children[i].position.y) + ") -> (" + JsonNumber(position.Value.x) + ", " + JsonNumber(position.Value.y) + ")");
                        children[i].position = position.Value;
                        owner.states = children;
                    }

                    break;
                }
            }

            if (makeDefault.HasValue && makeDefault.Value)
            {
                if (owner.defaultState == state) changes.Add("already the default state");
                else { changes.Add("default state " + (owner.defaultState == null ? "null" : "'" + owner.defaultState.name + "'") + " -> '" + state.name + "'"); owner.defaultState = state; }
            }

            var sb = new StringBuilder();
            if (changes.Count > 0) sb.Append(' ').Append(string.Join("; ", changes)).Append('.');
            if (unknownFields.Count > 0) sb.Append(" WARNING ignored unknown field(s): ").Append(string.Join(", ", unknownFields)).Append('.');
            report = sb.ToString();
            return null;
        }

        /// <summary>
        /// Validates that a state-modifier parameter reference names an existing controller
        /// parameter of the required type. A null reference (clearing the driver) is always valid.
        /// </summary>
        /// <param name="controller">Controller whose parameters are checked.</param>
        /// <param name="parameterName">Referenced parameter name, or null to clear.</param>
        /// <param name="requiredType">Parameter type the modifier needs.</param>
        /// <param name="fieldName">Payload field name, for the error message.</param>
        /// <returns>An error string when the reference is invalid, otherwise null.</returns>
        private static string ValidateAnimatorParameterReference(AnimatorController controller, string parameterName, AnimatorControllerParameterType requiredType, string fieldName)
        {
            if (parameterName == null)
            {
                return null;
            }

            foreach (var parameter in controller.parameters)
            {
                if (!string.Equals(parameter.name, parameterName, StringComparison.Ordinal))
                {
                    continue;
                }

                return parameter.type == requiredType
                    ? null
                    : fieldName + " '" + parameterName + "' is a " + parameter.type + " parameter but must be " + requiredType + ". Nothing was applied.";
            }

            return fieldName + " references parameter '" + parameterName + "' which does not exist on the controller. Add it with animator-parameter-set first. Nothing was applied.";
        }

        /// <summary>
        /// Applies one parameter-driven state modifier (speed/mirror/cycleOffset/time parameter):
        /// null deactivates the driver, a name activates it, and the evidence line reports the change.
        /// </summary>
        /// <param name="fieldName">Payload field name, for the evidence line.</param>
        /// <param name="oldValue">Current parameter name, or null when the driver is inactive.</param>
        /// <param name="newValue">Requested parameter name, or null to deactivate.</param>
        /// <param name="setActive">Setter for the driver's active flag.</param>
        /// <param name="setName">Setter for the driver's parameter name.</param>
        /// <returns>The evidence line for the report.</returns>
        private static string ApplyStateParameterDriver(string fieldName, string oldValue, string newValue, Action<bool> setActive, Action<string> setName)
        {
            if (oldValue == newValue)
            {
                return fieldName + " already " + (oldValue ?? "null");
            }

            setActive(newValue != null);
            if (newValue != null)
            {
                setName(newValue);
            }

            return fieldName + " " + (oldValue ?? "null") + " -> " + (newValue ?? "null");
        }

        /// <summary>
        /// Removes a state by path. Every transition pointing at the state (from other states,
        /// any-state, or entry) is detached by Unity along with it; the report says so.
        /// </summary>
        /// <param name="args">Pipe-separated controller path, layer, and state path.</param>
        /// <returns>A confirmation naming the removed state, or an error.</returns>
        private static string AnimatorStateRemoveCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return "Usage: animator-state-remove <controllerPath> | <layerNameOrIndex> | <statePath>";
            }

            AnimatorController controller;
            string assetPath;
            var error = TryLoadAnimatorController(sections[0], out controller, out assetPath);
            if (error != null)
            {
                return error;
            }

            AnimatorControllerLayer[] layers;
            int layerIndex;
            error = TryResolveAnimatorLayer(controller, sections[1], out layers, out layerIndex);
            if (error != null)
            {
                return error;
            }

            AnimatorState state;
            AnimatorStateMachine owner;
            error = TryResolveStatePath(layers[layerIndex].stateMachine, sections[2], out state, out owner);
            if (error != null)
            {
                return error;
            }

            var name = state.name;
            var wasDefault = owner.defaultState == state;
            owner.RemoveState(state);
            PersistAnimationAssetEdit(controller);
            return "Removed state '" + name + "' from state machine '" + owner.name + "' in layer '" + layers[layerIndex].name + "' on " + assetPath +
                ". Transitions targeting it were removed with it." + (wasDefault ? " It was the DEFAULT state - set a new default with animator-state-set makeDefault." : string.Empty);
        }



        /// <summary>
        /// Adds a sub-state machine at a slash-separated path (parents must exist), or updates an
        /// existing one's layout anchors from a flat JSON payload: entryPosition, exitPosition,
        /// anyStatePosition, parentStateMachinePosition - each a [x, y] array.
        /// </summary>
        /// <param name="args">Pipe-separated controller path, layer, state-machine path, and options JSON.</param>
        /// <returns>A per-property evidence report, or an error with nothing applied.</returns>
        private static string AnimatorStateMachineSetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return "Usage: animator-statemachine-set <controllerPath> | <layerNameOrIndex> | <smPath> [| {\"entryPosition\":[x,y],\"exitPosition\":[x,y],\"anyStatePosition\":[x,y],\"parentStateMachinePosition\":[x,y]}]";
            }

            AnimatorController controller;
            string assetPath;
            var error = TryLoadAnimatorController(sections[0], out controller, out assetPath);
            if (error != null)
            {
                return error;
            }

            AnimatorControllerLayer[] layers;
            int layerIndex;
            error = TryResolveAnimatorLayer(controller, sections[1], out layers, out layerIndex);
            if (error != null)
            {
                return error;
            }

            List<KeyValuePair<string, string>> fields;
            error = TryParsePayloadSection(sections.Length > 3 ? sections[3] : string.Empty, out fields);
            if (error != null)
            {
                return error;
            }

            var root = layers[layerIndex].stateMachine;
            AnimatorStateMachine target;
            var created = false;
            if (TryResolveStateMachinePath(root, sections[2], out target) != null)
            {
                // The path's parent chain must exist; the final segment becomes the new sub-SM.
                var trimmed = sections[2].Trim().Trim('"');
                var split = trimmed.LastIndexOf('/');
                var parentPath = split < 0 ? string.Empty : trimmed.Substring(0, split);
                var smName = split < 0 ? trimmed : trimmed.Substring(split + 1);
                if (smName.Length == 0)
                {
                    return "Missing state machine name in path: '" + sections[2] + "'.";
                }

                AnimatorStateMachine parent;
                error = TryResolveStateMachinePath(root, parentPath, out parent);
                if (error != null)
                {
                    return error + " Create parents first, one animator-statemachine-set per level.";
                }

                target = parent.AddStateMachine(smName);
                created = true;
            }

            // Pass 1 - validate the layout payload.
            var anchors = new List<KeyValuePair<string, Vector3>>();
            var unknownFields = new List<string>();
            foreach (var entry in fields)
            {
                switch (entry.Key)
                {
                    case "entryPosition":
                    case "exitPosition":
                    case "anyStatePosition":
                    case "parentStateMachinePosition":
                        Vector3 position;
                        if (!TryJsonTokenToPosition(entry.Value, out position)) return entry.Key + " must be a [x, y] number array. Nothing was applied.";
                        anchors.Add(new KeyValuePair<string, Vector3>(entry.Key, position));
                        break;

                    default:
                        unknownFields.Add(entry.Key);
                        break;
                }
            }

            // Pass 2 - apply anchors with evidence.
            var changes = new List<string>();
            foreach (var anchor in anchors)
            {
                Vector3 old;
                switch (anchor.Key)
                {
                    case "entryPosition":               old = target.entryPosition;               target.entryPosition = anchor.Value;               break;
                    case "exitPosition":                old = target.exitPosition;                target.exitPosition = anchor.Value;                break;
                    case "anyStatePosition":            old = target.anyStatePosition;            target.anyStatePosition = anchor.Value;            break;
                    default:                            old = target.parentStateMachinePosition;  target.parentStateMachinePosition = anchor.Value;  break;
                }

                changes.Add(old == anchor.Value
                    ? anchor.Key + " already (" + JsonNumber(anchor.Value.x) + ", " + JsonNumber(anchor.Value.y) + ")"
                    : anchor.Key + " (" + JsonNumber(old.x) + ", " + JsonNumber(old.y) + ") -> (" + JsonNumber(anchor.Value.x) + ", " + JsonNumber(anchor.Value.y) + ")");
            }

            PersistAnimationAssetEdit(controller);

            var sb = new StringBuilder();
            sb.Append(created ? "Added sub-state machine '" : "Sub-state machine '").Append(target.name)
              .Append("' in layer '").Append(layers[layerIndex].name).Append("' on ").Append(assetPath).Append('.');
            if (changes.Count > 0) sb.Append(' ').Append(string.Join("; ", changes)).Append('.');
            if (unknownFields.Count > 0) sb.Append(" WARNING ignored unknown field(s): ").Append(string.Join(", ", unknownFields)).Append('.');
            return sb.ToString();
        }

        /// <summary>
        /// Removes a sub-state machine by path, including every state and transition inside it.
        /// The layer root cannot be removed (remove the layer instead).
        /// </summary>
        /// <param name="args">Pipe-separated controller path, layer, and state-machine path.</param>
        /// <returns>A confirmation naming the removed state machine, or an error.</returns>
        private static string AnimatorStateMachineRemoveCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return "Usage: animator-statemachine-remove <controllerPath> | <layerNameOrIndex> | <smPath>";
            }

            AnimatorController controller;
            string assetPath;
            var error = TryLoadAnimatorController(sections[0], out controller, out assetPath);
            if (error != null)
            {
                return error;
            }

            AnimatorControllerLayer[] layers;
            int layerIndex;
            error = TryResolveAnimatorLayer(controller, sections[1], out layers, out layerIndex);
            if (error != null)
            {
                return error;
            }

            var root = layers[layerIndex].stateMachine;
            var trimmed = sections[2].Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                return "Cannot remove the layer's root state machine - remove the layer itself with animator-layer-remove.";
            }

            AnimatorStateMachine target;
            error = TryResolveStateMachinePath(root, trimmed, out target);
            if (error != null)
            {
                return error;
            }

            var split = trimmed.LastIndexOf('/');
            var parentPath = split < 0 ? string.Empty : trimmed.Substring(0, split);
            AnimatorStateMachine parent;
            error = TryResolveStateMachinePath(root, parentPath, out parent);
            if (error != null)
            {
                return error;
            }

            var name = target.name;
            var stateCount = target.states.Length;
            parent.RemoveStateMachine(target);
            PersistAnimationAssetEdit(controller);
            return "Removed sub-state machine '" + name + "' (and its " + stateCount + " state(s)) from '" + parent.name + "' in layer '" + layers[layerIndex].name + "' on " + assetPath + ".";
        }
    }
}
