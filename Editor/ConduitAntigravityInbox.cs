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
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>Delivers notices to an exact Antigravity cascade through its local Connect endpoint.</summary>
    /// <remarks>
    /// The endpoint is owned by Antigravity's language server and authenticated with the CSRF token on that
    /// process's own command line. A cascade is read immediately before every send, both to prove the id and
    /// to preserve the last model configuration that <c>SendUserCascadeMessage</c> requires. The native call
    /// handles both a running cascade and an idle one, so Conduit installs nothing on this client's side.
    /// </remarks>
    internal static class ConduitAntigravityInbox
    {
        #region Constants

        /// <summary>Connect service path published by Antigravity's language server.</summary>
        private const string SERVICE = "/exa.language_server_pb.LanguageServerService/";

        /// <summary>Longest endpoint-discovery helper run.</summary>
        private const int DISCOVERY_TIMEOUT_MS = 8000;

        /// <summary>Longest probe of one candidate local listener.</summary>
        private const int PROBE_TIMEOUT_MS = 1500;

        /// <summary>Longest Connect request.</summary>
        private const int REQUEST_TIMEOUT_MS = 30000;

        /// <summary>Largest Connect response accepted.</summary>
        private const int MAX_RESPONSE_BYTES = 8 * 1024 * 1024;

        /// <summary>CSRF switch on the language server command line.</summary>
        private static readonly Regex TokenPattern = new Regex(
            @"(?:^|\s)--csrf_token\s+(\S+)", RegexOptions.CultureInvariant);

        /// <summary>Application-version switch on the language server command line.</summary>
        private static readonly Regex VersionPattern = new Regex(
            @"(?:^|\s)--override_ide_version\s+(\S+)", RegexOptions.CultureInvariant);

        #endregion

        #region Endpoint

        /// <summary>One verified Antigravity Connect endpoint.</summary>
        private sealed class Endpoint
        {
            /// <summary>Owning language-server process.</summary>
            public int Pid;

            /// <summary>HTTP listener port.</summary>
            public int Port;

            /// <summary>CSRF token, kept in memory only.</summary>
            public string Token;

            /// <summary>Antigravity version from its own launch arguments.</summary>
            public string Version;
        }

        /// <summary>Endpoint cached only while its exact process remains alive.</summary>
        private static Endpoint _endpoint;

        /// <summary>Guards endpoint replacement across simultaneous stage notices.</summary>
        private static readonly object EndpointLock = new object();

        #endregion

        #region Availability and delivery

        /// <summary>Whether an exact cascade exists and carries a reusable model configuration.</summary>
        /// <param name="cascadeId">Antigravity cascade id.</param>
        /// <param name="detail">Receives why it is or is not reachable.</param>
        /// <returns>True when a native send can be constructed.</returns>
        internal static bool CanDeliver(string cascadeId, out string detail)
        {
            Endpoint endpoint;
            JObject trajectory;
            JToken config;
            string status;
            return TryPrepare(
                cascadeId, out endpoint, out trajectory, out config, out status, out detail);
        }

        /// <summary>Sends one notice after reading the exact cascade's current state and model config.</summary>
        /// <param name="cascadeId">Antigravity cascade id.</param>
        /// <param name="text">Notice body.</param>
        /// <param name="detail">Receives the native outcome.</param>
        /// <param name="unconfirmed">True when the request was written but its answer was lost.</param>
        /// <returns>True when Antigravity accepted the message.</returns>
        internal static bool TryDeliver(
            string cascadeId, string text, out string detail, out bool unconfirmed)
        {
            unconfirmed = false;

            Endpoint endpoint;
            JObject trajectory;
            JToken config;
            string status;
            if (!TryPrepare(
                    cascadeId, out endpoint, out trajectory, out config, out status, out detail))
            {
                return false;
            }

            bool active = string.Equals(
                    status, "CASCADE_RUN_STATUS_RUNNING", StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    status, "CASCADE_RUN_STATUS_GENERATING", StringComparison.OrdinalIgnoreCase);
            bool idle = string.Equals(
                status, "CASCADE_RUN_STATUS_IDLE", StringComparison.OrdinalIgnoreCase);
            if (!active && !idle)
            {
                detail = "Antigravity cascade " + cascadeId + " is " + status
                    + ", so it is not safe to start or steer";
                return false;
            }

            var arguments = new JObject
            {
                ["cascadeId"] = cascadeId,
                ["items"] = new JArray { new JObject { ["text"] = text ?? string.Empty } },
                ["cascadeConfig"] = config.DeepClone(),
            };

            JObject ignored;
            bool wrote;
            if (!TryCall(
                    endpoint, "SendUserCascadeMessage", arguments, out ignored, out detail, out wrote))
            {
                unconfirmed = wrote;
                if (!wrote) Invalidate(endpoint);
                return false;
            }

            detail = "Antigravity cascade " + cascadeId + " was "
                + (active ? "active" : "idle")
                + ", and its native SendUserCascadeMessage call accepted the notice using that cascade's "
                + "preserved model configuration";
            return true;
        }

        /// <summary>Resolves the endpoint, exact cascade, current status and last model configuration.</summary>
        /// <param name="cascadeId">Cascade to resolve.</param>
        /// <param name="endpoint">Receives the verified endpoint.</param>
        /// <param name="trajectory">Receives the native trajectory.</param>
        /// <param name="config">Receives the last reusable model configuration.</param>
        /// <param name="status">Receives the current native run status.</param>
        /// <param name="error">Receives why preparation failed.</param>
        /// <returns>True when a complete send can be built.</returns>
        private static bool TryPrepare(
            string cascadeId, out Endpoint endpoint, out JObject trajectory, out JToken config,
            out string status, out string error)
        {
            endpoint = null;
            trajectory = null;
            config = null;
            status = null;
            error = null;
            if (string.IsNullOrEmpty(cascadeId))
            {
                error = "no Antigravity cascade id was given";
                return false;
            }

            if (!TryEndpoint(out endpoint, out error)) return false;

            JObject response;
            bool wrote;
            if (!TryCall(
                    endpoint, "GetCascadeTrajectory", new JObject { ["cascadeId"] = cascadeId },
                    out response, out error, out wrote))
            {
                // A read-only request can be retried after rediscovery because it cannot duplicate a notice.
                Invalidate(endpoint);
                if (!TryEndpoint(out endpoint, out error)) return false;
                if (!TryCall(
                        endpoint, "GetCascadeTrajectory", new JObject { ["cascadeId"] = cascadeId },
                        out response, out error, out wrote))
                {
                    return false;
                }
            }

            trajectory = response["trajectory"] as JObject;
            if (trajectory == null)
            {
                error = "Antigravity returned no trajectory for cascade " + cascadeId;
                return false;
            }

            status = (string)trajectory["status"] ?? "CASCADE_RUN_STATUS_UNKNOWN";
            config = LastConfig(trajectory);
            if (config == null)
            {
                error = "Antigravity cascade " + cascadeId
                    + " has no reusable model configuration in its trajectory";
                return false;
            }

            return true;
        }

        /// <summary>Finds the newest user configuration recorded in a trajectory.</summary>
        /// <param name="trajectory">Native trajectory object.</param>
        /// <returns>The config token, or null.</returns>
        private static JToken LastConfig(JObject trajectory)
        {
            var steps = trajectory["steps"] as JArray;
            if (steps == null) return null;

            for (int index = steps.Count - 1; index >= 0; index--)
            {
                var input = steps[index]["userInput"] as JObject;
                if (input == null) continue;
                if (input["userConfig"] != null) return input["userConfig"];
                if (input["lastUserConfig"] != null) return input["lastUserConfig"];
            }

            return null;
        }

        #endregion

        #region Discovery

        /// <summary>Returns a cached live endpoint or discovers and verifies one.</summary>
        /// <param name="endpoint">Receives the endpoint.</param>
        /// <param name="error">Receives why discovery failed.</param>
        /// <returns>True when the Connect endpoint is verified.</returns>
        private static bool TryEndpoint(out Endpoint endpoint, out string error)
        {
            lock (EndpointLock)
            {
                if (_endpoint != null && ProcessAlive(_endpoint.Pid))
                {
                    endpoint = _endpoint;
                    error = null;
                    return true;
                }

                _endpoint = null;
            }

            JObject process;
            if (!TryProcessInfo(out process, out error))
            {
                endpoint = null;
                return false;
            }

            int pid = (int?)process["pid"] ?? 0;
            string commandLine = (string)process["commandLine"] ?? string.Empty;
            Match token = TokenPattern.Match(commandLine);
            if (pid <= 0 || !token.Success)
            {
                endpoint = null;
                error = "Antigravity's language server or its CSRF token was not found";
                return false;
            }

            Match version = VersionPattern.Match(commandLine);
            var ports = process["ports"] as JArray;
            if (ports == null)
            {
                endpoint = null;
                error = "Antigravity's language server has no local HTTP listener";
                return false;
            }

            foreach (JToken value in ports)
            {
                var port = (int?)value ?? 0;
                if (port <= 0) continue;

                var candidate = new Endpoint
                {
                    Pid = pid,
                    Port = port,
                    Token = token.Groups[1].Value,
                    Version = version.Success ? version.Groups[1].Value : "(unknown version)",
                };
                if (!Probe(candidate)) continue;

                lock (EndpointLock) _endpoint = candidate;
                endpoint = candidate;
                error = null;
                return true;
            }

            endpoint = null;
            error = "Antigravity's language server is running, but its HTTP Connect endpoint was not found";
            return false;
        }

        /// <summary>Clears a cached endpoint only if it is still the failed one.</summary>
        /// <param name="endpoint">Endpoint that failed.</param>
        private static void Invalidate(Endpoint endpoint)
        {
            lock (EndpointLock)
            {
                if (ReferenceEquals(_endpoint, endpoint)) _endpoint = null;
            }
        }

        /// <summary>Reads the Antigravity language server and its listener ports in one bounded helper.</summary>
        /// <param name="value">Receives pid, command line and ports.</param>
        /// <param name="error">Receives why the helper failed.</param>
        /// <returns>True when one language server was found.</returns>
        private static bool TryProcessInfo(out JObject value, out string error)
        {
            value = null;
            error = null;
            const string script =
                "$p = Get-CimInstance Win32_Process | Where-Object { "
                + "$_.Name -ieq 'language_server.exe' -and "
                + "$_.CommandLine -match 'override_ide_name antigravity' } | Select-Object -First 1; "
                + "if ($null -eq $p) { exit 0 }; "
                + "$ports = @(Get-NetTCPConnection -OwningProcess $p.ProcessId -State Listen "
                + "-ErrorAction SilentlyContinue | Where-Object { $_.LocalAddress -eq '127.0.0.1' } "
                + "| Select-Object -ExpandProperty LocalPort); "
                + "[pscustomobject]@{ pid = [int]$p.ProcessId; commandLine = [string]$p.CommandLine; "
                + "ports = $ports } | ConvertTo-Json -Compress";

            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var start = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -EncodedCommand " + encoded,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            try
            {
                using (var process = new Process { StartInfo = start })
                {
                    process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs args)
                    {
                        if (args.Data != null) stdout.AppendLine(args.Data);
                    };
                    process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs args)
                    {
                        if (args.Data != null) stderr.AppendLine(args.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(DISCOVERY_TIMEOUT_MS))
                    {
                        try { process.Kill(); } catch { }
                        error = "Antigravity endpoint discovery did not finish within "
                            + DISCOVERY_TIMEOUT_MS + " ms";
                        return false;
                    }

                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        error = "Antigravity endpoint discovery failed"
                            + (stderr.Length == 0 ? string.Empty : ": " + stderr.ToString().Trim());
                        return false;
                    }
                }

                string raw = stdout.ToString().Trim();
                if (raw.Length == 0)
                {
                    error = "no running Antigravity language server was found";
                    return false;
                }

                value = JObject.Parse(raw);

                // PowerShell emits a scalar for one port. Normalize it once so the discovery loop has one
                // shape regardless of how many local listeners the process owns.
                if (!(value["ports"] is JArray))
                {
                    value["ports"] = value["ports"] == null
                        ? new JArray()
                        : new JArray { value["ports"].DeepClone() };
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Confirms a cached endpoint still belongs to a live process.</summary>
        /// <param name="pid">Language-server process id.</param>
        /// <returns>True while the process exists.</returns>
        private static bool ProcessAlive(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid)) return !process.HasExited;
            }
            catch (Exception) { return false; }
        }

        /// <summary>Confirms one candidate port is Antigravity's token-bearing HTTP application.</summary>
        /// <param name="endpoint">Candidate endpoint.</param>
        /// <returns>True when the root document identifies the expected application and token.</returns>
        private static bool Probe(Endpoint endpoint)
        {
            HttpWebRequest request = null;
            try
            {
                request = (HttpWebRequest)WebRequest.Create(
                    "http://127.0.0.1:" + endpoint.Port.ToString(CultureInfo.InvariantCulture) + "/");
                request.Method = "GET";
                request.Timeout = PROBE_TIMEOUT_MS;
                request.ReadWriteTimeout = PROBE_TIMEOUT_MS;
                request.Proxy = null;

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string text = reader.ReadToEnd();
                    return response.StatusCode == HttpStatusCode.OK
                        && text.IndexOf("__APP_CONFIG__", StringComparison.Ordinal) >= 0
                        && text.IndexOf(endpoint.Token, StringComparison.Ordinal) >= 0;
                }
            }
            catch (Exception) { return false; }
            finally { if (request != null) request.Abort(); }
        }

        #endregion

        #region Connect calls

        /// <summary>Calls one CSRF-authenticated Antigravity Connect method.</summary>
        /// <param name="endpoint">Verified endpoint.</param>
        /// <param name="method">Connect method name.</param>
        /// <param name="arguments">JSON request body.</param>
        /// <param name="response">Receives the JSON response.</param>
        /// <param name="error">Receives the transport or method failure.</param>
        /// <param name="wrote">Receives whether the request body was written.</param>
        /// <returns>True for an HTTP success carrying JSON.</returns>
        private static bool TryCall(
            Endpoint endpoint, string method, JObject arguments, out JObject response,
            out string error, out bool wrote)
        {
            response = null;
            error = null;
            wrote = false;
            HttpWebRequest request = null;

            try
            {
                string url = "http://127.0.0.1:"
                    + endpoint.Port.ToString(CultureInfo.InvariantCulture) + SERVICE + method;
                request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Accept = "application/json";
                request.Timeout = REQUEST_TIMEOUT_MS;
                request.ReadWriteTimeout = REQUEST_TIMEOUT_MS;
                request.Proxy = null;
                request.Headers["connect-protocol-version"] = "1";
                request.Headers["x-codeium-csrf-token"] = endpoint.Token;

                byte[] body = new UTF8Encoding(false).GetBytes(
                    (arguments ?? new JObject()).ToString(Formatting.None));
                request.ContentLength = body.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                wrote = true;

                using (var native = (HttpWebResponse)request.GetResponse())
                {
                    string raw = ReadBounded(native.GetResponseStream());
                    if ((int)native.StatusCode < 200 || (int)native.StatusCode >= 300)
                    {
                        error = method + " answered HTTP " + (int)native.StatusCode
                            + (raw.Length == 0 ? string.Empty : ": " + raw);
                        return false;
                    }

                    try { response = raw.Length == 0 ? new JObject() : JObject.Parse(raw); }
                    catch (Exception)
                    {
                        error = method + " answered with unreadable JSON";
                        return false;
                    }

                    return true;
                }
            }
            catch (WebException ex)
            {
                string native = string.Empty;
                try
                {
                    var failed = ex.Response as HttpWebResponse;
                    if (failed != null)
                    {
                        using (failed) native = ReadBounded(failed.GetResponseStream());
                    }
                }
                catch (Exception) { }

                error = method + " failed: " + ex.Status
                    + (native.Length == 0 ? string.Empty : ": " + native);
                return false;
            }
            catch (Exception ex)
            {
                error = method + " failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally { if (request != null) request.Abort(); }
        }

        /// <summary>Reads a Connect response under an explicit size ceiling.</summary>
        /// <param name="stream">Response stream.</param>
        /// <returns>UTF-8 response text.</returns>
        private static string ReadBounded(Stream stream)
        {
            if (stream == null) return string.Empty;

            using (var output = new MemoryStream())
            {
                var buffer = new byte[8192];
                for (;;)
                {
                    int count = stream.Read(buffer, 0, buffer.Length);
                    if (count <= 0) break;
                    if (output.Length + count > MAX_RESPONSE_BYTES)
                    {
                        throw new IOException("Antigravity response exceeded " + MAX_RESPONSE_BYTES + " bytes");
                    }

                    output.Write(buffer, 0, count);
                }

                return Encoding.UTF8.GetString(output.ToArray());
            }
        }

        #endregion
    }
}
