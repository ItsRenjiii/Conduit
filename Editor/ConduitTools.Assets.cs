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
    /// AssetDatabase, prefab-stage, material, and shader commands in Conduit's command shape.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Asset create, move and delete

        /// <summary>
        /// Copies an asset to a new project-relative path; returns a confirmation line or a validation error.
        /// </summary>
        /// <param name="args">Pipe-separated source path and destination path.</param>
        /// <returns>A confirmation line or a clear validation error.</returns>
        private static string AssetsCopyCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: assets-copy <sourcePath> | <destinationPath>";
            }

            var sourcePath = NormalizeAssetPath(sections[0]);
            var destinationPath = NormalizeAssetPath(sections[1]);
            if (AssetDatabase.LoadMainAssetAtPath(sourcePath) == null)
            {
                return "Asset not found: " + sourcePath;
            }

            if (!AssetDatabase.CopyAsset(sourcePath, destinationPath))
            {
                return "Failed to copy asset from " + sourcePath + " to " + destinationPath + ".";
            }

            // Copying a compilation input adds a file the script assemblies must be rebuilt from, and that
            // rebuild is scheduled on a later editor tick rather than run by the Refresh below, so answering
            // here hands the caller a reply the editor is about to be too busy to follow. Arm the deferral
            // before the import, as assets-delete does.
            if (PathTriggersScriptCompile(destinationPath) &&
                TryDeferUntilReload("__deferred-note Copied asset to " + destinationPath + "."))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return "Copied asset to " + destinationPath + ".";
        }

        /// <summary>
        /// Creates a folder beneath an existing Assets or Packages folder, with exactly the name asked for.
        /// Unity's CreateFolder can give a new folder the casing of a sibling whose name it prefix-matches
        /// ignoring case ("Players" beside "PlayerSeat.cs" lands as "PlayerS"), so a name that differs only
        /// in case is corrected through a temporary name and the correction is reported.
        /// </summary>
        /// <param name="args">Pipe-separated parent folder and child folder name.</param>
        /// <returns>A JSON result containing the created folder GUID and path, plus the mis-cased name when one was corrected.</returns>
        private static string AssetsCreateFolderCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: assets-create-folder <parentFolder> | <name>";
            }

            var parentFolder = NormalizeAssetPath(sections[0]);
            var folderName = sections[1].Trim().Trim('"');
            if (!AssetDatabase.IsValidFolder(parentFolder))
            {
                return "Parent folder does not exist: " + parentFolder;
            }

            if (string.IsNullOrWhiteSpace(folderName) || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return "Invalid folder name: " + folderName;
            }

            // Idempotent: if the folder already exists, return it as-is. AssetDatabase.CreateFolder would
            // otherwise mint a de-duplicated sibling ("Materials 1") on a name collision, silently leaving a
            // stray empty folder in the project.
            var existingPath = parentFolder + "/" + folderName;
            if (AssetDatabase.IsValidFolder(existingPath))
            {
                return "{\"guid\":" + JsonQuote(AssetDatabase.AssetPathToGUID(existingPath)) +
                    ",\"path\":" + JsonQuote(existingPath) + ",\"existed\":true}";
            }

            var guid = AssetDatabase.CreateFolder(parentFolder, folderName);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var path = AssetDatabase.GUIDToAssetPath(guid);

            var landedName = Path.GetFileName(path);
            string correctedFrom = null;
            if (!string.Equals(landedName, folderName, StringComparison.Ordinal) &&
                string.Equals(landedName, folderName, StringComparison.OrdinalIgnoreCase))
            {
                // A case-only move is a no-op on a case-insensitive file system, so go through a temporary name.
                var temporaryPath = AssetDatabase.GenerateUniqueAssetPath(parentFolder + "/" + folderName + "_casing");
                var moveError = AssetDatabase.MoveAsset(path, temporaryPath);
                if (string.IsNullOrEmpty(moveError)) moveError = AssetDatabase.MoveAsset(temporaryPath, existingPath);

                path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(moveError) || !string.Equals(path, existingPath, StringComparison.Ordinal))
                {
                    return "Created the folder, but Unity named it " + landedName + " and correcting it to " + folderName +
                        " failed; it is at " + path + (string.IsNullOrEmpty(moveError) ? "." : ": " + moveError);
                }

                correctedFrom = landedName;
            }

            return "{\"guid\":" + JsonQuote(guid) + ",\"path\":" + JsonQuote(path) +
                (correctedFrom != null ? ",\"correctedFrom\":" + JsonQuote(correctedFrom) : string.Empty) + "}";
        }

        /// <summary>
        /// Deletes a project asset through AssetDatabase so meta files and imports stay coherent.
        /// </summary>
        /// <param name="args">The asset path, GUID, or instance ID to delete.</param>
        /// <returns>A confirmation line or an AssetDatabase error.</returns>
        private static string AssetsDeleteCommand(string args)
        {
            UnityEngine.Object asset;
            string assetPath;
            var error = TryFindAssetObject(args, out asset, out assetPath);
            if (error != null)
            {
                return error;
            }

            var isCsharpScript = assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

            // A U# behaviour is a PAIR. Deleting only the .cs here would leave its program asset orphaned,
            // so hand those to delete-usharp instead. Keyed on a real linked program asset rather than on
            // the file extension, because plain C# - MonoBehaviours, editor scripts - has none and must
            // still delete normally right here.
            if (isCsharpScript)
            {
                var programAssetPath = FindUdonSharpProgramAssetPath(assetPath);
                if (programAssetPath != null)
                {
                    return "Refusing: " + assetPath + " is a UdonSharp behaviour, linked to the program asset "
                        + programAssetPath + ". Deleting the script alone would orphan it. Use 'delete-usharp "
                        + assetPath + "', which removes both.";
                }
            }

            // Settled before the delete: once a folder is gone it can no longer be inspected for the
            // scripts it held, and those scripts leaving the project is what rebuilds the assemblies.
            var triggersCompile = PathTriggersScriptCompile(assetPath);

            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                return "Failed to delete asset: " + assetPath;
            }

            // Deleting a compilation input recompiles, and UdonSharp rebuilds every U# program from any
            // script change, so the editor is busy afterwards and cannot serve the caller's next command.
            // Withhold the response until that settles. Other asset types import synchronously and answer
            // immediately, so they keep the cheap path.
            if (triggersCompile && TryDeferUntilReload("__deferred-note Deleted asset " + assetPath + "."))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return "Deleted asset " + assetPath + ".";
        }

        /// <summary>
        /// Moves or renames an asset through AssetDatabase.
        /// </summary>
        /// <param name="args">Pipe-separated source path and destination path.</param>
        /// <returns>A confirmation line or AssetDatabase move error.</returns>
        private static string AssetsMoveCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: assets-move <sourcePath> | <destinationPath>";
            }

            var sourcePath = NormalizeAssetPath(sections[0]);
            var destinationPath = NormalizeAssetPath(sections[1]);
            var error = AssetDatabase.MoveAsset(sourcePath, destinationPath);
            if (!string.IsNullOrEmpty(error))
            {
                return "Failed to move asset: " + error;
            }

            // Either end being a compilation input rebuilds the script assemblies: the source leaves the
            // compilation set and the destination joins it, and a rename between those extensions does both.
            // That rebuild is scheduled rather than run by the Refresh below, so arm the deferral first.
            if ((PathTriggersScriptCompile(sourcePath) || PathTriggersScriptCompile(destinationPath)) &&
                TryDeferUntilReload("__deferred-note Moved asset to " + destinationPath + "."))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return "Moved asset to " + destinationPath + ".";
        }

        /// <summary>
        /// Reads a Unity asset's reference metadata and serialized editor JSON where Unity supports it.
        /// </summary>
        /// <param name="args">The asset path, GUID, or instance ID to inspect.</param>
        /// <returns>A compact JSON object with asset metadata and serialized data.</returns>
        private static string AssetsGetDataCommand(string args)
        {
            UnityEngine.Object asset;
            string assetPath;
            var error = TryFindAssetObject(args, out asset, out assetPath);
            if (error != null)
            {
                return error;
            }

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var json = EditorJsonUtility.ToJson(asset, false);
            return "{\"asset\":" + JsonObjectRef(asset) +
                ",\"guid\":" + JsonQuote(guid) +
                ",\"serialized\":" + JsonQuote(json) + "}";
        }



        /// <summary>
        /// Creates missing parent folders below Assets so generated assets have a valid destination.
        /// </summary>
        /// <param name="folderPath">The project-relative folder path to ensure.</param>
        private static void EnsureAssetFolder(string folderPath)
        {
            folderPath = NormalizeAssetPath(folderPath);
            if (string.IsNullOrEmpty(folderPath) || AssetDatabase.IsValidFolder(folderPath))
            {
                return;
            }

            var parts = folderPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0] != "Assets")
            {
                return;
            }

            var current = parts[0];
            for (var i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }

                current = next;
            }
        }

        #endregion

        #region Search

        /// <summary>
        /// Finds assets with Unity's filter syntax and optional folder scope.
        /// </summary>
        /// <param name="args">Optional filter text and optional pipe-separated folder list.</param>
        /// <returns>A compact JSON array of matching assets.</returns>
        private static string AssetsFindCommand(string args)
        {
            var sections = SplitCommandSections(args);
            var filter = sections.Length > 0 && !string.IsNullOrWhiteSpace(sections[0]) ? sections[0].Trim() : string.Empty;
            var folders = new string[0];
            if (sections.Length > 1 && !string.IsNullOrWhiteSpace(sections[1]))
            {
                // The MCP server sends pipe sections unquoted, so a single folder containing a space would
                // otherwise split into two folders that do not exist ("Assets/My Project" becomes
                // "Assets/My" and "Project") and scope the search to nothing while reporting zero matches as
                // if that were the answer. Prefer the whole section whenever it names a real folder.
                var wholeSection = NormalizeAssetPath(sections[1].Trim());
                if (AssetDatabase.IsValidFolder(wholeSection))
                {
                    folders = new[] { wholeSection };
                }
                else
                {
                    folders = SplitCommandTokens(sections[1]);
                    for (var i = 0; i < folders.Length; i++)
                    {
                        folders[i] = NormalizeAssetPath(folders[i]);
                    }
                }
            }

            var guids = folders.Length == 0 ? AssetDatabase.FindAssets(filter) : AssetDatabase.FindAssets(filter, folders);
            var builder = new StringBuilder();
            builder.Append("{\"count\":").Append(guids.Length).Append(",\"items\":[");
            for (var i = 0; i < guids.Length && i < DefaultJsonResultLimit; i++)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[i]);
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"guid\":").Append(JsonQuote(guids[i]))
                    .Append(",\"path\":").Append(JsonQuote(path))
                    .Append(",\"name\":").Append(JsonQuote(asset == null ? Path.GetFileNameWithoutExtension(path) : asset.name))
                    .Append(",\"type\":").Append(JsonQuote(asset == null ? string.Empty : asset.GetType().FullName))
                    .Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        /// <summary>
        /// Finds loaded built-in/editor resources by name or type when Unity exposes them through Resources.
        /// </summary>
        /// <param name="args">Optional case-insensitive text filter.</param>
        /// <returns>A compact JSON array of matching built-in objects.</returns>
        private static string AssetsFindBuiltInCommand(string args)
        {
            var filter = (args ?? string.Empty).Trim().Trim('"');
            var objects = Resources.FindObjectsOfTypeAll<UnityEngine.Object>();
            var builder = new StringBuilder();
            var count = 0;
            builder.Append('[');
            foreach (var obj in objects)
            {
                if (obj == null || !string.IsNullOrEmpty(AssetDatabase.GetAssetPath(obj)))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(filter) &&
                    obj.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    obj.GetType().Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonObjectRef(obj));
                count++;
                if (count >= DefaultJsonResultLimit)
                {
                    break;
                }
            }

            builder.Append(']');
            return builder.ToString();
        }

        #endregion

        #region References

        /// <summary>
        /// Reverse-reference lookup: finds every GameObject in the scene named by the required scene argument
        /// (or in the open prefab stage when one is open) whose components hold a serialized
        /// object-reference to the target asset. Unity exposes only forward dependencies, so this walks each
        /// component's <see cref="SerializedObject"/> (descending into nested fields and arrays, e.g. a
        /// Renderer's materials) and reports the owning GameObject path, component type, and serialized
        /// field. The asset's sub-assets (e.g. a texture's Sprite) count as the same target, so a reference
        /// to any representation of the asset is reported.
        /// </summary>
        /// <param name="args">The scene, then the target asset path, GUID, or instance ID to find references to.</param>
        /// <returns>A compact JSON object with the resolved target, the scanned context, and the hits.</returns>
        private static string AssetsFindReferencesCommand(string args)
        {
            var tokens = SplitCommandTokens(args ?? string.Empty, false);
            UnityEngine.SceneManagement.Scene scene;
            var sceneError = TryResolveRequiredScene(tokens.Length > 0 ? tokens[0] : string.Empty, out scene);
            if (sceneError != null)
            {
                return sceneError;
            }

            if (tokens.Length < 2)
            {
                return "Usage: assets-find-references <scene> <assetPath>";
            }

            UnityEngine.Object target;
            string targetPath;
            var error = TryFindAssetObject(string.Join(" ", tokens, 1, tokens.Length - 1), out target, out targetPath);
            if (error != null)
            {
                return error;
            }

            // Treat the asset and every sub-asset at its path as one target, so a reference to (say) a
            // texture's Sprite counts as a hit on the asset rather than a miss.
            var targetIds = new HashSet<int> { target.GetInstanceID() };
            foreach (var representation in AssetDatabase.LoadAllAssetsAtPath(targetPath))
            {
                if (representation != null)
                {
                    targetIds.Add(representation.GetInstanceID());
                }
            }

            var builder = new StringBuilder();
            builder.Append("{\"target\":").Append(JsonObjectRef(target))
                .Append(",\"context\":").Append(JsonQuote(CurrentContextLabel()))
                .Append(",\"scene\":").Append(CurrentContextLabel() == "scene" ? JsonQuote(scene.name) : "null")
                .Append(",\"references\":[");

            var count = 0;
            var truncated = false;
            foreach (var gameObject in EnumerateContextGameObjects(scene))
            {
                if (truncated)
                {
                    break;
                }

                foreach (var component in gameObject.GetComponents<Component>())
                {
                    if (component == null)
                    {
                        continue;
                    }

                    foreach (var fieldPath in EnumerateReferencingFields(component, targetIds))
                    {
                        if (count >= DefaultJsonResultLimit)
                        {
                            truncated = true;
                            break;
                        }

                        if (count > 0)
                        {
                            builder.Append(',');
                        }

                        builder.Append("{\"gameObject\":").Append(JsonQuote(GetGameObjectPath(gameObject)))
                            .Append(",\"component\":").Append(JsonQuote(component.GetType().FullName))
                            .Append(",\"field\":").Append(JsonQuote(fieldPath))
                            .Append('}');
                        count++;
                    }

                    if (truncated)
                    {
                        break;
                    }
                }
            }

            builder.Append("],\"count\":").Append(count);
            if (truncated)
            {
                builder.Append(",\"truncated\":true");
            }

            builder.Append('}');
            return builder.ToString();
        }

        /// <summary>
        /// Repoints every reference to one asset onto another across the scene named by the required scene
        /// argument (or the open prefab stage when one is open), so a
        /// wholesale asset swap costs one call instead of one call per referencing field.
        /// <para>
        /// The old asset and its sub-assets count as a single target, and the replacement is chosen per field
        /// from the new asset's sub-assets by the type that field actually demands. That is what lets a baked
        /// Sprite asset be swapped for an imported PNG: an Image.m_Sprite field wants a Sprite, so it receives
        /// the PNG's Sprite sub-asset rather than the Texture2D that is the PNG's main asset.
        /// </para>
        /// <para>
        /// Every hit is resolved before anything is written. A field whose demanded type has no counterpart in
        /// the new asset refuses the whole call and names what the new asset does offer, because a partial
        /// swap would leave the scene half-referencing an asset the caller is about to retire.
        /// </para>
        /// </summary>
        /// <param name="args">Pipe-separated scene, old asset and new asset, each asset a path, GUID, or instance ID.</param>
        /// <returns>An evidence-based report naming what changed, or a validation error.</returns>
        private static string AssetsReplaceReferencesCommand(string args)
        {
            var sections = SplitCommandSections(args);
            UnityEngine.SceneManagement.Scene scene;
            var sceneError = TryResolveRequiredScene(sections.Length > 0 ? sections[0] : string.Empty, out scene);
            if (sceneError != null)
            {
                return sceneError;
            }

            if (sections.Length < 3)
            {
                return "Usage: assets-replace-references <scene> | <oldAsset> | <newAsset>";
            }

            UnityEngine.Object oldAsset;
            string oldPath;
            var oldError = TryFindAssetObject(sections[1], out oldAsset, out oldPath);
            if (oldError != null)
            {
                return "Old asset: " + oldError;
            }

            UnityEngine.Object newAsset;
            string newPath;
            var newError = TryFindAssetObject(sections[2], out newAsset, out newPath);
            if (newError != null)
            {
                return "New asset: " + newError;
            }

            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Old and new asset are the same asset (" + oldPath + "). Nothing was changed.";
            }

            // The asset and every sub-asset at its path are one target, so a reference held to a texture's
            // Sprite counts as a reference to the texture.
            var targetIds = new HashSet<int> { oldAsset.GetInstanceID() };
            foreach (var representation in AssetDatabase.LoadAllAssetsAtPath(oldPath))
            {
                if (representation != null)
                {
                    targetIds.Add(representation.GetInstanceID());
                }
            }

            var candidates = new List<UnityEngine.Object>();
            foreach (var representation in AssetDatabase.LoadAllAssetsAtPath(newPath))
            {
                if (representation != null)
                {
                    candidates.Add(representation);
                }
            }

            if (candidates.Count == 0 && newAsset != null)
            {
                candidates.Add(newAsset);
            }

            // Resolve every hit first; an unsatisfiable field must not leave a half-swapped scene behind.
            var planned = new List<ReferenceReplacement>();
            var pending = new Dictionary<Component, SerializedObject>();
            foreach (var gameObject in EnumerateContextGameObjects(scene))
            {
                foreach (var component in gameObject.GetComponents<Component>())
                {
                    if (component == null)
                    {
                        continue;
                    }

                    var serializedObject = new SerializedObject(component);
                    var matched = false;
                    foreach (var property in EnumerateReferencingProperties(serializedObject, targetIds))
                    {
                        var demanded = PropertyReferenceType(property);
                        var replacement = PickAssignableAsset(candidates, demanded);
                        if (replacement == null)
                        {
                            return "Cannot replace " + GetGameObjectPath(gameObject) + " -> " +
                                component.GetType().Name + "." + property.propertyPath + ", which needs a " +
                                (demanded == null ? "UnityEngine.Object" : demanded.Name) + " and " + newPath +
                                " provides " + DescribeAssetCandidates(candidates) + ". Nothing was changed.";
                        }

                        var entry = new ReferenceReplacement();
                        entry.Owner = gameObject;
                        entry.Component = component;
                        entry.Property = property;
                        entry.Replacement = replacement;
                        planned.Add(entry);
                        matched = true;
                    }

                    if (matched)
                    {
                        pending[component] = serializedObject;
                    }
                }
            }

            if (planned.Count == 0)
            {
                return "No references to " + oldPath + " found in the " + DescribeContext(scene) +
                    ". Nothing was changed.";
            }

            var evidence = new List<string>();
            foreach (var entry in planned)
            {
                entry.Property.objectReferenceValue = entry.Replacement;
                if (evidence.Count < ArrayDescriptionElementLimit)
                {
                    evidence.Add(GetGameObjectPath(entry.Owner) + " -> " +
                        entry.Component.GetType().Name + "." + entry.Property.propertyPath);
                }
            }

            foreach (var pair in pending)
            {
                // Taken before the apply, which is the moment the staged writes above reach the component.
                var authoredBefore = CaptureUdonSharpAuthoredValues(pair.Key);
                pair.Value.ApplyModifiedProperties();
                SyncUdonSharpProxy(pair.Key, authoredBefore);
                MarkGameObjectSceneDirty(pair.Key.gameObject);
            }

            var report = "Replaced " + planned.Count + " reference(s) to " + oldPath + " with " + newPath +
                " across " + pending.Count + " component(s) in the " + DescribeContext(scene) + ": " +
                string.Join("; ", evidence.ToArray());
            if (planned.Count > evidence.Count)
            {
                report += "; and " + (planned.Count - evidence.Count) + " more";
            }

            return report + ".";
        }

        /// <summary>
        /// One resolved reference rewrite, held between the validation pass and the write pass so a field that
        /// cannot be satisfied refuses the call before anything has been mutated.
        /// </summary>
        private sealed class ReferenceReplacement
        {
            /// <summary>GameObject owning the referencing component, used for the report path.</summary>
            public GameObject Owner;

            /// <summary>Component holding the reference, and the one whose proxy is resynced after writing.</summary>
            public Component Component;

            /// <summary>The object-reference property to rewrite.</summary>
            public SerializedProperty Property;

            /// <summary>Asset chosen for this property by the type the property demands.</summary>
            public UnityEngine.Object Replacement;
        }

        /// <summary>
        /// Reads the object type a reference property demands from its serialized PPtr spelling, which is the
        /// only place that type is stated for a native field with no reflectable C# counterpart.
        /// </summary>
        /// <param name="property">The object-reference property to inspect.</param>
        /// <returns>The demanded type, or null when the spelling cannot be resolved.</returns>
        private static Type PropertyReferenceType(SerializedProperty property)
        {
            var spelling = property.type;
            if (string.IsNullOrEmpty(spelling))
            {
                return null;
            }

            var marker = "PPtr<$";
            var open = spelling.IndexOf(marker, StringComparison.Ordinal);
            if (open < 0)
            {
                return FindTypeLoose(spelling);
            }

            var start = open + marker.Length;
            var close = spelling.IndexOf('>', start);
            return close <= start ? null : FindTypeLoose(spelling.Substring(start, close - start));
        }

        /// <summary>
        /// Picks the representation assignable to a field of the demanded type, preferring an exact type match
        /// so a texture's Sprite sub-asset wins a Sprite field over the Texture2D beside it.
        /// </summary>
        /// <param name="candidates">The new asset's main object and sub-assets.</param>
        /// <param name="demanded">The type the field requires, or null when it could not be determined.</param>
        /// <returns>The best matching asset, or null when none is assignable.</returns>
        private static UnityEngine.Object PickAssignableAsset(List<UnityEngine.Object> candidates, Type demanded)
        {
            if (demanded == null)
            {
                return candidates.Count > 0 ? candidates[0] : null;
            }

            foreach (var candidate in candidates)
            {
                if (candidate != null && candidate.GetType() == demanded)
                {
                    return candidate;
                }
            }

            foreach (var candidate in candidates)
            {
                if (candidate != null && demanded.IsInstanceOfType(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Names the representations an asset offers, so a refusal tells the caller what they can target
        /// rather than only what failed.
        /// </summary>
        /// <param name="candidates">The new asset's main object and sub-assets.</param>
        /// <returns>A comma-separated list of type and name pairs.</returns>
        private static string DescribeAssetCandidates(List<UnityEngine.Object> candidates)
        {
            if (candidates.Count == 0)
            {
                return "nothing";
            }

            var described = new List<string>();
            foreach (var candidate in candidates)
            {
                described.Add(candidate.GetType().Name + " " + candidate.name);
            }

            return string.Join(", ", described.ToArray());
        }

        /// <summary>
        /// Yields the serialized properties on an object whose object-reference value is one of the given
        /// target instance ids. Walks the full property tree, descending into children and array elements so
        /// references held inside arrays (for example a Renderer's m_Materials) are found.
        /// </summary>
        /// <remarks>
        /// The walk is Next rather than NextVisible on purpose. Inspector visibility hides a field whose
        /// editor draws it by hand, and a hidden reference is still a real reference: skipping it makes a
        /// reverse lookup under-report, and would let a replace leave live references behind on an asset the
        /// caller is retiring.
        /// </remarks>
        /// <param name="serializedObject">The object to scan.</param>
        /// <param name="targetInstanceIds">Instance ids that count as a match (the asset plus its sub-assets).</param>
        /// <returns>Copies of the matching serialized properties.</returns>
        private static IEnumerable<SerializedProperty> EnumerateReferencingProperties(SerializedObject serializedObject, HashSet<int> targetInstanceIds)
        {
            var iterator = serializedObject.GetIterator();
            while (iterator.Next(true))
            {
                if (iterator.propertyType != SerializedPropertyType.ObjectReference)
                {
                    continue;
                }

                var reference = iterator.objectReferenceValue;
                if (reference != null && targetInstanceIds.Contains(reference.GetInstanceID()))
                {
                    yield return iterator.Copy();
                }
            }
        }

        /// <summary>
        /// Yields the serialized property paths on a component whose object-reference value is one of the
        /// given target instance ids.
        /// </summary>
        /// <param name="component">The component to scan.</param>
        /// <param name="targetInstanceIds">Instance ids that count as a match (the asset plus its sub-assets).</param>
        /// <returns>The matching serialized property paths.</returns>
        private static IEnumerable<string> EnumerateReferencingFields(Component component, HashSet<int> targetInstanceIds)
        {
            foreach (var property in EnumerateReferencingProperties(new SerializedObject(component), targetInstanceIds))
            {
                yield return property.propertyPath;
            }
        }

        #endregion

        #region Orphan scan

        /// <summary>
        /// Reports assets under a folder that nothing in the scanned tree references by GUID, so a cleanup
        /// pass has a list to review. REPORT ONLY - it never deletes and never says an asset is safe to
        /// delete. Unreferenced is not unwanted: work in progress legitimately sits unassigned, and a GUID
        /// scan cannot see a Resources load or a path built at runtime, so those are listed out of scope.
        /// </summary>
        /// <param name="args">The folder to examine, and an optional folder limiting the reference scan.</param>
        /// <returns>A JSON report naming what was scanned, the unreferenced candidates, and what was skipped.</returns>
        private static string AssetsOrphansCommand(string args)
        {
            var sections = SplitCommandSections(args);
            var folder = NormalizeAssetPath(sections.Length > 0 ? sections[0] : string.Empty);
            if (string.IsNullOrEmpty(folder) || !AssetDatabase.IsValidFolder(folder))
            {
                return "Usage: assets-orphans <folder> [| <scanRoot>]. Folder not found: " + folder;
            }

            var scanRoot = sections.Length > 1 && !string.IsNullOrWhiteSpace(sections[1])
                ? NormalizeAssetPath(sections[1])
                : "Assets";
            if (!AssetDatabase.IsValidFolder(scanRoot))
            {
                return "Scan root not found: " + scanRoot + ". Nothing was scanned.";
            }

            // Every asset under the scan root contributes its DIRECT dependencies, and their union is the set
            // of things something points at. Recursive adds nothing: an asset reachable only through a chain
            // is still named directly by whichever asset in that chain holds it.
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scanCount = 0;
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { scanRoot }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path))
                {
                    continue;
                }

                scanCount++;
                foreach (var dependency in AssetDatabase.GetDependencies(path, false))
                {
                    // GetDependencies reports the asset itself; counting that would make everything look used.
                    if (!string.Equals(dependency, path, StringComparison.OrdinalIgnoreCase))
                    {
                        referenced.Add(dependency);
                    }
                }
            }

            var unreferenced = new List<string>();
            var outOfScope = new List<string>();
            var candidateCount = 0;
            foreach (var guid in AssetDatabase.FindAssets(string.Empty, new[] { folder }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path) || referenced.Contains(path))
                {
                    continue;
                }

                candidateCount++;
                if (IsOutOfScopeForOrphanScan(path))
                {
                    outOfScope.Add(path);
                }
                else
                {
                    unreferenced.Add(path);
                }
            }

            unreferenced.Sort(StringComparer.OrdinalIgnoreCase);
            outOfScope.Sort(StringComparer.OrdinalIgnoreCase);

            var builder = new StringBuilder();
            builder.Append("{\"folder\":").Append(JsonQuote(folder))
                .Append(",\"scanRoot\":").Append(JsonQuote(scanRoot))
                .Append(",\"assetsScanned\":").Append(scanCount)
                .Append(",\"unreferencedFound\":").Append(candidateCount)
                .Append(",\"verdict\":").Append(JsonQuote(
                    "No GUID references found in the scanned assets. That is not the same as unwanted, and " +
                    "this is not a safe-to-delete list - review each entry by hand."))
                .Append(",\"unreferenced\":");
            AppendOrphanPathArray(builder, unreferenced);
            builder.Append(",\"outOfScope\":");
            AppendOrphanPathArray(builder, outOfScope);
            builder.Append("}");
            return builder.ToString();
        }

        /// <summary>
        /// Writes a path list as a JSON array, capped so one enormous folder cannot bury the response.
        /// </summary>
        /// <param name="builder">The report being built.</param>
        /// <param name="paths">The paths to write.</param>
        private static void AppendOrphanPathArray(StringBuilder builder, List<string> paths)
        {
            builder.Append('[');
            var written = 0;
            foreach (var path in paths)
            {
                if (written >= DefaultJsonResultLimit)
                {
                    break;
                }

                if (written > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonQuote(path));
                written++;
            }

            builder.Append(']');
            if (written < paths.Count)
            {
                builder.Append(",\"truncated\":").Append(paths.Count - written);
            }
        }

        /// <summary>
        /// Reports whether an unreferenced asset is one a GUID scan cannot speak for. Anything under a
        /// Resources folder is loadable by string path at runtime, and a scene is a root rather than a
        /// dependency of something else, so neither can be called an orphan on this evidence.
        /// </summary>
        /// <param name="assetPath">The asset path to classify.</param>
        /// <returns>True when the asset must be reported as out of scope rather than as unreferenced.</returns>
        private static bool IsOutOfScopeForOrphanScan(string assetPath)
        {
            if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return assetPath.IndexOf("/Resources/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        #endregion

        #region Dispatch

            /// <summary>
        /// Routes the Assets verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchAssets(string verb, string command, string[] parts)
        {
            if (verb == "assets-copy")
            {
                return AssetsCopyCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-create-folder")
            {
                return AssetsCreateFolderCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-delete")
            {
                return AssetsDeleteCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-find")
            {
                return AssetsFindCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-find-built-in")
            {
                return AssetsFindBuiltInCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-find-references")
            {
                return AssetsFindReferencesCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-orphans")
            {
                return AssetsOrphansCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-get-data")
            {
                return AssetsGetDataCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-material-create")
            {
                return AssetsMaterialCreateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-material-set-shader")
            {
                return AssetsMaterialSetShaderCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-material-set-texture")
            {
                return AssetsMaterialSetTextureCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-modify")
            {
                return AssetsModifyCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-importer-get")
            {
                return AssetImporterGetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-importer-modify")
            {
                return AssetImporterModifyCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-move")
            {
                return AssetsMoveCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-prefab-close")
            {
                return AssetsPrefabCloseCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-prefab-create")
            {
                return AssetsPrefabCreateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-prefab-instantiate")
            {
                return AssetsPrefabInstantiateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-prefab-open")
            {
                return AssetsPrefabOpenCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-prefab-unpack")
            {
                return AssetsPrefabUnpackCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-prefab-save")
            {
                return AssetsPrefabSaveCommand();
            }

            if (verb == "assets-refresh")
            {
                return RefreshAssetDatabase();
            }

            if (verb == "assets-replace-references")
            {
                return AssetsReplaceReferencesCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-shader-get-data")
            {
                return AssetsShaderGetDataCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "assets-shader-list-all")
            {
                return AssetsShaderListAllCommand(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }

        #endregion
    }
}
