/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// The tool catalogue this Editor publishes over MCP, built once per domain and then immutable.
//
// Built once, on the main thread, before the listener accepts anything. The reason is not tidiness: a worker
// thread cannot call into the bridge to discover verbs, so a catalogue built lazily on first request would
// either have to marshal mid-request or be built from a worker, and both are worse than doing it at startup
// while the main thread is already ours.
//
// Immutable afterwards, for a different reason. MCP clients cache tools/list, so a catalogue that changed
// shape under a connected client would hand two clients different contracts for one server. A new verb
// becomes reachable through conduit-run the moment it compiles, and its typed tool arrives on the client's
// next reconnect - which is what the revision hash below is for: a client can tell whether the catalogue it
// cached is still the one being served.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>Holds this domain's published tool catalogue and the builders that go with it.</summary>
    internal static class ConduitMcpCatalog
    {
        #region Private state

        /// <summary>Guards publication, so a worker cannot observe a half-built snapshot.</summary>
        private static readonly object _gate = new object();

        /// <summary>The published tools array, or null until the snapshot is built.</summary>
        private static JArray _tools;

        /// <summary>Command builders keyed by tool name.</summary>
        private static Dictionary<string, Func<JObject, string>> _builders;

        /// <summary>Bridge verb keyed by tool name, so a durable admission records the canonical verb.</summary>
        private static Dictionary<string, string> _verbs;

        /// <summary>Published input schema keyed by tool name, so validation never scans the catalogue.</summary>
        private static Dictionary<string, JObject> _schemas;

        /// <summary>Stable digest of the published catalogue.</summary>
        private static string _revision;

        /// <summary>Why the snapshot could not be built, or null.</summary>
        private static string _error;

        /// <summary>Instructions served at initialize, snapshotted with the catalogue and refreshed on edit.</summary>
        private static string _instructions;

        /// <summary>
        /// Where the conventions index lives, resolved on the main thread at publish and kept so the
        /// instructions can be rebuilt later from a thread that must not touch the AssetDatabase.
        /// </summary>
        private static string _conventionsPath;

        /// <summary>
        /// The modification stamps of every document the instructions were built from. Publication is
        /// triggered by the TOOL surface changing, and editing a convention document changes no verb, so a
        /// snapshot alone served the rules as they read at the last compile and went quietly stale the
        /// moment a rule was edited. Comparing this at serve time is what keeps the served rules current.
        /// </summary>
        private static string _conventionsStamp;

        /// <summary>
        /// Commands that create or modify code or assets, where the house conventions apply. Kept exactly as
        /// the Node server it replaces had it; the classification is load-bearing and inventing a new one would
        /// silently change which results carry the reminder.
        /// </summary>
        private static readonly Regex _authoringCommand = new Regex(
            @"usharp|create|modify|component-add|script-|batch-edit|prefab|material|set-reference|unity-event-set-string|gameobject-(duplicate|destroy|set-parent)|rename-object|reparent-object|set-(position|rotation|scale|sibling-index|transform)|save-(assets|scenes)|scene-save|assets-(copy|create|delete|move)|object-modify|execute-menu",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// Prepended to the result of an authoring command, so the mandate to read the conventions stays in
        /// the agent's live context after a compaction has wiped its memory of the system prompt. Read-only
        /// queries stay clean.
        /// </summary>
        /// <summary>
        /// Built from the located index rather than written down: Conduit's folder can be renamed, moved, or
        /// shipped as a package, and a reminder naming a path that no longer exists sends every agent that
        /// obeys it to read nothing.
        /// </summary>
        internal static string CONVENTIONS_REMINDER
        {
            get
            {
                string index = ConduitSkills.IndexDocumentPath();
                return "[Conduit] Authoring command. If you have not read "
                    + (index.Length == 0 ? "Conduit's conventions index" : index)
                    + " in this context (including since the last compaction/summary), read it in full now "
                    + "before continuing.";
            }
        }

        /// <summary>True for a command that creates or modifies code or assets.</summary>
        /// <param name="name">Verb or command name.</param>
        /// <returns>True when the conventions reminder applies.</returns>
        internal static bool IsAuthoringCommand(string name)
        {
            return _authoringCommand.IsMatch(name ?? string.Empty);
        }

        /// <summary>
        /// The instructions served at initialize, or a short fallback when none were readable.
        ///
        /// Rebuilt when a convention document has been edited since the snapshot was taken. That check is
        /// three file stats and runs once per client connection, and it is the difference between serving
        /// this project's current rules and serving whatever they said at the last compile.
        /// </summary>
        internal static string Instructions
        {
            get
            {
                string path;
                string stamp;
                lock (_gate)
                {
                    path = _conventionsPath;
                    stamp = _conventionsStamp;
                }

                // Composed outside the lock: it reads files, and holding the catalogue lock across disk IO
                // would stall a publish behind a client's connection.
                if (!string.IsNullOrEmpty(path))
                {
                    var current = ConventionsStamp(path);
                    if (!string.Equals(current, stamp, StringComparison.Ordinal))
                    {
                        var rebuilt = BuildInstructions(path);
                        lock (_gate)
                        {
                            _instructions = rebuilt;
                            _conventionsStamp = current;
                            return _instructions;
                        }
                    }
                }

                lock (_gate)
                {
                    return _instructions ?? "Unity editor bridge for this project.";
                }
            }
        }

        /// <summary>
        /// A comparable stamp over every document the instructions are built from. A deleted file stamps as
        /// its own absence rather than leaving the stamp unchanged, so removing one is a change like any
        /// other.
        /// </summary>
        /// <param name="conventionsPath">Where the conventions index lives.</param>
        /// <returns>A value that differs whenever any source document has changed.</returns>
        private static string ConventionsStamp(string conventionsPath)
        {
            var stamp = new StringBuilder();
            foreach (var file in new[] { conventionsPath, "AGENTS.md", "CONDUIT.md" })
            {
                try
                {
                    stamp.Append(File.Exists(file)
                        ? File.GetLastWriteTimeUtc(file).Ticks.ToString(CultureInfo.InvariantCulture)
                        : "absent");
                }
                catch (Exception)
                {
                    // An unreadable document must not take the served instructions down with it.
                    stamp.Append("unreadable");
                }

                stamp.Append('|');
            }

            return stamp.ToString();
        }

        #endregion

        #region Publication

        /// <summary>True once a catalogue is published and the transport may serve tool calls.</summary>
        internal static bool IsPublished
        {
            get { lock (_gate) { return _tools != null; } }
        }

        /// <summary>Why publication failed, or null when it succeeded or has not been attempted.</summary>
        internal static string PublicationError
        {
            get { lock (_gate) { return _error; } }
        }

        /// <summary>Stable digest of the published catalogue, or "unpublished".</summary>
        internal static string Revision
        {
            get { lock (_gate) { return _revision ?? "unpublished"; } }
        }

        /// <summary>Number of published tools, or 0.</summary>
        internal static int ToolCount
        {
            get { lock (_gate) { return _tools == null ? 0 : _tools.Count; } }
        }

        /// <summary>
        /// The published tools array. Returned as a deep clone: a caller that mutated the live snapshot would
        /// change what every other client is told, and this is the one object every client shares.
        /// </summary>
        /// <returns>A copy of the tools array, or an empty array when unpublished.</returns>
        internal static JArray ToolsSnapshot()
        {
            lock (_gate) { return _tools == null ? new JArray() : (JArray)_tools.DeepClone(); }
        }

        /// <summary>Finds the command builder for a tool.</summary>
        /// <param name="toolName">Published tool name.</param>
        /// <param name="build">Receives the builder.</param>
        /// <returns>True when the tool is published.</returns>
        internal static bool TryGetBuilder(string toolName, out Func<JObject, string> build)
        {
            lock (_gate)
            {
                build = null;
                return _builders != null && _builders.TryGetValue(toolName, out build);
            }
        }

        /// <summary>
        /// Finds the published input schema for a tool. Served from a map built with the snapshot rather than
        /// by scanning the tools array, because validation runs on every call and a linear scan over the whole
        /// catalogue per request is a cost paid for nothing.
        ///
        /// Returned as a deep clone. An earlier revision handed out the live object with a comment asking
        /// callers not to mutate it, which is not immutability - it is a convention, and one validator bug
        /// would have silently rewritten the published contract for every client. Cloning costs one small
        /// schema per call, not the catalogue.
        /// </summary>
        /// <param name="toolName">Published tool name.</param>
        /// <param name="schema">Receives a private copy of the schema.</param>
        /// <returns>True when the tool is published.</returns>
        internal static bool TryGetSchema(string toolName, out JObject schema)
        {
            lock (_gate)
            {
                schema = null;
                if (_schemas == null) return false;

                JObject published;
                if (!_schemas.TryGetValue(toolName, out published)) return false;

                schema = (JObject)published.DeepClone();
                return true;
            }
        }

        /// <summary>Finds the canonical bridge verb behind a tool name.</summary>
        /// <param name="toolName">Published tool name.</param>
        /// <param name="verb">Receives the verb.</param>
        /// <returns>True when the tool is published.</returns>
        internal static bool TryGetVerb(string toolName, out string verb)
        {
            lock (_gate)
            {
                verb = null;
                return _verbs != null && _verbs.TryGetValue(toolName, out verb);
            }
        }

        /// <summary>
        /// Builds and publishes the catalogue from the bridge's own live command list. Runs on the main
        /// thread during domain load, before the transport accepts a request.
        /// </summary>
        internal static void Publish()
        {
            lock (_gate)
            {
                if (_tools != null) return;

                try
                {
                    var commands = JArray.Parse(ConduitBridge.ExecuteImmediate("commands"));
                    var builders = new Dictionary<string, Func<JObject, string>>(StringComparer.Ordinal);
                    var tools = ConduitToolCatalog.BuildMigratedCatalog(commands, builders);

                    var verbs = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var entry in commands)
                    {
                        var verb = (string)((JObject)entry)["verb"];
                        if (string.IsNullOrEmpty(verb)) continue;

                        verbs[verb] = verb;
                    }

                    // conduit-run is the escape hatch for a verb added after this client connected, so it is
                    // its own verb rather than an alias of one.
                    verbs["conduit-run"] = "conduit-run";

                    AppendJobStatusTool(tools, builders, verbs);

                    // The map holds its own copies rather than aliasing the tools array, so neither side can
                    // be reached through the other.
                    var schemas = new Dictionary<string, JObject>(StringComparer.Ordinal);
                    foreach (var tool in tools)
                    {
                        schemas[(string)tool["name"]] = (JObject)tool["inputSchema"].DeepClone();
                    }

                    _tools = tools;
                    _builders = builders;
                    _verbs = verbs;
                    _schemas = schemas;
                    _revision = DigestOf(tools);
                    // The path is resolved here, on the main thread, because it goes through the
                    // AssetDatabase. Everything after it is plain file IO, which is what lets the getter
                    // refresh the text later from the listener thread.
                    _conventionsPath = ConduitSkills.IndexDocumentPath();
                    _conventionsStamp = ConventionsStamp(_conventionsPath);
                    _instructions = BuildInstructions(_conventionsPath);
                    _error = null;
                }
                catch (Exception ex)
                {
                    _tools = null;
                    _builders = null;
                    _verbs = null;
                    _schemas = null;
                    _revision = null;
                    _instructions = null;
                    // Held on the Error property, where a client that asks is told. Not logged: Conduit is
                    // silent, and a console message would only reach whoever happened to be looking.
                    _error = ex.GetType().Name + ": " + ex.Message;
                }
            }
        }

        /// <summary>
        /// Appends the one tool the transport owns rather than the bridge. It reads a durable job's record, so
        /// it has no bridge verb behind it and never reaches the main thread.
        /// </summary>
        /// <param name="tools">Catalogue being built.</param>
        /// <param name="builders">Builder map being built.</param>
        /// <param name="verbs">Verb map being built.</param>
        private static void AppendJobStatusTool(
            JArray tools,
            Dictionary<string, Func<JObject, string>> builders,
            Dictionary<string, string> verbs)
        {
            tools.Add(new JObject
            {
                ["name"] = "job_status",
                ["description"] =
                    "Read the durable record of a job created by a command that had to survive a domain reload. " +
                    "MCP calls normally keep their response across that reload; this reads the job when the wait " +
                    "budget expired, socket handoff was unavailable, or a raw /command caller received only its id.",
                ["inputSchema"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject
                    {
                        ["jobId"] = new JObject
                        {
                            ["type"] = "string",
                            ["minLength"] = 1,
                            ["description"] = "Job identifier returned when the command was admitted.",
                        },
                    },
                    ["required"] = new JArray { "jobId" },
                    ["additionalProperties"] = false,
                },
            });

            // It has no command line, so its builder refuses rather than inventing one. The transport answers
            // this tool directly and never asks for a command.
            builders["job_status"] = arguments =>
            {
                throw new InvalidOperationException("job_status is answered by the transport and has no bridge command");
            };

            verbs["job_status"] = "job_status";
        }

        /// <summary>
        /// Assembles the instructions served at initialize, reading the convention documents from disk while
        /// the main thread is ours. These are not decoration: the hard stops live in that file, and an agent
        /// that never reads it edits prefabs through scene instances and polls status after blocking verbs.
        /// The mandatory directive goes first so a client that truncates long instructions still surfaces it.
        /// </summary>
        /// <param name="conventionsPath">
        /// Where the conventions index lives, resolved by the caller. Passed in rather than looked up here
        /// because resolving it goes through the AssetDatabase, and this is also called from the thread that
        /// serves a client connection.
        /// </param>
        /// <returns>The assembled instructions.</returns>
        private static string BuildInstructions(string conventionsPath)
        {
            var parts = new List<string>();

            parts.Add(
                "MANDATORY FIRST STEP - before ANY Conduit tool call, and AGAIN immediately after any context " +
                "compaction/summary: read the FULL conventions file at " + conventionsPath +
                " before editing code or Unity assets. Never skip it; treat a fresh or compacted context as a new session.");
            try
            {
                if (File.Exists(conventionsPath))
                {
                    var text = File.ReadAllText(conventionsPath, Encoding.UTF8).Trim();
                    if (text.Length > 0)
                    {
                        parts.Add(
                            "Before editing code or Unity assets through this bridge, read the project-local conventions file:\n" +
                            "- Unity asset path: " + conventionsPath + "\n\n" + text);
                    }
                }
            }
            catch
            {
                // An unreadable conventions file must not take the whole catalogue down with it.
            }

            // Optional per-project additions from the project root. First one found wins, as the Node server
            // did, so a project carries one voice rather than two.
            foreach (var name in new[] { "AGENTS.md", "CONDUIT.md" })
            {
                try
                {
                    if (!File.Exists(name)) continue;

                    var text = File.ReadAllText(name, Encoding.UTF8).Trim();
                    if (text.Length == 0) continue;

                    parts.Add("--- This project's own conventions (" + name + ") ---\n" + text);
                    break;
                }
                catch
                {
                    // Same reasoning as above.
                }
            }

            return string.Join("\n\n", parts.ToArray());
        }

        /// <summary>
        /// Stable digest of a catalogue, so a client can tell whether the copy it cached is still the one
        /// being served. The full digest is the protocol identity and is never truncated here - a display may
        /// shorten it, but a shortened value must not be what two parties compare.
        /// </summary>
        /// <param name="tools">The tools array.</param>
        /// <returns>Full lowercase hex SHA-256.</returns>
        private static string DigestOf(JArray tools)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(tools.ToString(Formatting.None)));
                var text = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        #endregion
    }
}
