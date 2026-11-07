/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Gate for the production HTTP transport.
//
// Every check here was run by hand while the transport was being built, which is exactly the problem: a
// measurement typed into a terminal proves the code worked once and catches nothing afterwards. These are
// the same checks, kept.
//
// The gate talks to the transport the way a real client does - it reads the published descriptor for the
// port, identity and token rather than reaching into the type - so a descriptor that stops being readable
// fails here too.
//
// One structural note. Commands are drained on the main thread, which is the thread this gate runs on, so a
// check that posted a command and then blocked for the reply would deadlock against itself: the request
// would queue and nothing would ever run it. Those checks post from a background thread and pump the real
// ConduitHttpTransport.Drain from here.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit.Tests
{
    /// <summary>Exercises the transport's auth, routing, body limits, queue bound and command path.</summary>
    internal static class ConduitHttpTransportGate
    {
        #region Constants

        /// <summary>Milliseconds a check waits for a reply that does not need the main thread.</summary>
        internal const int DIRECT_TIMEOUT_MS = 5000;

        /// <summary>Milliseconds a check waits while pumping the drain for a queued command.</summary>
        private const int PUMPED_TIMEOUT_MS = 10000;

        /// <summary>Requests fired at once to prove the queue bound sheds load. Above the transport's 64.</summary>
        private const int BURST_SIZE = 72;

        /// <summary>Milliseconds a burst request waits before giving up, since most will never be drained.</summary>
        private const int BURST_TIMEOUT_MS = 750;

        #endregion

        #region Descriptor

        /// <summary>Connection facts a client needs, read from the published descriptor.</summary>
        internal sealed class Descriptor
        {
            /// <summary>Bound port.</summary>
            public int Port;

            /// <summary>Project identity digest.</summary>
            public string Identity;

            /// <summary>Bearer token.</summary>
            public string Token;
        }

        /// <summary>Reads the descriptor this Editor published.</summary>
        /// <param name="error">Receives why the descriptor could not be used.</param>
        /// <returns>The descriptor, or null.</returns>
        internal static Descriptor ReadDescriptor(out string error)
        {
            error = null;
            var directory = Path.Combine("Library", "ItsRenji", "ConduitTransport");
            if (!Directory.Exists(directory))
            {
                error = "no descriptor directory at " + directory;
                return null;
            }

            var files = Directory.GetFiles(directory, "*.json");
            if (files.Length != 1)
            {
                error = "expected exactly one descriptor, found " + files.Length;
                return null;
            }

            try
            {
                var json = JObject.Parse(File.ReadAllText(files[0], Encoding.UTF8));
                return new Descriptor
                {
                    Port = (int)json["port"],
                    Identity = (string)json["identity"],
                    Token = (string)json["token"],
                };
            }
            catch (Exception ex)
            {
                error = "descriptor unreadable: " + ex.Message;
                return null;
            }
        }

        #endregion

        #region Requests

        /// <summary>One request's outcome.</summary>
        internal sealed class Reply
        {
            /// <summary>HTTP status, or 0 when the request never got one.</summary>
            public int Status;

            /// <summary>Response body, or the transport error text.</summary>
            public string Body;
        }

        /// <summary>
        /// Sends one request and returns its status and body. A refusal is an answer here, not a failure, so
        /// the non-2xx statuses this gate is mostly checking come back through the normal path.
        /// </summary>
        /// <param name="descriptor">Connection facts.</param>
        /// <param name="method">HTTP method.</param>
        /// <param name="path">Request path.</param>
        /// <param name="identity">Identity header value, or null to omit the header.</param>
        /// <param name="token">Bearer value, or null to omit the header.</param>
        /// <param name="body">Request body bytes, or null.</param>
        /// <param name="chunked">True to send without a declared length.</param>
        /// <param name="timeoutMs">Client timeout.</param>
        /// <returns>The reply.</returns>
        internal static Reply Send(
            Descriptor descriptor,
            string method,
            string path,
            string identity,
            string token,
            byte[] body,
            bool chunked,
            int timeoutMs)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + descriptor.Port + path);
                request.Method = method;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                if (identity != null) request.Headers.Add("X-Conduit-Project", identity);
                if (token != null) request.Headers.Add("Authorization", "Bearer " + token);

                if (body != null)
                {
                    if (chunked) request.SendChunked = true;
                    else request.ContentLength = body.Length;

                    using (var stream = request.GetRequestStream())
                    {
                        stream.Write(body, 0, body.Length);
                    }
                }

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return new Reply { Status = (int)response.StatusCode, Body = reader.ReadToEnd() };
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null) return new Reply { Status = 0, Body = ex.Message };

                using (response)
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return new Reply { Status = (int)response.StatusCode, Body = reader.ReadToEnd() };
                }
            }
            catch (Exception ex)
            {
                return new Reply { Status = 0, Body = ex.Message };
            }
        }

        /// <summary>
        /// Posts a command from a background thread while pumping the drain here, because the drain and this
        /// gate share the main thread.
        /// </summary>
        /// <param name="descriptor">Connection facts.</param>
        /// <param name="command">Command line.</param>
        /// <returns>The reply, or a timeout reply.</returns>
        private static Reply SendCommandPumped(Descriptor descriptor, string command)
        {
            Reply reply = null;
            var worker = new Thread(() =>
            {
                reply = Send(descriptor, "POST", "/command", descriptor.Identity, descriptor.Token,
                    Encoding.UTF8.GetBytes(command), false, PUMPED_TIMEOUT_MS);
            });

            worker.IsBackground = true;
            worker.Start();

            var deadline = DateTime.UtcNow.AddMilliseconds(PUMPED_TIMEOUT_MS);
            while (reply == null && DateTime.UtcNow < deadline)
            {
                ConduitHttpTransport.Drain();
                Thread.Sleep(5);
            }

            worker.Join(500);
            return reply ?? new Reply { Status = 0, Body = "timed out waiting for a pumped reply" };
        }

        #endregion

        #region Gate

        /// <summary>Reads the envelope's outcome, so a report line says what happened rather than raw JSON.</summary>
        /// <param name="body">Response body.</param>
        /// <returns>The operation and outcome, or the raw body when it is not an envelope.</returns>
        internal static string Outcome(string body)
        {
            try
            {
                var json = JObject.Parse(body);
                return (string)json["operation"] + "/" + (string)json["outcome"];
            }
            catch
            {
                return body.Substring(0, Math.Min(60, body.Length));
            }
        }

        /// <summary>Reads the command result out of a completion envelope.</summary>
        /// <param name="body">Response body.</param>
        /// <returns>The result text, or null when the body is not a completion envelope.</returns>
        private static string ResultOf(string body)
        {
            try { return (string)JObject.Parse(body)["result"]; }
            catch { return null; }
        }

        /// <summary>Reads the job id out of an admission envelope.</summary>
        /// <param name="body">Response body.</param>
        /// <returns>The job id, or null.</returns>
        internal static string JobIdOf(string body)
        {
            try { return (string)JObject.Parse(body)["jobId"]; }
            catch { return null; }
        }

        /// <summary>Posts a durable command carrying caller identity.</summary>
        /// <param name="descriptor">Connection facts.</param>
        /// <param name="command">Command line.</param>
        /// <param name="sessionId">Client session.</param>
        /// <param name="requestId">Client request id.</param>
        /// <returns>The reply.</returns>
        internal static Reply SendDurable(Descriptor descriptor, string command, string sessionId, string requestId)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + descriptor.Port + "/command");
                request.Method = "POST";
                request.Timeout = DIRECT_TIMEOUT_MS;
                request.Headers.Add("X-Conduit-Project", descriptor.Identity);
                request.Headers.Add("Authorization", "Bearer " + descriptor.Token);
                request.Headers.Add("Mcp-Session-Id", sessionId);
                request.Headers.Add("X-Conduit-Request-Id", requestId);

                var body = Encoding.UTF8.GetBytes(command);
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return new Reply { Status = (int)response.StatusCode, Body = reader.ReadToEnd() };
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null) return new Reply { Status = 0, Body = ex.Message };

                using (response)
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return new Reply { Status = (int)response.StatusCode, Body = reader.ReadToEnd() };
                }
            }
            catch (Exception ex)
            {
                return new Reply { Status = 0, Body = ex.Message };
            }
        }

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

        /// <summary>Runs every transport check and reports.</summary>
        [MenuItem("Tools/Conduit Gates/Verify HTTP transport")]
        private static void Verify()
        {
            var report = new StringBuilder("HTTP transport - production ConduitHttpTransport\n");

            string descriptorError;
            var descriptor = ReadDescriptor(out descriptorError);
            if (descriptor == null)
            {
                Debug.LogError("[TransportGate] " + report + "  FAIL  descriptor: " + descriptorError);
                return;
            }

            report.AppendLine("descriptor: port " + descriptor.Port + ", identity " + descriptor.Identity.Substring(0, 16) + "...");

            // Raise the per-endpoint connection limit before any request, on the ServicePoint itself.
            // ServicePointManager.DefaultConnectionLimit only seeds ServicePoints created after it is set,
            // so setting it later - once the first health check has already created this endpoint's - does
            // nothing at all. Left unraised, the default of 2 means the burst below never reaches the
            // listener: it measures the client's connection pool and reports a server defect that is not
            // there.
            var servicePoint = ServicePointManager.FindServicePoint(new Uri("http://127.0.0.1:" + descriptor.Port + "/"));
            servicePoint.ConnectionLimit = BURST_SIZE * 2;
            report.AppendLine("client connection limit: " + servicePoint.ConnectionLimit);

            var ok = true;

            // Identity is required, not merely checked when present. The absent case is the one a
            // mismatch-only check waves through, and it is also the CSRF barrier.
            var healthy = Send(descriptor, "GET", "/health", descriptor.Identity, descriptor.Token, null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "health with full auth",
                healthy.Status == 200 && healthy.Body.Contains("\"operation\":\"health\"") && healthy.Body.Contains("\"ledger\""),
                healthy.Status + " " + healthy.Body.Substring(0, Math.Min(90, healthy.Body.Length)));

            // The activity block is what answers what the editor is doing while its main thread is busy, and
            // it is the part a refactor can drop without any endpoint failing on its way out.
            var missingKeys = new List<string>();
            foreach (var key in new[] { "activity", "busy", "reloading", "capturedSecondsAgo", "taskCount", "tasks",
                "lifecycle", "isPlaying", "isPlayingOrWillChangePlaymode", "isPaused", "isCompiling", "isUpdating",
                "isUdonSharpCreatePending" })
            {
                if (healthy.Body.IndexOf("\"" + key + "\"", StringComparison.Ordinal) < 0) missingKeys.Add(key);
            }

            ok &= Check(report, "health carries the activity block",
                missingKeys.Count == 0,
                missingKeys.Count == 0 ? "every key present" : "missing " + string.Join(", ", missingKeys.ToArray()));

            // Time zero: the snapshot has to be readable before Unity has reported anything at all, or the
            // first probe after a reload throws where it should have answered.
            var activity = ConduitEditorActivity.Current;
            ok &= Check(report, "activity snapshot exists at time zero",
                activity != null && activity.Tasks != null,
                activity == null
                    ? "snapshot was null"
                    : "tasks " + (activity.Tasks == null ? "null" : activity.Tasks.Length + " of " + activity.TaskCount));

            // The lifecycle block is what lets a caller learn the editor's state without the main thread.
            // It has to agree with the live properties here, where the update loop is demonstrably running,
            // or the capture is wrong everywhere it matters more.
            var live = activity == null ? null : activity.State;
            var agrees = live != null
                && live.Playing == EditorApplication.isPlaying
                && live.WillChangePlaymode == EditorApplication.isPlayingOrWillChangePlaymode
                && live.Paused == EditorApplication.isPaused
                && live.Compiling == EditorApplication.isCompiling
                && live.Updating == EditorApplication.isUpdating;
            ok &= Check(report, "captured lifecycle agrees with the live properties",
                agrees,
                live == null
                    ? "no lifecycle captured"
                    : "play=" + live.Playing + "/" + EditorApplication.isPlaying
                        + " will=" + live.WillChangePlaymode + "/" + EditorApplication.isPlayingOrWillChangePlaymode
                        + " pause=" + live.Paused + "/" + EditorApplication.isPaused
                        + " compile=" + live.Compiling + "/" + EditorApplication.isCompiling
                        + " update=" + live.Updating + "/" + EditorApplication.isUpdating);

            // The reload flag starts true because the static constructor runs mid-reload, so nothing clearing
            // it would leave every later probe claiming a reload that finished long ago.
            ok &= Check(report, "activity reload flag settles",
                activity != null && !activity.Reloading,
                activity == null
                    ? "snapshot was null"
                    : "reloading=" + activity.Reloading + " busy=" + activity.Busy + " fault=" + (activity.Fault ?? "none"));

            // The status verb has to answer without the main thread. This gate runs on the main thread and
            // deliberately does not pump the drain here, so a reply arriving at all is proof the verb was
            // never parked for a tick that is not coming. Before it was routed as thread free, this timed out.
            var busyStatus = Send(descriptor, "POST", "/command", descriptor.Identity, descriptor.Token,
                Encoding.UTF8.GetBytes("status"), false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "status answers without the main thread",
                busyStatus.Status == 200
                    && busyStatus.Body.IndexOf("isUdonSharpCreatePending", StringComparison.Ordinal) >= 0
                    && busyStatus.Body.IndexOf("isCompiling", StringComparison.Ordinal) >= 0,
                busyStatus.Status + " " + busyStatus.Body.Substring(0, Math.Min(140, busyStatus.Body.Length)));

            // Its JSON twin has to answer the same way, or the pair splits into one verb that survives a busy
            // editor and one that does not, which is the worse failure: a caller cannot tell which it holds.
            var busyState = Send(descriptor, "POST", "/command", descriptor.Identity, descriptor.Token,
                Encoding.UTF8.GetBytes("editor-application-get-state"), false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "editor-application-get-state answers without the main thread",
                busyState.Status == 200
                    && busyState.Body.IndexOf("timeSinceStartup", StringComparison.Ordinal) >= 0
                    && busyState.Body.IndexOf("unityVersion", StringComparison.Ordinal) >= 0,
                busyState.Status + " " + busyState.Body.Substring(0, Math.Min(140, busyState.Body.Length)));

            // The editor clock is reproduced off an anchor taken at domain load rather than read. Both sides
            // of that anchor are wall clocks, so it should not drift at all, and this is where a drift that
            // theory says cannot happen would actually show up. Compared on the main thread, where the real
            // property can be read to compare against.
            var reproduced = ConduitEditorActivity.TimeSinceStartup;
            var actual = EditorApplication.timeSinceStartup;
            ok &= Check(report, "reproduced editor clock matches the real one",
                Math.Abs(reproduced - actual) < 0.25d,
                "reproduced " + reproduced.ToString("0.000", CultureInfo.InvariantCulture)
                    + " vs actual " + actual.ToString("0.000", CultureInfo.InvariantCulture)
                    + " drift " + Math.Abs(reproduced - actual).ToString("0.000", CultureInfo.InvariantCulture) + "s");

            var noIdentity = Send(descriptor, "GET", "/health", null, descriptor.Token, null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "identity header absent", noIdentity.Status == 403, noIdentity.Status + " " + noIdentity.Body);

            var emptyIdentity = Send(descriptor, "GET", "/health", string.Empty, descriptor.Token, null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "identity header empty", emptyIdentity.Status == 403, emptyIdentity.Status + " " + emptyIdentity.Body);

            var wrongIdentity = Send(descriptor, "GET", "/health", new string('0', 64), descriptor.Token, null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "identity mismatch", wrongIdentity.Status == 403, wrongIdentity.Status + " " + wrongIdentity.Body);

            var noToken = Send(descriptor, "GET", "/health", descriptor.Identity, null, null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "bearer absent", noToken.Status == 401, noToken.Status + " " + noToken.Body);

            var wrongToken = Send(descriptor, "GET", "/health", descriptor.Identity, new string('0', 32), null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "bearer wrong", wrongToken.Status == 401, wrongToken.Status + " " + wrongToken.Body);

            var wrongMethod = Send(descriptor, "GET", "/command", descriptor.Identity, descriptor.Token, null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "GET on /command", wrongMethod.Status == 405, wrongMethod.Status + " " + wrongMethod.Body);

            var wrongRoute = Send(descriptor, "GET", "/nope", descriptor.Identity, descriptor.Token, null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "unknown endpoint", wrongRoute.Status == 404, wrongRoute.Status + " " + wrongRoute.Body);

            // Declared length and chunked are separate paths: a chunked body declares no length up front, so
            // the declared-length check cannot be the only one.
            var oversize = Send(descriptor, "POST", "/command", descriptor.Identity, descriptor.Token,
                new byte[300 * 1024], false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "declared body over cap", oversize.Status == 413, oversize.Status + " " + oversize.Body);

            var chunkedOversize = Send(descriptor, "POST", "/command", descriptor.Identity, descriptor.Token,
                new byte[640 * 1024], true, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "chunked body over cap", chunkedOversize.Status == 413, chunkedOversize.Status + " " + chunkedOversize.Body);

            // Strict decoding: a replacement character would change the command that gets run.
            var badEncoding = Send(descriptor, "POST", "/command", descriptor.Identity, descriptor.Token,
                new byte[] { 0x70, 0x69, 0x6e, 0x67, 0xff, 0xfe }, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "invalid UTF-8 body", badEncoding.Status == 400, badEncoding.Status + " " + badEncoding.Body);

            var empty = Send(descriptor, "POST", "/command", descriptor.Identity, descriptor.Token,
                new byte[0], false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "empty body", empty.Status == 400, empty.Status + " " + empty.Body);

            // Durable verbs need caller identity. Without it two clients share one idempotency namespace and
            // a retry cannot be told from a second deliberate call, so it is refused rather than defaulted.
            var durableNoIdentity = Send(descriptor, "POST", "/command", descriptor.Identity, descriptor.Token,
                Encoding.UTF8.GetBytes("console-clear-logs"), false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "durable verb without caller identity",
                durableNoIdentity.Status == 400 && durableNoIdentity.Body.Contains("caller_identity_required"),
                durableNoIdentity.Status + " " + Outcome(durableNoIdentity.Body));

            // An editor-state verb is job-aware: it parks its job for the reload it causes and is finished
            // from the lifecycle callback, so it takes the ordinary durable path rather than being refused
            // outright. Sent WITHOUT caller identity, which settles the routing at the identity check - a
            // refused verb answers 409 before admission is reached, a durable one answers 400 here - and
            // proves it without entering play mode.
            var lifecycle = Send(descriptor, "POST", "/command", descriptor.Identity, descriptor.Token,
                Encoding.UTF8.GetBytes("play"), false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "an editor-state verb takes the durable path, not a refusal",
                lifecycle.Status == 400 && lifecycle.Body.Contains("caller_identity_required"),
                lifecycle.Status + " " + Outcome(lifecycle.Body));

            // The durable lifecycle: admit once, retry onto the same job, reuse the id for different work and
            // be refused, and collect the stored result afterwards.
            var session = "gate-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var admitted = SendDurable(descriptor, "console-clear-logs", session, "req-1");
            var jobId = JobIdOf(admitted.Body);
            ok &= Check(report, "durable admission", admitted.Status == 202 && jobId != null,
                admitted.Status + " " + Outcome(admitted.Body));

            var retried = SendDurable(descriptor, "console-clear-logs", session, "req-1");
            ok &= Check(report, "retry resolves onto the same job",
                (retried.Status == 202 || retried.Status == 200) && JobIdOf(retried.Body) == jobId,
                retried.Status + " " + Outcome(retried.Body) + " sameJob=" + (JobIdOf(retried.Body) == jobId));

            var conflicted = SendDurable(descriptor, "mcp-refresh-tools", session, "req-1");
            ok &= Check(report, "same request id for different work conflicts",
                conflicted.Status == 409 && conflicted.Body.Contains("idempotency_conflict"),
                conflicted.Status + " " + Outcome(conflicted.Body));

            var second = SendDurable(descriptor, "console-clear-logs", session, "req-2");
            ok &= Check(report, "a deliberate repeat is a new job",
                second.Status == 202 && JobIdOf(second.Body) != jobId,
                second.Status + " " + Outcome(second.Body));

            // Let the queued jobs drain before collecting, pumping because the drain shares this thread.
            var settle = DateTime.UtcNow.AddMilliseconds(PUMPED_TIMEOUT_MS);
            while (DateTime.UtcNow < settle)
            {
                ConduitHttpTransport.Drain();
                Thread.Sleep(5);
            }

            var jobStatus = Send(descriptor, "GET", "/job/status?id=" + jobId, descriptor.Identity, descriptor.Token, null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "job status returns the stored record",
                jobStatus.Status == 200 && jobStatus.Body.Contains("\"state\":\"completed\""),
                jobStatus.Status + " " + Outcome(jobStatus.Body));

            var traversal = Send(descriptor, "GET", "/job/status?id=../../secrets", descriptor.Identity, descriptor.Token, null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "malformed job id is a caller error, not a 404",
                traversal.Status == 400 && traversal.Body.Contains("invalid_job_id"),
                traversal.Status + " " + Outcome(traversal.Body));

            var absent = Send(descriptor, "GET", "/job/status?id=job-" + new string('0', 32), descriptor.Identity, descriptor.Token, null, false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "well-formed but absent job is 404",
                absent.Status == 404 && absent.Body.Contains("job_not_found"),
                absent.Status + " " + Outcome(absent.Body));

            var statusPost = Send(descriptor, "POST", "/job/status?id=x", descriptor.Identity, descriptor.Token,
                new byte[0], false, DIRECT_TIMEOUT_MS);
            ok &= Check(report, "job status refuses POST", statusPost.Status == 405, statusPost.Status + " " + Outcome(statusPost.Body));

            // A gate that leaves records behind changes its own preconditions, so its next result depends on
            // how many times it has been run before. That is not a gate, it is a moving target, and this one
            // was exactly that until now.
            var created = new List<string>();
            if (jobId != null) created.Add(jobId);
            var secondJob = JobIdOf(second.Body);
            if (secondJob != null && secondJob != jobId) created.Add(secondJob);

            string cleanupError;
            var cleaned = ConduitJobLedger.CleanupTerminalGateJobs(created, out cleanupError);
            ok &= Check(report, "gate removes the jobs it created", cleaned,
                created.Count + " id(s)" + (cleaned ? string.Empty : "  " + cleanupError));

            // Real verb through the real dispatcher, drained from here.
            var ping = SendCommandPumped(descriptor, "ping");
            ok &= Check(report, "immediate verb round-trip",
                ping.Status == 200 && ResultOf(ping.Body) == "pong",
                ping.Status + " " + Outcome(ping.Body) + " result=" + ResultOf(ping.Body));

            // Queue bound. Nothing is drained during the burst, so every accepted request stays queued and
            // the ones past the bound must be shed rather than piling up open listener contexts.
            var statuses = new int[BURST_SIZE];
            var threads = new List<Thread>();
            for (var i = 0; i < BURST_SIZE; i++)
            {
                var slot = i;
                var thread = new Thread(() =>
                {
                    var reply = Send(descriptor, "POST", "/command", descriptor.Identity, descriptor.Token,
                        Encoding.UTF8.GetBytes("ping"), false, BURST_TIMEOUT_MS);
                    statuses[slot] = reply.Status;
                });

                thread.IsBackground = true;
                threads.Add(thread);
                thread.Start();
            }

            foreach (var thread in threads) thread.Join(BURST_TIMEOUT_MS * 3);

            // Report the whole distribution, not just the count being asserted on. A bare "0 answered 429"
            // cannot distinguish a queue that failed to shed from requests that never arrived, and that
            // ambiguity is what made the first run of this check misread its own client library.
            var distribution = new SortedDictionary<int, int>();
            var shed = 0;
            foreach (var status in statuses)
            {
                if (status == 429) shed++;
                int seen;
                distribution[status] = distribution.TryGetValue(status, out seen) ? seen + 1 : 1;
            }

            var spread = new StringBuilder();
            foreach (var pair in distribution)
            {
                if (spread.Length > 0) spread.Append(", ");
                spread.Append(pair.Key == 0 ? "no-reply" : pair.Key.ToString()).Append(" x").Append(pair.Value);
            }

            ok &= Check(report, "queue bound sheds load", shed > 0,
                shed + " of " + BURST_SIZE + " answered 429  [" + spread + "]");

            // Drain whatever the burst left queued so the transport is idle again for the next caller.
            var flushDeadline = DateTime.UtcNow.AddMilliseconds(PUMPED_TIMEOUT_MS);
            while (DateTime.UtcNow < flushDeadline)
            {
                ConduitHttpTransport.Drain();
                Thread.Sleep(2);
            }

            var afterFlush = SendCommandPumped(descriptor, "ping");
            ok &= Check(report, "transport still serves after the burst",
                afterFlush.Status == 200 && ResultOf(afterFlush.Body) == "pong",
                afterFlush.Status + " " + Outcome(afterFlush.Body) + " result=" + ResultOf(afterFlush.Body));

            report.AppendLine();
            report.AppendLine(ok ? "PASS - production transport" : "FAIL - see the checks above");
            if (ok) Debug.Log("[TransportGate] " + report);
            else Debug.LogError("[TransportGate] " + report);
        }

        #endregion
    }
}
