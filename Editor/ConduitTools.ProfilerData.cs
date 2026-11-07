/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Profiling;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Profiling;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// The profiler's buffered frame data read as figures, markers and counters, compared between ranges with
    /// intervals, and the measurement verbs built on it: frame labels, thread and counter discovery, marker
    /// totals and timelines, profiler settings, isolating one object's cost in play mode, object memory, and
    /// Frame Debugger capture.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Profiler Data Constants

        /// <summary>Most frames one range may hold, in <c>profiler-frames</c> and every range-taking verb.</summary>
        private const int PROFILER_FRAMES_MAX = 1000;

        /// <summary>Thread name the profiler gives the main thread.</summary>
        private const string PROFILER_MAIN_THREAD_NAME = "Main Thread";

        /// <summary>Thread name the profiler gives the render thread.</summary>
        private const string PROFILER_RENDER_THREAD_NAME = "Render Thread";

        /// <summary>Most thread indices searched for a thread name; the editor records around a hundred threads.</summary>
        private const int PROFILER_THREAD_SEARCH_MAX = 256;

        /// <summary>Marker under which the editor's own frame work runs; everything beneath it is editor-only, except the player loop.</summary>
        private const string PROFILER_EDITOR_LOOP_MARKER = "EditorLoop";

        /// <summary>Key prefix for the editor's own markers when they are kept by name rather than filed under the editor loop.</summary>
        private const string PROFILER_EDITOR_SCOPE_PREFIX = "EditorLoop/";

        /// <summary>Sample Unity records under the code that made each managed allocation.</summary>
        private const string PROFILER_GC_ALLOC_MARKER = "GC.Alloc";

        /// <summary>Marker under which the game's own frame work runs; it nests inside the editor loop when the editor is profiled.</summary>
        private const string PROFILER_PLAYER_LOOP_MARKER = "PlayerLoop";

        /// <summary>Frames per block in a comparison when the caller names none.</summary>
        private const int PROFILER_COMPARE_BLOCK_FRAMES = 10;

        /// <summary>
        /// Conduit numbers profiler frames as the Profiler window's Frame label shows them, which is the profiler's own
        /// frame index plus this. Every frame number a profiler verb takes or reports is in the window's numbering, so a
        /// frame named in a reply is the frame the window shows under that number; only <see cref="ProfilerFirstFrame"/>,
        /// <see cref="ProfilerLastFrame"/>, <see cref="ProfilerRawFrame"/>, <see cref="ProfilerHierarchyFrame"/> and the
        /// window selection convert to the index.
        /// </summary>
        private const int PROFILER_FRAME_NUMBER_OFFSET = 1;

        /// <summary>Most markers a markers comparison returns, largest difference first.</summary>
        private const int PROFILER_COMPARE_MARKERS_MAX = 25;

        /// <summary>Two-sided 95% Student t critical values for 1 to 4 degrees of freedom, where the series approximation is poor.</summary>
        private static readonly double[] ProfilerTCritical95Small = { 12.706, 4.303, 3.182, 2.776 };

        /// <summary>Session key holding the frame labels, which survive a script reload but not an editor restart.</summary>
        private const string PROFILER_MARKS_SESSION_KEY = "ItsRenji.Conduit.ProfilerMarks.WindowNumbering";

        /// <summary>Longest frame label accepted.</summary>
        private const int PROFILER_LABEL_MAX = 64;

        /// <summary>HierarchyFrameDataView column ids Unity keeps internal, read from the type registry: draw calls.</summary>
        private const int PROFILER_COLUMN_DRAW_CALLS = 7;

        /// <summary>HierarchyFrameDataView column id for total GPU time (internal in Unity 2022).</summary>
        private const int PROFILER_COLUMN_TOTAL_GPU = 8;

        /// <summary>HierarchyFrameDataView column id for self GPU time (internal in Unity 2022).</summary>
        private const int PROFILER_COLUMN_SELF_GPU = 9;

        /// <summary>Most counters <c>profiler-counters</c> lists in one reply.</summary>
        private const int PROFILER_COUNTERS_LIST_MAX = 500;

        /// <summary>Shortest window, in seconds of frame time, the settle test compares; frame counts differ by machine, time does not.</summary>
        private const double PROFILER_SETTLE_WINDOW_SECONDS = 1.0;

        /// <summary>Fewest whole blocks each settle window holds, so its interval has a spread to come from.</summary>
        private const int PROFILER_SETTLE_MIN_BLOCKS = 3;

        /// <summary>
        /// Multiples of <see cref="PROFILER_SETTLE_WINDOW_SECONDS"/> the settle test tries in turn when a window is too noisy
        /// to tell; a wider window narrows the interval.
        /// </summary>
        private static readonly double[] ProfilerSettleWindowScales = { 1.0, 2.0, 4.0 };

        /// <summary>
        /// Settled requires the whole 95% interval of the window difference inside this fraction of the figure's own level,
        /// so the band scales with the machine instead of being a fixed number of milliseconds.
        /// </summary>
        private const double PROFILER_SETTLE_TOLERANCE = 0.05;

        /// <summary>Most new frames the settle watch reads per editor tick; it keeps up with recording while costing about 1 to 2 ms a tick.</summary>
        private const int PROFILER_SETTLE_READS_PER_TICK = 2;

        /// <summary>Seconds between settle checks, so reading frames never becomes the load being judged.</summary>
        private const double PROFILER_SETTLE_CHECK_SECONDS = 0.25;

        /// <summary>Seconds a settle wait allows when the caller names none, and each <c>profiler-isolate</c> phase allows.</summary>
        private const int PROFILER_SETTLE_DEFAULT_SECONDS = 30;

        /// <summary>Most seconds <c>profiler-wait-settled</c> accepts.</summary>
        private const int PROFILER_SETTLE_MAX_SECONDS = 300;

        /// <summary>Frames <c>profiler-marker-timeline</c> lists by default: the worst by self time and the worst by garbage.</summary>
        private const int PROFILER_TIMELINE_WORST = 10;

        /// <summary>Most threads <c>profiler-threads</c> lists in one reply.</summary>
        private const int PROFILER_THREADS_LIST_MAX = 100;

        /// <summary>Display name of the Profiler window module that records GPU time.</summary>
        private const string PROFILER_GPU_MODULE_NAME = "GPU Usage";

        /// <summary>Default cycles of <c>profiler-isolate</c>; each cycle is one phase with the target and one without.</summary>
        private const int PROFILER_ISOLATE_DEFAULT_CYCLES = 3;

        /// <summary>Default measured frames per phase of <c>profiler-isolate</c>.</summary>
        private const int PROFILER_ISOLATE_DEFAULT_PHASE_FRAMES = 90;

        /// <summary>Longest <c>profiler-isolate</c> may run before it restores the target and gives up.</summary>
        private const double PROFILER_ISOLATE_MAX_SECONDS = 600.0;

        /// <summary>Seconds <c>frame-debugger-capture</c> waits for the first captured frame.</summary>
        private const double FRAME_DEBUGGER_CAPTURE_SECONDS = 10.0;

        /// <summary>Seconds <c>frame-debugger-capture</c> waits for one event's details.</summary>
        private const double FRAME_DEBUGGER_DETAIL_SECONDS = 2.0;

        /// <summary>Most events <c>frame-debugger-capture</c> reads details for.</summary>
        private const int FRAME_DEBUGGER_DETAILS_MAX = 200;

        /// <summary>Most groups each Frame Debugger grouping returns.</summary>
        private const int FRAME_DEBUGGER_GROUPS_MAX = 40;

        /// <summary>
        /// Editor ticks a Frame Debugger window the capture opened is given to draw itself before the debugger is switched
        /// on; enabled in the tick it opened, the window builds its event tree before it is set up and throws.
        /// </summary>
        private const int FRAME_DEBUGGER_WINDOW_SETTLE_TICKS = 2;

        /// <summary>Seconds <c>frame-debugger-capture</c> waits for the editor to report itself active after focusing it.</summary>
        private const double FRAME_DEBUGGER_FOCUS_SECONDS = 3.0;

        /// <summary>Frame Debugger event types that draw something, by enum name.</summary>
        private static readonly HashSet<string> FrameDebuggerDrawTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "StaticBatch", "DynamicBatch", "Mesh", "DynamicGeometry", "GLDraw", "SkinOnGPU", "DrawProcedural",
            "DrawProceduralIndirect", "DrawProceduralIndexed", "DrawProceduralIndexedIndirect", "InstancedMesh",
            "SRPBatch", "HybridBatch",
        };

        /// <summary>Object types <c>profiler-memory-objects</c> sums when the caller names none.</summary>
        private static readonly string[] ProfilerMemoryDefaultTypes =
        {
            "Texture", "Mesh", "AudioClip", "Material", "AnimationClip", "Shader", "Font",
        };

        /// <summary>Column kinds of <see cref="ProfilerColumnSpec"/>.</summary>
        private const int PROFILER_KIND_FRAME_CPU = 0;
        private const int PROFILER_KIND_FRAME_GPU = 1;
        private const int PROFILER_KIND_THREAD_WORK = 2;
        private const int PROFILER_KIND_COUNTER = 3;

        #endregion

        #region Profiler Data Types

        /// <summary>One figure read from every frame: whole-frame CPU or GPU time, a thread's shippable work, or a named counter.</summary>
        private sealed class ProfilerColumnSpec
        {
            /// <summary>JSON key the figure is reported under.</summary>
            public string Key;

            /// <summary>One of the PROFILER_KIND constants.</summary>
            public int Kind;

            /// <summary>Thread name for thread work, counter name for a counter, otherwise empty.</summary>
            public string Name;

            /// <summary>Thread index last found for <see cref="Name"/>, re-checked against the name every frame.</summary>
            public int ThreadIndex = -1;
        }

        /// <summary>An inclusive range of profiler frame numbers.</summary>
        private struct ProfilerFrameRange
        {
            /// <summary>First frame.</summary>
            public int From;

            /// <summary>Last frame, inclusive.</summary>
            public int To;
        }

        /// <summary>Running state of one <c>profiler-isolate</c>, advanced on editor ticks.</summary>
        private sealed class ProfilerIsolateRun
        {
            /// <summary>Job the result is reported to.</summary>
            public string JobId;

            /// <summary>The object being measured.</summary>
            public GameObject Target;

            /// <summary>Hierarchy path of the target, for the report.</summary>
            public string TargetPath;

            /// <summary>What is switched: object, renderers or component.</summary>
            public string What;

            /// <summary>The component switched when <see cref="What"/> is component and it is a Behaviour.</summary>
            public Behaviour Behaviour;

            /// <summary>The component switched when <see cref="What"/> is component and it is a Renderer.</summary>
            public Renderer RendererComponent;

            /// <summary>The component switched when <see cref="What"/> is component and it is a Collider.</summary>
            public Collider ColliderComponent;

            /// <summary>The target's own active flag as found.</summary>
            public bool FoundActive;

            /// <summary>The component's enabled flag as found.</summary>
            public bool FoundComponentEnabled;

            /// <summary>Every renderer on and below the target when <see cref="What"/> is renderers.</summary>
            public Renderer[] Renderers;

            /// <summary>Each renderer's enabled flag as found, in <see cref="Renderers"/> order.</summary>
            public bool[] FoundRenderersEnabled;

            /// <summary>Cycles to run; each is one phase as found and one switched.</summary>
            public int Cycles;

            /// <summary>Frames measured per phase.</summary>
            public int PhaseFrames;

            /// <summary>Index of the phase in progress, from 0.</summary>
            public int Phase;

            /// <summary>True while the phase in progress has the target as found.</summary>
            public bool PhaseWithTarget;

            /// <summary>0 while the phase is settling after its switch, 1 while it is being measured.</summary>
            public int PhaseState;

            /// <summary>Settle watch of the phase in progress.</summary>
            public ProfilerSettleWatch Settle;

            /// <summary>How long each phase took to settle, as JSON objects.</summary>
            public readonly StringBuilder SettleJson = new StringBuilder();

            /// <summary>First frame measured in the phase in progress.</summary>
            public int PhaseFrom;

            /// <summary>Editor time after which the run gives up.</summary>
            public double Deadline;

            /// <summary>Profiler.enabled as found.</summary>
            public bool ProfilerWasEnabled;

            /// <summary>Whether the profiler was recording as found.</summary>
            public bool RecordingWas;

            /// <summary>True when the caller asked for GPU time.</summary>
            public bool WantGpu;

            /// <summary>Whether the GPU Usage module was on as found.</summary>
            public bool GpuModuleWas;

            /// <summary>True when reading the GPU Usage module had to open the Profiler window.</summary>
            public bool ProfilerWindowOpened;

            /// <summary>Measured ranges with the target as found.</summary>
            public readonly List<ProfilerFrameRange> With = new List<ProfilerFrameRange>();

            /// <summary>Measured ranges with the target switched the other way.</summary>
            public readonly List<ProfilerFrameRange> Without = new List<ProfilerFrameRange>();

            /// <summary>Editor update callback, kept so it can be removed.</summary>
            public EditorApplication.CallbackFunction Tick;

            /// <summary>Script reload callback, kept so it can be removed.</summary>
            public AssemblyReloadEvents.AssemblyReloadCallback BeforeReload;
        }

        /// <summary>Watches the frames recorded after a change until they stop trending; see <see cref="ProfilerSettleCheck"/>.</summary>
        private sealed class ProfilerSettleWatch
        {
            /// <summary>First frame recorded after the change; earlier frames are never judged.</summary>
            public int StartFrame;

            /// <summary>Editor time the watch began.</summary>
            public double StartTime;

            /// <summary>Editor time after which the watch gives up.</summary>
            public double Deadline;

            /// <summary>Editor time of the next check.</summary>
            public double NextCheck;

            /// <summary>Next frame to read into <see cref="Values"/>; frames are read a few per tick as they arrive.</summary>
            public int NextRead = -1;

            /// <summary>Seconds from the start of the watch to the check that passed.</summary>
            public double SettledSeconds;

            /// <summary>Newest frame of the check that passed, or -1 while unsettled.</summary>
            public int SettledFrame = -1;

            /// <summary>The last full check's windows and intervals as JSON, or null before the first full check.</summary>
            public string LastCheck;

            /// <summary>Figures read per frame: whole-frame CPU and GPU time, main-thread and render-thread work.</summary>
            public List<ProfilerColumnSpec> Columns;

            /// <summary>Figures already read, by frame (null for an unreadable frame), so each frame is read once.</summary>
            public readonly Dictionary<int, double[]> Values = new Dictionary<int, double[]>();
        }

        /// <summary>Running state of one <c>profiler-wait-settled</c>, advanced on editor ticks.</summary>
        private sealed class ProfilerSettleRun
        {
            /// <summary>Job the result is reported to.</summary>
            public string JobId;

            /// <summary>The watch being advanced.</summary>
            public ProfilerSettleWatch Watch;

            /// <summary>Seconds the caller allowed.</summary>
            public int MaxSeconds;

            /// <summary>Editor update callback, kept so it can be removed.</summary>
            public EditorApplication.CallbackFunction Tick;

            /// <summary>Script reload callback, kept so it can be removed.</summary>
            public AssemblyReloadEvents.AssemblyReloadCallback BeforeReload;
        }

        /// <summary>Running state of one <c>frame-debugger-capture</c>, advanced on editor ticks.</summary>
        private sealed class FrameDebuggerCaptureRun
        {
            /// <summary>Job the result is reported to.</summary>
            public string JobId;

            /// <summary>How many draw events to read details for.</summary>
            public int DetailsWanted;

            /// <summary>Name fragment choosing the detail events, or empty for the first ones.</summary>
            public string Filter;

            /// <summary>The window in front when the capture began, handed the foreground back afterwards.</summary>
            public IntPtr PreviousForeground;

            /// <summary>True when this platform can bring the editor forward.</summary>
            public bool FocusSupported;

            /// <summary>True when the capture brought the editor forward.</summary>
            public bool BroughtToFront;

            /// <summary>True when the Frame Debugger was already on.</summary>
            public bool WasEnabled;

            /// <summary>True when the capture paused a running play mode, as the Frame Debugger window itself does.</summary>
            public bool PausedForCapture;

            /// <summary>The Frame Debugger window the capture opened, or null when one was already open.</summary>
            public EditorWindow OpenedWindow;

            /// <summary>0 while waiting for the capture, 1 while stepping through detail events.</summary>
            public int Step;

            /// <summary>Editor time after which the current step gives up.</summary>
            public double Deadline;

            /// <summary>Events in the captured frame.</summary>
            public int EventCount;

            /// <summary>Event indices chosen for details.</summary>
            public readonly List<int> DetailIndices = new List<int>();

            /// <summary>Position in <see cref="DetailIndices"/> being read.</summary>
            public int DetailCursor;

            /// <summary>Editor ticks left before the Frame Debugger is switched on, while a window the capture opened sets up.</summary>
            public int EnableDelayTicks;

            /// <summary>Whether the Frame Debugger's last event-data read reported data at all.</summary>
            public bool LastDataReturned;

            /// <summary>Event index the last event-data read carried, or -1 before any read.</summary>
            public int LastDataIndex = -1;

            /// <summary>The event summary as JSON members.</summary>
            public string EventsJson;

            /// <summary>Detail objects read so far, as JSON.</summary>
            public readonly StringBuilder DetailsJson = new StringBuilder();

            /// <summary>Details read so far.</summary>
            public int DetailsRead;

            /// <summary>Batch-break reasons counted over the details read.</summary>
            public readonly Dictionary<string, int> BreakCauses = new Dictionary<string, int>(StringComparer.Ordinal);

            /// <summary>The Frame Debugger's batch-break reason strings, by cause index.</summary>
            public string[] BreakCauseNames;

            /// <summary>Each event's object, by event index.</summary>
            public object[] EventObjects;

            /// <summary>Each event's type name, by event index.</summary>
            public string[] EventTypes;

            /// <summary>Editor update callback, kept so it can be removed.</summary>
            public EditorApplication.CallbackFunction Tick;

            /// <summary>Script reload callback, kept so it can be removed.</summary>
            public AssemblyReloadEvents.AssemblyReloadCallback BeforeReload;
        }

        #endregion

        #region Profiler Data State

        /// <summary>The <c>profiler-isolate</c> run in progress, or null.</summary>
        private static ProfilerIsolateRun _profilerIsolateRun;

        /// <summary>The <c>profiler-wait-settled</c> wait in progress, or null.</summary>
        private static ProfilerSettleRun _profilerSettleRun;

        /// <summary>The <c>frame-debugger-capture</c> run in progress, or null.</summary>
        private static FrameDebuggerCaptureRun _frameDebuggerRun;

        #endregion

        #region Frame Numbers

        /// <summary>The first buffered frame in the window's numbering, or -1 when none is buffered.</summary>
        /// <returns>The frame number.</returns>
        private static int ProfilerFirstFrame()
        {
            int index = ProfilerDriver.firstFrameIndex;
            return index < 0 ? -1 : index + PROFILER_FRAME_NUMBER_OFFSET;
        }

        /// <summary>The newest buffered frame in the window's numbering, or -1 when none is buffered.</summary>
        /// <returns>The frame number.</returns>
        private static int ProfilerLastFrame()
        {
            int index = ProfilerDriver.lastFrameIndex;
            return index < 0 ? -1 : index + PROFILER_FRAME_NUMBER_OFFSET;
        }

        /// <summary>The raw data of one thread of a frame given in the window's numbering.</summary>
        /// <param name="frame">Frame number as the window shows it.</param>
        /// <param name="thread">Thread index.</param>
        /// <returns>The view, which the caller disposes, or null.</returns>
        private static RawFrameDataView ProfilerRawFrame(int frame, int thread)
        {
            return ProfilerDriver.GetRawFrameDataView(frame - PROFILER_FRAME_NUMBER_OFFSET, thread);
        }

        /// <summary>The merged sample hierarchy of one thread of a frame given in the window's numbering.</summary>
        /// <param name="frame">Frame number as the window shows it.</param>
        /// <param name="thread">Thread index.</param>
        /// <returns>The view, which the caller disposes, or null.</returns>
        private static HierarchyFrameDataView ProfilerHierarchyFrame(int frame, int thread)
        {
            return ProfilerDriver.GetHierarchyFrameDataView(frame - PROFILER_FRAME_NUMBER_OFFSET, thread, HierarchyFrameDataView.ViewModes.MergeSamplesWithTheSameName, HierarchyFrameDataView.columnSelfTime, false);
        }

        #endregion

        #region Frame Labels

        /// <summary>
        /// Labels the newest buffered profiler frame, so ranges can be written as label..label instead of frame numbers
        /// read off a previous reply. A label already in use moves to the new frame and the reply says so.
        /// </summary>
        /// <param name="args">The label: letters, digits, dot, underscore and hyphen.</param>
        /// <returns>JSON naming the frame labelled, or an error.</returns>
        private static string ProfilerMarkCommand(string args)
        {
            string label = (args ?? string.Empty).Trim();
            string labelError = ProfilerLabelError(label);
            if (labelError != null) return "{\"error\":" + JsonQuote(labelError + " Nothing was labelled.") + "}";

            int first = ProfilerFirstFrame();
            int last = ProfilerLastFrame();
            if (first < 0 || last < 0) return "{\"error\":\"No profiler frames are buffered, so there is no frame to label. Nothing was labelled.\"}";

            Dictionary<string, int> marks = LoadProfilerMarks();
            int previous;
            bool replaced = marks.TryGetValue(label, out previous);
            marks[label] = last;
            SaveProfilerMarks(marks);
            var sb = new StringBuilder();
            sb.Append("{\"label\":").Append(JsonQuote(label))
                .Append(",\"frame\":").Append(last)
                .Append(",\"replaced\":").Append(replaced ? previous.ToString() : "null")
                .Append(",\"firstBuffered\":").Append(first)
                .Append(",\"lastBuffered\":").Append(last)
                .Append(",\"recording\":").Append(ProfilerDriver.enabled ? "true" : "false");
            if (!ProfilerDriver.enabled) sb.Append(",\"warning\":\"The profiler is not recording, so no frames after this one will be buffered.\"");
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Lists the frame labels with whether each frame is still buffered, or clears them all.</summary>
        /// <param name="args">"list" (default) or "clear".</param>
        /// <returns>JSON of the labels, or of how many were cleared.</returns>
        private static string ProfilerMarksCommand(string args)
        {
            string action = (args ?? string.Empty).Trim().ToLowerInvariant();
            if (action.Length == 0) action = "list";
            Dictionary<string, int> marks = LoadProfilerMarks();
            if (action == "clear")
            {
                SaveProfilerMarks(new Dictionary<string, int>());
                return "{\"cleared\":" + marks.Count + "}";
            }

            if (action != "list") return "{\"error\":" + JsonQuote("action must be list or clear; '" + action + "' is neither. Nothing changed.") + "}";

            int first = ProfilerFirstFrame();
            int last = ProfilerLastFrame();
            var sb = new StringBuilder();
            sb.Append("{\"firstBuffered\":").Append(first).Append(",\"lastBuffered\":").Append(last).Append(",\"labels\":[");
            int written = 0;
            foreach (var pair in marks)
            {
                if (written++ > 0) sb.Append(',');
                bool buffered = first >= 0 && pair.Value >= first && pair.Value <= last;
                sb.Append("{\"label\":").Append(JsonQuote(pair.Key)).Append(",\"frame\":").Append(pair.Value)
                    .Append(",\"buffered\":").Append(buffered ? "true" : "false").Append('}');
            }

            sb.Append("]}");
            return sb.ToString();
        }

        /// <summary>Why a label is unusable, or null when it is fine.</summary>
        /// <param name="label">Candidate label.</param>
        /// <returns>The problem, or null.</returns>
        private static string ProfilerLabelError(string label)
        {
            if (label.Length == 0) return "Usage: profiler-mark <label>.";
            if (label.Length > PROFILER_LABEL_MAX) return "A label is at most " + PROFILER_LABEL_MAX + " characters.";
            foreach (char c in label)
            {
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-' && c != '.') return "A label may hold letters, digits, dot, underscore and hyphen only; '" + label + "' has '" + c + "'.";
            }

            int number;
            if (int.TryParse(label, out number)) return "A label cannot be a number, which would read as a frame number.";
            return null;
        }

        /// <summary>Reads the stored labels.</summary>
        /// <returns>Label to frame, in insertion order.</returns>
        private static Dictionary<string, int> LoadProfilerMarks()
        {
            var marks = new Dictionary<string, int>(StringComparer.Ordinal);
            string stored = SessionState.GetString(PROFILER_MARKS_SESSION_KEY, string.Empty);
            foreach (string line in stored.Split('\n'))
            {
                int equals = line.LastIndexOf('=');
                int frame;
                if (equals > 0 && int.TryParse(line.Substring(equals + 1), out frame)) marks[line.Substring(0, equals)] = frame;
            }

            return marks;
        }

        /// <summary>Stores the labels for the rest of the editor session.</summary>
        /// <param name="marks">Label to frame.</param>
        private static void SaveProfilerMarks(Dictionary<string, int> marks)
        {
            var sb = new StringBuilder();
            foreach (var pair in marks)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(pair.Key).Append('=').Append(pair.Value);
            }

            SessionState.SetString(PROFILER_MARKS_SESSION_KEY, sb.ToString());
        }

        /// <summary>Resolves a frame number or a label to a frame number.</summary>
        /// <param name="token">Frame number or label.</param>
        /// <param name="frame">The frame.</param>
        /// <returns>Null on success, or why it could not be resolved.</returns>
        private static string ResolveProfilerFrameToken(string token, out int frame)
        {
            frame = -1;
            token = (token ?? string.Empty).Trim();
            if (int.TryParse(token, out frame)) return null;
            int marked;
            if (LoadProfilerMarks().TryGetValue(token, out marked))
            {
                frame = marked;
                return null;
            }

            return "'" + token + "' is neither a frame number nor a label from profiler-mark.";
        }

        /// <summary>
        /// Parses "start..end", each end a frame number or a label, and checks the whole range is buffered and no longer
        /// than <see cref="PROFILER_FRAMES_MAX"/> frames.
        /// </summary>
        /// <param name="token">The range text.</param>
        /// <param name="range">The parsed range.</param>
        /// <returns>Null on success, or why the range is unusable.</returns>
        private static string ParseProfilerRange(string token, out ProfilerFrameRange range)
        {
            range = new ProfilerFrameRange();
            token = (token ?? string.Empty).Trim();
            int dots = token.IndexOf("..", StringComparison.Ordinal);
            if (dots <= 0 || dots + 2 >= token.Length) return "'" + token + "' is not a range; write start..end, each a frame number or a profiler-mark label.";

            int from;
            int to = -1;
            string error = ResolveProfilerFrameToken(token.Substring(0, dots), out from) ?? ResolveProfilerFrameToken(token.Substring(dots + 2), out to);
            if (error != null) return error;
            if (to < from) return "Range " + token + " ends (" + to + ") before it starts (" + from + ").";
            if (to - from + 1 > PROFILER_FRAMES_MAX) return "Range " + token + " is " + (to - from + 1) + " frames; at most " + PROFILER_FRAMES_MAX + ".";
            int first = ProfilerFirstFrame();
            int last = ProfilerLastFrame();
            if (first < 0 || from < first || to > last) return "Range " + token + " (" + from + " to " + to + ") is not buffered; buffered frames are " + first + " to " + last + ".";
            range.From = from;
            range.To = to;
            return null;
        }

        #endregion

        #region Threads and Counters

        /// <summary>
        /// Lists the threads a buffered frame recorded. Without a filter it gives an overview: the main and render threads
        /// with their indices, and every group with its thread count and its names collapsed, numbered workers counted
        /// under one name. With a name or group fragment it lists the matching threads with their indices; "*" lists all.
        /// </summary>
        /// <param name="args">"[frame] [| filter]".</param>
        /// <returns>JSON of the overview or the threads, or an error.</returns>
        private static string ProfilerThreadsCommand(string args)
        {
            string[] sections = SplitCommandSections(args ?? string.Empty);
            int frame;
            string error = ResolveProfilerReadFrame(sections.Length > 0 ? sections[0] : string.Empty, out frame);
            if (error != null) return "{\"error\":" + JsonQuote(error) + "}";
            string filter = sections.Length > 1 ? sections[1].Trim() : string.Empty;
            bool overview = filter.Length == 0;
            bool all = filter == "*";

            var listed = new StringBuilder();
            var groups = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            var groupOrder = new List<string>();
            int count = 0;
            int matched = 0;
            for (int thread = 0; thread < PROFILER_THREAD_SEARCH_MAX; thread++)
            {
                string name;
                string group;
                using (RawFrameDataView view = ProfilerRawFrame(frame, thread))
                {
                    if (view == null || !view.valid) break;
                    name = view.threadName ?? string.Empty;
                    group = view.threadGroupName ?? string.Empty;
                }

                count++;
                bool list = overview
                    ? name == PROFILER_MAIN_THREAD_NAME || name == PROFILER_RENDER_THREAD_NAME
                    : all || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || group.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                if (list)
                {
                    if (matched < PROFILER_THREADS_LIST_MAX)
                    {
                        if (matched > 0) listed.Append(',');
                        listed.Append("{\"index\":").Append(thread).Append(",\"name\":").Append(JsonQuote(name)).Append(",\"group\":").Append(JsonQuote(group)).Append('}');
                    }

                    matched++;
                }

                if (!overview) continue;
                string groupKey = group.Length > 0 ? group : "(no group)";
                Dictionary<string, int> names;
                if (!groups.TryGetValue(groupKey, out names))
                {
                    names = new Dictionary<string, int>(StringComparer.Ordinal);
                    groups[groupKey] = names;
                    groupOrder.Add(groupKey);
                }

                IncrementCount(names, ProfilerThreadBaseName(name));
            }

            var sb = new StringBuilder();
            sb.Append("{\"frame\":").Append(frame).Append(",\"threadCount\":").Append(count).Append(",\"threads\":[").Append(listed).Append(']');
            if (matched > PROFILER_THREADS_LIST_MAX) sb.Append(",\"truncated\":").Append(matched - PROFILER_THREADS_LIST_MAX);
            if (overview)
            {
                sb.Append(",\"groups\":[");
                for (int i = 0; i < groupOrder.Count; i++)
                {
                    Dictionary<string, int> names = groups[groupOrder[i]];
                    int total = 0;
                    foreach (int n in names.Values) total += n;
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"group\":").Append(JsonQuote(groupOrder[i])).Append(",\"count\":").Append(total).Append(",\"names\":");
                    AppendCountGroups(sb, names, "name");
                    sb.Append('}');
                }

                sb.Append("],\"note\":\"Overview: the main and render threads with their indices, and every group with its names collapsed. Pass a name or group fragment after a pipe to list those threads with their indices; * lists every thread.\"");
            }
            else
            {
                sb.Append(",\"matched\":").Append(matched);
            }

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>A thread name with its trailing number taken off, so numbered workers count under one name.</summary>
        /// <param name="name">Thread name.</param>
        /// <returns>The name without its number, or "(numbered)" for a name that is only a number.</returns>
        private static string ProfilerThreadBaseName(string name)
        {
            string trimmed = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').TrimEnd(' ', '-', '#');
            return trimmed.Length > 0 ? trimmed : "(numbered)";
        }

        /// <summary>
        /// Lists the counters a buffered frame carries. Without a filter it gives an overview by category with counts; a
        /// name or category fragment lists the matching counters with their values, and "*" lists every counter.
        /// </summary>
        /// <param name="args">"[frame] [| filter]".</param>
        /// <returns>JSON of the counters, or an error.</returns>
        private static string ProfilerCountersCommand(string args)
        {
            string[] sections = SplitCommandSections(args ?? string.Empty);
            int frame;
            string error = ResolveProfilerReadFrame(sections.Length > 0 ? sections[0] : string.Empty, out frame);
            if (error != null) return "{\"error\":" + JsonQuote(error) + "}";
            string filter = sections.Length > 1 ? sections[1].Trim() : string.Empty;

            using (RawFrameDataView view = ProfilerRawFrame(frame, 0))
            {
                if (view == null || !view.valid) return "{\"error\":" + JsonQuote("Frame " + frame + " could not be read.") + "}";
                var markers = new List<FrameDataView.MarkerInfo>();
                view.GetMarkers(markers);
                bool overview = filter.Length == 0;
                bool all = filter == "*";
                var rows = new List<KeyValuePair<string, string>>();
                var categories = new Dictionary<string, int>(StringComparer.Ordinal);
                int total = 0;
                foreach (FrameDataView.MarkerInfo marker in markers)
                {
                    if ((marker.flags & Unity.Profiling.LowLevel.MarkerFlags.Counter) == 0) continue;
                    total++;
                    string category = view.GetCategoryInfo(marker.category).name;
                    if (overview)
                    {
                        IncrementCount(categories, category);
                        continue;
                    }

                    if (!all && marker.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && category.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string value = view.HasCounterValue(marker.id) ? JsonNumber(view.GetCounterValueAsDouble(marker.id)) : "null";
                    rows.Add(new KeyValuePair<string, string>(category + "\u0000" + marker.name,
                        "{\"name\":" + JsonQuote(marker.name) + ",\"category\":" + JsonQuote(category) + ",\"value\":" + value + "}"));
                }

                var sb = new StringBuilder();
                if (overview)
                {
                    sb.Append("{\"frame\":").Append(frame).Append(",\"countersInFrame\":").Append(total).Append(",\"categories\":");
                    AppendCountGroups(sb, categories, "category");
                    sb.Append(",\"note\":\"Overview by category. Pass a name or category fragment after a pipe to list those counters with their values; * lists every counter.\"}");
                    return sb.ToString();
                }

                rows.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                sb.Append("{\"frame\":").Append(frame).Append(",\"countersInFrame\":").Append(total)
                    .Append(",\"matched\":").Append(rows.Count).Append(",\"counters\":[");
                int limit = Math.Min(rows.Count, PROFILER_COUNTERS_LIST_MAX);
                for (int i = 0; i < limit; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(rows[i].Value);
                }

                sb.Append(']');
                if (rows.Count > limit) sb.Append(",\"truncated\":").Append(rows.Count - limit);
                sb.Append(",\"note\":\"Values are raw: times are nanoseconds and memory is bytes, as Unity records them. Pass a name to profiler-frames or profiler-compare in their counters section.\"}");
                return sb.ToString();
            }
        }

        /// <summary>Resolves an optional frame argument, defaulting to the newest complete frame.</summary>
        /// <param name="token">Frame number, label, or empty or "-" for the newest complete frame.</param>
        /// <param name="frame">The frame.</param>
        /// <returns>Null on success, or why no frame can be read.</returns>
        private static string ResolveProfilerReadFrame(string token, out int frame)
        {
            frame = -1;
            int first = ProfilerFirstFrame();
            int last = ProfilerLastFrame();
            if (first < 0 || last < 0) return "No profiler frames are buffered.";
            token = (token ?? string.Empty).Trim();
            if (token.Length == 0 || token == "-")
            {
                frame = last > first ? last - 1 : last;
                return null;
            }

            string error = ResolveProfilerFrameToken(token, out frame);
            if (error != null) return error;
            if (frame < first || frame > last) return "Frame " + frame + " is not buffered; buffered frames are " + first + " to " + last + ".";
            return null;
        }

        #endregion

        #region Frame Figures

        /// <summary>
        /// Builds the figures read from every frame: whole-frame CPU and GPU time, each named thread's shippable work
        /// (main and render thread by default), and each named counter (the rendering and garbage counters by default,
        /// none for "none").
        /// </summary>
        /// <param name="countersSection">Comma-separated counter names, "none", or empty for the defaults.</param>
        /// <param name="threadsSection">Comma-separated thread names, or empty for main and render thread.</param>
        /// <returns>The figures, in report order.</returns>
        private static List<ProfilerColumnSpec> BuildProfilerColumns(string countersSection, string threadsSection)
        {
            var columns = new List<ProfilerColumnSpec>
            {
                new ProfilerColumnSpec { Key = "cpuMs", Kind = PROFILER_KIND_FRAME_CPU, Name = string.Empty },
                new ProfilerColumnSpec { Key = "gpuMs", Kind = PROFILER_KIND_FRAME_GPU, Name = string.Empty },
            };

            foreach (string thread in ProfilerThreadNames(threadsSection))
            {
                string key = thread == PROFILER_MAIN_THREAD_NAME ? "mainWorkMs" : thread == PROFILER_RENDER_THREAD_NAME ? "renderWorkMs" : thread + " workMs";
                columns.Add(new ProfilerColumnSpec { Key = key, Kind = PROFILER_KIND_THREAD_WORK, Name = thread });
            }

            string counters = (countersSection ?? string.Empty).Trim();
            if (counters.Length == 0)
            {
                for (int i = 0; i < ProfilerFrameCounterNames.Length; i++)
                {
                    columns.Add(new ProfilerColumnSpec { Key = ProfilerFrameCounterKeys[i], Kind = PROFILER_KIND_COUNTER, Name = ProfilerFrameCounterNames[i] });
                }
            }
            else if (!string.Equals(counters, "none", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string name in counters.Split(','))
                {
                    string trimmed = name.Trim();
                    if (trimmed.Length > 0) columns.Add(new ProfilerColumnSpec { Key = trimmed, Kind = PROFILER_KIND_COUNTER, Name = trimmed });
                }
            }

            return columns;
        }

        /// <summary>The thread names a threads section asks for, main and render thread when it is empty.</summary>
        /// <param name="threadsSection">Comma-separated thread names, or empty.</param>
        /// <returns>The names.</returns>
        private static List<string> ProfilerThreadNames(string threadsSection)
        {
            var names = new List<string>();
            foreach (string name in (threadsSection ?? string.Empty).Split(','))
            {
                string trimmed = name.Trim();
                if (trimmed.Length > 0 && !names.Contains(trimmed)) names.Add(trimmed);
            }

            if (names.Count == 0)
            {
                names.Add(PROFILER_MAIN_THREAD_NAME);
                names.Add(PROFILER_RENDER_THREAD_NAME);
            }

            return names;
        }

        /// <summary>
        /// Reads one buffered frame's figures into <paramref name="values"/>, in <paramref name="columns"/> order. A figure
        /// the frame does not carry is NaN: GPU time on a frame without a GPU sample, a thread the frame did not record,
        /// a counter it does not carry.
        /// </summary>
        /// <param name="frame">Profiler frame number.</param>
        /// <param name="columns">Figures to read.</param>
        /// <param name="values">Receives the values; same length as <paramref name="columns"/>.</param>
        /// <returns>True when the frame could be read.</returns>
        private static bool ReadProfilerFrameValues(int frame, List<ProfilerColumnSpec> columns, double[] values)
        {
            using (RawFrameDataView view = ProfilerRawFrame(frame, 0))
            {
                if (view == null || !view.valid) return false;
                for (int k = 0; k < columns.Count; k++)
                {
                    ProfilerColumnSpec column = columns[k];
                    if (column.Kind == PROFILER_KIND_FRAME_CPU) values[k] = view.frameTimeMs;
                    else if (column.Kind == PROFILER_KIND_FRAME_GPU) values[k] = view.frameGpuTimeMs > 0f ? view.frameGpuTimeMs : double.NaN;
                    else if (column.Kind == PROFILER_KIND_COUNTER)
                    {
                        int id = view.GetMarkerId(column.Name);
                        values[k] = id >= 0 && view.HasCounterValue(id) ? view.GetCounterValueAsDouble(id) : double.NaN;
                    }
                }
            }

            for (int k = 0; k < columns.Count; k++)
            {
                ProfilerColumnSpec column = columns[k];
                if (column.Kind != PROFILER_KIND_THREAD_WORK) continue;
                int thread = ResolveProfilerThreadIndex(frame, column.Name, ref column.ThreadIndex);
                double work = thread >= 0 ? FrameRealCostMs(frame, thread) : -1.0;
                values[k] = work >= 0.0 ? work : double.NaN;
            }

            return true;
        }

        /// <summary>Finds a thread's index in a frame by name, trying the cached index first.</summary>
        /// <param name="frame">Profiler frame number.</param>
        /// <param name="threadName">Thread name as the profiler records it.</param>
        /// <param name="cachedIndex">Index found last time; updated when the thread is found elsewhere.</param>
        /// <returns>The index, or -1 when the frame has no thread of that name.</returns>
        private static int ResolveProfilerThreadIndex(int frame, string threadName, ref int cachedIndex)
        {
            if (cachedIndex >= 0 && ProfilerThreadNameAt(frame, cachedIndex) == threadName) return cachedIndex;
            for (int thread = 0; thread < PROFILER_THREAD_SEARCH_MAX; thread++)
            {
                string name = ProfilerThreadNameAt(frame, thread);
                if (name == null) break;
                if (name == threadName)
                {
                    cachedIndex = thread;
                    return thread;
                }
            }

            return -1;
        }

        /// <summary>The name of the thread at an index in a frame.</summary>
        /// <param name="frame">Profiler frame number.</param>
        /// <param name="thread">Thread index.</param>
        /// <returns>The name, or null when the index is past the last thread.</returns>
        private static string ProfilerThreadNameAt(int frame, int thread)
        {
            using (RawFrameDataView view = ProfilerRawFrame(frame, thread))
            {
                return view != null && view.valid ? view.threadName : null;
            }
        }

        /// <summary>Reads a range of frames into one value list per figure, optionally appending each frame's JSON row.</summary>
        /// <param name="range">Frames to read.</param>
        /// <param name="columns">Figures to read.</param>
        /// <param name="rows">Builder for per-frame rows, or null to skip them.</param>
        /// <param name="read">Frames that could be read.</param>
        /// <returns>Values by figure, NaN values left out.</returns>
        private static List<double>[] CollectProfilerFrameColumns(ProfilerFrameRange range, List<ProfilerColumnSpec> columns, StringBuilder rows, out int read)
        {
            var collected = new List<double>[columns.Count];
            for (int k = 0; k < columns.Count; k++) collected[k] = new List<double>(range.To - range.From + 1);
            var values = new double[columns.Count];
            read = 0;
            for (int frame = range.From; frame <= range.To; frame++)
            {
                if (!ReadProfilerFrameValues(frame, columns, values)) continue;
                if (rows != null)
                {
                    if (read > 0) rows.Append(',');
                    AppendProfilerFrameRow(rows, frame, values, columns);
                }

                for (int k = 0; k < columns.Count; k++)
                {
                    if (!double.IsNaN(values[k])) collected[k].Add(values[k]);
                }

                read++;
            }

            return collected;
        }

        /// <summary>Appends one frame's figures as a JSON object; a figure the frame does not carry is written as null.</summary>
        /// <param name="sb">Builder appended to.</param>
        /// <param name="frame">Profiler frame number.</param>
        /// <param name="values">Values from <see cref="ReadProfilerFrameValues"/>.</param>
        /// <param name="columns">The figures the values belong to.</param>
        private static void AppendProfilerFrameRow(StringBuilder sb, int frame, double[] values, List<ProfilerColumnSpec> columns)
        {
            sb.Append("{\"frame\":").Append(frame);
            for (int k = 0; k < columns.Count; k++)
            {
                sb.Append(',').Append(JsonQuote(columns[k].Key)).Append(':').Append(double.IsNaN(values[k]) ? "null" : JsonNumber(values[k]));
            }

            sb.Append('}');
        }

        /// <summary>Appends one statistic of every figure as JSON members, null for a figure no frame carried.</summary>
        /// <param name="sb">Builder appended to.</param>
        /// <param name="collected">Values by figure.</param>
        /// <param name="columns">The figures.</param>
        /// <param name="statistic">0 mean, 1 median, 2 sample standard deviation, 3 sample count.</param>
        private static void AppendProfilerFrameSummary(StringBuilder sb, List<double>[] collected, List<ProfilerColumnSpec> columns, int statistic)
        {
            for (int k = 0; k < columns.Count; k++)
            {
                if (k > 0) sb.Append(',');
                sb.Append(JsonQuote(columns[k].Key)).Append(':');
                if (statistic == 3)
                {
                    sb.Append(collected[k].Count);
                    continue;
                }

                if (collected[k].Count == 0 || (statistic == 2 && collected[k].Count < 2))
                {
                    sb.Append("null");
                    continue;
                }

                double[] sorted = collected[k].ToArray();
                Array.Sort(sorted);
                double value = statistic == 1 ? Percentile(sorted, 0.5) : statistic == 2 ? Math.Sqrt(SampleVariance(sorted)) : Mean(sorted);
                sb.Append(JsonNumber(value));
            }
        }

        /// <summary>
        /// Reads per-frame figures from the profiler's buffered frames, with the mean, median, standard deviation and
        /// sample count of each, and one row per frame unless rows is "none".
        /// </summary>
        /// <param name="args">"[count] [from] [rows] [| counters] [| threads]"; from may be a label.</param>
        /// <returns>JSON of the summary and rows, or an error.</returns>
        private static string ProfilerFramesCommand(string args)
        {
            string[] sections = SplitCommandSections(args ?? string.Empty);
            string[] tokens = SplitCommandTokens(sections.Length > 0 ? sections[0] : string.Empty);
            int count = tokens.Length >= 1 ? ParsePositiveInt(tokens[0], 30, PROFILER_FRAMES_MAX) : 30;
            bool includeRows = true;
            if (tokens.Length >= 3)
            {
                string rowsMode = tokens[2].ToLowerInvariant();
                if (rowsMode == "none") includeRows = false;
                else if (rowsMode != "all") return "{\"error\":" + JsonQuote("rows must be all or none; '" + tokens[2] + "' is neither.") + "}";
            }

            int first = ProfilerFirstFrame();
            int last = ProfilerLastFrame();
            if (first < 0 || last < 0 || last < first)
            {
                return "{\"error\":\"No profiler frames are buffered. Record some with profiler-record-start or profiler-start in play mode first.\"}";
            }

            // The newest frame can still be collecting, so the default range ends one before it.
            int newestComplete = last > first ? last - 1 : last;
            int from;
            if (tokens.Length >= 2 && tokens[1] != "-")
            {
                string fromError = ResolveProfilerFrameToken(tokens[1], out from);
                if (fromError != null) return "{\"error\":" + JsonQuote(fromError) + "}";
                if (from < first || from > last) return "{\"error\":" + JsonQuote("Frame " + from + " is not buffered; buffered frames are " + first + " to " + last + ".") + "}";
            }
            else
            {
                from = Math.Max(first, newestComplete - count + 1);
            }

            var range = new ProfilerFrameRange { From = from, To = Math.Min(last, from + count - 1) };
            List<ProfilerColumnSpec> columns = BuildProfilerColumns(sections.Length > 1 ? sections[1] : string.Empty, sections.Length > 2 ? sections[2] : string.Empty);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var rows = includeRows ? new StringBuilder() : null;
            int read;
            List<double>[] collected = CollectProfilerFrameColumns(range, columns, rows, out read);
            if (read == 0) return "{\"error\":" + JsonQuote("None of frames " + range.From + " to " + range.To + " could be read.") + "}";

            var sb = new StringBuilder();
            sb.Append("{\"firstBuffered\":").Append(first)
                .Append(",\"lastBuffered\":").Append(last)
                .Append(",\"from\":").Append(range.From)
                .Append(",\"to\":").Append(range.To)
                .Append(",\"framesRead\":").Append(read)
                .Append(",\"readMs\":").Append(stopwatch.ElapsedMilliseconds)
                .Append(",\"mean\":{");
            AppendProfilerFrameSummary(sb, collected, columns, 0);
            sb.Append("},\"median\":{");
            AppendProfilerFrameSummary(sb, collected, columns, 1);
            sb.Append("},\"sd\":{");
            AppendProfilerFrameSummary(sb, collected, columns, 2);
            sb.Append("},\"n\":{");
            AppendProfilerFrameSummary(sb, collected, columns, 3);
            sb.Append('}');
            AppendMissingFigures(sb, collected, columns);
            if (rows != null) sb.Append(",\"frames\":[").Append(rows).Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>Appends a warning naming every figure no frame carried, so a misspelt counter or thread is never silent.</summary>
        /// <param name="sb">Builder appended to.</param>
        /// <param name="collected">Values by figure.</param>
        /// <param name="columns">The figures.</param>
        private static void AppendMissingFigures(StringBuilder sb, List<double>[] collected, List<ProfilerColumnSpec> columns)
        {
            var missing = new List<string>();
            for (int k = 0; k < columns.Count; k++)
            {
                if (collected[k].Count == 0 && columns[k].Kind != PROFILER_KIND_FRAME_GPU) missing.Add(columns[k].Key);
            }

            if (missing.Count == 0) return;
            sb.Append(",\"warning\":").Append(JsonQuote("No frame carried " + string.Join(", ", missing.ToArray())
                + ". Check counter names with profiler-counters and thread names with profiler-threads."));
        }

        #endregion

        #region Markers

        /// <summary>Walks one frame's hierarchy on one thread into per-marker aggregates, or null when unreadable.</summary>
        /// <param name="frame">Profiler frame number.</param>
        /// <param name="thread">Thread index.</param>
        /// <param name="editorByName">True to keep the editor's own markers by name under <see cref="PROFILER_EDITOR_SCOPE_PREFIX"/>.</param>
        /// <returns>Aggregates by marker name, editor work filed under the editor loop unless kept by name, or null.</returns>
        private static Dictionary<string, MarkerAggregate> ReadFrameAggregates(int frame, int thread, bool editorByName = false)
        {
            HierarchyFrameDataView view = null;
            try
            {
                view = ProfilerHierarchyFrame(frame, thread);
                if (view == null || !view.valid) return null;
                return WalkFrameAggregates(view, editorByName);
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (view != null) view.Dispose();
            }
        }

        /// <summary>
        /// Totals every marker over a frame range on one thread, as mean per frame read: self and total CPU time, self
        /// and total GPU time, garbage, calls and draw calls, with the worst single frame's self time. Editor, profiler
        /// and wait markers are left out and their combined time reported separately. Scope editor lists the editor's
        /// own markers by name instead, which the profiler records only while profileEditor is on.
        /// </summary>
        /// <param name="args">"&lt;range&gt; [topN] [sort] [| thread] [| filter] [| scope]".</param>
        /// <returns>JSON of the markers, or an error.</returns>
        private static string ProfilerMarkersCommand(string args)
        {
            string[] sections = SplitCommandSections(args ?? string.Empty);
            string[] tokens = SplitCommandTokens(sections.Length > 0 ? sections[0] : string.Empty);
            if (tokens.Length < 1) return "{\"error\":\"Usage: profiler-markers <range> [topN] [sort] [| thread] [| filter].\"}";
            ProfilerFrameRange range;
            string error = ParseProfilerRange(tokens[0], out range);
            if (error != null) return "{\"error\":" + JsonQuote(error) + "}";
            int topN = tokens.Length >= 2 ? ParsePositiveInt(tokens[1], 30, 1000) : 30;
            string sort = tokens.Length >= 3 ? tokens[2].ToLowerInvariant() : "self";
            if (Array.IndexOf(new[] { "self", "total", "gpu", "gc", "calls", "draws", "max" }, sort) < 0)
            {
                return "{\"error\":" + JsonQuote("sort must be self, total, gpu, gc, calls, draws or max; '" + sort + "' is none of them.") + "}";
            }

            string threadName = sections.Length > 1 && sections[1].Trim().Length > 0 ? sections[1].Trim() : PROFILER_MAIN_THREAD_NAME;
            string filter = sections.Length > 2 ? sections[2].Trim() : string.Empty;
            string scope = sections.Length > 3 && sections[3].Trim().Length > 0 ? sections[3].Trim().ToLowerInvariant() : "game";
            if (scope != "game" && scope != "editor") return "{\"error\":" + JsonQuote("scope must be game or editor; '" + scope + "' is neither.") + "}";
            bool editorScope = scope == "editor";
            var totals = new Dictionary<string, double[]>(StringComparer.Ordinal);    // self, total, gpuSelf, gpuTotal, gc, calls, draws, maxSelf, framesPresent, totalGc
            int read = 0;
            int cached = -1;
            double noise = 0.0;
            double conduitMs = 0.0;
            double conduitGc = 0.0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            for (int frame = range.From; frame <= range.To; frame++)
            {
                int thread = ResolveProfilerThreadIndex(frame, threadName, ref cached);
                if (thread < 0) continue;
                Dictionary<string, MarkerAggregate> aggregates = ReadFrameAggregates(frame, thread, editorScope);
                if (aggregates == null) continue;
                read++;
                foreach (var pair in aggregates)
                {
                    if (ConduitProfiling.IsConduitMarker(pair.Key))
                    {
                        conduitMs += pair.Value.SelfMs;
                        conduitGc += pair.Value.GcBytes;
                        continue;
                    }

                    bool editorEntry = pair.Key.StartsWith(PROFILER_EDITOR_SCOPE_PREFIX, StringComparison.Ordinal);
                    if (editorEntry != editorScope || (!editorEntry && IsProfilerNoiseMarker(pair.Key)))
                    {
                        noise += pair.Value.SelfMs;
                        continue;
                    }

                    string name = editorEntry ? pair.Key.Substring(PROFILER_EDITOR_SCOPE_PREFIX.Length) : pair.Key;
                    if (filter.Length > 0 && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    double[] total;
                    if (!totals.TryGetValue(name, out total)) totals[name] = total = new double[10];
                    total[0] += pair.Value.SelfMs;
                    total[1] += pair.Value.TotalMs;
                    total[2] += pair.Value.SelfGpuMs;
                    total[3] += pair.Value.TotalGpuMs;
                    total[4] += pair.Value.GcBytes;
                    total[5] += pair.Value.Calls;
                    total[6] += pair.Value.DrawCalls;
                    total[7] = Math.Max(total[7], pair.Value.SelfMs);
                    total[8] += 1.0;
                    total[9] += pair.Value.TotalGcBytes;
                }
            }

            if (read == 0) return "{\"error\":" + JsonQuote("No frame from " + range.From + " to " + range.To + " recorded a thread named '" + threadName + "'; profiler-threads lists the names.") + "}";

            int sortIndex = sort == "self" ? 0 : sort == "total" ? 1 : sort == "gpu" ? 2 : sort == "gc" ? 4 : sort == "calls" ? 5 : sort == "draws" ? 6 : 7;
            var ordered = new List<KeyValuePair<string, double[]>>(totals);
            ordered.Sort((a, b) => b.Value[sortIndex].CompareTo(a.Value[sortIndex]));
            var sb = new StringBuilder();
            sb.Append("{\"from\":").Append(range.From).Append(",\"to\":").Append(range.To)
                .Append(",\"thread\":").Append(JsonQuote(threadName))
                .Append(",\"framesRead\":").Append(read)
                .Append(",\"markersFound\":").Append(totals.Count)
                .Append(",\"sort\":").Append(JsonQuote(sort))
                .Append(",\"scope\":").Append(JsonQuote(scope))
                .Append(",\"noiseSelfMsPerFrame\":").Append(JsonNumber(noise / read))
                .Append(",\"conduitSelfMsPerFrame\":").Append(JsonNumber(conduitMs / read))
                .Append(",\"conduitGcBytesPerFrame\":").Append(JsonNumber(conduitGc / read))
                .Append(",\"readMs\":").Append(stopwatch.ElapsedMilliseconds)
                .Append(",\"markers\":[");
            int limit = Math.Min(topN, ordered.Count);
            for (int i = 0; i < limit; i++)
            {
                double[] total = ordered[i].Value;
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(JsonQuote(ordered[i].Key))
                    .Append(",\"selfMs\":").Append(JsonNumber(total[0] / read))
                    .Append(",\"totalMs\":").Append(JsonNumber(total[1] / read))
                    .Append(",\"selfGpuMs\":").Append(JsonNumber(total[2] / read))
                    .Append(",\"totalGpuMs\":").Append(JsonNumber(total[3] / read))
                    .Append(",\"gcBytes\":").Append(JsonNumber(total[4] / read))
                    .Append(",\"totalGcBytes\":").Append(JsonNumber(total[9] / read))
                    .Append(",\"calls\":").Append(JsonNumber(total[5] / read))
                    .Append(",\"drawCalls\":").Append(JsonNumber(total[6] / read))
                    .Append(",\"maxSelfMs\":").Append(JsonNumber(total[7]))
                    .Append(",\"framesPresent\":").Append((int)total[8]).Append('}');
            }

            sb.Append("],\"note\":\"Every value but maxSelfMs and framesPresent is a mean per frame read. GPU columns are zero unless the GPU Usage module recorded the frames. gcBytes is garbage the marker allocated itself; totalGcBytes includes its children. totalMs and totalGcBytes add up a marker everywhere it appears, so a marker nested in itself counts twice.\"}");
            return sb.ToString();
        }

        /// <summary>
        /// One marker's cost over a range on one thread: self time and garbage summarised (mean, median, spread, worst
        /// frame), and by default only the <see cref="PROFILER_TIMELINE_WORST"/> worst frames by self time and by garbage,
        /// with the gaps between the heaviest garbage frames to show a period. "all" lists every frame; "none" lists none.
        /// </summary>
        /// <param name="args">"&lt;range&gt; | &lt;marker&gt; [| thread] [| rows]".</param>
        /// <returns>JSON of the summary and the chosen frames, or an error.</returns>
        private static string ProfilerMarkerTimelineCommand(string args)
        {
            string[] sections = SplitCommandSections(args ?? string.Empty);
            if (sections.Length < 2 || sections[1].Trim().Length == 0) return "{\"error\":\"Usage: profiler-marker-timeline <range> | <marker> [| thread] [| rows].\"}";
            ProfilerFrameRange range;
            string error = ParseProfilerRange(sections[0], out range);
            if (error != null) return "{\"error\":" + JsonQuote(error) + "}";
            string marker = sections[1].Trim();
            string threadName = sections.Length > 2 && sections[2].Trim().Length > 0 ? sections[2].Trim() : PROFILER_MAIN_THREAD_NAME;
            string rowsMode = sections.Length > 3 && sections[3].Trim().Length > 0 ? sections[3].Trim().ToLowerInvariant() : "worst";
            if (rowsMode != "worst" && rowsMode != "all" && rowsMode != "none") return "{\"error\":" + JsonQuote("rows must be worst, all or none; '" + rowsMode + "' is none of them.") + "}";

            var frameRows = new List<double[]>();
            var selfValues = new List<double>();
            var nearNames = new HashSet<string>(StringComparer.Ordinal);
            int cached = -1;
            int present = 0;
            int worstFrame = -1;
            double worst = -1.0;
            for (int frame = range.From; frame <= range.To; frame++)
            {
                int thread = ResolveProfilerThreadIndex(frame, threadName, ref cached);
                if (thread < 0) continue;
                Dictionary<string, MarkerAggregate> aggregates = ReadFrameAggregates(frame, thread);
                if (aggregates == null) continue;
                MarkerAggregate value;
                bool found = aggregates.TryGetValue(marker, out value);
                if (!found && present == 0 && nearNames.Count < 10)
                {
                    foreach (string name in aggregates.Keys)
                    {
                        if (nearNames.Count < 10 && name.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) nearNames.Add(name);
                    }
                }

                if (found) present++;
                selfValues.Add(value.SelfMs);
                if (value.SelfMs > worst)
                {
                    worst = value.SelfMs;
                    worstFrame = frame;
                }

                frameRows.Add(new[] { frame, value.SelfMs, value.TotalMs, value.SelfGpuMs, value.Calls, value.GcBytes, value.TotalGcBytes });
            }

            if (selfValues.Count == 0) return "{\"error\":" + JsonQuote("No frame from " + range.From + " to " + range.To + " recorded a thread named '" + threadName + "'.") + "}";
            if (present == 0)
            {
                var near = new List<string>(nearNames);
                return "{\"error\":" + JsonQuote("No frame in the range recorded a marker named exactly '" + marker + "' on " + threadName + ".")
                    + ",\"similarNames\":[" + string.Join(",", near.ConvertAll(JsonQuote).ToArray()) + "]}";
            }

            double[] sorted = selfValues.ToArray();
            Array.Sort(sorted);
            var sb = new StringBuilder();
            sb.Append("{\"marker\":").Append(JsonQuote(marker))
                .Append(",\"thread\":").Append(JsonQuote(threadName))
                .Append(",\"from\":").Append(range.From).Append(",\"to\":").Append(range.To)
                .Append(",\"framesRead\":").Append(selfValues.Count)
                .Append(",\"framesPresent\":").Append(present)
                .Append(",\"selfMs\":{\"mean\":").Append(JsonNumber(Mean(sorted)))
                .Append(",\"median\":").Append(JsonNumber(Percentile(sorted, 0.5)))
                .Append(",\"sd\":").Append(sorted.Length > 1 ? JsonNumber(Math.Sqrt(SampleVariance(sorted))) : "null")
                .Append(",\"max\":").Append(JsonNumber(worst))
                .Append(",\"maxFrame\":").Append(worstFrame).Append('}');

            var gcValues = new double[frameRows.Count];
            var totalGcValues = new double[frameRows.Count];
            int gcFrames = 0;
            for (int i = 0; i < frameRows.Count; i++)
            {
                gcValues[i] = frameRows[i][5];
                totalGcValues[i] = frameRows[i][6];
                if (gcValues[i] > 0.0) gcFrames++;
            }

            Array.Sort(gcValues);
            Array.Sort(totalGcValues);
            sb.Append(",\"gcBytes\":{\"mean\":").Append(JsonNumber(Mean(gcValues)))
                .Append(",\"median\":").Append(JsonNumber(Percentile(gcValues, 0.5)))
                .Append(",\"max\":").Append(JsonNumber(gcValues[gcValues.Length - 1]))
                .Append(",\"framesWithGarbage\":").Append(gcFrames).Append('}')
                .Append(",\"totalGcBytes\":{\"mean\":").Append(JsonNumber(Mean(totalGcValues)))
                .Append(",\"median\":").Append(JsonNumber(Percentile(totalGcValues, 0.5)))
                .Append(",\"max\":").Append(JsonNumber(totalGcValues[totalGcValues.Length - 1])).Append('}');

            if (rowsMode == "all")
            {
                sb.Append(",\"frames\":");
                AppendTimelineRows(sb, frameRows, frameRows.Count);
            }
            else if (rowsMode == "worst")
            {
                var bySelf = new List<double[]>(frameRows);
                bySelf.Sort((a, b) => b[1].CompareTo(a[1]));
                sb.Append(",\"worstBySelfMs\":");
                AppendTimelineRows(sb, bySelf, PROFILER_TIMELINE_WORST);

                var byGc = new List<double[]>(frameRows);
                byGc.Sort((a, b) => b[5].CompareTo(a[5]));
                sb.Append(",\"worstByGcBytes\":");
                AppendTimelineRows(sb, byGc, PROFILER_TIMELINE_WORST);

                // Gaps between the heaviest garbage frames, in frame order, show whether the bursts repeat on a period.
                var heavy = new List<int>();
                for (int i = 0; i < Math.Min(PROFILER_TIMELINE_WORST, byGc.Count); i++)
                {
                    if (byGc[i][5] > 0.0) heavy.Add((int)byGc[i][0]);
                }

                heavy.Sort();
                sb.Append(",\"gcBurstGaps\":[");
                for (int i = 1; i < heavy.Count; i++)
                {
                    if (i > 1) sb.Append(',');
                    sb.Append(heavy[i] - heavy[i - 1]);
                }

                sb.Append(']');
            }

            sb.Append(",\"note\":\"A frame without the marker counts as zero. Frames listed are the worst by self time and by garbage; rows all lists every frame, rows none lists none. gcBurstGaps are the frame gaps between the heaviest garbage frames, in order. gcBytes is garbage the marker allocated itself (for EditorLoop, everything the editor allocated); totalGcBytes includes its children.\"}");
            return sb.ToString();
        }

        /// <summary>Appends timeline rows as a JSON array, up to a limit.</summary>
        /// <param name="sb">Builder appended to.</param>
        /// <param name="rows">Rows: frame, self ms, total ms, self GPU ms, calls, own garbage bytes, garbage bytes with children.</param>
        /// <param name="limit">Most rows to write.</param>
        private static void AppendTimelineRows(StringBuilder sb, List<double[]> rows, int limit)
        {
            sb.Append('[');
            int count = Math.Min(limit, rows.Count);
            for (int i = 0; i < count; i++)
            {
                double[] row = rows[i];
                if (i > 0) sb.Append(',');
                sb.Append("{\"frame\":").Append((int)row[0])
                    .Append(",\"selfMs\":").Append(JsonNumber(row[1]))
                    .Append(",\"totalMs\":").Append(JsonNumber(row[2]))
                    .Append(",\"selfGpuMs\":").Append(JsonNumber(row[3]))
                    .Append(",\"calls\":").Append(JsonNumber(row[4]))
                    .Append(",\"gcBytes\":").Append(JsonNumber(row[5]))
                    .Append(",\"totalGcBytes\":").Append(JsonNumber(row[6])).Append('}');
            }

            sb.Append(']');
        }

        #endregion

        #region Comparison

        /// <summary>
        /// Compares two ranges of frames, A and B, and says whether each difference is real: on the figures
        /// <c>profiler-frames</c> reads, on markers, or both.
        /// </summary>
        /// <param name="args">"&lt;rangesA&gt; &lt;rangesB&gt; [blockFrames] [| what] [| counters] [| threads]"; each side is one range or several joined by plus signs.</param>
        /// <returns>JSON of the comparison, or an error.</returns>
        private static string ProfilerCompareCommand(string args)
        {
            string[] sections = SplitCommandSections(args ?? string.Empty);
            string[] tokens = SplitCommandTokens(sections.Length > 0 ? sections[0] : string.Empty);
            if (tokens.Length < 2) return "{\"error\":\"Usage: profiler-compare <rangesA> <rangesB> [blockFrames] [| what] [| counters] [| threads]; a range is start..end, each a frame number or a profiler-mark label, and a side may join several ranges with plus signs.\"}";
            List<ProfilerFrameRange> rangesA;
            List<ProfilerFrameRange> rangesB = null;
            string error = ParseProfilerRangeList(tokens[0], "A", out rangesA) ?? ParseProfilerRangeList(tokens[1], "B", out rangesB);
            if (error != null) return "{\"error\":" + JsonQuote(error) + "}";
            error = ProfilerRangesOverlap(rangesA, rangesB, "A", "B");
            if (error != null) return "{\"error\":" + JsonQuote(error) + "}";
            int blockFrames = PROFILER_COMPARE_BLOCK_FRAMES;
            if (tokens.Length >= 3 && (!int.TryParse(tokens[2], out blockFrames) || blockFrames < 2))
            {
                return "{\"error\":" + JsonQuote("blockFrames must be a whole number of 2 or more; '" + tokens[2] + "' is not.") + "}";
            }
            string what = sections.Length > 1 && sections[1].Trim().Length > 0 ? sections[1].Trim().ToLowerInvariant() : "figures";
            if (what != "figures" && what != "markers" && what != "all") return "{\"error\":" + JsonQuote("what must be figures, markers or all; '" + what + "' is none of them.") + "}";

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var sb = new StringBuilder();
            sb.Append('{');
            AppendProfilerComparison(sb, rangesA, rangesB, blockFrames, what != "markers", what != "figures",
                sections.Length > 2 ? sections[2] : string.Empty, sections.Length > 3 ? sections[3] : string.Empty);
            sb.Append(",\"readMs\":").Append(stopwatch.ElapsedMilliseconds).Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Parses one side of a comparison: a range, or several joined by plus signs (commas split command tokens), which interleave states so slow drift in
        /// the scene falls on both sides. The side's ranges must not overlap each other, and together they hold at most
        /// <see cref="PROFILER_FRAMES_MAX"/> frames.
        /// </summary>
        /// <param name="token">The side's text.</param>
        /// <param name="side">Name of the side, for messages.</param>
        /// <param name="ranges">The parsed ranges.</param>
        /// <returns>Null on success, or why the side is unusable.</returns>
        private static string ParseProfilerRangeList(string token, string side, out List<ProfilerFrameRange> ranges)
        {
            ranges = new List<ProfilerFrameRange>();
            int frames = 0;
            foreach (string part in (token ?? string.Empty).Split('+'))
            {
                ProfilerFrameRange range;
                string error = ParseProfilerRange(part, out range);
                if (error != null) return side + ": " + error;
                ranges.Add(range);
                frames += range.To - range.From + 1;
            }

            if (frames > PROFILER_FRAMES_MAX) return side + " holds " + frames + " frames; at most " + PROFILER_FRAMES_MAX + " per side.";
            for (int i = 0; i < ranges.Count; i++)
            {
                for (int j = i + 1; j < ranges.Count; j++)
                {
                    if (ranges[i].From <= ranges[j].To && ranges[j].From <= ranges[i].To) return side + "'s ranges " + ranges[i].From + ".." + ranges[i].To + " and " + ranges[j].From + ".." + ranges[j].To + " overlap.";
                }
            }

            return null;
        }

        /// <summary>Refuses two sides that share a frame, since a frame cannot count for both.</summary>
        /// <param name="a">Ranges of side A.</param>
        /// <param name="b">Ranges of side B.</param>
        /// <param name="nameA">Name of side A, for the message.</param>
        /// <param name="nameB">Name of side B, for the message.</param>
        /// <returns>Null when they are apart, or which ranges overlap.</returns>
        private static string ProfilerRangesOverlap(List<ProfilerFrameRange> a, List<ProfilerFrameRange> b, string nameA, string nameB)
        {
            foreach (ProfilerFrameRange ra in a)
            {
                foreach (ProfilerFrameRange rb in b)
                {
                    if (ra.From <= rb.To && rb.From <= ra.To) return "The ranges overlap (" + nameA + " " + ra.From + ".." + ra.To + ", " + nameB + " " + rb.From + ".." + rb.To + "); a frame cannot count for both.";
                }
            }

            return null;
        }

        /// <summary>
        /// Appends the members of a comparison of two sets of frame ranges. Neighbouring frames are not independent, so each
        /// range is cut into blocks of whole <paramref name="blockFrames"/> frames (blocks never span two ranges) and the
        /// 95% interval of B minus A comes from Welch's t over the block means. Markers are compared on self CPU time.
        /// </summary>
        /// <param name="sb">Builder appended to, inside an open object.</param>
        /// <param name="rangesA">Ranges of state A.</param>
        /// <param name="rangesB">Ranges of state B.</param>
        /// <param name="blockFrames">Frames per block.</param>
        /// <param name="figures">Compare the frame figures.</param>
        /// <param name="markers">Compare markers.</param>
        /// <param name="countersSection">Counters section for the figures.</param>
        /// <param name="threadsSection">Threads section, for the figures' thread work and the markers compared.</param>
        private static void AppendProfilerComparison(StringBuilder sb, List<ProfilerFrameRange> rangesA, List<ProfilerFrameRange> rangesB, int blockFrames,
            bool figures, bool markers, string countersSection, string threadsSection)
        {
            sb.Append("\"a\":");
            AppendProfilerRanges(sb, rangesA);
            sb.Append(",\"b\":");
            AppendProfilerRanges(sb, rangesB);
            sb.Append(",\"blockFrames\":").Append(blockFrames);

            if (figures)
            {
                List<ProfilerColumnSpec> columns = BuildProfilerColumns(countersSection, threadsSection);
                var blocksA = ProfilerFigureBlocks(rangesA, columns, blockFrames);
                var blocksB = ProfilerFigureBlocks(rangesB, columns, blockFrames);
                sb.Append(",\"figures\":[");
                for (int k = 0; k < columns.Count; k++)
                {
                    if (k > 0) sb.Append(',');
                    AppendProfilerDifference(sb, "key", columns[k].Key, blocksA[k].ToArray(), blocksB[k].ToArray());
                    sb.Append('}');
                }

                sb.Append(']');
            }

            if (markers)
            {
                sb.Append(",\"markers\":[");
                int threadCount = 0;
                foreach (string threadName in ProfilerThreadNames(threadsSection))
                {
                    if (threadCount++ > 0) sb.Append(',');
                    AppendProfilerMarkerComparison(sb, threadName, rangesA, rangesB, blockFrames);
                }

                sb.Append(']');
            }

            sb.Append(",\"note\":\"diff is B minus A. distinguishable is true only when the 95% interval excludes zero; otherwise the difference is not proven, whatever the means say. Check the method first with an A/A comparison of two ranges of one state.\"");
        }

        /// <summary>Appends a set of ranges as a JSON array of from and to.</summary>
        /// <param name="sb">Builder appended to.</param>
        /// <param name="ranges">The ranges.</param>
        private static void AppendProfilerRanges(StringBuilder sb, List<ProfilerFrameRange> ranges)
        {
            sb.Append('[');
            for (int i = 0; i < ranges.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"from\":").Append(ranges[i].From).Append(",\"to\":").Append(ranges[i].To).Append('}');
            }

            sb.Append(']');
        }

        /// <summary>Block means of every figure over a set of ranges, blocks never spanning two ranges.</summary>
        /// <param name="ranges">The ranges.</param>
        /// <param name="columns">The figures.</param>
        /// <param name="blockFrames">Frames per block.</param>
        /// <returns>Block means by figure.</returns>
        private static List<double>[] ProfilerFigureBlocks(List<ProfilerFrameRange> ranges, List<ProfilerColumnSpec> columns, int blockFrames)
        {
            var blocks = new List<double>[columns.Count];
            for (int k = 0; k < columns.Count; k++) blocks[k] = new List<double>();
            foreach (ProfilerFrameRange range in ranges)
            {
                int read;
                List<double>[] collected = CollectProfilerFrameColumns(range, columns, null, out read);
                for (int k = 0; k < columns.Count; k++) blocks[k].AddRange(ProfilerBlockMeans(collected[k], blockFrames));
            }

            return blocks;
        }

        /// <summary>
        /// Appends the top markers of one thread whose self time differs between A and B, largest difference first, with
        /// how many markers were compared and how many differ provably.
        /// </summary>
        /// <param name="sb">Builder appended to.</param>
        /// <param name="threadName">Thread to compare.</param>
        /// <param name="rangesA">Ranges of state A.</param>
        /// <param name="rangesB">Ranges of state B.</param>
        /// <param name="blockFrames">Frames per block.</param>
        private static void AppendProfilerMarkerComparison(StringBuilder sb, string threadName, List<ProfilerFrameRange> rangesA, List<ProfilerFrameRange> rangesB, int blockFrames)
        {
            int cached = -1;
            List<Dictionary<string, double>> blocksA = ProfilerMarkerBlocks(threadName, rangesA, blockFrames, ref cached);
            List<Dictionary<string, double>> blocksB = ProfilerMarkerBlocks(threadName, rangesB, blockFrames, ref cached);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in blocksA) names.UnionWith(block.Keys);
            foreach (var block in blocksB) names.UnionWith(block.Keys);

            var results = new List<KeyValuePair<double, string>>();
            int distinguishable = 0;
            foreach (string name in names)
            {
                double[] valuesA = ProfilerMarkerSeries(blocksA, name);
                double[] valuesB = ProfilerMarkerSeries(blocksB, name);
                double difference, low, high;
                if (!ProfilerWelchInterval(valuesA, valuesB, out difference, out low, out high)) continue;
                bool proven = low > 0.0 || high < 0.0;
                if (proven) distinguishable++;
                var entry = new StringBuilder();
                entry.Append("{\"name\":").Append(JsonQuote(name))
                    .Append(",\"meanA\":").Append(JsonNumber(Mean(valuesA)))
                    .Append(",\"meanB\":").Append(JsonNumber(Mean(valuesB)))
                    .Append(",\"diff\":").Append(JsonNumber(difference))
                    .Append(",\"ciLow\":").Append(JsonNumber(low))
                    .Append(",\"ciHigh\":").Append(JsonNumber(high))
                    .Append(",\"distinguishable\":").Append(proven ? "true" : "false").Append('}');
                results.Add(new KeyValuePair<double, string>(Math.Abs(difference), entry.ToString()));
            }

            results.Sort((x, y) => y.Key.CompareTo(x.Key));
            sb.Append("{\"thread\":").Append(JsonQuote(threadName))
                .Append(",\"blocksA\":").Append(blocksA.Count)
                .Append(",\"blocksB\":").Append(blocksB.Count)
                .Append(",\"markersCompared\":").Append(results.Count)
                .Append(",\"distinguishable\":").Append(distinguishable)
                .Append(",\"top\":[");
            int limit = Math.Min(PROFILER_COMPARE_MARKERS_MAX, results.Count);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(results[i].Value);
            }

            sb.Append("]}");
        }

        /// <summary>Per-block mean self time of every shippable marker on one thread over a set of ranges.</summary>
        /// <param name="threadName">Thread to read.</param>
        /// <param name="ranges">The ranges.</param>
        /// <param name="blockFrames">Frames per block; a partial block at a range's end is dropped.</param>
        /// <param name="cachedIndex">Thread index cache.</param>
        /// <returns>One dictionary of marker to mean self ms per whole block.</returns>
        private static List<Dictionary<string, double>> ProfilerMarkerBlocks(string threadName, List<ProfilerFrameRange> ranges, int blockFrames, ref int cachedIndex)
        {
            var blocks = new List<Dictionary<string, double>>();
            foreach (ProfilerFrameRange range in ranges)
            {
                var current = new Dictionary<string, double>(StringComparer.Ordinal);
                int framesInBlock = 0;
                for (int frame = range.From; frame <= range.To; frame++)
                {
                    int thread = ResolveProfilerThreadIndex(frame, threadName, ref cachedIndex);
                    Dictionary<string, MarkerAggregate> aggregates = thread >= 0 ? ReadFrameAggregates(frame, thread) : null;
                    if (aggregates == null) continue;
                    foreach (var pair in aggregates)
                    {
                        if (IsProfilerNoiseMarker(pair.Key)) continue;
                        double sum;
                        current.TryGetValue(pair.Key, out sum);
                        current[pair.Key] = sum + pair.Value.SelfMs;
                    }

                    if (++framesInBlock < blockFrames) continue;
                    var means = new Dictionary<string, double>(current.Count, StringComparer.Ordinal);
                    foreach (var pair in current) means[pair.Key] = pair.Value / blockFrames;
                    blocks.Add(means);
                    current.Clear();
                    framesInBlock = 0;
                }
            }

            return blocks;
        }

        /// <summary>One marker's value in every block, zero where the block never recorded it.</summary>
        /// <param name="blocks">Block dictionaries.</param>
        /// <param name="name">Marker name.</param>
        /// <returns>The series.</returns>
        private static double[] ProfilerMarkerSeries(List<Dictionary<string, double>> blocks, string name)
        {
            var series = new double[blocks.Count];
            for (int i = 0; i < blocks.Count; i++)
            {
                double value;
                series[i] = blocks[i].TryGetValue(name, out value) ? value : 0.0;
            }

            return series;
        }

        /// <summary>
        /// Appends one figure's comparison members (key, block counts, means, difference, interval, verdict) into an object
        /// it opens; the caller closes it.
        /// </summary>
        /// <param name="sb">Builder appended to.</param>
        /// <param name="nameKey">JSON key the figure's name goes under.</param>
        /// <param name="name">The figure's name.</param>
        /// <param name="blocksA">Block means of A.</param>
        /// <param name="blocksB">Block means of B.</param>
        private static void AppendProfilerDifference(StringBuilder sb, string nameKey, string name, double[] blocksA, double[] blocksB)
        {
            sb.Append('{').Append(JsonQuote(nameKey)).Append(':').Append(JsonQuote(name))
                .Append(",\"blocksA\":").Append(blocksA.Length).Append(",\"blocksB\":").Append(blocksB.Length);
            double difference, low, high;
            if (!ProfilerWelchInterval(blocksA, blocksB, out difference, out low, out high))
            {
                sb.Append(",\"distinguishable\":null,\"why\":\"Fewer than two whole blocks in a range, or no samples (GPU time has none while GPU Usage is off); read more frames or use smaller blocks.\"");
                return;
            }

            sb.Append(",\"meanA\":").Append(JsonNumber(Mean(blocksA)))
                .Append(",\"meanB\":").Append(JsonNumber(Mean(blocksB)))
                .Append(",\"diff\":").Append(JsonNumber(difference))
                .Append(",\"ciLow\":").Append(JsonNumber(low))
                .Append(",\"ciHigh\":").Append(JsonNumber(high))
                .Append(",\"distinguishable\":").Append(low > 0.0 || high < 0.0 ? "true" : "false");
        }

        /// <summary>B minus A and its 95% interval from Welch's t over two sets of block means.</summary>
        /// <param name="a">Block means of A.</param>
        /// <param name="b">Block means of B.</param>
        /// <param name="difference">Mean of B minus mean of A.</param>
        /// <param name="low">Lower end of the interval.</param>
        /// <param name="high">Upper end of the interval.</param>
        /// <returns>False when either side has fewer than two blocks.</returns>
        private static bool ProfilerWelchInterval(double[] a, double[] b, out double difference, out double low, out double high)
        {
            difference = low = high = 0.0;
            if (a.Length < 2 || b.Length < 2) return false;
            double errorA = SampleVariance(a) / a.Length;
            double errorB = SampleVariance(b) / b.Length;
            double standardError = Math.Sqrt(errorA + errorB);
            double degrees = standardError > 0.0
                ? (errorA + errorB) * (errorA + errorB) / (errorA * errorA / (a.Length - 1) + errorB * errorB / (b.Length - 1))
                : a.Length + b.Length - 2;
            difference = Mean(b) - Mean(a);
            double halfWidth = ProfilerTCritical95(degrees) * standardError;
            low = difference - halfWidth;
            high = difference + halfWidth;
            return true;
        }

        /// <summary>Means of consecutive whole blocks of values; a partial block at the end is dropped.</summary>
        /// <param name="values">Values in frame order.</param>
        /// <param name="blockFrames">Values per block.</param>
        /// <returns>One mean per whole block.</returns>
        private static double[] ProfilerBlockMeans(List<double> values, int blockFrames)
        {
            int blocks = blockFrames > 0 ? values.Count / blockFrames : 0;
            var means = new double[blocks];
            for (int block = 0; block < blocks; block++)
            {
                double sum = 0.0;
                for (int i = 0; i < blockFrames; i++) sum += values[block * blockFrames + i];
                means[block] = sum / blockFrames;
            }

            return means;
        }

        /// <summary>Unbiased sample variance, or 0 for fewer than two values.</summary>
        /// <param name="values">The samples.</param>
        /// <returns>The sample variance.</returns>
        private static double SampleVariance(double[] values)
        {
            if (values == null || values.Length < 2) return 0.0;
            double mean = Mean(values);
            double sum = 0.0;
            for (int i = 0; i < values.Length; i++) sum += (values[i] - mean) * (values[i] - mean);
            return sum / (values.Length - 1);
        }

        /// <summary>
        /// Two-sided 95% Student t critical value: a table for 1 to 4 degrees of freedom, and the Cornish-Fisher series in
        /// the normal quantile above that, within 0.05 of the exact value from 5 degrees up.
        /// </summary>
        /// <param name="degrees">Degrees of freedom, possibly fractional (Welch).</param>
        /// <returns>The critical value.</returns>
        private static double ProfilerTCritical95(double degrees)
        {
            if (double.IsNaN(degrees) || degrees < 1.0) return ProfilerTCritical95Small[0];
            if (degrees < 5.0) return ProfilerTCritical95Small[(int)Math.Floor(degrees) - 1];

            const double z = 1.959963984540054;
            double z3 = z * z * z;
            double z5 = z3 * z * z;
            return z + (z3 + z) / (4.0 * degrees) + (5.0 * z5 + 16.0 * z3 + 3.0 * z) / (96.0 * degrees * degrees);
        }

        #endregion

        #region Settings

        /// <summary>
        /// Reads the profiler's settings, and with a JSON object writes them: frameCount (frames buffered, 300 to 2000),
        /// deepProfiling (refused during play mode), allocationCallstacks (the GC allocation call stack recording) and
        /// profileEditor (records the editor's own work broken down instead of as one EditorLoop sample).
        /// Every write is validated before any is applied, and each is read back.
        /// </summary>
        /// <param name="args">Empty to read, or "| {json}" to write.</param>
        /// <returns>JSON of the settings before and after, or an error.</returns>
        private static string ProfilerSettingsCommand(string args)
        {
            string text = (args ?? string.Empty).Trim();
            if (text.StartsWith("|", StringComparison.Ordinal)) text = text.Substring(1).Trim();
            string before = ProfilerSettingsJson();
            if (text.Length == 0) return "{\"settings\":" + before + "}";

            JObject payload;
            try
            {
                payload = JObject.Parse(text);
            }
            catch (Exception ex)
            {
                return "{\"error\":" + JsonQuote("The settings are not a JSON object: " + ex.Message + " Nothing changed.") + "}";
            }

            PropertyInfo frameCountProperty = ProfilerFrameCountProperty();
            int? frameCount = null;
            bool? deep = null;
            bool? callstacks = null;
            bool? profileEditor = null;
            foreach (var property in payload.Properties())
            {
                if (property.Name == "frameCount")
                {
                    if (property.Value.Type != JTokenType.Integer) return "{\"error\":\"frameCount must be a whole number. Nothing changed.\"}";
                    int value = property.Value.Value<int>();
                    if (value < 300 || value > 2000) return "{\"error\":" + JsonQuote("frameCount must be 300 to 2000; " + value + " is outside that. Nothing changed.") + "}";
                    if (frameCountProperty == null) return "{\"error\":\"This editor has no ProfilerUserSettings.frameCount, so the buffer size cannot be set. Nothing changed.\"}";
                    frameCount = value;
                }
                else if (property.Name == "deepProfiling")
                {
                    if (property.Value.Type != JTokenType.Boolean) return "{\"error\":\"deepProfiling must be true or false. Nothing changed.\"}";
                    if (EditorApplication.isPlayingOrWillChangePlaymode) return "{\"error\":\"deepProfiling is not changed during play mode, where switching it is untested. Stop play mode first. Nothing changed.\"}";
                    deep = property.Value.Value<bool>();
                }
                else if (property.Name == "allocationCallstacks")
                {
                    if (property.Value.Type != JTokenType.Boolean) return "{\"error\":\"allocationCallstacks must be true or false. Nothing changed.\"}";
                    callstacks = property.Value.Value<bool>();
                }
                else if (property.Name == "profileEditor")
                {
                    if (property.Value.Type != JTokenType.Boolean) return "{\"error\":\"profileEditor must be true or false. Nothing changed.\"}";
                    profileEditor = property.Value.Value<bool>();
                }
                else
                {
                    return "{\"error\":" + JsonQuote("Unknown setting '" + property.Name + "'; the settings are frameCount, deepProfiling, allocationCallstacks and profileEditor. Nothing changed.") + "}";
                }
            }

            if (frameCount.HasValue) frameCountProperty.SetValue(null, frameCount.Value);
            if (deep.HasValue) ProfilerDriver.deepProfiling = deep.Value;
            if (profileEditor.HasValue) ProfilerDriver.profileEditor = profileEditor.Value;
            if (callstacks.HasValue)
            {
                ProfilerMemoryRecordMode mode = ProfilerDriver.memoryRecordMode;
                ProfilerDriver.memoryRecordMode = callstacks.Value ? mode | ProfilerMemoryRecordMode.GCAlloc : mode & ~ProfilerMemoryRecordMode.GCAlloc;
            }

            var mismatches = new List<string>();
            if (frameCount.HasValue && (int)frameCountProperty.GetValue(null) != frameCount.Value) mismatches.Add("frameCount");
            if (deep.HasValue && ProfilerDriver.deepProfiling != deep.Value) mismatches.Add("deepProfiling");
            if (profileEditor.HasValue && ProfilerDriver.profileEditor != profileEditor.Value) mismatches.Add("profileEditor");
            if (callstacks.HasValue && ((ProfilerDriver.memoryRecordMode & ProfilerMemoryRecordMode.GCAlloc) != 0) != callstacks.Value) mismatches.Add("allocationCallstacks");
            var sb = new StringBuilder();
            sb.Append("{\"before\":").Append(before).Append(",\"after\":").Append(ProfilerSettingsJson());
            if (mismatches.Count > 0) sb.Append(",\"warning\":").Append(JsonQuote("Read back differs from what was asked for: " + string.Join(", ", mismatches.ToArray()) + "."));
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>The profiler settings as read now.</summary>
        /// <returns>A JSON object.</returns>
        private static string ProfilerSettingsJson()
        {
            PropertyInfo frameCountProperty = ProfilerFrameCountProperty();
            ProfilerMemoryRecordMode mode = ProfilerDriver.memoryRecordMode;
            return "{\"frameCount\":" + (frameCountProperty != null ? frameCountProperty.GetValue(null).ToString() : "null")
                + ",\"deepProfiling\":" + (ProfilerDriver.deepProfiling ? "true" : "false")
                + ",\"allocationCallstacks\":" + ((mode & ProfilerMemoryRecordMode.GCAlloc) != 0 ? "true" : "false")
                + ",\"profileEditor\":" + (ProfilerDriver.profileEditor ? "true" : "false")
                + ",\"memoryRecordMode\":" + JsonQuote(mode.ToString())
                + ",\"enabled\":" + (Profiler.enabled ? "true" : "false")
                + ",\"recording\":" + (ProfilerDriver.enabled ? "true" : "false")
                + ",\"firstBuffered\":" + ProfilerFirstFrame()
                + ",\"lastBuffered\":" + ProfilerLastFrame() + "}";
        }

        /// <summary>The internal ProfilerUserSettings.frameCount property, or null when this editor lacks it.</summary>
        /// <returns>The property, or null.</returns>
        private static PropertyInfo ProfilerFrameCountProperty()
        {
            Type settings = typeof(Editor).Assembly.GetType("UnityEditor.Profiling.ProfilerUserSettings");
            return settings != null ? settings.GetProperty("frameCount", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) : null;
        }

        #endregion

        #region Isolate

        /// <summary>
        /// Measures what one object costs in play mode: switches it off and on in turn (cycles of one phase with it and
        /// one without), records each phase, puts it back exactly as found, then compares the phases on the frame figures
        /// and on markers. The work runs over many frames, so the command reports when it finishes.
        /// </summary>
        /// <param name="args">"&lt;target&gt; [| what] [| componentType] [| cycles] [| phaseFrames]".</param>
        /// <returns>An empty withheld response, or a refusal.</returns>
        private static string ProfilerIsolateCommand(string args)
        {
            string[] sections = SplitCommandSections(args ?? string.Empty);
            CommandExecutionContext context = _commandContext;
            if (context == null || string.IsNullOrEmpty(context.JobId) || context.CompletingDeferred) return "profiler-isolate needs a durable command (a typed tool call or conduit-run) to report back on. Nothing changed.";
            if (!EditorApplication.isPlaying) return "profiler-isolate measures play mode; enter play mode first. Nothing changed.";
            if (_profilerIsolateRun != null) return "profiler-isolate is already running for " + _profilerIsolateRun.TargetPath + "; one at a time, because each would disturb the other's measurement. Nothing changed.";

            GameObject target;
            string findError = TryFindGameObject(sections.Length > 0 ? sections[0] : string.Empty, out target);
            if (findError != null) return findError + " Nothing changed.";
            string what = sections.Length > 1 && sections[1].Trim().Length > 0 ? sections[1].Trim().ToLowerInvariant() : "object";
            if (what != "object" && what != "renderers" && what != "component") return "what must be object, renderers or component; '" + what + "' is none of them. Nothing changed.";
            int cycles = sections.Length > 3 && sections[3].Trim().Length > 0 ? ParsePositiveInt(sections[3].Trim(), PROFILER_ISOLATE_DEFAULT_CYCLES, 20) : PROFILER_ISOLATE_DEFAULT_CYCLES;
            int phaseFrames = sections.Length > 4 && sections[4].Trim().Length > 0 ? ParsePositiveInt(sections[4].Trim(), PROFILER_ISOLATE_DEFAULT_PHASE_FRAMES, PROFILER_FRAMES_MAX) : PROFILER_ISOLATE_DEFAULT_PHASE_FRAMES;
            if (phaseFrames < 2 * PROFILER_COMPARE_BLOCK_FRAMES) return "phaseFrames must be at least " + (2 * PROFILER_COMPARE_BLOCK_FRAMES) + " so every phase holds two blocks. Nothing changed.";
            string gpu = sections.Length > 5 ? sections[5].Trim().ToLowerInvariant() : string.Empty;
            if (gpu.Length > 0 && gpu != "on" && gpu != "off") return "gpu must be on or off; '" + gpu + "' is neither. Nothing changed.";

            var run = new ProfilerIsolateRun
            {
                JobId = context.JobId,
                Target = target,
                TargetPath = GetGameObjectPath(target),
                What = what,
                Cycles = cycles,
                PhaseFrames = phaseFrames,
                FoundActive = target.activeSelf,
                ProfilerWasEnabled = Profiler.enabled,
                RecordingWas = ProfilerDriver.enabled,
                WantGpu = gpu == "on",
                Deadline = EditorApplication.timeSinceStartup + PROFILER_ISOLATE_MAX_SECONDS,
            };

            if (what == "component")
            {
                string typeName = sections.Length > 2 ? sections[2].Trim() : string.Empty;
                if (typeName.Length == 0) return "what component needs a component type in the next section, such as Light or ReflectionProbe. Nothing changed.";
                Component component = null;
                foreach (Component candidate in target.GetComponents<Component>())
                {
                    if (candidate == null) continue;
                    Type type = candidate.GetType();
                    if (string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase) || string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        component = candidate;
                        break;
                    }
                }

                if (component == null) return "No component of type '" + typeName + "' on " + run.TargetPath + "; gameobject-component-list lists them. Nothing changed.";
                run.Behaviour = component as Behaviour;
                run.RendererComponent = component as Renderer;
                run.ColliderComponent = component as Collider;
                if (run.Behaviour == null && run.RendererComponent == null && run.ColliderComponent == null) return "A " + component.GetType().Name + " cannot be switched off and on, so its cost cannot be isolated this way. Nothing changed.";
                run.FoundComponentEnabled = IsolateComponentEnabled(run);
            }
            else if (what == "renderers")
            {
                run.Renderers = target.GetComponentsInChildren<Renderer>(true);
                if (run.Renderers.Length == 0) return run.TargetPath + " has no renderers on it or below it. Nothing changed.";
                run.FoundRenderersEnabled = new bool[run.Renderers.Length];
                for (int i = 0; i < run.Renderers.Length; i++) run.FoundRenderersEnabled[i] = run.Renderers[i].enabled;
            }

            if (run.WantGpu)
            {
                bool gpuWas;
                bool opened;
                string moduleError = ProfilerModuleActiveByName(PROFILER_GPU_MODULE_NAME, out gpuWas, out opened);
                run.ProfilerWindowOpened = opened;
                if (moduleError != null) return moduleError + " GPU time cannot be measured; nothing else changed." + (opened ? " The Profiler window was opened to read it." : string.Empty);
                run.GpuModuleWas = gpuWas;
                if (!gpuWas)
                {
                    ProfilerEnableModuleCommand(PROFILER_GPU_MODULE_NAME + " | true");
                    bool gpuNow;
                    moduleError = ProfilerModuleActiveByName(PROFILER_GPU_MODULE_NAME, out gpuNow, out opened);
                    if (moduleError != null || !gpuNow) return "The " + PROFILER_GPU_MODULE_NAME + " module did not switch on (" + (moduleError ?? "it reads back off") + "), so GPU time cannot be measured. Nothing else changed.";
                }
            }

            Profiler.enabled = true;
            if (!ProfilerDriver.enabled) EnableProfilerDriverRecording(true);
            if (!ProfilerDriver.enabled)
            {
                Profiler.enabled = run.ProfilerWasEnabled;
                return "The profiler would not start recording, so nothing can be measured. " + PROFILER_GPU_MODULE_NAME + " module: " + IsolateRestoreGpu(run) + ". Nothing else changed.";
            }

            context.Deferred = true;
            _profilerIsolateRun = run;
            run.Tick = () =>
            {
                using (ConduitProfiling.MainThread.Auto()) IsolateTick(run);
            };
            run.BeforeReload = () => IsolateEnd(run, "Scripts reloaded during the measurement.");
            EditorApplication.update += run.Tick;
            AssemblyReloadEvents.beforeAssemblyReload += run.BeforeReload;
            IsolateBeginPhase(run);
            return string.Empty;
        }

        /// <summary>Whether the isolated component is enabled now.</summary>
        /// <param name="run">The run.</param>
        /// <returns>Its enabled flag.</returns>
        private static bool IsolateComponentEnabled(ProfilerIsolateRun run)
        {
            if (run.Behaviour != null) return run.Behaviour.enabled;
            if (run.RendererComponent != null) return run.RendererComponent.enabled;
            return run.ColliderComponent != null && run.ColliderComponent.enabled;
        }

        /// <summary>Puts the target in the state of the current phase: as found, or switched the other way.</summary>
        /// <param name="run">The run.</param>
        private static void IsolateBeginPhase(ProfilerIsolateRun run)
        {
            run.PhaseWithTarget = run.Phase % 2 == 0;
            IsolateApply(run, run.PhaseWithTarget);
            run.PhaseState = 0;
            run.Settle = NewProfilerSettleWatch(PROFILER_SETTLE_DEFAULT_SECONDS);
        }

        /// <summary>Switches the target on (as found) or off.</summary>
        /// <param name="run">The run.</param>
        /// <param name="asFound">True for the state the target was found in.</param>
        private static void IsolateApply(ProfilerIsolateRun run, bool asFound)
        {
            if (run.What == "object")
            {
                run.Target.SetActive(asFound ? run.FoundActive : !run.FoundActive);
            }
            else if (run.What == "renderers")
            {
                for (int i = 0; i < run.Renderers.Length; i++)
                {
                    if (run.Renderers[i] != null) run.Renderers[i].enabled = asFound && run.FoundRenderersEnabled[i];
                }
            }
            else
            {
                bool enabled = asFound ? run.FoundComponentEnabled : !run.FoundComponentEnabled;
                if (run.Behaviour != null) run.Behaviour.enabled = enabled;
                else if (run.RendererComponent != null) run.RendererComponent.enabled = enabled;
                else if (run.ColliderComponent != null) run.ColliderComponent.enabled = enabled;
            }
        }

        /// <summary>Advances the phases: settle, measure, record the range, next phase; finishes after the last.</summary>
        /// <param name="run">The run.</param>
        private static void IsolateTick(ProfilerIsolateRun run)
        {
            if (!EditorApplication.isPlaying)
            {
                IsolateEnd(run, "Play mode ended during the measurement.");
                return;
            }

            if (run.Target == null)
            {
                IsolateEnd(run, "The target was destroyed during the measurement.");
                return;
            }

            if (!ProfilerDriver.enabled)
            {
                IsolateEnd(run, "The profiler stopped recording during the measurement.");
                return;
            }

            if (EditorApplication.timeSinceStartup > run.Deadline)
            {
                IsolateEnd(run, "The measurement took longer than " + PROFILER_ISOLATE_MAX_SECONDS + " seconds.");
                return;
            }

            int last = ProfilerLastFrame();
            if (run.PhaseState == 0)
            {
                int settle = ProfilerSettleCheck(run.Settle);
                if (settle == 0) return;
                if (settle == 2)
                {
                    IsolateEnd(run, "Phase " + (run.Phase + 1) + " (target " + (run.PhaseWithTarget ? "as found" : "switched the other way") + ") was still trending "
                        + PROFILER_SETTLE_DEFAULT_SECONDS + " seconds after the switch, so it was never measured. Last check: " + (run.Settle.LastCheck ?? "fewer frames than two windows") + ".");
                    return;
                }

                if (run.SettleJson.Length > 0) run.SettleJson.Append(',');
                run.SettleJson.Append("{\"phase\":").Append(run.Phase + 1)
                    .Append(",\"target\":").Append(JsonQuote(run.PhaseWithTarget ? "as found" : "switched"))
                    .Append(",\"settledAfterSeconds\":").Append(JsonNumber(run.Settle.SettledSeconds)).Append('}');
                run.PhaseFrom = last + 1;
                run.PhaseState = 1;
                return;
            }

            // The newest frame may still be collecting, so the phase ends once a frame after it exists.
            if (last < run.PhaseFrom + run.PhaseFrames) return;
            var range = new ProfilerFrameRange { From = run.PhaseFrom, To = run.PhaseFrom + run.PhaseFrames - 1 };
            if (run.PhaseWithTarget) run.With.Add(range);
            else run.Without.Add(range);
            run.Phase++;
            if (run.Phase >= run.Cycles * 2)
            {
                IsolateEnd(run, null);
                return;
            }

            IsolateBeginPhase(run);
        }

        /// <summary>
        /// Ends the run: puts the target and the profiler back as found, reads the target back, then completes the job with
        /// the comparison, or fails it with the reason.
        /// </summary>
        /// <param name="run">The run.</param>
        /// <param name="failure">Why the run stopped early, or null when it finished.</param>
        private static void IsolateEnd(ProfilerIsolateRun run, string failure)
        {
            EditorApplication.update -= run.Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= run.BeforeReload;
            if (ReferenceEquals(_profilerIsolateRun, run)) _profilerIsolateRun = null;

            string restore = "not applicable, the target no longer exists";
            if (run.Target != null)
            {
                IsolateApply(run, true);
                bool restored = run.What == "object" ? run.Target.activeSelf == run.FoundActive
                    : run.What == "component" ? IsolateComponentEnabled(run) == run.FoundComponentEnabled
                    : IsolateRenderersRestored(run);
                restore = restored ? "restored to the state found, read back" : "read back differs from the state found";
            }

            if (!run.RecordingWas) EnableProfilerDriverRecording(false);
            Profiler.enabled = run.ProfilerWasEnabled;
            string recording = ProfilerDriver.enabled == run.RecordingWas
                ? (run.RecordingWas ? "was on, left on, read back" : "switched on for the run, back off, read back")
                : "found " + (run.RecordingWas ? "on" : "off") + " but reads back " + (ProfilerDriver.enabled ? "on" : "off");
            if (Profiler.enabled != run.ProfilerWasEnabled) recording += "; Profiler.enabled found " + run.ProfilerWasEnabled + " but reads back " + Profiler.enabled;
            string gpuModule = IsolateRestoreGpu(run) + (run.ProfilerWindowOpened ? "; the Profiler window was opened to read it and left open" : string.Empty);

            string error;
            if (failure != null)
            {
                ConduitJobLedger.Fail(run.JobId, "profiler_isolate_failed", failure + " Target " + restore + "; profiler recording " + recording + "; " + PROFILER_GPU_MODULE_NAME + " module " + gpuModule + ". No comparison was made.", out error);
                return;
            }

            var sb = new StringBuilder();
            sb.Append("{\"target\":").Append(JsonQuote(run.TargetPath))
                .Append(",\"what\":").Append(JsonQuote(run.What))
                .Append(",\"foundState\":").Append(JsonQuote(IsolateFoundState(run)))
                .Append(",\"restore\":").Append(JsonQuote(restore))
                .Append(",\"cycles\":").Append(run.Cycles)
                .Append(",\"phaseFrames\":").Append(run.PhaseFrames)
                .Append(",\"settle\":[").Append(run.SettleJson).Append(']')
                .Append(",\"profilerRecording\":").Append(JsonQuote(recording))
                .Append(",\"gpuUsageModule\":").Append(JsonQuote(gpuModule))
                .Append(",\"cost\":\"A is the phases with the target switched the other way, B the phases as found, so diff is what the target as found costs.\",");
            try
            {
                AppendProfilerComparison(sb, run.Without, run.With, PROFILER_COMPARE_BLOCK_FRAMES, true, true, string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                ConduitJobLedger.Fail(run.JobId, "profiler_isolate_failed", "The phases ran and the target was " + restore + ", but the comparison failed: " + ex.Message, out error);
                return;
            }

            sb.Append('}');
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (ConduitJobLedger.Complete(run.JobId, "command_completed", sb.ToString(), out error)) break;
            }
        }

        /// <summary>Whether every isolated renderer reads back as found.</summary>
        /// <param name="run">The run.</param>
        /// <returns>True when all match.</returns>
        private static bool IsolateRenderersRestored(ProfilerIsolateRun run)
        {
            for (int i = 0; i < run.Renderers.Length; i++)
            {
                if (run.Renderers[i] != null && run.Renderers[i].enabled != run.FoundRenderersEnabled[i]) return false;
            }

            return true;
        }

        /// <summary>Describes the state the target was found in.</summary>
        /// <param name="run">The run.</param>
        /// <returns>A short description.</returns>
        private static string IsolateFoundState(ProfilerIsolateRun run)
        {
            if (run.What == "object") return run.FoundActive ? "active" : "inactive";
            if (run.What == "component") return run.FoundComponentEnabled ? "component enabled" : "component disabled";
            int enabled = 0;
            foreach (bool on in run.FoundRenderersEnabled) if (on) enabled++;
            return enabled + " of " + run.FoundRenderersEnabled.Length + " renderers enabled";
        }

        /// <summary>Switches the GPU Usage module back off when the run switched it on, and reads it back.</summary>
        /// <param name="run">The run.</param>
        /// <returns>The module's state for the report.</returns>
        private static string IsolateRestoreGpu(ProfilerIsolateRun run)
        {
            if (!run.WantGpu) return "not used";
            if (run.GpuModuleWas) return "was already on, left on";
            ProfilerEnableModuleCommand(PROFILER_GPU_MODULE_NAME + " | false");
            bool now;
            bool opened;
            string error = ProfilerModuleActiveByName(PROFILER_GPU_MODULE_NAME, out now, out opened);
            if (error != null) return "switched on for the run; reading it back failed: " + error;
            return now ? "switched on for the run; switching it back off did not take, it reads back on" : "switched on for the run, back off, read back";
        }

        /// <summary>Reads a Profiler window module's active state by its display name.</summary>
        /// <param name="moduleName">The module's display name.</param>
        /// <param name="active">Its state, as the window holds it.</param>
        /// <param name="opened">True when the Profiler window had to be opened to read it.</param>
        /// <returns>Null on success, or why it could not be read.</returns>
        private static string ProfilerModuleActiveByName(string moduleName, out bool active, out bool opened)
        {
            active = false;
            ProfilerWindow window = GetOrOpenProfilerWindow(out opened);
            string error;
            IList modules = ProfilerWindowModules(window, out error);
            if (modules == null) return error;
            for (int i = 0; i < modules.Count; i++)
            {
                if (!string.Equals(ProfilerModuleName(modules[i]), moduleName, StringComparison.OrdinalIgnoreCase)) continue;
                return TryProfilerModuleActive(window, modules[i], out active, out error) ? null : error;
            }

            return "The Profiler window has no module named '" + moduleName + "'.";
        }

        #endregion

        #region Settling

        /// <summary>Starts watching the frames recorded from the next one on.</summary>
        /// <param name="maxSeconds">Seconds before the watch gives up.</param>
        /// <returns>The watch.</returns>
        private static ProfilerSettleWatch NewProfilerSettleWatch(double maxSeconds)
        {
            double now = EditorApplication.timeSinceStartup;
            return new ProfilerSettleWatch
            {
                StartFrame = ProfilerLastFrame() + 1,
                StartTime = now,
                Deadline = now + maxSeconds,
                NextCheck = now,
                Columns = BuildProfilerColumns("none", string.Empty),
            };
        }

        /// <summary>
        /// Judges whether the frames since the watch began have stopped trending. Frame times never stop moving, so the
        /// test compares the two newest windows, each at least the window length in frame time and
        /// <see cref="PROFILER_SETTLE_MIN_BLOCKS"/> whole blocks, on main-thread and render-thread work: block medians, so a
        /// lone spike cannot pass a state that is still shifting, and Welch 95% intervals. A difference the interval
        /// proves means still trending. Settled needs positive evidence: every interval inside
        /// <see cref="PROFILER_SETTLE_TOLERANCE"/> of the figure's level; an interval too wide to show either is
        /// inconclusive and the next wider window in <see cref="ProfilerSettleWindowScales"/> is tried. Checks run at most
        /// every <see cref="PROFILER_SETTLE_CHECK_SECONDS"/> and each frame is read once, so watching does not become the
        /// load being judged.
        /// </summary>
        /// <param name="watch">The watch.</param>
        /// <returns>0 still trending or too few frames, 1 settled, 2 out of time.</returns>
        private static int ProfilerSettleCheck(ProfilerSettleWatch watch)
        {
            double now = EditorApplication.timeSinceStartup;
            if (now > watch.Deadline) return 2;

            // The newest frame may still be collecting.
            int last = ProfilerLastFrame() - 1;
            int first = Math.Max(watch.StartFrame, ProfilerFirstFrame());

            // Read new frames a few per tick as they arrive: reading a frame costs about half a millisecond per thread,
            // and saving them up for each check put 15 to 30 ms into one editor frame.
            if (watch.NextRead < first) watch.NextRead = first;
            using (ConduitProfiling.SettleRead.Auto())
            {
                for (int i = 0; i < PROFILER_SETTLE_READS_PER_TICK && watch.NextRead <= last; i++, watch.NextRead++)
                {
                    if (watch.Values.ContainsKey(watch.NextRead)) continue;
                    var values = new double[watch.Columns.Count];
                    watch.Values[watch.NextRead] = ReadProfilerFrameValues(watch.NextRead, watch.Columns, values) ? values : null;
                }
            }

            if (watch.NextRead <= last || now < watch.NextCheck) return 0;
            watch.NextCheck = now + PROFILER_SETTLE_CHECK_SECONDS;
            if (last < first) return 0;
            using var checkScope = ConduitProfiling.SettleCheck.Auto();

            foreach (double scale in ProfilerSettleWindowScales)
            {
                double seconds = PROFILER_SETTLE_WINDOW_SECONDS * scale;
                var windowB = new List<double[]>();
                var windowA = new List<double[]>();
                int fromB;
                int fromA;
                if (!ProfilerSettleWindow(watch, last, first, seconds, windowB, out fromB)) return 0;
                if (!ProfilerSettleWindow(watch, fromB - 1, first, seconds, windowA, out fromA)) return 0;

                var sb = new StringBuilder();
                sb.Append("{\"windowSeconds\":").Append(JsonNumber(seconds))
                    .Append(",\"a\":{\"from\":").Append(fromA).Append(",\"to\":").Append(fromB - 1)
                    .Append("},\"b\":{\"from\":").Append(fromB).Append(",\"to\":").Append(last).Append("},\"figures\":[");
                bool trending = false;
                bool withinAll = true;
                int compared = 0;
                for (int k = 0; k < watch.Columns.Count; k++)
                {
                    if (watch.Columns[k].Kind != PROFILER_KIND_THREAD_WORK) continue;
                    double difference;
                    double low;
                    double high;
                    double[] blocksA = ProfilerBlockMedians(windowA, k, PROFILER_COMPARE_BLOCK_FRAMES);
                    double[] blocksB = ProfilerBlockMedians(windowB, k, PROFILER_COMPARE_BLOCK_FRAMES);
                    if (!ProfilerWelchInterval(blocksA, blocksB, out difference, out low, out high)) continue;
                    double band = PROFILER_SETTLE_TOLERANCE * Math.Abs(Mean(blocksB));
                    bool distinguishable = low > 0.0 || high < 0.0;
                    bool within = low >= -band && high <= band;
                    if (distinguishable) trending = true;
                    if (!within) withinAll = false;
                    if (compared++ > 0) sb.Append(',');
                    sb.Append("{\"key\":").Append(JsonQuote(watch.Columns[k].Key))
                        .Append(",\"diff\":").Append(JsonNumber(difference))
                        .Append(",\"ciLow\":").Append(JsonNumber(low))
                        .Append(",\"ciHigh\":").Append(JsonNumber(high))
                        .Append(",\"band\":").Append(JsonNumber(band))
                        .Append(",\"verdict\":").Append(JsonQuote(distinguishable ? "trending" : within ? "steady" : "inconclusive")).Append('}');
                }

                sb.Append("]}");
                watch.LastCheck = sb.ToString();
                if (compared == 0 || trending) return 0;
                if (withinAll)
                {
                    watch.SettledSeconds = now - watch.StartTime;
                    watch.SettledFrame = last;
                    return 1;
                }
            }

            return 0;
        }

        /// <summary>
        /// Collects one window of frames ending at <paramref name="end"/> and reaching back no further than
        /// <paramref name="first"/>: at least <paramref name="seconds"/> of frame time and
        /// <see cref="PROFILER_SETTLE_MIN_BLOCKS"/> whole blocks. Frames that cannot be read are skipped.
        /// </summary>
        /// <param name="watch">The watch, whose cache supplies and keeps each frame's figures.</param>
        /// <param name="end">Newest frame of the window.</param>
        /// <param name="first">Oldest frame the window may take.</param>
        /// <param name="seconds">Frame time the window must cover.</param>
        /// <param name="window">Receives the frames' figures, oldest first.</param>
        /// <param name="from">Oldest frame taken.</param>
        /// <returns>False when the frames ran out before the window was full.</returns>
        private static bool ProfilerSettleWindow(ProfilerSettleWatch watch, int end, int first, double seconds, List<double[]> window, out int from)
        {
            from = end;
            double windowMs = 0.0;
            int minFrames = PROFILER_SETTLE_MIN_BLOCKS * PROFILER_COMPARE_BLOCK_FRAMES;
            for (int frame = end; frame >= first; frame--)
            {
                double[] values;
                if (!watch.Values.TryGetValue(frame, out values))
                {
                    values = new double[watch.Columns.Count];
                    if (!ReadProfilerFrameValues(frame, watch.Columns, values)) values = null;
                    watch.Values[frame] = values;
                }

                if (values == null) continue;
                window.Add(values);
                from = frame;
                windowMs += values[0];
                if (windowMs >= seconds * 1000.0 && window.Count >= minFrames)
                {
                    window.Reverse();
                    return true;
                }
            }

            return false;
        }

        /// <summary>Medians of consecutive whole blocks of one figure; NaN values are left out and a partial block is dropped.</summary>
        /// <param name="window">Frames' figures, oldest first.</param>
        /// <param name="column">Figure index.</param>
        /// <param name="blockFrames">Values per block.</param>
        /// <returns>One median per whole block.</returns>
        private static double[] ProfilerBlockMedians(List<double[]> window, int column, int blockFrames)
        {
            var values = new List<double>(window.Count);
            foreach (double[] frame in window)
            {
                if (!double.IsNaN(frame[column])) values.Add(frame[column]);
            }

            int blocks = values.Count / blockFrames;
            var medians = new double[blocks];
            var block = new double[blockFrames];
            for (int i = 0; i < blocks; i++)
            {
                values.CopyTo(i * blockFrames, block, 0, blockFrames);
                Array.Sort(block);
                medians[i] = Percentile(block, 0.5);
            }

            return medians;
        }

        /// <summary>
        /// Waits until the frames recorded after the call stop trending (see <see cref="ProfilerSettleCheck"/>), then
        /// reports how long that took and the frame it settled at, or that it was still trending when the time ran out.
        /// Recording must already be on; nothing is changed.
        /// </summary>
        /// <param name="args">"[maxSeconds]": seconds to wait, default <see cref="PROFILER_SETTLE_DEFAULT_SECONDS"/>.</param>
        /// <returns>An empty withheld response, or a refusal.</returns>
        private static string ProfilerWaitSettledCommand(string args)
        {
            CommandExecutionContext context = _commandContext;
            if (context == null || string.IsNullOrEmpty(context.JobId) || context.CompletingDeferred) return "profiler-wait-settled needs a durable command (a typed tool call or conduit-run) to report back on. Nothing changed.";
            if (!ProfilerDriver.enabled) return "The profiler is not recording, so no new frames arrive to judge; start recording first (profiler-record-start). Nothing changed.";
            if (_profilerSettleRun != null) return "profiler-wait-settled is already waiting; one at a time. Nothing changed.";
            string token = (args ?? string.Empty).Trim();
            int maxSeconds = PROFILER_SETTLE_DEFAULT_SECONDS;
            if (token.Length > 0 && (!int.TryParse(token, out maxSeconds) || maxSeconds < 1 || maxSeconds > PROFILER_SETTLE_MAX_SECONDS))
            {
                return "maxSeconds must be a whole number from 1 to " + PROFILER_SETTLE_MAX_SECONDS + "; '" + token + "' is not. Nothing changed.";
            }

            var run = new ProfilerSettleRun { JobId = context.JobId, MaxSeconds = maxSeconds, Watch = NewProfilerSettleWatch(maxSeconds) };
            context.Deferred = true;
            _profilerSettleRun = run;
            run.Tick = () =>
            {
                using (ConduitProfiling.MainThread.Auto()) SettleTick(run);
            };
            run.BeforeReload = () => SettleEnd(run, "Scripts reloaded while waiting.");
            EditorApplication.update += run.Tick;
            AssemblyReloadEvents.beforeAssemblyReload += run.BeforeReload;
            return string.Empty;
        }

        /// <summary>Advances a wait: ends it when the frames settle, the time runs out or recording stops.</summary>
        /// <param name="run">The wait.</param>
        private static void SettleTick(ProfilerSettleRun run)
        {
            if (!ProfilerDriver.enabled)
            {
                SettleEnd(run, "The profiler stopped recording while waiting.");
                return;
            }

            if (ProfilerSettleCheck(run.Watch) != 0) SettleEnd(run, null);
        }

        /// <summary>Ends a wait and reports whether the frames settled, with the last check either way.</summary>
        /// <param name="run">The wait.</param>
        /// <param name="failure">Why the wait stopped early, or null when it settled or ran out of time.</param>
        private static void SettleEnd(ProfilerSettleRun run, string failure)
        {
            EditorApplication.update -= run.Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= run.BeforeReload;
            if (ReferenceEquals(_profilerSettleRun, run)) _profilerSettleRun = null;

            string error;
            if (failure != null)
            {
                ConduitJobLedger.Fail(run.JobId, "profiler_wait_settled_failed", failure + " Nothing was changed.", out error);
                return;
            }

            ProfilerSettleWatch watch = run.Watch;
            var sb = new StringBuilder();
            if (watch.SettledFrame >= 0)
            {
                sb.Append("{\"settled\":true,\"settledAfterSeconds\":").Append(JsonNumber(watch.SettledSeconds))
                    .Append(",\"settledAtFrame\":").Append(watch.SettledFrame)
                    .Append(",\"lastCheck\":").Append(watch.LastCheck)
                    .Append(",\"note\":\"The two newest windows of main and render work differ by less than the band (5% of each figure's level), at 95%: the figures still move, but no longer trend. profiler-mark now starts a clean state.\"}");
            }
            else
            {
                sb.Append("{\"settled\":false,\"waitedSeconds\":").Append(run.MaxSeconds)
                    .Append(",\"lastCheck\":").Append(watch.LastCheck ?? "null")
                    .Append(",\"note\":\"Not settled when the time ran out. The last check's verdicts say why: trending means the windows provably differ, inconclusive means the noise was too wide to show steadiness even at the widest window. lastCheck null means too few frames for two windows. A comparison started now measures that trend or noise as well as the change.\"}");
            }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (ConduitJobLedger.Complete(run.JobId, "command_completed", sb.ToString(), out error)) break;
            }
        }

        #endregion

        #region Object Memory

        /// <summary>
        /// Sums the memory every loaded object of the given types takes, per type, and lists the largest, each marked as a
        /// project asset, a scene or runtime object, or an editor-only object (hidden and not saved).
        /// </summary>
        /// <param name="args">"[topN] [| types]".</param>
        /// <returns>JSON of the totals and the largest objects.</returns>
        private static string ProfilerMemoryObjectsCommand(string args)
        {
            string[] sections = SplitCommandSections(args ?? string.Empty);
            string[] tokens = SplitCommandTokens(sections.Length > 0 ? sections[0] : string.Empty);
            int topN = tokens.Length >= 1 ? ParsePositiveInt(tokens[0], 20, 500) : 20;
            string[] typeNames = sections.Length > 1 && sections[1].Trim().Length > 0 ? sections[1].Split(',') : ProfilerMemoryDefaultTypes;
            var types = new List<Type>();
            var unknown = new List<string>();
            foreach (string raw in typeNames)
            {
                string name = raw.Trim();
                if (name.Length == 0) continue;
                Type type = FindTypeLoose(name);
                if (type != null && typeof(UnityEngine.Object).IsAssignableFrom(type)) types.Add(type);
                else unknown.Add(name);
            }

            if (types.Count == 0) return "{\"error\":" + JsonQuote("None of the types are Unity object types: " + string.Join(", ", unknown.ToArray()) + ".") + "}";

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var totals = new Dictionary<Type, long[]>();    // bytes, count, editor-only bytes
            foreach (Type type in types) totals[type] = new long[3];
            var largest = new List<KeyValuePair<long, UnityEngine.Object>>();
            foreach (UnityEngine.Object obj in Resources.FindObjectsOfTypeAll<UnityEngine.Object>())
            {
                if (obj == null) continue;
                Type matched = null;
                foreach (Type type in types)
                {
                    if (type.IsInstanceOfType(obj))
                    {
                        matched = type;
                        break;
                    }
                }

                if (matched == null) continue;
                long size = Profiler.GetRuntimeMemorySizeLong(obj);
                long[] total = totals[matched];
                total[0] += size;
                total[1]++;
                if (MemoryObjectOrigin(obj) == "editor") total[2] += size;
                largest.Add(new KeyValuePair<long, UnityEngine.Object>(size, obj));
            }

            largest.Sort((a, b) => b.Key.CompareTo(a.Key));
            var sb = new StringBuilder();
            sb.Append("{\"readMs\":").Append(stopwatch.ElapsedMilliseconds).Append(",\"types\":[");
            int written = 0;
            foreach (Type type in types)
            {
                long[] total = totals[type];
                if (written++ > 0) sb.Append(',');
                sb.Append("{\"type\":").Append(JsonQuote(type.Name))
                    .Append(",\"count\":").Append(total[1])
                    .Append(",\"mb\":").Append(JsonNumber(total[0] / 1048576.0))
                    .Append(",\"editorOnlyMb\":").Append(JsonNumber(total[2] / 1048576.0)).Append('}');
            }

            sb.Append("],\"largest\":[");
            int limit = Math.Min(topN, largest.Count);
            for (int i = 0; i < limit; i++)
            {
                UnityEngine.Object obj = largest[i].Value;
                string origin = MemoryObjectOrigin(obj);
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(JsonQuote(obj.name))
                    .Append(",\"type\":").Append(JsonQuote(obj.GetType().Name))
                    .Append(",\"mb\":").Append(JsonNumber(largest[i].Key / 1048576.0))
                    .Append(",\"origin\":").Append(JsonQuote(origin))
                    .Append(",\"assetPath\":").Append(JsonQuote(origin == "asset" ? AssetDatabase.GetAssetPath(obj) : string.Empty)).Append('}');
            }

            sb.Append(']');
            if (unknown.Count > 0) sb.Append(",\"warning\":").Append(JsonQuote("Not Unity object types, so not counted: " + string.Join(", ", unknown.ToArray()) + "."));
            sb.Append(",\"note\":\"Memory is what the editor process holds now. editor objects (hidden and never saved, such as window render textures) do not exist in a build.\"}");
            return sb.ToString();
        }

        /// <summary>Classifies where an object comes from.</summary>
        /// <param name="obj">The object.</param>
        /// <returns>"asset", "editor" or "scene".</returns>
        private static string MemoryObjectOrigin(UnityEngine.Object obj)
        {
            if (EditorUtility.IsPersistent(obj)) return "asset";
            if ((obj.hideFlags & HideFlags.DontSave) == HideFlags.DontSave) return "editor";
            return "scene";
        }

        #endregion

        #region Frame Debugger

        /// <summary>
        /// Captures one frame with the Frame Debugger and reports every event: counts by type, events grouped by the object
        /// drawn and by render pass, and details (shader, pass, mesh size, batch-break reason) for the first draw events.
        /// The Frame Debugger only captures while the editor is in front and its window exists, so on Windows the editor is
        /// brought forward and the window opened unfocused, then both are put back and the foreground handed back.
        /// </summary>
        /// <param name="args">"[details] [| filter]": draw events to read details for (default 0), and a name filter choosing them.</param>
        /// <returns>An empty withheld response, or a refusal.</returns>
        private static string FrameDebuggerCaptureCommand(string args)
        {
            string[] sections = SplitCommandSections(args ?? string.Empty);
            string[] tokens = SplitCommandTokens(sections.Length > 0 ? sections[0] : string.Empty);
            CommandExecutionContext context = _commandContext;
            if (context == null || string.IsNullOrEmpty(context.JobId) || context.CompletingDeferred) return "frame-debugger-capture needs a durable command (a typed tool call or conduit-run) to report back on. Nothing changed.";
            if (_frameDebuggerRun != null) return "frame-debugger-capture is already running; one capture at a time. Nothing changed.";
            int details = 0;
            if (tokens.Length >= 1 && (!int.TryParse(tokens[0], out details) || details < 0 || details > FRAME_DEBUGGER_DETAILS_MAX))
            {
                return "details must be a whole number from 0 to " + FRAME_DEBUGGER_DETAILS_MAX + "; '" + tokens[0] + "' is not. Nothing changed.";
            }

            Type utility = FrameDebuggerUtilityType();
            if (utility == null || FrameDebuggerEventDataType() == null) return "This editor has no FrameDebuggerUtility in UnityEditorInternal.FrameDebuggerInternal, so frames cannot be captured. Nothing changed.";
            if (!(bool)FrameDebuggerGet("locallySupported")) return "The Frame Debugger reports itself unsupported on this graphics device. Nothing changed.";

            var run = new FrameDebuggerCaptureRun
            {
                JobId = context.JobId,
                DetailsWanted = details,
                Filter = sections.Length > 1 ? sections[1].Trim() : string.Empty,
            };

            // The foreground is recorded before anything is opened: opening a window activates the editor.
            run.FocusSupported = EditorForeground.Supported;
            run.PreviousForeground = EditorForeground.Current();
            run.WasEnabled = (int)FrameDebuggerGet("count") > 0;
            Type windowType = typeof(Editor).Assembly.GetType("UnityEditor.FrameDebuggerWindow");
            if (windowType != null && Resources.FindObjectsOfTypeAll(windowType).Length == 0) run.OpenedWindow = EditorWindow.GetWindow(windowType, false, null, false);
            if (run.FocusSupported && !InternalEditorUtility.isApplicationActive)
            {
                run.BroughtToFront = true;
                EditorForeground.Bring(EditorForeground.EditorWindow());
            }

            // As the Frame Debugger window's own EnableFrameDebugger does: pause a running play mode, and enable for the
            // editor's own profiler connection (-1) rather than player 0. Without both, play mode never delivered a frame.
            if (!run.WasEnabled)
            {
                if (EditorApplication.isPlaying && !EditorApplication.isPaused)
                {
                    run.PausedForCapture = true;
                    EditorApplication.isPaused = true;
                }

                if (run.OpenedWindow != null)
                {
                    run.EnableDelayTicks = FRAME_DEBUGGER_WINDOW_SETTLE_TICKS;
                    run.OpenedWindow.Repaint();
                }
                else
                {
                    FrameDebuggerCall("SetEnabled", true, ProfilerDriver.connectedProfiler);
                }
            }

            run.Deadline = EditorApplication.timeSinceStartup + FRAME_DEBUGGER_FOCUS_SECONDS + FRAME_DEBUGGER_CAPTURE_SECONDS;
            context.Deferred = true;
            _frameDebuggerRun = run;
            run.Tick = () =>
            {
                using (ConduitProfiling.MainThread.Auto()) FrameDebuggerTick(run);
            };
            run.BeforeReload = () => FrameDebuggerEnd(run, "Scripts reloaded during the capture.");
            EditorApplication.update += run.Tick;
            AssemblyReloadEvents.beforeAssemblyReload += run.BeforeReload;
            return string.Empty;
        }

        /// <summary>Waits for the capture, reads the events, steps through the detail events, then finishes.</summary>
        /// <param name="run">The run.</param>
        private static void FrameDebuggerTick(FrameDebuggerCaptureRun run)
        {
            try
            {
                if (EditorApplication.timeSinceStartup > run.Deadline)
                {
                    FrameDebuggerEnd(run, run.Step == 0
                        ? "No frame was captured within " + (FRAME_DEBUGGER_FOCUS_SECONDS + FRAME_DEBUGGER_CAPTURE_SECONDS) + " seconds"
                            + (InternalEditorUtility.isApplicationActive ? "." : "; the editor never became active, and it renders nothing in the background" + (run.FocusSupported ? "." : " (bringing it forward is Windows only)."))
                        : "Event details stopped arriving after " + run.DetailsRead + " of " + run.DetailIndices.Count + ": waiting for event "
                            + run.DetailIndices[run.DetailCursor] + ", the last read " + (run.LastDataReturned ? "carried event " + run.LastDataIndex : "returned no data") + ".");
                    return;
                }

                if (run.EnableDelayTicks > 0)
                {
                    run.EnableDelayTicks--;
                    if (run.EnableDelayTicks == 0) FrameDebuggerCall("SetEnabled", true, ProfilerDriver.connectedProfiler);
                    return;
                }

                if (run.Step == 0)
                {
                    int count = (int)FrameDebuggerGet("count");
                    if (count <= 0)
                    {
                        InternalEditorUtility.RepaintAllViews();
                        return;
                    }

                    run.EventCount = count;
                    FrameDebuggerReadEvents(run);
                    if (run.DetailIndices.Count == 0)
                    {
                        FrameDebuggerEnd(run, null);
                        return;
                    }

                    run.Step = 1;
                    run.DetailCursor = 0;
                    FrameDebuggerSet("limit", run.DetailIndices[0] + 1);
                    run.Deadline = EditorApplication.timeSinceStartup + FRAME_DEBUGGER_DETAIL_SECONDS;
                    return;
                }

                int index = run.DetailIndices[run.DetailCursor];
                object data = Activator.CreateInstance(FrameDebuggerEventDataType());
                MethodInfo getData = FrameDebuggerUtilityType().GetMethod("GetFrameEventData", BindingFlags.Static | BindingFlags.Public);
                // The data can still be the previous limit's until the frame is drawn again at the new one, so it is taken
                // only once it names the event asked for; otherwise a detail is reported against the wrong event.
                run.LastDataReturned = (bool)getData.Invoke(null, new[] { index, data });
                run.LastDataIndex = Convert.ToInt32(FrameDebuggerEventDataType().GetField("m_FrameEventIndex").GetValue(data));
                if (!run.LastDataReturned || run.LastDataIndex != index)
                {
                    InternalEditorUtility.RepaintAllViews();
                    return;
                }

                FrameDebuggerAppendDetail(run, index, data);
                run.DetailCursor++;
                if (run.DetailCursor >= run.DetailIndices.Count)
                {
                    FrameDebuggerEnd(run, null);
                    return;
                }

                FrameDebuggerSet("limit", run.DetailIndices[run.DetailCursor] + 1);
                run.Deadline = EditorApplication.timeSinceStartup + FRAME_DEBUGGER_DETAIL_SECONDS;
            }
            catch (Exception ex)
            {
                FrameDebuggerEnd(run, "The capture failed: " + ex.Message);
            }
        }

        /// <summary>Reads every event's type, object and pass, groups them, and chooses the events to read details for.</summary>
        /// <param name="run">The run.</param>
        private static void FrameDebuggerReadEvents(FrameDebuggerCaptureRun run)
        {
            Type utility = FrameDebuggerUtilityType();
            var events = (Array)utility.GetMethod("GetFrameEvents", BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
            MethodInfo infoName = utility.GetMethod("GetFrameEventInfoName", BindingFlags.Static | BindingFlags.Public);
            run.BreakCauseNames = (string[])utility.GetMethod("GetBatchBreakCauseStrings", BindingFlags.Static | BindingFlags.Public).Invoke(null, null);
            Type eventType = events.GetType().GetElementType();
            FieldInfo typeField = eventType.GetField("m_Type", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo objField = eventType.GetField("m_Obj", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var byType = new Dictionary<string, int>(StringComparer.Ordinal);
            var byObject = new Dictionary<string, int[]>(StringComparer.Ordinal);    // events, draw events
            var byPass = new Dictionary<string, int>(StringComparer.Ordinal);
            var byGroup = new Dictionary<string, int>(StringComparer.Ordinal);    // draw events per top two hierarchy levels
            run.EventTypes = new string[events.Length];
            run.EventObjects = new object[events.Length];
            int draws = 0;
            for (int i = 0; i < events.Length; i++)
            {
                object frameEvent = events.GetValue(i);
                string type = typeField.GetValue(frameEvent).ToString();
                var obj = objField.GetValue(frameEvent) as UnityEngine.Object;
                string objectLabel = FrameDebuggerObjectLabel(obj);
                string pass = infoName.Invoke(null, new object[] { i }) as string ?? string.Empty;
                bool draw = FrameDebuggerDrawTypes.Contains(type);
                run.EventTypes[i] = type;
                run.EventObjects[i] = objectLabel;
                IncrementCount(byType, type);
                IncrementCount(byPass, pass);
                int[] counts;
                if (!byObject.TryGetValue(objectLabel, out counts)) byObject[objectLabel] = counts = new int[2];
                counts[0]++;
                if (draw)
                {
                    counts[1]++;
                    draws++;
                    IncrementCount(byGroup, FrameDebuggerGroupLabel(objectLabel));
                    if (run.DetailIndices.Count < run.DetailsWanted && (run.Filter.Length == 0
                        || objectLabel.IndexOf(run.Filter, StringComparison.OrdinalIgnoreCase) >= 0
                        || pass.IndexOf(run.Filter, StringComparison.OrdinalIgnoreCase) >= 0)) run.DetailIndices.Add(i);
                }
            }

            var sb = new StringBuilder();
            sb.Append("\"eventCount\":").Append(events.Length).Append(",\"drawEvents\":").Append(draws).Append(",\"byType\":");
            AppendCountGroups(sb, byType, "type");
            sb.Append(",\"byObject\":[");
            var objects = new List<KeyValuePair<string, int[]>>(byObject);
            objects.Sort((a, b) => b.Value[1] != a.Value[1] ? b.Value[1].CompareTo(a.Value[1]) : b.Value[0].CompareTo(a.Value[0]));
            int limit = Math.Min(FRAME_DEBUGGER_GROUPS_MAX, objects.Count);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"object\":").Append(JsonQuote(objects[i].Key)).Append(",\"events\":").Append(objects[i].Value[0]).Append(",\"drawEvents\":").Append(objects[i].Value[1]).Append('}');
            }

            if (objects.Count > limit)
            {
                int restEvents = 0, restDraws = 0;
                for (int i = limit; i < objects.Count; i++)
                {
                    restEvents += objects[i].Value[0];
                    restDraws += objects[i].Value[1];
                }

                sb.Append(",{\"object\":").Append(JsonQuote("(" + (objects.Count - limit) + " more)")).Append(",\"events\":").Append(restEvents).Append(",\"drawEvents\":").Append(restDraws).Append('}');
            }

            sb.Append("],\"byGroup\":");
            AppendCountGroups(sb, byGroup, "group");
            sb.Append(",\"objectsDrawn\":").Append(objects.Count).Append(",\"byPass\":");
            AppendCountGroups(sb, byPass, "pass");
            run.EventsJson = sb.ToString();
        }

        /// <summary>Appends one event's details and counts its batch-break reason.</summary>
        /// <param name="run">The run.</param>
        /// <param name="index">Event index.</param>
        /// <param name="data">The event data filled by the Frame Debugger.</param>
        private static void FrameDebuggerAppendDetail(FrameDebuggerCaptureRun run, int index, object data)
        {
            Type dataType = data.GetType();
            Func<string, object> field = name =>
            {
                FieldInfo info = dataType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return info != null ? info.GetValue(data) : null;
            };

            int cause = Convert.ToInt32(field("m_BatchBreakCause") ?? 0);
            string causeText = run.BreakCauseNames != null && cause >= 0 && cause < run.BreakCauseNames.Length ? run.BreakCauseNames[cause] : "cause " + cause;
            IncrementCount(run.BreakCauses, causeText);
            if (run.DetailsRead++ > 0) run.DetailsJson.Append(',');
            run.DetailsJson.Append("{\"index\":").Append(index)
                .Append(",\"type\":").Append(JsonQuote(run.EventTypes[index]))
                .Append(",\"object\":").Append(JsonQuote((string)run.EventObjects[index]))
                .Append(",\"shader\":").Append(JsonQuote(Convert.ToString(field("m_RealShaderName"))))
                .Append(",\"pass\":").Append(JsonQuote(Convert.ToString(field("m_PassName"))))
                .Append(",\"lightMode\":").Append(JsonQuote(Convert.ToString(field("m_PassLightMode"))))
                .Append(",\"vertices\":").Append(Convert.ToInt32(field("m_VertexCount") ?? 0))
                .Append(",\"indices\":").Append(Convert.ToInt32(field("m_IndexCount") ?? 0))
                .Append(",\"instances\":").Append(Convert.ToInt32(field("m_InstanceCount") ?? 0))
                .Append(",\"drawCalls\":").Append(Convert.ToInt32(field("m_DrawCallCount") ?? 0))
                .Append(",\"renderTarget\":").Append(JsonQuote(Convert.ToString(field("m_RenderTargetName"))))
                .Append(",\"batchBreak\":").Append(JsonQuote(causeText)).Append('}');
        }

        /// <summary>
        /// Ends the capture: switches the Frame Debugger back off unless it was on before, closes the window it opened,
        /// hands the foreground back, and completes or fails the job with what happened.
        /// </summary>
        /// <param name="run">The run.</param>
        /// <param name="failure">Why the capture stopped early, or null.</param>
        private static void FrameDebuggerEnd(FrameDebuggerCaptureRun run, string failure)
        {
            EditorApplication.update -= run.Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= run.BeforeReload;
            if (ReferenceEquals(_frameDebuggerRun, run)) _frameDebuggerRun = null;

            string cleanup;
            try
            {
                if (!run.WasEnabled) FrameDebuggerCall("SetEnabled", false, ProfilerDriver.connectedProfiler);
                int countAfter = (int)FrameDebuggerGet("count");
                if (run.PausedForCapture) EditorApplication.isPaused = false;

                // Whether the capture opened the window is decided before closing it: a closed window compares equal to null.
                bool openedWindow = !ReferenceEquals(run.OpenedWindow, null);
                if (openedWindow && run.OpenedWindow != null) run.OpenedWindow.Close();
                bool windowClosed = openedWindow && run.OpenedWindow == null;
                bool handedBack = false;
                if (run.BroughtToFront && run.PreviousForeground != IntPtr.Zero)
                {
                    EditorForeground.Bring(run.PreviousForeground);
                    handedBack = EditorForeground.Current() == run.PreviousForeground;
                }

                cleanup = "{\"frameDebugger\":" + JsonQuote(run.WasEnabled ? "was already on, left on" : countAfter == 0 ? "switched back off, read back" : "still reports " + countAfter + " events after switching off")
                    + ",\"window\":" + JsonQuote(!openedWindow ? "was already open, left open" : windowClosed ? "opened unfocused for the capture, closed again, read back" : "opened for the capture; closing it did not take, it is still open")
                    + ",\"foreground\":" + JsonQuote(!run.BroughtToFront ? "the editor was already active, nothing to hand back" : handedBack ? "editor brought forward for the capture, handed back to the previous window, read back" : "editor brought forward; handing back to the previous window did not take")
                    + ",\"playMode\":" + JsonQuote(!run.PausedForCapture ? "left as found" : EditorApplication.isPaused ? "paused for the capture; resuming did not take, it is still paused" : "paused for the capture, resumed, read back")
                    + "}";
            }
            catch (Exception ex)
            {
                cleanup = "{\"error\":" + JsonQuote("Putting the editor back failed: " + ex.Message) + "}";
            }

            string error;
            if (failure != null)
            {
                ConduitJobLedger.Fail(run.JobId, "frame_debugger_capture_failed", failure + " Cleanup: " + cleanup, out error);
                return;
            }

            // Fewer details than asked for is said outright, so an empty list never passes for a frame with nothing to show.
            string detailsNote = run.DetailsWanted == 0 || run.DetailIndices.Count >= run.DetailsWanted ? null
                : run.DetailIndices.Count == 0
                    ? (run.Filter.Length > 0 ? "No draw event's object or pass contains '" + run.Filter + "', so no details were read." : "The frame has no draw events, so no details were read.")
                    : "Only " + run.DetailIndices.Count + " draw event(s) " + (run.Filter.Length > 0 ? "match '" + run.Filter + "'" : "exist") + ", so " + run.DetailIndices.Count + " of the " + run.DetailsWanted + " asked for were read.";

            var sb = new StringBuilder();
            sb.Append('{').Append(run.EventsJson)
                .Append(",\"detailsRead\":").Append(run.DetailsRead);
            if (detailsNote != null) sb.Append(",\"detailsNote\":").Append(JsonQuote(detailsNote));
            sb
                .Append(",\"details\":[").Append(run.DetailsJson).Append("],\"batchBreaks\":");
            AppendCountGroups(sb, run.BreakCauses, "reason");
            sb.Append(",\"cleanup\":").Append(cleanup)
                .Append(",\"note\":\"One frame, rendered by whichever view the editor drew (the Game view in play mode). An object label is its hierarchy path; (none) is an event with no object, such as a clear. Each detail needs a rendered frame of its own, so many details take a moment.\"}");
            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (ConduitJobLedger.Complete(run.JobId, "command_completed", sb.ToString(), out error)) break;
            }
        }

        /// <summary>Labels the object behind a Frame Debugger event by its hierarchy path.</summary>
        /// <param name="obj">The event's object.</param>
        /// <returns>The path, the component's path and type, the object's name, or "(none)".</returns>
        private static string FrameDebuggerObjectLabel(UnityEngine.Object obj)
        {
            if (obj == null) return "(none)";
            var gameObject = obj as GameObject;
            if (gameObject != null) return GetGameObjectPath(gameObject);
            var component = obj as Component;
            if (component != null) return GetGameObjectPath(component.gameObject) + " (" + component.GetType().Name + ")";
            return obj.name + " (" + obj.GetType().Name + ")";
        }

        /// <summary>
        /// Groups an event's object label by its top two hierarchy levels, such as /Systems/Traffic, so the draw events of
        /// a system or place add up in one entry however many objects it draws.
        /// </summary>
        /// <param name="objectLabel">Label from <see cref="FrameDebuggerObjectLabel"/>.</param>
        /// <returns>The first two path levels, or the label itself when it is not a hierarchy path.</returns>
        private static string FrameDebuggerGroupLabel(string objectLabel)
        {
            if (!objectLabel.StartsWith("/", StringComparison.Ordinal)) return objectLabel;

            int typeAt = objectLabel.IndexOf(" (", StringComparison.Ordinal);
            string path = typeAt > 0 ? objectLabel.Substring(0, typeAt) : objectLabel;
            int second = path.IndexOf('/', 1);
            if (second < 0) return path;

            int third = path.IndexOf('/', second + 1);
            return third < 0 ? path : path.Substring(0, third);
        }

        /// <summary>The Frame Debugger utility type, internal in Unity 2022.</summary>
        /// <returns>The type, or null.</returns>
        private static Type FrameDebuggerUtilityType()
        {
            return typeof(Editor).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility");
        }

        /// <summary>The Frame Debugger event data type, internal in Unity 2022.</summary>
        /// <returns>The type, or null.</returns>
        private static Type FrameDebuggerEventDataType()
        {
            return typeof(Editor).Assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData");
        }

        /// <summary>Reads a static property of the Frame Debugger utility.</summary>
        /// <param name="name">Property name.</param>
        /// <returns>Its value.</returns>
        private static object FrameDebuggerGet(string name)
        {
            return FrameDebuggerUtilityType().GetProperty(name, BindingFlags.Static | BindingFlags.Public).GetValue(null);
        }

        /// <summary>Writes a static property of the Frame Debugger utility.</summary>
        /// <param name="name">Property name.</param>
        /// <param name="value">Value to write.</param>
        private static void FrameDebuggerSet(string name, object value)
        {
            FrameDebuggerUtilityType().GetProperty(name, BindingFlags.Static | BindingFlags.Public).SetValue(null, value);
        }

        /// <summary>Calls a static method of the Frame Debugger utility.</summary>
        /// <param name="name">Method name.</param>
        /// <param name="args">Arguments.</param>
        private static void FrameDebuggerCall(string name, params object[] args)
        {
            FrameDebuggerUtilityType().GetMethod(name, BindingFlags.Static | BindingFlags.Public).Invoke(null, args);
        }

        /// <summary>Adds one to a named count.</summary>
        /// <param name="counts">The counts.</param>
        /// <param name="key">The name.</param>
        private static void IncrementCount(Dictionary<string, int> counts, string key)
        {
            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
        }

        /// <summary>
        /// Appends named counts as a JSON array, largest first, capped at <see cref="FRAME_DEBUGGER_GROUPS_MAX"/>; past the
        /// cap, one last entry named "(N more)" carries the rest of the count, so nothing is dropped unseen.
        /// </summary>
        /// <param name="sb">Builder appended to.</param>
        /// <param name="counts">The counts.</param>
        /// <param name="nameKey">JSON key for each name.</param>
        private static void AppendCountGroups(StringBuilder sb, Dictionary<string, int> counts, string nameKey)
        {
            var ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
            sb.Append('[');
            int limit = Math.Min(FRAME_DEBUGGER_GROUPS_MAX, ordered.Count);
            for (int i = 0; i < limit; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{').Append(JsonQuote(nameKey)).Append(':').Append(JsonQuote(ordered[i].Key)).Append(",\"count\":").Append(ordered[i].Value).Append('}');
            }

            if (ordered.Count > limit)
            {
                int rest = 0;
                for (int i = limit; i < ordered.Count; i++) rest += ordered[i].Value;
                sb.Append(",{").Append(JsonQuote(nameKey)).Append(':').Append(JsonQuote("(" + (ordered.Count - limit) + " more)")).Append(",\"count\":").Append(rest).Append('}');
            }

            sb.Append(']');
        }

        #endregion

        #region Dispatch

        /// <summary>Routes the profiler data and Frame Debugger verbs.</summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchProfilerData(string verb, string command, string[] parts)
        {
            string args = GetCommandRemainder(command, parts[0]);
            switch (verb)
            {
                case "profiler-frames": return ProfilerFramesCommand(args);
                case "profiler-compare": return ProfilerCompareCommand(args);
                case "profiler-mark": return ProfilerMarkCommand(args);
                case "profiler-marks": return ProfilerMarksCommand(args);
                case "profiler-threads": return ProfilerThreadsCommand(args);
                case "profiler-counters": return ProfilerCountersCommand(args);
                case "profiler-markers": return ProfilerMarkersCommand(args);
                case "profiler-marker-timeline": return ProfilerMarkerTimelineCommand(args);
                case "profiler-settings": return ProfilerSettingsCommand(args);
                case "profiler-isolate": return ProfilerIsolateCommand(args);
                case "profiler-wait-settled": return ProfilerWaitSettledCommand(args);
                case "profiler-memory-objects": return ProfilerMemoryObjectsCommand(args);
                case "frame-debugger-capture": return FrameDebuggerCaptureCommand(args);
                default: return null;
            }
        }

        #endregion
    }
}
