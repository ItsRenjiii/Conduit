/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Undo and redo, and the reason both are reported before they are performed.
//
// Unity's undo stack is shared: it holds whatever the person at the keyboard last did alongside whatever
// the bridge last did, and PerformUndo reverts the top of it regardless of which. A baseline read during
// development found "Driving RectTransform" sitting on top with a bridge operation merged into the SAME
// group behind it, so a single undo would have reverted a hand edit and a bridge edit together, with
// nothing reporting that it had.
//
// Two things follow. Conduit opens its own undo group before any authoring command, so one call is one
// entry and the name describes it. And these verbs name what they are about to revert and change nothing
// until the call is repeated with confirm, because an undo cannot be inspected after the fact.
//
// Coverage is partial and the verbs say so rather than implying otherwise: only operations that registered
// with Undo are on the stack at all. Asset and file work goes through the AssetDatabase and is not
// undoable by any means, so an undo issued after a script write reverts whatever came before it instead.
using System;
using System.Globalization;
using UnityEditor;

namespace ItsRenji.Conduit
{
    internal static partial class ConduitBridge
    {
        #region Undo constants

        /// <summary>Prefix on every undo group Conduit opens, so a caller can tell whose work is on top.</summary>
        internal const string UNDO_GROUP_PREFIX = "Conduit ";

        #endregion

        #region Undo commands

        /// <summary>
        /// Reverts the top of Unity's undo stack, naming it first. The name is the whole point: the stack is
        /// shared with the editor's own history, so what sits on top is frequently not the bridge's work.
        /// </summary>
        /// <param name="args">Optionally the confirm token.</param>
        /// <returns>What would be reverted, or what was.</returns>
        private static string UndoCommand(string args)
        {
            var confirmed = CarriesConfirmToken(args);

            var group = UnityEditor.Undo.GetCurrentGroup();
            var label = UnityEditor.Undo.GetCurrentGroupName();

            if (!confirmed)
            {
                return "Refused: the next undo would revert undo group "
                    + group.ToString(CultureInfo.InvariantCulture) + ", which Unity labels '" + label + "'."
                    + Environment.NewLine
                    + "A group's label is set when it opens and does not describe whatever merged into it "
                    + "afterwards, so this can revert more, or other, work than the label names - including "
                    + "the editor's own if it shares the group."
                    + Environment.NewLine
                    + "Nothing changed. Repeat with | " + CONFIRM_TOKEN + " to revert it.";
            }

            UnityEditor.Undo.PerformUndo();

            return "Undid undo group " + group.ToString(CultureInfo.InvariantCulture)
                + ", labelled '" + label + "'. Read back whatever you expected it to change rather than "
                + "trusting the label, which names when the group opened and not what was in it. The next "
                + "undo would revert group "
                + UnityEditor.Undo.GetCurrentGroup().ToString(CultureInfo.InvariantCulture)
                + ", labelled '" + UnityEditor.Undo.GetCurrentGroupName() + "'.";
        }

        /// <summary>
        /// Replays the last undone operation. Unity exposes no name for the next redo, so unlike undo this
        /// cannot say in advance what it will restore, and the report says that rather than inventing one.
        /// </summary>
        /// <param name="args">Optionally the confirm token.</param>
        /// <returns>What changed, or the refusal.</returns>
        private static string RedoCommand(string args)
        {
            var confirmed = CarriesConfirmToken(args);

            var before = UnityEditor.Undo.GetCurrentGroup();
            if (!confirmed)
            {
                return "Refused: redo replays whatever was last undone, and Unity exposes no name for it, so "
                    + "this cannot say in advance what it would restore. The undo stack is at group "
                    + before.ToString(CultureInfo.InvariantCulture) + "."
                    + Environment.NewLine
                    + "Nothing changed. Repeat with | " + CONFIRM_TOKEN + " to redo.";
            }

            UnityEditor.Undo.PerformRedo();

            var after = UnityEditor.Undo.GetCurrentGroup();
            if (after == before)
            {
                return "Nothing to redo. The undo stack is still at group "
                    + after.ToString(CultureInfo.InvariantCulture) + ".";
            }

            return "Redone. The undo stack moved from group " + before.ToString(CultureInfo.InvariantCulture)
                + " to " + after.ToString(CultureInfo.InvariantCulture)
                + ", and the next undo would revert '" + UnityEditor.Undo.GetCurrentGroupName() + "'.";
        }

