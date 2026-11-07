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
    /// Object-reference and UnityEvent wiring, including persistent string listeners.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Object reference verbs

        /// <summary>
        /// Sets (or clears) an object-reference field on a component, pointing it at another scene object,
        /// component, or asset. This is the reliable way to wire references that a JSON overwrite cannot set:
        /// it assigns through SerializedProperty (so scene-local and sub-asset references serialize correctly)
        /// and then syncs the UdonSharp proxy so the edit persists on U# behaviours. Only UnityEngine.Object
        /// fields are supported - not UnityEvents (e.g. a Button's OnClick) or value-type fields. For an ARRAY of
        /// references, send the whole array through gameobject-component-modify instead; this command is scalar only.
        /// </summary>
        /// <param name="args">Pipe-separated target, component selector, serialized field name, and a
        /// reference spec: an asset path (Assets/... or Packages/...), a scene path / name / instance id, or
        /// "null" to clear the field.</param>
        /// <returns>A confirmation line, or a validation error.</returns>
        private static string SetReferenceCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 4)
            {
                return "Usage: set-reference <target> | <component> | <field> | <reference>  (reference = Assets/... path, scene path/name/id, or null)";
            }

            Component component;
            var error = ResolveComponentFromSections(sections, out component);
            if (error != null)
            {
                return error;
            }

            var fieldName = sections[2].Trim().Trim('"');
            if (string.IsNullOrEmpty(fieldName))
            {
                return "Missing field name. Usage: set-reference <target> | <component> | <field> | <reference>";
            }

            // Resolve the field through reflection (walking base types for inherited private [SerializeField]
            // fields like a built-in's m_Sprite) so we know the type the reference must satisfy.
            var field = FindSerializedField(component.GetType(), fieldName);
            var fieldType = field == null ? null : field.FieldType;

            // Reflection first, because a managed field also states the exact type the reference must
            // satisfy. But a NATIVE serialized field has no C# field to find: Joint exposes connectedBody
            // as an extern property, so m_ConnectedBody is invisible to reflection while being perfectly
            // settable through SerializedProperty. Without this fallback the command reported a field that
            // plainly exists as "not found".
            if (fieldType == null)
            {
                var probe = new SerializedObject(component).FindProperty(fieldName);
                if (probe == null)
                {
                    return "Field not found on " + component.GetType().FullName + ": " + fieldName + ". Use the serialized field name (e.g. _myRef, or m_Sprite for built-ins).";
                }

                if (probe.propertyType != SerializedPropertyType.ObjectReference)
                {
                    return "Field '" + fieldName + "' is " + probe.propertyType + ", not an object reference. set-reference only sets UnityEngine.Object fields.";
                }

                fieldType = ObjectReferenceFieldType(probe);
            }

            if (!typeof(UnityEngine.Object).IsAssignableFrom(fieldType))
            {
                return "Field '" + fieldName + "' is " + fieldType.Name + ", not an object reference. set-reference only sets UnityEngine.Object fields.";
            }

            UnityEngine.Object reference = null;
            var spec = sections[3].Trim().Trim('"');
            var clearing = spec.Length == 0 ||
                string.Equals(spec, "null", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(spec, "none", StringComparison.OrdinalIgnoreCase);
            if (!clearing)
            {
                var resolveError = ResolveObjectReference(spec, fieldType, out reference);
                if (resolveError != null)
                {
                    return resolveError;
                }
            }

            var serializedObject = new SerializedObject(component);
            var property = serializedObject.FindProperty(fieldName);
            if (property == null)
            {
                return "Field '" + fieldName + "' is not a serialized property on " + component.GetType().FullName + ".";
            }

            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                return "Serialized field '" + fieldName + "' is not an object reference.";
            }

            var before = DescribePropertyValue(property);
            RecordCompleteUndo(component, "Set Reference");
            var authoredBefore = CaptureUdonSharpAuthoredValues(component);
            property.objectReferenceValue = reference;
            serializedObject.ApplyModifiedProperties();
            SyncUdonSharpProxy(component, authoredBefore);

            // Read back from a fresh serialized copy, so the report says what the field holds, not what was assigned.
            string after;
            using (var written = new SerializedObject(component))
            {
                var readBack = written.FindProperty(fieldName);
                after = readBack == null ? "absent" : DescribePropertyValue(readBack);
            }

            var asked = reference == null ? "null" : reference.name + " (" + reference.GetType().Name + " #" + reference.GetInstanceID() + ")";
            var label = component.GetType().Name + "." + fieldName + " on " + GetGameObjectPath(component.gameObject);
            if (after == before)
            {
                return after == asked
                    ? label + " is already " + after + ". Nothing changed."
                    : "NOT applied: " + label + " is still " + after + "; asked for " + asked + ".";
            }

            EditorUtility.SetDirty(component);
            MarkGameObjectSceneDirty(component.gameObject);
            return "Set " + label + ": " + before + " -> " + after +
                (after == asked ? "." : "; asked for " + asked + ", so something after the write changed it.");
        }

        /// <summary>
        /// Resolves a set-reference spec into a UnityEngine.Object assignable to the requested field type. An
        /// Assets/... or Packages/... path is loaded as the field type first (so a sprite field pulls the Sprite
        /// sub-asset out of a texture path) then falls back to the main asset; anything else is treated as a
        /// scene GameObject path / name / instance id, returning the GameObject or its requested component.
        /// </summary>
        /// <param name="spec">The reference spec.</param>
        /// <param name="fieldType">The type the resolved reference must satisfy.</param>
        /// <param name="reference">The resolved object.</param>
        /// <returns>An error string when resolution fails, otherwise null.</returns>
        private static string ResolveObjectReference(string spec, Type fieldType, out UnityEngine.Object reference)
        {
            reference = null;

            if (spec.StartsWith("Assets/", StringComparison.Ordinal) || spec.StartsWith("Packages/", StringComparison.Ordinal))
            {
                reference = AssetDatabase.LoadAssetAtPath(spec, fieldType) ?? AssetDatabase.LoadMainAssetAtPath(spec);
                if (reference == null)
                {
                    return "No asset found at " + spec + ".";
                }

                if (!fieldType.IsInstanceOfType(reference))
                {
                    return "Asset at " + spec + " is " + reference.GetType().Name + ", not assignable to field type " + fieldType.Name + ".";
                }

                return null;
            }

            GameObject gameObject;
            var error = TryFindGameObject(spec, out gameObject);
            if (error != null)
            {
                return error;
            }

            if (fieldType == typeof(GameObject))
            {
                reference = gameObject;
                return null;
            }

            if (typeof(Component).IsAssignableFrom(fieldType))
            {
                var matches = gameObject.GetComponents(fieldType);
                if (matches.Length == 0)
                {
                    return "GameObject " + GetGameObjectPath(gameObject) + " has no " + fieldType.Name + " component to assign.";
                }

                if (matches.Length > 1)
                {
                    return "Ambiguous: " + matches.Length + " " + fieldType.Name + " components on " + GetGameObjectPath(gameObject) + ". Use a more specific field/component type or target.";
                }

                reference = matches[0];
                return null;
            }

            return "Field type " + fieldType.Name + " is not a GameObject or Component; pass an Assets/... asset path instead.";
        }

        /// <summary>
        /// Derives the type an object-reference property accepts, for a native field reflection cannot see.
        /// </summary>
        /// <param name="property">An ObjectReference serialized property.</param>
        /// <returns>The referenced type, or UnityEngine.Object when the name cannot be resolved.</returns>
        private static Type ObjectReferenceFieldType(SerializedProperty property)
        {
            // Unity spells these "PPtr<$Rigidbody>", and the dollar is not part of the type name.
            var raw = property.type;
            var open = raw.IndexOf('<');
            var close = raw.LastIndexOf('>');
            if (open >= 0 && close > open)
            {
                var inner = raw.Substring(open + 1, close - open - 1).TrimStart('$');
                var resolved = FindTypeLoose(inner);
                if (resolved != null)
                {
                    return resolved;
                }
            }

            // Falling back to the base type still lets an explicit instanceID or asset path through; only
            // "resolve a component off this GameObject" needs the precise type.
            return typeof(UnityEngine.Object);
        }

        /// <summary>
        /// Finds a serialized instance field by name, walking base types so inherited private [SerializeField]
        /// fields (such as a built-in component's m_-prefixed fields) are found too.
        /// </summary>
        /// <param name="type">The component type to search.</param>
        /// <param name="fieldName">The serialized field name.</param>
        /// <returns>The matching FieldInfo, or null.</returns>
        private static FieldInfo FindSerializedField(Type type, string fieldName)
        {
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                var field = current.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field;
                }
            }

            return null;
        }

        #endregion

        #region Reading UnityEvents

        /// <summary>
        /// Reads a UnityEvent serialized field through Unity's live SerializedProperty API so persistent-call
        /// object references are reported truthfully.
        /// </summary>
        /// <param name="args">Pipe-separated GameObject target, component selector, and UnityEvent field path.</param>
        /// <returns>A compact JSON description of the UnityEvent calls, or a validation error.</returns>
        private static string UnityEventGetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return "Usage: unity-event-get <target> | <component> | <eventField>";
            }

            Component component;
            var error = ResolveComponentFromSections(sections, out component);
            if (error != null)
            {
                return error;
            }

            var serializedObject = new SerializedObject(component);
            SerializedProperty eventProperty;
            SerializedProperty callsProperty;
            error = ResolveUnityEventProperty(serializedObject, sections[2], out eventProperty, out callsProperty);
            if (error != null)
            {
                return error;
            }

            return "{\"component\":" + JsonComponent(component) +
                ",\"eventField\":" + JsonQuote(eventProperty.propertyPath) +
                ",\"calls\":" + JsonUnityEventCalls(callsProperty) + "}";
        }

        /// <summary>
        /// Encodes every top-level UnityEvent on a component using SerializedProperty so nested object
        /// references are read from Unity's live object-reference API, not EditorJsonUtility's lossy JSON.
        /// </summary>
        /// <param name="target">The object to inspect for UnityEvent fields.</param>
        /// <returns>A compact JSON object keyed by UnityEvent serialized property path.</returns>
        private static string JsonUnityEvents(UnityEngine.Object target)
        {
            if (target == null)
            {
                return "{}";
            }

            var serializedObject = new SerializedObject(target);
            var property = serializedObject.GetIterator();
            var builder = new StringBuilder();
            var first = true;

            builder.Append('{');
            if (property.NextVisible(true))
            {
                do
                {
                    if (property.depth != 0)
                    {
                        continue;
                    }

                    SerializedProperty callsProperty;
                    if (!TryGetUnityEventCallsProperty(property, out callsProperty))
                    {
                        continue;
                    }

                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonQuote(property.propertyPath))
                        .Append(":{\"type\":")
                        .Append(JsonQuote(property.type))
                        .Append(",\"calls\":")
                        .Append(JsonUnityEventCalls(callsProperty))
                        .Append('}');
                }
                while (property.NextVisible(false));
            }

            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>
        /// Encodes a UnityEvent persistent-call array.
        /// </summary>
        /// <param name="callsProperty">The <c>m_PersistentCalls.m_Calls</c> array property.</param>
        /// <returns>A JSON array of persistent-call objects.</returns>
        private static string JsonUnityEventCalls(SerializedProperty callsProperty)
        {
            var builder = new StringBuilder();
            builder.Append('[');

            var count = callsProperty == null ? 0 : Math.Min(callsProperty.arraySize, DefaultJsonResultLimit);
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUnityEventCall(callsProperty.GetArrayElementAtIndex(i), i));
            }

            if (callsProperty != null && callsProperty.arraySize > count)
            {
                builder.Append(count > 0 ? "," : string.Empty)
                    .Append("{\"truncated\":true,\"totalCount\":")
                    .Append(callsProperty.arraySize)
                    .Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// Encodes one UnityEvent persistent call, including nested target references from SerializedProperty.
        /// </summary>
        /// <param name="callProperty">The call property to encode.</param>
        /// <param name="index">The call index in the UnityEvent array.</param>
        /// <returns>A JSON object describing the persistent call.</returns>
        private static string JsonUnityEventCall(SerializedProperty callProperty, int index)
        {
            if (callProperty == null)
            {
                return "{\"index\":" + index + ",\"missing\":true}";
            }

            var targetProperty = callProperty.FindPropertyRelative("m_Target");
            var targetAssemblyProperty = callProperty.FindPropertyRelative("m_TargetAssemblyTypeName");
            var methodProperty = callProperty.FindPropertyRelative("m_MethodName");
            var modeProperty = callProperty.FindPropertyRelative("m_Mode");
            var callStateProperty = callProperty.FindPropertyRelative("m_CallState");
            var objectArgumentProperty = callProperty.FindPropertyRelative("m_Arguments.m_ObjectArgument");
            var objectArgumentTypeProperty = callProperty.FindPropertyRelative("m_Arguments.m_ObjectArgumentAssemblyTypeName");
            var intArgumentProperty = callProperty.FindPropertyRelative("m_Arguments.m_IntArgument");
            var floatArgumentProperty = callProperty.FindPropertyRelative("m_Arguments.m_FloatArgument");
            var stringArgumentProperty = callProperty.FindPropertyRelative("m_Arguments.m_StringArgument");
            var boolArgumentProperty = callProperty.FindPropertyRelative("m_Arguments.m_BoolArgument");

            return "{\"index\":" + index +
                ",\"target\":" + JsonUnityEventObjectReference(targetProperty) +
                ",\"targetAssemblyTypeName\":" + JsonQuote(targetAssemblyProperty == null ? string.Empty : targetAssemblyProperty.stringValue) +
                ",\"methodName\":" + JsonQuote(methodProperty == null ? string.Empty : methodProperty.stringValue) +
                ",\"mode\":" + JsonSerializedEnum(modeProperty) +
                ",\"arguments\":{\"object\":" + JsonUnityEventObjectReference(objectArgumentProperty) +
                ",\"objectAssemblyTypeName\":" + JsonQuote(objectArgumentTypeProperty == null ? string.Empty : objectArgumentTypeProperty.stringValue) +
                ",\"int\":" + (intArgumentProperty == null ? "0" : intArgumentProperty.intValue.ToString(CultureInfo.InvariantCulture)) +
                ",\"float\":" + (floatArgumentProperty == null ? "0" : JsonNumber(floatArgumentProperty.floatValue)) +
                ",\"string\":" + JsonQuote(stringArgumentProperty == null ? string.Empty : stringArgumentProperty.stringValue) +
                ",\"bool\":" + (boolArgumentProperty != null && boolArgumentProperty.boolValue ? "true" : "false") + "}" +
                ",\"callState\":" + JsonSerializedEnum(callStateProperty) + "}";
        }

        /// <summary>
        /// Encodes a UnityEvent object-reference property with GameObject/component details when available.
        /// </summary>
        /// <param name="property">The object-reference property to encode.</param>
        /// <returns>A JSON object reference, a missing-reference object, or null.</returns>
        private static string JsonUnityEventObjectReference(SerializedProperty property)
        {
            if (property == null)
            {
                return "null";
            }

            var reference = property.objectReferenceValue;
            if (reference is Component)
            {
                return JsonComponent((Component)reference);
            }

            if (reference is GameObject)
            {
                return JsonGameObject((GameObject)reference);
            }

            return JsonSerializedObjectReference(property);
        }

        /// <summary>
        /// Encodes an enum SerializedProperty with both the numeric index and current display name.
        /// </summary>
        /// <param name="property">The enum property to encode.</param>
        /// <returns>A JSON enum object.</returns>
        private static string JsonSerializedEnum(SerializedProperty property)
        {
            if (property == null || property.propertyType != SerializedPropertyType.Enum)
            {
                return "{\"index\":0,\"name\":\"\"}";
            }

            var name = property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length
                ? property.enumDisplayNames[property.enumValueIndex]
                : string.Empty;
            return "{\"index\":" + property.enumValueIndex + ",\"name\":" + JsonQuote(name) + "}";
        }

        /// <summary>
        /// Lists top-level UnityEvent property paths for validation messages.
        /// </summary>
        /// <param name="serializedObject">The serialized object to scan.</param>
        /// <returns>A comma-separated list of UnityEvent property paths, or "none".</returns>
        private static string ListUnityEventPropertyNames(SerializedObject serializedObject)
        {
            var names = new List<string>();
            var property = serializedObject.GetIterator();
            if (property.NextVisible(true))
            {
                do
                {
                    if (property.depth != 0)
                    {
                        continue;
                    }

                    SerializedProperty callsProperty;
                    if (TryGetUnityEventCallsProperty(property, out callsProperty))
                    {
                        names.Add(property.propertyPath);
                    }
                }
                while (property.NextVisible(false));
            }

            return names.Count == 0 ? "none" : string.Join(", ", names.ToArray());
        }

        #endregion

        #region Writing persistent listeners

        /// <summary>
        /// Sets one persistent string listener on a UnityEvent field, covering VRChat button hooks such as
        /// <c>Button.m_OnClick</c> to backing <c>UdonBehaviour.SendCustomEvent("_Event")</c>.
        /// </summary>
        /// <param name="args">Pipe-separated owner target, owner component, event field, operation, listener
        /// target, listener component selector, method name, string argument, and optional call state.</param>
        /// <returns>A confirmation JSON payload describing the written call, or a validation error.</returns>
        private static string UnityEventSetStringCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 8)
            {
                return "Usage: unity-event-set-string <target> | <component> | <eventField> | <replace|append|index> | <listenerTarget> | <listenerComponent|none> | <methodName> | <stringArg> [| runtimeOnly|editorAndRuntime|off]";
            }

            Component component;
            var error = ResolveComponentFromSections(sections, out component);
            if (error != null)
            {
                return error;
            }

            var serializedObject = new SerializedObject(component);
            SerializedProperty eventProperty;
            SerializedProperty callsProperty;
            error = ResolveUnityEventProperty(serializedObject, sections[2], true, out eventProperty, out callsProperty);
            if (error != null)
            {
                return error;
            }

            UnityEngine.Object listenerTarget;
            error = ResolveUnityEventListenerTarget(sections[4], sections[5], out listenerTarget);
            if (error != null)
            {
                return error;
            }

            var methodName = sections[6].Trim().Trim('"');
            if (string.IsNullOrEmpty(methodName))
            {
                return "Missing listener method name.";
            }

            error = ValidateStringListenerMethod(listenerTarget, methodName);
            if (error != null)
            {
                return error;
            }

            UnityEventCallState callState;
            error = ParseUnityEventCallState(sections.Length > 8 ? sections[8] : null, out callState);
            if (error != null)
            {
                return error;
            }

            // The calls as they stand before the write; an event element the write is about to create reads as null.
            string callsBefore;
            using (var beforeObject = new SerializedObject(component))
            {
                SerializedProperty beforeEvent;
                SerializedProperty beforeCalls;
                callsBefore = ResolveUnityEventProperty(beforeObject, sections[2], out beforeEvent, out beforeCalls) == null
                    ? JsonUnityEventCalls(beforeCalls)
                    : "null";
            }

            RecordCompleteUndo(component, "Set UnityEvent String Listener");

            int callIndex;
            var operation = sections[3].Trim().Trim('"');
            error = PrepareUnityEventCallSlot(callsProperty, operation, out callIndex);
            if (error != null)
            {
                return error;
            }

            var stringArgument = Unquote(sections[7]);

            var callProperty = callsProperty.GetArrayElementAtIndex(callIndex);
            error = SetUnityEventStringCall(callProperty, listenerTarget, methodName, stringArgument, callState);
            if (error != null)
            {
                return error;
            }

            serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(component);
            MarkGameObjectSceneDirty(component.gameObject);

            // Read back from a fresh serialized copy, so the report says what the event holds, not what was staged.
            string writtenCall;
            string callsAfter;
            using (var afterObject = new SerializedObject(component))
            {
                SerializedProperty afterEvent;
                SerializedProperty afterCalls;
                if (ResolveUnityEventProperty(afterObject, eventProperty.propertyPath, out afterEvent, out afterCalls) == null)
                {
                    callsAfter = JsonUnityEventCalls(afterCalls);
                    writtenCall = callIndex < afterCalls.arraySize
                        ? JsonUnityEventCall(afterCalls.GetArrayElementAtIndex(callIndex), callIndex)
                        : JsonUnityEventCall(null, callIndex);
                }
                else
                {
                    callsAfter = "null";
                    writtenCall = JsonUnityEventCall(null, callIndex);
                }
            }

            return "{\"component\":" + JsonComponent(component) +
                ",\"eventField\":" + JsonQuote(eventProperty.propertyPath) +
                ",\"operation\":" + JsonQuote(operation) +
                ",\"writtenCall\":" + writtenCall +
                ",\"callsBefore\":" + callsBefore +
                ",\"callsAfter\":" + callsAfter + "}";
        }

        /// <summary>
        /// Resolves a UnityEvent field on a SerializedObject and returns its persistent call array.
        /// </summary>
        /// <param name="serializedObject">The serialized owner object.</param>
        /// <param name="eventField">The requested event property path or common field name.</param>
        /// <param name="eventProperty">The resolved UnityEvent property.</param>
        /// <param name="callsProperty">The resolved persistent call array.</param>
        /// <returns>An error string when the event field is missing or not a UnityEvent, otherwise null.</returns>
        private static string ResolveUnityEventProperty(
            SerializedObject serializedObject,
            string eventField,
            out SerializedProperty eventProperty,
            out SerializedProperty callsProperty)
        {
            return ResolveUnityEventProperty(serializedObject, eventField, false, out eventProperty, out callsProperty);
        }

        /// <summary>
        /// Resolves a UnityEvent field, optionally creating the array element that holds it. An ARRAY-ELEMENT
        /// path such as <c>m_Delegates.Array.data[0].callback</c> is how an EventTrigger entry is addressed,
        /// and that element does not exist until something creates it, so a writer may grow the array by one.
        /// An optional <c>:name</c> suffix on the field sets the new element's sibling enum, which is how an
        /// EventTrigger entry gets its eventID in the same call instead of arriving half-formed.
        /// </summary>
        /// <param name="serializedObject">The serialized owner object.</param>
        /// <param name="eventField">The requested event property path, optionally suffixed with :enumName.</param>
        /// <param name="allowGrow">False for read commands, which must never mutate the array.</param>
        /// <param name="eventProperty">The resolved UnityEvent property.</param>
        /// <param name="callsProperty">The resolved persistent call array.</param>
        /// <returns>An error string when the event field is missing or not a UnityEvent, otherwise null.</returns>
        private static string ResolveUnityEventProperty(
            SerializedObject serializedObject,
            string eventField,
            bool allowGrow,
            out SerializedProperty eventProperty,
            out SerializedProperty callsProperty)
        {
            eventProperty = null;
            callsProperty = null;

            var fieldName = (eventField ?? string.Empty).Trim().Trim('"');
            if (string.IsNullOrEmpty(fieldName))
            {
                return "Missing UnityEvent field name. Use m_OnClick for Button.onClick.";
            }

            // A trailing ":name" carries the sibling enum value for an array element about to be created.
            // Split it off before any lookup, or the colon lands in the property path and nothing resolves.
            string siblingEnumValue = null;
            var suffixAt = fieldName.LastIndexOf(':');
            if (suffixAt > 0)
            {
                siblingEnumValue = fieldName.Substring(suffixAt + 1).Trim();
                fieldName = fieldName.Substring(0, suffixAt).Trim();
            }

            eventProperty = serializedObject.FindProperty(fieldName);
            if (eventProperty == null && !fieldName.StartsWith("m_", StringComparison.Ordinal) && fieldName.Length > 0)
            {
                var unityBackingName = "m_" + char.ToUpperInvariant(fieldName[0]) + fieldName.Substring(1);
                eventProperty = serializedObject.FindProperty(unityBackingName);
            }

            if (eventProperty == null)
            {
                var growError = TryGrowUnityEventArrayElement(
                    serializedObject, fieldName, allowGrow, siblingEnumValue, out eventProperty);
                if (growError != null)
                {
                    return growError;
                }
            }

            if (eventProperty == null)
            {
                return "UnityEvent field not found: " + fieldName + ". Available UnityEvents: " + ListUnityEventPropertyNames(serializedObject) + ".";
            }

            if (!TryGetUnityEventCallsProperty(eventProperty, out callsProperty))
            {
                return "Serialized field '" + eventProperty.propertyPath + "' is not a UnityEvent persistent-call field. Available UnityEvents: " + ListUnityEventPropertyNames(serializedObject) + ".";
            }

            return null;
        }

        /// <summary>
        /// Tests whether a SerializedProperty has UnityEvent persistent-call storage and returns its call array.
        /// </summary>
        /// <param name="eventProperty">The candidate event property.</param>
        /// <param name="callsProperty">The persistent call array when found.</param>
        /// <returns>True when the property has UnityEvent persistent calls.</returns>
        private static bool TryGetUnityEventCallsProperty(SerializedProperty eventProperty, out SerializedProperty callsProperty)
        {
            callsProperty = null;
            if (eventProperty == null)
            {
                return false;
            }

            callsProperty = eventProperty.FindPropertyRelative("m_PersistentCalls.m_Calls");
            return callsProperty != null && callsProperty.isArray;
        }

        /// <summary>
        /// Creates the array element an array-element UnityEvent path addresses, when the requested index is
        /// exactly one past the end. Anything further is refused naming the current size, so a caller cannot
        /// punch a hole in the array, and a read is refused outright because it must not mutate anything.
        /// </summary>
        /// <param name="serializedObject">The serialized owner object.</param>
        /// <param name="fieldPath">The requested property path, suffix already removed.</param>
        /// <param name="allowGrow">False for read commands.</param>
        /// <param name="siblingEnumValue">Enum name to write into the new element's eventID sibling, or null.</param>
        /// <param name="eventProperty">The resolved property when the element was created.</param>
        /// <returns>An error string when the path is addressable but unusable, otherwise null.</returns>
        private static string TryGrowUnityEventArrayElement(
            SerializedObject serializedObject,
            string fieldPath,
            bool allowGrow,
            string siblingEnumValue,
            out SerializedProperty eventProperty)
        {
            eventProperty = null;

            const string ARRAY_MARKER = ".Array.data[";
            var markerAt = fieldPath.IndexOf(ARRAY_MARKER, StringComparison.Ordinal);
            if (markerAt < 0)
            {
                return null;
            }

            var closeAt = fieldPath.IndexOf(']', markerAt);
            if (closeAt < 0)
            {
                return null;
            }

            var arrayPath = fieldPath.Substring(0, markerAt);
            var indexText = fieldPath.Substring(markerAt + ARRAY_MARKER.Length, closeAt - markerAt - ARRAY_MARKER.Length);

            int elementIndex;
            if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out elementIndex))
            {
                return null;
            }

            var arrayProperty = serializedObject.FindProperty(arrayPath);
            if (arrayProperty == null || !arrayProperty.isArray)
            {
                return null;
            }

            if (elementIndex < 0 || elementIndex > arrayProperty.arraySize)
            {
                return "Array element index " + elementIndex + " is out of range for '" + arrayPath +
                    "', which currently holds " + arrayProperty.arraySize + " element(s). Nothing was written.";
            }

            if (elementIndex == arrayProperty.arraySize)
            {
                if (!allowGrow)
                {
                    return "'" + arrayPath + "' holds " + arrayProperty.arraySize + " element(s), so index " +
                        elementIndex + " does not exist yet and a read cannot create it.";
                }

                arrayProperty.InsertArrayElementAtIndex(elementIndex);
            }

            eventProperty = serializedObject.FindProperty(fieldPath);
            if (eventProperty == null)
            {
                return "Created '" + arrayPath + "' element " + elementIndex + " but '" + fieldPath +
                    "' still does not resolve. Nothing was written.";
            }

            // The sibling enum decides WHICH event an EventTrigger entry answers. Without it a created entry
            // silently takes the zero value, which is a wired-looking entry on the wrong event.
            if (!string.IsNullOrEmpty(siblingEnumValue))
            {
                var siblingPath = arrayPath + ARRAY_MARKER + elementIndex + "].eventID";
                var siblingProperty = serializedObject.FindProperty(siblingPath);
                if (siblingProperty == null || siblingProperty.propertyType != SerializedPropertyType.Enum)
                {
                    return "No enum sibling 'eventID' on '" + arrayPath + "' element " + elementIndex +
                        ". Nothing was written.";
                }

                string[] names = siblingProperty.enumNames;
                var matched = -1;
                for (var i = 0; i < names.Length; i++)
                {
                    if (string.Equals(names[i], siblingEnumValue, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = i;
                        break;
                    }
                }

                if (matched < 0)
                {
                    return "Unknown eventID '" + siblingEnumValue + "'. Valid values: " +
                        string.Join(", ", names) + ".";
                }

                siblingProperty.enumValueIndex = matched;
            }

            return null;
        }

        /// <summary>
        /// Resolves the listener target for a UnityEvent persistent call, optionally selecting a component from
        /// a GameObject so UdonBehaviour targets are explicit.
        /// </summary>
        /// <param name="targetSpec">Scene object, component instance id, asset path, or GUID.</param>
        /// <param name="componentSelector">Optional component selector when the target spec resolves to a GameObject.</param>
        /// <param name="listenerTarget">The resolved listener target object.</param>
        /// <returns>An error string when resolution fails, otherwise null.</returns>
        private static string ResolveUnityEventListenerTarget(string targetSpec, string componentSelector, out UnityEngine.Object listenerTarget)
        {
            listenerTarget = null;

            var selector = (componentSelector ?? string.Empty).Trim().Trim('"');
            var hasComponentSelector = selector.Length > 0 &&
                !selector.Equals("null", StringComparison.OrdinalIgnoreCase) &&
                !selector.Equals("none", StringComparison.OrdinalIgnoreCase) &&
                selector != "-";

            if (hasComponentSelector)
            {
                GameObject gameObject;
                var error = TryFindGameObject(targetSpec, out gameObject);
                if (error != null)
                {
                    return error;
                }

                Component component;
                error = TryFindUnityEventListenerComponent(gameObject, selector, out component);
                if (error != null)
                {
                    return error;
                }

                listenerTarget = component;
                return null;
            }

            return TryFindAnyUnityObject(targetSpec, out listenerTarget);
        }

        /// <summary>
        /// Resolves a UnityEvent listener component without silently choosing the first type match when several
        /// components of the same type live on the target GameObject.
        /// </summary>
        /// <param name="gameObject">The GameObject that owns the listener component.</param>
        /// <param name="selector">A component instance id, index, full type name, or short type name.</param>
        /// <param name="component">The resolved component when the selector is unambiguous.</param>
        /// <returns>An error string when lookup fails or the type selector is ambiguous, otherwise null.</returns>
        private static string TryFindUnityEventListenerComponent(GameObject gameObject, string selector, out Component component)
        {
            component = null;
            if (gameObject == null)
            {
                return "Missing GameObject for UnityEvent listener component lookup.";
            }

            selector = (selector ?? string.Empty).Trim().Trim('"');
            if (selector.Length == 0)
            {
                return "Missing UnityEvent listener component selector.";
            }

            var components = gameObject.GetComponents<Component>();
            int numeric;
            if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                foreach (var candidate in components)
                {
                    if (candidate != null && candidate.GetInstanceID() == numeric)
                    {
                        component = candidate;
                        return null;
                    }
                }

                if (numeric >= 0 && numeric < components.Length)
                {
                    component = components[numeric];
                    return component == null ? "Component at index " + numeric + " is missing." : null;
                }
            }

            var matches = new List<Component>();
            foreach (var candidate in components)
            {
                if (candidate == null)
                {
                    continue;
                }

                var type = candidate.GetType();
                if (string.Equals(type.FullName, selector, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.Name, selector, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 1)
            {
                component = matches[0];
                return null;
            }

            if (matches.Count > 1)
            {
                var builder = new StringBuilder();
                builder.Append("Ambiguous UnityEvent listener component selector '")
                    .Append(selector)
                    .Append("' on ")
                    .Append(GetGameObjectPath(gameObject))
                    .Append(". Use a component index or instanceID. Matches:");

                for (var i = 0; i < components.Length; i++)
                {
                    var candidate = components[i];
                    if (candidate == null || !matches.Contains(candidate))
                    {
                        continue;
                    }

                    builder.Append(" index ")
                        .Append(i)
                        .Append(" instanceID ")
                        .Append(candidate.GetInstanceID())
                        .Append(" type ")
                        .Append(candidate.GetType().FullName)
                        .Append(';');
                }

                return builder.ToString();
            }

            return "UnityEvent listener component not found on " + GetGameObjectPath(gameObject) + ": " + selector;
        }

        /// <summary>
        /// Verifies that a listener target exposes the requested one-string-parameter method.
        /// </summary>
        /// <param name="listenerTarget">The target object assigned to the persistent call.</param>
        /// <param name="methodName">The method the UnityEvent will invoke.</param>
        /// <returns>An error string when the method is missing, otherwise null.</returns>
        private static string ValidateStringListenerMethod(UnityEngine.Object listenerTarget, string methodName)
        {
            if (listenerTarget == null)
            {
                return "Missing UnityEvent listener target.";
            }

            var methods = listenerTarget.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (var method in methods)
            {
                if (!string.Equals(method.Name, methodName, StringComparison.Ordinal))
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                {
                    return null;
                }
            }

            return "Listener target " + listenerTarget.GetType().FullName + " has no method " + methodName + "(string). Nothing was applied.";
        }

        /// <summary>
        /// Parses a persistent-call state token.
        /// </summary>
        /// <param name="value">Optional call-state token.</param>
        /// <param name="callState">The parsed UnityEvent call state.</param>
        /// <returns>An error string when the token is invalid, otherwise null.</returns>
        private static string ParseUnityEventCallState(string value, out UnityEventCallState callState)
        {
            var token = (value ?? string.Empty).Trim().Trim('"');
            if (token.Length == 0 || token.Equals("runtimeOnly", StringComparison.OrdinalIgnoreCase) || token.Equals("runtime", StringComparison.OrdinalIgnoreCase))
            {
                callState = UnityEventCallState.RuntimeOnly;
                return null;
            }

            if (token.Equals("editorAndRuntime", StringComparison.OrdinalIgnoreCase) || token.Equals("editor", StringComparison.OrdinalIgnoreCase))
            {
                callState = UnityEventCallState.EditorAndRuntime;
                return null;
            }

            if (token.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                callState = UnityEventCallState.Off;
                return null;
            }

            callState = UnityEventCallState.RuntimeOnly;
            return "Unknown UnityEvent call state '" + token + "'. Use runtimeOnly, editorAndRuntime, or off.";
        }

        /// <summary>
        /// Applies a replace/append/index operation to the persistent-call array and returns the call index to write.
        /// </summary>
        /// <param name="callsProperty">The UnityEvent persistent-call array.</param>
        /// <param name="operation">The requested operation token.</param>
        /// <param name="callIndex">The call index that should be populated.</param>
        /// <returns>An error string when the operation is invalid, otherwise null.</returns>
        private static string PrepareUnityEventCallSlot(SerializedProperty callsProperty, string operation, out int callIndex)
        {
            callIndex = -1;
            var token = (operation ?? string.Empty).Trim().Trim('"');

            if (token.Equals("replace", StringComparison.OrdinalIgnoreCase))
            {
                callsProperty.arraySize = 0;
                callsProperty.InsertArrayElementAtIndex(0);
                callIndex = 0;
                return null;
            }

            if (token.Equals("append", StringComparison.OrdinalIgnoreCase))
            {
                callIndex = callsProperty.arraySize;
                callsProperty.InsertArrayElementAtIndex(callIndex);
                return null;
            }

            int parsedIndex;
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedIndex))
            {
                if (parsedIndex < 0 || parsedIndex > callsProperty.arraySize)
                {
                    return "UnityEvent call index " + parsedIndex + " is out of range 0.." + callsProperty.arraySize + ".";
                }

                if (parsedIndex == callsProperty.arraySize)
                {
                    callsProperty.InsertArrayElementAtIndex(parsedIndex);
                }

                callIndex = parsedIndex;
                return null;
            }

            return "Unknown UnityEvent set operation '" + token + "'. Use replace, append, or a numeric call index.";
        }

        /// <summary>
        /// Writes a string-mode persistent call into an existing UnityEvent call slot.
        /// </summary>
        /// <param name="callProperty">The persistent-call property to populate.</param>
        /// <param name="listenerTarget">The target object that receives the method call.</param>
        /// <param name="methodName">The target method name.</param>
        /// <param name="stringArgument">The string argument passed to the listener.</param>
        /// <param name="callState">The persistent-call runtime state.</param>
        /// <returns>An error string when the serialized call shape is invalid, otherwise null.</returns>
        private static string SetUnityEventStringCall(
            SerializedProperty callProperty,
            UnityEngine.Object listenerTarget,
            string methodName,
            string stringArgument,
            UnityEventCallState callState)
        {
            var targetProperty = callProperty.FindPropertyRelative("m_Target");
            var targetAssemblyProperty = callProperty.FindPropertyRelative("m_TargetAssemblyTypeName");
            var methodProperty = callProperty.FindPropertyRelative("m_MethodName");
            var modeProperty = callProperty.FindPropertyRelative("m_Mode");
            var callStateProperty = callProperty.FindPropertyRelative("m_CallState");
            var objectArgumentProperty = callProperty.FindPropertyRelative("m_Arguments.m_ObjectArgument");
            var objectArgumentTypeProperty = callProperty.FindPropertyRelative("m_Arguments.m_ObjectArgumentAssemblyTypeName");
            var intArgumentProperty = callProperty.FindPropertyRelative("m_Arguments.m_IntArgument");
            var floatArgumentProperty = callProperty.FindPropertyRelative("m_Arguments.m_FloatArgument");
            var stringArgumentProperty = callProperty.FindPropertyRelative("m_Arguments.m_StringArgument");
            var boolArgumentProperty = callProperty.FindPropertyRelative("m_Arguments.m_BoolArgument");

            if (targetProperty == null || targetAssemblyProperty == null || methodProperty == null ||
                modeProperty == null || callStateProperty == null || stringArgumentProperty == null)
            {
                return "UnityEvent persistent-call serialized shape was not recognised. Nothing was applied.";
            }

            targetProperty.objectReferenceValue = listenerTarget;
            targetAssemblyProperty.stringValue = listenerTarget.GetType().AssemblyQualifiedName;
            methodProperty.stringValue = methodName;
            modeProperty.enumValueIndex = (int)PersistentListenerMode.String;
            callStateProperty.enumValueIndex = (int)callState;

            if (objectArgumentProperty != null) objectArgumentProperty.objectReferenceValue = null;
            if (objectArgumentTypeProperty != null) objectArgumentTypeProperty.stringValue = "UnityEngine.Object, UnityEngine";
            if (intArgumentProperty != null) intArgumentProperty.intValue = 0;
            if (floatArgumentProperty != null) floatArgumentProperty.floatValue = 0f;
            stringArgumentProperty.stringValue = stringArgument;
            if (boolArgumentProperty != null) boolArgumentProperty.boolValue = false;

            return null;
        }

        #endregion
    }
}
