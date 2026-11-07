/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Gate for the published MCP catalogue snapshot.
//
// The interesting checks here are the immutability ones. Everything else confirms the catalogue is what we
// think it is; those confirm it cannot be changed out from under a connected client by a bug somewhere else
// in this assembly. They work by actually mutating what the catalogue handed back and then proving the
// catalogue did not move - a test that merely asserted "we return a clone" would pass on a comment.
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit.Tests
{
    /// <summary>Proves the published catalogue is complete, correctly shaped, and genuinely immutable.</summary>
    internal static class ConduitMcpCatalogGate
    {
        /// <summary>
        /// Tools the catalogue must publish: one per bridge verb under its canonical spelling, plus
        /// conduit-run and the job_status infrastructure tool. 225 verbs in a VRChat Worlds project, then
        /// those two, and the 74 ProBuilder verbs on top wherever that optional package is installed - so the
        /// figure is conditional for the same reason the verbs are.
        /// </summary>
#if CONDUIT_PROBUILDER
        private const int WORLDS_TOOL_COUNT = 301;
#else
        private const int WORLDS_TOOL_COUNT = 227;
#endif

        /// <summary>
        /// Worlds-only verbs the catalogue leaves out where the Worlds SDK is absent: UdonSharp authoring (3),
        /// compiled-Udon inspection (3) and the vrc-* player and build verbs (17).
        /// </summary>
        private const int WORLDS_ONLY_TOOL_COUNT = 23;

        /// <summary>Tools this project must publish: every verb in a Worlds project, the rest without one.</summary>
        private static int ExpectedToolCount =>
            ConduitBridge.IsWorldsProject ? WORLDS_TOOL_COUNT : WORLDS_TOOL_COUNT - WORLDS_ONLY_TOOL_COUNT;

        /// <summary>A full SHA-256 in lowercase hex, which is what the protocol identity must be.</summary>
        private static readonly Regex _fullDigest = new Regex("^[0-9a-f]{64}$");

        /// <summary>
        /// One identifier-shaped run in a signature. A hyphen is INSIDE the token on purpose, so
        /// "listenerComponent-or-empty" reads as one name rather than lending its leading word to a
        /// property that the signature never actually offers.
        /// </summary>
        private static readonly Regex _signatureToken = new Regex("[A-Za-z][A-Za-z0-9_-]*");

        /// <summary>Records one check's result.</summary>
        /// <param name="report">Report sink.</param>
        /// <param name="label">Check name.</param>
        /// <param name="ok">Whether it held.</param>
        /// <param name="detail">Observed value, printed either way.</param>
        /// <returns>The same result, for accumulation.</returns>
        private static bool Check(StringBuilder report, string label, bool ok, string detail)
        {
            report.AppendLine((ok ? "  PASS  " : "  FAIL  ") + label + "  ->  " + detail);
            return ok;
        }

        /// <summary>
        /// Every argument name a caller could read off a tool's published signature, sanitised with the
        /// generator's own sanitiser so the two cannot drift apart.
        /// <para>
        /// The set is deliberately permissive: a name appearing anywhere in the signature counts, because
        /// a repair may legitimately split one group into several properties, as udon-exposure's mode and
        /// path both come out of "[| mode:types|dump &lt;path&gt;|diff &lt;path&gt;]". What it will not
        /// forgive is a name the signature never spells at all.
        /// </para>
        /// </summary>
        /// <param name="description">The tool's published description, which carries its signature verbatim.</param>
        /// <returns>Every derivable name; empty for a verb that takes no arguments.</returns>
        private static HashSet<string> DerivableArgumentNames(string description)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var text = description ?? string.Empty;
            var marker = text.IndexOf("Signature: ", StringComparison.Ordinal);
            if (marker < 0) return names;

            var start = marker + "Signature: ".Length;
            var stop = text.IndexOf(" NOTE:", start, StringComparison.Ordinal);
            var argSpec = stop < 0 ? text.Substring(start) : text.Substring(start, stop - start);

            foreach (Match token in _signatureToken.Matches(argSpec))
            {
                names.Add(ConduitToolCatalog.SanitizeArgumentName(token.Value, token.Value));
            }

            return names;
        }

        /// <summary>Runs every catalogue check and reports.</summary>
        [MenuItem("Tools/Conduit Gates/Verify MCP catalogue")]
        private static void Verify()
        {
            var report = new StringBuilder("MCP catalogue - production ConduitMcpCatalog\n");
            var ok = true;

            ok &= Check(report, "catalogue is published", ConduitMcpCatalog.IsPublished,
                "published=" + ConduitMcpCatalog.IsPublished + " error=" + (ConduitMcpCatalog.PublicationError ?? "none"));

            if (!ConduitMcpCatalog.IsPublished)
            {
                Debug.LogError("[CatalogueGate] " + report + "\nFAIL - nothing published to check");
                return;
            }

            var tools = ConduitMcpCatalog.ToolsSnapshot();
            ok &= Check(report, "tool count", tools.Count == ExpectedToolCount,
                tools.Count + " (expected " + ExpectedToolCount + ")");

            var revision = ConduitMcpCatalog.Revision;
            ok &= Check(report, "revision is a full SHA-256", _fullDigest.IsMatch(revision),
                revision.Length + " chars: " + revision);

            // The infrastructure tool is underscore-named and stands alone. A hyphen variant alongside it
            // would publish an extra tool and quietly break the count both sides gate on.
            JObject jobStatus = null;
            var hyphenVariant = false;
            foreach (var tool in tools)
            {
                var name = (string)tool["name"];
                if (name == "job_status") jobStatus = (JObject)tool;
                if (name == "job-status") hyphenVariant = true;
            }

            ok &= Check(report, "job_status is published", jobStatus != null, jobStatus == null ? "absent" : "present");
            ok &= Check(report, "no hyphenated job-status duplicate", !hyphenVariant, hyphenVariant ? "found" : "absent");

            if (jobStatus != null)
            {
                var schema = (JObject)jobStatus["inputSchema"];
                var required = schema["required"] as JArray;
                ok &= Check(report, "job_status schema requires jobId and is closed",
                    required != null && required.Count == 1 && (string)required[0] == "jobId"
                    && (bool)schema["additionalProperties"] == false
                    && (string)schema["properties"]["jobId"]["type"] == "string",
                    schema.ToString(Formatting.None));
            }

            JObject scriptRead;
            var scriptReadFound = ConduitMcpCatalog.TryGetSchema("script-read", out scriptRead);
            var scriptReadProperties = scriptReadFound ? scriptRead["properties"] as JObject : null;
            var startLine = scriptReadProperties == null ? null : scriptReadProperties["startLine"] as JObject;
            var count = scriptReadProperties == null ? null : scriptReadProperties["count"] as JObject;
            ok &= Check(report, "script-read line arguments are integers",
                startLine != null && count != null
                && (string)startLine["type"] == "integer"
                && (string)count["type"] == "integer",
                scriptReadFound ? scriptRead.ToString(Formatting.None) : "schema not found");

            bool historyFound = ConduitMcpCatalog.TryGetSchema("file-history", out JObject historySchema);
            ok &= Check(report, "file-history publishes its named view enum",
                historyFound && (string)historySchema["properties"]?["view"]?["enum"]?[0] == "content"
                && (string)historySchema["properties"]?["view"]?["enum"]?[1] == "diff",
                historyFound ? historySchema.ToString(Formatting.None) : "absent");
            bool restoreFound = ConduitMcpCatalog.TryGetSchema("file-restore", out JObject restoreSchema);
            var restoreRequired = restoreFound ? restoreSchema["required"] as JArray : null;
            ok &= Check(report, "file-restore requires path, revision and current hash",
                restoreRequired != null && restoreRequired.Count == 3
                && (string)restoreRequired[0] == "assetPath" && (string)restoreRequired[1] == "revision"
                && (string)restoreRequired[2] == "expectedCurrentSha256",
                restoreFound ? restoreSchema.ToString(Formatting.None) : "absent");

            // The report option is optional and closes the command. Left out, a call builds exactly the text
            // it always did, with a pipe inside the payload left unescaped; given, it is one final section; an
            // unknown mode is refused by the published schema before the bridge sees it.
            bool batchFound = ConduitMcpCatalog.TryGetSchema("udon-batch-edit", out JObject batchSchema);
            var batchMode = batchFound ? batchSchema["properties"]?["report"] as JObject : null;
            var batchRequired = batchFound ? batchSchema["required"] as JArray : null;
            var batchEdits = new JArray { new JObject { ["file_path"] = "Assets/Probe.cs", ["old_string"] = "a | b", ["new_string"] = "c" } };
            var batchCommand = "udon-batch-edit | " + batchEdits.ToString(Formatting.None);
            var batchPlain = "no builder";
            var batchSummary = "no builder";
            if (ConduitMcpCatalog.TryGetBuilder("udon-batch-edit", out Func<JObject, string> buildBatch))
            {
                batchPlain = buildBatch(new JObject { ["edits"] = batchEdits });
                batchSummary = buildBatch(new JObject { ["edits"] = batchEdits, ["report"] = "summary" });
            }

            string acceptError = "schema absent";
            string refuseError = "schema absent";
            bool batchAccepts = batchFound && ConduitJsonSchemaValidator.Validate(
                batchSchema, new JObject { ["edits"] = batchEdits, ["report"] = "summary" }, out acceptError);
            bool batchRefuses = batchFound && !ConduitJsonSchemaValidator.Validate(
                batchSchema, new JObject { ["edits"] = batchEdits, ["report"] = "brief" }, out refuseError);
            ok &= Check(report, "udon-batch-edit takes an optional report enum as its last section",
                batchMode != null
                && (string)batchMode["enum"]?[0] == "full" && (string)batchMode["enum"]?[1] == "summary"
                && batchRequired != null && batchRequired.Count == 1 && (string)batchRequired[0] == "edits"
                && batchPlain == batchCommand && batchSummary == batchCommand + " | summary"
                && batchAccepts && batchRefuses,
                "enum=" + (batchMode?["enum"]?.ToString(Formatting.None) ?? "absent") + " built=" + batchSummary
                + " accept=" + (batchAccepts ? "ok" : acceptError) + " brief=" + refuseError);

            // A tool name resolves to the bridge verb behind it, and the map carries the canonical spelling
            // alone: a snake_case twin per verb doubles the surface every client holds and reaches nothing
            // the canonical name does not already reach.
            string canonical;
            var canonicalOk = ConduitMcpCatalog.TryGetVerb("rename-object", out canonical) && canonical == "rename-object";
            string aliased;
            var aliasAbsent = !ConduitMcpCatalog.TryGetVerb("rename_object", out aliased);
            ok &= Check(report, "the verb map carries the canonical spelling only", canonicalOk && aliasAbsent,
                "rename-object->" + canonical + "  rename_object->" + (aliasAbsent ? "absent" : aliased));

            string runVerb;
            ok &= Check(report, "conduit-run is its own verb, not an alias",
                ConduitMcpCatalog.TryGetVerb("conduit-run", out runVerb) && runVerb == "conduit-run",
                "conduit-run->" + runVerb);

            // Every published property name must be readable off the published signature, because the
            // description carries that signature verbatim and a client is entitled to build from it. A
            // repair may RETYPE what the signature names; renaming it publishes two contracts and the tool
            // then refuses its own documented call. This is the check that was absent when udon-batch-edit
            // shipped "edits" under a signature reading "<| editsJson>", and it compares NAMES only.
            var underivable = new List<string>();
            var unsignatured = new List<string>();
            var checkedTools = 0;
            foreach (var tool in tools)
            {
                var toolName = (string)tool["name"];
                var description = (string)tool["description"] ?? string.Empty;

                // The server's own tools, conduit-run and job_status, are hand-written rather than
                // generated from a bridge signature, so they have no signature for a name to derive from.
                if (!description.StartsWith("Unity editor bridge command ", StringComparison.Ordinal)) continue;

                var schema = tool["inputSchema"] as JObject;
                var properties = schema == null ? null : schema["properties"] as JObject;
                if (properties == null || properties.Count == 0) continue;

                // A generated description carries either a signature or "Takes no arguments", so a verb
                // publishing properties under neither has lost the signature its schema was built from -
                // which would otherwise exempt it from this check silently.
                if (description.IndexOf("Signature: ", StringComparison.Ordinal) < 0)
                {
                    unsignatured.Add(toolName);
                    continue;
                }

                checkedTools++;
                var derivable = DerivableArgumentNames(description);
                foreach (var property in properties.Properties())
                {
                    if (!derivable.Contains(property.Name)) underivable.Add(toolName + "." + property.Name);
                }
            }

            ok &= Check(report, "every published property name derives from its signature",
                underivable.Count == 0,
                underivable.Count == 0
                    ? checkedTools + " tools with arguments, 0 underivable names"
                    : underivable.Count + " underivable: " + string.Join(", ", underivable.ToArray()));

            ok &= Check(report, "every verb with arguments publishes a signature", unsignatured.Count == 0,
                unsignatured.Count == 0
                    ? "all signatured"
                    : unsignatured.Count + " without one: " + string.Join(", ", unsignatured.ToArray()));

            // Immutability, proven by mutation rather than asserted. Both of these would have passed before
            // the snapshot actually cloned, because the old code returned the live object with a comment.
            JObject firstFetch;
            var fetched = ConduitMcpCatalog.TryGetSchema("rename-object", out firstFetch);
            if (fetched)
            {
                firstFetch["properties"]["__gate_injected"] = "tampered";
                firstFetch["additionalProperties"] = true;

                JObject secondFetch;
                ConduitMcpCatalog.TryGetSchema("rename-object", out secondFetch);
                var clean = secondFetch["properties"]["__gate_injected"] == null
                    && (bool)secondFetch["additionalProperties"] == false;
                ok &= Check(report, "mutating a fetched schema does not change the published one", clean,
                    clean ? "second fetch is clean" : "second fetch was tampered");
            }
            else
            {
                ok &= Check(report, "mutating a fetched schema does not change the published one", false,
                    "rename-object schema not found");
            }

            tools[0]["name"] = "__gate_tampered";
            var refetched = ConduitMcpCatalog.ToolsSnapshot();
            ok &= Check(report, "mutating a tools snapshot does not change the published one",
                (string)refetched[0]["name"] != "__gate_tampered",
                "refetched[0]=" + (string)refetched[0]["name"]);

            ok &= Check(report, "revision is stable across reads", ConduitMcpCatalog.Revision == revision,
                ConduitMcpCatalog.Revision == revision ? "stable" : "changed");

            report.AppendLine();
            report.AppendLine(ok ? "PASS - production MCP catalogue" : "FAIL - see the checks above");
            if (ok) Debug.Log("[CatalogueGate] " + report);
            else Debug.LogError("[CatalogueGate] " + report);
        }
    }
}
