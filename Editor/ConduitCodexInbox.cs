/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Delivery to one harness whose desktop application exposes an inter-process endpoint for addressing
    /// an exact conversation.
    /// </summary>
    /// <remarks>
    /// The third of the three delivery shapes, and the one that needs nothing at all installed. That
    /// harness sends its own conversation id in the metadata envelope of every tool call, so the return
    /// address arrives free, and its application already exposes the call that puts a message into a named
    /// conversation. Neither half has to be added to the machine.
    /// <para>
    /// EVERY WAIT HERE IS BOUNDED AND NONE OF IT RUNS ON THE MAIN THREAD, which is the whole reason this
    /// class is shaped the way it is rather than as a few straight-line calls. The endpoint is a pipe that
    /// a sibling service may accept and then never answer on, so a read without a deadline is not slow but
    /// permanently stuck - and a stuck read on the editor thread takes the editor with it until the process
    /// is killed from outside. A connect deadline does not help: it guards the one step that was never
    /// going to hang.
    /// </para>
    /// <para>
    /// Candidates are also matched on an EXACT name pattern rather than a prefix. Several sibling pipes
    /// share the leading words and implement something else entirely, so a prefix match means opening
    /// endpoints that cannot answer the question being asked. Matching narrowly first, then confirming by
    /// asking for the catalogue, is what makes a silent endpoint a rejected candidate rather than a hang.
    /// </para>
    /// <para>
    /// Delivery has two shapes because a running turn and a sleeping conversation are not the same target.
    /// The conversation listing is read before every notice. An active conversation is steered through the
    /// application's owner-routing IPC, which reaches its exact running turn. An idle or unloaded
    /// conversation needs waking, so it uses the application's conversation-addressed send call. Reusing
    /// that wake call for active progress is lossy: the application can accept every call while the running
    /// turn surfaces only some of them.
    /// </para>
    /// </remarks>
    internal static class ConduitCodexInbox
    {
        #region Constants

        /// <summary>Where Windows publishes its named pipes.</summary>
        private const string PIPE_ROOT = @"\\.\pipe\";

        /// <summary>
        /// Exact shape of a candidate endpoint's name.
        /// </summary>
        /// <remarks>
        /// Anchored at both ends deliberately. Sibling services publish pipes sharing the same leading
        /// words while implementing a different protocol, and a loosened match reaches them: observed 5
        /// endpoints reached where 3 carry this protocol, and the two that could not answer are exactly the
        /// ones a prefix admits.
        /// </remarks>
        private static readonly Regex CandidateName =
            new Regex(@"^codex-browser-use-[0-9a-f-]+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>The call that places a message into a named conversation.</summary>
        private const string SEND_TOOL = "send_message_to_thread";

        /// <summary>The call that lists conversations, required as proof an endpoint is the right one.</summary>
        private const string LIST_TOOL = "list_threads";

        /// <summary>Application IPC router used to find and steer the exact window owning an active turn.</summary>
        private const string IPC_PIPE = "codex-ipc";

        /// <summary>Current application IPC version for <c>thread-follower-steer-turn</c>.</summary>
        private const int IPC_STEER_VERSION = 1;

        /// <summary>How long a connect may take before a candidate is abandoned.</summary>
        private const int CONNECT_TIMEOUT_MS = 1500;

        /// <summary>
        /// How long a real request may take before the endpoint is treated as silent.
        /// </summary>
        /// <remarks>
        /// Generous because the send is not a read: placing a message into a conversation can interrupt a
        /// turn that is already running, so the application legitimately takes seconds to answer. The
        /// working implementation of this protocol allows the same order of budget, and a shorter one here
        /// would abandon sends that were about to succeed - reporting them as unconfirmed, which is the one
        /// outcome that cannot safely be retried.
        /// </remarks>
        private const int REQUEST_TIMEOUT_MS = 30000;

        /// <summary>
        /// How long a CANDIDATE may take to answer while its identity is still being established.
        /// </summary>
        /// <remarks>
        /// Shorter than a real request on purpose. Discovery walks several candidates in turn, so the
        /// deadline is paid once per candidate rather than once, and the total is what a caller waits
        /// through. A candidate that has not answered a catalogue request in this long is not the endpoint
        /// being looked for: the right one answers immediately, and a sibling that shares the name either
        /// declines at once or never answers at all.
        /// </remarks>
        private const int DISCOVERY_TIMEOUT_MS = 1500;

        /// <summary>
        /// How long the whole delivery, discovery included, may take.
        /// </summary>
        /// <remarks>
        /// Must exceed the request deadline it contains, or this one fires first and every slow-but-healthy
        /// send is reported as unconfirmed while the request that would have succeeded is still in flight.
        /// </remarks>
        private const int DELIVERY_TIMEOUT_MS = 35000;

        /// <summary>Largest frame accepted, matching the protocol's own ceiling.</summary>
        private const int MAX_FRAME_BYTES = 8 * 1024 * 1024;

        #endregion

        #region Cached endpoint

        /// <summary>Endpoint discovered last time, so a send does not re-open every candidate.</summary>
        private static string _endpointPipe;

        /// <summary>Namespace the send call is published under, which the call must echo back.</summary>
        private static string _endpointNamespace;

        /// <summary>Guards the cached endpoint against two deliveries discovering at once.</summary>
        private static readonly object _endpointLock = new object();

        /// <summary>Forgets the cached endpoint, so the next send rediscovers.</summary>
        /// <remarks>
        /// Called when a send fails rather than on a timer. The application is restarted often and picks a
        /// new pipe each launch, so a cached name outlives its endpoint routinely - but the failure is the
        /// notification of that, and re-discovering on a schedule would be checking for something that
        /// announces itself.
        /// </remarks>
        private static void Invalidate()
        {
            lock (_endpointLock)
            {
                _endpointPipe = null;
                _endpointNamespace = null;
            }
        }

        #endregion

        #region Availability

        /// <summary>
        /// Whether that harness's application is reachable right now, for a pre-flight that has to answer
        /// before long work starts rather than after it finishes.
        /// </summary>
        /// <remarks>
        /// Answers about the ENDPOINT, not about a particular conversation. Confirming a conversation
        /// exists would mean listing them and matching, which is a second round trip to learn something the
        /// send itself reports accurately a moment later. What a pre-flight needs to rule out is the case
        /// worth stopping for: the application is not running at all, so nothing this session starts could
        /// ever be reported back.
        /// <para>
        /// Cached after the first success, so a pre-flight on every call does not reopen every candidate.
        /// </para>
        /// </remarks>
        /// <param name="detail">Receives why the application is not reachable.</param>
        /// <returns>True when a confirmed endpoint is available.</returns>
        internal static bool EndpointAvailable(out string detail)
        {
            detail = null;

            lock (_endpointLock)
            {
                if (_endpointPipe != null) return true;
            }

            string outcome = null;
            var available = false;
            var finished = new ManualResetEventSlim(false);

            var worker = new Thread(delegate ()
            {
                try
                {
                    string pipe;
                    string toolNamespace;
                    available = TryDiscover(out pipe, out toolNamespace, out outcome);
                }
                catch (Exception ex) { outcome = ex.GetType().Name + ": " + ex.Message; }
                finally { finished.Set(); }
            });

            worker.IsBackground = true;
            worker.Start();

            if (!finished.Wait(DELIVERY_TIMEOUT_MS))
            {
                detail = "that harness's application did not answer a discovery attempt in time";
                return false;
            }

            detail = outcome;
            return available;
        }

        /// <summary>Whether an exact conversation appears in the application's current live listing.</summary>
        /// <remarks>
        /// Unlike <see cref="EndpointAvailable"/>, this validates the return address itself. It is used by
        /// route selection before long work starts, where proving only that SOME Codex window exists would
        /// accept an id belonging to another harness and let the eventual result fail after the work ended.
        /// </remarks>
        /// <param name="threadId">Conversation to find.</param>
        /// <param name="detail">Receives its state or why it could not be found.</param>
        /// <returns>True when the exact conversation is listed.</returns>
        internal static bool CanDeliver(string threadId, out string detail)
        {
            detail = null;
            if (string.IsNullOrEmpty(threadId))
            {
                detail = "no Codex conversation id was given";
                return false;
            }

            string pipe;
            string toolNamespace;
            lock (_endpointLock)
            {
                pipe = _endpointPipe;
                toolNamespace = _endpointNamespace;
            }

            if (pipe == null && !TryDiscover(out pipe, out toolNamespace, out detail)) return false;

            string host;
            string status;
            string cwd;
            bool found;
            if (!TryConversation(
                    pipe, toolNamespace, threadId, out host, out status, out cwd, out found, out detail))
            {
                // Listing is read-only, so rediscovering after a stale cached endpoint cannot duplicate
                // anything. One retry covers the routine application-restart case.
                Invalidate();
                if (!TryDiscover(out pipe, out toolNamespace, out detail)) return false;
                if (!TryConversation(
                        pipe, toolNamespace, threadId, out host, out status, out cwd, out found, out detail))
                {
                    return false;
                }
            }

            if (!found)
            {
                detail = "Codex lists no conversation named " + threadId;
                return false;
            }

            detail = "Codex conversation " + threadId + " is "
                + (string.IsNullOrEmpty(status) ? "in an unknown state" : status);
            return true;
        }

        #endregion

        #region Delivery

        /// <summary>
        /// Puts one notice into a named conversation, discovering the endpoint if it is not already known.
        /// </summary>
        /// <remarks>
        /// Runs entirely on a worker with one overall deadline, so a caller on the editor thread is never
        /// blocked by an endpoint that has stopped answering.
        /// </remarks>
        /// <param name="threadId">Conversation to deliver to.</param>
        /// <param name="text">Notice body, already wrapped by the caller.</param>
        /// <param name="detail">Receives what happened, in terms a caller can act on.</param>
        /// <param name="unconfirmed">
        /// Receives true when the attempt neither succeeded nor was refused - the outcome is genuinely
        /// unknown. Reported separately because a caller routes on the difference: a refusal means try
        /// something else, while an unknown means the message may well have arrived and sending it again
        /// would duplicate it.
        /// </param>
        /// <returns>True when the application accepted the message.</returns>
        internal static bool TryDeliver(string threadId, string text, out string detail, out bool unconfirmed)
        {
            detail = null;
            unconfirmed = false;
            if (string.IsNullOrEmpty(threadId)) { detail = "no conversation was named"; return false; }

            string outcome = null;
            var delivered = false;
            var finished = new ManualResetEventSlim(false);

            var unknown = false;
            var worker = new Thread(delegate ()
            {
                try { delivered = Deliver(threadId, text, out outcome, out unknown); }
                catch (Exception ex) { outcome = ex.GetType().Name + ": " + ex.Message; }
                finally { finished.Set(); }
            });

            worker.IsBackground = true;
            worker.Start();

            if (!finished.Wait(DELIVERY_TIMEOUT_MS))
            {
                // The worker is abandoned rather than aborted. It holds only its own pipe, every read in it
                // is bounded, and it will end on its own; aborting a thread mid-write is how a half-written
                // frame reaches the endpoint.
                //
                // Unknown rather than refused, because the write may already have landed: the deadline
                // expired on the ANSWER, which says nothing about whether the message was taken.
                unconfirmed = true;
                detail = "the harness application did not answer within "
                    + (DELIVERY_TIMEOUT_MS / 1000) + " seconds, so delivery is unconfirmed";
                return false;
            }

            // BOTH results are carried out of the worker. Copying only the text and leaving the out
            // parameter at its default reported every genuinely unknown send as a refusal, which is the one
            // mistake this distinction exists to prevent: a refusal invites a retry, and retrying a message
            // that did land puts it in the conversation twice.
            unconfirmed = unknown;
            detail = outcome;
            return delivered;
        }

        /// <summary>Performs discovery and delivery on the calling worker.</summary>
        /// <param name="threadId">Conversation to deliver to.</param>
        /// <param name="text">Notice body.</param>
        /// <param name="detail">Receives what happened.</param>
        /// <param name="unconfirmed">Receives true when the outcome is genuinely unknown.</param>
        /// <returns>True when the application accepted the message.</returns>
        private static bool Deliver(string threadId, string text, out string detail, out bool unconfirmed)
        {
            unconfirmed = false;

            string pipe;
            string toolNamespace;
            lock (_endpointLock)
            {
                pipe = _endpointPipe;
                toolNamespace = _endpointNamespace;
            }

            if (pipe == null && !TryDiscover(out pipe, out toolNamespace, out detail)) return false;

            if (TrySend(pipe, toolNamespace, threadId, text, out detail, out unconfirmed)) return true;

            // An unknown outcome is never retried. The request frame was already written, so the message
            // may well be in that conversation; sending it again to find out would put it there twice.
            if (unconfirmed) return false;

            // One retry, after rediscovery. A cached endpoint that has gone is the common failure and it
            // looks identical to a refusal from here, so the endpoint is confirmed before the refusal is
            // reported as one. Only ONE retry: a second would be a loop against an application that is
            // simply not running.
            Invalidate();
            if (!TryDiscover(out pipe, out toolNamespace, out detail)) return false;

            return TrySend(pipe, toolNamespace, threadId, text, out detail, out unconfirmed);
        }

        /// <summary>
        /// Finds the endpoint that actually implements the conversation calls, by asking rather than by name.
        /// </summary>
        /// <param name="pipe">Receives the endpoint's pipe name.</param>
        /// <param name="toolNamespace">Receives the namespace the send call is published under.</param>
        /// <param name="error">Receives why no endpoint was found.</param>
        /// <returns>True when an endpoint was confirmed.</returns>
        private static bool TryDiscover(out string pipe, out string toolNamespace, out string error)
        {
            pipe = null;
            toolNamespace = null;
            error = null;

            List<string> candidates;
            try { candidates = Candidates(); }
            catch (Exception ex)
            {
                error = "the machine's named pipes could not be listed (" + ex.Message + ")";
                return false;
            }

            if (candidates.Count == 0)
            {
                error = "that harness's desktop application is not running, or is a build that does not "
                    + "publish the endpoint - no candidate pipe exists";
                return false;
            }

            var refused = 0;
            foreach (string candidate in candidates)
            {
                JObject listed;
                string requestError;
                bool asked;
                if (!TryRequest(
                        candidate, "tools/list", new JObject { ["threadStartKind"] = "all" },
                        DISCOVERY_TIMEOUT_MS, out listed, out requestError, out asked))
                {
                    refused++;
                    continue;
                }

                // Confirmed by BEHAVIOUR. The sibling endpoints answer this call by refusing it, so the
                // presence of both conversation calls is the only thing that separates the right endpoint
                // from one that merely shares its name.
                var tools = (listed["result"] as JObject) == null
                    ? null
                    : listed["result"]["tools"] as JArray;
                if (tools == null) { refused++; continue; }

                var hasList = false;
                string sendNamespace = null;
                var hasSend = false;
                foreach (JToken tool in tools)
                {
                    var name = (string)tool["name"];
                    if (name == LIST_TOOL) hasList = true;
                    if (name != SEND_TOOL) continue;

                    hasSend = true;
                    sendNamespace = (string)tool["namespace"];
                }

                if (!hasList || !hasSend) { refused++; continue; }

                lock (_endpointLock)
                {
                    _endpointPipe = candidate;
                    _endpointNamespace = sendNamespace;
                }

                pipe = candidate;
                toolNamespace = sendNamespace;
                return true;
            }

            error = "none of the " + candidates.Count + " candidate endpoints published the conversation "
                + "calls (" + refused + " declined or stayed silent), so the application is running a build "
                + "whose endpoint cannot address a conversation";
            return false;
        }

        /// <summary>Lists the pipes whose names match the endpoint's exact shape.</summary>
        /// <returns>Candidate pipe names, without the namespace prefix.</returns>
        private static List<string> Candidates()
        {
            var found = new List<string>();
            foreach (string entry in Directory.GetFiles(PIPE_ROOT))
            {
                string name = entry.StartsWith(PIPE_ROOT, StringComparison.Ordinal)
                    ? entry.Substring(PIPE_ROOT.Length)
                    : Path.GetFileName(entry);
                if (CandidateName.IsMatch(name)) found.Add(name);
            }

            return found;
        }

        /// <summary>Issues the send call against a confirmed endpoint.</summary>
        /// <param name="pipe">Endpoint pipe name.</param>
        /// <param name="toolNamespace">Namespace the send call is published under.</param>
        /// <param name="threadId">Conversation to deliver to.</param>
        /// <param name="text">Notice body.</param>
        /// <param name="detail">Receives what happened.</param>
        /// <param name="unconfirmed">Receives true when the request was written but never answered.</param>
        /// <returns>True when the application reported success.</returns>
        private static bool TrySend(
            string pipe, string toolNamespace, string threadId, string text, out string detail,
            out bool unconfirmed)
        {
            unconfirmed = false;

            // Read on EVERY notice. A running turn and a sleeping conversation need different delivery
            // paths, and caching this state recreates the exact race the split exists to avoid.
            string host;
            string status;
            string cwd;
            string hostDetail;
            bool found;
            var located = TryConversation(
                pipe, toolNamespace, threadId, out host, out status, out cwd, out found, out hostDetail);

            if (located && found && string.Equals(status, "active", StringComparison.OrdinalIgnoreCase))
            {
                string steerDetail;
                bool steerUnknown;
                if (TrySteerActive(threadId, cwd, text, out steerDetail, out steerUnknown))
                {
                    detail = "conversation " + threadId + " was active, so its owning Codex window "
                        + "accepted a thread-follower-steer-turn request (" + steerDetail + ")";
                    return true;
                }

                if (steerUnknown)
                {
                    unconfirmed = true;
                    detail = steerDetail;
                    return false;
                }

                // A refusal means nothing landed, so one immediate re-read is safe. If the turn ended
                // between the first read and the steer, the application path can now wake it. If it is
                // still active, failing truthfully is better than using the lossy active-turn path.
                string refreshedHost;
                string refreshedStatus;
                string refreshedCwd;
                string refreshDetail;
                bool refreshedFound;
                var refreshed = TryConversation(
                    pipe, toolNamespace, threadId, out refreshedHost, out refreshedStatus,
                    out refreshedCwd, out refreshedFound, out refreshDetail);

                if (!refreshed || !refreshedFound
                    || string.Equals(refreshedStatus, "active", StringComparison.OrdinalIgnoreCase))
                {
                    detail = "conversation " + threadId + " was active but its owner-routed steer failed ("
                        + steerDetail + ")"
                        + (refreshed ? string.Empty : "; its state re-read also failed (" + refreshDetail + ")");
                    return false;
                }

                host = refreshedHost;
                status = refreshedStatus;
                cwd = refreshedCwd;
                found = true;
            }

            var arguments = new JObject
            {
                ["threadId"] = threadId,
                ["prompt"] = text ?? string.Empty,
            };

            if (!string.IsNullOrEmpty(host)) arguments["hostId"] = host;

            var parameters = new JObject
            {
                ["arguments"] = arguments,
                ["callId"] = Guid.NewGuid().ToString(),

                // The target conversation is its own context. There is no separate conversation to make the
                // call from, and using one would tie delivery to some other conversation still existing.
                ["threadId"] = threadId,
                ["tool"] = SEND_TOOL,
                // Prefixed identifier with the separators kept, matching the shape the working
                // implementation of this protocol sends rather than a stripped variant of it.
                ["turnId"] = "conduit-" + Guid.NewGuid().ToString(),
            };

            if (!string.IsNullOrEmpty(toolNamespace)) parameters["namespace"] = toolNamespace;

            // Kept for the refusal below. When the conversation was not in the listing, the application's
            // own error is a generic failure that names nothing, and reporting that verbatim sends a reader
            // looking for a transport fault instead of at a conversation id that does not exist.
            var absent = located && !found;

            JObject response;
            bool wrote;
            if (!TryRequest(
                    pipe, "tools/call", parameters, REQUEST_TIMEOUT_MS, out response, out detail, out wrote))
            {
                // A decline is not silence. This endpoint refuses at the protocol level rather than by
                // answering with an unsuccessful result, so an error frame IS the refusal and the message
                // definitely did not land - only a request that went unanswered is genuinely unknown.
                unconfirmed = wrote && response == null;

                // The listing's outcome is appended HERE because this is where the failure actually
                // surfaces. The endpoint's own error names nothing at all, so without this a caller reads a
                // transport fault where the real cause is a conversation id that does not exist.
                if (absent)
                {
                    detail = "conversation " + threadId + " is not in the application's own list of "
                        + "conversations, and it declined the message (" + (detail ?? "no detail") + ")";
                }
                else if (!located)
                {
                    detail = (detail ?? "no detail") + ". Its conversation list could not be read either ("
                        + hostDetail + "), so the id may simply not name a conversation there";
                }

                return false;
            }

            var result = response["result"] as JObject;
            if (result == null)
            {
                detail = "the application answered without a result, so delivery is unconfirmed";
                return false;
            }

            var reported = ResultText(result);
            if ((bool?)result["success"] == true)
            {
                detail = "conversation " + threadId + " was "
                    + (string.IsNullOrEmpty(status) ? "not active" : status)
                    + ", so the harness application accepted the wake message"
                    + (reported.Length == 0 ? string.Empty : " (" + reported + ")")
                    + ". Acceptance by the application, not proof the model has read it.";
                return true;
            }

            if (absent)
            {
                detail = "the application refused the message because conversation " + threadId
                    + " is not in its own list of conversations"
                    + (reported.Length == 0 ? string.Empty : " (it reported: " + reported + ")");
                return false;
            }

            detail = "the application refused the message"
                + (reported.Length == 0 ? string.Empty : ": " + reported)
                + (located ? string.Empty : " (its conversation list could not be read: " + hostDetail + ")");
            return false;
        }

        /// <summary>Steers a notice into the exact active turn through Codex's owner-routing IPC.</summary>
        /// <remarks>
        /// The application tool used for idle wakeups packages its payload as a tool output. During an
        /// active turn that makes Codex issue <c>turn/start</c>, and repeated accepted starts can be omitted
        /// from the live model turn. The follower steer request carries ordinary text with no tool output,
        /// so the owning window performs a real <c>turn/steer</c> instead.
        /// </remarks>
        /// <param name="threadId">Active conversation to steer.</param>
        /// <param name="cwd">Conversation working directory from the immediately preceding state read.</param>
        /// <param name="text">Notice body.</param>
        /// <param name="detail">Receives the accepted turn id or the refusal.</param>
        /// <param name="unconfirmed">Receives true when the steer was written but no answer returned.</param>
        /// <returns>True when the owning window accepted the steer.</returns>
        private static bool TrySteerActive(
            string threadId, string cwd, string text, out string detail, out bool unconfirmed)
        {
            detail = null;
            unconfirmed = false;

            try
            {
                using (var stream = new NamedPipeClientStream(
                    ".", IPC_PIPE, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    try { stream.Connect(CONNECT_TIMEOUT_MS); }
                    catch (TimeoutException)
                    {
                        detail = "Codex IPC did not accept a connection within " + CONNECT_TIMEOUT_MS + " ms";
                        return false;
                    }

                    var initializeId = Guid.NewGuid().ToString();
                    var initialize = new JObject
                    {
                        ["type"] = "request",
                        ["requestId"] = initializeId,
                        ["method"] = "initialize",
                        ["params"] = new JObject { ["clientType"] = "conduit" },
                    };

                    string writeError;
                    if (!TryWriteFrame(stream, initialize.ToString(Formatting.None), out writeError))
                    {
                        detail = "Codex IPC initialization could not be written (" + writeError + ")";
                        return false;
                    }

                    JObject initialized;
                    if (!TryReadIpcResponse(
                            stream, initializeId, DISCOVERY_TIMEOUT_MS, out initialized, out detail))
                    {
                        return false;
                    }

                    var clientId = (string)initialized.SelectToken("result.clientId");
                    if ((string)initialized["resultType"] != "success" || string.IsNullOrEmpty(clientId))
                    {
                        detail = "Codex IPC refused initialization"
                            + (initialized["error"] == null ? string.Empty : " (" + initialized["error"] + ")");
                        return false;
                    }

                    var messageId = Guid.NewGuid().ToString();
                    var requestId = Guid.NewGuid().ToString();
                    var workspace = string.IsNullOrEmpty(cwd) ? "/" : cwd;
                    var input = new JArray
                    {
                        new JObject
                        {
                            ["type"] = "text",
                            ["text"] = text ?? string.Empty,
                            ["text_elements"] = new JArray(),
                        },
                    };
                    var restoreMessage = new JObject
                    {
                        ["id"] = messageId,
                        ["text"] = text ?? string.Empty,
                        ["context"] = new JObject
                        {
                            ["prompt"] = text ?? string.Empty,
                            ["messageThreadId"] = JValue.CreateNull(),
                            ["turnTrigger"] = "app_tool_send_message",
                            ["addedFiles"] = new JArray(),
                            ["fileAttachments"] = new JArray(),
                            ["ideContext"] = JValue.CreateNull(),
                            ["imageAttachments"] = new JArray(),
                            ["workspaceRoots"] = new JArray { workspace },
                        },
                        ["cwd"] = workspace,
                        ["createdAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        ["responsesapiClientMetadata"] = JValue.CreateNull(),
                    };
                    var steer = new JObject
                    {
                        ["type"] = "request",
                        ["requestId"] = requestId,
                        ["sourceClientId"] = clientId,
                        ["version"] = IPC_STEER_VERSION,
                        ["method"] = "thread-follower-steer-turn",
                        ["params"] = new JObject
                        {
                            ["conversationId"] = threadId,
                            ["clientUserMessageId"] = messageId,
                            ["input"] = input,
                            ["serviceTier"] = JValue.CreateNull(),
                            ["attachments"] = new JArray(),
                            ["additionalContext"] = JValue.CreateNull(),
                            ["toolOutput"] = JValue.CreateNull(),
                            ["restoreMessage"] = restoreMessage,
                        },
                        ["timeoutMs"] = REQUEST_TIMEOUT_MS,
                    };

                    if (!TryWriteFrame(stream, steer.ToString(Formatting.None), out writeError))
                    {
                        detail = "the active-turn steer could not be written (" + writeError + ")";
                        return false;
                    }

                    JObject response;
                    if (!TryReadIpcResponse(
                            stream, requestId, REQUEST_TIMEOUT_MS, out response, out detail))
                    {
                        unconfirmed = true;
                        detail = "the active-turn steer was written but not answered (" + detail + ")";
                        return false;
                    }

                    if ((string)response["resultType"] != "success")
                    {
                        detail = "Codex IPC refused the active-turn steer"
                            + (response["error"] == null ? string.Empty : " (" + response["error"] + ")");
                        return false;
                    }

                    var turnId = (string)response.SelectToken("result.result.turnId");
                    detail = string.IsNullOrEmpty(turnId) ? "accepted" : "active turn " + turnId;
                    return true;
                }
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Reads one matching Codex IPC response while declining unrelated discovery requests.</summary>
        /// <param name="stream">Open Codex IPC pipe.</param>
        /// <param name="requestId">Request whose response is wanted.</param>
        /// <param name="timeoutMs">Whole-operation deadline.</param>
        /// <param name="response">Receives the matching response.</param>
        /// <param name="error">Receives why no matching response arrived.</param>
        /// <returns>True when the matching response arrived.</returns>
        private static bool TryReadIpcResponse(
            Stream stream, string requestId, int timeoutMs, out JObject response, out string error)
        {
            response = null;
            error = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                var remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                string raw;
                if (!TryReadFrame(stream, remaining, out raw, out error)) return false;

                JObject message;
                try { message = JObject.Parse(raw); }
                catch (Exception)
                {
                    error = "Codex IPC answered with something that is not JSON";
                    return false;
                }

                if ((string)message["type"] == "response"
                    && (string)message["requestId"] == requestId)
                {
                    response = message;
                    return true;
                }

                if ((string)message["type"] != "client-discovery-request") continue;

                var discoveryId = (string)message["requestId"];
                if (string.IsNullOrEmpty(discoveryId)) continue;

                string writeError;
                if (!TryWriteFrame(stream, new JObject
                    {
                        ["type"] = "client-discovery-response",
                        ["requestId"] = discoveryId,
                        ["response"] = new JObject { ["canHandle"] = false },
                    }.ToString(Formatting.None), out writeError))
                {
                    error = "Codex IPC discovery refusal could not be written (" + writeError + ")";
                    return false;
                }
            }

            error = "Codex IPC did not answer within " + timeoutMs + " ms";
            return false;
        }

        /// <summary>Reads whatever human-readable text a result carries, without assuming its shape.</summary>
        /// <param name="result">The call's result object.</param>
        /// <returns>The text, or an empty string.</returns>
        private static string ResultText(JObject result)
        {
            var raw = RawResultText(result);
            return raw.Length == 0 ? string.Empty : Flatten(raw);
        }

        /// <summary>Reads result text without flattening or truncating structured payloads carried as text.</summary>
        /// <param name="result">The call's result object.</param>
        /// <returns>The raw text, or an empty string.</returns>
        private static string RawResultText(JObject result)
        {
            var content = result["content"] as JArray;
            if (content != null)
            {
                foreach (JToken part in content)
                {
                    var partText = (string)part["text"];
                    if (!string.IsNullOrEmpty(partText)) return partText;
                }
            }

            // The application's private pipe carries tool text in its native input-item envelope. The
            // public MCP wrapper translates the same value to content[].text, so both observed shapes are
            // accepted rather than assuming the private endpoint has already performed that translation.
            var contentItems = result["contentItems"] as JArray;
            if (contentItems != null)
            {
                foreach (JToken item in contentItems)
                {
                    var itemText = (string)item["text"];
                    if (!string.IsNullOrEmpty(itemText)) return itemText;
                }
            }

            var direct = (string)result["message"] ?? (string)result["detail"] ?? (string)result["text"];
            return direct ?? string.Empty;
        }

        /// <summary>Collapses a multi-line value so it cannot break the single-line report it lands in.</summary>
        /// <param name="value">Raw value.</param>
        /// <returns>One line, trimmed.</returns>
        private static string Flatten(string value)
        {
            string flat = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return flat.Length > 200 ? flat.Substring(0, 200) + "..." : flat;
        }

        #endregion

        #region Conversation listing

        /// <summary>
        /// Finds a conversation in the application's own listing and reads its host and live state.
        /// </summary>
        /// <remarks>
        /// Serves two purposes from one call, which is why it is worth the round trip on a notice that is
        /// sent once per piece of long work. It supplies the host the wake send should name, establishes
        /// whether the conversation exists at all, and selects the active-loop or wake delivery path.
        /// </remarks>
        /// <param name="pipe">Endpoint pipe name.</param>
        /// <param name="toolNamespace">Namespace the listing call is published under.</param>
        /// <param name="threadId">Conversation to find.</param>
        /// <param name="host">Receives the host id, or null when the conversation carries none.</param>
        /// <param name="status">Receives the application's current conversation state.</param>
        /// <param name="cwd">Receives the conversation working directory used to construct a steer.</param>
        /// <param name="found">Receives whether the conversation appears in the listing.</param>
        /// <param name="error">Receives why the listing could not be read.</param>
        /// <returns>True when the listing was read, whether or not the conversation was in it.</returns>
        private static bool TryConversation(
            string pipe, string toolNamespace, string threadId, out string host, out string status,
            out string cwd, out bool found, out string error)
        {
            host = null;
            status = null;
            cwd = null;
            found = false;
            error = null;

            var parameters = new JObject
            {
                ["arguments"] = new JObject { ["limit"] = 50 },
                ["callId"] = Guid.NewGuid().ToString(),

                // The conversation being looked for is its own context, exactly as it is for the send. An
                // id that is not a conversation makes this call fail rather than return an empty listing,
                // which the caller treats as "could not read" rather than as "not present".
                ["threadId"] = threadId,
                ["tool"] = LIST_TOOL,
                ["turnId"] = "conduit-" + Guid.NewGuid().ToString(),
            };

            if (!string.IsNullOrEmpty(toolNamespace)) parameters["namespace"] = toolNamespace;

            JObject response;
            bool wrote;
            if (!TryRequest(
                    pipe, "tools/call", parameters, DISCOVERY_TIMEOUT_MS, out response, out error, out wrote))
            {
                return false;
            }

            var result = response["result"] as JObject;
            if (result == null) { error = "the listing came back without a result"; return false; }

            // The listing arrives as text holding its own document, rather than as structured members of
            // the result, so it is parsed rather than read directly.
            var payload = RawResultText(result);
            if (payload.Length == 0) { error = "the listing came back empty"; return false; }

            JObject listing;
            try { listing = JObject.Parse(payload); }
            catch (Exception) { error = "the listing was not readable as a document"; return false; }

            // Both collections are searched. A conversation the owner has pinned is listed separately from
            // the recent ones, so reading only one of them reports a pinned conversation as absent.
            foreach (string collection in new[] { "pinnedThreads", "threads" })
            {
                var entries = listing[collection] as JArray;
                if (entries == null) continue;

                foreach (JToken entry in entries)
                {
                    if ((string)entry["id"] != threadId) continue;

                    host = (string)entry["hostId"];
                    status = (string)entry["status"];
                    cwd = (string)entry["cwd"];
                    found = true;
                    return true;
                }
            }

            return true;
        }

        #endregion

        #region Framed request

        /// <summary>
        /// Performs one request against an endpoint and returns its answer, with a deadline on every step.
        /// </summary>
        /// <param name="pipe">Pipe name.</param>
        /// <param name="method">Method to call.</param>
        /// <param name="parameters">Params object.</param>
        /// <param name="response">Receives the parsed answer.</param>
        /// <param name="error">Receives why there was no answer.</param>
        /// <param name="wroteRequest">
        /// Receives whether the request frame reached the endpoint. It separates "never asked" from "asked
        /// and heard nothing", which are the same failure to a caller that only sees a false return and
        /// opposite failures to one deciding whether a retry is safe.
        /// </param>
        /// <returns>True when an answer came back.</returns>
        private static bool TryRequest(
            string pipe, string method, JObject parameters, int readTimeoutMs, out JObject response,
            out string error, out bool wroteRequest)
        {
            response = null;
            error = null;
            wroteRequest = false;

            try
            {
                // Asynchronous is required rather than preferred: the bounded read below is built on the
                // begin/end pair, and a pipe opened without it serves those synchronously, which is the
                // unbounded read this class exists to avoid.
                using (var stream = new NamedPipeClientStream(
                    ".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    try { stream.Connect(CONNECT_TIMEOUT_MS); }
                    catch (TimeoutException)
                    {
                        error = "the endpoint did not accept a connection within "
                            + CONNECT_TIMEOUT_MS + " ms";
                        return false;
                    }

                    var request = new JObject
                    {
                        ["id"] = 1,
                        ["jsonrpc"] = "2.0",
                        ["method"] = method,
                        ["params"] = parameters ?? new JObject(),
                    };

                    if (!TryWriteFrame(stream, request.ToString(Formatting.None), out error)) return false;
                    wroteRequest = true;

                    string raw;
                    if (!TryReadFrame(stream, readTimeoutMs, out raw, out error)) return false;

                    try { response = JObject.Parse(raw); }
                    catch (Exception)
                    {
                        error = "the endpoint answered with something that is not JSON";
                        return false;
                    }

                    var failure = response["error"] as JObject;
                    if (failure == null) return true;

                    error = "the endpoint declined " + method + ": "
                        + Flatten(failure.ToString(Formatting.None));
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Writes one length-prefixed frame.</summary>
        /// <param name="stream">Open pipe.</param>
        /// <param name="json">Payload.</param>
        /// <param name="error">Receives the failure reason.</param>
        /// <returns>True on success.</returns>
        private static bool TryWriteFrame(Stream stream, string json, out string error)
        {
            error = null;

            byte[] payload = new UTF8Encoding(false).GetBytes(json);
            if (payload.Length > MAX_FRAME_BYTES)
            {
                error = "the message is larger than the endpoint's frame limit";
                return false;
            }

            byte[] header = BitConverter.GetBytes(payload.Length);

            // The protocol is little-endian on the wire regardless of what this machine is, so the bytes
            // are ordered explicitly rather than taken as the platform produced them.
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);

            stream.Write(header, 0, header.Length);
            stream.Write(payload, 0, payload.Length);
            stream.Flush();
            return true;
        }

        /// <summary>Reads one length-prefixed frame, refusing to wait indefinitely for any part of it.</summary>
        /// <param name="stream">Open pipe.</param>
        /// <param name="json">Receives the payload.</param>
        /// <param name="error">Receives the failure reason.</param>
        /// <returns>True on success.</returns>
        private static bool TryReadFrame(Stream stream, int timeoutMs, out string json, out string error)
        {
            json = null;

            byte[] header;
            if (!TryReadExactly(stream, 4, timeoutMs, out header, out error)) return false;
            if (!BitConverter.IsLittleEndian) Array.Reverse(header);

            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0 || length > MAX_FRAME_BYTES)
            {
                error = "the endpoint declared a frame length of " + length + ", which is not usable";
                return false;
            }

            byte[] payload;
            if (!TryReadExactly(stream, length, timeoutMs, out payload, out error)) return false;

            json = Encoding.UTF8.GetString(payload);
            return true;
        }

        /// <summary>
        /// Reads exactly this many bytes, or gives up. The deadline covers each read, so a stream that
        /// goes quiet part way through a frame ends the attempt rather than parking the caller on it.
        /// </summary>
        /// <param name="stream">Open pipe.</param>
        /// <param name="count">Bytes required.</param>
        /// <param name="buffer">Receives the bytes.</param>
        /// <param name="error">Receives the failure reason.</param>
        /// <returns>True when every byte arrived.</returns>
        private static bool TryReadExactly(
            Stream stream, int count, int timeoutMs, out byte[] buffer, out string error)
        {
            buffer = new byte[count];
            error = null;

            var read = 0;
            while (read < count)
            {
                IAsyncResult pending;
                try { pending = stream.BeginRead(buffer, read, count - read, null, null); }
                catch (Exception ex)
                {
                    error = "the endpoint's stream could not be read (" + ex.Message + ")";
                    return false;
                }

                if (!pending.AsyncWaitHandle.WaitOne(timeoutMs))
                {
                    // Left pending on purpose. Disposing the stream - which the caller's using block does
                    // on the way out - is what releases it; calling EndRead here would block on exactly the
                    // read that has already proven it does not return.
                    error = "the endpoint accepted the request and then stayed silent for "
                        + timeoutMs + " ms";
                    return false;
                }

                int got;
                try { got = stream.EndRead(pending); }
                catch (Exception ex)
                {
                    error = "the endpoint's stream failed mid-read (" + ex.Message + ")";
                    return false;
                }

                if (got <= 0)
                {
                    error = "the endpoint closed the connection after " + read + " of " + count + " bytes";
                    return false;
                }

                read += got;
            }

            return true;
        }

        #endregion
    }
}
