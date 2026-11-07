/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using Unity.Profiling;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Profiler markers that put Conduit's own work under its name in the Unity Profiler, in a category of its own, so a
    /// capture shows what the bridge itself cost and the profiler verbs can report it apart from the game and the rest
    /// of the editor.
    /// </summary>
    internal static class ConduitProfiling
    {
        /// <summary>Name of the category, of the main-thread marker, and the start of every Conduit marker name.</summary>
        internal const string MARKER_NAME = "ItsRenji Conduit";

        /// <summary>The profiler category every Conduit marker belongs to.</summary>
        internal static readonly ProfilerCategory Category = new ProfilerCategory(MARKER_NAME, ProfilerCategoryColor.Scripts);

        /// <summary>Wraps Conduit's main-thread work each tick: running queued commands, and the ticks of work spanning many frames.</summary>
        internal static readonly ProfilerMarker MainThread = new ProfilerMarker(Category, MARKER_NAME);

        /// <summary>Wraps answering one request on a listener thread, before anything is queued for the main thread.</summary>
        internal static readonly ProfilerMarker Request = new ProfilerMarker(Category, MARKER_NAME + ".Request");

        /// <summary>Wraps a settle watch reading newly recorded frames into its cache.</summary>
        internal static readonly ProfilerMarker SettleRead = new ProfilerMarker(Category, MARKER_NAME + ".SettleRead");

        /// <summary>Wraps a settle watch comparing its two newest windows.</summary>
        internal static readonly ProfilerMarker SettleCheck = new ProfilerMarker(Category, MARKER_NAME + ".SettleCheck");

        /// <summary>Wraps taking the next durable job from the job ledger.</summary>
        internal static readonly ProfilerMarker Claim = new ProfilerMarker(Category, MARKER_NAME + ".Claim");

        /// <summary>Wraps recording a finished job's result in the job ledger.</summary>
        internal static readonly ProfilerMarker Complete = new ProfilerMarker(Category, MARKER_NAME + ".Complete");

        /// <summary>Wraps writing one job record to disk, on whichever thread writes it.</summary>
        internal static readonly ProfilerMarker LedgerWrite = new ProfilerMarker(Category, MARKER_NAME + ".LedgerWrite");

        /// <summary>Wraps sending an immediate request's reply.</summary>
        internal static readonly ProfilerMarker Respond = new ProfilerMarker(Category, MARKER_NAME + ".Respond");

        /// <summary>One marker per command verb, created the first time the verb runs and reused after.</summary>
        private static readonly Dictionary<string, ProfilerMarker> CommandMarkers = new Dictionary<string, ProfilerMarker>(StringComparer.Ordinal);

        /// <summary>Guards <see cref="CommandMarkers"/>.</summary>
        private static readonly object CommandMarkersLock = new object();

        /// <summary>The marker for running one command, named after its verb, such as ItsRenji Conduit.profiler-mark.</summary>
        /// <param name="command">The raw command line.</param>
        /// <returns>The verb's marker.</returns>
        internal static ProfilerMarker Command(string command)
        {
            string text = (command ?? string.Empty).TrimStart();
            int end = 0;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            string verb = end > 0 ? text.Substring(0, end).ToLowerInvariant() : "unknown";
            lock (CommandMarkersLock)
            {
                ProfilerMarker marker;
                if (!CommandMarkers.TryGetValue(verb, out marker))
                {
                    marker = new ProfilerMarker(Category, MARKER_NAME + "." + verb);
                    CommandMarkers[verb] = marker;
                }

                return marker;
            }
        }

        /// <summary>True for a marker that is Conduit's own work.</summary>
        /// <param name="name">Marker name.</param>
        /// <returns>True when the name is a Conduit marker.</returns>
        internal static bool IsConduitMarker(string name)
        {
            return name != null && (name == MARKER_NAME || name.StartsWith(MARKER_NAME + ".", StringComparison.Ordinal));
        }
    }
}
