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
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace ItsRenji.Conduit
{
    /// <summary>
    /// Unity Console reading, filtering, formatting, and the post-compile report.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Reading entries

        /// <returns>Console entries with rendered text, type, file, and line data.</returns>
        private static List<ConsoleEntry> ReadConsoleEntries()
        {
            var restoreFilters = ForceConsoleFiltersOn();
            try
            {
                return ReadConsoleEntriesRaw();
            }
            finally
            {
                if (restoreFilters != null) restoreFilters();
            }
        }

        /// <summary>Reads Unity Console entries through UnityEditor.LogEntries reflection, using the console's current view.</summary>
        /// <returns>Console entries with rendered text, type, file, and line data.</returns>
        private static List<ConsoleEntry> ReadConsoleEntriesRaw()
        {
            var result = new List<ConsoleEntry>();
            var unityEditorAssembly = typeof(EditorWindow).Assembly;
            var logEntriesType = unityEditorAssembly.GetType("UnityEditor.LogEntries", true);
            var logEntryType = unityEditorAssembly.GetType("UnityEditor.LogEntry", true);

            var startGettingEntries = GetStaticMethod(logEntriesType, "StartGettingEntries");
            var endGettingEntries = GetStaticMethod(logEntriesType, "EndGettingEntries");
            var getCount = GetStaticMethod(logEntriesType, "GetCount");
            var getEntryInternal = GetStaticMethod(logEntriesType, "GetEntryInternal");
            var getLinesAndMode = logEntriesType.GetMethod(
                "GetLinesAndModeFromEntryInternal",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

            var messageField = GetField(logEntryType, "message", false) ?? GetField(logEntryType, "condition");
            var modeField = GetField(logEntryType, "mode");
            var fileField = GetField(logEntryType, "file");
            var lineField = GetField(logEntryType, "line");

            var logEntry = Activator.CreateInstance(logEntryType);

            startGettingEntries.Invoke(null, null);
            try
            {
                var count = Convert.ToInt32(getCount.Invoke(null, null));
                for (var i = 0; i < count; i++)
                {
                    var gotEntry = getEntryInternal.Invoke(null, new[] { (object)i, logEntry });
                    if (gotEntry is bool && !(bool)gotEntry)
                    {
                        continue;
                    }

                    var line = lineField == null ? 0 : Convert.ToInt32(lineField.GetValue(logEntry));
                    var message = Convert.ToString(messageField.GetValue(logEntry)) ?? string.Empty;
                    var mode = Convert.ToInt32(modeField.GetValue(logEntry));
                    var rendered = GetRenderedText(getLinesAndMode, i, message);
                    if (rendered.Mode != 0)
                    {
                        mode = rendered.Mode;
                    }

                    result.Add(new ConsoleEntry
                    {
                        Type = TypeFromMode(mode),
                        Condition = rendered.Text,
                        StackTrace = string.Empty,
                        File = fileField == null ? string.Empty : Convert.ToString(fileField.GetValue(logEntry)) ?? string.Empty,
                        Line = line
                    });
                }
            }
            finally
            {
                endGettingEntries.Invoke(null, null);
            }

            return result;
        }

        /// <summary>
        /// Forces the console's three log-level flags on and blanks its search filter, so a bridge read sees every
        /// entry. Everything is reached by reflection with null-guards: on a Unity version where any piece is
        /// missing, nothing is touched.
        /// </summary>
        /// <returns>An action restoring the user's previous filter state, or null when nothing was changed.</returns>
        private static Action ForceConsoleFiltersOn()
        {
            try
            {
                var unityEditorAssembly = typeof(EditorWindow).Assembly;
                var logEntriesType = unityEditorAssembly.GetType("UnityEditor.LogEntries", false);
                var consoleWindowType = unityEditorAssembly.GetType("UnityEditor.ConsoleWindow", false);
                if (logEntriesType == null || consoleWindowType == null) return null;

                var flagsType = consoleWindowType.GetNestedType("ConsoleFlags", BindingFlags.Public | BindingFlags.NonPublic);
                var consoleFlagsProperty = logEntriesType.GetProperty("consoleFlags", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var setConsoleFlag = logEntriesType.GetMethod("SetConsoleFlag", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var getFilterText = logEntriesType.GetMethod("GetFilteringText", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var setFilterText = logEntriesType.GetMethod("SetFilteringText", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (flagsType == null || consoleFlagsProperty == null || setConsoleFlag == null) return null;

                var previousFlags = Convert.ToInt32(consoleFlagsProperty.GetValue(null, null));
                var forcedBits = new List<int>();
                foreach (var flagName in new[] { "LogLevelLog", "LogLevelWarning", "LogLevelError" })
                {
                    var bit = Convert.ToInt32(Enum.Parse(flagsType, flagName));
                    if ((previousFlags & bit) != 0) continue;

                    setConsoleFlag.Invoke(null, new object[] { bit, true });
                    forcedBits.Add(bit);
                }

                string previousFilter = null;
                if (getFilterText != null && setFilterText != null)
                {
                    previousFilter = Convert.ToString(getFilterText.Invoke(null, null));
                    if (!string.IsNullOrEmpty(previousFilter)) setFilterText.Invoke(null, new object[] { string.Empty });
                    else previousFilter = null;
                }

                if (forcedBits.Count == 0 && previousFilter == null) return null;

                return () =>
                {
                    foreach (var bit in forcedBits) setConsoleFlag.Invoke(null, new object[] { bit, false });
                    if (previousFilter != null) setFilterText.Invoke(null, new object[] { previousFilter });
                };
            }
            catch
            {
                // A reflection surprise on some Unity version must degrade to the old behaviour, never break reads.
                return null;
            }
        }

        /// <summary>Reads Unity's rendered console text and mode for one row when that internal API exists.</summary>
        /// <param name="getLinesAndMode">Reflected GetLinesAndModeFromEntryInternal method, or null.</param>
        /// <param name="row">Console row index.</param>
        /// <param name="fallback">Fallback text when rendered text is unavailable.</param>
        /// <returns>Rendered console text plus mode, or fallback text with mode zero.</returns>
        private static RenderedConsoleEntry GetRenderedText(MethodInfo getLinesAndMode, int row, string fallback)
        {
            if (getLinesAndMode == null)
            {
                return new RenderedConsoleEntry { Text = fallback, Mode = 0 };
            }

            try
            {
                var args = new object[] { row, 1000, 0, string.Empty };
                getLinesAndMode.Invoke(null, args);
                var text = Convert.ToString(args[3]);
                return new RenderedConsoleEntry
                {
                    Text = string.IsNullOrEmpty(text) ? fallback : text,
                    Mode = Convert.ToInt32(args[2])
                };
            }
            catch
            {
                return new RenderedConsoleEntry { Text = fallback, Mode = 0 };
            }
        }

        /// <summary>Maps Unity internal console mode bits to a public LogType name.</summary>
        /// <param name="mode">Unity console mode bitfield.</param>
        /// <returns>LogType name inferred from the mode bits.</returns>
        private static string TypeFromMode(int mode)
        {
            const int errorBits = 1 | 16 | 64 | 256 | 2048 | 8192 | 131072 | 4194304;
            const int warningBits = 128 | 512 | 4096;
            const int assertBits = 2 | 2097152;
            const int logBits = 4 | 1024;

            if ((mode & errorBits) != 0)
            {
                return LogType.Error.ToString();
            }

            if ((mode & assertBits) != 0)
            {
                return LogType.Assert.ToString();
            }

            if ((mode & warningBits) != 0)
            {
                return LogType.Warning.ToString();
            }

            if ((mode & logBits) != 0)
            {
                return LogType.Log.ToString();
            }

            return LogType.Log.ToString();
        }

        /// <summary>Rendered console row text and mode returned by Unity's internal console API.</summary>
        private sealed class RenderedConsoleEntry
        {
            /// <summary>Rendered console text.</summary>
            public string Text;

            /// <summary>Unity internal console mode bitfield.</summary>
            public int Mode;
        }

        /// <summary>Console entry data copied from Unity's internal console row representation.</summary>
        private sealed class ConsoleEntry
        {
            /// <summary>Log type name for the entry.</summary>
            public string Type;

            /// <summary>Rendered message text.</summary>
            public string Condition;

            /// <summary>Stack trace text when available.</summary>
            public string StackTrace;

            /// <summary>Source file path when available.</summary>
            public string File;

            /// <summary>Source line number when available.</summary>
            public int Line;
        }

        /// <summary>Highest console entry count any read verb returns in one response.</summary>
        private const int MAX_CONSOLE_ENTRIES = 500;

        #endregion

        #region Formatting

        /// <summary>Formats one console entry as type, message, optional source file, and optional stack trace.</summary>
        /// <param name="type">Console entry type label.</param>
        /// <param name="condition">Console message text.</param>
        /// <param name="stackTrace">Stack trace text.</param>
        /// <param name="file">Source file path.</param>
        /// <param name="line">Source line number.</param>
        /// <returns>Formatted console entry text.</returns>
        private static string FormatEntry(string type, string condition, string stackTrace, string file, int line)
        {
            var builder = new StringBuilder();
            builder.Append('[').Append(type).Append("] ").AppendLine(condition ?? string.Empty);

            if (!string.IsNullOrWhiteSpace(file))
            {
                builder.AppendLine(line > 0 ? file + ":" + line : file);
            }

            if (!string.IsNullOrWhiteSpace(stackTrace))
            {
                builder.AppendLine(stackTrace.TrimEnd());
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>Formats console entries in full, each tagged with its absolute entry number.</summary>
        /// <remarks>
        /// The number is what makes selective reading possible: `summary` is cheap and shows every entry's
        /// number, then `entry &lt;n&gt;` pulls just the ones worth the stack trace. Numbering is ABSOLUTE
        /// within the current console buffer - oldest is 1 - so a number means the same thing to every read
        /// verb, and stays valid until the console is cleared.
        /// </remarks>
        /// <param name="entries">Console entries to format.</param>
        /// <param name="firstNumber">Absolute entry number of the first item in this slice.</param>
        /// <returns>Formatted console log text.</returns>
        private static string FormatEntries(List<ConsoleEntry> entries, int firstNumber)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Entry count: " + entries.Count);
            builder.AppendLine();

            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                builder.Append('#').Append(firstNumber + index).Append(' ');
                builder.AppendLine(FormatEntry(entry.Type, entry.Condition, entry.StackTrace, entry.File, entry.Line));
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Lists console entries whose MESSAGE contains a pattern, as numbers plus one line each.
        /// </summary>
        /// <remarks>
        /// The cheap half of reading a busy console: find the entries that matter by content, then spend
        /// tokens on them with `entry &lt;n&gt;`. Matching ignores case and ignores stack frames, because a
        /// search for a type name would otherwise match every entry whose CALL PATH mentions it rather than
        /// the ones actually about it.
        /// </remarks>
        /// <param name="entries">The full console buffer.</param>
        /// <param name="pattern">Text to look for, matched case-insensitively.</param>
        /// <returns>Numbered one-line matches, or a usage or no-match line.</returns>
        private static string FormatMatchingEntries(List<ConsoleEntry> entries, string pattern)
        {
            pattern = (pattern ?? string.Empty).Trim().Trim('"');
            if (pattern.Length == 0)
            {
                return "Usage: grep <text>  - lists matching console entries by number; read one with 'entry <n>'.";
            }

            var builder = new StringBuilder();
            var matches = 0;

            for (var index = 0; index < entries.Count; index++)
            {
                var lines = MessageLinesOnly(entries[index].Condition);
                var haystack = string.Join(" ", lines);
                if (haystack.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                matches++;
                builder.Append('#').Append(index + 1).Append(" [").Append(entries[index].Type).Append("] ");
                builder.AppendLine(lines.Length == 0 ? string.Empty : lines[0]);
            }

            if (matches == 0)
            {
                return "No console entry matches '" + pattern + "' (" + entries.Count + " entries searched).";
            }

            return matches + " of " + entries.Count + " entries match '" + pattern +
                "'; read one in full with 'entry <n>'.\n\n" + builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Formats the specific numbered entries a caller asked for, in full, skipping everything else.
        /// </summary>
        /// <remarks>
        /// The point of the whole numbering scheme: triage with `summary`, which is cheap and carries no
        /// stack traces, then spend the tokens only on the entries that earned it. Out-of-range numbers are
        /// reported individually rather than failing the call, so one bad number in a list still returns the
        /// good ones.
        /// </remarks>
        /// <param name="entries">The full console buffer.</param>
        /// <param name="arguments">Requested entry numbers, space or comma separated.</param>
        /// <returns>The requested entries in full, or a usage or parse error.</returns>
        private static string FormatSelectedEntries(List<ConsoleEntry> entries, string[] arguments)
        {
            var wanted = new List<int>();
            for (var i = 1; i < arguments.Length; i++)
            {
                foreach (var token in arguments[i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int number;
                    if (!int.TryParse(token.Trim(), out number))
                    {
                        return "Entry numbers must be whole numbers, got '" + token.Trim() + "'. Nothing was read.";
                    }

                    wanted.Add(number);
                }
            }

            if (wanted.Count == 0)
            {
                return "Usage: entry <entries> [entries ...]  - numbers come from summary, head or tail; #1 is the OLDEST entry.";
            }

            var builder = new StringBuilder();
            builder.AppendLine("Console holds " + entries.Count + " entries; showing " + wanted.Count + ".");
            builder.AppendLine();

            foreach (var number in wanted)
            {
                if (number < 1 || number > entries.Count)
                {
                    builder.AppendLine("#" + number + " is out of range (1.." + entries.Count + ").");
                    builder.AppendLine();
                    continue;
                }

                var entry = entries[number - 1];
                builder.Append('#').Append(number).Append(' ');
                builder.AppendLine(FormatEntry(entry.Type, entry.Condition, entry.StackTrace, entry.File, entry.Line));
                builder.AppendLine();
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>Formats aggregate console counts and the first line of the latest entries.</summary>
        /// <param name="entries">Console entries to summarize.</param>
        /// <param name="latestCount">Maximum number of latest entries to include.</param>
        /// <returns>Formatted console summary text.</returns>
        private static string FormatSummary(List<ConsoleEntry> entries, int latestCount)
        {
            var logCount = 0;
            var warningCount = 0;
            var errorCount = 0;
            var assertCount = 0;

            foreach (var entry in entries)
            {
                if (entry.Type == LogType.Warning.ToString())
                {
                    warningCount++;
                }
                else if (entry.Type == LogType.Error.ToString() || entry.Type == LogType.Exception.ToString())
                {
                    errorCount++;
                }
                else if (entry.Type == LogType.Assert.ToString())
                {
                    assertCount++;
                }
                else
                {
                    logCount++;
                }
            }

            var builder = new StringBuilder();
            builder.AppendLine("Total entries: " + entries.Count);
            builder.AppendLine("Logs: " + logCount);
            builder.AppendLine("Warnings: " + warningCount);
            builder.AppendLine("Errors: " + errorCount);
            builder.AppendLine("Asserts: " + assertCount);
            builder.AppendLine();
            builder.AppendLine("Latest " + Math.Min(latestCount, entries.Count) + " (#n is the entry number - read one in full with 'entry <n>'):");

            var latest = TakeLast(entries, latestCount);
            var firstNumber = entries.Count - latest.Count + 1;

            for (var index = 0; index < latest.Count; index++)
            {
                var entry = latest[index];

                // The WHOLE message, not just its first line. A multi-line Debug.Log carries its findings on
                // the lines AFTER the first, so truncating there hid exactly the output a diagnostic was
                // written to produce - and the only other reader, `tail`, adds a full stack trace per entry,
                // which is the wrong trade for reading a log. Stack traces still belong to `tail` alone.
                var lines = MessageLinesOnly(entry.Condition);
                builder.Append('#').Append(firstNumber + index).Append(" [").Append(entry.Type).Append("] ");
                builder.AppendLine(lines.Length == 0 ? string.Empty : lines[0]);

                var shown = Math.Min(lines.Length, SUMMARY_MESSAGE_LINE_LIMIT);
                for (var i = 1; i < shown; i++)
                {
                    builder.Append("    ").AppendLine(lines[i].Trim());
                }

                if (lines.Length > shown)
                {
                    builder.Append("    +").Append(lines.Length - shown).AppendLine(" more line(s)");
                }
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>Returns the FIRST entries from a console entry list, the oldest end of the buffer.</summary>
        /// <param name="entries">Console entries to slice.</param>
        /// <param name="count">Maximum number of entries to return.</param>
        /// <returns>The original list or a leading range copy.</returns>
        private static List<ConsoleEntry> TakeFirst(List<ConsoleEntry> entries, int count)
        {
            if (count >= entries.Count)
            {
                return entries;
            }

            return entries.GetRange(0, count);
        }

        /// <summary>Returns the last entries from a console entry list without copying when all are requested.</summary>
        /// <param name="entries">Console entries to slice.</param>
        /// <param name="count">Maximum number of entries to return.</param>
        /// <returns>The original list or a trailing range copy.</returns>
        private static List<ConsoleEntry> TakeLast(List<ConsoleEntry> entries, int count)
        {
            if (count >= entries.Count)
            {
                return entries;
            }

            return entries.GetRange(entries.Count - count, count);
        }

        /// <summary>
        /// Returns only the MESSAGE lines of a rendered console entry, stopping at the stack trace.
        /// </summary>
        /// <remarks>
        /// Console reads store the whole rendered text in one field and leave the stack field empty, so the
        /// split has to be recovered by shape here. Deliberately conservative: a line is only treated as a
        /// stack frame on strong evidence, so the worst case is a summary carrying one extra frame. Erring
        /// the other way would swallow real output, which is the exact failure this exists to prevent.
        /// </remarks>
        /// <param name="rendered">The rendered entry text.</param>
        /// <returns>The leading message lines, stack frames removed.</returns>
        private static string[] MessageLinesOnly(string rendered)
        {
            var lines = (rendered ?? string.Empty).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var kept = new List<string>();
            foreach (var line in lines)
            {
                // Never drop the first line: an entry is always allowed to have a message.
                if (kept.Count > 0 && LooksLikeStackFrame(line))
                {
                    break;
                }

                kept.Add(line);
            }

            return kept.ToArray();
        }

        /// <summary>Recognises a stack frame by shape, so a message line is never mistaken for one.</summary>
        /// <param name="line">One rendered line.</param>
        /// <returns><c>true</c> only on strong evidence that the line is a stack frame.</returns>
        private static bool LooksLikeStackFrame(string line)
        {
            var trimmed = (line ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return false;
            }

            // The file-and-line suffix Unity appends to a frame is unambiguous on its own.
            if (trimmed.IndexOf(" (at ", StringComparison.Ordinal) >= 0)
            {
                return true;
            }

            // Otherwise require the full `Namespace.Type:Method (` shape: a DOTTED, space-free head before
            // the colon and a parameter list after it. Requiring the dot is what stops an ordinary message
            // such as "elapsed: 5 (fast)" from being read as a frame.
            var colon = trimmed.IndexOf(':');
            if (colon <= 0)
            {
                return false;
            }

            var head = trimmed.Substring(0, colon);
            return head.IndexOf('.') > 0 && head.IndexOf(' ') < 0 && trimmed.IndexOf('(', colon) > colon;
        }

        /// <summary>
        /// Most lines of ONE message `summary` prints before abbreviating. Enough for a multi-line
        /// diagnostic to be readable, low enough that one enormous log cannot swamp the whole summary.
        /// </summary>
        private const int SUMMARY_MESSAGE_LINE_LIMIT = 12;

        #endregion

        #region Compile reporting

        /// <summary>
        /// Builds the post-compile report returned by a deferred reimport once the domain reload settles: the
        /// Console failures AND warnings belonging to THIS compile, the first line of each, plus editor status.
        /// Lets one reimport call report success or failure inline instead of the caller polling status and
        /// reading the console. A FAILURE older than the newest "compile ... finished" line is counted
        /// separately as stale rather than reported as this compile's, because a batch of edits makes Unity
        /// compile each half-applied intermediate state and those transient errors linger in the buffer
        /// afterwards.
        /// </summary>
        /// <returns>A human-readable compile report.</returns>
        private static string BuildPostCompileReport()
        {
            var entries = ReadConsoleEntries();

            // Entries arrive in console insertion order, so the NEWEST "compile ... finished" line is the
            // boundary: anything before it belongs to an earlier compile and is history. Keying on order
            // rather than a parsed timestamp keeps this independent of log formatting, and ConsoleEntry
            // carries no timestamp field to parse in the first place.
            var boundary = -1;
            for (var i = 0; i < entries.Count; i++)
            {
                var condition = entries[i].Condition ?? string.Empty;
                if (condition.IndexOf("finished", StringComparison.OrdinalIgnoreCase) >= 0
                    && condition.IndexOf("compile", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    boundary = i;
                }
            }

            // The other floor is the entry count captured before this command triggered its compile, and it
            // is the one that holds when NO compile-finished line was logged - a delete or move whose rebuild
            // emits none still has to tell its own errors from the ones already in the buffer. Without it
            // boundary stayed -1 and EVERY error in the console was reported as "from this compile", which
            // sends the caller hunting a file it never broke. Consume the key so a later report cannot reuse
            // a stale baseline.
            var baseline = SessionState.GetInt(ConsoleBaselineSessionKey, -1);
            SessionState.EraseInt(ConsoleBaselineSessionKey);
            var floor = Math.Max(boundary, baseline);

            var errors = new List<string>();
            var warnings = new List<string>();
            var staleErrors = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                // Unity gives a failure three different LogTypes and matching Error alone dropped two of
                // them, so a Debug.LogException or a failed assertion reported as "no errors". Warning is
                // collected separately: it does not fail the compile, but an obsolete call or an unreachable
                // branch is a real defect, and a report saying "no errors" while silently dropping a dozen
                // of them is misleading by omission rather than concise.
                var type = entries[i].Type;
                var failure = type == LogType.Error.ToString()
                    || type == LogType.Assert.ToString()
                    || type == LogType.Exception.ToString();
                var warning = type == LogType.Warning.ToString();
                if (!failure && !warning)
                {
                    continue;
                }

                if (i < floor)
                {
                    // Stale WARNINGS are dropped without a count. A stale failure is worth flagging because
                    // the caller may be about to act on it; a warning that predates the command is noise the
                    // console already holds.
                    if (failure) staleErrors++;
                    continue;
                }

                var message = entries[i].Condition ?? string.Empty;
                var newline = message.IndexOf('\n');
                var first = newline >= 0 ? message.Substring(0, newline) : message;
                if (failure) errors.Add(first);
                else warnings.Add(first);
            }

            var staleNote = staleErrors == 0
                ? string.Empty
                : " " + staleErrors + " older error(s) predate this compile and are stale; clear the console to drop them.";

            // With neither floor available nothing can honestly be attributed, so the report says exactly that
            // rather than claiming the buffer as its own work.
            var attributable = floor >= 0;
            var builder = new StringBuilder();
            if (errors.Count == 0)
            {
                builder.AppendLine(attributable
                    ? "Reimport + compile finished - no errors from this compile." + staleNote
                    : "Reimport + compile finished - no errors in the console." + staleNote);
            }
            else
            {
                builder.AppendLine(attributable
                    ? "Reimport + compile finished with " + errors.Count + " Console error(s) from this compile:" + staleNote
                    : "Reimport + compile finished. " + errors.Count + " Console error(s) are present but CANNOT be attributed to this compile - no compile-finished line was logged and no pre-compile baseline was recorded, so they may predate this command:");
                var shown = Math.Min(errors.Count, 15);
                for (var i = 0; i < shown; i++)
                {
                    builder.AppendLine("- " + errors[i]);
                }

                if (errors.Count > shown)
                {
                    builder.AppendLine("... and " + (errors.Count - shown) + " more (use console-get-logs).");
                }
            }

            // Listed after the failures and under their own heading, so a clean compile carrying warnings
            // cannot be skimmed as clean.
            if (warnings.Count > 0)
            {
                builder.AppendLine(warnings.Count + " warning(s) from this compile:");
                var shownWarnings = Math.Min(warnings.Count, 15);
                for (var i = 0; i < shownWarnings; i++)
                {
                    builder.AppendLine("- " + warnings[i]);
                }

                if (warnings.Count > shownWarnings)
                {
                    builder.AppendLine(
                        "... and " + (warnings.Count - shownWarnings) + " more (use console-get-logs).");
                }
            }

            builder.Append(FormatEditorStatus());
            return builder.ToString();
        }

        /// <summary>
        /// Reads Unity Console entries through UnityEditor.LogEntries reflection. The read runs with the console's
        /// level filters forced on and its search filter blanked, both restored afterwards: LogEntries.GetCount and
        /// GetEntryInternal answer for the console WINDOW's current view, so hidden log levels or a leftover search
        /// term made every read return zero entries while logs demonstrably existed (observed 2026-08-01, entries
        /// present in Editor.log while the bridge reported none). Forcing the view makes the read deterministic
        /// regardless of window state; if any of the filter APIs is missing on this Unity version, the read simply
        /// proceeds with the user's current view as before.
        /// </summary>
        /// <summary>
        /// Returns the Console errors Unity logged after a captured entry index. A Unity API that reports
        /// only a false return value writes its real reason to the Console, so a verb that wraps one would
        /// otherwise hand the caller a refusal with nothing to act on.
        /// </summary>
        /// <param name="firstNewEntryIndex">Console entry count captured before the operation ran.</param>
        /// <returns>A summary that begins with a space, or an empty string when Unity logged no error.</returns>
        private static string DescribeConsoleErrorsSince(int firstNewEntryIndex)
        {
            var entries = ReadConsoleEntries();
            var report = new StringBuilder();
            for (var i = Math.Max(0, firstNewEntryIndex); i < entries.Count; i++)
            {
                if (entries[i].Type != LogType.Error.ToString())
                {
                    continue;
                }

                var message = entries[i].Condition ?? string.Empty;
                var newline = message.IndexOf('\n');
                report.Append(report.Length == 0 ? " Unity reported: " : " | ");
                report.Append(newline >= 0 ? message.Substring(0, newline) : message);
            }

            return report.ToString();
        }
        /// <summary>Clears Unity Console entries through UnityEditor.LogEntries reflection.</summary>
        private static void ClearConsole()
        {
            var unityEditorAssembly = typeof(EditorWindow).Assembly;
            var logEntriesType = unityEditorAssembly.GetType("UnityEditor.LogEntries", true);
            GetStaticMethod(logEntriesType, "Clear").Invoke(null, null);
        }

        #endregion

        #region Dispatch
            /// <summary>
        /// Routes the Console verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchConsole(string verb, string command, string[] parts)
        {
            if (verb == "console-get-logs")
            {
                return ConsoleGetLogsCommand(parts);
            }

            if (verb == "console-clear-logs")
            {
                ClearConsole();
                return "Unity Console cleared.";
            }

            if (verb == "tail")
            {
                var count = parts.Length > 1 ? ParsePositiveInt(parts[1], 40, MAX_CONSOLE_ENTRIES) : 40;
                var entries = ReadConsoleEntries();
                var slice = TakeLast(entries, count);
                return FormatEntries(slice, entries.Count - slice.Count + 1);
            }

            if (verb == "head")
            {
                var count = parts.Length > 1 ? ParsePositiveInt(parts[1], 40, MAX_CONSOLE_ENTRIES) : 40;
                return FormatEntries(TakeFirst(ReadConsoleEntries(), count), 1);
            }

            if (verb == "entry")
            {
                return FormatSelectedEntries(ReadConsoleEntries(), parts);
            }

            if (verb == "grep")
            {
                return FormatMatchingEntries(ReadConsoleEntries(), GetCommandRemainder(command, parts[0]));
            }

            if (verb == "summary")
            {
                var count = parts.Length > 1 ? ParsePositiveInt(parts[1], 20, MAX_CONSOLE_ENTRIES) : 20;
                return FormatSummary(ReadConsoleEntries(), count);
            }

            if (verb == "clear")
            {
                ClearConsole();
                return "Unity Console cleared.";
            }


            return null;
        }

        #endregion
    }
}
