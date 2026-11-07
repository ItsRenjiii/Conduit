/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Diff rendering for udon-batch-edit's evidence and for file-history's diff view. A batch shows the whole-file diff
// of each file it edited, and keeps a hunk per replacement only to report where each edit landed.
//
// A command that changed a file should show what it changed, not assert a count. "replaced 1 occurrence at
// line 128" is a claim the caller has to go and verify; a diff is the evidence itself, and the bridge
// already holds both sides of every replacement so producing one costs nothing extra.
//
// The desktop client's own diff widget cannot be reached from here: it is selected by the literal tool name
// (Edit, Write, MultiEdit, NotebookEdit) and every other path returns null, so an MCP tool renders as plain
// text whatever it sends. Plain text is therefore the target, so the diff goes out unfenced as numbered
// lines marked -, + or a space, which read correctly in any client and need nothing to render them.
//
// Line numbers on the old side are exact. On the new side they accumulate the net line change of every
// earlier hunk in the same file, so a later hunk's numbers stay true after an earlier one has grown or
// shrunk the file.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ItsRenji.Conduit
{
    internal static partial class ConduitBridge
    {
        #region Diff constants

        /// <summary>Unchanged lines shown either side of a change, as ordinary diff convention.</summary>
        private const int DIFF_CONTEXT_LINES = 3;

        // There is deliberately no cap on rendered diff lines. Both paths used to stop and print "N more
        // changed line(s) not shown", on the reasoning that a large diff buries the report around it. That
        // is the wrong trade for this tool: a diff is the evidence a write actually did what it said, and a
        // truncated one asks the reader to take the rest on trust, which is the opposite of the point.

        /// <summary>
        /// Most work the line matcher will do, counted as diagonals tried plus lines compared. The work grows
        /// with the length of the changed span times the number of lines that differ, and the path kept for
        /// walking back grows with the square of that number, so two long, wholly different texts would
        /// otherwise hold the history worker and its memory. Past this the changed span is shown whole, and
        /// its header says so.
        /// </summary>
        private const long DIFF_MAX_MATCH_STEPS = 2000000L;

        #endregion

        #region Diff rendering

        /// <summary>
        /// Renders one replacement as a unified-diff hunk against the file as it was before any edit.
        ///
        /// Whole lines are shown even when the replaced text starts or ends mid-line, because a diff that
        /// cut a line in half would not correspond to anything the reader can see in the file.
        /// </summary>
        /// <param name="original">File content before any edit in this batch was applied.</param>
        /// <param name="offset">Where the replaced text starts in <paramref name="original"/>.</param>
        /// <param name="oldLength">How many characters were replaced.</param>
        /// <param name="replacement">What replaced them.</param>
        /// <param name="newLineOffset">Net lines added by earlier hunks in this file, for the new-side numbers.</param>
        /// <returns>The hunk, how many lines this replacement adds and removes, and where its added lines start.</returns>
        private static DiffHunk BuildReplacementHunk(
            string original, int offset, int oldLength, string replacement, int newLineOffset)
        {
            int blockStart = StartOfLine(original, offset);
            int blockEnd = EndOfLine(original, offset + oldLength);

            string oldBlock = original.Substring(blockStart, blockEnd - blockStart);
            string newBlock = original.Substring(blockStart, offset - blockStart)
                + replacement
                + original.Substring(offset + oldLength, blockEnd - (offset + oldLength));

            var before = LinesBefore(original, blockStart, DIFF_CONTEXT_LINES);
            var after = LinesAfter(original, blockEnd, DIFF_CONTEXT_LINES);
            var removed = SplitLines(oldBlock);
            var added = SplitLines(newBlock);

            int oldStart = LineNumberAt(original, blockStart) - before.Count;
            int newStart = oldStart + newLineOffset;

            // The header is a sentence rather than a unified-diff @@ range. The @@ form encodes four numbers
            // for patch to consume and answers none of the questions a reader has, which was the owner's
            // objection to it: what changed, and where. Line numbers go on every line for the same reason,
            // since the point of the diff is to be found in the file afterwards.
            int width = LineNumberWidth(
                oldStart + before.Count + removed.Count + after.Count,
                newStart + before.Count + added.Count + after.Count);

            var body = new StringBuilder();
            body.Append("line ").Append(LineNumberAt(original, blockStart).ToString(CultureInfo.InvariantCulture))
                .Append(", ").Append(removed.Count.ToString(CultureInfo.InvariantCulture)).Append(" removed, ")
                .Append(added.Count.ToString(CultureInfo.InvariantCulture)).Append(" added");

            int oldLine = oldStart;
            int newLine = newStart;
            foreach (var line in before)
            {
                AppendNumberedLine(body, width, oldLine++, ' ', line);
                newLine++;
            }

            // A removed line is numbered in the file as it was and an added line in the file as it now is,
            // because those are the only numbers that locate each one.
            foreach (var line in removed) AppendNumberedLine(body, width, oldLine++, '-', line);
            foreach (var line in added) AppendNumberedLine(body, width, newLine++, '+', line);

            foreach (var line in after) AppendNumberedLine(body, width, oldLine++, ' ', line);

            return new DiffHunk
            {
                Text = body.ToString(),
                LineDelta = added.Count - removed.Count,
                NewFirstLine = newStart + before.Count,
                RemovedLines = removed.Count,
                AddedLines = added.Count
            };
        }

        /// <summary>
        /// Renders a whole-file change as diff hunks, one for each group of changed lines.
        ///
        /// Identical head and tail are trimmed before anything else, which is what keeps a one-line change to
        /// a large file rendering as a one-line change. What remains is matched line by line so scattered
        /// edits come back as separate hunks rather than as one span covering everything between them, and
        /// changes whose context would meet share one hunk instead of printing that context twice. A span too
        /// large to match within <see cref="DIFF_MAX_MATCH_STEPS"/> is shown whole, and its header says so.
        /// </summary>
        /// <param name="path">Path shown above the diff.</param>
        /// <param name="oldText">Content before the write, empty when the file is new.</param>
        /// <param name="newText">Content written.</param>
        /// <returns>The diff, or null when no line differs.</returns>
        private static string BuildFileDiff(string path, string oldText, string newText)
        {
            if (string.Equals(oldText, newText, StringComparison.Ordinal)) return null;

            var oldLines = SplitLines(oldText ?? string.Empty);
            var newLines = SplitLines(newText ?? string.Empty);
            if (string.IsNullOrEmpty(oldText)) oldLines.Clear();

            int head = 0;
            while (head < oldLines.Count && head < newLines.Count
                && string.Equals(oldLines[head], newLines[head], StringComparison.Ordinal))
            {
                head++;
            }

            int oldTail = oldLines.Count - 1;
            int newTail = newLines.Count - 1;
            while (oldTail >= head && newTail >= head
                && string.Equals(oldLines[oldTail], newLines[newTail], StringComparison.Ordinal))
            {
                oldTail--;
                newTail--;
            }

            if (oldTail < head && newTail < head) return null;

            // The trimmed head and tail are unchanged by construction; only the span between them is matched.
            var oldKept = new bool[oldLines.Count];
            var newKept = new bool[newLines.Count];
            for (int i = 0; i < head; i++)
            {
                oldKept[i] = true;
                newKept[i] = true;
            }

            for (int i = oldTail + 1, j = newTail + 1; i < oldLines.Count; i++, j++)
            {
                oldKept[i] = true;
                newKept[j] = true;
            }

            bool matched = TryMatchDiffLines(oldLines, head, oldTail + 1, newLines, head, newTail + 1, oldKept, newKept);
            List<DiffRegion> regions = CollectDiffRegions(oldKept, newKept);

            // Regions no further apart than twice the context would print overlapping context, so they share
            // a hunk.
            var body = new StringBuilder();
            int first = 0;
            while (first < regions.Count)
            {
                int last = first;
                while (last + 1 < regions.Count
                    && regions[last + 1].OldStart - regions[last].OldEnd <= 2 * DIFF_CONTEXT_LINES)
                {
                    last++;
                }

                if (body.Length > 0) body.Append(Environment.NewLine);
                AppendFileHunk(body, oldLines, newLines, regions, first, last, matched);
                first = last + 1;
            }

            // Unfenced, for the reason given where the batch hunks are assembled: the client shows fence
            // markers verbatim rather than highlighting what they wrap.
            return path + Environment.NewLine + body;
        }

        /// <summary>
        /// Walks an alignment from the top of both files and returns each run of changed lines. Kept lines
        /// pair off in order, so wherever both sides stand on a kept line it is the same unchanged line.
        /// </summary>
        /// <param name="oldKept">Which old lines the alignment keeps unchanged.</param>
        /// <param name="newKept">Which new lines the alignment keeps unchanged.</param>
        /// <returns>The changed runs, in file order.</returns>
        private static List<DiffRegion> CollectDiffRegions(bool[] oldKept, bool[] newKept)
        {
            var regions = new List<DiffRegion>();
            int oldIndex = 0;
            int newIndex = 0;
            while (oldIndex < oldKept.Length || newIndex < newKept.Length)
            {
                if (oldIndex < oldKept.Length && newIndex < newKept.Length && oldKept[oldIndex] && newKept[newIndex])
                {
                    oldIndex++;
                    newIndex++;
                    continue;
                }

                var region = new DiffRegion { OldStart = oldIndex, NewStart = newIndex };
                while (oldIndex < oldKept.Length && !oldKept[oldIndex]) oldIndex++;
                while (newIndex < newKept.Length && !newKept[newIndex]) newIndex++;
                region.OldEnd = oldIndex;
                region.NewEnd = newIndex;

                // Neither side moving means the two sides keep different numbers of lines, and the walk would
                // never finish.
                if (region.OldEnd == region.OldStart && region.NewEnd == region.NewStart)
                {
                    throw new InvalidOperationException("diff alignment keeps a different number of lines on each side");
                }

                regions.Add(region);
            }

            return regions;
        }

        /// <summary>
        /// Appends one hunk covering a run of changed regions: a header naming the first changed line and the
        /// hunk's counts, then its lines. Unchanged and removed lines carry their number in the file as it was
        /// and added lines their number in the file as it now is, as a batch hunk numbers them.
        /// </summary>
        /// <param name="body">Diff being built.</param>
        /// <param name="oldLines">The old file's lines.</param>
        /// <param name="newLines">The new file's lines.</param>
        /// <param name="regions">Every changed run in the file.</param>
        /// <param name="first">Index of the hunk's first region.</param>
        /// <param name="last">Index of the hunk's last region.</param>
        /// <param name="matched">False when the span was too large to match, so unchanged lines may sit inside it.</param>
        private static void AppendFileHunk(StringBuilder body, List<string> oldLines, List<string> newLines,
            List<DiffRegion> regions, int first, int last, bool matched)
        {
            DiffRegion opening = regions[first];
            DiffRegion closing = regions[last];
            int removed = 0;
            int added = 0;
            for (int r = first; r <= last; r++)
            {
                removed += regions[r].OldEnd - regions[r].OldStart;
                added += regions[r].NewEnd - regions[r].NewStart;
            }

            int width = LineNumberWidth(closing.OldEnd + DIFF_CONTEXT_LINES, closing.NewEnd + DIFF_CONTEXT_LINES);
            body.Append("line ").Append((opening.OldStart + 1).ToString(CultureInfo.InvariantCulture))
                .Append(", ").Append(removed.ToString(CultureInfo.InvariantCulture)).Append(" removed, ")
                .Append(added.ToString(CultureInfo.InvariantCulture)).Append(" added");
            if (!matched) body.Append(" (too large to match line by line, so the span may include unchanged lines)");

            int cursor = Math.Max(0, opening.OldStart - DIFF_CONTEXT_LINES);
            for (int r = first; r <= last; r++)
            {
                DiffRegion region = regions[r];
                for (; cursor < region.OldStart; cursor++)
                {
                    AppendNumberedLine(body, width, cursor + 1, ' ', oldLines[cursor]);
                }

                for (int i = region.OldStart; i < region.OldEnd; i++) AppendNumberedLine(body, width, i + 1, '-', oldLines[i]);
                for (int j = region.NewStart; j < region.NewEnd; j++) AppendNumberedLine(body, width, j + 1, '+', newLines[j]);
                cursor = region.OldEnd;
            }

            int stop = Math.Min(oldLines.Count, closing.OldEnd + DIFF_CONTEXT_LINES);
            for (; cursor < stop; cursor++)
            {
                AppendNumberedLine(body, width, cursor + 1, ' ', oldLines[cursor]);
            }
        }

        /// <summary>
        /// Aligns two runs of lines by the fewest lines removed and added (Myers' greedy algorithm) and marks
        /// every line the alignment keeps unchanged. It stops once its work passes
        /// <see cref="DIFF_MAX_MATCH_STEPS"/>, marking nothing, so the caller shows the span whole.
        /// </summary>
        /// <param name="oldLines">The old file's lines.</param>
        /// <param name="oldStart">First old line of the run.</param>
        /// <param name="oldEnd">Index just past the old run.</param>
        /// <param name="newLines">The new file's lines.</param>
        /// <param name="newStart">First new line of the run.</param>
        /// <param name="newEnd">Index just past the new run.</param>
        /// <param name="oldKept">Receives true for every old line kept unchanged.</param>
        /// <param name="newKept">Receives true for every new line kept unchanged.</param>
        /// <returns>True when the runs were aligned, false when the work limit stopped the matcher.</returns>
        private static bool TryMatchDiffLines(List<string> oldLines, int oldStart, int oldEnd,
            List<string> newLines, int newStart, int newEnd, bool[] oldKept, bool[] newKept)
        {
            int n = oldEnd - oldStart;
            int m = newEnd - newStart;
            if (n == 0 || m == 0) return true;   // One side is empty, so no line can be kept.

            // Lines compare as small integers, so the search never compares strings.
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            var a = new int[n];
            var b = new int[m];
            for (int i = 0; i < n; i++) a[i] = InternDiffLine(ids, oldLines[oldStart + i]);
            for (int j = 0; j < m; j++) b[j] = InternDiffLine(ids, newLines[newStart + j]);

            // furthest[offset + k] is the furthest old index reached on diagonal k = x - y. Each round's values
            // are kept, because walking back from the far corner needs the round before it.
            int offset = n + m + 1;
            var furthest = new int[2 * offset + 1];
            var rounds = new List<int[]>();
            long steps = 0;
            int found = -1;
            for (int d = 0; d <= n + m && found < 0; d++)
            {
                var round = new int[d + 1];
                for (int k = -d; k <= d; k += 2)
                {
                    // Step down (a line added) from diagonal k + 1 or right (a line removed) from k - 1,
                    // whichever had reached further, then follow the unchanged lines along the diagonal.
                    int x = k == -d || (k != d && furthest[offset + k - 1] < furthest[offset + k + 1])
                        ? furthest[offset + k + 1]
                        : furthest[offset + k - 1] + 1;
                    int y = x - k;
                    while (x < n && y < m && a[x] == b[y])
                    {
                        x++;
                        y++;
                        steps++;
                    }

                    furthest[offset + k] = x;
                    round[(k + d) / 2] = x;
                    steps++;
                    if (x >= n && y >= m)
                    {
                        found = d;
                        break;
                    }

                    if (steps > DIFF_MAX_MATCH_STEPS) return false;
                }

                rounds.Add(round);
            }

            if (found < 0) return false;

            // Walk back: each round took one step down or right and then a diagonal run of unchanged lines.
            int bx = n;
            int by = m;
            for (int d = found; d > 0; d--)
            {
                int[] previous = rounds[d - 1];
                int k = bx - by;
                bool down = k == -d || (k != d && previous[(k + d - 2) / 2] < previous[(k + d) / 2]);
                int previousK = down ? k + 1 : k - 1;
                int previousX = previous[(previousK + d - 1) / 2];
                int runStart = down ? previousX : previousX + 1;
                while (bx > runStart)
                {
                    bx--;
                    by--;
                    oldKept[oldStart + bx] = true;
                    newKept[newStart + by] = true;
                }

                bx = previousX;
                by = previousX - previousK;
            }

            // Round zero's run starts at the origin.
            while (bx > 0)
            {
                bx--;
                by--;
                oldKept[oldStart + bx] = true;
                newKept[newStart + by] = true;
            }

            return true;
        }

        /// <summary>Returns a line's number in the table, adding it when it is new.</summary>
        /// <param name="ids">Numbers given out so far, by exact line text.</param>
        /// <param name="line">The line.</param>
        /// <returns>The same number for every identical line.</returns>
        private static int InternDiffLine(Dictionary<string, int> ids, string line)
        {
            if (ids.TryGetValue(line, out int id)) return id;
            id = ids.Count;
            ids.Add(line, id);
            return id;
        }

        /// <summary>Widest line number either side of a diff will print, so the numbers form a column.</summary>
        /// <param name="highestOld">Largest old-side line number the hunk can reach.</param>
        /// <param name="highestNew">Largest new-side line number the hunk can reach.</param>
        /// <returns>Digit count to pad every number to.</returns>
        private static int LineNumberWidth(int highestOld, int highestNew)
        {
            int highest = Math.Max(Math.Max(highestOld, highestNew), 1);
            return highest.ToString(CultureInfo.InvariantCulture).Length;
        }

        /// <summary>
        /// Appends one diff line as a padded number, its marker, then the line's own text with its
        /// indentation intact. The marker is spaced away from the text so the code still lines up with
        /// itself down the hunk instead of being shunted one column by the marker.
        /// </summary>
        /// <param name="body">Hunk being built.</param>
        /// <param name="width">Digit count every number is padded to.</param>
        /// <param name="number">Line number in whichever side of the file this line belongs to.</param>
        /// <param name="marker">Space for unchanged, minus for removed, plus for added.</param>
        /// <param name="text">The line itself.</param>
        private static void AppendNumberedLine(StringBuilder body, int width, int number, char marker, string text)
        {
            body.Append(Environment.NewLine)
                .Append(number.ToString(CultureInfo.InvariantCulture).PadLeft(width))
                .Append(' ').Append(marker).Append(' ').Append(text);
        }

        /// <summary>One rendered hunk and the effect it has on the line numbers of everything after it.</summary>
        private struct DiffHunk
        {
            /// <summary>The rendered hunk.</summary>
            public string Text;

            /// <summary>Lines added minus lines removed.</summary>
            public int LineDelta;

            /// <summary>Line number of the first added line in the file as written, where the replacement landed.</summary>
            public int NewFirstLine;

            /// <summary>Whole lines removed, the count the hunk's header prints.</summary>
            public int RemovedLines;

            /// <summary>Whole lines added, the count the hunk's header prints.</summary>
            public int AddedLines;
        }

        /// <summary>One run of changed lines: a span of the old file replaced by a span of the new one.</summary>
        private struct DiffRegion
        {
            /// <summary>Index of the first removed line in the old file.</summary>
            public int OldStart;

            /// <summary>Index just past the last removed line in the old file.</summary>
            public int OldEnd;

            /// <summary>Index of the first added line in the new file.</summary>
            public int NewStart;

            /// <summary>Index just past the last added line in the new file.</summary>
            public int NewEnd;
        }

        #endregion

        #region Diff helpers

        /// <summary>Finds the start of the line containing an offset.</summary>
        /// <param name="text">Content searched.</param>
        /// <param name="offset">Position inside it.</param>
        /// <returns>Index of the first character on that line.</returns>
        private static int StartOfLine(string text, int offset)
        {
            int i = Math.Min(offset, text.Length);
            while (i > 0 && text[i - 1] != '\n') i--;
            return i;
        }

        /// <summary>Finds the end of the line containing an offset, excluding the newline itself.</summary>
        /// <param name="text">Content searched.</param>
        /// <param name="offset">Position inside it.</param>
        /// <returns>Index just past the last character on that line.</returns>
        private static int EndOfLine(string text, int offset)
        {
            int i = Math.Min(offset, text.Length);
            while (i < text.Length && text[i] != '\n') i++;
            return i;
        }

        /// <summary>Counts which line an offset falls on, counting from one.</summary>
        /// <param name="text">Content searched.</param>
        /// <param name="offset">Position inside it.</param>
        /// <returns>The one-based line number.</returns>
        private static int LineNumberAt(string text, int offset)
        {
            int line = 1;
            int limit = Math.Min(offset, text.Length);
            for (int i = 0; i < limit; i++)
            {
                if (text[i] == '\n') line++;
            }

            return line;
        }

        /// <summary>Collects up to a number of whole lines ending just before an offset.</summary>
        /// <param name="text">Content searched.</param>
        /// <param name="start">Start of the line the context precedes.</param>
        /// <param name="count">How many lines at most.</param>
        /// <returns>The context lines, in file order.</returns>
        private static List<string> LinesBefore(string text, int start, int count)
        {
            var lines = new List<string>();
            int cursor = start;
            while (lines.Count < count && cursor > 0)
            {
                int previousEnd = cursor - 1;
                int previousStart = StartOfLine(text, previousEnd);
                lines.Insert(0, StripCarriageReturn(text.Substring(previousStart, previousEnd - previousStart)));
                cursor = previousStart;
            }

            return lines;
        }

        /// <summary>Collects up to a number of whole lines starting just after an offset.</summary>
        /// <param name="text">Content searched.</param>
        /// <param name="end">End of the line the context follows.</param>
        /// <param name="count">How many lines at most.</param>
        /// <returns>The context lines, in file order.</returns>
        private static List<string> LinesAfter(string text, int end, int count)
        {
            var lines = new List<string>();
            int cursor = end;
            while (lines.Count < count && cursor < text.Length)
            {
                if (text[cursor] == '\n') cursor++;
                if (cursor >= text.Length) break;

                int lineEnd = EndOfLine(text, cursor);
                lines.Add(StripCarriageReturn(text.Substring(cursor, lineEnd - cursor)));
                cursor = lineEnd;
            }

            return lines;
        }

        /// <summary>Splits a block into lines without inventing a trailing empty one.</summary>
        /// <param name="block">The block to split.</param>
        /// <returns>Its lines.</returns>
        private static List<string> SplitLines(string block)
        {
            var lines = new List<string>();
            foreach (var line in block.Split('\n')) lines.Add(StripCarriageReturn(line));
            return lines;
        }

        /// <summary>Drops a trailing carriage return so a CRLF file does not render one per line.</summary>
        /// <param name="line">The line to clean.</param>
        /// <returns>The line without its carriage return.</returns>
        private static string StripCarriageReturn(string line)
        {
            return line.EndsWith("\r", StringComparison.Ordinal) ? line.Substring(0, line.Length - 1) : line;
        }

        #endregion
    }
}
