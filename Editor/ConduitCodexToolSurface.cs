/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// The tool surface one harness receives, which differs from every other harness's because that
    /// harness already tells the bridge which conversation is calling.
    /// </summary>
    /// <remarks>
    /// A verb that reports back when its work outlives its own call has to be told which conversation to
    /// report to, and whether the model can answer that question is a property of the HARNESS rather than
    /// of the verb. Most harnesses expose their conversation id to the model, so it passes it. This one
    /// does not - it puts the id in the metadata envelope of the request instead, where the model never
    /// sees it but the bridge always does.
    /// <para>
    /// So this harness is served the same verbs WITHOUT that parameter, and the bridge fills it in from
    /// the envelope. The alternative was one signature everywhere with the parameter optional, which is
    /// worse in both directions: it asks a model for something it cannot obtain, and an optional return
    /// address is silently absent exactly when a caller forgot it, producing an hour-long build that
    /// reports to nobody. A published signature should be answerable by whoever receives it.
    /// </para>
    /// <para>
    /// This mirrors the per-caller tool already served elsewhere in the catalogue: advertising a control
    /// that can only refuse is worse than not advertising it.
    /// </para>
    /// </remarks>
    internal static class ConduitCodexToolSurface
    {
        #region Constants

        /// <summary>Client display name whose surface this is, as the window's cards spell it.</summary>
        private const string CLIENT = "Codex";

        /// <summary>Parameter this harness never has to supply.</summary>
        private const string SESSION_PROPERTY = "sessionId";

        /// <summary>
        /// Verbs whose session parameter is filled from the request instead of by the caller.
        /// </summary>
        /// <remarks>
        /// Both spellings of each are listed because the catalogue publishes a hyphenated verb and its
        /// underscored alias, and a client may call either. Matching one form would adapt the schema a
        /// client read while leaving the call it actually makes unfilled.
        /// </remarks>
        private static readonly string[] AutoFilledTools =
        {
            "vrc-build", "vrc_build",
            "vrc-build-test", "vrc_build_test",
            "vrc-test-last", "vrc_test_last",
        };

        /// <summary>
        /// Envelope keys that may carry this harness's conversation id, in the order they are trusted.
        /// </summary>
        /// <remarks>
        /// Several spellings because the harness has used more than one and a build in the field may send
        /// any of them. Reading only the current spelling is how this silently stops working after an
        /// update, with the symptom appearing as a build that reports to nobody.
        /// </remarks>
        private static readonly string[] ConversationKeys =
        {
            "openai/threadId", "openai/thread_id", "codexThreadId", "codex_thread_id",
            "threadId", "thread_id",
        };

        /// <summary>Envelope key holding a nested blob that also carries the conversation id.</summary>
        private const string TURN_METADATA_KEY = "x-codex-turn-metadata";

        #endregion

        #region Caller

        /// <summary>Whether this request's bearer identified the harness this surface belongs to.</summary>
        /// <param name="caller">Client display name resolved from the request bearer.</param>
        /// <returns>True for that harness only.</returns>
        internal static bool IsCaller(string caller)
        {
            return string.Equals(caller, CLIENT, StringComparison.Ordinal);
        }

        #endregion

        #region Conversation id

        /// <summary>
        /// Reads the calling conversation's id out of a request's metadata envelope.
        /// </summary>
        /// <param name="meta">The request's metadata envelope, or null.</param>
        /// <returns>The conversation id, or null when the envelope carries none.</returns>
        internal static string ConversationFrom(JObject meta)
        {
            if (meta == null) return null;

            foreach (string key in ConversationKeys)
            {
                var value = (string)meta[key];
                if (!string.IsNullOrEmpty(value) && value.Trim().Length > 0) return value.Trim();
            }

            // The nested blob arrives as either an object or a string holding one, depending on the build,
            // so both are read rather than assuming the shape this machine happens to receive.
            JToken turn = meta[TURN_METADATA_KEY];
            if (turn != null)
            {
                JObject parsed = turn as JObject;
                if (parsed == null && turn.Type == JTokenType.String)
                {
                    try { parsed = JObject.Parse((string)turn); }
                    catch (Exception) { parsed = null; }
                }

                if (parsed != null)
                {
                    var nested = (string)parsed["thread_id"] ?? (string)parsed["threadId"];
                    if (!string.IsNullOrEmpty(nested) && nested.Trim().Length > 0) return nested.Trim();
                }
            }

            var thread = meta["thread"] as JObject;
            if (thread != null)
            {
                var threadId = (string)thread["id"];
                if (!string.IsNullOrEmpty(threadId) && threadId.Trim().Length > 0) return threadId.Trim();
            }

            return null;
        }

        #endregion

        #region Catalogue

        /// <summary>Whether this verb's session parameter is filled from the request for this harness.</summary>
        /// <param name="toolName">Published tool name, either spelling.</param>
        /// <returns>True when the parameter is supplied by the bridge.</returns>
        internal static bool IsAutoFilled(string toolName)
        {
            if (string.IsNullOrEmpty(toolName)) return false;
            foreach (string name in AutoFilledTools)
            {
                if (string.Equals(toolName, name, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// Rewrites the published catalogue for this harness, removing the parameter it never supplies.
        /// </summary>
        /// <remarks>
        /// Operates on the caller's own snapshot. The published catalogue is immutable by design so two
        /// clients cannot be handed different contracts by accident - this hands one client a different
        /// contract ON PURPOSE, and it must not reach the shared copy to do it.
        /// </remarks>
        /// <param name="tools">This response's tool array, modified in place.</param>
        internal static void Adapt(JArray tools)
        {
            if (tools == null) return;

            foreach (JToken tool in tools)
            {
                var definition = tool as JObject;
                if (definition == null) continue;
                if (!IsAutoFilled((string)definition["name"])) continue;

                var schema = definition["inputSchema"] as JObject;
                if (schema != null)
                {
                    var properties = schema["properties"] as JObject;
                    if (properties != null) properties.Remove(SESSION_PROPERTY);

                    // Removed from the required list as well. A schema requiring a property it no longer
                    // publishes is one a client refuses every call against, which is the failure this
                    // rewrite exists to prevent rather than cause.
                    var required = schema["required"] as JArray;
                    if (required != null)
                    {
                        for (int index = required.Count - 1; index >= 0; index--)
                        {
                            if ((string)required[index] != SESSION_PROPERTY) continue;
                            required.RemoveAt(index);
                        }

                        if (required.Count == 0) schema.Remove("required");
                    }
                }

                // The description is corrected too, because the published text names the parameter and
                // tells the reader to pass its own session. Left alone it would instruct this harness's
                // model to supply something its schema no longer accepts and it could not obtain anyway.
                definition["description"] = Describe((string)definition["description"]);
            }
        }

        /// <summary>Replaces the session-parameter sentence with what is true for this harness.</summary>
        /// <param name="published">The catalogue's own description.</param>
        /// <returns>The description this harness receives.</returns>
        private static string Describe(string published)
        {
            return (published ?? string.Empty)
                + " This harness does not take a session parameter: it identifies the calling conversation "
                + "on every request, so the bridge already knows where to report and fills it in. The "
                + "result is delivered into this conversation when the work finishes - do not ask for it.";
        }

        #endregion
    }
}
