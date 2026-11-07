/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Conduit transport: an HttpListener hosted by the Editor. It is the only way a command reaches Unity.
//
// Two ways in. A small allowlist of non-deferring verbs runs immediately and answers on the same request.
// Everything else is admitted to the durable job ledger. Raw /command answers with a job id for later
// collection from /job/status. MCP tools/call waits for the terminal record; when work reloads the managed
// domain, its accepted socket is transferred to the replacement domain and the same call returns the result.
//
// The allowlist is not caution. A command arriving here has no response path, so a deferring verb executed
// immediately would return before its compile settled, and answering before the work lands is the defect
// class this admission model exists to prevent. Durable admission is what makes deferral safe here, and it
// also makes a retry safe: a client that loses the reply and asks again resolves onto the same job rather
// than doing the work twice.
//
// Every response on every endpoint is a JSON envelope carrying operation and outcome. A client never parses
// plain text from an endpoint that also returns JSON, and an empty body is never a success.
//
// This file IS the ingress. Without it the editor accepts no commands at all.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Hosts the HTTP transport for this project's Editor. The listener accepts on a background thread and
    /// marshals every command onto the main thread, because almost nothing in the Unity API is callable from
    /// anywhere else.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConduitHttpTransport
    {
        #region Constants

        /// <summary>Lowest port the transport will bind. The range is private and unassigned by IANA.</summary>
        private const int PORT_RANGE_START = 27200;

        /// <summary>One past the highest port the transport will bind.</summary>
        private const int PORT_RANGE_END = 28000;

        /// <summary>How many ports to try past the derived one before giving up.</summary>
        private const int PORT_PROBE_LIMIT = 32;

        /// <summary>Largest command body accepted, in bytes. A bridge command line is small.</summary>
        private const int MAX_BODY_BYTES = 262144;

        /// <summary>How much of an over-limit body to read and discard before giving up on a clean reply.</summary>
        private const int MAX_DRAIN_BYTES = 4 * MAX_BODY_BYTES;

        /// <summary>How many requests may wait for a main-thread tick before the transport sheds load.</summary>
        private const int MAX_PENDING = 64;

        /// <summary>Length of a minted bearer token, used to sanity-check a persisted one.</summary>
        private const int TOKEN_LENGTH = 32;

        /// <summary>
        /// How often a held stream is written to. This is also how quickly a departed client is noticed, so
        /// it is the resolution of the window's attached state rather than only a connection-keeping detail.
        /// </summary>
        private const int STREAM_KEEPALIVE_MS = 5000;

        /// <summary>Encoding revision recorded against every durable admission from this transport.</summary>
        private const string ENCODING_REVISION = "command-v1";

        /// <summary>
        /// Longest the teardown waits for parked callers to write their answers before the port is released.
        /// A response that loses that race reaches the client as a dropped connection, so the wait is what
        /// makes the outcome deliberate and bounded instead of a matter of timing.
        /// </summary>
        private const int RESPONSE_FLUSH_MS = 2000;

        #endregion

        #region Private state

        /// <summary>
        /// Verbs that run immediately on the request that asked for them. None can defer, and none writes a
        /// caller-supplied path, so none needs a durable record to be answerable.
        /// </summary>
        private static readonly HashSet<string> _immediateVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "ping", "help", "commands", "project-info", "status", "unity-tool-list",
            "tail", "head", "entry", "grep", "summary", "console-get-logs",
            "scene-tree", "scene-list-opened", "scene-get-data", "scene-tool-state",
            "gameobject-tree", "gameobject-find", "find-object", "find-by-component",
            "gameobject-component-list", "gameobject-component-list-all", "gameobject-component-get",
            "assets-find", "assets-find-built-in", "assets-find-references", "assets-get-data",
            "assets-importer-get", "assets-shader-list-all", "assets-shader-get-data",
            "object-get-data", "type-find", "type-get-json-schema", "reflection-method-find",
            "udon-program", "udon-program-audit",
            "animator-get", "animation-clip-get", "unity-event-get",
            "editor-window-list", "editor-window-controls", "editor-window-overlays",
            "editor-selection-get", "editor-application-get-state", "scene-view-state",
            "package-list", "package-search", "vrc-player-state",
            "profiler-get-status", "profiler-get-memory-stats", "profiler-get-rendering-stats",
            "profiler-get-script-stats", "profiler-list-modules",
        };

        /// <summary>
        /// Verbs refused outright rather than admitted. Empty: the editor lifecycle verbs are job-aware now.
        /// Play and stop park their job WAITING for the reload they cause and are finished by the editor_state
        /// arm of ExecuteJob; pause and resume raise no reload, so their job stays running and the lifecycle
        /// callback completes it against the ledger.
        /// </summary>
        private static readonly HashSet<string> _blockedVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
        };

        /// <summary>Queue of accepted immediate requests waiting for a main-thread tick.</summary>
        private static readonly ConcurrentQueue<PendingRequest> _pending = new ConcurrentQueue<PendingRequest>();

        /// <summary>Directory holding this project's transport descriptor.</summary>
        private static readonly string _descriptorDir = Path.Combine("Library", "ItsRenji", "ConduitTransport");

        /// <summary>Depth of the pending queue, counted separately so the bound never walks the queue.</summary>
        private static int _pendingCount;

        /// <summary>
        /// Bearer per client, keyed by the window's client display name. The shared bearer proves only that
        /// SOMEBODY holds the secret, which cannot answer "is THIS client attached" while several are talking
        /// to one editor at once. A bearer issued per client makes every request name its own sender.
        /// </summary>
        private static readonly Dictionary<string, string> _clientTokens =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Non-POST requests seen on /mcp since this domain loaded, which are the SSE and session-end verbs.</summary>
        private static int _nonPostMcp;

        /// <summary>The most recent non-POST /mcp request, as method and path.</summary>
        private static string _lastNonPostMcp;

        /// <summary>The live listener, or null when the transport is down.</summary>
        private static HttpListener _listener;

        /// <summary>Bearer token a caller must present.</summary>
        private static string _token;

        /// <summary>Stable identity of this project, derived from its canonical root.</summary>
        private static string _identity;

        /// <summary>Port actually bound, or 0 when the transport is down.</summary>
        internal static int Port { get; private set; }

        /// <summary>
        /// Project identity a caller must present in the X-Conduit-Project header, or null when the transport
        /// is down. It is derived from the project path and is not a secret, which is why it answers the
        /// routing question rather than the authentication one.
        /// </summary>
        internal static string Identity { get { return _identity; } }

        /// <summary>
        /// Bearer a caller must present, or null when the transport is down. This is the secret half of the
        /// pair, so it belongs in a connection artifact the owner pastes and never in a log line or a report.
        /// </summary>
        internal static string Token { get { return _token; } }

        /// <summary>One accepted immediate request, waiting to be run on the main thread.</summary>
        private sealed class PendingRequest
        {
            /// <summary>The context to answer.</summary>
            public HttpListenerContext Context;

            /// <summary>Raw command line.</summary>
            public string Command;

            /// <summary>
            /// JSON-RPC id when this came from /mcp, or null when it came from raw /command. The two answer
            /// in different shapes, and the queue carries which one so the drain does not have to guess.
            /// </summary>
            public JToken McpId;

            /// <summary>Tool name, for the MCP reply.</summary>
            public string ToolName;

            /// <summary>Verb that actually executes, which decides whether the conventions reminder applies.</summary>
            public string ExecutionVerb;

            /// <summary>
            /// Authenticated client name, carried so a discovery answer can withhold a harness-gated tool
            /// exactly as tools/list withholds it. Null means unknown, which withholds every gated tool.
            /// </summary>
            public string Caller;
        }

        #endregion

        #region Lifecycle

        /// <summary>
        /// Starts the transport for this domain and arranges its shutdown.
        ///
        /// Only an interactive Editor serves. Unity launches asset import workers as extra Editor processes
        /// with -batchMode, and they load these same assemblies, so this constructor runs in every one of
        /// them. Left unguarded each worker bound its own port and republished this project's descriptor over
        /// the real Editor's - pointing every client at a worker whose ledger never leaves "starting", while
        /// the Editor itself sat healthy on the port the clients had originally been given.
        ///
        /// An Editor that no human is driving has nobody to serve, so batch mode does not start a listener and
        /// does not touch the descriptor.
        /// </summary>
        static ConduitHttpTransport()
        {
            if (Application.isBatchMode) return;

            AssemblyReloadEvents.beforeAssemblyReload -= ShutdownForReload;
            AssemblyReloadEvents.beforeAssemblyReload += ShutdownForReload;
            EditorApplication.quitting -= Shutdown;
            EditorApplication.quitting += Shutdown;

            EditorApplication.update -= Drain;
            EditorApplication.update += Drain;

            Start();
        }

        /// <summary>Binds the listener, preferring the port a previous run used.</summary>
        private static void Start()
        {
            if (_listener != null) return;

            _identity = IdentityOf(CanonicalProjectRoot());

            // The bearer is persisted rather than minted per domain. A domain reload is precisely the moment
            // a client has to recover, and rotating its credential then is the one time it cannot.
            _token = ReadPersistedToken() ?? Guid.NewGuid().ToString("N");
            LoadClientTokens();
            LoadPresented();
            RestoreStreams();
            ConduitMcpEndpoint.RestoreClientProfiles();

            // Read before this domain overwrites it below. Whether the catalogue changed across the reload is
            // knowable only here and only from the file: every static that could have remembered the old
            // revision was reset by the same reload that may have changed it.
            var previousRevision = ReadDescriptorField("catalogueRevision");

            // The catalogue is built before the listener accepts anything. A worker thread cannot ask the
            // bridge what verbs exist, so discovering them lazily on the first request would mean marshalling
            // mid-request; doing it here costs nothing because the main thread is already ours.
            ConduitMcpCatalog.Publish();

            foreach (var port in PortCandidates())
            {
                var listener = new HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
                try
                {
                    listener.Start();
                    _listener = listener;
                    Port = port;
                    break;
                }
                catch
                {
                    // A candidate that cannot bind is closed here. Leaving it undisposed leaks a handle per
                    // probe, and the probe loop is the one place a bind failure is expected.
                    try { listener.Close(); } catch { }
                }
            }

            if (_listener == null)
            {
                // Silent by design. An unbound transport shows as such in the Conduit window, and the file
                // bridge is unaffected, so there is nothing a console message would tell anyone first.
                return;
            }

            PublishDescriptor();
            AnnounceCatalogueIfChanged(previousRevision);
            try { _listener.BeginGetContext(OnContext, _listener); } catch { }
        }

        /// <summary>
        /// Releases the port before the domain goes away, so the next domain can bind it.
        ///
        /// Eligible MCP callers transfer their accepted sockets to the replacement domain. Every other caller
        /// is woken and given a bounded moment to answer through the durable-job fallback before the listener
        /// closes, so a capture failure still produces a deliberate result instead of a timing-dependent drop.
        /// </summary>
        private static void ShutdownForReload()
        {
            Shutdown(true);
        }

        /// <summary>Closes the listener and every retained response when the Editor process is quitting.</summary>
        private static void Shutdown()
        {
            Shutdown(false);
        }

        /// <summary>Stops the listener while either preserving or closing deferred MCP response sockets.</summary>
        /// <param name="preserveDeferredResponses">True for a managed-domain reload; false for process exit.</param>
        private static void Shutdown(bool preserveDeferredResponses)
        {
            var listener = _listener;
            _listener = null;
            Port = 0;
            if (listener == null) return;

            ConduitReloadResponseHandoff.Trace("transport shutdown; preserve=" + preserveDeferredResponses);

            CloseAllStreams(preserveDeferredResponses);

            // Saved beside the streams and on the same condition, because a profile describes one of those
            // streams. Deliberately NOT inside CloseAllStreams: that body runs under the stream lock, and
            // taking the profile lock inside it would be the only place in the transport where the two are
            // held at once, which is what makes a future caller taking them the other way round a deadlock.
            if (preserveDeferredResponses) ConduitMcpEndpoint.PersistClientProfilesForReload();

            // Mono's HttpListener force-shuts every accepted Socket during Close. A deferred MCP call whose
            // honest answer only exists in the next managed domain gets one process-local duplicate detached
            // first; ordinary shutdown still closes everything normally.
            if (preserveDeferredResponses) ConduitReloadResponseHandoff.CaptureForReload();
            else ConduitReloadResponseHandoff.CloseAll();

            ConduitJobLedger.ReleaseWaitersForReload();
            ConduitJobLedger.WaitForParkedCallersToDrain(RESPONSE_FLUSH_MS);

            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            ConduitReloadResponseHandoff.Trace("transport listener closed; preserve=" + preserveDeferredResponses);
        }

        #endregion

        #region Event streams

        /// <summary>One held-open server-to-client stream, and the client holding it.</summary>
        private sealed class EventStream
        {
            /// <summary>Client display name whose bearer authenticated the request that opened this.</summary>
            public string Client;

            /// <summary>Whether this is a modern subscriptions/listen stream rather than a legacy GET stream.</summary>
            public bool Modern;

            /// <summary>Whether the modern client opted into tool-list change notifications.</summary>
            public bool ToolsListChanged;

            /// <summary>
            /// The notification set the client asked for, verbatim, so a kind it requested and this server
            /// did not honour is visible rather than silently absent. A subscription is an opt-in filter, and
            /// without this the only record of what was asked for is what happened to be implemented.
            /// </summary>
            public string Requested;

            /// <summary>Typed subscriptions/listen id, used to correlate every modern stream frame.</summary>
            public JToken RequestId;

            /// <summary>
            /// Process-local handle to the accepted connection, taken out of Mono's hands when the stream
            /// opened. Held raw rather than as an HttpListenerResponse because closing the listener force-shuts
            /// every socket it still owns, and a stream that dies on every script reload reports a disconnect
            /// that did not happen.
            /// </summary>
            public IntPtr Socket;

            /// <summary>
            /// Harness session this stream delivers notices TO, or empty when it is a protocol stream.
            /// </summary>
            /// <remarks>
            /// Set only for a stream opened by a harness's in-process plugin on behalf of one of its
            /// sessions. It is what makes such a stream addressable by session rather than by client, which
            /// a protocol stream can never be: a client's calls each mint their own synthetic session, so
            /// the conversation that started the work is not recoverable from the transport at all.
            /// <para>
            /// It also EXCLUDES the stream from protocol notifications. Those are addressed by client, and
            /// every one of this client's streams would otherwise match - sending a subscription frame down
            /// a delivery stream puts a JSON-RPC notification the plugin never subscribed to in front of it,
            /// and a frame carrying an unknown subscription id is an error at the receiver.
            /// </para>
            /// </remarks>
            public string AgentSession;
        }

        /// <summary>
        /// Key prefix for a harness session's delivery stream, keeping it from colliding with an MCP
        /// session id in the same table.
        /// </summary>
        private const string AGENT_STREAM_PREFIX = "agent:";

        /// <summary>Streams currently held open, keyed by the MCP session that opened each.</summary>
        private static readonly Dictionary<string, EventStream> _streams =
            new Dictionary<string, EventStream>(StringComparer.Ordinal);

        /// <summary>Drives the tick that both holds streams open and notices a client that has gone.</summary>
        private static Timer _streamKeepalive;

        /// <summary>How the most recent authenticated request identified itself, for diagnosis on /health.</summary>
        private static string _lastCaller;

        /// <summary>Every request as it arrived, before any check could refuse it, newest last.</summary>
        private static readonly List<string> _arrivals = new List<string>();

        /// <summary>
        /// File carrying arrivals across a domain reload. A FILE rather than SessionState because every
        /// request is handled on a listener thread, and SessionState is a main-thread editor API: reading it
        /// from here throws, and inside the health handler that throw becomes a 500 on the one endpoint that
        /// has to keep answering while the editor is busy.
        /// </summary>
        private static string ArrivalsPath
        {
            get { return Path.Combine(StateDir, _identity + ".arrivals.log"); }
        }

        /// <summary>
        /// Records one request before authentication, so a refused request is still visible. Kept across the
        /// reload because what a client does ACROSS one is the question being asked.
        /// </summary>
        /// <param name="method">HTTP method as received.</param>
        /// <param name="path">Request path as received.</param>
        private static void RecordArrival(string method, string path)
        {
            if (string.Equals(method, "POST", StringComparison.Ordinal)) return;

            lock (_arrivals)
            {
                try
                {
                    Directory.CreateDirectory(StateDir);
                    File.AppendAllText(
                        ArrivalsPath,
                        DateTime.Now.ToString("HH:mm:ss") + " " + method + " " + path + "\n",
                        new UTF8Encoding(false));
                }
                catch { /* Diagnostics only - never let recording break the request path. */ }
            }
        }

        /// <summary>Arrivals recorded by this and previous domains, for diagnosis on /health.</summary>
        private static JArray Arrivals
        {
            get
            {
                try
                {
                    if (!File.Exists(ArrivalsPath)) return new JArray();

                    var lines = File.ReadAllLines(ArrivalsPath);
                    var take = Math.Min(lines.Length, 24);
                    var tail = new string[take];
                    Array.Copy(lines, lines.Length - take, tail, 0, take);
                    return new JArray(tail);
                }
                catch
                {
                    return new JArray();
                }
            }
        }

        /// <summary>Recent non-POST /mcp requests as method and caller, newest last, for diagnosis on /health.</summary>
        private static readonly List<string> _nonPostLog = new List<string>();

        /// <summary>Records one non-POST request against the client that sent it.</summary>
        /// <param name="method">HTTP method received.</param>
        /// <param name="caller">Client the bearer identified, or empty for the shared bearer.</param>
        private static void RecordNonPost(string method, string caller)
        {
            Interlocked.Increment(ref _nonPostMcp);
            _lastNonPostMcp = method + " as '" + caller + "'";

            lock (_nonPostLog)
            {
                _nonPostLog.Add(_lastNonPostMcp);
                if (_nonPostLog.Count > 12) _nonPostLog.RemoveAt(0);
            }
        }

        /// <summary>The recent non-POST requests, for diagnosis on /health.</summary>
        private static JArray NonPostLog
        {
            get { lock (_nonPostLog) { return new JArray(_nonPostLog.ToArray()); } }
        }

        /// <summary>
        /// Whether the editor update loop is ticking, and how long ago it last did.
        ///
        /// Health is answered on the listener thread precisely so it stays truthful while the editor is busy,
        /// but that means it cannot ask Unity anything: every lifecycle property is main-thread only, which is
        /// why the status verb queues behind the work being asked about and times out. The bridge already
        /// writes its state to a file once per update tick, so reading that file here reports the editor
        /// through the one channel a blocked main thread cannot close.
        ///
        /// This answers one question, whether the loop is running, and deliberately reports no durations. A
        /// tick that stops because the main thread is blocked also stops any counter carried in it, so such a
        /// figure freezes at the moment it starts mattering and then reads as though the work did. What the
        /// editor is working on comes from Unity's own progress records instead: see
        /// <see cref="EditorActivity"/>.
        /// </summary>
        /// <returns>The last heartbeat line and its age in seconds, or an explicit absence.</returns>
        private static JObject MainThreadHealth()
        {
            try
            {
                if (!File.Exists(ConduitBridge.HeartbeatPath))
                {
                    return new JObject { ["state"] = "no heartbeat file" };
                }

                var age = (DateTime.UtcNow - File.GetLastWriteTimeUtc(ConduitBridge.HeartbeatPath)).TotalSeconds;
                var line = File.ReadAllText(ConduitBridge.HeartbeatPath).Trim();

                return new JObject
                {
                    ["lastTick"] = line,
                    ["ageSeconds"] = Math.Round(age, 1),

                    // The tick is about one per second, so a few seconds of slack absorbs ordinary jitter
                    // without calling a healthy editor blocked.
                    ["ticking"] = age < 5d,
                };
            }
            catch (Exception ex)
            {
                return new JObject { ["state"] = "unreadable: " + ex.Message };
            }
        }

        /// <summary>
        /// What Unity reports it is working on, as of the last time Unity reported anything.
        ///
        /// The editor's own progress records are the source. They are captured on the main thread by
        /// <see cref="ConduitEditorActivity"/>, because reading them anywhere else throws, and published as a
        /// finished object that this thread only reads. Unity was measured pushing those records about four
        /// times a second during a compile, naming each phase as it changed, and reporting an import from
        /// inside the import call itself, which is the only channel that sees one at all.
        ///
        /// The age of the capture is reported beside the elapsed times rather than added to them. While work
        /// is live it sits near zero and the elapsed figures are current; when it grows, Unity has stopped
        /// reporting, and that is the one fact a sum would hide.
        /// </summary>
        /// <returns>The tasks Unity last reported, with the age of that report.</returns>
        private static JObject EditorActivity()
        {
            var snapshot = ConduitEditorActivity.Current;
            var tasks = new JArray();

            foreach (var task in snapshot.Tasks)
            {
                var entry = new JObject
                {
                    ["id"] = task.Id,
                    ["name"] = task.Name,
                    ["description"] = task.Description,
                    ["elapsedSeconds"] = Math.Round(task.ElapsedSeconds, 1),
                    ["status"] = task.Status,
                    ["responding"] = task.Responding,
                };

                if (task.ParentId >= 0) entry["parentId"] = task.ParentId;
                if (task.StepLabel.Length > 0) entry["stepLabel"] = task.StepLabel;
                if (task.TotalSteps > 0)
                {
                    entry["step"] = task.CurrentStep;
                    entry["totalSteps"] = task.TotalSteps;
                }

                if (!task.Indefinite && task.Fraction > 0f) entry["fraction"] = Math.Round(task.Fraction, 3);
                if (task.RemainingSeconds >= 0d) entry["remainingSeconds"] = Math.Round(task.RemainingSeconds, 1);

                tasks.Add(entry);
            }

            var activity = new JObject
            {
                ["busy"] = snapshot.Busy,
                ["reloading"] = snapshot.Reloading,
                ["capturedSecondsAgo"] = Math.Round((DateTime.UtcNow - snapshot.CapturedUtc).TotalSeconds, 1),
                ["taskCount"] = snapshot.TaskCount,
                ["tasks"] = tasks,
            };

            // The import duration is computed here from a real fixed start instant, so it keeps growing
            // while the main thread is inside the import. That is the opposite of a task's elapsedSeconds,
            // which is Unity's own figure as of its last report, and the two differ on purpose.
            if (snapshot.Importing != null)
            {
                activity["importing"] = new JObject
                {
                    ["assetPath"] = snapshot.Importing.AssetPath,
                    ["assetsSeen"] = snapshot.Importing.AssetsSeen,
                    ["elapsedSeconds"] = Math.Round((DateTime.UtcNow - snapshot.Importing.StartedUtc).TotalSeconds, 1),
                };
            }

            if (snapshot.State != null)
            {
                activity["lifecycle"] = new JObject
                {
                    ["isPlaying"] = snapshot.State.Playing,
                    ["isPlayingOrWillChangePlaymode"] = snapshot.State.WillChangePlaymode,
                    ["isPaused"] = snapshot.State.Paused,
                    ["isCompiling"] = snapshot.State.Compiling,
                    ["isUpdating"] = snapshot.State.Updating,

                    // Not captured, because it needs no capturing: the marker's existence IS the state, and
                    // a file test is answerable from this thread as readily as from the main one.
                    ["isUdonSharpCreatePending"] = File.Exists(ConduitBridge.PendingUdonSharpCreatePath),
                };
            }

            if (!string.IsNullOrEmpty(snapshot.Fault)) activity["fault"] = snapshot.Fault;
            return activity;
        }

        /// <summary>Streams held open right now.</summary>
        private static int StreamCount
        {
            get { lock (_streams) { return _streams.Count; } }
        }

        /// <summary>How many per-client bearers this domain loaded, which is zero when the map did not read.</summary>
        private static int KnownClientTokenCount
        {
            get { lock (_clientTokens) { return _clientTokens.Count; } }
        }

        /// <summary>
        /// Clients holding a stream right now. Read at the moment it is asked for, which is the whole point:
        /// a name is here only while a socket that client authenticated is still open, so a caller gets a fact
        /// rather than a remembered timestamp that decays into a guess.
        ///
        /// These are the names a BEARER resolves to, de-duplicated: one name per connection entry Conduit
        /// issued, present only while a stream that entry authenticated is open. It answers whether a given
        /// card is attached, which is the question the window asks of it, and nothing beyond that. It is not
        /// a client's own name for itself, and one entry serving more than one harness reports a single name
        /// for all of them. StreamDetail carries what each client declared and the era it negotiated.
        /// </summary>
        /// <returns>Client display names with a live stream.</returns>
        internal static string[] AttachedClients()
        {
            lock (_streams)
            {
                var names = new List<string>();
                foreach (var pair in _streams)
                {
                    var name = pair.Value.Client;
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
                }

                return names.ToArray();
            }
        }

        /// <summary>
        /// One entry per held stream, naming both the client its bearer resolves to and the name that client
        /// declared for itself at initialize.
        ///
        /// Both are reported because they answer different questions. The bearer names the connection entry
        /// that authenticated the stream, which is what the window matches its cards against; the declaration
        /// is the client's own name and version, which is the only thing that separates two harnesses sharing
        /// one entry. A declaration is absent for any stream whose session predates this domain, because the
        /// profiles do not survive a reload, so the protocol era is carried too: it lives on the stream rather
        /// than in a profile and still tells two clients apart once a reload has cleared every name.
        /// </summary>
        /// <returns>One object per stream, in the order the streams are held.</returns>
        internal static JArray StreamDetail()
        {
            List<KeyValuePair<string, EventStream>> held;
            lock (_streams) { held = new List<KeyValuePair<string, EventStream>>(_streams); }

            // Profiles are looked up AFTER the stream lock is released rather than inside it. Holding two
            // locks at once is only safe while nothing ever takes them in the other order, which is a
            // property of every future caller rather than of this one, and a stream entry is not mutated
            // after it is created so reading it out here is sound.
            var detail = new JArray();
            foreach (var pair in held)
            {
                // The session match is exact, so it is tried first. A modern stream can never satisfy it, and
                // the bearer answers for those instead - which is a weaker claim, so the row states which of
                // the two produced the name rather than presenting both as the same fact.
                var matchedBy = "session";
                var profile = ConduitMcpEndpoint.ClientProfileFor(pair.Key);
                if (profile == null)
                {
                    profile = ConduitMcpEndpoint.NewestProfileForEntry(pair.Value.Client);
                    matchedBy = profile == null ? "none" : "bearer";
                }

                detail.Add(new JObject
                {
                    ["bearerClient"] = string.IsNullOrEmpty(pair.Value.Client) ? "(shared bearer)" : pair.Value.Client,
                    ["declaredName"] = profile == null ? null : profile["name"],
                    ["declaredVersion"] = profile == null ? null : profile["version"],
                    ["matchedBy"] = matchedBy,
                    ["modern"] = pair.Value.Modern,
                    ["toolsListChanged"] = pair.Value.ToolsListChanged,
                    ["requested"] = pair.Value.Requested ?? string.Empty,
                });
            }

            return detail;
        }

        /// <summary>
        /// Drops everything held for one session because its client said it is finished with it.
        ///
        /// The stream lock is released before the profile store is touched, for the same reason StreamDetail
        /// does it: the two are never held at once, so no future caller can deadlock them against each other.
        /// </summary>
        /// <param name="session">Session the client asked to end.</param>
        /// <param name="streamClosed">Receives whether a held stream was closed.</param>
        /// <returns>True when a profile was dropped.</returns>
        internal static bool ForgetSession(string session, out bool streamClosed)
        {
            streamClosed = false;
            if (string.IsNullOrEmpty(session)) return false;

            lock (_streams)
            {
                if (_streams.ContainsKey(session))
                {
                    CloseStream(session, false);
                    streamClosed = true;
                }
            }

            return ConduitMcpEndpoint.ForgetClientProfile(session);
        }

        /// <summary>
        /// Serves the stream a client opens with GET. Holding it open is what makes attachment observable at
        /// all: the socket exists for exactly as long as the client does. Every request is still answered on
        /// its own POST; the stream carries only what a client cannot ask for, which is notice that the tool
        /// list it holds has changed.
        /// </summary>
        /// <param name="context">The GET being answered.</param>
        /// <param name="client">Client the bearer identified, or empty for the shared bearer.</param>
        private static void OpenEventStream(HttpListenerContext context, string client)
        {
            var session = context.Request.Headers["Mcp-Session-Id"];
            var key = string.IsNullOrEmpty(session) ? Guid.NewGuid().ToString("N") : session;

            // Taken over before a single byte is written. Serving the stream through the managed response and
            // detaching later would still leave a window where a reload force-shuts it, and the response head
            // is written here by hand for the same reason: no chunked framing to continue correctly from a
            // different managed domain afterwards. Read-until-close is the framing, which is what SSE wants.
            var handle = ConduitReloadResponseHandoff.DetachSocket(context);
            if (handle == IntPtr.Zero)
            {
                // Not a method refusal: the GET was correct and the server failed to take the socket. Saying
                // 405 here tells the client to stop using a route that works, so it never asks for a stream
                // again and the failure is permanent for that connection. The subscription path already
                // reports this as a server fault; this reports it the same way.
                ConduitMcpEndpoint.RespondTransportError(
                    context, 500, "stream_unavailable", "could not take ownership of the response stream");
                return;
            }

            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Cache-Control: no-cache\r\n" +
                "X-Accel-Buffering: no\r\n" +
                "\r\n");

            if (!ConduitReloadResponseHandoff.SendRaw(handle, head)
                || !ConduitReloadResponseHandoff.SendRaw(handle, Encoding.UTF8.GetBytes(": open\n\n")))
            {
                ConduitReloadResponseHandoff.CloseRaw(handle);
                return;
            }

            lock (_streams)
            {
                CloseStream(key, false);
                _streams[key] = new EventStream { Client = client, Modern = false, Socket = handle };
                ArmKeepalive();
            }
        }

        /// <summary>Key one harness session's delivery stream is filed under.</summary>
        /// <param name="sessionId">Harness session id.</param>
        /// <returns>The stream table key.</returns>
        private static string AgentStreamKey(string sessionId)
        {
            return AGENT_STREAM_PREFIX + sessionId;
        }

        /// <summary>
        /// Holds a stream open for a harness's in-process plugin, so notices for one of its sessions have
        /// somewhere to go after that session's own call has already answered.
        /// </summary>
        /// <remarks>
        /// This is the second of the two delivery shapes and the one that needs no address at all. Where a
        /// harness exports a per-session messaging endpoint, the session hands that over and the editor
        /// writes to it directly. Where it does not, the harness's plugin runs INSIDE that harness and
        /// connects outward to here instead, which inverts the problem: nothing has to be discoverable from
        /// the editor, and no platform-specific ingress has to be reimplemented in C#.
        /// <para>
        /// Filed in the same table as the protocol streams deliberately, rather than a parallel one. The
        /// keepalive that holds a stream open and notices a client that has gone, the reload handoff that
        /// keeps the socket across a domain reload, and the close path all already operate on that table;
        /// a second table would need its own copy of each and would drift from this one.
        /// </para>
        /// </remarks>
        /// <param name="context">The GET being answered.</param>
        /// <param name="client">Client the bearer identified.</param>
        /// <param name="sessionId">Harness session this stream delivers to.</param>
        private static void OpenAgentStream(HttpListenerContext context, string client, string sessionId)
        {
            var handle = ConduitReloadResponseHandoff.DetachSocket(context);
            if (handle == IntPtr.Zero)
            {
                // A server-side failure reported as one. Calling it a method or request fault would tell the
                // plugin to stop using a route that works, and it would then never reconnect.
                ConduitMcpEndpoint.RespondTransportError(
                    context, 500, "stream_unavailable", "could not take ownership of the response stream");
                return;
            }

            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Cache-Control: no-cache\r\n" +
                "X-Accel-Buffering: no\r\n" +
                "\r\n");

            // The open frame names the session back. A plugin holding several of these has no other way to
            // tell which one it just got, and confirming the id here is what lets it refuse a mismatch
            // rather than inject one session's build report into another's conversation.
            var opened = new JObject
            {
                ["type"] = "open",
                ["sessionId"] = sessionId,
            };

            if (!ConduitReloadResponseHandoff.SendRaw(handle, head)
                || !ConduitReloadResponseHandoff.SendRaw(
                    handle,
                    Encoding.UTF8.GetBytes("event: message\ndata: " + opened.ToString(Formatting.None) + "\n\n")))
            {
                ConduitReloadResponseHandoff.CloseRaw(handle);
                return;
            }

            lock (_streams)
            {
                // Replaces rather than rejects. A plugin that reconnected after a dropped socket is the
                // common case, and refusing the new stream because a dead one is still filed would leave
                // that session permanently unreachable until the editor restarted.
                var key = AgentStreamKey(sessionId);
                CloseStream(key, false);
                _streams[key] = new EventStream
                {
                    Client = client,
                    Modern = false,
                    Socket = handle,
                    AgentSession = sessionId,
                };
                ArmKeepalive();
            }
        }

        /// <summary>Whether a harness session currently holds a delivery stream.</summary>
        /// <param name="sessionId">Harness session id.</param>
        /// <returns>True when a notice to that session would be attempted.</returns>
        internal static bool HasAgentStream(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;
            lock (_streams) { return _streams.ContainsKey(AgentStreamKey(sessionId)); }
        }

        /// <summary>Every harness session holding a delivery stream, with the client each belongs to.</summary>
        /// <returns>Session id to client display name.</returns>
        internal static List<KeyValuePair<string, string>> AgentStreamSessions()
        {
            var found = new List<KeyValuePair<string, string>>();
            lock (_streams)
            {
                foreach (var pair in _streams)
                {
                    if (string.IsNullOrEmpty(pair.Value.AgentSession)) continue;
                    found.Add(new KeyValuePair<string, string>(
                        pair.Value.AgentSession, pair.Value.Client ?? string.Empty));
                }
            }

            return found;
        }

        /// <summary>Pushes one notice down a harness session's delivery stream.</summary>
        /// <remarks>
        /// A failed write closes the stream rather than leaving it filed. The socket is the only evidence
        /// available that the plugin is still there, so a send that fails IS the disconnect notice, and a
        /// stream kept after it would report a session as reachable for as long as the editor ran.
        /// </remarks>
        /// <param name="sessionId">Harness session to deliver to.</param>
        /// <param name="text">Notice body, already wrapped by the caller.</param>
        /// <param name="error">Receives why delivery did not happen.</param>
        /// <returns>True when the frame was written to the socket.</returns>
        internal static bool SendToAgentSession(string sessionId, string text, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(sessionId)) { error = "no session was named"; return false; }

            var notice = new JObject
            {
                ["type"] = "notice",
                ["sessionId"] = sessionId,
                ["text"] = text ?? string.Empty,
            };
            var frame = Encoding.UTF8.GetBytes(
                "event: message\ndata: " + notice.ToString(Formatting.None) + "\n\n");

            lock (_streams)
            {
                var key = AgentStreamKey(sessionId);
                EventStream stream;
                if (!_streams.TryGetValue(key, out stream))
                {
                    error = "that session holds no delivery stream, so its harness plugin is not connected";
                    return false;
                }

                if (ConduitReloadResponseHandoff.SendRaw(stream.Socket, frame)) return true;

                CloseStream(key, false);
                error = "the session's delivery stream had already closed, so the frame could not be written";
                return false;
            }
        }

        /// <summary>Opens the long-lived POST response used by a modern notification subscription.</summary>
        /// <param name="context">The subscriptions/listen request being answered.</param>
        /// <param name="client">Client whose bearer authenticated the request.</param>
        /// <param name="requestId">Typed JSON-RPC id used as the subscription id.</param>
        /// <param name="requested">Notification filter requested by the client.</param>
        internal static void OpenSubscriptionStream(
            HttpListenerContext context, string client, JToken requestId, JObject requested)
        {
            var handle = ConduitReloadResponseHandoff.DetachSocket(context);
            if (handle == IntPtr.Zero)
            {
                ConduitMcpEndpoint.RespondTransportError(
                    context, 500, "subscription_unavailable", "could not take ownership of the response stream");
                return;
            }

            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Cache-Control: no-cache\r\n" +
                "X-Accel-Buffering: no\r\n" +
                "\r\n");

            var wantsTools = (bool?)requested["toolsListChanged"] == true;
            var honored = new JObject();
            if (wantsTools) honored["toolsListChanged"] = true;

            var acknowledged = new JObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/subscriptions/acknowledged",
                ["params"] = new JObject
                {
                    ["notifications"] = honored,
                    ["_meta"] = new JObject
                    {
                        [ConduitMcpEndpoint.SUBSCRIPTION_ID_META] =
                            requestId == null ? JValue.CreateNull() : requestId.DeepClone(),
                    },
                },
            };
            var frame = Encoding.UTF8.GetBytes(
                "event: message\ndata: " + acknowledged.ToString(Formatting.None) + "\n\n");

            if (!ConduitReloadResponseHandoff.SendRaw(handle, head)
                || !ConduitReloadResponseHandoff.SendRaw(handle, frame))
            {
                ConduitReloadResponseHandoff.CloseRaw(handle);
                return;
            }

            var key = "modern:" + Guid.NewGuid().ToString("N");
            lock (_streams)
            {
                _streams[key] = new EventStream
                {
                    Client = client,
                    Modern = true,
                    ToolsListChanged = wantsTools,
                    Requested = requested == null ? "{}" : requested.ToString(Formatting.None),
                    RequestId = requestId == null ? JValue.CreateNull() : requestId.DeepClone(),
                    Socket = handle,
                };
                ArmKeepalive();
            }
        }

        /// <summary>
        /// Writes one comment frame to every stream. Writing is the only way to learn a peer has gone: a
        /// client that closed its end fails here, and that failure IS the disconnect signal. Nothing else in
        /// this transport can tell a departed client from a quiet one.
        /// </summary>
        private static void Keepalive()
        {
            lock (_streams)
            {
                if (_streams.Count == 0) return;

                var frame = Encoding.UTF8.GetBytes(": keepalive\n\n");
                var dead = new List<string>();

                foreach (var pair in _streams)
                {
                    if (!ConduitReloadResponseHandoff.SendRaw(pair.Value.Socket, frame)) dead.Add(pair.Key);
                }

                foreach (var key in dead) CloseStream(key, false);
            }
        }

        /// <summary>
        /// Tells every attached client the tool list changed, when it did. The comparison is against the
        /// revision the previous domain published rather than anything remembered, because the reload that
        /// changed the catalogue also reset every static that could have remembered it. Nothing is sent when
        /// the revision is unchanged: a compile that touched no signature changes nothing a client holds, and
        /// a notice on every reload would teach a client to ignore them.
        /// </summary>
        /// <param name="previousRevision">Revision the descriptor carried before this domain republished it.</param>
        private static void AnnounceCatalogueIfChanged(string previousRevision)
        {
            _toolListNoticePrevious = previousRevision ?? string.Empty;

            var current = ConduitMcpCatalog.Revision;
            if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(previousRevision)) return;
            if (string.Equals(current, previousRevision, StringComparison.Ordinal)) return;

            AnnounceToolListChanged();
            _toolListNoticeRevision = current;
        }

        /// <summary>
        /// Pushes notifications/tools/list_changed to every attached stream, unconditionally. The automatic
        /// path calls this only when the revision moved; the mcp-refresh-tools verb calls it because a caller
        /// asked, and the count is what that caller can act on.
        /// </summary>
        /// <returns>How many streams accepted the notice.</returns>
        internal static int AnnounceToolListChanged()
        {
            var delivered = BroadcastNotification("notifications/tools/list_changed");
            Interlocked.Increment(ref _toolListNoticesSent);
            _toolListNoticeDelivered = delivered;
            return delivered;
        }

        /// <summary>
        /// Writes one JSON-RPC notification to every open stream, framed as the SSE message event the
        /// Streamable HTTP transport specifies for server-to-client traffic. A client that closed its end
        /// fails the write and is dropped, exactly as the keepalive drops it.
        /// </summary>
        /// <param name="method">Notification method.</param>
        /// <returns>How many streams accepted the frame.</returns>
        private static int BroadcastNotification(string method)
        {
            lock (_streams)
            {
                if (_streams.Count == 0) return 0;

                var delivered = 0;
                var dead = new List<string>();

                foreach (var pair in _streams)
                {
                    var stream = pair.Value;

                    // Excluded for the same reason it is excluded from an addressed send: a delivery stream
                    // is not a protocol stream and never negotiated one. "Every open stream" has meant every
                    // stream a CLIENT opened since this was written, and a plugin's stream is the first that
                    // is not one.
                    if (!string.IsNullOrEmpty(stream.AgentSession)) continue;

                    if (stream.Modern && method == "notifications/tools/list_changed"
                        && !stream.ToolsListChanged) continue;

                    var payload = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
                    if (stream.Modern)
                    {
                        payload["params"] = new JObject
                        {
                            ["_meta"] = new JObject
                            {
                                [ConduitMcpEndpoint.SUBSCRIPTION_ID_META] = stream.RequestId.DeepClone(),
                            },
                        };
                    }

                    var frame = Encoding.UTF8.GetBytes(
                        "event: message\ndata: " + payload.ToString(Formatting.None) + "\n\n");
                    if (ConduitReloadResponseHandoff.SendRaw(stream.Socket, frame)) delivered++;
                    else dead.Add(pair.Key);
                }

                foreach (var key in dead) CloseStream(key, false);
                return delivered;
            }
        }

        /// <summary>
        /// Writes one server-initiated notification down every stream a named client holds, which is how work
        /// that outlives its own tool call reports what happened.
        /// </summary>
        /// <remarks>
        /// Addressed by CLIENT rather than by session, because a session key cannot address a modern caller at
        /// all: its calls each mint a synthetic session, while the stream it genuinely holds is filed under the
        /// id its subscriptions/listen opened with. Broadcasting instead is not the alternative - a notice
        /// about one client's work arriving at every other client is something they cannot attribute, and a
        /// frame carrying a token they never issued is an error at the receiver rather than something to drop.
        /// The shared bearer identifies nobody, so a stream opened under it is never addressed.
        /// </remarks>
        /// <param name="client">Client display name, as the bearer that authenticated the work identified it.</param>
        /// <param name="method">Notification method.</param>
        /// <param name="parameters">Params object, or null for a bare notification.</param>
        /// <returns>How many of that client's streams accepted the frame.</returns>
        internal static int SendToClient(string client, string method, JObject parameters)
        {
            if (string.IsNullOrEmpty(client) || string.IsNullOrEmpty(method)) return 0;

            lock (_streams)
            {
                if (_streams.Count == 0) return 0;

                var delivered = 0;
                var dead = new List<string>();

                foreach (var pair in _streams)
                {
                    var stream = pair.Value;
                    if (string.IsNullOrEmpty(stream.Client)
                        || !string.Equals(stream.Client, client, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // A delivery stream belongs to one session of this client and carries plain notices, not
                    // protocol traffic. It matches the client like any other stream, so it has to be excluded
                    // here by what it IS: a plugin receiving a subscription frame it never asked for reads a
                    // subscription id it never issued, which is an error at the receiver rather than a frame
                    // it discards.
                    if (!string.IsNullOrEmpty(stream.AgentSession)) continue;

                    var payload = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
                    var body = parameters == null ? new JObject() : (JObject)parameters.DeepClone();
                    if (stream.Modern)
                    {
                        body["_meta"] = new JObject
                        {
                            [ConduitMcpEndpoint.SUBSCRIPTION_ID_META] = stream.RequestId.DeepClone(),
                        };
                    }

                    if (body.Count > 0) payload["params"] = body;

                    var frame = Encoding.UTF8.GetBytes(
                        "event: message\ndata: " + payload.ToString(Formatting.None) + "\n\n");
                    if (ConduitReloadResponseHandoff.SendRaw(stream.Socket, frame)) delivered++;
                    else dead.Add(pair.Key);
                }

                foreach (var key in dead) CloseStream(key, false);
                return delivered;
            }
        }

        /// <summary>
        /// Writes one notifications/progress frame for a call that asked for progress, down the stream the
        /// asking session holds.
        ///
        /// Targeted rather than broadcast, and that is not tidiness: a receiver treats a notice carrying a
        /// token it never issued as an error rather than as something to ignore, so sending one to every
        /// stream would manufacture an error in every client except the one that asked.
        ///
        /// The session key answers for a caller that has one. A MODERN caller does not: the protocol
        /// dropped the session id, so its calls carry a synthetic key that matches no stream by
        /// construction, and it is addressed by the client its bearer names instead - the same fallback
        /// <see cref="SendToClient"/> uses, and only ever to a modern, non-delivery stream.
        ///
        /// The message is a status line and never result content. A client is free to render progress, and
        /// one that does must not be handed something that reads as a duplicate of the answer still to come.
        /// </summary>
        /// <param name="session">Session whose stream to write to, when the caller has one.</param>
        /// <param name="client">Client the bearer names, used to address a caller carrying no real session.</param>
        /// <param name="progressToken">Token exactly as the caller sent it, echoed back unchanged.</param>
        /// <param name="progress">Work done so far. Must only ever increase for one token.</param>
        /// <param name="message">Short status line.</param>
        /// <returns>True when a stream accepted the frame.</returns>
        internal static bool SendProgress(
            string session, string client, JToken progressToken, long progress, string message)
        {
            if (progressToken == null) return false;
            if (string.IsNullOrEmpty(session) && string.IsNullOrEmpty(client)) return false;

            lock (_streams)
            {
                var key = session;
                EventStream stream = null;
                if (!string.IsNullOrEmpty(session)) _streams.TryGetValue(session, out stream);

                if (stream == null && !string.IsNullOrEmpty(client))
                {
                    key = ModernStreamKeyFor(client);
                    if (key != null) _streams.TryGetValue(key, out stream);
                }

                if (stream == null)
                {
                    Interlocked.Increment(ref _progressNoticesDropped);
                    return false;
                }

                var body = new JObject
                {
                    ["progressToken"] = progressToken.DeepClone(),
                    ["progress"] = progress,
                    ["message"] = message,
                };

                // A modern stream acknowledges the subscription id it opened with, exactly as a broadcast
                // frame carries one; without it the frame arrives against a subscription the client cannot
                // place.
                if (stream.Modern)
                {
                    body["_meta"] = new JObject
                    {
                        [ConduitMcpEndpoint.SUBSCRIPTION_ID_META] = stream.RequestId.DeepClone(),
                    };
                }

                var payload = new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/progress",
                    ["params"] = body,
                };

                var frame = Encoding.UTF8.GetBytes(
                    "event: message\ndata: " + payload.ToString(Formatting.None) + "\n\n");
                if (ConduitReloadResponseHandoff.SendRaw(stream.Socket, frame))
                {
                    Interlocked.Increment(ref _progressNoticesSent);
                    return true;
                }

                // A client that closed its end is dropped here exactly as the keepalive drops it, and the
                // caller reads the false as its cue to stop trying rather than to fail the call.
                CloseStream(key, false);
                Interlocked.Increment(ref _progressNoticesDropped);
                return false;
            }
        }

        /// <summary>
        /// The one modern stream a client holds, or null when it holds none or SEVERAL. Several is refused
        /// rather than guessed at: the modern protocol carries no session id, so nothing distinguishes which
        /// of that client's sessions issued the token, and handing it to the wrong one is an error at that
        /// receiver rather than a frame it discards. A delivery stream is excluded by what it IS, because a
        /// plugin reading a subscription id it never issued fails the same way. The caller holds the lock.
        /// </summary>
        /// <param name="client">Client display name the bearer resolved to.</param>
        /// <returns>That client's sole modern stream key, or null.</returns>
        private static string ModernStreamKeyFor(string client)
        {
            string found = null;
            foreach (var pair in _streams)
            {
                var candidate = pair.Value;
                if (!candidate.Modern) continue;
                if (!string.IsNullOrEmpty(candidate.AgentSession)) continue;
                if (!string.Equals(candidate.Client, client, StringComparison.Ordinal)) continue;

                if (found != null) return null;
                found = pair.Key;
            }

            return found;
        }

        /// <summary>Closes and forgets one stream. The caller holds the stream lock.</summary>
        /// <param name="key">Session key the stream is filed under.</param>
        /// <param name="keepSocket">
        /// True to forget the stream WITHOUT closing its socket, which is what a reload does: the handle is
        /// handed to the next managed domain, and closing it here would end the very connection the handoff
        /// exists to preserve.
        /// </param>
        private static void CloseStream(string key, bool keepSocket)
        {
            EventStream stream;
            if (!_streams.TryGetValue(key, out stream)) return;

            _streams.Remove(key);
            if (keepSocket) return;

            if (stream.Modern)
            {
                var body = ConduitMcpEndpoint.SubscriptionCompletionJson(stream.RequestId);
                var frame = Encoding.UTF8.GetBytes("event: message\ndata: " + body + "\n\n");
                ConduitReloadResponseHandoff.SendRaw(stream.Socket, frame);
            }

            ConduitReloadResponseHandoff.CloseRaw(stream.Socket);
        }

        /// <summary>Revision the notice sent at this domain's start announced, or empty when none was due.</summary>
        private static string _toolListNoticeRevision = string.Empty;

        /// <summary>Revision the previous domain had published, or empty when this domain found none.</summary>
        private static string _toolListNoticePrevious = string.Empty;

        /// <summary>Streams that accepted the most recent tool-list notice, automatic or on demand.</summary>
        private static int _toolListNoticeDelivered;

        /// <summary>Tool-list notices sent since this domain loaded, automatic and on demand together.</summary>
        private static int _toolListNoticesSent;

        /// <summary>Progress notices a stream accepted since this domain loaded.</summary>
        private static int _progressNoticesSent;

        /// <summary>
        /// Progress notices that reached no stream since this domain loaded. Not an error on its own: a
        /// caller that asked for progress while holding no open stream lands here on its first slice and is
        /// then left alone, so a steady count beside a zero sent count says the stream is the thing missing.
        /// </summary>
        private static int _progressNoticesDropped;

        /// <summary>Editor-session key carrying held stream handles across a domain reload.</summary>
        private const string STREAMS_SESSION_KEY = "ItsRenji.Conduit.Transport.Streams.v1";

        /// <summary>
        /// Releases this domain's hold on the streams.
        ///
        /// A reload KEEPS the sockets: the handles are process-local and the process is not going away, so
        /// they are recorded for the next domain to adopt and the connections never break. Measured, not
        /// assumed: a client opens its stream once when it starts and never asks again, so a stream ended at a
        /// reload stays ended, and the window would report a disconnect that never happened on every compile.
        /// An editor exit closes them properly, because then there is no next domain.
        /// </summary>
        /// <param name="forReload">True when a replacement domain will adopt the handles.</param>
        private static void CloseAllStreams(bool forReload)
        {
            lock (_streams)
            {
                if (forReload) PersistStreamsForReload();

                foreach (var key in new List<string>(_streams.Keys)) CloseStream(key, forReload);

                if (_streamKeepalive == null) return;

                try { _streamKeepalive.Dispose(); } catch { }
                _streamKeepalive = null;
            }
        }

        /// <summary>Records the held handles for the replacement domain. The caller holds the stream lock.</summary>
        private static void PersistStreamsForReload()
        {
            try
            {
                var values = new JArray();
                foreach (var pair in _streams)
                {
                    values.Add(new JObject
                    {
                        ["key"] = pair.Key,
                        ["client"] = pair.Value.Client ?? string.Empty,
                        ["modern"] = pair.Value.Modern,
                        ["toolsListChanged"] = pair.Value.ToolsListChanged,
                        ["requested"] = pair.Value.Requested ?? string.Empty,
                        ["requestId"] = pair.Value.RequestId == null
                            ? JValue.CreateNull()
                            : pair.Value.RequestId.DeepClone(),
                        ["handle"] = pair.Value.Socket.ToInt64(),
                    });
                }

                SessionState.SetString(STREAMS_SESSION_KEY, values.ToString(Newtonsoft.Json.Formatting.None));
            }
            catch
            {
                // A stream that cannot be recorded is simply lost to the reload, which is the old behaviour.
            }
        }

        /// <summary>
        /// Adopts the handles the previous domain held. Run on the main thread at domain load, where the
        /// editor-session store is legal to read; the store is per editor session, so a handle found here
        /// always belongs to this process.
        /// </summary>
        private static void RestoreStreams()
        {
            try
            {
                var encoded = SessionState.GetString(STREAMS_SESSION_KEY, string.Empty);
                if (string.IsNullOrEmpty(encoded)) return;
                SessionState.EraseString(STREAMS_SESSION_KEY);

                lock (_streams)
                {
                    foreach (var token in JArray.Parse(encoded))
                    {
                        var entry = token as JObject;
                        if (entry == null) continue;

                        var key = (string)entry["key"];
                        var handle = new IntPtr((long)entry["handle"]);
                        if (string.IsNullOrEmpty(key) || handle == IntPtr.Zero) continue;

                        _streams[key] = new EventStream
                        {
                            Client = (string)entry["client"],
                            Modern = (bool?)entry["modern"] == true,
                            ToolsListChanged = (bool?)entry["toolsListChanged"] == true,
                            Requested = (string)entry["requested"] ?? string.Empty,
                            RequestId = entry["requestId"] == null
                                ? null
                                : entry["requestId"].DeepClone(),
                            Socket = handle,
                        };
                    }

                    if (_streams.Count > 0) ArmKeepalive();
                }
            }
            catch
            {
                // An unreadable record costs the streams it described and nothing else.
            }
        }

        /// <summary>Starts the keepalive tick if it is not already running. The caller holds the stream lock.</summary>
        private static void ArmKeepalive()
        {
            if (_streamKeepalive != null) return;

            _streamKeepalive = new Timer(delegate { Keepalive(); }, null, STREAM_KEEPALIVE_MS, STREAM_KEEPALIVE_MS);
        }

        #endregion

        #region Project identity and port

        /// <summary>
        /// Canonical form of the project root: absolute, forward slashes, no trailing separator, and case
        /// folded, because Windows treats two spellings of one path as the same directory while a hash
        /// does not.
        /// </summary>
        /// <returns>The canonical root.</returns>
        private static string CanonicalProjectRoot()
        {
            var root = Path.GetFullPath(Directory.GetCurrentDirectory());
            root = root.Replace('\\', '/').TrimEnd('/');
            return root.ToLowerInvariant();
        }

        /// <summary>Stable identity digest of the canonical project root.</summary>
        /// <param name="canonicalRoot">Canonical root path.</param>
        /// <returns>Lowercase hex SHA-256.</returns>
        private static string IdentityOf(string canonicalRoot)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalRoot));
                var text = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        /// <summary>
        /// Ports to try, in order. The persisted one comes first so a client's URL survives a restart; the
        /// derived base only decides where a first run starts looking.
        /// </summary>
        /// <returns>Candidate ports, best first.</returns>
        private static List<int> PortCandidates()
        {
            var candidates = new List<int>();

            var persisted = ReadPersistedPort();
            if (persisted >= PORT_RANGE_START && persisted < PORT_RANGE_END) candidates.Add(persisted);

            var span = PORT_RANGE_END - PORT_RANGE_START;
            var derived = DerivePort(_identity);
            for (var i = 0; i < PORT_PROBE_LIMIT; i++)
            {
                // Probes wrap inside the reserved range rather than walking out of it.
                var port = PORT_RANGE_START + ((derived - PORT_RANGE_START + i) % span);
                if (!candidates.Contains(port)) candidates.Add(port);
            }

            return candidates;
        }

        /// <summary>Port this project prefers, derived from its identity so it is stable across restarts.</summary>
        /// <param name="identity">Project identity digest.</param>
        /// <returns>A port inside the reserved range.</returns>
        private static int DerivePort(string identity)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var c in identity) { hash ^= c; hash *= 16777619u; }
                return PORT_RANGE_START + (int)(hash % (uint)(PORT_RANGE_END - PORT_RANGE_START));
            }
        }

        #endregion

        #region Descriptor

        /// <summary>Path of this project's descriptor.</summary>
        private static string DescriptorPath
        {
            get { return Path.Combine(_descriptorDir, _identity + ".json"); }
        }

        /// <summary>
        /// Absolute path of this project's descriptor, for a client that has to be pointed at it. The console
        /// bridge binds to one exact project by reading this file, so the path is part of its command line and
        /// has to be absolute: the client that launches it does not run from this project's folder.
        /// </summary>
        internal static string DescriptorFile
        {
            get { return Path.GetFullPath(DescriptorPath); }
        }

        /// <summary>Reads one string field out of the published descriptor.</summary>
        /// <param name="field">Field name.</param>
        /// <returns>The value, or null when absent or unreadable.</returns>
        private static string ReadDescriptorField(string field)
        {
            try
            {
                if (!File.Exists(DescriptorPath)) return null;

                var text = File.ReadAllText(DescriptorPath, Encoding.UTF8);
                var marker = "\"" + field + "\":";
                var at = text.IndexOf(marker, StringComparison.Ordinal);
                if (at < 0) return null;

                var start = at + marker.Length;
                while (start < text.Length && (text[start] == ' ' || text[start] == '"')) start++;

                var end = start;
                while (end < text.Length && text[end] != '"' && text[end] != ',' && text[end] != '}') end++;
                return end > start ? text.Substring(start, end - start) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Reads the port a previous run bound, so a client's URL stays valid across restarts.</summary>
        /// <returns>The persisted port, or 0 when there is none.</returns>
        private static int ReadPersistedPort()
        {
            int port;
            var value = ReadDescriptorField("port");
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) ? port : 0;
        }

        /// <summary>Reads the bearer a previous run published, so a domain reload does not rotate it.</summary>
        /// <returns>The persisted token, or null when there is none.</returns>
        private static string ReadPersistedToken()
        {
            var token = ReadDescriptorField("token");
            return token != null && token.Length == TOKEN_LENGTH ? token : null;
        }

        /// <summary>
        /// Folder for this transport's own state files. A SUBFOLDER, not the descriptor directory: a
        /// descriptor reader identifies its file by finding the single `.json` in that directory, so a state
        /// file landing beside it does not read as extra state, it reads as an ambiguous descriptor.
        /// </summary>
        private static string StateDir
        {
            get { return Path.Combine(_descriptorDir, "state"); }
        }

        /// <summary>Path of the per-client bearer map.</summary>
        private static string ClientTokenPath
        {
            get { return Path.Combine(StateDir, _identity + ".clients.json"); }
        }

        /// <summary>Clients observed presenting their own bearer, one name per line.</summary>
        private static string PresentedPath
        {
            get { return Path.Combine(StateDir, _identity + ".presented"); }
        }

        /// <summary>Clients seen presenting their own bearer during this editor session and earlier ones.</summary>
        private static readonly HashSet<string> _presented = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Records that a client has presented the bearer currently issued to it, which proves it has LOADED
        /// the config carrying that bearer. Restarting it after that gains nothing, so this is what decides
        /// whether "restart to connect" is true - not whether a stream happens to be open, which answers a
        /// different question and is false for any client that connected before streams were held.
        /// <para>
        /// A file rather than the editor-session store because this runs on a listener thread, and it must
        /// survive an editor restart: the bearer does, so the fact that a client already loaded it does too.
        /// </para>
        /// </summary>
        /// <param name="client">Client the bearer identified, or empty for the shared bearer.</param>
        private static void NoteClientPresented(string client)
        {
            if (string.IsNullOrEmpty(client)) return;

            lock (_presented)
            {
                if (!_presented.Add(client)) return;

                try
                {
                    Directory.CreateDirectory(StateDir);
                    File.AppendAllText(PresentedPath, client + "\n", new UTF8Encoding(false));
                }
                catch
                {
                    // Losing the record costs one client a "restart to connect" it does not need, never access.
                }
            }
        }

        /// <summary>Reads the clients recorded as having presented their own bearer.</summary>
        private static void LoadPresented()
        {
            try
            {
                if (!File.Exists(PresentedPath)) return;

                lock (_presented)
                {
                    foreach (var line in File.ReadAllLines(PresentedPath))
                    {
                        if (line.Length > 0) _presented.Add(line);
                    }
                }
            }
            catch { /* An unreadable record rebuilds itself on the next request from each client. */ }
        }

        /// <summary>
        /// Whether this client has been observed using the bearer it is currently issued, and has therefore
        /// already loaded the config Conduit last wrote for it.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>True when that client has presented its current bearer.</returns>
        internal static bool HasPresentedCurrentToken(string client)
        {
            lock (_presented) { return _presented.Contains(client); }
        }

        /// <summary>
        /// The bearer written into one client's config, minted the first time that client is asked about.
        /// It is stable for the same reason the shared one is: a config the client has already loaded must
        /// keep working, and rotating a credential is precisely what a running client cannot recover from.
        /// </summary>
        /// <param name="client">Client display name, as the window's cards spell it.</param>
        /// <returns>That client's bearer, or the shared one when no client was named.</returns>
        internal static string TokenForClient(string client)
        {
            if (string.IsNullOrEmpty(client)) return _token;

            lock (_clientTokens)
            {
                string existing;
                if (_clientTokens.TryGetValue(client, out existing) && !string.IsNullOrEmpty(existing))
                {
                    return existing;
                }

                var minted = Guid.NewGuid().ToString("N");
                _clientTokens[client] = minted;
                SaveClientTokens();
                return minted;
            }
        }

        /// <summary>
        /// Resolves the client a request's bearer identifies, for the admission paths that need a return
        /// address. Read from the request rather than passed down the call chain, so there is one source of
        /// truth for who is asking and no layer can hand on a stale copy of it. An unrecognised bearer and
        /// the shared bearer both answer empty, which is correct: neither names a client to report back to.
        /// </summary>
        /// <param name="context">The request being admitted.</param>
        /// <returns>The client display name, or empty when the bearer names no client.</returns>
        internal static string ClientFor(HttpListenerContext context)
        {
            if (context == null) return string.Empty;

            return AuthenticatedClient(context.Request.Headers["Authorization"]) ?? string.Empty;
        }

        /// <summary>
        /// The client a bearer belongs to: its display name, an empty string for the shared bearer, or null
        /// when it matches nothing. The shared bearer stays valid on purpose - every config written before
        /// clients had their own carries it, and refusing those would lock every client out at the exact
        /// moment the only way back in is through a client.
        /// </summary>
        /// <param name="authorization">The Authorization header as presented.</param>
        /// <returns>The owning client, an empty string for the shared bearer, or null when unrecognised.</returns>
        private static string AuthenticatedClient(string authorization)
        {
            // Every candidate is compared, with no early exit, so a wrong bearer costs the same whichever
            // one it failed to match. The set is tiny; this is not a lookup worth optimising into a leak.
            var matched = FixedTimeEquals(authorization, "Bearer " + _token) ? string.Empty : null;

            lock (_clientTokens)
            {
                foreach (var pair in _clientTokens)
                {
                    if (FixedTimeEquals(authorization, "Bearer " + pair.Value)) matched = pair.Key;
                }
            }

            return matched;
        }

        /// <summary>Reads the per-client bearers a previous run minted, so a restart does not invalidate configs.</summary>
        private static void LoadClientTokens()
        {
            try
            {
                if (!File.Exists(ClientTokenPath)) return;

                var parsed = JObject.Parse(File.ReadAllText(ClientTokenPath, Encoding.UTF8));
                lock (_clientTokens)
                {
                    foreach (var property in parsed.Properties())
                    {
                        var value = (string)property.Value;
                        if (!string.IsNullOrEmpty(value) && value.Length == TOKEN_LENGTH)
                        {
                            _clientTokens[property.Name] = value;
                        }
                    }
                }
            }
            catch
            {
                // An unreadable map costs a client its own bearer and nothing else: the shared one still
                // authenticates, so the editor stays reachable while the map rebuilds on the next Connect.
            }
        }

        /// <summary>Writes the per-client bearers. Called under the map's lock.</summary>
        private static void SaveClientTokens()
        {
            try
            {
                Directory.CreateDirectory(StateDir);

                var body = new JObject();
                foreach (var pair in _clientTokens) body[pair.Key] = pair.Value;
                File.WriteAllText(ClientTokenPath, body.ToString(Newtonsoft.Json.Formatting.None), new UTF8Encoding(false));
            }
            catch
            {
                // Same trade as the descriptor: a map that did not persist costs a re-Connect, never access.
            }
        }

        /// <summary>
        /// Publishes the descriptor. It carries identity, port and token, because the port alone never
        /// identifies a project: a client must check the identity it finds here against the project it was
        /// configured for.
        /// </summary>
        private static void PublishDescriptor()
        {
            try
            {
                Directory.CreateDirectory(_descriptorDir);
                var body = "{\"schemaVersion\":1,\"identity\":\"" + _identity +
                    "\",\"port\":" + Port +
                    ",\"token\":\"" + _token +
                    "\",\"project\":\"" + CanonicalProjectRoot().Replace("\\", "\\\\").Replace("\"", "\\\"") +
                    "\",\"phase\":4,\"mode\":\"durable-jobs\",\"catalogueRevision\":\"" + ConduitMcpCatalog.Revision +
                    "\",\"toolCount\":" + ConduitMcpCatalog.ToolCount + "}";

                var temp = DescriptorPath + ".tmp";
                using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(body);
                    writer.Flush();
                    stream.Flush(true);
                }

                // File.Replace is the atomic form. Delete followed by move leaves a window with no descriptor
                // on disk, and that window is exactly when a client is trying to discover this editor.
                if (File.Exists(DescriptorPath)) File.Replace(temp, DescriptorPath, null);
                else File.Move(temp, DescriptorPath);
            }
            catch (Exception)
            {
                // Silent by design. A descriptor that was not published shows as an unreachable endpoint to
                // the window and to any client that looks for it. The console is not Conduit's channel.
            }
        }

        #endregion

        #region Request handling

        /// <summary>Accepts one request on a listener thread and queues anything needing Unity.</summary>
        /// <param name="result">Pending accept.</param>
        private static void OnContext(IAsyncResult result)
        {
            var listener = result.AsyncState as HttpListener;
            if (listener == null || !listener.IsListening) return;

            HttpListenerContext context;
            try { context = listener.EndGetContext(result); }
            catch { return; }

            try { listener.BeginGetContext(OnContext, listener); }
            catch { }

            using (ConduitProfiling.Request.Auto())
            {
                try { Route(context); }
                catch (Exception ex)
                {
                    try { Respond(context, 500, "internal_error", "unhandled", new JObject { ["error"] = ex.Message }); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Enforces identity and token, then dispatches. Both checks run on the listener thread because
        /// neither touches Unity, and a rejected request should never reach the main-thread queue.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        private static void Route(HttpListenerContext context)
        {
            // Counted BEFORE the identity and bearer checks on purpose. A counter placed after them cannot
            // tell "the client never asked" from "the client asked and we refused", and those two call for
            // opposite fixes.
            RecordArrival(context.Request.HttpMethod, context.Request.Url.AbsolutePath);

            // Identity is required, not merely checked when present. A missing header is refused for the same
            // reason a wrong one is - a port alone never identifies a project - and requiring a custom header
            // is also what stops a browser reaching this endpoint, since it cannot set one cross-origin
            // without a preflight that is never granted here.
            var claimed = context.Request.Headers["X-Conduit-Project"];
            if (string.IsNullOrEmpty(claimed) || claimed != _identity)
            {
                Respond(context, 403, "identity_refused",
                    string.IsNullOrEmpty(claimed) ? "project_identity_absent" : "project_identity_mismatch", null);
                return;
            }

            // Compared without an early exit. The identity above is derivable from the project path and is
            // not a secret; this is.
            var authorization = context.Request.Headers["Authorization"];
            var caller = AuthenticatedClient(authorization);
            if (caller == null)
            {
                Respond(context, 401, "authentication_refused", "bad_or_missing_bearer", null);
                return;
            }

            _lastCaller = context.Request.HttpMethod + " as '" + caller + "'";
            NoteClientPresented(caller);

            var path = context.Request.Url.AbsolutePath;

            // A harness's in-process plugin holds a stream here on behalf of one of its sessions. This is the
            // inverse of the route below and exists for every harness that exports no per-session address at
            // all: rather than the editor discovering an ingress and writing to it, the side that is already
            // inside the harness connects outward and is written to. Nothing needs to be discoverable and no
            // platform-specific ingress has to be reimplemented here.
            if (path == "/agent-stream")
            {
                if (context.Request.HttpMethod != "GET")
                {
                    Respond(context, 405, "method_refused", "get_required", null);
                    return;
                }

                // Read from either, because a plugin building this request has a query string available while
                // a header is what every other Conduit route uses. Neither is preferred; whichever arrives is
                // the session.
                var streamSession = context.Request.QueryString["session"];
                if (string.IsNullOrEmpty(streamSession))
                {
                    streamSession = context.Request.Headers["X-Conduit-Session"];
                }

                if (string.IsNullOrEmpty(streamSession))
                {
                    Respond(context, 400, "stream_refused", "session_absent", null);
                    return;
                }

                OpenAgentStream(context, caller, streamSession);
                return;
            }

            // An adapter that cannot hold an outward return stream can hand over its session address
            // and credential here. The value never travels any further: it is stored beside this transport's
            // own bearers and used only to open that exact session's inbox.
            if (path == "/agent-session")
            {
                if (context.Request.HttpMethod != "POST")
                {
                    Respond(context, 405, "method_refused", "post_required", null);
                    return;
                }

                string registrationBody;
                try
                {
                    using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
                    {
                        registrationBody = reader.ReadToEnd();
                    }
                }
                catch (Exception ex)
                {
                    Respond(context, 400, "registration_refused", "unreadable_body",
                        new JObject { ["detail"] = ex.Message });
                    return;
                }

                // Parsed leniently on purpose. Registration adapters may put every value in headers
                // or send the harness's own payload. An unfamiliar body is no reason to refuse values that
                // arrived completely in headers.
                JObject registration;
                try { registration = JObject.Parse(registrationBody); }
                catch (Exception) { registration = new JObject(); }

                // Headers take precedence, and exist because a Windows pipe path survives a header verbatim:
                // putting one through JSON inside a hook configuration would need backslash escaping that
                // nothing at that layer can perform.
                var registeredSession = context.Request.Headers["X-Conduit-Session"];
                var registeredSocket = context.Request.Headers["X-Conduit-Socket"];
                var registeredToken = context.Request.Headers["X-Conduit-Messaging-Token"];

                // Both spellings are read: the harness's payload names it session_id, and a caller building
                // its own body follows the camel case every other Conduit payload uses.
                if (string.IsNullOrEmpty(registeredSession))
                {
                    registeredSession = (string)registration["session_id"]
                        ?? (string)registration["sessionId"];
                }

                if (string.IsNullOrEmpty(registeredSocket)) registeredSocket = (string)registration["socket"];
                if (string.IsNullOrEmpty(registeredToken)) registeredToken = (string)registration["token"];

                string registrationError;
                if (!ConduitAgentInbox.StoreRegistration(
                        registeredSession, registeredSocket, registeredToken, out registrationError))
                {
                    Respond(context, 400, "registration_refused", "invalid_registration",
                        new JObject { ["detail"] = registrationError });
                    return;
                }

                // The credential is never echoed, so a log or a transcript of this exchange cannot leak it.
                Respond(context, 200, "agent_session", "registered", new JObject
                {
                    ["sessionId"] = registeredSession,
                    ["socket"] = registeredSocket,
                    ["credentialStored"] = true,
                });
                return;
            }

            if (path == "/health")
            {
                // Answered off the main thread on purpose: a health probe has to stay truthful while the
                // editor is busy compiling, which is exactly when the main thread cannot answer.
                Respond(context, 200, "health", "ok", new JObject
                {
                    ["identity"] = _identity,
                    ["port"] = Port,
                    ["phase"] = 4,
                    ["ledger"] = JObject.Parse(ConduitJobLedger.GetHealthJson()),
                    ["mcpNonPost"] = new JObject
                    {
                        ["count"] = _nonPostMcp,
                        ["last"] = _lastNonPostMcp ?? string.Empty,
                    },
                    ["mainThread"] = MainThreadHealth(),
                    ["activity"] = EditorActivity(),
                    ["attachedClients"] = new JArray(AttachedClients()),
                    ["clientProfiles"] = ConduitMcpEndpoint.ClientProfiles(),
                    ["mcpRequests"] = ConduitMcpEndpoint.MethodCounts(),
                    ["toolListNotice"] = new JObject
                    {
                        ["sent"] = _toolListNoticesSent,
                        ["delivered"] = _toolListNoticeDelivered,
                        ["revision"] = _toolListNoticeRevision,
                        ["previous"] = _toolListNoticePrevious,
                    },
                    ["progressNotices"] = new JObject
                    {
                        ["sent"] = _progressNoticesSent,
                        ["dropped"] = _progressNoticesDropped,
                    },
                    ["streams"] = StreamCount,
                    ["streamDetail"] = StreamDetail(),
                    ["lastCaller"] = _lastCaller ?? "(none)",
                    ["knownClientTokens"] = KnownClientTokenCount,
                    ["nonPostLog"] = NonPostLog,
                    ["arrivals"] = Arrivals,
                    ["suggestedRetryAfterSeconds"] = ConduitJobLedger.SuggestedRetryAfterSeconds,
                    ["catalogue"] = new JObject
                    {
                        ["published"] = ConduitMcpCatalog.IsPublished,
                        ["revision"] = ConduitMcpCatalog.Revision,
                        ["toolCount"] = ConduitMcpCatalog.ToolCount,
                        ["error"] = ConduitMcpCatalog.PublicationError,
                    },
                });
                return;
            }

            if (path == "/job/status")
            {
                RouteJobStatus(context);
                return;
            }

            if (path == "/mcp")
            {
                RouteMcp(context, caller);
                return;
            }

            if (path != "/command")
            {
                Respond(context, 404, "route_refused", "no_such_endpoint", new JObject { ["path"] = path });
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                Respond(context, 405, "method_refused", "post_required", null);
                return;
            }

            string body;
            var readStatus = TryReadBody(context.Request, out body);
            if (readStatus != 200)
            {
                // A refusal is only useful if the caller can read it. Answering while the client is still
                // writing makes the listener reset the connection, so an over-limit body gets drained first
                // and the caller sees the 413 instead of a dropped socket.
                if (readStatus == 413 && !TryDrain(context.Request))
                {
                    try { context.Response.Abort(); } catch { }
                    return;
                }

                Respond(context, readStatus, "body_refused",
                    readStatus == 413 ? "body_too_large" : "invalid_utf8",
                    new JObject { ["limitBytes"] = MAX_BODY_BYTES });
                return;
            }

            var command = body.Trim();
            if (command.Length == 0)
            {
                Respond(context, 400, "body_refused", "empty_command", null);
                return;
            }

            var space = command.IndexOf(' ');
            var verb = space < 0 ? command : command.Substring(0, space);

            if (_blockedVerbs.Contains(verb))
            {
                Respond(context, 409, "verb_refused", "editor_state_not_job_aware", new JObject
                {
                    ["verb"] = verb,
                    ["detail"] = "editor-state transitions complete through a callback that cannot yet finish a job",
                });
                return;
            }

            // Thread free is strictly narrower than immediate, and the distinction is the whole reason
            // status used to time out while /health kept answering. An immediate verb does not defer, but it
            // still RUNS on the main thread, so QueueImmediate parks it until a tick drains it and no tick
            // comes while the main thread is busy. A thread-free verb is answered right here on the listener
            // thread, the same way /health is, so the work being asked about cannot delay the answer.
            string threadFree;
            if (ConduitBridge.TryExecuteThreadFreeCommand(verb, command, out threadFree))
            {
                Respond(context, 200, "command_completed", "ok", new JObject { ["result"] = threadFree });
                return;
            }

            if (_immediateVerbs.Contains(verb))
            {
                QueueImmediate(context, command);
                return;
            }

            AdmitDurable(context, verb, command);
        }

        /// <summary>
        /// Reads the body under the same caps as /command, then hands the request to the MCP endpoint. The
        /// body limits are enforced here rather than there so one endpoint cannot be given a larger ceiling
        /// than another by accident.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="caller">Client the bearer identified, or empty for the shared bearer.</param>
        private static void RouteMcp(HttpListenerContext context, string caller)
        {
            // Every client that asks for a stream gets one, whatever protocol version it negotiated. The
            // modern revision replaced this route with subscriptions/listen rather than forbidding it, and
            // refusing it by version left a client that opens no subscription with no server-to-client channel
            // at all: one harness held the only stream, and progress notices for every other caller were
            // counted as dropped rather than sent. A client that wants no stream does not ask for one.
            if (context.Request.HttpMethod == "GET")
            {
                RecordNonPost("GET", caller);
                OpenEventStream(context, caller);
                return;
            }

            // A client that has finished with a session says so with DELETE, and honouring it is what keeps
            // per-session state alive only as long as the session is. The transport spec permits answering 405
            // instead, which is what this did, and the cost appeared as soon as a client arrived that opens a
            // session per status query and politely closes each one: every record it asked to have dropped was
            // kept instead, and the store filled with sessions whose work was already finished.
            if (context.Request.HttpMethod == "DELETE")
            {
                RecordNonPost("DELETE", caller);

                var ending = context.Request.Headers["Mcp-Session-Id"];
                if (string.IsNullOrEmpty(ending))
                {
                    ConduitMcpEndpoint.RespondTransportError(context, 400, "session_required",
                        "DELETE ends one session and must name it in Mcp-Session-Id");
                    return;
                }

                bool streamClosed;
                var profileForgotten = ForgetSession(ending, out streamClosed);

                // Answered with what was actually dropped. A DELETE naming a session this domain never held
                // reports holding nothing, because reporting it as forgotten is a success for a no-op.
                Respond(context, 200, "session_terminated",
                    streamClosed || profileForgotten ? "forgotten" : "nothing_held",
                    new JObject
                    {
                        ["streamClosed"] = streamClosed,
                        ["profileForgotten"] = profileForgotten,
                    });
                return;
            }

            if (context.Request.HttpMethod != "POST")
            {
                RecordNonPost(context.Request.HttpMethod, caller);
                ConduitMcpEndpoint.RespondMethodNotAllowed(context);
                return;
            }

            string body;
            var readStatus = TryReadBody(context.Request, out body);
            if (readStatus != 200)
            {
                if (readStatus == 413 && !TryDrain(context.Request))
                {
                    try { context.Response.Abort(); } catch { }
                    return;
                }

                ConduitMcpEndpoint.RespondTransportError(context, readStatus,
                    readStatus == 413 ? "body_too_large" : "invalid_utf8",
                    readStatus == 413 ? "request body exceeds " + MAX_BODY_BYTES + " bytes" : "request body is not valid UTF-8");
                return;
            }

            ConduitMcpEndpoint.Handle(context, body, caller);
        }

        /// <summary>
        /// True when a verb answers on the request that asked for it. The endpoint and the raw command path
        /// both consult this one list, so the two cannot disagree about which verbs are safe to run inline.
        /// </summary>
        /// <param name="verb">Bridge verb.</param>
        /// <returns>True when the verb may run immediately.</returns>
        internal static bool IsImmediateVerb(string verb)
        {
            return _immediateVerbs.Contains(verb);
        }

        /// <summary>True when a verb is refused outright rather than admitted.</summary>
        /// <param name="verb">Bridge verb.</param>
        /// <returns>True when the verb cannot be run over HTTP at all yet.</returns>
        internal static bool IsBlockedVerb(string verb)
        {
            return _blockedVerbs.Contains(verb);
        }

        /// <summary>Queues a tool call from /mcp for the main thread, shedding load past the bound.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="command">Command line.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="toolName">Tool being called.</param>
        /// <param name="executionVerb">Verb that actually executes, for the conventions reminder.</param>
        /// <param name="caller">Authenticated client name, so a gated tool stays withheld from discovery.</param>
        /// <returns>True when the call was queued.</returns>
        internal static bool QueueMcpTool(
            HttpListenerContext context, string command, JToken id, string toolName, string executionVerb,
            string caller)
        {
            if (Interlocked.Increment(ref _pendingCount) > MAX_PENDING)
            {
                Interlocked.Decrement(ref _pendingCount);
                return false;
            }

            _pending.Enqueue(new PendingRequest
            {
                Context = context,
                Command = command,
                McpId = id,
                ToolName = toolName,
                ExecutionVerb = executionVerb,
                Caller = caller,
            });
            return true;
        }

        /// <summary>Queues an immediate verb for the main thread, shedding load past the bound.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="command">Command line.</param>
        private static void QueueImmediate(HttpListenerContext context, string command)
        {
            // Bounded, so a caller cannot pile up contexts faster than one tick can drain them. Each queued
            // entry holds an open listener context, so an unbounded queue leaks handles as well as memory.
            if (Interlocked.Increment(ref _pendingCount) > MAX_PENDING)
            {
                Interlocked.Decrement(ref _pendingCount);
                Respond(context, 429, "capacity_refused", "queue_full", new JObject { ["limit"] = MAX_PENDING });
                return;
            }

            _pending.Enqueue(new PendingRequest { Context = context, Command = command });
        }

        /// <summary>
        /// Admits a command to the durable ledger and answers with its job, never with its result. The work
        /// runs later on the main thread and may outlive this socket, which is the entire reason the job
        /// exists.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="verb">Bridge verb.</param>
        /// <param name="command">Raw command line.</param>
        private static void AdmitDurable(HttpListenerContext context, string verb, string command)
        {
            // Caller identity is required for durable work and cannot be defaulted. Without it two unrelated
            // clients share one idempotency namespace, and a retry cannot be told from a second deliberate
            // call - which is the whole property the ledger exists to provide.
            var sessionId = context.Request.Headers["Mcp-Session-Id"];
            var requestId = context.Request.Headers["X-Conduit-Request-Id"];
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(requestId))
            {
                Respond(context, 400, "admission_refused", "caller_identity_required", new JObject
                {
                    ["detail"] = "Mcp-Session-Id and X-Conduit-Request-Id are both required for a durable command",
                });
                return;
            }

            var result = ConduitJobLedger.Admit(
                sessionId, new JValue(requestId), verb,
                new JObject { ["command"] = command }, command, ENCODING_REVISION, ClientFor(context));

            switch (result.Outcome)
            {
                case "invalid":
                    Respond(context, 400, "admission_refused", "invalid", new JObject { ["error"] = result.Error });
                    return;

                case "not_ready":
                    var retryAfter = ConduitJobLedger.SuggestedRetryAfterSeconds;
                    context.Response.AddHeader("Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture));
                    Respond(context, 503, "admission_deferred", "ledger_not_ready", new JObject
                    {
                        ["error"] = result.Error,
                        ["retryAfterSeconds"] = retryAfter,
                    });
                    return;

                case "idempotency_conflict":
                    Respond(context, 409, "idempotency_conflict", "request_identity_reused", new JObject
                    {
                        ["error"] = result.Error,
                    });
                    return;

                case "created":
                    Respond(context, 202, "job_admitted", "created", JobEnvelope(result));
                    return;

                case "duplicate":
                    // A retry of a finished job gets its stored result; a retry of one still running gets the
                    // same job back to poll. Both resolve onto one job, which is the point.
                    if (ConduitJobLedger.IsTerminalState(result.State))
                    {
                        JObject record;
                        string readError;
                        if (!ConduitJobLedger.TryReadJob(result.JobId, out record, out readError))
                        {
                            // Never claim a terminal result that could not be read. The job is finished, but
                            // answering 200 here would be a success carrying no evidence of what happened.
                            var unreadableRetry = ConduitJobLedger.SuggestedRetryAfterSeconds;
                            context.Response.AddHeader("Retry-After", unreadableRetry.ToString(CultureInfo.InvariantCulture));
                            var unavailable = JobEnvelope(result);
                            unavailable["error"] = readError;
                            unavailable["retryAfterSeconds"] = unreadableRetry;
                            Respond(context, 503, "job_result_unavailable", "record_unreadable", unavailable);
                            return;
                        }

                        var envelope = JobEnvelope(result);
                        envelope["record"] = record;
                        Respond(context, 200, "job_result", "duplicate_terminal", envelope);
                        return;
                    }

                    Respond(context, 202, "job_recovered", "duplicate_nonterminal", JobEnvelope(result));
                    return;

                default:
                    Respond(context, 500, "internal_error", "unknown_admission_outcome", new JObject
                    {
                        ["admissionOutcome"] = result.Outcome,
                    });
                    return;
            }
        }

        /// <summary>Job identity fields shared by every admission response that has a job.</summary>
        /// <param name="result">Admission result.</param>
        /// <returns>The envelope fragment.</returns>
        private static JObject JobEnvelope(ConduitJobLedger.AdmissionResult result)
        {
            return new JObject
            {
                ["jobId"] = result.JobId,
                ["state"] = result.State,
                ["revision"] = result.Revision,
            };
        }

        /// <summary>Answers a job-status read. Runs on the listener thread; it touches no Unity API.</summary>
        /// <param name="context">The request being answered.</param>
        private static void RouteJobStatus(HttpListenerContext context)
        {
            if (context.Request.HttpMethod != "GET")
            {
                Respond(context, 405, "method_refused", "get_required", null);
                return;
            }

            var jobId = context.Request.QueryString["id"];
            if (string.IsNullOrEmpty(jobId))
            {
                Respond(context, 400, "job_status_refused", "id_required", null);
                return;
            }

            JObject record;
            string error;
            if (ConduitJobLedger.TryReadJob(jobId, out record, out error))
            {
                Respond(context, 200, "job_status", "found", new JObject { ["record"] = record });
                return;
            }

            // Three different failures, three different answers. A malformed id is the caller's mistake, a
            // missing job is a stable fact, and anything else is the filesystem being briefly unavailable.
            // Collapsing all three onto 404 tells a caller their job vanished when it may simply be
            // unreadable this instant, which is the difference between giving up and retrying.
            if (string.Equals(error, "invalid job id", StringComparison.Ordinal))
            {
                Respond(context, 400, "job_status_refused", "invalid_job_id", null);
                return;
            }

            if (string.Equals(error, "job_not_found", StringComparison.Ordinal))
            {
                Respond(context, 404, "job_status_refused", "job_not_found", null);
                return;
            }

            var retryAfter = ConduitJobLedger.SuggestedRetryAfterSeconds;
            context.Response.AddHeader("Retry-After", retryAfter.ToString(CultureInfo.InvariantCulture));
            Respond(context, 503, "job_status_unavailable", "record_unreadable", new JObject
            {
                ["error"] = error,
                ["retryAfterSeconds"] = retryAfter,
            });
        }

        /// <summary>
        /// Reads the request body under a hard byte ceiling. The declared content length cannot be the only
        /// check, because a chunked request declares none, so the stream is read to one byte past the cap and
        /// anything still arriving is refused.
        /// </summary>
        /// <param name="request">Incoming request.</param>
        /// <param name="body">Receives the decoded body when it fits and decodes.</param>
        /// <returns>200 when the body is usable, 413 when it is too large, 400 when it is not valid UTF-8.</returns>
        private static int TryReadBody(HttpListenerRequest request, out string body)
        {
            body = null;
            if (request.ContentLength64 > MAX_BODY_BYTES) return 413;

            var buffer = new byte[MAX_BODY_BYTES + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = request.InputStream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
            }

            if (total > MAX_BODY_BYTES) return 413;

            try
            {
                // Strict decoding, because a replacement character would silently change the command that
                // gets run rather than failing the request.
                body = new UTF8Encoding(false, true).GetString(buffer, 0, total);
                return 200;
            }
            catch (ArgumentException)
            {
                return 400;
            }
        }

        /// <summary>
        /// Reads and discards whatever remains of an over-limit request body, so the refusal can be delivered
        /// on a connection the client is no longer writing to. A caller far past even the drain ceiling is
        /// misbehaving and gets dropped rather than read forever.
        /// </summary>
        /// <param name="request">Incoming request.</param>
        /// <returns>True when the stream was drained, false when it exceeded the drain ceiling.</returns>
        private static bool TryDrain(HttpListenerRequest request)
        {
            var scratch = new byte[16384];
            var discarded = 0;
            while (discarded <= MAX_DRAIN_BYTES)
            {
                int read;
                try { read = request.InputStream.Read(scratch, 0, scratch.Length); }
                catch { return false; }

                if (read <= 0) return true;
                discarded += read;
            }

            return false;
        }

        /// <summary>
        /// Compares two secrets without an early exit, so the time taken does not report how many leading
        /// characters were right. Length still differs observably, which is unavoidable and standard.
        /// </summary>
        /// <param name="left">First value.</param>
        /// <param name="right">Second value.</param>
        /// <returns>True when both are present and equal.</returns>
        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null) return false;

            var a = Encoding.UTF8.GetBytes(left);
            var b = Encoding.UTF8.GetBytes(right);
            var difference = a.Length ^ b.Length;
            for (var i = 0; i < a.Length && i < b.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }

        /// <summary>
        /// Writes one JSON envelope and closes the exchange. Every endpoint answers in this shape, refusals
        /// included, so a client never has to guess whether a body is JSON or prose.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="status">HTTP status code.</param>
        /// <param name="operation">What the transport did.</param>
        /// <param name="outcome">How it turned out.</param>
        /// <param name="extra">Additional fields, or null.</param>
        private static void Respond(HttpListenerContext context, int status, string operation, string outcome, JObject extra)
        {
            var envelope = new JObject { ["operation"] = operation, ["outcome"] = outcome };
            if (extra != null)
            {
                foreach (var property in extra.Properties())
                {
                    // operation and outcome are the envelope's own contract and are never overwritable by a
                    // payload field. Merging blindly let one caller's extra silently replace the outcome it
                    // was reporting, which is a lie the client has no way to detect.
                    if (property.Name == "operation" || property.Name == "outcome") continue;
                    envelope[property.Name] = property.Value;
                }
            }

            var bytes = Encoding.UTF8.GetBytes(envelope.ToString(Formatting.None));
            try
            {
                context.Response.StatusCode = status;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // The caller may have gone away mid-write; nothing useful remains to report.
            }
            finally
            {
                try { context.Response.OutputStream.Close(); } catch { }
                try { context.Response.Close(); } catch { }
            }
        }

        #endregion

        #region Main-thread execution

        /// <summary>
        /// Runs one unit of work per editor tick, durable jobs first. That order matters: a client hammering
        /// immediate reads would otherwise keep this queue permanently non-empty and starve the mutations it
        /// is waiting on. Note that job_status is answered off the main thread and never enters this queue,
        /// so polling a job cannot starve it - the hazard is ordinary read traffic, not status polling.
        ///
        /// One unit per tick is deliberate. The bridge dispatches on this same update loop, so taking one
        /// piece of work per tick is what keeps the transport and the file poller from interleaving.
        /// While a source command prepares history off-thread, later durable commands stay queued;
        /// immediate reads remain available and the original command resumes before any later mutation.
        /// </summary>
        internal static void Drain()
        {
            using (ConduitProfiling.MainThread.Auto())
            {
                if (!ConduitBridge.FileHistoryBusy && DrainDurableJob()) return;
                DrainImmediateRequest();
            }
        }

        /// <summary>Claims and runs at most one durable job.</summary>
        /// <returns>True when a job was claimed, whether or not it deferred.</returns>
        private static bool DrainDurableJob()
        {
            ConduitJobLedger.JobLease lease;
            string error;
            bool claimed;
            using (ConduitProfiling.Claim.Auto())
            {
                claimed = ConduitJobLedger.TryClaimNext(out lease, out error);
            }

            if (!claimed || lease == null) return false;

            string result;
            bool deferred;
            try
            {
                result = ConduitBridge.ExecuteJob(lease, out deferred);
            }
            catch (Exception ex)
            {
                // The work itself failed, so failure is the truthful terminal state.
                // A failure that cannot be recorded leaves the job non-terminal, and that is exactly what
                // job_status reports to the caller who is waiting on it. That is the channel.
                ConduitJobLedger.Fail(lease.JobId, "command_failed", ex.Message, out error);

                return true;
            }

            // A deferred job is owned either by a reload recipe or by a bounded main-thread continuation.
            if (deferred) return true;

            // Persisting the result is a separate failure domain from running the work, and the two must not
            // share a catch. The side effect has already happened by this point, so recording a storage
            // failure as command_failed would invite the caller to retry work that was actually done.
            using (ConduitProfiling.Complete.Auto())
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    if (ConduitJobLedger.Complete(lease.JobId, "command_completed", result, out error)) return true;
                }
            }

            // The work is done and the record could not be updated, most often because recovery already
            // terminalised it while the drain was running - an ordinary race, not an error. The job is left
            // non-terminal rather than marked failed, and job_status carries that to the caller.
            return true;
        }

        /// <summary>Answers at most one queued immediate request.</summary>
        private static void DrainImmediateRequest()
        {
            PendingRequest request;
            if (!_pending.TryDequeue(out request)) return;
            Interlocked.Decrement(ref _pendingCount);

            try
            {
                var result = ConduitBridge.ExecuteImmediate(request.Command);
                using (ConduitProfiling.Respond.Auto())
                {
                    if (request.McpId != null)
                    {
                        ConduitMcpEndpoint.RespondToolResult(request.Context, request.McpId,
                            ConduitMcpEndpoint.WithConventions(request.ExecutionVerb, result, request.Caller),
                            false);
                        return;
                    }

                    Respond(request.Context, 200, "command_completed", "ok", new JObject { ["result"] = result });
                }
            }
            catch (Exception ex)
            {
                try
                {
                    if (request.McpId != null)
                    {
                        ConduitMcpEndpoint.RespondToolResult(request.Context, request.McpId,
                            "tool '" + request.ToolName + "' failed: " + ex.Message, true);
                        return;
                    }

                    Respond(request.Context, 500, "internal_error", "command_failed",
                        new JObject { ["error"] = ex.Message });
                }
                catch { }
            }
        }

        #endregion
    }
}
