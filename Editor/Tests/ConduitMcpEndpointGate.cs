/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Gate for the MCP endpoint's protocol surface.
//
// Most of these checks exist because the endpoint got them wrong at least once. Two are worth reading as
// history rather than as tests: conduit-run once bypassed the editor-state block because classification
// inspected the wrapper instead of the command it carried, and notifications once skipped both session and
// protocol validation because they were acknowledged before either check ran. Both were checks placed after
// the branch that avoided them, and both are pinned here so they cannot come back quietly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit.Tests
{
    /// <summary>Exercises the JSON-RPC and HTTP contract of the /mcp endpoint.</summary>
    internal static class ConduitMcpEndpointGate
    {
        #region Requests

        /// <summary>Milliseconds a call waits when it does not need the main thread.</summary>
        private const int TIMEOUT_MS = 5000;

        /// <summary>Milliseconds a call waits while the drain is being pumped for it.</summary>
        private const int PUMPED_TIMEOUT_MS = 10000;

        /// <summary>One call's outcome.</summary>
        private sealed class Reply
        {
            /// <summary>HTTP status, or 0 when the call never got one.</summary>
            public int Status;

            /// <summary>Response body.</summary>
            public string Body;

            /// <summary>Session header returned by initialize.</summary>
            public string Session;

            /// <summary>Parsed body, or an empty object when it did not parse.</summary>
            public JObject Json
            {
                get
                {
                    try { return JObject.Parse(Body); }
                    catch { return new JObject(); }
                }
            }
        }

        /// <summary>
        /// Reads only the response HEAD, then abandons the connection without touching the body.
        ///
        /// This is how an endpoint that answers with an ENDLESS response gets tested at all. A GET on /mcp
        /// opens the server-to-client stream, so reading its body never returns; and because this gate runs
        /// on the main thread while both ends of the socket live in this process, a blocking read there
        /// deadlocks the editor outright - the keepalive arrives before the read timeout expires and resets
        /// it, forever, with no repaint and no chance to save. Headers arrive as soon as the response starts,
        /// which is all the evidence a transport check needs.
        /// </summary>
        /// <param name="descriptor">Connection facts.</param>
        /// <param name="method">HTTP method to send.</param>
        /// <returns>Status and content type, with no body read.</returns>
        private static Reply CallHead(
            ConduitHttpTransportGate.Descriptor descriptor,
            string method,
            string payload = null,
            Dictionary<string, string> headers = null)
        {
            HttpWebRequest request = null;
            try
            {
                request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + descriptor.Port + "/mcp");
                request.Method = method;
                request.Timeout = TIMEOUT_MS;
                request.Headers.Add("X-Conduit-Project", descriptor.Identity);
                request.Headers.Add("Authorization", "Bearer " + descriptor.Token);

                if (headers != null)
                {
                    foreach (var pair in headers)
                    {
                        if (string.Equals(pair.Key, "Accept", StringComparison.OrdinalIgnoreCase))
                            request.Accept = pair.Value;
                        else request.Headers.Add(pair.Key, pair.Value);
                    }
                }

                if (method == "POST")
                {
                    request.ContentType = "application/json";
                    var body = Encoding.UTF8.GetBytes(payload ?? string.Empty);
                    request.ContentLength = body.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                }

                // Deliberately NOT disposed. Disposing a response whose body never ends makes .NET try to
                // drain it so the connection can be reused, which blocks exactly as a body read would. The
                // status and content type are already in hand; the finally below drops the socket instead.
                var response = (HttpWebResponse)request.GetResponse();
                return new Reply { Status = (int)response.StatusCode, Body = response.ContentType ?? string.Empty };
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null) return new Reply { Status = 0, Body = ex.Message };

                using (response)
                {
                    return new Reply { Status = (int)response.StatusCode, Body = response.ContentType ?? string.Empty };
                }
            }
            catch (Exception ex)
            {
                return new Reply { Status = 0, Body = ex.Message };
            }
            finally
            {
                // Dropped deliberately, and this is the load-bearing line: leaving the request alive holds a
                // stream open on the server, and abandoning it here is what lets the keepalive notice the
                // peer has gone and reap it.
                if (request != null) { try { request.Abort(); } catch { } }
            }
        }

        /// <summary>Posts one message to /mcp.</summary>
        /// <param name="descriptor">Connection facts.</param>
        /// <param name="payload">Raw body.</param>
        /// <param name="headers">Extra headers, or null.</param>
        /// <param name="method">HTTP method.</param>
        /// <param name="contentType">Content type, or null to omit.</param>
        /// <returns>The reply.</returns>
        private static Reply Call(
            ConduitHttpTransportGate.Descriptor descriptor,
            string payload,
            Dictionary<string, string> headers,
            string method = "POST",
            string contentType = "application/json")
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + descriptor.Port + "/mcp");
                request.Method = method;
                request.Timeout = TIMEOUT_MS;
                request.Headers.Add("X-Conduit-Project", descriptor.Identity);
                request.Headers.Add("Authorization", "Bearer " + descriptor.Token);
                if (contentType != null) request.ContentType = contentType;

                if (headers != null)
                {
                    foreach (var pair in headers)
                    {
                        if (string.Equals(pair.Key, "Accept", StringComparison.OrdinalIgnoreCase)) request.Accept = pair.Value;
                        else request.Headers.Add(pair.Key, pair.Value);
                    }
                }

                if (method == "POST")
                {
                    var body = Encoding.UTF8.GetBytes(payload ?? string.Empty);
                    request.ContentLength = body.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return new Reply
                    {
                        Status = (int)response.StatusCode,
                        Body = reader.ReadToEnd(),
                        Session = response.Headers["Mcp-Session-Id"],
                    };
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null) return new Reply { Status = 0, Body = ex.Message };

                using (response)
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return new Reply
                    {
                        Status = (int)response.StatusCode,
                        Body = reader.ReadToEnd(),
                        Session = response.Headers["Mcp-Session-Id"],
                    };
                }
            }
            catch (Exception ex)
            {
                return new Reply { Status = 0, Body = ex.Message };
            }
        }

        /// <summary>
        /// Posts a call that needs the main thread, pumping the drain from here. The drain and this gate share
        /// a thread, so a blocking call would otherwise deadlock against itself.
        /// </summary>
        /// <param name="descriptor">Connection facts.</param>
        /// <param name="payload">Raw body.</param>
        /// <param name="headers">Extra headers.</param>
        /// <returns>The reply.</returns>
        private static Reply CallPumped(
            ConduitHttpTransportGate.Descriptor descriptor, string payload, Dictionary<string, string> headers)
        {
            Reply reply = null;
            var worker = new Thread(() => { reply = Call(descriptor, payload, headers); });
            worker.IsBackground = true;
            worker.Start();

            var deadline = DateTime.UtcNow.AddMilliseconds(PUMPED_TIMEOUT_MS);
            while (reply == null && DateTime.UtcNow < deadline)
            {
                ConduitHttpTransport.Drain();
                Thread.Sleep(5);
            }

            worker.Join(500);
            return reply ?? new Reply { Status = 0, Body = "timed out" };
        }

        /// <summary>Builds a JSON-RPC message.</summary>
        /// <param name="id">Id, or null for a notification.</param>
        /// <param name="method">Method name.</param>
        /// <param name="parameters">Params, or null.</param>
        /// <returns>The serialised message.</returns>
        private static string Rpc(object id, string method, JObject parameters = null)
        {
            var message = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (id != null) message["id"] = JToken.FromObject(id);
            if (parameters != null) message["params"] = parameters;
            return message.ToString(Newtonsoft.Json.Formatting.None);
        }

        /// <summary>Adds the reserved 2026 request envelope to a params object.</summary>
        /// <param name="parameters">Method parameters, or null for an otherwise empty object.</param>
        /// <returns>The same object with modern metadata.</returns>
        private static JObject ModernParams(JObject parameters = null)
        {
            parameters = parameters ?? new JObject();
            parameters["_meta"] = new JObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientInfo"] = new JObject
                {
                    ["name"] = "conduit-gate",
                    ["version"] = "1.0.0",
                },
                ["io.modelcontextprotocol/clientCapabilities"] = new JObject(),
            };
            return parameters;
        }

        /// <summary>Builds the standard Streamable HTTP routing headers for one modern request.</summary>
        /// <param name="method">JSON-RPC method.</param>
        /// <param name="name">Principal name for a named method, or null.</param>
        /// <param name="accept">Accepted response type.</param>
        /// <returns>Request headers.</returns>
        private static Dictionary<string, string> ModernHeaders(
            string method, string name = null, string accept = "application/json")
        {
            var headers = new Dictionary<string, string>
            {
                { "MCP-Protocol-Version", "2026-07-28" },
                { "Mcp-Method", method },
                { "Accept", accept },
            };
            if (name != null) headers.Add("Mcp-Name", name);
            return headers;
        }

        /// <summary>Reads a tools/call result's text.</summary>
        /// <param name="reply">The reply.</param>
        /// <returns>The first content block's text, or an empty string.</returns>
        private static string ToolText(Reply reply)
        {
            try { return (string)reply.Json["result"]["content"][0]["text"] ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>
        /// Reads the job id a tool result carries in its _meta companion. The prose is written for a reader
        /// and is free to change wording, so scraping it for the id breaks silently the first time it does.
        /// </summary>
        /// <param name="reply">The reply.</param>
        /// <returns>The job id, or an empty string when the result carries none.</returns>
        private static string JobIdOf(Reply reply)
        {
            try { return (string)reply.Json["result"]["_meta"]["conduitJob"]["jobId"] ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>Reads a JSON-RPC error code.</summary>
        /// <param name="reply">The reply.</param>
        /// <returns>The code, or 0.</returns>
        private static int ErrorCode(Reply reply)
        {
            var code = reply.Json["error"] == null ? null : reply.Json["error"]["code"];
            return code == null ? 0 : (int)code;
        }

        /// <summary>Reads the job state a tools/call result carries in its metadata.</summary>
        /// <param name="reply">The reply.</param>
        /// <returns>The state, or an empty string when the result carries none.</returns>
        private static string ReadJobState(Reply reply)
        {
            try { return (string)reply.Json["result"]["_meta"]["conduitJob"]["state"] ?? string.Empty; }
            catch { return string.Empty; }
        }

        /// <summary>Reads whether a tools/call result is flagged as an error.</summary>
        /// <param name="reply">The reply.</param>
        /// <returns>The isError flag, or false when it cannot be read.</returns>
        private static bool ReadIsError(Reply reply)
        {
            try { return (bool)reply.Json["result"]["isError"]; }
            catch { return false; }
        }

        #endregion

        #region Gate

        /// <summary>Records one check's result.</summary>
        /// <param name="report">Report sink.</param>
        /// <param name="label">Check name.</param>
        /// <param name="ok">Whether it held.</param>
        /// <param name="detail">Observed value, printed either way.</param>
        /// <returns>The same result, for accumulation.</returns>
        private static bool Check(StringBuilder report, string label, bool ok, string detail)
        {
            report.AppendLine((ok ? "  PASS  " : "  FAIL  ") + label + "  ->  " + detail);
            return ok;
        }

        /// <summary>Runs every protocol check and reports.</summary>
        [MenuItem("Tools/Conduit Gates/Verify MCP endpoint")]
        private static void Verify()
        {
            var report = new StringBuilder("MCP endpoint - production ConduitMcpEndpoint\n");

            string descriptorError;
            var descriptor = ConduitHttpTransportGate.ReadDescriptor(out descriptorError);
            if (descriptor == null)
            {
                Debug.LogError("[McpGate] " + report + "  FAIL  descriptor: " + descriptorError);
                return;
            }

            var ok = true;

            // ---- transport layer -------------------------------------------------------------------
            // GET opens the stream, so it is checked through CallHead, which reads the response head and
            // never the body. Calling it through Call() instead deadlocks the editor permanently - see the
            // note on CallHead - so this check must never be "simplified" back to the other helper.
            var stream = CallHead(descriptor, "GET");
            ok &= Check(report, "GET opens an event stream",
                stream.Status == 200 && stream.Body.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0,
                stream.Status + " " + stream.Body);

            // The same check carrying a negotiated protocol version, because that was the case carved out: a
            // client declaring the modern revision was answered 405 and left with no server-to-client channel
            // at all, since a subscription it never opens is not a replacement for the stream it asked for.
            // The unversioned check above passed throughout, which is exactly why nothing caught it, so the
            // version has to be sent explicitly or this gate re-admits the bug.
            var modernStream = CallHead(descriptor, "GET", null,
                new Dictionary<string, string> { { "MCP-Protocol-Version", "2026-07-28" } });
            ok &= Check(report, "GET opens an event stream for a modern client",
                modernStream.Status == 200
                && modernStream.Body.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0,
                modernStream.Status + " " + modernStream.Body);

            var del = Call(descriptor, null, null, "DELETE", null);
            ok &= Check(report, "DELETE naming no session is refused",
                del.Status == 400, del.Status + " " + (string)del.Json["outcome"]);

            // Given its own session, minted and ended here, so ending one cannot disturb the session every
            // later check in this gate runs on.
            var doomed = Call(descriptor, Rpc(1, "initialize", new JObject()), null);
            var doomedDelete = Call(descriptor, null,
                new Dictionary<string, string> { { "Mcp-Session-Id", doomed.Session } }, "DELETE", null);
            ok &= Check(report, "DELETE ends the session it names",
                doomedDelete.Status == 200 && (string)doomedDelete.Json["outcome"] == "forgotten",
                doomedDelete.Status + " " + (string)doomedDelete.Json["outcome"]);

            // And ending it twice is not reported as having dropped something the second time.
            var doomedAgain = Call(descriptor, null,
                new Dictionary<string, string> { { "Mcp-Session-Id", doomed.Session } }, "DELETE", null);
            ok &= Check(report, "ending an already ended session reports holding nothing",
                doomedAgain.Status == 200 && (string)doomedAgain.Json["outcome"] == "nothing_held",
                doomedAgain.Status + " " + (string)doomedAgain.Json["outcome"]);

            var origin = Call(descriptor, Rpc(1, "ping"), new Dictionary<string, string> { { "Origin", "https://evil.example" } });
            ok &= Check(report, "a browser Origin is refused",
                origin.Status == 403 && (string)origin.Json["outcome"] == "origin_refused",
                origin.Status + " " + (string)origin.Json["outcome"]);

            var badType = Call(descriptor, Rpc(1, "ping"), null, "POST", "application/jsonevil");
            ok &= Check(report, "a content type that merely contains application/json is refused",
                badType.Status == 415, badType.Status + " " + (string)badType.Json["outcome"]);

            var badAccept = Call(descriptor, Rpc(1, "ping"), new Dictionary<string, string> { { "Accept", "text/plain" } });
            ok &= Check(report, "an Accept that excludes JSON is refused",
                badAccept.Status == 406, badAccept.Status + " " + (string)badAccept.Json["outcome"]);

            // ---- lifecycle -------------------------------------------------------------------------
            var initNoId = Call(descriptor, Rpc(null, "initialize", new JObject()), null);
            ok &= Check(report, "initialize without an id is refused",
                initNoId.Status == 400, initNoId.Status + " " + (string)initNoId.Json["outcome"]);

            var initBadParams = Call(descriptor, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":\"nope\"}", null);
            ok &= Check(report, "initialize with non-object params is refused",
                ErrorCode(initBadParams) == -32602, "code=" + ErrorCode(initBadParams));

            var init = Call(descriptor, Rpc(1, "initialize", new JObject()), null);
            var session = init.Session;
            var instructions = (string)(init.Json["result"] == null ? null : init.Json["result"]["instructions"]) ?? string.Empty;
            ok &= Check(report, "initialize mints a session", init.Status == 200 && !string.IsNullOrEmpty(session),
                init.Status + " session=" + (string.IsNullOrEmpty(session) ? "missing" : "minted"));

            // The catalogue republishes on every reload, so a handshake claiming a static tool list licenses
            // a client to keep a definition that changed in place for good. The declaration has to be true.
            var capabilities = init.Json["result"] == null ? null : init.Json["result"]["capabilities"] as JObject;
            var toolsCapability = capabilities == null ? null : capabilities["tools"] as JObject;
            var declared = toolsCapability == null || toolsCapability["listChanged"] == null
                ? "absent"
                : toolsCapability["listChanged"].ToString();
            ok &= Check(report, "initialize declares the tool list can change",
                string.Equals(declared, "True", StringComparison.Ordinal), "tools.listChanged=" + declared);

            // The conventions document is the payload here, not a friendly blurb. A short instructions string
            // means the hard stops never reach the agent.
            ok &= Check(report, "initialize serves the full conventions document",
                instructions.Length > 5000 && instructions.StartsWith("MANDATORY FIRST STEP")
                && instructions.Contains("udon-batch-edit"),
                instructions.Length + " chars");

            var withSession = new Dictionary<string, string> { { "Mcp-Session-Id", session } };

            // ---- modern lifecycle -------------------------------------------------------------------
            var discover = Call(descriptor, Rpc(100, "server/discover", ModernParams()),
                ModernHeaders("server/discover"));
            var discoverResult = discover.Json["result"] as JObject;
            var supported = discoverResult == null ? null : discoverResult["supportedVersions"] as JArray;
            ok &= Check(report, "server/discover negotiates the modern era without a session",
                discover.Status == 200 && string.IsNullOrEmpty(discover.Session)
                && supported != null && supported.ToString().Contains("2026-07-28")
                && (string)discoverResult["resultType"] == "complete",
                discover.Status + " session=" + (string.IsNullOrEmpty(discover.Session) ? "absent" : "present"));

            ok &= Check(report, "modern responses identify the server in reserved metadata",
                discoverResult != null
                && (string)discoverResult["_meta"]["io.modelcontextprotocol/serverInfo"]["name"] == "conduit",
                discoverResult == null ? "no result" : "conduit");

            var modernList = Call(descriptor, Rpc(101, "tools/list", ModernParams()),
                ModernHeaders("tools/list"));
            var modernListResult = modernList.Json["result"] as JObject;
            var modernTools = modernListResult == null ? null : modernListResult["tools"] as JArray;
            ok &= Check(report, "modern tools/list is stateless and carries conservative cache hints",
                modernList.Status == 200 && modernTools != null
                && modernTools.Count == ConduitMcpCatalog.ToolCount
                && (string)modernListResult["resultType"] == "complete"
                && (int)modernListResult["ttlMs"] == 0
                && (string)modernListResult["cacheScope"] == "private",
                modernTools == null ? "no tools" : modernTools.Count + " tools");

            var missingMethodHeader = Call(descriptor, Rpc(102, "tools/list", ModernParams()),
                new Dictionary<string, string>
                {
                    { "MCP-Protocol-Version", "2026-07-28" },
                    { "Accept", "application/json" },
                });
            ok &= Check(report, "a modern request missing Mcp-Method is refused",
                missingMethodHeader.Status == 400 && ErrorCode(missingMethodHeader) == -32020,
                missingMethodHeader.Status + " code=" + ErrorCode(missingMethodHeader));

            var wrongName = Call(descriptor,
                Rpc(103, "tools/call", ModernParams(new JObject
                {
                    ["name"] = "status",
                    ["arguments"] = new JObject(),
                })),
                ModernHeaders("tools/call", "not-status"));
            ok &= Check(report, "a modern named request with a mismatched Mcp-Name is refused",
                wrongName.Status == 400 && ErrorCode(wrongName) == -32020,
                wrongName.Status + " code=" + ErrorCode(wrongName));

            var modernStatus = Call(descriptor,
                Rpc(104, "tools/call", ModernParams(new JObject
                {
                    ["name"] = "status",
                    ["arguments"] = new JObject(),
                })),
                ModernHeaders("tools/call", "status"));
            ok &= Check(report, "a modern tool call is stateless and returns a discriminated result",
                modernStatus.Status == 200 && ToolText(modernStatus).Contains("isUdonSharpCreatePending")
                && (string)modernStatus.Json["result"]["resultType"] == "complete",
                modernStatus.Status + " " + ToolText(modernStatus).Replace("\r\n", " "));

            var modernPing = Call(descriptor, Rpc(105, "ping", ModernParams()), ModernHeaders("ping"));
            ok &= Check(report, "modern ping is absent from the 2026 method registry",
                ErrorCode(modernPing) == -32601, "code=" + ErrorCode(modernPing));

            var listenParams = ModernParams(new JObject
            {
                ["notifications"] = new JObject { ["toolsListChanged"] = true },
            });
            var listen = CallHead(descriptor, "POST", Rpc("listen-gate", "subscriptions/listen", listenParams),
                ModernHeaders("subscriptions/listen", null, "text/event-stream"));
            ok &= Check(report, "subscriptions/listen opens a modern event stream",
                listen.Status == 200
                && listen.Body.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0,
                listen.Status + " " + listen.Body);

            // ---- subsequent-request headers, on requests AND notifications --------------------------
            var noSession = Call(descriptor, Rpc(2, "ping"), null);
            ok &= Check(report, "a request without a session is refused",
                noSession.Status == 400 && (string)noSession.Json["outcome"] == "session_required",
                noSession.Status + " " + (string)noSession.Json["outcome"]);

            var notifyNoSession = Call(descriptor, Rpc(null, "notifications/initialized"), null);
            ok &= Check(report, "a NOTIFICATION without a session is refused too",
                notifyNoSession.Status == 400 && (string)notifyNoSession.Json["outcome"] == "session_required",
                notifyNoSession.Status + " " + (string)notifyNoSession.Json["outcome"]);

            var badVersionHeaders = new Dictionary<string, string>(withSession) { { "MCP-Protocol-Version", "1999-01-01" } };
            var badVersion = Call(descriptor, Rpc(3, "ping"), badVersionHeaders);
            ok &= Check(report, "an unsupported protocol version is refused",
                badVersion.Status == 400 && (string)badVersion.Json["outcome"] == "protocol_version_refused",
                badVersion.Status + " " + (string)badVersion.Json["outcome"]);

            var notifyBadVersion = Call(descriptor, Rpc(null, "notifications/initialized"), badVersionHeaders);
            ok &= Check(report, "a NOTIFICATION with a bad protocol version is refused too",
                notifyBadVersion.Status == 400, notifyBadVersion.Status + " " + (string)notifyBadVersion.Json["outcome"]);

            var olderVersion = Call(descriptor, Rpc(4, "ping"),
                new Dictionary<string, string>(withSession) { { "MCP-Protocol-Version", "2025-03-26" } });
            ok &= Check(report, "an older supported version is accepted", olderVersion.Status == 200,
                olderVersion.Status.ToString());

            var noVersion = Call(descriptor, Rpc(5, "ping"), withSession);
            ok &= Check(report, "a missing version header is assumed, not refused", noVersion.Status == 200,
                noVersion.Status.ToString());

            var realClientAccept = Call(descriptor, Rpc(6, "ping"),
                new Dictionary<string, string>(withSession) { { "Accept", "application/json, text/event-stream" } });
            ok &= Check(report, "a real client's Accept is honoured", realClientAccept.Status == 200,
                realClientAccept.Status.ToString());

            var notify = Call(descriptor, Rpc(null, "notifications/initialized"), withSession);
            ok &= Check(report, "a valid notification is acknowledged with no body",
                notify.Status == 202 && notify.Body.Length == 0, notify.Status + " bodyLen=" + notify.Body.Length);

            // ---- JSON-RPC shape --------------------------------------------------------------------
            var badJson = Call(descriptor, "{not json", withSession);
            ok &= Check(report, "invalid JSON is a parse error", ErrorCode(badJson) == -32700, "code=" + ErrorCode(badJson));

            var batch = Call(descriptor, "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}]", withSession);
            ok &= Check(report, "a batch is refused", ErrorCode(batch) == -32600, "code=" + ErrorCode(batch));

            var objectId = Call(descriptor, "{\"jsonrpc\":\"2.0\",\"id\":{\"a\":1},\"method\":\"ping\"}", withSession);
            ok &= Check(report, "a non-scalar id is refused", ErrorCode(objectId) == -32600, "code=" + ErrorCode(objectId));

            var unknownMethod = Call(descriptor, Rpc(7, "no/such/method"), withSession);
            ok &= Check(report, "an unknown method is refused", ErrorCode(unknownMethod) == -32601,
                "code=" + ErrorCode(unknownMethod));

            // ---- tools -----------------------------------------------------------------------------
            var list = Call(descriptor, Rpc(8, "tools/list"), withSession);
            var listed = list.Json["result"] == null ? null : list.Json["result"]["tools"] as JArray;
            ok &= Check(report, "tools/list serves the published catalogue",
                list.Status == 200 && listed != null && listed.Count == ConduitMcpCatalog.ToolCount,
                (listed == null ? "none" : listed.Count.ToString()) + " tools, revision "
                + (string)list.Json["result"]["_catalogueRevision"]);

            var unknownTool = Call(descriptor, Rpc(9, "tools/call", new JObject { ["name"] = "__nope__" }), withSession);
            ok &= Check(report, "an unknown tool is refused", ErrorCode(unknownTool) == -32602,
                "code=" + ErrorCode(unknownTool));

            var badArguments = Call(descriptor,
                "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{\"name\":\"ping\",\"arguments\":\"nope\"}}",
                withSession);
            ok &= Check(report, "non-object arguments are refused", ErrorCode(badArguments) == -32602,
                "code=" + ErrorCode(badArguments));

            var ping = CallPumped(descriptor,
                Rpc(11, "tools/call", new JObject { ["name"] = "ping", ["arguments"] = new JObject() }), withSession);
            ok &= Check(report, "an immediate tool round-trips", ping.Status == 200 && ToolText(ping) == "pong",
                ping.Status + " " + ToolText(ping));

            ok &= Check(report, "a non-authoring result carries no conventions reminder",
                !ToolText(ping).Contains("[Conduit] Authoring command"), "clean");

            // Call rather than CallPumped is the whole assertion here. ping above needs the pump because an
            // immediate tool still runs on the main thread, and this gate holds that thread, so nothing is
            // draining. A tool that answers anyway never needed it - which is the point of these two: a
            // client asking what the editor is doing must not queue behind the work it is asking about.
            var unpumpedStatus = Call(descriptor,
                Rpc(20, "tools/call", new JObject { ["name"] = "status", ["arguments"] = new JObject() }), withSession);
            ok &= Check(report, "status round-trips without pumping the main thread",
                unpumpedStatus.Status == 200 && ToolText(unpumpedStatus).Contains("isUdonSharpCreatePending"),
                unpumpedStatus.Status + " " + ToolText(unpumpedStatus).Replace("\r\n", " "));

            var unpumpedState = Call(descriptor,
                Rpc(21, "tools/call", new JObject { ["name"] = "editor-application-get-state", ["arguments"] = new JObject() }),
                withSession);
            ok &= Check(report, "editor-application-get-state round-trips without pumping",
                unpumpedState.Status == 200 && ToolText(unpumpedState).Contains("timeSinceStartup"),
                unpumpedState.Status + " " + ToolText(unpumpedState));

            // conduit-run is a wrapper, and everything downstream must classify the verb it CARRIES rather
            // than the wrapper itself - otherwise a durable job is recorded against a verb that never ran.
            // The conventions reminder is the observable: it is attached from the execution verb, so a
            // wrapper-classified admission would come back without it. The specimen is an authoring verb
            // aimed at an absent target, so the admitted job reports a miss and changes nothing.
            var wrappedSession = new Dictionary<string, string>
            {
                { "Mcp-Session-Id", "mcp-gate-wrap-" + Guid.NewGuid().ToString("N").Substring(0, 8) },
            };
            var runWrapped = CallPumped(descriptor,
                Rpc(12, "tools/call", new JObject
                {
                    ["name"] = "conduit-run",
                    ["arguments"] = new JObject
                    {
                        ["command"] = "rename-object __conduit_gate_absent__ | __conduit_gate_x__",
                    },
                }), wrappedSession);
            var runWrappedText = ToolText(runWrapped);
            ok &= Check(report, "conduit-run is classified by the verb it carries",
                runWrapped.Status == 200 && runWrappedText.StartsWith("[Conduit] Authoring command"),
                runWrappedText.Split('\n')[0]);

            var runPing = CallPumped(descriptor,
                Rpc(13, "tools/call", new JObject
                {
                    ["name"] = "conduit-run",
                    ["arguments"] = new JObject { ["command"] = "ping" },
                }), withSession);
            ok &= Check(report, "conduit-run still reaches an allowed verb",
                runPing.Status == 200 && ToolText(runPing) == "pong", ToolText(runPing));

            // ---- durable admission -----------------------------------------------------------------
            var durableSession = new Dictionary<string, string> { { "Mcp-Session-Id", "mcp-gate-" + Guid.NewGuid().ToString("N").Substring(0, 8) } };
            var admitted = CallPumped(descriptor,
                Rpc("gate-1", "tools/call", new JObject
                {
                    ["name"] = "console-clear-logs",
                    ["arguments"] = new JObject(),
                }), durableSession);

            var admittedText = ToolText(admitted);
            var jobId = JobIdOf(admitted);
            ok &= Check(report, "a durable tool returns a job", admitted.Status == 200 && jobId.Length > 0,
                jobId.Length > 0 ? jobId : admittedText.Substring(0, Math.Min(60, admittedText.Length)));

            // console-clear-logs clears a view; it authors nothing, so it must not carry the reminder.
            ok &= Check(report, "a non-authoring admission carries no reminder",
                !admittedText.Contains("[Conduit] Authoring command"), "clean");

            // A second admission with a verb the classification does treat as authoring. Testing the reminder
            // with console-clear-logs would have been testing the wrong verb and calling the result a defect.
            var authoringSession = new Dictionary<string, string>
            {
                { "Mcp-Session-Id", "mcp-gate-auth-" + Guid.NewGuid().ToString("N").Substring(0, 8) },
            };
            var authoring = CallPumped(descriptor,
                Rpc("gate-auth", "tools/call", new JObject
                {
                    ["name"] = "rename-object",
                    ["arguments"] = new JObject
                    {
                        ["target"] = "__conduit_gate_absent__",
                        ["newName"] = "__conduit_gate_x__",
                    },
                }), authoringSession);
            var authoringText = ToolText(authoring);
            var authoringJob = JobIdOf(authoring);

            ok &= Check(report, "an authoring admission carries the conventions reminder",
                authoringText.StartsWith("[Conduit] Authoring command"),
                authoringText.Split('\n')[0]);

            var retried = Call(descriptor,
                Rpc("gate-1", "tools/call", new JObject
                {
                    ["name"] = "console-clear-logs",
                    ["arguments"] = new JObject(),
                }), durableSession);
            ok &= Check(report, "the same request id resolves onto the same job",
                jobId.Length > 0 && JobIdOf(retried) == jobId, JobIdOf(retried) + " vs " + jobId);

            // Drain so the job reaches a terminal state before it is read back.
            var settle = DateTime.UtcNow.AddMilliseconds(PUMPED_TIMEOUT_MS);
            while (DateTime.UtcNow < settle)
            {
                ConduitHttpTransport.Drain();
                Thread.Sleep(5);
            }

            var status = Call(descriptor,
                Rpc(14, "tools/call", new JObject
                {
                    ["name"] = "job_status",
                    ["arguments"] = new JObject { ["jobId"] = jobId },
                }), durableSession);
            var statusText = ToolText(status);
            ok &= Check(report, "job_status reads the stored record",
                status.Status == 200 && statusText.Contains("\"state\": \"completed\""),
                status.Status + " " + (statusText.Length > 0 ? "record returned" : "empty"));

            ok &= Check(report, "the reminder is not repeated on every poll",
                !statusText.Contains("[Conduit] Authoring command"), "clean");

            // ---- the retry that lands on a job which has already finished ----------------------------
            // This is the path a lost response takes, and for an authoring verb it is the ordinary one rather
            // than an edge case, because those verbs cause the reloads that destroy the socket carrying their
            // own first answer. It used to return the raw ledger record, with no conventions reminder and with
            // isError false however the job had actually ended.
            var terminalRetry = Call(descriptor,
                Rpc("gate-auth", "tools/call", new JObject
                {
                    ["name"] = "rename-object",
                    ["arguments"] = new JObject
                    {
                        ["target"] = "__conduit_gate_absent__",
                        ["newName"] = "__conduit_gate_x__",
                    },
                }), authoringSession);

            var terminalText = ToolText(terminalRetry);
            var terminalState = ReadJobState(terminalRetry);
            var terminalIsError = ReadIsError(terminalRetry);

            ok &= Check(report, "a retry after completion resolves onto the finished job",
                terminalState.Length > 0 && ConduitJobLedger.IsTerminalState(terminalState),
                terminalState.Length > 0 ? terminalState : "unreadable");

            ok &= Check(report, "an authoring retry after completion still carries the reminder",
                terminalText.StartsWith("[Conduit] Authoring command"),
                terminalText.Length == 0 ? "empty" : terminalText.Split('\n')[0]);

            ok &= Check(report, "a terminal retry answers with the outcome, not the ledger record",
                terminalText.Length > 0 && !terminalText.Contains("\"schemaVersion\""),
                terminalText.Contains("\"schemaVersion\"") ? "raw record returned" : "outcome text");

            ok &= Check(report, "isError is false for a retry whose job completed",
                terminalIsError == (terminalState != "completed"),
                "state " + terminalState + ", isError " + terminalIsError);

            // The live fixture completes, so the check above cannot tell a derived isError from a hardcoded
            // false, and it passed against the very code this pair was written to catch. A synthetic terminal
            // record exercises the half that matters: work which finished without succeeding must not come
            // back as a success, because a caller reading isError would then retry nothing and move on broken.
            string failedText;
            bool failedFlag;
            ConduitMcpEndpoint.TerminalToolResponse("rename-object", "job-gate-synthetic", new JObject
            {
                ["state"] = "failed",
                ["revision"] = 3L,
                ["outcome"] = new JObject { ["operation"] = "command_failed", ["text"] = "synthetic failure" },
            }, out failedText, out failedFlag);

            ok &= Check(report, "a retry whose job did not complete is reported as an error",
                failedFlag && failedText.Contains("failed") && failedText.Contains("synthetic failure"),
                "isError " + failedFlag);

            ok &= Check(report, "a retry that did not complete still carries the reminder",
                failedText.StartsWith("[Conduit] Authoring command"),
                failedText.Length == 0 ? "empty" : "reminder present");

            var created = new List<string>();
            if (jobId.Length > 0) created.Add(jobId);
            if (authoringJob.Length > 0 && authoringJob != jobId) created.Add(authoringJob);

            string cleanupError;
            var cleaned = ConduitJobLedger.CleanupTerminalGateJobs(created, out cleanupError);
            ok &= Check(report, "gate removes the jobs it created", cleaned,
                cleaned ? created.Count + " id(s)" : cleanupError);

            report.AppendLine();
            report.AppendLine(ok ? "PASS - production MCP endpoint" : "FAIL - see the checks above");
            if (ok) Debug.Log("[McpGate] " + report);
            else Debug.LogError("[McpGate] " + report);
        }

        #endregion
    }
}
