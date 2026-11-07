/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// The MCP Streamable HTTP endpoint.
//
// POST carries every request and its answer. Legacy clients open GET as their server-to-client stream;
// modern clients open subscriptions/listen as a held POST stream. Both carry tools/list_changed when a reload
// publishes a different catalogue, so a client holding a definition that changed in place learns to fetch
// again. A new verb is reachable through conduit-run before any of that, and the catalogue revision tells a
// client whether the list it cached is still the one being served. Legacy sessions have no DELETE operation;
// modern requests carry no protocol session at all.
//
// Two status layers meet here and must not be confused. Transport failures - bad content type, oversized
// body, missing session - are real HTTP codes. A JSON-RPC response rides on 200 even when it carries an
// error object, because the request was delivered and answered; the error is the answer. The raw /command
// endpoint's 202 belongs to admission and is deliberately not reused here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>Answers JSON-RPC over HTTP for MCP clients.</summary>
    internal static class ConduitMcpEndpoint
    {
        #region Constants

        /// <summary>
        /// Streamable HTTP revisions this server speaks. 2024-11-05 is deliberately absent because it uses the
        /// legacy HTTP+SSE transport with separate SSE and message endpoints. This endpoint implements the newer
        /// single-endpoint Streamable HTTP transport, including its GET event stream.
        /// </summary>
        private static readonly string[] _supportedLegacyProtocols =
            { "2025-11-25", "2025-06-18", "2025-03-26" };

        /// <summary>Every protocol revision this endpoint implements, newest first.</summary>
        private static readonly string[] _supportedProtocols =
            { "2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26" };

        /// <summary>The stateless protocol revision negotiated through server/discover.</summary>
        private const string MODERN_PROTOCOL = "2026-07-28";

        /// <summary>Revision assumed when a legacy client sends no version header, per the transport spec.</summary>
        private const string ASSUMED_PROTOCOL = "2025-03-26";

        /// <summary>Revision offered when a legacy client asks for one this server does not know.</summary>
        private const string PREFERRED_LEGACY_PROTOCOL = "2025-11-25";

        /// <summary>Reserved request metadata key carrying the stateless protocol revision.</summary>
        private const string PROTOCOL_VERSION_META = "io.modelcontextprotocol/protocolVersion";

        /// <summary>Reserved request metadata key carrying the client's identity.</summary>
        private const string CLIENT_INFO_META = "io.modelcontextprotocol/clientInfo";

        /// <summary>Reserved request metadata key carrying the client's capabilities.</summary>
        private const string CLIENT_CAPABILITIES_META = "io.modelcontextprotocol/clientCapabilities";

        /// <summary>Reserved response metadata key carrying this server's identity.</summary>
        private const string SERVER_INFO_META = "io.modelcontextprotocol/serverInfo";

        /// <summary>Reserved notification metadata key correlating a subscription stream.</summary>
        internal const string SUBSCRIPTION_ID_META = "io.modelcontextprotocol/subscriptionId";

        /// <summary>Modern list results are immediately stale and must be fetched again after a notice.</summary>
        private const int MODERN_CACHE_TTL_MS = 0;

        /// <summary>Modern results are private to the client that requested them.</summary>
        private const string MODERN_CACHE_SCOPE = "private";

        /// <summary>JSON-RPC: the request was not valid JSON.</summary>
        private const int PARSE_ERROR = -32700;

        /// <summary>JSON-RPC: the request was valid JSON but not a valid request object.</summary>
        private const int INVALID_REQUEST = -32600;

        /// <summary>JSON-RPC: the method is not implemented.</summary>
        private const int METHOD_NOT_FOUND = -32601;

        /// <summary>JSON-RPC: the params were structurally wrong for the method.</summary>
        private const int INVALID_PARAMS = -32602;

        /// <summary>JSON-RPC: the server failed while handling a valid request.</summary>
        private const int INTERNAL_ERROR = -32603;

        /// <summary>MCP 2026: an HTTP routing header disagreed with the JSON-RPC body.</summary>
        private const int HEADER_MISMATCH = -32020;

        /// <summary>MCP 2026: the request names a protocol revision this endpoint does not implement.</summary>
        private const int UNSUPPORTED_PROTOCOL = -32022;

        /// <summary>
        /// The one terminal job state that is a success. It is transcribed from the ledger's vocabulary rather
        /// than referenced, because the ledger keeps its state names private. The gate drives this against real
        /// records, so a rename there turns the gate red instead of quietly reporting failures as successes.
        /// </summary>
        private const string COMPLETED_STATE = "completed";

        /// <summary>
        /// Longest a tool call waits for its own job before answering with a handle rather than a result. It is
        /// the cross-assembly compile budget this project already used for the same reason, and running out is
        /// not a failure: the job keeps going and job_status still reads it.
        ///
        /// Read by the connect plan as well, which tells a client that declares its own limit to allow longer
        /// than this. A client that gives up first aborts a call this wait is still holding, and the handle
        /// that would have made the job findable is never delivered, so the two cannot be set independently.
        /// </summary>
        internal const int TOOL_WAIT_MS = 180000;

        /// <summary>
        /// How often a caller that asked for progress is told the work is still going. It has to sit under the
        /// shortest limit any client applies of its own accord, because each notice is what resets that limit;
        /// one arriving after the client already gave up resets nothing. Sized well under the smallest default
        /// observed in a client rather than close to it, since the cost is one small frame.
        /// </summary>
        private const int PROGRESS_INTERVAL_MS = 10000;

        /// <summary>
        /// How many client profiles are kept in total. A client mints a fresh session on every handshake, so
        /// this grows without a bound unless one is set. It is the backstop rather than the fairness rule:
        /// on its own a total cap cannot keep every client present, because it evicts the oldest profile in
        /// the store no matter whose it is, and one client handshaking repeatedly then empties it of everyone
        /// else. The per-entry allowance below is what actually holds a place for each client.
        /// </summary>
        private const int MAX_CLIENT_PROFILES = 32;

        /// <summary>
        /// How many profiles ONE connection entry may hold. A client is free to open several MCP connections
        /// at once, and one measured here opened five inside ten seconds and seven inside ten minutes, each
        /// with its own session and so its own profile. Without a per-entry limit that client alone reaches
        /// the total cap and every other client's declaration is evicted, which is the report going blank for
        /// everybody because one of them was busy. Sized so the total cap can hold a full allowance for more
        /// entries than Conduit declares clients, leaving the backstop genuinely spare.
        /// </summary>
        private const int MAX_PROFILES_PER_ENTRY = 4;

        #endregion

        #region Fields

        /// <summary>
        /// What each client said about itself at initialize, keyed by the session minted for it. Clients differ
        /// in what they can render, and nothing else on the wire carries that: the bearer and the project header
        /// identify the project rather than the caller, so this handshake is the only place a client names
        /// itself. Recorded rather than inferred, and reported verbatim, so a question about one client is
        /// answered from what it actually sent.
        /// </summary>
        private static readonly Dictionary<string, JObject> _clientProfiles = new Dictionary<string, JObject>();

        /// <summary>Session ids in arrival order, which is what makes the oldest profile identifiable at either cap.</summary>
        private static readonly List<string> _clientOrder = new List<string>();

        /// <summary>
        /// How many times each JSON-RPC method has arrived on /mcp since this domain loaded, counted after
        /// the transport's bearer and identity checks and before anything judges the request. It answers
        /// the one question nothing else here can: whether a client actually asked again after being told
        /// the tool list changed, as against merely holding a stream that carried the notice.
        /// </summary>
        private static readonly Dictionary<string, int> _methodCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        #endregion

        #region Entry

        /// <summary>
        /// Handles one POST to /mcp. Runs on a listener thread; anything needing Unity is queued to the main
        /// thread and answered from there.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="body">Raw request body, already within the transport's size cap.</param>
        /// <param name="caller">Client whose bearer authenticated the request, or empty for the shared bearer.</param>
        internal static void Handle(HttpListenerContext context, string body, string caller)
        {
            // The transport spec requires an Origin defense. Every client here is a native process and sends
            // no Origin; a browser always sends one, so any Origin at all means a page is talking to this
            // port. Refusing the lot is both the simplest policy and the correct one for this deployment -
            // there is no browser client to allowlist.
            var origin = context.Request.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin))
            {
                RespondTransportError(context, 403, "origin_refused",
                    "requests carrying an Origin header are refused; this endpoint serves native clients only");
                return;
            }

            // Media types are compared as parsed tokens, not as substrings. A substring test accepts
            // "application/jsonevil" and any other type that merely contains the right letters.
            if (!IsJsonContentType(context.Request.ContentType))
            {
                RespondTransportError(context, 415, "Content-Type must be application/json");
                return;
            }

            JToken parsed;
            try { parsed = JToken.Parse(body); }
            catch (Exception ex)
            {
                RespondError(context, null, PARSE_ERROR, "invalid JSON: " + ex.Message);
                return;
            }

            // A batch is a JSON-RPC feature this server does not implement, and answering the first element
            // of one silently would be worse than refusing it.
            if (parsed.Type == JTokenType.Array)
            {
                RespondError(context, null, INVALID_REQUEST, "batched requests are not supported");
                return;
            }

            var request = parsed as JObject;
            if (request == null || (string)request["jsonrpc"] != "2.0" || string.IsNullOrEmpty((string)request["method"]))
            {
                RespondError(context, null, INVALID_REQUEST, "expected a JSON-RPC 2.0 request object");
                return;
            }

            var method = (string)request["method"];
            var id = request["id"];
            var isNotification = id == null || id.Type == JTokenType.Null;

            var acceptsResponse = method == "subscriptions/listen"
                ? AcceptsEventStream(context.Request.Headers["Accept"])
                : AcceptsJson(context.Request.Headers["Accept"]);
            if (!acceptsResponse)
            {
                RespondTransportError(context, 406, method == "subscriptions/listen"
                    ? "subscriptions/listen produces text/event-stream"
                    : "this request produces application/json");
                return;
            }

            // JSON-RPC allows a string, a number or null. Anything else is not an id, and echoing it back
            // would produce a response the client cannot match to its request.
            if (!isNotification
                && id.Type != JTokenType.String
                && id.Type != JTokenType.Integer
                && id.Type != JTokenType.Float)
            {
                RespondError(context, null, INVALID_REQUEST, "JSON-RPC id must be a string or a number");
                return;
            }

            // initialize establishes the session, so it cannot arrive as a notification: there would be no
            // response for the client to read the session out of, and the lifecycle could never start.
            if (method == "initialize" && isNotification)
            {
                RespondTransportError(context, 400, "bad_request",
                    "initialize must be a request with an id; its response carries the session id");
                return;
            }

            var session = context.Request.Headers["Mcp-Session-Id"];
            CountMethod(method);

            bool modern;
            if (!TryValidateProtocol(context, request, method, out modern)) return;

            // Subsequent legacy-request headers apply to notifications exactly as they do to requests, and
            // they are checked before a notification is acknowledged. Modern requests are stateless and carry
            // the same information in their reserved per-request metadata instead.
            if (!modern && method != "initialize")
            {
                var declared = context.Request.Headers["MCP-Protocol-Version"];
                if (!string.IsNullOrEmpty(declared) && Array.IndexOf(_supportedLegacyProtocols, declared) < 0)
                {
                    RespondTransportError(context, 400, "protocol_version_refused",
                        "unsupported MCP-Protocol-Version '" + declared + "'; this server speaks "
                        + string.Join(", ", _supportedProtocols) + " (a missing legacy header is assumed to be "
                        + ASSUMED_PROTOCOL + ")");
                    return;
                }

                if (string.IsNullOrEmpty(session))
                {
                    RespondTransportError(context, 400, "session_required",
                        "Mcp-Session-Id is required; call initialize first");
                    return;
                }
            }

            if (isNotification)
            {
                // Acknowledged at the transport layer with no JSON-RPC body, so an unknown notification is
                // accepted rather than answered with an error nobody is listening for.
                Acknowledge(context);
                return;
            }

            switch (method)
            {
                case "server/discover":
                    if (!modern)
                    {
                        RespondError(context, id, METHOD_NOT_FOUND, "method not supported on a legacy connection");
                        return;
                    }

                    HandleDiscover(context, id, request["params"] as JObject, caller);
                    return;

                case "initialize":
                    // A present-but-non-object params is malformed. Treating it as absent would negotiate a
                    // protocol version the client never asked for.
                    var initialiseParams = request["params"];
                    if (initialiseParams != null && initialiseParams.Type != JTokenType.Null
                        && !(initialiseParams is JObject))
                    {
                        RespondError(context, id, INVALID_PARAMS, "params must be an object");
                        return;
                    }

                    HandleInitialize(context, id, initialiseParams as JObject, caller);
                    return;

                case "ping":
                    if (modern)
                    {
                        RespondError(context, id, METHOD_NOT_FOUND, "ping was removed in " + MODERN_PROTOCOL);
                        return;
                    }

                    RespondResult(context, id, new JObject());
                    return;

                case "tools/list":
                    HandleToolsList(context, id, caller);
                    return;

                case "tools/call":
                    HandleToolsCall(context, id, request["params"] as JObject, session, caller);
                    return;

                case "subscriptions/listen":
                    if (!modern)
                    {
                        RespondError(context, id, METHOD_NOT_FOUND,
                            "subscriptions/listen requires " + MODERN_PROTOCOL);
                        return;
                    }

                    HandleSubscriptionListen(context, id, request["params"] as JObject, caller);
                    return;

                case "logging/setLevel":
                    HandleLoggingSetLevel(context, id, request["params"] as JObject, caller);
                    return;

                default:
                    RespondError(context, id, METHOD_NOT_FOUND, "method not supported: " + method);
                    return;
            }
        }

        #endregion

        #region Methods

        /// <summary>Mints a session and describes this server.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="parameters">Initialize params.</param>
        /// <param name="caller">Connection entry the request bearer resolved to.</param>
        private static void HandleInitialize(
            HttpListenerContext context, JToken id, JObject parameters, string caller)
        {
            var requested = (string)(parameters == null ? null : parameters["protocolVersion"]);
            var agreed = PREFERRED_LEGACY_PROTOCOL;
            if (!string.IsNullOrEmpty(requested))
            {
                agreed = Array.IndexOf(_supportedLegacyProtocols, requested) >= 0
                    ? requested
                    : PREFERRED_LEGACY_PROTOCOL;
            }

            // Minted fresh on every handshake. A legacy client echoes it on each later request, so its stream
            // is filed under this same key and its profile is found by it exactly. A modern client is
            // stateless and never sends it back, which is why the entry the bearer resolved to is recorded
            // beside it: that is the only identifier a later request from such a client still carries.
            var session = Guid.NewGuid().ToString("N");
            context.Response.AddHeader("Mcp-Session-Id", session);
            RecordClientProfile(session, caller, agreed, parameters);

            RespondResult(context, id, new JObject
            {
                ["protocolVersion"] = agreed,
                // The tool list changes whenever a reload publishes a different catalogue, and the transport
                // says so on the stream when it does. Declaring it static here licensed a client to keep every
                // definition it already held for good, so a tool changed in place was never seen again.
                ["capabilities"] = ServerCapabilities(),
                ["serverInfo"] = new JObject
                {
                    ["name"] = "conduit",
                    ["version"] = "1.0.0",
                    ["catalogueRevision"] = ConduitMcpCatalog.Revision,
                },
                // The full conventions text, snapshotted with the catalogue. A four-line summary here was a
                // real regression: the hard stops live in that document, and an agent that never sees them
                // edits prefabs through scene instances and polls status after blocking verbs.
                ["instructions"] = ConduitMcpCatalog.Instructions,
            });
        }

        /// <summary>Describes the stateless protocol surface before any modern request is made.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="parameters">Discovery params carrying the request metadata envelope.</param>
        /// <param name="caller">Client whose bearer authenticated the request.</param>
        private static void HandleDiscover(
            HttpListenerContext context, JToken id, JObject parameters, string caller)
        {
            RecordModernClientProfile(caller, parameters);
            RespondCacheableResult(context, id, new JObject
            {
                ["supportedVersions"] = new JArray(_supportedProtocols),
                ["capabilities"] = ServerCapabilities(),
                ["instructions"] = ConduitMcpCatalog.Instructions,
            });
        }

        /// <summary>Opens the modern notification subscription requested by the client.</summary>
        /// <param name="context">The request whose response becomes the stream.</param>
        /// <param name="id">JSON-RPC id used as the subscription id.</param>
        /// <param name="parameters">Listen params carrying the notification filter.</param>
        /// <param name="caller">Client whose bearer authenticated the request.</param>
        private static void HandleSubscriptionListen(
            HttpListenerContext context, JToken id, JObject parameters, string caller)
        {
            var notifications = parameters == null ? null : parameters["notifications"] as JObject;
            if (notifications == null)
            {
                RespondError(context, id, INVALID_PARAMS,
                    "subscriptions/listen requires params.notifications as an object");
                return;
            }

            var toolsChanged = notifications["toolsListChanged"];
            if (toolsChanged != null && toolsChanged.Type != JTokenType.Boolean)
            {
                RespondError(context, id, INVALID_PARAMS,
                    "params.notifications.toolsListChanged must be a boolean");
                return;
            }

            ConduitHttpTransport.OpenSubscriptionStream(context, caller, id, notifications);
        }

        /// <summary>Capabilities shared by legacy initialize and modern discovery.</summary>
        /// <returns>A new capabilities object safe for one response.</returns>
        private static JObject ServerCapabilities()
        {
            return new JObject
            {
                ["tools"] = new JObject { ["listChanged"] = true },

                // Logging is declared because work that outlives its own tool call has no other way to report
                // what happened: a client is correct to ignore notifications/message from a server that never
                // declared this, so the stream carried the frame and the session never saw it. Declaring it
                // obliges this server to honour logging/setLevel, which is why that method exists below rather
                // than being answered method-not-found.
                ["logging"] = new JObject(),
            };
        }

        /// <summary>
        /// Severities a client may set, in RFC 5424 order from least to most severe, which is the order the
        /// MCP logging capability specifies. Index IS the threshold comparison.
        /// </summary>
        private static readonly string[] LogLevels =
        {
            "debug", "info", "notice", "warning", "error", "critical", "alert", "emergency",
        };

        /// <summary>SessionState key holding each client's requested minimum severity.</summary>
        private const string LOG_LEVEL_SESSION_KEY = "ItsRenji.Conduit.Mcp.LogLevels";

        /// <summary>Severity assumed for a client that has not set one, as the logging capability specifies.</summary>
        private const string DEFAULT_LOG_LEVEL = "info";

        /// <summary>Sets one client's minimum severity, or refuses a level the specification does not define.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="parameters">setLevel params carrying the requested level.</param>
        /// <param name="caller">Client whose bearer authenticated the request.</param>
        private static void HandleLoggingSetLevel(
            HttpListenerContext context, JToken id, JObject parameters, string caller)
        {
            var level = parameters == null ? null : (string)parameters["level"];
            if (string.IsNullOrEmpty(level) || Array.IndexOf(LogLevels, level) < 0)
            {
                RespondError(context, id, INVALID_PARAMS,
                    "level must be one of " + string.Join(", ", LogLevels));
                return;
            }

            // Kept in the editor-session store rather than a static, because a script reload would otherwise
            // silently drop the client back to the default and start sending it severities it asked not to see.
            JObject levels;
            try { levels = JObject.Parse(UnityEditor.SessionState.GetString(LOG_LEVEL_SESSION_KEY, "{}")); }
            catch { levels = new JObject(); }

            levels[caller ?? string.Empty] = level;
            UnityEditor.SessionState.SetString(LOG_LEVEL_SESSION_KEY, levels.ToString(Formatting.None));
            RespondResult(context, id, new JObject());
        }

        /// <summary>
        /// Sends one log notification to a named client, dropping it when that client asked for a higher
        /// severity than this one.
        /// </summary>
        /// <param name="client">Client display name the work was authenticated under.</param>
        /// <param name="level">Severity, which must be one of <see cref="LogLevels"/>.</param>
        /// <param name="logger">Short source name shown beside the entry.</param>
        /// <param name="data">Message body.</param>
        /// <returns>How many of that client's streams accepted the frame.</returns>
        internal static int SendLogToClient(string client, string level, string logger, string data)
        {
            var wanted = Array.IndexOf(LogLevels, level ?? string.Empty);
            if (wanted < 0) return 0;

            JObject levels;
            try { levels = JObject.Parse(UnityEditor.SessionState.GetString(LOG_LEVEL_SESSION_KEY, "{}")); }
            catch { levels = new JObject(); }

            var configured = (string)levels[client ?? string.Empty] ?? DEFAULT_LOG_LEVEL;
            var floor = Array.IndexOf(LogLevels, configured);
            if (floor >= 0 && wanted < floor) return 0;

            return ConduitHttpTransport.SendToClient(client, "notifications/message", new JObject
            {
                ["level"] = level,
                ["logger"] = string.IsNullOrEmpty(logger) ? "conduit" : logger,
                ["data"] = data ?? string.Empty,
            });
        }

        /// <summary>This server's identity for the reserved modern response metadata envelope.</summary>
        /// <returns>A new identity object safe for one response.</returns>
        private static JObject ServerInfo()
        {
            return new JObject
            {
                ["name"] = "conduit",
                ["version"] = "1.0.0",
            };
        }

        /// <summary>Records the identity and capabilities carried by a stateless discovery request.</summary>
        /// <param name="caller">Client whose bearer authenticated the request.</param>
        /// <param name="parameters">Discovery params carrying the request metadata envelope.</param>
        private static void RecordModernClientProfile(string caller, JObject parameters)
        {
            var meta = parameters == null ? null : parameters["_meta"] as JObject;
            var info = meta == null ? null : meta[CLIENT_INFO_META] as JObject;
            var declared = meta == null ? null : meta[CLIENT_CAPABILITIES_META] as JObject;
            var key = "modern:" + (string.IsNullOrEmpty(caller) ? "shared" : caller);

            var profile = new JObject
            {
                ["name"] = info == null ? null : info["name"],
                ["version"] = info == null ? null : info["version"],
                ["title"] = info == null ? null : info["title"],
                ["protocol"] = MODERN_PROTOCOL,
                ["requestedProtocol"] = meta == null ? null : meta[PROTOCOL_VERSION_META],
                ["capabilities"] = declared == null ? new JObject() : (JObject)declared.DeepClone(),
                ["atSeconds"] = ConduitEditorActivity.TimeSinceStartup,
            };

            lock (_clientProfiles)
            {
                if (!_clientProfiles.ContainsKey(key)) _clientOrder.Add(key);
                _clientProfiles[key] = profile;

                while (_clientOrder.Count > MAX_CLIENT_PROFILES)
                {
                    _clientProfiles.Remove(_clientOrder[0]);
                    _clientOrder.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Files what one client said about itself at initialize. Stored verbatim, including the capabilities
        /// object, because the useful question later is what a client declared rather than what this server
        /// made of it. A client that declares nothing is recorded declaring nothing, which is itself the
        /// answer to whether it supports a given feature.
        /// </summary>
        /// <param name="session">Session minted for this client.</param>
        /// <param name="caller">Connection entry the request bearer resolved to.</param>
        /// <param name="agreed">Protocol revision settled on.</param>
        /// <param name="parameters">Initialize params as the client sent them.</param>
        private static void RecordClientProfile(string session, string caller, string agreed, JObject parameters)
        {
            var info = parameters == null ? null : parameters["clientInfo"] as JObject;
            var declared = parameters == null ? null : parameters["capabilities"] as JObject;

            var profile = new JObject
            {
                ["bearerClient"] = string.IsNullOrEmpty(caller) ? null : caller,
                ["name"] = info == null ? null : info["name"],
                ["version"] = info == null ? null : info["version"],
                ["title"] = info == null ? null : info["title"],
                ["protocol"] = agreed,
                ["requestedProtocol"] = parameters == null ? null : parameters["protocolVersion"],
                ["capabilities"] = declared == null ? new JObject() : (JObject)declared.DeepClone(),
                ["atSeconds"] = ConduitEditorActivity.TimeSinceStartup,
            };

            lock (_clientProfiles)
            {
                // Ordered once per session, not once per handshake. A client that hands over a second
                // profile for a session it already holds would otherwise be listed twice, and the trim below
                // walks that order, so the duplicate evicts a profile still in use.
                if (!_clientProfiles.ContainsKey(session)) _clientOrder.Add(session);
                _clientProfiles[session] = profile;

                // This entry's own allowance first, so a client that handshakes in bursts spends only its
                // own slots. The total cap below then evicts the oldest of anything left, which is a backstop
                // against many entries rather than against one busy client.
                TrimEntryLocked(caller);

                while (_clientOrder.Count > MAX_CLIENT_PROFILES)
                {
                    _clientProfiles.Remove(_clientOrder[0]);
                    _clientOrder.RemoveAt(0);
                }
            }
        }

        /// <summary>
        /// Drops one entry's oldest profiles until it is inside its allowance. The caller holds the profile
        /// lock, and the profile being recorded is already in the store when this runs.
        /// </summary>
        /// <param name="bearerClient">
        /// Entry just recorded against. An empty one is left alone: profiles written before the entry was
        /// recorded carry none, and they cannot be grouped, so only the total cap can reach them.
        /// </param>
        private static void TrimEntryLocked(string bearerClient)
        {
            if (string.IsNullOrEmpty(bearerClient)) return;

            var mine = new List<string>();
            foreach (var session in _clientOrder)
            {
                JObject existing;
                if (!_clientProfiles.TryGetValue(session, out existing)) continue;
                if (!string.Equals((string)existing["bearerClient"], bearerClient, StringComparison.Ordinal)) continue;

                mine.Add(session);
            }

            for (int i = 0; i < mine.Count && mine.Count - i > MAX_PROFILES_PER_ENTRY; i++)
            {
                _clientProfiles.Remove(mine[i]);
                _clientOrder.Remove(mine[i]);
            }
        }

        /// <summary>
        /// Every client seen since this editor started, newest last, for the health report. Copies are handed
        /// out so a reader on the listener thread cannot observe a profile mid-write.
        /// </summary>
        /// <returns>One entry per recorded client.</returns>
        internal static JArray ClientProfiles()
        {
            var clients = new JArray();

            lock (_clientProfiles)
            {
                foreach (var session in _clientOrder)
                {
                    JObject profile;
                    if (!_clientProfiles.TryGetValue(session, out profile)) continue;

                    clients.Add(profile.DeepClone());
                }
            }

            return clients;
        }

        /// <summary>
        /// What one session's client declared about itself at initialize, or null when nothing is filed under
        /// that session. A copy is handed out for the same reason the full listing hands out copies: a reader
        /// on the listener thread must not observe a profile mid-write.
        /// </summary>
        /// <param name="session">Session the client was issued at initialize.</param>
        /// <returns>A copy of the profile, or null when none is recorded.</returns>
        internal static JObject ClientProfileFor(string session)
        {
            if (string.IsNullOrEmpty(session)) return null;

            lock (_clientProfiles)
            {
                JObject profile;
                if (!_clientProfiles.TryGetValue(session, out profile)) return null;

                return (JObject)profile.DeepClone();
            }
        }

        /// <summary>
        /// The newest profile recorded against one connection entry, or null when that entry has none.
        ///
        /// This is how a modern client's declaration is found at all. Its stream cannot be matched by session,
        /// because that era is stateless and never echoes the minted session, so the bearer it authenticated
        /// with is the only identifier left to match on. That makes this weaker than a session match and it is
        /// reported as such by its caller: one entry can serve more than one harness, and then the newest
        /// handshake answers rather than provably the right one.
        /// </summary>
        /// <param name="bearerClient">Connection entry to match.</param>
        /// <returns>A copy of the newest matching profile, or null.</returns>
        internal static JObject NewestProfileForEntry(string bearerClient)
        {
            if (string.IsNullOrEmpty(bearerClient)) return null;

            lock (_clientProfiles)
            {
                for (int i = _clientOrder.Count - 1; i >= 0; i--)
                {
                    JObject profile;
                    if (!_clientProfiles.TryGetValue(_clientOrder[i], out profile)) continue;
                    if (!string.Equals((string)profile["bearerClient"], bearerClient, StringComparison.Ordinal)) continue;

                    return (JObject)profile.DeepClone();
                }

                return null;
            }
        }

        /// <summary>
        /// Drops one session's profile because its client ended the session. Keeping it would hold a record of
        /// a client that has said it is gone, and a client that opens and closes a session for each piece of
        /// housekeeping then accumulates records faster than any live client produces them.
        /// </summary>
        /// <param name="session">Session being ended.</param>
        /// <returns>True when a profile was held under that session and has been dropped.</returns>
        internal static bool ForgetClientProfile(string session)
        {
            if (string.IsNullOrEmpty(session)) return false;

            lock (_clientProfiles)
            {
                if (!_clientProfiles.Remove(session)) return false;

                _clientOrder.Remove(session);
                return true;
            }
        }

        /// <summary>Editor-session key carrying the client profiles across a domain reload.</summary>
        private const string PROFILES_SESSION_KEY = "ItsRenji.Conduit.Endpoint.ClientProfiles.v1";

        /// <summary>
        /// Records what each client declared, so the replacement domain still knows whose streams it adopted.
        ///
        /// A profile describes a CONNECTION, and a reload deliberately keeps connections: the sockets are
        /// handed to the next domain and a client never handshakes again, so without this the streams come
        /// back and the record of who owns them does not. Nothing in a declared name is editor state, and a
        /// report that names a stream without its client invites exactly the wrong conclusion about which
        /// client is attached. Held in the editor-session store rather than on disk because that is the
        /// lifetime of the thing described: the same store the handles ride in, cleared when the editor exits
        /// and the connections end with it.
        /// </summary>
        internal static void PersistClientProfilesForReload()
        {
            try
            {
                var values = new JArray();

                lock (_clientProfiles)
                {
                    foreach (var session in _clientOrder)
                    {
                        JObject profile;
                        if (!_clientProfiles.TryGetValue(session, out profile)) continue;

                        values.Add(new JObject
                        {
                            ["session"] = session,
                            ["profile"] = profile.DeepClone(),
                        });
                    }
                }

                UnityEditor.SessionState.SetString(PROFILES_SESSION_KEY, values.ToString(Formatting.None));
            }
            catch
            {
                // A profile that cannot be recorded is lost to the reload, which leaves the report naming a
                // stream without its client rather than failing the reload over a diagnostic.
            }
        }

        /// <summary>
        /// Adopts the profiles the previous domain recorded, in the order it recorded them. Run at domain load
        /// on the main thread, where the editor-session store is legal to read; that store is per editor
        /// session, so anything found here was written by this process.
        /// </summary>
        internal static void RestoreClientProfiles()
        {
            try
            {
                var encoded = UnityEditor.SessionState.GetString(PROFILES_SESSION_KEY, string.Empty);
                if (string.IsNullOrEmpty(encoded)) return;
                UnityEditor.SessionState.EraseString(PROFILES_SESSION_KEY);

                lock (_clientProfiles)
                {
                    foreach (var token in JArray.Parse(encoded))
                    {
                        var entry = token as JObject;
                        if (entry == null) continue;

                        var session = (string)entry["session"];
                        var profile = entry["profile"] as JObject;
                        if (string.IsNullOrEmpty(session) || profile == null) continue;

                        if (!_clientProfiles.ContainsKey(session)) _clientOrder.Add(session);
                        _clientProfiles[session] = (JObject)profile.DeepClone();
                    }

                    while (_clientOrder.Count > MAX_CLIENT_PROFILES)
                    {
                        _clientProfiles.Remove(_clientOrder[0]);
                        _clientOrder.RemoveAt(0);
                    }
                }
            }
            catch
            {
                // An unreadable record leaves this domain with no profiles, which is what it had before.
            }
        }

        /// <summary>Records one arrival of a method.</summary>
        /// <param name="method">JSON-RPC method as the client spelt it.</param>
        private static void CountMethod(string method)
        {
            if (string.IsNullOrEmpty(method)) return;

            lock (_methodCounts)
            {
                int seen;
                _methodCounts.TryGetValue(method, out seen);
                _methodCounts[method] = seen + 1;
            }
        }

        /// <summary>Arrivals per method since this domain loaded, for the health report.</summary>
        /// <returns>Method name to count.</returns>
        internal static JObject MethodCounts()
        {
            var counts = new JObject();

            lock (_methodCounts)
            {
                foreach (var pair in _methodCounts) counts[pair.Key] = pair.Value;
            }

            return counts;
        }

        /// <summary>
        /// Validates the per-request version and routing headers that distinguish the modern protocol era.
        /// Legacy requests are left to the session checks in <see cref="Handle"/>.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="request">Parsed JSON-RPC request.</param>
        /// <param name="method">JSON-RPC method.</param>
        /// <param name="modern">Receives whether this is a stateless modern request.</param>
        /// <returns>True when protocol and routing metadata are coherent.</returns>
        private static bool TryValidateProtocol(
            HttpListenerContext context, JObject request, string method, out bool modern)
        {
            modern = false;
            var parameters = request["params"] as JObject;
            var meta = parameters == null ? null : parameters["_meta"] as JObject;
            var bodyVersion = meta == null ? null : (string)meta[PROTOCOL_VERSION_META];
            var headerVersion = context.Request.Headers["MCP-Protocol-Version"];
            var hasModernSignal = method == "server/discover"
                || !string.IsNullOrEmpty(bodyVersion)
                || string.Equals(headerVersion, MODERN_PROTOCOL, StringComparison.Ordinal);

            if (!hasModernSignal) return true;

            if (!string.IsNullOrEmpty(bodyVersion)
                && !string.Equals(bodyVersion, MODERN_PROTOCOL, StringComparison.Ordinal))
            {
                RespondProtocolError(context, request["id"], UNSUPPORTED_PROTOCOL,
                    "unsupported protocol version '" + bodyVersion + "'",
                    new JObject { ["supported"] = new JArray(_supportedProtocols) });
                return false;
            }

            if (!string.Equals(bodyVersion, MODERN_PROTOCOL, StringComparison.Ordinal)
                || !string.Equals(headerVersion, MODERN_PROTOCOL, StringComparison.Ordinal))
            {
                RespondProtocolError(context, request["id"], HEADER_MISMATCH,
                    "MCP-Protocol-Version must match params._meta." + PROTOCOL_VERSION_META);
                return false;
            }

            var methodHeader = context.Request.Headers["Mcp-Method"];
            if (!string.Equals(methodHeader, method, StringComparison.Ordinal))
            {
                RespondProtocolError(context, request["id"], HEADER_MISMATCH,
                    "Mcp-Method must match the JSON-RPC method");
                return false;
            }

            var expectedName = ModernRequestName(method, parameters);
            if (expectedName != null
                && !string.Equals(context.Request.Headers["Mcp-Name"], expectedName, StringComparison.Ordinal))
            {
                RespondProtocolError(context, request["id"], HEADER_MISMATCH,
                    "Mcp-Name must match the request's principal name");
                return false;
            }

            modern = true;
            return true;
        }

        /// <summary>Returns the body field mirrored by Mcp-Name for a named modern request.</summary>
        /// <param name="method">JSON-RPC method.</param>
        /// <param name="parameters">Request params.</param>
        /// <returns>The required header value, or null when this method has no principal name.</returns>
        private static string ModernRequestName(string method, JObject parameters)
        {
            if (parameters == null) return null;
            if (method == "tools/call" || method == "prompts/get") return (string)parameters["name"];
            if (method == "resources/read") return (string)parameters["uri"];
            return null;
        }

        /// <summary>Whether a response belongs to the stateless modern protocol era.</summary>
        /// <param name="context">Accepted HTTP exchange.</param>
        /// <returns>True for a 2026-07-28 request.</returns>
        private static bool IsModernContext(HttpListenerContext context)
        {
            return context != null && string.Equals(
                context.Request.Headers["MCP-Protocol-Version"], MODERN_PROTOCOL, StringComparison.Ordinal);
        }

        /// <summary>
        /// Serves the published catalogue plus the session-repair tool that only an authenticated Claude
        /// bearer receives. The latter edits Claude's own persisted conversation state and has no meaning in
        /// another client, so advertising it there would offer a button that can only refuse.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="caller">Client display name resolved from the request bearer.</param>
        private static void HandleToolsList(HttpListenerContext context, JToken id, string caller)
        {
            if (!ConduitMcpCatalog.IsPublished)
            {
                RespondError(context, id, INTERNAL_ERROR,
                    "the tool catalogue is not published: " + (ConduitMcpCatalog.PublicationError ?? "unknown"));
                return;
            }

            var tools = ConduitMcpCatalog.ToolsSnapshot();
            if (ConduitClaudeSessionCacheReset.IsClaudeCaller(caller))
            {
                tools.Add(ConduitClaudeSessionCacheReset.ToolDefinition());
            }

            // One harness is served the reporting verbs without their session parameter, because it names
            // the calling conversation on every request and its model never sees that name. Publishing a
            // parameter only the bridge can answer would ask it for something it cannot obtain.
            if (ConduitCodexToolSurface.IsCaller(caller))
            {
                ConduitCodexToolSurface.Adapt(tools);
            }

            RespondCacheableResult(context, id, new JObject
            {
                ["tools"] = tools,
                ["_catalogueRevision"] = ConduitMcpCatalog.Revision,
            });
        }

        /// <summary>
        /// Prepares the exact Claude conversation that issued this call for fresh Conduit schemas on resume.
        /// Its current harness remains unchanged because ToolSearch has already materialised that index in
        /// memory; the result states the required restart rather than claiming the running process was fixed.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="arguments">Validated tool arguments.</param>
        /// <param name="caller">Client display name resolved from the request bearer.</param>
        private static void HandleClaudeSessionRefresh(
            HttpListenerContext context,
            JToken id,
            JObject arguments,
            string caller)
        {
            if (!ConduitClaudeSessionCacheReset.IsClaudeCaller(caller))
            {
                RespondError(context, id, INVALID_PARAMS,
                    "unknown tool: " + ConduitClaudeSessionCacheReset.TOOL_NAME);
                return;
            }

            JObject definition = ConduitClaudeSessionCacheReset.ToolDefinition();
            string validationError;
            if (!ConduitJsonSchemaValidator.Validate(
                (JObject)definition["inputSchema"], arguments, out validationError))
            {
                RespondToolResult(context, id, validationError, true);
                return;
            }

            ConduitClaudeSessionCacheReset.Outcome outcome;
            string error;
            if (!ConduitClaudeSessionCacheReset.TryPrepare(
                (string)arguments["nonce"], out outcome, out error))
            {
                RespondToolResult(context, id, error, true);
                return;
            }

            string text;
            if (outcome.RemovedEntries == 0 && outcome.RemovedSkills == 0)
            {
                text = "Claude session " + outcome.SessionId +
                       " has no saved Conduit tool schemas or skill snapshots. No transcript data changed.";
            }
            else
            {
                text = "Prepared Claude session " + outcome.SessionId +
                       " to reload Conduit from source. Removed " +
                       outcome.RemovedEntries.ToString(CultureInfo.InvariantCulture) +
                       " saved tool schema entr" + (outcome.RemovedEntries == 1 ? "y" : "ies") + " and " +
                       outcome.RemovedSkills.ToString(CultureInfo.InvariantCulture) +
                       " saved skill snapshot" + (outcome.RemovedSkills == 1 ? string.Empty : "s") +
                       " across " + outcome.ChangedRecords.ToString(CultureInfo.InvariantCulture) +
                       " record" + (outcome.ChangedRecords == 1 ? string.Empty : "s") + ".\n" +
                       "Backup: " + outcome.BackupPath + "\n\n" +
                       "The running Claude harness still holds both of those in memory. Tell the user to " +
                       "restart that Claude harness now, then resume this same task. Do not start a new task.";
            }

            RespondToolResult(context, id, text, false);
        }

        /// <summary>Dispatches one tool call, immediately or durably depending on the verb.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id, passed to the ledger with its type intact.</param>
        /// <param name="parameters">Call params.</param>
        /// <param name="session">Client session.</param>
        /// <param name="caller">Client display name resolved from the request bearer.</param>
        private static void HandleToolsCall(
            HttpListenerContext context,
            JToken id,
            JObject parameters,
            string session,
            string caller)
        {
            var toolName = (string)(parameters == null ? null : parameters["name"]);
            if (string.IsNullOrEmpty(toolName))
            {
                RespondError(context, id, INVALID_PARAMS, "params.name is required");
                return;
            }

            // An absent arguments member means no arguments. A PRESENT one that is not an object is a
            // malformed call, and quietly substituting an empty object would run the tool with defaults the
            // caller never asked for.
            JObject arguments;
            var suppliedArguments = parameters["arguments"];
            if (suppliedArguments == null || suppliedArguments.Type == JTokenType.Null)
            {
                arguments = new JObject();
            }
            else
            {
                arguments = suppliedArguments as JObject;
                if (arguments == null)
                {
                    RespondError(context, id, INVALID_PARAMS, "params.arguments must be an object");
                    return;
                }
            }

            // The other half of that harness's surface. Its schema omits the session parameter, so this is
            // where the value comes from: the conversation id it puts in every request's metadata envelope.
            // Filled only when the caller supplied none, so a caller that names one explicitly still wins -
            // and only for the verbs whose published schema actually dropped the parameter, so this can
            // never introduce an argument a verb did not advertise.
            if (ConduitCodexToolSurface.IsCaller(caller) && ConduitCodexToolSurface.IsAutoFilled(toolName)
                && string.IsNullOrEmpty((string)arguments["sessionId"]))
            {
                var conversation = ConduitCodexToolSurface.ConversationFrom(parameters["_meta"] as JObject);

                // Absent is left absent rather than substituted with something plausible. The verb reports
                // that it has nowhere to send its result, which is the truth; inventing an id here would
                // produce a build that reports into a conversation that does not exist.
                if (!string.IsNullOrEmpty(conversation)) arguments["sessionId"] = conversation;
            }

            if (string.Equals(
                toolName, ConduitClaudeSessionCacheReset.TOOL_NAME, StringComparison.Ordinal))
            {
                HandleClaudeSessionRefresh(context, id, arguments, caller);
                return;
            }

            string verb;
            if (!ConduitMcpCatalog.TryGetVerb(toolName, out verb))
            {
                RespondError(context, id, INVALID_PARAMS, "unknown tool: " + toolName);
                return;
            }

            // Server-side validation against the published schema, before any dispatch decision and for every
            // tool including job_status. The builder refusal downstream is a second gate rather than the only
            // one: a builder only notices the arguments it needs, so a wrong type or an extra property can
            // reach a handler unremarked. A tool with no published schema is an internal inconsistency and is
            // refused rather than waved through.
            JObject schema;
            if (!ConduitMcpCatalog.TryGetSchema(toolName, out schema))
            {
                RespondError(context, id, INTERNAL_ERROR, "tool '" + toolName + "' has no published schema");
                return;
            }

            string validationError;
            if (!ConduitJsonSchemaValidator.Validate(schema, arguments, out validationError))
            {
                RespondToolResult(context, id, validationError, true);
                return;
            }

            // Answered here rather than through the bridge: it reads a durable record and never needs Unity.
            if (verb == "job_status")
            {
                HandleJobStatusTool(context, id, arguments);
                return;
            }

            Func<JObject, string> build;
            if (!ConduitMcpCatalog.TryGetBuilder(toolName, out build))
            {
                RespondError(context, id, INTERNAL_ERROR, "tool '" + toolName + "' has no command builder");
                return;
            }

            string command;
            try
            {
                command = build(arguments);
            }
            catch (Exception ex)
            {
                // A builder refusal is the caller's mistake and is reported as a tool error rather than a
                // protocol error, because the call itself was well formed.
                RespondToolResult(context, id, ex.Message, true);
                return;
            }

            // conduit-run is a wrapper whose command carries the verb that will actually execute. Everything
            // downstream has to classify that verb rather than the wrapper: classifying the wrapper makes the
            // editor-state block bypassable by asking conduit-run to run play, and records a durable job
            // against a verb that never ran.
            var executionVerb = verb;
            if (verb == "conduit-run")
            {
                var line = command.Trim();
                var firstSpace = line.IndexOf(' ');
                executionVerb = firstSpace < 0 ? line : line.Substring(0, firstSpace);
            }

            if (ConduitHttpTransport.IsBlockedVerb(executionVerb))
            {
                RespondToolResult(context, id,
                    "'" + executionVerb + "' is on the blocked list and cannot be run through Conduit.", true);
                return;
            }

            // Answered without the main thread, so it cannot queue behind the work it is reporting on. See
            // ConduitBridge.TryExecuteThreadFreeCommand for why the set is as small as it is.
            string threadFree;
            if (ConduitBridge.TryExecuteThreadFreeCommand(executionVerb, command, out threadFree))
            {
                RespondToolResult(context, id, WithConventions(executionVerb, threadFree, caller), false);
                return;
            }

            if (ConduitHttpTransport.IsImmediateVerb(executionVerb))
            {
                if (!ConduitHttpTransport.QueueMcpTool(
                    context, command, id, toolName, executionVerb, caller))
                {
                    var queueRetry = ConduitJobLedger.SuggestedRetryAfterSeconds;
                    context.Response.AddHeader("Retry-After", queueRetry.ToString(CultureInfo.InvariantCulture));
                    RespondTransportError(context, 429, "too many calls queued; the editor drains one per tick");
                }

                return;
            }

            if (IsModernContext(context))
            {
                // The modern protocol has no session id. A fresh internal namespace prevents unrelated calls
                // that reuse a JSON-RPC id from colliding in the durable ledger. This key therefore matches
                // no open stream by construction, which is why a progress notice falls back to addressing
                // the client the bearer names instead of the session - see SendProgress.
                session = "modern:" + Guid.NewGuid().ToString("N");
            }

            AdmitDurableTool(context, id, session, executionVerb, arguments, command, ProgressTokenOf(parameters));
        }

        /// <summary>
        /// Reads the progress token a caller attached to its call, or null when it asked for none.
        ///
        /// Asking is the presence of this token and nothing else. A receiver treats a notice carrying a token
        /// it never issued as an error rather than ignoring it, so a caller that did not ask must be sent
        /// nothing at all - inferring that it would probably like progress is what turns a courtesy into
        /// errors in its log.
        /// </summary>
        /// <param name="parameters">Call params.</param>
        /// <returns>The token exactly as sent, or null.</returns>
        private static JToken ProgressTokenOf(JObject parameters)
        {
            var meta = parameters == null ? null : parameters["_meta"] as JObject;
            var token = meta == null ? null : meta["progressToken"];
            return token == null || token.Type == JTokenType.Null ? null : token;
        }

        /// <summary>Reads a durable job for the job_status tool.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="arguments">Tool arguments.</param>
        private static void HandleJobStatusTool(HttpListenerContext context, JToken id, JObject arguments)
        {
            var jobId = (string)arguments["jobId"];
            if (string.IsNullOrEmpty(jobId))
            {
                RespondToolResult(context, id, "jobId is required", true);
                return;
            }

            JObject record;
            string error;
            if (!ConduitJobLedger.TryReadJob(jobId, out record, out error))
            {
                RespondToolResult(context, id, "job " + jobId + " could not be read: " + error, true);
                return;
            }

            RespondToolResult(context, id, record.ToString(Formatting.Indented), false,
                JobMeta(jobId, (string)record["state"], (long)record["revision"]));
        }

        /// <summary>
        /// Admits a compiling or otherwise unanswerable command to the durable ledger. The structured
        /// arguments are recorded as the client sent them, before command encoding, so a retry is compared on
        /// what was asked for rather than on how it happened to be spelled on the wire.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id, used as the request identity with its type intact.</param>
        /// <param name="session">Client session.</param>
        /// <param name="verb">Canonical bridge verb.</param>
        /// <param name="arguments">Structured arguments as supplied.</param>
        /// <param name="command">Encoded command line.</param>
        /// <param name="progressToken">Token this caller asked progress under, or null when it asked for none.</param>
        private static void AdmitDurableTool(
            HttpListenerContext context, JToken id, string session, string verb, JObject arguments, string command,
            JToken progressToken)
        {
            // The encoding revision ties a stored record to the catalogue that produced its command, so a
            // record written by one catalogue is not silently read as if it came from another.
            var result = ConduitJobLedger.Admit(
                session, id, verb, arguments, command, "mcp/" + ConduitMcpCatalog.Revision,
                ConduitHttpTransport.ClientFor(context));

            switch (result.Outcome)
            {
                case "invalid":
                    RespondToolResult(context, id, "the command could not be admitted: " + result.Error, true);
                    return;

                case "not_ready":
                    var retry = ConduitJobLedger.SuggestedRetryAfterSeconds;
                    context.Response.AddHeader("Retry-After", retry.ToString(CultureInfo.InvariantCulture));
                    RespondToolResult(context, id,
                        "the editor is still recovering its job ledger and cannot accept work yet. Retry in about "
                        + retry + " seconds. (" + result.Error + ")", true);
                    return;

                case "idempotency_conflict":
                    RespondToolResult(context, id,
                        "this request id was already used for different work. Use a new request id, or read the "
                        + "original job with job_status.", true);
                    return;

                case "created":
                    AnswerWhenTerminal(context, id, verb, result, "admitted", session, progressToken);
                    return;

                case "duplicate":
                    if (ConduitJobLedger.IsTerminalState(result.State))
                    {
                        JObject record;
                        string readError;
                        if (!ConduitJobLedger.TryReadJob(result.JobId, out record, out readError))
                        {
                            RespondToolResult(context, id,
                                "job " + result.JobId + " is finished but its record could not be read: " + readError, true);
                            return;
                        }

                        string terminalText;
                        bool terminalFailed;
                        TerminalToolResponse(verb, result.JobId, record, out terminalText, out terminalFailed);

                        RespondToolResult(context, id, terminalText, terminalFailed,
                            JobMeta(result.JobId, (string)record["state"], (long)record["revision"]));
                        return;
                    }

                    AnswerWhenTerminal(context, id, verb, result, "already running", session, progressToken);
                    return;

                default:
                    RespondError(context, id, INTERNAL_ERROR, "unknown admission outcome: " + result.Outcome);
                    return;
            }
        }

        /// <summary>
        /// Prepends the conventions reminder to an authoring result, and appends the server-side tool
        /// inventory to a discovery result. The reminder rides on the admission response only: a caller
        /// polling job_status has already been told once, and repeating it on every poll trains the reader to
        /// skip it, which defeats the reminder.
        /// </summary>
        /// <param name="verb">Execution verb, so a conduit-run wrapper is classified by what it actually runs.</param>
        /// <param name="text">Result text.</param>
        /// <param name="caller">Authenticated client name, or null when the path does not carry one.</param>
        /// <returns>The text, with the reminder in front when the verb authors and the inventory after when it discovers.</returns>
        internal static string WithConventions(string verb, string text, string caller)
        {
            if (IsDiscoveryCommand(verb)) text = text + ServerToolInventory(caller);

            return ConduitMcpCatalog.IsAuthoringCommand(verb)
                ? ConduitMcpCatalog.CONVENTIONS_REMINDER + "\n\n" + text
                : text;
        }

        /// <summary>
        /// Whether a verb answers the question "what can I call", which is the only place the server's own
        /// tools can be discovered from.
        /// </summary>
        /// <param name="verb">Execution verb.</param>
        /// <returns>True for the discovery verbs.</returns>
        private static bool IsDiscoveryCommand(string verb)
        {
            return verb == "help" || verb == "commands" || verb == "unity-tool-list";
        }

        /// <summary>
        /// Renders the UNGATED tools this server answers itself, which no bridge discovery verb can list: the
        /// bridge runs inside Unity and knows nothing of the MCP layer wrapping it, so its command list is the
        /// bridge half of the surface and never the whole of it. Without this section a caller's only route to
        /// their arguments is the tool's own tools/list entry, which a client that defers tool schemas has not
        /// loaded, so the first call is a guess.
        ///
        /// Every argument list is read back from the published schema rather than restated here, so it cannot
        /// drift from what the server actually validates.
        ///
        /// THE INVARIANT: this section names a tool only when tools/list would serve that same tool to the
        /// same caller, and it is enforced by asking the SAME predicate tools/list asks rather than by a
        /// second rule that could drift from it. A per-harness gate is a security boundary and not a
        /// convenience - the session refresh tool rewrites another product's own session records on disk, and
        /// the gate exists so that no other harness learns the capability is there at all. The identity behind
        /// it is what the client DECLARED at initialize, so secrecy is load-bearing rather than defence in
        /// depth on top of a strong check: naming a gated tool to every caller would hand every other harness
        /// the name and the argument list, which is most of the distance to using it.
        ///
        /// A null caller therefore FAILS CLOSED and is served the ungated tools only. That is a real state
        /// rather than a defensive flourish: a response path that does not carry the caller reaches here with
        /// nothing, and withholding is the only safe reading of "I do not know who is asking".
        /// </summary>
        /// <param name="caller">Authenticated client name, or null when the path does not carry one.</param>
        /// <returns>The inventory section, ready to append to a discovery result.</returns>
        private static string ServerToolInventory(string caller)
        {
            var section = "\n\nServer-side tools. These are not bridge verbs, so no discovery verb above lists"
                + " them, and each one's arguments come from its own tools/list entry:\n";

            section += ServerToolLine("conduit-run", null, null);
            section += ServerToolLine("job_status", null, null);

            if (ConduitClaudeSessionCacheReset.IsClaudeCaller(caller))
            {
                section += ServerToolLine(
                    ConduitClaudeSessionCacheReset.TOOL_NAME,
                    ConduitClaudeSessionCacheReset.ToolDefinition(),
                    "your harness only");
            }

            return section;
        }

        /// <summary>
        /// Renders one server-side tool as its name, its arguments taken from the published schema with
        /// required ones in angle brackets and optional ones in square brackets, and its gate where it has one.
        /// </summary>
        /// <param name="name">Published tool name.</param>
        /// <param name="definition">The tool's definition, or null to read the schema from the catalogue.</param>
        /// <param name="gate">Who may call it, or null when any caller may.</param>
        /// <returns>One line, newline terminated.</returns>
        private static string ServerToolLine(string name, JObject definition, string gate)
        {
            JObject schema = null;
            if (definition != null)
            {
                schema = definition["inputSchema"] as JObject;
            }
            else
            {
                JObject published;
                if (ConduitMcpCatalog.TryGetSchema(name, out published)) schema = published;
            }

            var line = "  " + name;

            // Said plainly rather than left blank, because a silent omission reads as "takes no arguments" -
            // the same wrong answer this section exists to remove.
            if (schema == null) return line + "  (arguments unavailable, read its tools/list entry)\n";

            var properties = schema["properties"] as JObject;
            if (properties == null || !properties.HasValues)
            {
                line += "  (no arguments)";
            }
            else
            {
                var required = schema["required"] as JArray;
                foreach (var property in properties.Properties())
                {
                    var isRequired = false;
                    if (required != null)
                    {
                        foreach (var entry in required)
                        {
                            if (!string.Equals((string)entry, property.Name, StringComparison.Ordinal)) continue;
                            isRequired = true;
                            break;
                        }
                    }

                    line += isRequired ? "  <" + property.Name + ">" : "  [" + property.Name + "]";
                }
            }

            if (!string.IsNullOrEmpty(gate)) line += "  (" + gate + ")";
            return line + "\n";
        }

        /// <summary>
        /// Composes the answer to a tools/call that resolved onto a job which had already finished. This is the
        /// recovery path when the original client disconnected, socket capture was unavailable, or a caller
        /// deliberately repeated an idempotent request after its first answer.
        ///
        /// It answers with the command's own outcome rather than with the ledger record, because the caller
        /// made a command call and not a status poll, and it reports a job that did not complete as an error.
        /// The previous form returned the raw record with isError false, so a retry of failed work read as
        /// success, and it skipped the conventions reminder that both sibling branches apply.
        /// </summary>
        /// <param name="verb">Execution verb, so a conduit-run wrapper is classified by what it actually ran.</param>
        /// <param name="jobId">Job the retry resolved onto.</param>
        /// <param name="record">The terminal record.</param>
        /// <param name="text">Receives the text to answer with.</param>
        /// <param name="failed">Receives whether the caller must treat the answer as an error.</param>
        internal static void TerminalToolResponse(string verb, string jobId, JObject record, out string text, out bool failed)
        {
            var state = record == null ? null : (string)record["state"];
            failed = !string.Equals(state, COMPLETED_STATE, StringComparison.Ordinal);

            var outcome = record == null ? null : record["outcome"] as JObject;
            var stored = outcome == null ? null : (string)outcome["text"];

            // A finished record carrying no outcome text is a defect rather than an ordinary result, so the
            // record is handed over whole instead. Answering nothing at all for work that ran is the blanket
            // success this response shape exists to prevent.
            var body = string.IsNullOrEmpty(stored)
                ? (record == null ? "the job record could not be read" : record.ToString(Formatting.Indented))
                : stored;

            // A state that is not a completion is named in the text as well as in isError, because a caller
            // that only reads the prose still has to see that the work did not succeed.
            // Null caller: a durable job is never one of the discovery verbs, so no server-tool inventory
            // renders on this path, and null withholds every gated tool in any case.
            text = WithConventions(verb, failed
                ? "job " + jobId + " finished as " + (state ?? "an unreadable state") + ".\n\n" + body
                : body, null);
        }

        /// <summary>
        /// Waits for an admitted job to finish and answers with its result, because the result is what the
        /// caller asked this verb for. A job id and a state are this ledger's bookkeeping, not an answer, and a
        /// verb that reports its own plumbing instead of its outcome makes every caller do a second call to
        /// learn what happened. The handle is returned only when the wait genuinely runs out, which leaves the
        /// job running and still readable rather than losing it.
        ///
        /// Safe to block here: durable verbs are handled inline on the listener thread while the editor drains
        /// and executes jobs on the main thread, so waiting holds up this one caller and nothing else.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="verb">Execution verb, so the answer is classified by what actually ran.</param>
        /// <param name="result">Admission result naming the job to wait for.</param>
        /// <param name="situation">How the job came to exist, used only if the wait runs out.</param>
        /// <param name="session">Session whose stream a progress notice is written to.</param>
        /// <param name="progressToken">Token this caller asked progress under, or null when it asked for none.</param>
        private static void AnswerWhenTerminal(
            HttpListenerContext context, JToken id, string verb, ConduitJobLedger.AdmissionResult result,
            string situation, string session, JToken progressToken)
        {
            var handoff = ConduitReloadResponseHandoff.Register(
                context, id, verb, result.JobId, IsModernContext(context));
            ConduitReloadResponseHandoff.Trace("terminal wait started job=" + result.JobId);
            try
            {
                JObject record;
                if (!WaitForTerminalReportingProgress(
                        context, result.JobId, verb, session, progressToken, handoff, out record))
                {
                    ConduitReloadResponseHandoff.Trace("terminal wait released job=" + result.JobId
                        + " handedOff=" + handoff.HandedOff);
                    // beforeAssemblyReload captured this response's raw socket and woke the managed waiter so
                    // its old-domain thread can return. The new domain owns the only valid response path now.
                    if (handoff.HandedOff) return;

                    RespondToolResult(context, id,
                        WithConventions(verb, JobInstructions(result, situation), null), false,
                        JobMeta(result.JobId, result.State, result.Revision));
                    return;
                }

                string text;
                bool failed;
                TerminalToolResponse(verb, result.JobId, record, out text, out failed);
                RespondToolResult(context, id, text, failed,
                    JobMeta(result.JobId, (string)record["state"], (long)record["revision"]));
            }
            finally { handoff.Dispose(); }
        }

        /// <summary>
        /// Waits out the tool budget, telling a caller that asked for progress that the work is still going.
        ///
        /// The wait is cut into slices only to get those notices out. Slicing costs nothing and loses nothing:
        /// the ledger registers its waiter BEFORE it reads the record, so a job that finishes between two
        /// slices is picked up by the next one instead of being missed. Why bother at all: a client applies a
        /// limit of its own to a call, that limit is shorter than this budget on every client measured, and a
        /// notice resets it - so without this, the calls that recompile are exactly the calls a client gives
        /// up on while the editor is still working and the answer is still coming.
        ///
        /// A caller that sent no token, or whose stream has gone, gets one uninterrupted wait: the same
        /// behaviour as before any of this existed, rather than a call that fails for want of a courtesy.
        /// </summary>
        /// <param name="context">The request being answered, read for the client a notice is addressed to.</param>
        /// <param name="jobId">Job to wait for.</param>
        /// <param name="verb">Execution verb, named in the status line.</param>
        /// <param name="session">Session whose stream a notice is written to.</param>
        /// <param name="progressToken">Token progress is reported under, or null for none.</param>
        /// <param name="handoff">This response's reload registration, checked between slices.</param>
        /// <param name="record">Receives the terminal record.</param>
        /// <returns>True when the job reached a terminal state inside the budget.</returns>
        private static bool WaitForTerminalReportingProgress(
            HttpListenerContext context, string jobId, string verb, string session, JToken progressToken,
            ConduitReloadResponseHandoff.Registration handoff, out JObject record)
        {
            if (progressToken == null || string.IsNullOrEmpty(session))
                return ConduitJobLedger.WaitForTerminal(jobId, TOOL_WAIT_MS, out record);

            // A modern caller's session is synthetic and addresses no stream, so the client its bearer names
            // is what reaches it. Read from the request, which is the one source of truth for who is asking.
            var client = ConduitHttpTransport.ClientFor(context);

            var remaining = TOOL_WAIT_MS;
            var elapsed = 0;

            while (true)
            {
                var slice = Math.Min(PROGRESS_INTERVAL_MS, remaining);
                if (ConduitJobLedger.WaitForTerminal(jobId, slice, out record)) return true;

                // A reload wakes EVERY parked caller, and that wake is indistinguishable from this slice
                // expiring: the ledger returns false for both. So the handoff is tested before any decision
                // to wait again. Re-parking on a reload wake is what leaves this thread inside the wait while
                // the listener closes underneath it, which reaches the caller as a transport abort instead of
                // the answer the next domain is about to write - and, because the drain only completes once no
                // caller is parked, it also stalls the reload for the whole flush budget. Checking here is not
                // a race: transport shutdown captures the response, which sets this, and releases the waiters
                // only afterwards.
                if (handoff.HandedOff) return false;

                remaining -= slice;
                elapsed += slice;
                if (remaining <= 0) return false;

                var seconds = elapsed / 1000;
                if (ConduitHttpTransport.SendProgress(
                    session, client, progressToken, seconds,
                    verb + " is still running after " + seconds + "s")) continue;

                // Nothing accepted the notice, so there is nobody to keep telling. The rest of the budget is
                // waited out in one go rather than slicing on for frames that will each fail the same way.
                return ConduitJobLedger.WaitForTerminal(jobId, remaining, out record);
            }
        }

        /// <summary>Builds the exact MCP terminal body written onto a socket restored after domain reload.</summary>
        /// <param name="id">Typed JSON-RPC request id.</param>
        /// <param name="verb">Canonical bridge verb.</param>
        /// <param name="jobId">Durable job id.</param>
        /// <param name="record">Persisted terminal job record.</param>
        /// <returns>Compact JSON-RPC tool-result body.</returns>
        internal static string TerminalToolResponseJson(
            JToken id, string verb, string jobId, JObject record, bool modern)
        {
            string text;
            bool failed;
            TerminalToolResponse(verb, jobId, record, out text, out failed);
            return ToolResultEnvelope(id, text, failed,
                JobMeta(jobId, (string)record["state"], (long)record["revision"]), modern)
                .ToString(Formatting.None);
        }

        /// <summary>Tells the caller what to do with a job that outlasted the wait above.</summary>
        /// <param name="result">Admission result.</param>
        /// <param name="situation">How the job came to exist.</param>
        /// <returns>Human-readable instructions.</returns>
        private static string JobInstructions(ConduitJobLedger.AdmissionResult result, string situation)
        {
            return "This command is still running after this call waited for it, so it is reported as a durable "
                + "job (" + situation + ").\n\njobId: " + result.JobId + "\nstate: " + result.State
                + "\n\nRead it with the job_status tool until its state is terminal. Retrying this same call with "
                + "the same request id returns this same job rather than starting the work again.";
        }

        #endregion

        #region Responses

        /// <summary>Answers a notification at the transport layer, with no JSON-RPC body.</summary>
        /// <param name="context">The request being answered.</param>
        private static void Acknowledge(HttpListenerContext context)
        {
            try
            {
                context.Response.StatusCode = 202;
                context.Response.ContentLength64 = 0;
            }
            catch
            {
                // The caller may have gone away; nothing useful remains to report.
            }
            finally
            {
                try { context.Response.OutputStream.Close(); } catch { }
                try { context.Response.Close(); } catch { }
            }
        }

        /// <summary>Rejects a method the MCP route could not serve, naming the ones it does serve.</summary>
        /// <param name="context">The request being answered.</param>
        internal static void RespondMethodNotAllowed(HttpListenerContext context)
        {
            // Allow lists what /mcp actually serves. It named POST alone while the route also opened a stream
            // on GET and ended a session on DELETE, so the one header a client consults to learn what to try
            // instead described neither of them.
            try { context.Response.AddHeader("Allow", "GET, POST, DELETE"); } catch { }
            RespondTransportError(context, 405, "/mcp could not serve " + context.Request.HttpMethod + " for this request");
        }

        /// <summary>
        /// Answers a transport-level failure with a real HTTP status. These are failures to deliver or accept
        /// a request at all, which is why they are not dressed as JSON-RPC errors.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="status">HTTP status.</param>
        /// <param name="message">What went wrong.</param>
        internal static void RespondTransportError(HttpListenerContext context, int status, string message)
        {
            RespondTransportError(context, status, OutcomeFor(status), message);
        }

        /// <summary>Answers a transport-level failure with an explicitly named outcome.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="status">HTTP status.</param>
        /// <param name="outcome">Outcome marker.</param>
        /// <param name="message">What went wrong.</param>
        internal static void RespondTransportError(HttpListenerContext context, int status, string outcome, string message)
        {
            Write(context, status, new JObject
            {
                ["operation"] = "mcp_transport_refused",
                ["outcome"] = outcome,
                ["error"] = message,
            });
        }

        /// <summary>Names a transport refusal, so a client reads a reason rather than inferring one.</summary>
        /// <param name="status">HTTP status.</param>
        /// <returns>The outcome marker.</returns>
        private static string OutcomeFor(int status)
        {
            switch (status)
            {
                case 400: return "bad_request";
                case 405: return "method_refused";
                case 406: return "accept_refused";
                case 413: return "body_too_large";
                case 415: return "content_type_refused";
                case 429: return "capacity_refused";
                default: return "refused";
            }
        }

        /// <summary>True when a Content-Type header names exactly application/json.</summary>
        /// <param name="headerValue">Raw header value.</param>
        /// <returns>True when the body may be parsed as JSON.</returns>
        private static bool IsJsonContentType(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue)) return false;
            var token = headerValue.Split(';')[0].Trim();
            return string.Equals(token, "application/json", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when an Accept header admits an SSE response.</summary>
        /// <param name="headerValue">Raw header value.</param>
        /// <returns>True when a subscription stream may be returned.</returns>
        private static bool AcceptsEventStream(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue)) return true;

            foreach (var part in headerValue.Split(','))
            {
                var token = part.Split(';')[0].Trim();
                if (string.Equals(token, "text/event-stream", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(token, "text/*", StringComparison.OrdinalIgnoreCase)) return true;
                if (token == "*/*") return true;
            }

            return false;
        }

        /// <summary>True when an Accept header admits JSON. An absent header accepts anything.</summary>
        /// <param name="headerValue">Raw header value.</param>
        /// <returns>True when a JSON response is acceptable.</returns>
        private static bool AcceptsJson(string headerValue)
        {
            if (string.IsNullOrEmpty(headerValue)) return true;

            foreach (var part in headerValue.Split(','))
            {
                var token = part.Split(';')[0].Trim();
                if (string.Equals(token, "application/json", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(token, "application/*", StringComparison.OrdinalIgnoreCase)) return true;
                if (token == "*/*") return true;
            }

            return false;
        }

        /// <summary>Answers a JSON-RPC result.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="result">Result payload.</param>
        private static void RespondResult(HttpListenerContext context, JToken id, JObject result)
        {
            if (IsModernContext(context)) DecorateModernResult(result, false);

            Write(context, 200, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result,
            });
        }

        /// <summary>Answers a list or resource read with the modern protocol's conservative cache hints.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="result">Result payload.</param>
        private static void RespondCacheableResult(HttpListenerContext context, JToken id, JObject result)
        {
            if (IsModernContext(context)) DecorateModernResult(result, true);

            Write(context, 200, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result,
            });
        }

        /// <summary>Adds the required 2026 result discriminator, server identity and optional cache policy.</summary>
        /// <param name="result">Result object to decorate.</param>
        /// <param name="cacheable">Whether the result needs ttlMs and cacheScope.</param>
        private static void DecorateModernResult(JObject result, bool cacheable)
        {
            result["resultType"] = "complete";
            if (cacheable)
            {
                result["ttlMs"] = MODERN_CACHE_TTL_MS;
                result["cacheScope"] = MODERN_CACHE_SCOPE;
            }

            var meta = result["_meta"] as JObject ?? new JObject();
            meta[SERVER_INFO_META] = ServerInfo();
            result["_meta"] = meta;
        }

        /// <summary>Answers a JSON-RPC error. Rides on 200: the request was delivered and this is its answer.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id, or null when it could not be read.</param>
        /// <param name="code">JSON-RPC error code.</param>
        /// <param name="message">What went wrong.</param>
        private static void RespondError(HttpListenerContext context, JToken id, int code, string message)
        {
            Write(context, 200, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            });
        }

        /// <summary>Answers a modern transport validation failure with its typed protocol error.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="code">Modern protocol error code.</param>
        /// <param name="message">What did not match.</param>
        /// <param name="data">Structured error evidence, or null.</param>
        private static void RespondProtocolError(
            HttpListenerContext context, JToken id, int code, string message, JObject data = null)
        {
            var error = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) error["data"] = data;

            Write(context, 400, new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = error,
            });
        }

        /// <summary>
        /// Answers a tools/call with MCP content. A tool that failed sets isError rather than raising a
        /// protocol error, because the call was well formed and the failure is the tool's own.
        /// </summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="id">JSON-RPC id.</param>
        /// <param name="text">Result or error text.</param>
        /// <param name="isError">True when the tool failed.</param>
        /// <param name="meta">
        /// Machine-readable companion to the prose, or null. A caller that needs the job id must never have to
        /// parse the human text for it: prose is written for a reader and is free to change wording, and a
        /// consumer regexing it breaks silently the first time it does.
        /// </param>
        internal static void RespondToolResult(
            HttpListenerContext context, JToken id, string text, bool isError, JObject meta = null)
        {
            Write(context, 200, ToolResultEnvelope(id, text, isError, meta, IsModernContext(context)));
        }

        /// <summary>Builds one MCP tools/call result without binding it to a managed response stream.</summary>
        /// <param name="id">Typed JSON-RPC request id.</param>
        /// <param name="text">Human-readable terminal result.</param>
        /// <param name="isError">Whether the tool outcome is a failure.</param>
        /// <param name="meta">Machine-readable job evidence.</param>
        /// <returns>JSON-RPC response envelope.</returns>
        private static JObject ToolResultEnvelope(
            JToken id, string text, bool isError, JObject meta, bool modern)
        {
            var result = new JObject
            {
                ["content"] = new JArray { new JObject { ["type"] = "text", ["text"] = text ?? string.Empty } },
                ["isError"] = isError,
            };

            if (meta != null) result["_meta"] = meta;
            if (modern) DecorateModernResult(result, false);

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result,
            };
        }

        /// <summary>Builds the graceful terminal result for a modern subscription stream.</summary>
        /// <param name="id">Typed subscription request id.</param>
        /// <returns>Compact JSON-RPC response body.</returns>
        internal static string SubscriptionCompletionJson(JToken id)
        {
            var result = new JObject();
            DecorateModernResult(result, false);
            var meta = (JObject)result["_meta"];
            meta[SUBSCRIPTION_ID_META] = id == null ? JValue.CreateNull() : id.DeepClone();

            return new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id == null ? JValue.CreateNull() : id.DeepClone(),
                ["result"] = result,
            }.ToString(Formatting.None);
        }

        /// <summary>Structured description of a job, carried beside the prose for machine consumers.</summary>
        /// <param name="jobId">Job identifier.</param>
        /// <param name="state">Current state.</param>
        /// <param name="revision">Record revision.</param>
        /// <returns>The _meta payload.</returns>
        private static JObject JobMeta(string jobId, string state, long revision)
        {
            return new JObject
            {
                ["conduitJob"] = new JObject
                {
                    ["jobId"] = jobId,
                    ["state"] = state,
                    ["revision"] = revision,
                    ["terminal"] = ConduitJobLedger.IsTerminalState(state),
                },
            };
        }

        /// <summary>Writes one response and closes the exchange.</summary>
        /// <param name="context">The request being answered.</param>
        /// <param name="status">HTTP status.</param>
        /// <param name="payload">Body.</param>
        private static void Write(HttpListenerContext context, int status, JObject payload)
        {
            var bytes = Encoding.UTF8.GetBytes(payload.ToString(Formatting.None));
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
    }
}
