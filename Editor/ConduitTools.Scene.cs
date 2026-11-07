/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Scene management commands that wrap UnityEditorSceneManager and expose compact scene JSON.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Scene Commands

        /// <summary>
        /// Creates and saves a new scene asset at the requested path.
        /// </summary>
        /// <param name="args">The scene asset path to create.</param>
        /// <returns>A JSON scene reference or validation error.</returns>
        private static string SceneCreateCommand(string args)
        {
            var assetPath = NormalizeAssetPath(args);
            if (string.IsNullOrEmpty(assetPath) || !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return "Usage: scene-create <Assets/path.unity>";
            }

            EnsureAssetFolder(Path.GetDirectoryName(assetPath));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!EditorSceneManager.SaveScene(scene, assetPath))
            {
                return "Failed to save new scene: " + assetPath;
            }

            return JsonScene(scene);
        }

        /// <summary>
        /// Returns opened scene metadata plus root GameObject references up to a requested limit. A scene that
        /// is listed but not loaded is refused, because it has no objects to read, and an ambiguous scene name
        /// is refused with each candidate listed.
        /// </summary>
        /// <param name="args">Optional scene selector and max depth token.</param>
        /// <returns>A compact JSON scene payload.</returns>
        private static string SceneGetDataCommand(string args)
        {
            var tokens = SplitCommandTokens(args);
            var scene = SceneManager.GetActiveScene();
            if (tokens.Length > 0)
            {
                string ambiguity;
                scene = FindSceneLoose(tokens[0], out ambiguity);
                if (ambiguity != null)
                {
                    return ambiguity;
                }
            }

            if (!scene.IsValid())
            {
                return "Scene not found: " + (tokens.Length > 0 ? tokens[0] : string.Empty);
            }

            if (!scene.isLoaded)
            {
                return "Scene " + scene.name + " is listed but not loaded, so it has no objects to read. scene-open it additive first.";
            }

            var maxDepth = tokens.Length > 1 ? ParsePositiveInt(tokens[1], 4) : 4;
            var builder = new StringBuilder();
            builder.Append("{\"scene\":").Append(JsonScene(scene)).Append(",\"roots\":[");
            var roots = scene.GetRootGameObjects();
            for (var i = 0; i < roots.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                AppendGameObjectTreeJson(builder, roots[i], 0, maxDepth);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        /// <summary>
        /// Lists currently opened scenes.
        /// </summary>
        /// <returns>A compact JSON array of opened scenes.</returns>
        private static string SceneListOpenedCommand()
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonScene(EditorSceneManager.GetSceneAt(i)));
            }

            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// Opens a scene in single, additive or unloaded mode. Unloaded adds the scene to the Hierarchy
        /// without loading its contents, the same as dragging it in with Unity's own unload state; a scene
        /// that is already listed is reported as such rather than opened a second time.
        /// </summary>
        /// <param name="args">Scene asset path plus an optional single, additive or unloaded token.</param>
        /// <returns>A JSON scene reference, or a line saying why nothing was opened.</returns>
        private static string SceneOpenCommand(string args)
        {
            var tokens = SplitCommandTokens(args);
            if (tokens.Length == 0)
            {
                return "Usage: scene-open <assetPath> [single|additive|unloaded]";
            }

            var modeToken = string.Empty;
            var assetTokenCount = tokens.Length;
            var lastToken = tokens[tokens.Length - 1];
            if (lastToken.Equals("single", StringComparison.OrdinalIgnoreCase) ||
                lastToken.Equals("additive", StringComparison.OrdinalIgnoreCase) ||
                lastToken.Equals("unloaded", StringComparison.OrdinalIgnoreCase))
            {
                modeToken = lastToken;
                assetTokenCount--;
            }

            var assetPath = NormalizeAssetPath(string.Join(" ", tokens, 0, assetTokenCount));
            if (AssetDatabase.LoadAssetAtPath<SceneAsset>(assetPath) == null)
            {
                return "Scene asset not found: " + assetPath;
            }

            if (modeToken.Equals("unloaded", StringComparison.OrdinalIgnoreCase))
            {
                var listed = SceneManager.GetSceneByPath(assetPath);
                if (listed.IsValid())
                {
                    return "Scene " + listed.name + " is already in the Hierarchy (" + (listed.isLoaded ? "loaded" : "unloaded") + "); nothing changed. scene-unload unloads it while keeping it listed.";
                }

                return JsonScene(EditorSceneManager.OpenScene(assetPath, OpenSceneMode.AdditiveWithoutLoading));
            }

            var mode = modeToken.Equals("additive", StringComparison.OrdinalIgnoreCase)
                ? OpenSceneMode.Additive
                : OpenSceneMode.Single;
            var scene = EditorSceneManager.OpenScene(assetPath, mode);
            return JsonScene(scene);
        }

        /// <summary>
        /// Saves one opened scene or all opened scenes when no selector is supplied.
        /// </summary>
        /// <param name="args">Optional scene selector.</param>
        /// <returns>A confirmation line.</returns>
        private static string SceneSaveCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                EditorSceneManager.SaveOpenScenes();
                return "Saved all open scenes.";
            }

            string ambiguity;
            var scene = FindSceneLoose(args, out ambiguity);
            if (ambiguity != null)
            {
                return ambiguity;
            }

            if (!scene.IsValid())
            {
                return "Scene not found: " + args;
            }

            if (!scene.isLoaded)
            {
                return "Cannot save " + scene.name + ": it is listed but not loaded. Nothing was saved.";
            }

            if (!EditorSceneManager.SaveScene(scene))
            {
                return "Failed to save scene: " + scene.path;
            }

            return "Saved scene " + scene.path + ".";
        }

        /// <summary>
        /// Sets the active scene by path, name, or opened-scene index. A scene that is already active is reported
        /// as such, and an unloaded scene is refused because Unity can only activate a loaded one.
        /// </summary>
        /// <param name="args">Scene selector text.</param>
        /// <returns>A confirmation line, a line saying why nothing changed, or a lookup error.</returns>
        private static string SceneSetActiveCommand(string args)
        {
            string ambiguity;
            var scene = FindSceneLoose(args, out ambiguity);
            if (ambiguity != null)
            {
                return ambiguity;
            }

            if (!scene.IsValid())
            {
                return "Scene not found: " + args;
            }

            if (scene == SceneManager.GetActiveScene())
            {
                return "Scene " + scene.name + " is already the active scene; nothing changed.";
            }

            if (!scene.isLoaded)
            {
                return "Cannot activate " + scene.name + ": it is not loaded. scene-open it additive first.";
            }

            return SceneManager.SetActiveScene(scene) ? "Set active scene to " + scene.name + "." : "Failed to set active scene " + scene.name + ".";
        }

        /// <summary>
        /// Unloads one scene by path, name, or opened-scene index. Keep, the default, matches Unity's own
        /// Unload Scene: the contents unload and the scene stays listed in the Hierarchy, greyed out. Remove
        /// closes it and takes it out of the Hierarchy as well. A scene with unsaved changes is only closed
        /// when the changes section says save or discard; without it the scene is left exactly as it was.
        /// </summary>
        /// <param name="args">Scene selector text, optionally followed by a keep or remove token, then an optional
        /// pipe section holding save or discard.</param>
        /// <returns>A line naming the scene and what happened to it, or why nothing changed.</returns>
        private static string SceneUnloadCommand(string args)
        {
            var sections = SplitCommandSections(args ?? string.Empty);
            var selector = sections.Length > 0 ? sections[0] : string.Empty;
            var changes = sections.Length > 1 ? sections[1].Trim() : string.Empty;
            var saveChanges = changes.Equals("save", StringComparison.OrdinalIgnoreCase);
            var discardChanges = changes.Equals("discard", StringComparison.OrdinalIgnoreCase);
            if (changes.Length > 0 && !saveChanges && !discardChanges)
            {
                return "Unknown changes value '" + changes + "': use save or discard. Nothing changed.";
            }

            var remove = false;
            var tokens = SplitCommandTokens(selector);
            if (tokens.Length > 1)
            {
                var lastToken = tokens[tokens.Length - 1];
                var isKeep = lastToken.Equals("keep", StringComparison.OrdinalIgnoreCase);
                var isRemove = lastToken.Equals("remove", StringComparison.OrdinalIgnoreCase);

                // A scene may itself be named "keep" or "remove"; the token is a mode only when the rest still resolves.
                if (isKeep || isRemove)
                {
                    string peelAmbiguity;
                    var peeled = FindSceneLoose(string.Join(" ", tokens, 0, tokens.Length - 1), out peelAmbiguity);
                    if (peelAmbiguity != null)
                    {
                        return peelAmbiguity;
                    }

                    if (peeled.IsValid())
                    {
                        remove = isRemove;
                        selector = string.Join(" ", tokens, 0, tokens.Length - 1);
                    }
                }
            }

            string selectorAmbiguity;
            var scene = FindSceneLoose(selector, out selectorAmbiguity);
            if (selectorAmbiguity != null)
            {
                return selectorAmbiguity;
            }

            if (!scene.IsValid())
            {
                return "Scene not found: " + selector;
            }

            // The Scene handle is invalid once the scene closes, so its name is read now.
            var sceneName = scene.name;

            if (!remove && !scene.isLoaded)
            {
                return "Scene " + sceneName + " is already unloaded; nothing changed. Use mode remove to take it out of the Hierarchy.";
            }

            if (scene.isLoaded && SceneManager.loadedSceneCount <= 1)
            {
                return "Cannot unload " + sceneName + ": it is the only loaded scene.";
            }

            var changesNote = string.Empty;
            if (scene.isDirty)
            {
                if (!saveChanges && !discardChanges)
                {
                    return "Scene " + sceneName + " has unsaved changes; nothing changed. Send the changes section as save or discard.";
                }

                if (saveChanges)
                {
                    if (!EditorSceneManager.SaveScene(scene))
                    {
                        return "Failed to save scene " + sceneName + "; it was left open and unchanged.";
                    }

                    changesNote = " Its changes were saved first.";
                }
                else
                {
                    changesNote = " Its unsaved changes were discarded.";
                }
            }
            else if (saveChanges || discardChanges)
            {
                changesNote = " It had no unsaved changes, so " + changes.ToLowerInvariant() + " was not needed.";
            }

            if (!EditorSceneManager.CloseScene(scene, remove))
            {
                return "Failed to " + (remove ? "remove" : "unload") + " scene " + sceneName + ".";
            }

            return (remove
                ? "Removed scene " + sceneName + " from the Hierarchy."
                : "Unloaded scene " + sceneName + "; it stays listed in the Hierarchy.") + changesNote;
        }

        #endregion

        #region Scene Helpers

        /// <summary>
        /// Finds an opened scene by index, exact path, or exact name. A path is unique, so it wins over any
        /// name. A name shared by several opened scenes resolves to none of them and sets the ambiguity text,
        /// listing each candidate, rather than returning whichever scene happens to come first. A blank
        /// selector matches nothing: no caller gets the active scene implicitly.
        /// </summary>
        /// <param name="selector">The scene selector supplied by a command.</param>
        /// <param name="ambiguity">Null normally; the refusal text when a name matches more than one opened scene.</param>
        /// <returns>The matching scene, or an invalid scene when nothing matches or the name is ambiguous.</returns>
        private static Scene FindSceneLoose(string selector, out string ambiguity)
        {
            ambiguity = null;
            if (string.IsNullOrWhiteSpace(selector))
            {
                return default(Scene);
            }

            selector = NormalizeAssetPath(selector);
            int index;
            if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) &&
                index >= 0 &&
                index < EditorSceneManager.sceneCount)
            {
                return EditorSceneManager.GetSceneAt(index);
            }

            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (string.Equals(scene.path, selector, StringComparison.OrdinalIgnoreCase))
                {
                    return scene;
                }
            }

            var named = new System.Collections.Generic.List<int>();
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (string.Equals(EditorSceneManager.GetSceneAt(i).name, selector, StringComparison.OrdinalIgnoreCase))
                {
                    named.Add(i);
                }
            }

            if (named.Count == 1)
            {
                return EditorSceneManager.GetSceneAt(named[0]);
            }

            if (named.Count > 1)
            {
                var builder = new StringBuilder();
                builder.AppendLine("Ambiguous scene name '" + selector + "': " + named.Count + " opened scenes share it. Use the scene path or the opened-scene index instead.");
                foreach (var i in named)
                {
                    var candidate = EditorSceneManager.GetSceneAt(i);
                    builder.AppendLine("- [" + i + "] " + candidate.name + " (" + (string.IsNullOrEmpty(candidate.path) ? "unsaved" : candidate.path) + ")");
                }

                ambiguity = builder.ToString().TrimEnd();
            }

            return default(Scene);
        }

        /// <summary>
        /// Resolves the required scene argument of a verb that acts on a scene without an object target. There
        /// is no implicit active scene: a blank selector, an unknown scene, an ambiguous name and a scene that
        /// is listed but not loaded are each refused with a specific message.
        /// </summary>
        /// <param name="selector">Scene name, path or opened-scene index.</param>
        /// <param name="scene">The resolved loaded scene when the call succeeds.</param>
        /// <returns>Null on success, or the refusal text.</returns>
        private static string TryResolveRequiredScene(string selector, out Scene scene)
        {
            scene = default(Scene);
            if (string.IsNullOrWhiteSpace(selector))
            {
                return "Missing scene argument. Name a loaded scene by name, path or opened-scene index; scene-list-opened lists them.";
            }

            string ambiguity;
            var found = FindSceneLoose(selector, out ambiguity);
            if (ambiguity != null)
            {
                return ambiguity;
            }

            if (!found.IsValid())
            {
                return "Scene not found: " + selector.Trim().Trim('"') + ". scene-list-opened lists the opened scenes.";
            }

            if (!found.isLoaded)
            {
                return "Scene " + found.name + " is listed but not loaded, so it has nothing to act on. scene-open it additive first.";
            }

            scene = found;
            return null;
        }

        /// <summary>Refuses a parent that lives in a different scene from the one an object is being created in.</summary>
        /// <param name="parent">The resolved parent GameObject.</param>
        /// <param name="scene">The scene the new object must land in.</param>
        /// <returns>Null when the parent is in that scene, otherwise the refusal text.</returns>
        private static string CheckParentInScene(UnityEngine.GameObject parent, Scene scene)
        {
            if (parent.scene == scene)
            {
                return null;
            }

            return "Parent " + GetGameObjectScenePath(parent) + " [" + parent.GetInstanceID() + "] is in scene " + parent.scene.name +
                ", not " + scene.name + ". A parent must be in the scene the object is created in. Nothing was created.";
        }

        /// <summary>Moves a newly created root GameObject into the named scene when it is not already there.</summary>
        /// <param name="gameObject">The new root GameObject.</param>
        /// <param name="scene">The loaded scene it must land in.</param>
        private static void PlaceRootInScene(UnityEngine.GameObject gameObject, Scene scene)
        {
            if (gameObject.scene != scene)
            {
                SceneManager.MoveGameObjectToScene(gameObject, scene);
            }
        }

        /// <summary>
        /// Appends a hierarchy object and limited descendants to an existing JSON builder.
        /// </summary>
        /// <param name="builder">The JSON builder receiving the object.</param>
        /// <param name="gameObject">The GameObject to append.</param>
        /// <param name="depth">The current traversal depth.</param>
        /// <param name="maxDepth">The maximum traversal depth to include.</param>
        private static void AppendGameObjectTreeJson(StringBuilder builder, UnityEngine.GameObject gameObject, int depth, int maxDepth)
        {
            builder.Append("{\"object\":").Append(JsonGameObject(gameObject)).Append(",\"children\":[");
            if (depth < maxDepth)
            {
                for (var i = 0; i < gameObject.transform.childCount; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    AppendGameObjectTreeJson(builder, gameObject.transform.GetChild(i).gameObject, depth + 1, maxDepth);
                }
            }

            builder.Append("]}");
        }

        /// <summary>
        /// Appends a hierarchy object and its descendants as indented name-only rows.
        /// </summary>
        /// <remarks>
        /// This is the shape to reach for when the question is "what is nested where" rather than "what are
        /// this object's values". The verbose trees carry a path, a transform triplet and a component list on
        /// EVERY row, which buries the structure they were called to show and can overrun a response on a
        /// scene of any size. Inactive objects are marked because a hierarchy of disabled menus otherwise
        /// reads as though everything is live.
        /// </remarks>
        /// <param name="builder">The report builder receiving the rows.</param>
        /// <param name="gameObject">The GameObject to append.</param>
        /// <param name="depth">The current traversal depth.</param>
        /// <param name="maxDepth">The maximum traversal depth to include.</param>
        private static void AppendGameObjectNameTree(StringBuilder builder, UnityEngine.GameObject gameObject, int depth, int maxDepth)
        {
            builder.Append(' ', depth * 2).Append(gameObject.name);
            if (!gameObject.activeSelf)
            {
                builder.Append("  [off]");
            }

            builder.AppendLine();

            var childCount = gameObject.transform.childCount;
            if (depth >= maxDepth)
            {
                // Say what was withheld rather than ending on a bare ellipsis the caller cannot size.
                if (childCount > 0)
                {
                    builder.Append(' ', (depth + 1) * 2).Append("... ").Append(childCount).AppendLine(" more, raise maxDepth");
                }

                return;
            }

            for (var i = 0; i < childCount; i++)
            {
                AppendGameObjectNameTree(builder, gameObject.transform.GetChild(i).gameObject, depth + 1, maxDepth);
            }
        }

        /// <summary>
        /// Formats one scene's whole hierarchy as an indented name-only tree.
        /// </summary>
        /// <param name="scene">The loaded scene to print.</param>
        /// <param name="maxDepth">Maximum child depth to include before collapsing with a count.</param>
        /// <returns>A text tree of object names and nesting.</returns>
        private static string FormatSceneNameTree(Scene scene, int maxDepth)
        {
            var roots = scene.GetRootGameObjects();
            var builder = new StringBuilder();
            builder.Append("Scene: ").Append(scene.name)
                .Append("  roots: ").Append(roots.Length)
                .Append("  maxDepth: ").Append(maxDepth)
                .AppendLine();
            builder.AppendLine();

            foreach (var root in roots)
            {
                AppendGameObjectNameTree(builder, root, 0, maxDepth);
            }

            return builder.ToString().TrimEnd();
        }

        #endregion
            /// <summary>
        /// Routes the Scene verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchScene(string verb, string command, string[] parts)
        {
            if (verb == "scene-create")
            {
                return SceneCreateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-get-data")
            {
                return SceneGetDataCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-list-opened")
            {
                return SceneListOpenedCommand();
            }

            if (verb == "scene-open")
            {
                return SceneOpenCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-save")
            {
                return SceneSaveCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-set-active")
            {
                return SceneSetActiveCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-unload")
            {
                return SceneUnloadCommand(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
