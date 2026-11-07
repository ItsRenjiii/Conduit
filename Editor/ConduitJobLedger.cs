/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Durable request admission and job recovery for Conduit's Editor-hosted HTTP transport.
//
// A job record under Library is the sole durable truth. The in-memory index and dispatch queue are rebuilt
// from those records after every domain load; SessionState carries only the Editor-instance epoch that
// distinguishes an assembly reload from a full Editor restart.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace ItsRenji.Conduit
{
    /// <summary>Owns durable mutation admission, legal job transitions, recovery, and off-thread reads.</summary>
    [InitializeOnLoad]
    internal static class ConduitJobLedger
    {
        #region Constants

        /// <summary>Record format understood by this implementation.</summary>
        private const int SCHEMA_VERSION = 1;
        /// <summary>Largest record set readiness will inspect rather than silently truncate.</summary>
        private const int MAX_SCAN_RECORDS = 10000;
        /// <summary>Newest terminal records retained regardless of age.</summary>
        private const int MAX_TERMINAL_RECORDS = 2000;
        /// <summary>Age after which a terminal record may be pruned.</summary>
        private const int TERMINAL_RETENTION_DAYS = 14;
        /// <summary>SessionState key distinguishing reload from restart.</summary>
        private const string EPOCH_SESSION_KEY = "ItsRenji.Conduit.Http.EditorEpoch";
        /// <summary>SessionState key holding the last measured post-bind readiness hint.</summary>
        private const string READINESS_HINT_SESSION_KEY = "ItsRenji.Conduit.Http.ReadinessHintSeconds";
        /// <summary>Durably admitted but not begun.</summary>
        private const string STATE_QUEUED = "queued";
        /// <summary>Side-effect execution has begun.</summary>
        private const string STATE_RUNNING = "running";
        /// <summary>A validated recipe awaits a domain reload.</summary>
        private const string STATE_WAITING = "waiting_for_reload";
        /// <summary>Operation finished successfully.</summary>
        private const string STATE_COMPLETED = "completed";
        /// <summary>Operation failed with a known outcome.</summary>
        private const string STATE_FAILED = "failed";
        /// <summary>Queued operation was cancelled before execution.</summary>
        private const string STATE_CANCELLED = "cancelled";
        /// <summary>Operation was deliberately interrupted.</summary>
        private const string STATE_INTERRUPTED = "interrupted";
        /// <summary>Execution crossed a boundary with unknowable side-effect outcome.</summary>
        private const string STATE_UNKNOWN = "outcome_unknown";

        #endregion

        #region Private state

        /// <summary>Serializes admission, transitions, recovery publication, and queue claims.</summary>
        private static readonly object _gate = new object();
        /// <summary>Prevents job ids from becoming filesystem paths.</summary>
        private static readonly Regex _jobIdPattern = new Regex(@"^job-[0-9a-f]{32}$", RegexOptions.CultureInvariant);
        /// <summary>Validates full lowercase SHA-256 fields.</summary>
        private static readonly Regex _digestPattern = new Regex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        /// <summary>Project-local authoritative job directory.</summary>
        private static readonly string _ledgerDirectory = Path.Combine("Library", "ItsRenji", "Conduit", "Jobs");
        /// <summary>Published in-memory record view rebuilt from disk.</summary>
        private static readonly Dictionary<string, JObject> _records = new Dictionary<string, JObject>(StringComparer.Ordinal);
        /// <summary>Derived request identity to job lookup.</summary>
        private static readonly Dictionary<string, string> _requestIndex = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>FIFO job ids waiting for main-thread execution or recovery.</summary>
        private static readonly Queue<string> _dispatchQueue = new Queue<string>();
        /// <summary>True only after validation, recovery, pruning, and queue reconstruction.</summary>
        private static bool _ready;
        /// <summary>Actionable reason mutation readiness is degraded.</summary>
        private static string _readinessError;
        /// <summary>Next sequence allocated while the admission lock is held.</summary>
        private static long _nextAdmissionSequence;
        /// <summary>Job whose persisted recipe must settle before another dispatch begins.</summary>
        private static string _barrierOwnerJobId;
        /// <summary>Deadline for observing the barrier owner's intended reload.</summary>
        private static DateTime _barrierDeadlineUtc;
        /// <summary>Whether Unity has reported that the barrier owner's compile or reload actually began.</summary>
        private static bool _compileObserved;
        /// <summary>
        /// Compiler errors reported while the barrier was held, or null when the compile produced none. This is
        /// the case a barrier cannot wait out: the compile reported errors, so the owning job is failed with
        /// them rather than left parked on a result that is not coming.
        /// </summary>
        private static string _compileErrors;
        /// <summary>
        /// Wait handles held by callers parked on a job, keyed by job id. A terminal transition signals them, so
        /// a caller learns the work finished from an event rather than by asking the ledger over and over.
        /// </summary>
        private static readonly Dictionary<string, List<ManualResetEventSlim>> _terminalWaiters =
            new Dictionary<string, List<ManualResetEventSlim>>(StringComparer.Ordinal);
        /// <summary>
        /// Signalled whenever no caller is parked on a job. The transport waits on it during teardown so
        /// answers are written before the listener closes, rather than racing the close and losing.
        /// </summary>
        private static readonly ManualResetEventSlim _waitersDrained = new ManualResetEventSlim(true);
        /// <summary>Atomically published health body for worker endpoints.</summary>
        private static string _healthJson = "{\"ready\":false,\"state\":\"starting\"}";
        /// <summary>UTC tick captured with the published health body.</summary>
        private static long _healthSnapshotUtcTicks;
        /// <summary>UTC instant at which this domain first initialized the ledger.</summary>
        private static DateTime _startupBeganUtc;
        /// <summary>Measured, clamped delay suggested to clients while readiness is closed.</summary>
        private static int _suggestedRetryAfterSeconds;
        /// <summary>UTC instant at which degraded readiness may retry after its underlying file is repaired.</summary>
        private static DateTime _nextReadinessRetryUtc;

        /// <summary>This Editor instance's epoch, stable across domain reloads and new after restart.</summary>
        internal static string EditorEpoch { get; private set; }

        /// <summary>One admitted job claimed for main-thread execution.</summary>
        internal sealed class JobLease
        {
            /// <summary>Durable job identifier.</summary>
            public string JobId;
            /// <summary>Canonical bridge verb.</summary>
            public string Verb;
            /// <summary>Client whose bearer admitted this, or empty when the shared bearer did.</summary>
            public string Client;
            /// <summary>Exact compatibility command admitted.</summary>
            public string Command;
            /// <summary>Canonical structured arguments.</summary>
            public JObject Arguments;
            /// <summary>Recipe recovered after a reload, or null for ordinary dispatch.</summary>
            public JObject CompletionRecipe;
            /// <summary>Running revision persisted before this lease was returned.</summary>
            public long Revision;
        }

        /// <summary>Result of one idempotent admission attempt.</summary>
        internal sealed class AdmissionResult
        {
            /// <summary>Created, duplicate, conflict, not-ready, or invalid outcome.</summary>
            public string Outcome;
            /// <summary>Resolved durable job id for created and duplicate outcomes.</summary>
            public string JobId;
            /// <summary>Current job state.</summary>
            public string State;
            /// <summary>Current durable revision.</summary>
            public long Revision;
            /// <summary>Actionable refusal reason.</summary>
            public string Error;
        }

        #endregion

        #region Lifecycle and readiness

        static ConduitJobLedger()
        {
            _startupBeganUtc = DateTime.UtcNow;
            _suggestedRetryAfterSeconds = Math.Max(1, Math.Min(10, SessionState.GetInt(READINESS_HINT_SESSION_KEY, 10)));
            var stored = SessionState.GetString(EPOCH_SESSION_KEY, string.Empty);
            EditorEpoch = string.IsNullOrEmpty(stored) ? Guid.NewGuid().ToString("N") : stored;
            if (string.IsNullOrEmpty(stored)) SessionState.SetString(EPOCH_SESSION_KEY, EditorEpoch);

            // Only an interactive Editor owns this ledger. Unity's asset import workers are Editor processes
            // launched with -batchMode and they load these same assemblies, so without this guard every worker
            // would scan, recover and republish readiness over the records the real Editor is using - several
            // processes mutating one durable store, each believing it is the only one.
            if (UnityEngine.Application.isBatchMode) return;

            // No barrier tick is subscribed here. A permanent per-frame tick asking whether a compile had
            // begun was the wrong instrument: Unity raises compilation and reload events, and a poll that
            // sampled "not compiling" once, after a fixed grace, reported a failure for work that had already
            // been written and went on to compile cleanly. The observers are armed with the barrier instead,
            // and removed by the first event to arrive.
            EditorApplication.update -= StartupTick;
            EditorApplication.update += StartupTick;
        }

        /// <summary>Builds the complete durable view before mutation admission opens.</summary>
        private static void StartupTick()
        {
            EditorApplication.update -= StartupTick;
            string error;
            RebuildAndPublishReadiness(out error);
            if (error != null)
            {
                // Held in the published health body, which every client can read, rather than written to the
                // console. Conduit does not talk to the console.
                _nextReadinessRetryUtc = DateTime.UtcNow.AddSeconds(_suggestedRetryAfterSeconds);
                EditorApplication.update -= ReadinessRetryTick;
                EditorApplication.update += ReadinessRetryTick;
            }
        }

        /// <summary>Retries degraded readiness after the corrupt or unsupported record is repaired.</summary>
        private static void ReadinessRetryTick()
        {
            if (DateTime.UtcNow < _nextReadinessRetryUtc) return;
            string error;
            if (RebuildAndPublishReadiness(out error))
            {
                EditorApplication.update -= ReadinessRetryTick;
                return;
            }
            _nextReadinessRetryUtc = DateTime.UtcNow.AddSeconds(_suggestedRetryAfterSeconds);
        }

        /// <summary>Returns an immutable health snapshot without touching a Unity API.</summary>
        internal static string GetHealthJson()
        {
            var health = JObject.Parse(Volatile.Read(ref _healthJson));
            var snapshotTicks = Interlocked.Read(ref _healthSnapshotUtcTicks);
            health["snapshotAgeMs"] = snapshotTicks <= 0
                ? JValue.CreateNull()
                : new JValue(Math.Max(0d, TimeSpan.FromTicks(DateTime.UtcNow.Ticks - snapshotTicks).TotalMilliseconds));
            return health.ToString(Formatting.None);
        }

        /// <summary>Reports whether recovery, validation, and queue reconstruction have all finished.</summary>
        internal static bool IsReady
        {
            get { lock (_gate) { return _ready; } }
        }

        /// <summary>Re-runs the production readiness path for the permanent corruption and recovery gates.</summary>
        /// <param name="error">Receives the same degraded-health evidence production would publish.</param>
        /// <returns>True when the complete rebuild and recovery path reopened admission.</returns>
        internal static bool RebuildAndPublishReadiness(out string error)
        {
            lock (_gate)
            {
                _ready = false;
                _readinessError = null;
                RefreshHealthLocked();
            }

            try
            {
                RebuildAndRecover();
                EditorApplication.update -= ReadinessRetryTick;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                lock (_gate)
                {
                    _ready = false;
                    _readinessError = error;
                    RefreshHealthLocked();
                }
                return false;
            }
        }

        /// <summary>Returns the authoritative directory solely so the permanent gate can seed corrupt files.</summary>
        internal static string ContractGateDirectory { get { return _ledgerDirectory; } }

        /// <summary>Applies the production readiness bound without creating thousands of physical files.</summary>
        internal static bool ContractGateAcceptsRecordCount(int count)
        {
            return count >= 0 && count <= MAX_SCAN_RECORDS;
        }

        /// <summary>Applies the production terminal retention boundary without thousands of durable writes.</summary>
        /// <param name="newestFirstIndex">Zero-based terminal order, newest first.</param>
        /// <param name="terminalUtc">Terminal timestamp.</param>
        /// <param name="nowUtc">Current UTC time.</param>
        /// <returns>True when count or age policy prunes the record.</returns>
        internal static bool ContractGateWouldPrune(int newestFirstIndex, DateTime terminalUtc, DateTime nowUtc)
        {
            return newestFirstIndex >= MAX_TERMINAL_RECORDS
                || terminalUtc.ToUniversalTime() < nowUtc.ToUniversalTime().AddDays(-TERMINAL_RETENTION_DAYS);
        }

        private static void RebuildAndRecover()
        {
            Directory.CreateDirectory(_ledgerDirectory);
            foreach (var temp in Directory.GetFiles(_ledgerDirectory, "*.tmp")) File.Delete(temp);

            var paths = Directory.GetFiles(_ledgerDirectory, "*.json");
            if (paths.Length > MAX_SCAN_RECORDS)
            {
                throw new InvalidDataException("Job record count " + paths.Length + " exceeds readiness bound " + MAX_SCAN_RECORDS + ".");
            }

            var loaded = new List<JObject>();
            var requestKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            long maxSequence = -1;
            foreach (var path in paths)
            {
                var record = ReadRecordPath(path);
                ValidateRecord(record, Path.GetFileNameWithoutExtension(path));
                var requestKey = (string)record["requestKey"];
                var jobId = (string)record["jobId"];
                string duplicate;
                if (requestKeys.TryGetValue(requestKey, out duplicate))
                {
                    throw new InvalidDataException("Duplicate requestKey belongs to " + duplicate + " and " + jobId + ".");
                }

                requestKeys.Add(requestKey, jobId);
                maxSequence = Math.Max(maxSequence, (long)record["admissionSequence"]);
                loaded.Add(record);
            }

            loaded.Sort((left, right) =>
            {
                var order = ((long)left["admissionSequence"]).CompareTo((long)right["admissionSequence"]);
                return order != 0 ? order : string.CompareOrdinal((string)left["jobId"], (string)right["jobId"]);
            });

            lock (_gate)
            {
                _ready = false;
                _readinessError = null;
                _records.Clear();
                _requestIndex.Clear();
                _dispatchQueue.Clear();
                _nextAdmissionSequence = maxSequence + 1;

                foreach (var original in loaded)
                {
                    var record = original;
                    var state = (string)record["state"];
                    var sameEpoch = string.Equals((string)record["editorEpoch"], EditorEpoch, StringComparison.Ordinal);
                    if (!IsTerminal(state))
                    {
                        if (sameEpoch && state == STATE_RUNNING)
                        {
                            record = RecoverTerminal(record, STATE_UNKNOWN, "domain_reloaded_during_execution",
                                "The AppDomain reloaded while the side effect was running; inspect before retrying.");
                        }
                        else if (!sameEpoch && state == STATE_QUEUED)
                        {
                            record = RecoverTerminal(record, STATE_FAILED, "editor_restarted_before_start",
                                "The Editor restarted before this job began; it is safe to resubmit with a new request id.");
                        }
                        else if (!sameEpoch && (state == STATE_RUNNING || state == STATE_WAITING))
                        {
                            record = RecoverTerminal(record, STATE_UNKNOWN, "editor_restarted_during_execution",
                                "The Editor restarted after work began; the side effect may or may not have landed.");
                        }
                    }

                    var jobId = (string)record["jobId"];
                    _records.Add(jobId, record);
                    _requestIndex.Add((string)record["requestKey"], jobId);
                    state = (string)record["state"];
                    if (sameEpoch && (state == STATE_QUEUED || state == STATE_WAITING)) _dispatchQueue.Enqueue(jobId);
                }

                PruneTerminalRecordsLocked();
                _suggestedRetryAfterSeconds = Math.Max(1, Math.Min(10,
                    (int)Math.Ceiling((DateTime.UtcNow - _startupBeganUtc).TotalSeconds)));
                SessionState.SetInt(READINESS_HINT_SESSION_KEY, _suggestedRetryAfterSeconds);
                _ready = true;
                RefreshHealthLocked();
            }
        }

        #endregion

        #region Admission and dispatch

        /// <summary>Returns the ledger's measured reconnect delay hint, clamped to one through ten seconds.</summary>
        internal static int SuggestedRetryAfterSeconds
        {
            get { lock (_gate) { return _suggestedRetryAfterSeconds; } }
        }

        /// <summary>Persists a mutation exactly once before it can enter the main-thread queue.</summary>
        /// <param name="sessionId">Echoed MCP session identity.</param>
        /// <param name="requestId">Typed JSON-RPC request id.</param>
        /// <param name="verb">Canonical bridge verb.</param>
        /// <param name="arguments">Structured arguments before compatibility encoding.</param>
        /// <param name="command">Exact command admitted for execution.</param>
        /// <param name="encodingRevision">Catalogue and command-codec revision.</param>
        /// <param name="client">
        /// Client the request's bearer identified, which is the return address for work that outlives its own
        /// call. Required rather than optional: a caller that forgets it produces a job nothing can report
        /// back to, and the failure appears an hour later as a completion with nowhere to go. An empty string
        /// is a legitimate value and means the shared bearer, which identifies nobody.
        /// </param>
        /// <returns>Created, duplicate, conflict, not-ready, or invalid admission evidence.</returns>
        internal static AdmissionResult Admit(
            string sessionId,
            JToken requestId,
            string verb,
            JObject arguments,
            string command,
            string encodingRevision,
            string client)
        {
            var invalid = ValidateAdmission(sessionId, requestId, verb, arguments, command, encodingRevision);
            if (invalid != null) return new AdmissionResult { Outcome = "invalid", Error = invalid };

            var requestKey = Sha256Hex(CanonicalJson(new JArray { sessionId, requestId.DeepClone() }));
            var argumentsHash = Sha256Hex(CanonicalJson(arguments));
            lock (_gate)
            {
                if (!_ready)
                {
                    return new AdmissionResult { Outcome = "not_ready", Error = _readinessError ?? "ledger readiness is still running" };
                }

                string existingId;
                if (_requestIndex.TryGetValue(requestKey, out existingId))
                {
                    var existing = _records[existingId];
                    if (!string.Equals((string)existing["verb"], verb, StringComparison.Ordinal)
                        || !string.Equals((string)existing["argumentsHash"], argumentsHash, StringComparison.Ordinal))
                    {
                        return new AdmissionResult { Outcome = "idempotency_conflict", Error = "request identity was reused for different work" };
                    }

                    return AdmissionFrom("duplicate", existing);
                }

                var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                var jobId = "job-" + Guid.NewGuid().ToString("N");
                var record = new JObject
                {
                    ["schemaVersion"] = SCHEMA_VERSION,
                    ["jobId"] = jobId,
                    ["requestKey"] = requestKey,
                    ["verb"] = verb,
                    ["argumentsHash"] = argumentsHash,
                    ["arguments"] = CanonicalClone(arguments),
                    ["command"] = command,
                    ["encodingRevision"] = encodingRevision,
                    ["client"] = client ?? string.Empty,
                    ["state"] = STATE_QUEUED,
                    ["revision"] = 1,
                    ["admissionSequence"] = _nextAdmissionSequence++,
                    ["editorEpoch"] = EditorEpoch,
                    ["createdUtc"] = now,
                    ["updatedUtc"] = now,
                };

                WriteAtomic(record);
                _records.Add(jobId, record);
                _requestIndex.Add(requestKey, jobId);
                _dispatchQueue.Enqueue(jobId);
                RefreshHealthLocked();
                return AdmissionFrom("created", record);
            }
        }

        /// <summary>Claims the next durable job and persists running before returning it to the dispatcher.</summary>
        /// <param name="lease">Receives the persisted running lease.</param>
        /// <param name="error">Receives a readiness or barrier reason.</param>
        /// <returns>True when a job was claimed.</returns>
        internal static bool TryClaimNext(out JobLease lease, out string error)
        {
            lease = null;
            error = null;
            lock (_gate)
            {
                if (!_ready) { error = _readinessError ?? "ledger not ready"; return false; }
                if (_barrierOwnerJobId != null) { error = "compile barrier armed"; return false; }

                while (_dispatchQueue.Count > 0)
                {
                    var jobId = _dispatchQueue.Peek();
                    JObject current;
                    if (!_records.TryGetValue(jobId, out current)) { _dispatchQueue.Dequeue(); continue; }
                    var state = (string)current["state"];
                    if (state != STATE_QUEUED && state != STATE_WAITING) { _dispatchQueue.Dequeue(); continue; }

                    var updated = (JObject)current.DeepClone();
                    updated["state"] = STATE_RUNNING;
                    updated["revision"] = (long)current["revision"] + 1;
                    updated["editorEpoch"] = EditorEpoch;
                    updated["updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                    if (updated["startedUtc"] == null) updated["startedUtc"] = updated["updatedUtc"];
                    try { WriteAtomic(updated); }
                    catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
                    _dispatchQueue.Dequeue();
                    _records[jobId] = updated;
                    RefreshHealthLocked();
                    lease = new JobLease
                    {
                        JobId = jobId,
                        Verb = (string)updated["verb"],

                        // Absent on any record written before jobs carried a return address, and an empty
                        // client means exactly what it means everywhere else: nobody to report back to.
                        Client = (string)updated["client"] ?? string.Empty,
                        Command = (string)updated["command"],
                        Arguments = (JObject)updated["arguments"].DeepClone(),
                        CompletionRecipe = updated["completionRecipe"] as JObject == null
                            ? null
                            : (JObject)updated["completionRecipe"].DeepClone(),
                        Revision = (long)updated["revision"],
                    };
                    return true;
                }

                return false;
            }
        }

        #endregion

        #region State transitions

        /// <summary>Persists a validated completion recipe before the caller triggers a reload.</summary>
        /// <param name="jobId">Running job that will own the barrier.</param>
        /// <param name="recipe">Validated tagged completion recipe.</param>
        /// <param name="deadlineUtc">UTC watchdog deadline.</param>
        /// <param name="error">Receives a transition refusal.</param>
        /// <returns>True when the waiting record and barrier were published.</returns>
        internal static bool MarkWaitingForReload(string jobId, JObject recipe, DateTime deadlineUtc, out string error)
        {
            error = ValidateRecipe(recipe);
            if (error != null) return false;
            if (deadlineUtc.ToUniversalTime() <= DateTime.UtcNow) { error = "compile-barrier deadline must be in the future"; return false; }
            lock (_gate)
            {
                JObject current;
                if (!TryCurrentLocked(jobId, out current, out error)) return false;
                if ((string)current["state"] != STATE_RUNNING) { error = "only a running job can wait for reload"; return false; }
                if (_barrierOwnerJobId != null) { error = "another job owns the compile barrier"; return false; }

                var updated = (JObject)current.DeepClone();
                updated["state"] = STATE_WAITING;
                updated["revision"] = (long)current["revision"] + 1;
                updated["completionRecipe"] = recipe.DeepClone();
                updated["barrierDeadlineUtc"] = deadlineUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                updated["updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                try { WriteAtomic(updated); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
                _records[jobId] = updated;
                _barrierOwnerJobId = jobId;
                _barrierDeadlineUtc = deadlineUtc.ToUniversalTime();
                RefreshHealthLocked();
                ArmCompileObservers();
                return true;
            }
        }

        /// <summary>Completes a running job with explicit operation evidence.</summary>
        /// <param name="jobId">Running job.</param><param name="operation">Operation marker.</param>
        /// <param name="resultText">Terminal result.</param><param name="error">Transition refusal.</param>
        /// <returns>True when the terminal record was persisted.</returns>
        internal static bool Complete(string jobId, string operation, string resultText, out string error)
        {
            return Terminal(jobId, STATE_COMPLETED, operation, resultText, out error);
        }

        /// <summary>Fails a nonterminal job with explicit operation evidence.</summary>
        /// <param name="jobId">Nonterminal job.</param><param name="operation">Operation marker.</param>
        /// <param name="errorText">Terminal failure evidence.</param><param name="error">Transition refusal.</param>
        /// <returns>True when the terminal record was persisted.</returns>
        internal static bool Fail(string jobId, string operation, string errorText, out string error)
        {
            return Terminal(jobId, STATE_FAILED, operation, errorText, out error);
        }

        /// <summary>Cancels a queued job before its side effect begins.</summary>
        /// <param name="jobId">Queued job.</param><param name="reason">Cancellation reason.</param>
        /// <param name="error">Transition refusal.</param><returns>True when cancellation was persisted.</returns>
        internal static bool Cancel(string jobId, string reason, out string error)
        {
            return Terminal(jobId, STATE_CANCELLED, "cancelled", reason, out error);
        }

        /// <summary>Records a deliberate interruption of a nonterminal job.</summary>
        /// <param name="jobId">Nonterminal job.</param><param name="reason">Interruption reason.</param>
        /// <param name="error">Transition refusal.</param><returns>True when interruption was persisted.</returns>
        internal static bool Interrupt(string jobId, string reason, out string error)
        {
            return Terminal(jobId, STATE_INTERRUPTED, "interrupted", reason, out error);
        }

        /// <summary>Applies one legal terminal transition and clears an owned barrier after persistence.</summary>
        /// <param name="jobId">Job to transition.</param><param name="state">Terminal state.</param>
        /// <param name="operation">Operation marker.</param><param name="text">Terminal evidence.</param>
        /// <param name="error">Transition refusal.</param><returns>True when persistence succeeded.</returns>
        private static bool Terminal(string jobId, string state, string operation, string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(operation)) { error = "terminal operation marker is required"; return false; }
            JObject terminalRecord;
            lock (_gate)
            {
                JObject current;
                if (!TryCurrentLocked(jobId, out current, out error)) return false;
                if (IsTerminal((string)current["state"])) { error = "job is already terminal"; return false; }
                if (!IsAllowedTerminalSource((string)current["state"], state))
                {
                    error = "illegal transition from " + (string)current["state"] + " to " + state;
                    return false;
                }
                var updated = (JObject)current.DeepClone();
                updated["state"] = state;
                updated["revision"] = (long)current["revision"] + 1;
                updated["updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
                updated["terminalUtc"] = updated["updatedUtc"];
                updated["outcome"] = new JObject { ["operation"] = operation, ["text"] = text ?? string.Empty };
                try { WriteAtomic(updated); }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
                _records[jobId] = updated;
                if (_barrierOwnerJobId == jobId) _barrierOwnerJobId = null;
                RefreshHealthLocked();
                SignalTerminalLocked(jobId);
                terminalRecord = updated;
            }

            // A listener thread in the old managed domain cannot survive the reload that made this result
            // honest. If its accepted socket was handed off, finish that original MCP call now that the
            // terminal record is durable. This runs outside the ledger lock because a loopback send is I/O.
            ConduitReloadResponseHandoff.OnJobTerminal(jobId, terminalRecord);
            return true;
        }

        /// <summary>Wakes every caller parked on a job. Called with the ledger lock already held.</summary>
        /// <param name="jobId">Job that has just become terminal.</param>
        private static void SignalTerminalLocked(string jobId)
        {
            List<ManualResetEventSlim> waiters;
            if (!_terminalWaiters.TryGetValue(jobId, out waiters)) return;

            foreach (var waiter in waiters) waiter.Set();
        }

        /// <summary>
        /// Parks the calling thread until a job reaches a terminal state, so the verb that started the work can
        /// answer with the work's result instead of a job id. The wait is on an event the terminal transition
        /// signals; nothing here asks the ledger repeatedly whether the job has finished.
        ///
        /// Only a thread that is not the editor's may call this. Jobs execute on the main thread, so parking
        /// that thread would stop the very work being waited on and the wait could never end.
        /// </summary>
        /// <param name="jobId">Job to wait for.</param>
        /// <param name="timeoutMs">Longest wait before the caller is told the job is still running.</param>
        /// <param name="record">Receives the terminal record, or null when the wait ran out.</param>
        /// <returns>True when the job reached a terminal state within the wait.</returns>
        internal static bool WaitForTerminal(string jobId, int timeoutMs, out JObject record)
        {
            record = null;
            if (!IsValidJobId(jobId)) return false;

            var waiter = new ManualResetEventSlim(false);
            lock (_gate)
            {
                List<ManualResetEventSlim> waiters;
                if (!_terminalWaiters.TryGetValue(jobId, out waiters))
                {
                    waiters = new List<ManualResetEventSlim>();
                    _terminalWaiters[jobId] = waiters;
                }

                waiters.Add(waiter);
                _waitersDrained.Reset();
            }

            try
            {
                // Read AFTER registering, never before. A job that finished between the two would otherwise
                // signal a handle nobody was holding yet, and this caller would then wait out the full timeout
                // for an event that had already happened.
                if (TryTerminalRecord(jobId, out record)) return true;
                if (!waiter.Wait(timeoutMs)) { record = null; return false; }

                return TryTerminalRecord(jobId, out record);
            }
            finally
            {
                lock (_gate)
                {
                    List<ManualResetEventSlim> waiters;
                    if (_terminalWaiters.TryGetValue(jobId, out waiters))
                    {
                        waiters.Remove(waiter);
                        if (waiters.Count == 0) _terminalWaiters.Remove(jobId);
                    }

                    if (_terminalWaiters.Count == 0) _waitersDrained.Set();
                }

                // Disposed only after removal under the lock, so a concurrent signal cannot reach a handle that
                // is on its way out.
                waiter.Dispose();
            }
        }

        /// <summary>
        /// Wakes every parked caller because the domain is about to reload and their connections go with it.
        /// Their jobs finish after the reload, so no result can be given here; what this prevents is the
        /// listener closing underneath them, which reaches a client as a transport error indistinguishable
        /// from the editor having died rather than as anything about the command they ran.
        /// </summary>
        internal static void ReleaseWaitersForReload()
        {
            lock (_gate)
            {
                foreach (var waiters in _terminalWaiters.Values)
                {
                    foreach (var waiter in waiters) waiter.Set();
                }
            }
        }

        /// <summary>Waits until no caller is parked, so their answers are written before the listener closes.</summary>
        /// <param name="timeoutMs">Longest wait before teardown proceeds regardless.</param>
        internal static void WaitForParkedCallersToDrain(int timeoutMs)
        {
            _waitersDrained.Wait(timeoutMs);
        }

        /// <summary>Reads a job's record and reports whether it is terminal.</summary>
        /// <param name="jobId">Job to read.</param>
        /// <param name="record">Receives the record when it is terminal, otherwise null.</param>
        /// <returns>True when a terminal record was read.</returns>
        private static bool TryTerminalRecord(string jobId, out JObject record)
        {
            string readError;
            if (!TryReadJob(jobId, out record, out readError)) { record = null; return false; }
            if (IsTerminalState((string)record["state"])) return true;

            record = null;
            return false;
        }

        /// <summary>
        /// Subscribes to the events that report the barrier owner's compile and reload, for exactly as long as
        /// one job holds the barrier. Unity raises all of them; nothing here asks it anything, and nothing runs
        /// on a tick. The observers are removed by the reload, by a compile that finished with errors, or by
        /// Unity reporting that no compile was required, and never by a compile merely starting: that proves
        /// work began and says nothing about whether the job will ever be answered.
        /// </summary>
        private static void ArmCompileObservers()
        {
            _compileObserved = false;
            _compileErrors = null;

            UnityEditor.Compilation.CompilationPipeline.compilationStarted -= OnCompileObserved;
            UnityEditor.Compilation.CompilationPipeline.compilationStarted += OnCompileObserved;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished -= OnCompilationFinished;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished += OnCompilationFinished;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationNotRequired -= OnCompilationNotRequired;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationNotRequired += OnCompilationNotRequired;
            AssemblyReloadEvents.beforeAssemblyReload -= OnReloadObserved;
            AssemblyReloadEvents.beforeAssemblyReload += OnReloadObserved;
        }

        /// <summary>Removes every observer armed for a barrier, so nothing runs once the barrier is settled.</summary>
        private static void DisarmCompileObservers()
        {
            UnityEditor.Compilation.CompilationPipeline.compilationStarted -= OnCompileObserved;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            UnityEditor.Compilation.CompilationPipeline.compilationFinished -= OnCompilationFinished;
            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationNotRequired -= OnCompilationNotRequired;
            AssemblyReloadEvents.beforeAssemblyReload -= OnReloadObserved;
        }

        /// <summary>
        /// Unity reports compilation started. That proves the barrier owner's compile began, and nothing more:
        /// a compile can still end in errors, which raises no reload.
        /// Disarming here is what stranded such a job in waiting_for_reload while it held the barrier against
        /// everything admitted behind it, and let a later, unrelated compile answer it as though it were its own.
        /// </summary>
        /// <param name="context">Unused compilation context supplied by Unity.</param>
        private static void OnCompileObserved(object context)
        {
            _compileObserved = true;
        }

        /// <summary>Collects one assembly's compiler errors, which decide whether a reload can follow at all.</summary>
        /// <param name="assemblyPath">Assembly Unity finished compiling.</param>
        /// <param name="messages">Messages Unity produced for that assembly.</param>
        private static void OnAssemblyCompiled(string assemblyPath, UnityEditor.Compilation.CompilerMessage[] messages)
        {
            if (messages == null) return;

            foreach (var message in messages)
            {
                if (message.type != UnityEditor.Compilation.CompilerMessageType.Error) continue;
                _compileErrors = _compileErrors == null ? message.message : _compileErrors + "\n" + message.message;
            }
        }

        /// <summary>
        /// Unity reports compilation finished. A clean compile is followed by a reload, which owns the job; a
        /// compile that produced errors is followed by nothing, so the job is settled here instead and carries
        /// those errors as its result. A compile result is what the caller asked this verb for, and a failure is
        /// the answer it most needs, so it is reported rather than withheld until some later compile supplies a
        /// verdict that was never about this work.
        /// </summary>
        /// <param name="context">Unused compilation context supplied by Unity.</param>
        private static void OnCompilationFinished(object context)
        {
            if (_compileErrors == null) return;

            var errors = _compileErrors;
            string owner;

            lock (_gate)
            {
                owner = _barrierOwnerJobId;
                if (owner == null) return;

                _barrierOwnerJobId = null;
                RefreshHealthLocked();
            }

            DisarmCompileObservers();

            // Outside the lock: Terminal takes it itself.
            string ignored;
            Terminal(owner, STATE_FAILED, "compile_failed",
                "The edit was applied and the compile that followed reported errors. The change is on disk; fix "
                + "these and compile again.\n\n" + errors,
                out ignored);
        }

        /// <summary>Unity reports the assemblies are about to reload, which is the same conclusion.</summary>
        private static void OnReloadObserved()
        {
            _compileObserved = true;
            DisarmCompileObservers();
        }

        /// <summary>
        /// Unity reports that no assembly needed recompiling. That is the one case a barrier cannot wait out on
        /// its own: no compile begins, so no reload follows and neither of the paths above will ever run.
        ///
        /// This used to be a six second deadline checked on every editor tick, which was wrong twice over. It
        /// made every no-op compile request pay six seconds before answering, and it decided by clock rather
        /// than by fact, so a slow start could be settled while the work was still legitimately coming. Unity
        /// states it outright instead, and the tick is gone with it.
        /// </summary>
        /// <param name="context">Unused compilation context supplied by Unity.</param>
        private static void OnCompilationNotRequired(object context)
        {
            if (_compileObserved) return;

            DisarmCompileObservers();
            SettleBarrierOwner("no_compile_required",
                "The edit was applied and Unity reported that no assembly needed recompiling, so no compile ran "
                + "and no reload followed. The change is on disk and the loaded assemblies already match it.");
        }

        /// <summary>
        /// Settles whichever job holds the barrier and releases the queue behind it. Shared by the watchdog and
        /// by the no-compile-required path so the two cannot drift in how they terminalise a job; they differ
        /// only in what they can honestly tell the owner happened.
        /// </summary>
        /// <param name="operation">Operation marker recorded against the terminal record.</param>
        /// <param name="text">What the owner is told about its command.</param>
        private static void SettleBarrierOwner(string operation, string text)
        {
            string owner;

            lock (_gate)
            {
                owner = _barrierOwnerJobId;
                if (owner == null) return;

                _barrierOwnerJobId = null;
                RefreshHealthLocked();
            }

            // Outside the lock: Terminal takes it itself.
            string ignored;
            Terminal(owner, STATE_UNKNOWN, operation, text, out ignored);
        }

        /// <summary>
        /// Settles a barrier whose compile never began.
        ///
        /// It does not fail the job: the side effect landed BEFORE the barrier was armed, so reporting a
        /// failure states something untrue of work that already happened. It does not requeue the job either,
        /// because dispatch re-runs a job's original command rather than its completion recipe, so requeueing
        /// a compile-triggering verb makes it request another reload, which settles the barrier again, which
        /// requeues it again - a reload loop that pins the editor.
        ///
        /// Both of those were tried and both were wrong, and simply releasing the barrier was wrong too: the
        /// owner then waits for a reload that is never coming, never answers its caller, and every job admitted
        /// behind it sits queued forever. So the job is settled as outcome_unknown, which is the only thing
        /// true of it - the edit was applied, the compile result was never observed - and being terminal is
        /// what lets the queue move again.
        /// </summary>
        /// <param name="nowUtc">Current UTC time.</param>
        /// <param name="editorBusy">Whether compilation or import is active; a busy editor settles nothing.</param>
        internal static void EvaluateBarrierWatchdog(DateTime nowUtc, bool editorBusy)
        {
            lock (_gate)
            {
                if (_barrierOwnerJobId == null || editorBusy || nowUtc.ToUniversalTime() < _barrierDeadlineUtc) return;
            }

            SettleBarrierOwner("reload_not_observed",
                "The edit was applied. The compile this job was waiting for never began, so its result was "
                + "never observed; inspect the console before retrying, and do not assume the command failed.");
        }

        #endregion

        #region Off-thread reads

        /// <summary>Reads one complete durable record with delete sharing, so replacement cannot be blocked.</summary>
        /// <param name="jobId">Validated job identifier.</param>
        /// <param name="record">Receives the complete record.</param>
        /// <param name="error">Receives not-found, validation, or read evidence.</param>
        /// <returns>True when a valid complete record was read.</returns>
        internal static bool TryReadJob(string jobId, out JObject record, out string error)
        {
            record = null;
            error = null;
            if (!IsValidJobId(jobId)) { error = "invalid job id"; return false; }
            var path = RecordPath(jobId);
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    record = ReadRecordPath(path);
                    ValidateRecord(record, jobId);
                    return true;
                }
                catch (FileNotFoundException)
                {
                    error = "job_not_found";
                }
                catch (DirectoryNotFoundException)
                {
                    error = "job_not_found";
                }
                catch (IOException ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                }
                catch (Exception ex)
                {
                    error = ex.GetType().Name + ": " + ex.Message;
                    return false;
                }

                if (attempt < 4) Thread.Sleep(1);
            }

            return false;
        }

        /// <summary>Deletes only terminal records created by the permanent no-op contract gate.</summary>
        /// <param name="error">Receives a refusal when a gate job is still nonterminal.</param>
        /// <returns>True when every terminal gate record was removed.</returns>
        internal static bool CleanupContractGateRecords(out string error)
        {
            error = null;
            lock (_gate)
            {
                var removable = new List<string>();
                foreach (var pair in _records)
                {
                    var record = pair.Value;
                    if ((string)record["verb"] != "gate-noop" || (string)record["encodingRevision"] != "gate-v1") continue;
                    if (!IsTerminal((string)record["state"]))
                    {
                        error = "gate job " + pair.Key + " is still nonterminal";
                        return false;
                    }
                    removable.Add(pair.Key);
                }

                foreach (var jobId in removable)
                {
                    var requestKey = (string)_records[jobId]["requestKey"];
                    File.Delete(RecordPath(jobId));
                    _records.Remove(jobId);
                    _requestIndex.Remove(requestKey);
                }
                RefreshHealthLocked();
                return true;
            }
        }

        /// <summary>Deletes an exact all-terminal job set created and retained by a permanent transport gate.</summary>
        /// <param name="jobIds">Exact job ids returned to that gate during the current run.</param>
        /// <param name="error">Receives a refusal before anything is deleted.</param>
        /// <returns>True when every supplied terminal gate job was removed.</returns>
        internal static bool CleanupTerminalGateJobs(IList<string> jobIds, out string error)
        {
            error = null;
            if (jobIds == null) { error = "job id list is required"; return false; }
            lock (_gate)
            {
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var jobId in jobIds)
                {
                    JObject record;
                    if (!IsValidJobId(jobId) || !unique.Add(jobId) || !_records.TryGetValue(jobId, out record)
                        || !IsTerminal((string)record["state"]))
                    {
                        error = "every gate job id must be unique, present, and terminal";
                        return false;
                    }
                }

                foreach (var jobId in unique)
                {
                    var requestKey = (string)_records[jobId]["requestKey"];
                    File.Delete(RecordPath(jobId));
                    _records.Remove(jobId);
                    _requestIndex.Remove(requestKey);
                }
                RefreshHealthLocked();
                return true;
            }
        }

        #endregion

        #region Persistence and validation

        /// <summary>Persists one recovery decision before readiness can open.</summary>
        /// <param name="current">Current durable record.</param><param name="state">Recovery terminal state.</param>
        /// <param name="operation">Recovery marker.</param><param name="text">Recovery evidence.</param>
        /// <returns>The persisted replacement record.</returns>
        private static JObject RecoverTerminal(JObject current, string state, string operation, string text)
        {
            var updated = (JObject)current.DeepClone();
            updated["state"] = state;
            updated["revision"] = (long)current["revision"] + 1;
            updated["updatedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            updated["terminalUtc"] = updated["updatedUtc"];
            updated["outcome"] = new JObject { ["operation"] = operation, ["text"] = text };
            WriteAtomic(updated);
            return updated;
        }

        /// <summary>
        /// Flushes a complete unique temp record to the disk itself, then atomically replaces or creates its final file
        /// with File.Replace. MoveFileEx was tried for the millisecond it saves per write and refused: it fails with
        /// access denied whenever any handle holds the old record open, even one sharing delete as this ledger's own
        /// reader does on the listener thread, so a read racing a write failed the request; File.Replace succeeds
        /// against that reader. The disk flush stays: a record that power loss left unreadable fails readiness closed,
        /// which is what keeps a retried request from running its side effect twice.
        /// </summary>
        private static void WriteAtomic(JObject record)
        {
            using (ConduitProfiling.LedgerWrite.Auto())
            {
                var jobId = (string)record["jobId"];
                if (!IsValidJobId(jobId)) throw new InvalidDataException("Invalid job id.");
                Directory.CreateDirectory(_ledgerDirectory);
                var final = RecordPath(jobId);
                var temp = final + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.Write(record.ToString(Formatting.None));
                        writer.Flush();
                        stream.Flush(true);
                    }

                    if (File.Exists(final)) File.Replace(temp, final, null);
                    else File.Move(temp, final);
                }
                finally
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
            }
        }

        /// <summary>Reads strict UTF-8 JSON without blocking a concurrent atomic replacement.</summary>
        private static JObject ReadRecordPath(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
            {
                return JObject.Parse(reader.ReadToEnd());
            }
        }

        /// <summary>Forms the record path after the caller has validated its job id.</summary>
        private static string RecordPath(string jobId)
        {
            return Path.Combine(_ledgerDirectory, jobId + ".json");
        }

        /// <summary>Reports whether a job id is safe and canonical.</summary>
        private static bool IsValidJobId(string jobId)
        {
            return !string.IsNullOrEmpty(jobId) && _jobIdPattern.IsMatch(jobId);
        }

        /// <summary>Rejects corrupt, unsupported, or internally inconsistent durable records.</summary>
        /// <param name="record">Parsed record.</param><param name="expectedJobId">Job id from its file name.</param>
        private static void ValidateRecord(JObject record, string expectedJobId)
        {
            if ((int?)record["schemaVersion"] != SCHEMA_VERSION) throw new InvalidDataException("Unsupported job schema version.");
            var jobId = (string)record["jobId"];
            if (!IsValidJobId(jobId) || jobId != expectedJobId) throw new InvalidDataException("Job id does not match its file name.");
            if (!_digestPattern.IsMatch((string)record["requestKey"] ?? string.Empty)) throw new InvalidDataException(jobId + " has an invalid requestKey.");
            if (!_digestPattern.IsMatch((string)record["argumentsHash"] ?? string.Empty)) throw new InvalidDataException(jobId + " has an invalid argumentsHash.");
            if (record["arguments"] == null || record["arguments"].Type != JTokenType.Object) throw new InvalidDataException(jobId + " has no arguments object.");
            if (Sha256Hex(CanonicalJson(record["arguments"])) != (string)record["argumentsHash"]) throw new InvalidDataException(jobId + " argumentsHash does not match its arguments.");
            if (string.IsNullOrEmpty((string)record["verb"]) || string.IsNullOrEmpty((string)record["command"])) throw new InvalidDataException(jobId + " has no command identity.");
            if (string.IsNullOrEmpty((string)record["encodingRevision"]) || string.IsNullOrEmpty((string)record["editorEpoch"])) throw new InvalidDataException(jobId + " has incomplete version identity.");
            if ((long?)record["revision"] < 1 || (long?)record["admissionSequence"] < 0) throw new InvalidDataException(jobId + " has invalid ordering fields.");
            var state = (string)record["state"];
            if (!IsState(state)) throw new InvalidDataException(jobId + " has unknown state " + state + ".");
            if (state == STATE_WAITING && ValidateRecipe(record["completionRecipe"] as JObject) != null) throw new InvalidDataException(jobId + " has an invalid completion recipe.");
            DateTime timestamp;
            if (state == STATE_WAITING && !DateTime.TryParse((string)record["barrierDeadlineUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp)) throw new InvalidDataException(jobId + " has no barrier deadline.");
            if (IsTerminal(state) && (record["outcome"]?["operation"]?.Type != JTokenType.String
                || !DateTime.TryParse((string)record["terminalUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp)))
            {
                throw new InvalidDataException(jobId + " has incomplete terminal evidence.");
            }
        }

        /// <summary>Returns the first invalid admission field, or null when the request can be hashed.</summary>
        /// <param name="sessionId">Echoed session.</param><param name="requestId">Typed request id.</param>
        /// <param name="verb">Canonical verb.</param><param name="arguments">Structured arguments.</param>
        /// <param name="command">Compatibility command.</param><param name="revision">Encoding revision.</param>
        /// <returns>First validation error, or null.</returns>
        private static string ValidateAdmission(string sessionId, JToken requestId, string verb, JObject arguments, string command, string revision)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return "Mcp-Session-Id is required for mutation admission";
            if (requestId == null || (requestId.Type != JTokenType.String && requestId.Type != JTokenType.Integer && requestId.Type != JTokenType.Float)) return "a typed string or number JSON-RPC id is required";
            if (string.IsNullOrWhiteSpace(verb) || string.IsNullOrWhiteSpace(command)) return "verb and command are required";
            if (arguments == null) return "structured arguments are required";
            if (string.IsNullOrWhiteSpace(revision)) return "encoding revision is required";
            return null;
        }

        /// <summary>Returns a recipe contract violation, or null for a complete known recipe.</summary>
        private static string ValidateRecipe(JObject recipe)
        {
            if (recipe == null) return "completion recipe is required";
            var kind = (string)recipe["kind"];
            if (kind == "post_compile_report") return recipe.Count == 1 ? null : "post_compile_report takes no fields";
            if (kind == "rerun_command")
            {
                if (recipe.Count != 2) return "rerun_command contains unexpected fields";
                return string.IsNullOrWhiteSpace((string)recipe["command"]) ? "rerun_command needs command" : null;
            }
            if (kind == "editor_state")
            {
                if (recipe.Count != 3) return "editor_state contains unexpected fields";
                var operation = (string)recipe["operation"];
                DateTime deadline;
                if (operation != "play" && operation != "stop" && operation != "pause" && operation != "resume") return "editor_state has invalid operation";
                return DateTime.TryParse((string)recipe["deadlineUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out deadline)
                    ? null
                    : "editor_state needs deadlineUtc";
            }

            return "unknown completion recipe kind";
        }

        /// <summary>Resolves a validated current record while the ledger lock is held.</summary>
        /// <param name="jobId">Job to resolve.</param><param name="record">Current record.</param>
        /// <param name="error">Lookup failure.</param><returns>True when found.</returns>
        private static bool TryCurrentLocked(string jobId, out JObject record, out string error)
        {
            record = null;
            error = null;
            if (!IsValidJobId(jobId)) { error = "invalid job id"; return false; }
            if (!_records.TryGetValue(jobId, out record)) { error = "job_not_found"; return false; }
            return true;
        }

        /// <summary>Reports whether a state belongs to the production state machine.</summary>
        private static bool IsState(string state)
        {
            return state == STATE_QUEUED || state == STATE_RUNNING || state == STATE_WAITING
                || state == STATE_COMPLETED || state == STATE_FAILED || state == STATE_CANCELLED
                || state == STATE_INTERRUPTED || state == STATE_UNKNOWN;
        }

        /// <summary>Reports whether a state can never execute again.</summary>
        private static bool IsTerminal(string state)
        {
            return state == STATE_COMPLETED || state == STATE_FAILED || state == STATE_CANCELLED
                || state == STATE_INTERRUPTED || state == STATE_UNKNOWN;
        }

        /// <summary>Reports the authoritative terminal predicate to transport response mapping.</summary>
        internal static bool IsTerminalState(string state)
        {
            return IsTerminal(state);
        }

        /// <summary>Enforces the legal source set for each public terminal transition.</summary>
        /// <param name="source">Current state.</param><param name="target">Requested terminal state.</param>
        /// <returns>True when the transition is legal.</returns>
        private static bool IsAllowedTerminalSource(string source, string target)
        {
            // WAITING completes as well as RUNNING because an editor-state job is parked WAITING to survive a
            // possible domain reload and is then finished by Unity's lifecycle callback. A transition that
            // does not reload - stop, pause, resume - is never re-dispatched, so that callback is the only
            // thing that can ever answer it, and refusing the transition here strands the job and the barrier
            // with it.
            if (target == STATE_COMPLETED) return source == STATE_RUNNING || source == STATE_WAITING;
            if (target == STATE_CANCELLED) return source == STATE_QUEUED;

            // A job waiting on a reload that never began can be settled as outcome_unknown. Its side effect
            // ran BEFORE the barrier was armed, so neither completed nor failed is true of it, and leaving it
            // nonterminal is worse than either: the job never answers and everything admitted behind it waits
            // on a barrier owner that will never finish.
            if (target == STATE_UNKNOWN) return source == STATE_WAITING || source == STATE_RUNNING;

            return (target == STATE_FAILED || target == STATE_INTERRUPTED)
                && (source == STATE_QUEUED || source == STATE_RUNNING || source == STATE_WAITING);
        }

        /// <summary>Projects a durable record into admission evidence.</summary>
        /// <param name="outcome">Admission outcome.</param><param name="record">Durable record.</param>
        /// <returns>Client-facing admission evidence.</returns>
        private static AdmissionResult AdmissionFrom(string outcome, JObject record)
        {
            return new AdmissionResult
            {
                Outcome = outcome,
                JobId = (string)record["jobId"],
                State = (string)record["state"],
                Revision = (long)record["revision"],
            };
        }

        /// <summary>Prunes terminal records by count and age without ever deleting nonterminal work.</summary>
        private static void PruneTerminalRecordsLocked()
        {
            var terminals = new List<JObject>();
            foreach (var record in _records.Values) if (IsTerminal((string)record["state"])) terminals.Add(record);
            terminals.Sort((left, right) => string.CompareOrdinal((string)right["terminalUtc"], (string)left["terminalUtc"]));
            for (var index = 0; index < terminals.Count; index++)
            {
                DateTime terminalUtc;
                var old = DateTime.TryParse((string)terminals[index]["terminalUtc"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out terminalUtc)
                    && ContractGateWouldPrune(index, terminalUtc, DateTime.UtcNow);
                if (!old) continue;
                var jobId = (string)terminals[index]["jobId"];
                File.Delete(RecordPath(jobId));
                _records.Remove(jobId);
                _requestIndex.Remove((string)terminals[index]["requestKey"]);
            }
        }

        /// <summary>Publishes an immutable worker-readable snapshot while the ledger lock is held.</summary>
        private static void RefreshHealthLocked()
        {
            var snapshotTicks = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref _healthSnapshotUtcTicks, snapshotTicks);
            var health = new JObject
            {
                ["ready"] = _ready,
                ["state"] = _ready ? "ready" : (_readinessError == null ? "starting" : "degraded"),
                ["error"] = _readinessError == null ? JValue.CreateNull() : new JValue(_readinessError),
                ["editorEpoch"] = EditorEpoch,
                ["recordCount"] = _records.Count,
                ["queuedCount"] = _dispatchQueue.Count,
                ["barrierOwnerJobId"] = _barrierOwnerJobId == null ? JValue.CreateNull() : new JValue(_barrierOwnerJobId),
                ["suggestedRetryAfterSeconds"] = _suggestedRetryAfterSeconds,
                ["snapshotUtc"] = new DateTime(snapshotTicks, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
            };
            Volatile.Write(ref _healthJson, health.ToString(Formatting.None));
        }

        #endregion

        #region Canonical JSON

        /// <summary>Returns the full SHA-256 digest of Conduit canonical JSON v1.</summary>
        /// <param name="arguments">Structured arguments.</param>
        /// <returns>Lowercase full SHA-256.</returns>
        internal static string ArgumentsHash(JObject arguments)
        {
            return Sha256Hex(CanonicalJson(arguments));
        }

        /// <summary>Renders object keys in ordinal order and normalizes JSON numbers for stable hashing.</summary>
        /// <param name="token">JSON token to canonicalize.</param>
        /// <returns>Conduit canonical JSON v1.</returns>
        internal static string CanonicalJson(JToken token)
        {
            var text = new StringBuilder();
            AppendCanonical(text, token);
            return text.ToString();
        }

        /// <summary>Appends one token in Conduit canonical JSON v1 form.</summary>
        private static void AppendCanonical(StringBuilder text, JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined) { text.Append("null"); return; }
            var asObject = token as JObject;
            if (asObject != null)
            {
                var names = new List<string>();
                foreach (var property in asObject.Properties()) names.Add(property.Name);
                names.Sort(StringComparer.Ordinal);
                text.Append('{');
                for (var index = 0; index < names.Count; index++)
                {
                    if (index > 0) text.Append(',');
                    text.Append(JsonConvert.ToString(names[index])).Append(':');
                    AppendCanonical(text, asObject[names[index]]);
                }
                text.Append('}');
                return;
            }

            var asArray = token as JArray;
            if (asArray != null)
            {
                text.Append('[');
                for (var index = 0; index < asArray.Count; index++) { if (index > 0) text.Append(','); AppendCanonical(text, asArray[index]); }
                text.Append(']');
                return;
            }

            if (token.Type == JTokenType.String) { text.Append(JsonConvert.ToString((string)token)); return; }
            if (token.Type == JTokenType.Boolean) { text.Append((bool)token ? "true" : "false"); return; }
            if (token.Type == JTokenType.Integer) { text.Append(Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture)); return; }
            if (token.Type == JTokenType.Float) { text.Append(NormalizeNumber(((JValue)token).Value)); return; }
            throw new InvalidDataException("Unsupported JSON token in canonical arguments: " + token.Type + ".");
        }

        /// <summary>Normalizes a finite JSON number across integer-like floats and exponent spellings.</summary>
        private static string NormalizeNumber(object value)
        {
            if (value is decimal) return ((decimal)value).ToString("G29", CultureInfo.InvariantCulture);
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidDataException("Non-finite JSON number.");
            if (number == 0d) return "0";
            var rendered = number.ToString("R", CultureInfo.InvariantCulture);
            var marker = rendered.IndexOfAny(new[] { 'e', 'E' });
            if (marker < 0) return rendered;
            var exponent = int.Parse(rendered.Substring(marker + 1), NumberStyles.Integer, CultureInfo.InvariantCulture);
            return rendered.Substring(0, marker) + "e" + exponent.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Clones a token with every object key in ordinal order.</summary>
        private static JToken CanonicalClone(JToken token)
        {
            var asObject = token as JObject;
            if (asObject != null)
            {
                var result = new JObject();
                var names = new List<string>();
                foreach (var property in asObject.Properties()) names.Add(property.Name);
                names.Sort(StringComparer.Ordinal);
                foreach (var name in names) result[name] = CanonicalClone(asObject[name]);
                return result;
            }
            var asArray = token as JArray;
            if (asArray != null)
            {
                var result = new JArray();
                foreach (var item in asArray) result.Add(CanonicalClone(item));
                return result;
            }
            return token == null ? JValue.CreateNull() : token.DeepClone();
        }

        /// <summary>Returns lowercase full SHA-256 of UTF-8 text.</summary>
        private static string Sha256Hex(string value)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var text = new StringBuilder(hash.Length * 2);
                foreach (var part in hash) text.Append(part.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        #endregion
    }
}
