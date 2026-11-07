/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// The udon-batch-edit command: N exact-string replacements across M source files in one atomic call,
    /// one write per file, and ONE compile for the whole batch. Accepts every file kind the house style
    /// covers - C# and shader sources - so a shader edit gets the same atomic validation, read-before-edit
    /// gate and single compile as a script. UdonSharp's file-watcher auto-compile is suppressed for the
    /// duration of a batch that touches C#, so intermediate writes cannot fire stale-assembly program
    /// compiles, and the user's own setting is restored afterwards whatever happens - success, reload, or
    /// crash. A batch with no C# in it leaves that setting alone.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Batch Edit Constants

        /// <summary>Full name of UdonSharp's settings type. It lives in the EDITOR namespace and is internal.</summary>
        private const string UDONSHARP_SETTINGS_TYPE = "UdonSharpEditor.UdonSharpSettings";

        /// <summary>Field on the settings object that arms the source file watcher.</summary>
        private const string AUTO_COMPILE_FIELD = "autoCompileOnModify";

        /// <summary>Ticks of confirmed idle before the no-reload fallback restores the auto-compile flag.</summary>
        private const int RESTORE_IDLE_TICKS = 50;

        /// <summary>Report mode that echoes every edit as a line-numbered diff with context. The default.</summary>
        private const string BATCH_REPORT_FULL = "full";

        /// <summary>
        /// Report mode that gives one line per edit in place of its diff: the file, the edit's index, the lines
        /// it landed on and how many lines it removed and added. The full echo of a long or whole-file batch
        /// outgrows what a client will show in one reply.
        /// </summary>
        private const string BATCH_REPORT_SUMMARY = "summary";

        /// <summary>
        /// File kinds this verb will edit, matching the set the house code style sets rules for. Excluding
        /// shaders forced them out to whole-file writes, losing the atomic all-or-nothing validation, the
        /// read-before-edit gate and the one-compile batching that are the entire point of the verb.
        /// </summary>
        private static readonly string[] BatchEditableExtensions = { ".cs", ".shader", ".hlsl", ".cginc" };

        /// <summary>
        /// EditorPrefs key stashing the pre-suppression auto-compile value. EditorPrefs is machine-global,
        /// so the key carries a project hash; it survives a crashed editor, which is the point.
        /// </summary>
        private static string AutoCompileStashKey =>
            "Conduit.BatchEdit.AutoCompileStash." + Application.dataPath.GetHashCode().ToString("X8");

        /// <summary>Idle ticks counted so far by the no-reload restore fallback.</summary>
        private static int _restoreIdleTicks;

        #endregion

        #region Batch Edit Command

        /// <summary>
        /// Validates and applies a JSON batch of exact-string edits. EVERY edit is resolved against its
        /// file's ORIGINAL content before a single byte is written, so one failure anywhere refuses the
        /// whole batch with nothing changed. On success the response is withheld until the one resulting
        /// compile and domain reload settle, then returns the per-edit evidence plus the post-compile report:
        /// every edit's diff, or one line per edit when the report section asks for a summary. A refusal
        /// reads the same whichever report was asked for.
        /// </summary>
        /// <param name="args">
        /// Optionally pipe-prefixed JSON array [{"file_path","old_string","new_string","replace_all"?}, ...], or the
        /// project path of a .json file holding one, optionally followed by a report section, "| full" or "| summary".
        /// </param>
        /// <returns>Per-edit evidence and the post-compile report, or a precise refusal.</returns>
        private static string UdonBatchEditCommand(string args)
        {
            string playModeRefusal = ScriptWriteRefusedInPlayMode("udon-batch-edit");
            if (playModeRefusal != null) return playModeRefusal;

            // The signature is "udon-batch-edit <| edits> [| report:full|summary]", so the MCP layer sends the
            // JSON verbatim with any report section after it; a raw hand-typed call without the leading pipe
            // is accepted too.
            string payload = (args ?? string.Empty).Trim();
            if (payload.StartsWith("|", StringComparison.Ordinal))
            {
                payload = payload.Substring(1).Trim();
            }

            if (payload.Length == 0)
            {
                return "Usage: udon-batch-edit | <edits> [| report:full|summary]  where edits is [{\"file_path\",\"old_string\",\"new_string\",\"replace_all\"?}, ...] or the project path of a .json file holding that array, written by hand with the agent's own file tool";
            }

            string reportError = SplitBatchEditReport(payload, out string editsJson, out string report);
            if (reportError != null)
            {
                return reportError;
            }

            List<BatchEditSpec> edits;
            try
            {
                edits = ParseBatchEditPayload(ReadBatchEditPayload(editsJson));
            }
            catch (Exception ex)
            {
                return "Invalid edits: " + ex.Message + " Nothing was written.";
            }

            // ── Validation phase: resolve every edit fully before any mutation ─────────
            var fileOrder = new List<string>();
            var editsByFile = new Dictionary<string, List<BatchEditSpec>>(StringComparer.OrdinalIgnoreCase);
            foreach (BatchEditSpec edit in edits)
            {
                if (!editsByFile.TryGetValue(edit.File, out List<BatchEditSpec> fileEdits))
                {
                    fileEdits = new List<BatchEditSpec>();
                    editsByFile[edit.File] = fileEdits;
                    fileOrder.Add(edit.File);
                }

                fileEdits.Add(edit);
            }

            var plans = new List<BatchEditFilePlan>();
            var evidence = new List<string>();
            var diffs = new List<string>();
            var landings = new List<string>();
            foreach (string file in fileOrder)
            {
                if (!File.Exists(file))
                {
                    return "Edit target does not exist: " + file + ". This tool never creates files; use script-update-or-create or create-usharp. Nothing was written.";
                }

                byte[] currentBytes = File.ReadAllBytes(file);
                string readGateError = CheckScriptReadGate(file, currentBytes);
                if (readGateError != null)
                {
                    return readGateError;
                }

                string original;
                bool hadBom;
                try
                {
                    original = DecodeVerifiedUtf8(file, currentBytes, out hadBom);
                }
                catch (InvalidOperationException ex)
                {
                    return ex.Message + " Nothing was written.";
                }

                string edited;
                try
                {
                    edited = ApplyBatchEdits(file, original, editsByFile[file], evidence, diffs, landings);
                }
                catch (InvalidOperationException ex)
                {
                    return ex.Message + " Nothing was written.";
                }

                plans.Add(new BatchEditFilePlan { Path = file, Original = original, OriginalBytes = currentBytes, Edited = edited, HadBom = hadBom });
            }

            var originals = new Dictionary<string, byte[]>();
            foreach (BatchEditFilePlan plan in plans) originals.Add(plan.Path, plan.OriginalBytes);
            return WithFileHistory(originals, "udon-batch-edit", history =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode)
                    return "Refused: editor entered Play Mode during history preparation. Nothing was written.\n" + history;
                foreach (BatchEditFilePlan plan in plans)
                    if (!FileHistoryCurrentMatches(plan.Path, plan.OriginalBytes))
                        return "Refused: " + plan.Path + " changed during history preparation. Nothing was written.\n" + history;
                return ApplyBatchEditPlans(plans, edits.Count, evidence, diffs, landings, report, history);
            });
        }

        /// <summary>Applies the validated batch with its original rollback semantics, adding capture evidence to the result.</summary>
        /// <param name="plans">Validated outcomes for all files.</param>
        /// <param name="editCount">Number of requested replacements.</param>
        /// <param name="evidence">Per-edit match evidence.</param>
        /// <param name="diffs">Per-file changes.</param>
        /// <param name="landings">One line per edit saying where it landed, for the summary report.</param>
        /// <param name="report">Report mode, full or summary.</param>
        /// <param name="history">Saved pre-images or explicit best-effort capture warnings.</param>
        /// <returns>The batch result or a withheld response until compilation finishes.</returns>
        private static string ApplyBatchEditPlans(List<BatchEditFilePlan> plans, int editCount,
            List<string> evidence, List<string> diffs, List<string> landings, string report, string history)
        {
            // Mutation phase: suppress the U# file watcher, write everything, import once.
            // Only C# sits behind the UdonSharp watcher, so a shader-only batch must not touch the setting at
            // all: suppressing and restoring a preference the batch never needed is a side effect on the
            // user's configuration for no gain.
            bool touchesCSharp = plans.Exists(plan => plan.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            bool suppressed = touchesCSharp && SuppressAutoCompileForBatch();

            var written = new List<BatchEditFilePlan>();
            Action releaseHistoryWrite = HoldFileHistoryWrite();
            try
            {
                foreach (BatchEditFilePlan plan in plans)
                {
                    try
                    {
                        File.WriteAllText(plan.Path, plan.Edited, new UTF8Encoding(plan.HadBom));
                        written.Add(plan);
                    }
                    catch (Exception ex)
                    {
                        string rollback = RollBackWrittenFiles(written);
                        if (suppressed)
                        {
                            RestoreAutoCompileFromStash("write failure");
                        }

                        releaseHistoryWrite();
                        return "I/O failure writing " + plan.Path + ": " + ex.Message + ". " + rollback + Environment.NewLine + history;
                    }
                }

                var completed = new Dictionary<string, byte[]>();
                foreach (BatchEditFilePlan plan in written)
                    completed.Add(plan.Path, FileHistoryWrittenBytes(plan.Edited, plan.HadBom));
                return WithFileHistory(completed, "udon-batch-edit", after =>
                    ImportBatchEditPlans(written, editCount, evidence, diffs, landings, report,
                        history + Environment.NewLine + after, suppressed),
                    "after-write", releaseHistoryWrite);
            }
            catch
            {
                releaseHistoryWrite();
                if (suppressed) RestoreAutoCompileFromStash("post-write preparation failed");
                throw;
            }
        }

        /// <summary>Imports a completed batch only after its post-write versions have been saved or warned.</summary>
        /// <param name="written">Successfully written source plans.</param>
        /// <param name="editCount">Number of applied replacements.</param>
        /// <param name="evidence">Per-edit match evidence.</param>
        /// <param name="diffs">Per-file changes.</param>
        /// <param name="landings">One line per edit saying where it landed, for the summary report.</param>
        /// <param name="report">Report mode, full or summary.</param>
        /// <param name="history">Before and after capture evidence.</param>
        /// <param name="suppressed">Whether the batch owns an auto-compile suppression to restore.</param>
        /// <returns>The batch result or a withheld compile response.</returns>
        private static string ImportBatchEditPlans(List<BatchEditFilePlan> written, int editCount,
            List<string> evidence, List<string> diffs, List<string> landings, string report, string history,
            bool suppressed)
        {
            foreach (BatchEditFilePlan plan in written)
            {
                RecordScriptReadHash(plan.Path);
                AssetDatabase.ImportAsset(plan.Path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            }

            string suppressionNote = suppressed
                ? " UdonSharp auto-compile was suppressed for the batch and restores after the reload."
                : string.Empty;
            string headline = "Batch-edited " + written.Count + " file(s), " + editCount + " edit(s)." + suppressionNote;

            // A deferred note may span lines: it crosses the reload inside the ledger's JSON recipe, which
            // escapes newlines.
            string summary;
            if (report == BATCH_REPORT_SUMMARY)
            {
                // One line per edit in place of its diff. Nothing else changes: the History lines and the
                // post-compile report are exactly those of the full report.
                summary = headline + Environment.NewLine + string.Join(Environment.NewLine, landings) +
                    Environment.NewLine + history;
            }
            else
            {
                // The headline and its evidence stay one scannable line and the diffs follow on real ones.
                summary = headline + " " + string.Join("; ", evidence) + Environment.NewLine + history;
                if (diffs.Count > 0)
                {
                    summary += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, diffs);
                }
            }

            // One deliberate compile for the whole batch. The response is withheld until the compile and
            // domain reload settle, exactly like reimport of a .cs; the flag restore rides the reload
            // through the InitializeOnLoadMethod hook below, with an idle fallback if no reload arrives.
            if (suppressed)
            {
                ArmAutoCompileRestoreFallback();
            }

            if (TryDeferUntilReload("__deferred-note " + summary))
            {
                CompilationPipeline.RequestScriptCompilation();
                return string.Empty; // Withheld; the deferred completion returns the summary plus the post-compile report.
            }

            CompilationPipeline.RequestScriptCompilation();
            return summary + Environment.NewLine + FormatEditorStatus();
        }

        #endregion

        #region Batch Edit Parsing

        /// <summary>One requested replacement: the file, the exact text to find, and its replacement.</summary>
        private sealed class BatchEditSpec
        {
            /// <summary>One-based position of this edit in the caller's payload, used in every message.</summary>
            public int Index;

            /// <summary>Normalized asset path of the file this edit applies to.</summary>
            public string File;

            /// <summary>Exact text to find. Never empty.</summary>
            public string Old;

            /// <summary>Replacement text. May be empty to delete the matched text.</summary>
            public string New;

            /// <summary>True to replace every occurrence instead of requiring exactly one.</summary>
            public bool ReplaceAll;
        }

        /// <summary>One file's validated outcome, held in memory until every file has validated.</summary>
        private sealed class BatchEditFilePlan
        {
            /// <summary>Asset path of the file.</summary>
            public string Path;

            /// <summary>Content before the batch, kept for rollback on a mid-batch I/O failure.</summary>
            public string Original;

            /// <summary>Original bytes already read for validation, retained for history without another capture read.</summary>
            public byte[] OriginalBytes;

            /// <summary>Content with every edit applied.</summary>
            public string Edited;

            /// <summary>Whether the original file carried a UTF-8 byte order mark to preserve.</summary>
            public bool HadBom;
        }

        /// <summary>
        /// Returns the edits array text: the payload itself when it is an array, or the contents of the .json file it
        /// names, which lets a batch larger than a harness allows in one tool argument still go through in one call. The
        /// file must sit inside the project.
        /// </summary>
        /// <param name="editsText">The edits section: an array, or a project path ending in .json, optionally quoted.</param>
        /// <returns>The JSON array text.</returns>
        private static string ReadBatchEditPayload(string editsText)
        {
            string text = editsText.Trim();
            if (text.StartsWith("[", StringComparison.Ordinal)) return text;

            if (text.Length > 1 && text[0] == '"' && text[text.Length - 1] == '"') text = text.Substring(1, text.Length - 2);
            if (!text.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                string shown = text.Length > 60 ? text.Substring(0, 60) + "..." : text;
                throw new InvalidOperationException("edits must be a JSON array, or the project path of a .json file holding one; got '" + shown + "'. "
                    + "SEND THE ARRAY ITSELF AND FIX THE ESCAPING IF IT WAS REJECTED. NEVER WORK AROUND THIS TOOL WITH A SCRIPT OR A SHELL EDIT.");
            }

            string projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(Path.Combine(projectRoot, text));
            if (!fullPath.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("the edits file '" + text + "' is outside the project.");
            }

            if (!File.Exists(fullPath))
            {
                throw new InvalidOperationException("the edits file '" + text + "' does not exist.");
            }

            return File.ReadAllText(fullPath);
        }

        /// <summary>
        /// Parses and strictly validates the edits payload. Its keys are the ones an agent's own edit tools use
        /// (file_path, old_string, new_string, replace_all), and unknown keys are refused by name so a typo fails
        /// loudly instead of silently sending an edit with an empty search string.
        /// </summary>
        /// <param name="payload">The JSON array text.</param>
        /// <returns>The validated edit list, in payload order.</returns>
        private static List<BatchEditSpec> ParseBatchEditPayload(string payload)
        {
            JArray array = JArray.Parse(payload);
            if (array.Count == 0)
            {
                throw new InvalidOperationException("the edits array is empty.");
            }

            var edits = new List<BatchEditSpec>(array.Count);
            for (int i = 0; i < array.Count; i++)
            {
                int index = i + 1;
                if (!(array[i] is JObject item))
                {
                    throw new InvalidOperationException("edit " + index + " is not an object.");
                }

                foreach (JProperty property in item.Properties())
                {
                    if (property.Name != "file_path" && property.Name != "old_string" && property.Name != "new_string" && property.Name != "replace_all")
                    {
                        throw new InvalidOperationException(
                            "edit " + index + " has unknown key '" + property.Name + "'. Allowed keys: file_path, old_string, new_string, replace_all.");
                    }
                }

                JToken fileToken = item["file_path"];
                string file = fileToken != null && fileToken.Type == JTokenType.String ? fileToken.Value<string>() : null;
                if (string.IsNullOrWhiteSpace(file))
                {
                    throw new InvalidOperationException("edit " + index + " is missing string 'file_path'.");
                }

                file = NormalizeAssetPath(file);
                string pathError;
                if (!TryValidateWritableScriptAssetPath(file, out pathError) || !IsBatchEditableFile(file))
                {
                    string reason = pathError ?? "file must end in " + string.Join(" / ", BatchEditableExtensions);
                    throw new InvalidOperationException("edit " + index + ": " + reason + " Got '" + file + "'.");
                }

                JToken oldToken = item["old_string"];
                JToken newToken = item["new_string"];
                if (oldToken == null || oldToken.Type != JTokenType.String)
                {
                    throw new InvalidOperationException("edit " + index + " is missing string 'old_string'.");
                }

                if (newToken == null || newToken.Type != JTokenType.String)
                {
                    throw new InvalidOperationException("edit " + index + " is missing string 'new_string'. Use an empty string to delete the matched text.");
                }

                JToken replaceAllToken = item["replace_all"];
                if (replaceAllToken != null && replaceAllToken.Type != JTokenType.Boolean)
                {
                    throw new InvalidOperationException("edit " + index + ": replace_all must be a boolean.");
                }

                edits.Add(new BatchEditSpec
                {
                    Index = index,
                    File = file,
                    Old = oldToken.Value<string>(),
                    New = newToken.Value<string>(),
                    ReplaceAll = replaceAllToken != null && replaceAllToken.Value<bool>()
                });
            }

            return edits;
        }

        /// <summary>
        /// Takes the optional report section off the end of the payload. The edits are raw JSON whose strings
        /// may hold a pipe, so the section is read only from the very end and only when the text before it
        /// closes the array or names a .json file: a JSON array always ends in ']', so a payload ending in one is
        /// taken whole and no valid payload can be misread. An empty section reads as an omitted one.
        /// </summary>
        /// <param name="payload">Everything after the leading pipe.</param>
        /// <param name="editsJson">Receives the edits JSON.</param>
        /// <param name="report">Receives the report mode, full unless the section asks for summary.</param>
        /// <returns>Null when the payload splits cleanly, otherwise the refusal.</returns>
        private static string SplitBatchEditReport(string payload, out string editsJson, out string report)
        {
            editsJson = payload;
            report = BATCH_REPORT_FULL;

            string trimmed = payload.TrimEnd();
            int pipe = trimmed.LastIndexOf('|');
            if (pipe < 0 || trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                return null;
            }

            string head = trimmed.Substring(0, pipe).TrimEnd();
            if (!head.EndsWith("]", StringComparison.Ordinal) && !head.TrimEnd('"').EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string section = trimmed.Substring(pipe + 1).Trim();
            if (section.Length > 0 && section != BATCH_REPORT_FULL && section != BATCH_REPORT_SUMMARY)
            {
                return "Unknown report '" + section + "' after the edits: use full or summary. Nothing was written.";
            }

            editsJson = head;
            if (section.Length > 0)
            {
                report = section;
            }

            return null;
        }

        #endregion

        #region Batch Edit Engine

        /// <summary>One resolved replacement range in a file's original content.</summary>
        private struct BatchEditMatchRange
        {
            /// <summary>One-based payload index of the edit that produced this range.</summary>
            public int EditIndex;

            /// <summary>Character offset of the match in the original content.</summary>
            public int Offset;

            /// <summary>Matched length in the original content.</summary>
            public int Length;

            /// <summary>Text spliced in over the matched range.</summary>
            public string Replacement;
        }

        /// <summary>
        /// Validates and applies one file's edits against its original content. Throws with a precise,
        /// caller-actionable message on ANY rule violation: a missing match, an ambiguous match with its
        /// line numbers, a no-op edit, or overlapping edits. Matching is byte-exact with ONE tolerance:
        /// the needle is also tried converted to the file's dominant line-ending flavour, because a JSON
        /// payload's \n can never match a CRLF file and the tool would otherwise refuse every edit here.
        /// </summary>
        /// <param name="displayPath">Path used in error and evidence text.</param>
        /// <param name="original">The file's current content, exactly as decoded.</param>
        /// <param name="edits">The edits targeting this file.</param>
        /// <param name="evidence">Receives one evidence line per applied edit.</param>
        /// <param name="diffs">Receives one unified diff per file that changed.</param>
        /// <param name="landings">Receives one line per applied edit saying where it landed, for the summary report.</param>
        /// <returns>The content with every edit applied.</returns>
        private static string ApplyBatchEdits(string displayPath, string original, List<BatchEditSpec> edits,
            List<string> evidence, List<string> diffs, List<string> landings)
        {
            int crlfCount = CountBatchOccurrences(original, "\r\n");
            int loneLfCount = CountBatchChar(original, '\n') - crlfCount;
            bool mixedEol = crlfCount > 0 && loneLfCount > 0;
            string dominantEol = crlfCount >= loneLfCount && crlfCount > 0 ? "\r\n" : "\n";

            // Every edit resolves to concrete ranges in the ORIGINAL content before anything is spliced,
            // so a later edit can never match text an earlier edit introduced.
            var ranges = new List<BatchEditMatchRange>();
            foreach (BatchEditSpec edit in edits)
            {
                if (string.IsNullOrEmpty(edit.Old))
                {
                    throw new InvalidOperationException("Edit " + edit.Index + ": 'old_string' is empty. Every edit must carry the exact text to find.");
                }

                string convertedOld = ConvertBatchEol(edit.Old, dominantEol);
                string convertedNew = ConvertBatchEol(edit.New ?? string.Empty, dominantEol);
                if (string.Equals(convertedOld, convertedNew, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Edit " + edit.Index + ": 'old_string' and 'new_string' are identical, so the edit would change nothing.");
                }

                List<int> offsets = FindBatchOccurrences(original, edit.Old);
                string matchedNeedle = edit.Old;
                if (offsets.Count == 0 && !string.Equals(convertedOld, edit.Old, StringComparison.Ordinal))
                {
                    offsets = FindBatchOccurrences(original, convertedOld);
                    matchedNeedle = convertedOld;
                }

                if (offsets.Count == 0)
                {
                    string mixedNote = mixedEol
                        ? " Note: this file mixes CRLF and LF line endings, so send 'old_string' with the exact endings of the target region."
                        : string.Empty;
                    throw new InvalidOperationException(
                        "Edit " + edit.Index + ": 'old_string' text not found in " + displayPath + "." + mixedNote);
                }

                if (!edit.ReplaceAll && offsets.Count > 1)
                {
                    throw new InvalidOperationException(
                        "Edit " + edit.Index + ": 'old_string' text appears " + offsets.Count + " times in " + displayPath +
                        " (lines " + DescribeBatchLines(original, offsets) + "). Add surrounding context to make it unique, or set replace_all true.");
                }

                foreach (int offset in offsets)
                {
                    ranges.Add(new BatchEditMatchRange
                    {
                        EditIndex = edit.Index,
                        Offset = offset,
                        Length = matchedNeedle.Length,
                        Replacement = convertedNew
                    });
                }

                evidence.Add(edit.ReplaceAll
                    ? "edit " + edit.Index + ": replaced " + offsets.Count + " occurrence(s) in " + displayPath + " at line(s) " + DescribeBatchLines(original, offsets)
                    : "edit " + edit.Index + ": replaced 1 occurrence in " + displayPath + " at line " + BatchLineOf(original, offsets[0]));
            }

            // Overlap detection runs across the whole file's range set, so two edits can never fight over
            // the same bytes - including the same edit sent twice.
            ranges.Sort((a, b) => a.Offset != b.Offset ? a.Offset.CompareTo(b.Offset) : a.Length.CompareTo(b.Length));
            for (int i = 1; i < ranges.Count; i++)
            {
                BatchEditMatchRange previous = ranges[i - 1];
                BatchEditMatchRange current = ranges[i];
                if (current.Offset < previous.Offset + previous.Length)
                {
                    throw new InvalidOperationException(
                        "Edits " + previous.EditIndex + " and " + current.EditIndex + " overlap in " + displayPath +
                        " at line " + BatchLineOf(original, current.Offset) + ". Overlapping replacements are refused because their combined result is ambiguous.");
                }
            }

            // Each edit's own hunk locates where it landed, and the summary below reads those. Built from the
            // sorted ranges so the new-side line numbers accumulate correctly when several edits land in the
            // same file.
            var hunksByEdit = new Dictionary<int, List<DiffHunk>>();
            int lineDelta = 0;
            foreach (BatchEditMatchRange range in ranges)
            {
                DiffHunk hunk = BuildReplacementHunk(original, range.Offset, range.Length, range.Replacement, lineDelta);
                lineDelta += hunk.LineDelta;
                if (!hunksByEdit.TryGetValue(range.EditIndex, out List<DiffHunk> editHunks))
                {
                    editHunks = new List<DiffHunk>();
                    hunksByEdit[range.EditIndex] = editHunks;
                }

                editHunks.Add(hunk);
            }

            // The summary report's line per edit is read off these same hunks, in payload order, so it can
            // never disagree with the diff about where an edit landed.
            foreach (BatchEditSpec edit in edits)
            {
                if (hunksByEdit.TryGetValue(edit.Index, out List<DiffHunk> landed))
                {
                    landings.Add(DescribeBatchLanding(edit.Index, displayPath, landed));
                }
            }

            // Splice from the end so earlier offsets stay valid; bytes outside the matched ranges are
            // untouched by construction.
            var builder = new StringBuilder(original);
            for (int i = ranges.Count - 1; i >= 0; i--)
            {
                builder.Remove(ranges[i].Offset, ranges[i].Length);
                builder.Insert(ranges[i].Offset, ranges[i].Replacement);
            }

            // The diff is the evidence, not decoration: a count of occurrences is a claim the caller has to go and
            // verify, the hunks are the change itself. It is drawn from the file as it was to the file with every
            // edit applied, line-matched, so edits near each other share a hunk that shows them all landed; a hunk
            // per edit drawn against the original showed its neighbours' lines unchanged, as though they never
            // landed. Unfenced: the client shows fence markers verbatim rather than highlighting what they wrap.
            string edited = builder.ToString();
            string diff = BuildFileDiff(displayPath, original, edited);
            if (diff != null) diffs.Add(diff);

            return edited;
        }

        /// <summary>
        /// Decodes file bytes with a byte-fidelity gate: the decoded text re-encoded must equal the bytes
        /// on disk, so a file this tool touches can never be silently corrupted by lossy decoding.
        /// </summary>
        /// <param name="path">Path used in the error message.</param>
        /// <param name="raw">The file's current bytes.</param>
        /// <param name="hadBom">True when the file starts with a UTF-8 byte order mark.</param>
        /// <returns>The decoded content, without the BOM.</returns>
        private static string DecodeVerifiedUtf8(string path, byte[] raw, out bool hadBom)
        {
            hadBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
            int start = hadBom ? 3 : 0;
            var strictUtf8 = new UTF8Encoding(false, false);
            string decoded = strictUtf8.GetString(raw, start, raw.Length - start);

            byte[] reEncoded = strictUtf8.GetBytes(decoded);
            bool identical = reEncoded.Length == raw.Length - start;
            for (int i = 0; identical && i < reEncoded.Length; i++)
            {
                identical = reEncoded[i] == raw[start + i];
            }

            if (!identical)
            {
                throw new InvalidOperationException(path + " is not valid UTF-8, so it cannot be edited byte-faithfully. Fix the file's encoding first.");
            }

            return decoded;
        }

        /// <summary>Converts text to one line-ending flavour without touching anything else.</summary>
        /// <param name="text">Text whose line endings are converted.</param>
        /// <param name="eol">Either "\n" or "\r\n".</param>
        /// <returns>The converted text.</returns>
        private static string ConvertBatchEol(string text, string eol)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            string normalized = text.Replace("\r\n", "\n");
            return eol == "\n" ? normalized : normalized.Replace("\n", "\r\n");
        }

        /// <summary>Finds every non-overlapping occurrence of a needle, left to right.</summary>
        /// <param name="haystack">Content searched.</param>
        /// <param name="needle">Exact text searched for.</param>
        /// <returns>Ascending match offsets.</returns>
        private static List<int> FindBatchOccurrences(string haystack, string needle)
        {
            var offsets = new List<int>();
            int at = 0;
            while (at <= haystack.Length - needle.Length)
            {
                int hit = haystack.IndexOf(needle, at, StringComparison.Ordinal);
                if (hit < 0)
                {
                    break;
                }

                offsets.Add(hit);
                at = hit + needle.Length;
            }

            return offsets;
        }

        /// <summary>Returns the one-based line number of a character offset.</summary>
        /// <param name="content">Content the offset points into.</param>
        /// <param name="offset">Character offset.</param>
        /// <returns>One-based line number.</returns>
        private static int BatchLineOf(string content, int offset)
        {
            int line = 1;
            for (int i = 0; i < offset && i < content.Length; i++)
            {
                if (content[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        /// <summary>Formats the line numbers of several offsets for an error or evidence line.</summary>
        /// <param name="content">Content the offsets point into.</param>
        /// <param name="offsets">Match offsets.</param>
        /// <returns>Comma-separated one-based line numbers.</returns>
        private static string DescribeBatchLines(string content, List<int> offsets)
        {
            var parts = new List<string>(offsets.Count);
            foreach (int offset in offsets)
            {
                parts.Add(BatchLineOf(content, offset).ToString());
            }

            return string.Join(", ", parts);
        }

        /// <summary>
        /// One edit's line in the summary report: its file, the lines it landed on in the file as written, and
        /// how many whole lines it removed and added. Read off the edit's own diff hunks, one per occurrence,
        /// so the summary can never disagree with the diff the full report prints.
        /// </summary>
        /// <param name="editIndex">One-based payload index of the edit.</param>
        /// <param name="displayPath">Path of the edited file.</param>
        /// <param name="hunks">The edit's hunks in file order, at least one.</param>
        /// <returns>The report line.</returns>
        private static string DescribeBatchLanding(int editIndex, string displayPath, List<DiffHunk> hunks)
        {
            var spans = new List<string>(hunks.Count);
            int removed = 0;
            int added = 0;
            foreach (DiffHunk hunk in hunks)
            {
                int lastLine = hunk.NewFirstLine + hunk.AddedLines - 1;
                spans.Add(lastLine > hunk.NewFirstLine ? hunk.NewFirstLine + "-" + lastLine : hunk.NewFirstLine.ToString());
                removed += hunk.RemovedLines;
                added += hunk.AddedLines;
            }

            string occurrences = hunks.Count > 1 ? ", " + hunks.Count + " occurrences," : string.Empty;
            string lines = (hunks.Count > 1 || hunks[0].AddedLines > 1) ? " landed on lines " : " landed on line ";
            return "edit " + editIndex + ": " + displayPath + occurrences + lines + string.Join(", ", spans) +
                " (" + removed + " removed, " + added + " added)";
        }

        /// <summary>Counts non-overlapping occurrences of a substring.</summary>
        /// <param name="content">Content searched.</param>
        /// <param name="needle">Substring counted.</param>
        /// <returns>Occurrence count.</returns>
        private static int CountBatchOccurrences(string content, string needle)
        {
            return FindBatchOccurrences(content, needle).Count;
        }

        /// <summary>Counts occurrences of one character.</summary>
        /// <param name="content">Content searched.</param>
        /// <param name="value">Character counted.</param>
        /// <returns>Occurrence count.</returns>
        private static int CountBatchChar(string content, char value)
        {
            int count = 0;
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == value)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Restores every already-written file's original content after a mid-batch I/O failure.</summary>
        /// <param name="written">Plans whose files were written before the failure.</param>
        /// <returns>A truthful description of the rollback outcome.</returns>
        private static string RollBackWrittenFiles(List<BatchEditFilePlan> written)
        {
            if (written.Count == 0)
            {
                return "Nothing had been written yet, so no rollback was needed.";
            }

            var failures = new List<string>();
            foreach (BatchEditFilePlan plan in written)
            {
                try
                {
                    File.WriteAllText(plan.Path, plan.Original, new UTF8Encoding(plan.HadBom));
                }
                catch (Exception ex)
                {
                    failures.Add(plan.Path + " (" + ex.Message + ")");
                }
            }

            return failures.Count == 0
                ? "Rolled back " + written.Count + " already-written file(s) to their original content."
                : "ROLLBACK INCOMPLETE - could not restore: " + string.Join(", ", failures) + ". Inspect file-history for each affected path and restore a saved pre-batch revision with file-restore before continuing. If capture warned, use your own backup.";
        }

        #endregion

        #region Read-Before-Edit Gate

        /// <summary>
        /// Records the current on-disk hash of a script so udon-batch-edit can enforce read-before-edit.
        /// Called by script-read on every read and by the write verbs after every write, because a caller
        /// that just wrote a file knows its content as surely as one that just read it.
        /// </summary>
        /// <param name="assetPath">Normalized script path.</param>
        private static void RecordScriptReadHash(string assetPath)
        {
            try
            {
                SessionState.SetString(ScriptReadHashKey(assetPath), HashBytes(File.ReadAllBytes(assetPath)));
            }
            catch
            {
                // The gate is a safety net; failing to record must never break the read or write itself.
            }
        }

        /// <summary>
        /// Enforces read-before-edit: the file must have been read (or written) through the bridge this
        /// editor session, and its bytes must be unchanged since. This is what makes editing blind or
        /// editing over someone else's concurrent change a refusal instead of a silent corruption.
        /// </summary>
        /// <param name="assetPath">Normalized script path.</param>
        /// <param name="currentBytes">The file's current bytes.</param>
        /// <returns>Null when the gate passes, otherwise the refusal message.</returns>
        private static string CheckScriptReadGate(string assetPath, byte[] currentBytes)
        {
            string recorded = SessionState.GetString(ScriptReadHashKey(assetPath), string.Empty);
            if (string.IsNullOrEmpty(recorded))
            {
                return "Refused: " + assetPath + " has not been read through the bridge this session. Run 'script-read " + assetPath +
                    "' first so the edit is grounded in the file's actual current content. Nothing was written.";
            }

            if (!string.Equals(recorded, HashBytes(currentBytes), StringComparison.Ordinal))
            {
                return "Refused: " + assetPath + " has changed since it was last read through the bridge. Run 'script-read " + assetPath +
                    "' again and rebuild the edits against the current content. Nothing was written.";
            }

            return null;
        }

        /// <summary>Builds the SessionState key holding one script's last-read hash.</summary>
        /// <param name="assetPath">Normalized script path.</param>
        /// <returns>The session key.</returns>
        private static string ScriptReadHashKey(string assetPath)
        {
            return "Conduit.ScriptReadHash." + assetPath.ToLowerInvariant();
        }

        /// <summary>Hashes file bytes for the read gate.</summary>
        /// <param name="bytes">Bytes to hash.</param>
        /// <returns>Lowercase hex SHA-256.</returns>
        private static string HashBytes(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(bytes);
                var builder = new StringBuilder(digest.Length * 2);
                foreach (byte value in digest)
                {
                    builder.Append(value.ToString("x2"));
                }

                return builder.ToString();
            }
        }

        #endregion

        /// <summary>True when the path ends in a file kind this verb will edit.</summary>
        /// <param name="path">Asset-relative path to test.</param>
        /// <returns><c>true</c> when the extension is editable here.</returns>
        private static bool IsBatchEditableFile(string path)
        {
            foreach (string extension in BatchEditableExtensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        #region Auto-Compile Suppression

        /// <summary>
        /// Stashes the user's autoCompileOnModify value and suppresses it for the batch. Without this,
        /// UdonSharp's file watcher fires a U# program compile against the STALE assemblies the moment the
        /// first write lands - pure waste that the batch's own explicit compile makes redundant. A stash
        /// left by a crashed earlier run is kept rather than overwritten, so the user's true default can
        /// never be replaced by a mid-suppression false.
        /// </summary>
        /// <returns>True when the flag was reachable and is now suppressed.</returns>
        private static bool SuppressAutoCompileForBatch()
        {
            bool? current = ReadAutoCompileSetting();
            if (current == null)
            {
                return false; // No UdonSharp, no watcher, nothing to suppress.
            }

            if (!EditorPrefs.HasKey(AutoCompileStashKey))
            {
                EditorPrefs.SetBool(AutoCompileStashKey, current.Value);
            }

            return WriteAutoCompileSetting(false);
        }

        /// <summary>
        /// Restores the stashed auto-compile value and clears the stash. Runs from the post-reload hook,
        /// the no-reload idle fallback, and the mid-batch failure path; a missing stash makes it a no-op,
        /// so every path may call it safely.
        /// </summary>
        /// <param name="reason">Short cause recorded for diagnostics when the write fails.</param>
        private static void RestoreAutoCompileFromStash(string reason)
        {
            if (!EditorPrefs.HasKey(AutoCompileStashKey))
            {
                return;
            }

            bool stashed = EditorPrefs.GetBool(AutoCompileStashKey, true);
            if (WriteAutoCompileSetting(stashed))
            {
                EditorPrefs.DeleteKey(AutoCompileStashKey);
                return;
            }

            // Keep the stash so the next domain load retries; a failed restore must never delete the
            // only record of the user's real setting.
            Debug.LogWarning("[Conduit] Could not restore UdonSharp autoCompileOnModify (" + reason + "); will retry on the next domain load.");
        }

        /// <summary>
        /// Post-reload restore: puts the user's auto-compile setting back after the batch's compile has
        /// landed, and heals a crashed session on the next editor start. The file watcher is rebuilt from
        /// scratch after every domain reload reading the restored flag, so no stale suppression can linger.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void RestoreAutoCompileStashOnLoad()
        {
            // Import workers load this assembly too; see ConduitBridge's static constructor. A worker must
            // not write the user's auto-compile setting back on the main editor's behalf.
            if (UnityEngine.Application.isBatchMode) return;

            RestoreAutoCompileFromStash("post-reload restore");
        }

        /// <summary>
        /// Arms the no-reload fallback. If the batch's compile somehow produces no domain reload, the
        /// EditorApplication.update hook below restores the flag once the editor has sat idle; a reload
        /// destroys the hook and the InitializeOnLoadMethod restore takes over instead.
        /// </summary>
        private static void ArmAutoCompileRestoreFallback()
        {
            _restoreIdleTicks = 0;
            EditorApplication.update -= AutoCompileRestoreFallbackTick;
            EditorApplication.update += AutoCompileRestoreFallbackTick;
        }

        /// <summary>
        /// Idle watchdog for the no-reload path. Waits for the editor to be neither compiling nor
        /// importing for a run of consecutive ticks, then restores the flag. The tick count also lets the
        /// watcher's own update drain and discard any queued write events while the flag is still false,
        /// so restoring can never hand it a backlog to compile.
        /// </summary>
        private static void AutoCompileRestoreFallbackTick()
        {
            if (!EditorPrefs.HasKey(AutoCompileStashKey))
            {
                EditorApplication.update -= AutoCompileRestoreFallbackTick;
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                _restoreIdleTicks = 0;
                return;
            }

            _restoreIdleTicks++;
            if (_restoreIdleTicks < RESTORE_IDLE_TICKS)
            {
                return;
            }

            EditorApplication.update -= AutoCompileRestoreFallbackTick;
            RestoreAutoCompileFromStash("idle fallback");
        }

        /// <summary>Reads the live autoCompileOnModify value through reflection.</summary>
        /// <returns>The current value, or null when UdonSharp is absent or the field is unreachable.</returns>
        private static bool? ReadAutoCompileSetting()
        {
            FieldInfo field = FindAutoCompileField(out object settings);
            if (field == null)
            {
                return null;
            }

            return (bool)field.GetValue(settings);
        }

        /// <summary>
        /// Writes the live autoCompileOnModify value through reflection, deliberately without saving the
        /// settings asset: the watcher reads the live object every tick, and an unsaved value means a hard
        /// crash leaves the on-disk setting already correct.
        /// </summary>
        /// <param name="value">Value to set.</param>
        /// <returns>True when the write landed and reads back as requested.</returns>
        private static bool WriteAutoCompileSetting(bool value)
        {
            FieldInfo field = FindAutoCompileField(out object settings);
            if (field == null)
            {
                return false;
            }

            field.SetValue(settings, value);
            return (bool)field.GetValue(settings) == value;
        }

        /// <summary>Resolves the UdonSharp settings instance and its auto-compile field via reflection.</summary>
        /// <param name="settings">Receives the live settings object, or null.</param>
        /// <returns>The field, or null when UdonSharp is absent.</returns>
        private static FieldInfo FindAutoCompileField(out object settings)
        {
            settings = null;
            Type settingsType = FindLoadedType(UDONSHARP_SETTINGS_TYPE);
            MethodInfo getSettings = settingsType?.GetMethod("GetSettings", BindingFlags.Public | BindingFlags.Static);
            settings = getSettings?.Invoke(null, null);
            FieldInfo field = settingsType?.GetField(AUTO_COMPILE_FIELD, BindingFlags.Public | BindingFlags.Instance);
            return settings == null ? null : field;
        }

        #endregion
    }
}
