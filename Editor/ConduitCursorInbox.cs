/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>Delivers notices to an exact Cursor agent through Cursor's authenticated Desktop Bridge.</summary>
    /// <remarks>
    /// Cursor publishes one discovery record per live bridge instance. Every delivery lists threads again
    /// before sending: a running thread must be force-steered, while an idle or completed thread must be
    /// woken without an interrupt. The discovery token and pipe are used only for that bridge request and
    /// are never logged.
    /// </remarks>
    internal static class ConduitCursorInbox
    {
        #region Constants

        /// <summary>Desktop Bridge contract verified by this implementation.</summary>
        private const int PROTOCOL_VERSION = 1;

        /// <summary>Largest message Cursor's bridge accepts.</summary>
        private const int MAX_MESSAGE_BYTES = 256 * 1024;

        /// <summary>Largest whole HTTP answer accepted from the local bridge.</summary>
        private const int MAX_RESPONSE_BYTES = 2 * 1024 * 1024;

        /// <summary>Longest wait for a bridge pipe to accept a connection.</summary>
        private const int CONNECT_TIMEOUT_MS = 1500;

        /// <summary>Longest wait for one complete Desktop Bridge request.</summary>
        private const int REQUEST_TIMEOUT_MS = 10000;

        /// <summary>Full Windows pipe prefix carried by Cursor's discovery record.</summary>
        private const string PIPE_PREFIX = @"\\.\pipe\";

        /// <summary>Shape of Cursor's bearer token.</summary>
        private static readonly Regex TokenPattern =
            new Regex("^[0-9a-f]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        #endregion

        #region Route

        /// <summary>One live bridge instance and the exact thread it currently reports.</summary>
        private sealed class Route
        {
            /// <summary>Pipe name without the Windows namespace prefix.</summary>
            public string PipeName;

            /// <summary>Bridge bearer, kept in memory only.</summary>
            public string Token;

            /// <summary>Thread state read immediately before delivery.</summary>
            public string Status;

            /// <summary>Thread source as Cursor reports it.</summary>
            public string Source;

            /// <summary>Last-update stamp used to resolve duplicate instance records.</summary>
            public double UpdatedAt;

            /// <summary>Application label for truthful diagnostics.</summary>
            public string Label;
        }

        #endregion

        #region Availability and delivery

        /// <summary>Whether an exact Cursor thread is visible through a live Desktop Bridge.</summary>
        /// <param name="threadId">Cursor agent id.</param>
        /// <param name="detail">Receives why it is or is not reachable.</param>
        /// <returns>True when the thread is currently sendable.</returns>
        internal static bool CanDeliver(string threadId, out string detail)
        {
            Route route;
            return TryLocate(threadId, out route, out detail);
        }

        /// <summary>Sends one notice after re-reading the target thread's current state.</summary>
        /// <param name="threadId">Cursor agent id.</param>
        /// <param name="text">Notice body.</param>
        /// <param name="detail">Receives the native outcome.</param>
        /// <param name="unconfirmed">True when Cursor may have accepted an unanswered request.</param>
        /// <returns>True when Cursor reported that the notice was submitted or queued.</returns>
        internal static bool TryDeliver(
            string threadId, string text, out string detail, out bool unconfirmed)
        {
            unconfirmed = false;

            Route route;
            if (!TryLocate(threadId, out route, out detail)) return false;

            bool active = string.Equals(route.Status, "running", StringComparison.OrdinalIgnoreCase);
            bool wakeable = string.Equals(route.Status, "idle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(route.Status, "completed", StringComparison.OrdinalIgnoreCase);
            if (!active && !wakeable)
            {
                detail = "Cursor thread " + threadId + " is " + route.Status
                    + ", so it is not safe to interrupt or wake";
                return false;
            }

            byte[] message = new UTF8Encoding(false).GetBytes(text ?? string.Empty);
            if (message.Length > MAX_MESSAGE_BYTES)
            {
                detail = "the notice is larger than Cursor Desktop Bridge's "
                    + MAX_MESSAGE_BYTES + " byte limit";
                return false;
            }

            var request = new JObject
            {
                ["type"] = "sendMessage",
                ["threadId"] = threadId,
                ["text"] = text ?? string.Empty,

                // Force is only an active-turn operation. On an idle thread it is omitted in substance by
                // sending false, which makes the same endpoint perform its normal wake path.
                ["force"] = active,
            };

            JObject response;
            bool wrote;
            if (!TryRequest(route, request, out response, out detail, out wrote))
            {
                unconfirmed = wrote;
                return false;
            }

            string status = (string)response["status"] ?? string.Empty;
            if (string.Equals(status, "submitted", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "queued", StringComparison.OrdinalIgnoreCase))
            {
                detail = "Cursor thread " + threadId + " was " + route.Status + ", so Desktop Bridge "
                    + (active ? "force-steered its active turn" : "used its idle wake path")
                    + " and reported " + status;
                return true;
            }

            if (string.Equals(status, "timeout", StringComparison.OrdinalIgnoreCase))
            {
                unconfirmed = true;
                detail = "Cursor timed out after accepting the send request, so delivery is unconfirmed";
                return false;
            }

            detail = "Cursor refused the notice with status "
                + (status.Length == 0 ? "(absent)" : status)
                + (response["reason"] == null ? string.Empty : ": " + (string)response["reason"])
                + (response["message"] == null ? string.Empty : ": " + (string)response["message"]);
            return false;
        }

        #endregion

        #region Discovery

        /// <summary>Finds an exact thread and reads its current state from every verified live instance.</summary>
        /// <param name="threadId">Cursor agent id.</param>
        /// <param name="route">Receives the newest matching route.</param>
        /// <param name="error">Receives why no route was found.</param>
        /// <returns>True when the exact thread is currently sendable.</returns>
        private static bool TryLocate(string threadId, out Route route, out string error)
        {
            route = null;
            error = null;
            if (string.IsNullOrEmpty(threadId)) { error = "no Cursor thread id was given"; return false; }

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cursor", "desktop-bridge");
            if (!Directory.Exists(folder))
            {
                error = "Cursor Desktop Bridge has no live discovery directory";
                return false;
            }

            int liveInstances = 0;
            int readableInstances = 0;
            foreach (string path in Directory.GetFiles(folder, "*.json"))
            {
                JObject discovery;
                try { discovery = JObject.Parse(File.ReadAllText(path, Encoding.UTF8)); }
                catch (Exception) { continue; }

                var protocol = (int?)discovery["protocolVersion"];
                var pid = (int?)discovery["pid"] ?? 0;
                var socketPath = (string)discovery["socketPath"] ?? string.Empty;
                var token = (string)discovery["token"] ?? string.Empty;
                if (protocol != PROTOCOL_VERSION || pid <= 0
                    || !socketPath.StartsWith(PIPE_PREFIX, StringComparison.OrdinalIgnoreCase)
                    || !TokenPattern.IsMatch(token) || !ProcessAlive(pid))
                {
                    continue;
                }

                liveInstances++;
                var candidate = new Route
                {
                    PipeName = socketPath.Substring(PIPE_PREFIX.Length),
                    Token = token,
                    Label = ((string)discovery["appName"] ?? "Cursor") + " "
                        + ((string)discovery["appVersion"] ?? "(unknown version)"),
                };

                JObject listed;
                string requestError;
                bool wrote;
                if (!TryRequest(candidate, new JObject { ["type"] = "listThreads" },
                        out listed, out requestError, out wrote))
                {
                    continue;
                }

                readableInstances++;
                var threads = listed["threads"] as JArray;
                if (threads == null) continue;

                foreach (JToken thread in threads)
                {
                    if ((string)thread["id"] != threadId) continue;

                    string source = (string)thread["source"] ?? string.Empty;
                    if (string.Equals(source, "draft", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(source, "claude-code", StringComparison.OrdinalIgnoreCase))
                    {
                        error = "Cursor lists thread " + threadId + " from source " + source
                            + ", which Desktop Bridge refuses to send to";
                        return false;
                    }

                    double updated = (double?)thread["lastUpdatedAt"] ?? 0d;
                    if (route != null && route.UpdatedAt >= updated) continue;

                    candidate.Status = (string)thread["status"] ?? "unknown";
                    candidate.Source = source;
                    candidate.UpdatedAt = updated;
                    route = candidate;
                }
            }

            if (route != null) return true;

            error = liveInstances == 0
                ? "no verified live Cursor Desktop Bridge instance is published"
                : readableInstances == 0
                    ? "Cursor Desktop Bridge is live but none of its instances answered a thread listing"
                    : "Cursor Desktop Bridge lists no thread named " + threadId;
            return false;
        }

        /// <summary>Checks a discovery record's process still exists.</summary>
        /// <param name="pid">Process id from the record.</param>
        /// <returns>True while that process exists.</returns>
        private static bool ProcessAlive(int pid)
        {
            try
            {
                using (Process process = Process.GetProcessById(pid)) return !process.HasExited;
            }
            catch (Exception) { return false; }
        }

        #endregion

        #region Desktop Bridge HTTP

        /// <summary>Issues one authenticated HTTP request over Cursor's named pipe.</summary>
        /// <param name="route">Verified bridge route.</param>
        /// <param name="payload">Request body.</param>
        /// <param name="response">Receives the JSON response body.</param>
        /// <param name="error">Receives why no usable response arrived.</param>
        /// <param name="wrote">Receives whether the whole request reached the pipe.</param>
        /// <returns>True for a successful HTTP response carrying a JSON object.</returns>
        private static bool TryRequest(
            Route route, JObject payload, out JObject response, out string error, out bool wrote)
        {
            response = null;
            error = null;
            wrote = false;

            try
            {
                using (var stream = new NamedPipeClientStream(
                    ".", route.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                {
                    try { stream.Connect(CONNECT_TIMEOUT_MS); }
                    catch (TimeoutException)
                    {
                        error = route.Label + " did not accept a Desktop Bridge connection within "
                            + CONNECT_TIMEOUT_MS + " ms";
                        return false;
                    }

                    byte[] body = new UTF8Encoding(false).GetBytes(payload.ToString(Formatting.None));
                    string head = "POST / HTTP/1.1\r\n"
                        + "Host: localhost\r\n"
                        + "Authorization: Bearer " + route.Token + "\r\n"
                        + "Content-Type: application/json\r\n"
                        + "Content-Length: " + body.Length + "\r\n"
                        + "Connection: close\r\n\r\n";
                    byte[] header = Encoding.ASCII.GetBytes(head);

                    stream.Write(header, 0, header.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();
                    wrote = true;

                    int statusCode;
                    string responseBody;
                    if (!TryReadHttpResponse(
                            stream, REQUEST_TIMEOUT_MS, out statusCode, out responseBody, out error))
                    {
                        return false;
                    }

                    if (statusCode != 200)
                    {
                        error = route.Label + " Desktop Bridge answered HTTP " + statusCode
                            + (responseBody.Length == 0 ? string.Empty : ": " + responseBody);
                        return false;
                    }

                    try { response = JObject.Parse(responseBody); }
                    catch (Exception)
                    {
                        error = route.Label + " Desktop Bridge answered with unreadable JSON";
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Reads one bounded HTTP response, including content-length and chunked bodies.</summary>
        /// <param name="stream">Connected bridge pipe.</param>
        /// <param name="timeoutMs">Whole-response deadline.</param>
        /// <param name="statusCode">Receives the HTTP status.</param>
        /// <param name="body">Receives the decoded body.</param>
        /// <param name="error">Receives why a complete response did not arrive.</param>
        /// <returns>True when a complete response was decoded.</returns>
        private static bool TryReadHttpResponse(
            Stream stream, int timeoutMs, out int statusCode, out string body, out string error)
        {
            statusCode = 0;
            body = null;
            error = null;
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            using (var received = new MemoryStream())
            {
                var buffer = new byte[8192];
                for (;;)
                {
                    if (TryParseHttpResponse(received.ToArray(), false, out statusCode, out body, out error))
                    {
                        return true;
                    }

                    if (received.Length >= MAX_RESPONSE_BYTES)
                    {
                        error = "Cursor Desktop Bridge response exceeded " + MAX_RESPONSE_BYTES + " bytes";
                        return false;
                    }

                    int remaining = Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (DateTime.UtcNow >= deadline)
                    {
                        error = "Cursor Desktop Bridge did not finish its response within "
                            + timeoutMs + " ms";
                        return false;
                    }

                    IAsyncResult pending = stream.BeginRead(buffer, 0, buffer.Length, null, null);
                    if (!pending.AsyncWaitHandle.WaitOne(remaining))
                    {
                        error = "Cursor Desktop Bridge accepted the request and then stayed silent for "
                            + timeoutMs + " ms";
                        return false;
                    }

                    int count = stream.EndRead(pending);
                    if (count <= 0)
                    {
                        return TryParseHttpResponse(
                            received.ToArray(), true, out statusCode, out body, out error);
                    }

                    received.Write(buffer, 0, count);
                }
            }
        }

        /// <summary>Parses a complete HTTP response when enough bytes have arrived.</summary>
        /// <param name="raw">Bytes read so far.</param>
        /// <param name="closed">Whether the peer closed the connection.</param>
        /// <param name="statusCode">Receives the HTTP status.</param>
        /// <param name="body">Receives the response body.</param>
        /// <param name="error">Receives a malformed-response reason.</param>
        /// <returns>True only when the response is complete and usable.</returns>
        private static bool TryParseHttpResponse(
            byte[] raw, bool closed, out int statusCode, out string body, out string error)
        {
            statusCode = 0;
            body = null;
            error = null;

            int headerEnd = IndexOf(raw, new byte[] { 13, 10, 13, 10 });
            if (headerEnd < 0)
            {
                if (closed) error = "Cursor Desktop Bridge closed before completing its HTTP headers";
                return false;
            }

            string headerText = Encoding.ASCII.GetString(raw, 0, headerEnd);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] status = lines[0].Split(' ');
            if (status.Length < 2 || !int.TryParse(status[1], out statusCode))
            {
                error = "Cursor Desktop Bridge returned an invalid HTTP status line";
                return false;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < lines.Length; index++)
            {
                int colon = lines[index].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[index].Substring(0, colon).Trim()] = lines[index].Substring(colon + 1).Trim();
            }

            int bodyStart = headerEnd + 4;
            string transfer;
            if (headers.TryGetValue("Transfer-Encoding", out transfer)
                && transfer.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                byte[] decoded;
                if (!TryDecodeChunks(raw, bodyStart, out decoded, out error)) return false;
                body = Encoding.UTF8.GetString(decoded);
                return true;
            }

            string lengthText;
            int length;
            if (headers.TryGetValue("Content-Length", out lengthText)
                && int.TryParse(lengthText, out length) && length >= 0)
            {
                if (raw.Length < bodyStart + length) return false;
                body = Encoding.UTF8.GetString(raw, bodyStart, length);
                return true;
            }

            if (!closed) return false;
            body = Encoding.UTF8.GetString(raw, bodyStart, raw.Length - bodyStart);
            return true;
        }

        /// <summary>Decodes a complete HTTP chunked body.</summary>
        /// <param name="raw">Whole response bytes read so far.</param>
        /// <param name="offset">First chunk-size byte.</param>
        /// <param name="decoded">Receives the decoded body.</param>
        /// <param name="error">Receives a malformed-body reason.</param>
        /// <returns>True after the terminating chunk arrived.</returns>
        private static bool TryDecodeChunks(byte[] raw, int offset, out byte[] decoded, out string error)
        {
            decoded = null;
            error = null;

            using (var output = new MemoryStream())
            {
                int cursor = offset;
                for (;;)
                {
                    int lineEnd = IndexOf(raw, new byte[] { 13, 10 }, cursor);
                    if (lineEnd < 0) return false;

                    string sizeText = Encoding.ASCII.GetString(raw, cursor, lineEnd - cursor);
                    int semicolon = sizeText.IndexOf(';');
                    if (semicolon >= 0) sizeText = sizeText.Substring(0, semicolon);

                    int size;
                    if (!int.TryParse(
                            sizeText.Trim(), System.Globalization.NumberStyles.HexNumber,
                            System.Globalization.CultureInfo.InvariantCulture, out size) || size < 0)
                    {
                        error = "Cursor Desktop Bridge returned an invalid HTTP chunk size";
                        return false;
                    }

                    cursor = lineEnd + 2;
                    if (size == 0)
                    {
                        decoded = output.ToArray();
                        return true;
                    }

                    if (raw.Length < cursor + size + 2) return false;
                    output.Write(raw, cursor, size);
                    cursor += size;
                    if (raw[cursor] != 13 || raw[cursor + 1] != 10)
                    {
                        error = "Cursor Desktop Bridge returned a malformed HTTP chunk";
                        return false;
                    }

                    cursor += 2;
                }
            }
        }

        /// <summary>Finds a byte sequence in a buffer.</summary>
        /// <param name="source">Buffer to search.</param>
        /// <param name="needle">Sequence to find.</param>
        /// <param name="start">First offset to consider.</param>
        /// <returns>First matching offset, or -1.</returns>
        private static int IndexOf(byte[] source, byte[] needle, int start = 0)
        {
            for (int index = Math.Max(0, start); index <= source.Length - needle.Length; index++)
            {
                bool match = true;
                for (int part = 0; part < needle.Length; part++)
                {
                    if (source[index + part] == needle[part]) continue;
                    match = false;
                    break;
                }

                if (match) return index;
            }

            return -1;
        }

        #endregion
    }
}
