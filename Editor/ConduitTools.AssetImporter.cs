/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// The assets-importer-get / assets-importer-modify pair: read and write an asset's AssetImporter
    /// settings, which are a DIFFERENT object from the asset itself.
    /// <para>
    /// <c>assets-modify</c> writes the imported ASSET (the <c>Texture2D</c>), so nothing there can reach
    /// <c>textureType</c>, <c>spriteMode</c>, compression, mip generation or max size. Without these verbs
    /// the only route to an importer setting was hand-editing the <c>.meta</c>, which means inventing YAML
    /// for a serialization format nobody validated. A PNG brought in for UI arrives as
    /// <c>textureType: 0</c> (Default), which a uGUI <c>Image</c> cannot use at all.
    /// </para>
    /// <para>
    /// Deliberately importer-TYPE agnostic: everything goes through <see cref="SerializedObject"/>, so the
    /// same two verbs cover TextureImporter, ModelImporter, AudioImporter and any other without a line of
    /// per-type code. GET exists alongside MODIFY because a write-only verb leaves the caller unable to
    /// discover the property names or verify the result, which is how guesswork starts.
    /// </para>
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Asset Importer Read

        /// <summary>
        /// Reports an asset's importer type and every serialized importer setting with its current value.
        /// This is the discovery half of the pair: the names it prints are exactly the names
        /// <c>assets-importer-modify</c> accepts.
        /// </summary>
        /// <param name="args">Asset path of the imported asset.</param>
        /// <returns>Importer type plus its serialized settings as JSON, or a precise refusal.</returns>
        private static string AssetImporterGetCommand(string args)
        {
            AssetImporter importer;
            string assetPath;
            string error = ResolveAssetImporter(args, out importer, out assetPath);
            if (error != null)
            {
                return error;
            }

            return "{\"assetPath\":" + JsonQuote(assetPath) +
                ",\"importerType\":" + JsonQuote(importer.GetType().FullName) +
                ",\"settings\":" + JsonSerializedProperties(importer) + "}";
        }

        #endregion

        #region Asset Importer Write

        /// <summary>
        /// Applies a flat JSON payload of importer settings, then saves and reimports the asset so the
        /// change actually takes effect. EVERY field is resolved and parsed before anything is written, so
        /// one bad name or unparseable value refuses the whole payload with the importer untouched.
        /// </summary>
        /// <param name="args">Pipe-separated asset path and flat JSON: <c>&lt;assetPath&gt; | &lt;json&gt;</c>.</param>
        /// <returns>Per-field before/after evidence, or a precise refusal.</returns>
        private static string AssetImporterModifyCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: assets-importer-modify <assetPath> | <json>  " +
                    "Run assets-importer-get on the same asset to list the real setting names.";
            }

            AssetImporter importer;
            string assetPath;
            string error = ResolveAssetImporter(sections[0], out importer, out assetPath);
            if (error != null)
            {
                return error;
            }

            try
            {
                // A stale MCP server can deliver the JSON section wrapped in quotes with inner escapes; peel
                // that defensively so the shape check below judges the real payload, not the wrapper.
                string payload = UnwrapQuotedJsonSection(sections[1]);

                List<KeyValuePair<string, string>> fields;
                if (!TrySplitFlatJsonObject(payload, out fields))
                {
                    // No raw-overwrite fallback here, unlike gameobject-component-modify. EditorJsonUtility
                    // cannot reach an importer's native serialized data, so an overwrite would report
                    // success having changed nothing at all - the exact false positive this bar forbids.
                    return "Payload is not a flat {\"setting\": value} object. Importer settings must be sent " +
                        "as a flat object literal; there is no raw-overwrite fallback, because a JSON overwrite " +
                        "cannot reach an importer's native data and would report success having changed nothing. " +
                        "Nothing was applied.";
                }

                if (fields.Count == 0)
                {
                    return "Empty payload: no settings to modify on " + importer.GetType().Name + " for " + assetPath + ".";
                }

                // ── Pass 1: resolve and validate EVERYTHING before a single write ───────────────────
                var serializedObject = new SerializedObject(importer);
                var staged = new List<StagedComponentFieldWrite>();

                foreach (KeyValuePair<string, string> entry in fields)
                {
                    string propertyName;
                    SerializedProperty property = FindImporterProperty(serializedObject, entry.Key, out propertyName);
                    if (property == null)
                    {
                        return "Unknown importer setting '" + entry.Key + "' on " + importer.GetType().Name +
                            ". Nothing was applied. Available settings: " + AvailableImporterSettingNames(serializedObject);
                    }

                    var stage = new StagedComponentFieldWrite { Name = propertyName, Kind = property.propertyType };

                    if (property.isArray && property.propertyType != SerializedPropertyType.String)
                    {
                        string arrayError;
                        if (TryStageValueArrayWrite(importer, property, entry.Value, stage, out arrayError))
                        {
                            staged.Add(stage);
                            continue;
                        }

                        return "Failed to parse '" + entry.Key + "': " +
                            (arrayError ?? "no typed writer for the elements of this array.") + " Nothing was applied.";
                    }

                    if (property.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        // An importer's reference fields are native, so there is no C# field to reflect a
                        // concrete type from; the base Object type lets the resolver pass them through.
                        UnityEngine.Object reference;
                        string resolveError = ResolveReferenceFromJsonValue(entry.Value, typeof(UnityEngine.Object), out reference);
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
                            return "Failed to parse '" + entry.Key + "': " +
                                (parseError ?? ("no typed writer for a " + property.propertyType + " importer setting")) +
                                ". Nothing was applied.";
                        }
                    }

                    staged.Add(stage);
                }

                // ── Pass 2: read every named setting and the whole importer, write, reimport, read back ──
                var evidence = CaptureFieldEvidence(importer, staged, null);
                var stateBefore = CaptureTopLevelState(importer);

                // A texture's type is set through Unity's own setter, which also applies the settings that type
                // needs - for a Sprite, no power-of-two rescale, no mipmaps, a sprite mode - exactly as the
                // importer inspector does. Writing the raw field left a rescaled non-power-of-two image with no
                // Sprite at all. The payload's other fields are applied afterwards, so any it names still win.
                string typeNote = ApplyTextureTypeThroughImporter(importer, serializedObject, staged);
                foreach (StagedComponentFieldWrite stage in staged)
                {
                    ApplyStagedPropertyValue(serializedObject.FindProperty(stage.Name), stage);
                }

                serializedObject.ApplyModifiedProperties();

                // An importer setting means nothing until the asset is rebuilt through it, so this verb owns
                // the reimport rather than leaving the caller to remember one. SaveAndReimport is the same
                // call the importer inspector's Apply button makes.
                importer.SaveAndReimport();

                // Read back after the reimport, because importing can rewrite a setting the write just made or move
                // one nobody named, and the report has to say what the asset is now built with.
                AssetImporter written = AssetImporter.GetAtPath(assetPath);
                if (written == null) written = importer;
                var setFields = new List<string>();
                var unchangedFields = new List<string>();
                var notAppliedFields = new List<string>();
                ClassifyCommittedFields(written, evidence, setFields, unchangedFields, notAppliedFields);

                var namedPaths = new List<string>();
                foreach (FieldEvidence fieldEvidence in evidence)
                {
                    namedPaths.Add(fieldEvidence.Path);
                }

                List<string> unnamedChanges = DescribeUnnamedChanges(written, stateBefore, namedPaths);

                var report = new StringBuilder();
                report.Append(setFields.Count > 0 || unnamedChanges.Count > 0 ? "Modified " : "No change to ");
                report.Append(importer.GetType().Name).Append(" for ").Append(assetPath).Append('.');
                if (setFields.Count > 0)
                {
                    report.Append(" Set: ").Append(string.Join("; ", setFields)).Append('.');
                }

                if (unchangedFields.Count > 0)
                {
                    report.Append(" Already at value: ").Append(string.Join("; ", unchangedFields)).Append('.');
                }

                if (notAppliedFields.Count > 0)
                {
                    report.Append(" WARNING, not applied: ").Append(string.Join("; ", notAppliedFields)).Append('.');
                }

                if (typeNote != null)
                {
                    report.Append(' ').Append(typeNote);
                }

                if (unnamedChanges.Count > 0)
                {
                    report.Append(typeNote != null ? " Changed besides the named settings: " : " WARNING, the write or its reimport also changed: ")
                        .Append(string.Join("; ", unnamedChanges)).Append('.');
                }

                report.Append(" Asset was saved and reimported, and every value above is read back after the reimport.");
                return report.ToString();
            }
            catch (Exception ex)
            {
                return "Failed to modify importer for " + assetPath + ": " + ex.Message;
            }
        }

        #endregion

        #region Asset Importer Helpers

        /// <summary>
        /// When the payload sets a texture's type, sets it through <see cref="TextureImporter.textureType"/> so
        /// Unity applies that type's dependent settings, then refreshes the serialized view so the payload's
        /// other fields are written on top of them.
        /// </summary>
        /// <param name="importer">The asset's importer.</param>
        /// <param name="serializedObject">The serialized view of the importer the staged writes go through.</param>
        /// <param name="staged">The validated writes.</param>
        /// <returns>A sentence for the report when the type was set this way, otherwise null.</returns>
        private static string ApplyTextureTypeThroughImporter(AssetImporter importer, SerializedObject serializedObject,
            List<StagedComponentFieldWrite> staged)
        {
            var textureImporter = importer as TextureImporter;
            if (textureImporter == null) return null;

            StagedComponentFieldWrite typeStage = staged.Find(stage => stage.Name == "m_TextureType");
            if (typeStage == null) return null;

            int typeValue;
            if (typeStage.Kind == SerializedPropertyType.Enum && typeStage.EnumIndex >= 0)
            {
                string typeName = serializedObject.FindProperty(typeStage.Name).enumNames[typeStage.EnumIndex];
                if (!Enum.TryParse(typeName, true, out TextureImporterType parsed)) return null;
                typeValue = (int)parsed;
            }
            else
            {
                typeValue = (int)typeStage.LongValue;
            }

            if (!Enum.IsDefined(typeof(TextureImporterType), typeValue)) return null;

            var type = (TextureImporterType)typeValue;
            if (textureImporter.textureType == type) return null;

            textureImporter.textureType = type;
            serializedObject.Update();
            return "The texture type was set through Unity's importer, which applies the settings a " + type
                + " texture needs, as the importer inspector does; they are listed as changed besides the named settings.";
        }

        /// <summary>
        /// Resolves an asset path to its AssetImporter, distinguishing the failure modes so the caller is
        /// told which one happened rather than getting one vague miss.
        /// </summary>
        /// <param name="rawPath">Caller-supplied asset path, possibly quoted or absolute.</param>
        /// <param name="importer">Receives the resolved importer on success.</param>
        /// <param name="assetPath">Receives the normalized asset path.</param>
        /// <returns>Null on success, otherwise the refusal to return verbatim.</returns>
        private static string ResolveAssetImporter(string rawPath, out AssetImporter importer, out string assetPath)
        {
            importer = null;
            assetPath = NormalizeAssetPath((rawPath ?? string.Empty).Trim());

            if (string.IsNullOrEmpty(assetPath))
            {
                return "Missing asset path. Usage: assets-importer-get <assetPath>";
            }

            if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null && !AssetDatabase.IsValidFolder(assetPath))
            {
                return "No asset at " + assetPath + ". Check the path, or refresh if it was added outside the editor.";
            }

            importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
            {
                // Assets Unity generates rather than imports (a ScriptableObject, a material, a scene) have
                // no importer at all, which is a different problem from a wrong path.
                return assetPath + " has no AssetImporter. Only IMPORTED assets (textures, models, audio, " +
                    "fonts) carry importer settings; an asset created inside Unity is edited with assets-modify.";
            }

            return null;
        }

        /// <summary>
        /// Finds an importer setting by serialized name, accepting the <c>.meta</c> YAML spelling too.
        /// <para>
        /// The serialized property is <c>m_TextureType</c> while the <c>.meta</c> file writes
        /// <c>textureType</c> - and reading the meta was the ONLY way to learn these names before these
        /// verbs existed, so a caller who did that would otherwise be told a real setting does not exist.
        /// Both spellings resolve to the same property, and the report names the canonical one.
        /// </para>
        /// </summary>
        /// <param name="serializedObject">Serialized view of the importer.</param>
        /// <param name="requestedName">Setting name as the caller spelled it.</param>
        /// <param name="resolvedName">Receives the canonical serialized name that matched.</param>
        /// <returns>The matching property, or null when neither spelling exists.</returns>
        private static SerializedProperty FindImporterProperty(SerializedObject serializedObject, string requestedName, out string resolvedName)
        {
            resolvedName = requestedName;
            SerializedProperty property = serializedObject.FindProperty(requestedName);
            if (property != null)
            {
                return property;
            }

            if (string.IsNullOrEmpty(requestedName) || requestedName.StartsWith("m_", StringComparison.Ordinal))
            {
                return null;
            }

            string prefixed = "m_" + char.ToUpperInvariant(requestedName[0]) + requestedName.Substring(1);
            property = serializedObject.FindProperty(prefixed);
            if (property != null)
            {
                resolvedName = prefixed;
            }

            return property;
        }

        /// <summary>
        /// Comma-joined list of the importer's settable setting names, used to turn an unknown-name refusal
        /// into a discoverable one instead of a dead end.
        /// </summary>
        /// <param name="serializedObject">Serialized view of the importer.</param>
        /// <returns>The available top-level setting names.</returns>
        private static string AvailableImporterSettingNames(SerializedObject serializedObject)
        {
            var names = new List<string>();
            foreach (SerializedProperty property in TopLevelReportableProperties(serializedObject))
            {
                names.Add(property.name);
            }

            return names.Count > 0 ? string.Join(", ", names) : "(none reported)";
        }

        #endregion
    }
}
