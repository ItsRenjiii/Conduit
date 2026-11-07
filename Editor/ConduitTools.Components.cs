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
    /// Component verbs: adding, destroying, listing and modifying components on a GameObject.
    /// </summary>
    internal static partial class ConduitBridge
    {

        /// <summary>
        /// Adds a component type to a target GameObject.
        /// </summary>
        /// <param name="args">Pipe-separated GameObject target and component type name.</param>
        /// <returns>A JSON component reference or a validation error.</returns>
        private static string GameObjectComponentAddCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: gameobject-component-add <target> | <typeName>";
            }

            GameObject gameObject;
            var error = TryFindGameObject(sections[0], out gameObject);
            if (error != null)
            {
                return error;
            }

            var type = FindTypeLoose(sections[1]);
            if (type == null || !typeof(Component).IsAssignableFrom(type))
            {
                return "Component type not found: " + sections[1];
            }

            RecordCompleteUndo(gameObject, "Add Component");
            Component component;
            try
            {
                component = AddComponentWithUdonSharpSetup(gameObject, type);
            }
            catch (TargetInvocationException exception)
            {
                var cause = exception.InnerException ?? exception;
                return "Failed to add component " + type.FullName + ": " + cause.Message;
            }
            catch (Exception exception)
            {
                return "Failed to add component " + type.FullName + ": " + exception.Message;
            }

            MarkGameObjectSceneDirty(gameObject);
            return JsonComponent(component);
        }

        /// <summary>
        /// Destroys a component selected by type, index, or instance ID.
        /// </summary>
        /// <param name="args">Pipe-separated GameObject target and component selector.</param>
        /// <returns>A confirmation line or a validation error.</returns>
        private static string GameObjectComponentDestroyCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: gameobject-component-destroy <target> | <component>";
            }

            Component component;
            var error = ResolveComponentFromSections(sections, out component);
            if (error != null)
            {
                return error;
            }

            var owner = component.gameObject;
            var typeName = component.GetType().FullName;
            DestroyWithUndo(component, "Destroy Component");
            MarkGameObjectSceneDirty(owner);
            return "Destroyed component " + typeName + " on " + GetGameObjectPath(owner) + ".";
        }

        /// <summary>
        /// Strips MonoBehaviour components whose script no longer resolves, from a target and its children.
        /// A missing component has neither a type nor a usable index, so it cannot be addressed through
        /// gameobject-component-destroy, and Unity refuses to save a prefab that still contains one.
        /// </summary>
        /// <param name="args">GameObject target, in the active scene or an open prefab stage.</param>
        /// <returns>The removed count and the objects it came from, or a validation error.</returns>
        private static string GameObjectRemoveMissingScriptsCommand(string args)
        {
            GameObject gameObject;
            var error = TryFindGameObject(args, out gameObject);
            if (error != null)
            {
                return error;
            }

            var touched = new StringBuilder();
            var touchedCount = 0;
            var removed = 0;
            foreach (var current in gameObject.GetComponentsInChildren<Transform>(true))
            {
                var owner = current.gameObject;
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(owner) <= 0)
                {
                    continue;
                }

                var count = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(owner);
                if (count <= 0)
                {
                    continue;
                }

                removed += count;
                touchedCount++;
                if (touched.Length > 0)
                {
                    touched.Append("; ");
                }

                touched.Append(GetGameObjectPath(owner)).Append(" (").Append(count).Append(")");
            }

            if (removed == 0)
            {
                return "No missing scripts under " + GetGameObjectPath(gameObject) + "; nothing was changed.";
            }

            MarkGameObjectSceneDirty(gameObject);
            return "Removed " + removed + " missing script(s) from " + touchedCount + " object(s): " + touched + ".";
        }

        /// <summary>
        /// Reads a component's reference metadata, its editor JSON serialization, and its serialized properties.
        /// </summary>
        /// <param name="args">Pipe-separated GameObject target and component selector.</param>
        /// <returns>A compact JSON component payload.</returns>
        private static string GameObjectComponentGetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: gameobject-component-get <target> | <component>";
            }

            Component component;
            var error = ResolveComponentFromSections(sections, out component);
            if (error != null)
            {
                return error;
            }

            var editorJson = EditorJsonUtility.ToJson(component, false);
            return "{\"component\":" + JsonComponent(component) +
                ",\"serialized\":" + JsonQuote(CorrectEditorJsonObjectReferences(component, editorJson)) +
                ",\"serializedProperties\":" + JsonSerializedProperties(component) +
                ",\"unityEvents\":" + JsonUnityEvents(component) + "}";
        }

        /// <summary>
        /// Lists loaded component types assignable to UnityEngine.Component.
        /// </summary>
        /// <param name="args">Optional case-insensitive type-name filter.</param>
        /// <returns>A compact JSON array of component type names.</returns>
        private static string GameObjectComponentListAllCommand(string args)
        {
            var filter = (args ?? string.Empty).Trim().Trim('"');
            var names = new List<string>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null || type.IsAbstract || !typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(filter) &&
                        type.FullName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                        type.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    names.Add(type.FullName);
                    if (names.Count >= DefaultJsonResultLimit)
                    {
                        return JsonStringArray(names);
                    }
                }
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return JsonStringArray(names);
        }

        /// <summary>
        /// Lists the components actually attached to one GameObject - the per-object companion to
        /// <see cref="GameObjectComponentListAllCommand"/> (which lists component types across the project).
        /// Reports each component's index, type, instance id, and enabled state (for the families that have
        /// one) so a caller can pick an exact selector for component-get / -modify / set-reference instead of
        /// guessing. A missing-script slot is reported with its index so it can still be addressed.
        /// </summary>
        /// <param name="args">A GameObject path, name, or instance id.</param>
        /// <returns>A compact JSON object with the owning GameObject and its ordered component list.</returns>
        private static string GameObjectComponentListCommand(string args)
        {
            GameObject gameObject;
            var error = TryFindGameObject(args, out gameObject);
            if (error != null)
            {
                return error;
            }

            var components = gameObject.GetComponents<Component>();
            var builder = new StringBuilder();
            builder.Append("{\"gameObject\":").Append(JsonGameObject(gameObject))
                .Append(",\"count\":").Append(components.Length)
                .Append(",\"components\":[");

            for (var i = 0; i < components.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                var component = components[i];
                if (component == null)
                {
                    // A missing/broken script leaves a null slot; surface its index so it stays addressable.
                    builder.Append("{\"index\":").Append(i).Append(",\"type\":null,\"missingScript\":true}");
                    continue;
                }

                builder.Append("{\"index\":").Append(i)
                    .Append(",\"type\":").Append(JsonQuote(component.GetType().FullName))
                    .Append(",\"instanceID\":").Append(component.GetInstanceID());

                // Surface the enabled flag for the component families that expose one.
                if (component is Behaviour)
                {
                    builder.Append(",\"enabled\":").Append(((Behaviour)component).enabled ? "true" : "false");
                }
                else if (component is Renderer)
                {
                    builder.Append(",\"enabled\":").Append(((Renderer)component).enabled ? "true" : "false");
                }
                else if (component is Collider)
                {
                    builder.Append(",\"enabled\":").Append(((Collider)component).enabled ? "true" : "false");
                }

                builder.Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        /// <summary>
        /// Modifies a component's serialized fields from a flat <c>{"field": value}</c> JSON payload through
        /// <see cref="ModifySerializedFields"/>, so the edit lands identically on MonoBehaviours, UdonSharp
        /// proxies and native engine components (Collider, Rigidbody, RectTransform) and every field is reported
        /// as Unity reads it back.
        /// </summary>
        /// <param name="args">Pipe-separated GameObject target, component selector, and JSON object.</param>
        /// <returns>A per-field report of set, unchanged, not applied and unknown fields, or a validation error.</returns>
        private static string GameObjectComponentModifyCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return "Usage: gameobject-component-modify <target> | <component> | <json>";
            }

            Component component;
            var error = ResolveComponentFromSections(sections, out component);
            if (error != null)
            {
                return error;
            }

            return ModifySerializedFields(component, sections[2], "Modify Component");
        }

        /// <summary>
        /// Writes a flat <c>{"field": value}</c> payload onto any serialized object field by field, and reports each
        /// named field as Unity reads it back once the write is committed: changed (old -> new), already at the asked
        /// value, or not applied, plus any field the write changed that the payload did not name. Never a blanket
        /// success.
        /// </summary>
        /// <remarks>
        /// Every field resolves and parses before anything is written, so one bad entry applies nothing. Values,
        /// object references, arrays of either and the leaves of nested structs go through SerializedProperty. A
        /// field kind with no typed writer goes through the JSON overwrite on a managed object and is refused on a
        /// native one, whose data an overwrite cannot reach. A payload that is not a flat object goes through the
        /// raw overwrite whole, and every top-level property it changed is listed.
        /// </remarks>
        /// <param name="target">The component, GameObject or other object to write.</param>
        /// <param name="payloadSection">The JSON section as the command received it.</param>
        /// <param name="undoName">Undo label for the write.</param>
        /// <returns>The per-field evidence report, or an error having applied nothing.</returns>
        private static string ModifySerializedFields(UnityEngine.Object target, string payloadSection, string undoName)
        {
            try
            {
                // A stale MCP server can deliver the JSON section wrapped in quotes with inner escapes; peel that
                // defensively so the payload-shape check below judges the real JSON instead of the wrapper.
                var payload = UnwrapQuotedJsonSection(payloadSection);
                var managed = target is MonoBehaviour || target is ScriptableObject;

                List<KeyValuePair<string, string>> fields;
                if (!TrySplitFlatJsonObject(payload, out fields))
                {
                    // Last-resort raw overwrite for a payload that is not a flat object, judged by what it changed:
                    // no field is named, so every top-level property is compared before and after.
                    var rawBefore = CaptureTopLevelState(target);
                    var rawAuthoredBefore = CaptureUdonSharpAuthoredValues(target);
                    RecordCompleteUndo(target, undoName);
                    EditorJsonUtility.FromJsonOverwrite(WrapJsonForOverwrite(target, payload), target);
                    SyncUdonSharpProxy(target, rawAuthoredBefore);
                    var rawChanges = DescribeUnnamedChanges(target, rawBefore, null);
                    if (rawChanges.Count == 0)
                    {
                        return "Modify changed NOTHING on " + DescribeWriteTarget(target) +
                            ": the payload is not a flat {\"field\": value} object and the raw JSON overwrite altered no serialized data. " +
                            "A native engine object keeps its data in C++, and a mismatched payload shape matches nothing. Re-send the payload as a flat object literal.";
                    }

                    MarkWriteTargetDirty(target);
                    return "Modified " + DescribeWriteTarget(target) +
                        " via raw JSON overwrite (the payload is not a flat object, so every property it changed is listed): " +
                        string.Join("; ", rawChanges) + "." + PlayModeWriteNote();
                }

                if (fields.Count == 0)
                {
                    return "Empty payload: no fields to modify on " + DescribeWriteTarget(target) + ".";
                }

                // Pass 1 - classify and fully validate every field before anything is written. Object references
                // resolve here, values parse here, and any hard failure returns without touching the object.
                var serializedObject = new SerializedObject(target);
                var staged = new List<StagedComponentFieldWrite>();
                var overwriteFields = new List<KeyValuePair<string, string>>();
                var unknownFields = new List<string>();

                foreach (var entry in fields)
                {
                    var property = serializedObject.FindProperty(entry.Key);
                    if (property == null)
                    {
                        unknownFields.Add(entry.Key);
                        continue;
                    }

                    var stage = new StagedComponentFieldWrite { Name = entry.Key, Kind = property.propertyType };

                    // Arrays of object references come through as Generic properties, so without this branch they
                    // fall to the JSON overwrite, which cannot carry UnityEngine.Object references and silently
                    // writes the array LENGTH with every element left null while reporting a successful change.
                    Type elementType;
                    if (TryGetObjectReferenceArrayElementType(target, entry.Key, property, out elementType))
                    {
                        List<UnityEngine.Object> elements;
                        var arrayError = ResolveReferenceArrayFromJsonValue(entry.Value, elementType, out elements);
                        if (arrayError != null)
                        {
                            return "Failed to resolve '" + entry.Key + "': " + arrayError + " Nothing was applied.";
                        }

                        stage.IsReferenceArray = true;
                        stage.ReferenceArray = elements;
                        staged.Add(stage);
                        continue;
                    }

                    // An array of plain values is written element by element through the typed writers, so it is
                    // set and read back like any other field. It used to fall to the JSON overwrite, whose only
                    // evidence was that the object changed somewhere.
                    if (property.isArray && property.propertyType != SerializedPropertyType.String)
                    {
                        string arrayError;
                        if (TryStageValueArrayWrite(target, property, entry.Value, stage, out arrayError))
                        {
                            staged.Add(stage);
                            continue;
                        }

                        if (arrayError != null)
                        {
                            return "Failed to parse '" + entry.Key + "': " + arrayError + " Nothing was applied.";
                        }
                    }

                    // A Generic property that is not an array is a nested STRUCT - JointLimits, JointSpring,
                    // JointMotor, JointDrive, soft joint limits. It has no typed writer of its own, but every
                    // leaf inside one is an ordinary float or bool that the existing writers handle, so expand
                    // it into one staged write per leaf rather than refusing. Refusing aborted the WHOLE
                    // payload, so a plain sibling field sent in the same call was lost with it.
                    if (property.propertyType == SerializedPropertyType.Generic && !property.isArray)
                    {
                        var expandError = StageNestedStructWrites(serializedObject, entry, property, staged, unknownFields);
                        if (expandError != null)
                        {
                            return expandError;
                        }

                        continue;
                    }

                    if (property.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        // Reflection supplies the concrete field type for coercion where it can; native m_ fields
                        // have no C# field to reflect, and the base Object type lets the resolver pass them through.
                        var field = FindSerializedField(target.GetType(), entry.Key);
                        var fieldType = field != null ? field.FieldType : typeof(UnityEngine.Object);
                        UnityEngine.Object reference;
                        var resolveError = ResolveReferenceFromJsonValue(entry.Value, fieldType, out reference);
                        if (resolveError != null)
                        {
                            return "Failed to resolve '" + entry.Key + "': " + resolveError + " Nothing was applied.";
                        }

                        stage.Reference = reference;
                    }
                    else
                    {
                        string parseError;
                        if (!TryParseValueForProperty(property, entry.Value, stage, out parseError))
                        {
                            if (parseError != null)
                            {
                                return "Failed to parse '" + entry.Key + "': " + parseError + " Nothing was applied.";
                            }

                            // Field kind without a typed writer here. A managed object can still take it through the
                            // JSON overwrite, read back afterwards like every other field; a native engine object
                            // cannot, and pretending otherwise is exactly the false positive this command used to produce.
                            if (managed)
                            {
                                overwriteFields.Add(entry);
                                continue;
                            }

                            return "Field '" + entry.Key + "' is a " + property.propertyType + " property on a native engine object; " +
                                "this kind has no typed SerializedProperty writer here and JSON overwrites cannot reach native data. Nothing was applied.";
                        }
                    }

                    staged.Add(stage);
                }

                if (staged.Count == 0 && overwriteFields.Count == 0)
                {
                    return "Modify changed NOTHING on " + DescribeWriteTarget(target) +
                        ": no supplied field matched a serialized property (" + string.Join(", ", unknownFields) +
                        "). Use gameobject-component-get or object-get-data to list the real serialized field names.";
                }

                // Every named field, and every top-level property, is read before the write, so the report can be
                // built from fresh reads after it instead of from the request.
                var evidence = CaptureFieldEvidence(target, staged, overwriteFields);
                var stateBefore = CaptureTopLevelState(target);

                RecordCompleteUndo(target, undoName);
                var authoredBefore = CaptureUdonSharpAuthoredValues(target);

                // Pass 2 - write every staged value through its typed setter, then commit.
                foreach (var stage in staged)
                {
                    ApplyStagedPropertyValue(serializedObject.FindProperty(stage.Name), stage);
                }

                serializedObject.ApplyModifiedProperties();

                // Field kinds with no typed writer on a managed object go through the JSON overwrite, judged by the
                // same fresh read as every other field.
                if (overwriteFields.Count > 0)
                {
                    EditorJsonUtility.FromJsonOverwrite(WrapJsonForOverwrite(target, BuildFlatJsonObject(overwriteFields)), target);
                }

                // Push what this command changed into a running Udon program last, once every write has landed.
                SyncUdonSharpProxy(target, authoredBefore);

                var setFields = new List<string>();
                var unchangedFields = new List<string>();
                var notAppliedFields = new List<string>();
                ClassifyCommittedFields(target, evidence, setFields, unchangedFields, notAppliedFields);

                var namedPaths = new List<string>();
                foreach (var fieldEvidence in evidence)
                {
                    namedPaths.Add(fieldEvidence.Path);
                }

                var unnamedChanges = DescribeUnnamedChanges(target, stateBefore, namedPaths);
                if (setFields.Count > 0 || unnamedChanges.Count > 0)
                {
                    MarkWriteTargetDirty(target);
                }

                return BuildFieldWriteReport(target, setFields, unchangedFields, notAppliedFields, unnamedChanges, unknownFields) +
                    PlayModeWriteNote();
            }
            catch (Exception ex)
            {
                return "Failed to modify " + DescribeWriteTarget(target) + ": " + ex.Message;
            }
        }



        /// <summary>
        /// Resolves a component from a split command argument array.
        /// </summary>
        /// <param name="sections">The split command sections containing target and component selector.</param>
        /// <param name="component">The resolved component.</param>
        /// <returns>An error string when lookup fails, otherwise null.</returns>
        private static string ResolveComponentFromSections(string[] sections, out Component component)
        {
            component = null;
            GameObject gameObject;
            var error = TryFindGameObject(sections[0], out gameObject);
            if (error != null)
            {
                return error;
            }

            return TryFindComponent(gameObject, sections[1], out component);
        }
    }
}
