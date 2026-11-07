/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Delivers a message into a live agent session's own authenticated inbox, which is the only channel that
    /// can put the result of work in front of an agent after the call that started it has already answered.
    /// </summary>
    /// <remarks>
    /// This exists because MCP has no route for it. Every notification kind the modern protocol serves is a
    /// "refetch, your cache is stale" signal, and the one content-carrying kind needs an in-flight request
    /// holding a progress token - so anything outliving its own call has nowhere to report. The reasoning and
    /// the measured notification table are in <c>conduit-bridge-authoring.md</c>.
    /// <para>
    /// Every field read here comes from the harness's own on-disk session record rather than from anything a
    /// caller supplied, and a route is re-resolved at SEND time: a client restart rotates the process id, the
    /// inbox address AND the credential together, so a cached route addresses something that no longer
    /// exists.
    /// </para>
    /// </remarks>
    internal static class ConduitAgentInbox
    {
        #region Constants

        /// <summary>
        /// Folder holding ONE harness's live session records, under the user profile.
        /// </summary>
        /// <remarks>
        /// This is that harness's own folder and no other's. Nothing else on this machine publishes a
        /// per-session record with a messaging address in it, which is exactly why this is the only harness
        /// a notice can currently be addressed to, and why a report built from this folder must name the
        /// harness rather than claim to list the machine.
        /// </remarks>
        private const string CLAUDE_SESSIONS_RELATIVE = ".claude/sessions";

        /// <summary>Prefix every Windows named pipe path carries.</summary>
        private const string PIPE_PREFIX = @"\\.\pipe\";

        /// <summary>Session-record protocol this implementation has been verified against.</summary>
        private const int SUPPORTED_PEER_PROTOCOL = 1;

        /// <summary>Longest wait for the inbox to accept a connection.</summary>
        private const int CONNECT_TIMEOUT_MS = 5000;

        /// <summary>Longest wait for the whole send, which runs off the editor thread.</summary>
        private const int SEND_TIMEOUT_MS = 8000;

        /// <summary>
        /// Shape the pipe name must match after the prefix is removed. The LOCAL component is part of the
        /// name rather than a directory separator, so a guard that refuses every backslash refuses every real
        /// pipe; this pins the shape instead, which still cannot express traversal.
        /// </summary>
        private static readonly Regex PipeNamePattern =
            new Regex(@"^LOCAL\\cc-msg-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Where a session's own registration is kept: the messaging address and credential it handed over
        /// from its own environment.
        /// </summary>
        /// <remarks>
        /// The credential cannot be read off disk. The harness exports it only to processes the session
        /// spawned, and the key file published beside the session record is a different credential for a
        /// different caller class - writing with that one authenticates and delivers nothing. This store
        /// remains for adapters that hand their exported values to Unity instead of holding a return stream.
        /// </remarks>
        private static readonly string RegistrationsPath =
            Path.Combine("Library", "ItsRenji", "Conduit", "AgentSessions.json");

        /// <summary>Native route discriminator for Codex.</summary>
        private const string NATIVE_CODEX = "codex";

        /// <summary>Native route discriminator for Cursor.</summary>
        private const string NATIVE_CURSOR = "cursor";

        /// <summary>Native route discriminator for Antigravity.</summary>
        private const string NATIVE_ANTIGRAVITY = "antigravity";

        /// <summary>
        /// Harness identity learned by exact-session discovery. This does NOT cache live state: every route
        /// implementation re-reads its target immediately before each send.
        /// </summary>
        private static readonly Dictionary<string, string> NativeRoutes =
            new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Guards the route-identity cache.</summary>
        private static readonly object NativeRoutesLock = new object();

        /// <summary>
        /// Serializes registration reads and read-modify-writes when several sessions start together.
        /// </summary>
        private static readonly object RegistrationsLock = new object();

        #endregion

        #region Delivery states

        /// <summary>The frames were written and the inbox closed cleanly. Acceptance, not receipt.</summary>
        internal const string STATE_ACCEPTED = "accepted";

        /// <summary>No route could be verified, so nothing was written.</summary>
        internal const string STATE_REFUSED = "refused";

        /// <summary>A route verified but the write failed, so the outcome is not known.</summary>
        internal const string STATE_UNCONFIRMED = "unconfirmed";

        #endregion

        #region Route resolution

        /// <summary>One verified delivery route, resolved fresh for a single send.</summary>
        internal sealed class Route
        {
            /// <summary>The harness session this route reaches.</summary>
            public string SessionId;

            /// <summary>Working directory the session reported, used only as evidence in a report.</summary>
            public string Cwd;

            /// <summary>Process id the record named, already proven to be that exact process.</summary>
            public int Pid;

            /// <summary>Whether the inbox is a Windows named pipe, which decides how it is opened.</summary>
            public bool IsNamedPipe;

            /// <summary>
            /// The address to open: a pipe name with its prefix removed on Windows, or the unix domain
            /// socket's filesystem path on macOS and Linux.
            /// </summary>
            public string InboxAddress;

            /// <summary>Harness version, reported so a shape change is attributable.</summary>
            public string Version;

            /// <summary>Optional inbox features the record advertises.</summary>
            public string[] PeerFeatures;

            /// <summary>The credential. Never logged, never reported, never sent anywhere but the inbox.</summary>
            public string Token;
        }

        /// <summary>
        /// Finds and fully verifies the route to one session, reading the harness's own records.
        /// </summary>
        /// <param name="sessionId">Session to reach.</param>
        /// <param name="route">Receives the verified route.</param>
        /// <param name="error">Receives why no route could be verified.</param>
        /// <returns>True when a route was verified.</returns>
        internal static bool TryResolve(string sessionId, out Route route, out string error)
        {
            route = null;
            error = null;

            if (string.IsNullOrEmpty(sessionId))
            {
                error = "no session id was given";
                return false;
            }

            string folder = SessionsFolder();
            if (!Directory.Exists(folder))
            {
                error = "no agent session records exist at " + folder;
                return false;
            }

            JObject record = null;
            foreach (string path in Directory.GetFiles(folder, "*.json"))
            {
                JObject candidate = ReadJson(path);
                if (candidate == null) continue;
                if (!string.Equals((string)candidate["sessionId"], sessionId, StringComparison.Ordinal)) continue;

                record = candidate;
                break;
            }

            if (record == null)
            {
                error = "no live session record names " + sessionId
                    + "; the session may have ended, and only a running one can be reached";
                return false;
            }

            // Refused rather than attempted on an unknown protocol. The record's shape is reverse engineered
            // from a client that is free to change it, so a version this has not been verified against is a
            // reason to stop rather than to guess: a wrong guess writes a credential somewhere unintended.
            var peerProtocol = (int?)record["peerProtocol"];
            if (peerProtocol != SUPPORTED_PEER_PROTOCOL)
            {
                error = "session " + sessionId + " reports peerProtocol "
                    + (peerProtocol == null ? "(absent)" : peerProtocol.ToString())
                    + " and this build is verified against " + SUPPORTED_PEER_PROTOCOL.ToString()
                    + "; refusing rather than guessing at a changed inbox contract";
                return false;
            }

            var pid = (int?)record["pid"] ?? 0;
            if (pid <= 0)
            {
                error = "session " + sessionId + " has no usable process id";
                return false;
            }

            var socketPath = (string)record["messagingSocketPath"] ?? string.Empty;
            bool isNamedPipe = socketPath.StartsWith(PIPE_PREFIX, StringComparison.OrdinalIgnoreCase);

            string inboxAddress;
            string addressError;
            if (!TryReadInboxAddress(
                    sessionId, socketPath, isNamedPipe, out inboxAddress, out addressError))
            {
                error = addressError;
                return false;
            }

            var procStart = (string)record["procStart"] ?? string.Empty;

            string token;
            string registrationError;
            if (!TryReadRegisteredCredential(
                    sessionId, socketPath, out token, out registrationError))
            {
                error = registrationError;
                return false;
            }

            string identityError;
            if (!ProcessMatches(pid, procStart, out identityError))
            {
                error = identityError;
                return false;
            }

            var features = new List<string>();
            var advertised = record["peerFeatures"] as JArray;
            if (advertised != null)
            {
                foreach (JToken feature in advertised) features.Add(Convert.ToString(feature));
            }

            route = new Route
            {
                SessionId = sessionId,
                Cwd = (string)record["cwd"] ?? string.Empty,
                Pid = pid,
                IsNamedPipe = isNamedPipe,
                InboxAddress = inboxAddress,
                Version = (string)record["version"] ?? "(absent)",
                PeerFeatures = features.ToArray(),
                Token = token,
            };

            return true;
        }

        /// <summary>
        /// Validates the inbox address a session record reports and narrows it to what can be opened.
        /// </summary>
        /// <remarks>
        /// The two families are guarded differently because the risk differs. A pipe NAME is concatenated
        /// onto a machine-wide prefix, so a crafted one addresses somebody else's pipe and its shape is
        /// pinned exactly. A unix socket is a filesystem path, so the guard refuses a relative path and any
        /// traversal segment and stops there: pinning a file name as well would be inventing a convention
        /// that has not been observed on macOS or Linux, and refusing a real inbox is a worse failure than
        /// opening a path the harness itself wrote into its own record.
        /// </remarks>
        /// <param name="sessionId">Session the record belongs to, named in any refusal.</param>
        /// <param name="socketPath">Messaging path exactly as the record reports it.</param>
        /// <param name="isNamedPipe">Whether the path carries the Windows pipe prefix.</param>
        /// <param name="address">Receives the address to open.</param>
        /// <param name="error">Receives why the address was refused.</param>
        /// <returns>True when the address is safe to open.</returns>
        private static bool TryReadInboxAddress(
            string sessionId, string socketPath, bool isNamedPipe, out string address, out string error)
        {
            address = null;
            error = null;

            if (isNamedPipe)
            {
                string pipeName = socketPath.Substring(PIPE_PREFIX.Length);
                if (!PipeNamePattern.IsMatch(pipeName))
                {
                    error = "session " + sessionId
                        + " reports an unrecognised pipe name shape; refusing to open it";
                    return false;
                }

                address = pipeName;
                return true;
            }

            if (socketPath.Length == 0)
            {
                error = "session " + sessionId + " reports no messaging path at all";
                return false;
            }

            if (!Path.IsPathRooted(socketPath))
            {
                error = "session " + sessionId + " reports a relative messaging path; refusing to resolve "
                    + "it against the editor's own working directory";
                return false;
            }

            foreach (string segment in socketPath.Split('/'))
            {
                if (segment != "..") continue;

                error = "session " + sessionId
                    + " reports a messaging path containing a traversal segment; refusing to open it";
                return false;
            }

            address = socketPath;
            return true;
        }

        /// <summary>
        /// Reads the credential a session registered for itself.
        /// </summary>
        /// <remarks>
        /// There is no fallback, deliberately. The credential published beside the session record belongs to
        /// a different caller class: a connection presenting it authenticates and its message is delivered to
        /// nobody, so falling back to it would turn a missing registration into a silent no-op rather than an
        /// answerable refusal. Only the value the session exported to its own child processes works, and only
        /// that session can hand it over.
        /// <para>
        /// Rejected authentication closes the pipe cleanly and returns no bytes, so reading after a write
        /// cannot distinguish rejection from acceptance. Binding the credential to the live socket generation
        /// is the only available check before anything is sent.
        /// </para>
        /// </remarks>
        /// <param name="sessionId">Session whose credential is wanted.</param>
        /// <param name="liveSocketPath">Messaging socket published by the current live process.</param>
        /// <param name="token">Receives the credential.</param>
        /// <param name="error">Receives why no credential is available.</param>
        /// <returns>True when the session has registered one.</returns>
        private static bool TryReadRegisteredCredential(
            string sessionId, string liveSocketPath, out string token, out string error)
        {
            token = null;
            error = null;

            string value;
            string registeredSocket;
            lock (RegistrationsLock)
            {
                JObject registrations = ReadJson(RegistrationsPath) ?? new JObject();
                var entry = registrations[sessionId] as JObject;
                value = entry == null ? null : (string)entry["token"];
                registeredSocket = entry == null ? string.Empty : (string)entry["socket"] ?? string.Empty;
            }

            if (string.IsNullOrEmpty(value))
            {
                error = "session " + sessionId + " has not registered its messaging credential. Unity cannot "
                    + "read it: the harness exports it only to processes that session spawned, so its child "
                    + "adapter has to provide a verified return route.";
                return false;
            }

            if (!string.Equals(registeredSocket, liveSocketPath, StringComparison.OrdinalIgnoreCase))
            {
                error = "session " + sessionId + " restarted after registering its messaging credential. "
                    + "Its current socket differs from the registered socket, so the stored token belongs "
                    + "to the previous process and was refused instead of being sent. Restart that harness "
                    + "session so its child adapter can publish a current return route.";
                return false;
            }

            token = value;
            return true;
        }

        /// <summary>
        /// Records the messaging address and credential a session exported to its own child processes.
        /// </summary>
        /// <param name="sessionId">Session registering itself.</param>
        /// <param name="socketPath">Messaging socket path from that session's environment.</param>
        /// <param name="token">Messaging credential from that session's environment.</param>
        /// <param name="error">Receives why the registration was refused.</param>
        /// <returns>True when the registration was stored.</returns>
        internal static bool StoreRegistration(
            string sessionId, string socketPath, string token, out string error)
        {
            error = null;

            if (string.IsNullOrEmpty(sessionId)) { error = "sessionId is required"; return false; }
            if (string.IsNullOrEmpty(token)) { error = "token is required"; return false; }

            var path = socketPath ?? string.Empty;
            if (!path.StartsWith(PIPE_PREFIX, StringComparison.OrdinalIgnoreCase)
                || !PipeNamePattern.IsMatch(path.Substring(PIPE_PREFIX.Length)))
            {
                error = "socket is not a messaging pipe of the expected shape";
                return false;
            }

            try
            {
                lock (RegistrationsLock)
                {
                    JObject registrations = ReadJson(RegistrationsPath) ?? new JObject();
                    registrations[sessionId] = new JObject
                    {
                        ["socket"] = path,
                        ["token"] = token,
                        ["registeredUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    };

                    Directory.CreateDirectory(Path.GetDirectoryName(RegistrationsPath));
                    File.WriteAllText(
                        RegistrationsPath, registrations.ToString(Formatting.Indented),
                        new UTF8Encoding(false));
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Reports whether a session has registered a credential, without revealing it.</summary>
        /// <param name="sessionId">Session to test.</param>
        /// <returns>True when a credential is on record.</returns>
        internal static bool HasRegistration(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return false;

            lock (RegistrationsLock)
            {
                var entry = (ReadJson(RegistrationsPath) ?? new JObject())[sessionId] as JObject;
                return entry != null && !string.IsNullOrEmpty((string)entry["token"]);
            }
        }

        /// <summary>
        /// Confirms the recorded process id really is the process the record describes, by its start time.
        /// </summary>
        /// <remarks>
        /// Start time is used rather than the executable's name because it answers both questions at once: a
        /// reused id belongs to a process that started later, and the harness runs several processes whose
        /// names are identical, so matching on the image would happily accept the wrong one.
        /// </remarks>
        /// <param name="pid">Process id from the record.</param>
        /// <param name="procStart">Expected start stamp, as a Windows file time.</param>
        /// <param name="error">Receives why the process could not be confirmed.</param>
        /// <returns>True when the live process matches the record.</returns>
        private static bool ProcessMatches(int pid, string procStart, out string error)
        {
            error = null;

            long expected;
            if (!long.TryParse(procStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out expected))
            {
                error = "the session record carries no readable process start stamp, so its process cannot be "
                    + "confirmed and a reused id would go unnoticed";
                return false;
            }

            Process process;
            try { process = Process.GetProcessById(pid); }
            catch (Exception)
            {
                error = "process " + pid.ToString() + " is not running, so that session has gone";
                return false;
            }

            try
            {
                long actual = process.StartTime.ToFileTime();
                if (actual == expected) return true;

                error = "process " + pid.ToString() + " started at " + actual.ToString(CultureInfo.InvariantCulture)
                    + " and the record expects " + expected.ToString(CultureInfo.InvariantCulture)
                    + ", so the id has been reused by a different process; refusing to write to it";
                return false;
            }
            catch (Exception ex)
            {
                error = "process " + pid.ToString() + " could not be inspected to confirm its identity: "
                    + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
        }

        #endregion

        #region Sending

        /// <summary>
        /// Resolves an unregistered session against every harness that exposes an addressable native inbox.
        /// </summary>
        /// <remarks>
        /// Exact target discovery is the discriminator. A UUID shape identifies none of these harnesses,
        /// and merely finding an application's endpoint proves only that SOME conversation can be reached.
        /// The winning harness id is cached because a session cannot change vendors, but reachability and
        /// active/idle state are never cached and are re-read by that harness on every notice.
        /// </remarks>
        /// <param name="sessionId">Exact conversation id.</param>
        /// <param name="routeKind">Receives the native harness route.</param>
        /// <param name="detail">Receives the resolution evidence or failures.</param>
        /// <returns>True when exactly one harness currently lists the target.</returns>
        internal static bool TryResolveNativeRoute(
            string sessionId, out string routeKind, out string detail)
        {
            routeKind = null;
            detail = null;
            if (string.IsNullOrEmpty(sessionId))
            {
                detail = "no session id was given";
                return false;
            }

            string cachedRoute;
            lock (NativeRoutesLock)
            {
                NativeRoutes.TryGetValue(sessionId, out cachedRoute);
            }

            if (!string.IsNullOrEmpty(cachedRoute))
            {
                string cachedDetail;
                bool stillReachable = cachedRoute == NATIVE_CODEX
                    ? ConduitCodexInbox.CanDeliver(sessionId, out cachedDetail)
                    : cachedRoute == NATIVE_CURSOR
                        ? ConduitCursorInbox.CanDeliver(sessionId, out cachedDetail)
                        : ConduitAntigravityInbox.CanDeliver(sessionId, out cachedDetail);
                detail = cachedDetail;
                if (stillReachable)
                {
                    routeKind = cachedRoute;
                    return true;
                }

                // The id remains tied to this harness. Dropping the cache permits a later restarted
                // process to be found, but this call reports the fresh reachability failure truthfully.
                lock (NativeRoutesLock) NativeRoutes.Remove(sessionId);
                routeKind = null;
                return false;
            }

            var matches = new List<string>();
            var reports = new List<string>();

            string cursor;
            if (ConduitCursorInbox.CanDeliver(sessionId, out cursor)) matches.Add(NATIVE_CURSOR);
            else reports.Add("Cursor: " + cursor);

            string antigravity;
            if (ConduitAntigravityInbox.CanDeliver(sessionId, out antigravity))
            {
                matches.Add(NATIVE_ANTIGRAVITY);
            }
            else reports.Add("Antigravity: " + antigravity);

            string codex;
            if (ConduitCodexInbox.CanDeliver(sessionId, out codex)) matches.Add(NATIVE_CODEX);
            else reports.Add("Codex: " + codex);

            if (matches.Count == 0)
            {
                detail = "no native harness lists session " + sessionId + " ("
                    + string.Join("; ", reports.ToArray()) + ")";
                return false;
            }

            if (matches.Count > 1)
            {
                detail = "session " + sessionId + " is listed by several native harnesses ("
                    + string.Join(", ", matches.ToArray()) + "), so delivery is refused as ambiguous";
                return false;
            }

            routeKind = matches[0];
            lock (NativeRoutesLock) NativeRoutes[sessionId] = routeKind;
            detail = routeKind == NATIVE_CODEX ? codex
                : routeKind == NATIVE_CURSOR ? cursor : antigravity;
            return true;
        }

        /// <summary>Delivers through the exact native route selected by live target discovery.</summary>
        /// <param name="sessionId">Exact conversation id.</param>
        /// <param name="body">Notice body.</param>
        /// <param name="detail">Receives the native outcome.</param>
        /// <param name="unconfirmed">Receives whether a written request went unanswered.</param>
        /// <returns>True when the native harness accepted the notice.</returns>
        private static bool TryDeliverNative(
            string sessionId, string body, out string detail, out bool unconfirmed)
        {
            unconfirmed = false;

            string routeKind;
            lock (NativeRoutesLock) NativeRoutes.TryGetValue(sessionId, out routeKind);

            // Build start resolves and caches the harness once. A notice then asks that harness directly,
            // whose delivery method performs the one fresh active/idle read required before the send. If a
            // domain reload cleared this in-memory identity, resolve it again once; state is never cached.
            if (string.IsNullOrEmpty(routeKind))
            {
                string routeDetail;
                if (!TryResolveNativeRoute(sessionId, out routeKind, out routeDetail))
                {
                    detail = routeDetail;
                    return false;
                }
            }

            if (routeKind == NATIVE_CURSOR)
            {
                return ConduitCursorInbox.TryDeliver(
                    sessionId, body ?? string.Empty, out detail, out unconfirmed);
            }

            if (routeKind == NATIVE_ANTIGRAVITY)
            {
                return ConduitAntigravityInbox.TryDeliver(
                    sessionId, body ?? string.Empty, out detail, out unconfirmed);
            }

            return ConduitCodexInbox.TryDeliver(
                sessionId, body ?? string.Empty, out detail, out unconfirmed);
        }

        /// <summary>
        /// Writes one message into a session's inbox, resolving the route first so nothing stale is used.
        /// </summary>
        /// <param name="sessionId">Session to reach.</param>
        /// <param name="body">Message text, already framed as out-of-band tool output by the caller.</param>
        /// <param name="detail">Receives what happened, in terms a caller can act on.</param>
        /// <returns>One of the delivery states on this class.</returns>
        internal static string Send(string sessionId, string body, out string detail)
        {
            // Tried first because a held socket is LIVE evidence that the receiver is there, where a
            // registration file only records that it once was. The two never compete in practice - a session
            // reached this way belongs to a harness that publishes no address to register - so this is an
            // ordering by evidence quality rather than a precedence rule between harnesses.
            if (ConduitHttpTransport.HasAgentStream(sessionId))
            {
                string streamError;
                if (ConduitHttpTransport.SendToAgentSession(sessionId, body ?? string.Empty, out streamError))
                {
                    detail = "the notice was written to the session's plugin stream, which injects it into "
                        + "that conversation. Acceptance by the plugin, not proof the model has read it.";
                    return STATE_ACCEPTED;
                }

                // Deliberately not falling through to the pipe. The stream WAS there when this started, so a
                // failed write is a receiver that went away mid-send, and reporting that as "no route" would
                // send the caller looking for a registration that was never the mechanism here.
                detail = streamError;
                return STATE_UNCONFIRMED;
            }

            // No registered address means the remaining route is the harness whose desktop application
            // addresses a conversation directly. Tried BEFORE refusing, because having nothing registered
            // is that harness's normal state rather than a fault: it publishes no address, so there was
            // never anything for a session there to hand over.
            if (!HasRegistration(sessionId))
            {
                string nativeDetail;
                bool nativeUnconfirmed;
                if (TryDeliverNative(
                        sessionId, body ?? string.Empty, out nativeDetail, out nativeUnconfirmed))
                {
                    detail = nativeDetail;
                    return STATE_ACCEPTED;
                }

                detail = nativeDetail;
                return nativeUnconfirmed ? STATE_UNCONFIRMED : STATE_REFUSED;
            }

            Route route;
            string error;
            if (!TryResolve(sessionId, out route, out error))
            {
                detail = error;
                return STATE_REFUSED;
            }

            string messageId = Guid.NewGuid().ToString();
            string authLine = new JObject
            {
                ["type"] = "auth",
                ["token"] = route.Token,
            }.ToString(Formatting.None);

            string messageLine = new JObject
            {
                ["msgV"] = 1,
                ["msg_id"] = messageId,
                ["type"] = "user",
                ["from"] = "Conduit",
                ["message"] = new JObject { ["role"] = "user", ["content"] = body ?? string.Empty },
                ["priority"] = "next",
            }.ToString(Formatting.None);

            string failure = null;
            var finished = new ManualResetEventSlim(false);

            // Off the editor thread on purpose: a connect and a drain both block, and blocking the main thread
            // here would stall the very editor whose work this message is reporting on.
            var worker = new Thread(delegate ()
            {
                try
                {
                    if (route.IsNamedPipe) WriteToPipe(route.InboxAddress, authLine, messageLine);
                    else WriteToSocket(route.InboxAddress, authLine, messageLine);
                }
                catch (Exception ex)
                {
                    failure = ex.GetType().Name + ": " + ex.Message;
                }
                finally
                {
                    finished.Set();
                }
            });

            worker.IsBackground = true;
            worker.Start();

            if (!finished.Wait(SEND_TIMEOUT_MS))
            {
                detail = "the inbox for session " + sessionId + " did not answer within "
                    + SEND_TIMEOUT_MS.ToString() + " ms, so whether it took the message is unknown";
                return STATE_UNCONFIRMED;
            }

            if (failure != null)
            {
                detail = "writing to the inbox for session " + sessionId + " failed: " + failure;
                return STATE_UNCONFIRMED;
            }

            // Deliberately not called delivered. A clean close proves the transport accepted the frames and
            // nothing more: the harness queues the message subject to its own permission and admission rules,
            // which can hold it for approval, decline it, expire it, or drop it, and none of those are visible
            // from this end. Claiming delivery here would be the blanket-success bug.
            detail = "wrote 2 frames to session " + sessionId + " (pid " + route.Pid.ToString()
                + ", harness " + route.Version + ") and the inbox closed cleanly. The harness queues it under "
                + "its own permission rules, so acceptance is not proof the agent saw it.";
            return STATE_ACCEPTED;
        }

        /// <summary>Writes both frames into a Windows named pipe inbox.</summary>
        /// <param name="pipeName">Pipe name with its prefix removed.</param>
        /// <param name="authLine">Authentication frame.</param>
        /// <param name="messageLine">Message frame.</param>
        private static void WriteToPipe(string pipeName, string authLine, string messageLine)
        {
            using (var pipe = new NamedPipeClientStream(
                ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                pipe.Connect(CONNECT_TIMEOUT_MS);
                WriteFrames(pipe, authLine, messageLine);

                try { pipe.WaitForPipeDrain(); }
                catch (PlatformNotSupportedException) { /* Acceptance is still the strongest claim. */ }
                catch (IOException) { /* The peer closed first, which is not a failure to write. */ }
            }
        }

        /// <summary>Writes both frames into a unix domain socket inbox.</summary>
        /// <remarks>
        /// A blocking Connect on a Socket takes no timeout, so the asynchronous form is used purely to
        /// bound that wait. The half-close afterwards is what a pipe drain is here: it hands the peer an
        /// end of stream so it can finish reading, rather than leaving it waiting on a socket the editor is
        /// about to dispose.
        /// </remarks>
        /// <param name="socketPath">Absolute filesystem path of the inbox socket.</param>
        /// <param name="authLine">Authentication frame.</param>
        /// <param name="messageLine">Message frame.</param>
        private static void WriteToSocket(string socketPath, string authLine, string messageLine)
        {
            using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                IAsyncResult pending = socket.BeginConnect(
                    new UnixDomainSocketEndPoint(socketPath), null, null);
                if (!pending.AsyncWaitHandle.WaitOne(CONNECT_TIMEOUT_MS))
                {
                    throw new TimeoutException(
                        "the inbox socket did not accept a connection within "
                        + CONNECT_TIMEOUT_MS.ToString(CultureInfo.InvariantCulture) + " ms");
                }

                socket.EndConnect(pending);

                using (var stream = new NetworkStream(socket, false))
                {
                    WriteFrames(stream, authLine, messageLine);
                }

                try { socket.Shutdown(SocketShutdown.Send); }
                catch (SocketException) { /* The peer closed first, which is not a failure to write. */ }
                catch (ObjectDisposedException) { /* Same, where the peer's close got there first. */ }
            }
        }

        /// <summary>Writes the two newline-terminated frames the inbox protocol expects.</summary>
        /// <remarks>
        /// Two separate frames with no byte-order mark: the inbox reads line-delimited JSON, so a mark on
        /// the first line corrupts the auth frame and a single combined object is not the contract. The
        /// stream is left open because the caller owns how it is shut down.
        /// </remarks>
        /// <param name="stream">Connected inbox stream.</param>
        /// <param name="authLine">Authentication frame.</param>
        /// <param name="messageLine">Message frame.</param>
        private static void WriteFrames(Stream stream, string authLine, string messageLine)
        {
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
            {
                writer.NewLine = "\n";
                writer.WriteLine(authLine);
                writer.WriteLine(messageLine);
                writer.Flush();
            }
        }

        #endregion

        #region Reading records

        /// <summary>Lists every live session record, for a report that has to say what is reachable.</summary>
        /// <returns>Each record's session id, working directory, process id and status.</returns>
        internal static List<JObject> LiveSessions()
        {
            var found = new List<JObject>();
            string folder = SessionsFolder();
            if (!Directory.Exists(folder)) return found;

            foreach (string path in Directory.GetFiles(folder, "*.json"))
            {
                JObject record = ReadJson(path);
                if (record != null && !string.IsNullOrEmpty((string)record["sessionId"])) found.Add(record);
            }

            return found;
        }

        /// <summary>Absolute path to the harness's session record folder.</summary>
        /// <returns>The folder, whether or not it exists.</returns>
        private static string SessionsFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                CLAUDE_SESSIONS_RELATIVE.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>Reads one JSON file, treating anything unreadable as absent rather than throwing.</summary>
        /// <param name="path">File to read.</param>
        /// <returns>The parsed object, or null.</returns>
        private static JObject ReadJson(string path)
        {
            try { return JObject.Parse(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception) { return null; }
        }

        #endregion
    }
}
