/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Gate for the property this whole transport exists to provide: a command that triggers a domain reload
// survives independently of its admission response, and its result remains collectable afterwards.
//
// It cannot run inside one menu invocation, because the reload tears down the calling assembly mid-check.
// So it is two halves - prepare admits the job and records a marker, the continuation runs on the next
// domain and verifies what survived. Anything that only checks one half proves nothing: the interesting
// failure is precisely the boundary between them.
//
// The marker is written only after a 202 admission. A marker without an admitted job is a failed prepare,
// not something to continue from, and the continuation says so rather than quietly reporting a pass.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit.Tests
{
    /// <summary>Proves a compiling command survives its own domain reload as a durable job.</summary>
    [InitializeOnLoad]
    internal static class ConduitTransportReloadGate
    {
        #region Constants

        /// <summary>This file's path within Conduit, resolved against the located root rather than written down.</summary>
        private const string SCRATCH_SCRIPT_SUFFIX = "/Editor/Tests/ConduitTransportReloadGate.cs";

        /// <summary>Seconds the continuation waits for the ledger to become ready after the reload.</summary>
        private const double READY_TIMEOUT_SECONDS = 60.0;

        #endregion

        #region Private state

        /// <summary>Marker describing the job this gate is waiting to see survive.</summary>
        private static readonly string _markerPath =
            Path.Combine("Library", "ItsRenji", "ConduitTransportGate", "reload-marker.json");

        /// <summary>When the continuation gives up waiting for readiness.</summary>
        private static double _deadline;

        #endregion

        #region Prepare

        /// <summary>
        /// Admits a compiling command over HTTP and records what must survive. The reimport it admits is what
        /// triggers the reload, so this method's own assembly is torn down shortly after it returns.
        /// </summary>
        [MenuItem("Tools/Conduit Gates/Prepare transport reload recovery")]
        private static void Prepare()
        {
            string descriptorError;
            var descriptor = ConduitHttpTransportGate.ReadDescriptor(out descriptorError);
            if (descriptor == null)
            {
                Debug.LogError("[ReloadGate] cannot prepare: " + descriptorError);
                return;
            }

            var session = "reload-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            const string request = "req-reload-1";
            var scratchScript = ScratchScript();
            if (scratchScript.Length == 0)
            {
                Debug.LogError("[ReloadGate] cannot prepare: Conduit's own folder could not be located.");
                return;
            }

            var command = "reimport " + scratchScript;

            // Records that already exist are not this gate's to account for. Another client driving Conduit
            // concurrently leaves its own durable jobs in the same ledger, so asserting on the ledger TOTAL
            // makes this gate fail for work it never did. Snapshot first, then judge only the difference.
            var preexistingIds = new JArray();
            foreach (var id in RecordIds()) preexistingIds.Add(id);

            var admitted = ConduitHttpTransportGate.SendDurable(descriptor, command, session, request);
            var jobId = ConduitHttpTransportGate.JobIdOf(admitted.Body);
            if (admitted.Status != 202 || string.IsNullOrEmpty(jobId))
            {
                // No marker is written here on purpose. A marker with no admitted job behind it would make the
                // continuation report on work that was never accepted.
                Debug.LogError("[ReloadGate] admission did not return 202 with a job id, so nothing was prepared: "
                    + admitted.Status + " " + ConduitHttpTransportGate.Outcome(admitted.Body));
                return;
            }

            var marker = new JObject
            {
                ["jobId"] = jobId,
                ["session"] = session,
                ["request"] = request,
                ["command"] = command,
                ["port"] = descriptor.Port,
                ["token"] = descriptor.Token,
                ["identity"] = descriptor.Identity,
                ["preparedUtc"] = DateTime.UtcNow.ToString("O"),
                ["preexisting"] = preexistingIds,
            };

            Directory.CreateDirectory(Path.GetDirectoryName(_markerPath));
            File.WriteAllText(_markerPath, marker.ToString(), Encoding.UTF8);

            Debug.Log("[ReloadGate] prepared job " + jobId + " on port " + descriptor.Port +
                ". The reimport it admitted will reload this domain; the continuation reports afterwards.");
        }

        #endregion

        #region Continuation

        /// <summary>Arms the continuation when a marker survived into this domain.</summary>
        static ConduitTransportReloadGate()
        {
            // Import workers load this assembly too; see ConduitBridge's static constructor. A gate that
            // ran in a worker would assert against a domain that is not the one under test.
            if (UnityEngine.Application.isBatchMode) return;

            if (!File.Exists(_markerPath)) return;

            // Not EditorApplication.delayCall. Registered from a static constructor it silently never fires,
            // which has cost this project two separate debugging sessions; a self-unsubscribing update tick
            // is the shape that actually runs.
            _deadline = EditorApplication.timeSinceStartup + READY_TIMEOUT_SECONDS;
            EditorApplication.update -= Continue;
            EditorApplication.update += Continue;
        }

        /// <summary>Waits for the ledger to be ready, then verifies everything that had to survive.</summary>
        private static void Continue()
        {
            if (!ConduitJobLedger.IsReady && EditorApplication.timeSinceStartup < _deadline) return;
            EditorApplication.update -= Continue;

            var report = new StringBuilder("Transport reload recovery - production ConduitHttpTransport\n");
            var ok = true;

            JObject marker;
            try { marker = JObject.Parse(File.ReadAllText(_markerPath, Encoding.UTF8)); }
            catch (Exception ex)
            {
                Debug.LogError("[ReloadGate] marker unreadable: " + ex.Message);
                return;
            }

            try { File.Delete(_markerPath); } catch { }

            ok &= Check(report, "ledger became ready after the reload", ConduitJobLedger.IsReady,
                "ready=" + ConduitJobLedger.IsReady);

            string descriptorError;
            var descriptor = ConduitHttpTransportGate.ReadDescriptor(out descriptorError);
            if (descriptor == null)
            {
                Debug.LogError("[ReloadGate] " + report + "  FAIL  descriptor unreadable after reload: " + descriptorError);
                return;
            }

            // A client that was talking to this editor before the reload must still be able to talk to it
            // after. A rotated bearer or a moved port would be a silent disconnection.
            ok &= Check(report, "port unchanged across the reload",
                descriptor.Port == (int)marker["port"],
                (int)marker["port"] + " -> " + descriptor.Port);
            ok &= Check(report, "bearer unchanged across the reload",
                descriptor.Token == (string)marker["token"],
                descriptor.Token == (string)marker["token"] ? "same" : "rotated");

            var jobId = (string)marker["jobId"];
            var status = ConduitHttpTransportGate.Send(descriptor, "GET", "/job/status?id=" + jobId,
                descriptor.Identity, descriptor.Token, null, false, ConduitHttpTransportGate.DIRECT_TIMEOUT_MS);

            JObject record = null;
            try { record = (JObject)JObject.Parse(status.Body)["record"]; } catch { }

            ok &= Check(report, "job survived the reload and is readable",
                status.Status == 200 && record != null,
                status.Status + " " + ConduitHttpTransportGate.Outcome(status.Body));

            if (record != null)
            {
                ok &= Check(report, "job completed", (string)record["state"] == "completed",
                    "state=" + (string)record["state"]);

                var text = (string)record["outcome"]?["text"] ?? string.Empty;
                ok &= Check(report, "result carries the post-compile report",
                    text.IndexOf("compile finished", StringComparison.OrdinalIgnoreCase) >= 0
                    && text.IndexOf("no errors", StringComparison.OrdinalIgnoreCase) >= 0,
                    text.Replace("\r", string.Empty).Replace("\n", " | "));
            }

            // The retry is the point of durability: a raw-command client asks again with the same identity and
            // gets the stored result rather than triggering a second compile.
            var retry = ConduitHttpTransportGate.SendDurable(
                descriptor, (string)marker["command"], (string)marker["session"], (string)marker["request"]);
            ok &= Check(report, "retry after the reload returns the stored result",
                retry.Status == 200 && ConduitHttpTransportGate.JobIdOf(retry.Body) == jobId,
                retry.Status + " " + ConduitHttpTransportGate.Outcome(retry.Body)
                + " sameJob=" + (ConduitHttpTransportGate.JobIdOf(retry.Body) == jobId));

            // A retry that minted a second record would mean the work could be done twice. Counted against the
            // records this gate created, never against the ledger total, because a second client's durable jobs
            // live in the same ledger and are none of this gate's business.
            var preexisting = PreexistingIds(marker);
            var newReimports = CountNewRecords("reimport", preexisting);
            ok &= Check(report, "retry did not mint a duplicate record", newReimports == 1,
                newReimports + " new reimport record(s)");

            string cleanupError;
            var cleaned = ConduitJobLedger.CleanupTerminalGateJobs(new List<string> { jobId }, out cleanupError);
            ok &= Check(report, "gate removes the job it created", cleaned,
                cleaned ? jobId : cleanupError);

            // The ledger total is reported beside the owned count rather than asserted on, so a reader can see
            // foreign activity without it failing this gate.
            var leftBehind = CountNewRecords(null, preexisting);
            ok &= Check(report, "gate left no records of its own", leftBehind == 0,
                leftBehind + " of its own, " + CountRecords(null) + " in the ledger overall");

            report.AppendLine();
            report.AppendLine(ok ? "PASS - a compiling command survived its own reload" : "FAIL - see the checks above");
            if (ok) Debug.Log("[ReloadGate] " + report);
            else Debug.LogError("[ReloadGate] " + report);
        }

        #endregion

        #region Helpers

        /// <summary>
        /// The script reimported to trigger a real compile. It is this gate's own file: reimporting it crosses
        /// exactly the same compile and domain-reload boundary as any other compilation input, and pointing at
        /// a file in a scratch folder made a permanent gate depend on a directory scheduled for deletion. The
        /// install location is resolved rather than written down, because Conduit also ships as a package and a
        /// path naming one layout still compiles under the other while pointing at nothing.
        /// </summary>
        /// <returns>Project-relative path to this file, or an empty string when Conduit cannot be located.</returns>
        private static string ScratchScript()
        {
            var root = ConduitSkills.ConduitRoot();
            return root.Length == 0 ? string.Empty : root + SCRATCH_SCRIPT_SUFFIX;
        }

        /// <summary>Ids of every record currently in the ledger, used to scope this gate to its own work.</summary>
        /// <returns>The id set.</returns>
        private static HashSet<string> RecordIds()
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var directory = ConduitJobLedger.ContractGateDirectory;
            if (!Directory.Exists(directory)) return ids;

            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                ids.Add(Path.GetFileNameWithoutExtension(path));
            }

            return ids;
        }

        /// <summary>Reads the pre-existing id set that the prepare step recorded in the marker.</summary>
        /// <param name="marker">The marker that survived the reload.</param>
        /// <returns>The id set, empty when the marker carries none.</returns>
        private static HashSet<string> PreexistingIds(JObject marker)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var stored = marker["preexisting"] as JArray;
            if (stored == null) return ids;

            foreach (var id in stored) ids.Add((string)id);
            return ids;
        }

        /// <summary>Counts records this gate created, ignoring anything that was already in the ledger.</summary>
        /// <param name="verb">Verb to match, or null to count every new record.</param>
        /// <param name="preexisting">Ids that existed before this gate admitted its job.</param>
        /// <returns>The count.</returns>
        private static int CountNewRecords(string verb, HashSet<string> preexisting)
        {
            var directory = ConduitJobLedger.ContractGateDirectory;
            if (!Directory.Exists(directory)) return 0;

            var count = 0;
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                if (preexisting.Contains(Path.GetFileNameWithoutExtension(path))) continue;
                if (verb == null) { count++; continue; }

                try
                {
                    if ((string)JObject.Parse(File.ReadAllText(path, Encoding.UTF8))["verb"] == verb) count++;
                }
                catch
                {
                    // A record that cannot be parsed is not the one being counted.
                }
            }

            return count;
        }

        /// <summary>Counts durable records on disk, optionally for one verb.</summary>
        /// <param name="verb">Verb to count, or null for every record.</param>
        /// <returns>The record count.</returns>
        private static int CountRecords(string verb)
        {
            var directory = ConduitJobLedger.ContractGateDirectory;
            if (!Directory.Exists(directory)) return 0;

            var count = 0;
            foreach (var path in Directory.GetFiles(directory, "*.json"))
            {
                if (verb == null) { count++; continue; }

                try
                {
                    if ((string)JObject.Parse(File.ReadAllText(path, Encoding.UTF8))["verb"] == verb) count++;
                }
                catch
                {
                    // A record that cannot be parsed is not the one being counted.
                }
            }

            return count;
        }

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

        #endregion
    }
}
