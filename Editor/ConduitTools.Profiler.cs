/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Unity.Profiling;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Profiler commands that expose stable Unity 2022 counters through Conduit's text bridge.
    /// </summary>
    internal static partial class ConduitBridge
    {
        /// <summary>Instance members of the Profiler window, public and internal, reached for its module list and states.</summary>
        private const BindingFlags PROFILER_WINDOW_MEMBERS = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>JSON keys of the per-frame counters read by <c>profiler-frames</c> and <c>profiler-select-frame</c>, aligned with <see cref="ProfilerFrameCounterNames"/>.</summary>
        private static readonly string[] ProfilerFrameCounterKeys =
        {
            "batches", "setPassCalls", "drawCalls", "triangles", "vertices", "shadowCasters", "gcAllocBytes",
        };

        /// <summary>Unity counter names recorded in each profiler frame, aligned with <see cref="ProfilerFrameCounterKeys"/>.</summary>
        private static readonly string[] ProfilerFrameCounterNames =
        {
            "Batches Count", "SetPass Calls Count", "Draw Calls Count", "Triangles Count", "Vertices Count",
            "Shadow Casters Count", "GC Allocated In Frame",
        };

        /// <summary>
        /// Stores the most recent profiler snapshot JSON so save/load commands have a coherent payload.
        /// </summary>
        private static string _lastProfilerSnapshotJson = "{}";

        // ── Rolling spike-capture session state ─────────────────────────────────────────────────

        /// <summary>
        /// Live per-frame counter recorders for the active <c>profiler-record-start</c> session, aligned
        /// index-for-index with <see cref="ProfilerRecordCounters"/>. Each recorder owns a ring buffer of
        /// the last <see cref="_profilerRecordCapacity"/> frames; <c>null</c> whenever no session is active.
        /// </summary>
        private static ProfilerRecorder[] _profilerRecorders;

        /// <summary>Ring-buffer length, in frames, requested for the active record session.</summary>
        private static int _profilerRecordCapacity;

        /// <summary>Whether the profiler was enabled before the record session turned it on.</summary>
        private static bool _profilerRecordPriorEnabled;

        /// <summary>Whether the Profiler window's Record was on before the record session turned it on.</summary>
        private static bool _profilerRecordPriorRecording;

        /// <summary>Whether a record session holds the editor state it found, so ending it puts that state back.</summary>
        private static bool _profilerRecordStateCaptured;

        /// <summary>Guards one-time installation of the domain-reload cleanup hook for record sessions.</summary>
        private static bool _profilerReloadHookInstalled;

        /// <summary>
        /// Describes one per-frame profiler counter captured by a record session: the JSON key Conduit
        /// emits for it, the Unity profiler category and stat name it samples, and whether its raw samples
        /// are nanosecond timings (converted to milliseconds) or plain counts/bytes.
        /// </summary>
        private struct ProfilerCounterDef
        {
            /// <summary>Stable machine-readable key emitted in the recorded timeline (e.g. "drawCalls").</summary>
            public string Key;

            /// <summary>Unity profiler category the stat lives in (Internal, Render, Memory, ...).</summary>
            public ProfilerCategory Category;

            /// <summary>Exact Unity 2022 profiler stat label sampled by the recorder.</summary>
            public string Stat;

            /// <summary>True when the raw sample is nanoseconds (rendered as ms); false for raw counts/bytes.</summary>
            public bool IsTimeNs;
        }

        /// <summary>
        /// Aggregates one marker's exclusive cost across every call site in a single profiler frame while
        /// the hierarchy is walked, so <c>profiler-frame-hierarchy</c> can rank markers by total self time.
        /// </summary>
        private struct MarkerAggregate
        {
            /// <summary>Exclusive (self) time for this marker name, summed across all call sites, in ms.</summary>
            public double SelfMs;

            /// <summary>
            /// Managed bytes this marker allocated itself (its own garbage less its child markers'), summed across call
            /// sites. Unity's GC Alloc column is inclusive, so summing it across nested markers would count each byte at
            /// every level; the GC.Alloc samples Unity records under the allocating code count for that code.
            /// </summary>
            public double GcBytes;

            /// <summary>Managed bytes allocated under this marker, children included, summed across call sites; zero for work filed under the editor loop.</summary>
            public double TotalGcBytes;

            /// <summary>Invocation count for this marker name, summed across call sites.</summary>
            public double Calls;

            /// <summary>Inclusive (total) time for this marker name, summed across call sites, in ms.</summary>
            public double TotalMs;

            /// <summary>Exclusive GPU time, summed across call sites, in ms; zero unless the GPU Usage module recorded the frame.</summary>
            public double SelfGpuMs;

            /// <summary>Inclusive GPU time, summed across call sites, in ms; zero unless the GPU Usage module recorded the frame.</summary>
            public double TotalGpuMs;

            /// <summary>Draw calls issued under this marker name, summed across call sites.</summary>
            public double DrawCalls;
        }

        /// <summary>
        /// The per-frame counters a record session samples, in report order. Stat labels are the stable
        /// Unity 2022 profiler counter names; any unsupported on the running build is detected at start and
        /// reported rather than silently emitting zero. Built lazily so the static initializer stays simple.
        /// </summary>
        private static readonly ProfilerCounterDef[] ProfilerRecordCounters = BuildProfilerRecordCounters();

        /// <summary>
        /// Builds the fixed counter table for record sessions. Main-thread time drives spike detection; the
        /// rest let a flagged spike be attributed to GC churn or draw-call/geometry load at a glance.
        /// </summary>
        /// <returns>The ordered counter descriptors.</returns>
        private static ProfilerCounterDef[] BuildProfilerRecordCounters()
        {
            return new[]
            {
                new ProfilerCounterDef { Key = "mainThreadMs", Category = ProfilerCategory.Internal, Stat = "Main Thread",           IsTimeNs = true  },
                new ProfilerCounterDef { Key = "gcAllocBytes", Category = ProfilerCategory.Memory,   Stat = "GC Allocated In Frame", IsTimeNs = false },
                new ProfilerCounterDef { Key = "drawCalls",    Category = ProfilerCategory.Render,   Stat = "Draw Calls Count",      IsTimeNs = false },
                new ProfilerCounterDef { Key = "setPassCalls", Category = ProfilerCategory.Render,   Stat = "SetPass Calls Count",   IsTimeNs = false },
                new ProfilerCounterDef { Key = "batches",      Category = ProfilerCategory.Render,   Stat = "Batches Count",         IsTimeNs = false },
                new ProfilerCounterDef { Key = "triangles",    Category = ProfilerCategory.Render,   Stat = "Triangles Count",       IsTimeNs = false },
                new ProfilerCounterDef { Key = "vertices",     Category = ProfilerCategory.Render,   Stat = "Vertices Count",        IsTimeNs = false },
            };
        }

        #region Profiler Commands

        /// <summary>
        /// Enables Unity profiling and opens the Profiler window.
        /// </summary>
        /// <returns>The profiler and Record states before and after, as read back.</returns>
        private static string ProfilerStartCommand()
        {
            bool enabledBefore = Profiler.enabled;
            bool recordingBefore = ProfilerDriver.enabled;
            Profiler.enabled = true;
            EditorWindow.GetWindow(Type.GetType("UnityEditor.ProfilerWindow,UnityEditor"));
            return ProfilerStateChange(enabledBefore, recordingBefore);
        }

        /// <summary>
        /// Disables Unity profiling and turns the Profiler window's Record off, which the editor layout would
        /// otherwise save and restore every time Unity opens.
        /// </summary>
        /// <returns>The profiler and Record states before and after, as read back.</returns>
        private static string ProfilerStopCommand()
        {
            bool enabledBefore = Profiler.enabled;
            bool recordingBefore = ProfilerDriver.enabled;
            Profiler.enabled = false;
            EnableProfilerDriverRecording(false);
            return ProfilerStateChange(enabledBefore, recordingBefore);
        }

        /// <summary>
        /// Describes the profiler and the Profiler window's Record as each was before a command and as it reads
        /// back now, naming a state that was already at its new value rather than reporting it as a change.
        /// </summary>
        /// <param name="enabledBefore">Whether profiling was enabled before the command.</param>
        /// <param name="recordingBefore">Whether the Profiler window's Record was on before the command.</param>
        /// <returns>One line covering both states.</returns>
        private static string ProfilerStateChange(bool enabledBefore, bool recordingBefore)
        {
            return "Profiler " + StateChange(enabledBefore, Profiler.enabled, "enabled", "disabled")
                + "; Profiler window Record " + StateChange(recordingBefore, ProfilerDriver.enabled, "on", "off") + ".";
        }

        /// <summary>Ends the record session, putting the editor state back, and says what changed in doing so.</summary>
        /// <returns>The profiler and Record states before and after the restore.</returns>
        private static string EndRecordSessionReportingRestore()
        {
            bool enabledBefore = Profiler.enabled;
            bool recordingBefore = ProfilerDriver.enabled;
            DisposeProfilerRecordSession();
            return ProfilerStateChange(enabledBefore, recordingBefore);
        }

        /// <summary>Words one boolean's transition, or says it was already at its value.</summary>
        /// <param name="before">Value before the command.</param>
        /// <param name="after">Value read back after the command.</param>
        /// <param name="onWord">Word for true.</param>
        /// <param name="offWord">Word for false.</param>
        /// <returns>"was X, now Y" or "already Y".</returns>
        private static string StateChange(bool before, bool after, string onWord, string offWord)
        {
            string now = after ? onWord : offWord;
            return before == after ? "already " + now : "was " + (before ? onWord : offWord) + ", now " + now;
        }

        /// <summary>
        /// Returns profiler enabled state, whether the Profiler window's Record is on, and basic editor/runtime
        /// context.
        /// </summary>
        /// <returns>A compact JSON profiler status object.</returns>
        private static string ProfilerGetStatusCommand()
        {
            return "{\"enabled\":" + (Profiler.enabled ? "true" : "false") +
                ",\"recording\":" + (ProfilerDriver.enabled ? "true" : "false") +
                ",\"supported\":" + (Profiler.supported ? "true" : "false") +
                ",\"enableBinaryLog\":" + (Profiler.enableBinaryLog ? "true" : "false") +
                ",\"logFile\":" + JsonQuote(Profiler.logFile) +
                ",\"isPlaying\":" + (EditorApplication.isPlaying ? "true" : "false") + "}";
        }

        /// <summary>
        /// Captures a lightweight frame/timing snapshot.
        /// </summary>
        /// <returns>A compact JSON frame snapshot.</returns>
        private static string ProfilerCaptureFrameCommand()
        {
            _lastProfilerSnapshotJson = "{\"frameCount\":" + Time.frameCount +
                ",\"realtimeSinceStartup\":" + JsonNumber(Time.realtimeSinceStartup) +
                ",\"editorTimeSinceStartup\":" + JsonNumber(EditorApplication.timeSinceStartup) +
                ",\"deltaTime\":" + JsonNumber(Time.deltaTime) +
                ",\"smoothDeltaTime\":" + JsonNumber(Time.smoothDeltaTime) + "}";
            return _lastProfilerSnapshotJson;
        }

        /// <summary>
        /// Returns Unity memory counters exposed by UnityEngine.Profiling.Profiler.
        /// </summary>
        /// <returns>A compact JSON memory stats object.</returns>
        private static string ProfilerGetMemoryStatsCommand()
        {
            return "{\"totalReservedMemory\":" + Profiler.GetTotalReservedMemoryLong() +
                ",\"totalAllocatedMemory\":" + Profiler.GetTotalAllocatedMemoryLong() +
                ",\"totalUnusedReservedMemory\":" + Profiler.GetTotalUnusedReservedMemoryLong() +
                ",\"monoUsedSize\":" + Profiler.GetMonoUsedSizeLong() +
                ",\"monoHeapSize\":" + Profiler.GetMonoHeapSizeLong() + "}";
        }

        /// <summary>
        /// Returns the live render statistics of the last frame the editor drew (the Game view in play mode), and the
        /// graphics device the editor runs on. The statistics only move while the editor renders, which it does only in
        /// front.
        /// </summary>
        /// <returns>A compact JSON object of the statistics and the device.</returns>
        private static string ProfilerGetRenderingStatsCommand()
        {
            return "{\"batches\":" + UnityStats.batches +
                ",\"drawCalls\":" + UnityStats.drawCalls +
                ",\"setPassCalls\":" + UnityStats.setPassCalls +
                ",\"triangles\":" + UnityStats.triangles +
                ",\"vertices\":" + UnityStats.vertices +
                ",\"shadowCasters\":" + UnityStats.shadowCasters +
                ",\"staticBatches\":" + UnityStats.staticBatches +
                ",\"dynamicBatches\":" + UnityStats.dynamicBatches +
                ",\"instancedBatches\":" + UnityStats.instancedBatches +
                ",\"renderTextureChanges\":" + UnityStats.renderTextureChanges +
                ",\"renderTextureCount\":" + UnityStats.renderTextureCount +
                ",\"visibleSkinnedMeshes\":" + UnityStats.visibleSkinnedMeshes +
                ",\"usedTextureMemoryBytes\":" + UnityStats.usedTextureMemorySize +
                ",\"screen\":" + JsonQuote(UnityStats.screenRes) +
                ",\"editorActive\":" + (UnityEditorInternal.InternalEditorUtility.isApplicationActive ? "true" : "false") +
                ",\"device\":{\"graphicsDeviceType\":" + JsonQuote(SystemInfo.graphicsDeviceType.ToString()) +
                ",\"graphicsDeviceName\":" + JsonQuote(SystemInfo.graphicsDeviceName) +
                ",\"graphicsMemorySize\":" + SystemInfo.graphicsMemorySize +
                ",\"processorCount\":" + SystemInfo.processorCount +
                ",\"targetFrameRate\":" + Application.targetFrameRate +
                ",\"vSyncCount\":" + QualitySettings.vSyncCount + "}}";
        }

        /// <summary>
        /// Returns script/GC memory counters available in Unity 2022.
        /// </summary>
        /// <returns>A compact JSON script stats object.</returns>
        private static string ProfilerGetScriptStatsCommand()
        {
            return "{\"monoUsedSize\":" + Profiler.GetMonoUsedSizeLong() +
                ",\"monoHeapSize\":" + Profiler.GetMonoHeapSizeLong() +
                ",\"gcAllocatedBytesForCurrentThread\":" + System.GC.GetAllocatedBytesForCurrentThread() + "}";
        }

        /// <summary>
        /// Lists the Profiler window's modules in its own order with each one's active state as the window reads it
        /// back, opening the window first when none is open and saying so.
        /// </summary>
        /// <returns>A compact JSON object of the modules, or a JSON error object.</returns>
        private static string ProfilerListModulesCommand()
        {
            bool opened;
            ProfilerWindow window = GetOrOpenProfilerWindow(out opened);
            string error;
            IList modules = ProfilerWindowModules(window, out error);
            if (modules == null) return "{\"error\":" + JsonQuote(error) + "}";

            var builder = new StringBuilder();
            builder.Append("{\"openedWindow\":").Append(opened ? "true" : "false").Append(",\"modules\":[");
            for (int i = 0; i < modules.Count; i++)
            {
                bool active;
                if (!TryProfilerModuleActive(window, modules[i], out active, out error)) return "{\"error\":" + JsonQuote(error) + "}";
                if (i > 0) builder.Append(',');
                builder.Append("{\"name\":").Append(JsonQuote(ProfilerModuleName(modules[i])))
                    .Append(",\"active\":").Append(active ? "true" : "false")
                    .Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        /// <summary>
        /// Turns one of the Profiler window's modules on or off by its display name, then reads the state back from
        /// the window and reports it as it was and as it is now. A module that is already at the asked state is said
        /// to be, a name the window does not have is refused with the names it does have, and a state that does not
        /// read back as asked is reported as not applied. Nothing changes on a refusal.
        /// </summary>
        /// <param name="args">Module name and the state to set, as "&lt;moduleName&gt; | &lt;enabled&gt;".</param>
        /// <returns>One line naming the module and its state before and after, or a refusal.</returns>
        private static string ProfilerEnableModuleCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: profiler-enable-module <moduleName> | <enabled>";
            }

            bool enabled;
            if (!TryParseBoolLoose(sections[1], out enabled))
            {
                return "Invalid enabled value: " + sections[1] + ". Nothing changed.";
            }

            string wanted = sections[0].Trim().Trim('"');
            bool opened;
            ProfilerWindow window = GetOrOpenProfilerWindow(out opened);
            string error;
            IList modules = ProfilerWindowModules(window, out error);
            if (modules == null) return error + " Nothing changed.";

            object module = null;
            var names = new List<string>(modules.Count);
            for (int i = 0; i < modules.Count; i++)
            {
                string name = ProfilerModuleName(modules[i]);
                names.Add(name);
                if (module == null && string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase)) module = modules[i];
            }

            if (module == null)
            {
                return "The Profiler window has no module named '" + wanted + "'. Nothing changed. Its modules are: " + string.Join(", ", names) + ".";
            }

            string moduleName = ProfilerModuleName(module);
            string openedNote = opened ? " The Profiler window was not open, so it was opened." : string.Empty;
            bool before;
            if (!TryProfilerModuleActive(window, module, out before, out error)) return error + " Nothing changed.";
            if (before == enabled)
            {
                return "Profiler module '" + moduleName + "' already " + (enabled ? "on" : "off") + ", as read back from the window. Nothing changed." + openedNote;
            }

            MethodInfo setActive = typeof(ProfilerWindow).GetMethod("SetProfilerModuleActiveState", PROFILER_WINDOW_MEMBERS);
            if (setActive == null)
            {
                return "This editor's Profiler window has no SetProfilerModuleActiveState, so '" + moduleName + "' cannot be switched from here. It is still " + (before ? "on" : "off") + ".";
            }

            setActive.Invoke(window, new[] { module, (object)enabled });
            window.Repaint();
            bool after;
            if (!TryProfilerModuleActive(window, module, out after, out error)) return error;
            if (after != enabled)
            {
                return "Asked to turn profiler module '" + moduleName + "' " + (enabled ? "on" : "off") + ", but it reads back " + (after ? "on" : "off") + ": the window did not apply it." + openedNote;
            }

            return "Profiler module '" + moduleName + "' was " + (before ? "on" : "off") + ", now " + (after ? "on" : "off") + ", as read back from the window." + openedNote;
        }

        /// <summary>
        /// Selects a buffered frame in the Profiler window, as clicking it in the chart does, so its panes show that
        /// frame. Reports the selection before and after as the window reads it back, and the frame's counters.
        /// </summary>
        /// <param name="args">The profiler frame number to select.</param>
        /// <returns>A compact JSON object of the selection and the frame's counters, or a JSON error object.</returns>
        private static string ProfilerSelectFrameCommand(string args)
        {
            int frame;
            string text = (args ?? string.Empty).Trim();
            if (!int.TryParse(text, out frame))
            {
                return "{\"error\":" + JsonQuote("Usage: profiler-select-frame <frame>; '" + text + "' is not a frame number. Nothing changed.") + "}";
            }

            int first = ProfilerFirstFrame();
            int last = ProfilerLastFrame();
            if (first < 0 || last < 0 || frame < first || frame > last)
            {
                return "{\"error\":" + JsonQuote("Frame " + frame + " is not buffered; buffered frames are " + first + " to " + last + ". Nothing changed.") + "}";
            }

            bool opened;
            ProfilerWindow window = GetOrOpenProfilerWindow(out opened);
            // The window holds the profiler's index, with -1 for following the newest frame; Conduit speaks the window's
            // Frame label numbering, so both directions convert and -1 stays -1.
            long before = window.selectedFrameIndex < 0 ? -1 : window.selectedFrameIndex + PROFILER_FRAME_NUMBER_OFFSET;
            window.selectedFrameIndex = frame - PROFILER_FRAME_NUMBER_OFFSET;
            window.Repaint();
            long after = window.selectedFrameIndex < 0 ? -1 : window.selectedFrameIndex + PROFILER_FRAME_NUMBER_OFFSET;

            List<ProfilerColumnSpec> columns = BuildProfilerColumns(string.Empty, string.Empty);
            var values = new double[columns.Count];
            var sb = new StringBuilder();
            sb.Append("{\"openedWindow\":").Append(opened ? "true" : "false")
                .Append(",\"selectedBefore\":").Append(before)
                .Append(",\"selectedAfter\":").Append(after)
                .Append(",\"applied\":").Append(after == frame ? "true" : "false")
                .Append(",\"frame\":");
            if (ReadProfilerFrameValues(frame, columns, values)) AppendProfilerFrameRow(sb, frame, values, columns);
            else sb.Append("null");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Clears profiler frame data through UnityEditorInternal when the editor exposes it.
        /// </summary>
        /// <returns>A confirmation line.</returns>
        private static string ProfilerClearDataCommand()
        {
            var driverType = Type.GetType("UnityEditorInternal.ProfilerDriver,UnityEditor");
            var clearMethod = driverType == null ? null : driverType.GetMethod("ClearAllFrames", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (clearMethod != null)
            {
                clearMethod.Invoke(null, null);
                return "Cleared profiler frame data.";
            }

            _lastProfilerSnapshotJson = "{}";
            return "Cleared Conduit profiler snapshot cache.";
        }

        /// <summary>
        /// Saves the most recent profiler snapshot to a project-relative or absolute JSON path.
        /// </summary>
        /// <param name="args">The destination file path.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string ProfilerSaveDataCommand(string args)
        {
            var path = NormalizeProjectRelativePath(args);
            if (string.IsNullOrEmpty(path))
            {
                return "Usage: profiler-save-data <path>";
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, _lastProfilerSnapshotJson);
            return "Saved profiler data to " + path + ".";
        }

        /// <summary>
        /// Loads a profiler snapshot JSON file into Conduit's cached profiler payload.
        /// </summary>
        /// <param name="args">The source file path.</param>
        /// <returns>The loaded JSON payload or validation error.</returns>
        private static string ProfilerLoadDataCommand(string args)
        {
            var path = NormalizeProjectRelativePath(args);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return "Profiler data file not found: " + path;
            }

            _lastProfilerSnapshotJson = File.ReadAllText(path);
            return _lastProfilerSnapshotJson;
        }

        // ── Spike capture & per-script attribution ──────────────────────────────────────────────

        /// <summary>
        /// Begins a rolling per-frame capture session: starts a <see cref="ProfilerRecorder"/> ring for each
        /// counter in <see cref="ProfilerRecordCounters"/> and enables the editor profiler frame buffer so a
        /// later <c>profiler-frame-hierarchy</c> call can attribute the worst captured frame to specific
        /// markers. Designed to bracket a reproduction (enter play mode, trigger the event - e.g. stop a
        /// song - then call <c>profiler-record-stop</c>). Restarts cleanly if a session is already active.
        /// </summary>
        /// <param name="args">
        /// Optional ring length in frames (default 2000, clamped to 60..20000). It sizes the counter rings that
        /// <c>profiler-record-stop</c> summarises; marks, comparisons and every per-frame read still reach only the
        /// Profiler's own frame history, which the reply reports as <c>historyFrames</c>.
        /// </param>
        /// <returns>
        /// A compact JSON object describing the started session, the Profiler's frame history length, any
        /// unsupported counters, and a note when the requested ring is longer than that history.
        /// </returns>
        private static string ProfilerRecordStartCommand(string args)
        {
            var capacity = 2000;
            var trimmed = (args ?? string.Empty).Trim();
            if (trimmed.Length > 0)
            {
                capacity = ParsePositiveInt(trimmed, 2000);
            }

            capacity = Mathf.Clamp(capacity, 60, 20000);

            // A fresh session must own a clean ring - never leak the previous session's recorders.
            if (_profilerRecorders != null)
            {
                DisposeProfilerRecordSession();
            }

            // The editor state is captured once per session, before anything is switched on, so ending the
            // session leaves the profiler and the Profiler window's Record exactly as they were found.
            _profilerRecordPriorEnabled = Profiler.enabled;
            _profilerRecordPriorRecording = ProfilerDriver.enabled;
            _profilerRecordStateCaptured = true;
            Profiler.enabled = true;
            EnableProfilerDriverRecording(true);
            InstallProfilerReloadHook();

            var defs = ProfilerRecordCounters;
            _profilerRecorders = new ProfilerRecorder[defs.Length];
            var unsupported = new List<string>();
            for (var i = 0; i < defs.Length; i++)
            {
                var recorder = ProfilerRecorder.StartNew(defs[i].Category, defs[i].Stat, capacity);
                _profilerRecorders[i] = recorder;
                if (!recorder.Valid)
                {
                    unsupported.Add(defs[i].Stat);
                }
            }

            _profilerRecordCapacity = capacity;

            var supportedKeys = new List<string>(defs.Length);
            for (var i = 0; i < defs.Length; i++)
            {
                if (_profilerRecorders[i].Valid)
                {
                    supportedKeys.Add(defs[i].Key);
                }
            }

            // The counter rings above can outlast the Profiler window's own frame history, which is the only
            // buffer marks, comparisons and hierarchy reads can reach, so a longer ring must say so.
            var historyFrames = ProfilerHistoryFrames();
            var historyNote = historyFrames > 0 && capacity > historyFrames
                ? ",\"note\":" + JsonQuote("The counter rings hold " + capacity + " frames for profiler-record-stop's " +
                    "summary, but marks, profiler-compare and every per-frame read reach only the newest " + historyFrames +
                    " frames, the Profiler's own history (Preferences > Analysis > Profiler > Frame count, 300 to 2000). " +
                    "Stop recording before a range scrolls out of it.")
                : string.Empty;

            return "{\"recording\":true,\"capacityFrames\":" + capacity +
                ",\"historyFrames\":" + (historyFrames > 0 ? historyFrames.ToString() : "null") + historyNote +
                ",\"previousProfilerEnabled\":" + (_profilerRecordPriorEnabled ? "true" : "false") +
                ",\"previousRecordOn\":" + (_profilerRecordPriorRecording ? "true" : "false") +
                ",\"isPlaying\":" + (EditorApplication.isPlaying ? "true" : "false") +
                ",\"frameBufferEnabled\":" + (ProfilerDriver.enabled ? "true" : "false") +
                ",\"counters\":" + JsonStringArray(supportedKeys) +
                ",\"unsupported\":" + JsonStringArray(unsupported) + "}";
        }

        /// <summary>
        /// Reads how many frames the Profiler window keeps (<c>ProfilerUserSettings.frameCount</c>, an internal
        /// editor setting between 300 and 2000), by reflection because Unity does not expose it publicly.
        /// </summary>
        /// <returns>The frame history length, or -1 when this Unity version does not carry the setting.</returns>
        private static int ProfilerHistoryFrames()
        {
            var settings = typeof(Editor).Assembly.GetType("UnityEditor.Profiling.ProfilerUserSettings");
            var frameCount = settings?.GetProperty("frameCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return frameCount != null && frameCount.GetValue(null) is int frames ? frames : -1;
        }

        /// <summary>
        /// Ends the active record session, reads the captured per-frame timeline, and returns the main-thread
        /// time distribution plus the worst "spike" frames (those well above the session's own median and the
        /// target frame budget), annotated with each frame's GC allocation and draw-call load so the spike's
        /// nature is obvious. Always ends the session before returning, putting the profiler and Record back as
        /// the session found them, and reports what that restore changed.
        /// </summary>
        /// <returns>A compact JSON report, or a JSON error object when no usable samples were captured; both carry
        /// the restore under 'restored' whenever a session was active.</returns>
        private static string ProfilerRecordStopCommand()
        {
            if (_profilerRecorders == null)
            {
                return "{\"error\":\"No active recording. Call profiler-record-start, reproduce the spike in play mode, then profiler-record-stop.\"}";
            }

            var defs = ProfilerRecordCounters;
            var mainIndex = IndexOfRecordCounter("mainThreadMs");
            if (mainIndex < 0 || !_profilerRecorders[mainIndex].Valid)
            {
                return "{\"error\":\"Main Thread timing counter is unsupported on this build, so spikes cannot be measured.\",\"restored\":"
                    + JsonQuote(EndRecordSessionReportingRestore()) + "}";
            }

            // Align every counter to the shortest captured ring so per-frame rows never read across boundaries.
            var frameCount = _profilerRecorders[mainIndex].Count;
            for (var i = 0; i < _profilerRecorders.Length; i++)
            {
                var recorder = _profilerRecorders[i];
                if (recorder.Valid && recorder.Count < frameCount)
                {
                    frameCount = recorder.Count;
                }
            }

            if (frameCount <= 0)
            {
                return "{\"error\":\"Recording captured 0 frames. Counters only advance while in play mode - enter play mode between start and stop.\",\"restored\":"
                    + JsonQuote(EndRecordSessionReportingRestore()) + "}";
            }

            // Pull the main-thread timeline (ms) for distribution and spike detection.
            var mainMs = new double[frameCount];
            for (var f = 0; f < frameCount; f++)
            {
                mainMs[f] = ReadCounterSample(mainIndex, f);
            }

            var sorted = (double[])mainMs.Clone();
            Array.Sort(sorted);
            var median = Percentile(sorted, 0.50);
            var p95 = Percentile(sorted, 0.95);
            var p99 = Percentile(sorted, 0.99);
            var maxMs = sorted[sorted.Length - 1];
            var meanMs = Mean(mainMs);

            // A spike is a frame both well above the session's own median and above 1.5x the target budget,
            // so a steadily heavy scene is not mistaken for a transient hitch.
            var targetMs = TargetFrameBudgetMs();
            var spikeThreshold = Math.Max(median * 1.75, targetMs * 1.5);

            var spikeIndices = new List<int>();
            for (var f = 0; f < frameCount; f++)
            {
                if (mainMs[f] >= spikeThreshold)
                {
                    spikeIndices.Add(f);
                }
            }

            // Worst first, and cap the emitted rows so a long bad stretch cannot flood the response.
            spikeIndices.Sort((a, b) => mainMs[b].CompareTo(mainMs[a]));
            const int maxReportedSpikes = 25;
            var reported = Math.Min(spikeIndices.Count, maxReportedSpikes);
            var firstEngineFrame = Time.frameCount - frameCount;

            var sb = new StringBuilder();
            sb.Append("{\"framesCaptured\":").Append(frameCount)
                .Append(",\"capacityFrames\":").Append(_profilerRecordCapacity)
                .Append(",\"targetBudgetMs\":").Append(JsonNumber(targetMs))
                .Append(",\"spikeThresholdMs\":").Append(JsonNumber(spikeThreshold))
                .Append(",\"distribution\":{")
                .Append("\"meanMs\":").Append(JsonNumber(meanMs))
                .Append(",\"medianMs\":").Append(JsonNumber(median))
                .Append(",\"p95Ms\":").Append(JsonNumber(p95))
                .Append(",\"p99Ms\":").Append(JsonNumber(p99))
                .Append(",\"maxMs\":").Append(JsonNumber(maxMs))
                .Append(",\"meanFps\":").Append(JsonNumber(meanMs > 0.0 ? 1000.0 / meanMs : 0.0))
                .Append('}')
                .Append(",\"spikeCount\":").Append(spikeIndices.Count)
                .Append(",\"spikesReported\":").Append(reported)
                .Append(",\"spikes\":[");

            for (var s = 0; s < reported; s++)
            {
                var f = spikeIndices[s];
                if (s > 0)
                {
                    sb.Append(',');
                }

                sb.Append("{\"approxEngineFrame\":").Append(firstEngineFrame + f + 1);
                for (var c = 0; c < defs.Length; c++)
                {
                    var value = ReadCounterSample(c, f);
                    if (double.IsNaN(value))
                    {
                        continue;
                    }

                    sb.Append(",\"").Append(defs[c].Key).Append("\":").Append(JsonNumber(value));
                }

                sb.Append('}');
            }

            sb.Append("],\"restored\":").Append(JsonQuote(EndRecordSessionReportingRestore()));
            sb.Append(",\"hint\":\"Spikes ranked worst-first. A high gcAllocBytes on the spike frame points at GC churn (allocation/Destroy/UnloadUnusedAssets on the event); a jump in drawCalls/triangles points at rendering. Call profiler-frame-hierarchy to attribute the heaviest buffered frame to specific markers/scripts.\"}");
            return sb.ToString();
        }

        /// <summary>
        /// Attributes a spike to specific code: scans the editor profiler's most recent buffered frames and
        /// picks the one with the highest REAL (shippable) cost - exclusive self-time after removing editor-
        /// only and idle markers (<c>EditorLoop</c>, <c>Mono.JIT</c>, <c>Profiler.*</c>, vsync/gfx waits),
        /// which dominate in-editor frame totals but do not exist in a player build. It then returns that
        /// frame's real-work markers ranked by exclusive (self) time; names such as
        /// <c>UdonBehaviour.ManagedUpdate</c>, <c>TMP GenerateText</c>, <c>GC.Collect</c>, or a U# class name
        /// identify the culprit. Requires the frame buffer populated (via <c>profiler-start</c> or
        /// <c>profiler-record-start</c>) and a recent reproduction - the editor retains only ~300 frames.
        /// </summary>
        /// <param name="args">Optional "[topN] [threadIndex] [frame]": markers to return (default 15, 1..1000), the
        /// thread to inspect (default 0, the main thread) and a frame to report instead of the worst one.</param>
        /// <returns>A compact JSON report of the chosen or worst real-cost frame and its top markers, or a JSON error object.</returns>
        private static string ProfilerFrameHierarchyCommand(string args)
        {
            var topN = 15;
            var threadIndex = 0;
            var tokens = SplitCommandTokens(args ?? string.Empty);
            if (tokens.Length >= 1)
            {
                topN = ParsePositiveInt(tokens[0], 15);
            }

            if (tokens.Length >= 2)
            {
                threadIndex = ParsePositiveInt(tokens[1], 0);
            }

            topN = Mathf.Clamp(topN, 1, 1000);

            var first = ProfilerFirstFrame();
            var last = ProfilerLastFrame();
            if (first < 0 || last < 0 || last < first)
            {
                return "{\"error\":\"No profiler frames buffered. Run profiler-start (or profiler-record-start), reproduce the spike in play mode, then call this again promptly - the editor only retains roughly the last ~300 frames.\"}";
            }

            if (tokens.Length >= 3)
            {
                int chosen;
                if (!int.TryParse(tokens[2], out chosen))
                {
                    return "{\"error\":" + JsonQuote("'" + tokens[2] + "' is not a frame number.") + "}";
                }

                if (chosen < first || chosen > last)
                {
                    return "{\"error\":" + JsonQuote("Frame " + chosen + " is not buffered; buffered frames are " + first + " to " + last + ".") + "}";
                }

                return BuildFrameHierarchyJson(chosen, threadIndex, topN, 0);
            }

            // Skip the most recent frame: it can still be mid-collection and report partial timings.
            var scanLast = last > first ? last - 1 : last;

            // Rank by REAL cost (self-time minus editor-only/idle noise), not raw frame time: in editor play
            // mode EditorLoop alone is routinely 10-18ms and would always win, burying the real spike. Only
            // the most recent window is scanned so a large frame buffer cannot turn this into thousands of
            // hierarchy builds.
            var scanFirst = Math.Max(first, scanLast - PROFILER_HIERARCHY_MAX_SCAN + 1);
            var worstFrame = -1;
            var worstRealMs = -1.0;
            var framesScanned = 0;
            for (var frame = scanLast; frame >= scanFirst; frame--)
            {
                var realMs = FrameRealCostMs(frame, threadIndex);
                if (realMs < 0.0) continue;

                framesScanned++;
                if (realMs > worstRealMs)
                {
                    worstRealMs = realMs;
                    worstFrame = frame;
                }
            }

            if (worstFrame < 0)
            {
                return "{\"error\":\"Could not read any buffered profiler frame on thread " + threadIndex + ". Ensure the profiler is recording and you are in play mode.\"}";
            }

            return BuildFrameHierarchyJson(worstFrame, threadIndex, topN, framesScanned);
        }

        #endregion

        #region Profiler Window and Frame Helpers

        /// <summary>The open Profiler window, or a newly opened one when none is open.</summary>
        /// <param name="opened">Set true when the window had to be opened.</param>
        /// <returns>The Profiler window.</returns>
        private static ProfilerWindow GetOrOpenProfilerWindow(out bool opened)
        {
            opened = !EditorWindow.HasOpenInstances<ProfilerWindow>();
            return EditorWindow.GetWindow<ProfilerWindow>();
        }

        /// <summary>The Profiler window's modules in its own order, read through its internal module list.</summary>
        /// <param name="window">The Profiler window.</param>
        /// <param name="error">Why the list could not be read, when it could not.</param>
        /// <returns>The modules, or null with <paramref name="error"/> set.</returns>
        private static IList ProfilerWindowModules(ProfilerWindow window, out string error)
        {
            error = null;
            MethodInfo getModules = typeof(ProfilerWindow).GetMethod("GetProfilerModules", PROFILER_WINDOW_MEMBERS, null, Type.EmptyTypes, null);
            if (getModules == null)
            {
                error = "This editor's Profiler window has no GetProfilerModules, so its modules cannot be read.";
                return null;
            }

            var modules = getModules.Invoke(window, null) as IList;
            if (modules == null) error = "The Profiler window returned no module list.";
            return modules;
        }

        /// <summary>A Profiler module's display name, the one its Profiler Modules menu shows.</summary>
        /// <param name="module">A module from <see cref="ProfilerWindowModules"/>.</param>
        /// <returns>The display name, or the module's type name when it has none.</returns>
        private static string ProfilerModuleName(object module)
        {
            PropertyInfo displayName = module.GetType().GetProperty("DisplayName", PROFILER_WINDOW_MEMBERS);
            var name = displayName != null ? displayName.GetValue(module) as string : null;
            return string.IsNullOrEmpty(name) ? module.GetType().Name : name;
        }

        /// <summary>Reads whether a Profiler module is active, as the window holds it.</summary>
        /// <param name="window">The Profiler window.</param>
        /// <param name="module">A module from <see cref="ProfilerWindowModules"/>.</param>
        /// <param name="active">The module's active state.</param>
        /// <param name="error">Why the state could not be read, when it could not.</param>
        /// <returns>True when the state was read.</returns>
        private static bool TryProfilerModuleActive(ProfilerWindow window, object module, out bool active, out string error)
        {
            active = false;
            error = null;
            MethodInfo getActive = typeof(ProfilerWindow).GetMethod("GetProfilerModuleActiveState", PROFILER_WINDOW_MEMBERS);
            if (getActive == null)
            {
                error = "This editor's Profiler window has no GetProfilerModuleActiveState, so module states cannot be read.";
                return false;
            }

            active = (bool)getActive.Invoke(window, new[] { module });
            return true;
        }

        #endregion

        #region Profiler Spike Helpers

        /// <summary>
        /// Reads one record-session counter's ring sample at a frame index, converting nanosecond timings to
        /// milliseconds. Returns <see cref="double.NaN"/> when the counter is unsupported or the index is out
        /// of range, so callers can skip emitting it rather than reporting a misleading zero.
        /// </summary>
        /// <param name="counterIndex">Index into <see cref="ProfilerRecordCounters"/>/<see cref="_profilerRecorders"/>.</param>
        /// <param name="frameIndex">Sample index within the recorder's ring (0 = oldest retained frame).</param>
        /// <returns>The sample value (ms for timings, raw for counts/bytes), or NaN when unavailable.</returns>
        private static double ReadCounterSample(int counterIndex, int frameIndex)
        {
            if (_profilerRecorders == null || counterIndex < 0 || counterIndex >= _profilerRecorders.Length)
            {
                return double.NaN;
            }

            var recorder = _profilerRecorders[counterIndex];
            if (!recorder.Valid || frameIndex < 0 || frameIndex >= recorder.Count)
            {
                return double.NaN;
            }

            var raw = recorder.GetSample(frameIndex).Value;
            return ProfilerRecordCounters[counterIndex].IsTimeNs ? raw / 1e6 : raw;
        }

        /// <summary>Finds a record-counter slot by its stable key, or -1 when absent.</summary>
        /// <param name="key">The counter key to locate (e.g. "mainThreadMs").</param>
        /// <returns>The index into <see cref="ProfilerRecordCounters"/>, or -1.</returns>
        private static int IndexOfRecordCounter(string key)
        {
            var defs = ProfilerRecordCounters;
            for (var i = 0; i < defs.Length; i++)
            {
                if (defs[i].Key == key)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Disposes every recorder in the active record session, clears its state, and puts the profiler and the
        /// Profiler window's Record back as the session found them. Safe to call when no session is active, and
        /// idempotent - used by <c>profiler-record-stop</c>, a restarted session and the domain-reload hook, so
        /// native recorder handles never leak and Record is never left on (the editor layout saves Record and
        /// turns recording back on whenever Unity opens).
        /// </summary>
        private static void DisposeProfilerRecordSession()
        {
            if (_profilerRecordStateCaptured)
            {
                EnableProfilerDriverRecording(_profilerRecordPriorRecording);
                Profiler.enabled = _profilerRecordPriorEnabled;
                _profilerRecordStateCaptured = false;
            }

            if (_profilerRecorders != null)
            {
                for (var i = 0; i < _profilerRecorders.Length; i++)
                {
                    if (_profilerRecorders[i].Valid)
                    {
                        _profilerRecorders[i].Dispose();
                    }
                }

                _profilerRecorders = null;
            }

            _profilerRecordCapacity = 0;
        }

        /// <summary>
        /// Installs, once, a domain-reload hook that disposes any active record session before the editor
        /// reloads the script domain - native <see cref="ProfilerRecorder"/> handles do not survive a reload
        /// and would otherwise leak.
        /// </summary>
        private static void InstallProfilerReloadHook()
        {
            if (_profilerReloadHookInstalled)
            {
                return;
            }

            AssemblyReloadEvents.beforeAssemblyReload += DisposeProfilerRecordSession;
            _profilerReloadHookInstalled = true;
        }

        /// <summary>
        /// Toggles the editor profiler's frame collection so the per-frame hierarchy buffer fills (or stops),
        /// which <c>profiler-frame-hierarchy</c> reads. Guarded because some editor builds gate this property.
        /// </summary>
        /// <param name="enabled">True to start collecting frames into the buffer.</param>
        private static void EnableProfilerDriverRecording(bool enabled)
        {
            try
            {
                ProfilerDriver.enabled = enabled;
            }
            catch (Exception)
            {
                // Older/locked-down editors may not allow toggling collection; the recorder ring still works.
            }
        }

        /// <summary>
        /// The per-frame time budget, in ms, implied by the current target frame rate. Falls back to 90 Hz
        /// (this project's VR target) when the rate is uncapped, so spike detection still has a sane budget.
        /// </summary>
        /// <returns>Target frame budget in milliseconds.</returns>
        private static double TargetFrameBudgetMs()
        {
            var target = Application.targetFrameRate;
            if (target <= 0)
            {
                target = 90;
            }

            return 1000.0 / target;
        }

        /// <summary>
        /// Linearly interpolated percentile over an ascending-sorted sample array.
        /// </summary>
        /// <param name="sortedAscending">Samples sorted ascending; not modified.</param>
        /// <param name="quantile">Quantile in 0..1 (e.g. 0.95 for p95).</param>
        /// <returns>The interpolated percentile value, or 0 for an empty array.</returns>
        private static double Percentile(double[] sortedAscending, double quantile)
        {
            if (sortedAscending == null || sortedAscending.Length == 0)
            {
                return 0.0;
            }

            if (sortedAscending.Length == 1)
            {
                return sortedAscending[0];
            }

            var position = quantile * (sortedAscending.Length - 1);
            var low = (int)Math.Floor(position);
            var high = (int)Math.Ceiling(position);
            var fraction = position - low;
            return sortedAscending[low] + ((sortedAscending[high] - sortedAscending[low]) * fraction);
        }

        /// <summary>Arithmetic mean of a sample array, or 0 when empty.</summary>
        /// <param name="values">The samples to average.</param>
        /// <returns>The mean value.</returns>
        private static double Mean(double[] values)
        {
            if (values == null || values.Length == 0)
            {
                return 0.0;
            }

            var sum = 0.0;
            for (var i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }

            return sum / values.Length;
        }

        /// <summary>
        /// Marker names excluded from <c>profiler-frame-hierarchy</c>'s real-cost ranking and its top list.
        /// Two kinds, neither representative of the shipped VRChat runtime: editor-only work with no equivalent
        /// in a player build (<c>EditorLoop</c>, JIT, profiler bookkeeping), and idle/sync waits where the CPU
        /// is parked rather than doing work (vsync and GPU-present stalls). In editor play mode
        /// <c>EditorLoop</c> alone routinely dwarfs every real marker, so without this filter the tool would
        /// always surface an editor-noise frame instead of the actual gameplay spike.
        /// </summary>
        private static readonly HashSet<string> ProfilerNoiseMarkers = new HashSet<string>(StringComparer.Ordinal)
        {
            "EditorLoop", "Mono.JIT", "Profiler.FlushMemoryCounters", "Profiler.CollectUIStats",
            "Profiler.CollectEditorStats", "Profiler.CollectGlobalStats", "CollectGPUSamples",
            "Idle", "WaitForTargetFPS", "WaitForJobGroupID", "WaitForRenderJobs", "Gfx.WaitForPresentOnGfxThread",
            "Gfx.PresentFrame", "Gfx.WaitForGfxCommandsFromMainThread",
            "GfxDeviceD3D11.WaitForLastPresentationAndGetTimestamp", "Semaphore.WaitForSignal",
        };

        /// <summary>
        /// True for a marker that is not shippable work: a name in <see cref="ProfilerNoiseMarkers"/>, Conduit's own work,
        /// or any profiler bookkeeping marker (<c>Profiler.</c> and <c>GPUProfiler.</c>). The GPU Usage module's own queries
        /// (<c>GPUProfiler.BeginQueries</c>, <c>EndQueries</c>) cost about 0.8 ms of render-thread time in the editor.
        /// </summary>
        /// <param name="name">Marker name.</param>
        /// <returns>True when the marker is editor, profiler or wait time.</returns>
        private static bool IsProfilerNoiseMarker(string name)
        {
            return ProfilerNoiseMarkers.Contains(name)
                || ConduitProfiling.IsConduitMarker(name)
                || name.StartsWith("Profiler.", StringComparison.Ordinal)
                || name.StartsWith("GPUProfiler.", StringComparison.Ordinal);
        }

        /// <summary>
        /// Marker names by marker id for this profiler session. Within a session an id names one marker on every frame and
        /// thread (236,932 samples over 200 frames on two threads matched with none differing), so each name is fetched once
        /// instead of once per sample. Emptied when the profiler clears its frames or loads a capture, and by every script reload.
        /// </summary>
        private static readonly Dictionary<int, string> ProfilerMarkerNames = new Dictionary<int, string>();

        /// <summary>True once the marker name cache is subscribed to the profiler's clear and load events.</summary>
        private static bool _profilerMarkerNamesHooked;

        /// <summary>A marker's name from the session cache, fetched from the view the first time the id is seen.</summary>
        /// <param name="view">A valid frame data view the id comes from.</param>
        /// <param name="markerId">The marker id.</param>
        /// <returns>The marker's name, or an empty string when it has none.</returns>
        private static string CachedProfilerMarkerName(FrameDataView view, int markerId)
        {
            if (!_profilerMarkerNamesHooked)
            {
                _profilerMarkerNamesHooked = true;
                ProfilerDriver.profileCleared += ProfilerMarkerNames.Clear;
                ProfilerDriver.profileLoaded += ProfilerMarkerNames.Clear;
            }

            string name;
            if (ProfilerMarkerNames.TryGetValue(markerId, out name)) return name;
            name = view.GetMarkerName(markerId) ?? string.Empty;
            ProfilerMarkerNames[markerId] = name;
            return name;
        }

        /// <summary>Most recent buffered frames the real-cost ranking scans, bounding its cost on a large buffer.</summary>
        private const int PROFILER_HIERARCHY_MAX_SCAN = 400;

        /// <summary>
        /// Walks a frame's merged sample hierarchy once and aggregates, per marker name, the exclusive (self)
        /// time, GC bytes, and call count summed across every call site. Shared by the real-cost ranking scan
        /// and the final attribution output so both see identical numbers. Work under the editor loop is filed
        /// under it as editor-only, except the player loop and everything below it: when the profiler records the
        /// editor, the game's player loop runs nested inside the editor loop, and it is still the shipping work.
        /// Each of Conduit's own markers, and everything below it, is filed under that marker's name wherever it sits, so
        /// the bridge's cost is reported as its own and split by what it was doing.
        /// </summary>
        /// <param name="view">A valid hierarchy frame data view to walk.</param>
        /// <param name="editorByName">
        /// True to keep the editor's own markers by name, each keyed with <see cref="PROFILER_EDITOR_SCOPE_PREFIX"/> so it never
        /// merges with a game marker of the same name, instead of filing them all under the editor loop.
        /// </param>
        /// <returns>Per-marker-name aggregates for the frame.</returns>
        private static Dictionary<string, MarkerAggregate> WalkFrameAggregates(HierarchyFrameDataView view, bool editorByName = false)
        {
            var byName = new Dictionary<string, MarkerAggregate>(256, StringComparer.Ordinal);
            var stack = new Stack<int>();
            // The owner a sample is filed under: null for shipping work filed by its own name, or the editor loop or
            // Conduit for everything nested beneath them.
            var owners = new Stack<string>();
            var children = new List<int>();
            var root = view.GetRootItemID();
            stack.Push(root);
            owners.Push(null);

            while (stack.Count > 0)
            {
                var id = stack.Pop();
                var owner = owners.Pop();

                // Names come from the marker id through a cache: GetItemName builds a new string per sample, which cost
                // about 79 KB of garbage and 0.6 ms per frame view. The root item has no name of its own.
                var name = id == root ? string.Empty : CachedProfilerMarkerName(view, view.GetItemMarkerID(id));
                if (name == PROFILER_PLAYER_LOOP_MARKER) owner = null;
                else if (ConduitProfiling.IsConduitMarker(name)) owner = name;
                var inside = owner != null;
                children.Clear();
                if (view.HasItemChildren(id)) view.GetItemChildren(id, children);
                if (!string.IsNullOrEmpty(name))
                {
                    // Work nested under the editor loop is editor-only whatever its own name, so it is filed under it,
                    // or under its own name in the editor's scope when the caller asked to see the editor broken down.
                    var key = owner == null ? name
                        : editorByName && owner == PROFILER_EDITOR_LOOP_MARKER ? PROFILER_EDITOR_SCOPE_PREFIX + name
                        : owner;
                    MarkerAggregate aggregate;
                    byName.TryGetValue(key, out aggregate);
                    aggregate.SelfMs += view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnSelfTime);

                    // The GC Alloc column includes the children, so a marker's own garbage is its value less theirs. Unity
                    // records each allocation as a GC.Alloc child of the code that made it, so those children's bytes stay
                    // with the parent, which is the allocator, and GC.Alloc itself owns none.
                    double totalGc = view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnGcMemory);
                    if (name != PROFILER_GC_ALLOC_MARKER)
                    {
                        double childGc = 0.0;
                        for (var k = 0; k < children.Count; k++)
                        {
                            if (CachedProfilerMarkerName(view, view.GetItemMarkerID(children[k])) == PROFILER_GC_ALLOC_MARKER) continue;
                            childGc += view.GetItemColumnDataAsSingle(children[k], HierarchyFrameDataView.columnGcMemory);
                        }

                        aggregate.GcBytes += Math.Max(0.0, totalGc - childGc);
                        if (!inside || key != owner) aggregate.TotalGcBytes += totalGc;
                    }
                    aggregate.Calls += view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnCalls);
                    aggregate.TotalMs += view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnTotalTime);
                    aggregate.SelfGpuMs += view.GetItemColumnDataAsSingle(id, PROFILER_COLUMN_SELF_GPU);
                    aggregate.TotalGpuMs += view.GetItemColumnDataAsSingle(id, PROFILER_COLUMN_TOTAL_GPU);
                    aggregate.DrawCalls += view.GetItemColumnDataAsSingle(id, PROFILER_COLUMN_DRAW_CALLS);
                    byName[key] = aggregate;
                }

                if (children.Count > 0)
                {
                    var childOwner = owner ?? (name == PROFILER_EDITOR_LOOP_MARKER ? PROFILER_EDITOR_LOOP_MARKER : null);
                    for (var k = 0; k < children.Count; k++)
                    {
                        stack.Push(children[k]);
                        owners.Push(childOwner);
                    }
                }
            }

            return byName;
        }

        /// <summary>
        /// Sums the exclusive self-time of every non-noise marker in a frame's aggregates - the frame's real,
        /// shippable CPU cost with editor-only and idle/wait markers removed (see <see cref="ProfilerNoiseMarkers"/>).
        /// </summary>
        /// <param name="aggregates">Per-marker aggregates from <see cref="WalkFrameAggregates"/>.</param>
        /// <returns>Total non-noise self-time in ms.</returns>
        private static double RealCostMs(Dictionary<string, MarkerAggregate> aggregates)
        {
            var sum = 0.0;
            foreach (var pair in aggregates)
            {
                if (!IsProfilerNoiseMarker(pair.Key))
                {
                    sum += pair.Value.SelfMs;
                }
            }

            return sum;
        }

        /// <summary>Item ids waiting to be visited by <see cref="SumRealWorkMs"/>, reused so the walk allocates nothing.</summary>
        private static readonly Stack<int> RealWorkStack = new Stack<int>();

        /// <summary>Owner of each waiting item in <see cref="RealWorkStack"/>, as <see cref="WalkFrameAggregates"/> files them.</summary>
        private static readonly Stack<string> RealWorkOwners = new Stack<string>();

        /// <summary>Children of the item being visited by <see cref="SumRealWorkMs"/>.</summary>
        private static readonly List<int> RealWorkChildren = new List<int>();

        /// <summary>
        /// The same total as <see cref="RealCostMs"/> over <see cref="WalkFrameAggregates"/>, summed during one walk without
        /// building the per-marker table: the self time of every sample filed under its own name (not under the editor loop
        /// or Conduit) whose name is not noise. Every per-frame figure read goes through it, so it reuses its collections and
        /// allocates nothing; building the table for each frame on two threads cost about 1.8 MB per settle check. Main
        /// thread only.
        /// </summary>
        /// <param name="view">A valid hierarchy frame data view.</param>
        /// <returns>Total non-noise self time in ms.</returns>
        private static double SumRealWorkMs(HierarchyFrameDataView view)
        {
            RealWorkStack.Clear();
            RealWorkOwners.Clear();
            var root = view.GetRootItemID();
            RealWorkStack.Push(root);
            RealWorkOwners.Push(null);
            var sum = 0.0;
            while (RealWorkStack.Count > 0)
            {
                var id = RealWorkStack.Pop();
                var owner = RealWorkOwners.Pop();
                var name = id == root ? string.Empty : CachedProfilerMarkerName(view, view.GetItemMarkerID(id));
                if (name == PROFILER_PLAYER_LOOP_MARKER) owner = null;
                else if (ConduitProfiling.IsConduitMarker(name)) owner = ConduitProfiling.MARKER_NAME;
                if (owner == null && name.Length > 0 && !IsProfilerNoiseMarker(name))
                {
                    sum += view.GetItemColumnDataAsSingle(id, HierarchyFrameDataView.columnSelfTime);
                }

                RealWorkChildren.Clear();
                if (!view.HasItemChildren(id)) continue;
                view.GetItemChildren(id, RealWorkChildren);
                var childOwner = owner ?? (name == PROFILER_EDITOR_LOOP_MARKER ? PROFILER_EDITOR_LOOP_MARKER : null);
                for (var k = 0; k < RealWorkChildren.Count; k++)
                {
                    RealWorkStack.Push(RealWorkChildren[k]);
                    RealWorkOwners.Push(childOwner);
                }
            }

            return sum;
        }

        /// <summary>
        /// Builds one buffered frame's hierarchy, computes its real (non-noise) self-time cost, and disposes
        /// the native view. Returns -1 when the frame or thread cannot be read, so the caller can skip it.
        /// </summary>
        /// <param name="frame">Frame number as the Profiler window shows it.</param>
        /// <param name="threadIndex">Thread index within the frame (0 = main thread).</param>
        /// <returns>Real cost in ms, or -1 when unavailable.</returns>
        private static double FrameRealCostMs(int frame, int threadIndex)
        {
            HierarchyFrameDataView view = null;
            try
            {
                view = ProfilerHierarchyFrame(frame, threadIndex);

                if (view == null || !view.valid)
                {
                    return -1.0;
                }

                return SumRealWorkMs(view);
            }
            catch (Exception)
            {
                return -1.0;
            }
            finally
            {
                if (view != null)
                {
                    view.Dispose();
                }
            }
        }

        /// <summary>
        /// Builds the final attribution report for the chosen frame: walks its hierarchy, splits markers into
        /// real (shippable) work and editor/idle noise, and returns the real markers ranked by exclusive self
        /// time, plus the frame's real-vs-total cost split and the noise markers that were filtered out (so
        /// nothing is hidden). This is the per-script attribution behind <c>profiler-frame-hierarchy</c>.
        /// </summary>
        /// <param name="frame">Frame number to inspect, as the Profiler window shows it.</param>
        /// <param name="threadIndex">Thread index within the frame (0 = main thread).</param>
        /// <param name="topN">Number of top real-work markers to return.</param>
        /// <param name="framesScanned">How many recent frames the ranking pass considered, echoed in the report; 0 when the caller chose the frame.</param>
        /// <returns>A compact JSON report, or a JSON error object when the frame has no readable hierarchy.</returns>
        private static string BuildFrameHierarchyJson(int frame, int threadIndex, int topN, int framesScanned)
        {
            HierarchyFrameDataView view = null;
            try
            {
                view = ProfilerHierarchyFrame(frame, threadIndex);

                if (view == null || !view.valid)
                {
                    return "{\"error\":\"Profiler frame " + frame + " has no readable hierarchy on thread " + threadIndex + ".\"}";
                }

                var byName = WalkFrameAggregates(view);

                // Split markers into shippable work and editor/idle noise, tracking each pool's self-time so
                // the caller sees how much of the frame was real versus editor overhead.
                var real = new List<KeyValuePair<string, MarkerAggregate>>(byName.Count);
                var excluded = new List<KeyValuePair<string, MarkerAggregate>>();
                var realCostMs = 0.0;
                var excludedMs = 0.0;
                foreach (var pair in byName)
                {
                    if (IsProfilerNoiseMarker(pair.Key))
                    {
                        excluded.Add(pair);
                        excludedMs += pair.Value.SelfMs;
                    }
                    else
                    {
                        real.Add(pair);
                        realCostMs += pair.Value.SelfMs;
                    }
                }

                real.Sort((a, b) => b.Value.SelfMs.CompareTo(a.Value.SelfMs));
                excluded.Sort((a, b) => b.Value.SelfMs.CompareTo(a.Value.SelfMs));

                var sb = new StringBuilder();
                sb.Append("{\"frame\":").Append(frame)
                    .Append(",\"threadIndex\":").Append(threadIndex)
                    .Append(",\"framesScanned\":").Append(framesScanned)
                    .Append(",\"frameTimeMs\":").Append(JsonNumber(view.frameTimeMs))
                    .Append(",\"realCostMs\":").Append(JsonNumber(realCostMs))
                    .Append(",\"excludedEditorMs\":").Append(JsonNumber(excludedMs))
                    .Append(",\"markerCount\":").Append(byName.Count)
                    .Append(",\"realMarkerCount\":").Append(real.Count)
                    .Append(",\"topNReturned\":").Append(Math.Min(topN, real.Count))
                    .Append(",\"topBySelfTime\":[");

                var limit = Math.Min(topN, real.Count);
                for (var i = 0; i < limit; i++)
                {
                    var entry = real[i];
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append("{\"name\":").Append(JsonQuote(entry.Key))
                        .Append(",\"selfTimeMs\":").Append(JsonNumber(entry.Value.SelfMs))
                        .Append(",\"gcAllocBytes\":").Append(JsonNumber(entry.Value.GcBytes))
                        .Append(",\"calls\":").Append(JsonNumber(entry.Value.Calls))
                        .Append('}');
                }

                // Echo the filtered editor/idle markers (worst first, capped) so the exclusion is transparent.
                sb.Append("],\"excludedMarkers\":[");
                var excludedLimit = Math.Min(excluded.Count, 8);
                for (var i = 0; i < excludedLimit; i++)
                {
                    var entry = excluded[i];
                    if (i > 0)
                    {
                        sb.Append(',');
                    }

                    sb.Append("{\"name\":").Append(JsonQuote(entry.Key))
                        .Append(",\"selfTimeMs\":").Append(JsonNumber(entry.Value.SelfMs))
                        .Append('}');
                }

                sb.Append(framesScanned == 0 ? "],\"hint\":\"Frame chosen by the caller. realCostMs = self-time of shippable work, EXCLUDING" : "],\"hint\":\"Frame picked by realCostMs = self-time of shippable work, EXCLUDING");
                sb.Append(" the editor-only/idle markers in excludedMarkers (EditorLoop, Mono.JIT, Profiler.*, vsync/gfx waits) which do not exist in a VRChat build. topBySelfTime is real work only - UdonBehaviour/script markers, TMP GenerateText, GC.Collect, Camera.Render; selfTimeMs is exclusive cost summed across call sites, large gcAllocBytes means allocation/GC-driven.\"}");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return "{\"error\":\"Failed to read profiler frame " + frame + " hierarchy: " + JsonEscape(ex.Message) + "\"}";
            }
            finally
            {
                if (view != null)
                {
                    view.Dispose();
                }
            }
        }

        #endregion
            /// <summary>
        /// Routes the Profiler verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchProfiler(string verb, string command, string[] parts)
        {
            if (verb == "profiler-start")
            {
                return ProfilerStartCommand();
            }

            if (verb == "profiler-stop")
            {
                return ProfilerStopCommand();
            }

            if (verb == "profiler-get-status")
            {
                return ProfilerGetStatusCommand();
            }

            if (verb == "profiler-capture-frame")
            {
                return ProfilerCaptureFrameCommand();
            }

            if (verb == "profiler-get-memory-stats")
            {
                return ProfilerGetMemoryStatsCommand();
            }

            if (verb == "profiler-get-rendering-stats")
            {
                return ProfilerGetRenderingStatsCommand();
            }

            if (verb == "profiler-get-script-stats")
            {
                return ProfilerGetScriptStatsCommand();
            }

            if (verb == "profiler-list-modules")
            {
                return ProfilerListModulesCommand();
            }

            if (verb == "profiler-enable-module")
            {
                return ProfilerEnableModuleCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "profiler-clear-data")
            {
                return ProfilerClearDataCommand();
            }

            if (verb == "profiler-save-data")
            {
                return ProfilerSaveDataCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "profiler-load-data")
            {
                return ProfilerLoadDataCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "profiler-record-start")
            {
                return ProfilerRecordStartCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "profiler-record-stop")
            {
                return ProfilerRecordStopCommand();
            }

            if (verb == "profiler-frame-hierarchy")
            {
                return ProfilerFrameHierarchyCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "profiler-select-frame")
            {
                return ProfilerSelectFrameCommand(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
