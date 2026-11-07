/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Script-file commands that use AssetDatabase refresh/import without adding a Roslyn execution surface.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Script Commands

        /// <summary>
        /// Creates or overwrites a C# script asset, retaining an existing file's exact pre-image on a
        /// best-effort basis outside Assets before writing, then capturing the completed version as well.
        /// Byte-identical requests are refused.
        /// Requests a synchronous import, which is what queues
        /// Unity's compile. The response says so explicitly because a caller that cannot see the import
        /// happen reaches for reimport next, and that second request compiles the same change twice and
        /// lands while the first compile is still running, which is what makes it time out.
        /// </summary>
        /// <param name="args">Pipe-separated asset path and file content.</param>
        /// <returns>A confirmation line carrying the do-not-reimport notice, or a validation error.</returns>
        private static string ScriptUpdateOrCreateCommand(string args)
        {
            // Split on the FIRST pipe only: everything before it is the asset path, everything after is the
            // file content, taken verbatim. Real C# source routinely contains '|' (logical || and bitwise
            // operators), so the generic SplitCommandSections - which splits on EVERY pipe and would return
            // only the text up to the first one - must NOT be used here; it truncates the file at its first '|'.
            var pipeIndex = args.IndexOf('|');
            if (pipeIndex < 0)
            {
                return "Usage: script-update-or-create <assetPath> | <content>";
            }

            var assetPath = NormalizeAssetPath(args.Substring(0, pipeIndex));

            // Leading whitespace is the separator after the pipe and goes, and so do trailing spaces and
            // tabs, but NOT a trailing newline: a plain Trim() took it off, so a file written here ended '}'
            // while every hand-authored source ends '}' and a newline. udon-batch-edit preserves whatever it
            // finds, so it then carried that difference forward as a whole-file change in the next diff.
            var content = args.Substring(pipeIndex + 1).TrimStart().TrimEnd(' ', '\t');
            if (content.Length == 0)
            {
                return "Usage: script-update-or-create <assetPath> | <content>";
            }

            if (!assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return "Script path must end in .cs: " + assetPath;
            }

            string pathError;
            if (!TryValidateWritableScriptAssetPath(assetPath, out pathError))
            {
                return pathError;
            }

            // A brand-new UdonSharp behaviour must be made via create-usharp (which writes the .cs AND links
            // the UdonSharpProgramAsset). Writing it directly leaves it unlinked, and Udon will later stub
            // over the file - wiping the content. Editing an existing script is always fine.
            var isNewFile = !File.Exists(assetPath);
            if (IsWorldsProject && isNewFile && DeclaresUdonSharpBehaviour(content))
            {
                return "Refused: '" + assetPath + "' declares a new UdonSharpBehaviour. Run 'create-usharp " + assetPath +
                    "' first (writes the script and links its UdonSharpProgramAsset), then re-run script-update-or-create to set the content. " +
                    "Writing a U# behaviour directly leaves it unlinked and Udon will overwrite it with a stub.";
            }

            string directory = Path.GetDirectoryName(assetPath);
            if (assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            }
            else
            {
                EnsureAssetFolder(directory);
            }

            // Encoding.UTF8 emits a byte order mark, which is why every file this verb produced began EF BB
            // BF while the hand-authored ones begin at their first character. An overwrite keeps whatever mark
            // the file already carries, matching udon-batch-edit, so writing a file never changes its leading
            // bytes as a side effect; a new file gets none, which is what the rest of these sources have.
            bool hadBom = false;
            byte[] originalBytes = null;
            if (!isNewFile)
            {
                try
                {
                    originalBytes = File.ReadAllBytes(assetPath);
                    hadBom = originalBytes.Length >= 3
                        && originalBytes[0] == 0xEF
                        && originalBytes[1] == 0xBB
                        && originalBytes[2] == 0xBF;
                }
                catch (Exception)
                {
                    // Unreadable here means the write below reports the real failure, so assume no mark.
                    hadBom = false;
                }
            }

            if (FileHistoryUtf8Matches(originalBytes, content, hadBom))
                return "No change: " + assetPath + " already contains these exact bytes. No write, history capture or import occurred.";
            if (isNewFile) return WriteScriptContent(assetPath, content, hadBom, true, "History: new file; no prior version exists.");
            return WithFileHistory(new System.Collections.Generic.Dictionary<string, byte[]> { [assetPath] = originalBytes },
                "script-update-or-create", history =>
                {
                    if (!FileHistoryCurrentMatches(assetPath, originalBytes))
                        return "Refused: target changed during history preparation. Nothing was written.\n" + history;
                    return WriteScriptContent(assetPath, content, hadBom, false, history);
                });
        }

        /// <summary>Writes validated source and preserves the write's evidence through its import and compile.</summary>
        /// <param name="assetPath">Validated writable source path.</param>
        /// <param name="content">Requested source text.</param>
        /// <param name="hadBom">Whether to preserve a UTF-8 byte order mark.</param>
        /// <param name="isNewFile">Whether this creates rather than replaces a script.</param>
        /// <param name="history">Pre-write history evidence or warning.</param>
        /// <returns>The write result or its withheld compile response.</returns>
        private static string WriteScriptContent(string assetPath, string content, bool hadBom, bool isNewFile, string history)
        {
            Action releaseHistoryWrite = HoldFileHistoryWrite();
            try
            {
                File.WriteAllText(assetPath, content, new UTF8Encoding(hadBom));
                var completed = new System.Collections.Generic.Dictionary<string, byte[]>
                    { [assetPath] = FileHistoryWrittenBytes(content, hadBom) };
                return WithFileHistory(completed, "script-update-or-create", after =>
                    ImportScriptContent(assetPath, content, isNewFile, history + Environment.NewLine + after),
                    "after-write", releaseHistoryWrite);
            }
            catch
            {
                releaseHistoryWrite();
                throw;
            }
        }

        /// <summary>Imports source after its completed version has been captured or a history warning recorded.</summary>
        /// <param name="assetPath">Successfully written script.</param>
        /// <param name="content">Written source used for runtime compatibility guidance.</param>
        /// <param name="isNewFile">Whether the operation created a script.</param>
        /// <param name="history">Before and after version evidence.</param>
        /// <returns>The withheld compile response or immediate status when no durable owner exists.</returns>
        private static string ImportScriptContent(string assetPath, string content, bool isNewFile, string history)
        {
            // A caller that just wrote the whole file knows its content as surely as one that just read
            // it, so the write satisfies udon-batch-edit's read-before-edit gate for follow-up edits.
            RecordScriptReadHash(assetPath);

            // The "won't ship" caveat is narrow: it applies only to a NEW plain-C# runtime MonoBehaviour,
            // which VRChat strips from world uploads (only Udon runs at runtime). UdonSharp behaviours DO
            // ship, editor scripts are editor-only by design, and editing an existing file is not a new ship
            // surface - flagging any of those (as this once did on every write) only misleads the caller.
            bool shipsAsUdon = DeclaresUdonSharpBehaviour(content);
            bool isEditorScript = assetPath.IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0;
            bool flagStripping = IsVrchatProject && isNewFile && !shipsAsUdon && !isEditorScript;
            string strippedNote = flagStripping
                ? " Note: this is a new plain-C# script (not a UdonSharpBehaviour) - it runs in the editor but is stripped from VRChat world uploads, so it won't ship at runtime. Use create-usharp for runtime logic."
                : string.Empty;
            // A deferred note may span several lines. It rides across the reload inside the ledger's JSON
            // recipe, which escapes newlines like any other character, so nothing is lost. The older file
            // channel that truncated at the first newline no longer exists: without a durable job there is
            // now no response channel to withhold at all, so every deferral goes through the ledger.
            // Measured by sending two extra lines through and reading both back.
            string summary = "Wrote script " + assetPath + " and imported it. The post-compile report below IS"
                + " this write's verdict, so nothing needs to follow this call - do not reimport it, and do not"
                + " read the console for it." + strippedNote + Environment.NewLine + history;

            // Importing a script compiles it, so the response is withheld until that compile and its domain
            // reload settle and the summary rides across with them. Returning early left the caller no verdict
            // at all, and the obvious next call - a console read - either queued behind the very compile it was
            // waiting on, or answered from before it and reported a failed build as a clean one.
            if (TryDeferUntilReload("__deferred-note " + summary))
            {
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
            return summary + Environment.NewLine + FormatEditorStatus();
        }

        /// <summary>
        /// Matches a real class declaration inheriting <c>UdonSharpBehaviour</c>, allowing a generic parameter
        /// list and other base types in the list. Anchored on <c>class</c> so the name alone never matches.
        /// </summary>
        private static readonly Regex UdonSharpDeclarationPattern = new Regex(
            @"\bclass\s+[A-Za-z_]\w*\s*(?:<[^>]*>)?\s*:\s*[^{;]*\bUdonSharpBehaviour\b",
            RegexOptions.Compiled);

        /// <summary>
        /// True when the script content DECLARES a class inheriting <c>UdonSharpBehaviour</c>. Used to gate
        /// direct writes of new U# behaviours toward the create-usharp workflow.
        /// </summary>
        /// <remarks>
        /// Comments and string literals are removed before matching. Testing the raw text for the substring
        /// fired on the type NAME appearing anywhere at all - a doc comment, an explanatory line, a generator
        /// emitting U# source - and refused the write while confidently describing a declaration the file did
        /// not contain. The guard itself is worth keeping; only the test was too broad.
        /// </remarks>
        /// <param name="content">The C# source about to be written.</param>
        /// <returns><c>true</c> if the source declares a class inheriting UdonSharpBehaviour.</returns>
        private static bool DeclaresUdonSharpBehaviour(string content)
        {
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            return UdonSharpDeclarationPattern.IsMatch(StripCommentsAndStringLiterals(content));
        }

        /// <summary>
        /// Returns the source with comments and string literals removed, so a textual search sees CODE only.
        /// </summary>
        /// <remarks>
        /// Single pass, left to right, because the constructs nest in one direction: a quote inside a comment
        /// is comment text and a slash pair inside a string is string text, and whichever opens first wins.
        /// Layout is otherwise preserved so line-based reporting still lines up.
        /// </remarks>
        /// <param name="source">C# source text.</param>
        /// <returns>The source with comment and literal spans dropped.</returns>
        private static string StripCommentsAndStringLiterals(string source)
        {
            var builder = new StringBuilder(source.Length);
            var index = 0;

            while (index < source.Length)
            {
                var current = source[index];
                var next = index + 1 < source.Length ? source[index + 1] : '\0';

                if (current == '/' && next == '/')
                {
                    while (index < source.Length && source[index] != '\n') index++;
                    continue;
                }

                if (current == '/' && next == '*')
                {
                    index += 2;
                    while (index + 1 < source.Length && !(source[index] == '*' && source[index + 1] == '/')) index++;
                    index = System.Math.Min(index + 2, source.Length);
                    continue;
                }

                if (current == '@' && next == '"')
                {
                    // Verbatim string: a doubled quote is an escaped quote, not the end.
                    index += 2;
                    while (index < source.Length)
                    {
                        if (source[index] != '"') { index++; continue; }
                        if (index + 1 < source.Length && source[index + 1] == '"') { index += 2; continue; }
                        index++;
                        break;
                    }

                    continue;
                }

                if (current == '"' || current == '\'')
                {
                    var quote = current;
                    index++;
                    while (index < source.Length)
                    {
                        if (source[index] == '\\') { index += 2; continue; }
                        if (source[index] == quote) { index++; break; }
                        index++;
                    }

                    continue;
                }

                builder.Append(current);
                index++;
            }

            return builder.ToString();
        }

        /// <summary>
        /// Reads a script file or a one-based line slice from it.
        /// </summary>
        /// <param name="args">Script path plus optional start line and count.</param>
        /// <returns>The requested script content.</returns>
        private static string ScriptReadCommand(string args)
        {
            var tokens = SplitCommandTokens(args);
            if (tokens.Length == 0)
            {
                return "Usage: script-read <assetPath> [startLine] [count]";
            }

            var assetPath = NormalizeAssetPath(tokens[0]);
            if (!File.Exists(assetPath))
            {
                return "Script not found: " + assetPath;
            }

            // Record the file's current hash so udon-batch-edit can enforce read-before-edit: an edit is
            // only accepted while the file still matches what the caller last saw through the bridge. A
            // line-slice read still records the FULL file's hash, because the gate is per-file.
            RecordScriptReadHash(assetPath);

            var lines = File.ReadAllLines(assetPath, Encoding.UTF8);
            if (tokens.Length == 1)
            {
                return string.Join(Environment.NewLine, lines);
            }

            // The start line takes NO ceiling: it is an OFFSET into the file, not a quantity. Bounding it
            // returned line 500 for every file longer than that, silently and with the right line labels.
            var start = ParsePositiveInt(tokens[1], 1);
            var count = tokens.Length > 2 ? ParsePositiveInt(tokens[2], 80, MAX_SCRIPT_READ_LINES) : 80;
            var builder = new StringBuilder();
            for (var i = start - 1; i < lines.Length && i < start - 1 + count; i++)
            {
                builder.Append(i + 1).Append(": ").AppendLine(lines[i]);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Deletes a C# script asset through AssetDatabase.
        /// </summary>
        /// <param name="args">The script asset path to delete.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string ScriptDeleteCommand(string args)
        {
            var assetPath = NormalizeAssetPath(args);
            if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return "Usage: script-delete <Assets/path.cs | Packages/embedded.id/path.cs>";
            }

            string pathError;
            if (!TryValidateWritableScriptAssetPath(assetPath, out pathError))
            {
                return pathError;
            }

            if (!AssetDatabase.DeleteAsset(assetPath))
            {
                return "Failed to delete script: " + assetPath;
            }

            // The guard above admits only a .cs, so removing it always rebuilds the script assemblies and
            // UdonSharp rebuilds every U# program with them. Arm the deferral before the import so the
            // caller receives the post-compile report rather than a reply the editor cannot follow.
            if (TryDeferUntilReload("__deferred-note Deleted script " + assetPath + "."))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return "Deleted script " + assetPath + ".";
        }

        /// <summary>
        /// The refusal every verb that writes, creates, deletes or restores a script returns while Play Mode is active
        /// or changing, or null when the write may go ahead. A script changed in Play Mode starts a compile that Play
        /// Mode holds back, and the bridge then waits on that compile until Play Mode ends, so the refusal comes before
        /// anything is touched and says so, with the editor state it read.
        /// </summary>
        /// <param name="verb">The verb being refused, named in the reply.</param>
        /// <returns>The refusal, or null when Play Mode is off.</returns>
        private static string ScriptWriteRefusedInPlayMode(string verb)
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode) return null;

            return verb + " refused: Play Mode is active or changing, and a script changed now starts a compile that Play Mode "
                + "holds back until it ends. Nothing was written, created, deleted or restored. Exit Play Mode, then run it again."
                + Environment.NewLine + FormatEditorStatus();
        }

        #endregion
            /// <summary>
        /// Routes the Script verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchScript(string verb, string command, string[] parts)
        {
            if (verb == "script-update-or-create")
            {
                // Verbatim, because the tail of this one is a file's content and its last byte is part of
                // the file. Every other verb here parses tokens and wants both ends trimmed.
                return ScriptWriteRefusedInPlayMode(verb) ?? ScriptUpdateOrCreateCommand(GetVerbatimCommandRemainder(command, parts[0]));
            }

            if (verb == "file-history")
            {
                return FileHistoryCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "file-restore")
            {
                return ScriptWriteRefusedInPlayMode(verb) ?? FileRestoreCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "script-read")
            {
                return ScriptReadCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "script-delete")
            {
                return ScriptWriteRefusedInPlayMode(verb) ?? ScriptDeleteCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "udon-batch-edit")
            {
                return UdonBatchEditCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "create-usharp-batch" || verb == "create-udonsharp-batch" || verb == "usharp-create-batch")
            {
                return ScriptWriteRefusedInPlayMode(verb) ?? CreateUdonSharpBatchCommand(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
