/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// GameObject, Component, and generic Unity object commands that mirror Unity-MCP's 2022 editor API surface.
    /// </summary>
    internal static partial class ConduitBridge
    {



        /// <summary>
        /// Creates an empty or primitive GameObject in the scene named by the required scene argument, with an
        /// optional parent that must be in that same scene. A root object is moved into that scene.
        /// </summary>
        /// <param name="args">Scene, name, then optional primitive type and parent sections.</param>
        /// <returns>A JSON GameObject reference for the new object, or a validation error that created nothing.</returns>
        private static string GameObjectCreateCommand(string args)
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
                return "Usage: gameobject-create <scene> | <name> [| primitive] [| parent]";
            }

            var name = sections[1].Trim().Trim('"');
            GameObject gameObject;
            PrimitiveType primitiveType;
            if (sections.Length > 2 && Enum.TryParse(sections[2].Trim(), true, out primitiveType))
            {
                gameObject = GameObject.CreatePrimitive(primitiveType);
                gameObject.name = name;
            }
            else
            {
                gameObject = new GameObject(name);
            }

            if (sections.Length > 3 && !IsRootTarget(sections[3]))
            {
                GameObject parent;
                var parentError = TryFindGameObject(sections[3], out parent);
                if (parentError == null)
                {
                    parentError = CheckParentInScene(parent, scene);
                }

                if (parentError != null)
                {
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    return parentError;
                }

                // Parent WITHOUT preserving the world pose, so no compensating local scale, rotation or
                // position is ever written - a child of a 0.001 world-space canvas would otherwise come back
                // at local scale 1000 and have to be repaired by hand.
                gameObject.transform.SetParent(parent.transform, false);

                // Inherit the parent's layer. A new object defaults to layer 0, and a UI object left on 0
                // under a canvas on the UI layer renders and raycasts wrongly for reasons that look nothing
                // like a layer problem when you hit them.
                gameObject.layer = parent.layer;
            }
            else
            {
                PlaceRootInScene(gameObject, scene);
            }

            RegisterCreatedUndo(gameObject, "Create GameObject");
            MarkGameObjectSceneDirty(gameObject);
            return JsonGameObject(gameObject);
        }

        /// <summary>
        /// Destroys one or more GameObjects through Undo so the editor can recover them. Every target is
        /// resolved before any is destroyed, so one bad entry cannot leave a set half-deleted.
        /// </summary>
        /// <param name="args">Pipe-separated GameObject paths, names, or instance IDs.</param>
        /// <returns>A confirmation naming each object destroyed, or a lookup error that changed nothing.</returns>
        private static string GameObjectDestroyCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length == 0 || string.IsNullOrWhiteSpace(sections[0]))
            {
                return "Usage: gameobject-destroy <target> [| <target> ...]";
            }

            if (sections.Length > DUPLICATE_MULTI_LIMIT)
            {
                return "Too many targets: " + sections.Length + ". The limit is " + DUPLICATE_MULTI_LIMIT +
                    ". Nothing was destroyed.";
            }

            // Resolve the whole list first. Destroying as we go would leave a partially deleted set with no
            // record of how far it got, which is worse than refusing outright.
            var targets = new List<GameObject>();
            for (var i = 0; i < sections.Length; i++)
            {
                GameObject resolved;
                var error = TryFindGameObject(sections[i], out resolved);
                if (error != null)
                {
                    return "Target " + (i + 1) + " of " + sections.Length + ": " + error +
                        " Nothing was destroyed.";
                }

                if (!targets.Contains(resolved))
                {
                    targets.Add(resolved);
                }
            }

            // Paths and ids are read BEFORE the destroy, because a destroyed object cannot report either.
            var evidence = new List<string>();
            foreach (GameObject target in targets)
            {
                evidence.Add(GetGameObjectPath(target) + " (#" + target.GetInstanceID() + ")");
            }

            foreach (GameObject target in targets)
            {
                DestroyWithUndo(target, "Destroy GameObject");
            }

            return "Destroyed " + targets.Count + " GameObject(s): " + string.Join("; ", evidence.ToArray()) + ".";
        }

        /// <summary>
        /// Duplicates a GameObject using Unity's editor duplicate command path.
        /// </summary>
        /// <param name="args">The GameObject path, name, or instance ID.</param>
        /// <returns>A JSON array of the duplicated selection.</returns>
        private static string GameObjectDuplicateCommand(string args)
        {
            GameObject gameObject;
            var error = TryFindGameObject(args, out gameObject);
            if (error != null)
            {
                return error;
            }

            Selection.instanceIDs = new[] { gameObject.GetInstanceID() };
            Unsupported.DuplicateGameObjectsUsingPasteboard();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            var builder = new StringBuilder();
            builder.Append('[');
            for (var i = 0; i < Selection.gameObjects.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonGameObject(Selection.gameObjects[i]));
                MarkGameObjectSceneDirty(Selection.gameObjects[i]);
            }

            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// Most copies one call will make. The cap exists so a mistyped amount cannot fill a scene with
        /// thousands of objects faster than anyone can notice and undo it.
        /// </summary>
        private const int DUPLICATE_MULTI_LIMIT = 512;

        /// <summary>
        /// Duplicates a GameObject N times through the same editor path as a manual Ctrl+D, so prefab links,
        /// components, overrides and references survive exactly as they do for the single duplicate.
        /// </summary>
        /// <remarks>
        /// Every copy is taken from the ORIGINAL rather than from the previous copy, so nothing compounds
        /// down the run and all N are identical siblings. The response carries every new object WITH its
        /// instance id, because the reason to make fifty of something is almost always to wire all fifty
        /// into an array straight afterwards, and a second lookup pass to recover them is wasted work.
        /// </remarks>
        /// <param name="args">Target section plus the number of copies to make.</param>
        /// <returns>A JSON array of the created GameObjects, or an error.</returns>
        private static string GameObjectDuplicateMultiCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2 || string.IsNullOrWhiteSpace(sections[0]) || string.IsNullOrWhiteSpace(sections[1]))
            {
                return "Usage: gameobject-duplicate-multi <target> | <amount>";
            }

            int amount;
            if (!int.TryParse(sections[1].Trim(), out amount))
            {
                return "Amount must be a whole number, got '" + sections[1].Trim() + "'. Nothing was created.";
            }

            if (amount < 1 || amount > DUPLICATE_MULTI_LIMIT)
            {
                return "Amount must be between 1 and " + DUPLICATE_MULTI_LIMIT + ", got " + amount +
                    ". Nothing was created.";
            }

            GameObject gameObject;
            var error = TryFindGameObject(sections[0], out gameObject);
            if (error != null)
            {
                return error;
            }

            var originalId = gameObject.GetInstanceID();
            var created = new List<GameObject>();
            for (var i = 0; i < amount; i++)
            {
                Selection.instanceIDs = new[] { originalId };
                Unsupported.DuplicateGameObjectsUsingPasteboard();
                foreach (var duplicate in Selection.gameObjects)
                {
                    created.Add(duplicate);
                    MarkGameObjectSceneDirty(duplicate);
                }
            }

            // Leave the whole new set selected rather than just the last copy: making N of something is
            // nearly always followed by acting on all N at once.
            var createdIds = new int[created.Count];
            for (var i = 0; i < created.Count; i++)
            {
                createdIds[i] = created[i].GetInstanceID();
            }

            Selection.instanceIDs = createdIds;
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

            var report = new StringBuilder();
            report.Append('[');
            for (var i = 0; i < created.Count; i++)
            {
                if (i > 0)
                {
                    report.Append(',');
                }

                report.Append(JsonGameObject(created[i]));
            }

            report.Append(']');
            return report.ToString();
        }

        /// <summary>
        /// Finds a GameObject using Conduit's Unity 2022 instance ID, path, or name resolver.
        /// </summary>
        /// <param name="args">The GameObject path, name, or instance ID.</param>
        /// <returns>A compact JSON GameObject reference.</returns>
        private static string GameObjectFindCommand(string args)
        {
            GameObject gameObject;
            var error = TryFindGameObject(args, out gameObject);
            return error ?? JsonGameObject(gameObject);
        }

        /// <summary>
        /// Finds every GameObject in the scene named by the required scene argument (or in the open prefab
        /// stage when one is open, which takes precedence and is said so in the reply), including inactive
        /// ones, that has a component of the given type.
        /// Resolves the type loosely by short or full name, so "KaraokeStageDisplay" and a built-in like
        /// "BoxCollider" both work. This is the reliable way to locate a behaviour by what it IS rather than
        /// dumping and grepping the whole scene tree.
        /// </summary>
        /// <param name="args">The scene, then a component type name (short, full, or assembly-qualified).</param>
        /// <returns>A JSON array of matching GameObject references, or a message when there are none.</returns>
        private static string FindByComponentCommand(string args)
        {
            var tokens = SplitCommandTokens(args ?? string.Empty, false);
            UnityEngine.SceneManagement.Scene scene;
            var sceneError = TryResolveRequiredScene(tokens.Length > 0 ? tokens[0] : string.Empty, out scene);
            if (sceneError != null)
            {
                return sceneError;
            }

            var typeName = tokens.Length > 1 ? string.Join(" ", tokens, 1, tokens.Length - 1).Trim() : string.Empty;
            if (string.IsNullOrEmpty(typeName))
            {
                return "Usage: find-by-component <scene> <componentType>";
            }

            var type = FindTypeLoose(typeName);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                return "Component type not found (or not a Component): " + typeName;
            }

            var builder = new StringBuilder();
            builder.Append('[');
            var count = 0;
            var truncated = false;
            foreach (var gameObject in EnumerateContextGameObjects(scene))
            {
                if (gameObject.GetComponent(type) == null)
                {
                    continue;
                }

                if (count >= DefaultJsonResultLimit)
                {
                    truncated = true;
                    break;
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonGameObject(gameObject));
                count++;
            }

            builder.Append(']');

            if (count == 0)
            {
                return "No GameObject in the " + DescribeContext(scene) + " has a " + type.FullName + " component.";
            }

            var stageNote = CurrentContextLabel() == "prefabStage"
                ? Environment.NewLine + "(searched the open prefab stage, which takes precedence over scene " + scene.name + ")"
                : string.Empty;
            return truncated
                ? builder.ToString() + Environment.NewLine + "(truncated at " + DefaultJsonResultLimit + " matches; narrow the type or use scene-tree)" + stageNote
                : builder.ToString() + stageNote;
        }

        /// <summary>
        /// Modifies one simple GameObject property through an explicit property/value command and reports it as it
        /// was and as it reads back afterwards. Position and rotation also work in play mode, where they move the
        /// live object and are discarded when play ends, so the scene is only marked dirty in edit mode; every
        /// other property is refused in play mode before anything is written. Every value is checked before the
        /// write, so a bad one changes nothing.
        /// </summary>
        /// <param name="args">Pipe-separated target, property name, and value.</param>
        /// <returns>The property before and after, or a validation error.</returns>
        private static string GameObjectModifyCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return "Usage: gameobject-modify <target> | <name|tag|layer|active|position|rotation|scale> | <value>";
            }

            GameObject gameObject;
            var error = TryFindGameObject(sections[0], out gameObject);
            if (error != null)
            {
                return error;
            }

            var property = sections[1].Trim().Trim('"').ToLowerInvariant();
            var value = sections[2].Trim();
            var text = value.Trim('"');
            var isPlaying = EditorApplication.isPlaying;
            if (isPlaying && property != "position" && property != "rotation")
            {
                return "gameobject-modify " + property + " is refused in play mode; only position and rotation move a live object. Nothing was changed.";
            }

            var layer = 0;
            var active = false;
            var vector = Vector3.zero;
            if (property == "layer")
            {
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out layer))
                {
                    layer = LayerMask.NameToLayer(text);
                }

                if (layer < 0 || layer > 31)
                {
                    return "Invalid layer: " + value + ". Nothing was changed.";
                }
            }
            else if (property == "active")
            {
                if (!TryParseBoolLoose(value, out active))
                {
                    return "Invalid active value: " + value + ". Nothing was changed.";
                }
            }
            else if (property == "position" || property == "rotation" || property == "scale")
            {
                if (!TryParseVector3(value, out vector))
                {
                    return "Expected three numeric values for " + property + ". Nothing was changed.";
                }
            }
            else if (property == "tag")
            {
                // Setting an undefined tag throws, so it is refused before the undo record and the write.
                if (Array.IndexOf(UnityEditorInternal.InternalEditorUtility.tags, text) < 0)
                {
                    return "Tag '" + text + "' is not defined in this project; editor-tag-add defines one. Nothing was changed.";
                }
            }
            else if (property != "name")
            {
                return "Unsupported GameObject property: " + property;
            }

            var pathBefore = GetGameObjectPath(gameObject);
            var before = DescribeGameObjectProperty(gameObject, property);
            RecordCompleteUndo(gameObject, "Modify GameObject");
            if (property == "name") gameObject.name = text;
            else if (property == "tag") gameObject.tag = text;
            else if (property == "layer") gameObject.layer = layer;
            else if (property == "active") gameObject.SetActive(active);
            else if (property == "position") gameObject.transform.position = vector;
            else if (property == "rotation") gameObject.transform.rotation = Quaternion.Euler(vector);
            else gameObject.transform.localScale = vector;

            var after = DescribeGameObjectProperty(gameObject, property);
            var playNote = isPlaying ? " Play mode: discarded when play ends." : string.Empty;
            if (before == after)
            {
                return "No change to " + pathBefore + ": " + property + " already " + after + "." + playNote;
            }

            if (!isPlaying)
            {
                EditorUtility.SetDirty(gameObject);
                MarkGameObjectSceneDirty(gameObject);
            }

            return "Modified GameObject " + pathBefore +
                (property == "name" ? " (now " + GetGameObjectPath(gameObject) + ")" : string.Empty) +
                ": " + property + " " + before + " -> " + after + "." + playNote;
        }

        /// <summary>Reads one gameobject-modify property exactly as it stands, for the before and after evidence.</summary>
        /// <param name="gameObject">Object to read.</param>
        /// <param name="property">Lowercased property name, already validated.</param>
        /// <returns>The value; vectors are given in world and local space where both exist.</returns>
        private static string DescribeGameObjectProperty(GameObject gameObject, string property)
        {
            var transform = gameObject.transform;
            switch (property)
            {
                case "name":
                    return "\"" + gameObject.name + "\"";
                case "tag":
                    return "\"" + gameObject.tag + "\"";
                case "layer":
                    var layerName = LayerMask.LayerToName(gameObject.layer);
                    return gameObject.layer.ToString(CultureInfo.InvariantCulture) +
                        (string.IsNullOrEmpty(layerName) ? " (unnamed)" : " (" + layerName + ")");
                case "active":
                    return (gameObject.activeSelf ? "true" : "false") +
                        " (active in hierarchy " + (gameObject.activeInHierarchy ? "true" : "false") + ")";
                case "position":
                    return "world " + FormatExactVector3(transform.position) + ", local " + FormatExactVector3(transform.localPosition);
                case "rotation":
                    return "world " + FormatExactVector3(transform.eulerAngles) + ", local " + FormatExactVector3(transform.localEulerAngles);
                default:
                    return "local " + FormatExactVector3(transform.localScale);
            }
        }

        /// <summary>
        /// Reparents a GameObject under another scene object or the scene root.
        /// </summary>
        /// <param name="args">Pipe-separated target and parent-or-root selector.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string GameObjectSetParentCommand(string args)
        {
            return ReparentObject(args);
        }

        /// <summary>
        /// Returns one GameObject and its descendants, as a recursive JSON tree by default or as an indented
        /// name-only tree when the caller asks for names.
        /// </summary>
        /// <param name="args">Pipe-separated GameObject target, then an optional max depth and "names" in either order.</param>
        /// <returns>A JSON tree, a name tree, or a lookup error.</returns>
        private static string GameObjectTreeCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 1 || string.IsNullOrWhiteSpace(sections[0]))
            {
                return "Usage: gameobject-tree <target> [| maxDepth] [| names]";
            }

            GameObject gameObject;
            var error = TryFindGameObject(sections[0], out gameObject);
            if (error != null)
            {
                return error;
            }

            var namesOnly = false;
            var maxDepth = 16;
            for (var i = 1; i < sections.Length; i++)
            {
                if (IsNameTreeToken(sections[i]))
                {
                    namesOnly = true;
                    continue;
                }

                maxDepth = ParsePositiveInt(sections[i], maxDepth);
            }

            var builder = new StringBuilder();
            if (namesOnly)
            {
                AppendGameObjectNameTree(builder, gameObject, 0, maxDepth);
                return builder.ToString().TrimEnd();
            }

            AppendGameObjectTreeJson(builder, gameObject, 0, maxDepth);
            return builder.ToString();
        }



        /// <summary>
        /// Reads a generic Unity object reference and editor JSON where supported.
        /// </summary>
        /// <param name="args">A scene object target or asset path/GUID/instance ID.</param>
        /// <returns>A compact JSON object payload.</returns>
        private static string ObjectGetDataCommand(string args)
        {
            UnityEngine.Object unityObject;
            var error = TryFindAnyUnityObject(args, out unityObject);
            if (error != null)
            {
                return error;
            }

            var editorJson = EditorJsonUtility.ToJson(unityObject, false);
            return "{\"object\":" + JsonObjectRef(unityObject) +
                ",\"serialized\":" + JsonQuote(CorrectEditorJsonObjectReferences(unityObject, editorJson)) +
                (unityObject is Component ? ",\"unityEvents\":" + JsonUnityEvents(unityObject) : string.Empty) + "}";
        }

        /// <summary>
        /// Writes named fields on any Unity object - a component, a GameObject, an asset - field by field through
        /// <see cref="ModifySerializedFields"/>, so each field is reported as it was and as Unity reads it back.
        /// </summary>
        /// <param name="args">Pipe-separated object target and JSON object.</param>
        /// <returns>The per-field evidence report, or an error having applied nothing.</returns>
        private static string ObjectModifyCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: object-modify <target> | <json>";
            }

            UnityEngine.Object unityObject;
            var error = TryFindAnyUnityObject(sections[0], out unityObject);
            if (error != null)
            {
                return error;
            }

            return ModifySerializedFields(unityObject, sections[1], "Modify Object");
        }

            /// <summary>
        /// Routes the GameObject verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchGameObject(string verb, string command, string[] parts)
        {
            if (verb == "gameobject-component-add")
            {
                return GameObjectComponentAddCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-component-destroy")
            {
                return GameObjectComponentDestroyCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-remove-missing-scripts")
            {
                return GameObjectRemoveMissingScriptsCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-component-get")
            {
                return GameObjectComponentGetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-component-list-all")
            {
                return GameObjectComponentListAllCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-component-list")
            {
                return GameObjectComponentListCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-component-modify")
            {
                return GameObjectComponentModifyCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "set-reference")
            {
                return SetReferenceCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "unity-event-get")
            {
                return UnityEventGetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "unity-event-set-string")
            {
                return UnityEventSetStringCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-create")
            {
                return GameObjectCreateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-destroy")
            {
                return GameObjectDestroyCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-duplicate-multi")
            {
                return GameObjectDuplicateMultiCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-duplicate")
            {
                return GameObjectDuplicateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-find")
            {
                return GameObjectFindCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "find-by-component")
            {
                return FindByComponentCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-modify")
            {
                return GameObjectModifyCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-set-parent")
            {
                return GameObjectSetParentCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "gameobject-tree")
            {
                return GameObjectTreeCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "object-get-data")
            {
                return ObjectGetDataCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "object-modify")
            {
                return ObjectModifyCommand(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
