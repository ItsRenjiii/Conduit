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
    /// Animator parameters, layers and avatar masks.
    /// </summary>
    internal static partial class ConduitBridge
    {



        /// <summary>
        /// Adds a controller parameter, or updates an existing one's default value. Type is one of
        /// float, int, bool, or trigger; a type change on an existing parameter is refused because
        /// it would silently break every condition and blend tree using it.
        /// </summary>
        /// <param name="args">Pipe-separated controller path, parameter name, type, and default value.</param>
        /// <returns>An evidence line stating exactly what was added or changed, or an error.</returns>
        private static string AnimatorParameterSetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return "Usage: animator-parameter-set <controllerPath> | <name> | <float|int|bool|trigger> [| <defaultValue>]";
            }

            AnimatorController controller;
            string assetPath;
            var error = TryLoadAnimatorController(sections[0], out controller, out assetPath);
            if (error != null)
            {
                return error;
            }

            var name = sections[1].Trim().Trim('"');
            if (name.Length == 0)
            {
                return "Missing parameter name.";
            }

            AnimatorControllerParameterType type;
            var typeToken = sections[2].Trim().Trim('"').ToLowerInvariant();
            switch (typeToken)
            {
                case "float":   type = AnimatorControllerParameterType.Float;   break;
                case "int":     type = AnimatorControllerParameterType.Int;     break;
                case "bool":    type = AnimatorControllerParameterType.Bool;    break;
                case "trigger": type = AnimatorControllerParameterType.Trigger; break;
                default:        return "Unknown parameter type '" + sections[2] + "'. Use float, int, bool, or trigger.";
            }

            var defaultToken = sections.Length > 3 ? sections[3].Trim() : string.Empty;
            float defaultFloat = 0f;
            var defaultInt = 0;
            var defaultBool = false;
            if (defaultToken.Length > 0)
            {
                if (type == AnimatorControllerParameterType.Float && !TryJsonTokenToFloat(defaultToken, out defaultFloat))
                {
                    return "Default value '" + defaultToken + "' is not a float.";
                }

                if (type == AnimatorControllerParameterType.Int && !int.TryParse(defaultToken.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out defaultInt))
                {
                    return "Default value '" + defaultToken + "' is not an int.";
                }

                if (type == AnimatorControllerParameterType.Bool && !TryJsonTokenToBool(defaultToken, out defaultBool))
                {
                    return "Default value '" + defaultToken + "' is not a bool.";
                }

                if (type == AnimatorControllerParameterType.Trigger)
                {
                    return "Trigger parameters have no default value; omit the fourth section.";
                }
            }

            // The parameters array is a copy - mutate the matching element and write the array back.
            var parameters = controller.parameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (!string.Equals(parameters[i].name, name, StringComparison.Ordinal))
                {
                    continue;
                }

                if (parameters[i].type != type)
                {
                    return "Parameter '" + name + "' already exists as " + parameters[i].type +
                        "; refusing to change its type to " + type + " because conditions and blend trees referencing it would silently break. Remove and re-add it deliberately if that is intended.";
                }

                if (defaultToken.Length == 0)
                {
                    return "Parameter '" + name + "' (" + type + ") already exists; nothing changed.";
                }

                string change;
                switch (type)
                {
                    case AnimatorControllerParameterType.Float:
                        if (Mathf.Approximately(parameters[i].defaultFloat, defaultFloat)) return "Parameter '" + name + "' defaultFloat already " + JsonNumber(defaultFloat) + "; nothing changed.";
                        change = "defaultFloat " + JsonNumber(parameters[i].defaultFloat) + " -> " + JsonNumber(defaultFloat);
                        parameters[i].defaultFloat = defaultFloat;
                        break;
                    case AnimatorControllerParameterType.Int:
                        if (parameters[i].defaultInt == defaultInt) return "Parameter '" + name + "' defaultInt already " + defaultInt + "; nothing changed.";
                        change = "defaultInt " + parameters[i].defaultInt + " -> " + defaultInt;
                        parameters[i].defaultInt = defaultInt;
                        break;
                    default:
                        if (parameters[i].defaultBool == defaultBool) return "Parameter '" + name + "' defaultBool already " + defaultBool.ToString().ToLowerInvariant() + "; nothing changed.";
                        change = "defaultBool " + parameters[i].defaultBool.ToString().ToLowerInvariant() + " -> " + defaultBool.ToString().ToLowerInvariant();
                        parameters[i].defaultBool = defaultBool;
                        break;
                }

                controller.parameters = parameters;
                PersistAnimationAssetEdit(controller);
                return "Updated parameter '" + name + "' on " + assetPath + ": " + change + ".";
            }

            var parameter = new AnimatorControllerParameter
            {
                name         = name,
                type         = type,
                defaultFloat = defaultFloat,
                defaultInt   = defaultInt,
                defaultBool  = defaultBool
            };
            controller.AddParameter(parameter);
            PersistAnimationAssetEdit(controller);
            return "Added parameter '" + name + "' (" + type + ") to " + assetPath +
                (defaultToken.Length > 0 ? " with default " + defaultToken.Trim('"') : string.Empty) + ".";
        }

        /// <summary>
        /// Removes a controller parameter by name, listing the available names when the lookup misses.
        /// </summary>
        /// <param name="args">Pipe-separated controller path and parameter name.</param>
        /// <returns>A confirmation naming the removed parameter, or an error.</returns>
        private static string AnimatorParameterRemoveCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: animator-parameter-remove <controllerPath> | <name>";
            }

            AnimatorController controller;
            string assetPath;
            var error = TryLoadAnimatorController(sections[0], out controller, out assetPath);
            if (error != null)
            {
                return error;
            }

            var name = sections[1].Trim().Trim('"');
            var parameters = controller.parameters;
            for (var i = 0; i < parameters.Length; i++)
            {
                if (string.Equals(parameters[i].name, name, StringComparison.Ordinal))
                {
                    var type = parameters[i].type;
                    controller.RemoveParameter(i);
                    PersistAnimationAssetEdit(controller);
                    return "Removed parameter '" + name + "' (" + type + ") from " + assetPath + ". Conditions that referenced it keep the stale name until edited.";
                }
            }

            var names = new string[parameters.Length];
            for (var i = 0; i < parameters.Length; i++) names[i] = parameters[i].name;
            return "Parameter not found on " + assetPath + ": '" + name + "'. Available: " + (names.Length == 0 ? "(none)" : string.Join(", ", names)) + ".";
        }



        /// <summary>
        /// Adds a layer by name, or updates an existing layer's properties from a flat JSON payload:
        /// defaultWeight, blendingMode (Override/Additive), avatarMask (asset path or null), ikPass,
        /// syncedLayerIndex, syncedLayerAffectsTiming. The whole payload validates before anything
        /// is applied, and the report lists each property as set (old -&gt; new) or already-at-value.
        /// </summary>
        /// <param name="args">Pipe-separated controller path, layer name/index, and options JSON.</param>
        /// <returns>A per-property evidence report, or an error with nothing applied.</returns>
        private static string AnimatorLayerSetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: animator-layer-set <controllerPath> | <layerNameOrIndex> [| {\"defaultWeight\":1,\"blendingMode\":\"Override|Additive\",\"avatarMask\":\"Assets/....mask\"|null,\"ikPass\":false,\"syncedLayerIndex\":-1,\"syncedLayerAffectsTiming\":false}]";
            }

            AnimatorController controller;
            string assetPath;
            var error = TryLoadAnimatorController(sections[0], out controller, out assetPath);
            if (error != null)
            {
                return error;
            }

            List<KeyValuePair<string, string>> fields;
            error = TryParsePayloadSection(sections.Length > 2 ? sections[2] : string.Empty, out fields);
            if (error != null)
            {
                return error;
            }

            AnimatorControllerLayer[] layers;
            int layerIndex;
            var created = false;
            if (TryResolveAnimatorLayer(controller, sections[1], out layers, out layerIndex) != null)
            {
                // Layer addition is name-based only; an unmatched numeric selector stays an error.
                var name = sections[1].Trim().Trim('"');
                int numeric;
                if (int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
                {
                    return "Layer index " + numeric + " does not exist on " + assetPath + " (" + layers.Length + " layer(s)); to add a layer, select it by name.";
                }

                // AddLayer(name) appends a fully-initialized layer (state machine sub-asset included).
                controller.AddLayer(name);
                layers = controller.layers;
                layerIndex = layers.Length - 1;
                created = true;
            }

            // Pass 1 - validate the whole payload; nothing mutates until every field resolves.
            float? defaultWeight = null;
            AnimatorLayerBlendingMode? blendingMode = null;
            var setMask = false;
            AvatarMask mask = null;
            bool? ikPass = null;
            int? syncedLayerIndex = null;
            bool? syncedLayerAffectsTiming = null;
            var unknownFields = new List<string>();

            foreach (var entry in fields)
            {
                switch (entry.Key)
                {
                    case "defaultWeight":
                        float weight;
                        if (!TryJsonTokenToFloat(entry.Value, out weight)) return "defaultWeight '" + entry.Value + "' is not a number. Nothing was applied.";
                        defaultWeight = Mathf.Clamp01(weight);
                        break;

                    case "blendingMode":
                        AnimatorLayerBlendingMode mode;
                        if (!TryJsonTokenToEnum(entry.Value, out mode)) return "blendingMode '" + entry.Value + "' is not Override or Additive. Nothing was applied.";
                        blendingMode = mode;
                        break;

                    case "avatarMask":
                        setMask = true;
                        var maskToken = JsonTokenToString(entry.Value);
                        if (maskToken != null)
                        {
                            mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(NormalizeAssetPath(maskToken));
                            if (mask == null) return "AvatarMask not found: " + maskToken + ". Nothing was applied.";
                        }

                        break;

                    case "ikPass":
                        bool ik;
                        if (!TryJsonTokenToBool(entry.Value, out ik)) return "ikPass '" + entry.Value + "' is not a bool. Nothing was applied.";
                        ikPass = ik;
                        break;

                    case "syncedLayerIndex":
                        int synced;
                        if (!int.TryParse(entry.Value.Trim().Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out synced)) return "syncedLayerIndex '" + entry.Value + "' is not an int. Nothing was applied.";
                        if (synced >= layers.Length || synced == layerIndex) return "syncedLayerIndex " + synced + " is invalid (must be another existing layer, or -1 to clear). Nothing was applied.";
                        syncedLayerIndex = synced;
                        break;

                    case "syncedLayerAffectsTiming":
                        bool timing;
                        if (!TryJsonTokenToBool(entry.Value, out timing)) return "syncedLayerAffectsTiming '" + entry.Value + "' is not a bool. Nothing was applied.";
                        syncedLayerAffectsTiming = timing;
                        break;

                    default:
                        unknownFields.Add(entry.Key);
                        break;
                }
            }

            // Pass 2 - apply to the array copy, then write the whole array back (copy-on-read semantics).
            var layer = layers[layerIndex];
            var report = new List<string>();
            if (defaultWeight.HasValue)
            {
                if (Mathf.Approximately(layer.defaultWeight, defaultWeight.Value)) report.Add("defaultWeight already " + JsonNumber(layer.defaultWeight));
                else { report.Add("defaultWeight " + JsonNumber(layer.defaultWeight) + " -> " + JsonNumber(defaultWeight.Value)); layer.defaultWeight = defaultWeight.Value; }
            }

            if (blendingMode.HasValue)
            {
                if (layer.blendingMode == blendingMode.Value) report.Add("blendingMode already " + layer.blendingMode);
                else { report.Add("blendingMode " + layer.blendingMode + " -> " + blendingMode.Value); layer.blendingMode = blendingMode.Value; }
            }

            if (setMask)
            {
                var oldName = layer.avatarMask == null ? "null" : layer.avatarMask.name;
                var newName = mask == null ? "null" : mask.name;
                if (layer.avatarMask == mask) report.Add("avatarMask already " + oldName);
                else { report.Add("avatarMask " + oldName + " -> " + newName); layer.avatarMask = mask; }
            }

            if (ikPass.HasValue)
            {
                if (layer.iKPass == ikPass.Value) report.Add("ikPass already " + layer.iKPass.ToString().ToLowerInvariant());
                else { report.Add("ikPass " + layer.iKPass.ToString().ToLowerInvariant() + " -> " + ikPass.Value.ToString().ToLowerInvariant()); layer.iKPass = ikPass.Value; }
            }

            if (syncedLayerIndex.HasValue)
            {
                if (layer.syncedLayerIndex == syncedLayerIndex.Value) report.Add("syncedLayerIndex already " + layer.syncedLayerIndex);
                else { report.Add("syncedLayerIndex " + layer.syncedLayerIndex + " -> " + syncedLayerIndex.Value); layer.syncedLayerIndex = syncedLayerIndex.Value; }
            }

            if (syncedLayerAffectsTiming.HasValue)
            {
                if (layer.syncedLayerAffectsTiming == syncedLayerAffectsTiming.Value) report.Add("syncedLayerAffectsTiming already " + layer.syncedLayerAffectsTiming.ToString().ToLowerInvariant());
                else { report.Add("syncedLayerAffectsTiming " + layer.syncedLayerAffectsTiming.ToString().ToLowerInvariant() + " -> " + syncedLayerAffectsTiming.Value.ToString().ToLowerInvariant()); layer.syncedLayerAffectsTiming = syncedLayerAffectsTiming.Value; }
            }

            layers[layerIndex] = layer;
            controller.layers = layers;
            PersistAnimationAssetEdit(controller);

            var sb = new StringBuilder();
            sb.Append(created ? "Added layer '" : "Layer '").Append(layers[layerIndex].name)
              .Append(created ? "' (index " : "' (index ").Append(layerIndex).Append(") on ").Append(assetPath).Append('.');
            if (report.Count > 0) sb.Append(' ').Append(string.Join("; ", report)).Append('.');
            else if (!created) sb.Append(" No properties supplied; nothing changed.");
            if (unknownFields.Count > 0) sb.Append(" WARNING ignored unknown field(s): ").Append(string.Join(", ", unknownFields)).Append('.');
            return sb.ToString();
        }

        /// <summary>
        /// Removes a layer by name or index. Removing the last remaining layer is refused because a
        /// controller with zero layers is unusable and the editor cannot recreate the base layer.
        /// </summary>
        /// <param name="args">Pipe-separated controller path and layer name/index.</param>
        /// <returns>A confirmation naming the removed layer, or an error.</returns>
        private static string AnimatorLayerRemoveCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: animator-layer-remove <controllerPath> | <layerNameOrIndex>";
            }

            AnimatorController controller;
            string assetPath;
            var error = TryLoadAnimatorController(sections[0], out controller, out assetPath);
            if (error != null)
            {
                return error;
            }

            AnimatorControllerLayer[] layers;
            int layerIndex;
            error = TryResolveAnimatorLayer(controller, sections[1], out layers, out layerIndex);
            if (error != null)
            {
                return error;
            }

            if (layers.Length == 1)
            {
                return "Refusing to remove the only layer ('" + layers[0].name + "') - a controller with zero layers is unusable. Delete the controller instead if that is the intent.";
            }

            var removedName = layers[layerIndex].name;
            controller.RemoveLayer(layerIndex);
            PersistAnimationAssetEdit(controller);
            return "Removed layer '" + removedName + "' (was index " + layerIndex + ") from " + assetPath + ". Layer indices above it shifted down by one; re-check syncedLayerIndex values that pointed past it.";
        }



        /// <summary>
        /// Creates an AvatarMask asset. All humanoid body parts start enabled (Unity's default);
        /// pass a payload to configure parts and transform paths in the same call via the shared
        /// apply path of <see cref="AnimatorMaskSetCommand"/>.
        /// </summary>
        /// <param name="args">Pipe-separated Assets/*.mask path and optional configuration JSON.</param>
        /// <returns>A creation confirmation plus the configuration report, or an error.</returns>
        private static string AnimatorMaskCreateCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 1 || string.IsNullOrWhiteSpace(sections[0]))
            {
                return "Usage: animator-mask-create <assetPath> [| json] - path must be an Assets/*.mask path.";
            }

            var assetPath = NormalizeAssetPath(sections[0]);
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !assetPath.EndsWith(".mask", StringComparison.OrdinalIgnoreCase))
            {
                return "Mask path must be an Assets/*.mask path.";
            }

            if (AssetDatabase.LoadAssetAtPath<AvatarMask>(assetPath) != null)
            {
                return "AvatarMask already exists: " + assetPath + ". Edit it with animator-mask-set.";
            }

            EnsureAssetFolder(Path.GetDirectoryName(assetPath));
            var mask = new AvatarMask();
            AssetDatabase.CreateAsset(mask, assetPath);
            AssetDatabase.SaveAssets();

            if (sections.Length >= 2 && !string.IsNullOrWhiteSpace(sections[1]))
            {
                // Re-join every trailing section so a payload is passed through whole even if it
                // happened to contain a pipe character inside a string value.
                var payload = string.Join(" | ", sections, 1, sections.Length - 1);
                var applyReport = AnimatorMaskSetCommand(assetPath + " | " + payload);
                return "Created AvatarMask " + assetPath + ". " + applyReport;
            }

            return "Created AvatarMask " + assetPath + " (all humanoid body parts enabled, no transform paths).";
        }

        /// <summary>
        /// Configures an AvatarMask from a flat JSON payload. <c>humanoid</c> maps
        /// AvatarMaskBodyPart names to bools (e.g. <c>{"LeftArm":true}</c>), <c>allHumanoid</c>
        /// sets every part at once (applied before per-part entries), and <c>transforms</c>
        /// REPLACES the generic transform list with <c>[{"path":"Armature/Hips","active":true}]</c>
        /// entries. Validates fully before applying and reports each change with evidence.
        /// </summary>
        /// <param name="args">Pipe-separated mask asset path and configuration JSON.</param>
        /// <returns>A per-change evidence report, or an error with nothing applied.</returns>
        private static string AnimatorMaskSetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: animator-mask-set <maskPath> | {\"allHumanoid\":bool, \"humanoid\":{\"Head\":true,...}, \"transforms\":[{\"path\":\"...\",\"active\":true},...]}";
            }

            var assetPath = NormalizeAssetPath(sections[0]);
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(assetPath);
            if (mask == null)
            {
                return "AvatarMask not found: " + assetPath;
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

            // Pass 1 - validate everything into staged values.
            bool? allHumanoid = null;
            var humanoidParts = new List<KeyValuePair<AvatarMaskBodyPart, bool>>();
            List<KeyValuePair<string, bool>> transforms = null;
            var unknownFields = new List<string>();

            foreach (var entry in fields)
            {
                switch (entry.Key)
                {
                    case "allHumanoid":
                        bool all;
                        if (!TryJsonTokenToBool(entry.Value, out all)) return "allHumanoid '" + entry.Value + "' is not a bool. Nothing was applied.";
                        allHumanoid = all;
                        break;

                    case "humanoid":
                        List<KeyValuePair<string, string>> partFields;
                        if (!TrySplitFlatJsonObject(entry.Value, out partFields)) return "humanoid must be a {\"BodyPart\": bool} object. Nothing was applied.";
                        foreach (var part in partFields)
                        {
                            AvatarMaskBodyPart bodyPart;
                            if (!TryJsonTokenToEnum("\"" + part.Key + "\"", out bodyPart) || bodyPart == AvatarMaskBodyPart.LastBodyPart)
                            {
                                return "Unknown humanoid body part '" + part.Key + "'. Valid: " + string.Join(", ", Enum.GetNames(typeof(AvatarMaskBodyPart))) + " (excluding LastBodyPart). Nothing was applied.";
                            }

                            bool active;
                            if (!TryJsonTokenToBool(part.Value, out active)) return "humanoid." + part.Key + " '" + part.Value + "' is not a bool. Nothing was applied.";
                            humanoidParts.Add(new KeyValuePair<AvatarMaskBodyPart, bool>(bodyPart, active));
                        }

                        break;

                    case "transforms":
                        List<string> items;
                        if (!TrySplitJsonArray(entry.Value, out items)) return "transforms must be a JSON array. Nothing was applied.";
                        transforms = new List<KeyValuePair<string, bool>>();
                        foreach (var item in items)
                        {
                            List<KeyValuePair<string, string>> transformFields;
                            if (!TrySplitFlatJsonObject(item, out transformFields)) return "transforms entries must be {\"path\":\"...\",\"active\":bool} objects. Nothing was applied.";
                            string path = null;
                            var active = true;
                            foreach (var field in transformFields)
                            {
                                if (field.Key == "path") path = JsonTokenToString(field.Value);
                                else if (field.Key == "active" && !TryJsonTokenToBool(field.Value, out active)) return "transforms[].active '" + field.Value + "' is not a bool. Nothing was applied.";
                            }

                            if (string.IsNullOrEmpty(path)) return "A transforms entry is missing its \"path\". Nothing was applied.";
                            transforms.Add(new KeyValuePair<string, bool>(path, active));
                        }

                        break;

                    default:
                        unknownFields.Add(entry.Key);
                        break;
                }
            }

            // Pass 2 - apply.
            var report = new List<string>();
            if (allHumanoid.HasValue)
            {
                for (var part = (AvatarMaskBodyPart)0; part < AvatarMaskBodyPart.LastBodyPart; part++)
                {
                    mask.SetHumanoidBodyPartActive(part, allHumanoid.Value);
                }

                report.Add("all humanoid parts -> " + allHumanoid.Value.ToString().ToLowerInvariant());
            }

            foreach (var part in humanoidParts)
            {
                var old = mask.GetHumanoidBodyPartActive(part.Key);
                if (old == part.Value) report.Add(part.Key + " already " + old.ToString().ToLowerInvariant());
                else { mask.SetHumanoidBodyPartActive(part.Key, part.Value); report.Add(part.Key + " " + old.ToString().ToLowerInvariant() + " -> " + part.Value.ToString().ToLowerInvariant()); }
            }

            if (transforms != null)
            {
                var oldCount = mask.transformCount;
                mask.transformCount = transforms.Count;
                for (var i = 0; i < transforms.Count; i++)
                {
                    mask.SetTransformPath(i, transforms[i].Key);
                    mask.SetTransformActive(i, transforms[i].Value);
                }

                report.Add("transform list replaced (" + oldCount + " -> " + transforms.Count + " entries)");
            }

            PersistAnimationAssetEdit(mask);

            var sb = new StringBuilder();
            sb.Append("Configured AvatarMask ").Append(assetPath).Append(": ").Append(string.Join("; ", report)).Append('.');
            if (unknownFields.Count > 0) sb.Append(" WARNING ignored unknown field(s): ").Append(string.Join(", ", unknownFields)).Append('.');
            return sb.ToString();
        }
    }
}
