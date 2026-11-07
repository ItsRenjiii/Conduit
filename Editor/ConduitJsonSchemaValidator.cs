/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Fail-closed JSON Schema validation for the exact subset published by Conduit's MCP catalogue.
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>Validates tool arguments against the immutable schema advertised to MCP clients.</summary>
    internal static class ConduitJsonSchemaValidator
    {
        #region Keywords

        /// <summary>Assertion keywords this validator implements rather than silently ignoring.</summary>
        private static readonly HashSet<string> _assertionKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "type", "properties", "required", "additionalProperties", "enum", "items", "minItems",
            "uniqueItems", "minLength", "anyOf", "allOf", "if", "then", "else",
        };

        /// <summary>Descriptive keywords which never change whether an instance is valid.</summary>
        private static readonly HashSet<string> _annotationKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "description", "default", "title", "examples", "deprecated", "readOnly", "writeOnly",
            "$schema", "$id", "$comment",
        };

        #endregion

        #region Public validation

        /// <summary>Validates one arguments object and names the first failed path and rule.</summary>
        /// <param name="schema">Published input schema.</param>
        /// <param name="arguments">Tool arguments.</param>
        /// <param name="error">Receives one actionable failure sentence.</param>
        /// <returns>True only when both the schema subset and the arguments are valid.</returns>
        internal static bool Validate(JObject schema, JObject arguments, out string error)
        {
            error = null;
            if (schema == null) { error = "$schema: schema is missing"; return false; }
            if (arguments == null) { error = "$: arguments object is missing"; return false; }
            if (!ValidateSchemaShape(schema, "$schema", out error)) return false;
            return ValidateToken(schema, arguments, "$", out error);
        }

        #endregion

        #region Schema preflight

        /// <summary>Rejects an unknown or malformed schema keyword before evaluating any branch.</summary>
        /// <param name="schema">Schema object to preflight.</param>
        /// <param name="path">Readable schema path.</param>
        /// <param name="error">Receives the first unsupported or malformed keyword.</param>
        /// <returns>True when every branch uses the supported subset.</returns>
        private static bool ValidateSchemaShape(JObject schema, string path, out string error)
        {
            error = null;
            foreach (var property in schema.Properties())
            {
                if (_assertionKeywords.Contains(property.Name) || _annotationKeywords.Contains(property.Name)) continue;
                error = path + ": unsupported schema keyword '" + property.Name + "'";
                return false;
            }

            if (schema["type"] != null && schema["type"].Type != JTokenType.String)
            {
                error = path + ".type: expected a string";
                return false;
            }
            var declaredType = (string)schema["type"];
            if (declaredType != null && declaredType != "object" && declaredType != "array"
                && declaredType != "string" && declaredType != "boolean" && declaredType != "null"
                && declaredType != "number" && declaredType != "integer")
            {
                error = path + ".type: unsupported JSON type '" + declaredType + "'";
                return false;
            }

            var properties = schema["properties"];
            if (properties != null)
            {
                var map = properties as JObject;
                if (map == null) { error = path + ".properties: expected an object"; return false; }
                foreach (var entry in map.Properties())
                {
                    var child = entry.Value as JObject;
                    if (child == null)
                    {
                        error = path + ".properties." + entry.Name + ": expected a schema object";
                        return false;
                    }
                    if (!ValidateSchemaShape(child, path + ".properties." + entry.Name, out error)) return false;
                }
            }

            if (!ValidateStringArrayKeyword(schema, "required", path, out error)) return false;
            if (!ValidateSchemaArrayKeyword(schema, "anyOf", path, out error)) return false;
            if (!ValidateSchemaArrayKeyword(schema, "allOf", path, out error)) return false;

            foreach (var name in new[] { "items", "if", "then", "else" })
            {
                if (schema[name] == null) continue;
                var child = schema[name] as JObject;
                if (child != null && ValidateSchemaShape(child, path + "." + name, out error)) continue;
                if (error == null) error = path + "." + name + ": expected a schema object";
                return false;
            }

            if (schema["additionalProperties"] != null && schema["additionalProperties"].Type != JTokenType.Boolean)
            {
                error = path + ".additionalProperties: expected a boolean";
                return false;
            }
            if (schema["enum"] != null && (schema["enum"].Type != JTokenType.Array || !schema["enum"].HasValues))
            {
                error = path + ".enum: expected a non-empty array";
                return false;
            }
            if (!ValidateNonNegativeIntegerKeyword(schema, "minItems", path, out error)) return false;
            if (!ValidateNonNegativeIntegerKeyword(schema, "minLength", path, out error)) return false;
            if (schema["uniqueItems"] != null && schema["uniqueItems"].Type != JTokenType.Boolean)
            {
                error = path + ".uniqueItems: expected a boolean";
                return false;
            }
            return true;
        }

        /// <summary>Validates an array of required property names.</summary>
        /// <param name="schema">Owning schema.</param>
        /// <param name="name">Keyword name.</param>
        /// <param name="path">Owning schema path.</param>
        /// <param name="error">Receives malformed-keyword evidence.</param>
        /// <returns>True when the keyword is absent or valid.</returns>
        private static bool ValidateStringArrayKeyword(JObject schema, string name, string path, out string error)
        {
            error = null;
            if (schema[name] == null) return true;
            var values = schema[name] as JArray;
            if (values == null) { error = path + "." + name + ": expected an array"; return false; }
            foreach (var value in values)
            {
                if (value.Type == JTokenType.String) continue;
                error = path + "." + name + ": every item must be a string";
                return false;
            }
            return true;
        }

        /// <summary>Validates an array whose items are schemas.</summary>
        /// <param name="schema">Owning schema.</param>
        /// <param name="name">Keyword name.</param>
        /// <param name="path">Owning schema path.</param>
        /// <param name="error">Receives malformed-branch evidence.</param>
        /// <returns>True when every branch is supported.</returns>
        private static bool ValidateSchemaArrayKeyword(JObject schema, string name, string path, out string error)
        {
            error = null;
            if (schema[name] == null) return true;
            var values = schema[name] as JArray;
            if (values == null || values.Count == 0) { error = path + "." + name + ": expected a non-empty schema array"; return false; }
            for (var index = 0; index < values.Count; index++)
            {
                var child = values[index] as JObject;
                if (child != null && ValidateSchemaShape(child, path + "." + name + "[" + index + "]", out error)) continue;
                if (error == null) error = path + "." + name + "[" + index + "]: expected a schema object";
                return false;
            }
            return true;
        }

        /// <summary>Validates a non-negative integer-valued schema keyword.</summary>
        /// <param name="schema">Owning schema.</param>
        /// <param name="name">Keyword name.</param>
        /// <param name="path">Owning schema path.</param>
        /// <param name="error">Receives malformed-value evidence.</param>
        /// <returns>True when the keyword is absent or valid.</returns>
        private static bool ValidateNonNegativeIntegerKeyword(JObject schema, string name, string path, out string error)
        {
            error = null;
            if (schema[name] == null) return true;
            long value;
            if (schema[name].Type == JTokenType.Integer
                && long.TryParse(schema[name].ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                && value >= 0) return true;
            error = path + "." + name + ": expected a non-negative integer";
            return false;
        }

        #endregion

        #region Instance validation

        /// <summary>Evaluates every supported assertion keyword against one token.</summary>
        /// <param name="schema">Schema to apply.</param>
        /// <param name="value">Instance token.</param>
        /// <param name="path">Readable argument path.</param>
        /// <param name="error">Receives the first failed assertion.</param>
        /// <returns>True when the token satisfies the schema.</returns>
        private static bool ValidateToken(JObject schema, JToken value, string path, out string error)
        {
            error = null;
            var type = (string)schema["type"];
            if (type != null && !MatchesType(type, value))
            {
                error = path + ": expected " + type + ", got " + TypeName(value);
                return false;
            }

            var choices = schema["enum"] as JArray;
            if (choices != null)
            {
                var matched = false;
                foreach (var choice in choices) if (JsonEqual(choice, value)) { matched = true; break; }
                if (!matched) { error = path + ": value is not in the advertised enum"; return false; }
            }

            if (schema["minLength"] != null && value.Type == JTokenType.String
                && ((string)value).Length < (long)schema["minLength"])
            {
                error = path + ": string is shorter than minLength " + (long)schema["minLength"];
                return false;
            }

            var asObject = value as JObject;
            if (asObject != null && !ValidateObject(schema, asObject, path, out error)) return false;
            var asArray = value as JArray;
            if (asArray != null && !ValidateArray(schema, asArray, path, out error)) return false;

            var allOf = schema["allOf"] as JArray;
            if (allOf != null)
            {
                foreach (var branch in allOf)
                {
                    if (!ValidateToken((JObject)branch, value, path, out error)) return false;
                }
            }

            var anyOf = schema["anyOf"] as JArray;
            if (anyOf != null)
            {
                var matched = false;
                foreach (var branch in anyOf)
                {
                    string ignored;
                    if (ValidateToken((JObject)branch, value, path, out ignored)) { matched = true; break; }
                }
                if (!matched) { error = path + ": value satisfies no anyOf branch"; return false; }
            }

            var condition = schema["if"] as JObject;
            if (condition != null)
            {
                string ignored;
                var selected = ValidateToken(condition, value, path, out ignored) ? schema["then"] : schema["else"];
                if (selected != null && !ValidateToken((JObject)selected, value, path, out error)) return false;
            }
            return true;
        }

        /// <summary>Validates required, declared, and additional object properties.</summary>
        /// <param name="schema">Object schema.</param>
        /// <param name="value">Object instance.</param>
        /// <param name="path">Readable argument path.</param>
        /// <param name="error">Receives the first property failure.</param>
        /// <returns>True when the object satisfies its property rules.</returns>
        private static bool ValidateObject(JObject schema, JObject value, string path, out string error)
        {
            error = null;
            var required = schema["required"] as JArray;
            if (required != null)
            {
                foreach (var item in required)
                {
                    var name = (string)item;
                    if (value[name] != null) continue;
                    error = path + ": required property '" + name + "' is missing";
                    return false;
                }
            }

            var properties = schema["properties"] as JObject;
            foreach (var property in value.Properties())
            {
                var childSchema = properties == null ? null : properties[property.Name] as JObject;
                if (childSchema != null)
                {
                    if (!ValidateToken(childSchema, property.Value, PropertyPath(path, property.Name), out error)) return false;
                    continue;
                }
                if ((bool?)schema["additionalProperties"] == false)
                {
                    error = PropertyPath(path, property.Name) + ": additional property is not allowed";
                    return false;
                }
            }
            return true;
        }

        /// <summary>Validates item count, item schemas, and structural uniqueness.</summary>
        /// <param name="schema">Array schema.</param>
        /// <param name="value">Array instance.</param>
        /// <param name="path">Readable argument path.</param>
        /// <param name="error">Receives the first item failure.</param>
        /// <returns>True when the array satisfies its rules.</returns>
        private static bool ValidateArray(JObject schema, JArray value, string path, out string error)
        {
            error = null;
            if (schema["minItems"] != null && value.Count < (long)schema["minItems"])
            {
                error = path + ": expected at least " + (long)schema["minItems"] + " item(s)";
                return false;
            }
            var items = schema["items"] as JObject;
            if (items != null)
            {
                for (var index = 0; index < value.Count; index++)
                {
                    if (!ValidateToken(items, value[index], path + "[" + index + "]", out error)) return false;
                }
            }
            if ((bool?)schema["uniqueItems"] == true)
            {
                for (var left = 0; left < value.Count; left++)
                {
                    for (var right = left + 1; right < value.Count; right++)
                    {
                        if (!JsonEqual(value[left], value[right])) continue;
                        error = path + "[" + right + "]: duplicates item " + left + " while uniqueItems is true";
                        return false;
                    }
                }
            }
            return true;
        }

        #endregion

        #region Value helpers

        /// <summary>Reports whether a token has the advertised JSON type.</summary>
        /// <param name="type">Advertised type.</param>
        /// <param name="value">Instance token.</param>
        /// <returns>True when the token has that type.</returns>
        private static bool MatchesType(string type, JToken value)
        {
            switch (type)
            {
                case "object": return value.Type == JTokenType.Object;
                case "array": return value.Type == JTokenType.Array;
                case "string": return value.Type == JTokenType.String;
                case "boolean": return value.Type == JTokenType.Boolean;
                case "null": return value.Type == JTokenType.Null;
                case "number": return IsFiniteNumber(value);
                case "integer": return IsInteger(value);
                default: return false;
            }
        }

        /// <summary>Reports whether a token is a finite JSON number.</summary>
        /// <param name="value">Instance token.</param>
        /// <returns>True for a finite integer or float token.</returns>
        private static bool IsFiniteNumber(JToken value)
        {
            if (value == null) return false;
            if (value.Type == JTokenType.Integer) return true;
            if (value.Type != JTokenType.Float) return false;
            var number = Convert.ToDouble(((JValue)value).Value, CultureInfo.InvariantCulture);
            return !double.IsNaN(number) && !double.IsInfinity(number);
        }

        /// <summary>Reports whether a numeric token has no fractional value.</summary>
        /// <param name="value">Instance token.</param>
        /// <returns>True when the numeric value is mathematically integral.</returns>
        private static bool IsInteger(JToken value)
        {
            if (value.Type == JTokenType.Integer) return true;
            if (!IsFiniteNumber(value)) return false;
            var number = Convert.ToDouble(((JValue)value).Value, CultureInfo.InvariantCulture);
            return Math.Truncate(number) == number;
        }

        /// <summary>Compares JSON values structurally, treating numerically equal spellings as equal.</summary>
        /// <param name="left">First value.</param>
        /// <param name="right">Second value.</param>
        /// <returns>True when JSON Schema equality treats them as equal.</returns>
        private static bool JsonEqual(JToken left, JToken right)
        {
            if (IsFiniteNumber(left) && IsFiniteNumber(right))
            {
                try
                {
                    return Convert.ToDecimal(((JValue)left).Value, CultureInfo.InvariantCulture)
                        == Convert.ToDecimal(((JValue)right).Value, CultureInfo.InvariantCulture);
                }
                catch (OverflowException)
                {
                    return Convert.ToDouble(((JValue)left).Value, CultureInfo.InvariantCulture)
                        == Convert.ToDouble(((JValue)right).Value, CultureInfo.InvariantCulture);
                }
            }
            return JToken.DeepEquals(left, right);
        }

        /// <summary>Returns a stable user-facing JSON type name.</summary>
        /// <param name="value">Instance token.</param>
        /// <returns>Readable type name.</returns>
        private static string TypeName(JToken value)
        {
            if (value == null) return "missing";
            switch (value.Type)
            {
                case JTokenType.Object: return "object";
                case JTokenType.Array: return "array";
                case JTokenType.String: return "string";
                case JTokenType.Boolean: return "boolean";
                case JTokenType.Null: return "null";
                case JTokenType.Integer: return "integer";
                case JTokenType.Float: return "number";
                default: return value.Type.ToString();
            }
        }

        /// <summary>Builds a readable property path without pretending every name is an identifier.</summary>
        /// <param name="parent">Parent path.</param>
        /// <param name="name">Property name.</param>
        /// <returns>Dotted or bracketed property path.</returns>
        private static string PropertyPath(string parent, string name)
        {
            return IsIdentifier(name) ? parent + "." + name : parent + "['" + name.Replace("'", "\\'") + "']";
        }

        /// <summary>Reports whether a property can use dotted path notation.</summary>
        /// <param name="value">Property name.</param>
        /// <returns>True when the name is identifier-shaped.</returns>
        private static bool IsIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || !(char.IsLetter(value[0]) || value[0] == '_')) return false;
            for (var index = 1; index < value.Length; index++)
            {
                if (!char.IsLetterOrDigit(value[index]) && value[index] != '_') return false;
            }
            return true;
        }

        #endregion
    }
}
