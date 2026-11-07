/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// VRChat SDK build-pipeline commands, reported rather than driven: the validations the Build panel
    /// runs are collected and returned so a caller can answer "will this upload" without a human reading
    /// the window aloud.
    /// <para>
    /// Every SDK member is reached through a resolved type rather than a compile-time reference, because
    /// Conduit ships into projects that have no Worlds SDK and this file still has to compile there. The
    /// verb itself is gated on a Worlds project by <c>WorldsOnlyVerbs</c>.
    /// </para>
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Constants

        /// <summary>The SDK's own menu path for surfacing the Control Panel.</summary>
        private const string VRC_SHOW_PANEL_MENU = "VRChat SDK/Show Control Panel";

        /// <summary>Report channels in the severity order the Build panel presents them.</summary>
        private static readonly string[] VrcAlertChannels =
        {
            "GUIErrors", "GUIWarnings", "GUIStats", "GUIInfos", "GUILinks",
        };

        /// <summary>Channel whose presence blocks a build. The rest are advisory.</summary>
        private const string VRC_BLOCKING_CHANNEL = "GUIErrors";

        /// <summary>The SDK type mirroring the Build panel's world fields across a domain reload.</summary>
        private const string VRC_WORLD_STATE_TYPE = "VRC.SDK3.Editor.WorldBuilderSessionState";

        /// <summary>Prefix the SDK puts on an author's own tag, lowercased with spaces as underscores.</summary>
        private const string VRC_AUTHOR_TAG_PREFIX = "author_tag_";

        /// <summary>Tag that opens the world to the in-client debug overlay.</summary>
        private const string VRC_DEBUG_TAG = "debug_allowed";

        /// <summary>Tag present once moderation has reviewed the warnings, which locks them in the panel.</summary>
        private const string VRC_REVIEWED_TAG = "admin_content_reviewed";

        /// <summary>
        /// The five content warnings, tag first and the label the panel shows second. A warning is an ordinary
        /// entry in the same Tags list as an author tag, which is why setting one kind has to preserve the
        /// others rather than replacing the list.
        /// </summary>
        private static readonly string[,] VrcContentWarnings =
        {
            { "content_sex", "Sexually Suggestive" },
            { "content_adult", "Adult Language and Themes" },
            { "content_violence", "Graphic Violence" },
            { "content_gore", "Excessive Gore" },
            { "content_horror", "Extreme Horror" },
        };

        #endregion

        #region Validate

        /// <summary>
        /// Runs the SDK's scene validations and reports every alert with its subject and whether a fix is
        /// offered. Read-only: no fix callback is ever invoked, and the pass itself mutates nothing.
        /// </summary>
        /// <returns>A severity-ordered report, or the reason validation could not run.</returns>
        private static string VrcValidateCommand()
        {
            Type panelType = FindLoadedType("VRCSdkControlPanel");
            if (panelType == null)
            {
                return "The VRChat SDK Control Panel type was not found; the Worlds SDK is not loaded.";
            }

            UnityEngine.Object panel = ReadStaticUnityObject(panelType, "window");
            if (panel == null)
            {
                // Surfaced rather than refused, so a closed panel costs the caller nothing. The SDK's own
                // menu item opens it synchronously once its remote config is initialised, and re-invokes
                // itself from the config callback when it is not - which is the one case that cannot answer
                // in this call, because the window genuinely does not exist yet.
                EditorApplication.ExecuteMenuItem(VRC_SHOW_PANEL_MENU);
                panel = ReadStaticUnityObject(panelType, "window");
                if (panel == null)
                {
                    return "The SDK Control Panel was closed and is opening now; its remote config was still "
                        + "initialising, so no validation result exists yet. Run vrc-validate again.";
                }
            }

            object builder;
            string builderError;
            if (!TryGetVrcWorldBuilder(panelType, out builder, out builderError))
            {
                return builderError;
            }

            Type builderType = builder.GetType();

            // IsValidBuilder is a precondition and not a courtesy: its FindScenes call is what fills the
            // scene list that CreateValidationsGUI then indexes before it would fill it itself, so calling
            // the validations first throws inside the SDK. Its message is also the only prose describing
            // why a scene cannot be validated, so it is what a caller should be told.
            MethodInfo isValid = builderType.GetMethod("IsValidBuilder");
            if (isValid == null)
            {
                return "This SDK's world builder has no IsValidBuilder; Conduit will not guess at its "
                    + "validation entry point.";
            }

            var validArgs = new object[] { null };
            object canValidate;
            try
            {
                canValidate = isValid.Invoke(builder, validArgs);
            }
            catch (Exception ex)
            {
                return "The SDK refused to report whether this scene can be validated: " + Unwrap(ex);
            }

            if (!(canValidate is bool && (bool)canValidate))
            {
                string why = validArgs[0] as string;
                return "This scene cannot be validated: "
                    + (string.IsNullOrEmpty(why) ? "the SDK gave no reason." : why);
            }

            MethodInfo validations = builderType.GetMethod("CreateValidationsGUI");
            if (validations == null)
            {
                return "This SDK's world builder has no CreateValidationsGUI; Conduit will not guess at its "
                    + "validation entry point.";
            }

            try
            {
                // The element is a throwaway container for GUI nobody reads; the checks run as its side
                // effect. It is never attached to a window, so the fix buttons it builds cannot be pressed.
                validations.Invoke(builder, new object[] { new VisualElement() });
            }
            catch (Exception ex)
            {
                return "The SDK's validation pass failed: " + Unwrap(ex);
            }

            return FormatVrcAlerts(panelType, panel);
        }

        /// <summary>
        /// Takes the world builder from the SDK's own static accessor, so the instance is the one the panel
        /// itself uses rather than a parallel copy that could report something the panel does not show.
        /// </summary>
        /// <param name="panelType">Resolved Control Panel type.</param>
        /// <param name="builder">Receives the world builder.</param>
        /// <param name="error">Receives a refusal naming what was missing.</param>
        /// <returns>True when a world builder was obtained.</returns>
        private static bool TryGetVrcWorldBuilder(Type panelType, out object builder, out string error)
        {
            builder = null;
            error = null;

            Type worldApi = FindLoadedType("VRC.SDK3.Editor.IVRCSdkWorldBuilderApi");
            if (worldApi == null)
            {
                error = "The Worlds builder interface was not found; this project has no Worlds SDK.";
                return false;
            }

            MethodInfo generic = null;
            foreach (MethodInfo candidate in panelType.GetMethods(BindingFlags.Static | BindingFlags.Public))
            {
                if (candidate.Name == "TryGetBuilder" && candidate.IsGenericMethodDefinition)
                {
                    generic = candidate;
                }
            }

            if (generic == null)
            {
                error = "This SDK has no TryGetBuilder accessor; Conduit will not wire a builder by hand.";
                return false;
            }

            var args = new object[] { null };
            try
            {
                if (!(bool)generic.MakeGenericMethod(worldApi).Invoke(null, args) || args[0] == null)
                {
                    error = "The SDK returned no world builder for this project.";
                    return false;
                }
            }
            catch (Exception ex)
            {
                error = "The SDK refused to hand over its world builder: " + Unwrap(ex);
                return false;
            }

            builder = args[0];
            return true;
        }

        /// <summary>
        /// Renders the collected alerts, blocking channel first, with the verdict on the opening line.
        /// </summary>
        /// <param name="panelType">Resolved Control Panel type.</param>
        /// <param name="panel">The live panel instance holding the reports.</param>
        /// <returns>The rendered report.</returns>
        private static string FormatVrcAlerts(Type panelType, object panel)
        {
            var body = new StringBuilder();
            int blocking = 0;
            int advisory = 0;
            int fixesOffered = 0;
            int unreadable = 0;

            foreach (string channel in VrcAlertChannels)
            {
                // The five report dictionaries are private to the panel and there is no public enumerator:
                // the one public read answers for a single subject the caller must already know, and only
                // for errors. Confining the reflection to this loop keeps an SDK rename a one-place fix.
                FieldInfo field = panelType.GetField(
                    channel, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                {
                    unreadable++;
                    body.Append(Environment.NewLine).Append(channel).Append(": NOT READABLE in this SDK version");
                    continue;
                }

                var report = field.GetValue(panel) as IDictionary;
                if (report == null || report.Count == 0)
                {
                    continue;
                }

                bool blocks = string.Equals(channel, VRC_BLOCKING_CHANNEL, StringComparison.Ordinal);
                foreach (DictionaryEntry entry in report)
                {
                    var issues = entry.Value as IList;
                    if (issues == null) continue;

                    string subject = DescribeVrcSubject(entry.Key as UnityEngine.Object);
                    foreach (object issue in issues)
                    {
                        Type issueType = issue.GetType();
                        bool hasFix = ReadMemberValue(issue, issueType, "fixThisIssue") != null;
                        if (hasFix) fixesOffered++;
                        if (blocks) blocking++; else advisory++;

                        body.Append(Environment.NewLine)
                            .Append(VrcSeverityLabel(channel)).Append("  ").Append(subject)
                            .Append(Environment.NewLine).Append("    ")
                            .Append(OneLine(Convert.ToString(ReadMemberValue(issue, issueType, "issueText"))));

                        if (hasFix)
                        {
                            body.Append(Environment.NewLine)
                                .Append("    offers an Auto Fix, which was NOT applied.");
                        }
                    }
                }
            }

            var head = new StringBuilder();
            head.Append(blocking == 0
                ? "No build-blocking errors: the SDK would let this scene build."
                : blocking.ToString() + " error(s) BLOCK a build of this scene.");
            head.Append(" ").Append(advisory.ToString()).Append(" advisory alert(s).");

            if (fixesOffered > 0)
            {
                // Stated rather than implied. A fix is an arbitrary callback written by whoever authored
                // the check, so what any given one does is only knowable by reading that check.
                head.Append(" ").Append(fixesOffered.ToString())
                    .Append(" alert(s) offer an Auto Fix; none were applied, and this verb never applies one.");
            }

            if (unreadable > 0)
            {
                head.Append(" ").Append(unreadable.ToString())
                    .Append(" report channel(s) could not be read in this SDK version, so this report is incomplete.");
            }

            if (blocking == 0 && advisory == 0 && unreadable == 0)
            {
                return head.ToString() + " The SDK reported no alerts at all.";
            }

            return head.ToString() + Environment.NewLine + body.ToString().TrimStart();
        }

        /// <summary>Maps a report channel to the severity word the panel shows for it.</summary>
        /// <param name="channel">Report dictionary name.</param>
        /// <returns>A short severity label.</returns>
        private static string VrcSeverityLabel(string channel)
        {
            switch (channel)
            {
                case "GUIErrors":   return "ERROR  ";
                case "GUIWarnings": return "warning";
                case "GUIStats":    return "stat   ";
                case "GUIInfos":    return "info   ";
                case "GUILinks":    return "link   ";
                default:            return channel;
            }
        }

        /// <summary>
        /// Names the object an alert is filed against, with its instance id, because two scene objects
        /// routinely share a name and a caller needs to reach the right one.
        /// </summary>
        /// <param name="subject">Alert subject, or null when the alert belongs to the panel itself.</param>
        /// <returns>A one-line description.</returns>
        private static string DescribeVrcSubject(UnityEngine.Object subject)
        {
            if (subject == null) return "(the SDK panel itself)";

            return subject.name + " [" + subject.GetType().Name + " #"
                + subject.GetInstanceID().ToString() + "]";
        }

        #endregion

        #region World metadata

        /// <summary>
        /// Reports the world fields an upload sends, split by the kind of tag each one really is.
        /// </summary>
        /// <returns>The current metadata, or the reason it could not be read.</returns>
        private static string VrcWorldInfoCommand()
        {
            Type state = FindLoadedType(VRC_WORLD_STATE_TYPE);
            if (state == null)
            {
                return "The Worlds SDK's world-field store was not found; this project has no Worlds SDK.";
            }

            List<string> tags = VrcReadTags(state);
            var report = new StringBuilder();

            report.Append("name: ").Append(VrcDescribe(ReadStaticMember(state, "WorldName") as string));
            report.Append(Environment.NewLine).Append("description: ")
                .Append(VrcDescribe(ReadStaticMember(state, "WorldDesc") as string));
            report.Append(Environment.NewLine).Append("capacity: ")
                .Append(Convert.ToString(ReadStaticMember(state, "WorldCapacity")));
            report.Append(Environment.NewLine).Append("recommendedCapacity: ")
                .Append(Convert.ToString(ReadStaticMember(state, "WorldRecommendedCapacity")));
            report.Append(Environment.NewLine).Append("thumbnailPath: ")
                .Append(VrcDescribe(ReadStaticMember(state, "WorldThumbPath") as string));

            var warnings = new List<string>();
            var authored = new List<string>();
            var other = new List<string>();
            foreach (string tag in tags)
            {
                if (VrcIsContentWarning(tag)) warnings.Add(tag + " (" + VrcWarningLabel(tag) + ")");
                else if (IsVrcAuthorTag(tag)) authored.Add(tag.Substring(VRC_AUTHOR_TAG_PREFIX.Length));

                // The debug tag has its own line and the reviewed tag its own sentence, so neither belongs in
                // the leftover bucket as well: reported in both places, a reader counting tags doubles them.
                else if (tag != VRC_DEBUG_TAG && tag != VRC_REVIEWED_TAG) other.Add(tag);
            }

            report.Append(Environment.NewLine).Append("contentWarnings: ")
                .Append(warnings.Count == 0 ? "(none marked)" : string.Join(", ", warnings.ToArray()));
            report.Append(Environment.NewLine).Append("tags: ")
                .Append(authored.Count == 0 ? "(none)" : string.Join(", ", authored.ToArray()));
            report.Append(Environment.NewLine).Append("debugAllowed: ")
                .Append(tags.Contains(VRC_DEBUG_TAG) ? "true" : "false");

            if (other.Count > 0)
            {
                report.Append(Environment.NewLine).Append("otherTags: ").Append(string.Join(", ", other.ToArray()));
            }

            var platforms = ReadStaticMember(state, "WorldPlatforms") as IList;
            var names = new List<string>();
            if (platforms != null)
            {
                foreach (object platform in platforms) names.Add(Convert.ToString(platform));
            }

            report.Append(Environment.NewLine).Append("platforms: ")
                .Append(names.Count == 0 ? "(none selected; the panel falls back to the active build target)"
                    : string.Join(", ", names.ToArray()));

            if (tags.Contains(VRC_REVIEWED_TAG))
            {
                report.Append(Environment.NewLine)
                    .Append("Moderation has reviewed this world's warnings, so the panel locks them.");
            }

            // Said on every read rather than left for a caller to discover after an upload shipped defaults.
            report.Append(Environment.NewLine)
                .Append("These live in editor session state: they survive a domain reload and are lost on an ")
                .Append("editor restart, which is the SDK's own design rather than a Conduit limit.");

            return report.ToString();
        }

        /// <summary>
        /// Writes the world fields an upload sends. Every value is validated before anything is stored, so a
        /// rejected payload leaves the previous metadata exactly as it was.
        /// </summary>
        /// <param name="args">Flat JSON naming the fields to change, with or without a leading section pipe.</param>
        /// <returns>Per-field old to new evidence, or a refusal naming what was wrong.</returns>
        private static string VrcWorldSetCommand(string args)
        {
            string payload = (args ?? string.Empty).Trim();

            // Accepted with or without the section pipe, because the signature publishes one and a caller
            // reading the payload back out of a report will not have it.
            if (payload.StartsWith("|", StringComparison.Ordinal)) payload = payload.Substring(1).Trim();

            if (payload.Length == 0)
            {
                return "Usage: vrc-world-set | <json>. Keys: name, description, capacity, "
                    + "recommendedCapacity, thumbnailPath, tags[], contentWarnings[], debugAllowed. "
                    + "Run vrc-world-info to see the current values.";
            }

            Type state = FindLoadedType(VRC_WORLD_STATE_TYPE);
            if (state == null)
            {
                return "The Worlds SDK's world-field store was not found; this project has no Worlds SDK.";
            }

            JObject requested;
            try { requested = JObject.Parse(payload); }
            catch (Exception ex) { return "The payload is not a JSON object: " + ex.Message; }

            // Resolved in full before a single write, so one bad value cannot leave half the metadata changed.
            var planned = new List<string[]>();
            var unknown = new List<string>();
            List<string> tags = VrcReadTags(state);
            bool tagsChanged = false;

            foreach (var field in requested)
            {
                switch (field.Key)
                {
                    case "name":
                    case "description":
                        planned.Add(new[]
                        {
                            field.Key == "name" ? "WorldName" : "WorldDesc",
                            (string)field.Value ?? string.Empty,
                        });
                        break;

                    case "capacity":
                    case "recommendedCapacity":
                    {
                        int number;
                        if (!int.TryParse(Convert.ToString(field.Value), out number) || number < 1)
                        {
                            return "Refusing the whole payload: " + field.Key
                                + " must be a whole number of at least 1, and was '"
                                + Convert.ToString(field.Value) + "'. Nothing was changed.";
                        }

                        planned.Add(new[]
                        {
                            field.Key == "capacity" ? "WorldCapacity" : "WorldRecommendedCapacity",
                            number.ToString(),
                        });
                        break;
                    }

                    case "thumbnailPath":
                    {
                        string path = (string)field.Value ?? string.Empty;
                        if (path.Length > 0 && !System.IO.File.Exists(path))
                        {
                            return "Refusing the whole payload: thumbnailPath '" + path
                                + "' does not exist on disk. An upload needs a real image, so this is refused "
                                + "now rather than at upload time. Nothing was changed.";
                        }

                        planned.Add(new[] { "WorldThumbPath", path });
                        break;
                    }

                    case "contentWarnings":
                    {
                        var resolved = new List<string>();
                        foreach (JToken entry in VrcAsArray(field.Value))
                        {
                            string tag = VrcResolveWarning(Convert.ToString(entry));
                            if (tag == null)
                            {
                                return "Refusing the whole payload: '" + Convert.ToString(entry)
                                    + "' is not a content warning. " + VrcWarningList() + " Nothing was changed.";
                            }

                            if (!resolved.Contains(tag)) resolved.Add(tag);
                        }

                        // Only the warnings are replaced. An author tag and the debug tag share this one list,
                        // so rewriting all of it would silently drop them.
                        tags.RemoveAll(VrcIsContentWarning);
                        tags.AddRange(resolved);
                        tagsChanged = true;
                        break;
                    }

                    case "tags":
                    {
                        var resolved = new List<string>();
                        foreach (JToken entry in VrcAsArray(field.Value))
                        {
                            string raw = (Convert.ToString(entry) ?? string.Empty).Trim();
                            if (raw.Length == 0) continue;

                            string tag = IsVrcAuthorTag(raw)
                                ? raw
                                : VRC_AUTHOR_TAG_PREFIX + raw.ToLowerInvariant().Replace(' ', '_');
                            if (!resolved.Contains(tag)) resolved.Add(tag);
                        }

                        tags.RemoveAll(IsVrcAuthorTag);
                        tags.AddRange(resolved);
                        tagsChanged = true;
                        break;
                    }

                    case "debugAllowed":
                    {
                        if (field.Value == null || field.Value.Type != JTokenType.Boolean)
                        {
                            return "Refusing the whole payload: debugAllowed must be true or false. "
                                + "Nothing was changed.";
                        }

                        tags.Remove(VRC_DEBUG_TAG);
                        if ((bool)field.Value) tags.Add(VRC_DEBUG_TAG);
                        tagsChanged = true;
                        break;
                    }

                    default:
                        unknown.Add(field.Key);
                        break;
                }
            }

            if (unknown.Count > 0)
            {
                return "Refusing the whole payload: no such field " + string.Join(", ", unknown.ToArray())
                    + ". Accepted: name, description, capacity, recommendedCapacity, thumbnailPath, tags, "
                    + "contentWarnings, debugAllowed. Nothing was changed.";
            }

            if (planned.Count == 0 && !tagsChanged)
            {
                return "Nothing to change: the payload named no fields.";
            }

            var evidence = new StringBuilder();
            foreach (string[] change in planned)
            {
                object before = ReadStaticMember(state, change[0]);
                object typed = change[0] == "WorldCapacity" || change[0] == "WorldRecommendedCapacity"
                    ? (object)int.Parse(change[1])
                    : change[1];

                string error = WriteStaticMember(state, change[0], typed);
                if (error != null)
                {
                    evidence.Append(Environment.NewLine).Append(change[0]).Append(": not written - ").Append(error);
                    continue;
                }

                evidence.Append(Environment.NewLine).Append(change[0]).Append(": ")
                    .Append(VrcDescribe(Convert.ToString(before))).Append(" -> ")
                    .Append(VrcDescribe(Convert.ToString(ReadStaticMember(state, change[0]))));
            }

            if (tagsChanged)
            {
                string before = Convert.ToString(ReadStaticMember(state, "WorldTags"));
                string error = WriteStaticMember(state, "WorldTags", string.Join("|", tags.ToArray()));
                evidence.Append(Environment.NewLine).Append("WorldTags: ");
                if (error != null) evidence.Append("not written - ").Append(error);
                else
                {
                    evidence.Append(VrcDescribe(before)).Append(" -> ")
                        .Append(VrcDescribe(Convert.ToString(ReadStaticMember(state, "WorldTags"))));
                }
            }

            // The panel reads these into its own copy when it is built, so one already open keeps showing what
            // it loaded. That matters for correctness rather than appearance: an upload started from that panel
            // sends its copy, not these values.
            evidence.Append(Environment.NewLine)
                .Append("An already-open Build panel keeps its own copy until it is rebuilt, so close it with ")
                .Append("editor-window-close VRCSdkControlPanel and reopen it before uploading from the panel.");

            return "Wrote the world fields." + evidence.ToString();
        }

        /// <summary>Reads the pipe-joined tag list into a workable list.</summary>
        /// <param name="state">Resolved world-field store type.</param>
        /// <returns>Every tag currently set, in order.</returns>
        private static List<string> VrcReadTags(Type state)
        {
            var joined = ReadStaticMember(state, "WorldTags") as string ?? string.Empty;
            var tags = new List<string>();
            foreach (string tag in joined.Split('|'))
            {
                string trimmed = tag.Trim();
                if (trimmed.Length > 0 && !tags.Contains(trimmed)) tags.Add(trimmed);
            }

            return tags;
        }

        /// <summary>Reports whether a tag is one of the five content warnings.</summary>
        /// <param name="tag">Tag to test.</param>
        /// <returns>True when the tag is a content warning.</returns>
        private static bool VrcIsContentWarning(string tag)
        {
            for (int index = 0; index < VrcContentWarnings.GetLength(0); index++)
            {
                if (string.Equals(VrcContentWarnings[index, 0], tag, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>Reports whether a tag is one the author added rather than one the SDK owns.</summary>
        /// <param name="tag">Tag to test.</param>
        /// <returns>True when the tag carries the author prefix.</returns>
        private static bool IsVrcAuthorTag(string tag)
        {
            return tag != null && tag.StartsWith(VRC_AUTHOR_TAG_PREFIX, StringComparison.Ordinal);
        }

        /// <summary>Maps a content warning's tag or its panel label onto the tag.</summary>
        /// <param name="value">Tag or display label, matched without regard to case.</param>
        /// <returns>The tag, or null when nothing matched.</returns>
        private static string VrcResolveWarning(string value)
        {
            string wanted = (value ?? string.Empty).Trim();
            for (int index = 0; index < VrcContentWarnings.GetLength(0); index++)
            {
                if (string.Equals(VrcContentWarnings[index, 0], wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(VrcContentWarnings[index, 1], wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return VrcContentWarnings[index, 0];
                }
            }

            return null;
        }

        /// <summary>Returns the label the panel shows for a content warning tag.</summary>
        /// <param name="tag">Content warning tag.</param>
        /// <returns>The label, or the tag when it is not a known warning.</returns>
        private static string VrcWarningLabel(string tag)
        {
            for (int index = 0; index < VrcContentWarnings.GetLength(0); index++)
            {
                if (string.Equals(VrcContentWarnings[index, 0], tag, StringComparison.Ordinal))
                {
                    return VrcContentWarnings[index, 1];
                }
            }

            return tag;
        }

        /// <summary>Lists the accepted content warnings for a refusal message.</summary>
        /// <returns>A sentence naming every warning and its label.</returns>
        private static string VrcWarningList()
        {
            var parts = new List<string>();
            for (int index = 0; index < VrcContentWarnings.GetLength(0); index++)
            {
                parts.Add(VrcContentWarnings[index, 0] + " (" + VrcContentWarnings[index, 1] + ")");
            }

            return "Accepted: " + string.Join(", ", parts.ToArray()) + ".";
        }

        /// <summary>Treats a single value as a one-element array so both spellings are accepted.</summary>
        /// <param name="token">Array, single value, or null.</param>
        /// <returns>Tokens to read, never null.</returns>
        private static IEnumerable<JToken> VrcAsArray(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return new JToken[0];
            if (token.Type == JTokenType.Array) return (JArray)token;

            return new[] { token };
        }

        /// <summary>Renders a value for a report, marking an empty one rather than printing nothing.</summary>
        /// <param name="value">Value to render.</param>
        /// <returns>The value, or an explicit empty marker.</returns>
        private static string VrcDescribe(string value)
        {
            return string.IsNullOrEmpty(value) ? "(empty)" : value;
        }

        #endregion

        #region Reflection helpers

        /// <summary>Reads a static property or field by name, whichever the type declares.</summary>
        /// <param name="type">Type declaring the member.</param>
        /// <param name="name">Member name.</param>
        /// <returns>The value, or null when no such member exists.</returns>
        private static object ReadStaticMember(Type type, string name)
        {
            PropertyInfo property = type.GetProperty(
                name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanRead) return property.GetValue(null);

            FieldInfo field = type.GetField(
                name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return field == null ? null : field.GetValue(null);
        }

        /// <summary>Writes a static property or field by name.</summary>
        /// <param name="type">Type declaring the member.</param>
        /// <param name="name">Member name.</param>
        /// <param name="value">Value to store.</param>
        /// <returns>Null on success, or why the write could not happen.</returns>
        private static string WriteStaticMember(Type type, string name, object value)
        {
            PropertyInfo property = type.GetProperty(
                name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite)
            {
                try { property.SetValue(null, value); return null; }
                catch (Exception ex) { return Unwrap(ex); }
            }

            FieldInfo field = type.GetField(
                name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) return "this SDK has no " + name + " on " + type.Name;

            try { field.SetValue(null, value); return null; }
            catch (Exception ex) { return Unwrap(ex); }
        }

        /// <summary>
        /// Reads a static field holding a Unity object, treating a DESTROYED object as absent.
        /// </summary>
        /// <remarks>
        /// Closing a window does not clear the static that pointed at it, so the field still holds a
        /// destroyed reference. Read into <see cref="object"/> and tested with <c>== null</c> that reference
        /// compares non-null, because the plain operator is used rather than Unity's overload, and a dead
        /// window is then handed on as a live one. Returning the typed reference is what restores the
        /// overload, so a closed panel reads as absent and gets surfaced.
        /// </remarks>
        /// <param name="type">Type declaring the field.</param>
        /// <param name="name">Field name.</param>
        /// <returns>The live object, or null when the field is absent, unset, or holds a destroyed object.</returns>
        private static UnityEngine.Object ReadStaticUnityObject(Type type, string name)
        {
            FieldInfo field = type.GetField(
                name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null) return null;

            var value = field.GetValue(null) as UnityEngine.Object;
            return value == null ? null : value;
        }

        /// <summary>Reads an instance field or property by name, whichever the type declares.</summary>
        /// <param name="target">Instance to read from.</param>
        /// <param name="type">Its type.</param>
        /// <param name="name">Member name.</param>
        /// <returns>The value, or null when no such member exists.</returns>
        private static object ReadMemberValue(object target, Type type, string name)
        {
            FieldInfo field = type.GetField(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null) return field.GetValue(target);

            PropertyInfo property = type.GetProperty(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null ? null : property.GetValue(target);
        }

        /// <summary>Unwraps a reflection exception so the reported failure is the real one.</summary>
        /// <param name="ex">Caught exception.</param>
        /// <returns>Type name and message of the underlying failure.</returns>
        private static string Unwrap(Exception ex)
        {
            Exception real = ex is TargetInvocationException && ex.InnerException != null
                ? ex.InnerException
                : ex;
            return real.GetType().Name + ": " + real.Message;
        }

        /// <summary>Collapses a multi-line alert onto one line so a report stays readable.</summary>
        /// <param name="text">Alert text.</param>
        /// <returns>The text with line breaks replaced by spaces.</returns>
        private static string OneLine(string text)
        {
            return (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        }

        #endregion

        /// <summary>
        /// Routes the VRChat build-pipeline verbs. Returns null when the verb belongs to another area.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, unused while these verbs take no arguments.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchVrcBuild(string verb, string command, string[] parts)
        {
            if (verb == "vrc-validate")
            {
                return VrcValidateCommand();
            }

            if (verb == "vrc-world-info")
            {
                return VrcWorldInfoCommand();
            }

            if (verb == "vrc-world-set")
            {
                return VrcWorldSetCommand(GetCommandRemainder(command, parts[0]));
            }

            return null;
        }
    }
}
