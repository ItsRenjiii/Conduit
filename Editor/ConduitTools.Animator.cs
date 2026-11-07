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
    /// Animator authoring commands for the Conduit bridge: AnimatorController graphs (parameters,
    /// layers, states, sub-state machines, transitions, blend trees), AvatarMask assets, and
    /// AnimationClip assets (curves, settings, events). Every mutating verb validates its whole
    /// payload before applying anything, applies atomically, persists via SetDirty + SaveAssets,
    /// and reports exactly what changed - never a blanket success.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Resolving controllers, layers and paths

        /// <summary>
        /// Resolves an AnimatorController asset from a path/GUID/instance-ID token.
        /// </summary>
        /// <param name="input">Asset path, GUID, or instance ID of the controller.</param>
        /// <param name="controller">The resolved controller.</param>
        /// <param name="assetPath">The resolved project-relative asset path.</param>
        /// <returns>An error string when resolution fails, otherwise null.</returns>
        private static string TryLoadAnimatorController(string input, out AnimatorController controller, out string assetPath)
        {
            controller = null;
            UnityEngine.Object asset;
            var error = TryFindAssetObject(input, out asset, out assetPath);
            if (error != null)
            {
                return error;
            }

            controller = asset as AnimatorController;
            return controller == null
                ? "Asset is not an AnimatorController: " + assetPath + " (" + asset.GetType().FullName + ")."
                : null;
        }

        /// <summary>
        /// Resolves a layer on a controller by name or zero-based index. Returns the live layers
        /// array so the caller can mutate an element and write the whole array back through
        /// <c>controller.layers = layers</c> - the array is a copy, so an un-reassigned edit is a no-op.
        /// </summary>
        /// <param name="controller">Controller that owns the layer.</param>
        /// <param name="selector">Layer name or zero-based index.</param>
        /// <param name="layers">The controller's layers array copy.</param>
        /// <param name="layerIndex">Resolved index into <paramref name="layers"/>.</param>
        /// <returns>An error string when no layer matches, otherwise null.</returns>
        private static string TryResolveAnimatorLayer(AnimatorController controller, string selector, out AnimatorControllerLayer[] layers, out int layerIndex)
        {
            layers = controller.layers;
            layerIndex = -1;
            if (string.IsNullOrWhiteSpace(selector))
            {
                return "Missing layer selector. Use a layer name or zero-based index.";
            }

            selector = selector.Trim().Trim('"');
            int numeric;
            if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                if (numeric >= 0 && numeric < layers.Length)
                {
                    layerIndex = numeric;
                    return null;
                }

                return "Layer index " + numeric + " is out of range (controller has " + layers.Length + " layer(s)).";
            }

            for (var i = 0; i < layers.Length; i++)
            {
                if (string.Equals(layers[i].name, selector, StringComparison.OrdinalIgnoreCase))
                {
                    layerIndex = i;
                    return null;
                }
            }

            return "Layer not found: " + selector + ". Available: " + JoinLayerNames(layers) + ".";
        }

        /// <summary>Renders the layer names of a controller for lookup-failure messages.</summary>
        /// <param name="layers">Layers to list.</param>
        /// <returns>A comma-separated layer-name list, or a placeholder for an empty controller.</returns>
        private static string JoinLayerNames(AnimatorControllerLayer[] layers)
        {
            if (layers == null || layers.Length == 0)
            {
                return "(none)";
            }

            var names = new string[layers.Length];
            for (var i = 0; i < layers.Length; i++)
            {
                names[i] = layers[i].name;
            }

            return string.Join(", ", names);
        }

        /// <summary>
        /// Walks a slash-separated sub-state-machine path (e.g. <c>Combat/Melee</c>) from a root
        /// state machine. An empty path resolves to the root itself.
        /// </summary>
        /// <param name="root">Root state machine of the layer.</param>
        /// <param name="path">Slash-separated sub-state-machine names, or empty for the root.</param>
        /// <param name="stateMachine">The resolved state machine.</param>
        /// <returns>An error string when a path segment does not exist, otherwise null.</returns>
        private static string TryResolveStateMachinePath(AnimatorStateMachine root, string path, out AnimatorStateMachine stateMachine)
        {
            stateMachine = root;
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            foreach (var segment in path.Trim().Trim('"').Split('/'))
            {
                var name = segment.Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                AnimatorStateMachine next = null;
                foreach (var child in stateMachine.stateMachines)
                {
                    if (string.Equals(child.stateMachine.name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        next = child.stateMachine;
                        break;
                    }
                }

                if (next == null)
                {
                    return "Sub-state machine not found: '" + name + "' under '" + stateMachine.name + "'.";
                }

                stateMachine = next;
            }

            return null;
        }

        /// <summary>
        /// Resolves a state by path within a layer: the final segment is the state name and any
        /// leading segments are sub-state machines (e.g. <c>Combat/Slash</c>). Also returns the
        /// state machine that directly owns the state, which transition and removal edits need.
        /// </summary>
        /// <param name="root">Root state machine of the layer.</param>
        /// <param name="statePath">State name, optionally prefixed with sub-state-machine names.</param>
        /// <param name="state">The resolved state.</param>
        /// <param name="owner">The state machine that directly contains the state.</param>
        /// <returns>An error string when the state cannot be found, otherwise null.</returns>
        private static string TryResolveStatePath(AnimatorStateMachine root, string statePath, out AnimatorState state, out AnimatorStateMachine owner)
        {
            state = null;
            owner = null;
            if (string.IsNullOrWhiteSpace(statePath))
            {
                return "Missing state path. Use a state name, optionally prefixed with sub-state-machine names (e.g. Combat/Slash).";
            }

            var trimmed = statePath.Trim().Trim('"');
            var split = trimmed.LastIndexOf('/');
            var smPath = split < 0 ? string.Empty : trimmed.Substring(0, split);
            var stateName = split < 0 ? trimmed : trimmed.Substring(split + 1);

            var error = TryResolveStateMachinePath(root, smPath, out owner);
            if (error != null)
            {
                return error;
            }

            foreach (var child in owner.states)
            {
                if (string.Equals(child.state.name, stateName, StringComparison.OrdinalIgnoreCase))
                {
                    state = child.state;
                    return null;
                }
            }

            return "State not found: '" + stateName + "' in state machine '" + owner.name + "'.";
        }

        /// <summary>
        /// Marks a controller (or any animation asset) dirty and saves the AssetDatabase so the
        /// edit is on disk before the command reports success.
        /// </summary>
        /// <param name="asset">The edited asset.</param>
        private static void PersistAnimationAssetEdit(UnityEngine.Object asset)
        {
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
        }

        #endregion

        #region Controller verbs



        /// <summary>
        /// Creates an AnimatorController asset with its default base layer.
        /// </summary>
        /// <param name="args">The target Assets/*.controller path.</param>
        /// <returns>A confirmation line with the created asset reference, or a validation error.</returns>
        private static string AnimatorCreateCommand(string args)
        {
            var assetPath = NormalizeAssetPath(args);
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !assetPath.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
            {
                return "Usage: animator-create <assetPath> - path must be an Assets/*.controller path.";
            }

            if (AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath) != null)
            {
                return "AnimatorController already exists: " + assetPath + ". Edit it with the other animator-* verbs, or delete it first.";
            }

            EnsureAssetFolder(Path.GetDirectoryName(assetPath));
            var controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);
            if (controller == null)
            {
                return "Unity failed to create an AnimatorController at " + assetPath + ".";
            }

            return "Created AnimatorController " + assetPath + " with layer '" + controller.layers[0].name + "'. Asset: " + JsonObjectRef(controller);
        }

        /// <summary>
        /// Reads a controller's complete graph - parameters, layers, and each layer's recursive
        /// state machine with states, motions, blend trees, and every transition - as JSON. This is
        /// the honest read-back companion to the animator-* write verbs: EditorJsonUtility cannot
        /// traverse this multi-object graph, so this walk is the only bridge view of it.
        /// </summary>
        /// <param name="args">The controller asset path, GUID, or instance ID.</param>
        /// <returns>A JSON object describing the whole controller, or a lookup error.</returns>
        private static string AnimatorGetCommand(string args)
        {
            AnimatorController controller;
            string assetPath;
            var error = TryLoadAnimatorController(args, out controller, out assetPath);
            if (error != null)
            {
                return error;
            }

            var sb = new StringBuilder();
            sb.Append("{\"asset\":").Append(JsonObjectRef(controller));

            sb.Append(",\"parameters\":[");
            var parameters = controller.parameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var p = parameters[i];
                sb.Append("{\"name\":").Append(JsonQuote(p.name))
                  .Append(",\"type\":").Append(JsonQuote(p.type.ToString()));
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        sb.Append(",\"defaultFloat\":").Append(JsonNumber(p.defaultFloat));
                        break;
                    case AnimatorControllerParameterType.Int:
                        sb.Append(",\"defaultInt\":").Append(p.defaultInt);
                        break;
                    case AnimatorControllerParameterType.Bool:
                        sb.Append(",\"defaultBool\":").Append(p.defaultBool ? "true" : "false");
                        break;
                }

                sb.Append('}');
            }

            sb.Append("],\"layers\":[");
            var layers = controller.layers;
            for (var i = 0; i < layers.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var layer = layers[i];
                sb.Append("{\"index\":").Append(i)
                  .Append(",\"name\":").Append(JsonQuote(layer.name))
                  .Append(",\"defaultWeight\":").Append(JsonNumber(layer.defaultWeight))
                  .Append(",\"blendingMode\":").Append(JsonQuote(layer.blendingMode.ToString()))
                  .Append(",\"ikPass\":").Append(layer.iKPass ? "true" : "false")
                  .Append(",\"avatarMask\":").Append(layer.avatarMask == null ? "null" : JsonObjectRef(layer.avatarMask))
                  .Append(",\"syncedLayerIndex\":").Append(layer.syncedLayerIndex)
                  .Append(",\"syncedLayerAffectsTiming\":").Append(layer.syncedLayerAffectsTiming ? "true" : "false")
                  .Append(",\"stateMachine\":").Append(JsonStateMachine(layer.stateMachine))
                  .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        #endregion

        #region JSON projection

        /// <summary>
        /// Encodes one state machine recursively: states (with motions and outgoing transitions),
        /// any-state/entry transitions, sub-state machines, and the graph layout anchor positions.
        /// </summary>
        /// <param name="sm">The state machine to encode.</param>
        /// <returns>A JSON object for the state machine, or the JSON null token.</returns>
        private static string JsonStateMachine(AnimatorStateMachine sm)
        {
            if (sm == null)
            {
                return "null";
            }

            var sb = new StringBuilder();
            sb.Append("{\"name\":").Append(JsonQuote(sm.name))
              .Append(",\"defaultState\":").Append(sm.defaultState == null ? "null" : JsonQuote(sm.defaultState.name));

            sb.Append(",\"states\":[");
            var states = sm.states;
            for (var i = 0; i < states.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonAnimatorState(states[i].state, states[i].position));
            }

            sb.Append("],\"anyStateTransitions\":[");
            var anyTransitions = sm.anyStateTransitions;
            for (var i = 0; i < anyTransitions.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonAnimatorTransition(anyTransitions[i]));
            }

            sb.Append("],\"entryTransitions\":[");
            var entryTransitions = sm.entryTransitions;
            for (var i = 0; i < entryTransitions.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonAnimatorTransition(entryTransitions[i]));
            }

            sb.Append("],\"stateMachines\":[");
            var children = sm.stateMachines;
            for (var i = 0; i < children.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonStateMachine(children[i].stateMachine));
            }

            sb.Append("],\"entryPosition\":").Append(JsonVector3(sm.entryPosition))
              .Append(",\"exitPosition\":").Append(JsonVector3(sm.exitPosition))
              .Append(",\"anyStatePosition\":").Append(JsonVector3(sm.anyStatePosition))
              .Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Encodes one state: motion (clip reference or inline blend tree), playback modifiers,
        /// graph position, and its outgoing transitions.
        /// </summary>
        /// <param name="state">The state to encode.</param>
        /// <param name="position">The state's node position in its owning state machine.</param>
        /// <returns>A JSON object for the state.</returns>
        private static string JsonAnimatorState(AnimatorState state, Vector3 position)
        {
            var sb = new StringBuilder();
            sb.Append("{\"name\":").Append(JsonQuote(state.name));

            var tree = state.motion as BlendTree;
            if (tree != null)
            {
                sb.Append(",\"blendTree\":").Append(JsonBlendTree(tree));
            }
            else
            {
                sb.Append(",\"motion\":").Append(state.motion == null ? "null" : JsonObjectRef(state.motion));
            }

            sb.Append(",\"speed\":").Append(JsonNumber(state.speed));
            if (state.speedParameterActive) sb.Append(",\"speedParameter\":").Append(JsonQuote(state.speedParameter));
            sb.Append(",\"mirror\":").Append(state.mirror ? "true" : "false");
            if (state.mirrorParameterActive) sb.Append(",\"mirrorParameter\":").Append(JsonQuote(state.mirrorParameter));
            sb.Append(",\"cycleOffset\":").Append(JsonNumber(state.cycleOffset));
            if (state.cycleOffsetParameterActive) sb.Append(",\"cycleOffsetParameter\":").Append(JsonQuote(state.cycleOffsetParameter));
            if (state.timeParameterActive) sb.Append(",\"timeParameter\":").Append(JsonQuote(state.timeParameter));
            sb.Append(",\"writeDefaultValues\":").Append(state.writeDefaultValues ? "true" : "false")
              .Append(",\"ikOnFeet\":").Append(state.iKOnFeet ? "true" : "false");
            if (!string.IsNullOrEmpty(state.tag)) sb.Append(",\"tag\":").Append(JsonQuote(state.tag));
            sb.Append(",\"position\":").Append(JsonVector3(position));

            sb.Append(",\"transitions\":[");
            var transitions = state.transitions;
            for (var i = 0; i < transitions.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(JsonAnimatorTransition(transitions[i]));
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Encodes a transition (state or entry/state-machine kind): destination, conditions, and -
        /// for state transitions - the timing block. Destinations render as the state name,
        /// <c>SM:Name</c> for a state-machine target, or <c>Exit</c>.
        /// </summary>
        /// <param name="transition">The transition to encode.</param>
        /// <returns>A JSON object for the transition.</returns>
        private static string JsonAnimatorTransition(AnimatorTransitionBase transition)
        {
            var sb = new StringBuilder();
            string destination;
            if (transition.isExit) destination = "Exit";
            else if (transition.destinationState != null) destination = transition.destinationState.name;
            else if (transition.destinationStateMachine != null) destination = "SM:" + transition.destinationStateMachine.name;
            else destination = "(none)";

            sb.Append("{\"to\":").Append(JsonQuote(destination))
              .Append(",\"mute\":").Append(transition.mute ? "true" : "false")
              .Append(",\"solo\":").Append(transition.solo ? "true" : "false");

            var stateTransition = transition as AnimatorStateTransition;
            if (stateTransition != null)
            {
                sb.Append(",\"hasExitTime\":").Append(stateTransition.hasExitTime ? "true" : "false")
                  .Append(",\"exitTime\":").Append(JsonNumber(stateTransition.exitTime))
                  .Append(",\"hasFixedDuration\":").Append(stateTransition.hasFixedDuration ? "true" : "false")
                  .Append(",\"duration\":").Append(JsonNumber(stateTransition.duration))
                  .Append(",\"offset\":").Append(JsonNumber(stateTransition.offset))
                  .Append(",\"interruptionSource\":").Append(JsonQuote(stateTransition.interruptionSource.ToString()))
                  .Append(",\"orderedInterruption\":").Append(stateTransition.orderedInterruption ? "true" : "false")
                  .Append(",\"canTransitionToSelf\":").Append(stateTransition.canTransitionToSelf ? "true" : "false");
            }

            sb.Append(",\"conditions\":[");
            var conditions = transition.conditions;
            for (var i = 0; i < conditions.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"parameter\":").Append(JsonQuote(conditions[i].parameter))
                  .Append(",\"mode\":").Append(JsonQuote(conditions[i].mode.ToString()))
                  .Append(",\"threshold\":").Append(JsonNumber(conditions[i].threshold))
                  .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>
        /// Encodes a blend tree recursively: blend type, parameters, and each child motion with its
        /// threshold / 2D position / playback settings. Nested trees encode inline.
        /// </summary>
        /// <param name="tree">The blend tree to encode.</param>
        /// <returns>A JSON object for the blend tree.</returns>
        private static string JsonBlendTree(BlendTree tree)
        {
            var sb = new StringBuilder();
            sb.Append("{\"name\":").Append(JsonQuote(tree.name))
              .Append(",\"blendType\":").Append(JsonQuote(tree.blendType.ToString()))
              .Append(",\"parameter\":").Append(JsonQuote(tree.blendParameter));
            if (tree.blendType != BlendTreeType.Simple1D && tree.blendType != BlendTreeType.Direct)
            {
                sb.Append(",\"parameterY\":").Append(JsonQuote(tree.blendParameterY));
            }

            sb.Append(",\"useAutomaticThresholds\":").Append(tree.useAutomaticThresholds ? "true" : "false")
              .Append(",\"children\":[");
            var children = tree.children;
            for (var i = 0; i < children.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var child = children[i];
                sb.Append('{');
                var nested = child.motion as BlendTree;
                if (nested != null)
                {
                    sb.Append("\"tree\":").Append(JsonBlendTree(nested));
                }
                else
                {
                    sb.Append("\"motion\":").Append(child.motion == null ? "null" : JsonObjectRef(child.motion));
                }

                sb.Append(",\"threshold\":").Append(JsonNumber(child.threshold))
                  .Append(",\"position\":{\"x\":").Append(JsonNumber(child.position.x)).Append(",\"y\":").Append(JsonNumber(child.position.y)).Append('}')
                  .Append(",\"timeScale\":").Append(JsonNumber(child.timeScale))
                  .Append(",\"mirror\":").Append(child.mirror ? "true" : "false")
                  .Append(",\"cycleOffset\":").Append(JsonNumber(child.cycleOffset));
                if (!string.IsNullOrEmpty(child.directBlendParameter))
                {
                    sb.Append(",\"directParameter\":").Append(JsonQuote(child.directBlendParameter));
                }

                sb.Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        #endregion

        #region Payload parsing



        /// <summary>
        /// Splits a raw JSON array token into its element tokens, reusing <see cref="SkipJsonValue"/>
        /// so nested objects, arrays, and quoted strings survive whole.
        /// </summary>
        /// <param name="json">The raw array token, e.g. <c>[{"a":1},{"b":2}]</c>.</param>
        /// <param name="items">The element tokens in order.</param>
        /// <returns>True when the token is a well-formed JSON array.</returns>
        private static bool TrySplitJsonArray(string json, out List<string> items)
        {
            items = new List<string>();
            var text = (json ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '[' || text[text.Length - 1] != ']')
            {
                return false;
            }

            var end = text.Length - 1;
            var i = 1;
            while (i < end)
            {
                while (i < end && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
                if (i >= end) break;

                var valueStart = i;
                if (!SkipJsonValue(text, ref i, end)) return false;
                items.Add(text.Substring(valueStart, i - valueStart).Trim());
            }

            return true;
        }

        /// <summary>
        /// Reads a raw JSON value token as a string: unquotes string tokens, passes primitives
        /// through verbatim, and maps the null token to null.
        /// </summary>
        /// <param name="token">The raw value token.</param>
        /// <returns>The decoded string value, or null for the JSON null token.</returns>
        private static string JsonTokenToString(string token)
        {
            var text = (token ?? string.Empty).Trim();
            if (text.Length == 0 || text.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return text.Length >= 2 && text[0] == '"' && text[text.Length - 1] == '"' ? Unquote(text) : text;
        }

        /// <summary>Parses a raw JSON value token as an invariant-culture float.</summary>
        /// <param name="token">The raw value token.</param>
        /// <param name="value">The parsed value.</param>
        /// <returns>True when the token is a number.</returns>
        private static bool TryJsonTokenToFloat(string token, out float value)
        {
            return float.TryParse((token ?? string.Empty).Trim().Trim('"'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Parses a raw JSON value token as a bool, accepting the bridge's loose forms.</summary>
        /// <param name="token">The raw value token.</param>
        /// <param name="value">The parsed value.</param>
        /// <returns>True when the token is a recognizable bool.</returns>
        private static bool TryJsonTokenToBool(string token, out bool value)
        {
            return TryParseBoolLoose((token ?? string.Empty).Trim(), out value);
        }

        /// <summary>
        /// Parses a raw JSON token as an enum member by name (case-insensitive) or numeric value.
        /// </summary>
        /// <typeparam name="TEnum">The enum type to parse into.</typeparam>
        /// <param name="token">The raw value token.</param>
        /// <param name="value">The parsed enum value.</param>
        /// <returns>True when the token names a member of the enum.</returns>
        private static bool TryJsonTokenToEnum<TEnum>(string token, out TEnum value) where TEnum : struct
        {
            var text = JsonTokenToString(token);
            if (!string.IsNullOrEmpty(text) && Enum.TryParse(text, true, out value) && Enum.IsDefined(typeof(TEnum), value))
            {
                return true;
            }

            value = default(TEnum);
            return false;
        }

        /// <summary>
        /// Parses a two-element JSON number array (e.g. <c>[240, 60]</c>) into a Vector3 with z=0,
        /// the shape animator graph node positions use.
        /// </summary>
        /// <param name="token">The raw array token.</param>
        /// <param name="position">The parsed position.</param>
        /// <returns>True when the token is a two-number array.</returns>
        private static bool TryJsonTokenToPosition(string token, out Vector3 position)
        {
            position = Vector3.zero;
            List<string> items;
            if (!TrySplitJsonArray(token, out items) || items.Count < 2)
            {
                return false;
            }

            float x, y;
            if (!TryJsonTokenToFloat(items[0], out x) || !TryJsonTokenToFloat(items[1], out y))
            {
                return false;
            }

            position = new Vector3(x, y, 0f);
            return true;
        }

        /// <summary>
        /// Parses a pipe section as a flat JSON payload, peeling a stale-server quote wrapper first.
        /// An empty/missing section parses as an empty payload so "no options" is valid.
        /// </summary>
        /// <param name="section">The raw pipe section.</param>
        /// <param name="fields">The parsed key/raw-value pairs.</param>
        /// <returns>An error string for a malformed payload, otherwise null.</returns>
        private static string TryParsePayloadSection(string section, out List<KeyValuePair<string, string>> fields)
        {
            fields = new List<KeyValuePair<string, string>>();
            var payload = UnwrapQuotedJsonSection(section ?? string.Empty);
            if (string.IsNullOrWhiteSpace(payload))
            {
                return null;
            }

            return TrySplitFlatJsonObject(payload, out fields)
                ? null
                : "Payload is not a flat {\"key\": value} JSON object: " + payload;
        }

        #endregion

        #region Dispatch

            /// <summary>
        /// Routes the Animator verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchAnimator(string verb, string command, string[] parts)
        {
            if (verb == "animation-clip-create")
            {
                return AnimationClipCreateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animation-clip-get")
            {
                return AnimationClipGetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animation-clip-set")
            {
                return AnimationClipSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animation-clip-set-curve")
            {
                return AnimationClipSetCurveCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-create")
            {
                return AnimatorCreateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-get")
            {
                return AnimatorGetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-parameter-set")
            {
                return AnimatorParameterSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-parameter-remove")
            {
                return AnimatorParameterRemoveCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-layer-set")
            {
                return AnimatorLayerSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-layer-remove")
            {
                return AnimatorLayerRemoveCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-mask-create")
            {
                return AnimatorMaskCreateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-mask-set")
            {
                return AnimatorMaskSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-state-set")
            {
                return AnimatorStateSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-state-remove")
            {
                return AnimatorStateRemoveCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-statemachine-set")
            {
                return AnimatorStateMachineSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-statemachine-remove")
            {
                return AnimatorStateMachineRemoveCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-transition-add")
            {
                return AnimatorTransitionAddCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-transition-set")
            {
                return AnimatorTransitionSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-transition-remove")
            {
                return AnimatorTransitionRemoveCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animator-blendtree-set")
            {
                return AnimatorBlendTreeSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animation-clip-sample")
            {
                return AnimationClipSampleCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "animation-clip-sample-stop")
            {
                return AnimationClipSampleStopCommand(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }

        #endregion
    }
}
