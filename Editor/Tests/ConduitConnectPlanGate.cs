/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Gate for the production connect plan.
//
// The checks that matter are not the string ones. An artifact that merely looks right is the failure this
// gate exists to catch, so the emitted endpoint and headers are fired at the LIVE transport: the
// authenticated case must get past authentication, and dropping either header must be refused. A header name
// transcribed wrongly, a path that moved, or a bearer left out all turn red on behaviour rather than on a
// comparison between two copies of the same guess.
//
// Each client's rendered entry is checked on its own text - the JSON entry is parsed back, the TOML header
// table is read back out - and those recovered values are the ones sent to the endpoint, so a quoting or key
// mistake inside a fragment is caught rather than bypassed by rebuilding it from the same helper that made it.
//
// Which file a client is pointed at is checked too, because that is what broke here once: the endpoint entry
// was correct and was written into the store an app reads for servers it spawns itself rather than the one its
// sessions read per project. The client dropped it at load, and the window, reading the same file back,
// reported the client as not connected. A string check cannot see that and a live request cannot either - only
// asserting the path and the nesting can.
//
// Building a plan writes nothing, and the last check proves it rather than asserting it: this project's entry
// in every client config is fingerprinted before the run and compared after. The entry rather than the file,
// because a running client rewrites its own config while the gate is going and a length that moved for that
// reason is not this code writing. Applying is a separate, deliberate act in the window.
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
    /// <summary>Runs the connect plan against fixed values and against the live transport.</summary>
    internal static class ConduitConnectPlanGate
    {
        #region Constants

        /// <summary>Port used by the fixed-value snapshot. Nothing binds it; it only has to be stable.</summary>
        private const int FIXTURE_PORT = 27999;

        /// <summary>Server key used by the fixed-value snapshot.</summary>
        private const string FIXTURE_SERVER = "fixture-project";

        /// <summary>Identity used by the fixed-value snapshot.</summary>
        private const string FIXTURE_IDENTITY = "fixtureidentitydigest";

        /// <summary>Bearer used by the fixed-value snapshot.</summary>
        private const string FIXTURE_TOKEN = "fixturebearertoken";

        /// <summary>Project root used by the fixed-value snapshot, as a per-project config would key it.</summary>
        private const string FIXTURE_PROJECT_ROOT = "D:/fixture/unity project";

        /// <summary>
        /// The endpoint the fixed-value snapshot must produce, written out rather than composed by calling the
        /// plan's own helper. Comparing the plan against itself would pass whatever it did.
        /// </summary>
        private const string FIXTURE_URL = "http://127.0.0.1:27999/mcp";

        /// <summary>How long a live request may take before it is treated as a failure.</summary>
        private const int REQUEST_TIMEOUT_MS = 4000;

        #endregion

        #region Live requests

        /// <summary>One live reply, reduced to what a check asserts on.</summary>
        private sealed class Reply
        {
            /// <summary>HTTP status, or 0 when no reply arrived at all.</summary>
            public int Status;

            /// <summary>Response body, or the transport error when nothing arrived.</summary>
            public string Body;
        }

        /// <summary>
        /// Issues one request on a worker thread while pumping the main-thread dispatcher, which is the shape
        /// the transport gate already uses. A synchronous call from a menu item would hold the main thread that
        /// some endpoints need in order to answer, and would deadlock rather than fail.
        /// </summary>
        /// <param name="url">Absolute URL to request.</param>
        /// <param name="headers">Headers to send; an entry may be omitted to test a refusal.</param>
        /// <returns>The reply, or a zero status when none arrived.</returns>
        private static Reply Send(string url, Dictionary<string, string> headers)
        {
            Reply reply = null;

            var worker = new Thread(() => { reply = SendBlocking(url, headers); });
            worker.IsBackground = true;
            worker.Start();

            var deadline = DateTime.UtcNow.AddMilliseconds(REQUEST_TIMEOUT_MS);
            while (reply == null && DateTime.UtcNow < deadline)
            {
                ConduitHttpTransport.Drain();
                Thread.Sleep(5);
            }

            worker.Join(500);
            return reply ?? new Reply { Status = 0, Body = "no reply before the deadline" };
        }

        /// <summary>Performs the request itself, turning a refusal into a reply rather than an exception.</summary>
        /// <param name="url">Absolute URL to request.</param>
        /// <param name="headers">Headers to send.</param>
        /// <returns>The reply.</returns>
        private static Reply SendBlocking(string url, Dictionary<string, string> headers)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);

                // POST, not GET. An authenticated GET on /mcp is the server-to-client STREAM: it is answered
                // by a response that deliberately never ends, so using it as an auth probe waits for a body
                // that is not coming and reports a timeout as though the credentials had failed. A POST is
                // refused or answered promptly either way, which is what a probe needs.
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = REQUEST_TIMEOUT_MS;
                request.ReadWriteTimeout = REQUEST_TIMEOUT_MS;

                foreach (var header in headers)
                {
                    // Authorization is a restricted header on HttpWebRequest and has to go through the
                    // property rather than the collection, which throws for it.
                    if (header.Key == ConduitConnectPlan.AUTHORIZATION_HEADER) request.Headers[HttpRequestHeader.Authorization] = header.Value;
                    else request.Headers[header.Key] = header.Value;
                }

                // Deliberately not a valid call. What is being tested is whether the credentials get PAST
                // authentication, and a request that is refused for its content has already proved that.
                var body = Encoding.UTF8.GetBytes("{}");
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return new Reply { Status = (int)response.StatusCode, Body = ReadBody(response) };
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null) return new Reply { Status = 0, Body = ex.Message };

                using (response)
                {
                    return new Reply { Status = (int)response.StatusCode, Body = ReadBody(response) };
                }
            }
            catch (Exception ex)
            {
                return new Reply { Status = 0, Body = ex.Message };
            }
        }

        /// <summary>Reads a response body, tolerating a response that carries none.</summary>
        /// <param name="response">Response to read.</param>
        /// <returns>The body, or an empty string.</returns>
        private static string ReadBody(HttpWebResponse response)
        {
            var stream = response.GetResponseStream();
            if (stream == null) return string.Empty;

            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        #endregion

        #region Snapshots

        /// <summary>The fixed-value snapshot every string check is built from.</summary>
        /// <returns>A snapshot with a complete transport.</returns>
        private static ConduitConnectSnapshot Fixture()
        {
            return new ConduitConnectSnapshot
            {
                ServerName  = FIXTURE_SERVER,
                Port        = FIXTURE_PORT,
                Identity    = FIXTURE_IDENTITY,
                Token       = FIXTURE_TOKEN,
                ProjectRoot = FIXTURE_PROJECT_ROOT,
            };
        }

        /// <summary>The live snapshot, used only by the checks that exercise the running transport.</summary>
        /// <returns>A snapshot carrying this editor's real port, identity and bearer.</returns>
        private static ConduitConnectSnapshot Live()
        {
            return new ConduitConnectSnapshot
            {
                ServerName  = FIXTURE_SERVER,
                Port        = ConduitHttpTransport.Port,
                Identity    = ConduitHttpTransport.Identity,
                Token       = ConduitHttpTransport.Token,
                ProjectRoot = ConduitWindow.LiveSnapshot().ProjectRoot,
            };
        }

        /// <summary>Finds one client's entry in a plan.</summary>
        /// <param name="plan">The plan to search.</param>
        /// <param name="client">Client display name.</param>
        /// <returns>The entry, or null when the client is absent.</returns>
        private static ConduitConnectEntry Entry(List<ConduitConnectEntry> plan, string client)
        {
            foreach (var entry in plan)
            {
                if (entry.Client == client) return entry;
            }

            return null;
        }

        /// <summary>
        /// Reads the header table back out of an emitted TOML fragment. The values come from the text that
        /// would actually be written rather than from the plan's header builder, so a quoting or key mistake
        /// made while rendering is caught instead of bypassed.
        /// </summary>
        /// <param name="toml">The emitted fragment.</param>
        /// <returns>Header name to value, empty when the table cannot be found.</returns>
        private static Dictionary<string, string> HeadersFromToml(string toml)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(toml)) return headers;

            var at = toml.IndexOf("http_headers", StringComparison.Ordinal);
            if (at < 0) return headers;

            var open = toml.IndexOf('{', at);
            var close = open < 0 ? -1 : toml.IndexOf('}', open);
            if (open < 0 || close < 0) return headers;

            foreach (var pair in toml.Substring(open + 1, close - open - 1).Split(','))
            {
                var split = pair.IndexOf('=');
                if (split < 0) continue;

                var key = pair.Substring(0, split).Trim().Trim('"');
                var value = pair.Substring(split + 1).Trim().Trim('"');
                if (key.Length > 0) headers[key] = value;
            }

            return headers;
        }

        /// <summary>Reads the header map back out of an emitted JSON fragment, for the same reason.</summary>
        /// <param name="json">The emitted fragment.</param>
        /// <param name="serverName">Key the entry sits under.</param>
        /// <param name="projectKey">Key the server map is nested under, or null when it sits at the root.</param>
        /// <returns>Header name to value, empty when the entry cannot be read.</returns>
        private static Dictionary<string, string> HeadersFromJson(string json, string serverName, string projectKey)
        {
            var headers = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(json)) return headers;

            try
            {
                // Walked by hand rather than through the plan's own navigator. The point of reading the
                // rendered text back is to catch a mistake made while rendering it, and a shared helper would
                // make the same mistake on the way in and on the way out.
                var document = JObject.Parse(json);
                var owner = string.IsNullOrEmpty(projectKey) ? document : document["projects"][projectKey] as JObject;
                var table = owner["mcpServers"][serverName]["headers"] as JObject;
                if (table == null) return headers;

                foreach (var property in table.Properties()) headers[property.Name] = (string)property.Value;
            }
            catch
            {
                // An unreadable fragment yields no headers, and the check that wanted them fails.
            }

            return headers;
        }

        #endregion

        #region Gate

        /// <summary>Runs every check and reports what each one saw.</summary>
        /// <returns>A human-readable report.</returns>
        internal static string Run()
        {
            var report = new StringBuilder();
            var passed = 0;
            var failed = 0;

            Action<bool, string, string> check = (ok, name, saw) =>
            {
                if (ok) passed++;
                else failed++;
                report.AppendLine("  " + (ok ? "PASS" : "FAIL") + "  " + name + (saw == null ? string.Empty : "   [" + saw + "]"));
            };

            var before = FingerprintConfigs();
            var plan = ConduitConnectPlan.Build(Fixture());

            // Every declared client answers. A client missing from the plan reads as one needing no action.
            var missing = new List<string>();
            foreach (var client in ConduitConnectPlan.Clients)
            {
                if (Entry(plan, client) == null) missing.Add(client);
            }

            check(missing.Count == 0 && plan.Count == ConduitConnectPlan.Clients.Length,
                "every declared client has exactly one entry",
                plan.Count + " of " + ConduitConnectPlan.Clients.Length + (missing.Count == 0 ? string.Empty : ", missing " + string.Join(", ", missing.ToArray())));

            // The Claude Code command, which is the artifact that carries credentials on a command line.
            var code = Entry(plan, "Claude Code");
            check(code != null && code.State == ConduitConnectState.Emitted && code.Form == ConduitConnectForm.CliCommand,
                "Claude Code emits a command", code == null ? "absent" : code.State + "/" + code.Form);

            var command = code == null ? string.Empty : code.Artifact ?? string.Empty;
            check(command.Contains("--transport http"), "the command selects the http transport", null);
            check(command.Contains(FIXTURE_URL), "the command carries the endpoint", FIXTURE_URL);
            check(command.Contains("--header \"" + ConduitConnectPlan.IDENTITY_HEADER + ": " + FIXTURE_IDENTITY + "\""),
                "the command carries the identity header", ConduitConnectPlan.IDENTITY_HEADER);
            check(command.Contains("--header \"" + ConduitConnectPlan.AUTHORIZATION_HEADER + ": Bearer " + FIXTURE_TOKEN + "\""),
                "the command carries the bearer", "Bearer " + Fingerprint(FIXTURE_TOKEN));

            // Claude Desktop reaches the endpoint directly. It is a JSON server entry, not a spawned process,
            // and the shape is pinned because a fabricated claim that this client could not speak local HTTP is
            // what put a whole sidecar into this product.
            var desktop = Entry(plan, "Claude Desktop");
            check(desktop != null && desktop.State == ConduitConnectState.Emitted && desktop.Form == ConduitConnectForm.JsonFragment,
                "Claude Desktop emits a JSON entry", desktop == null ? "absent" : desktop.State + "/" + desktop.Form);

            var desktopJson = desktop == null ? string.Empty : desktop.Artifact ?? string.Empty;
            var desktopShape = "unreadable";
            var desktopOk = false;
            var desktopNested = false;
            try
            {
                var document = JObject.Parse(desktopJson);
                var entry = (JObject)document["projects"][FIXTURE_PROJECT_ROOT]["mcpServers"][FIXTURE_SERVER];
                desktopNested = document["mcpServers"] == null;
                desktopOk = (string)entry["type"] == "http"
                    && (string)entry["url"] == FIXTURE_URL
                    && entry["command"] == null;
                desktopShape = (string)entry["type"] + " " + (string)entry["url"]
                    + (entry["command"] == null ? ", no spawned command" : ", still spawns a command");
            }
            catch (Exception ex)
            {
                desktopShape = "unparseable: " + ex.Message;
            }

            check(desktopOk, "the entry is a direct endpoint and launches no process", desktopShape);
            check(desktopNested, "the entry is keyed under this project and not at the root of the file",
                desktopNested ? "nested under " + FIXTURE_PROJECT_ROOT : "a root server map is present");

            var desktopHeaders = HeadersFromJson(desktopJson, FIXTURE_SERVER, FIXTURE_PROJECT_ROOT);
            string desktopIdentity;
            string desktopBearer;
            check(desktopHeaders.TryGetValue(ConduitConnectPlan.IDENTITY_HEADER, out desktopIdentity)
                    && desktopIdentity == FIXTURE_IDENTITY
                    && desktopHeaders.TryGetValue(ConduitConnectPlan.AUTHORIZATION_HEADER, out desktopBearer)
                    && desktopBearer == "Bearer " + FIXTURE_TOKEN,
                "the entry's header map parses back to the right values",
                desktopHeaders.Count + " header(s) read back");

            // The Codex fragment, checked on its rendered text rather than on the values it was built from.
            var codex = Entry(plan, "Codex");
            check(codex != null && codex.State == ConduitConnectState.Emitted && codex.Form == ConduitConnectForm.TomlFragment,
                "Codex emits a TOML fragment", codex == null ? "absent" : codex.State + "/" + codex.Form);

            var toml = codex == null ? string.Empty : codex.Artifact ?? string.Empty;
            check(toml.Contains("[mcp_servers." + FIXTURE_SERVER + "]") && toml.Contains("url = \"" + FIXTURE_URL + "\""),
                "the fragment is keyed per project and carries the endpoint",
                toml.Contains("[mcp_servers." + FIXTURE_SERVER + "]") ? "keyed" : "wrong key");

            var fixtureTomlHeaders = HeadersFromToml(toml);
            string tomlIdentity;
            string tomlBearer;
            check(fixtureTomlHeaders.TryGetValue(ConduitConnectPlan.IDENTITY_HEADER, out tomlIdentity)
                    && tomlIdentity == FIXTURE_IDENTITY
                    && fixtureTomlHeaders.TryGetValue(ConduitConnectPlan.AUTHORIZATION_HEADER, out tomlBearer)
                    && tomlBearer == "Bearer " + FIXTURE_TOKEN,
                "the fragment's header table parses back to the right values",
                fixtureTomlHeaders.Count + " header(s) read back");

            // No emitted artifact may name a spawned process any more. This is the regression pin for the
            // sidecar: if one ever comes back, it comes back deliberately.
            var spawning = new List<string>();
            foreach (var entry in plan)
            {
                if (entry.Artifact == null) continue;
                if (entry.Artifact.IndexOf("\"command\"", StringComparison.Ordinal) >= 0
                    || entry.Artifact.IndexOf("command = ", StringComparison.Ordinal) >= 0) spawning.Add(entry.Client);
            }

            check(spawning.Count == 0, "no client is given a process to spawn",
                spawning.Count == 0 ? "all direct" : string.Join(", ", spawning.ToArray()));

            // The same endpoint and headers against the running transport. This is what separates an artifact
            // that is correct from one that merely reads correctly.
            if (ConduitHttpTransport.Port <= 0)
            {
                check(false, "live transport reachable for the endpoint checks", "transport down, port 0");
            }
            else
            {
                var live = Live();
                var livePlan = ConduitConnectPlan.Build(live);
                var url = ConduitConnectPlan.EndpointUrl(live.Port);
                var headers = ConduitConnectPlan.HeadersFor(live);

                var accepted = Send(url, headers);
                check(accepted.Status != 0 && accepted.Status != 401 && accepted.Status != 403,
                    "the emitted endpoint and headers get past authentication", "status " + accepted.Status);

                var noIdentity = new Dictionary<string, string>(headers);
                noIdentity.Remove(ConduitConnectPlan.IDENTITY_HEADER);
                var identityRefused = Send(url, noIdentity);
                check(identityRefused.Status == 403, "dropping the identity header is refused", "status " + identityRefused.Status);

                var noBearer = new Dictionary<string, string>(headers);
                noBearer.Remove(ConduitConnectPlan.AUTHORIZATION_HEADER);
                var bearerRefused = Send(url, noBearer);
                check(bearerRefused.Status == 401, "dropping the bearer is refused", "status " + bearerRefused.Status);

                // Each client's own rendered values, sent exactly as they would be written to its config.
                var liveDesktop = Entry(livePlan, "Claude Desktop");
                var desktopLive = HeadersFromJson(liveDesktop == null ? string.Empty : liveDesktop.Artifact, FIXTURE_SERVER, live.ProjectRoot);
                var desktopAccepted = desktopLive.Count == 2 ? Send(url, desktopLive) : new Reply { Status = 0, Body = "header map unreadable" };
                check(desktopAccepted.Status != 0 && desktopAccepted.Status != 401 && desktopAccepted.Status != 403,
                    "the Claude Desktop entry's own header values authenticate", "status " + desktopAccepted.Status);

                var liveCodex = Entry(livePlan, "Codex");
                var codexLive = HeadersFromToml(liveCodex == null ? string.Empty : liveCodex.Artifact);
                var codexAccepted = codexLive.Count == 2 ? Send(url, codexLive) : new Reply { Status = 0, Body = "header table unreadable" };
                check(codexAccepted.Status != 0 && codexAccepted.Status != 401 && codexAccepted.Status != 403,
                    "the Codex fragment's own header values authenticate", "status " + codexAccepted.Status);

                // The live snapshot builder, which is what the window actually feeds the plan.
                var windowSnapshot = ConduitWindow.LiveSnapshot();
                check(windowSnapshot.Port == ConduitHttpTransport.Port
                        && windowSnapshot.Identity == ConduitHttpTransport.Identity
                        && windowSnapshot.Token == ConduitHttpTransport.Token
                        && !string.IsNullOrEmpty(windowSnapshot.ServerName),
                    "the window's live snapshot matches the running transport",
                    "port " + windowSnapshot.Port + ", server " + windowSnapshot.ServerName);

                var windowPlan = ConduitConnectPlan.Build(windowSnapshot);
                var windowCode = Entry(windowPlan, "Claude Code");
                var windowDesktop = Entry(windowPlan, "Claude Desktop");
                var windowCodex = Entry(windowPlan, "Codex");
                check(windowCode != null && windowCode.State == ConduitConnectState.Emitted
                        && windowDesktop != null && windowDesktop.State == ConduitConnectState.Emitted
                        && windowCodex != null && windowCodex.State == ConduitConnectState.Emitted,
                    "the window's snapshot produces usable artifacts for every direct client",
                    (windowCode == null ? "absent" : windowCode.State.ToString()) + " / "
                        + (windowDesktop == null ? "absent" : windowDesktop.State.ToString()) + " / "
                        + (windowCodex == null ? "absent" : windowCodex.State.ToString()));
            }

            // Every emitted client is pointed at the SAME endpoint carrying the SAME two header values. The
            // key spelling is allowed to differ - one client names the endpoint url, another serverUrl - and
            // that is the ONLY thing allowed to differ. Pinning the values rather than the bytes is what stops
            // a client-specific entry quietly becoming a client-specific connection.
            var mismatched = new List<string>();
            foreach (var entry in plan)
            {
                if (entry.Artifact == null) continue;

                string entryIdentity;
                string entryBearer;
                if (entry.Url != FIXTURE_URL) { mismatched.Add(entry.Client + " endpoint"); continue; }
                if (entry.Headers == null
                    || !entry.Headers.TryGetValue(ConduitConnectPlan.IDENTITY_HEADER, out entryIdentity)
                    || entryIdentity != FIXTURE_IDENTITY
                    || !entry.Headers.TryGetValue(ConduitConnectPlan.AUTHORIZATION_HEADER, out entryBearer)
                    || entryBearer != "Bearer " + FIXTURE_TOKEN)
                {
                    mismatched.Add(entry.Client + " credentials");
                }
            }

            check(mismatched.Count == 0,
                "every emitted client points at one endpoint with one set of credentials",
                mismatched.Count == 0 ? "all agree" : string.Join(", ", mismatched.ToArray()));

            // And the spelling each client actually reads is the spelling it gets.
            var cursor = Entry(plan, "Cursor");
            var antigravity = Entry(plan, "Antigravity");
            var spellingOk = cursor != null && cursor.Artifact != null && antigravity != null && antigravity.Artifact != null
                && cursor.Artifact.Contains("\"url\"")
                && !cursor.Artifact.Contains("\"serverUrl\"")
                && antigravity.Artifact.Contains("\"serverUrl\"")
                && !antigravity.Artifact.Contains("\"url\"");

            check(spellingOk && antigravity.State == ConduitConnectState.Emitted
                    && antigravity.Form == ConduitConnectForm.JsonFragment,
                "each client gets the endpoint under the key it reads",
                antigravity == null ? "Antigravity absent" : "serverUrl for Antigravity, url shared by the rest");

            // This client differs from every other JSON one in three ways at once - the map it nests servers
            // under, the word it calls this transport, and an enabled flag whose omitted default its schema
            // does not document. Walked by hand with every key and value written out, for the reason
            // HeadersFromJson gives: navigating with the plan's own lookups would agree with whatever the plan
            // rendered, so a map key wrong on both sides would still pass.
            var openCode = Entry(plan, "OpenCode");
            var openCodeArtifact = openCode == null ? string.Empty : (openCode.Artifact ?? string.Empty);
            JObject openCodeEntry = null;
            var openCodeProjectScoped = true;
            try
            {
                var openCodeDocument = JObject.Parse(openCodeArtifact);
                openCodeProjectScoped = openCodeDocument["projects"] != null;
                openCodeEntry = openCodeDocument["mcp"][FIXTURE_SERVER] as JObject;
            }
            catch
            {
                // An unreadable fragment yields no entry and stays marked as scoped, so both checks fail on it.
            }

            // The flag's type is asserted as well as its value: a quoted "true" casts to true here while the
            // client's schema wants a boolean, which would read as enabled in the gate and as invalid there.
            check(openCodeEntry != null
                    && (string)openCodeEntry["type"] == "remote"
                    && (string)openCodeEntry["url"] == FIXTURE_URL
                    && openCodeEntry["enabled"] != null
                    && openCodeEntry["enabled"].Type == JTokenType.Boolean
                    && (bool)openCodeEntry["enabled"],
                "OpenCode nests under mcp with a remote transport and an explicit boolean enabled",
                openCodeEntry == null
                    ? "no mcp entry in " + openCodeArtifact.Length + " chars"
                    : (string)openCodeEntry["type"] + " " + (string)openCodeEntry["url"]
                        + ", enabled " + openCodeEntry["enabled"].Type + " " + openCodeEntry["enabled"]);

            // Asserted as a relation against the endpoint's own budget rather than against a figure written
            // here. The property is that the client outlasts the wait, so pinning a number would turn red the
            // day the budget legitimately moves while still saying nothing about the case that matters. The
            // constant belongs to the endpoint rather than to the plan, so this is not the plan agreeing with
            // itself: the two are only ever equal because one is derived from the other on purpose.
            check(openCodeEntry != null
                    && openCodeEntry["timeout"] != null
                    && openCodeEntry["timeout"].Type == JTokenType.Integer
                    && (int)openCodeEntry["timeout"] > ConduitMcpEndpoint.TOOL_WAIT_MS,
                "OpenCode is told to allow longer than the endpoint holds a call",
                openCodeEntry == null || openCodeEntry["timeout"] == null
                    ? "no timeout"
                    : openCodeEntry["timeout"] + " against a " + ConduitMcpEndpoint.TOOL_WAIT_MS + " wait");

            // Pinned to this client's global config FOLDER and not merely to the filename, because the
            // filename alone is equally satisfied by a file sitting in the project root - which is where this
            // entry was first written, and which made the server exist only while that project was the open
            // workspace. The artifact must also carry no projects key: this client has no per-project nesting,
            // so one would be scoping that nothing reads.
            var openCodeConfig = (ConduitWindow.ConfigPath("OpenCode") ?? string.Empty).Replace('\\', '/');
            check(openCodeConfig.EndsWith("/.config/opencode/opencode.json", StringComparison.Ordinal)
                    && !openCodeProjectScoped,
                "OpenCode is pointed at its own global config with no project key inside it",
                (openCodeConfig.Length == 0 ? "no path" : openCodeConfig)
                    + (openCodeProjectScoped ? ", nested under projects" : ", root scoped"));

            // The file the window will actually write. The endpoint entry above is correct and was still
            // dropped once, because it was written into the store an app reads for servers it spawns itself
            // rather than the one its sessions read per project. Only the path answers that.
            var desktopConfig = (ConduitWindow.ConfigPath("Claude Desktop") ?? string.Empty).Replace('\\', '/');
            check(desktopConfig.EndsWith("/.claude.json", StringComparison.Ordinal),
                "Claude Desktop is pointed at the per-project store its sessions read",
                desktopConfig.Length == 0 ? "no path" : desktopConfig);

            // A transport that is not serving produces no artifact anywhere, rather than one that would be
            // applied, look right, and answer 401 forever.
            var down = Fixture();
            down.Port = 0;
            var downPlan = ConduitConnectPlan.Build(down);
            var anyArtifact = false;
            var allStated = true;

            foreach (var entry in downPlan)
            {
                if (entry.Artifact != null) anyArtifact = true;
                if (entry.State != ConduitConnectState.TransportDown || string.IsNullOrEmpty(entry.Reason)) allStated = false;
            }

            check(!anyArtifact && allStated && downPlan.Count == ConduitConnectPlan.Clients.Length,
                "a down transport refuses every client and emits nothing",
                downPlan.Count + " refused, artifacts " + (anyArtifact ? "present" : "none"));

            // Building a plan is measured to be inert. Applying one is a separate, deliberate act.
            var after = FingerprintConfigs();
            var changed = new List<string>();
            foreach (var pair in before)
            {
                string now;
                if (!after.TryGetValue(pair.Key, out now) || now != pair.Value) changed.Add(pair.Key);
            }

            check(changed.Count == 0, "building the plan wrote no entry into any client config",
                changed.Count == 0 ? before.Count + " client(s) unchanged" : string.Join(", ", changed.ToArray()));

            return "connect plan gate: " + passed + " passed, " + failed + " failed\n" + report;
        }

        /// <summary>
        /// Fingerprints this project's entry in every client config the window knows about, so a write by
        /// anything in this run shows up as a difference. Absence is part of the fingerprint because creating a
        /// missing entry is exactly as much of a write as editing a present one. The entry is fingerprinted
        /// rather than the whole file because a running client rewrites its own config while the gate is
        /// going, and a length that moved for that reason would fail this check for something it did not do.
        /// </summary>
        /// <returns>Client display name to fingerprint.</returns>
        private static Dictionary<string, string> FingerprintConfigs()
        {
            var fingerprints = new Dictionary<string, string>(StringComparer.Ordinal);
            var snapshot = ConduitWindow.LiveSnapshot();

            foreach (var client in ConduitConnectPlan.Clients)
            {
                var path = ConduitWindow.ConfigPath(client);
                if (string.IsNullOrEmpty(path) || fingerprints.ContainsKey(client)) continue;

                try
                {
                    if (!File.Exists(path)) { fingerprints[client] = "absent"; continue; }

                    var text = File.ReadAllText(path);
                    if (path.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
                    {
                        fingerprints[client] = text.Contains("[mcp_servers." + snapshot.ServerName + "]") ? "present" : "missing";
                        continue;
                    }

                    var servers = ConduitConnectPlan.ServerMap(
                        JObject.Parse(text),
                        ConduitConnectPlan.ProjectKeyFor(client, snapshot),
                        ConduitConnectPlan.ServerMapKeyFor(client),
                        false);
                    var entry = servers == null ? null : servers[snapshot.ServerName];
                    fingerprints[client] = entry == null ? "missing" : entry.ToString(Newtonsoft.Json.Formatting.None);
                }
                catch (Exception ex)
                {
                    fingerprints[client] = "unreadable: " + ex.Message;
                }
            }

            return fingerprints;
        }

        /// <summary>Renders a secret as a short fingerprint, so a report can name it without printing it.</summary>
        /// <param name="secret">The value to fingerprint.</param>
        /// <returns>A leading fragment and the length.</returns>
        private static string Fingerprint(string secret)
        {
            if (string.IsNullOrEmpty(secret)) return "empty";
            return secret.Substring(0, Math.Min(4, secret.Length)) + "... (" + secret.Length + " chars)";
        }

        /// <summary>Runs the gate and writes the report to the console.</summary>
        [MenuItem("Tools/Conduit Gates/Verify connect plan")]
        private static void VerifyFromMenu()
        {
            Debug.Log("[ConnectPlan] " + Run());
        }

        #endregion
    }
}
