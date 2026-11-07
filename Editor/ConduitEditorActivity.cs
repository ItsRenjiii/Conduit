/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Conduit editor activity: what Unity says it is doing, captured on the thread that can answer and read on
// the thread a busy editor cannot silence.
//
// Health is served on the listener thread so it stays truthful while the editor is busy, and that is exactly
// why health cannot ask Unity anything. Measured in this editor: Progress.GetDescription and
// Progress.GetRunningProgressCount both throw "can only be called from the main thread" off it, and
// Progress.EnumerateItems, which does not throw, walks a list the main thread mutates. So the main thread
// captures and this publishes; the listener thread only ever reads a finished object.
//
// Nothing here polls, because polling cannot see what it is asked about. Every lifecycle flag is a
// main-thread property and the work that makes an editor busy runs on the main thread, so an update-loop
// poll is structurally blind to it: a 1.63 second texture import was measured with isUpdating true from
// start to finish while not one update tick observed it.
//
// Unity announces its own work from inside that work, on two channels, and this listens on both.
// Progress.added, updated and removed were measured arriving about four times a second during a script
// compile, naming each phase as it changed: Running BuildProgram, then Csc Assembly-CSharp-Editor, then
// Running Backend. AssetPostprocessor's callbacks fire inside the import call itself, and they are the only
// thing that sees an import at all, because Unity registers no progress record for one.
//
// Two properties of Unity's numbers are load-bearing and are reported rather than smoothed over. Its elapsed
// time is its own clock and keeps running across a domain reload, and every snapshot carries the instant it
// was taken, so a reader can tell live work from a picture Unity stopped updating. The two are never added
// together here: a sum reads as progress in precisely the case the age exists to expose.
//
// One oddity is passed through rather than cleaned up. A task's status is briefly wrong across a domain
// reload: a compile that succeeded was observed reading Failed while the replacement domain was still
// loading, then Succeeded two seconds later. Status is reported as Unity reports it, with the reload flag
// beside it so a reader can see when that window is in play.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Compilation;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Captures Unity's own account of the work it is doing and publishes it as an immutable snapshot that
    /// any thread may read. This is the only supported source for "what is the editor busy with": the
    /// lifecycle flags a caller would otherwise reach for are main-thread only, so they cannot be read at
    /// the moment the question is asked.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConduitEditorActivity
    {
        #region Constants

        /// <summary>
        /// How many tasks a snapshot carries. A large import registers one entry per asset, and a health
        /// payload holding thousands of them answers nothing a reader could use; the full count is reported
        /// beside the list so a trimmed list never reads as the whole picture.
        /// </summary>
        private const int MAX_REPORTED_TASKS = 24;

        #endregion

        #region Types

        /// <summary>One unit of work Unity is reporting, copied out of Unity's own record.</summary>
        internal sealed class Task
        {
            /// <summary>Unity's id for this task, stable for its lifetime.</summary>
            public int Id;

            /// <summary>Id of the task this one runs under, or -1 when it is top level.</summary>
            public int ParentId;

            /// <summary>What the task is, for example "Compiling Scripts".</summary>
            public string Name;

            /// <summary>What it is doing right now, for example "Csc Assembly-CSharp-Editor".</summary>
            public string Description;

            /// <summary>Label for the current step, where the task reports steps.</summary>
            public string StepLabel;

            /// <summary>Step reached, or -1 where the task does not count steps.</summary>
            public int CurrentStep;

            /// <summary>Steps in total, or -1 where the task does not count steps.</summary>
            public int TotalSteps;

            /// <summary>Completion from 0 to 1, where the task reports one.</summary>
            public float Fraction;

            /// <summary>Seconds the task had been running when this was captured, on Unity's clock.</summary>
            public double ElapsedSeconds;

            /// <summary>Unity's own estimate of the seconds left, or -1 when it has none.</summary>
            public double RemainingSeconds;

            /// <summary>Unity's status word: Running, Succeeded, Failed, Canceled or Paused.</summary>
            public string Status;

            /// <summary>True while the status is Running.</summary>
            public bool Running;

            /// <summary>Unity's own answer to whether the task is still responding.</summary>
            public bool Responding;

            /// <summary>True when the task has no measurable end, so no fraction is meaningful.</summary>
            public bool Indefinite;
        }

        /// <summary>The import Unity is inside right now, as Unity's own import callbacks reported it.</summary>
        internal sealed class Import
        {
            /// <summary>Asset path Unity was last seen beginning to import.</summary>
            public string AssetPath;

            /// <summary>How many assets have begun importing since this batch started.</summary>
            public int AssetsSeen;

            /// <summary>
            /// When the batch began, in UTC. A real fixed instant, so a reader computes the duration on its
            /// own clock and it keeps growing while the main thread is inside the import.
            /// </summary>
            public DateTime StartedUtc;
        }

        /// <summary>
        /// The editor lifecycle flags, held rather than read. Each one can only change on the main thread,
        /// so a held value cannot fall behind reality while that thread is occupied: nothing is able to
        /// change it until the thread is free again.
        /// </summary>
        internal sealed class Lifecycle
        {
            /// <summary>Mirror of EditorApplication.isPlaying.</summary>
            public bool Playing;

            /// <summary>Mirror of EditorApplication.isPlayingOrWillChangePlaymode.</summary>
            public bool WillChangePlaymode;

            /// <summary>Mirror of EditorApplication.isPaused.</summary>
            public bool Paused;

            /// <summary>Mirror of EditorApplication.isCompiling.</summary>
            public bool Compiling;

            /// <summary>Mirror of EditorApplication.isUpdating, maintained from Unity's import callbacks.</summary>
            public bool Updating;
        }

        /// <summary>
        /// An immutable picture of the editor's work at one instant. Published by reference swap, so a
        /// reader on any thread sees either the previous snapshot or this one whole, never a half-built one.
        /// </summary>
        internal sealed class Snapshot
        {
            /// <summary>The tasks, longest-running first, trimmed to <see cref="MAX_REPORTED_TASKS"/>.</summary>
            public Task[] Tasks;

            /// <summary>How many tasks Unity had, before the list was trimmed.</summary>
            public int TaskCount;

            /// <summary>True when any of Unity's tasks was running, counted before the trim.</summary>
            public bool Busy;

            /// <summary>True while Unity is between tearing the managed domain down and finishing its reload.</summary>
            public bool Reloading;

            /// <summary>The import in flight, or null when none is.</summary>
            public Import Importing;

            /// <summary>The editor lifecycle flags as of this capture. Never null.</summary>
            public Lifecycle State;

            /// <summary>When this was captured, in UTC, so a reader can measure its own staleness.</summary>
            public DateTime CapturedUtc;

            /// <summary>Why the last capture failed, or null when it did not. Reported rather than swallowed.</summary>
            public string Fault;
        }

        #endregion

        #region State

        /// <summary>Tasks by Unity's id, maintained on the main thread only.</summary>
        private static readonly Dictionary<int, Task> _tasks = new Dictionary<int, Task>();

        /// <summary>Orders a snapshot so the work that has taken longest is the part a trimmed list keeps.</summary>
        private static readonly Comparison<Task> _longestFirst =
            (a, b) => b.ElapsedSeconds.CompareTo(a.ElapsedSeconds);

        /// <summary>
        /// True from the moment a domain reload is announced until the replacement domain has settled. It
        /// starts true because the static constructor runs DURING the reload: Unity was measured raising
        /// afterAssemblyReload a further two seconds after the new domain had loaded, and it is still
        /// reserializing through that window.
        /// </summary>
        private static bool _reloading = true;

        /// <summary>The import Unity is inside, or null. Written only from Unity's own import callbacks.</summary>
        private static Import _importing;

        /// <summary>The lifecycle flags, refreshed from Unity's events and from every update tick.</summary>
        private static Lifecycle _state = new Lifecycle();

        /// <summary>
        /// Unity's version string, read once on load. It answered from a background thread when measured, but
        /// it is a constant for the life of the process, so holding it costs one field and depends on nothing.
        /// </summary>
        private static string _unityVersion = string.Empty;

        /// <summary>
        /// EditorApplication.timeSinceStartup as it read at the anchor instant, paired with the monotonic
        /// counter value at that same instant. Measured: the editor clock advanced 2.009 s across a 2.000 s
        /// block of the main thread, matching wall time to 0.000 s, so it is a wall clock and this pair
        /// reproduces it exactly from any thread rather than approximating it.
        /// </summary>
        private static double _clockAnchorSeconds;

        /// <summary>The monotonic counter read at the same instant as <see cref="_clockAnchorSeconds"/>.</summary>
        private static long _clockAnchorTicks;

        /// <summary>The published snapshot. Never null, so a reader needs no null case at time zero.</summary>
        private static volatile Snapshot _snapshot = new Snapshot
        {
            Tasks = new Task[0],
            TaskCount = 0,
            Busy = false,
            Reloading = true,
            State = new Lifecycle(),
            CapturedUtc = DateTime.UtcNow,
        };

        #endregion

        #region Lifecycle

        /// <summary>Subscribes to Unity's progress and reload events, once per domain load.</summary>
        static ConduitEditorActivity()
        {
            // An AssetImportWorker is this same editor binary launched with -batchMode and it loads this
            // assembly too. Nothing there serves health, so it keeps the idle snapshot rather than
            // subscribing and maintaining a picture no one reads.
            if (UnityEngine.Application.isBatchMode)
            {
                _reloading = false;
                Publish(null);
                return;
            }

            // Anchoring once is enough and stays exact, because both sides of the pair are wall clocks: the
            // offset between them is the definition of the editor clock, not an estimate of it. Anchoring on
            // a monotonic counter rather than on the calendar keeps that true across a system time change.
            _unityVersion = UnityEngine.Application.unityVersion;
            _clockAnchorSeconds = EditorApplication.timeSinceStartup;
            _clockAnchorTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            Progress.added -= OnProgressChanged;
            Progress.added += OnProgressChanged;
            Progress.updated -= OnProgressChanged;
            Progress.updated += OnProgressChanged;
            Progress.removed -= OnProgressRemoved;
            Progress.removed += OnProgressRemoved;

            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= SettleReload;
            AssemblyReloadEvents.afterAssemblyReload += SettleReload;
            EditorApplication.update -= SettleReload;
            EditorApplication.update += SettleReload;

            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            EditorApplication.pauseStateChanged -= OnPauseChanged;
            EditorApplication.pauseStateChanged += OnPauseChanged;
            CompilationPipeline.compilationStarted -= OnCompilationStarted;
            CompilationPipeline.compilationStarted += OnCompilationStarted;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
            EditorApplication.update -= Refresh;
            EditorApplication.update += Refresh;

            _state = LiveState();
            Seed();
        }

        /// <summary>
        /// Reads the tasks already in flight when this domain loaded, so a reload does not blank the picture
        /// until the next event arrives. Unity was measured returning the running compile item here on a
        /// freshly loaded domain, two seconds before its next update event.
        /// </summary>
        private static void Seed()
        {
            try
            {
                foreach (var item in Progress.EnumerateItems())
                {
                    _tasks[item.id] = Describe(item);
                }

                Publish(null);
            }
            catch (Exception ex)
            {
                Publish(Fault("seed", ex));
            }
        }

        /// <summary>Marks the picture as reloading while the old domain is still the one answering.</summary>
        private static void OnBeforeAssemblyReload()
        {
            _reloading = true;
            Publish(_snapshot.Fault);
        }

        /// <summary>
        /// Clears the reloading flag once the reload has demonstrably finished, and stops listening for it.
        /// Two signals are accepted because either one alone can be missed: Unity's afterAssemblyReload, and
        /// the first update tick, which cannot run until the editor is back in its own loop.
        /// </summary>
        private static void SettleReload()
        {
            EditorApplication.update -= SettleReload;
            AssemblyReloadEvents.afterAssemblyReload -= SettleReload;

            if (!_reloading) return;
            _reloading = false;
            Publish(_snapshot.Fault);
        }

        #endregion

        #region Capture

        /// <summary>
        /// Refreshes the lifecycle flags from the live properties on every update tick, and closes out an
        /// import whose end callback never arrived. The tick is authoritative whenever it runs, which closes
        /// the one gap the events leave: isCompiling was measured going true 0.3 seconds before
        /// compilationStarted fired. The events then carry the flags through the windows the tick cannot
        /// reach, so between them the picture is right whenever either channel can speak.
        /// </summary>
        private static void Refresh()
        {
            var playing = EditorApplication.isPlaying;
            var willChange = EditorApplication.isPlayingOrWillChangePlaymode;
            var paused = EditorApplication.isPaused;
            var compiling = EditorApplication.isCompiling;
            var updating = EditorApplication.isUpdating;

            var orphaned = _importing != null && !updating;
            if (!orphaned
                && _state.Playing == playing
                && _state.WillChangePlaymode == willChange
                && _state.Paused == paused
                && _state.Compiling == compiling
                && _state.Updating == updating)
            {
                return;
            }

            // An import whose batch callback never arrived would otherwise be reported as running forever.
            // The update loop running at all proves the import has ended, so this is an observation rather
            // than a timeout guess.
            if (orphaned) _importing = null;

            _state = new Lifecycle
            {
                Playing = playing,
                WillChangePlaymode = willChange,
                Paused = paused,
                Compiling = compiling,
                Updating = updating,
            };

            Publish(_snapshot.Fault);
        }

        /// <summary>
        /// Records a play-mode transition. ExitingPlayMode sets WillChangePlaymode false, which is not what
        /// the property's name suggests: Unity was measured reporting isPlaying true and
        /// isPlayingOrWillChangePlaymode false at that exact moment, so the name is not a safe guide here.
        /// </summary>
        /// <param name="change">The transition Unity reported.</param>
        private static void OnPlayModeChanged(PlayModeStateChange change)
        {
            var next = CopyState();
            switch (change)
            {
                case PlayModeStateChange.ExitingEditMode:
                    next.Playing = false;
                    next.WillChangePlaymode = true;
                    break;
                case PlayModeStateChange.EnteredPlayMode:
                    next.Playing = true;
                    next.WillChangePlaymode = true;
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    next.Playing = true;
                    next.WillChangePlaymode = false;
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    next.Playing = false;
                    next.WillChangePlaymode = false;
                    next.Paused = false;
                    break;
            }

            _state = next;
            Publish(_snapshot.Fault);
        }

        /// <summary>Records a pause transition.</summary>
        /// <param name="pause">The pause state Unity reported.</param>
        private static void OnPauseChanged(PauseState pause)
        {
            var next = CopyState();
            next.Paused = pause == PauseState.Paused;
            _state = next;
            Publish(_snapshot.Fault);
        }

        /// <summary>Records the start of a compile.</summary>
        /// <param name="context">Unity's compilation context, which carries nothing this needs.</param>
        private static void OnCompilationStarted(object context)
        {
            var next = CopyState();
            next.Compiling = true;
            _state = next;
            Publish(_snapshot.Fault);
        }

        /// <summary>Records the end of a compile.</summary>
        /// <param name="context">Unity's compilation context, which carries nothing this needs.</param>
        private static void OnCompilationFinished(object context)
        {
            var next = CopyState();
            next.Compiling = false;
            _state = next;
            Publish(_snapshot.Fault);
        }

        /// <summary>
        /// Records that Unity has begun importing one asset. Called from the import callbacks, which run
        /// inside the import, so this is the only report that arrives while an import owns the main thread.
        /// The batch start instant is kept across assets so a long import reports one duration rather than
        /// restarting the clock on each file.
        /// </summary>
        /// <param name="assetPath">The asset Unity is about to import.</param>
        internal static void NoteAssetImportStarted(string assetPath)
        {
            if (UnityEngine.Application.isBatchMode) return;

            try
            {
                var held = _importing;
                _importing = new Import
                {
                    AssetPath = assetPath ?? string.Empty,
                    AssetsSeen = held == null ? 1 : held.AssetsSeen + 1,
                    StartedUtc = held == null ? DateTime.UtcNow : held.StartedUtc,
                };

                var next = CopyState();
                next.Updating = true;
                _state = next;
                Publish(_snapshot.Fault);
            }
            catch (Exception ex)
            {
                Publish(Fault("import-start", ex));
            }
        }

        /// <summary>
        /// Records that an import batch has finished. A batch that only moved or deleted assets raises this
        /// without a matching start, so it changes nothing unless an import was actually in flight.
        /// </summary>
        internal static void NoteAssetImportFinished()
        {
            if (UnityEngine.Application.isBatchMode) return;

            try
            {
                if (_importing == null && !_state.Updating) return;

                _importing = null;
                var next = CopyState();
                next.Updating = EditorApplication.isUpdating;
                _state = next;
                Publish(_snapshot.Fault);
            }
            catch (Exception ex)
            {
                Publish(Fault("import-finish", ex));
            }
        }

        /// <summary>Reads the live lifecycle flags. Main thread only, and exact at the moment of the call.</summary>
        /// <returns>A lifecycle record matching the editor right now.</returns>
        private static Lifecycle LiveState()
        {
            return new Lifecycle
            {
                Playing = EditorApplication.isPlaying,
                WillChangePlaymode = EditorApplication.isPlayingOrWillChangePlaymode,
                Paused = EditorApplication.isPaused,
                Compiling = EditorApplication.isCompiling,
                Updating = EditorApplication.isUpdating,
            };
        }

        /// <summary>Copies the lifecycle flags so one field can change without tearing the held value.</summary>
        /// <returns>A mutable copy.</returns>
        private static Lifecycle CopyState()
        {
            var held = _state;
            return new Lifecycle
            {
                Playing = held.Playing,
                WillChangePlaymode = held.WillChangePlaymode,
                Paused = held.Paused,
                Compiling = held.Compiling,
                Updating = held.Updating,
            };
        }

        /// <summary>Records tasks Unity has started or advanced.</summary>
        /// <param name="items">The tasks Unity reported, or null.</param>
        private static void OnProgressChanged(Progress.Item[] items)
        {
            Merge(items, false);
        }

        /// <summary>Drops tasks Unity has finished with.</summary>
        /// <param name="items">The tasks Unity reported, or null.</param>
        private static void OnProgressRemoved(Progress.Item[] items)
        {
            Merge(items, true);
        }

        /// <summary>
        /// Applies one of Unity's progress events to the held set and publishes the result. Unity's three
        /// events describe the set completely, so the delta is applied rather than the whole list re-read.
        /// </summary>
        /// <param name="items">The tasks the event carried.</param>
        /// <param name="removed">True when the event was a removal.</param>
        private static void Merge(Progress.Item[] items, bool removed)
        {
            if (items == null) return;

            try
            {
                foreach (var item in items)
                {
                    if (removed) _tasks.Remove(item.id);
                    else _tasks[item.id] = Describe(item);
                }

                Publish(null);
            }
            catch (Exception ex)
            {
                // Unity drops a handler that throws and can take the rest of the subscriber list with it, so
                // this must not propagate: breaking the editor's own progress dispatch would be a far worse
                // failure than a stale health payload. The reason is carried in the snapshot instead of
                // being swallowed, because a reader that cannot see this has no way to tell it happened.
                Publish(Fault(removed ? "removed" : "changed", ex));
            }
        }

        /// <summary>Copies one of Unity's task records into a value this class owns.</summary>
        /// <param name="item">Unity's record.</param>
        /// <returns>The copy, safe to publish and read from any thread.</returns>
        private static Task Describe(Progress.Item item)
        {
            var remaining = item.remainingTime;
            return new Task
            {
                Id              = item.id,
                ParentId        = item.parentId,
                Name            = item.name ?? string.Empty,
                Description     = item.description ?? string.Empty,
                StepLabel       = item.stepLabel ?? string.Empty,
                CurrentStep     = item.currentStep,
                TotalSteps      = item.totalSteps,
                Fraction        = item.progress,
                ElapsedSeconds  = item.elapsedTime,
                RemainingSeconds = remaining > TimeSpan.Zero ? remaining.TotalSeconds : -1d,
                Status          = item.status.ToString(),
                Running         = item.status == Progress.Status.Running,
                Responding      = item.responding,
                Indefinite      = item.indefinite,
            };
        }

        /// <summary>Formats a capture failure so a reader learns where it happened.</summary>
        /// <param name="stage">Which capture path failed.</param>
        /// <param name="ex">The failure.</param>
        /// <returns>A one-line description.</returns>
        private static string Fault(string stage, Exception ex)
        {
            return stage + ": " + ex.GetType().Name + " " + ex.Message;
        }

        /// <summary>
        /// Builds and publishes a snapshot from the held set. Busy is decided across every task, before the
        /// list is trimmed, so trimming can never turn a busy editor into an idle one.
        /// </summary>
        /// <param name="fault">Why the capture that led here failed, or null.</param>
        private static void Publish(string fault)
        {
            var busy = false;
            var all = new List<Task>(_tasks.Count);
            foreach (var task in _tasks.Values)
            {
                if (task.Running) busy = true;
                all.Add(task);
            }

            all.Sort(_longestFirst);
            var total = all.Count;
            if (total > MAX_REPORTED_TASKS) all.RemoveRange(MAX_REPORTED_TASKS, total - MAX_REPORTED_TASKS);

            _snapshot = new Snapshot
            {
                Tasks = all.ToArray(),
                TaskCount = total,

                // An import counts as busy on its own. Unity registers no progress record for one, so a
                // snapshot that decided this from the task list alone reported an idle editor through every
                // long import, which is the exact dishonesty this class exists to remove.
                Busy = busy || _importing != null,
                Reloading = _reloading,
                Importing = _importing,
                State = _state,
                CapturedUtc = DateTime.UtcNow,
                Fault = fault,
            };
        }

        #endregion

        #region Reads

        /// <summary>
        /// The most recent picture of the editor's work. Safe to read from any thread, and never null.
        /// </summary>
        internal static Snapshot Current
        {
            get { return _snapshot; }
        }

        /// <summary>Unity's version string, safe to read from any thread.</summary>
        internal static string UnityVersion
        {
            get { return _unityVersion; }
        }

        /// <summary>
        /// EditorApplication.timeSinceStartup, reproduced from the load-time anchor so it is readable from
        /// any thread. Exact rather than estimated: the editor clock was measured advancing in step with
        /// wall time straight through a blocked main thread, so adding the elapsed counter to the anchor
        /// returns what the property itself would return.
        /// </summary>
        internal static double TimeSinceStartup
        {
            get
            {
                var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - _clockAnchorTicks;
                return _clockAnchorSeconds + (double)elapsed / System.Diagnostics.Stopwatch.Frequency;
            }
        }

        #endregion
    }

    /// <summary>
    /// Feeds <see cref="ConduitEditorActivity"/> from Unity's import callbacks, which are the only channel
    /// that reports an asset import at all. Unity registers no progress record for one, and the lifecycle
    /// flag it raises cannot be seen by anything running on the update loop, because the import owns that
    /// thread for its whole duration. These callbacks are resolved by name rather than as overrides, which
    /// is why they are private and carry no override keyword.
    /// </summary>
    internal sealed class ConduitImportWatch : AssetPostprocessor
    {
        /// <summary>Runs ahead of other postprocessors, so the report precedes whatever work they add.</summary>
        /// <returns>A low order value.</returns>
        public override int GetPostprocessOrder()
        {
            return -10000;
        }

        /// <summary>Reports that Unity has begun importing one asset.</summary>
        private void OnPreprocessAsset()
        {
            ConduitEditorActivity.NoteAssetImportStarted(assetPath);
        }

        /// <summary>Reports that an import batch has finished. The arguments are Unity's required signature.</summary>
        /// <param name="imported">Asset paths imported.</param>
        /// <param name="deleted">Asset paths deleted.</param>
        /// <param name="moved">Asset paths moved, at their new path.</param>
        /// <param name="movedFrom">Where each moved asset came from.</param>
        private static void OnPostprocessAllAssets(
            string[] imported, string[] deleted, string[] moved, string[] movedFrom)
        {
            ConduitEditorActivity.NoteAssetImportFinished();
        }
    }
}
