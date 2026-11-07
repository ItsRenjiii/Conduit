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
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Prefab assets and Prefab Mode: create, open, save, close, unpack and instantiate.
    /// </summary>
    internal static partial class ConduitBridge
    {



        /// <summary>
        /// Closes the active prefab editing stage after explicitly saving or discarding its changes.
        /// </summary>
        /// <param name="args">Optional true/false save flag. An omitted flag safely defaults to save.</param>
        /// <returns>A confirmation line, validation error, or save/discard failure.</returns>
        private static string AssetsPrefabCloseCommand(string args)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                return "No prefab stage is currently open.";
            }

            bool save = true;
            if (!string.IsNullOrWhiteSpace(args) && !TryParseBoolLoose(args, out save))
            {
                return "Usage: assets-prefab-close [true|false]";
            }

            string error;
            if (save)
            {
                if (!TrySavePrefabStageWithoutDialog(stage, out error))
                {
                    return error;
                }
            }
            else if (!TryDiscardPrefabStageWithoutDialog(stage, out error))
            {
                return error;
            }

            string assetPath = stage.assetPath;
            StageUtility.GoBackToPreviousStage();
            return save
                ? "Saved and closed prefab " + assetPath + "."
                : "Discarded changes and closed prefab " + assetPath + ".";
        }

        /// <summary>
        /// Creates a prefab asset from a scene object.
        /// </summary>
        /// <param name="args">Pipe-separated GameObject target and prefab asset path.</param>
        /// <returns>A confirmation line or a validation error.</returns>
        private static string AssetsPrefabCreateCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: assets-prefab-create <target> | <assetPath>";
            }

            GameObject gameObject;
            var error = TryFindGameObject(sections[0], out gameObject);
            if (error != null)
            {
                return error;
            }

            var assetPath = NormalizeAssetPath(sections[1]);
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return "Prefab path must be an Assets/*.prefab path.";
            }

            EnsureAssetFolder(Path.GetDirectoryName(assetPath));
            PrefabUtility.SaveAsPrefabAssetAndConnect(gameObject, assetPath, InteractionMode.AutomatedAction);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return "Created prefab " + assetPath + ".";
        }

        /// <summary>
        /// Unpacks a prefab instance in an open scene, replacing the link with plain GameObjects. Used when
        /// deriving a new variant from an existing prefab, so the restructure is direct editing rather than
        /// a pile of instance overrides.
        /// </summary>
        /// <param name="args">Target, then an optional mode section: outermost (default) or completely.</param>
        /// <returns>A confirmation carrying the object, source prefab, mode and nested-instance count, or a refusal.</returns>
        private static string AssetsPrefabUnpackCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length == 0 || string.IsNullOrWhiteSpace(sections[0]))
            {
                return "Usage: assets-prefab-unpack <target> [| outermost|completely]";
            }

            // Unpacking targets a prefab INSTANCE in a scene. Inside a stage you are editing the asset
            // itself, where the operation has no meaning, so refuse rather than do something surprising.
            if (PrefabStageUtility.GetCurrentPrefabStage() != null)
            {
                return "Refusing to unpack while Prefab Mode is open: unpacking applies to a prefab instance in a scene, not to the contents of a stage. Close the stage with assets-prefab-close first.";
            }

            var mode = PrefabUnpackMode.OutermostRoot;
            if (sections.Length >= 2 && !string.IsNullOrWhiteSpace(sections[1]))
            {
                var requested = sections[1].Trim().ToLowerInvariant();
                if (requested == "completely")
                {
                    mode = PrefabUnpackMode.Completely;
                }
                else if (requested != "outermost")
                {
                    return "Unknown unpack mode: " + sections[1].Trim() + ". Use 'outermost' (default) or 'completely'.";
                }
            }

            GameObject gameObject;
            var error = TryFindGameObject(sections[0], out gameObject);
            if (error != null)
            {
                return error;
            }

            // Resolving to the instance root means passing a CHILD of an instance still unpacks the right
            // object instead of erroring, which is the common case when targeting by name.
            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(gameObject);
            if (root == null)
            {
                return "Not part of a prefab instance: " + gameObject.name + " (#" + gameObject.GetInstanceID() + "). Nothing to unpack.";
            }

            var resolvedFromChild = root != gameObject;
            var sourcePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(root);
            var rootLabel = root.name + " (#" + root.GetInstanceID() + ")";

            PrefabUtility.UnpackPrefabInstance(root, mode, InteractionMode.UserAction);

            // Report what actually survived: OutermostRoot leaves nested instances linked, Completely does not,
            // and the caller needs to know which they got rather than assume.
            var remaining = 0;
            foreach (var child in root.GetComponentsInChildren<Transform>(true))
            {
                if (PrefabUtility.IsAnyPrefabInstanceRoot(child.gameObject))
                {
                    remaining++;
                }
            }

            return "Unpacked " + rootLabel + ", was an instance of " + sourcePath + ", mode " + mode + "."
                + (resolvedFromChild ? " Resolved up from the child that was passed." : string.Empty)
                + " Nested prefab instances still linked inside it: " + remaining + ".";
        }

        /// <summary>
        /// Instantiates a prefab into the scene named by the required scene argument, with an optional parent
        /// that must be in that same scene and an optional world position.
        /// </summary>
        /// <param name="args">Scene, prefab path, then optional parent and position sections.</param>
        /// <returns>A JSON GameObject reference for the new instance, or a validation error that created nothing.</returns>
        private static string AssetsPrefabInstantiateCommand(string args)
        {
            var sections = SplitCommandSections(args);
            UnityEngine.SceneManagement.Scene scene;
            var sceneError = TryResolveRequiredScene(sections.Length > 0 ? sections[0] : string.Empty, out scene);
            if (sceneError != null)
            {
                return sceneError;
            }

            if (sections.Length < 2 || string.IsNullOrWhiteSpace(sections[1]))
            {
                return "Usage: assets-prefab-instantiate <scene> | <prefabPath> [| parent] [| x y z]";
            }

            var prefabPath = NormalizeAssetPath(sections[1]);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                return "Prefab not found: " + prefabPath;
            }

            GameObject parent = null;
            if (sections.Length > 2 && !IsRootTarget(sections[2]))
            {
                var parentError = TryFindGameObject(sections[2], out parent);
                if (parentError == null)
                {
                    parentError = CheckParentInScene(parent, scene);
                }

                if (parentError != null)
                {
                    return parentError;
                }
            }

            var instanceObject = PrefabUtility.InstantiatePrefab(prefab, scene) as GameObject;
            if (instanceObject == null)
            {
                return "Failed to instantiate prefab: " + prefabPath;
            }

            if (parent != null)
            {
                instanceObject.transform.SetParent(parent.transform, false);
            }

            Vector3 position;
            if (sections.Length > 3 && TryParseVector3(sections[3], out position))
            {
                instanceObject.transform.position = position;
            }

            RegisterCreatedUndo(instanceObject, "Instantiate Prefab");
            MarkGameObjectSceneDirty(instanceObject);
            return JsonGameObject(instanceObject);
        }

        /// <summary>
        /// Opens a prefab asset or the prefab source of a scene instance in prefab mode.
        /// </summary>
        /// <param name="args">A prefab asset path or scene GameObject target.</param>
        /// <returns>A confirmation line with the opened prefab path.</returns>
        private static string AssetsPrefabOpenCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return "Usage: assets-prefab-open <targetOrPath>";
            }

            var assetPath = NormalizeAssetPath(args);
            if (AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) == null)
            {
                GameObject gameObject;
                var error = TryFindGameObject(args, out gameObject);
                if (error != null)
                {
                    return error;
                }

                assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(gameObject);
            }

            if (string.IsNullOrEmpty(assetPath))
            {
                return "Target is not a prefab asset or prefab instance.";
            }

            AssetDatabase.OpenAsset(AssetDatabase.LoadAssetAtPath<GameObject>(assetPath));
            return "Opened prefab " + assetPath + ".";
        }

        /// <summary>
        /// Saves the current prefab editing stage.
        /// </summary>
        /// <returns>A confirmation line or a missing-stage error.</returns>
        private static string AssetsPrefabSaveCommand()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                return "No prefab stage is currently open.";
            }

            string error;
            if (!TrySavePrefabStageWithoutDialog(stage, out error))
            {
                return error;
            }

            return "Saved prefab " + stage.assetPath + " and cleared its Prefab Mode dirty state.";
        }

        /// <summary>
        /// Saves a prefab through the PUBLIC out-bool asset API, then clears the stage dirtiness explicitly
        /// so a later automated close does not prompt. The internal PrefabStage.SavePrefab is deliberately
        /// NOT used: on failure it raises a modal that blocks the editor thread, so the caller never
        /// receives the error and simply times out with a dialog left on screen for a human to dismiss.
        /// The public API fails by returning false and logging the reason, which is why it is reachable at
        /// all - see the console diff appended to the error below.
        /// </summary>
        /// <param name="stage">Active prefab editing stage to save.</param>
        /// <param name="error">Actionable failure report when Unity could not save the stage.</param>
        /// <returns>True only when Unity reports that the prefab save succeeded.</returns>
        private static bool TrySavePrefabStageWithoutDialog(PrefabStage stage, out string error)
        {
            error = null;
            if (stage == null || stage.prefabContentsRoot == null || string.IsNullOrEmpty(stage.assetPath))
            {
                error = "The active prefab stage is invalid and cannot be saved.";
                return false;
            }

            int consoleEntriesBeforeSave = ReadConsoleEntries().Count;

            try
            {
                bool success;
                PrefabUtility.SaveAsPrefabAsset(stage.prefabContentsRoot, stage.assetPath, out success);
                if (!success)
                {
                    error = "Unity refused to save prefab " + stage.assetPath + "; the prefab stage remains open." + DescribeConsoleErrorsSince(consoleEntriesBeforeSave);
                    return false;
                }

                stage.ClearDirtiness();
                AssetDatabase.SaveAssets();
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to save prefab " + stage.assetPath + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Discards prefab-stage changes through Unity's stage lifecycle so an explicit no-save close cannot
        /// raise a blocking confirmation dialog.
        /// </summary>
        /// <param name="stage">Active prefab editing stage whose changes should be discarded.</param>
        /// <param name="error">Actionable failure report when Unity could not discard the stage changes.</param>
        /// <returns>True when the stage is clean and safe to close without prompting.</returns>
        private static bool TryDiscardPrefabStageWithoutDialog(PrefabStage stage, out string error)
        {
            error = null;
            if (stage == null)
            {
                error = "No prefab stage is currently open.";
                return false;
            }

            try
            {
                MethodInfo discardChanges = typeof(PrefabStage).GetMethod(
                    "DiscardChanges",
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);

                if (discardChanges != null)
                {
                    discardChanges.Invoke(stage, null);
                }
                else
                {
                    stage.ClearDirtiness();
                }

                return true;
            }
            catch (TargetInvocationException ex)
            {
                Exception cause = ex.InnerException ?? ex;
                error = "Failed to discard prefab-stage changes: " + cause.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = "Failed to discard prefab-stage changes: " + ex.Message;
                return false;
            }
        }
    }
}
