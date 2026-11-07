/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Editor application, console, selection, and Conduit command-list tools as MCP-style verbs.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Console Commands

        /// <summary>
        /// Returns the latest Unity Console entries through the existing Conduit console reader.
        /// </summary>
        /// <param name="parts">The command tokens, optionally including a count.</param>
        /// <returns>Formatted log entries.</returns>
        private static string ConsoleGetLogsCommand(string[] parts)
        {
            var count = parts.Length > 1 ? ParsePositiveInt(parts[1], 40, MAX_CONSOLE_ENTRIES) : 40;
            var entries = ReadConsoleEntries();
            var slice = TakeLast(entries, count);
            return FormatEntries(slice, entries.Count - slice.Count + 1);
        }

        #endregion

        #region Editor Application Commands

        /// <summary>
        /// Returns editor play, pause, compile, update, queued UdonSharp authoring, and version state as compact JSON.
        ///
        /// Every value comes from <see cref="ConduitEditorActivity"/> rather than from EditorApplication, so
        /// this answers from any thread and no longer has to queue behind the work it is reporting on. The
        /// flags are captured, the version is a constant, and the clock is reproduced from a load-time anchor
        /// because it was measured advancing with wall time through a blocked main thread.
        /// </summary>
        /// <returns>A compact JSON status object.</returns>
        private static string EditorApplicationGetStateCommand()
        {
            var state = ConduitEditorActivity.Current.State;
            return "{\"unityVersion\":" + JsonQuote(ConduitEditorActivity.UnityVersion) +
                ",\"isPlaying\":" + (state.Playing ? "true" : "false") +
                ",\"isPlayingOrWillChangePlaymode\":" + (state.WillChangePlaymode ? "true" : "false") +
                ",\"isPaused\":" + (state.Paused ? "true" : "false") +
                ",\"isCompiling\":" + (state.Compiling ? "true" : "false") +
                ",\"isUpdating\":" + (state.Updating ? "true" : "false") +
                ",\"isUdonSharpCreatePending\":" + (System.IO.File.Exists(PendingUdonSharpCreatePath) ? "true" : "false") +
                ",\"timeSinceStartup\":" + JsonNumber(ConduitEditorActivity.TimeSinceStartup) + "}";
        }

        /// <summary>
        /// Changes editor play or pause state through the same deferred lifecycle handlers as the dedicated
        /// play, stop, pause, and resume verbs.
        /// </summary>
        /// <param name="args">One of play, stop, pause, resume, or unpause.</param>
        /// <returns>An immediate no-op or refusal report; successful transitions complete through their deferred response.</returns>
        private static string EditorApplicationSetStateCommand(string args)
        {
            var action = (args ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
            if (action == "play")
            {
                return SetPlayMode(true);
            }

            if (action == "stop")
            {
                return SetPlayMode(false);
            }

            if (action == "pause")
            {
                return SetPauseMode(true);
            }

            if (action == "resume" || action == "unpause")
            {
                return SetPauseMode(false);
            }

            return "Usage: editor-application-set-state <play|stop|pause|resume|unpause>";
        }

        /// <summary>
        /// Imports pending on-disk changes and then reloads the script domain exactly once.
        /// </summary>
        /// <returns>A deferred response placeholder, confirmation line, or Play Mode refusal.</returns>
        private static string DomainRefreshCommand()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Refusing to refresh the script domain while Play Mode is active or changing." + Environment.NewLine + FormatEditorStatus();
            }

            if (TryDeferUntilReload("__domain-refresh-complete"))
            {
                RequestFreshDomainReload();
                return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
            }

            RequestFreshDomainReload();
            return "Requested Unity script domain refresh. Reconnect the MCP bridge after Unity finishes reloading if newly-added Conduit tools or serialized fields are still missing." + Environment.NewLine + FormatEditorStatus();
        }

        /// <summary>
        /// Refreshes the AssetDatabase so pending filesystem edits are imported, then reloads the script
        /// domain exactly once. A bare RequestScriptReload reloads the OLD assemblies and leaves edited
        /// files unimported, which the next focus-triggered auto-refresh then pays for with a second
        /// compile and reload. When the refresh queues a compile, isCompiling reads true synchronously and
        /// that compile's own reload already delivers the fresh domain, so no explicit request is added.
        /// </summary>
        private static void RequestFreshDomainReload()
        {
            AssetDatabase.Refresh();
            if (!EditorApplication.isCompiling)
            {
                EditorUtility.RequestScriptReload();
            }
        }

        /// <summary>
        /// Reports completion after <see cref="DomainRefreshCommand"/> survives the requested domain reload.
        /// </summary>
        /// <returns>A confirmation line and current editor state.</returns>
        private static string DomainRefreshCompleteCommand()
        {
            return "Unity script domain refresh completed. Reconnect the MCP bridge if the client still has a stale tool or serialized-field cache." + Environment.NewLine + FormatEditorStatus();
        }

        #endregion

        #region Selection Commands

        /// <summary>
        /// Emits the active instance id, selected asset GUIDs, and per-object JSON for each entry in Selection.objects.
        /// </summary>
        /// <returns>A compact JSON selection payload.</returns>
        private static string EditorSelectionGetCommand()
        {
            var builder = new StringBuilder();
            builder.Append("{\"activeInstanceID\":").Append(Selection.activeInstanceID)
                .Append(",\"assetGUIDs\":").Append(JsonStringArray(Selection.assetGUIDs ?? new string[0]))
                .Append(",\"objects\":[");
            var objects = Selection.objects ?? new UnityEngine.Object[0];
            for (var i = 0; i < objects.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                if (objects[i] is GameObject)
                {
                    builder.Append(JsonGameObject((GameObject)objects[i]));
                }
                else if (objects[i] is Component)
                {
                    builder.Append(JsonComponent((Component)objects[i]));
                }
                else
                {
                    builder.Append(JsonObjectRef(objects[i]));
                }
            }

            builder.Append("]}");
            return builder.ToString();
        }

        /// <summary>
        /// Sets Unity's current selection to a scene object or asset.
        /// </summary>
        /// <param name="args">The object target to select.</param>
        /// <returns>A compact JSON selection payload after applying the selection.</returns>
        private static string EditorSelectionSetCommand(string args)
        {
            UnityEngine.Object unityObject;
            var error = TryFindAnyUnityObject(args, out unityObject);
            if (error != null)
            {
                return error;
            }

            Selection.objects = new[] { unityObject };
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
            return EditorSelectionGetCommand();
        }

        #endregion

        #region Tool Listing

        /// <summary>
        /// Lists the Conduit verbs available in this project. It reads the same filtered set as help,
        /// commands and the MCP tool list, so discovery never names a verb this project refuses.
        /// </summary>
        /// <param name="args">Optional case-insensitive verb filter.</param>
        /// <returns>A JSON array of command signatures.</returns>
        private static string UnityToolListCommand(string args)
        {
            var filter = (args ?? string.Empty).Trim().Trim('"');
            // Match ANY whitespace/comma-separated term (OR), so a batch filter like
            // "create-usharp gameobject-tree save-assets" returns all of them, not nothing.
            var terms = string.IsNullOrEmpty(filter)
                ? null
                : filter.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var builder = new StringBuilder();
            builder.Append('[');
            var count = 0;
            foreach (var signature in EffectiveCommandSignatures())
            {
                var space = signature.IndexOf(' ');
                var verb = space < 0 ? signature : signature.Substring(0, space);
                var commandArgs = space < 0 ? string.Empty : signature.Substring(space + 1);
                if (terms != null)
                {
                    var matched = false;
                    foreach (var term in terms)
                    {
                        if (verb.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matched = true;
                            break;
                        }
                    }
                    if (!matched)
                    {
                        continue;
                    }
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"verb\":").Append(JsonQuote(verb))
                    .Append(",\"args\":").Append(JsonQuote(commandArgs))
                    .Append('}');
                count++;
            }

            builder.Append(']');
            return builder.ToString();
        }

        #endregion
    }
}
