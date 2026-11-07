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
    /// AnimationClip authoring: creation, settings, curves, events and reads.
    /// </summary>
    internal static partial class ConduitBridge
    {



        /// <summary>
        /// Creates an AnimationClip asset, optionally applying a settings payload (frameRate, loop
        /// settings, events) in the same call through the shared apply path of
        /// <see cref="AnimationClipSetCommand"/>.
        /// </summary>
        /// <param name="args">Pipe-separated Assets/*.anim path and optional settings JSON.</param>
        /// <returns>A creation confirmation plus the settings report, or an error.</returns>
        private static string AnimationClipCreateCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 1 || string.IsNullOrWhiteSpace(sections[0]))
            {
                return "Usage: animation-clip-create <assetPath> [| settingsJson] - path must be an Assets/*.anim path.";
            }

            var assetPath = NormalizeAssetPath(sections[0]);
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                return "Clip path must be an Assets/*.anim path.";
            }

            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath) != null)
            {
                return "AnimationClip already exists: " + assetPath + ". Edit it with animation-clip-set / animation-clip-set-curve.";
            }

            EnsureAssetFolder(Path.GetDirectoryName(assetPath));
            var clip = new AnimationClip();
            AssetDatabase.CreateAsset(clip, assetPath);
            AssetDatabase.SaveAssets();

            if (sections.Length >= 2 && !string.IsNullOrWhiteSpace(sections[1]))
            {
                var payload = string.Join(" | ", sections, 1, sections.Length - 1);
                var applyReport = AnimationClipSetCommand(assetPath + " | " + payload);
                return "Created AnimationClip " + assetPath + ". " + applyReport;
            }

            return "Created AnimationClip " + assetPath + " (empty, " + JsonNumber(clip.frameRate) + " fps). Add curves with animation-clip-set-curve.";
        }

        /// <summary>
        /// Applies clip-level settings from a flat JSON payload: frameRate, the
        /// AnimationClipSettings loop block (loopTime, loopBlend, cycleOffset, loopBlendOrientation,
        /// loopBlendPositionY, loopBlendPositionXZ, keepOriginalOrientation, keepOriginalPositionY,
        /// keepOriginalPositionXZ, heightFromFeet, mirror), and <c>events</c>
        /// (REPLACES the event list; entries are {"time","functionName","floatParameter",
        /// "intParameter","stringParameter"}). Validates fully before applying.
        /// </summary>
        /// <param name="args">Pipe-separated clip asset path and settings JSON.</param>
        /// <returns>A per-property evidence report, or an error with nothing applied.</returns>
        private static string AnimationClipSetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: animation-clip-set <clipPath> | {\"frameRate\":60,\"loopTime\":true,...,\"events\":[{\"time\":0.5,\"functionName\":\"...\",...}]}";
            }

            var assetPath = NormalizeAssetPath(sections[0]);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (clip == null)
            {
                return "AnimationClip not found: " + assetPath;
            }

            List<KeyValuePair<string, string>> fields;
            var error = TryParsePayloadSection(sections[1], out fields);
            if (error != null)
            {
                return error;
            }

            if (fields.Count == 0)
            {
                return "Empty payload: nothing to change on " + assetPath + ".";
            }

            // Pass 1 - validate. Settings bools stage into a name/value list applied via the
            // AnimationClipSettings struct; frameRate and events are separate channels.
            float? frameRate = null;
            var boolSettings = new List<KeyValuePair<string, bool>>();
            float? cycleOffset = null;
            AnimationEvent[] events = null;
            var unknownFields = new List<string>();

            foreach (var entry in fields)
            {
                switch (entry.Key)
                {
                    case "frameRate":
                        float rate;
                        if (!TryJsonTokenToFloat(entry.Value, out rate) || rate <= 0f) return "frameRate '" + entry.Value + "' is not a positive number. Nothing was applied.";
                        frameRate = rate;
                        break;

                    case "cycleOffset":
                        float offsetValue;
                        if (!TryJsonTokenToFloat(entry.Value, out offsetValue)) return "cycleOffset '" + entry.Value + "' is not a number. Nothing was applied.";
                        cycleOffset = offsetValue;
                        break;

                    case "loopTime":
                    case "loopBlend":
                    case "loopBlendOrientation":
                    case "loopBlendPositionY":
                    case "loopBlendPositionXZ":
                    case "keepOriginalOrientation":
                    case "keepOriginalPositionY":
                    case "keepOriginalPositionXZ":
                    case "heightFromFeet":
                    case "mirror":
                        bool boolValue;
                        if (!TryJsonTokenToBool(entry.Value, out boolValue)) return entry.Key + " '" + entry.Value + "' is not a bool. Nothing was applied.";
                        boolSettings.Add(new KeyValuePair<string, bool>(entry.Key, boolValue));
                        break;

                    case "events":
                        List<string> items;
                        if (!TrySplitJsonArray(entry.Value, out items)) return "events must be a JSON array. Nothing was applied.";
                        var list = new List<AnimationEvent>();
                        foreach (var item in items)
                        {
                            AnimationEvent animationEvent;
                            var eventError = TryParseAnimationEvent(item, out animationEvent);
                            if (eventError != null) return eventError;
                            list.Add(animationEvent);
                        }

                        events = list.ToArray();
                        break;

                    default:
                        unknownFields.Add(entry.Key);
                        break;
                }
            }

            // Pass 2 - apply with evidence.
            var changes = new List<string>();
            if (frameRate.HasValue)
            {
                if (Mathf.Approximately(clip.frameRate, frameRate.Value)) changes.Add("frameRate already " + JsonNumber(clip.frameRate));
                else { changes.Add("frameRate " + JsonNumber(clip.frameRate) + " -> " + JsonNumber(frameRate.Value)); clip.frameRate = frameRate.Value; }
            }

            if (boolSettings.Count > 0 || cycleOffset.HasValue)
            {
                var settings = AnimationUtility.GetAnimationClipSettings(clip);
                foreach (var setting in boolSettings)
                {
                    bool old;
                    switch (setting.Key)
                    {
                        case "loopTime":                old = settings.loopTime;                settings.loopTime = setting.Value;                break;
                        case "loopBlend":               old = settings.loopBlend;               settings.loopBlend = setting.Value;               break;
                        case "loopBlendOrientation":    old = settings.loopBlendOrientation;    settings.loopBlendOrientation = setting.Value;    break;
                        case "loopBlendPositionY":      old = settings.loopBlendPositionY;      settings.loopBlendPositionY = setting.Value;      break;
                        case "loopBlendPositionXZ":     old = settings.loopBlendPositionXZ;     settings.loopBlendPositionXZ = setting.Value;     break;
                        case "keepOriginalOrientation": old = settings.keepOriginalOrientation; settings.keepOriginalOrientation = setting.Value; break;
                        case "keepOriginalPositionY":   old = settings.keepOriginalPositionY;   settings.keepOriginalPositionY = setting.Value;   break;
                        case "keepOriginalPositionXZ":  old = settings.keepOriginalPositionXZ;  settings.keepOriginalPositionXZ = setting.Value;  break;
                        case "heightFromFeet":          old = settings.heightFromFeet;          settings.heightFromFeet = setting.Value;          break;
                        default:                        old = settings.mirror;                  settings.mirror = setting.Value;                  break;
                    }

                    changes.Add(old == setting.Value
                        ? setting.Key + " already " + old.ToString().ToLowerInvariant()
                        : setting.Key + " " + old.ToString().ToLowerInvariant() + " -> " + setting.Value.ToString().ToLowerInvariant());
                }

                if (cycleOffset.HasValue)
                {
                    changes.Add(Mathf.Approximately(settings.cycleOffset, cycleOffset.Value)
                        ? "cycleOffset already " + JsonNumber(settings.cycleOffset)
                        : "cycleOffset " + JsonNumber(settings.cycleOffset) + " -> " + JsonNumber(cycleOffset.Value));
                    settings.cycleOffset = cycleOffset.Value;
                }

                AnimationUtility.SetAnimationClipSettings(clip, settings);
            }

            if (events != null)
            {
                var oldCount = AnimationUtility.GetAnimationEvents(clip).Length;
                AnimationUtility.SetAnimationEvents(clip, events);
                changes.Add("events replaced (" + oldCount + " -> " + events.Length + ")");
            }

            PersistAnimationAssetEdit(clip);

            var sb = new StringBuilder();
            sb.Append("Configured AnimationClip ").Append(assetPath).Append(": ").Append(string.Join("; ", changes)).Append('.');
            if (unknownFields.Count > 0) sb.Append(" WARNING ignored unknown field(s): ").Append(string.Join(", ", unknownFields)).Append('.');
            return sb.ToString();
        }

        /// <summary>
        /// Parses one animation event object; only functionName is required.
        /// </summary>
        /// <param name="json">Raw event object token.</param>
        /// <param name="animationEvent">The parsed event.</param>
        /// <returns>An error string when invalid, otherwise null.</returns>
        private static string TryParseAnimationEvent(string json, out AnimationEvent animationEvent)
        {
            animationEvent = new AnimationEvent();
            List<KeyValuePair<string, string>> fields;
            if (!TrySplitFlatJsonObject(json, out fields))
            {
                return "Each event must be a {\"time\":n,\"functionName\":\"...\"} object. Nothing was applied.";
            }

            foreach (var entry in fields)
            {
                switch (entry.Key)
                {
                    case "time":
                        float time;
                        if (!TryJsonTokenToFloat(entry.Value, out time)) return "Event time '" + entry.Value + "' is not a number. Nothing was applied.";
                        animationEvent.time = time;
                        break;
                    case "functionName":
                        animationEvent.functionName = JsonTokenToString(entry.Value);
                        break;
                    case "floatParameter":
                        float floatParameter;
                        if (!TryJsonTokenToFloat(entry.Value, out floatParameter)) return "Event floatParameter '" + entry.Value + "' is not a number. Nothing was applied.";
                        animationEvent.floatParameter = floatParameter;
                        break;
                    case "intParameter":
                        int intParameter;
                        if (!int.TryParse(entry.Value.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out intParameter)) return "Event intParameter '" + entry.Value + "' is not an int. Nothing was applied.";
                        animationEvent.intParameter = intParameter;
                        break;
                    case "stringParameter":
                        animationEvent.stringParameter = JsonTokenToString(entry.Value);
                        break;
                    default:
                        return "Unknown event field '" + entry.Key + "'. Use time, functionName, floatParameter, intParameter, stringParameter. Nothing was applied.";
                }
            }

            if (string.IsNullOrEmpty(animationEvent.functionName))
            {
                return "An event is missing its \"functionName\". Nothing was applied.";
            }

            return null;
        }

        /// <summary>
        /// Sets, replaces, or removes ONE curve on a clip. Float curves take a keys array
        /// (<c>[{"time":0,"value":1,"inTangent":0,"outTangent":0},...]</c>); object-reference curves
        /// (sprite/material swaps) are detected by keys carrying <c>"reference"</c> asset paths
        /// instead of <c>"value"</c>; the JSON null token removes the binding entirely.
        /// </summary>
        /// <param name="args">Pipe-separated clip path, target path, component type, property name, and keys JSON.</param>
        /// <returns>A confirmation with key count and duration, or an error.</returns>
        private static string AnimationClipSetCurveCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 5)
            {
                return "Usage: animation-clip-set-curve <clipPath> | <targetPath> | <componentType> | <propertyName> | <keysJson|null>  - targetPath is the animated child's hierarchy path relative to the Animator ('' for the root), e.g. Body/Head. Float keys: [{\"time\":0,\"value\":1,\"inTangent\":0,\"outTangent\":0},...]; reference keys: [{\"time\":0,\"reference\":\"Assets/....png\"},...]; null removes the curve.";
            }

            var assetPath = NormalizeAssetPath(sections[0]);
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
            if (clip == null)
            {
                return "AnimationClip not found: " + assetPath;
            }

            var targetPath = sections[1].Trim().Trim('"');
            if (targetPath == "/" || targetPath == ".") targetPath = string.Empty;

            var type = FindTypeLoose(sections[2]);
            if (type == null)
            {
                return "Component type not found: " + sections[2] + ". Use a short or full type name, e.g. Transform, SkinnedMeshRenderer, UnityEngine.UI.Image.";
            }

            var propertyName = sections[3].Trim().Trim('"');
            if (propertyName.Length == 0)
            {
                return "Missing property name (the serialized curve property, e.g. m_LocalPosition.x, blendShape.Smile, m_IsActive).";
            }

            var keysToken = UnwrapQuotedJsonSection(sections[4]);
            var binding = new EditorCurveBinding { path = targetPath, type = type, propertyName = propertyName };

            // Removal: clear both channels so the binding really disappears whichever kind it was.
            if (string.IsNullOrWhiteSpace(keysToken) || keysToken.Trim().Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                var floatBefore = AnimationUtility.GetEditorCurve(clip, binding);
                var referenceBefore = AnimationUtility.GetObjectReferenceCurve(clip, binding);
                var hadFloat = floatBefore != null;
                var hadReference = referenceBefore != null;
                if (!hadFloat && !hadReference)
                {
                    return "No curve exists on " + assetPath + " for [" + targetPath + "/" + type.Name + "/" + propertyName + "] - nothing removed.";
                }

                if (hadFloat) AnimationUtility.SetEditorCurve(clip, binding, null);
                if (hadReference) AnimationUtility.SetObjectReferenceCurve(clip, binding, null);
                PersistAnimationAssetEdit(clip);
                var gone = AnimationUtility.GetEditorCurve(clip, binding) == null && AnimationUtility.GetObjectReferenceCurve(clip, binding) == null;
                return (gone ? "Removed " : "WARNING, still present after removal: ") + (hadFloat ? "float" : "object-reference") +
                    " curve [" + targetPath + "/" + type.Name + "/" + propertyName + "] from " + assetPath + "; it held " +
                    (hadFloat ? DescribeCurveKeys(floatBefore) : DescribeReferenceKeys(referenceBefore)) + ".";
            }

            List<string> items;
            if (!TrySplitJsonArray(keysToken, out items)) return "Keys must be a JSON array (or null to remove the curve).";
            if (items.Count == 0) return "Keys array is empty - pass at least one key, or null to remove the curve.";

            // Detect the curve kind from the first key's shape, then hold every key to it.
            List<KeyValuePair<string, string>> probeFields;
            if (!TrySplitFlatJsonObject(items[0], out probeFields)) return "Each key must be a JSON object.";
            var isReferenceCurve = false;
            foreach (var field in probeFields)
            {
                if (field.Key == "reference") { isReferenceCurve = true; break; }
            }

            if (isReferenceCurve)
            {
                var keyframes = new ObjectReferenceKeyframe[items.Count];
                for (var i = 0; i < items.Count; i++)
                {
                    List<KeyValuePair<string, string>> keyFields;
                    if (!TrySplitFlatJsonObject(items[i], out keyFields)) return "Key " + i + " is not a JSON object. Nothing was applied.";
                    var keyframe = new ObjectReferenceKeyframe();
                    var hasReference = false;
                    foreach (var field in keyFields)
                    {
                        switch (field.Key)
                        {
                            case "time":
                                if (!TryJsonTokenToFloat(field.Value, out keyframe.time)) return "Key " + i + " time '" + field.Value + "' is not a number. Nothing was applied.";
                                break;
                            case "reference":
                                var referenceToken = JsonTokenToString(field.Value);
                                if (referenceToken != null)
                                {
                                    keyframe.value = AssetDatabase.LoadMainAssetAtPath(NormalizeAssetPath(referenceToken));
                                    if (keyframe.value == null) return "Key " + i + " reference not found: " + referenceToken + ". Nothing was applied.";
                                }

                                hasReference = true;
                                break;
                            default:
                                return "Unknown reference-key field '" + field.Key + "' on key " + i + ". Use time, reference. Nothing was applied.";
                        }
                    }

                    if (!hasReference) return "Key " + i + " mixes float and reference shapes - every key needs \"reference\". Nothing was applied.";
                    keyframes[i] = keyframe;
                }

                var keysBefore = DescribeReferenceKeys(AnimationUtility.GetObjectReferenceCurve(clip, binding));
                AnimationUtility.SetObjectReferenceCurve(clip, binding, keyframes);
                PersistAnimationAssetEdit(clip);
                return "Set object-reference curve [" + targetPath + "/" + type.Name + "/" + propertyName + "] on " + assetPath + ": " +
                    keysBefore + " -> " + DescribeReferenceKeys(AnimationUtility.GetObjectReferenceCurve(clip, binding)) + ".";
            }

            var keys = new Keyframe[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                List<KeyValuePair<string, string>> keyFields;
                if (!TrySplitFlatJsonObject(items[i], out keyFields)) return "Key " + i + " is not a JSON object. Nothing was applied.";
                var key = new Keyframe();
                var hasTime = false;
                var hasValue = false;
                foreach (var field in keyFields)
                {
                    float number;
                    switch (field.Key)
                    {
                        case "time":
                            if (!TryJsonTokenToFloat(field.Value, out number)) return "Key " + i + " time '" + field.Value + "' is not a number. Nothing was applied.";
                            key.time = number; hasTime = true;
                            break;
                        case "value":
                            if (!TryJsonTokenToFloat(field.Value, out number)) return "Key " + i + " value '" + field.Value + "' is not a number. Nothing was applied.";
                            key.value = number; hasValue = true;
                            break;
                        case "inTangent":
                            if (!TryJsonTokenToFloat(field.Value, out number)) return "Key " + i + " inTangent '" + field.Value + "' is not a number. Nothing was applied.";
                            key.inTangent = number;
                            break;
                        case "outTangent":
                            if (!TryJsonTokenToFloat(field.Value, out number)) return "Key " + i + " outTangent '" + field.Value + "' is not a number. Nothing was applied.";
                            key.outTangent = number;
                            break;
                        case "inWeight":
                            if (!TryJsonTokenToFloat(field.Value, out number)) return "Key " + i + " inWeight '" + field.Value + "' is not a number. Nothing was applied.";
                            key.inWeight = number;
                            break;
                        case "outWeight":
                            if (!TryJsonTokenToFloat(field.Value, out number)) return "Key " + i + " outWeight '" + field.Value + "' is not a number. Nothing was applied.";
                            key.outWeight = number;
                            break;
                        case "weightedMode":
                            WeightedMode weightedMode;
                            if (!TryJsonTokenToEnum(field.Value, out weightedMode)) return "Key " + i + " weightedMode '" + field.Value + "' is not one of " + string.Join(", ", Enum.GetNames(typeof(WeightedMode))) + ". Nothing was applied.";
                            key.weightedMode = weightedMode;
                            break;
                        default:
                            return "Unknown key field '" + field.Key + "' on key " + i + ". Use time, value, inTangent, outTangent, inWeight, outWeight, weightedMode. Nothing was applied.";
                    }
                }

                if (!hasTime || !hasValue) return "Key " + i + " needs both \"time\" and \"value\". Nothing was applied.";
                keys[i] = key;
            }

            var floatCurveBefore = AnimationUtility.GetEditorCurve(clip, binding);
            var replaced = floatCurveBefore != null;
            AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(keys));
            PersistAnimationAssetEdit(clip);
            return (replaced ? "Replaced" : "Set") + " float curve [" + targetPath + "/" + type.Name + "/" + propertyName + "] on " + assetPath + ": " +
                DescribeCurveKeys(floatCurveBefore) + " -> " + DescribeCurveKeys(AnimationUtility.GetEditorCurve(clip, binding)) +
                ", clip length now " + JsonNumber(clip.length) + "s.";
        }

        /// <summary>Lists a float curve's keys as time and value at full precision, summarising past the display limit.</summary>
        /// <param name="curve">Curve to describe, or null.</param>
        /// <returns>The key count and the keys, or "no curve".</returns>
        private static string DescribeCurveKeys(AnimationCurve curve)
        {
            if (curve == null) return "no curve";

            var keys = curve.keys;
            var builder = new StringBuilder();
            builder.Append(keys.Length).Append(" key(s)");
            var shown = Math.Min(keys.Length, ArrayDescriptionElementLimit);
            for (var i = 0; i < shown; i++)
            {
                builder.Append(i == 0 ? ": " : ", ").Append("t ").Append(JsonNumber(keys[i].time))
                    .Append(" = ").Append(JsonNumber(keys[i].value));
            }

            if (keys.Length > shown) builder.Append(", +").Append(keys.Length - shown).Append(" more");
            return builder.ToString();
        }

        /// <summary>Lists an object-reference curve's keys as time and asset, summarising past the display limit.</summary>
        /// <param name="keys">Keys to describe, or null.</param>
        /// <returns>The key count and the keys, or "no curve".</returns>
        private static string DescribeReferenceKeys(ObjectReferenceKeyframe[] keys)
        {
            if (keys == null) return "no curve";

            var builder = new StringBuilder();
            builder.Append(keys.Length).Append(" key(s)");
            var shown = Math.Min(keys.Length, ArrayDescriptionElementLimit);
            for (var i = 0; i < shown; i++)
            {
                var value = keys[i].value;
                builder.Append(i == 0 ? ": " : ", ").Append("t ").Append(JsonNumber(keys[i].time))
                    .Append(" = ").Append(value == null ? "null" : value.name);
            }

            if (keys.Length > shown) builder.Append(", +").Append(keys.Length - shown).Append(" more");
            return builder.ToString();
        }

        /// <summary>
        /// Reads a clip's full authoring view as JSON: frameRate, length, loop settings, every
        /// float and object-reference curve binding (with key counts), and events - the read-back
        /// companion to the clip write verbs.
        /// </summary>
        /// <param name="args">The clip asset path, GUID, or instance ID.</param>
        /// <returns>A JSON object describing the clip, or a lookup error.</returns>
        private static string AnimationClipGetCommand(string args)
        {
            UnityEngine.Object asset;
            string assetPath;
            var error = TryFindAssetObject(args, out asset, out assetPath);
            if (error != null)
            {
                return error;
            }

            var clip = asset as AnimationClip;
            if (clip == null)
            {
                return "Asset is not an AnimationClip: " + assetPath + " (" + asset.GetType().FullName + ").";
            }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            var sb = new StringBuilder();
            sb.Append("{\"asset\":").Append(JsonObjectRef(clip))
              .Append(",\"frameRate\":").Append(JsonNumber(clip.frameRate))
              .Append(",\"length\":").Append(JsonNumber(clip.length))
              .Append(",\"settings\":{")
              .Append("\"loopTime\":").Append(settings.loopTime ? "true" : "false")
              .Append(",\"loopBlend\":").Append(settings.loopBlend ? "true" : "false")
              .Append(",\"cycleOffset\":").Append(JsonNumber(settings.cycleOffset))
              .Append(",\"loopBlendOrientation\":").Append(settings.loopBlendOrientation ? "true" : "false")
              .Append(",\"loopBlendPositionY\":").Append(settings.loopBlendPositionY ? "true" : "false")
              .Append(",\"loopBlendPositionXZ\":").Append(settings.loopBlendPositionXZ ? "true" : "false")
              .Append(",\"keepOriginalOrientation\":").Append(settings.keepOriginalOrientation ? "true" : "false")
              .Append(",\"keepOriginalPositionY\":").Append(settings.keepOriginalPositionY ? "true" : "false")
              .Append(",\"keepOriginalPositionXZ\":").Append(settings.keepOriginalPositionXZ ? "true" : "false")
              .Append(",\"heightFromFeet\":").Append(settings.heightFromFeet ? "true" : "false")
              .Append(",\"mirror\":").Append(settings.mirror ? "true" : "false")
              .Append('}');

            sb.Append(",\"curves\":[");
            var bindings = AnimationUtility.GetCurveBindings(clip);
            for (var i = 0; i < bindings.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var curve = AnimationUtility.GetEditorCurve(clip, bindings[i]);
                sb.Append("{\"path\":").Append(JsonQuote(bindings[i].path))
                  .Append(",\"type\":").Append(JsonQuote(bindings[i].type == null ? "(unknown)" : bindings[i].type.Name))
                  .Append(",\"property\":").Append(JsonQuote(bindings[i].propertyName))
                  .Append(",\"keys\":").Append(curve == null ? 0 : curve.keys.Length)
                  .Append('}');
            }

            sb.Append("],\"objectReferenceCurves\":[");
            var referenceBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
            for (var i = 0; i < referenceBindings.Length; i++)
            {
                if (i > 0) sb.Append(',');
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, referenceBindings[i]);
                sb.Append("{\"path\":").Append(JsonQuote(referenceBindings[i].path))
                  .Append(",\"type\":").Append(JsonQuote(referenceBindings[i].type == null ? "(unknown)" : referenceBindings[i].type.Name))
                  .Append(",\"property\":").Append(JsonQuote(referenceBindings[i].propertyName))
                  .Append(",\"keys\":").Append(keyframes == null ? 0 : keyframes.Length)
                  .Append('}');
            }

            sb.Append("],\"events\":[");
            var events = AnimationUtility.GetAnimationEvents(clip);
            for (var i = 0; i < events.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"time\":").Append(JsonNumber(events[i].time))
                  .Append(",\"functionName\":").Append(JsonQuote(events[i].functionName))
                  .Append(",\"floatParameter\":").Append(JsonNumber(events[i].floatParameter))
                  .Append(",\"intParameter\":").Append(events[i].intParameter)
                  .Append(",\"stringParameter\":").Append(JsonQuote(events[i].stringParameter))
                  .Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }
    }
}
