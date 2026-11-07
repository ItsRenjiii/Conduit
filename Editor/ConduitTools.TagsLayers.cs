/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Tag and layer definitions: the tables themselves, not the assignment of one to an object.
//
// Two things measured about this surface shape every command here. Unity's tag API reports nothing: adding
// a duplicate, removing a tag that does not exist and adding an empty name were each observed leaving the
// count unchanged without throwing, so a command that trusted the call would report success for a no-op.
// Every verb below reads the table back and reports what actually moved.
//
// And there is no AddLayer anywhere in the editor API. InternalEditorUtility exposes only get_layers and
// GetLayerName, and get_layers drops empty slots, so it cannot answer a question that names an index. The
// writable route is the TagManager asset's "layers" array, always 32 entries, reached through
// SerializedObject. A write there was observed reaching GetLayerName and LayerMask.NameToLayer with no
// import or save in between.
//
// Overwriting an occupied slot needs an explicit confirm rather than a warning after the fact. Every object
// already on that layer silently changes meaning, and nothing in Unity flags it. That is not a rule about
// any one engine or SDK: it is true of any project, and which layers are spoken for is the project's
// business, documented per project rather than decided here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ItsRenji.Conduit
{
    internal static partial class ConduitBridge
    {
        #region Tag and layer constants

        /// <summary>The project asset holding both tables. Outside Assets, so it is loaded, never imported.</summary>
        private const string TAG_MANAGER_PATH = "ProjectSettings/TagManager.asset";

        /// <summary>The serialized name of the layer array. Not m_Layers, which does not resolve.</summary>
        private const string LAYER_ARRAY_PROPERTY = "layers";

        /// <summary>The token a caller repeats to authorise a destructive change.</summary>
        private const string CONFIRM_TOKEN = "confirm";

        /// <summary>Unity's layer table is fixed at 32 entries, indices 0 to 31.</summary>
        private const int LAYER_COUNT = 32;

        #endregion

        #region Tag commands

        /// <summary>Lists every tag the project defines, builtin and custom alike.</summary>
        /// <returns>One tag per line, numbered.</returns>
        private static string TagListCommand()
        {
            var tags = InternalEditorUtility.tags;
            if (tags == null || tags.Length == 0) return "No tags defined.";

            var report = new StringBuilder("Tags (" + tags.Length + "):");
            foreach (var tag in tags) report.AppendLine().Append("  ").Append(tag);
            return report.ToString();
        }

        /// <summary>
        /// Defines a new tag. Unity silently ignores a duplicate and an empty name, so the table is read
        /// back and the report says which of those happened rather than claiming an add either way.
        /// </summary>
        /// <param name="args">The tag name.</param>
        /// <returns>What changed, or why nothing did.</returns>
        private static string TagAddCommand(string args)
        {
            var tag = (args ?? string.Empty).Trim();
            if (tag.Length == 0) return "Error: a tag name is required. Usage: editor-tag-add <tag>";

            if (Array.IndexOf(InternalEditorUtility.tags, tag) >= 0)
            {
                return "Tag '" + tag + "' already exists. Nothing changed.";
            }

            int before = InternalEditorUtility.tags.Length;
            InternalEditorUtility.AddTag(tag);

            var after = InternalEditorUtility.tags;
            if (Array.IndexOf(after, tag) < 0)
            {
                return "Error: Unity refused the tag '" + tag + "'. The table still holds " + before
                    + " tag(s) and nothing changed.";
            }

            return "Added tag '" + tag + "'. Tags " + before + " -> " + after.Length + ".";
        }

        /// <summary>
        /// Removes a tag. Every object still carrying it becomes Untagged, so a tag that is in use is not
        /// removed until the call is repeated with the confirm token.
        /// </summary>
        /// <param name="args">The tag name, then optionally the confirm token.</param>
        /// <returns>What changed, what it would change, or why nothing did.</returns>
        private static string TagRemoveCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length == 0 || sections[0].Length == 0)
            {
                return "Error: a tag name is required. Usage: editor-tag-remove <tag> [| confirm]";
            }

            var tag = sections[0];
            var confirmed = sections.Length > 1
                && string.Equals(sections[1], CONFIRM_TOKEN, StringComparison.OrdinalIgnoreCase);

            if (Array.IndexOf(InternalEditorUtility.tags, tag) < 0)
            {
                return "Tag '" + tag + "' does not exist. Nothing changed.";
            }

            var users = ObjectsWithTag(tag);
            if (users.Count > 0 && !confirmed)
            {
                var preview = new StringBuilder();
                for (int i = 0; i < users.Count && i < 5; i++)
                {
                    preview.AppendLine().Append("  ").Append(users[i]);
                }

                if (users.Count > 5) preview.AppendLine().Append("  ... and ").Append(users.Count - 5).Append(" more");

                return "Refused: '" + tag + "' is on " + users.Count
                    + " object(s) in the open scene(s), which would all become Untagged." + preview
                    + Environment.NewLine + "Nothing changed. Repeat with | " + CONFIRM_TOKEN + " to remove it.";
            }

            int before = InternalEditorUtility.tags.Length;
            InternalEditorUtility.RemoveTag(tag);

            var after = InternalEditorUtility.tags;
            if (Array.IndexOf(after, tag) >= 0)
            {
                return "Error: Unity kept the tag '" + tag + "'. Nothing changed.";
            }

            var note = users.Count > 0
                ? " " + users.Count + " object(s) became Untagged."
                : " No object in the open scene(s) was using it.";
            return "Removed tag '" + tag + "'. Tags " + before + " -> " + after.Length + "." + note;
        }

        /// <summary>
        /// Renames a tag in place, which carries every object already carrying it. Measured: an object read
        /// back the NEW name immediately, CompareTag against the old name returned false and against the new
        /// one true, so a tag is held on the object as a table entry rather than as the literal string. That
        /// is what makes this worth a verb: removing and re-adding would set every one of those objects to
        /// Untagged and not put them back.
        /// </summary>
        /// <param name="args">The current name, then the new one.</param>
        /// <returns>What changed, or why nothing did.</returns>
        private static string TagRenameCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2 || sections[0].Length == 0 || sections[1].Length == 0)
            {
                return "Error: two names are required. Usage: editor-tag-rename <from> | <to>";
            }

            var from = sections[0];
            var to = sections[1];
            if (string.Equals(from, to, StringComparison.Ordinal)) return "Both names are '" + from + "'. Nothing changed.";

            if (Array.IndexOf(InternalEditorUtility.tags, from) < 0)
            {
                return "Error: no tag named '" + from + "'. Nothing changed.";
            }

            if (Array.IndexOf(InternalEditorUtility.tags, to) >= 0)
            {
                return "Error: a tag named '" + to + "' already exists. Nothing changed.";
            }

            var assets = AssetDatabase.LoadAllAssetsAtPath(TAG_MANAGER_PATH);
            if (assets == null || assets.Length == 0) return "Error: could not load " + TAG_MANAGER_PATH;

            var owner = new SerializedObject(assets[0]);
            var tags = owner.FindProperty("tags");
            if (tags == null || !tags.isArray) return "Error: the 'tags' array is missing from " + TAG_MANAGER_PATH;

            for (int i = 0; i < tags.arraySize; i++)
            {
                if (!string.Equals(tags.GetArrayElementAtIndex(i).stringValue, from, StringComparison.Ordinal))
                {
                    continue;
                }

                var carried = ObjectsWithTag(from).Count;
                tags.GetArrayElementAtIndex(i).stringValue = to;
                owner.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();

                if (Array.IndexOf(InternalEditorUtility.tags, to) < 0)
                {
                    return "Error: Unity did not take the rename. The table still holds '" + from + "'.";
                }

                return "Renamed tag '" + from + "' to '" + to + "'. " + carried
                    + " object(s) in the open scene(s) carried it and now read the new name.";
            }

            // Unity's builtin tags are not stored in the project at all, so they cannot be reached here.
            return "Error: '" + from + "' is one of Unity's builtin tags, which are not stored in the "
                + "project and cannot be renamed. Nothing changed.";
        }

        #endregion

        #region Layer commands

        /// <summary>
        /// Lists all 32 layer slots by index, empty ones included. The index is the part that matters: a
        /// mask is built from a number, and the editor API that returns names drops the empty slots so it
        /// cannot tell you which index a name sits at.
        /// </summary>
        /// <returns>One slot per line.</returns>
        private static string LayerListCommand()
        {
            string error;
            var layers = LoadLayerArray(out error);
            if (layers == null) return "Error: " + error;

            var report = new StringBuilder("Layers (" + layers.arraySize + " slots):");
            for (int i = 0; i < layers.arraySize; i++)
            {
                var name = layers.GetArrayElementAtIndex(i).stringValue;
                report.AppendLine().Append("  ").Append(i.ToString("00", CultureInfo.InvariantCulture))
                    .Append("  ").Append(string.IsNullOrEmpty(name) ? "(empty)" : name);
            }

            return report.ToString();
        }

        /// <summary>
        /// Names a layer slot. An empty slot is written straight away; an occupied one is refused until the
        /// call is repeated with the confirm token, because every object already on it changes meaning
        /// without anything in Unity reporting it.
        /// </summary>
        /// <param name="args">Index, then the name, then optionally the confirm token.</param>
        /// <returns>What changed, what it would change, or why nothing did.</returns>
        private static string LayerSetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Error: a slot and a name are required. "
                    + "Usage: editor-layer-set <indexOrName> | <name> [| confirm]";
            }

            var name = sections[1];
            if (name.Length == 0) return "Error: a layer name is required. To clear a slot use editor-layer-remove.";

            var confirmed = sections.Length > 2
                && string.Equals(sections[2], CONFIRM_TOKEN, StringComparison.OrdinalIgnoreCase);

            string error;
            var layers = LoadLayerArray(out error);
            if (layers == null) return "Error: " + error;

            int index = ResolveLayerIndex(sections[0], layers, out error);
            if (index < 0) return "Error: " + error;

            // Two slots sharing a name makes NameToLayer answer with one of them and silently ignore the
            // other, so this is refused outright rather than confirmed: no confirmation makes it coherent.
            for (int i = 0; i < layers.arraySize; i++)
            {
                if (i == index) continue;
                if (string.Equals(layers.GetArrayElementAtIndex(i).stringValue, name, StringComparison.Ordinal))
                {
                    return "Error: layer " + i + " is already named '" + name
                        + "'. Two slots with one name make a name lookup ambiguous. Nothing changed.";
                }
            }

            var slot = layers.GetArrayElementAtIndex(index);
            var previous = slot.stringValue;

            if (string.Equals(previous, name, StringComparison.Ordinal))
            {
                return "Layer " + index + " is already named '" + name + "'. Nothing changed.";
            }

            if (!string.IsNullOrEmpty(previous) && !confirmed)
            {
                var users = ObjectsOnLayer(index);
                var note = users > 0
                    ? " " + users + " object(s) in the open scene(s) are on it and would keep index " + index
                        + " under the new name."
                    : " No object in the open scene(s) is on it.";
                return "Refused: layer " + index + " already holds '" + previous + "'." + note
                    + Environment.NewLine + "Nothing changed. Repeat with | " + CONFIRM_TOKEN + " to rename it.";
            }

            slot.stringValue = name;
            slot.serializedObject.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            return "Set layer " + index + " to '" + name + "'"
                + (string.IsNullOrEmpty(previous) ? " (slot was empty)" : " (was '" + previous + "')")
                + ". " + VerifyLayer(index, name);
        }

        /// <summary>
        /// Clears a layer slot, addressed by index or by name. An occupied slot is refused until the call is
        /// repeated with the confirm token, and the report says how many objects are on it first.
        /// </summary>
        /// <param name="args">Index or name, then optionally the confirm token.</param>
        /// <returns>What changed, what it would change, or why nothing did.</returns>
        private static string LayerRemoveCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length == 0 || sections[0].Length == 0)
            {
                return "Error: an index or name is required. Usage: editor-layer-remove <indexOrName> [| confirm]";
            }

            var confirmed = sections.Length > 1
                && string.Equals(sections[1], CONFIRM_TOKEN, StringComparison.OrdinalIgnoreCase);

            string error;
            var layers = LoadLayerArray(out error);
            if (layers == null) return "Error: " + error;

            int index = ResolveLayerIndex(sections[0], layers, out error);
            if (index < 0) return "Error: " + error;

            var slot = layers.GetArrayElementAtIndex(index);
            var previous = slot.stringValue;
            if (string.IsNullOrEmpty(previous)) return "Layer " + index + " is already empty. Nothing changed.";

            if (!confirmed)
            {
                var users = ObjectsOnLayer(index);
                var note = users > 0
                    ? " " + users + " object(s) in the open scene(s) are on it and would be left on an unnamed index."
                    : " No object in the open scene(s) is on it.";
                return "Refused: layer " + index + " holds '" + previous + "'." + note
                    + Environment.NewLine + "Nothing changed. Repeat with | " + CONFIRM_TOKEN + " to clear it.";
            }

            slot.stringValue = string.Empty;
            slot.serializedObject.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();

            return "Cleared layer " + index + " (was '" + previous + "'). " + VerifyLayer(index, string.Empty);
        }

        #endregion

        #region Tag and layer helpers

        /// <summary>
        /// Resolves a slot given either its index or its current name, so a caller who knows a layer as
        /// "Environment" need not look its number up first. An index is tried before a name, which decides
        /// the one ambiguous case: a layer literally named "22" is reached by name only.
        /// </summary>
        /// <param name="token">The index or name the caller supplied.</param>
        /// <param name="layers">The open layer array.</param>
        /// <param name="error">Receives why the slot could not be resolved.</param>
        /// <returns>The slot index, or -1 with <paramref name="error"/> set.</returns>
        private static int ResolveLayerIndex(string token, SerializedProperty layers, out string error)
        {
            error = null;

            int index;
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
            {
                if (index >= 0 && index < LAYER_COUNT) return index;

                error = "layer index " + index + " is out of range. Unity has exactly "
                    + LAYER_COUNT + " slots, 0 to " + (LAYER_COUNT - 1) + ".";
                return -1;
            }

            for (int i = 0; i < layers.arraySize; i++)
            {
                if (string.Equals(layers.GetArrayElementAtIndex(i).stringValue, token, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            error = "no layer is named '" + token + "' and it is not an index. Nothing changed.";
            return -1;
        }

        /// <summary>
        /// Opens the layer array on the TagManager asset. Held as a live SerializedProperty rather than
        /// copied out, because the write goes back through the same object.
        /// </summary>
        /// <param name="error">Receives why the array could not be opened.</param>
        /// <returns>The array property, or null.</returns>
        private static SerializedProperty LoadLayerArray(out string error)
        {
            error = null;
            var assets = AssetDatabase.LoadAllAssetsAtPath(TAG_MANAGER_PATH);
            if (assets == null || assets.Length == 0)
            {
                error = "could not load " + TAG_MANAGER_PATH;
                return null;
            }

            var layers = new SerializedObject(assets[0]).FindProperty(LAYER_ARRAY_PROPERTY);
            if (layers == null || !layers.isArray)
            {
                error = "the '" + LAYER_ARRAY_PROPERTY + "' array is missing from " + TAG_MANAGER_PATH;
                return null;
            }

            return layers;
        }

        /// <summary>
        /// Reports whether a layer write actually took, on both the live API and on disk. Applying the
        /// property is not evidence on its own, and a table that changed only in memory would be lost on
        /// the next editor start while every read in this session kept agreeing with it.
        /// </summary>
        /// <param name="index">The slot written.</param>
        /// <param name="expected">The name it should now hold, empty when cleared.</param>
        /// <returns>A read-back sentence.</returns>
        private static string VerifyLayer(int index, string expected)
        {
            var live = InternalEditorUtility.GetLayerName(index) ?? string.Empty;
            var liveMatches = string.Equals(live, expected, StringComparison.Ordinal);

            string onDisk;
            try
            {
                onDisk = expected.Length == 0
                    ? "cleared"
                    : (DiskHoldsLayerName(expected) ? "present" : "absent");
            }
            catch (Exception ex)
            {
                onDisk = "unreadable (" + ex.GetType().Name + ")";
            }

            return "Read back: editor API "
                + (liveMatches ? "agrees" : "still reports '" + live + "'")
                + ", " + TAG_MANAGER_PATH + " " + onDisk + ".";
        }

        /// <summary>
        /// Looks for a layer name as a whole YAML list entry rather than as a substring anywhere in the
        /// file. A substring search reports success for any short name the file happens to contain in
        /// another role, so a two-letter layer such as UI would verify against a word inside an unrelated
        /// value and confirm a write that never landed.
        /// </summary>
        /// <param name="name">The layer name to look for.</param>
        /// <returns>True when a list entry on its own line holds exactly that name.</returns>
        private static bool DiskHoldsLayerName(string name)
        {
            var wanted = "- " + name;
            foreach (var line in File.ReadAllLines(TAG_MANAGER_PATH))
            {
                if (string.Equals(line.Trim(), wanted, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Names the objects in the open scenes carrying a tag, inactive ones included.</summary>
        /// <param name="tag">The tag to look for.</param>
        /// <returns>Scene paths of the objects using it.</returns>
        private static List<string> ObjectsWithTag(string tag)
        {
            var found = new List<string>();
            foreach (var transform in OpenSceneTransforms())
            {
                if (transform.gameObject.CompareTag(tag)) found.Add(GetGameObjectPath(transform.gameObject));
            }

            return found;
        }

        /// <summary>Counts the objects in the open scenes on a layer, inactive ones included.</summary>
        /// <param name="layer">The layer index.</param>
        /// <returns>How many objects sit on it.</returns>
        private static int ObjectsOnLayer(int layer)
        {
            int count = 0;
            foreach (var transform in OpenSceneTransforms())
            {
                if (transform.gameObject.layer == layer) count++;
            }

            return count;
        }

        /// <summary>
        /// Every transform in every loaded scene, inactive included. Scene roots are walked rather than
        /// FindObjectsOfType, which skips inactive objects and would under-report exactly the objects a
        /// caller is least likely to remember.
        /// </summary>
        /// <returns>Each transform in the open scenes.</returns>
        private static IEnumerable<Transform> OpenSceneTransforms()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                    {
                        yield return transform;
                    }
                }
            }
        }

        #endregion

        #region Tag and layer dispatch

        /// <summary>
        /// Routes the tag and layer verbs. Returns null when the verb belongs to another area, which is what
        /// lets <see cref="ExecuteCommand"/> try each area in turn.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchTagsLayers(string verb, string command, string[] parts)
        {
            if (verb == "editor-tag-list") return TagListCommand();
            if (verb == "editor-tag-add") return TagAddCommand(GetCommandRemainder(command, parts[0]));
            if (verb == "editor-tag-remove") return TagRemoveCommand(GetCommandRemainder(command, parts[0]));
            if (verb == "editor-tag-rename") return TagRenameCommand(GetCommandRemainder(command, parts[0]));

            if (verb == "editor-layer-list") return LayerListCommand();
            if (verb == "editor-layer-set") return LayerSetCommand(GetCommandRemainder(command, parts[0]));
            if (verb == "editor-layer-remove") return LayerRemoveCommand(GetCommandRemainder(command, parts[0]));

            return null;
        }

        #endregion
    }
}
