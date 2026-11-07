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
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace ItsRenji.Conduit
{
    /// <summary>
    /// Scene object verbs: hierarchy reads, creation, renaming, reparenting and transforms.
    /// </summary>
    internal static partial class ConduitBridge
    {
        /// <summary>Formats one scene's root hierarchy as an indented text tree.</summary>
        /// <param name="scene">The loaded scene to print.</param>
        /// <param name="maxDepth">Maximum child depth to include before truncating with an ellipsis.</param>
        /// <returns>Text report containing scene metadata and visible hierarchy rows.</returns>
        private static string FormatSceneTree(Scene scene, int maxDepth)
        {
            var roots = scene.GetRootGameObjects();
            var builder = new StringBuilder();
            builder.AppendLine("Scene: " + scene.name);
            builder.AppendLine("Path: " + scene.path);
            builder.AppendLine("Roots: " + roots.Length);
            builder.AppendLine("MaxDepth: " + maxDepth);
            builder.AppendLine();

            foreach (var root in roots)
            {
                AppendGameObjectTree(builder, root, 0, maxDepth);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>Appends one GameObject and its children to a scene-tree report.</summary>
        /// <param name="builder">Report builder receiving the formatted hierarchy rows.</param>
        /// <param name="gameObject">GameObject to append.</param>
        /// <param name="depth">Current hierarchy depth.</param>
        /// <param name="maxDepth">Maximum child depth to include.</param>
        private static void AppendGameObjectTree(StringBuilder builder, GameObject gameObject, int depth, int maxDepth)
        {
            if (depth > maxDepth)
            {
                return;
            }

            builder.Append(new string(' ', depth * 2));
            builder.Append("- [").Append(gameObject.GetInstanceID()).Append("] ");
            builder.Append(GetGameObjectPath(gameObject));
            builder.Append(" active=").Append(gameObject.activeSelf);
            builder.Append(" pos=").Append(FormatVector3(gameObject.transform.localPosition));
            builder.Append(" rot=").Append(FormatVector3(gameObject.transform.localEulerAngles));
            builder.Append(" scale=").Append(FormatVector3(gameObject.transform.localScale));
            builder.Append(" comps=");
            var components = gameObject.GetComponents<Component>();
            for (var i = 0; i < components.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(",");
                }

                builder.Append(components[i] == null ? "<missing>" : components[i].GetType().Name);
            }

            builder.AppendLine();

            if (depth == maxDepth && gameObject.transform.childCount > 0)
            {
                builder.Append(new string(' ', (depth + 1) * 2));
                builder.AppendLine("...");
                return;
            }

            for (var i = 0; i < gameObject.transform.childCount; i++)
            {
                AppendGameObjectTree(builder, gameObject.transform.GetChild(i).gameObject, depth + 1, maxDepth);
            }
        }

        /// <summary>Builds a text report for one resolved GameObject target, including the scene it lives in.</summary>
        /// <param name="target">Path, name, scene-qualified path or name, or instance id to resolve.</param>
        /// <returns>Formatted GameObject details or a lookup error.</returns>
        private static string FindObjectReport(string target)
        {
            GameObject gameObject;
            var error = TryFindGameObject(target, out gameObject);
            if (error != null)
            {
                return error;
            }

            var builder = new StringBuilder();
            builder.AppendLine("id: " + gameObject.GetInstanceID());
            builder.AppendLine("name: " + gameObject.name);
            builder.AppendLine("path: " + GetGameObjectPath(gameObject));
            builder.AppendLine("scene: " + gameObject.scene.name);
            builder.AppendLine("activeSelf: " + gameObject.activeSelf);
            builder.AppendLine("activeInHierarchy: " + gameObject.activeInHierarchy);
            builder.AppendLine("localPosition: " + FormatVector3(gameObject.transform.localPosition));
            builder.AppendLine("localEulerAngles: " + FormatVector3(gameObject.transform.localEulerAngles));
            builder.AppendLine("localScale: " + FormatVector3(gameObject.transform.localScale));
            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Creates an empty GameObject in the scene named by the required scene argument. A root object lands
        /// in that scene; a parent must itself be in that scene, or nothing is created. The object's own scene
        /// is marked dirty.
        /// </summary>
        /// <param name="args">Pipe-separated scene, object name and optional parent target.</param>
        /// <returns>A confirmation line naming the scene, or a validation error.</returns>
        private static string CreateEmptyObject(string args)
        {
            var pieces = SplitPipeArgs(args);
            Scene scene;
            var sceneError = TryResolveRequiredScene(pieces.Length > 0 ? pieces[0] : string.Empty, out scene);
            if (sceneError != null)
            {
                return sceneError;
            }

            var name = pieces.Length > 1 ? pieces[1] : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                return "Missing object name. Usage: create-empty <scene> | <name> [| parent]";
            }

            Transform parent = null;
            if (pieces.Length > 2 && !IsRootTarget(pieces[2]))
            {
                GameObject parentObject;
                var error = TryFindGameObject(pieces[2], out parentObject);
                if (error != null)
                {
                    return error;
                }

                var scopeError = CheckParentInScene(parentObject, scene);
                if (scopeError != null)
                {
                    return scopeError;
                }

                parent = parentObject.transform;
            }

            var gameObject = new GameObject(name.Trim().Trim('"'));
            if (parent == null)
            {
                PlaceRootInScene(gameObject, scene);
            }

            RegisterCreatedUndo(gameObject, "Conduit Create Empty");
            if (parent != null)
            {
                SetTransformParentUndo(gameObject.transform, parent, "Conduit Parent Empty");
            }

            Selection.activeGameObject = gameObject;
            MarkGameObjectSceneDirty(gameObject);
            return "Created GameObject: " + GetGameObjectScenePath(gameObject) + " [" + gameObject.GetInstanceID() + "]";
        }

        /// <summary>
        /// Renames one or more resolved GameObjects through Unity Undo. Arguments are target and new-name
        /// PAIRS, and every target is resolved before any is renamed so one bad entry cannot leave a set
        /// half-renamed with no record of how far it got.
        /// </summary>
        /// <param name="args">Pipe-separated target and new-name pairs.</param>
        /// <returns>Each rename as old -> new with its instance id, or an error that changed nothing.</returns>
        private static string RenameObject(string args)
        {
            var pieces = SplitPipeArgs(args);
            if (pieces.Length < 2)
            {
                return "Usage: rename-object <target> | <newName> [| <target> | <newName> ...]";
            }

            if (pieces.Length % 2 != 0)
            {
                return "Expected target and new-name PAIRS, got " + pieces.Length +
                    " section(s). Nothing was renamed.";
            }

            var pairCount = pieces.Length / 2;
            if (pairCount > RENAME_MULTI_LIMIT)
            {
                return "Too many renames: " + pairCount + ". The limit is " + RENAME_MULTI_LIMIT +
                    ". Nothing was renamed.";
            }

            // Resolve and validate the whole list first. The inputs here are typically duplicates that still
            // share a stem, so a half-applied run is especially hard to read back afterwards.
            var targets = new List<GameObject>();
            var names = new List<string>();
            for (var i = 0; i < pairCount; i++)
            {
                var targetSection = pieces[i * 2];
                var newName = pieces[(i * 2) + 1].Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(newName))
                {
                    return "Pair " + (i + 1) + " of " + pairCount + ": new name is empty. Nothing was renamed.";
                }

                GameObject resolved;
                var error = TryFindGameObject(targetSection, out resolved);
                if (error != null)
                {
                    return "Pair " + (i + 1) + " of " + pairCount + ": " + error + " Nothing was renamed.";
                }

                if (targets.Contains(resolved))
                {
                    return "Pair " + (i + 1) + " of " + pairCount + ": " + GetGameObjectPath(resolved) +
                        " [" + resolved.GetInstanceID() + "] is named twice in one call. Nothing was renamed.";
                }

                targets.Add(resolved);
                names.Add(newName);
            }

            // Old paths are read before the first write, because renaming an ancestor changes the path of
            // everything below it and a later pair would otherwise report a path that never existed.
            var evidence = new List<string>();
            for (var i = 0; i < targets.Count; i++)
            {
                evidence.Add(GetGameObjectPath(targets[i]));
            }

            for (var i = 0; i < targets.Count; i++)
            {
                RecordUndo(targets[i], "Conduit Rename GameObject");
                targets[i].name = names[i];
                MarkGameObjectSceneDirty(targets[i]);
                evidence[i] = evidence[i] + " -> " + GetGameObjectPath(targets[i]) +
                    " [" + targets[i].GetInstanceID() + "]";
            }

            return targets.Count == 1
                ? "Renamed: " + evidence[0]
                : "Renamed " + targets.Count + " GameObject(s): " + string.Join("; ", evidence.ToArray());
        }

        /// <summary>Most renames one rename-object call will carry, matching gameobject-duplicate-multi.</summary>
        private const int RENAME_MULTI_LIMIT = 512;

        /// <summary>Moves a GameObject under another transform or back to the scene root.</summary>
        /// <param name="args">Pipe-separated target and parent/root specifier.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string ReparentObject(string args)
        {
            var pieces = SplitPipeArgs(args);
            if (pieces.Length < 2 || string.IsNullOrWhiteSpace(pieces[0]))
            {
                return "Usage: reparent-object <target> | <parent-or-root>";
            }

            GameObject child;
            var error = TryFindGameObject(pieces[0], out child);
            if (error != null)
            {
                return error;
            }

            Transform parent = null;
            if (!IsRootTarget(pieces[1]))
            {
                GameObject parentObject;
                error = TryFindGameObject(pieces[1], out parentObject);
                if (error != null)
                {
                    return error;
                }

                if (parentObject == child || parentObject.transform.IsChildOf(child.transform))
                {
                    return "Cannot parent an object under itself or one of its children.";
                }

                parent = parentObject.transform;
            }

            var oldPath = GetGameObjectPath(child);
            SetTransformParentUndo(child.transform, parent, "Conduit Reparent GameObject");
            MarkGameObjectSceneDirty(child);
            return "Reparented: " + oldPath + " -> " + GetGameObjectPath(child);
        }

        /// <summary>Sets a GameObject transform's sibling index within its current parent.</summary>
        /// <param name="args">Pipe-separated target and non-negative sibling index.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string SetSiblingIndex(string args)
        {
            var pieces = SplitPipeArgs(args);
            if (pieces.Length < 2)
            {
                return "Usage: set-sibling-index <target> | <index>";
            }

            GameObject gameObject;
            var error = TryFindGameObject(pieces[0], out gameObject);
            if (error != null)
            {
                return error;
            }

            int index;
            if (!int.TryParse(pieces[1], out index) || index < 0)
            {
                return "Sibling index must be a non-negative integer.";
            }

            var before = gameObject.transform.GetSiblingIndex();
            RecordUndo(gameObject.transform, "Conduit Set Sibling Index");
            gameObject.transform.SetSiblingIndex(index);
            var after = gameObject.transform.GetSiblingIndex();
            var path = GetGameObjectPath(gameObject);
            if (after == before)
            {
                return "Sibling index of " + path + " is already " + after +
                    (after == index ? "." : "; asked for " + index + ", past the last sibling.") + " Nothing changed.";
            }

            MarkGameObjectSceneDirty(gameObject);
            return "Set sibling index: " + path + " " + before + " -> " + after +
                (after == index ? "." : " (asked for " + index + ", past the last sibling, so it went last).");
        }

        /// <summary>
        /// Sets a local transform position, rotation, or scale vector on a resolved GameObject and reports the local
        /// value as it was and as it reads back. Position and rotation also work in play mode, where they move the
        /// live object and are discarded when play ends, so the scene is only marked dirty in edit mode; scale is
        /// refused in play mode before anything is written.
        /// </summary>
        /// <param name="args">Pipe-separated target and x/y/z vector.</param>
        /// <param name="kind">Transform vector to update.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string SetTransformVector(string args, TransformVectorKind kind)
        {
            var pieces = SplitPipeArgs(args);
            if (pieces.Length < 2)
            {
                return "Usage: set-" + kind.ToString().ToLowerInvariant() + " <target> | <x> <y> <z>";
            }

            GameObject gameObject;
            var error = TryFindGameObject(pieces[0], out gameObject);
            if (error != null)
            {
                return error;
            }

            Vector3 value;
            if (!TryParseVector3(pieces[1], out value))
            {
                return "Vector must be three numbers: <x> <y> <z>";
            }

            var isPlaying = EditorApplication.isPlaying;
            if (isPlaying && kind == TransformVectorKind.Scale)
            {
                return "set-scale is refused in play mode; only set-position and set-rotation move a live object. Nothing was changed.";
            }

            var transform = gameObject.transform;
            var before = ReadLocalTransformVector(transform, kind);
            RecordUndo(transform, "Conduit Set " + kind);
            if (kind == TransformVectorKind.Position)
            {
                transform.localPosition = value;
            }
            else if (kind == TransformVectorKind.Rotation)
            {
                transform.localEulerAngles = value;
            }
            else
            {
                transform.localScale = value;
            }

            var beforeText = FormatExactVector3(before);
            var afterText = FormatExactVector3(ReadLocalTransformVector(transform, kind));
            var path = GetGameObjectPath(gameObject);
            var playNote = isPlaying ? " (play mode: discarded when play ends)" : string.Empty;
            if (beforeText == afterText)
            {
                return "No change to " + kind + " of " + path + ": local already " + afterText + "." + playNote;
            }

            if (!isPlaying)
            {
                EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }

            return "Updated " + kind + ": " + path + " local " + beforeText + " -> " + afterText +
                (kind == TransformVectorKind.Position ? ", world now " + FormatExactVector3(transform.position) : string.Empty) +
                playNote;
        }

        /// <summary>Reads the local transform vector one of the transform verbs writes.</summary>
        /// <param name="transform">Transform to read.</param>
        /// <param name="kind">Which vector.</param>
        /// <returns>localPosition, localEulerAngles or localScale.</returns>
        private static Vector3 ReadLocalTransformVector(Transform transform, TransformVectorKind kind)
        {
            if (kind == TransformVectorKind.Position) return transform.localPosition;
            if (kind == TransformVectorKind.Rotation) return transform.localEulerAngles;
            return transform.localScale;
        }

        /// <summary>Formats Unity project, active scene, build target, and editor status information.</summary>
        /// <returns>Text report for the current project and editor state.</returns>
        private static string FormatProjectInfo()
        {
            var activeScene = SceneManager.GetActiveScene();
            var builder = new StringBuilder();
            builder.AppendLine("unityVersion: " + Application.unityVersion);
            builder.AppendLine("projectKind: " + ProjectKindLabel());
            builder.AppendLine("isVrchatProject: " + IsVrchatProject);
            builder.AppendLine("isWorldsProject: " + IsWorldsProject);
            builder.AppendLine("projectPath: " + Directory.GetCurrentDirectory());
            builder.AppendLine("dataPath: " + Application.dataPath);
            builder.AppendLine("activeSceneName: " + activeScene.name);
            builder.AppendLine("activeScenePath: " + activeScene.path);
            builder.AppendLine("activeSceneDirty: " + activeScene.isDirty);
            builder.AppendLine("buildTarget: " + EditorUserBuildSettings.activeBuildTarget);
            builder.AppendLine("buildTargetGroup: " + EditorUserBuildSettings.selectedBuildTargetGroup);
            builder.AppendLine("applicationPlatform: " + Application.platform);
            builder.AppendLine(FormatEditorStatus());
            return builder.ToString().TrimEnd();
        }

        /// <summary>Saves dirty assets through AssetDatabase and returns the editor state.</summary>
        /// <returns>A confirmation line and editor status.</returns>
        private static string SaveAssets()
        {
            AssetDatabase.SaveAssets();
            return "Saved assets." + Environment.NewLine + FormatEditorStatus();
        }

        /// <summary>
        /// Saves the one scene named by the required scene argument and returns Unity's save result. No other
        /// open scene is touched, and a scene that has never been saved is refused because saving it would
        /// need a Save As dialog.
        /// </summary>
        /// <param name="selector">Scene name, path or opened-scene index.</param>
        /// <returns>The save result and editor status, or a refusal that saved nothing.</returns>
        private static string SaveScenes(string selector)
        {
            Scene scene;
            var sceneError = TryResolveRequiredScene(selector, out scene);
            if (sceneError != null)
            {
                return sceneError;
            }

            var path = scene.path;
            if (string.IsNullOrEmpty(path))
            {
                return "Scene " + scene.name + " has never been saved, so it has no asset path and saving it would need a Save As dialog. Nothing was saved.";
            }

            var saved = EditorSceneManager.SaveScene(scene);
            return (saved ? "Saved scene " : "Failed to save scene ") + path + ". SaveScene returned: " + saved + Environment.NewLine + FormatEditorStatus();
        }
            /// <summary>
        /// Routes the SceneObjects verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchSceneObjects(string verb, string command, string[] parts)
        {
            if (verb == "project-info")
            {
                return FormatProjectInfo();
            }

            if (verb == "save-assets")
            {
                return SaveAssets();
            }

            if (verb == "save-scenes")
            {
                return SaveScenes(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-tree" || verb == "hierarchy")
            {
                Scene sceneTreeScene;
                var sceneTreeError = TryResolveRequiredScene(parts.Length > 1 ? parts[1] : string.Empty, out sceneTreeScene);
                if (sceneTreeError != null)
                {
                    return sceneTreeError;
                }

                var sceneTreeNamesOnly = false;
                var sceneTreeDepth = 12;
                for (var i = 2; i < parts.Length; i++)
                {
                    if (IsNameTreeToken(parts[i]))
                    {
                        sceneTreeNamesOnly = true;
                        continue;
                    }

                    sceneTreeDepth = ParsePositiveInt(parts[i], sceneTreeDepth);
                }

                return sceneTreeNamesOnly ? FormatSceneNameTree(sceneTreeScene, sceneTreeDepth) : FormatSceneTree(sceneTreeScene, sceneTreeDepth);
            }

            if (verb == "find-object")
            {
                return FindObjectReport(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "create-empty")
            {
                return CreateEmptyObject(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "rename-object" || verb == "rename-go")
            {
                return RenameObject(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "reparent-object" || verb == "move-object")
            {
                return ReparentObject(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "set-sibling-index")
            {
                return SetSiblingIndex(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "set-position")
            {
                return SetTransformVector(GetCommandRemainder(command, parts[0]), TransformVectorKind.Position);
            }

            if (verb == "set-rotation")
            {
                return SetTransformVector(GetCommandRemainder(command, parts[0]), TransformVectorKind.Rotation);
            }

            if (verb == "set-scale")
            {
                return SetTransformVector(GetCommandRemainder(command, parts[0]), TransformVectorKind.Scale);
            }


            return null;
        }
    }
}
