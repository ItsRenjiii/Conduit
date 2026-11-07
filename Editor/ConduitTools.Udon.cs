/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Reads what an UdonSharp behaviour COMPILED TO, rather than what its C# says.
    /// <para>
    /// UdonSharp keeps the assembly listing for every program in `UdonSharp.UdonSharpEditorCache`, an
    /// internal class carrying a public `GetUASMStr`. Internal is why this reaches it by reflection, the
    /// same way the rest of the bridge reaches UdonSharp, so Conduit keeps working in a project that does
    /// not have the package at all.
    /// </para>
    /// <para>
    /// The listing answers questions the source cannot. A method that survives the compile but is never
    /// exported is dead code Udon can never reach; an extern list is exactly which engine calls a
    /// behaviour makes, which is what to read when a behaviour runs but does not do what its C# appears to
    /// say. It is also the only way to confirm an event is really an entry point rather than an ordinary
    /// method that happens to be named like one.
    /// </para>
    /// <para>
    /// The file also answers what Udon can call at all: udon-exposure searches the SDK's own
    /// node-definition registry, the authoritative whitelist that the one-member-per-compile probe in
    /// the console workflow only approximates.
    /// </para>
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Udon Program Constants

        /// <summary>
        /// Usage line for the single-program command.
        ///
        /// The first token is ONE placeholder naming the kinds it accepts, never a pipe-separated list of
        /// them. The schema generator reads an angle-bracket group as a choice list, correctly, because five
        /// other verbs use that form for real literal values; spelling two argument KINDS the same way
        /// published an enum of the words "sceneObject" and "assetPath" and rejected every real target
        /// before the verb ran.
        /// </summary>
        private const string UdonProgramUsage =
            "Usage: udon-program <targetOrAssetPath> [| summary|exports|methods|externs|symbols|raw]";

        /// <summary>Entry points that cost a behaviour something on every frame.</summary>
        private static readonly string[] UdonPerFrameEntryPoints = { "_update", "_lateUpdate", "_fixedUpdate", "_postLateUpdate" };

        /// <summary>How many node-definition matches udon-exposure lists before summarising the rest.</summary>
        private const int UdonExposureMatchLimit = 100;

        #endregion

        #region Udon Program Commands

        /// <summary>
        /// Reports what one UdonSharp program compiled to.
        /// </summary>
        /// <param name="args">Target, then an optional section after a pipe.</param>
        /// <returns>A readable report, or an error naming what could not be resolved.</returns>
        private static string UdonProgramCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length == 0 || string.IsNullOrWhiteSpace(sections[0]))
            {
                return UdonProgramUsage;
            }

            var section = sections.Length > 1 ? sections[1].Trim().ToLowerInvariant() : "summary";
            if (section.Length == 0)
            {
                section = "summary";
            }

            if (section != "summary" && section != "exports" && section != "methods"
                && section != "externs" && section != "symbols" && section != "raw")
            {
                return "Unknown section '" + section + "'. " + UdonProgramUsage;
            }

            string resolveError;
            var programs = ResolveUdonPrograms(sections[0].Trim(), out resolveError);
            if (resolveError != null)
            {
                return resolveError;
            }

            var builder = new StringBuilder();
            for (var i = 0; i < programs.Count; i++)
            {
                if (i > 0)
                {
                    builder.AppendLine();
                }

                AppendUdonProgramReport(builder, programs[i], section);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Reports every UdonSharp program used by one scene, one line each.
        /// </summary>
        /// <param name="args">The required scene, then an optional case-insensitive filter on the behaviour type name.</param>
        /// <returns>A readable report, or a statement that the scene holds none.</returns>
        private static string UdonProgramAuditCommand(string args)
        {
            var tokens = SplitCommandTokens(args ?? string.Empty, false);
            Scene scene;
            var sceneError = TryResolveRequiredScene(tokens.Length > 0 ? tokens[0] : string.Empty, out scene);
            if (sceneError != null)
            {
                return sceneError;
            }

            var filter = tokens.Length > 1 ? string.Join(" ", tokens, 1, tokens.Length - 1).Trim() : string.Empty;

            string cacheError;
            if (!UdonAssemblyReaderAvailable(out cacheError))
            {
                return cacheError;
            }

            var entries = new List<UdonProgramTarget>();
            foreach (var behaviour in EnumerateSceneUdonBehaviours(scene))
            {
                var programAsset = GetUdonProgramSource(behaviour);
                if (programAsset == null)
                {
                    continue;
                }

                var typeName = programAsset.name;
                if (filter.Length > 0 && typeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                entries.Add(new UdonProgramTarget
                {
                    Label = GetGameObjectPath(behaviour.gameObject),
                    ProgramAsset = programAsset,
                });
            }

            if (entries.Count == 0)
            {
                return filter.Length > 0
                    ? "No UdonSharp behaviours in scene " + scene.name + " match '" + filter + "'."
                    : "No UdonSharp behaviours in scene " + scene.name + ".";
            }

            var builder = new StringBuilder();
            builder.Append("UdonSharp behaviours in scene ").Append(scene.name).Append(": ").Append(entries.Count).AppendLine();

            var perFrame = 0;
            foreach (var entry in entries)
            {
                var listing = ReadUdonAssembly(entry.ProgramAsset);
                builder.Append("  ").Append(entry.Label).Append("  ").Append(entry.ProgramAsset.name);

                if (listing == null)
                {
                    builder.AppendLine("  (no cached listing; recompile to populate)");
                    continue;
                }

                var parsed = ParseUdonAssembly(listing);
                var entryPoints = parsed.EntryPoints();
                var frameEvents = entryPoints.Where(IsPerFrameEntryPoint).ToArray();
                if (frameEvents.Length > 0)
                {
                    perFrame++;
                }

                builder.Append("  entry ").Append(entryPoints.Count == 0 ? "none" : string.Join(",", entryPoints))
                    .Append("  externs ").Append(parsed.ExternCount)
                    .Append("  methods ").Append(parsed.Methods.Count);

                if (frameEvents.Length > 0)
                {
                    builder.Append("  PER-FRAME: ").Append(string.Join(",", frameEvents));
                }

                builder.AppendLine();
            }

            builder.Append("Behaviours with a per-frame entry point: ").Append(perFrame).Append(" of ").Append(entries.Count).Append('.');
            return builder.ToString();
        }

        /// <summary>
        /// Reports which Udon node definitions match a name fragment, which is the authoritative
        /// answer to whether Udon can call a member at all.
        /// <para>
        /// The registry names strip the dots out of type names (VRCSDK3RenderingVRCAsyncGPUReadback),
        /// so the fragment is also matched with its own dots and pluses removed, letting type-find
        /// output be pasted straight in. Events appear as Event_ names and types as Type_ names.
        /// </para>
        /// <para>
        /// A fragment search only finds what the caller already suspected exists, so the registry is
        /// also enumerable: the types section renders the whole surface as an overview, the dump
        /// section writes every definition name to a sorted baseline file, and the diff section
        /// compares the live registry against such a baseline and reports what changed - so an SDK
        /// update announces its own new surface, with no keyword guessed and no shell tooling. An
        /// empty fragment covers the whole registry.
        /// </para>
        /// </summary>
        /// <param name="args">Fragment (may be empty for types/dump), then an optional section after a pipe.</param>
        /// <returns>Matching definition names, an overview, a dump confirmation, or a plain no-match statement.</returns>
        private static string UdonExposureCommand(string args)
        {
            const string usage = "Usage: udon-exposure <nameFragment> [| types | dump <path> | diff <path>] - an empty fragment with a section covers the whole registry";

            var sections = SplitCommandSections(args);
            var fragment = sections.Length > 0 ? sections[0].Trim().Trim('"') : string.Empty;
            var modeSection = sections.Length > 1 ? sections[1].Trim() : string.Empty;

            var mode = "list";
            var sectionPath = string.Empty;
            if (modeSection.Length > 0)
            {
                var modeTokens = SplitCommandTokens(modeSection);
                if (modeTokens.Length == 1 && modeTokens[0].Equals("types", StringComparison.OrdinalIgnoreCase))
                {
                    mode = "types";
                }
                else if (modeTokens.Length == 2
                    && (modeTokens[0].Equals("dump", StringComparison.OrdinalIgnoreCase)
                        || modeTokens[0].Equals("diff", StringComparison.OrdinalIgnoreCase)))
                {
                    mode = modeTokens[0].ToLowerInvariant();
                    sectionPath = modeTokens[1].Trim('"');
                }
                else
                {
                    return usage;
                }
            }

            // A bare full listing would be tens of thousands of response lines, so enumerating the
            // whole registry goes through the overview, the baseline dump, or the diff instead.
            if (fragment.Length == 0 && mode == "list")
            {
                return usage;
            }

            string registryError;
            var names = ReadUdonNodeDefinitionNames(out registryError);
            if (registryError != null)
            {
                return registryError;
            }

            // Registry names carry no dots inside type names, so a dotted or nested spelling coming
            // from type-find output is matched through a stripped variant as well.
            var stripped = fragment.Replace(".", string.Empty).Replace("+", string.Empty);
            List<string> matches;
            if (fragment.Length == 0)
            {
                matches = names;
            }
            else
            {
                matches = new List<string>();
                foreach (var name in names)
                {
                    if (MatchesUdonExposureFragment(name, fragment, stripped))
                    {
                        matches.Add(name);
                    }
                }

                // A diff still proceeds on zero live matches, because the fragment's whole surface
                // having been removed since the baseline is exactly what it exists to report.
                if (matches.Count == 0 && mode != "diff")
                {
                    return "No Udon node definition matches '" + fragment + "' across all " + names.Count
                         + " definitions (raw and dot-stripped spellings searched) - Udon does not expose it under that name.";
                }
            }

            if (mode == "dump")
            {
                return DumpUdonExposureNames(matches, names.Count, fragment, sectionPath);
            }

            if (mode == "diff")
            {
                return DiffUdonExposureNames(matches, fragment, stripped, sectionPath);
            }

            if (mode == "types")
            {
                return RenderUdonExposureOverview(matches, names.Count, fragment);
            }

            matches.Sort(StringComparer.Ordinal);
            var suppressed = 0;
            if (matches.Count > UdonExposureMatchLimit)
            {
                suppressed = matches.Count - UdonExposureMatchLimit;
                matches.RemoveRange(UdonExposureMatchLimit, suppressed);
            }

            var builder = new StringBuilder();
            builder.Append("Udon node definitions matching '").Append(fragment).Append("': ").Append(matches.Count + suppressed)
                .Append(" of ").Append(names.Count).AppendLine();
            foreach (var match in matches)
            {
                builder.Append("  ").AppendLine(match);
            }

            if (suppressed > 0)
            {
                builder.Append("  ... and ").Append(suppressed).AppendLine(" more; narrow the fragment, or write the full set with | dump <path>.");
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Writes definition names to a sorted text file, the baseline the diff section compares a
        /// later registry against.
        /// </summary>
        /// <param name="matches">Definition names to write.</param>
        /// <param name="total">How many definitions the registry holds in all.</param>
        /// <param name="fragment">The fragment that selected the names; empty means the whole registry.</param>
        /// <param name="path">Destination file path.</param>
        /// <returns>A confirmation carrying the count, path and size, or a validation error.</returns>
        private static string DumpUdonExposureNames(List<string> matches, int total, string fragment, string path)
        {
            path = NormalizeProjectRelativePath(path);
            if (string.IsNullOrEmpty(path))
            {
                return "Invalid dump path.";
            }

            var sorted = new List<string>(matches);
            sorted.Sort(StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllLines(path, sorted);

            var size = new FileInfo(path).Length;
            return "Wrote " + sorted.Count + (fragment.Length == 0 ? " of " + total : " match(es) of " + total)
                 + " definition names to " + path + " (" + (size / 1024) + " KB); compare a later SDK's registry against it with udon-exposure | diff <path>.";
        }

        /// <summary>
        /// Reports whether one definition name matches the fragment in its raw or dot-stripped spelling.
        /// </summary>
        /// <param name="name">Definition full name.</param>
        /// <param name="fragment">Fragment as the caller typed it.</param>
        /// <param name="stripped">The fragment with dots and pluses removed.</param>
        /// <returns>True when either spelling is contained in the name.</returns>
        private static bool MatchesUdonExposureFragment(string name, string fragment, string stripped)
        {
            return name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0
                || (stripped.Length != fragment.Length && name.IndexOf(stripped, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Compares the live registry against a baseline written by the dump section, reporting every
        /// definition added and removed since, so an SDK update surfaces its own exposure changes
        /// without anyone guessing a name to search for.
        /// </summary>
        /// <param name="liveMatches">Live definition names, already narrowed by the fragment.</param>
        /// <param name="fragment">The narrowing fragment; empty compares the whole registry.</param>
        /// <param name="stripped">The fragment's dot-stripped spelling, for filtering the baseline.</param>
        /// <param name="path">Baseline file, one definition name per line.</param>
        /// <returns>The added and removed names, or a plain statement that nothing changed.</returns>
        private static string DiffUdonExposureNames(List<string> liveMatches, string fragment, string stripped, string path)
        {
            path = NormalizeProjectRelativePath(path);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return "No baseline file at " + (string.IsNullOrEmpty(path) ? "that path" : path)
                     + ". Write one first with udon-exposure | dump <path>.";
            }

            var baseline = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(path))
            {
                var name = line.Trim();
                if (name.Length == 0 || (fragment.Length > 0 && !MatchesUdonExposureFragment(name, fragment, stripped)))
                {
                    continue;
                }

                baseline.Add(name);
            }

            var live = new HashSet<string>(liveMatches, StringComparer.Ordinal);
            var added = new List<string>();
            foreach (var name in live)
            {
                if (!baseline.Contains(name))
                {
                    added.Add(name);
                }
            }

            var removed = new List<string>();
            foreach (var name in baseline)
            {
                if (!live.Contains(name))
                {
                    removed.Add(name);
                }
            }

            var scope = fragment.Length == 0 ? "the whole registry" : "'" + fragment + "'";
            if (added.Count == 0 && removed.Count == 0)
            {
                return "No exposure changes for " + scope + ": the live registry matches " + path
                     + " (" + live.Count + " definitions).";
            }

            added.Sort(StringComparer.Ordinal);
            removed.Sort(StringComparer.Ordinal);

            var builder = new StringBuilder();
            builder.Append("Udon exposure changes for ").Append(scope).Append(" against ").Append(path)
                .Append(": ").Append(added.Count).Append(" added, ").Append(removed.Count)
                .Append(" removed  (baseline ").Append(baseline.Count).Append(", live ").Append(live.Count).AppendLine(" definitions)");

            // A baseline that is not really an exposure dump produces two nearly disjoint sets; the
            // count header plus a sample says so more usefully than tens of thousands of lines.
            var disjoint = baseline.Count > 0 && live.Count > 0
                && added.Count > live.Count / 2 && removed.Count > baseline.Count / 2;
            var listCap = disjoint ? 20 : int.MaxValue;
            if (disjoint)
            {
                builder.AppendLine("These sets barely overlap - check that the file really is a dump from this verb. Showing 20 of each:");
            }

            AppendUdonExposureDiffSection(builder, "Added", added, listCap);
            AppendUdonExposureDiffSection(builder, "Removed", removed, listCap);
            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Appends one side of an exposure diff, capped only when the sets look disjoint.
        /// </summary>
        /// <param name="builder">Report being built.</param>
        /// <param name="label">Section label, Added or Removed.</param>
        /// <param name="namesToList">Sorted names for this side.</param>
        /// <param name="cap">How many lines to render before summarising the rest.</param>
        private static void AppendUdonExposureDiffSection(StringBuilder builder, string label, List<string> namesToList, int cap)
        {
            builder.Append(label).Append(" (").Append(namesToList.Count).AppendLine("):");
            if (namesToList.Count == 0)
            {
                builder.AppendLine("  none");
                return;
            }

            var shown = 0;
            foreach (var name in namesToList)
            {
                if (shown >= cap)
                {
                    builder.Append("  ... and ").Append(namesToList.Count - shown).AppendLine(" more.");
                    break;
                }

                builder.Append("  ").AppendLine(name);
                shown++;
            }
        }

        /// <summary>
        /// Renders the exposed surface as an overview: every member group with its definition count,
        /// and every event by name, so the registry can be browsed without guessing a keyword.
        /// </summary>
        /// <param name="matches">Definition names to summarise.</param>
        /// <param name="total">How many definitions the registry holds in all.</param>
        /// <param name="fragment">The fragment that selected the names; empty means the whole registry.</param>
        /// <returns>The overview report.</returns>
        private static string RenderUdonExposureOverview(List<string> matches, int total, string fragment)
        {
            var groups = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var events = new List<string>();
            var specials = 0;
            foreach (var name in matches)
            {
                var marker = name.IndexOf(".__", StringComparison.Ordinal);
                if (marker > 0)
                {
                    var group = name.Substring(0, marker);
                    int count;
                    groups.TryGetValue(group, out count);
                    groups[group] = count + 1;
                }
                else if (name.StartsWith("Event_", StringComparison.Ordinal))
                {
                    events.Add(name);
                }
                else
                {
                    specials++;
                }
            }

            events.Sort(StringComparer.Ordinal);
            var builder = new StringBuilder();
            builder.Append("Udon exposure overview");
            if (fragment.Length > 0)
            {
                builder.Append(" for '").Append(fragment).Append('\'');
            }

            builder.Append(": ").Append(matches.Count).Append(" of ").Append(total).Append(" definitions, ")
                .Append(groups.Count).Append(" member groups, ").Append(events.Count).AppendLine(" events");

            builder.Append("Events (").Append(events.Count).AppendLine("):");
            if (events.Count == 0)
            {
                builder.AppendLine("  none");
            }

            foreach (var eventName in events)
            {
                builder.Append("  ").AppendLine(eventName);
            }

            builder.Append("Member groups (").Append(groups.Count).AppendLine(") - definition count each:");
            if (groups.Count == 0)
            {
                builder.AppendLine("  none");
            }

            foreach (var pair in groups)
            {
                builder.Append("  ").Append(pair.Key).Append("  ").AppendLine(pair.Value.ToString());
            }

            if (specials > 0)
            {
                builder.Append(specials).Append(" further type and variable definitions (Type_, Variable_, ...) are in the full list; write it with | dump <path>.");
            }

            return builder.ToString().TrimEnd();
        }

        #endregion

        #region Udon Program Reporting

        /// <summary>
        /// Appends one program's report in the requested section.
        /// </summary>
        /// <param name="builder">Report being built.</param>
        /// <param name="target">Program and the label it was resolved from.</param>
        /// <param name="section">Which part of the listing to render.</param>
        private static void AppendUdonProgramReport(StringBuilder builder, UdonProgramTarget target, string section)
        {
            var listing = ReadUdonAssembly(target.ProgramAsset);
            if (listing == null)
            {
                builder.Append(target.Label).AppendLine(": no cached assembly listing. Recompile the behaviour to populate it.");
                return;
            }

            if (section == "raw")
            {
                builder.Append(target.Label).AppendLine(":").Append(listing);
                return;
            }

            var parsed = ParseUdonAssembly(listing);
            builder.Append(target.Label).Append("  (").Append(target.ProgramAsset.name).AppendLine(")");

            if (section == "summary")
            {
                var entryPoints = parsed.EntryPoints();
                var frameEvents = entryPoints.Where(IsPerFrameEntryPoint).ToArray();

                builder.Append("  entry points     : ").AppendLine(entryPoints.Count == 0 ? "none" : string.Join(", ", entryPoints));
                builder.Append("  public variables : ").AppendLine(parsed.PublicVariables().Count == 0 ? "none" : string.Join(", ", parsed.PublicVariables()));
                builder.Append("  methods          : ").AppendLine(parsed.Methods.Count.ToString());
                builder.Append("  externs          : ").Append(parsed.ExternCount).Append(" calls, ").Append(parsed.UniqueExterns().Count).AppendLine(" unique");

                if (frameEvents.Length > 0)
                {
                    builder.Append("  per-frame        : ").AppendLine(string.Join(", ", frameEvents));
                }

                return;
            }

            if (section == "exports")
            {
                foreach (var entryPoint in parsed.ExportedEntryPoints)
                {
                    builder.Append("  ").Append(entryPoint).AppendLine("  (entry point)");
                }

                foreach (var variable in parsed.ExportedVariables)
                {
                    builder.Append("  ").Append(variable).AppendLine("  (public variable)");
                }

                if (parsed.ExportedEntryPoints.Count == 0 && parsed.ExportedVariables.Count == 0)
                {
                    builder.AppendLine("  nothing exported");
                }

                return;
            }

            if (section == "symbols")
            {
                foreach (var symbol in parsed.Symbols)
                {
                    builder.Append("  ").AppendLine(symbol);
                }

                return;
            }

            if (section == "methods")
            {
                foreach (var method in parsed.Methods)
                {
                    List<string> externs;
                    var count = parsed.ExternsByMethod.TryGetValue(method, out externs) ? externs.Count : 0;
                    builder.Append("  ").Append(method).Append("  externs ").AppendLine(count.ToString());
                }

                if (parsed.Methods.Count == 0)
                {
                    builder.AppendLine("  no methods compiled");
                }

                return;
            }

            // externs, grouped under the method that calls them, which is what makes this readable when a
            // behaviour runs but does not do what its source appears to say.
            var wrote = false;
            foreach (var method in parsed.ExternsByMethod.Keys)
            {
                var externs = parsed.ExternsByMethod[method];
                if (externs.Count == 0)
                {
                    continue;
                }

                builder.Append("  ").AppendLine(method);
                foreach (var name in externs)
                {
                    builder.Append("    ").AppendLine(name);
                }

                wrote = true;
            }

            if (!wrote)
            {
                builder.AppendLine("  no extern calls");
            }
        }

        #endregion

        #region Udon Program Parsing

        /// <summary>One program to report on, and the label it was resolved from.</summary>
        private sealed class UdonProgramTarget
        {
            /// <summary>Scene path or asset path the caller asked about.</summary>
            public string Label;

            /// <summary>The UdonSharpProgramAsset itself, held as a plain object to avoid a package dependency.</summary>
            public UnityEngine.Object ProgramAsset;
        }

        /// <summary>A parsed assembly listing, split into the parts worth reporting.</summary>
        private sealed class UdonProgramListing
        {
            /// <summary>
            /// Entry points, exported from the CODE section. These are the only names Udon can raise as an
            /// event; a method that exists without one is unreachable.
            /// </summary>
            public readonly List<string> ExportedEntryPoints = new List<string>();

            /// <summary>Public variables, exported from the DATA section.</summary>
            public readonly List<string> ExportedVariables = new List<string>();

            /// <summary>Data table lines, each naming a symbol and its type.</summary>
            public readonly List<string> Symbols = new List<string>();

            /// <summary>Names from the data table.</summary>
            public readonly HashSet<string> SymbolNames = new HashSet<string>();

            /// <summary>Every method the compile emitted, in listing order.</summary>
            public readonly List<string> Methods = new List<string>();

            /// <summary>Extern calls grouped under the method they appear in.</summary>
            public readonly Dictionary<string, List<string>> ExternsByMethod = new Dictionary<string, List<string>>();

            /// <summary>How many extern calls the program makes in total.</summary>
            public int ExternCount;

            /// <summary>
            /// The entry points Udon can raise on this program.
            /// </summary>
            /// <returns>Entry point names.</returns>
            public List<string> EntryPoints()
            {
                return ExportedEntryPoints;
            }

            /// <summary>
            /// The behaviour's public variables.
            /// </summary>
            /// <returns>Public variable names.</returns>
            public List<string> PublicVariables()
            {
                return ExportedVariables;
            }

            /// <summary>
            /// Distinct extern signatures across the whole program.
            /// </summary>
            /// <returns>Unique extern names.</returns>
            public List<string> UniqueExterns()
            {
                return ExternsByMethod.Values.SelectMany(v => v).Distinct().ToList();
            }
        }

        /// <summary>
        /// Splits an assembly listing into its data table, its methods, and the externs each method calls.
        /// </summary>
        /// <param name="listing">Raw assembly text.</param>
        /// <returns>The parsed listing.</returns>
        private static UdonProgramListing ParseUdonAssembly(string listing)
        {
            var parsed = new UdonProgramListing();
            var inData = false;
            var inCode = false;

            // Code before the first method comment belongs to the program's own preamble rather than to
            // anything the author wrote, so it is named rather than attributed to the first method.
            var currentMethod = "(program preamble)";

            foreach (var raw in listing.Split('\n'))
            {
                var line = raw.Trim();

                if (line == ".data_start") { inData = true; continue; }
                if (line == ".data_end") { inData = false; continue; }
                if (line == ".code_start") { inCode = true; continue; }
                if (line == ".code_end") { inCode = false; continue; }

                if (inData)
                {
                    // The two sections export different things: a name exported from the data table is a
                    // public variable, while one exported from the code section is an entry point. Reading
                    // only the data section finds every variable and no events at all.
                    if (line.StartsWith(".export ", StringComparison.Ordinal))
                    {
                        parsed.ExportedVariables.Add(line.Substring(8).Trim().TrimEnd(',', ':'));
                        continue;
                    }

                    var colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        parsed.Symbols.Add(line);
                        parsed.SymbolNames.Add(line.Substring(0, colon).Trim());
                    }

                    continue;
                }

                if (!inCode)
                {
                    continue;
                }

                if (line.StartsWith(".export ", StringComparison.Ordinal))
                {
                    parsed.ExportedEntryPoints.Add(line.Substring(8).Trim().TrimEnd(',', ':'));
                    continue;
                }

                if (line.StartsWith("#", StringComparison.Ordinal))
                {
                    var name = line.TrimStart('#').Trim();

                    // The emitter brackets each method name with empty comment lines, so only a line that
                    // actually carries a signature marks a new method. A "Calling X()" comment marks a CALL
                    // SITE inside the method being read, so it must not open a new one, or its caller's
                    // externs get filed under the callee.
                    if (name.StartsWith("Calling ", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (name.Length > 0 && name.EndsWith(")", StringComparison.Ordinal))
                    {
                        currentMethod = name;
                        if (!parsed.Methods.Contains(name))
                        {
                            parsed.Methods.Add(name);
                        }
                    }

                    continue;
                }

                if (!line.StartsWith("EXTERN", StringComparison.Ordinal))
                {
                    continue;
                }

                var first = line.IndexOf('"');
                var last = line.LastIndexOf('"');
                if (first < 0 || last <= first)
                {
                    continue;
                }

                var signature = line.Substring(first + 1, last - first - 1);
                List<string> bucket;
                if (!parsed.ExternsByMethod.TryGetValue(currentMethod, out bucket))
                {
                    bucket = new List<string>();
                    parsed.ExternsByMethod[currentMethod] = bucket;
                }

                bucket.Add(signature);
                parsed.ExternCount++;
            }

            return parsed;
        }

        /// <summary>
        /// Reports whether an entry point costs the behaviour something on every frame.
        /// </summary>
        /// <param name="entryPoint">Exported entry point name.</param>
        /// <returns>True when Udon raises it per frame.</returns>
        private static bool IsPerFrameEntryPoint(string entryPoint)
        {
            return UdonPerFrameEntryPoints.Contains(entryPoint);
        }

        #endregion

        #region Udon Program Resolution

        /// <summary>
        /// Resolves what the caller named into the programs it refers to.
        /// </summary>
        /// <param name="target">Scene object, program asset path, or behaviour source path.</param>
        /// <param name="error">Set to the reason nothing could be resolved, otherwise null.</param>
        /// <returns>The programs to report on.</returns>
        private static List<UdonProgramTarget> ResolveUdonPrograms(string target, out string error)
        {
            error = null;
            var results = new List<UdonProgramTarget>();

            string cacheError;
            if (!UdonAssemblyReaderAvailable(out cacheError))
            {
                error = cacheError;
                return results;
            }

            var cleaned = target.Trim('"');

            if (cleaned.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(cleaned);
                if (asset == null)
                {
                    error = "No asset at " + cleaned + ".";
                    return results;
                }

                // A .cs was named rather than its program asset, so find the program built from it.
                if (asset is MonoScript)
                {
                    var owner = FindProgramAssetForScript(cleaned);
                    if (owner == null)
                    {
                        error = "No UdonSharpProgramAsset is built from " + cleaned + ".";
                        return results;
                    }

                    results.Add(new UdonProgramTarget { Label = cleaned, ProgramAsset = owner });
                    return results;
                }

                if (!IsUdonSharpProgramAsset(asset))
                {
                    error = cleaned + " is a " + asset.GetType().Name + ", not an UdonSharpProgramAsset.";
                    return results;
                }

                results.Add(new UdonProgramTarget { Label = cleaned, ProgramAsset = asset });
                return results;
            }

            GameObject gameObject;
            var findError = TryFindGameObject(cleaned, out gameObject);
            if (findError != null)
            {
                error = findError;
                return results;
            }

            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null || !IsUdonBehaviour(component))
                {
                    continue;
                }

                var programAsset = GetUdonProgramSource(component);
                if (programAsset == null)
                {
                    continue;
                }

                results.Add(new UdonProgramTarget
                {
                    Label = GetGameObjectPath(gameObject),
                    ProgramAsset = programAsset,
                });
            }

            if (results.Count == 0)
            {
                error = "No UdonSharp behaviour with a program on " + cleaned + ".";
            }

            return results;
        }

        /// <summary>
        /// Finds the program asset compiled from a behaviour source file.
        /// </summary>
        /// <param name="scriptPath">Asset path of the .cs file.</param>
        /// <returns>The program asset, or null when nothing is built from it.</returns>
        private static UnityEngine.Object FindProgramAssetForScript(string scriptPath)
        {
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(scriptPath);
            if (script == null)
            {
                return null;
            }

            foreach (var guid in AssetDatabase.FindAssets("t:UdonSharpProgramAsset"))
            {
                var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(AssetDatabase.GUIDToAssetPath(guid));
                if (asset == null)
                {
                    continue;
                }

                var field = asset.GetType().GetField("sourceCsScript",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && ReferenceEquals(field.GetValue(asset), script))
                {
                    return asset;
                }
            }

            return null;
        }

        /// <summary>
        /// Walks one scene for components that are UdonBehaviours.
        /// </summary>
        /// <param name="scene">The scene to walk; an unloaded scene yields nothing.</param>
        /// <returns>Every UdonBehaviour in that scene, inactive objects included.</returns>
        private static IEnumerable<Component> EnumerateSceneUdonBehaviours(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded)
            {
                yield break;
            }

            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var component in root.GetComponentsInChildren<Component>(true))
                {
                    if (component != null && IsUdonBehaviour(component))
                    {
                        yield return component;
                    }
                }
            }
        }

        #endregion

        #region Udon Reflection Access

        /// <summary>Cached UdonSharpEditorCache instance, resolved on first use.</summary>
        private static object _udonSharpCache;

        /// <summary>Cached GetUASMStr method, resolved on first use.</summary>
        private static MethodInfo _udonSharpGetUasm;

        /// <summary>True once the reflection lookup has been attempted, successfully or not.</summary>
        private static bool _udonSharpCacheResolved;

        /// <summary>
        /// Reports whether the assembly listing can be read at all, so every caller fails the same way.
        /// </summary>
        /// <param name="error">Set to the reason it cannot, otherwise null.</param>
        /// <returns>True when listings can be read.</returns>
        private static bool UdonAssemblyReaderAvailable(out string error)
        {
            error = null;

            if (!_udonSharpCacheResolved)
            {
                _udonSharpCacheResolved = true;

                var editorAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "UdonSharp.Editor");
                var cacheType = editorAssembly == null ? null : editorAssembly.GetType("UdonSharp.UdonSharpEditorCache");

                if (cacheType != null)
                {
                    var instance = cacheType.GetProperty("Instance",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    _udonSharpCache = instance == null ? null : instance.GetValue(null);
                    _udonSharpGetUasm = cacheType.GetMethod("GetUASMStr",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                }
            }

            if (_udonSharpCache == null || _udonSharpGetUasm == null)
            {
                error = "UdonSharp's assembly listing is unreachable: UdonSharp.UdonSharpEditorCache.GetUASMStr did not resolve. "
                      + "Either the package is absent, or its internal shape has moved.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reads one program's assembly listing.
        /// </summary>
        /// <param name="programAsset">The UdonSharpProgramAsset to read.</param>
        /// <returns>The listing, or null when the program has never been compiled.</returns>
        private static string ReadUdonAssembly(UnityEngine.Object programAsset)
        {
            string error;
            if (programAsset == null || !UdonAssemblyReaderAvailable(out error))
            {
                return null;
            }

            var listing = _udonSharpGetUasm.Invoke(_udonSharpCache, new object[] { programAsset }) as string;
            return string.IsNullOrEmpty(listing) ? null : listing;
        }

        /// <summary>
        /// Reports whether a component is a VRChat UdonBehaviour, without a compile-time dependency on it.
        /// </summary>
        /// <param name="component">Component to test.</param>
        /// <returns>True when it is an UdonBehaviour.</returns>
        private static bool IsUdonBehaviour(Component component)
        {
            for (var type = component.GetType(); type != null; type = type.BaseType)
            {
                if (type.FullName == "VRC.Udon.UdonBehaviour")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reports whether an asset is an UdonSharpProgramAsset, without depending on the package.
        /// </summary>
        /// <param name="asset">Asset to test.</param>
        /// <returns>True when it is an UdonSharp program asset.</returns>
        private static bool IsUdonSharpProgramAsset(UnityEngine.Object asset)
        {
            for (var type = asset.GetType(); type != null; type = type.BaseType)
            {
                if (type.FullName == "UdonSharp.UdonSharpProgramAsset")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Reads an UdonBehaviour's program source, keeping only the UdonSharp ones.
        /// </summary>
        /// <param name="behaviour">The UdonBehaviour to read.</param>
        /// <returns>Its UdonSharpProgramAsset, or null when it has none or is a graph program.</returns>
        private static UnityEngine.Object GetUdonProgramSource(Component behaviour)
        {
            const BindingFlags memberFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            UnityEngine.Object source = null;

            // programSource is a serialized FIELD on UdonBehaviour, not a property. Looking only for a
            // property finds nothing and reports every scene as holding no behaviours, so the field is
            // tried first and the property kept as a fallback in case a later SDK wraps it.
            var field = behaviour.GetType().GetField("programSource", memberFlags);
            if (field != null)
            {
                source = field.GetValue(behaviour) as UnityEngine.Object;
            }
            else
            {
                var property = behaviour.GetType().GetProperty("programSource", memberFlags);
                if (property != null)
                {
                    source = property.GetValue(behaviour, null) as UnityEngine.Object;
                }
            }

            return source != null && IsUdonSharpProgramAsset(source) ? source : null;
        }

        /// <summary>
        /// Reads every distinct node-definition full name from the SDK's UdonEditorManager, by
        /// reflection so the bridge keeps compiling in a project without the SDK.
        /// </summary>
        /// <param name="error">Set to the reason the registry is unreachable, otherwise null.</param>
        /// <returns>Definition names, in registry order; empty when unreachable.</returns>
        private static List<string> ReadUdonNodeDefinitionNames(out string error)
        {
            error = null;
            var names = new List<string>();

            var managerType = Type.GetType("VRC.Udon.Editor.UdonEditorManager, VRC.Udon.Editor", false);
            var instanceProperty = managerType == null
                ? null
                : managerType.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            var instance = instanceProperty == null ? null : instanceProperty.GetValue(null);
            var getDefinitions = managerType == null ? null : managerType.GetMethod("GetNodeDefinitions", Type.EmptyTypes);

            if (instance == null || getDefinitions == null)
            {
                error = "Udon's node registry is unreachable: VRC.Udon.Editor.UdonEditorManager.Instance.GetNodeDefinitions() "
                      + "did not resolve. Either the SDK is absent, or its internal shape has moved.";
                return names;
            }

            // The registry holds tens of thousands of definitions and the manager caches them itself,
            // so a fresh walk per call (about 50 ms) beats keeping a second copy of the list alive.
            var definitions = getDefinitions.Invoke(instance, null) as IEnumerable;
            if (definitions == null)
            {
                error = "Udon's node registry returned nothing readable from GetNodeDefinitions().";
                return names;
            }

            // The registry registers some names more than once (about 110 of 35323 on SDK
            // 3.10.5-beta.1). One name is one answer for exposure purposes, so duplicates are folded
            // here and every mode of the command reports the same distinct count.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            FieldInfo fullNameField = null;
            foreach (var definition in definitions)
            {
                if (definition == null)
                {
                    continue;
                }

                if (fullNameField == null)
                {
                    fullNameField = definition.GetType().GetField("fullName");
                }

                var fullName = fullNameField == null ? null : fullNameField.GetValue(definition) as string;
                if (!string.IsNullOrEmpty(fullName) && seen.Add(fullName))
                {
                    names.Add(fullName);
                }
            }

            return names;
        }

        #endregion
            /// <summary>
        /// Routes the Udon verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchUdon(string verb, string command, string[] parts)
        {
            if (verb == "reflection-method-find")
            {
                return ReflectionMethodFindCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "type-get-json-schema")
            {
                return TypeGetJsonSchemaCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "type-find")
            {
                return TypeFindCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "udon-program")
            {
                return UdonProgramCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "udon-program-audit")
            {
                return UdonProgramAuditCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "udon-exposure")
            {
                return UdonExposureCommand(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
