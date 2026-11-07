/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Carries one accepted MCP response across a Unity managed-domain reload.
//
// Unity 2022's Mono HttpListener owns the accepted Socket through the private chain
// HttpListenerContext.cnc -> HttpConnection.sock. HttpListener.Close(true) normally calls
// Socket.Shutdown(Both), so merely duplicating the handle is not enough: shutdown poisons every
// duplicate. The handoff therefore duplicates the kernel handle and detaches the managed Socket
// from HttpConnection before the old listener is closed. The old AppDomain can then disappear
// while the duplicate remains in Unity's process handle table. The new domain writes one complete
// HTTP response through Winsock after the durable job reaches its real terminal state.
//
// This is deliberately Windows/Mono-specific. Reflection or handle duplication failure leaves the
// registration untouched, and the existing durable-job response path remains the fallback.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Preserves accepted MCP response sockets across a Unity managed-domain reload and writes their
    /// durable terminal results from the replacement domain.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConduitReloadResponseHandoff
    {
        #region Constants

        /// <summary>Editor-session key carrying process-local socket handles between managed domains.</summary>
        private const string SESSION_KEY = "ItsRenji.Conduit.ReloadResponseHandoffs.v1";

        /// <summary>Largest number of response sockets retained during one Editor session.</summary>
        private const int MAX_HANDOFFS = 64;

        /// <summary>Largest terminal MCP body written through a preserved socket.</summary>
        private const int MAX_RESPONSE_BYTES = 1024 * 1024;

        /// <summary>Longest a raw loopback socket send may block the Editor thread.</summary>
        private const int SEND_TIMEOUT_MS = 10000;

        /// <summary>Maximum lifetime of a process-local socket record without a terminal job.</summary>
        private static readonly TimeSpan MAX_AGE = TimeSpan.FromMinutes(10);

        /// <summary>Marker that enables the temporary domain-handoff trace during live diagnosis.</summary>
        private static readonly string TRACE_MARKER = Path.Combine(
            "Library", "ItsRenji", "ConduitTransport", "handoff-trace.enabled");

        /// <summary>Temporary diagnostic record written only while the trace marker exists.</summary>
        private static readonly string TRACE_PATH = Path.Combine(
            "Library", "ItsRenji", "ConduitTransport", "handoff-trace.log");

        #endregion

        #region Private state

        /// <summary>Serializes listener-thread registration against main-thread reload capture.</summary>
        private static readonly object _gate = new object();

        /// <summary>Managed-domain requests currently parked on durable job completion.</summary>
        private static readonly List<Registration> _registrations = new List<Registration>();

        /// <summary>Native socket handles retained in the Unity process for the replacement domain.</summary>
        private static readonly List<StoredHandoff> _stored = new List<StoredHandoff>();

        #endregion

        #region Lifecycle

        /// <summary>Restores process-local handles and arranges cleanup on a real Editor exit.</summary>
        static ConduitReloadResponseHandoff()
        {
            // Import workers load this assembly too; see ConduitBridge's static constructor. A worker holds
            // no response sockets of its own, so restoring them there is meaningless as well as unsafe.
            if (UnityEngine.Application.isBatchMode) return;

            Restore();
            Trace("domain initialized; restored=" + _stored.Count);
            EditorApplication.quitting -= CloseAll;
            EditorApplication.quitting += CloseAll;
        }

        #endregion

        #region State records

        /// <summary>Tracks one old-domain listener thread while it waits for a durable terminal record.</summary>
        internal sealed class Registration : IDisposable
        {
            /// <summary>Accepted request whose response may need to cross a managed-domain reload.</summary>
            internal readonly HttpListenerContext Context;

            /// <summary>Typed JSON-RPC identity echoed by the eventual terminal response.</summary>
            internal readonly JToken RequestId;

            /// <summary>Executed bridge verb used to format conventions and terminal evidence.</summary>
            internal readonly string Verb;

            /// <summary>Durable job whose terminal transition supplies the response body.</summary>
            internal readonly string JobId;

            /// <summary>Whether the eventual result needs the modern protocol response envelope.</summary>
            internal readonly bool Modern;

            /// <summary>Whether reload capture transferred the response path out of this domain.</summary>
            internal volatile bool HandedOff;

            /// <summary>Prevents duplicate removal from the live registration set.</summary>
            private bool _disposed;

            /// <summary>Captures immutable response identity before the listener thread waits.</summary>
            /// <param name="context">Accepted HTTP exchange.</param>
            /// <param name="requestId">Typed JSON-RPC request id.</param>
            /// <param name="verb">Canonical bridge verb.</param>
            /// <param name="jobId">Durable job id.</param>
            /// <param name="modern">Whether the result uses the modern protocol envelope.</param>
            internal Registration(
                HttpListenerContext context, JToken requestId, string verb, string jobId, bool modern)
            {
                Context = context;
                RequestId = requestId == null ? JValue.CreateNull() : requestId.DeepClone();
                Verb = verb;
                JobId = jobId;
                Modern = modern;
            }

            /// <summary>Removes this request from future reload capture.</summary>
            public void Dispose()
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _registrations.Remove(this);
                }
            }
        }

        /// <summary>Serializable shape persisted through Unity's native Editor session state.</summary>
        private sealed class StoredHandoff
        {
            /// <summary>Winsock handle duplicated in the current Unity process.</summary>
            internal long Handle;

            /// <summary>Process that owns the numeric handle.</summary>
            internal int ProcessId;

            /// <summary>UTC process-start ticks that prevent PID reuse from validating a stale handle.</summary>
            internal long ProcessStartUtcTicks;

            /// <summary>Durable job whose terminal record completes the response.</summary>
            internal string JobId;

            /// <summary>Typed JSON-RPC identity echoed to the client.</summary>
            internal JToken RequestId;

            /// <summary>Canonical bridge verb used to render the terminal result.</summary>
            internal string Verb;

            /// <summary>Whether the eventual result needs the modern protocol response envelope.</summary>
            internal bool Modern;

            /// <summary>UTC capture ticks used to reject abandoned handles without locale parsing.</summary>
            internal long CapturedUtcTicks;
        }

        /// <summary>Rollback data retained until a newly detached socket is safely persisted.</summary>
        private sealed class Captured
        {
            /// <summary>Listener-thread registration transferred by this capture.</summary>
            internal Registration Registration;

            /// <summary>Mono HttpConnection whose private socket field was detached.</summary>
            internal object Connection;

            /// <summary>Private field restored if persistence fails.</summary>
            internal FieldInfo SocketField;

            /// <summary>Original managed socket restored if persistence fails.</summary>
            internal Socket OriginalSocket;

            /// <summary>Replacement-domain record for the duplicated handle.</summary>
            internal StoredHandoff Stored;
        }

        #endregion

        #region Handoff API

        /// <summary>Registers one listener-thread tool call as eligible for a reload handoff.</summary>
        /// <param name="context">Accepted HTTP exchange.</param>
        /// <param name="requestId">Typed JSON-RPC request id.</param>
        /// <param name="verb">Canonical bridge verb.</param>
        /// <param name="jobId">Durable job id.</param>
        /// <param name="modern">Whether the result uses the modern protocol envelope.</param>
        /// <returns>A registration whose handoff flag distinguishes reload from an ordinary wait timeout.</returns>
        internal static Registration Register(
            HttpListenerContext context, JToken requestId, string verb, string jobId, bool modern)
        {
            var registration = new Registration(context, requestId, verb, jobId, modern);
            lock (_gate)
            {
                _registrations.Add(registration);
                Trace("registered job=" + jobId + " count=" + _registrations.Count);
            }
            return registration;
        }

        /// <summary>
        /// Steals every eligible accepted socket before HttpListener shutdown. A failed capture is rolled back
        /// to the original HttpConnection so the established durable-job fallback can answer it normally.
        /// </summary>
        internal static void CaptureForReload()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;

            List<Registration> registrations;
            lock (_gate) registrations = new List<Registration>(_registrations);
            Trace("capture entered; registrations=" + registrations.Count + " stored=" + _stored.Count);
            if (registrations.Count == 0) return;

            var captured = new List<Captured>();
            foreach (var registration in registrations)
            {
                if (_stored.Count + captured.Count >= MAX_HANDOFFS) break;
                Captured item;
                if (TryCapture(registration, out item)) captured.Add(item);
            }
            if (captured.Count == 0) return;

            lock (_gate)
            {
                foreach (var item in captured) _stored.Add(item.Stored);
                try
                {
                    PersistLocked();
                    foreach (var item in captured) item.Registration.HandedOff = true;
                    Trace("capture persisted=" + captured.Count + " stored=" + _stored.Count);
                }
                catch (Exception ex)
                {
                    Trace("capture persistence failed: " + ex);
                    foreach (var item in captured)
                    {
                        _stored.Remove(item.Stored);
                        try { item.SocketField.SetValue(item.Connection, item.OriginalSocket); } catch { }
                        NativeSocket.Close(new IntPtr(item.Stored.Handle));
                    }
                }
            }
        }

        /// <summary>Writes terminal MCP answers onto every socket waiting for this exact durable job.</summary>
        /// <param name="jobId">Job that reached a terminal state.</param>
        /// <param name="record">Persisted terminal record.</param>
        internal static void OnJobTerminal(string jobId, JObject record)
        {
            if (string.IsNullOrEmpty(jobId) || record == null) return;
            Trace("terminal job=" + jobId + " state=" + (string)record["state"] + " stored=" + _stored.Count);

            var ready = new List<StoredHandoff>();
            lock (_gate)
            {
                for (var index = _stored.Count - 1; index >= 0; index--)
                {
                    if (_stored[index].JobId != jobId) continue;
                    ready.Add(_stored[index]);
                    _stored.RemoveAt(index);
                }
                if (ready.Count > 0) PersistLocked();
            }

            foreach (var handoff in ready)
            {
                try
                {
                    var body = ConduitMcpEndpoint.TerminalToolResponseJson(
                        handoff.RequestId, handoff.Verb, handoff.JobId, record, handoff.Modern);
                    NativeSocket.WriteHttpJson(new IntPtr(handoff.Handle), body);
                    Trace("response sent job=" + handoff.JobId + " bytes=" + Encoding.UTF8.GetByteCount(body));
                }
                catch (Exception ex)
                {
                    Trace("response failed job=" + handoff.JobId + ": " + ex);
                }
                finally
                {
                    NativeSocket.Close(new IntPtr(handoff.Handle));
                }
            }
        }

        /// <summary>Closes process-local handles when the Editor exits rather than reloads.</summary>
        internal static void CloseAll()
        {
            lock (_gate)
            {
                foreach (var handoff in _stored) NativeSocket.Close(new IntPtr(handoff.Handle));
                _stored.Clear();
                SessionState.EraseString(SESSION_KEY);
            }
        }

        #endregion

        #region Detached sockets

        /// <summary>
        /// Takes ownership of one accepted connection, returning a process-local handle and leaving the
        /// managed listener with nothing to close. This is the same duplicate-and-detach the reload capture
        /// performs, exposed for a caller that wants to own a connection for longer than one response: a
        /// held-open stream cannot be left in Mono's hands, because closing the listener force-shuts every
        /// socket it still owns.
        /// </summary>
        /// <param name="context">Accepted request whose connection is being taken over.</param>
        /// <returns>The duplicated handle, or <c>IntPtr.Zero</c> when the connection could not be detached.</returns>
        internal static IntPtr DetachSocket(HttpListenerContext context)
        {
            try
            {
                var connectionField = typeof(HttpListenerContext).GetField(
                    "cnc", BindingFlags.Instance | BindingFlags.NonPublic);
                if (connectionField == null) return IntPtr.Zero;
                var connection = connectionField.GetValue(context);
                if (connection == null) return IntPtr.Zero;

                var socketField = connection.GetType().GetField(
                    "sock", BindingFlags.Instance | BindingFlags.NonPublic);
                if (socketField == null) return IntPtr.Zero;
                var socket = socketField.GetValue(connection) as Socket;
                if (socket == null) return IntPtr.Zero;

                var duplicate = NativeSocket.Duplicate(socket.Handle);
                try
                {
                    socketField.SetValue(connection, null);
                }
                catch
                {
                    NativeSocket.Close(duplicate);
                    return IntPtr.Zero;
                }

                Trace("socket detached source=" + socket.Handle + " duplicate=" + duplicate);
                return duplicate;
            }
            catch (Exception ex)
            {
                Trace("socket detach failed: " + ex);
                return IntPtr.Zero;
            }
        }

        /// <summary>Writes bytes to a detached socket.</summary>
        /// <param name="socket">Handle from <see cref="DetachSocket"/>.</param>
        /// <param name="bytes">Buffer to write in full.</param>
        /// <returns>True when every byte was written; false when the peer has gone.</returns>
        internal static bool SendRaw(IntPtr socket, byte[] bytes)
        {
            if (socket == IntPtr.Zero || bytes == null) return false;

            try
            {
                NativeSocket.Send(socket, bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Releases a detached socket.</summary>
        /// <param name="socket">Handle from <see cref="DetachSocket"/>.</param>
        internal static void CloseRaw(IntPtr socket)
        {
            NativeSocket.Close(socket);
        }

        #endregion

        #region Capture and persistence

        /// <summary>Duplicates and detaches one accepted Mono HttpListener socket.</summary>
        /// <param name="registration">Waiting request to capture.</param>
        /// <param name="captured">Receives rollback and persistence state.</param>
        /// <returns>True when a duplicate handle was created and the managed owner was detached.</returns>
        private static bool TryCapture(Registration registration, out Captured captured)
        {
            captured = null;
            try
            {
                var connectionField = typeof(HttpListenerContext).GetField(
                    "cnc", BindingFlags.Instance | BindingFlags.NonPublic);
                if (connectionField == null) return false;
                var connection = connectionField.GetValue(registration.Context);
                if (connection == null) return false;

                var socketField = connection.GetType().GetField(
                    "sock", BindingFlags.Instance | BindingFlags.NonPublic);
                if (socketField == null) return false;
                var socket = socketField.GetValue(connection) as Socket;
                if (socket == null) return false;

                var duplicate = NativeSocket.Duplicate(socket.Handle);
                try { socketField.SetValue(connection, null); }
                catch
                {
                    NativeSocket.Close(duplicate);
                    return false;
                }

                int processId;
                long processStartUtcTicks;
                using (var process = Process.GetCurrentProcess())
                {
                    processId = process.Id;
                    processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
                }

                captured = new Captured
                {
                    Registration = registration,
                    Connection = connection,
                    SocketField = socketField,
                    OriginalSocket = socket,
                    Stored = new StoredHandoff
                    {
                        Handle = duplicate.ToInt64(),
                        ProcessId = processId,
                        ProcessStartUtcTicks = processStartUtcTicks,
                        JobId = registration.JobId,
                        RequestId = registration.RequestId.DeepClone(),
                        Verb = registration.Verb,
                        Modern = registration.Modern,
                        CapturedUtcTicks = DateTime.UtcNow.Ticks,
                    },
                };
                Trace("socket captured job=" + registration.JobId + " source=" + socket.Handle
                    + " duplicate=" + duplicate);
                return true;
            }
            catch (Exception ex)
            {
                Trace("socket capture failed job=" + registration.JobId + ": " + ex);
                return false;
            }
        }

        /// <summary>Restores valid process-local handle records left by the previous managed domain.</summary>
        private static void Restore()
        {
            var encoded = SessionState.GetString(SESSION_KEY, string.Empty);
            Trace("restore read session bytes=" + encoded.Length);
            if (string.IsNullOrEmpty(encoded)) return;

            JArray values;
            try { values = JArray.Parse(encoded); }
            catch (Exception ex)
            {
                Trace("restore parse failed: " + ex);
                SessionState.EraseString(SESSION_KEY);
                return;
            }

            int processId;
            long processStartUtcTicks;
            using (var process = Process.GetCurrentProcess())
            {
                processId = process.Id;
                processStartUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            }
            foreach (var token in values)
            {
                var value = token as JObject;
                var handle = (long?)value?["handle"] ?? 0;
                var storedProcessId = (int?)value?["processId"] ?? 0;
                var storedProcessStartUtcTicks = (long?)value?["processStartUtcTicks"] ?? 0;
                var jobId = (string)value?["jobId"];
                var verb = (string)value?["verb"];
                var modern = (bool?)value?["modern"] == true;
                var requestId = value?["requestId"];
                var capturedUtcTicks = (long?)value?["capturedUtcTicks"] ?? 0;
                var ageTicks = DateTime.UtcNow.Ticks - capturedUtcTicks;
                var validTime = capturedUtcTicks > 0 && ageTicks >= -TimeSpan.FromMinutes(1).Ticks
                    && ageTicks <= MAX_AGE.Ticks;
                if (handle == 0 || storedProcessId != processId
                    || storedProcessStartUtcTicks != processStartUtcTicks || string.IsNullOrEmpty(jobId)
                    || string.IsNullOrEmpty(verb) || requestId == null || !validTime)
                {
                    Trace("restore rejected handle=" + handle + " storedPid=" + storedProcessId
                        + " currentPid=" + processId + " storedStart=" + storedProcessStartUtcTicks
                        + " currentStart=" + processStartUtcTicks + " job=" + (jobId ?? "null")
                        + " verb=" + (verb ?? "null") + " request=" + (requestId == null ? "null" : requestId.Type.ToString())
                        + " capturedTicks=" + capturedUtcTicks + " ageTicks=" + ageTicks
                        + " validTime=" + validTime);
                    if (handle != 0 && storedProcessId == processId
                        && storedProcessStartUtcTicks == processStartUtcTicks)
                    {
                        NativeSocket.Close(new IntPtr(handle));
                    }
                    continue;
                }

                _stored.Add(new StoredHandoff
                {
                    Handle = handle,
                    ProcessId = processId,
                    ProcessStartUtcTicks = processStartUtcTicks,
                    JobId = jobId,
                    RequestId = requestId.DeepClone(),
                    Verb = verb,
                    Modern = modern,
                    CapturedUtcTicks = capturedUtcTicks,
                });
                Trace("restore accepted job=" + jobId + " handle=" + handle);
            }
            PersistLocked();
        }

        /// <summary>Writes the current handle set to Unity's native Editor-session storage.</summary>
        private static void PersistLocked()
        {
            if (_stored.Count == 0)
            {
                SessionState.EraseString(SESSION_KEY);
                return;
            }

            var values = new JArray();
            foreach (var handoff in _stored)
            {
                values.Add(new JObject
                {
                    ["handle"] = handoff.Handle,
                    ["processId"] = handoff.ProcessId,
                    ["processStartUtcTicks"] = handoff.ProcessStartUtcTicks,
                    ["jobId"] = handoff.JobId,
                    ["requestId"] = handoff.RequestId.DeepClone(),
                    ["verb"] = handoff.Verb,
                    ["modern"] = handoff.Modern,
                    ["capturedUtcTicks"] = handoff.CapturedUtcTicks,
                });
            }
            SessionState.SetString(SESSION_KEY, values.ToString(Formatting.None));
        }

        /// <summary>Appends one marker-gated diagnostic line without involving Unity's Console.</summary>
        /// <param name="message">Observed handoff transition.</param>
        internal static void Trace(string message)
        {
            if (!File.Exists(TRACE_MARKER)) return;
            try
            {
                int processId;
                using (var process = Process.GetCurrentProcess()) processId = process.Id;
                Directory.CreateDirectory(Path.GetDirectoryName(TRACE_PATH));
                File.AppendAllText(TRACE_PATH, DateTime.UtcNow.ToString("O") + " pid=" + processId
                    + " " + message + Environment.NewLine);
            }
            catch { /* Diagnostic output must never affect the transport path being measured. */ }
        }

        #endregion

        #region Native socket operations

        /// <summary>Minimal Windows APIs used after Mono no longer owns the accepted connection.</summary>
        private static class NativeSocket
        {
            /// <summary>Requests an identical-access duplicate from DuplicateHandle.</summary>
            private const uint DUPLICATE_SAME_ACCESS = 0x00000002;

            /// <summary>Winsock socket option level.</summary>
            private const int SOL_SOCKET = 0xffff;

            /// <summary>Winsock blocking-send timeout option.</summary>
            private const int SO_SNDTIMEO = 0x1005;

            /// <summary>Returns the pseudo-handle for Unity's current process.</summary>
            /// <returns>Current-process pseudo-handle.</returns>
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern IntPtr GetCurrentProcess();

            /// <summary>Duplicates a kernel handle into the same Unity process.</summary>
            /// <param name="sourceProcess">Process owning the source handle.</param>
            /// <param name="sourceHandle">Handle to duplicate.</param>
            /// <param name="targetProcess">Process receiving the duplicate.</param>
            /// <param name="targetHandle">Receives the duplicate.</param>
            /// <param name="desiredAccess">Requested access mask.</param>
            /// <param name="inheritHandle">Whether child processes inherit the duplicate.</param>
            /// <param name="options">Duplication behavior flags.</param>
            /// <returns>True when Windows created the duplicate.</returns>
            [DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool DuplicateHandle(
                IntPtr sourceProcess, IntPtr sourceHandle, IntPtr targetProcess,
                out IntPtr targetHandle, uint desiredAccess, bool inheritHandle, uint options);

            /// <summary>Sends bytes through an accepted Winsock connection.</summary>
            /// <param name="socket">Accepted socket handle.</param>
            /// <param name="buffer">First byte to send.</param>
            /// <param name="length">Remaining byte count.</param>
            /// <param name="flags">Winsock send flags.</param>
            /// <returns>Number of bytes sent, or a negative value on failure.</returns>
            [DllImport("ws2_32.dll", SetLastError = true)]
            private static extern int send(IntPtr socket, IntPtr buffer, int length, int flags);

            /// <summary>Sets one integer option on an accepted Winsock connection.</summary>
            /// <param name="socket">Accepted socket handle.</param>
            /// <param name="level">Socket option level.</param>
            /// <param name="option">Socket option id.</param>
            /// <param name="value">Integer option value.</param>
            /// <param name="length">Size of the integer value.</param>
            /// <returns>Zero on success or a negative value on failure.</returns>
            [DllImport("ws2_32.dll", SetLastError = true)]
            private static extern int setsockopt(IntPtr socket, int level, int option, ref int value, int length);

            /// <summary>Disables one or both directions of an accepted Winsock connection.</summary>
            /// <param name="socket">Accepted socket handle.</param>
            /// <param name="how">Winsock shutdown direction.</param>
            /// <returns>Zero on success or a negative value on failure.</returns>
            [DllImport("ws2_32.dll", SetLastError = true)]
            private static extern int shutdown(IntPtr socket, int how);

            /// <summary>Releases one Winsock handle.</summary>
            /// <param name="socket">Socket handle to release.</param>
            /// <returns>Zero on success or a negative value on failure.</returns>
            [DllImport("ws2_32.dll", SetLastError = true)]
            private static extern int closesocket(IntPtr socket);

            /// <summary>Duplicates an accepted socket without changing its TCP connection state.</summary>
            /// <param name="source">Mono-owned accepted socket.</param>
            /// <returns>Process-local duplicate handle.</returns>
            internal static IntPtr Duplicate(IntPtr source)
            {
                var process = GetCurrentProcess();
                IntPtr duplicate;
                if (!DuplicateHandle(process, source, process, out duplicate, 0, false, DUPLICATE_SAME_ACCESS))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle failed");
                return duplicate;
            }

            /// <summary>Writes one complete close-delimited HTTP JSON response.</summary>
            /// <param name="socket">Detached accepted socket.</param>
            /// <param name="body">Serialized JSON-RPC response body.</param>
            internal static void WriteHttpJson(IntPtr socket, string body)
            {
                var bodyBytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
                if (bodyBytes.Length > MAX_RESPONSE_BYTES) throw new InvalidOperationException("response exceeds handoff limit");

                var timeout = SEND_TIMEOUT_MS;
                setsockopt(socket, SOL_SOCKET, SO_SNDTIMEO, ref timeout, sizeof(int));
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/json; charset=utf-8\r\n" +
                    "Content-Length: " + bodyBytes.Length + "\r\n" +
                    "Connection: close\r\n" +
                    "Cache-Control: no-store\r\n\r\n");
                SendAll(socket, head);
                SendAll(socket, bodyBytes);
                shutdown(socket, 1);
            }

            /// <summary>Releases a duplicated socket handle without throwing during teardown.</summary>
            /// <param name="socket">Duplicated Winsock handle.</param>
            internal static void Close(IntPtr socket)
            {
                if (socket == IntPtr.Zero || socket == new IntPtr(-1)) return;
                try { closesocket(socket); } catch { }
            }

            /// <summary>Sends a buffer under the standard send timeout, for a caller outside this class.</summary>
            /// <param name="socket">Detached accepted socket.</param>
            /// <param name="bytes">Complete buffer to write.</param>
            internal static void Send(IntPtr socket, byte[] bytes)
            {
                var timeout = SEND_TIMEOUT_MS;
                setsockopt(socket, SOL_SOCKET, SO_SNDTIMEO, ref timeout, sizeof(int));
                SendAll(socket, bytes);
            }

            /// <summary>Sends an entire byte array despite partial Winsock writes.</summary>
            /// <param name="socket">Detached accepted socket.</param>
            /// <param name="bytes">Complete buffer to write.</param>
            private static void SendAll(IntPtr socket, byte[] bytes)
            {
                var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var sent = send(socket, IntPtr.Add(pinned.AddrOfPinnedObject(), offset), bytes.Length - offset, 0);
                        if (sent <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Winsock send failed");
                        offset += sent;
                    }
                }
                finally { pinned.Free(); }
            }
        }

        #endregion
    }
}
