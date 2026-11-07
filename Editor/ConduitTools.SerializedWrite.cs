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
    /// Writing serialized values: flat JSON parsing, typed property staging, and per-field change evidence.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Flat JSON payload parsing

        /// <summary>
        /// Splits a flat <c>{"field": value, ...}</c> JSON object into its top-level key/raw-value pairs, leaving
        /// each value as its original JSON token (string, number, object, or array). Returns false for anything
        /// that is not a flat object literal (already wrapped, empty, or malformed) so the caller can fall back to
        /// the raw overwrite path. This is a deliberately small scanner for the modify payload shape, not a
        /// general JSON parser.
        /// </summary>
        /// <param name="json">The caller-supplied payload.</param>
        /// <param name="fields">The parsed key/raw-value pairs when the method returns true.</param>
        /// <returns>True when the payload is a flat object that was split, otherwise false.</returns>
        private static bool TrySplitFlatJsonObject(string json, out List<KeyValuePair<string, string>> fields)
        {
            fields = new List<KeyValuePair<string, string>>();

            var text = (json ?? string.Empty).Trim();
            if (text.Length < 2 || text[0] != '{' || text[text.Length - 1] != '}')
            {
                return false;
            }

            var end = text.Length - 1;
            var i = 1;
            while (i < end)
            {
                while (i < end && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
                if (i >= end) break;
                if (text[i] != '"') return false;

                i++;
                var key = new StringBuilder();
                while (i < end && text[i] != '"')
                {
                    // Both characters are kept so the escape reaches the decoder below intact; consuming
                    // only the second turns an escaped key's \t into a bare 't'.
                    if (text[i] == '\\' && i + 1 < end) { key.Append(text[i]).Append(text[i + 1]); i += 2; continue; }
                    key.Append(text[i]); i++;
                }

                if (i >= end) return false;
                i++;

                while (i < end && char.IsWhiteSpace(text[i])) i++;
                if (i >= end || text[i] != ':') return false;
                i++;
                while (i < end && char.IsWhiteSpace(text[i])) i++;

                var valueStart = i;
                if (!SkipJsonValue(text, ref i, end)) return false;
                fields.Add(new KeyValuePair<string, string>(DecodeJsonEscapes(key.ToString()), text.Substring(valueStart, i - valueStart).Trim()));
            }

            return true;
        }

        /// <summary>
        /// Advances <paramref name="i"/> past a single JSON value (string, object, array, or primitive),
        /// respecting nested braces, brackets, and quoted strings so the value token is captured whole.
        /// </summary>
        /// <param name="text">The JSON text.</param>
        /// <param name="i">Cursor positioned at the value start; left just past the value on success.</param>
        /// <param name="end">Exclusive upper bound (the object's closing brace index).</param>
        /// <returns>True when a value was consumed, false on a malformed token.</returns>
        private static bool SkipJsonValue(string text, ref int i, int end)
        {
            if (i >= end) return false;

            var c = text[i];
            if (c == '"')
            {
                i++;
                while (i < end && text[i] != '"')
                {
                    if (text[i] == '\\') i += 2; else i++;
                }

                if (i >= end) return false;
                i++;
                return true;
            }

            if (c == '{' || c == '[')
            {
                var open = c;
                var close = c == '{' ? '}' : ']';
                var depth = 0;
                while (i < end)
                {
                    var ch = text[i];
                    if (ch == '"')
                    {
                        i++;
                        while (i < end && text[i] != '"') { if (text[i] == '\\') i += 2; else i++; }
                        if (i >= end) return false;
                        i++;
                        continue;
                    }

                    if (ch == open) { depth++; }
                    else if (ch == close) { depth--; i++; if (depth == 0) return true; continue; }
                    i++;
                }

                return false;
            }

            while (i < end && text[i] != ',' && text[i] != '}' && text[i] != ']') i++;
            return true;
        }

        /// <summary>
        /// Rebuilds a flat JSON object from key/raw-value pairs whose values are already valid JSON tokens.
        /// </summary>
        /// <param name="fields">The fields to encode.</param>
        /// <returns>A <c>{"field": value, ...}</c> JSON object string.</returns>
        private static string BuildFlatJsonObject(List<KeyValuePair<string, string>> fields)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            for (var i = 0; i < fields.Count; i++)
            {
                if (i > 0) builder.Append(',');
                builder.Append(JsonQuote(fields[i].Key)).Append(':').Append(fields[i].Value);
            }

            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>
        /// Peels one layer of outer quotes (with inner quote/backslash escapes) off a command section when the
        /// unwrapped text is a JSON object or array. A stale MCP server quoted JSON payloads in transit, which
        /// silently degraded every modify into a no-op raw overwrite; this keeps such servers working while the
        /// verbatim transport fix rolls out. Non-quoted sections and quoted plain strings pass through untouched.
        /// </summary>
        /// <param name="section">The raw command section.</param>
        /// <returns>The JSON payload with any transport quoting removed.</returns>
        private static string UnwrapQuotedJsonSection(string section)
        {
            var text = (section ?? string.Empty).Trim();
            if (text.Length < 4 || text[0] != '"' || text[text.Length - 1] != '"')
            {
                return text;
            }

            var unwrapped = Unquote(text);
            if (unwrapped.Length >= 2 &&
                ((unwrapped[0] == '{' && unwrapped[unwrapped.Length - 1] == '}') ||
                 (unwrapped[0] == '[' && unwrapped[unwrapped.Length - 1] == ']')))
            {
                return unwrapped;
            }

            return text;
        }

        /// <summary>
        /// Strips surrounding double quotes from a JSON string token and unescapes the quote/backslash escapes.
        /// </summary>
        /// <param name="value">The raw token.</param>
        /// <returns>The unquoted string, or the input unchanged when it was not a quoted token.</returns>
        private static string Unquote(string value)
        {
            var trimmed = (value ?? string.Empty).Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[trimmed.Length - 1] == '"')
            {
                return DecodeJsonEscapes(trimmed.Substring(1, trimmed.Length - 2));
            }

            return trimmed;
        }

        /// <summary>
        /// Decodes the standard JSON string escapes inside an already-unquoted string body.
        ///
        /// A payload arrives as text, so an escape in it is two characters until something turns them into
        /// one. Storing them as they arrive puts a backslash and a 't' into the field, and the response then
        /// prints exactly what it stored, so the write reads as a success while the tab the caller asked for
        /// never existed. Decoding in one pass also fixes an ordering fault the chained Replace calls had:
        /// an escaped backslash followed by a quote decoded in the wrong order and lost the quote.
        ///
        /// An unrecognised escape is left exactly as written rather than guessed at, so a Windows path
        /// carried in a string cannot be quietly mangled by this.
        /// </summary>
        /// <param name="body">String contents with the surrounding quotes already removed.</param>
        /// <returns>The decoded text.</returns>
        private static string DecodeJsonEscapes(string body)
        {
            if (string.IsNullOrEmpty(body) || body.IndexOf('\\') < 0) return body;

            var decoded = new StringBuilder(body.Length);
            for (var i = 0; i < body.Length; i++)
            {
                if (body[i] != '\\' || i + 1 >= body.Length) { decoded.Append(body[i]); continue; }

                switch (body[i + 1])
                {
                    case '"': decoded.Append('"'); i++; break;
                    case '\\': decoded.Append('\\'); i++; break;
                    case '/': decoded.Append('/'); i++; break;
                    case 'b': decoded.Append('\b'); i++; break;
                    case 'f': decoded.Append('\f'); i++; break;
                    case 'n': decoded.Append('\n'); i++; break;
                    case 'r': decoded.Append('\r'); i++; break;
                    case 't': decoded.Append('\t'); i++; break;
                    case 'u':
                        int code;
                        if (i + 5 < body.Length && int.TryParse(body.Substring(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                        {
                            decoded.Append((char)code);
                            i += 5;
                        }
                        else
                        {
                            decoded.Append(body[i]);
                        }

                        break;
                    default: decoded.Append(body[i]); break;
                }
            }

            return decoded.ToString();
        }

        /// <summary>
        /// Reads an integer value for a key from a small flat JSON object such as <c>{"instanceID":-123}</c>.
        /// </summary>
        /// <param name="objectJson">The JSON object text.</param>
        /// <param name="key">The key to read.</param>
        /// <param name="value">The parsed integer when found.</param>
        /// <returns>True when the key held an integer.</returns>
        private static bool TryReadJsonInt(string objectJson, string key, out int value)
        {
            value = 0;

            var marker = "\"" + key + "\"";
            var k = objectJson.IndexOf(marker, StringComparison.Ordinal);
            if (k < 0) return false;

            var colon = objectJson.IndexOf(':', k + marker.Length);
            if (colon < 0) return false;

            var i = colon + 1;
            while (i < objectJson.Length && char.IsWhiteSpace(objectJson[i])) i++;

            var start = i;
            if (i < objectJson.Length && (objectJson[i] == '-' || objectJson[i] == '+')) i++;
            while (i < objectJson.Length && char.IsDigit(objectJson[i])) i++;
            if (i == start) return false;

            return int.TryParse(objectJson.Substring(start, i - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Reads a string value for a key from a small flat JSON object such as <c>{"guid":"abc123"}</c>.
        /// </summary>
        /// <param name="objectJson">The JSON object text.</param>
        /// <param name="key">The key to read.</param>
        /// <param name="value">The parsed string when found.</param>
        /// <returns>True when the key held a string.</returns>
        private static bool TryReadJsonString(string objectJson, string key, out string value)
        {
            value = null;

            var marker = "\"" + key + "\"";
            var k = objectJson.IndexOf(marker, StringComparison.Ordinal);
            if (k < 0) return false;

            var colon = objectJson.IndexOf(':', k + marker.Length);
            if (colon < 0) return false;

            var i = colon + 1;
            while (i < objectJson.Length && char.IsWhiteSpace(objectJson[i])) i++;
            if (i >= objectJson.Length || objectJson[i] != '"') return false;
            i++;

            var builder = new StringBuilder();
            while (i < objectJson.Length && objectJson[i] != '"')
            {
                if (objectJson[i] == '\\' && i + 1 < objectJson.Length) { builder.Append(objectJson[i + 1]); i += 2; continue; }
                builder.Append(objectJson[i]); i++;
            }

            value = builder.ToString();
            return true;
        }

        /// <summary>
        /// Reads a float member out of a small flat JSON object token, mirroring <see cref="TryReadJsonInt"/>
        /// for the vector and colour parsers.
        /// </summary>
        /// <param name="objectJson">The JSON object token.</param>
        /// <param name="key">The member name to read.</param>
        /// <param name="value">The parsed float on success.</param>
        /// <returns>True when the member exists and parses as a number.</returns>
        private static bool TryReadJsonFloat(string objectJson, string key, out float value)
        {
            value = 0f;

            var marker = "\"" + key + "\"";
            var k = objectJson.IndexOf(marker, StringComparison.Ordinal);
            if (k < 0) return false;

            var i = k + marker.Length;
            while (i < objectJson.Length && (char.IsWhiteSpace(objectJson[i]) || objectJson[i] == ':')) i++;

            var start = i;
            while (i < objectJson.Length && (char.IsDigit(objectJson[i]) || objectJson[i] == '-' || objectJson[i] == '+' || objectJson[i] == '.' || objectJson[i] == 'e' || objectJson[i] == 'E')) i++;

            return i > start && float.TryParse(objectJson.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        #endregion

        #region Staging and applying property writes

        /// <summary>
        /// One fully validated field write staged during the modify command's first pass: the property name and
        /// kind, plus the parsed value in the slot matching the kind, ready to apply without re-parsing.
        /// </summary>
        private sealed class StagedComponentFieldWrite
        {
            /// <summary>Serialized property name this write targets.</summary>
            public string Name;

            /// <summary>Serialized property kind, selecting which parsed slot below applies.</summary>
            public SerializedPropertyType Kind;

            /// <summary>Resolved object for an ObjectReference property; null clears the field.</summary>
            public UnityEngine.Object Reference;

            /// <summary>Parsed value for a Boolean property.</summary>
            public bool BoolValue;

            /// <summary>Parsed value for an Integer, LayerMask, Character, or numeric Enum property.</summary>
            public long LongValue;

            /// <summary>Parsed value for a Float property.</summary>
            public double DoubleValue;

            /// <summary>Parsed value for a String property.</summary>
            public string StringValue;

            /// <summary>Parsed value for a Color property.</summary>
            public Color ColorValue;

            /// <summary>Parsed components for a Vector2/3/4 or Quaternion property (unused axes stay zero).</summary>
            public Vector4 VectorValue;

            /// <summary>Parsed curve for an AnimationCurve property.</summary>
            public AnimationCurve CurveValue;

            /// <summary>Enum-name index resolved against the property's enum names, or -1 when the value was numeric.</summary>
            public int EnumIndex = -1;

            /// <summary>True when this write targets an array of object references rather than a single value.</summary>
            public bool IsReferenceArray;

            /// <summary>Resolved elements for an object-reference array; entries may be null to clear a slot.</summary>
            public List<UnityEngine.Object> ReferenceArray;

            /// <summary>Parsed elements for an array of plain values, one staged write per element; null for any other write.</summary>
            public List<StagedComponentFieldWrite> ValueArray;
        }

        /// <summary>
        /// Parses a raw JSON token into the staged slot matching a property's kind. Returns true when the value
        /// was parsed; returns false with an error for a malformed value on a supported kind, and false with a
        /// null error for a kind that has no typed writer here (the caller decides the fallback per component).
        /// </summary>
        /// <param name="property">The property whose kind selects the parser.</param>
        /// <param name="rawValue">The raw JSON token supplied for the field.</param>
        /// <param name="stage">The staged write receiving the parsed value.</param>
        /// <param name="error">A parse error for a supported kind, otherwise null.</param>
        /// <returns>True when the value is staged and ready to apply.</returns>
        private static bool TryParseValueForProperty(SerializedProperty property, string rawValue, StagedComponentFieldWrite stage, out string error)
        {
            error = null;
            var value = (rawValue ?? string.Empty).Trim();

            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    if (value == "true" || value == "1") { stage.BoolValue = true; return true; }
                    if (value == "false" || value == "0") { stage.BoolValue = false; return true; }
                    error = "'" + value + "' is not a boolean (use true or false).";
                    return false;

                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                    {
                        stage.LongValue = longValue;
                        return true;
                    }

                    error = "'" + value + "' is not an integer.";
                    return false;

                case SerializedPropertyType.Float:
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
                    {
                        stage.DoubleValue = doubleValue;
                        return true;
                    }

                    error = "'" + value + "' is not a number.";
                    return false;

                case SerializedPropertyType.String:
                    stage.StringValue = Unquote(value);
                    return true;

                case SerializedPropertyType.Enum:
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var enumNumber))
                    {
                        stage.LongValue = enumNumber;
                        stage.EnumIndex = -1;
                        return true;
                    }

                    var enumName = Unquote(value);
                    var names = property.enumNames;
                    for (var i = 0; i < names.Length; i++)
                    {
                        if (string.Equals(names[i], enumName, StringComparison.OrdinalIgnoreCase))
                        {
                            stage.EnumIndex = i;
                            return true;
                        }
                    }

                    error = "'" + enumName + "' matches no enum value; valid: " + string.Join(", ", names) + ".";
                    return false;

                case SerializedPropertyType.Color:
                {
                    float r, g, b, a;
                    if (TryReadJsonFloat(value, "r", out r) && TryReadJsonFloat(value, "g", out g) && TryReadJsonFloat(value, "b", out b))
                    {
                        if (!TryReadJsonFloat(value, "a", out a)) a = 1f;
                        stage.ColorValue = new Color(r, g, b, a);
                        return true;
                    }

                    error = "a Color needs {\"r\": ..., \"g\": ..., \"b\": ...} (alpha optional).";
                    return false;
                }

                case SerializedPropertyType.AnimationCurve:
                {
                    AnimationCurve curve;
                    var curveError = ParseAnimationCurve(value, out curve);
                    if (curveError != null)
                    {
                        error = curveError;
                        return false;
                    }

                    stage.CurveValue = curve;
                    return true;
                }

                case SerializedPropertyType.Vector2:
                case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Quaternion:
                {
                    float x, y;
                    if (!TryReadJsonFloat(value, "x", out x) || !TryReadJsonFloat(value, "y", out y))
                    {
                        error = "a vector needs {\"x\": ..., \"y\": ...} plus z/w where the type has them.";
                        return false;
                    }

                    var z = 0f;
                    var w = 0f;
                    var needsZ = property.propertyType != SerializedPropertyType.Vector2;
                    var needsW = property.propertyType == SerializedPropertyType.Vector4 || property.propertyType == SerializedPropertyType.Quaternion;
                    if (needsZ && !TryReadJsonFloat(value, "z", out z))
                    {
                        error = "missing \"z\" for a " + property.propertyType + " value.";
                        return false;
                    }

                    if (needsW && !TryReadJsonFloat(value, "w", out w))
                    {
                        error = "missing \"w\" for a " + property.propertyType + " value.";
                        return false;
                    }

                    stage.VectorValue = new Vector4(x, y, z, w);
                    return true;
                }

                default:
                    return false;   // no typed writer for this kind; caller picks the fallback
            }
        }

        /// <summary>
        /// Applies a staged, already-validated value to its serialized property through the typed setter for
        /// its kind, so the write lands on managed and native components alike. An array of values is resized
        /// to the staged length and written element by element.
        /// </summary>
        /// <param name="property">The property to write.</param>
        /// <param name="stage">The staged write carrying the parsed value.</param>
        private static void ApplyStagedPropertyValue(SerializedProperty property, StagedComponentFieldWrite stage)
        {
            if (stage.ValueArray != null)
            {
                property.arraySize = stage.ValueArray.Count;
                for (var i = 0; i < stage.ValueArray.Count; i++)
                {
                    ApplyStagedPropertyValue(property.GetArrayElementAtIndex(i), stage.ValueArray[i]);
                }

                return;
            }

            if (stage.IsReferenceArray)
            {
                property.arraySize = stage.ReferenceArray.Count;
                for (var i = 0; i < stage.ReferenceArray.Count; i++)
                {
                    property.GetArrayElementAtIndex(i).objectReferenceValue = stage.ReferenceArray[i];
                }

                return;
            }

            switch (stage.Kind)
            {
                case SerializedPropertyType.ObjectReference: property.objectReferenceValue = stage.Reference; break;
                case SerializedPropertyType.Boolean: property.boolValue = stage.BoolValue; break;
                case SerializedPropertyType.Integer: property.longValue = stage.LongValue; break;
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character: property.intValue = (int)stage.LongValue; break;
                case SerializedPropertyType.Float: property.doubleValue = stage.DoubleValue; break;
                case SerializedPropertyType.String: property.stringValue = stage.StringValue; break;
                case SerializedPropertyType.Enum:
                    if (stage.EnumIndex >= 0) property.enumValueIndex = stage.EnumIndex;
                    else property.intValue = (int)stage.LongValue;
                    break;
                case SerializedPropertyType.Color: property.colorValue = stage.ColorValue; break;
                case SerializedPropertyType.Vector2: property.vector2Value = new Vector2(stage.VectorValue.x, stage.VectorValue.y); break;
                case SerializedPropertyType.Vector3: property.vector3Value = new Vector3(stage.VectorValue.x, stage.VectorValue.y, stage.VectorValue.z); break;
                case SerializedPropertyType.Vector4: property.vector4Value = stage.VectorValue; break;
                case SerializedPropertyType.Quaternion: property.quaternionValue = new Quaternion(stage.VectorValue.x, stage.VectorValue.y, stage.VectorValue.z, stage.VectorValue.w); break;
                case SerializedPropertyType.AnimationCurve: property.animationCurveValue = stage.CurveValue; break;
            }
        }

        /// <summary>
        /// Stages a JSON array onto an array of plain values - numbers, booleans, strings, enums, colours,
        /// vectors, curves - as one typed write per element, so the array is written field by field and
        /// reported with its real values like any other field.
        /// </summary>
        /// <remarks>
        /// Each element parses against a scratch copy of the target resized to the payload's length, which is
        /// what gives an array that is empty now its element kind; the copy is never applied, so nothing reaches
        /// the target before the write. Elements that are structs, classes or object references have no typed
        /// writer here, so the method stages nothing and leaves the caller's fallback to decide.
        /// </remarks>
        /// <param name="target">The object the array belongs to.</param>
        /// <param name="property">The array property itself.</param>
        /// <param name="rawValue">The raw JSON token supplied for the array.</param>
        /// <param name="stage">The staged write receiving the parsed elements.</param>
        /// <param name="error">Why the value cannot be staged, or null when the element kind has no typed writer.</param>
        /// <returns>True when every element is staged and ready to apply.</returns>
        private static bool TryStageValueArrayWrite(
            UnityEngine.Object target,
            SerializedProperty property,
            string rawValue,
            StagedComponentFieldWrite stage,
            out string error)
        {
            error = null;
            if (property.isFixedBuffer) return false;

            List<string> items;
            if (!TrySplitJsonArray(rawValue, out items))
            {
                error = "'" + property.propertyPath + "' is an array, so it takes a JSON array of its elements, e.g. [1, 2].";
                return false;
            }

            var elements = new List<StagedComponentFieldWrite>(items.Count);
            using (var scratch = new SerializedObject(target))
            {
                var scratchArray = scratch.FindProperty(property.propertyPath);
                if (scratchArray == null) return false;

                scratchArray.arraySize = items.Count;
                for (var i = 0; i < items.Count; i++)
                {
                    var element = scratchArray.GetArrayElementAtIndex(i);
                    if (element.propertyType == SerializedPropertyType.ObjectReference ||
                        element.propertyType == SerializedPropertyType.Generic)
                    {
                        return false;
                    }

                    var elementStage = new StagedComponentFieldWrite { Name = element.propertyPath, Kind = element.propertyType };
                    string elementError;
                    if (!TryParseValueForProperty(element, items[i], elementStage, out elementError))
                    {
                        if (elementError != null) error = "element " + i + ": " + elementError;
                        return false;
                    }

                    elements.Add(elementStage);
                }
            }

            stage.ValueArray = elements;
            return true;
        }

        /// <summary>
        /// Expands a nested-struct payload into one staged write per leaf field, e.g.
        /// <c>{"m_Limits":{"min":0,"max":105}}</c> into writes of <c>m_Limits.min</c> and <c>m_Limits.max</c>.
        /// </summary>
        /// <remarks>
        /// <see cref="SerializedObject.FindProperty"/> resolves a dotted path, so the leaves reach the same
        /// typed writers and the same per-field evidence as any top-level field. Staging them individually is
        /// what keeps a plain sibling in the same payload alive: the previous behaviour returned an error for
        /// the struct and abandoned the whole call, so a bool sent beside it silently never landed.
        /// </remarks>
        /// <param name="serializedObject">The component's serialized object.</param>
        /// <param name="entry">Payload entry naming the struct, whose value is the nested JSON object.</param>
        /// <param name="property">The Generic property itself, used for the error text.</param>
        /// <param name="staged">Staged write list to append each resolved leaf to.</param>
        /// <param name="unknownFields">Receives leaf names that matched no serialized property.</param>
        /// <returns>An error string when a supplied leaf cannot be parsed, otherwise null.</returns>
        private static string StageNestedStructWrites(
            SerializedObject serializedObject,
            KeyValuePair<string, string> entry,
            SerializedProperty property,
            List<StagedComponentFieldWrite> staged,
            List<string> unknownFields)
        {
            List<KeyValuePair<string, string>> children;
            if (!TrySplitFlatJsonObject(entry.Value, out children) || children.Count == 0)
            {
                return "Field '" + entry.Key + "' is a " + property.propertyType + " property (a nested struct); " +
                    "send it as a JSON object of its leaf fields, e.g. {\"" + entry.Key + "\":{\"min\":0,\"max\":105}}. " +
                    "Nothing was applied.";
            }

            foreach (var child in children)
            {
                var childPath = entry.Key + "." + child.Key;
                var childProperty = serializedObject.FindProperty(childPath);
                if (childProperty == null)
                {
                    unknownFields.Add(childPath);
                    continue;
                }

                var childStage = new StagedComponentFieldWrite { Name = childPath, Kind = childProperty.propertyType };
                string parseError;
                if (!TryParseValueForProperty(childProperty, child.Value, childStage, out parseError))
                {
                    return "Failed to parse '" + childPath + "': " +
                        (parseError ?? "no typed writer for a " + childProperty.propertyType + " leaf.") +
                        " Nothing was applied.";
                }

                staged.Add(childStage);
            }

            return null;
        }

        /// <summary>
        /// Renders a property's value at FULL precision, for deciding changed versus already-at-value.
        /// </summary>
        /// <remarks>
        /// <see cref="DescribePropertyValue"/> exists to be READ, so it formats vectors through Unity's
        /// two-decimal ToString and summarises long arrays after a few elements. Deciding equality from that
        /// text reports a real write as already-at-value whenever the change is finer than the display or sits
        /// past the summary cut - and that is worse than saying nothing, because the caller either repeats a
        /// write that landed or abandons one that did. Equality is decided here on exact values; the display
        /// token is only ever printed.
        /// </remarks>
        /// <param name="property">The property to tokenise.</param>
        /// <returns>A token in which no two distinct values can collide.</returns>
        private static string PropertyValueToken(SerializedProperty property)
        {
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                // Every element, not a summary: a change past the display limit must not compare equal.
                var builder = new StringBuilder();
                builder.Append(property.arraySize).Append(':');
                for (var i = 0; i < property.arraySize; i++)
                {
                    builder.Append('|').Append(PropertyValueToken(property.GetArrayElementAtIndex(i)));
                }

                return builder.ToString();
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.Vector2:
                    var vector2 = property.vector2Value;
                    return FormatExactFloat(vector2.x) + "," + FormatExactFloat(vector2.y);
                case SerializedPropertyType.Vector3:
                    var vector3 = property.vector3Value;
                    return FormatExactFloat(vector3.x) + "," + FormatExactFloat(vector3.y) + "," + FormatExactFloat(vector3.z);
                case SerializedPropertyType.Vector4:
                    var vector4 = property.vector4Value;
                    return FormatExactFloat(vector4.x) + "," + FormatExactFloat(vector4.y) + "," + FormatExactFloat(vector4.z) + "," + FormatExactFloat(vector4.w);
                case SerializedPropertyType.Quaternion:
                    var quaternion = property.quaternionValue;
                    return FormatExactFloat(quaternion.x) + "," + FormatExactFloat(quaternion.y) + "," + FormatExactFloat(quaternion.z) + "," + FormatExactFloat(quaternion.w);
                case SerializedPropertyType.Color:
                    var colour = property.colorValue;
                    return FormatExactFloat(colour.r) + "," + FormatExactFloat(colour.g) + "," + FormatExactFloat(colour.b) + "," + FormatExactFloat(colour.a);
                case SerializedPropertyType.Boolean:
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.Float:
                case SerializedPropertyType.String:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.ObjectReference:
                case SerializedPropertyType.ExposedReference:
                case SerializedPropertyType.ArraySize:
                case SerializedPropertyType.FixedBufferSize:
                case SerializedPropertyType.Vector2Int:
                case SerializedPropertyType.Vector3Int:
                case SerializedPropertyType.RectInt:
                case SerializedPropertyType.BoundsInt:
                case SerializedPropertyType.Hash128:
                    // These describe at full precision, so the display token IS exact.
                    return DescribePropertyValue(property);
                default:
                    // Structs, managed references, curves, gradients, rects and bounds describe in summary or at
                    // display precision, so their equality is decided on Unity's hash of the whole serialized
                    // content instead.
                    return "#" + property.contentHash.ToString(CultureInfo.InvariantCulture);
            }
        }

        #endregion

        #region Object reference resolution

        /// <summary>
        /// Resolves the JSON value supplied for an object-reference field into a concrete UnityEngine.Object
        /// assignable to the field. Accepts the same shapes the read side emits - <c>{"instanceID": N}</c> for a
        /// live scene object/component and <c>{"guid": "..."}</c> for an asset - plus a quoted scene path / name /
        /// asset path, a bare instance id, and null/none/empty to clear the field.
        /// </summary>
        /// <param name="rawValue">The raw JSON token for the field's value.</param>
        /// <param name="fieldType">The type the resolved reference must satisfy.</param>
        /// <param name="reference">The resolved object, or null to clear.</param>
        /// <returns>An error string when resolution fails, otherwise null (including the clear case).</returns>
        private static string ResolveReferenceFromJsonValue(string rawValue, Type fieldType, out UnityEngine.Object reference)
        {
            reference = null;

            var value = (rawValue ?? string.Empty).Trim();
            if (value.Length == 0 || value.Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (value.StartsWith("{", StringComparison.Ordinal))
            {
                if (TryReadJsonInt(value, "instanceID", out var instanceId))
                {
                    if (instanceId == 0) return null;
                    var resolved = EditorUtility.InstanceIDToObject(instanceId);
                    if (resolved == null) return "instanceID " + instanceId + " does not resolve to a live object.";
                    return CoerceReference(resolved, fieldType, out reference);
                }

                if (TryReadJsonString(value, "guid", out var guid) && !string.IsNullOrEmpty(guid))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path)) return "asset guid " + guid + " does not resolve to a path.";
                    return ResolveObjectReference(path, fieldType, out reference);
                }

                return "object reference value " + value + " has no instanceID or guid to resolve.";
            }

            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                var spec = Unquote(value);
                if (spec.Length == 0 ||
                    spec.Equals("null", StringComparison.OrdinalIgnoreCase) ||
                    spec.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return ResolveObjectReference(spec, fieldType, out reference);
            }

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bareId))
            {
                if (bareId == 0) return null;
                var resolved = EditorUtility.InstanceIDToObject(bareId);
                if (resolved == null) return "instanceID " + bareId + " does not resolve to a live object.";
                return CoerceReference(resolved, fieldType, out reference);
            }

            return ResolveObjectReference(value, fieldType, out reference);
        }

        /// <summary>
        /// Coerces a resolved object to satisfy a reference field: passes assignable objects through, and pulls
        /// the requested Component off a GameObject (or a sibling component) when the field wants a Component.
        /// </summary>
        /// <param name="resolved">The object resolved from the value spec.</param>
        /// <param name="fieldType">The field type the reference must satisfy.</param>
        /// <param name="reference">The coerced reference on success.</param>
        /// <returns>An error string when the object cannot satisfy the field, otherwise null.</returns>
        private static string CoerceReference(UnityEngine.Object resolved, Type fieldType, out UnityEngine.Object reference)
        {
            reference = null;

            if (fieldType.IsInstanceOfType(resolved))
            {
                reference = resolved;
                return null;
            }

            var gameObject = resolved as GameObject;
            if (gameObject == null && resolved is Component sourceComponent)
            {
                gameObject = sourceComponent.gameObject;
            }

            if (gameObject == null)
            {
                return "resolved " + resolved.GetType().Name + " is not assignable to field type " + fieldType.Name + ".";
            }

            if (fieldType == typeof(GameObject))
            {
                reference = gameObject;
                return null;
            }

            if (typeof(Component).IsAssignableFrom(fieldType))
            {
                var matches = gameObject.GetComponents(fieldType);
                if (matches.Length == 0) return gameObject.name + " has no " + fieldType.Name + " component to assign.";
                if (matches.Length > 1) return "ambiguous: " + matches.Length + " " + fieldType.Name + " on " + gameObject.name + ".";
                reference = matches[0];
                return null;
            }

            return "resolved " + resolved.GetType().Name + " is not assignable to field type " + fieldType.Name + ".";
        }

        /// <summary>
        /// Determines whether a serialized property is an array whose elements are UnityEngine.Object references,
        /// and reports the element type so each entry can be coerced the same way a scalar reference field is.
        ///
        /// Reflection is authoritative when the C# field exists, because it answers correctly for an EMPTY array,
        /// where inspecting element zero cannot. Native <c>m_</c> arrays have no reflectable field, so those fall
        /// back to the live element kind and only work when the array already has an entry.
        /// </summary>
        /// <param name="owner">Component or asset that owns the property; only its TYPE is used.</param>
        /// <param name="fieldName">Serialized field name being written.</param>
        /// <param name="property">The resolved serialized property.</param>
        /// <param name="elementType">The element type to coerce references to, when this returns true.</param>
        /// <returns>True when the property is an array of object references.</returns>
        private static bool TryGetObjectReferenceArrayElementType(UnityEngine.Object owner, string fieldName, SerializedProperty property, out Type elementType)
        {
            elementType = null;
            if (property == null || !property.isArray || property.propertyType == SerializedPropertyType.String)
            {
                return false;
            }

            var field = FindSerializedField(owner.GetType(), fieldName);
            if (field != null)
            {
                var fieldType = field.FieldType;
                if (fieldType.IsArray)
                {
                    elementType = fieldType.GetElementType();
                }
                else if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition() == typeof(List<>))
                {
                    elementType = fieldType.GetGenericArguments()[0];
                }

                if (elementType != null && typeof(UnityEngine.Object).IsAssignableFrom(elementType))
                {
                    return true;
                }

                // The field is real and is not a reference array, so leave it to the existing value paths.
                elementType = null;
                return false;
            }

            if (property.arraySize > 0 && property.GetArrayElementAtIndex(0).propertyType == SerializedPropertyType.ObjectReference)
            {
                elementType = typeof(UnityEngine.Object);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resolves a JSON array of reference specs into concrete objects, accepting per element exactly what the
        /// scalar reference path accepts: <c>{"instanceID":N}</c>, <c>{"guid":"..."}</c>, a quoted scene path, name,
        /// or asset path, a bare instance id, and null or "none" to leave a slot empty.
        ///
        /// Resolves the WHOLE array before anything is written, so one bad element aborts the command with nothing
        /// applied rather than leaving the array half wired.
        /// </summary>
        /// <param name="rawValue">The raw JSON array token from the payload.</param>
        /// <param name="elementType">Type each element is coerced to.</param>
        /// <param name="references">The resolved elements when this returns null.</param>
        /// <returns>An error string naming the offending element index, or null on success.</returns>
        private static string ResolveReferenceArrayFromJsonValue(string rawValue, Type elementType, out List<UnityEngine.Object> references)
        {
            references = null;

            List<string> tokens;
            if (!TrySplitJsonArray(rawValue, out tokens))
            {
                var shown = (rawValue ?? string.Empty).Trim();
                if (shown.Length > 60) shown = shown.Substring(0, 60) + "...";
                return "expected a JSON array of references such as [{\"instanceID\":123}, \"Assets/Thing.prefab\", null], but got " + shown + ".";
            }

            var resolved = new List<UnityEngine.Object>(tokens.Count);
            for (var i = 0; i < tokens.Count; i++)
            {
                UnityEngine.Object reference;
                var error = ResolveReferenceFromJsonValue(tokens[i], elementType, out reference);
                if (error != null)
                {
                    return "element " + i + ": " + error;
                }

                resolved.Add(reference);
            }

            references = resolved;
            return null;
        }

        #endregion

        #region Change evidence

        /// <summary>
        /// One named field's value before a write and the value the write asked for, kept so the report judges
        /// the field from a fresh read once the write is committed rather than from the request.
        /// </summary>
        private sealed class FieldEvidence
        {
            /// <summary>Serialized property path of the field.</summary>
            public string Path;

            /// <summary>Exact value token before the write; null when the field did not exist.</summary>
            public string BeforeToken;

            /// <summary>Readable value before the write.</summary>
            public string BeforeDisplay;

            /// <summary>
            /// Exact token of the value asked for, worked out on a copy that is never applied; null when it cannot
            /// be known before the write, as for a field the JSON overwrite writes.
            /// </summary>
            public string RequestedToken;

            /// <summary>Readable form of the value asked for; null alongside a null token.</summary>
            public string RequestedDisplay;

            /// <summary>Whether the JSON overwrite writes the field rather than a typed writer.</summary>
            public bool ViaOverwrite;
        }

        /// <summary>One top-level property's content hash and readable value, the baseline for finding changes nobody named.</summary>
        private sealed class PropertyState
        {
            /// <summary>Unity's hash of the property's whole serialized content.</summary>
            public uint Hash;

            /// <summary>Readable value.</summary>
            public string Display;
        }

        /// <summary>
        /// Reads every field a write names before anything is written, and works out each typed write's asked-for
        /// value on a scratch copy of the target that is never applied, so the report can tell a value that was
        /// already there from one that did not land.
        /// </summary>
        /// <param name="target">The object about to be written.</param>
        /// <param name="staged">Typed writes, fully validated.</param>
        /// <param name="overwriteFields">Fields the JSON overwrite writes, or null.</param>
        /// <returns>One entry per named field, typed writes first.</returns>
        private static List<FieldEvidence> CaptureFieldEvidence(
            UnityEngine.Object target,
            List<StagedComponentFieldWrite> staged,
            List<KeyValuePair<string, string>> overwriteFields)
        {
            var evidence = new List<FieldEvidence>();
            using (var current = new SerializedObject(target))
            using (var scratch = new SerializedObject(target))
            {
                foreach (var stage in staged)
                {
                    var field = ReadFieldBefore(current, stage.Name, false);
                    var requested = scratch.FindProperty(stage.Name);
                    if (requested != null)
                    {
                        ApplyStagedPropertyValue(requested, stage);
                        field.RequestedToken = PropertyValueToken(requested);
                        field.RequestedDisplay = DescribePropertyValue(requested);
                    }

                    evidence.Add(field);
                }

                if (overwriteFields != null)
                {
                    foreach (var entry in overwriteFields)
                    {
                        evidence.Add(ReadFieldBefore(current, entry.Key, true));
                    }
                }
            }

            return evidence;
        }

        /// <summary>Reads one field's value as it stands, as the before half of its evidence.</summary>
        /// <param name="serializedObject">A serialized copy taken before the write.</param>
        /// <param name="path">The field's property path.</param>
        /// <param name="viaOverwrite">Whether the JSON overwrite writes it.</param>
        /// <returns>The field's evidence, without an asked-for value.</returns>
        private static FieldEvidence ReadFieldBefore(SerializedObject serializedObject, string path, bool viaOverwrite)
        {
            var property = serializedObject.FindProperty(path);
            return new FieldEvidence
            {
                Path = path,
                BeforeToken = property == null ? null : PropertyValueToken(property),
                BeforeDisplay = property == null ? "absent" : DescribePropertyValue(property),
                ViaOverwrite = viaOverwrite,
            };
        }

        /// <summary>
        /// Reads every named field again from a fresh serialized copy once the write is committed, and sorts each by
        /// what Unity now holds: changed (old -> new), already at the asked value, or not applied - still at its old
        /// value although something else was asked for. A changed field that does not hold the asked value says so,
        /// because something after the write altered it.
        /// </summary>
        /// <param name="target">The written object.</param>
        /// <param name="evidence">Each named field's value before the write.</param>
        /// <param name="setFields">Receives the fields that changed.</param>
        /// <param name="unchangedFields">Receives the fields already at the asked value.</param>
        /// <param name="notAppliedFields">Receives the fields still at their old value against the request.</param>
        private static void ClassifyCommittedFields(
            UnityEngine.Object target,
            List<FieldEvidence> evidence,
            List<string> setFields,
            List<string> unchangedFields,
            List<string> notAppliedFields)
        {
            using (var serializedObject = new SerializedObject(target))
            {
                foreach (var field in evidence)
                {
                    var property = serializedObject.FindProperty(field.Path);
                    var afterToken = property == null ? null : PropertyValueToken(property);
                    var afterDisplay = property == null ? "absent" : DescribePropertyValue(property);

                    if (afterToken == field.BeforeToken)
                    {
                        if (field.ViaOverwrite)
                        {
                            // The overwrite's asked-for value cannot be known before it runs, so an unchanged field
                            // there is either already at that value or out of the overwrite's reach.
                            unchangedFields.Add(field.Path + " (still " + afterDisplay +
                                " after the JSON overwrite: already that value, or out of the overwrite's reach)");
                        }
                        else if (field.RequestedToken == null || field.RequestedToken == afterToken)
                        {
                            unchangedFields.Add(field.Path + " (already " + afterDisplay + ")");
                        }
                        else
                        {
                            notAppliedFields.Add(field.Path + " (still " + afterDisplay + "; asked for " + field.RequestedDisplay + ")");
                        }

                        continue;
                    }

                    // The display can be identical for a change finer than it shows; the exact tokens say it there.
                    var shownBefore = field.BeforeDisplay == afterDisplay ? field.BeforeToken : field.BeforeDisplay;
                    var shownAfter = field.BeforeDisplay == afterDisplay ? afterToken : afterDisplay;
                    var line = field.Path + " = " + shownAfter + " (was " + shownBefore +
                        (field.ViaOverwrite ? ", via the JSON overwrite)" : ")");
                    if (field.RequestedToken != null && field.RequestedToken != afterToken)
                    {
                        line += ", not the asked " + field.RequestedDisplay + ": something after the write changed it";
                    }

                    setFields.Add(line);
                }
            }
        }

        /// <summary>
        /// Every top-level serialized property, those the inspector hides included, so a change nothing shows is
        /// still found. Each entry is a copy, valid while its serialized object is.
        /// </summary>
        /// <param name="serializedObject">The object to walk.</param>
        /// <returns>Top-level properties in serialization order.</returns>
        private static List<SerializedProperty> AllTopLevelProperties(SerializedObject serializedObject)
        {
            var properties = new List<SerializedProperty>();
            var iterator = serializedObject.GetIterator();
            if (!iterator.Next(true)) return properties;

            do
            {
                properties.Add(iterator.Copy());
            }
            while (iterator.Next(false));

            return properties;
        }

        /// <summary>Reads every top-level property's content hash and readable value before a write.</summary>
        /// <param name="target">The object about to be written.</param>
        /// <returns>Each property's state, keyed by property path.</returns>
        private static Dictionary<string, PropertyState> CaptureTopLevelState(UnityEngine.Object target)
        {
            var state = new Dictionary<string, PropertyState>();
            using (var serializedObject = new SerializedObject(target))
            {
                foreach (var property in AllTopLevelProperties(serializedObject))
                {
                    state[property.propertyPath] = new PropertyState { Hash = property.contentHash, Display = DescribePropertyValue(property) };
                }
            }

            return state;
        }

        /// <summary>
        /// Lists every top-level property a write changed that the payload did not name, with its value before and
        /// after. A whole-object JSON overwrite rebuilds the object, a reimport rewrites settings and OnValidate can
        /// move a sibling, so a report listing only the named fields would hide what those did.
        /// </summary>
        /// <param name="target">The written object.</param>
        /// <param name="before">Every top-level property's state before the write.</param>
        /// <param name="namedPaths">The fields the payload named, or null to list every change.</param>
        /// <returns>One line per changed property the payload did not name.</returns>
        private static List<string> DescribeUnnamedChanges(UnityEngine.Object target, Dictionary<string, PropertyState> before, ICollection<string> namedPaths)
        {
            var changes = new List<string>();
            using (var serializedObject = new SerializedObject(target))
            {
                foreach (var property in AllTopLevelProperties(serializedObject))
                {
                    var path = property.propertyPath;
                    if (IsNamedPath(path, namedPaths)) continue;

                    PropertyState was;
                    if (!before.TryGetValue(path, out was))
                    {
                        changes.Add(path + " = " + DescribePropertyValue(property) + " (absent before)");
                        continue;
                    }

                    if (was.Hash == property.contentHash) continue;
                    changes.Add(path + " = " + DescribePropertyValue(property) + " (was " + was.Display + ")");
                }
            }

            return changes;
        }

        /// <summary>Whether a top-level property is one the payload named, or holds a nested field it named.</summary>
        /// <param name="path">Top-level property path.</param>
        /// <param name="namedPaths">The fields the payload named, or null.</param>
        /// <returns>True when the payload named it or a field inside it.</returns>
        private static bool IsNamedPath(string path, ICollection<string> namedPaths)
        {
            if (namedPaths == null) return false;

            foreach (var named in namedPaths)
            {
                if (named == path || named.StartsWith(path + ".", StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// Builds the report of a field write from what Unity reads back once it is committed: fields that changed
        /// (old -> new), fields already at the asked value, fields that did not take the asked value, fields the
        /// write changed though the payload did not name them, and payload keys that matched no serialized
        /// property. The lead sentence states plainly when nothing changed, so a no-op can never read as a success.
        /// </summary>
        /// <param name="target">The written object.</param>
        /// <param name="setFields">Fields that changed, as read back.</param>
        /// <param name="unchangedFields">Fields already at the asked value.</param>
        /// <param name="notAppliedFields">Fields still at their old value against the request.</param>
        /// <param name="unnamedChanges">Fields the write changed that the payload did not name.</param>
        /// <param name="unknownFields">Payload keys that matched no serialized property.</param>
        /// <returns>A report describing exactly what changed and what did not.</returns>
        private static string BuildFieldWriteReport(
            UnityEngine.Object target,
            List<string> setFields,
            List<string> unchangedFields,
            List<string> notAppliedFields,
            List<string> unnamedChanges,
            List<string> unknownFields)
        {
            var builder = new StringBuilder();
            var anythingChanged = setFields.Count > 0 || unnamedChanges.Count > 0;
            builder.Append(anythingChanged ? "Modified " : "No serialized data changed on ")
                .Append(DescribeWriteTarget(target)).Append('.');

            if (setFields.Count > 0)
            {
                builder.Append(" Set ").Append(string.Join("; ", setFields)).Append('.');
            }

            if (unchangedFields.Count > 0)
            {
                builder.Append(" Already at value: ").Append(string.Join("; ", unchangedFields)).Append('.');
            }

            if (notAppliedFields.Count > 0)
            {
                builder.Append(" WARNING, not applied: ").Append(string.Join("; ", notAppliedFields)).Append('.');
            }

            if (unnamedChanges.Count > 0)
            {
                builder.Append(" WARNING, also changed though the payload did not name it: ")
                    .Append(string.Join("; ", unnamedChanges)).Append('.');
            }

            if (unknownFields.Count > 0)
            {
                builder.Append(" WARNING: no serialized property named ")
                    .Append(string.Join(", ", unknownFields))
                    .Append(" on this object - ignored.");
            }

            return builder.ToString();
        }

        /// <summary>
        /// Names a write's target for its report: a component by type and object path, a GameObject by path, and
        /// anything else by name, type and asset path.
        /// </summary>
        /// <param name="target">The written object.</param>
        /// <returns>A short description of the object.</returns>
        private static string DescribeWriteTarget(UnityEngine.Object target)
        {
            var component = target as Component;
            if (component != null) return component.GetType().FullName + " on " + GetGameObjectPath(component.gameObject);

            var gameObject = target as GameObject;
            if (gameObject != null) return "GameObject " + GetGameObjectPath(gameObject);

            var assetPath = AssetDatabase.GetAssetPath(target);
            return target.name + " (" + target.GetType().FullName + (string.IsNullOrEmpty(assetPath) ? ")" : ", " + assetPath + ")");
        }

        /// <summary>Marks a written object dirty, and its scene too when it lives in one, so the change is saved.</summary>
        /// <param name="target">The written object.</param>
        private static void MarkWriteTargetDirty(UnityEngine.Object target)
        {
            EditorUtility.SetDirty(target);
            var gameObject = target as GameObject;
            if (gameObject == null && target is Component) gameObject = ((Component)target).gameObject;
            if (gameObject != null) MarkGameObjectSceneDirty(gameObject);
        }

        /// <summary>The note a write's report carries in play mode, where the change does not outlast play.</summary>
        /// <returns>The note, or an empty string outside play mode.</returns>
        private static string PlayModeWriteNote()
        {
            return EditorApplication.isPlaying ? " Play mode: this lasts until play mode ends and is not saved." : string.Empty;
        }

        /// <summary>
        /// Renders a serialized property's current value as a short readable token for the before and after
        /// evidence. Equality is never decided from it: <see cref="PropertyValueToken"/> does that exactly.
        /// </summary>
        /// <param name="property">The property to describe.</param>
        /// <returns>A compact human-readable value token.</returns>
        private static string DescribePropertyValue(SerializedProperty property)
        {
            return DescribePropertyValue(property, 0);
        }

        /// <summary>
        /// Renders a property's value at a nesting depth, so a struct inside an array inside a struct stays
        /// bounded: fields and elements past the display limit, and levels past the depth limit, are summarised.
        /// </summary>
        /// <param name="property">The property to describe.</param>
        /// <param name="depth">How many structs or arrays enclose it in this description.</param>
        /// <returns>A compact human-readable value token.</returns>
        private static string DescribePropertyValue(SerializedProperty property, int depth)
        {
            // Arrays are Generic properties, so without this they would all describe as their kind and every
            // array would read the same whatever it held.
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                return DescribeArrayPropertyValue(property, depth);
            }

            switch (property.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    // The instance id is part of the evidence, not decoration: two DIFFERENT objects
                    // sharing a name (an FBX's embedded material and the project .mat it was authored
                    // from) otherwise produce identical before/after strings, and a genuine change gets
                    // reported as already-at-value.
                    var reference = property.objectReferenceValue;
                    return reference == null ? "null" : reference.name + " (" + reference.GetType().Name + " #" + reference.GetInstanceID() + ")";
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Integer: return property.longValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.Character: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Float:
                    // A float field read as a double widens 7.6 to 7.599999904632568, so a float reads as a float.
                    return property.numericType == SerializedPropertyNumericType.Double
                        ? property.doubleValue.ToString("R", CultureInfo.InvariantCulture)
                        : property.floatValue.ToString("R", CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return "\"" + property.stringValue + "\"";
                case SerializedPropertyType.Enum:
                    var names = property.enumNames;
                    var index = property.enumValueIndex;
                    return index >= 0 && index < names.Length ? names[index] : property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Color: var c = property.colorValue; return "(" + c.r + ", " + c.g + ", " + c.b + ", " + c.a + ")";
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString();
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString();
                case SerializedPropertyType.Vector4: return property.vector4Value.ToString();
                case SerializedPropertyType.Quaternion: return property.quaternionValue.ToString();
                case SerializedPropertyType.AnimationCurve: return DescribeAnimationCurve(property.animationCurveValue);
                case SerializedPropertyType.ArraySize: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.FixedBufferSize: return property.fixedBufferSize.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Vector2Int: return property.vector2IntValue.ToString();
                case SerializedPropertyType.Vector3Int: return property.vector3IntValue.ToString();
                case SerializedPropertyType.Rect: return property.rectValue.ToString();
                case SerializedPropertyType.RectInt: return property.rectIntValue.ToString();
                case SerializedPropertyType.Bounds: return property.boundsValue.ToString();
                case SerializedPropertyType.BoundsInt: return property.boundsIntValue.ToString();
                case SerializedPropertyType.Hash128: return property.hash128Value.ToString();
                case SerializedPropertyType.Gradient: return DescribeGradient(property.gradientValue);
                case SerializedPropertyType.ExposedReference:
                    var exposed = property.exposedReferenceValue;
                    return exposed == null ? "null" : exposed.name + " (" + exposed.GetType().Name + " #" + exposed.GetInstanceID() + ")";
                case SerializedPropertyType.ManagedReference:
                    return string.IsNullOrEmpty(property.managedReferenceFullTypename)
                        ? "null"
                        : property.managedReferenceFullTypename + " " + DescribeCompoundPropertyValue(property, depth);
                case SerializedPropertyType.Generic: return DescribeCompoundPropertyValue(property, depth);
                default: return property.propertyType + " value";
            }
        }

        /// <summary>
        /// Renders an array property as a compact count-and-contents token for the before/after evidence, e.g.
        /// <c>[3: Craft_00, Craft_01, null]</c>. Long arrays are summarised rather than dumped whole.
        /// </summary>
        /// <param name="property">The array property to describe.</param>
        /// <param name="depth">How many structs or arrays enclose it in this description.</param>
        /// <returns>A compact human-readable token covering the array's length and leading elements.</returns>
        private static string DescribeArrayPropertyValue(SerializedProperty property, int depth)
        {
            var builder = new StringBuilder();
            builder.Append('[').Append(property.arraySize);

            var shown = Math.Min(property.arraySize, ArrayDescriptionElementLimit);
            for (var i = 0; i < shown; i++)
            {
                builder.Append(i == 0 ? ": " : ", ");
                builder.Append(DescribePropertyValue(property.GetArrayElementAtIndex(i), depth + 1));
            }

            if (property.arraySize > shown)
            {
                builder.Append(", +").Append(property.arraySize - shown).Append(" more");
            }

            return builder.Append(']').ToString();
        }

        /// <summary>Formats one float round-trippably, so two distinct values can never share a token.</summary>
        /// <param name="value">Value to format.</param>
        /// <returns>The round-trip representation.</returns>
        private static string FormatExactFloat(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>Formats a vector round-trippably, so two distinct vectors can never share a description.</summary>
        /// <param name="value">Vector to format.</param>
        /// <returns>The components in parentheses.</returns>
        private static string FormatExactVector3(Vector3 value)
        {
            return "(" + FormatExactFloat(value.x) + ", " + FormatExactFloat(value.y) + ", " + FormatExactFloat(value.z) + ")";
        }

        /// <summary>Levels of nested structs and arrays a value description opens before it summarises the rest.</summary>
        private const int COMPOUND_DESCRIPTION_DEPTH_LIMIT = 3;

        /// <summary>
        /// Renders a struct or class value as its own fields, so a nested value is reported as values rather than
        /// as the name of its kind. Each level shows at most <see cref="ArrayDescriptionElementLimit"/> fields and
        /// says how many more there are.
        /// </summary>
        /// <param name="property">The struct, class or managed-reference property.</param>
        /// <param name="depth">How many structs or arrays enclose it in this description.</param>
        /// <returns>The fields as name: value pairs in braces.</returns>
        private static string DescribeCompoundPropertyValue(SerializedProperty property, int depth)
        {
            if (depth >= COMPOUND_DESCRIPTION_DEPTH_LIMIT) return "{...}";

            var child = property.Copy();
            var end = property.GetEndProperty();
            if (!child.Next(true) || SerializedProperty.EqualContents(child, end)) return "{}";

            var builder = new StringBuilder();
            builder.Append('{');
            var shown = 0;
            var hidden = 0;
            do
            {
                if (shown >= ArrayDescriptionElementLimit)
                {
                    hidden++;
                    continue;
                }

                builder.Append(shown == 0 ? string.Empty : ", ").Append(child.name).Append(": ")
                    .Append(DescribePropertyValue(child, depth + 1));
                shown++;
            }
            while (child.Next(false) && !SerializedProperty.EqualContents(child, end));

            if (hidden > 0) builder.Append(", +").Append(hidden).Append(" more");
            return builder.Append('}').ToString();
        }

        /// <summary>Renders a gradient as its mode and key counts.</summary>
        /// <param name="gradient">Gradient to describe.</param>
        /// <returns>A short description.</returns>
        private static string DescribeGradient(Gradient gradient)
        {
            if (gradient == null) return "null";
            return "gradient " + gradient.mode + ", " + gradient.colorKeys.Length + " colour key(s), " +
                gradient.alphaKeys.Length + " alpha key(s)";
        }

        /// <summary>
        /// Parses an AnimationCurve from either a bare number or an array of keyframes.
        /// </summary>
        /// <remarks>
        /// A bare number becomes a ONE-key curve, which is exactly what Unity itself writes when a curve-backed
        /// property is set through its C# accessor: AudioSource.spatialBlend, spread and the volume rolloff are
        /// all stored as curves, and an author setting a blend of 1 is not thinking in keyframes. The array form
        /// takes the same key fields as animation-clip-set-curve, so the two stay writable from one another.
        /// </remarks>
        /// <param name="value">Raw JSON token supplied for the field.</param>
        /// <param name="curve">Receives the parsed curve.</param>
        /// <returns>An error string, or null when the curve parsed.</returns>
        private static string ParseAnimationCurve(string value, out AnimationCurve curve)
        {
            curve = null;

            float constant;
            if (TryJsonTokenToFloat(value, out constant))
            {
                curve = new AnimationCurve(new Keyframe(0f, constant));
                return null;
            }

            List<string> items;
            if (!TrySplitJsonArray(value, out items))
            {
                return "a curve takes a number for a constant, or [{\"time\": ..., \"value\": ...}, ...] for keyframes.";
            }

            var keys = new Keyframe[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                float time, keyValue;
                if (!TryReadJsonFloat(items[i], "time", out time) || !TryReadJsonFloat(items[i], "value", out keyValue))
                {
                    return "key " + i + " needs both \"time\" and \"value\".";
                }

                var key = new Keyframe(time, keyValue);

                float tangent;
                if (TryReadJsonFloat(items[i], "inTangent", out tangent)) key.inTangent = tangent;
                if (TryReadJsonFloat(items[i], "outTangent", out tangent)) key.outTangent = tangent;

                keys[i] = key;
            }

            curve = new AnimationCurve(keys);
            return null;
        }

        /// <summary>Renders a curve as evidence, naming the constant case these fields usually hold.</summary>
        /// <param name="curve">Curve to describe.</param>
        /// <returns>A short description of the curve's shape.</returns>
        private static string DescribeAnimationCurve(AnimationCurve curve)
        {
            if (curve == null || curve.length == 0) return "empty curve";

            var keys = curve.keys;
            if (keys.Length == 1)
            {
                return "constant " + keys[0].value.ToString("R", CultureInfo.InvariantCulture);
            }

            return keys.Length + " keys, " +
                keys[0].value.ToString("R", CultureInfo.InvariantCulture) + " at t" +
                keys[0].time.ToString("R", CultureInfo.InvariantCulture) + " to " +
                keys[keys.Length - 1].value.ToString("R", CultureInfo.InvariantCulture) + " at t" +
                keys[keys.Length - 1].time.ToString("R", CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
