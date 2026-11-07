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
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Removes Conduit's persisted deferred-tool definitions from the Claude conversation that called the
    /// reset tool. Claude Code keeps those definitions in memory until its harness restarts, so this prepares
    /// the exact saved conversation for a fresh live tool list on its next resume.
    /// </summary>
    internal static class ConduitClaudeSessionCacheReset
    {
        #region Constants

        /// <summary>Published MCP name of the Claude-only reset tool.</summary>
        internal const string TOOL_NAME = "claude-session-refresh-tools";

        /// <summary>Smallest nonce accepted as a useful call-site fingerprint.</summary>
        private const int MIN_NONCE_LENGTH = 16;

        /// <summary>Largest nonce accepted, bounding every transcript substring comparison.</summary>
        private const int MAX_NONCE_LENGTH = 128;

        /// <summary>Largest transcript rewritten in one call, so a corrupt file cannot consume the editor.</summary>
        private const long MAX_TRANSCRIPT_BYTES = 256L * 1024L * 1024L;

        /// <summary>UTF-8 decoder that refuses malformed transcript bytes instead of replacing them silently.</summary>
        private static readonly UTF8Encoding _strictUtf8 = new UTF8Encoding(false, true);

        #endregion

        #region Result types

        /// <summary>Evidence returned after one conversation was prepared for a fresh Conduit tool list.</summary>
        internal sealed class Outcome
        {
            /// <summary>Claude conversation id taken from the matched transcript record.</summary>
            public string SessionId;

            /// <summary>Transcript that was changed, or inspected when no definitions were present.</summary>
            public string TranscriptPath;

            /// <summary>Backup containing the exact pre-reset transcript, or null when nothing changed.</summary>
            public string BackupPath;

            /// <summary>Number of Conduit schema entries removed.</summary>
            public int RemovedEntries;

            /// <summary>Number of Conduit skill snapshots removed.</summary>
            public int RemovedSkills;

            /// <summary>Number of records changed, deferred-tool and invoked-skill together.</summary>
            public int ChangedRecords;
        }

        /// <summary>One exact pending tool-use match inside a Claude transcript.</summary>
        private sealed class CallMatch
        {
            /// <summary>Transcript containing the call.</summary>
            public string TranscriptPath;

            /// <summary>Conversation id recorded beside the call.</summary>
            public string SessionId;

            /// <summary>Qualified MCP prefix recorded by Claude, including its trailing separator.</summary>
            public string ToolPrefix;
        }

        #endregion

        #region Public surface

        /// <summary>Builds the tool definition added only to an authenticated Claude client's list.</summary>
        /// <returns>A private definition safe for one response.</returns>
        internal static JObject ToolDefinition()
        {
            return new JObject
            {
                ["name"] = TOOL_NAME,
                ["description"] =
                    "Claude Code only. Fixes ONE condition, in two places it shows up: something of Conduit's " +
                    "CHANGED while this conversation already had it loaded, so what you are reading is the old " +
                    "version. Claude Code saves both a tool's schema and a skill's full text into the " +
                    "conversation transcript, and a resume restores those copies, which is why no reconnect, " +
                    "restart or skill re-sync alone has ever fixed it. This removes Conduit's saved tool " +
                    "schemas and its saved skill snapshots from the transcript of the conversation making the " +
                    "call, so the same task rebuilds both from source on its next resume. Reach for it when a " +
                    "tool card contradicts the live signature, or when a convention document you were given " +
                    "contradicts the file on disk. DO NOT use it for a tool that was newly ADDED - new names " +
                    "arrive on their own and this would cost a restart for nothing. CONFIRM the staleness " +
                    "first by reading the live source (unity-tool-list or the Signature line tools/list " +
                    "serves for a tool; the document itself for a skill); a call with nothing to remove " +
                    "writes nothing and reports zero. A ZERO COUNT IS A CORRECT RESULT, NOT A FAULT IN THIS " +
                    "TOOL - it means that kind was never saved in this transcript. A skill's text is written " +
                    "there only once the skill has been invoked in THIS conversation, so a conversation that " +
                    "never invoked one reports zero skills and is right to; the same holds for a schema never " +
                    "loaded here. DO NOT investigate the zero, do not widen what this removes, and do not " +
                    "re-call for that kind. If you expected a snapshot, invoke the skill and call once more. " +
                    "ASK THE USER BEFORE CALLING: it only takes effect once " +
                    "they restart the harness, which costs them their prompt cache, so it is their decision " +
                    "and not a refresh to fire on a hunch. THE TWO KINDS SHARE THAT ONE RESTART, which is why " +
                    "they are cleared together rather than by separate verbs: a schema and a document go " +
                    "stale independently, but clearing both costs exactly what clearing either costs, and " +
                    "the caller is often wrong about which of the two is lying to them. Never call it twice " +
                    "in a row for the two kinds. It rewrites the transcript file itself, keeping a " +
                    "timestamped backup beside it, and it touches only Conduit's own entries, leaving other " +
                    "servers' tools and the user's own skills alone. It cannot change what the running " +
                    "harness already holds in memory. Generate a fresh random nonce of at least 16 letters, " +
                    "digits, hyphens or underscores, never reusing one. After success, tell the user to " +
                    "restart the Claude harness and resume this same task, not a new one.",
                ["inputSchema"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        // Only the keywords ConduitJsonSchemaValidator implements are published. It refuses a
                        // schema carrying an unknown one, so maxLength and pattern made this tool reject every
                        // call before the verb ran. ValidNonce enforces both bounds and the charset in code and
                        // says so when it refuses, which is where the contract actually lives; stating them here
                        // as well only adds a way for the tool to become uncallable.
                        ["nonce"] = new JObject
                        {
                            ["type"] = "string",
                            ["minLength"] = MIN_NONCE_LENGTH,
                            ["description"] =
                                "Fresh random call-site fingerprint; never reuse one. " +
                                "16 to 128 ASCII letters, digits, hyphens or underscores.",
                        },
                    },
                    ["required"] = new JArray { "nonce" },
                    ["additionalProperties"] = false,
                },
            };
        }

        /// <summary>Whether a per-client bearer belongs to a Claude harness supported by this reset.</summary>
        /// <param name="caller">Client display name resolved from the bearer.</param>
        /// <returns>True only for the two Claude connection entries Conduit issues.</returns>
        internal static bool IsClaudeCaller(string caller)
        {
            return string.Equals(caller, "Claude Code", StringComparison.Ordinal)
                || string.Equals(caller, "Claude Desktop", StringComparison.Ordinal);
        }

        /// <summary>
        /// Finds and prepares the calling conversation under the current user's Claude project store.
        /// </summary>
        /// <param name="nonce">Fresh nonce carried by this tool call.</param>
        /// <param name="outcome">Receives reset evidence.</param>
        /// <param name="error">Receives a refusal that identifies what failed.</param>
        /// <returns>True when the exact conversation was found and is ready for resume.</returns>
        internal static bool TryPrepare(string nonce, out Outcome outcome, out string error)
        {
            string projectRoot;
            string claudeProjectsRoot;
            try
            {
                projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
                claudeProjectsRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".claude",
                    "projects");
            }
            catch (Exception ex)
            {
                outcome = null;
                error = "Could not resolve the project or Claude data directory: " + ex.Message;
                return false;
            }

            return TryPrepareAt(projectRoot, claudeProjectsRoot, nonce, out outcome, out error);
        }

        /// <summary>
        /// Finds and prepares one matching transcript beneath an explicit Claude project store. Kept separate
        /// from environment discovery so the gate exercises the same matcher and rewrite against fixtures.
        /// </summary>
        /// <param name="projectRoot">Project cwd the transcript must record.</param>
        /// <param name="claudeProjectsRoot">Directory holding Claude's encoded project directories.</param>
        /// <param name="nonce">Fresh nonce carried by this tool call.</param>
        /// <param name="outcome">Receives reset evidence.</param>
        /// <param name="error">Receives a refusal that identifies what failed.</param>
        /// <returns>True when exactly one transcript was matched and safely processed.</returns>
        internal static bool TryPrepareAt(
            string projectRoot,
            string claudeProjectsRoot,
            string nonce,
            out Outcome outcome,
            out string error)
        {
            outcome = null;
            error = null;

            if (!ValidNonce(nonce))
            {
                error = "nonce must contain 16 to 128 ASCII letters, digits, hyphens or underscores";
                return false;
            }

            string canonicalProject;
            string projectStore;
            try
            {
                canonicalProject = Path.GetFullPath(projectRoot);
                var canonicalStore = Path.GetFullPath(claudeProjectsRoot);
                projectStore = Path.Combine(canonicalStore, ClaudeProjectSlug(canonicalProject));
            }
            catch (Exception ex)
            {
                error = "Could not canonicalise the project paths: " + ex.Message;
                return false;
            }

            if (!Directory.Exists(projectStore))
            {
                error = "Claude has no transcript directory for this project at " + projectStore;
                return false;
            }

            var matches = new List<CallMatch>();
            var unreadable = new List<string>();
            foreach (string transcript in Directory.GetFiles(projectStore, "*.jsonl", SearchOption.TopDirectoryOnly))
            {
                string readError;
                if (!CollectCallMatches(transcript, canonicalProject, nonce, matches, out readError))
                {
                    unreadable.Add(Path.GetFileName(transcript) + ": " + readError);
                }
            }

            if (matches.Count != 1)
            {
                error = matches.Count == 0
                    ? "No pending " + TOOL_NAME + " call with this nonce was found in this project's Claude transcripts"
                    : "The nonce matched " + matches.Count.ToString(CultureInfo.InvariantCulture) +
                      " tool calls; refusing an ambiguous transcript reset";

                if (unreadable.Count > 0)
                {
                    error += ". " + unreadable.Count.ToString(CultureInfo.InvariantCulture) +
                             " transcript(s) could not be read";
                }

                return false;
            }

            return Rewrite(matches[0], out outcome, out error);
        }

        #endregion

        #region Matching

        /// <summary>Whether a nonce is narrow enough to search safely and strong enough to be unique.</summary>
        /// <param name="nonce">Candidate nonce.</param>
        /// <returns>True when it meets the published schema.</returns>
        private static bool ValidNonce(string nonce)
        {
            if (string.IsNullOrEmpty(nonce)
                || nonce.Length < MIN_NONCE_LENGTH
                || nonce.Length > MAX_NONCE_LENGTH)
            {
                return false;
            }

            foreach (char c in nonce)
            {
                if ((c >= 'A' && c <= 'Z')
                    || (c >= 'a' && c <= 'z')
                    || (c >= '0' && c <= '9')
                    || c == '-'
                    || c == '_')
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        /// <summary>Encodes a project path the way Claude names its directory under .claude/projects.</summary>
        /// <param name="projectRoot">Canonical absolute project path.</param>
        /// <returns>Path with every non-alphanumeric character replaced by a hyphen.</returns>
        private static string ClaudeProjectSlug(string projectRoot)
        {
            var slug = new StringBuilder(projectRoot.Length);
            foreach (char c in projectRoot)
            {
                slug.Append(char.IsLetterOrDigit(c) ? c : '-');
            }

            return slug.ToString();
        }

        /// <summary>Collects structural tool-use matches from one transcript.</summary>
        /// <param name="transcript">Candidate JSONL file.</param>
        /// <param name="projectRoot">Canonical project cwd the record must carry.</param>
        /// <param name="nonce">Exact nonce supplied to the tool.</param>
        /// <param name="matches">Sink for valid matches.</param>
        /// <param name="error">Receives a read or parse failure.</param>
        /// <returns>True when the file was read without an invalid candidate record.</returns>
        private static bool CollectCallMatches(
            string transcript,
            string projectRoot,
            string nonce,
            List<CallMatch> matches,
            out string error)
        {
            error = null;
            try
            {
                foreach (string line in File.ReadLines(transcript, _strictUtf8))
                {
                    if (line.IndexOf(nonce, StringComparison.Ordinal) < 0
                        || line.IndexOf(TOOL_NAME, StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    JObject record;
                    try { record = JObject.Parse(line); }
                    catch (Exception ex)
                    {
                        error = "candidate record is invalid JSON: " + ex.Message;
                        return false;
                    }

                    if (!string.Equals((string)record["type"], "assistant", StringComparison.Ordinal)) continue;
                    if (!SamePath((string)record["cwd"], projectRoot)) continue;

                    string sessionId = (string)record["sessionId"];
                    if (string.IsNullOrEmpty(sessionId)
                        || !string.Equals(
                            sessionId,
                            Path.GetFileNameWithoutExtension(transcript),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var message = record["message"] as JObject;
                    var content = message == null ? null : message["content"] as JArray;
                    if (content == null) continue;

                    foreach (JObject item in content.Children<JObject>())
                    {
                        if (!string.Equals((string)item["type"], "tool_use", StringComparison.Ordinal)) continue;

                        string qualifiedName = (string)item["name"];
                        string suffix = "__" + TOOL_NAME;
                        if (string.IsNullOrEmpty(qualifiedName)
                            || !qualifiedName.StartsWith("mcp__", StringComparison.Ordinal)
                            || !qualifiedName.EndsWith(suffix, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var input = item["input"] as JObject;
                        if (!string.Equals((string)(input == null ? null : input["nonce"]), nonce, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        matches.Add(new CallMatch
                        {
                            TranscriptPath = transcript,
                            SessionId = sessionId,
                            ToolPrefix = qualifiedName.Substring(0, qualifiedName.Length - TOOL_NAME.Length),
                        });
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>Compares canonical paths using the host filesystem's case rules.</summary>
        /// <param name="left">First path.</param>
        /// <param name="right">Second path.</param>
        /// <returns>True when both resolve to the same path.</returns>
        private static bool SamePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return false;

            try
            {
                var comparison = Path.DirectorySeparatorChar == '\\'
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                return string.Equals(
                    Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    comparison);
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Rewrite

        /// <summary>Rewrites one uniquely matched transcript and retains its exact original as a backup.</summary>
        /// <param name="match">Exact conversation selected by the pending tool call.</param>
        /// <param name="outcome">Receives reset evidence.</param>
        /// <param name="error">Receives a safe refusal.</param>
        /// <returns>True when the transcript is ready for resume.</returns>
        private static bool Rewrite(CallMatch match, out Outcome outcome, out string error)
        {
            outcome = null;
            error = null;

            byte[] original;
            try
            {
                original = ReadExclusive(match.TranscriptPath);
            }
            catch (Exception ex)
            {
                error = "Could not acquire the calling transcript exclusively: " + ex.Message;
                return false;
            }

            if (original.LongLength > MAX_TRANSCRIPT_BYTES)
            {
                error = "The calling transcript is " + original.LongLength.ToString(CultureInfo.InvariantCulture) +
                        " bytes; the reset limit is " + MAX_TRANSCRIPT_BYTES.ToString(CultureInfo.InvariantCulture);
                return false;
            }

            bool hadBom = original.Length >= 3
                && original[0] == 0xEF
                && original[1] == 0xBB
                && original[2] == 0xBF;

            string source;
            try
            {
                source = _strictUtf8.GetString(original, hadBom ? 3 : 0, original.Length - (hadBom ? 3 : 0));
            }
            catch (Exception ex)
            {
                error = "The calling transcript is not valid UTF-8: " + ex.Message;
                return false;
            }

            int removed;
            int removedSkills;
            int changedRecords;
            string transformed;
            if (!TryTransform(
                source, match.ToolPrefix, out transformed, out removed, out removedSkills, out changedRecords, out error))
            {
                return false;
            }

            outcome = new Outcome
            {
                SessionId = match.SessionId,
                TranscriptPath = match.TranscriptPath,
                RemovedEntries = removed,
                RemovedSkills = removedSkills,
                ChangedRecords = changedRecords,
            };

            if (removed == 0 && removedSkills == 0) return true;

            byte[] replacement = Encode(transformed, hadBom);
            string temp = match.TranscriptPath + ".conduit-refresh-" + Guid.NewGuid().ToString("N") + ".tmp";
            string backup = match.TranscriptPath + ".before-conduit-refresh-" +
                            DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" +
                            Guid.NewGuid().ToString("N").Substring(0, 8) + ".bak";

            try
            {
                WriteThrough(temp, replacement);

                byte[] current = ReadExclusive(match.TranscriptPath);
                if (!FixedBytesEqual(Hash(original), Hash(current)))
                {
                    error = "The calling transcript changed while the reset was prepared; nothing was replaced";
                    TryDelete(temp);
                    outcome = null;
                    return false;
                }

                File.Replace(temp, match.TranscriptPath, backup, false);
                outcome.BackupPath = backup;
                return true;
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                error = "Could not replace the calling transcript atomically: " + ex.Message;
                outcome = null;
                return false;
            }
        }

        /// <summary>Reads a file while refusing every concurrent reader or writer.</summary>
        /// <param name="path">File to read.</param>
        /// <returns>Exact bytes.</returns>
        private static byte[] ReadExclusive(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (stream.Length > int.MaxValue) throw new IOException("file is too large to read");

                var bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = stream.Read(bytes, offset, bytes.Length - offset);
                    if (read <= 0) throw new EndOfStreamException("transcript ended while it was being read");
                    offset += read;
                }

                return bytes;
            }
        }

        /// <summary>
        /// Removes every schema entry carrying the calling Conduit server prefix while preserving all
        /// unrelated lines and their original newline spelling.
        /// </summary>
        /// <param name="source">Decoded JSONL text without a byte-order mark.</param>
        /// <param name="toolPrefix">Qualified Conduit prefix recorded on the reset call.</param>
        /// <param name="transformed">Receives rewritten JSONL.</param>
        /// <param name="removed">Receives removed tool-schema entry count.</param>
        /// <param name="removedSkills">Receives removed skill-snapshot count.</param>
        /// <param name="changedRecords">Receives changed attachment count.</param>
        /// <param name="error">Receives a malformed transcript error.</param>
        /// <returns>True when every line remained valid JSON.</returns>
        private static bool TryTransform(
            string source,
            string toolPrefix,
            out string transformed,
            out int removed,
            out int removedSkills,
            out int changedRecords,
            out string error)
        {
            removed = 0;
            removedSkills = 0;
            changedRecords = 0;
            error = null;
            var output = new StringBuilder(source.Length);
            int start = 0;
            int lineNumber = 0;

            while (start < source.Length)
            {
                lineNumber++;
                int newline = source.IndexOf('\n', start);
                int physicalEnd = newline < 0 ? source.Length : newline;
                int jsonEnd = physicalEnd > start && source[physicalEnd - 1] == '\r'
                    ? physicalEnd - 1
                    : physicalEnd;
                string line = source.Substring(start, jsonEnd - start);
                string ending = newline < 0
                    ? string.Empty
                    : (jsonEnd < physicalEnd ? "\r\n" : "\n");

                JObject record;
                try { record = JObject.Parse(line); }
                catch (Exception ex)
                {
                    transformed = null;
                    error = "Transcript line " + lineNumber.ToString(CultureInfo.InvariantCulture) +
                            " is invalid JSON: " + ex.Message;
                    return false;
                }

                int removedHere = 0;
                int removedSkillsHere = 0;
                var attachment = record["attachment"] as JObject;
                bool isAttachment = string.Equals((string)record["type"], "attachment", StringComparison.Ordinal)
                    && attachment != null;

                if (isAttachment
                    && string.Equals((string)attachment["type"], "deferred_tools_record", StringComparison.Ordinal))
                {
                    var entries = attachment["entries"] as JArray;
                    if (entries != null)
                    {
                        for (int i = entries.Count - 1; i >= 0; i--)
                        {
                            string name = (string)entries[i]["name"];
                            if (string.IsNullOrEmpty(name)
                                || !name.StartsWith(toolPrefix, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            entries.RemoveAt(i);
                            removedHere++;
                        }
                    }
                }

                // The same staleness in a second place. Claude Code saves the TEXT of every skill it has
                // loaded into the transcript, so a resumed conversation keeps whichever version of a Conduit
                // convention document it first read, no matter how many times the installed skill is synced
                // from source afterwards. It is removed rather than rewritten: the skill reloads from disk on
                // its next invocation, so nothing here has to be kept in step with the document.
                if (isAttachment
                    && string.Equals((string)attachment["type"], "invoked_skills", StringComparison.Ordinal))
                {
                    var skills = attachment["skills"] as JArray;
                    if (skills != null)
                    {
                        for (int i = skills.Count - 1; i >= 0; i--)
                        {
                            // Matched against Conduit's own registration table. The client's skills folder
                            // holds the user's skills beside Conduit's, and removing one of theirs would
                            // discard something this bridge has no business touching.
                            if (!ConduitSkills.IsConduitSkillName((string)skills[i]["name"])) continue;

                            skills.RemoveAt(i);
                            removedSkillsHere++;
                        }
                    }
                }

                if (removedHere > 0 || removedSkillsHere > 0)
                {
                    removed += removedHere;
                    removedSkills += removedSkillsHere;
                    changedRecords++;
                    line = record.ToString(Formatting.None);
                }

                output.Append(line);
                output.Append(ending);
                start = newline < 0 ? source.Length : newline + 1;
            }

            if (source.Length == 0)
            {
                transformed = null;
                error = "The calling transcript is empty";
                return false;
            }

            transformed = output.ToString();
            return true;
        }

        /// <summary>Encodes transformed text while preserving whether the original carried a UTF-8 BOM.</summary>
        /// <param name="text">Text to encode.</param>
        /// <param name="withBom">Whether to prepend a UTF-8 byte-order mark.</param>
        /// <returns>Encoded bytes.</returns>
        private static byte[] Encode(string text, bool withBom)
        {
            byte[] body = _strictUtf8.GetBytes(text);
            if (!withBom) return body;

            var bytes = new byte[body.Length + 3];
            bytes[0] = 0xEF;
            bytes[1] = 0xBB;
            bytes[2] = 0xBF;
            Buffer.BlockCopy(body, 0, bytes, 3, body.Length);
            return bytes;
        }

        /// <summary>Writes a complete replacement and flushes it to disk before the atomic swap.</summary>
        /// <param name="path">Temporary path beside the transcript.</param>
        /// <param name="bytes">Complete replacement bytes.</param>
        private static void WriteThrough(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        /// <summary>Computes SHA-256 over one byte array.</summary>
        /// <param name="bytes">Bytes to hash.</param>
        /// <returns>Digest.</returns>
        private static byte[] Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(bytes);
        }

        /// <summary>Compares two fixed-size digests without stopping at their first difference.</summary>
        /// <param name="left">First digest.</param>
        /// <param name="right">Second digest.</param>
        /// <returns>True when every byte matches.</returns>
        private static bool FixedBytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;

            int difference = 0;
            for (int i = 0; i < left.Length; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        /// <summary>Deletes a temporary file after a refused replacement.</summary>
        /// <param name="path">Temporary path.</param>
        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // The original transcript is already untouched; an orphaned temp is safer than another failure.
            }
        }

        #endregion
    }

    /// <summary>Exercises exact-session matching, selective removal, backup and ambiguity refusal.</summary>
    internal static class ConduitClaudeSessionCacheResetGate
    {
        #region Gate

        /// <summary>Runs the reset engine against isolated synthetic Claude transcripts.</summary>
        [MenuItem("Tools/Conduit Gates/Verify Claude session tool refresh")]
        private static void Verify()
        {
            string root = Path.Combine(
                "Library",
                "CCG",
                Guid.NewGuid().ToString("N").Substring(0, 8));
            string project = Path.GetFullPath(Path.Combine(root, "p"));
            string store = Path.GetFullPath(Path.Combine(root, "s"));
            string projectStore = Path.Combine(store, ClaudeProjectSlug(project));
            Directory.CreateDirectory(projectStore);

            bool ok = false;
            string detail;
            try
            {
                string nonce = "gate_" + Guid.NewGuid().ToString("N");
                string session = Guid.NewGuid().ToString();
                string transcript = Path.Combine(projectStore, session + ".jsonl");
                string prefix = "mcp__fixture__";

                var deferred = new JObject
                {
                    ["type"] = "attachment",
                    ["attachment"] = new JObject
                    {
                        ["type"] = "deferred_tools_record",
                        ["entries"] = new JArray
                        {
                            new JObject { ["name"] = prefix + "one", ["input_schema"] = new JObject() },
                            new JObject { ["name"] = "mcp__other__keep", ["input_schema"] = new JObject() },
                            new JObject { ["name"] = prefix + "two", ["input_schema"] = new JObject() },
                        },
                    },
                };
                var call = new JObject
                {
                    ["type"] = "assistant",
                    ["sessionId"] = session,
                    ["cwd"] = project,
                    ["message"] = new JObject
                    {
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "tool_use",
                                ["id"] = "toolu_gate",
                                ["name"] = prefix + ConduitClaudeSessionCacheReset.TOOL_NAME,
                                ["input"] = new JObject { ["nonce"] = nonce },
                            },
                        },
                    },
                };

                // A real skill name from Conduit's own table beside two that are not Conduit's at all. The
                // user's skills live in the same folder as the installed ones, so the case that matters is
                // that a reset reaches exactly one of these three.
                var invoked = new JObject
                {
                    ["type"] = "attachment",
                    ["attachment"] = new JObject
                    {
                        ["type"] = "invoked_skills",
                        ["skills"] = new JArray
                        {
                            new JObject { ["name"] = "engineering-method", ["content"] = "keep me" },
                            new JObject { ["name"] = "conduit-bridge-authoring", ["content"] = "stale copy" },
                            new JObject { ["name"] = "itsrenji-brand", ["content"] = "keep me too" },
                        },
                    },
                };

                File.WriteAllText(
                    transcript,
                    deferred.ToString(Formatting.None) + "\n" +
                    invoked.ToString(Formatting.None) + "\n" +
                    call.ToString(Formatting.None) + "\n",
                    new UTF8Encoding(false));

                ConduitClaudeSessionCacheReset.Outcome outcome;
                string error;
                bool prepared = ConduitClaudeSessionCacheReset.TryPrepareAt(
                    project, store, nonce, out outcome, out error);

                var lines = File.ReadAllLines(transcript, new UTF8Encoding(false, true));
                var rewritten = JObject.Parse(lines[0]);
                var entries = (JArray)rewritten["attachment"]["entries"];
                bool selective = entries.Count == 1
                    && (string)entries[0]["name"] == "mcp__other__keep";

                var rewrittenSkills = (JArray)JObject.Parse(lines[1])["attachment"]["skills"];
                bool skillsSelective = rewrittenSkills.Count == 2
                    && (string)rewrittenSkills[0]["name"] == "engineering-method"
                    && (string)rewrittenSkills[1]["name"] == "itsrenji-brand";
                bool backup = prepared
                    && outcome != null
                    && !string.IsNullOrEmpty(outcome.BackupPath)
                    && File.Exists(outcome.BackupPath);
                bool counts = prepared
                    && outcome.RemovedEntries == 2
                    && outcome.RemovedSkills == 1
                    && outcome.ChangedRecords == 2
                    && outcome.SessionId == session;

                string second = Guid.NewGuid().ToString();
                File.WriteAllText(
                    Path.Combine(projectStore, second + ".jsonl"),
                    new JObject
                    {
                        ["type"] = "assistant",
                        ["sessionId"] = second,
                        ["cwd"] = project,
                        ["message"] = call["message"].DeepClone(),
                    }.ToString(Formatting.None) + "\n",
                    new UTF8Encoding(false));

                ConduitClaudeSessionCacheReset.Outcome ambiguousOutcome;
                string ambiguousError;
                bool ambiguousRefused = !ConduitClaudeSessionCacheReset.TryPrepareAt(
                    project, store, nonce, out ambiguousOutcome, out ambiguousError)
                    && ambiguousError != null
                    && ambiguousError.Contains("matched 2 tool calls");

                ok = prepared && selective && skillsSelective && backup && counts && ambiguousRefused;
                detail = ok
                    ? "removed only the two matching schemas and the one Conduit skill, kept the user's own " +
                      "skills and another server's tool, retained a backup, and refused two nonce matches"
                    : "prepared=" + prepared + ", selective=" + selective +
                      ", skillsSelective=" + skillsSelective + ", backup=" + backup +
                      ", counts=" + counts + ", ambiguousRefused=" + ambiguousRefused +
                      ", error=" + (error ?? ambiguousError ?? "none");
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                try
                {
                    string canonicalRoot = Path.GetFullPath(root);
                    string canonicalLibrary = Path.GetFullPath("Library");
                    if (canonicalRoot.StartsWith(
                        canonicalLibrary + Path.DirectorySeparatorChar,
                        Path.DirectorySeparatorChar == '\\'
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                    {
                        Directory.Delete(canonicalRoot, true);
                    }
                }
                catch
                {
                    // A failed gate may leave its isolated fixture for inspection.
                }
            }

            if (ok) Debug.Log("[ClaudeSessionRefreshGate] PASS - " + detail);
            else Debug.LogError("[ClaudeSessionRefreshGate] FAIL - " + detail);
        }

        /// <summary>Encodes a project path the way the production matcher selects its fixture directory.</summary>
        /// <param name="projectRoot">Canonical fixture project path.</param>
        /// <returns>Claude project-directory slug.</returns>
        private static string ClaudeProjectSlug(string projectRoot)
        {
            var slug = new StringBuilder(projectRoot.Length);
            foreach (char c in projectRoot)
            {
                slug.Append(char.IsLetterOrDigit(c) ? c : '-');
            }

            return slug.ToString();
        }

        #endregion
    }
}
