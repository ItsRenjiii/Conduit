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
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Material, shader and non-material asset writing, with per-property evidence.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Material verbs

        /// <summary>
        /// Creates a material asset with the requested shader.
        /// </summary>
        /// <param name="args">Pipe-separated material path and shader name.</param>
        /// <returns>A confirmation line or a validation error.</returns>
        private static string AssetsMaterialCreateCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: assets-material-create <assetPath> | <shaderName>";
            }

            var assetPath = NormalizeAssetPath(sections[0]);
            if (!assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || !assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                return "Material path must be an Assets/*.mat path.";
            }

            var shader = Shader.Find(sections[1].Trim().Trim('"'));
            if (shader == null)
            {
                return "Shader not found: " + sections[1];
            }

            EnsureAssetFolder(Path.GetDirectoryName(assetPath));
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return "Created material " + assetPath + ".";
        }

        /// <summary>
        /// Assigns a texture asset to a named shader texture property on a material asset.
        /// </summary>
        /// <param name="args">Pipe-separated material path, shader property name, and texture asset path.</param>
        /// <returns>A confirmation line or a validation error.</returns>
        private static string AssetsMaterialSetTextureCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return "Usage: assets-material-set-texture <materialPath> | <propertyName> | <texturePath>";
            }

            var materialPath = NormalizeAssetPath(sections[0]);
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                return "Material not found: " + materialPath;
            }

            var propertyName = sections[1].Trim();
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return "Missing shader texture property name.";
            }

            if (!material.HasProperty(propertyName))
            {
                return "Material " + materialPath + " does not have shader property " + propertyName + ".";
            }

            var texturePath = NormalizeAssetPath(sections[2]);
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (texture == null)
            {
                return "Texture not found: " + texturePath;
            }

            var before = DescribeTextureSlot(material.GetTexture(propertyName));
            material.SetTexture(propertyName, texture);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(materialPath, ImportAssetOptions.ForceSynchronousImport);

            // Read back from the material as the asset database now holds it, not from the reference just assigned.
            var reloaded = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            var after = DescribeTextureSlot(reloaded == null ? null : reloaded.GetTexture(propertyName));
            var asked = DescribeTextureSlot(texture);
            if (after == before)
            {
                return after == asked
                    ? propertyName + " on " + materialPath + " is already " + after + ". Nothing changed."
                    : "NOT applied: " + propertyName + " on " + materialPath + " is still " + after + "; asked for " + asked + ".";
            }

            return "Set " + propertyName + " on " + materialPath + ": " + before + " -> " + after +
                (after == asked ? "." : "; asked for " + asked + ", so something after the write changed it.");
        }

        /// <summary>Names the texture in a material slot by name and asset path, for the before and after evidence.</summary>
        /// <param name="texture">Texture in the slot, or null.</param>
        /// <returns>The description, or "none" for an empty slot.</returns>
        private static string DescribeTextureSlot(Texture texture)
        {
            return texture == null ? "none" : texture.name + " (" + AssetDatabase.GetAssetPath(texture) + ")";
        }

        /// <summary>
        /// Retargets a material asset onto another shader, which nothing else in the bridge could do:
        /// assets-modify resolves names against the CURRENT shader's property list, so m_Shader is
        /// unreachable there, and a JSON overwrite cannot touch it either.
        /// <para>
        /// The report is the point. A swap silently keeps only the properties the two shaders share by NAME,
        /// so textures sitting on slots the incoming shader does not declare are dropped with no warning, the
        /// incoming shader's own slots arrive empty, and the OUTGOING shader's keywords stay switched on. That
        /// last one is the quiet killer: the material still lists keywords the new shader never reads, so its
        /// features stay off and the result renders flat while looking wired. Every one of those is named here
        /// so the caller can re-wire without a follow-up read.
        /// </para>
        /// </summary>
        /// <param name="args">Pipe-separated material path, shader name or asset path, and optional keyword list.</param>
        /// <returns>An evidence-based report of what carried over, what was dropped, and what is left empty.</returns>
        private static string AssetsMaterialSetShaderCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: assets-material-set-shader <materialPath> | <shaderNameOrPath> [| <keyword,keyword,...>]";
            }

            var materialPath = NormalizeAssetPath(sections[0]);
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                return "Material not found: " + materialPath;
            }

            // Resolved BEFORE anything mutates, so an unknown shader leaves the material untouched.
            var shaderToken = sections[1].Trim().Trim('"');
            var shader = Shader.Find(shaderToken);
            if (shader == null)
            {
                shader = AssetDatabase.LoadAssetAtPath<Shader>(NormalizeAssetPath(shaderToken));
            }

            if (shader == null)
            {
                return "Shader not found by name or asset path: " + shaderToken;
            }

            string[] requestedKeywords = null;
            if (sections.Length >= 3 && !string.IsNullOrWhiteSpace(sections[2]))
            {
                requestedKeywords = sections[2].Split(',');
                for (var i = 0; i < requestedKeywords.Length; i++)
                {
                    requestedKeywords[i] = requestedKeywords[i].Trim().Trim('"');
                }
            }

            var previousShader = material.shader;
            var previousShaderName = previousShader == null ? "<none>" : previousShader.name;
            var previousKeywords = material.shaderKeywords;
            var carried = CollectMaterialTextures(material);

            material.shader = shader;
            if (requestedKeywords != null)
            {
                material.shaderKeywords = requestedKeywords;
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(materialPath, ImportAssetOptions.ForceSynchronousImport);

            // The shader is read back from the material, not taken from the one just assigned.
            var currentShader = material.shader;
            var currentShaderName = currentShader == null ? "<none>" : currentShader.name;
            var report = new StringBuilder();
            report.Append(previousShaderName == currentShaderName
                ? "Material " + materialPath + " already on shader " + currentShaderName + "."
                : "Material " + materialPath + " shader " + previousShaderName + " -> " + currentShaderName + ".");
            if (currentShader != shader)
            {
                report.Append(" WARNING: asked for ").Append(shader.name).Append(", but the material reads back ")
                    .Append(currentShaderName).Append('.');
            }

            var kept = new List<string>();
            var dropped = new List<string>();
            foreach (var slot in carried)
            {
                if (material.HasProperty(slot.Key))
                {
                    kept.Add(slot.Key + "=" + slot.Value);
                    continue;
                }

                dropped.Add(slot.Key + "=" + slot.Value);
            }

            if (kept.Count > 0)
            {
                report.Append(" Carried over: ").Append(string.Join(", ", kept.ToArray())).Append('.');
            }

            if (dropped.Count > 0)
            {
                report.Append(" DROPPED (slot absent on new shader): ").Append(string.Join(", ", dropped.ToArray())).Append('.');
            }

            var empty = new List<string>();
            foreach (var name in CollectShaderTextureProperties(shader))
            {
                if (material.GetTexture(name) == null)
                {
                    empty.Add(name);
                }
            }

            if (empty.Count > 0)
            {
                report.Append(" Empty texture slots on new shader: ").Append(string.Join(", ", empty.ToArray())).Append('.');
            }

            if (requestedKeywords != null)
            {
                report.Append(" Keywords: ").Append(DescribeKeywords(previousKeywords)).Append(" -> ")
                    .Append(DescribeKeywords(material.shaderKeywords)).Append('.');
                return report.ToString();
            }

            var stale = CollectStaleKeywords(shader, previousKeywords);
            if (stale.Count > 0)
            {
                report.Append(" WARNING, keywords carried from the old shader that the new one does not declare: ")
                    .Append(string.Join(", ", stale.ToArray()))
                    .Append(". Pass a keyword list to replace them, or the new shader's features stay off.");
            }

            return report.ToString();
        }

        /// <summary>Lists a material's keywords for the before and after evidence.</summary>
        /// <param name="keywords">Keywords, or null.</param>
        /// <returns>The keywords separated by commas, or "none".</returns>
        private static string DescribeKeywords(string[] keywords)
        {
            return keywords == null || keywords.Length == 0 ? "none" : string.Join(", ", keywords);
        }

        #endregion

        #region Shader inspection



        /// <summary>
        /// Returns compact shader metadata available through Unity's public Shader API.
        /// </summary>
        /// <param name="args">A shader name or shader asset path.</param>
        /// <returns>A JSON object describing the shader.</returns>
        private static string AssetsShaderGetDataCommand(string args)
        {
            var shader = FindShaderLoose(args);
            if (shader == null)
            {
                return "Shader not found: " + args;
            }

            return "{\"name\":" + JsonQuote(shader.name) +
                ",\"instanceID\":" + shader.GetInstanceID() +
                ",\"isSupported\":" + (shader.isSupported ? "true" : "false") +
                ",\"maximumLOD\":" + shader.maximumLOD +
                ",\"renderQueue\":" + shader.renderQueue +
                ",\"assetPath\":" + JsonQuote(AssetDatabase.GetAssetPath(shader)) + "}";
        }

        /// <summary>
        /// Lists loaded shaders with optional case-insensitive filtering.
        /// </summary>
        /// <param name="args">Optional name filter.</param>
        /// <returns>A compact JSON array of shaders.</returns>
        private static string AssetsShaderListAllCommand(string args)
        {
            var filter = (args ?? string.Empty).Trim().Trim('"');
            var shaders = Resources.FindObjectsOfTypeAll<Shader>();
            var builder = new StringBuilder();
            var count = 0;
            builder.Append('[');
            foreach (var shader in shaders)
            {
                if (shader == null)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(filter) && shader.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"name\":").Append(JsonQuote(shader.name))
                    .Append(",\"instanceID\":").Append(shader.GetInstanceID())
                    .Append(",\"isSupported\":").Append(shader.isSupported ? "true" : "false")
                    .Append('}');
                count++;
                if (count >= DefaultJsonResultLimit)
                {
                    break;
                }
            }

            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// Finds a shader by asset path first and Shader.Find name second.
        /// </summary>
        /// <param name="input">The shader path or shader name.</param>
        /// <returns>The shader when Unity can resolve it.</returns>
        private static Shader FindShaderLoose(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return null;
            }

            input = input.Trim().Trim('"');
            var assetPath = NormalizeAssetPath(input);
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(assetPath);
            return shader != null ? shader : Shader.Find(input);
        }

        /// <summary>Lists every texture property a material currently has a texture on.</summary>
        /// <param name="material">Material to snapshot.</param>
        /// <returns>Property name to texture name, for the shader the material is on right now.</returns>
        private static List<KeyValuePair<string, string>> CollectMaterialTextures(Material material)
        {
            var found = new List<KeyValuePair<string, string>>();
            foreach (var name in CollectShaderTextureProperties(material.shader))
            {
                var texture = material.GetTexture(name);
                if (texture != null)
                {
                    found.Add(new KeyValuePair<string, string>(name, texture.name));
                }
            }

            return found;
        }

        /// <summary>Lists the texture property names a shader declares.</summary>
        /// <param name="shader">Shader to read.</param>
        /// <returns>Every property of type TexEnv, in declaration order.</returns>
        private static List<string> CollectShaderTextureProperties(Shader shader)
        {
            var names = new List<string>();
            if (shader == null) return names;

            var count = ShaderUtil.GetPropertyCount(shader);
            for (var i = 0; i < count; i++)
            {
                if (ShaderUtil.GetPropertyType(shader, i) == ShaderUtil.ShaderPropertyType.TexEnv)
                {
                    names.Add(ShaderUtil.GetPropertyName(shader, i));
                }
            }

            return names;
        }

        /// <summary>Returns the keywords a material carries that the incoming shader does not declare.</summary>
        /// <param name="shader">Shader the material is being moved to.</param>
        /// <param name="keywords">Keywords the material carried before the swap.</param>
        /// <returns>The subset the new shader never reads.</returns>
        private static List<string> CollectStaleKeywords(Shader shader, string[] keywords)
        {
            var stale = new List<string>();
            if (keywords == null || keywords.Length == 0) return stale;

            var declared = new HashSet<string>(shader.keywordSpace.keywordNames);
            foreach (var keyword in keywords)
            {
                if (!declared.Contains(keyword))
                {
                    stale.Add(keyword);
                }
            }

            return stale;
        }

        #endregion

        #region Writing asset and material properties

        /// <summary>
        /// Modifies a serialized asset. Materials get a dedicated shader-property path, because their properties
        /// live inside m_SavedProperties where a JSON overwrite cannot reach them. Every other asset is written
        /// field by field through SerializedProperty, and each field is reported as Unity reads it back after the
        /// save and reimport.
        /// </summary>
        /// <param name="args">Pipe-separated asset path and JSON object.</param>
        /// <returns>An evidence-based report of what changed, or an error.</returns>
        private static string AssetsModifyCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: assets-modify <assetPath> | <json>";
            }

            UnityEngine.Object asset;
            string assetPath;
            var error = TryFindAssetObject(sections[0], out asset, out assetPath);
            if (error != null)
            {
                return error;
            }

            try
            {
                var payload = UnwrapQuotedJsonSection(sections[1]);

                var material = asset as Material;
                if (material != null)
                {
                    return ModifyMaterialProperties(material, assetPath, payload);
                }

                return ModifyAssetSerializedFields(asset, assetPath, payload);
            }
            catch (Exception ex)
            {
                return "Failed to modify asset " + assetPath + ": " + ex.Message;
            }
        }

        /// <summary>
        /// Writes a flat payload onto a non-material asset field by field through SerializedProperty.
        /// </summary>
        /// <remarks>
        /// A per-field write PATCHES the asset. The whole-object <c>EditorJsonUtility.FromJsonOverwrite</c>
        /// this replaces REBUILT it, and rebuilding moves fields nobody supplied: setting a RenderTexture's
        /// m_Height also reset m_ColorFormat, and setting an AnimationClip's m_SampleRate also flipped
        /// m_Legacy true, which silently takes a clip out of the Animator system. Both reported success. This
        /// is the same SerializedProperty path component-modify uses, and it cannot reach a field the caller
        /// did not name.
        /// </remarks>
        /// <param name="asset">Asset to modify.</param>
        /// <param name="assetPath">Asset path, used in the report.</param>
        /// <param name="payload">Flat <c>{"field": value}</c> JSON.</param>
        /// <returns>A per-field evidence report, or an error having applied nothing.</returns>
        private static string ModifyAssetSerializedFields(UnityEngine.Object asset, string assetPath, string payload)
        {
            List<KeyValuePair<string, string>> fields;
            if (!TrySplitFlatJsonObject(payload, out fields))
            {
                return "Payload must be a flat {\"field\": value} object; nested objects are only valid as the " +
                    "VALUE of a struct field. Nothing was applied.";
            }

            if (fields.Count == 0)
            {
                return "Empty payload: no fields to modify on " + assetPath + ".";
            }

            var serializedObject = new SerializedObject(asset);
            var staged = new List<StagedComponentFieldWrite>();
            var unknownFields = new List<string>();

            // Pass 1 - resolve and validate everything before touching the asset, so one bad field changes
            // nothing at all.
            foreach (var entry in fields)
            {
                var property = serializedObject.FindProperty(entry.Key);
                if (property == null)
                {
                    unknownFields.Add(entry.Key);
                    continue;
                }

                var stage = new StagedComponentFieldWrite { Name = entry.Key, Kind = property.propertyType };

                Type elementType;
                if (TryGetObjectReferenceArrayElementType(asset, entry.Key, property, out elementType))
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

                if (property.isArray && property.propertyType != SerializedPropertyType.String)
                {
                    string arrayError;
                    if (TryStageValueArrayWrite(asset, property, entry.Value, stage, out arrayError))
                    {
                        staged.Add(stage);
                        continue;
                    }

                    if (arrayError != null)
                    {
                        return "Failed to parse '" + entry.Key + "': " + arrayError + " Nothing was applied.";
                    }
                }

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
                    var field = FindSerializedField(asset.GetType(), entry.Key);
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
                        return "Failed to parse '" + entry.Key + "': " +
                            (parseError ?? "no typed writer for a " + property.propertyType + " field.") +
                            " Nothing was applied.";
                    }
                }

                staged.Add(stage);
            }

            if (staged.Count == 0)
            {
                return "Modify changed NOTHING on " + assetPath +
                    ": no supplied field matched a serialized property (" + string.Join(", ", unknownFields) +
                    "). Use assets-get-data to list the real field names.";
            }

            // Every named field, and every top-level property, is read before the write, so the report is built
            // from fresh reads after the save and reimport instead of from the request.
            var evidence = CaptureFieldEvidence(asset, staged, null);
            var stateBefore = CaptureTopLevelState(asset);
            RecordCompleteUndo(asset, "Modify Asset");
            var authoredBefore = CaptureUdonSharpAuthoredValues(asset);

            // Pass 2 - write every staged value, commit, save and reimport.
            foreach (var stage in staged)
            {
                ApplyStagedPropertyValue(serializedObject.FindProperty(stage.Name), stage);
            }

            serializedObject.ApplyModifiedProperties();
            SyncUdonSharpProxy(asset, authoredBefore);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            // A reimport can replace the loaded object, so the read-back finds the asset again by path when it has.
            var written = asset != null ? asset : AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (written == null)
            {
                return "Wrote " + assetPath + ", but it could not be loaded again to read the result back.";
            }

            var setFields = new List<string>();
            var unchangedFields = new List<string>();
            var notAppliedFields = new List<string>();
            ClassifyCommittedFields(written, evidence, setFields, unchangedFields, notAppliedFields);

            var namedPaths = new List<string>();
            foreach (var fieldEvidence in evidence)
            {
                namedPaths.Add(fieldEvidence.Path);
            }

            var unnamedChanges = DescribeUnnamedChanges(written, stateBefore, namedPaths);
            return BuildFieldWriteReport(written, setFields, unchangedFields, notAppliedFields, unnamedChanges, unknownFields);
        }

        /// <summary>
        /// One fully validated material property write staged during the modify command's first pass, mirroring
        /// the component-modify staging pattern: nothing is applied until every entry has parsed.
        /// </summary>
        private sealed class StagedMaterialPropertyWrite
        {
            /// <summary>Shader property name this write targets.</summary>
            public string Name;

            /// <summary>Shader property type, selecting which parsed slot below applies.</summary>
            public UnityEngine.Rendering.ShaderPropertyType Kind;

            /// <summary>Parsed value for a Color property.</summary>
            public Color ColorValue;

            /// <summary>Parsed value for a Vector property (unused axes stay zero).</summary>
            public Vector4 VectorValue;

            /// <summary>Parsed value for a Float or Range property.</summary>
            public float FloatValue;

            /// <summary>Parsed value for an Int property.</summary>
            public int IntValue;

            /// <summary>Resolved texture for a Texture property; null clears the slot.</summary>
            public Texture TextureValue;
        }

        /// <summary>
        /// Sets shader properties on a material through the material API, with per-property old-to-new evidence.
        /// Validation is atomic: a payload with one unparseable value applies nothing. Property names and types
        /// come from the material's shader, so a typo is reported as unknown instead of silently ignored.
        /// </summary>
        /// <param name="material">Material to modify.</param>
        /// <param name="assetPath">Asset path, for the report.</param>
        /// <param name="payload">Flat JSON object of shader property names to values.</param>
        /// <returns>An evidence-based report of what changed.</returns>
        private static string ModifyMaterialProperties(Material material, string assetPath, string payload)
        {
            List<KeyValuePair<string, string>> fields;
            if (!TrySplitFlatJsonObject(payload, out fields))
            {
                return "Material properties need a flat {\"_Property\": value} object; nested or wrapped payloads have no meaning for shader data. Nothing was applied.";
            }

            if (fields.Count == 0)
            {
                return "Empty payload: no properties to modify on " + assetPath + ".";
            }

            var shader = material.shader;
            var staged = new List<StagedMaterialPropertyWrite>();
            var unknown = new List<string>();

            // Pass 1 - resolve every name against the shader and parse every value before anything is written.
            foreach (var entry in fields)
            {
                var index = shader.FindPropertyIndex(entry.Key);
                if (index < 0)
                {
                    unknown.Add(entry.Key);
                    continue;
                }

                var stage = new StagedMaterialPropertyWrite { Name = entry.Key, Kind = shader.GetPropertyType(index) };
                var raw = (entry.Value ?? string.Empty).Trim();

                switch (stage.Kind)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                    {
                        float r, g, b, a;
                        if (!TryReadJsonFloat(raw, "r", out r) || !TryReadJsonFloat(raw, "g", out g) || !TryReadJsonFloat(raw, "b", out b))
                        {
                            return "Failed to parse '" + entry.Key + "': a Color needs {\"r\": ..., \"g\": ..., \"b\": ...} (alpha optional). Nothing was applied.";
                        }

                        if (!TryReadJsonFloat(raw, "a", out a)) a = 1f;
                        stage.ColorValue = new Color(r, g, b, a);
                        break;
                    }

                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                    {
                        float x, y, z, w;
                        if (!TryReadJsonFloat(raw, "x", out x) || !TryReadJsonFloat(raw, "y", out y))
                        {
                            return "Failed to parse '" + entry.Key + "': a Vector needs {\"x\": ..., \"y\": ...} (z and w optional). Nothing was applied.";
                        }

                        if (!TryReadJsonFloat(raw, "z", out z)) z = 0f;
                        if (!TryReadJsonFloat(raw, "w", out w)) w = 0f;
                        stage.VectorValue = new Vector4(x, y, z, w);
                        break;
                    }

                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                    {
                        float floatValue;
                        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out floatValue))
                        {
                            return "Failed to parse '" + entry.Key + "': '" + raw + "' is not a number. Nothing was applied.";
                        }

                        stage.FloatValue = floatValue;
                        break;
                    }

                    case UnityEngine.Rendering.ShaderPropertyType.Int:
                    {
                        int intValue;
                        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out intValue))
                        {
                            return "Failed to parse '" + entry.Key + "': '" + raw + "' is not an integer. Nothing was applied.";
                        }

                        stage.IntValue = intValue;
                        break;
                    }

                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                    {
                        if (raw == "null" || raw == "\"none\"")
                        {
                            stage.TextureValue = null;
                            break;
                        }

                        UnityEngine.Object reference;
                        var resolveError = ResolveReferenceFromJsonValue(raw, typeof(Texture), out reference);
                        if (resolveError != null)
                        {
                            return "Failed to resolve '" + entry.Key + "': " + resolveError + " Nothing was applied.";
                        }

                        stage.TextureValue = reference as Texture;
                        if (reference != null && stage.TextureValue == null)
                        {
                            return "Failed to resolve '" + entry.Key + "': the referenced asset is not a Texture. Nothing was applied.";
                        }

                        break;
                    }

                    default:
                        return "Property '" + entry.Key + "' has shader property type " + stage.Kind + ", which has no writer here. Nothing was applied.";
                }

                staged.Add(stage);
            }

            if (staged.Count == 0)
            {
                return "Modify changed NOTHING on " + assetPath + ": no supplied name is a property of shader '" +
                    shader.name + "' (" + string.Join(", ", unknown.ToArray()) + ").";
            }

            // Pass 2 - capture, write, re-read, so every property is classified from evidence.
            RecordCompleteUndo(material, "Modify Material");
            var set = new List<string>();
            var unchangedValues = new List<string>();
            foreach (var stage in staged)
            {
                var before = DescribeMaterialProperty(material, stage);
                switch (stage.Kind)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color: material.SetColor(stage.Name, stage.ColorValue); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector: material.SetVector(stage.Name, stage.VectorValue); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float: material.SetFloat(stage.Name, stage.FloatValue); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Range: material.SetFloat(stage.Name, stage.FloatValue); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Int: material.SetInteger(stage.Name, stage.IntValue); break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture: material.SetTexture(stage.Name, stage.TextureValue); break;
                }

                var after = DescribeMaterialProperty(material, stage);
                if (before == after) unchangedValues.Add(stage.Name + " (already " + after + ")");
                else set.Add(stage.Name + " = " + after + " (was " + before + ")");
            }

            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();

            var report = new StringBuilder();
            report.Append(set.Count > 0 ? "Modified material " + assetPath + "." : "No material data changed on " + assetPath + ".");
            if (set.Count > 0) report.Append(" Set: " + string.Join("; ", set.ToArray()) + ".");
            if (unchangedValues.Count > 0) report.Append(" Already at value: " + string.Join("; ", unchangedValues.ToArray()) + ".");
            if (unknown.Count > 0) report.Append(" WARNING - not properties of shader '" + shader.name + "': " + string.Join(", ", unknown.ToArray()) + ".");
            return report.ToString();
        }

        /// <summary>
        /// Reads one shader property's current value at full precision, for the before/after evidence lines, which
        /// also decide from it whether the property changed.
        /// </summary>
        /// <param name="material">Material to read.</param>
        /// <param name="stage">Staged write naming the property and its type.</param>
        /// <returns>A short human-readable value.</returns>
        private static string DescribeMaterialProperty(Material material, StagedMaterialPropertyWrite stage)
        {
            switch (stage.Kind)
            {
                case UnityEngine.Rendering.ShaderPropertyType.Color:
                {
                    var color = material.GetColor(stage.Name);
                    return "RGBA(" + FormatExactFloat(color.r) + ", " + FormatExactFloat(color.g) + ", " +
                        FormatExactFloat(color.b) + ", " + FormatExactFloat(color.a) + ")";
                }

                case UnityEngine.Rendering.ShaderPropertyType.Vector:
                {
                    var vector = material.GetVector(stage.Name);
                    return "(" + FormatExactFloat(vector.x) + ", " + FormatExactFloat(vector.y) + ", " +
                        FormatExactFloat(vector.z) + ", " + FormatExactFloat(vector.w) + ")";
                }

                case UnityEngine.Rendering.ShaderPropertyType.Float:
                case UnityEngine.Rendering.ShaderPropertyType.Range:
                    return FormatExactFloat(material.GetFloat(stage.Name));

                case UnityEngine.Rendering.ShaderPropertyType.Int:
                    return material.GetInteger(stage.Name).ToString(CultureInfo.InvariantCulture);

                case UnityEngine.Rendering.ShaderPropertyType.Texture:
                {
                    var texture = material.GetTexture(stage.Name);
                    return texture == null ? "none" : texture.name;
                }

                default:
                    return "?";
            }
        }

        #endregion
    }
}
