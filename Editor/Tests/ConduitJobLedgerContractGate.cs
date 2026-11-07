/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Permanent executable contract for durable admission, canonical identity, transitions, and atomic reads.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit.Tests
{
    /// <summary>Exercises the production job ledger without invoking a Unity side effect.</summary>
    [InitializeOnLoad]
    internal static class ConduitJobLedgerContractGate
    {
        /// <summary>Marker carrying the four jobs across the test's real domain reload.</summary>
        private static readonly string _recoveryMarker = Path.Combine("Library", "ItsRenji", "Conduit", "Gates", "recovery.json");
        /// <summary>Marker carrying restart identities across a full Editor process boundary.</summary>
        private static readonly string _restartMarker = Path.Combine("Library", "ItsRenji", "Conduit", "Gates", "restart.json");

        /// <summary>Re-arms a pending recovery assertion after every assembly load.</summary>
        static ConduitJobLedgerContractGate()
        {
            // Import workers load this assembly too; see ConduitBridge's static constructor. A gate that
            // ran in a worker would assert against a domain that is not the one under test.
            if (UnityEngine.Application.isBatchMode) return;

            if (File.Exists(_recoveryMarker))
            {
                EditorApplication.update -= ContinueRecoveryGate;
                EditorApplication.update += ContinueRecoveryGate;
            }
            if (File.Exists(_restartMarker))
            {
                EditorApplication.update -= ContinueRestartGate;
                EditorApplication.update += ContinueRestartGate;
            }
        }

        #region Gate entry point

        /// <summary>Runs the non-destructive ledger contract and writes one consolidated report.</summary>
        [MenuItem("Tools/Conduit Gates/Verify job ledger contract")]
        private static void Verify()
        {
            var report = new System.Text.StringBuilder("Job ledger contract - production ConduitJobLedger\n");
            var failures = 0;
            if (!ConduitJobLedger.IsReady)
            {
                Debug.LogError("[LedgerGate] ledger is not ready: " + ConduitJobLedger.GetHealthJson());
                return;
            }

            string cleanupError;
            if (!ConduitJobLedger.CleanupContractGateRecords(out cleanupError))
            {
                Debug.LogError("[LedgerGate] cannot establish a clean gate precondition: " + cleanupError);
                return;
            }

            var run = Guid.NewGuid().ToString("N");
            var session = "gate-session-" + run;
            var arguments = new JObject { ["label"] = "same", ["count"] = 1.0 };
            var reordered = new JObject { ["count"] = 1, ["label"] = "same" };

            var admissions = new ConduitJobLedger.AdmissionResult[10];
            var tasks = new Task[admissions.Length];
            for (var index = 0; index < tasks.Length; index++)
            {
                var slot = index;
                tasks[index] = Task.Run(() =>
                {
                    admissions[slot] = ConduitJobLedger.Admit(
                        session,
                        new JValue("request-one"),
                        "gate-noop",
                        reordered,
                        "gate-noop same",
                        "gate-v1",
                        "gate-client");
                });
            }
            Task.WaitAll(tasks);

            var created = 0;
            var duplicate = 0;
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var admission in admissions)
            {
                if (admission.Outcome == "created") created++;
                if (admission.Outcome == "duplicate") duplicate++;
                if (admission.JobId != null) ids.Add(admission.JobId);
            }
            Check(created == 1 && duplicate == 9 && ids.Count == 1,
                "ten concurrent retries create one job", report, ref failures);

            var firstId = admissions[0].JobId;
            var same = ConduitJobLedger.Admit(session, new JValue("request-one"), "gate-noop", arguments,
                "gate-noop same", "gate-v1", "gate-client");
            Check(same.Outcome == "duplicate" && same.JobId == firstId,
                "object order and integer-like float recover one job", report, ref failures);

            var conflictArguments = ConduitJobLedger.Admit(session, new JValue("request-one"), "gate-noop",
                new JObject { ["label"] = "different", ["count"] = 1 }, "gate-noop different", "gate-v1", "gate-client");
            var conflictVerb = ConduitJobLedger.Admit(session, new JValue("request-one"), "other-noop", reordered,
                "other-noop same", "gate-v1", "gate-client");
            Check(conflictArguments.Outcome == "idempotency_conflict" && conflictArguments.JobId == null
                && conflictVerb.Outcome == "idempotency_conflict" && conflictVerb.JobId == null,
                "request identity conflicts never mint a second job", report, ref failures);

            var deliberate = ConduitJobLedger.Admit(session, new JValue("request-two"), "gate-noop", reordered,
                "gate-noop same", "gate-v1", "gate-client");
            var otherClient = ConduitJobLedger.Admit(session + "-other", new JValue("request-one"), "gate-noop",
                reordered, "gate-noop same", "gate-v1", "gate-client");
            Check(deliberate.Outcome == "created" && otherClient.Outcome == "created"
                && deliberate.JobId != firstId && otherClient.JobId != firstId,
                "deliberate repeat and another client create new jobs", report, ref failures);

            var numericId = ConduitJobLedger.Admit(session, new JValue(1), "gate-noop", reordered,
                "gate-noop same", "gate-v1", "gate-client");
            var stringId = ConduitJobLedger.Admit(session, new JValue("1"), "gate-noop", reordered,
                "gate-noop same", "gate-v1", "gate-client");
            Check(numericId.Outcome == "created" && stringId.Outcome == "created" && numericId.JobId != stringId.JobId,
                "numeric and string JSON-RPC ids stay distinct", report, ref failures);

            Check(ConduitJobLedger.ArgumentsHash(arguments) == ConduitJobLedger.ArgumentsHash(reordered),
                "canonical argument hash ignores object order and integer-like float spelling", report, ref failures);

            ExerciseTransitions(report, ref failures);

            JObject ignoredRecord;
            string ignoredError;
            Check(!ConduitJobLedger.TryReadJob("../escape", out ignoredRecord, out ignoredError)
                && ignoredError == "invalid job id",
                "path-traversal job ids fail before filesystem access", report, ref failures);

            Check(ConduitJobLedger.CleanupContractGateRecords(out cleanupError),
                "gate removes only its own terminal records", report, ref failures);

            report.AppendLine("health: " + ConduitJobLedger.GetHealthJson());
            report.AppendLine(failures == 0 ? "PASS - production ledger contract" : "FAIL - " + failures + " problem(s)");
            if (failures == 0) Debug.Log("[LedgerGate] " + report);
            else Debug.LogError("[LedgerGate] " + report);
        }

        /// <summary>Seeds each recoverable state and requests a real AppDomain reload.</summary>
        [MenuItem("Tools/Conduit Gates/Verify job ledger reload recovery")]
        private static void VerifyReloadRecovery()
        {
            string error = null;
            if (!ConduitJobLedger.IsReady || !ConduitJobLedger.CleanupContractGateRecords(out error))
            {
                Debug.LogError("[LedgerGate] recovery gate cannot establish a clean ledger: " + error);
                return;
            }

            var run = Guid.NewGuid().ToString("N");
            Func<string, ConduitJobLedger.AdmissionResult> admit = id => ConduitJobLedger.Admit(
                "recovery-gate-" + run, new JValue(id), "gate-noop", new JObject { ["id"] = id },
                "gate-noop " + id, "gate-v1", "gate-client");
            ConduitJobLedger.JobLease lease;

            var completed = admit("completed");
            ConduitJobLedger.TryClaimNext(out lease, out error);
            ConduitJobLedger.Complete(completed.JobId, "gate_completed", "before reload", out error);

            var running = admit("running");
            ConduitJobLedger.TryClaimNext(out lease, out error);

            var waiting = admit("waiting");
            ConduitJobLedger.TryClaimNext(out lease, out error);
            ConduitJobLedger.MarkWaitingForReload(waiting.JobId,
                new JObject { ["kind"] = "post_compile_report" }, DateTime.UtcNow.AddMinutes(2), out error);

            var queued = admit("queued");
            Directory.CreateDirectory(Path.GetDirectoryName(_recoveryMarker));
            File.WriteAllText(_recoveryMarker, new JObject
            {
                ["epoch"] = ConduitJobLedger.EditorEpoch,
                ["completed"] = completed.JobId,
                ["running"] = running.JobId,
                ["waiting"] = waiting.JobId,
                ["queued"] = queued.JobId,
            }.ToString(Newtonsoft.Json.Formatting.None));
            EditorUtility.RequestScriptReload();
        }

        /// <summary>Checks recovery once both the marker and the rebuilt production ledger are ready.</summary>
        private static void ContinueRecoveryGate()
        {
            if (!File.Exists(_recoveryMarker) || !ConduitJobLedger.IsReady) return;
            EditorApplication.update -= ContinueRecoveryGate;
            var marker = JObject.Parse(File.ReadAllText(_recoveryMarker));
            var report = new System.Text.StringBuilder("Job ledger reload recovery - production ConduitJobLedger\n");
            var failures = 0;
            JObject completed;
            JObject running;
            JObject waiting;
            JObject queued;
            string error;
            ConduitJobLedger.TryReadJob((string)marker["completed"], out completed, out error);
            ConduitJobLedger.TryReadJob((string)marker["running"], out running, out error);
            ConduitJobLedger.TryReadJob((string)marker["waiting"], out waiting, out error);
            ConduitJobLedger.TryReadJob((string)marker["queued"], out queued, out error);
            Check((string)marker["epoch"] == ConduitJobLedger.EditorEpoch,
                "domain reload preserves the Editor epoch", report, ref failures);
            Check((string)completed?["state"] == "completed", "terminal record is preserved", report, ref failures);
            Check((string)running?["state"] == "outcome_unknown"
                && (string)running?["outcome"]?["operation"] == "domain_reloaded_during_execution",
                "running becomes outcome_unknown after domain reload", report, ref failures);
            // Two observations are legal here, and which one happens is a genuine race rather than a defect.
            // ConduitHttpTransport.Drain is an EditorApplication.update subscriber exactly like this
            // continuation, and TryClaimNext accepts a waiting job, so production may legitimately have claimed
            // and completed the recipe before this check runs. Asserting only the not-yet-drained observation
            // made a correct ordering read as a failure. Each branch still proves something: the recipe is
            // retained either way, and a drained job has to carry real completion evidence.
            var waitingState = (string)waiting?["state"];
            var waitingRecipe = (string)waiting?["completionRecipe"]?["kind"];
            var drained = waitingState == "completed";

            Check(waiting != null && waitingRecipe == "post_compile_report"
                    && (waitingState == "waiting_for_reload"
                        || (drained && (string)waiting["outcome"]?["operation"] == "command_completed")),
                "waiting recipe survives, still queued or already drained", report, ref failures,
                (waitingState ?? "record unreadable") + ", recipe " + (waitingRecipe ?? "none"));
            Check((string)queued?["state"] == "queued", "queued job remains safe to dispatch", report, ref failures);

            ConduitJobLedger.JobLease first;
            ConduitJobLedger.JobLease second;
            ConduitJobLedger.TryClaimNext(out first, out error);
            ConduitJobLedger.TryClaimNext(out second, out error);
            // When the drain already took the waiting job it is gone from the queue and the queued job is
            // legitimately first, with nothing behind it. Admission order is asserted in BOTH orderings rather
            // than in only one, so a correct drain cannot read as a queue that lost its order.
            Check(drained
                    ? first != null && first.JobId == (string)marker["queued"] && second == null
                    : first != null && first.JobId == (string)marker["waiting"] && first.CompletionRecipe != null
                        && second != null && second.JobId == (string)marker["queued"],
                "recovery queue preserves admission order and recipe identity", report, ref failures,
                (drained ? "drained, expect queued first: " : "not drained, expect waiting first: ")
                    + (first == null ? "none" : first.JobId) + " then " + (second == null ? "none" : second.JobId));
            if (first != null) ConduitJobLedger.Complete(first.JobId, "gate_recipe_completed", "after reload", out error);
            if (second != null) ConduitJobLedger.Complete(second.JobId, "gate_queued_completed", "after reload", out error);
            Check(ConduitJobLedger.CleanupContractGateRecords(out error), "recovery gate removes its terminal records", report, ref failures);
            File.Delete(_recoveryMarker);
            report.AppendLine(failures == 0 ? "PASS - production reload recovery" : "FAIL - " + failures + " problem(s)");
            if (failures == 0) Debug.Log("[LedgerGate] " + report);
            else Debug.LogError("[LedgerGate] " + report);
        }

        /// <summary>Seeds all restart-sensitive states without closing or saving the Editor.</summary>
        [MenuItem("Tools/Conduit Gates/Prepare job ledger restart recovery")]
        private static void PrepareRestartGate()
        {
            string error = null;
            if (!ConduitJobLedger.IsReady || !ConduitJobLedger.CleanupContractGateRecords(out error))
            {
                Debug.LogError("[LedgerGate] restart gate cannot establish a clean ledger: " + error);
                return;
            }
            var run = Guid.NewGuid().ToString("N");
            var session = "restart-gate-" + run;
            Func<string, ConduitJobLedger.AdmissionResult> admit = id => ConduitJobLedger.Admit(
                session, new JValue(id), "gate-noop", new JObject { ["id"] = id },
                "gate-noop " + id, "gate-v1", "gate-client");
            ConduitJobLedger.JobLease lease;
            var completed = admit("completed");
            ConduitJobLedger.TryClaimNext(out lease, out error);
            ConduitJobLedger.Complete(completed.JobId, "gate_completed", "before restart", out error);
            var running = admit("running");
            ConduitJobLedger.TryClaimNext(out lease, out error);
            var waiting = admit("waiting");
            ConduitJobLedger.TryClaimNext(out lease, out error);
            ConduitJobLedger.MarkWaitingForReload(waiting.JobId,
                new JObject { ["kind"] = "post_compile_report" }, DateTime.UtcNow.AddMinutes(5), out error);
            var queued = admit("queued");
            Directory.CreateDirectory(Path.GetDirectoryName(_restartMarker));
            File.WriteAllText(_restartMarker, new JObject
            {
                ["epoch"] = ConduitJobLedger.EditorEpoch,
                ["session"] = session,
                ["completed"] = completed.JobId,
                ["running"] = running.JobId,
                ["waiting"] = waiting.JobId,
                ["queued"] = queued.JobId,
            }.ToString(Newtonsoft.Json.Formatting.None));
            Debug.Log("[LedgerGate] restart states prepared; close and reopen the Editor without saving the scene.");
        }

        /// <summary>Batch-mode entry point that prepares restart evidence in an isolated project copy.</summary>
        public static void PrepareRestartGateBatch()
        {
            string error;
            if (!ConduitJobLedger.RebuildAndPublishReadiness(out error)) throw new InvalidOperationException(error);
            PrepareRestartGate();
            if (!File.Exists(_restartMarker)) throw new InvalidOperationException("Restart gate marker was not created.");
        }

        /// <summary>Batch-mode entry point that verifies restart evidence in the next Editor process.</summary>
        public static void VerifyRestartGateBatch()
        {
            string error;
            if (!ConduitJobLedger.RebuildAndPublishReadiness(out error)) throw new InvalidOperationException(error);
            ContinueRestartGate();
            if (File.Exists(_restartMarker)) throw new InvalidOperationException("Restart gate did not complete.");
        }

        /// <summary>Verifies new-epoch classifications after the Editor has been reopened.</summary>
        private static void ContinueRestartGate()
        {
            if (!File.Exists(_restartMarker) || !ConduitJobLedger.IsReady) return;
            EditorApplication.update -= ContinueRestartGate;
            var marker = JObject.Parse(File.ReadAllText(_restartMarker));
            var report = new System.Text.StringBuilder("Job ledger Editor restart - production ConduitJobLedger\n");
            var failures = 0;
            JObject completed;
            JObject running;
            JObject waiting;
            JObject queued;
            string error;
            ConduitJobLedger.TryReadJob((string)marker["completed"], out completed, out error);
            ConduitJobLedger.TryReadJob((string)marker["running"], out running, out error);
            ConduitJobLedger.TryReadJob((string)marker["waiting"], out waiting, out error);
            ConduitJobLedger.TryReadJob((string)marker["queued"], out queued, out error);
            Check((string)marker["epoch"] != ConduitJobLedger.EditorEpoch, "full restart changes the Editor epoch", report, ref failures);
            Check((string)completed?["state"] == "completed", "completed result survives restart", report, ref failures);
            Check((string)running?["state"] == "outcome_unknown"
                && (string)running?["outcome"]?["operation"] == "editor_restarted_during_execution",
                "running becomes outcome_unknown on restart", report, ref failures);
            Check((string)waiting?["state"] == "outcome_unknown"
                && (string)waiting?["outcome"]?["operation"] == "editor_restarted_during_execution",
                "waiting recipe is not mistaken for a completed reload", report, ref failures);
            Check((string)queued?["state"] == "failed"
                && (string)queued?["outcome"]?["operation"] == "editor_restarted_before_start",
                "queued job fails as never-started and safe to resubmit", report, ref failures);

            var retry = ConduitJobLedger.Admit((string)marker["session"], new JValue("completed"), "gate-noop",
                new JObject { ["id"] = "completed" }, "gate-noop completed", "gate-v1", "gate-client");
            var conflict = ConduitJobLedger.Admit((string)marker["session"], new JValue("completed"), "gate-noop",
                new JObject { ["id"] = "different" }, "gate-noop different", "gate-v1", "gate-client");
            Check(retry.Outcome == "duplicate" && retry.JobId == (string)marker["completed"],
                "old request identity resolves the same terminal job", report, ref failures);
            Check(conflict.Outcome == "idempotency_conflict" && conflict.JobId == null,
                "old request identity still rejects conflicting work", report, ref failures);
            ConduitJobLedger.JobLease lease;
            Check(!ConduitJobLedger.TryClaimNext(out lease, out error), "restart leaves no unsafe work queued", report, ref failures);
            Check(ConduitJobLedger.CleanupContractGateRecords(out error), "restart gate removes its terminal records", report, ref failures);
            File.Delete(_restartMarker);
            report.AppendLine(failures == 0 ? "PASS - production Editor restart recovery" : "FAIL - " + failures + " problem(s)");
            if (failures == 0) Debug.Log("[LedgerGate] " + report);
            else Debug.LogError("[LedgerGate] " + report);
        }

        /// <summary>Exercises degraded readiness, retention, stale-temp cleanup, and the barrier watchdog.</summary>
        [MenuItem("Tools/Conduit Gates/Verify job ledger failure modes")]
        private static void VerifyFailureModes()
        {
            var report = new System.Text.StringBuilder("Job ledger failure modes - production ConduitJobLedger\n");
            var failures = 0;
            string error = null;
            if (!ConduitJobLedger.IsReady || !ConduitJobLedger.CleanupContractGateRecords(out error))
            {
                Debug.LogError("[LedgerGate] failure-mode gate cannot establish a clean ledger: " + error);
                return;
            }

            var directory = ConduitJobLedger.ContractGateDirectory;
            Directory.CreateDirectory(directory);
            var stale = Path.Combine(directory, "gate-stale.tmp");
            File.WriteAllText(stale, "stale");
            Check(ConduitJobLedger.RebuildAndPublishReadiness(out error) && !File.Exists(stale),
                "readiness removes stale temp files", report, ref failures);
            Check(ConduitJobLedger.ContractGateAcceptsRecordCount(10000)
                && !ConduitJobLedger.ContractGateAcceptsRecordCount(10001),
                "readiness record-count bound is closed", report, ref failures);
            var now = DateTime.UtcNow;
            Check(!ConduitJobLedger.ContractGateWouldPrune(1999, now, now)
                && ConduitJobLedger.ContractGateWouldPrune(2000, now, now)
                && ConduitJobLedger.ContractGateWouldPrune(0, now.AddDays(-15), now),
                "terminal retention enforces count and age boundaries", report, ref failures);

            var old = AdmitGate("old-terminal");
            ConduitJobLedger.JobLease lease;
            ConduitJobLedger.TryClaimNext(out lease, out error);
            ConduitJobLedger.Complete(old.JobId, "gate_completed", "old", out error);
            JObject oldRecord;
            ConduitJobLedger.TryReadJob(old.JobId, out oldRecord, out error);
            oldRecord["terminalUtc"] = now.AddDays(-15).ToString("O");
            WriteGateRecord(directory, oldRecord);
            Check(ConduitJobLedger.RebuildAndPublishReadiness(out error)
                && !File.Exists(Path.Combine(directory, old.JobId + ".json")),
                "readiness prunes an expired terminal record", report, ref failures);

            var corruptId = "job-" + Guid.NewGuid().ToString("N");
            var corruptPath = Path.Combine(directory, corruptId + ".json");
            File.WriteAllText(corruptPath, "{broken", new System.Text.UTF8Encoding(false));
            Check(!ConduitJobLedger.RebuildAndPublishReadiness(out error)
                && ConduitJobLedger.GetHealthJson().Contains("degraded"),
                "invalid JSON blocks mutation readiness", report, ref failures);
            File.Delete(corruptPath);
            Check(ConduitJobLedger.RebuildAndPublishReadiness(out error), "readiness recovers after corrupt JSON is removed", report, ref failures);

            var future = AdmitGate("future-schema");
            ConduitJobLedger.TryClaimNext(out lease, out error);
            ConduitJobLedger.Complete(future.JobId, "gate_completed", "future", out error);
            JObject futureRecord;
            ConduitJobLedger.TryReadJob(future.JobId, out futureRecord, out error);
            futureRecord["schemaVersion"] = 2;
            WriteGateRecord(directory, futureRecord);
            Check(!ConduitJobLedger.RebuildAndPublishReadiness(out error) && error.Contains("Unsupported job schema"),
                "future schema blocks mutation readiness", report, ref failures);
            File.Delete(Path.Combine(directory, future.JobId + ".json"));
            Check(ConduitJobLedger.RebuildAndPublishReadiness(out error), "readiness recovers after future record is removed", report, ref failures);

            var duplicate = AdmitGate("duplicate-key");
            JObject duplicateRecord;
            ConduitJobLedger.TryReadJob(duplicate.JobId, out duplicateRecord, out error);
            var secondId = "job-" + Guid.NewGuid().ToString("N");
            duplicateRecord["jobId"] = secondId;
            duplicateRecord["admissionSequence"] = (long)duplicateRecord["admissionSequence"] + 1;
            WriteGateRecord(directory, duplicateRecord);
            Check(!ConduitJobLedger.RebuildAndPublishReadiness(out error) && error.Contains("Duplicate requestKey"),
                "duplicate durable request keys fail closed", report, ref failures);
            File.Delete(Path.Combine(directory, secondId + ".json"));
            Check(ConduitJobLedger.RebuildAndPublishReadiness(out error), "readiness recovers after duplicate is removed", report, ref failures);
            ConduitJobLedger.Cancel(duplicate.JobId, "gate cleanup", out error);

            var watchdog = AdmitGate("watchdog");
            ConduitJobLedger.TryClaimNext(out lease, out error);
            ConduitJobLedger.MarkWaitingForReload(watchdog.JobId, new JObject { ["kind"] = "post_compile_report" },
                DateTime.UtcNow.AddMilliseconds(25), out error);
            Thread.Sleep(35);
            ConduitJobLedger.EvaluateBarrierWatchdog(DateTime.UtcNow, true);
            JObject watchdogRecord;
            ConduitJobLedger.TryReadJob(watchdog.JobId, out watchdogRecord, out error);
            Check((string)watchdogRecord["state"] == "waiting_for_reload",
                "watchdog does not fire while the Editor is busy", report, ref failures);
            ConduitJobLedger.EvaluateBarrierWatchdog(DateTime.UtcNow, false);
            ConduitJobLedger.TryReadJob(watchdog.JobId, out watchdogRecord, out error);

            // This check is inverted from what it used to assert, and the old version is why the defect
            // survived. It previously required the watchdog to record failed/reload_not_observed, so the
            // contract itself said reporting a failure was correct. It is not: the side effect lands before
            // the barrier is armed, so a failure recorded here is one reported for work that already
            // succeeded. The job settles as outcome_unknown instead - the only thing true of it - and being
            // terminal is what lets everything queued behind it move again.
            var barrierReleased = ConduitJobLedger.GetHealthJson().Contains("\"barrierOwnerJobId\":null");
            Check((string)watchdogRecord["state"] == "outcome_unknown"
                    && (string)watchdogRecord["outcome"]?["operation"] == "reload_not_observed"
                    && barrierReleased,
                "watchdog settles its owner as outcome_unknown and releases the barrier", report, ref failures,
                (string)watchdogRecord["state"] + ", barrier released " + barrierReleased);

            Check(ConduitJobLedger.CleanupContractGateRecords(out error),
                "failure-mode gate removes only its terminal records", report, ref failures);
            report.AppendLine("health: " + ConduitJobLedger.GetHealthJson());
            report.AppendLine(failures == 0 ? "PASS - production ledger failure modes" : "FAIL - " + failures + " problem(s)");
            if (failures == 0) Debug.Log("[LedgerGate] " + report);
            else Debug.LogError("[LedgerGate] " + report);
        }

        /// <summary>Admits one uniquely named no-op gate job.</summary>
        private static ConduitJobLedger.AdmissionResult AdmitGate(string label)
        {
            var unique = label + "-" + Guid.NewGuid().ToString("N");
            return ConduitJobLedger.Admit("failure-gate", new JValue(unique), "gate-noop",
                new JObject { ["id"] = unique }, "gate-noop " + unique, "gate-v1", "gate-client");
        }

        /// <summary>Writes one intentionally altered gate record for readiness validation.</summary>
        private static void WriteGateRecord(string directory, JObject record)
        {
            File.WriteAllText(Path.Combine(directory, (string)record["jobId"] + ".json"),
                record.ToString(Newtonsoft.Json.Formatting.None), new System.Text.UTF8Encoding(false));
        }

        #endregion

        #region Transition and atomic-read gate

        /// <summary>Claims every gate job, exercises legal transitions, and reads through atomic replacement.</summary>
        /// <param name="report">Consolidated report.</param>
        /// <param name="failures">Failure counter.</param>
        private static void ExerciseTransitions(System.Text.StringBuilder report, ref int failures)
        {
            var leases = new List<ConduitJobLedger.JobLease>();
            ConduitJobLedger.JobLease lease;
            string error;
            while (ConduitJobLedger.TryClaimNext(out lease, out error)) leases.Add(lease);
            Check(leases.Count >= 5, "FIFO queue yields the admitted jobs", report, ref failures);
            if (leases.Count == 0) return;

            var readFailures = 0;
            var readAttempts = 0;
            string firstReadError = null;
            var stop = 0;
            var readerStarted = new ManualResetEventSlim(false);
            var firstReadDone = new ManualResetEventSlim(false);
            var reader = Task.Run(() =>
            {
                readerStarted.Set();
                while (Volatile.Read(ref stop) == 0)
                {
                    Interlocked.Increment(ref readAttempts);
                    JObject record;
                    string readError;
                    if (!ConduitJobLedger.TryReadJob(leases[0].JobId, out record, out readError))
                    {
                        Interlocked.CompareExchange(ref firstReadError, readError, null);
                        Interlocked.Increment(ref readFailures);
                    }
                    firstReadDone.Set();
                }
            });
            readerStarted.Wait();
            firstReadDone.Wait();

            Check(ConduitJobLedger.Complete(leases[0].JobId, "gate_completed", "complete evidence", out error),
                "running job completes with operation evidence", report, ref failures);
            Volatile.Write(ref stop, 1);
            reader.Wait();
            readerStarted.Dispose();
            firstReadDone.Dispose();
            Check(readAttempts > 0 && readFailures == 0,
                "concurrent readers see complete JSON through replacement (" + readAttempts + " reads)" +
                (firstReadError == null ? string.Empty : " (first error: " + firstReadError + ")"),
                report, ref failures);

            if (leases.Count > 1)
            {
                var recipe = new JObject { ["kind"] = "post_compile_report" };
                var marked = ConduitJobLedger.MarkWaitingForReload(
                    leases[1].JobId, recipe, DateTime.UtcNow.AddMinutes(1), out error);
                Check(marked, "running job persists a validated reload recipe", report, ref failures);
                Check(ConduitJobLedger.Fail(leases[1].JobId, "reload_not_observed", "gate release", out error),
                    "barrier owner terminalizes before release", report, ref failures);
            }

            for (var index = 2; index < leases.Count; index++)
            {
                Check(ConduitJobLedger.Fail(leases[index].JobId, "gate_failed", "cleanup", out error),
                    "remaining running job " + index + " terminalizes", report, ref failures);
            }
        }

        #endregion

        #region Assertions

        /// <summary>Appends one pass or fail without stopping the remaining evidence.</summary>
        /// <param name="condition">Assertion result.</param>
        /// <param name="label">Evidence label.</param>
        /// <param name="report">Consolidated report.</param>
        /// <param name="failures">Failure counter.</param>
        private static void Check(bool condition, string label, System.Text.StringBuilder report, ref int failures)
        {
            report.AppendLine("  " + (condition ? "PASS  " : "FAIL  ") + label);
            if (!condition) failures++;
        }

        /// <summary>
        /// Records one check's result together with the value actually observed. A check that prints only its
        /// label cannot distinguish a wrong value from an unreadable record, which is what made the recovery
        /// race above undiagnosable from its own report.
        /// </summary>
        /// <param name="condition">Whether the check held.</param>
        /// <param name="label">Check name.</param>
        /// <param name="report">Report sink.</param>
        /// <param name="failures">Running failure count.</param>
        /// <param name="observed">What was seen, printed whether the check passed or failed.</param>
        private static void Check(bool condition, string label, System.Text.StringBuilder report, ref int failures, string observed)
        {
            report.AppendLine("  " + (condition ? "PASS  " : "FAIL  ") + label + "  ->  " + observed);
            if (!condition) failures++;
        }

        #endregion
    }
}
