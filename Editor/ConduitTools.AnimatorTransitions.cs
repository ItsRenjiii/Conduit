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
    /// Animator transitions: endpoint resolution, conditions, and add/set/remove.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Endpoints



        /// <summary>Kinds a transition endpoint token can resolve to.</summary>
        private enum TransitionEndpointKind
        {
            /// <summary>A concrete state, addressed by state path.</summary>
            State,

            /// <summary>A sub-state machine, addressed as <c>SM:path</c>.</summary>
            StateMachine,

            /// <summary>The AnyState node of a state machine (<c>AnyState</c> or <c>Sub/AnyState</c>).</summary>
            AnyState,

            /// <summary>The Entry node of a state machine (<c>Entry</c> or <c>Sub/Entry</c>).</summary>
            Entry,

            /// <summary>The Exit node (<c>Exit</c>), valid as a destination only.</summary>
            Exit
        }

        /// <summary>One resolved transition endpoint: its kind plus the objects the kind implies.</summary>
        private struct TransitionEndpoint
        {
            /// <summary>What the token resolved to.</summary>
            public TransitionEndpointKind Kind;

            /// <summary>Resolved state for <see cref="TransitionEndpointKind.State"/>.</summary>
            public AnimatorState State;

            /// <summary>Resolved state machine for StateMachine / AnyState / Entry kinds.</summary>
            public AnimatorStateMachine StateMachine;

            /// <summary>State machine that directly owns the endpoint (parent SM for a state or sub-SM).</summary>
            public AnimatorStateMachine Owner;

            /// <summary>The original display token, for reports.</summary>
            public string Token;
        }

        /// <summary>
        /// Resolves a transition endpoint token within a layer. Accepted forms: a state path
        /// (<c>Combat/Slash</c>), <c>SM:path</c> for a sub-state machine, <c>AnyState</c> /
        /// <c>Sub/AnyState</c>, <c>Entry</c> / <c>Sub/Entry</c>, and <c>Exit</c>.
        /// </summary>
        /// <param name="root">Root state machine of the layer.</param>
        /// <param name="token">The endpoint token.</param>
        /// <param name="isSource">True when resolving the transition's source (rejects Exit, allows AnyState/Entry).</param>
        /// <param name="endpoint">The resolved endpoint.</param>
        /// <returns>An error string when the token cannot resolve, otherwise null.</returns>
        private static string TryResolveTransitionEndpoint(AnimatorStateMachine root, string token, bool isSource, out TransitionEndpoint endpoint)
        {
            endpoint = new TransitionEndpoint { Token = (token ?? string.Empty).Trim().Trim('"') };
            var text = endpoint.Token;
            if (text.Length == 0)
            {
                return "Missing transition endpoint. Use a state path, SM:<path>, AnyState, Entry, or Exit.";
            }

            if (string.Equals(text, "Exit", StringComparison.OrdinalIgnoreCase))
            {
                if (isSource) return "Exit cannot be a transition source.";
                endpoint.Kind = TransitionEndpointKind.Exit;
                return null;
            }

            // AnyState / Entry, optionally prefixed with a sub-state-machine path. Only a whole
            // final segment counts - a state named "GrandEntry" must fall through to state lookup.
            foreach (var special in new[] { "AnyState", "Entry" })
            {
                var isBare = string.Equals(text, special, StringComparison.OrdinalIgnoreCase);
                var isSegment = text.EndsWith("/" + special, StringComparison.OrdinalIgnoreCase);
                if (!isBare && !isSegment)
                {
                    continue;
                }

                var prefix = isBare ? string.Empty : text.Substring(0, text.Length - special.Length - 1);
                AnimatorStateMachine sm;
                var prefixError = TryResolveStateMachinePath(root, prefix, out sm);
                if (prefixError != null)
                {
                    return prefixError;
                }

                if (!isSource) return special + " cannot be a transition destination.";
                endpoint.Kind = special == "AnyState" ? TransitionEndpointKind.AnyState : TransitionEndpointKind.Entry;
                endpoint.StateMachine = sm;
                endpoint.Owner = sm;
                return null;
            }

            if (text.StartsWith("SM:", StringComparison.OrdinalIgnoreCase))
            {
                var smPath = text.Substring(3).Trim();
                AnimatorStateMachine sm;
                var smError = TryResolveStateMachinePath(root, smPath, out sm);
                if (smError != null)
                {
                    return smError;
                }

                if (sm == root)
                {
                    return "The layer root state machine cannot be a transition endpoint; use its states, AnyState, Entry, or Exit.";
                }

                var split = smPath.Replace('\\', '/').TrimEnd('/').LastIndexOf('/');
                var parentPath = split < 0 ? string.Empty : smPath.Substring(0, split);
                AnimatorStateMachine parent;
                var parentError = TryResolveStateMachinePath(root, parentPath, out parent);
                if (parentError != null)
                {
                    return parentError;
                }

                endpoint.Kind = TransitionEndpointKind.StateMachine;
                endpoint.StateMachine = sm;
                endpoint.Owner = parent;
                return null;
            }

            AnimatorState state;
            AnimatorStateMachine owner;
            var stateError = TryResolveStatePath(root, text, out state, out owner);
            if (stateError != null)
            {
                return stateError + " (For a sub-state machine endpoint use SM:<path>; for special nodes use AnyState, Entry, or Exit.)";
            }

            endpoint.Kind = TransitionEndpointKind.State;
            endpoint.State = state;
            endpoint.Owner = owner;
            return null;
        }

        /// <summary>
        /// Collects the transitions leaving a resolved source endpoint, so set/remove can filter
        /// them by destination. State-machine sources read through the parent's transition store.
        /// </summary>
        /// <param name="source">Resolved source endpoint.</param>
        /// <returns>The outgoing transitions, in Unity's stored order.</returns>
        private static AnimatorTransitionBase[] GetTransitionsFromEndpoint(TransitionEndpoint source)
        {
            switch (source.Kind)
            {
                case TransitionEndpointKind.State:        return ToTransitionBaseArray(source.State.transitions);
                case TransitionEndpointKind.AnyState:     return ToTransitionBaseArray(source.StateMachine.anyStateTransitions);
                case TransitionEndpointKind.Entry:        return source.StateMachine.entryTransitions;
                case TransitionEndpointKind.StateMachine: return source.Owner.GetStateMachineTransitions(source.StateMachine);
                default:                                  return new AnimatorTransitionBase[0];
            }
        }

        /// <summary>Upcasts a state-transition array to the shared base type for uniform filtering.</summary>
        /// <param name="transitions">The state transitions.</param>
        /// <returns>The same transitions typed as the base class.</returns>
        private static AnimatorTransitionBase[] ToTransitionBaseArray(AnimatorStateTransition[] transitions)
        {
            var result = new AnimatorTransitionBase[transitions.Length];
            for (var i = 0; i < transitions.Length; i++) result[i] = transitions[i];
            return result;
        }

        /// <summary>
        /// Removes a transition through the API matching its source endpoint kind.
        /// </summary>
        /// <param name="source">Resolved source endpoint the transition leaves from.</param>
        /// <param name="transition">The transition to remove.</param>
        /// <returns>True when the transition was removed.</returns>
        private static bool RemoveTransitionFromEndpoint(TransitionEndpoint source, AnimatorTransitionBase transition)
        {
            switch (source.Kind)
            {
                case TransitionEndpointKind.State:
                    source.State.RemoveTransition((AnimatorStateTransition)transition);
                    return true;
                case TransitionEndpointKind.AnyState:
                    return source.StateMachine.RemoveAnyStateTransition((AnimatorStateTransition)transition);
                case TransitionEndpointKind.Entry:
                    return source.StateMachine.RemoveEntryTransition((AnimatorTransition)transition);
                case TransitionEndpointKind.StateMachine:
                    return source.Owner.RemoveStateMachineTransition(source.StateMachine, (AnimatorTransition)transition);
                default:
                    return false;
            }
        }

        #endregion

        #region Matching an existing transition

        /// <summary>
        /// Tests whether an existing transition's destination matches a resolved endpoint.
        /// </summary>
        /// <param name="transition">Transition to test.</param>
        /// <param name="destination">Resolved destination endpoint.</param>
        /// <returns>True when the transition points at the endpoint.</returns>
        private static bool TransitionMatchesDestination(AnimatorTransitionBase transition, TransitionEndpoint destination)
        {
            switch (destination.Kind)
            {
                case TransitionEndpointKind.Exit:         return transition.isExit;
                case TransitionEndpointKind.State:        return transition.destinationState == destination.State;
                case TransitionEndpointKind.StateMachine: return transition.destinationStateMachine == destination.StateMachine;
                default:                                  return false;
            }
        }

        /// <summary>
        /// Shared resolver for set/remove: loads the controller, layer, and source endpoint from
        /// the first three sections, then finds the transition matching the destination token.
        /// </summary>
        /// <param name="sections">The command's pipe sections (controller, layer, from, to[#index], ...).</param>
        /// <param name="transition">The resolved transition.</param>
        /// <param name="context">A display string ("from -> to[#i]") for reports.</param>
        /// <returns>An error string when any step fails, otherwise null.</returns>
        private static string ResolveExistingTransition(string[] sections, out AnimatorTransitionBase transition, out string context)
        {
            transition = null;
            context = string.Empty;

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
            TransitionEndpoint source;
            error = TryResolveTransitionEndpoint(root, sections[2], true, out source);
            if (error != null)
            {
                return error;
            }

            return FindTransitionByDestination(root, source, sections[3], out transition, out context);
        }

        /// <summary>
        /// Finds the transition leaving <paramref name="source"/> whose destination matches a token
        /// with optional <c>#index</c> suffix. Zero matches and ambiguous matches are both errors -
        /// the error for an ambiguous match tells the caller to disambiguate with the index.
        /// </summary>
        /// <param name="root">Root state machine of the layer.</param>
        /// <param name="source">Resolved source endpoint.</param>
        /// <param name="destinationToken">Destination token, optionally suffixed <c>#index</c>.</param>
        /// <param name="transition">The matched transition.</param>
        /// <param name="context">A display string ("from -> to[#i]") for reports.</param>
        /// <returns>An error string when resolution fails, otherwise null.</returns>
        private static string FindTransitionByDestination(AnimatorStateMachine root, TransitionEndpoint source, string destinationToken, out AnimatorTransitionBase transition, out string context)
        {
            transition = null;
            var token = (destinationToken ?? string.Empty).Trim().Trim('"');
            var pickIndex = -1;
            var hash = token.LastIndexOf('#');
            if (hash > 0 && int.TryParse(token.Substring(hash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out pickIndex))
            {
                token = token.Substring(0, hash);
            }
            else
            {
                pickIndex = -1;
            }

            context = source.Token + " -> " + token + (pickIndex >= 0 ? "#" + pickIndex : string.Empty);

            TransitionEndpoint destination;
            var error = TryResolveTransitionEndpoint(root, token, false, out destination);
            if (error != null)
            {
                return error;
            }

            var matches = new List<AnimatorTransitionBase>();
            foreach (var candidate in GetTransitionsFromEndpoint(source))
            {
                if (TransitionMatchesDestination(candidate, destination))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 0)
            {
                return "No transition " + source.Token + " -> " + token + " exists.";
            }

            if (pickIndex < 0)
            {
                if (matches.Count > 1)
                {
                    return matches.Count + " transitions match " + source.Token + " -> " + token + "; disambiguate with " + token + "#0 .. " + token + "#" + (matches.Count - 1) + ".";
                }

                transition = matches[0];
                return null;
            }

            if (pickIndex >= matches.Count)
            {
                return "Index #" + pickIndex + " is out of range: only " + matches.Count + " transition(s) match " + source.Token + " -> " + token + ".";
            }

            transition = matches[pickIndex];
            return null;
        }

        #endregion

        #region Payload and conditions

        /// <summary>
        /// Applies a validated transition options payload: <c>conditions</c> (REPLACES the list;
        /// each <c>{"parameter","mode","threshold"}</c> validates against the controller's
        /// parameters, including mode-vs-type compatibility), <c>mute</c>, <c>solo</c>, and - for
        /// state transitions only - hasExitTime, exitTime, hasFixedDuration, duration, offset,
        /// interruptionSource, orderedInterruption, canTransitionToSelf. Timing keys sent to an
        /// entry / state-machine transition are refused, not silently dropped.
        /// </summary>
        /// <param name="controller">Controller that owns the transition (for parameter validation).</param>
        /// <param name="transition">Transition to configure.</param>
        /// <param name="fields">Parsed payload fields.</param>
        /// <param name="report">The per-property evidence report (leading space included when non-empty).</param>
        /// <returns>An error string with nothing applied, otherwise null.</returns>
        private static string ApplyAnimatorTransitionPayload(AnimatorController controller, AnimatorTransitionBase transition, List<KeyValuePair<string, string>> fields, out string report)
        {
            report = string.Empty;
            if (fields.Count == 0)
            {
                return null;
            }

            var stateTransition = transition as AnimatorStateTransition;

            // Pass 1 - validate everything into staged values.
            List<AnimatorCondition> conditions = null;
            bool? mute = null, solo = null;
            bool? hasExitTime = null, hasFixedDuration = null, orderedInterruption = null, canTransitionToSelf = null;
            float? exitTime = null, duration = null, offset = null;
            TransitionInterruptionSource? interruptionSource = null;
            var unknownFields = new List<string>();

            foreach (var entry in fields)
            {
                switch (entry.Key)
                {
                    case "conditions":
                        List<string> items;
                        if (!TrySplitJsonArray(entry.Value, out items)) return "conditions must be a JSON array. Nothing was applied.";
                        conditions = new List<AnimatorCondition>();
                        foreach (var item in items)
                        {
                            AnimatorCondition condition;
                            var conditionError = TryParseAnimatorCondition(controller, item, out condition);
                            if (conditionError != null) return conditionError;
                            conditions.Add(condition);
                        }

                        break;

                    case "mute":
                        bool muteValue;
                        if (!TryJsonTokenToBool(entry.Value, out muteValue)) return "mute '" + entry.Value + "' is not a bool. Nothing was applied.";
                        mute = muteValue;
                        break;

                    case "solo":
                        bool soloValue;
                        if (!TryJsonTokenToBool(entry.Value, out soloValue)) return "solo '" + entry.Value + "' is not a bool. Nothing was applied.";
                        solo = soloValue;
                        break;

                    case "hasExitTime":
                    case "exitTime":
                    case "hasFixedDuration":
                    case "duration":
                    case "offset":
                    case "interruptionSource":
                    case "orderedInterruption":
                    case "canTransitionToSelf":
                        if (stateTransition == null)
                        {
                            return "'" + entry.Key + "' only exists on state transitions; entry and state-machine transitions have conditions, mute, and solo only. Nothing was applied.";
                        }

                        switch (entry.Key)
                        {
                            case "hasExitTime":
                                bool hasExitValue;
                                if (!TryJsonTokenToBool(entry.Value, out hasExitValue)) return "hasExitTime '" + entry.Value + "' is not a bool. Nothing was applied.";
                                hasExitTime = hasExitValue;
                                break;
                            case "exitTime":
                                float exitValue;
                                if (!TryJsonTokenToFloat(entry.Value, out exitValue)) return "exitTime '" + entry.Value + "' is not a number. Nothing was applied.";
                                exitTime = exitValue;
                                break;
                            case "hasFixedDuration":
                                bool fixedValue;
                                if (!TryJsonTokenToBool(entry.Value, out fixedValue)) return "hasFixedDuration '" + entry.Value + "' is not a bool. Nothing was applied.";
                                hasFixedDuration = fixedValue;
                                break;
                            case "duration":
                                float durationValue;
                                if (!TryJsonTokenToFloat(entry.Value, out durationValue)) return "duration '" + entry.Value + "' is not a number. Nothing was applied.";
                                duration = durationValue;
                                break;
                            case "offset":
                                float offsetValue;
                                if (!TryJsonTokenToFloat(entry.Value, out offsetValue)) return "offset '" + entry.Value + "' is not a number. Nothing was applied.";
                                offset = offsetValue;
                                break;
                            case "interruptionSource":
                                TransitionInterruptionSource sourceValue;
                                if (!TryJsonTokenToEnum(entry.Value, out sourceValue)) return "interruptionSource '" + entry.Value + "' is not one of " + string.Join(", ", Enum.GetNames(typeof(TransitionInterruptionSource))) + ". Nothing was applied.";
                                interruptionSource = sourceValue;
                                break;
                            case "orderedInterruption":
                                bool orderedValue;
                                if (!TryJsonTokenToBool(entry.Value, out orderedValue)) return "orderedInterruption '" + entry.Value + "' is not a bool. Nothing was applied.";
                                orderedInterruption = orderedValue;
                                break;
                            default:
                                bool selfValue;
                                if (!TryJsonTokenToBool(entry.Value, out selfValue)) return "canTransitionToSelf '" + entry.Value + "' is not a bool. Nothing was applied.";
                                canTransitionToSelf = selfValue;
                                break;
                        }

                        break;

                    default:
                        unknownFields.Add(entry.Key);
                        break;
                }
            }

            // Pass 2 - apply with per-property evidence.
            var changes = new List<string>();
            if (conditions != null)
            {
                changes.Add("conditions replaced (" + transition.conditions.Length + " -> " + conditions.Count + ")");
                transition.conditions = conditions.ToArray();
            }

            if (mute.HasValue)
            {
                if (transition.mute == mute.Value) changes.Add("mute already " + transition.mute.ToString().ToLowerInvariant());
                else { changes.Add("mute " + transition.mute.ToString().ToLowerInvariant() + " -> " + mute.Value.ToString().ToLowerInvariant()); transition.mute = mute.Value; }
            }

            if (solo.HasValue)
            {
                if (transition.solo == solo.Value) changes.Add("solo already " + transition.solo.ToString().ToLowerInvariant());
                else { changes.Add("solo " + transition.solo.ToString().ToLowerInvariant() + " -> " + solo.Value.ToString().ToLowerInvariant()); transition.solo = solo.Value; }
            }

            if (stateTransition != null)
            {
                if (hasExitTime.HasValue)
                {
                    if (stateTransition.hasExitTime == hasExitTime.Value) changes.Add("hasExitTime already " + stateTransition.hasExitTime.ToString().ToLowerInvariant());
                    else { changes.Add("hasExitTime " + stateTransition.hasExitTime.ToString().ToLowerInvariant() + " -> " + hasExitTime.Value.ToString().ToLowerInvariant()); stateTransition.hasExitTime = hasExitTime.Value; }
                }

                if (exitTime.HasValue)
                {
                    if (Mathf.Approximately(stateTransition.exitTime, exitTime.Value)) changes.Add("exitTime already " + JsonNumber(stateTransition.exitTime));
                    else { changes.Add("exitTime " + JsonNumber(stateTransition.exitTime) + " -> " + JsonNumber(exitTime.Value)); stateTransition.exitTime = exitTime.Value; }
                }

                if (hasFixedDuration.HasValue)
                {
                    if (stateTransition.hasFixedDuration == hasFixedDuration.Value) changes.Add("hasFixedDuration already " + stateTransition.hasFixedDuration.ToString().ToLowerInvariant());
                    else { changes.Add("hasFixedDuration " + stateTransition.hasFixedDuration.ToString().ToLowerInvariant() + " -> " + hasFixedDuration.Value.ToString().ToLowerInvariant()); stateTransition.hasFixedDuration = hasFixedDuration.Value; }
                }

                if (duration.HasValue)
                {
                    if (Mathf.Approximately(stateTransition.duration, duration.Value)) changes.Add("duration already " + JsonNumber(stateTransition.duration));
                    else { changes.Add("duration " + JsonNumber(stateTransition.duration) + " -> " + JsonNumber(duration.Value)); stateTransition.duration = duration.Value; }
                }

                if (offset.HasValue)
                {
                    if (Mathf.Approximately(stateTransition.offset, offset.Value)) changes.Add("offset already " + JsonNumber(stateTransition.offset));
                    else { changes.Add("offset " + JsonNumber(stateTransition.offset) + " -> " + JsonNumber(offset.Value)); stateTransition.offset = offset.Value; }
                }

                if (interruptionSource.HasValue)
                {
                    if (stateTransition.interruptionSource == interruptionSource.Value) changes.Add("interruptionSource already " + stateTransition.interruptionSource);
                    else { changes.Add("interruptionSource " + stateTransition.interruptionSource + " -> " + interruptionSource.Value); stateTransition.interruptionSource = interruptionSource.Value; }
                }

                if (orderedInterruption.HasValue)
                {
                    if (stateTransition.orderedInterruption == orderedInterruption.Value) changes.Add("orderedInterruption already " + stateTransition.orderedInterruption.ToString().ToLowerInvariant());
                    else { changes.Add("orderedInterruption " + stateTransition.orderedInterruption.ToString().ToLowerInvariant() + " -> " + orderedInterruption.Value.ToString().ToLowerInvariant()); stateTransition.orderedInterruption = orderedInterruption.Value; }
                }

                if (canTransitionToSelf.HasValue)
                {
                    if (stateTransition.canTransitionToSelf == canTransitionToSelf.Value) changes.Add("canTransitionToSelf already " + stateTransition.canTransitionToSelf.ToString().ToLowerInvariant());
                    else { changes.Add("canTransitionToSelf " + stateTransition.canTransitionToSelf.ToString().ToLowerInvariant() + " -> " + canTransitionToSelf.Value.ToString().ToLowerInvariant()); stateTransition.canTransitionToSelf = canTransitionToSelf.Value; }
                }
            }

            var sb = new StringBuilder();
            if (changes.Count > 0) sb.Append(' ').Append(string.Join("; ", changes)).Append('.');
            if (unknownFields.Count > 0) sb.Append(" WARNING ignored unknown field(s): ").Append(string.Join(", ", unknownFields)).Append('.');
            report = sb.ToString();
            return null;
        }

        /// <summary>
        /// Parses and validates one condition object: the parameter must exist on the controller
        /// and the mode must be compatible with the parameter's type - an incompatible mode would
        /// compile fine and then silently never fire, which is exactly the class of bug this
        /// validation exists to stop.
        /// </summary>
        /// <param name="controller">Controller whose parameters are checked.</param>
        /// <param name="json">Raw condition object token.</param>
        /// <param name="condition">The parsed condition.</param>
        /// <returns>An error string when invalid, otherwise null.</returns>
        private static string TryParseAnimatorCondition(AnimatorController controller, string json, out AnimatorCondition condition)
        {
            condition = new AnimatorCondition();
            List<KeyValuePair<string, string>> fields;
            if (!TrySplitFlatJsonObject(json, out fields))
            {
                return "Each condition must be a {\"parameter\":\"...\",\"mode\":\"...\",\"threshold\":n} object. Nothing was applied.";
            }

            string parameterName = null;
            AnimatorConditionMode? mode = null;
            float threshold = 0f;
            foreach (var entry in fields)
            {
                switch (entry.Key)
                {
                    case "parameter":
                        parameterName = JsonTokenToString(entry.Value);
                        break;
                    case "mode":
                        AnimatorConditionMode modeValue;
                        if (!TryJsonTokenToEnum(entry.Value, out modeValue)) return "Condition mode '" + entry.Value + "' is not one of " + string.Join(", ", Enum.GetNames(typeof(AnimatorConditionMode))) + ". Nothing was applied.";
                        mode = modeValue;
                        break;
                    case "threshold":
                        if (!TryJsonTokenToFloat(entry.Value, out threshold)) return "Condition threshold '" + entry.Value + "' is not a number. Nothing was applied.";
                        break;
                    default:
                        return "Unknown condition field '" + entry.Key + "'. Use parameter, mode, threshold. Nothing was applied.";
                }
            }

            if (string.IsNullOrEmpty(parameterName)) return "A condition is missing its \"parameter\". Nothing was applied.";

            AnimatorControllerParameter matched = null;
            foreach (var parameter in controller.parameters)
            {
                if (string.Equals(parameter.name, parameterName, StringComparison.Ordinal))
                {
                    matched = parameter;
                    break;
                }
            }

            if (matched == null) return "Condition references parameter '" + parameterName + "' which does not exist on the controller. Add it with animator-parameter-set first. Nothing was applied.";

            // Triggers default to If and take no other mode; other types get their valid mode set enforced.
            if (!mode.HasValue)
            {
                if (matched.type != AnimatorControllerParameterType.Trigger) return "Condition on '" + parameterName + "' is missing its \"mode\". Nothing was applied.";
                mode = AnimatorConditionMode.If;
            }

            string validModes = null;
            switch (matched.type)
            {
                case AnimatorControllerParameterType.Trigger:
                    if (mode.Value != AnimatorConditionMode.If) validModes = "If";
                    break;
                case AnimatorControllerParameterType.Bool:
                    if (mode.Value != AnimatorConditionMode.If && mode.Value != AnimatorConditionMode.IfNot) validModes = "If, IfNot";
                    break;
                case AnimatorControllerParameterType.Int:
                    if (mode.Value != AnimatorConditionMode.Greater && mode.Value != AnimatorConditionMode.Less && mode.Value != AnimatorConditionMode.Equals && mode.Value != AnimatorConditionMode.NotEqual) validModes = "Greater, Less, Equals, NotEqual";
                    break;
                default:
                    if (mode.Value != AnimatorConditionMode.Greater && mode.Value != AnimatorConditionMode.Less) validModes = "Greater, Less";
                    break;
            }

            if (validModes != null)
            {
                return "Condition mode " + mode.Value + " is invalid for " + matched.type + " parameter '" + parameterName + "' (valid: " + validModes + ") - it would silently never fire. Nothing was applied.";
            }

            condition = new AnimatorCondition { parameter = parameterName, mode = mode.Value, threshold = threshold };
            return null;
        }

        #endregion

        #region Verbs



        /// <summary>
        /// Adds a transition between two endpoints and applies an optional options payload in the
        /// same call. Sources: state path, AnyState, Entry, SM:&lt;path&gt;. Destinations: state
        /// path, SM:&lt;path&gt;, Exit. New state transitions get Unity's defaults (exit time on)
        /// until the payload says otherwise.
        /// </summary>
        /// <param name="args">Pipe-separated controller path, layer, source, destination, and options JSON.</param>
        /// <returns>A confirmation with the applied-options report, or an error.</returns>
        private static string AnimatorTransitionAddCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 4)
            {
                return "Usage: animator-transition-add <controllerPath> | <layerNameOrIndex> | <from: statePath|AnyState|Entry|SM:path> | <to: statePath|SM:path|Exit> [| optionsJson]";
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
            TransitionEndpoint source, destination;
            error = TryResolveTransitionEndpoint(root, sections[2], true, out source);
            if (error != null)
            {
                return error;
            }

            error = TryResolveTransitionEndpoint(root, sections[3], false, out destination);
            if (error != null)
            {
                return error;
            }

            List<KeyValuePair<string, string>> fields;
            error = TryParsePayloadSection(sections.Length > 4 ? sections[4] : string.Empty, out fields);
            if (error != null)
            {
                return error;
            }

            // Create the transition through the API that matches the endpoint pair.
            AnimatorTransitionBase transition;
            switch (source.Kind)
            {
                case TransitionEndpointKind.State:
                    if (destination.Kind == TransitionEndpointKind.Exit) transition = source.State.AddExitTransition();
                    else if (destination.Kind == TransitionEndpointKind.State) transition = source.State.AddTransition(destination.State);
                    else transition = source.State.AddTransition(destination.StateMachine);
                    break;

                case TransitionEndpointKind.AnyState:
                    if (destination.Kind == TransitionEndpointKind.Exit) return "AnyState -> Exit is not a valid transition.";
                    transition = destination.Kind == TransitionEndpointKind.State
                        ? source.StateMachine.AddAnyStateTransition(destination.State)
                        : source.StateMachine.AddAnyStateTransition(destination.StateMachine);
                    break;

                case TransitionEndpointKind.Entry:
                    if (destination.Kind == TransitionEndpointKind.Exit) return "Entry -> Exit is not a valid transition.";
                    transition = destination.Kind == TransitionEndpointKind.State
                        ? source.StateMachine.AddEntryTransition(destination.State)
                        : source.StateMachine.AddEntryTransition(destination.StateMachine);
                    break;

                default:
                    if (destination.Kind == TransitionEndpointKind.Exit) transition = source.Owner.AddStateMachineExitTransition(source.StateMachine);
                    else if (destination.Kind == TransitionEndpointKind.State) transition = source.Owner.AddStateMachineTransition(source.StateMachine, destination.State);
                    else transition = source.Owner.AddStateMachineTransition(source.StateMachine, destination.StateMachine);
                    break;
            }

            string report;
            error = ApplyAnimatorTransitionPayload(controller, transition, fields, out report);
            if (error != null)
            {
                // The transition object was created before the payload failed; remove it so a bad
                // payload leaves the controller exactly as it was (atomic apply).
                RemoveTransitionFromEndpoint(source, transition);
                return error;
            }

            PersistAnimationAssetEdit(controller);
            return "Added transition " + source.Token + " -> " + destination.Token + " in layer '" + layers[layerIndex].name + "' on " + assetPath + "." + report;
        }

        /// <summary>
        /// Updates an existing transition addressed as <c>from</c> and <c>to</c>, with an optional
        /// <c>#index</c> suffix on the destination (e.g. <c>Idle#1</c>) when several transitions
        /// share the same endpoints. An ambiguous match without an index is an error, not a guess.
        /// </summary>
        /// <param name="args">Pipe-separated controller path, layer, source, destination[#index], and options JSON.</param>
        /// <returns>A per-property evidence report, or an error with nothing applied.</returns>
        private static string AnimatorTransitionSetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 5)
            {
                return "Usage: animator-transition-set <controllerPath> | <layerNameOrIndex> | <from> | <to[#index]> | <optionsJson>";
            }

            AnimatorTransitionBase transition;
            string context;
            var error = ResolveExistingTransition(sections, out transition, out context);
            if (error != null)
            {
                return error;
            }

            AnimatorController controller;
            string assetPath;
            TryLoadAnimatorController(sections[0], out controller, out assetPath);

            List<KeyValuePair<string, string>> fields;
            error = TryParsePayloadSection(sections[4], out fields);
            if (error != null)
            {
                return error;
            }

            if (fields.Count == 0)
            {
                return "Empty payload: nothing to change on transition " + context + ".";
            }

            string report;
            error = ApplyAnimatorTransitionPayload(controller, transition, fields, out report);
            if (error != null)
            {
                return error;
            }

            PersistAnimationAssetEdit(controller);
            return "Transition " + context + " on " + assetPath + "." + report;
        }

        /// <summary>
        /// Removes an existing transition addressed the same way animator-transition-set addresses one.
        /// </summary>
        /// <param name="args">Pipe-separated controller path, layer, source, and destination[#index].</param>
        /// <returns>A confirmation, or an error.</returns>
        private static string AnimatorTransitionRemoveCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 4)
            {
                return "Usage: animator-transition-remove <controllerPath> | <layerNameOrIndex> | <from> | <to[#index]>";
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
            TransitionEndpoint source;
            error = TryResolveTransitionEndpoint(root, sections[2], true, out source);
            if (error != null)
            {
                return error;
            }

            AnimatorTransitionBase transition;
            string context;
            error = FindTransitionByDestination(root, source, sections[3], out transition, out context);
            if (error != null)
            {
                return error;
            }

            if (!RemoveTransitionFromEndpoint(source, transition))
            {
                return "Unity refused to remove transition " + context + " - it was not found on its source's transition list (stale addressing?). Nothing was removed.";
            }

            PersistAnimationAssetEdit(controller);
            return "Removed transition " + context + " from layer '" + layers[layerIndex].name + "' on " + assetPath + ".";
        }

        #endregion
    }
}
