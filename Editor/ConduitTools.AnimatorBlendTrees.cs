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
    /// Animator blend trees: definition parsing and tree construction.
    /// </summary>
    internal static partial class ConduitBridge
    {



        /// <summary>
        /// Builds a blend tree and assigns it as a state's motion, creating the state when it does
        /// not exist yet. The tree definition JSON supports every blend type, nested child trees,
        /// and per-child settings; it REPLACES the state's current motion. The whole definition
        /// validates (blend parameters must exist as Floats, child clips must load) before any
        /// tree object is created, so a bad payload leaves the controller untouched.
        /// </summary>
        /// <param name="args">Pipe-separated controller path, layer, state path, and tree JSON.</param>
        /// <returns>A build summary with child counts, or an error with nothing applied.</returns>
        private static string AnimatorBlendTreeSetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 4)
            {
                return "Usage: animator-blendtree-set <controllerPath> | <layerNameOrIndex> | <statePath> | {\"blendType\":\"Simple1D|SimpleDirectional2D|FreeformDirectional2D|FreeformCartesian2D|Direct\",\"parameter\":\"...\",\"parameterY\":\"...\",\"useAutomaticThresholds\":bool,\"children\":[{\"clip\":\"Assets/....anim\"|\"tree\":{...},\"threshold\":n,\"position\":[x,y],\"timeScale\":1,\"mirror\":false,\"cycleOffset\":0,\"directParameter\":\"...\"},...]}";
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
            error = TryParsePayloadSection(sections[3], out fields);
            if (error != null)
            {
                return error;
            }

            if (fields.Count == 0)
            {
                return "Missing blend tree definition JSON.";
            }

            // Pass 1 - validate the whole tree definition before creating anything.
            BlendTreeDefinition definition;
            error = TryParseBlendTreeDefinition(controller, fields, out definition);
            if (error != null)
            {
                return error;
            }

            // Resolve or create the target state.
            var root = layers[layerIndex].stateMachine;
            AnimatorState state;
            AnimatorStateMachine owner;
            var created = false;
            if (TryResolveStatePath(root, sections[2], out state, out owner) != null)
            {
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

            // Pass 2 - build. Trees are controller sub-assets; the root tree and every nested tree
            // are added to the controller asset explicitly and hidden from the hierarchy, the same
            // shape the Animator window produces.
            var oldMotion = state.motion == null ? "null" : state.motion.name;
            var tree = BuildBlendTree(controller, definition, state.name + " Tree");
            state.motion = tree;
            PersistAnimationAssetEdit(controller);

            var totals = CountBlendTreeChildren(definition);
            return (created ? "Added state '" : "State '") + state.name + "' in layer '" + layers[layerIndex].name + "' on " + assetPath +
                ": motion " + oldMotion + " -> blend tree (" + definition.BlendType + ", " + totals.Key + " clip(s), " + totals.Value + " nested tree(s))." +
                " Note: the previous motion object stays in the asset if it was itself a blend tree; re-check with animator-get.";
        }

        /// <summary>Validated in-memory form of a blend tree definition payload.</summary>
        private sealed class BlendTreeDefinition
        {
            /// <summary>Blend type of this tree node.</summary>
            public BlendTreeType BlendType;

            /// <summary>Primary blend parameter (unused by Direct trees).</summary>
            public string Parameter;

            /// <summary>Secondary blend parameter for 2D blend types.</summary>
            public string ParameterY;

            /// <summary>Whether 1D thresholds are computed automatically.</summary>
            public bool? UseAutomaticThresholds;

            /// <summary>True when any child carried an explicit threshold, so automatic thresholds must default OFF.</summary>
            public bool HasExplicitThresholds;

            /// <summary>Validated children, clips and nested trees alike.</summary>
            public readonly List<BlendTreeChildDefinition> Children = new List<BlendTreeChildDefinition>();
        }

        /// <summary>Validated in-memory form of one blend tree child entry.</summary>
        private sealed class BlendTreeChildDefinition
        {
            /// <summary>Resolved clip motion; null when the child is a nested tree.</summary>
            public Motion Clip;

            /// <summary>Nested tree definition; null when the child is a clip.</summary>
            public BlendTreeDefinition Tree;

            /// <summary>1D threshold.</summary>
            public float Threshold;

            /// <summary>2D blend-space position.</summary>
            public Vector2 Position;

            /// <summary>Playback speed multiplier.</summary>
            public float TimeScale = 1f;

            /// <summary>Whether the child motion plays mirrored.</summary>
            public bool Mirror;

            /// <summary>Cycle offset applied to the child motion.</summary>
            public float CycleOffset;

            /// <summary>Weight parameter for Direct trees.</summary>
            public string DirectParameter;
        }

        /// <summary>
        /// Parses and validates a blend tree definition, recursing into nested trees. Blend
        /// parameters must exist on the controller as Floats and every child clip must load;
        /// the first failure aborts the whole parse so nothing gets half-built.
        /// </summary>
        /// <param name="controller">Controller whose parameters validate the blend inputs.</param>
        /// <param name="fields">Parsed definition fields.</param>
        /// <param name="definition">The validated definition.</param>
        /// <returns>An error string when invalid, otherwise null.</returns>
        private static string TryParseBlendTreeDefinition(AnimatorController controller, List<KeyValuePair<string, string>> fields, out BlendTreeDefinition definition)
        {
            definition = new BlendTreeDefinition { BlendType = BlendTreeType.Simple1D };
            string childrenToken = null;
            foreach (var entry in fields)
            {
                switch (entry.Key)
                {
                    case "blendType":
                        BlendTreeType blendType;
                        if (!TryJsonTokenToEnum(entry.Value, out blendType)) return "blendType '" + entry.Value + "' is not one of " + string.Join(", ", Enum.GetNames(typeof(BlendTreeType))) + ". Nothing was applied.";
                        definition.BlendType = blendType;
                        break;

                    case "parameter":
                        definition.Parameter = JsonTokenToString(entry.Value);
                        break;

                    case "parameterY":
                        definition.ParameterY = JsonTokenToString(entry.Value);
                        break;

                    case "useAutomaticThresholds":
                        bool auto;
                        if (!TryJsonTokenToBool(entry.Value, out auto)) return "useAutomaticThresholds '" + entry.Value + "' is not a bool. Nothing was applied.";
                        definition.UseAutomaticThresholds = auto;
                        break;

                    case "children":
                        childrenToken = entry.Value;
                        break;

                    default:
                        return "Unknown blend tree field '" + entry.Key + "'. Use blendType, parameter, parameterY, useAutomaticThresholds, children. Nothing was applied.";
                }
            }

            // Blend inputs: every non-Direct tree needs a primary parameter; 2D types need Y too.
            if (definition.BlendType != BlendTreeType.Direct)
            {
                var parameterError = ValidateAnimatorParameterReference(controller, definition.Parameter ?? string.Empty, AnimatorControllerParameterType.Float, "parameter");
                if (string.IsNullOrEmpty(definition.Parameter)) return "Blend tree is missing its \"parameter\" (a Float controller parameter). Nothing was applied.";
                if (parameterError != null) return parameterError;

                var is2D = definition.BlendType != BlendTreeType.Simple1D;
                if (is2D)
                {
                    if (string.IsNullOrEmpty(definition.ParameterY)) return definition.BlendType + " needs \"parameterY\" (a Float controller parameter). Nothing was applied.";
                    var parameterYError = ValidateAnimatorParameterReference(controller, definition.ParameterY, AnimatorControllerParameterType.Float, "parameterY");
                    if (parameterYError != null) return parameterYError;
                }
            }

            if (childrenToken == null)
            {
                return "Blend tree is missing its \"children\" array. Nothing was applied.";
            }

            List<string> items;
            if (!TrySplitJsonArray(childrenToken, out items)) return "children must be a JSON array. Nothing was applied.";
            if (items.Count == 0) return "children is empty - a blend tree with no motions plays nothing. Nothing was applied.";

            foreach (var item in items)
            {
                List<KeyValuePair<string, string>> childFields;
                if (!TrySplitFlatJsonObject(item, out childFields)) return "Each child must be a JSON object with \"clip\" or \"tree\". Nothing was applied.";

                var child = new BlendTreeChildDefinition();
                foreach (var field in childFields)
                {
                    switch (field.Key)
                    {
                        case "clip":
                            var clipToken = JsonTokenToString(field.Value);
                            if (clipToken == null) return "A child \"clip\" is null. Nothing was applied.";
                            child.Clip = AssetDatabase.LoadAssetAtPath<Motion>(NormalizeAssetPath(clipToken));
                            if (child.Clip == null) return "Child clip not found: " + clipToken + ". Nothing was applied.";
                            break;

                        case "tree":
                            List<KeyValuePair<string, string>> nestedFields;
                            if (!TrySplitFlatJsonObject(field.Value, out nestedFields)) return "A child \"tree\" must be a blend tree definition object. Nothing was applied.";
                            BlendTreeDefinition nested;
                            var nestedError = TryParseBlendTreeDefinition(controller, nestedFields, out nested);
                            if (nestedError != null) return nestedError;
                            child.Tree = nested;
                            break;

                        case "threshold":
                            if (!TryJsonTokenToFloat(field.Value, out child.Threshold)) return "Child threshold '" + field.Value + "' is not a number. Nothing was applied.";
                            definition.HasExplicitThresholds = true;
                            break;

                        case "position":
                            Vector3 position;
                            if (!TryJsonTokenToPosition(field.Value, out position)) return "Child position must be a [x, y] number array. Nothing was applied.";
                            child.Position = new Vector2(position.x, position.y);
                            break;

                        case "timeScale":
                            if (!TryJsonTokenToFloat(field.Value, out child.TimeScale)) return "Child timeScale '" + field.Value + "' is not a number. Nothing was applied.";
                            break;

                        case "mirror":
                            if (!TryJsonTokenToBool(field.Value, out child.Mirror)) return "Child mirror '" + field.Value + "' is not a bool. Nothing was applied.";
                            break;

                        case "cycleOffset":
                            if (!TryJsonTokenToFloat(field.Value, out child.CycleOffset)) return "Child cycleOffset '" + field.Value + "' is not a number. Nothing was applied.";
                            break;

                        case "directParameter":
                            child.DirectParameter = JsonTokenToString(field.Value);
                            var directError = ValidateAnimatorParameterReference(controller, child.DirectParameter, AnimatorControllerParameterType.Float, "directParameter");
                            if (directError != null) return directError;
                            break;

                        default:
                            return "Unknown blend tree child field '" + field.Key + "'. Nothing was applied.";
                    }
                }

                if (child.Clip == null && child.Tree == null) return "A child has neither \"clip\" nor \"tree\". Nothing was applied.";
                if (child.Clip != null && child.Tree != null) return "A child has BOTH \"clip\" and \"tree\" - pick one. Nothing was applied.";
                if (definition.BlendType == BlendTreeType.Direct && string.IsNullOrEmpty(child.DirectParameter)) return "Direct blend tree children each need a \"directParameter\". Nothing was applied.";
                definition.Children.Add(child);
            }

            return null;
        }

        /// <summary>
        /// Builds a BlendTree object graph from a validated definition, registering every tree as a
        /// hidden sub-asset of the controller - the same persistence shape the Animator window
        /// produces, so nothing leaks as a loose object.
        /// </summary>
        /// <param name="controller">Controller asset the trees are added to.</param>
        /// <param name="definition">Validated definition to build.</param>
        /// <param name="name">Name for this tree node.</param>
        /// <returns>The built tree.</returns>
        private static BlendTree BuildBlendTree(AnimatorController controller, BlendTreeDefinition definition, string name)
        {
            var tree = new BlendTree
            {
                name           = name,
                blendType      = definition.BlendType,
                hideFlags      = HideFlags.HideInHierarchy
            };
            if (definition.Parameter != null) tree.blendParameter = definition.Parameter;
            if (definition.ParameterY != null) tree.blendParameterY = definition.ParameterY;

            // Set BEFORE assigning children: Unity recomputes thresholds evenly the moment the
            // children array lands while this flag is true (a new BlendTree defaults it ON), which
            // would silently discard explicit thresholds. Explicit thresholds therefore default it
            // OFF unless the payload says otherwise.
            tree.useAutomaticThresholds = definition.UseAutomaticThresholds ?? !definition.HasExplicitThresholds;
            AssetDatabase.AddObjectToAsset(tree, controller);

            // Children are assembled as a whole array so per-child settings (timeScale, mirror,
            // direct parameter) land in one write - AddChild alone cannot set them.
            var children = new ChildMotion[definition.Children.Count];
            for (var i = 0; i < definition.Children.Count; i++)
            {
                var child = definition.Children[i];
                children[i] = new ChildMotion
                {
                    motion               = child.Tree != null ? BuildBlendTree(controller, child.Tree, name + " " + (i + 1)) : child.Clip,
                    threshold            = child.Threshold,
                    position             = child.Position,
                    timeScale            = child.TimeScale,
                    mirror               = child.Mirror,
                    cycleOffset          = child.CycleOffset,
                    directBlendParameter = string.IsNullOrEmpty(child.DirectParameter) ? "Blend" : child.DirectParameter
                };
            }

            tree.children = children;

            return tree;
        }

        /// <summary>Counts clips and nested trees in a definition, recursively, for the build report.</summary>
        /// <param name="definition">Definition to count.</param>
        /// <returns>Pair of (clip count, nested tree count).</returns>
        private static KeyValuePair<int, int> CountBlendTreeChildren(BlendTreeDefinition definition)
        {
            var clips = 0;
            var trees = 0;
            foreach (var child in definition.Children)
            {
                if (child.Tree != null)
                {
                    trees++;
                    var nested = CountBlendTreeChildren(child.Tree);
                    clips += nested.Key;
                    trees += nested.Value;
                }
                else
                {
                    clips++;
                }
            }

            return new KeyValuePair<int, int>(clips, trees);
        }
    }
}
