/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Runs a VRChat build and reports its stages to the session that asked for it.
    /// </summary>
    /// <remarks>
    /// A world build runs for minutes to over an hour, crosses domain reloads, and outlives the tool call
    /// that started it, so the call answers immediately and every stage is reported afterwards through the
    /// requesting session's own inbox. Nothing polls: the SDK raises an event per stage and those events are
    /// the only thing that moves this forward.
    /// <para>
    /// Every notice carries the SDK's OWN message rather than prose written here. A stage report that
    /// paraphrases is a report that can be wrong while sounding confident, and the SDK's string is the only
    /// account of what actually happened.
    /// </para>
    /// </remarks>
    internal static partial class ConduitBridge
    {
        #region Constants

        /// <summary>Marker for a build in flight, surviving the domain reloads a build causes.</summary>
        private const string VRC_BUILD_SESSION_KEY = "ItsRenji.Conduit.VrcBuild.InFlight";

        #endregion

        #region Starting a build

        /// <summary>
        /// Starts one builder operation, answers immediately, and arranges for its stages to be reported.
        /// </summary>
        /// <param name="operation">Verb name, used in every notice so a reader knows what was asked for.</param>
        /// <param name="method">Builder method to invoke.</param>
        /// <param name="describesResult">What a successful result means, for the closing notice.</param>
        /// <param name="sessionId">Session the caller named, which is where every stage is reported.</param>
        /// <returns>What was started, or the reason nothing was.</returns>
        private static string StartVrcBuild(
            string operation, string method, string describesResult, string sessionId)
        {
            // Checked before anything is started. A build that runs for an hour and then has nowhere to
            // report is worse than one that never started, and the caller can fix this in one call.
            string reachError;
            if (!CanNotifyAgent(sessionId, out reachError))
            {
                return "Refusing to start " + operation + ": " + reachError;
            }

            object builder;
            string prepareError;
            if (!TryPrepareVrcBuilder(out builder, out prepareError))
            {
                return prepareError;
            }

            // Refused rather than queued. Two builds at once is not a thing the SDK supports, and starting a
            // second would report stages from one against the other's job.
            object state = ReadMemberValue(builder, builder.GetType(), "BuildState");
            if (state != null && string.Equals(Convert.ToString(state), "Building", StringComparison.Ordinal))
            {
                return "A build is already running. Its stages are being reported to whoever started it; "
                    + "wait for it to finish rather than starting a second.";
            }

            if (!string.IsNullOrEmpty(SessionState.GetString(VRC_BUILD_SESSION_KEY, string.Empty)))
            {
                return "Conduit is still reporting a previous build. If that build is genuinely over, the "
                    + "marker clears when its final stage arrives; vrc-build-state reads what the SDK "
                    + "currently reports.";
            }

            MethodInfo entry = builder.GetType().GetMethod(method, Type.EmptyTypes);
            if (entry == null)
            {
                return "This SDK's world builder has no " + method + "; Conduit will not guess at another "
                    + "entry point for a build.";
            }

            var context = _commandContext;
            string jobId = context == null ? string.Empty : (context.JobId ?? string.Empty);

            // Recorded BEFORE the build is asked for. A build that starts with nothing recorded is a build
            // whose stages arrive with no one to attribute them to.
            SessionState.SetString(VRC_BUILD_SESSION_KEY, new JObject
            {
                ["operation"] = operation,
                ["sessionId"] = sessionId,
                ["jobId"] = jobId,
                ["startedUtc"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["describesResult"] = describesResult,
                ["lastStage"] = string.Empty,
            }.ToString(Formatting.None));

            string subscribeError;
            if (!TrySubscribeVrcBuildEvents(builder, out subscribeError))
            {
                SessionState.EraseString(VRC_BUILD_SESSION_KEY);
                return "Refusing to start: " + subscribeError + " Without those events nothing would report "
                    + "what the build did, and a build whose outcome is never reported is worse than one "
                    + "that never started.";
            }

            try
            {
                entry.Invoke(builder, null);
            }
            catch (Exception ex)
            {
                UnsubscribeVrcBuildEvents(builder);
                SessionState.EraseString(VRC_BUILD_SESSION_KEY);
                return "The SDK refused to start " + operation + ": " + Unwrap(ex);
            }

            var report = new StringBuilder();
            report.Append(operation).Append(" started. This call is NOT the result: a world build runs for ")
                .Append("minutes to over an hour and crosses domain reloads, so it cannot be answered here.");

            report.Append(Environment.NewLine).Append("Stages are reported to session ").Append(sessionId)
                .Append('.');
            report.Append(Environment.NewLine).Append("Stages are reported as the SDK raises them, each ")
                .Append("carrying the SDK's own message. vrc-build-state reads the current state on demand.");

            return report.ToString();
        }

        /// <summary>
        /// Resolves the world builder, surfacing the Control Panel when it is closed because the SDK will not
        /// hand over a builder without it.
        /// </summary>
        /// <param name="builder">Receives the builder.</param>
        /// <param name="error">Receives why no builder is available.</param>
        /// <returns>True when a builder was obtained.</returns>
        private static bool TryPrepareVrcBuilder(out object builder, out string error)
        {
            builder = null;

            Type panelType = FindLoadedType("VRCSdkControlPanel");
            if (panelType == null)
            {
                error = "The VRChat SDK Control Panel type was not found; the Worlds SDK is not loaded.";
                return false;
            }

            UnityEngine.Object panel = ReadStaticUnityObject(panelType, "window");
            if (panel == null)
            {
                EditorApplication.ExecuteMenuItem(VRC_SHOW_PANEL_MENU);
                panel = ReadStaticUnityObject(panelType, "window");
                if (panel == null)
                {
                    error = "The SDK Control Panel was closed and is opening now; its remote config was still "
                        + "initialising, so no builder exists yet. Run this again.";
                    return false;
                }
            }

            return TryGetVrcWorldBuilder(panelType, out builder, out error);
        }

        #endregion

        #region Stage reporting

        /// <summary>
        /// Subscribes to every build and upload stage event the SDK publishes as a plain string, plus the
        /// two that carry no payload and the upload progress pair.
        /// </summary>
        /// <param name="builder">Builder to subscribe to.</param>
        /// <param name="error">Receives which events could not be reached.</param>
        /// <returns>True when the outcome events were subscribed.</returns>
        private static bool TrySubscribeVrcBuildEvents(object builder, out string error)
        {
            error = null;

            // Success and error are the two that MUST be reachable. Progress is a courtesy; an outcome is the
            // whole point, so a missing outcome event is a refusal rather than a degraded mode.
            if (!TryHookVrcEvent(builder, "OnSdkBuildSuccess", "VrcBuildSucceeded")
                || !TryHookVrcEvent(builder, "OnSdkBuildError", "VrcBuildFailed"))
            {
                error = "this SDK does not publish OnSdkBuildSuccess and OnSdkBuildError as this build "
                    + "expects them.";
                return false;
            }

            // Best effort, and their absence is reported in the notices rather than failing the build.
            TryHookVrcEvent(builder, "OnSdkBuildProgress", "VrcBuildProgressed");
            TryHookVrcEvent(builder, "OnSdkUploadSuccess", "VrcUploadSucceeded");
            TryHookVrcEvent(builder, "OnSdkUploadError", "VrcUploadFailed");
            return true;
        }

        /// <summary>Attaches one named handler to one named event, by matching the event's own delegate type.</summary>
        /// <param name="builder">Object publishing the event.</param>
        /// <param name="eventName">Event to attach to.</param>
        /// <param name="handlerName">Private static handler in this class to attach.</param>
        /// <returns>True when the handler was attached.</returns>
        private static bool TryHookVrcEvent(object builder, string eventName, string handlerName)
        {
            try
            {
                EventInfo info = builder.GetType().GetEvent(eventName);
                if (info == null) return false;

                MethodInfo handler = typeof(ConduitBridge).GetMethod(
                    handlerName, BindingFlags.Static | BindingFlags.NonPublic);
                if (handler == null) return false;

                Delegate typed = Delegate.CreateDelegate(info.EventHandlerType, handler, false);
                if (typed == null) return false;

                info.RemoveEventHandler(builder, typed);
                info.AddEventHandler(builder, typed);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Detaches the handlers, so a finished build stops reporting against a cleared marker.</summary>
        /// <param name="builder">Object publishing the events.</param>
        private static void UnsubscribeVrcBuildEvents(object builder)
        {
            foreach (string pair in new[]
            {
                "OnSdkBuildSuccess|VrcBuildSucceeded", "OnSdkBuildError|VrcBuildFailed",
                "OnSdkBuildProgress|VrcBuildProgressed", "OnSdkUploadSuccess|VrcUploadSucceeded",
                "OnSdkUploadError|VrcUploadFailed",
            })
            {
                string[] parts = pair.Split('|');
                try
                {
                    EventInfo info = builder.GetType().GetEvent(parts[0]);
                    MethodInfo handler = typeof(ConduitBridge).GetMethod(
                        parts[1], BindingFlags.Static | BindingFlags.NonPublic);
                    if (info == null || handler == null) continue;

                    Delegate typed = Delegate.CreateDelegate(info.EventHandlerType, handler, false);
                    if (typed != null) info.RemoveEventHandler(builder, typed);
                }
                catch (Exception)
                {
                    // A handler that cannot be detached is left attached; it reads a cleared marker and
                    // reports nothing, which is why clearing the marker is what ends a build's reporting.
                }
            }
        }

        /// <summary>Reports one build stage that is neither an outcome nor a failure.</summary>
        /// <param name="sender">Builder raising the event.</param>
        /// <param name="stage">The SDK's own stage message.</param>
        private static void VrcBuildProgressed(object sender, string stage)
        {
            JObject flight = ReadVrcFlight();
            if (flight == null) return;

            // Only a CHANGED stage is reported. The SDK repeats a stage string while it works, and a notice
            // per repeat would bury the transitions that matter in noise the reader cannot skip.
            if (string.Equals((string)flight["lastStage"], stage ?? string.Empty, StringComparison.Ordinal)) return;

            flight["lastStage"] = stage ?? string.Empty;
            SessionState.SetString(VRC_BUILD_SESSION_KEY, flight.ToString(Formatting.None));

            SendVrcNotice(flight, "build stage", "Stage: " + OneLine(stage) + Environment.NewLine
                + "This is progress, not an outcome. Nothing has been produced or uploaded yet.");
        }

        /// <summary>Reports a finished build and completes its job.</summary>
        /// <param name="sender">Builder raising the event.</param>
        /// <param name="message">The SDK's own success message.</param>
        private static void VrcBuildSucceeded(object sender, string message)
        {
            JObject flight = ReadVrcFlight();
            if (flight == null) return;

            var body = new StringBuilder();
            body.Append("PASSED: ").Append((string)flight["operation"]).Append(" finished.");
            body.Append(Environment.NewLine).Append("The SDK reported: ").Append(OneLine(message));
            body.Append(Environment.NewLine).Append((string)flight["describesResult"]);
            body.Append(Environment.NewLine).Append("Nothing was uploaded by this operation unless the line ")
                .Append("above says it was.");

            FinishVrcBuild(flight, "build passed", body.ToString(), true);
        }

        /// <summary>Reports a failed build and fails its job with the SDK's reason.</summary>
        /// <param name="sender">Builder raising the event.</param>
        /// <param name="message">The SDK's own error message.</param>
        private static void VrcBuildFailed(object sender, string message)
        {
            JObject flight = ReadVrcFlight();
            if (flight == null) return;

            var body = new StringBuilder();
            body.Append("FAILED: ").Append((string)flight["operation"]).Append(" did not finish.");
            body.Append(Environment.NewLine).Append("The SDK reported: ").Append(OneLine(message));

            var stage = (string)flight["lastStage"] ?? string.Empty;
            body.Append(Environment.NewLine).Append(stage.Length == 0
                ? "It failed before reporting any stage, so the fault is in validation or setup rather than "
                    + "in building the bundle."
                : "The last stage it reached was: " + stage);

            body.Append(Environment.NewLine).Append("No bundle was produced. Run vrc-validate for the SDK's ")
                .Append("own account of what blocks this scene.");

            FinishVrcBuild(flight, "build failed", body.ToString(), false);
        }

        /// <summary>Reports a finished upload.</summary>
        /// <param name="sender">Builder raising the event.</param>
        /// <param name="message">The SDK's own success message.</param>
        private static void VrcUploadSucceeded(object sender, string message)
        {
            JObject flight = ReadVrcFlight();
            if (flight == null) return;

            SendVrcNotice(flight, "upload passed", "PASSED: the upload finished." + Environment.NewLine
                + "The SDK reported: " + OneLine(message) + Environment.NewLine
                + "An instance already running still carries the OLD build; only instances created after "
                + "this upload have the new one.");
        }

        /// <summary>Reports a failed upload.</summary>
        /// <param name="sender">Builder raising the event.</param>
        /// <param name="message">The SDK's own error message.</param>
        private static void VrcUploadFailed(object sender, string message)
        {
            JObject flight = ReadVrcFlight();
            if (flight == null) return;

            SendVrcNotice(flight, "upload failed", "FAILED: the upload did not finish." + Environment.NewLine
                + "The SDK reported: " + OneLine(message) + Environment.NewLine
                + "The bundle may already have been built, so check before rebuilding from scratch.");
        }

        /// <summary>Sends the closing notice, terminalises the job, and clears the marker.</summary>
        /// <param name="flight">The in-flight record.</param>
        /// <param name="subject">Notice subject.</param>
        /// <param name="body">Notice body.</param>
        /// <param name="passed">Whether the operation succeeded.</param>
        private static void FinishVrcBuild(JObject flight, string subject, string body, bool passed)
        {
            SessionState.EraseString(VRC_BUILD_SESSION_KEY);

            // The job is terminalised BEFORE the notice is attempted. The record is the durable truth and a
            // delivery failure must not be able to lose the outcome as well.
            var jobId = (string)flight["jobId"] ?? string.Empty;
            if (jobId.Length > 0)
            {
                string ledgerError;
                if (passed) ConduitJobLedger.Complete(jobId, "vrc_build_passed", body, out ledgerError);
                else ConduitJobLedger.Fail(jobId, "vrc_build_failed", body, out ledgerError);
            }

            SendVrcNotice(flight, subject, body);
        }

        /// <summary>Sends one notice to the session that asked for the build.</summary>
        /// <param name="flight">The in-flight record naming the requesting client.</param>
        /// <param name="subject">Notice subject.</param>
        /// <param name="body">Notice body.</param>
        private static void SendVrcNotice(JObject flight, string subject, string body)
        {
            string detail;
            string state = NotifyAgent((string)flight["sessionId"] ?? string.Empty, subject, body, out detail);

            // A delivery that did not happen is recorded where a human will see it, because the alternative
            // is a build whose stages went nowhere and left no trace of having tried.
            if (state != ConduitAgentInbox.STATE_ACCEPTED)
            {
                UnityEngine.Debug.LogWarning(
                    "[Conduit] Could not report a build stage to the requesting session (" + state + "): "
                    + detail);
            }
        }

        /// <summary>Reads the in-flight record, or null when no build is being reported.</summary>
        /// <returns>The record, or null.</returns>
        private static JObject ReadVrcFlight()
        {
            var stored = SessionState.GetString(VRC_BUILD_SESSION_KEY, string.Empty);
            if (string.IsNullOrEmpty(stored)) return null;

            try { return JObject.Parse(stored); }
            catch (Exception) { return null; }
        }

        #endregion

        #region State

        /// <summary>Reads what the SDK currently reports, for a caller checking in rather than waiting.</summary>
        /// <returns>Build and upload state, the last stage seen, and what is being reported to whom.</returns>
        private static string VrcBuildStateCommand()
        {
            object builder;
            string error;
            if (!TryPrepareVrcBuilder(out builder, out error))
            {
                return error;
            }

            var report = new StringBuilder();
            report.Append("buildState: ")
                .Append(Convert.ToString(ReadMemberValue(builder, builder.GetType(), "BuildState")));
            report.Append(Environment.NewLine).Append("uploadState: ")
                .Append(Convert.ToString(ReadMemberValue(builder, builder.GetType(), "UploadState")));

            JObject flight = ReadVrcFlight();
            if (flight == null)
            {
                report.Append(Environment.NewLine)
                    .Append("Conduit is reporting no build. A build started from the panel by hand raises the ")
                    .Append("same SDK events, but Conduit only reports one it was asked for, because only ")
                    .Append("then does it know which session to tell.");
                return report.ToString();
            }

            report.Append(Environment.NewLine).Append("reporting: ").Append((string)flight["operation"])
                .Append(" started ").Append((string)flight["startedUtc"])
                .Append(" to session ").Append((string)flight["sessionId"]);

            var stage = (string)flight["lastStage"] ?? string.Empty;
            report.Append(Environment.NewLine).Append("lastStage: ")
                .Append(stage.Length == 0 ? "(none reported yet)" : stage);

            return report.ToString();
        }

        #endregion

        /// <summary>
        /// Routes the build-running verbs. Returns null when the verb belongs to another area.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, unused while these verbs take no arguments.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchVrcBuildRun(string verb, string command, string[] parts)
        {
            if (verb == "vrc-build")
            {
                return StartVrcBuild("vrc-build", "Build",
                    "A bundle was built and is the last build on disk, which vrc-test-last can launch.",
                    GetCommandRemainder(command, parts[0]));
            }

            if (verb == "vrc-build-test")
            {
                return StartVrcBuild("vrc-build-test", "BuildAndTest",
                    "A bundle was built and a test client was launched or reloaded onto it.",
                    GetCommandRemainder(command, parts[0]));
            }

            if (verb == "vrc-test-last")
            {
                return StartVrcBuild("vrc-test-last", "TestLastBuild",
                    "The last build on disk was reopened in a test client; nothing was rebuilt.",
                    GetCommandRemainder(command, parts[0]));
            }

            if (verb == "vrc-build-state")
            {
                return VrcBuildStateCommand();
            }

            return null;
        }
    }
}
