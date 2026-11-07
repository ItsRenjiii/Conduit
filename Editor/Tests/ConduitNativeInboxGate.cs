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
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit.Tests
{
    /// <summary>Proves Cursor native delivery chooses its active and idle paths from fresh state.</summary>
    internal static class ConduitNativeInboxGate
    {
        /// <summary>Maximum time allowed for the disposable bridge worker to become ready.</summary>
        private const int READY_TIMEOUT_MS = 2000;

        /// <summary>Maximum time allowed for all four request exchanges to finish.</summary>
        private const int FINISH_TIMEOUT_MS = 30000;

        /// <summary>Fixed test bearer with Cursor's required 64-hex-character shape.</summary>
        private const string TOKEN =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        /// <summary>Runs the isolated Cursor Desktop Bridge contract checks.</summary>
        [MenuItem("Tools/Conduit Gates/Verify native inbox routing")]
        private static void Verify()
        {
            string suffix = Guid.NewGuid().ToString("N");
            string threadId = "conduit-cursor-gate-" + suffix;
            string pipeName = "conduit-cursor-gate-" + suffix;
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cursor", "desktop-bridge");
            string recordPath = Path.Combine(folder, threadId + ".json");

            var ready = new ManualResetEvent(false);
            var requests = new List<JObject>();
            Exception workerFailure = null;
            bool cancelled = false;
            var worker = new Thread(() =>
            {
                try
                {
                    for (int index = 0; index < 4 && !cancelled; index++)
                    {
                        using (var pipe = new NamedPipeServerStream(
                            pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                            PipeOptions.None))
                        {
                            if (index == 0) ready.Set();
                            pipe.WaitForConnection();
                            if (cancelled) return;

                            string authorization;
                            JObject request = ReadRequest(pipe, out authorization);
                            if (authorization != "Bearer " + TOKEN)
                            {
                                throw new InvalidDataException("Desktop Bridge bearer was absent or changed");
                            }

                            lock (requests) requests.Add(request);
                            JObject response;
                            if ((string)request["type"] == "listThreads")
                            {
                                string status = index < 2 ? "running" : "idle";
                                response = new JObject
                                {
                                    ["threads"] = new JArray
                                    {
                                        new JObject
                                        {
                                            ["id"] = threadId,
                                            ["status"] = status,
                                            ["source"] = "composer",
                                            ["lastUpdatedAt"] = index + 1,
                                        },
                                    },
                                };
                            }
                            else
                            {
                                response = new JObject { ["status"] = "submitted" };
                            }

                            WriteResponse(pipe, response);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!cancelled) workerFailure = ex;
                }
                finally { ready.Set(); }
            });

            worker.IsBackground = true;
            worker.Start();

            bool activeAccepted = false;
            bool idleAccepted = false;
            string activeDetail = null;
            string idleDetail = null;
            bool activeUnconfirmed;
            bool idleUnconfirmed;
            try
            {
                if (!ready.WaitOne(READY_TIMEOUT_MS))
                {
                    throw new TimeoutException("Disposable Cursor bridge did not become ready");
                }

                Directory.CreateDirectory(folder);
                var record = new JObject
                {
                    ["protocolVersion"] = 1,
                    ["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id,
                    ["socketPath"] = @"\\.\pipe\" + pipeName,
                    ["token"] = TOKEN,
                    ["appName"] = "Conduit gate",
                    ["appVersion"] = "1",
                };
                File.WriteAllText(recordPath, record.ToString(Formatting.None), new UTF8Encoding(false));

                activeAccepted = ConduitCursorInbox.TryDeliver(
                    threadId, "active probe", out activeDetail, out activeUnconfirmed);
                idleAccepted = ConduitCursorInbox.TryDeliver(
                    threadId, "idle probe", out idleDetail, out idleUnconfirmed);

                if (!worker.Join(FINISH_TIMEOUT_MS))
                {
                    throw new TimeoutException("Disposable Cursor bridge did not finish");
                }
            }
            catch (Exception ex)
            {
                workerFailure = workerFailure ?? ex;
            }
            finally
            {
                cancelled = true;
                try { if (File.Exists(recordPath)) File.Delete(recordPath); } catch (Exception) { }
                ReleaseWorker(pipeName, worker);
                ready.Dispose();
            }

            JObject[] captured;
            lock (requests) captured = requests.ToArray();
            bool sequence = captured.Length == 4
                && (string)captured[0]["type"] == "listThreads"
                && (string)captured[1]["type"] == "sendMessage"
                && (bool?)captured[1]["force"] == true
                && (string)captured[2]["type"] == "listThreads"
                && (string)captured[3]["type"] == "sendMessage"
                && (bool?)captured[3]["force"] == false;
            bool passed = workerFailure == null && activeAccepted && idleAccepted && sequence;
            string report = "Cursor native inbox gate\n"
                + "  " + (activeAccepted ? "PASS" : "FAIL") + " active -> " + activeDetail + "\n"
                + "  " + (idleAccepted ? "PASS" : "FAIL") + " idle -> " + idleDetail + "\n"
                + "  " + (sequence ? "PASS" : "FAIL")
                + " request sequence -> " + Sequence(captured)
                + (workerFailure == null ? string.Empty
                    : "\n  FAIL worker -> " + workerFailure.GetType().Name + ": " + workerFailure.Message);

            if (passed) Debug.Log("[NativeInboxGate] " + report);
            else Debug.LogError("[NativeInboxGate] " + report);
        }

        /// <summary>Reads one bounded request body from the disposable pipe.</summary>
        /// <param name="stream">Connected pipe.</param>
        /// <param name="authorization">Receives the Authorization header.</param>
        /// <returns>Parsed JSON request.</returns>
        private static JObject ReadRequest(Stream stream, out string authorization)
        {
            var bytes = new List<byte>();
            int headerEnd = -1;
            while (headerEnd < 0 && bytes.Count < 64 * 1024)
            {
                int value = stream.ReadByte();
                if (value < 0) throw new EndOfStreamException("Bridge request ended in its headers");
                bytes.Add((byte)value);
                int count = bytes.Count;
                if (count >= 4 && bytes[count - 4] == 13 && bytes[count - 3] == 10
                    && bytes[count - 2] == 13 && bytes[count - 1] == 10)
                {
                    headerEnd = count;
                }
            }

            if (headerEnd < 0) throw new InvalidDataException("Bridge request headers exceeded the gate limit");
            string headers = Encoding.ASCII.GetString(bytes.ToArray(), 0, headerEnd);
            int length = -1;
            authorization = null;
            foreach (string line in headers.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(value, out length);
                }
                else if (string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
                {
                    authorization = value;
                }
            }

            if (length < 0 || length > 1024 * 1024)
            {
                throw new InvalidDataException("Bridge request Content-Length was absent or invalid");
            }

            var body = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int count = stream.Read(body, offset, length - offset);
                if (count <= 0) throw new EndOfStreamException("Bridge request ended in its body");
                offset += count;
            }

            return JObject.Parse(Encoding.UTF8.GetString(body));
        }

        /// <summary>Writes one complete JSON HTTP response to the disposable pipe.</summary>
        /// <param name="stream">Connected pipe.</param>
        /// <param name="response">Response object.</param>
        private static void WriteResponse(Stream stream, JObject response)
        {
            byte[] body = new UTF8Encoding(false).GetBytes(response.ToString(Formatting.None));
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                + body.Length + "\r\nConnection: close\r\n\r\n");
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        /// <summary>Releases a worker left waiting after a failed assertion.</summary>
        /// <param name="pipeName">Disposable pipe.</param>
        /// <param name="worker">Worker to release.</param>
        private static void ReleaseWorker(string pipeName, Thread worker)
        {
            for (int attempt = 0; worker.IsAlive && attempt < 4; attempt++)
            {
                try
                {
                    using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                    {
                        client.Connect(100);
                    }
                }
                catch (Exception) { }
            }

            if (worker.IsAlive) worker.Join(500);
        }

        /// <summary>Formats captured request kinds and force values for a useful failure report.</summary>
        /// <param name="requests">Captured requests.</param>
        /// <returns>Compact sequence.</returns>
        private static string Sequence(JObject[] requests)
        {
            var values = new List<string>();
            foreach (JObject request in requests)
            {
                string value = (string)request["type"] ?? "(absent)";
                if (request["force"] != null) value += " force=" + (bool)request["force"];
                values.Add(value);
            }

            return values.Count == 0 ? "(none)" : string.Join(", ", values.ToArray());
        }
    }
}
