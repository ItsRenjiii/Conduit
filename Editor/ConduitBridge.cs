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
    /// ItsRenji Conduit - the Unity Editor side of the agent bridge. Loaded on editor start
    /// (<see cref="InitializeOnLoadAttribute"/>), it runs each verb on the editor main thread for the HTTP
    /// endpoint, which admits work to the durable job ledger and answers from its terminal record. Verbs
    /// are dispatched in <see cref="ExecuteCommand"/>; the canonical command list lives in
    /// <see cref="CommandSignatures"/> and is surfaced for discovery via the <c>help</c> (text) and
    /// <c>commands</c> (JSON) verbs. The class is split by area across the ConduitBridge.*.cs partials.
    /// </summary>
    [InitializeOnLoad]
    internal static partial class ConduitBridge
    {
        /// <summary>Tool-list epoch sentinel watched by MCP clients so they can refresh exposed tools after script reloads.</summary>
        private const string ToolsEpochPath = "Temp/ConduitToolsEpoch.txt";

        /// <summary>Durable marker for play and pause transitions, storing the operation, its deadline, and the owning job id across domain reloads.</summary>
        private const string PENDING_EDITOR_STATE_RESPONSE_PATH = "Temp/ConduitPendingEditorStateResponse.txt";

        /// <summary>Maximum time a play or pause transition may remain unresolved before its command receives a truthful timeout response.</summary>
        private const double EDITOR_STATE_TRANSITION_TIMEOUT_SECONDS = 60d;

        /// <summary>Heartbeat file proving the editor update loop is ticking: timestamp, focus, play state, about once per second. MCP servers watch it to fast-fail while no editor is running and to wake the moment one appears.</summary>
        internal const string HeartbeatPath = "Temp/ConduitHeartbeat.txt";

        /// <summary>
        /// Reload-safe marker containing a UdonSharp source path whose program asset must be linked once
        /// Unity has compiled the new MonoScript. Internal because its mere existence is the whole answer to
        /// one of the status fields, and a file test is the one such answer any thread can make for itself.
        /// </summary>
        internal const string PendingUdonSharpCreatePath = "Temp/ConduitPendingUdonSharpCreate.txt";

        /// <summary>Diagnostic report from the most recent background UdonSharp program-asset link attempt.</summary>
        private const string LastUdonSharpCreateResultPath = "Temp/ConduitLastUdonSharpCreateResult.txt";

        /// <summary>
        /// SessionState key holding the Console entry count captured just before a deferring command triggers
        /// its compile. The post-compile report uses it as the floor for "this compile", because the
        /// compile-finished line it otherwise keys on is not always logged.
        /// </summary>
        private const string ConsoleBaselineSessionKey = "ItsRenji.Conduit.PreCompileConsoleCount";

        /// <summary>
        /// State belonging to a single executing command: which durable job owns it, whether it withheld its
        /// response, and whether it is the post-reload re-run of a command that already deferred once.
        ///
        /// These were three loose statics, and two callers sharing one set of globals is how one command's
        /// result ends up answering another. Grouping them is not by itself a concurrency fix - the
        /// dispatcher is still main-thread serialized, so one current context is enough - but it removes the
        /// cross-talk.
        /// </summary>
        private sealed class CommandExecutionContext
        {
            /// <summary>True once this command has withheld its response pending a compile or transition.</summary>
            public bool Deferred;

            /// <summary>True when this is the post-reload re-run, which must never defer a second time.</summary>
            public bool CompletingDeferred;

            /// <summary>Durable job this command belongs to, or null when it cannot defer at all.</summary>
            public string JobId;

            /// <summary>
            /// Client whose bearer admitted this command, or empty when nothing identifies one. It is the
            /// return address a verb needs when its work will finish after the call has already answered.
            /// </summary>
            public string Client;

            /// <summary>
            /// True once this command has opened its own undo group. One command is one undo entry, and the
            /// group is opened by the first operation that actually registers an undo rather than up front,
            /// so a command that turns out to register nothing leaves no entry behind.
            /// </summary>
            public bool UndoGroupOpened;
        }

        /// <summary>The command currently executing, or null when none is.</summary>
        private static CommandExecutionContext _commandContext;

        /// <summary>
        /// Runs one command with its own execution context installed, restoring whatever was there before. The
        /// restore sits in a finally so a throwing verb cannot leave the next command reading a dead context.
        /// </summary>
        /// <param name="context">Context to install for the duration of this command.</param>
        /// <param name="command">Raw command line.</param>
        /// <returns>Command result text or JSON returned to the caller.</returns>
        private static string ExecuteWithContext(CommandExecutionContext context, string command)
        {
            var previous = _commandContext;
            _commandContext = context;
            try
            {
                using (ConduitProfiling.Command(command).Auto())
                {
                    return ExecuteCommand(command);
                }
            }
            finally
            {
                _commandContext = previous;
            }
        }

        /// <summary>Seconds to wait for a compile or reload before completing a deferral in the current domain.</summary>
        private const double DeferWatchdogGraceSeconds = 6.0;

        /// <summary>Next editor time at which the heartbeat file should be rewritten.</summary>
        private static double _nextHeartbeatTime;

        /// <summary>
        /// Registers bridge callbacks when Unity loads the editor assembly.
        /// </summary>
        static ConduitBridge()
        {
            // An AssetImportWorker is this same editor binary launched with -batchMode, and it loads this
            // assembly, so without this guard every worker ALSO writes the same Temp files and resumes
            // pending work: N processes racing the main editor. A worker has no message pump either, so any
            // editor window raised by the work that follows wedges it permanently while it holds the whole
            // project heap.
            if (UnityEngine.Application.isBatchMode) return;

            MarkLoaded();
            EditorApplication.update -= HeartbeatTick;
            EditorApplication.update += HeartbeatTick;
            EditorApplication.update -= TickVrcDriver;
            EditorApplication.update += TickVrcDriver;
            EditorApplication.update -= TickVrcUseRelease;
            EditorApplication.update += TickVrcUseRelease;
            RegisterEditorStateTransitionCallbacks();
            ResumePendingUdonSharpCreate();
        }

        /// <summary>
        /// Re-registers update callbacks after Unity finishes an editor load or domain reload.
        /// </summary>
        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            // Import workers load this assembly too; see the static constructor above.
            if (UnityEngine.Application.isBatchMode) return;

            MarkLoaded();
            EditorApplication.update -= HeartbeatTick;
            EditorApplication.update += HeartbeatTick;
            EditorApplication.update -= TickVrcDriver;
            EditorApplication.update += TickVrcDriver;
            EditorApplication.update -= TickVrcUseRelease;
            EditorApplication.update += TickVrcUseRelease;
            RegisterEditorStateTransitionCallbacks();
            ResumePendingUdonSharpCreate();
        }

        /// <summary>
        /// Bumps the tools-epoch sentinel after every script recompile, for a stdio server that watches it.
        /// The HTTP transport needs no prompt: at domain load it compares the catalogue revision it publishes
        /// against the one the previous domain left behind and tells attached clients only when they differ.
        /// Fires only on script reloads (not play-mode domain reloads), which is exactly when the tool
        /// surface can change.
        /// </summary>
        [UnityEditor.Callbacks.DidReloadScripts]
        private static void RefreshToolsOnScriptReload()
        {
            ResumePendingUdonSharpCreate();
            BumpToolsEpoch();
        }

        /// <summary>
        /// Registers the idle callback that completes a queued UdonSharp create after Unity produces the
        /// new MonoScript. The marker survives domain reloads and terminalises the durable job that an MCP
        /// response handoff may still be waiting to return.
        /// </summary>
        private static void ResumePendingUdonSharpCreate()
        {
            EditorApplication.update -= CompletePendingUdonSharpCreateTick;
            if (File.Exists(PendingUdonSharpCreatePath))
            {
                EditorApplication.update += CompletePendingUdonSharpCreateTick;
            }
        }

        /// <summary>
        /// Queues a UdonSharp source path for program-asset linking after compilation without withholding the
        /// current bridge response.
        /// </summary>
        /// <param name="csPath">Normalized source path under Assets or a writable embedded package.</param>
        /// <param name="error">File-system error when the durable marker could not be written.</param>
        /// <returns>True when the path was persisted and its completion callback was armed.</returns>
        private static bool TryQueuePendingUdonSharpCreate(string csPath, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory("Temp");
                File.WriteAllText(PendingUdonSharpCreatePath, csPath, Encoding.UTF8);
                ResumePendingUdonSharpCreate();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Waits until Unity is idle and the new MonoScript exists, then completes the normal UdonSharp
        /// program-asset linking path. A failed attempt remains queued for the next script reload.
        /// </summary>
        private static void CompletePendingUdonSharpCreateTick()
        {
            if (!File.Exists(PendingUdonSharpCreatePath))
            {
                EditorApplication.update -= CompletePendingUdonSharpCreateTick;
                return;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return;
            }

            string csPath;
            try
            {
                csPath = File.ReadAllText(PendingUdonSharpCreatePath, Encoding.UTF8).Trim();
            }
            catch (Exception ex)
            {
                WriteLastUdonSharpCreateResult("ERROR reading pending UdonSharp create marker: " + ex.Message);
                EditorApplication.update -= CompletePendingUdonSharpCreateTick;
                return;
            }

            if (string.IsNullOrEmpty(csPath))
            {
                WriteLastUdonSharpCreateResult("ERROR pending UdonSharp create marker contained no source path.");
                try { File.Delete(PendingUdonSharpCreatePath); } catch { /* best effort */ }
                EditorApplication.update -= CompletePendingUdonSharpCreateTick;
                return;
            }

            if (AssetDatabase.LoadAssetAtPath<MonoScript>(csPath) == null)
            {
                return;
            }

            EditorApplication.update -= CompletePendingUdonSharpCreateTick;

            string result;
            try
            {
                result = CreateUdonSharpScript(csPath);
            }
            catch (Exception ex)
            {
                result = "ERROR completing queued UdonSharp create" + Environment.NewLine + ex;
            }

            WriteLastUdonSharpCreateResult(result);
            if (result.StartsWith("U# behaviour ready:", StringComparison.Ordinal))
            {
                try { File.Delete(PendingUdonSharpCreatePath); } catch { /* best effort */ }
            }
        }

        /// <summary>Writes a durable diagnostic report for the latest queued UdonSharp link attempt.</summary>
        /// <param name="result">Human-readable completion or error report.</param>
        private static void WriteLastUdonSharpCreateResult(string result)
        {
            try
            {
                Directory.CreateDirectory("Temp");
                File.WriteAllText(LastUdonSharpCreateResultPath, result ?? string.Empty, Encoding.UTF8);
            }
            catch
            {
                // Diagnostics must never interfere with the authoring operation.
            }
        }

        /// <summary>
        /// Withholds the currently-executing command's response and records the response path plus a
        /// command to re-run after the next domain reload. Used when a result only exists post-compile
        /// (e.g. a freshly-written UdonSharp script's MonoScript). Returns false if deferral isn't possible
        /// (no response channel, or we're already completing a deferred command) so the caller can fall back.
        /// </summary>
        /// <param name="rerunCommand">Command to run after reload, or null to produce a generic post-compile report.</param>
        /// <returns>True when the command response was deferred successfully.</returns>
        private static bool TryDeferUntilReload(string rerunCommand)
        {
            var context = _commandContext;
            if (context == null || context.CompletingDeferred)
            {
                return false;
            }

            // Record how many Console entries exist BEFORE the caller triggers its compile, so the report can
            // tell this compile's errors from the ones already sitting in the buffer. SessionState because the
            // reload this deferral exists for wipes statics.
            SessionState.SetInt(ConsoleBaselineSessionKey, ReadConsoleEntries().Count);

            // A durable job records its continuation in the ledger rather than in a response file, because a
            // job has no file channel to withhold.
            if (!string.IsNullOrEmpty(context.JobId))
            {
                var recipe = string.IsNullOrEmpty(rerunCommand)
                    ? new JObject { ["kind"] = "post_compile_report" }
                    : new JObject { ["kind"] = "rerun_command", ["command"] = rerunCommand };

                // Durability has to land before the caller triggers its compile. If this throws, the handler
                // must not carry on and fire the side effect anyway: a reload with nothing durable behind it
                // produces work no one can attribute or collect, which is worse than a failed command.
                string ledgerError;
                if (!ConduitJobLedger.MarkWaitingForReload(
                        context.JobId, recipe, DateTime.UtcNow.AddSeconds(DeferWatchdogGraceSeconds), out ledgerError))
                {
                    throw new InvalidOperationException(
                        "could not persist the deferral for job " + context.JobId + ": " + ledgerError);
                }

                // The ledger owns the barrier and the watchdog for a job.
                context.Deferred = true;
                return true;
            }

            // Without a durable job there is no response channel to withhold. HTTP is the only ingress and
            // every request it admits carries a job id, so reaching here means the caller cannot defer.
            return false;
        }

        /// <summary>
        /// Runs one command with no response channel and no job, for a caller that receives the result on the
        /// request that asked for it. A verb that tries to defer finds neither a response path nor a job id
        /// and declines to defer, which is exactly why only non-deferring verbs may be dispatched this way -
        /// a compiling verb sent through here would return before its compile settled.
        /// </summary>
        /// <param name="command">Raw command line.</param>
        /// <returns>Command result text or JSON returned to the caller.</returns>
        internal static string ExecuteImmediate(string command)
        {
            return ExecuteWithContext(new CommandExecutionContext(), command);
        }

        /// <summary>
        /// Runs one durable job on the main thread. An ordinary lease executes its admitted command; a lease
        /// carrying a recovered recipe finishes the work a previous domain left waiting, which is why the
        /// re-run is marked CompletingDeferred and cannot defer a second time.
        /// </summary>
        /// <param name="lease">The claimed lease, already persisted as running.</param>
        /// <param name="deferred">True when the job withheld its result pending a reload.</param>
        /// <returns>The result text to store against the job.</returns>
        internal static string ExecuteJob(ConduitJobLedger.JobLease lease, out bool deferred)
        {
            var recipe = lease.CompletionRecipe;
            if (recipe == null)
            {
                var context = new CommandExecutionContext { JobId = lease.JobId, Client = lease.Client };
                var result = ExecuteWithContext(context, lease.Command);
                deferred = context.Deferred;
                return result;
            }

            var kind = (string)recipe["kind"];
            if (kind == "post_compile_report")
            {
                deferred = false;
                return BuildPostCompileReport();
            }

            if (kind == "rerun_command")
            {
                var rerun = new CommandExecutionContext
                {
                    JobId = lease.JobId,
                    Client = lease.Client,
                    CompletingDeferred = true,
                };
                var result = ExecuteWithContext(rerun, (string)recipe["command"]);
                deferred = rerun.Deferred;
                return result;
            }

            if (kind == "editor_state")
            {
                // Normally unreachable: the lifecycle callback completes an editor-state job and the record is
                // terminal before this lease could be taken. It survives as the recovery answer for a reload
                // that dropped the callback, and it clears the marker the callback would have cleared - a
                // marker left behind refuses the NEXT transition as still owned. Nothing is sampled here: the
                // reload this job was parked for is itself the evidence the transition happened.
                string recoveryCleanupError;
                TryClearPendingEditorStateMarker(out recoveryCleanupError);
                deferred = false;
                return EditorStateCompletionMessage((string)recipe["operation"]) +
                    Environment.NewLine + FormatEditorStatus();
            }

            throw new InvalidOperationException("completion recipe kind is not dispatchable yet: " + kind);
        }

        /// <summary>
        /// Reports whether adding, removing or relocating this asset path makes Unity rebuild the script
        /// assemblies. A verb that does so must withhold its response until the resulting compile and domain
        /// reload settle, because the editor cannot serve the caller's next command while it recompiles, and
        /// the rebuild is scheduled on a later editor tick rather than run by a synchronous Refresh. A folder
        /// counts when anything beneath it does, because deleting or moving one carries every script inside
        /// it and rebuilds the assemblies exactly as the individual files would have.
        /// </summary>
        /// <param name="assetPath">Project-relative asset path to classify.</param>
        /// <returns>True when the path is, or contains, a compilation input.</returns>
        private static bool PathTriggersScriptCompile(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath)) return false;
            if (HasCompilationInputExtension(assetPath)) return true;
            if (!AssetDatabase.IsValidFolder(assetPath)) return false;

            try
            {
                foreach (var file in Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories))
                {
                    if (HasCompilationInputExtension(file)) return true;
                }
            }
            catch
            {
                // An unreadable folder is treated as compiling. A deferral that turns out to be unnecessary
                // costs the caller the bounded watchdog wait, while a missed one costs the next command.
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reports whether a file name carries one of the extensions Unity rebuilds the script assemblies
        /// from. This is the one place that set is written down.
        /// </summary>
        /// <param name="path">File path or name to classify.</param>
        /// <returns>True when the extension is a compilation input.</returns>
        private static bool HasCompilationInputExtension(string path)
        {
            return path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Writes a small marker file confirming that the bridge assembly loaded in the editor.
        /// </summary>
        private static void MarkLoaded()
        {
            try
            {
                Directory.CreateDirectory("Temp");
                File.WriteAllText(
                    "Temp/ConduitBridge.loaded.txt",
                    "Loaded: " + DateTimeOffset.Now.ToString("O"),
                    Encoding.UTF8);
            }
            catch
            {
                // Keep the bridge invisible if the editor is in a transient IO state.
            }
        }

        /// <summary>
        /// Writes the heartbeat file about once a second: timestamp, focus, play, update and compile state.
        /// MCP servers watch it to fast-fail while no editor is running and to wake the moment one appears,
        /// and its age is what separates an editor whose update loop has stopped from a merely slow command.
        /// <para>
        /// It answers whether the loop is ticking, and reports no durations at all. A tick that stops because
        /// the main thread is blocked also stops any counter carried in it, so a duration measured here
        /// freezes at the moment it starts mattering. What the editor is working on comes from Unity's own
        /// progress reports instead, which keep arriving through a block: see
        /// <see cref="ConduitEditorActivity"/>.
        /// </para>
        /// </summary>
        private static void HeartbeatTick()
        {
            if (EditorApplication.timeSinceStartup < _nextHeartbeatTime)
            {
                return;
            }

            _nextHeartbeatTime = EditorApplication.timeSinceStartup + 1d;
            try
            {
                File.WriteAllText(HeartbeatPath,
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                    " focused=" + UnityEditorInternal.InternalEditorUtility.isApplicationActive +
                    " playing=" + EditorApplication.isPlaying +
                    " updating=" + EditorApplication.isUpdating +
                    " compiling=" + EditorApplication.isCompiling);
            }
            catch
            {
                // Diagnostics only - never let the heartbeat break the update loop.
            }
        }
    }
}