        /// <summary>
        /// Reports whether a confirm token appears anywhere in the arguments.
        ///
        /// A verb whose only argument is the optional confirm has no value before the pipe, so the documented
        /// form leaves an empty leading section and a fixed-index check reads that empty string instead of
        /// the token: the call then refuses forever and looks like the confirm is being ignored. Scanning
        /// every section also accepts the form without the pipe, which is what a caller types by hand.
        /// </summary>
        /// <param name="args">Raw argument text.</param>
        /// <returns>True when the caller asked for the change to be made.</returns>
        private static bool CarriesConfirmToken(string args)
        {
            foreach (var section in SplitCommandSections(args))
            {
                if (string.Equals(section.Trim(), CONFIRM_TOKEN, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        #endregion

        #region Undo grouping

        /// <summary>
        /// Opens this command's undo group, once, on the first operation that actually registers an undo.
        ///
        /// The grouping has to happen here rather than at dispatch, because only this point knows that an
        /// undo is genuinely being registered. Deciding it from the verb's name instead meant classifying
        /// every verb twice, and the second classification was wrong: a script read and a batch edit both
        /// opened a group, and neither can be undone by Unity at all, so the stack filled with entries that
        /// revert nothing. Those are not merely untidy - they land in the editor's own undo history, so the
        /// person at the keyboard presses undo and watches it do nothing.
        ///
        /// Grouping at all is still necessary in the other direction: without it the bridge's work joins
        /// whatever group the editor has open, which is routinely the user's own last action, and one undo
        /// then takes both.
        /// </summary>
        /// <param name="label">What the registering operation calls itself, used to name the entry.</param>
        private static void EnsureUndoGroup(string label)
        {
            var context = _commandContext;

            // No context means this is not running as a bridge command, so the undo history belongs to
            // somebody else and nothing here should be reorganising it.
            if (context == null || context.UndoGroupOpened) return;

            context.UndoGroupOpened = true;
            try
            {
                var name = string.IsNullOrEmpty(label) ? UNDO_GROUP_PREFIX.Trim() : label;
                if (!name.StartsWith(UNDO_GROUP_PREFIX, StringComparison.Ordinal)) name = UNDO_GROUP_PREFIX + name;

                UnityEditor.Undo.IncrementCurrentGroup();
                UnityEditor.Undo.SetCurrentGroupName(name);
            }
            catch (Exception)
            {
                // Grouping is a convenience for the caller's undo history, never a precondition for the
                // command itself, so a failure here must not stop the work from running.
            }
        }

        /// <summary>Records an object's state before a change, opening this command's undo group first.</summary>
        /// <param name="target">The object about to change.</param>
        /// <param name="label">What to call the entry.</param>
        private static void RecordUndo(UnityEngine.Object target, string label)
        {
            EnsureUndoGroup(label);
            UnityEditor.Undo.RecordObject(target, label);
        }

        /// <summary>Records an object's complete state before a change, opening this command's group first.</summary>
        /// <param name="target">The object about to change.</param>
        /// <param name="label">What to call the entry.</param>
        private static void RecordCompleteUndo(UnityEngine.Object target, string label)
        {
            EnsureUndoGroup(label);
            UnityEditor.Undo.RegisterCompleteObjectUndo(target, label);
        }

        /// <summary>Registers a newly created object, opening this command's undo group first.</summary>
        /// <param name="target">The object just created.</param>
        /// <param name="label">What to call the entry.</param>
        private static void RegisterCreatedUndo(UnityEngine.Object target, string label)
        {
            EnsureUndoGroup(label);
            UnityEditor.Undo.RegisterCreatedObjectUndo(target, label);
        }

        /// <summary>Reparents a transform undoably, opening this command's undo group first.</summary>
        /// <param name="child">The transform being moved.</param>
        /// <param name="parent">Its new parent, or null for the scene root.</param>
        /// <param name="label">What to call the entry.</param>
        private static void SetTransformParentUndo(
            UnityEngine.Transform child, UnityEngine.Transform parent, string label)
        {
            EnsureUndoGroup(label);
            UnityEditor.Undo.SetTransformParent(child, parent, label);
        }

        /// <summary>Destroys an object undoably, opening this command's undo group first.</summary>
        /// <param name="target">The object to destroy.</param>
        /// <param name="label">What to call the entry.</param>
        private static void DestroyWithUndo(UnityEngine.Object target, string label)
        {
            EnsureUndoGroup(label);
            UnityEditor.Undo.DestroyObjectImmediate(target);
        }

        #endregion

        #region Undo dispatch

        /// <summary>
        /// Routes the undo verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchUndo(string verb, string command, string[] parts)
        {
            if (verb == "editor-undo") return UndoCommand(GetCommandRemainder(command, parts[0]));
            if (verb == "editor-redo") return RedoCommand(GetCommandRemainder(command, parts[0]));

            return null;
        }

        #endregion
    }
}
