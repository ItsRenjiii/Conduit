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
    /// Editor lifecycle: play, pause and stop, and the durable transition marker.
    /// </summary>
    internal static partial class ConduitBridge
    {
        /// <summary>
        /// Requests entering or leaving Play Mode and withholds the command response until Unity reaches
        /// the matching settled lifecycle callback.
        /// </summary>
        /// <param name="shouldPlay">True to enter Play Mode; false to return to Edit Mode.</param>
        /// <returns>An immediate no-op or refusal report; successful transitions complete through their deferred response.</returns>
        private static string SetPlayMode(bool shouldPlay)
        {
            if (EditorApplication.isCompiling)
            {
                return "Cannot change Play Mode while Unity is compiling." + Environment.NewLine + FormatEditorStatus();
            }

            if (EditorApplication.isUpdating)
            {
                return "Cannot change Play Mode while Unity is importing/updating assets." + Environment.NewLine + FormatEditorStatus();
            }

            if (shouldPlay && EditorApplication.isPlaying)
            {
                return "Unity is already in Play Mode." + Environment.NewLine + FormatEditorStatus();
            }

            if (shouldPlay && EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Cannot enter Play Mode because another play-mode transition is already in progress." + Environment.NewLine + FormatEditorStatus();
            }

            if (!shouldPlay && !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Unity is already in Edit Mode." + Environment.NewLine + FormatEditorStatus();
            }

            if (!shouldPlay && !EditorApplication.isPlaying)
            {
                return "Cannot stop Play Mode because another play-mode transition is already in progress." + Environment.NewLine + FormatEditorStatus();
            }

            var operation = shouldPlay ? "play" : "stop";
            string deferError;
            if (!TryDeferUntilEditorState(operation, out deferError))
            {
                return "Cannot request '" + operation + "': " + deferError + Environment.NewLine + FormatEditorStatus();
            }

            try
            {
                EditorApplication.isPlaying = shouldPlay;
                return string.Empty;
            }
            catch (Exception ex)
            {
                string cleanupError;
                TryClearPendingEditorStateMarker(out cleanupError);
                if (_commandContext != null) _commandContext.Deferred = false;
                var cleanupReport = string.IsNullOrEmpty(cleanupError)
                    ? string.Empty
                    : Environment.NewLine + "Pending transition cleanup also failed: " + cleanupError;
                return "Cannot request '" + operation + "': " + ex.Message + cleanupReport + Environment.NewLine + FormatEditorStatus();
            }
        }

        /// <summary>
        /// Requests pausing or resuming an active Play Mode session and withholds the command response until
        /// Unity reports the matching pause-state callback.
        /// </summary>
        /// <param name="shouldPause">True to pause the active player loop; false to resume it.</param>
        /// <returns>An immediate no-op or refusal report; successful transitions complete through their deferred response.</returns>
        private static string SetPauseMode(bool shouldPause)
        {
            if (EditorApplication.isCompiling)
            {
                return "Cannot change pause state while Unity is compiling." + Environment.NewLine + FormatEditorStatus();
            }

            if (EditorApplication.isUpdating)
            {
                return "Cannot change pause state while Unity is importing/updating assets." + Environment.NewLine + FormatEditorStatus();
            }

            if (!EditorApplication.isPlaying)
            {
                return "Cannot change pause state because Unity is not in Play Mode." + Environment.NewLine + FormatEditorStatus();
            }

            if (shouldPause == EditorApplication.isPaused)
            {
                return shouldPause
                    ? "Unity is already paused." + Environment.NewLine + FormatEditorStatus()
                    : "Unity is already running and not paused." + Environment.NewLine + FormatEditorStatus();
            }

            var operation = shouldPause ? "pause" : "resume";
            string deferError;
            if (!TryDeferUntilEditorState(operation, out deferError))
            {
                return "Cannot request '" + operation + "': " + deferError + Environment.NewLine + FormatEditorStatus();
            }

            try
            {
                EditorApplication.isPaused = shouldPause;
                return string.Empty;
            }
            catch (Exception ex)
            {
                string cleanupError;
                TryClearPendingEditorStateMarker(out cleanupError);
                if (_commandContext != null) _commandContext.Deferred = false;
                var cleanupReport = string.IsNullOrEmpty(cleanupError)
                    ? string.Empty
                    : Environment.NewLine + "Pending transition cleanup also failed: " + cleanupError;
                return "Cannot request '" + operation + "': " + ex.Message + cleanupReport + Environment.NewLine + FormatEditorStatus();
            }
        }

        /// <summary>
        /// Persists the current response channel and requested editor-state operation before any lifecycle
        /// mutation occurs, allowing the reply to survive a play-mode domain reload.
        /// </summary>
        /// <param name="operation">One of play, stop, pause, or resume.</param>
        /// <param name="error">Reason the deferral could not be established.</param>
        /// <returns>True when the durable marker was written and the command response is being withheld.</returns>
        private static bool TryDeferUntilEditorState(string operation, out string error)
        {
            error = null;
            var context = _commandContext;
            if (context == null)
            {
                error = "No active response channel is available.";
                return false;
            }

            var jobId = context.JobId ?? string.Empty;
            if (jobId.Length == 0)
            {
                error = "No active response channel is available.";
                return false;
            }

            if (File.Exists(PENDING_EDITOR_STATE_RESPONSE_PATH))
            {
                error = "Another editor-state transition still owns the pending response marker.";
                return false;
            }

            try
            {
                Directory.CreateDirectory("Temp");
                var deadlineUtc = DateTimeOffset.UtcNow.AddSeconds(EDITOR_STATE_TRANSITION_TIMEOUT_SECONDS);
                File.WriteAllText(
                    PENDING_EDITOR_STATE_RESPONSE_PATH,
                    operation + "\n" +
                    deadlineUtc.ToString("O", CultureInfo.InvariantCulture) + "\n" +
                    jobId,
                    Encoding.UTF8);

                // A job has to survive whatever this transition does to the AppDomain, and which transitions
                // reload is not something to predict: a RUNNING record is recovered to outcome_unknown by a
                // reload, while a WAITING one survives it. So every operation parks WAITING, and Unity's own
                // EnteredPlayMode, EnteredEditMode or pauseStateChanged callback completes it from the marker
                // written above. Nothing here waits for a reload or samples editor state.
                if (jobId.Length != 0)
                {
                    var jobDeadlineUtc = DateTime.UtcNow.AddSeconds(EDITOR_STATE_TRANSITION_TIMEOUT_SECONDS);
                    var recipe = new JObject
                    {
                        ["kind"] = "editor_state",
                        ["operation"] = operation,
                        ["deadlineUtc"] = jobDeadlineUtc.ToString("O", CultureInfo.InvariantCulture),
                    };

                    string ledgerError;
                    if (!ConduitJobLedger.MarkWaitingForReload(jobId, recipe, jobDeadlineUtc, out ledgerError))
                    {
                        string markerCleanupError;
                        TryClearPendingEditorStateMarker(out markerCleanupError);
                        error = "Could not persist the deferral for job " + jobId + ": " + ledgerError;
                        return false;
                    }
                }

                context.Deferred = true;
                EditorApplication.update -= PendingEditorStateMonitorTick;
                EditorApplication.update += PendingEditorStateMonitorTick;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Registers exact play and pause lifecycle callbacks after every editor assembly load, and resumes
        /// monitoring any durable transition marker that survived a domain reload.
        /// </summary>
        private static void RegisterEditorStateTransitionCallbacks()
        {
            EditorApplication.playModeStateChanged -= HandlePlayModeStateChanged;
            EditorApplication.playModeStateChanged += HandlePlayModeStateChanged;
            EditorApplication.pauseStateChanged -= HandlePauseStateChanged;
            EditorApplication.pauseStateChanged += HandlePauseStateChanged;
            EditorApplication.update -= PendingEditorStateMonitorTick;
            if (File.Exists(PENDING_EDITOR_STATE_RESPONSE_PATH))
            {
                EditorApplication.update += PendingEditorStateMonitorTick;
            }
        }

        /// <summary>
        /// Completes play and stop commands only from Unity's exact entered-state callbacks.
        /// </summary>
        /// <param name="state">Play-mode lifecycle state emitted by Unity.</param>
        private static void HandlePlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
            {
                ResolvePendingEditorState("play", EditorStateCompletionMessage("play"));
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
            {
                ResolvePendingEditorState("stop", EditorStateCompletionMessage("stop"));
            }
        }

        /// <summary>
        /// Completes pause and resume commands only from Unity's exact pause-state callbacks.
        /// </summary>
        /// <param name="state">Pause state emitted by Unity.</param>
        private static void HandlePauseStateChanged(PauseState state)
        {
            if (state == PauseState.Paused)
            {
                ResolvePendingEditorState("pause", EditorStateCompletionMessage("pause"));
            }
            else if (state == PauseState.Unpaused)
            {
                ResolvePendingEditorState("resume", EditorStateCompletionMessage("resume"));
            }
        }

        /// <summary>
        /// Recovers a missed lifecycle callback from settled editor state and bounds every deferred transition
        /// with a timeout shorter than the bridge transport timeout.
        /// </summary>
        private static void PendingEditorStateMonitorTick()
        {
            if (!File.Exists(PENDING_EDITOR_STATE_RESPONSE_PATH))
            {
                EditorApplication.update -= PendingEditorStateMonitorTick;
                return;
            }

            string operation;
            DateTimeOffset deadlineUtc;
            string pendingJobId;
            string readError;
            if (!TryReadPendingEditorState(out operation, out deadlineUtc, out pendingJobId, out readError))
            {
                // An unreadable marker can only be cleared: with no job id there is nothing to complete
                // against, and leaving it refuses the NEXT transition as still owned.
                string cleanupError;
                TryClearPendingEditorStateMarker(out cleanupError);
                return;
            }

            if (IsEditorStateSatisfied(operation))
            {
                ResolvePendingEditorState(operation, EditorStateCompletionMessage(operation));
                return;
            }

            if (DateTimeOffset.UtcNow < deadlineUtc)
            {
                return;
            }

            ResolvePendingEditorState(
                operation,
                "ERROR: Timed out waiting for editor-state operation '" + operation + "' to settle within " +
                EDITOR_STATE_TRANSITION_TIMEOUT_SECONDS.ToString("0", CultureInfo.InvariantCulture) + " seconds.");
        }

        /// <summary>
        /// Reads and validates the durable editor-state transition marker.
        /// </summary>
        /// <param name="operation">Requested play, stop, pause, or resume operation.</param>
        /// <param name="deadlineUtc">UTC time after which the pending command must fail truthfully.</param>
        /// <param name="jobId">Durable job that owns the transition.</param>
        /// <param name="error">Validation or file-system error.</param>
        /// <returns>True when all marker fields are present and valid.</returns>
        private static bool TryReadPendingEditorState(
            out string operation,
            out DateTimeOffset deadlineUtc,
            out string jobId,
            out string error)
        {
            operation = null;
            deadlineUtc = default(DateTimeOffset);
            jobId = null;
            error = null;

            try
            {
                var lines = File.ReadAllText(PENDING_EDITOR_STATE_RESPONSE_PATH, Encoding.UTF8)
                    .Replace("\r\n", "\n")
                    .Split('\n');
                if (lines.Length > 0) operation = lines[0].Trim();
                if (lines.Length > 2) jobId = lines[2].Trim();

                // Every transition is completed against the ledger, so the job id is the whole channel.
                if (string.IsNullOrEmpty(jobId))
                {
                    error = "The durable job id is missing.";
                    return false;
                }

                if (operation != "play" && operation != "stop" && operation != "pause" && operation != "resume")
                {
                    error = "The operation is missing or unsupported.";
                    return false;
                }

                if (lines.Length < 2 ||
                    !DateTimeOffset.TryParseExact(
                        lines[1].Trim(),
                        "O",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out deadlineUtc))
                {
                    error = "The transition deadline is missing or invalid.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Writes a deferred lifecycle result to its original response path after verifying that the stored
        /// operation matches the callback that attempted completion.
        /// </summary>
        /// <param name="expectedOperation">Operation associated with the lifecycle callback or recovery check.</param>
        /// <param name="result">Specific completion or timeout line to return.</param>
        private static void ResolvePendingEditorState(string expectedOperation, string result)
        {
            if (!File.Exists(PENDING_EDITOR_STATE_RESPONSE_PATH))
            {
                return;
            }

            string storedOperation;
            DateTimeOffset deadlineUtc;
            string pendingJobId;
            string readError;
            if (!TryReadPendingEditorState(out storedOperation, out deadlineUtc, out pendingJobId, out readError) ||
                !string.Equals(storedOperation, expectedOperation, StringComparison.Ordinal))
            {
                return;
            }

            // The transition is completed against the ledger. Pause and resume arrive here because they raise
            // no reload, so their job is still running and this is the only thing that will ever finish it.
            string clearErrorForJob;
            TryClearPendingEditorStateMarker(out clearErrorForJob);
            string completionError;
            if (!ConduitJobLedger.Complete(
                    pendingJobId,
                    storedOperation,
                    result + Environment.NewLine + FormatEditorStatus(),
                    out completionError))
            {
                Debug.LogWarning(
                    "[Conduit] Could not complete editor-state job " + pendingJobId + ": " + completionError);
            }
        }

        /// <summary>
        /// Returns whether Unity currently satisfies the durable operation, used only as recovery when an
        /// exact lifecycle callback was missed.
        /// </summary>
        /// <param name="operation">Requested play, stop, pause, or resume operation.</param>
        /// <returns>True only when the corresponding settled state is observable.</returns>
        private static bool IsEditorStateSatisfied(string operation)
        {
            if (operation == "play")
            {
                return EditorApplication.isPlaying;
            }

            if (operation == "stop")
            {
                return !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode;
            }

            if (operation == "pause")
            {
                return EditorApplication.isPlaying && EditorApplication.isPaused;
            }

            return operation == "resume" && EditorApplication.isPlaying && !EditorApplication.isPaused;
        }

        /// <summary>
        /// Returns the specific success line for a completed editor-state operation.
        /// </summary>
        /// <param name="operation">Completed play, stop, pause, or resume operation.</param>
        /// <returns>Human-readable settled-state confirmation.</returns>
        private static string EditorStateCompletionMessage(string operation)
        {
            if (operation == "play") return "Unity entered Play Mode.";
            if (operation == "stop") return "Unity entered Edit Mode.";
            if (operation == "pause") return "Unity paused Play Mode.";
            if (operation == "resume") return "Unity resumed Play Mode.";
            return "Unity editor state changed.";
        }

        /// <summary>
        /// Removes the durable editor-state marker and unregisters its monitor.
        /// </summary>
        /// <param name="error">File-system error when the marker could not be removed.</param>
        /// <returns>True when no pending marker remains.</returns>
        private static bool TryClearPendingEditorStateMarker(out string error)
        {
            error = null;
            EditorApplication.update -= PendingEditorStateMonitorTick;
            try
            {
                if (File.Exists(PENDING_EDITOR_STATE_RESPONSE_PATH))
                {
                    File.Delete(PENDING_EDITOR_STATE_RESPONSE_PATH);
                }

                if (File.Exists(PENDING_EDITOR_STATE_RESPONSE_PATH))
                {
                    error = "The marker file still exists after deletion.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Formats the current editor play, pause, compile, update, and queued UdonSharp authoring state.
        ///
        /// Every flag is read from <see cref="ConduitEditorActivity"/> rather than from EditorApplication,
        /// which is what lets this answer from any thread. It is not a cached approximation: each of these
        /// states can only change ON the main thread, so while that thread is busy nothing is able to move
        /// them, and the capture is refreshed by Unity's own transition events and by every update tick.
        /// The queued-authoring marker is a file test, which never needed a thread in the first place.
        /// </summary>
        /// <returns>Text report for editor lifecycle state.</returns>
        internal static string FormatEditorStatus()
        {
            var state = ConduitEditorActivity.Current.State;
            var builder = new StringBuilder();
            builder.AppendLine("isPlaying: " + state.Playing);
            builder.AppendLine("isPlayingOrWillChangePlaymode: " + state.WillChangePlaymode);
            builder.AppendLine("isPaused: " + state.Paused);
            builder.AppendLine("isCompiling: " + state.Compiling);
            builder.AppendLine("isUpdating: " + state.Updating);
            builder.AppendLine("isUdonSharpCreatePending: " + File.Exists(PendingUdonSharpCreatePath));
            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Answers the verbs that need no main thread at all, so a caller can still learn the editor's state
        /// while the main thread is busy with the very work being asked about.
        ///
        /// The set is deliberately small. A verb belongs here only when every value it reports is readable
        /// from any thread: the captured lifecycle flags, one file test, a constant, and a clock that was
        /// measured advancing in step with wall time through a blocked main thread, so it reproduces exactly
        /// from an anchor. A value that merely looked stable would not qualify.
        /// </summary>
        /// <param name="verb">The leading verb of the command.</param>
        /// <param name="result">Receives the report when the verb was answered here.</param>
        /// <returns>True when the verb needs no main thread and <paramref name="result"/> is set.</returns>
        internal static bool TryExecuteThreadFree(string verb, out string result)
        {
            result = null;

            if (verb == "status")
            {
                result = FormatEditorStatus();
                return true;
            }

            if (verb == "editor-application-get-state")
            {
                result = EditorApplicationGetStateCommand();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Routes the EditorState verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchEditorState(string verb, string command, string[] parts)
        {
            if (verb == "editor-application-get-state")
            {
                return EditorApplicationGetStateCommand();
            }

            if (verb == "editor-application-set-state")
            {
                return EditorApplicationSetStateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "play")
            {
                return SetPlayMode(true);
            }

            if (verb == "stop")
            {
                return SetPlayMode(false);
            }

            if (verb == "toggleplay")
            {
                return SetPlayMode(!EditorApplication.isPlaying);
            }

            if (verb == "pause")
            {
                return SetPauseMode(true);
            }

            if (verb == "resume")
            {
                return SetPauseMode(false);
            }

            if (verb == "togglepause")
            {
                return SetPauseMode(!EditorApplication.isPaused);
            }


            return null;
        }
    }
}
