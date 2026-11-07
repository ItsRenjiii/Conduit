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
    /// Reading serialized state back as JSON, including EditorJsonUtility reference correction.
    /// </summary>
    internal static partial class ConduitBridge
    {

        /// <summary>
        /// Rewrites object-reference fields in EditorJsonUtility output with live SerializedProperty data so
        /// the legacy serialized blob does not report valid scene/component references as null.
        /// </summary>
        /// <param name="target">The object whose raw editor JSON was generated.</param>
        /// <param name="editorJson">The raw EditorJsonUtility JSON.</param>
        /// <returns>Editor JSON with top-level object-reference and UnityEvent fields corrected from SerializedProperty.</returns>
        private static string CorrectEditorJsonObjectReferences(UnityEngine.Object target, string editorJson)
        {
            if (target == null || string.IsNullOrEmpty(editorJson))
            {
                return editorJson;
            }

            var correctedJson = editorJson;
            var serializedObject = new SerializedObject(target);

            foreach (var property in TopLevelReportableProperties(serializedObject))
            {
                if (property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    correctedJson = ReplaceSerializedJsonProperty(
                        correctedJson,
                        property.propertyPath,
                        EditorJsonObjectReference(property));
                    continue;
                }

                // An ARRAY of object references is lossy the same way, and worse: EditorJsonUtility renders
                // EVERY element as instanceID 0, so a fully wired array is indistinguishable from an empty
                // one. That reading has already sent someone re-wiring references that were already correct.
                if (IsObjectReferenceArray(property))
                {
                    correctedJson = ReplaceSerializedJsonProperty(
                        correctedJson,
                        property.propertyPath,
                        EditorJsonObjectReferenceArray(property));
                }

                SerializedProperty callsProperty;
                if (!TryGetUnityEventCallsProperty(property, out callsProperty))
                {
                    continue;
                }

                correctedJson = ReplaceSerializedJsonProperty(
                    correctedJson,
                    property.propertyPath,
                    EditorJsonUnityEvent(property, callsProperty));
            }

            return correctedJson;
        }

        /// <summary>
        /// Replaces one named property value in a compact EditorJsonUtility object while preserving the
        /// surrounding JSON text.
        /// </summary>
        /// <param name="json">The JSON document to patch.</param>
        /// <param name="propertyName">The serialized top-level property name.</param>
        /// <param name="replacementJson">The replacement JSON value.</param>
        /// <returns>The patched JSON, or the original JSON when the property cannot be found safely.</returns>
        private static string ReplaceSerializedJsonProperty(string json, string propertyName, string replacementJson)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName) || string.IsNullOrEmpty(replacementJson))
            {
                return json;
            }

            var marker = JsonQuote(propertyName) + ":";
            var markerIndex = json.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0)
            {
                return json;
            }

            var valueStart = markerIndex + marker.Length;
            while (valueStart < json.Length && char.IsWhiteSpace(json[valueStart]))
            {
                valueStart++;
            }

            var valueEnd = valueStart;
            if (!SkipJsonValue(json, ref valueEnd, json.Length))
            {
                return json;
            }

            return json.Substring(0, valueStart) + replacementJson + json.Substring(valueEnd);
        }

        /// <summary>
        /// Builds an EditorJsonUtility-shaped UnityEvent value from live SerializedProperty persistent calls.
        /// </summary>
        /// <param name="eventProperty">The UnityEvent field being encoded.</param>
        /// <param name="callsProperty">The persistent call array for the event.</param>
        /// <returns>A JSON value matching UnityEvent's editor JSON shape.</returns>
        private static string EditorJsonUnityEvent(SerializedProperty eventProperty, SerializedProperty callsProperty)
        {
            return "{\"m_PersistentCalls\":{\"m_Calls\":" + EditorJsonUnityEventCalls(callsProperty) + "}}";
        }

        /// <summary>
        /// Builds an EditorJsonUtility-shaped persistent-call array from live SerializedProperty data.
        /// </summary>
        /// <param name="callsProperty">The persistent call array.</param>
        /// <returns>A JSON array of UnityEvent persistent calls.</returns>
        private static string EditorJsonUnityEventCalls(SerializedProperty callsProperty)
        {
            var builder = new StringBuilder();
            builder.Append('[');

            var count = callsProperty == null ? 0 : callsProperty.arraySize;
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(EditorJsonUnityEventCall(callsProperty.GetArrayElementAtIndex(i)));
            }

            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// Builds one EditorJsonUtility-shaped persistent-call object with corrected object-reference instance ids.
        /// </summary>
        /// <param name="callProperty">The persistent call property to encode.</param>
        /// <returns>A JSON object matching UnityEvent's editor JSON call shape.</returns>
        private static string EditorJsonUnityEventCall(SerializedProperty callProperty)
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

            return "{\"m_Target\":" + EditorJsonObjectReference(targetProperty) +
                ",\"m_TargetAssemblyTypeName\":" + JsonQuote(targetAssemblyProperty == null ? string.Empty : targetAssemblyProperty.stringValue) +
                ",\"m_MethodName\":" + JsonQuote(methodProperty == null ? string.Empty : methodProperty.stringValue) +
                ",\"m_Mode\":" + (modeProperty == null ? "0" : modeProperty.enumValueIndex.ToString(CultureInfo.InvariantCulture)) +
                ",\"m_Arguments\":{\"m_ObjectArgument\":" + EditorJsonObjectReference(objectArgumentProperty) +
                ",\"m_ObjectArgumentAssemblyTypeName\":" + JsonQuote(objectArgumentTypeProperty == null ? "UnityEngine.Object, UnityEngine" : objectArgumentTypeProperty.stringValue) +
                ",\"m_IntArgument\":" + (intArgumentProperty == null ? "0" : intArgumentProperty.intValue.ToString(CultureInfo.InvariantCulture)) +
                ",\"m_FloatArgument\":" + (floatArgumentProperty == null ? "0.0" : JsonNumber(floatArgumentProperty.floatValue)) +
                ",\"m_StringArgument\":" + JsonQuote(stringArgumentProperty == null ? string.Empty : stringArgumentProperty.stringValue) +
                ",\"m_BoolArgument\":" + (boolArgumentProperty != null && boolArgumentProperty.boolValue ? "true" : "false") + "}" +
                ",\"m_CallState\":" + (callStateProperty == null ? "0" : callStateProperty.enumValueIndex.ToString(CultureInfo.InvariantCulture)) + "}";
        }

        /// <summary>
        /// Encodes an object-reference property in EditorJsonUtility's instance-id-only object shape.
        /// </summary>
        /// <summary>True when the property is an array whose elements are object references.</summary>
        /// <remarks>
        /// Judged from element ZERO, so an EMPTY array returns false and is left exactly as
        /// EditorJsonUtility rendered it. There is nothing to correct in an empty array anyway, and
        /// guessing at its element kind risks rewriting an array of VALUES as references.
        /// </remarks>
        /// <param name="property">The property to test.</param>
        /// <returns><c>true</c> when patching this array is both possible and needed.</returns>
        private static bool IsObjectReferenceArray(SerializedProperty property)
        {
            if (property == null || !property.isArray || property.propertyType == SerializedPropertyType.String)
            {
                return false;
            }

            return property.arraySize > 0 &&
                property.GetArrayElementAtIndex(0).propertyType == SerializedPropertyType.ObjectReference;
        }

        /// <summary>
        /// Builds an EditorJsonUtility-shaped array value from live object-reference elements.
        /// </summary>
        /// <param name="property">The array property to encode.</param>
        /// <returns>A JSON array of <c>instanceID</c> objects.</returns>
        private static string EditorJsonObjectReferenceArray(SerializedProperty property)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var i = 0; i < property.arraySize; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(EditorJsonObjectReference(property.GetArrayElementAtIndex(i)));
            }

            builder.Append(']');
            return builder.ToString();
        }

        /// <param name="property">The object-reference property to encode.</param>
        /// <returns>A JSON object with an <c>instanceID</c> field.</returns>
        private static string EditorJsonObjectReference(SerializedProperty property)
        {
            if (property == null)
            {
                return "{\"instanceID\":0}";
            }

            if (property.objectReferenceValue != null)
            {
                return "{\"instanceID\":" + property.objectReferenceValue.GetInstanceID() + "}";
            }

            return "{\"instanceID\":" + property.objectReferenceInstanceIDValue + "}";
        }

        /// <summary>
        /// Serialized names Unity puts on every object, which carry no authoring meaning for a reader.
        /// </summary>
        /// <param name="propertyPath">Top-level property path to test.</param>
        /// <returns>True when the property is engine bookkeeping rather than authored data.</returns>
        private static bool IsEngineHousekeepingProperty(string propertyPath)
        {
            return propertyPath == "m_GameObject"
                || propertyPath == "m_CorrespondingSourceObject"
                || propertyPath == "m_PrefabInstance"
                || propertyPath == "m_PrefabAsset"
                || propertyPath == "m_ObjectHideFlags";
        }

        /// <summary>
        /// Collects the top-level serialized properties a reader should report: everything the inspector
        /// shows, PLUS any object reference Unity hides from it.
        /// <para>
        /// Walking with `NextVisible` alone is NOT enough. It honours inspector visibility, and a
        /// component whose editor draws a field by hand marks that field hidden - `Joint.m_ConnectedBody`
        /// is the case that proved it, absent from the visible walk's 18 properties and present in the
        /// full walk's 25. Dropping it meant every joint read reported EditorJsonUtility's lossy
        /// `instanceID 0`, so a hinge wired to an anchor was indistinguishable from an unwired one and a
        /// real scene fault got "ruled out" against a value the bridge had invented.
        /// </para>
        /// </summary>
        /// <param name="serializedObject">The object to walk.</param>
        /// <returns>Top-level properties in inspector order, with hidden object references appended.</returns>
        private static List<SerializedProperty> TopLevelReportableProperties(SerializedObject serializedObject)
        {
            var reportable = new List<SerializedProperty>();
            var emitted = new HashSet<string>();

            var visible = serializedObject.GetIterator();
            if (visible.NextVisible(true))
            {
                do
                {
                    if (visible.depth != 0) continue;
                    if (emitted.Add(visible.propertyPath)) reportable.Add(visible.Copy());
                }
                while (visible.NextVisible(false));
            }

            // Second pass for what visibility hid. Restricted to object references because those are the
            // ones EditorJsonUtility reports wrongly rather than merely omitting, and skipping the engine
            // bookkeeping keeps every ordinary component's output byte-identical to before.
            var all = serializedObject.GetIterator();
            if (all.Next(true))
            {
                do
                {
                    if (all.depth != 0) continue;
                    if (all.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (IsEngineHousekeepingProperty(all.propertyPath)) continue;
                    if (emitted.Add(all.propertyPath)) reportable.Add(all.Copy());
                }
                while (all.Next(false));
            }

            return reportable;
        }

        /// <summary>
        /// Encodes Unity's SerializedObject view so scene-local references survive component readback.
        /// </summary>
        /// <param name="target">The Unity object to inspect through SerializedObject.</param>
        /// <returns>A compact JSON object keyed by serialized property path.</returns>
        private static string JsonSerializedProperties(UnityEngine.Object target)
        {
            if (target == null)
            {
                return "{}";
            }

            var builder = new StringBuilder();
            var first = true;

            builder.Append('{');
            foreach (var property in TopLevelReportableProperties(new SerializedObject(target)))
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(JsonQuote(property.propertyPath))
                    .Append(':')
                    .Append(JsonSerializedPropertyValue(property));
            }

            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>
        /// Encodes a single SerializedProperty value with object references read from Unity's live reference API.
        /// </summary>
        /// <param name="property">The property to encode.</param>
        /// <returns>A JSON value describing the serialized property.</returns>
        private static string JsonSerializedPropertyValue(SerializedProperty property)
        {
            if (property == null)
            {
                return "null";
            }

            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                return JsonSerializedPropertyArray(property);
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    return property.boolValue ? "true" : "false";

                case SerializedPropertyType.Integer:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);

                case SerializedPropertyType.Float:
                    return JsonNumber(property.floatValue);

                case SerializedPropertyType.String:
                    return JsonQuote(property.stringValue);

                case SerializedPropertyType.Enum:
                    return "{\"index\":" + property.enumValueIndex +
                        ",\"name\":" + JsonQuote(property.enumDisplayNames[property.enumValueIndex]) + "}";

                case SerializedPropertyType.ObjectReference:
                    return JsonSerializedObjectReference(property);

                case SerializedPropertyType.Color:
                    return "{\"r\":" + JsonNumber(property.colorValue.r) +
                        ",\"g\":" + JsonNumber(property.colorValue.g) +
                        ",\"b\":" + JsonNumber(property.colorValue.b) +
                        ",\"a\":" + JsonNumber(property.colorValue.a) + "}";

                case SerializedPropertyType.Vector2:
                    return "{\"x\":" + JsonNumber(property.vector2Value.x) +
                        ",\"y\":" + JsonNumber(property.vector2Value.y) + "}";

                case SerializedPropertyType.Vector3:
                    return JsonVector3(property.vector3Value);

                case SerializedPropertyType.Rect:
                    return "{\"x\":" + JsonNumber(property.rectValue.x) +
                        ",\"y\":" + JsonNumber(property.rectValue.y) +
                        ",\"width\":" + JsonNumber(property.rectValue.width) +
                        ",\"height\":" + JsonNumber(property.rectValue.height) + "}";

                case SerializedPropertyType.AnimationCurve:
                    return JsonAnimationCurve(property.animationCurveValue);

                default:
                    return "{\"propertyType\":" + JsonQuote(property.propertyType.ToString()) +
                        ",\"type\":" + JsonQuote(property.type) + "}";
            }
        }

        /// <summary>
        /// Renders an AnimationCurve as the same keyframe array the modify commands accept, so a curve read
        /// back can be written straight back. The old fallback emitted only the property KIND, which told a
        /// caller a curve was there and nothing whatsoever about its shape.
        /// </summary>
        /// <param name="curve">Curve to render.</param>
        /// <returns>A JSON array of keyframes.</returns>
        private static string JsonAnimationCurve(AnimationCurve curve)
        {
            if (curve == null) return "[]";

            var builder = new StringBuilder("[");
            var keys = curve.keys;
            for (var i = 0; i < keys.Length; i++)
            {
                if (i > 0) builder.Append(",");
                builder.Append("{\"time\":").Append(JsonNumber(keys[i].time))
                    .Append(",\"value\":").Append(JsonNumber(keys[i].value))
                    .Append(",\"inTangent\":").Append(JsonNumber(keys[i].inTangent))
                    .Append(",\"outTangent\":").Append(JsonNumber(keys[i].outTangent))
                    .Append("}");
            }

            return builder.Append("]").ToString();
        }

        /// <summary>
        /// Encodes array properties without expanding nested child iterator paths.
        /// </summary>
        /// <param name="property">The array property to encode.</param>
        /// <returns>A JSON array of serialized property values.</returns>
        private static string JsonSerializedPropertyArray(SerializedProperty property)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var count = Math.Min(property.arraySize, DefaultJsonResultLimit);
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonSerializedPropertyValue(property.GetArrayElementAtIndex(i)));
            }

            // Match the codebase's DefaultJsonResultLimit convention - cap fat arrays
            // so a huge serialized list can't flood the response. Signal truncation
            // explicitly rather than silently dropping elements.
            if (property.arraySize > count)
            {
                builder.Append(",{\"truncated\":true,\"totalCount\":")
                    .Append(property.arraySize)
                    .Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// Encodes Unity object references, preserving broken or unresolved instance IDs for diagnostics.
        /// </summary>
        /// <param name="property">The object reference property to encode.</param>
        /// <returns>A JSON object reference or null.</returns>
        private static string JsonSerializedObjectReference(SerializedProperty property)
        {
            if (property.objectReferenceValue != null)
            {
                return JsonObjectRef(property.objectReferenceValue);
            }

            if (property.objectReferenceInstanceIDValue != 0)
            {
                return "{\"instanceID\":" + property.objectReferenceInstanceIDValue + ",\"missing\":true}";
            }

            return "null";
        }
    }
}
