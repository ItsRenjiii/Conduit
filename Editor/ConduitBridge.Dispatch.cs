/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace ItsRenji.Conduit
{
    /// <summary>
    /// Verb dispatch: routes one parsed command line to the handler that serves it.
    /// </summary>
    internal static partial class ConduitBridge
    {
        /// <summary>
        /// Parses one command line and routes its verb to the area that owns it. Each area dispatcher returns
        /// null when the verb is not its own, which is what lets them be tried in turn; every verb handler
        /// returns a result string, so null unambiguously means the area did not claim the verb.
        /// </summary>
        /// <param name="command">Raw command line.</param>
        /// <returns>The verb's result text, or the unknown-command message.</returns>
        private static string ExecuteCommand(string command)
        {            // Quote-aware, so a spaced value the MCP server quoted (an asset path such as
            // "Assets/Scenes/Drink System.unity") survives as ONE token. A plain whitespace split left the
            // quote characters in the token and truncated the value at its first space, which made the
            // server's quoting inert and silently wrote screenshots to a path ending at that space.
            // Commas are NOT separators here: a top-level argument may legitimately contain one.
            var parts = SplitCommandTokens(command, false);
            if (parts.Length == 0)
            {
                return "No command.";
            }

            var verb = parts[0].ToLowerInvariant();
            if (verb == "ping")
            {
                return "pong";
            }

            if (verb == "help")
            {
                return RenderHelp();
            }

            if (verb == "commands")
            {
                return RenderCommandsJson();
            }

            if (verb == "mcp-refresh-tools")
            {
                return AnnounceToolsOnDemand();
            }

            // Worlds-only capabilities (UdonSharp authoring + ClientSim driver) are not registered outside a
            // Worlds project; refuse them defensively too, in case a manual client calls one directly.
            if (WorldsOnlyVerbs.Contains(verb) && !IsWorldsProject)
            {
                return verb + " requires a VRChat Worlds project (Packages/com.vrchat.worlds not found). Detected: " + ProjectKindLabel() + ".";
            }

            if (verb == "status")
            {
                return FormatEditorStatus();
            }

            if (verb == "domain-refresh" || verb == "domain-reload")
            {
                return DomainRefreshCommand();
            }

            if (verb == "__domain-refresh-complete")
            {
                return DomainRefreshCompleteCommand();
            }

            // Internal. A deferred command records its own single-line summary here so the post-reload
            // response can carry what the command actually DID as well as the compile outcome; without it a
            // deferred verb can only return the generic post-compile report and its specifics are lost.
            if (verb == "__deferred-note")
            {
                return GetCommandRemainder(command, parts[0]) + Environment.NewLine + BuildPostCompileReport();
            }


            string routed;
            if ((routed = DispatchUndo(verb, command, parts)) != null) return routed;
            if ((routed = DispatchAnimator(verb, command, parts)) != null) return routed;
            if ((routed = DispatchAssets(verb, command, parts)) != null) return routed;
            if ((routed = DispatchGameObject(verb, command, parts)) != null) return routed;
            if ((routed = DispatchScene(verb, command, parts)) != null) return routed;
            if ((routed = DispatchScript(verb, command, parts)) != null) return routed;
            if ((routed = DispatchConsole(verb, command, parts)) != null) return routed;
            if ((routed = DispatchEditorState(verb, command, parts)) != null) return routed;
            if ((routed = DispatchTagsLayers(verb, command, parts)) != null) return routed;
            if ((routed = DispatchEditorOps(verb, command, parts)) != null) return routed;
            if ((routed = DispatchUdon(verb, command, parts)) != null) return routed;
            if ((routed = DispatchPackage(verb, command, parts)) != null) return routed;
            if ((routed = DispatchEditorWindow(verb, command, parts)) != null) return routed;
            if ((routed = DispatchProfiler(verb, command, parts)) != null) return routed;
            if ((routed = DispatchProfilerData(verb, command, parts)) != null) return routed;
            if ((routed = DispatchEditorFocus(verb, command, parts)) != null) return routed;
            if ((routed = DispatchCatalog(verb, command, parts)) != null) return routed;
            if ((routed = DispatchUdonSharpAuthoring(verb, command, parts)) != null) return routed;
            if ((routed = DispatchSceneObjects(verb, command, parts)) != null) return routed;
            if ((routed = DispatchSceneView(verb, command, parts)) != null) return routed;
            if ((routed = DispatchVrcInput(verb, command, parts)) != null) return routed;
            if ((routed = DispatchVrcBuild(verb, command, parts)) != null) return routed;
            if ((routed = DispatchVrcBuildRun(verb, command, parts)) != null) return routed;
#if CONDUIT_PROBUILDER
            if ((routed = DispatchProBuilder(verb, command, parts)) != null) return routed;
#endif
            if ((routed = DispatchAgentNotify(verb, command, parts)) != null) return routed;

            return "Unknown command '" + verb + "'. Try: help";

        }
    }
}
