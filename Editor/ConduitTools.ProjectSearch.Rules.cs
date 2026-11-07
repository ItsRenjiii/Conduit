/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Glob and ignore-file rules used by dependency-free project search.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Rule models

        /// <summary>One queued directory with inherited ignore rules and its scoped depth.</summary>
        private sealed class ProjectSearchDirectory
        {
            /// <summary>Absolute directory path.</summary>
            internal string FullPath;

            /// <summary>Project-relative forward-slash path.</summary>
            internal string RelativePath;

            /// <summary>Depth below the caller's requested root.</summary>
            internal int Depth;

            /// <summary>Ordered ignore rules inherited from ancestor directories.</summary>
            internal List<ProjectSearchPathRule> IgnoreRules;
        }

        /// <summary>One ordered glob or ignore-file rule.</summary>
        private sealed class ProjectSearchPathRule
        {
            /// <summary>Compiled project-relative path expression.</summary>
            internal Regex Expression;

            /// <summary>True when a match includes or re-includes the path.</summary>
            internal bool Include;

            /// <summary>True when this rule applies only to directories and their descendants.</summary>
            internal bool DirectoryOnly;

            /// <summary>Whether a directory match also applies to its descendants.</summary>
            internal bool MatchAncestors;

            /// <summary>Human-readable source used in parse failures.</summary>
            internal string Source;

            /// <summary>Tests the path itself and, for directory rules, every ancestor directory.</summary>
            internal bool Matches(string relativePath, bool isDirectory)
            {
                if (Expression.IsMatch(relativePath) && (!DirectoryOnly || isDirectory))
                {
                    return true;
                }

                if (!MatchAncestors) return false;

                int slash = relativePath.LastIndexOf('/');
                while (slash > 0)
                {
                    string ancestor = relativePath.Substring(0, slash);
                    if (Expression.IsMatch(ancestor))
                    {
                        return true;
                    }

                    slash = ancestor.LastIndexOf('/');
                }

                return false;
            }
        }

        #endregion

        #region Explicit globs

        /// <summary>Compiles caller globs and reports whether any positive include rule exists.</summary>
        private static string TryCompileProjectSearchGlobs(
            string[] rawGlobs,
            out List<ProjectSearchPathRule> rules,
            out bool hasPositive)
        {
            rules = new List<ProjectSearchPathRule>();
            hasPositive = false;
            if (rawGlobs == null) return null;

            for (int i = 0; i < rawGlobs.Length; i++)
            {
                string raw = rawGlobs[i];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    return "project-search globs cannot contain an empty value.";
                }

                bool include = true;
                string pattern = raw.Trim().Replace('\\', '/');
                if (pattern[0] == '!')
                {
                    include = false;
                    pattern = pattern.Substring(1);
                }
                else
                {
                    hasPositive = true;
                }

                if (pattern.Length == 0)
                {
                    return "project-search glob '" + raw + "' has no pattern.";
                }

                bool directoryOnly = pattern.EndsWith("/", StringComparison.Ordinal);
                if (directoryOnly) pattern = pattern.TrimEnd('/');

                string expression;
                string error = TryBuildProjectSearchGlobRegex(
                    pattern,
                    string.Empty,
                    pattern.StartsWith("/", StringComparison.Ordinal),
                    out expression);
                if (error != null)
                {
                    return "Invalid project-search glob '" + raw + "': " + error;
                }

                rules.Add(new ProjectSearchPathRule
                {
                    Expression = new Regex(
                        expression,
                        ProjectSearchPathRegexOptions(),
                        TimeSpan.FromMilliseconds(250)),
                    Include = include,
                    DirectoryOnly = directoryOnly,
                    MatchAncestors = directoryOnly,
                    Source = "options.globs[" + i + "]",
                });
            }

            return null;
        }

        /// <summary>Applies ordered caller globs to one project-relative path.</summary>
        private static bool ProjectSearchGlobsAdmit(
            string relativePath,
            bool isDirectory,
            List<ProjectSearchPathRule> rules,
            bool hasPositive)
        {
            bool admitted = !hasPositive;
            for (int i = 0; i < rules.Count; i++)
            {
                ProjectSearchPathRule rule = rules[i];
                if (rule.Matches(relativePath, isDirectory))
                {
                    admitted = rule.Include;
                }
            }

            return admitted;
        }

        #endregion

        #region Ignore files

        /// <summary>Reads ignore rules in one directory, preserving inherited ordering.</summary>
        private static List<ProjectSearchPathRule> LoadProjectSearchIgnoreRules(
            string directoryFullPath,
            string directoryRelativePath,
            List<ProjectSearchPathRule> inherited,
            ConcurrentQueue<string> errors)
        {
            List<ProjectSearchPathRule> combined = inherited;
            string[] names = { ".gitignore", ".ignore", ".rgignore" };
            for (int i = 0; i < names.Length; i++)
            {
                string path = Path.Combine(directoryFullPath, names[i]);
                if (!File.Exists(path)) continue;

                if (ReferenceEquals(combined, inherited))
                {
                    combined = new List<ProjectSearchPathRule>(inherited);
                }

                try
                {
                    string[] lines = File.ReadAllLines(path, Encoding.UTF8);
                    for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
                    {
                        string error;
                        ProjectSearchPathRule rule = ParseProjectSearchIgnoreRule(
                            lines[lineIndex],
                            directoryRelativePath,
                            path + ":" + (lineIndex + 1),
                            out error);
                        if (error != null)
                        {
                            AddProjectSearchError(errors, path, new InvalidDataException(error));
                            continue;
                        }

                        if (rule != null) combined.Add(rule);
                    }
                }
                catch (Exception ex)
                {
                    AddProjectSearchError(errors, path, ex);
                }
            }

            return combined;
        }

        /// <summary>Parses one gitignore-style rule into a project-relative regular expression.</summary>
        private static ProjectSearchPathRule ParseProjectSearchIgnoreRule(
            string rawLine,
            string baseRelativePath,
            string source,
            out string error)
        {
            error = null;
            if (rawLine == null) return null;

            string line = rawLine.TrimEnd();
            if (line.Length == 0 || line[0] == '#') return null;

            bool include = false;
            if (line[0] == '!')
            {
                include = true;
                line = line.Substring(1);
            }
            else if (line.StartsWith("\\!", StringComparison.Ordinal) ||
                line.StartsWith("\\#", StringComparison.Ordinal))
            {
                line = line.Substring(1);
            }

            if (line.Length == 0) return null;
            line = line.Replace('\\', '/');

            bool directoryOnly = line.EndsWith("/", StringComparison.Ordinal);
            if (directoryOnly) line = line.TrimEnd('/');

            bool anchored = line.StartsWith("/", StringComparison.Ordinal);
            string expression;
            error = TryBuildProjectSearchGlobRegex(line, baseRelativePath, anchored, out expression);
            if (error != null) return null;

            return new ProjectSearchPathRule
            {
                Expression = new Regex(
                    expression,
                    ProjectSearchPathRegexOptions(),
                    TimeSpan.FromMilliseconds(250)),
                Include = include,
                DirectoryOnly = directoryOnly,
                MatchAncestors = true,
                Source = source,
            };
        }

        /// <summary>Applies ordered ignore rules, where the final matching rule wins.</summary>
        private static bool ProjectSearchIgnoreAdmits(
            string relativePath,
            bool isDirectory,
            List<ProjectSearchPathRule> rules)
        {
            bool admitted = true;
            for (int i = 0; i < rules.Count; i++)
            {
                ProjectSearchPathRule rule = rules[i];
                if (rule.Matches(relativePath, isDirectory))
                {
                    admitted = rule.Include;
                }
            }

            return admitted;
        }

        #endregion

        #region Glob compiler

        /// <summary>Uses filesystem case rules for project-relative path matching.</summary>
        private static RegexOptions ProjectSearchPathRegexOptions()
        {
            RegexOptions options = RegexOptions.CultureInvariant;
            if (Path.DirectorySeparatorChar == '\\') options |= RegexOptions.IgnoreCase;
            return options;
        }

        /// <summary>Builds a bounded regex for one forward-slash glob pattern.</summary>
        private static string TryBuildProjectSearchGlobRegex(
            string rawPattern,
            string baseRelativePath,
            bool anchored,
            out string expression)
        {
            expression = null;
            string pattern = rawPattern.Trim();
            while (pattern.StartsWith("/", StringComparison.Ordinal))
            {
                pattern = pattern.Substring(1);
            }

            if (pattern.Length == 0)
            {
                return "pattern is empty.";
            }

            string basePath = baseRelativePath == "."
                ? string.Empty
                : baseRelativePath.Trim('/');

            bool containsSlash = pattern.IndexOf('/') >= 0;
            var builder = new StringBuilder();
            builder.Append("^");
            if (basePath.Length > 0)
            {
                builder.Append(Regex.Escape(basePath)).Append("/");
            }

            if (!anchored && !containsSlash)
            {
                builder.Append("(?:.*/)?");
            }

            string translateError = AppendProjectSearchGlobPattern(builder, pattern);
            if (translateError != null) return translateError;
            builder.Append("$");
            expression = builder.ToString();
            return null;
        }

        /// <summary>Translates glob tokens, including double-star and brace alternatives.</summary>
        private static string AppendProjectSearchGlobPattern(StringBuilder builder, string pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                char value = pattern[i];
                if (value == '*')
                {
                    bool doubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                    if (doubleStar)
                    {
                        i++;
                        bool slashAfter = i + 1 < pattern.Length && pattern[i + 1] == '/';
                        if (slashAfter)
                        {
                            i++;
                            builder.Append("(?:.*/)?");
                        }
                        else
                        {
                            builder.Append(".*");
                        }
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    continue;
                }

                if (value == '?')
                {
                    builder.Append("[^/]");
                    continue;
                }

                if (value == '[')
                {
                    int end = pattern.IndexOf(']', i + 1);
                    if (end < 0) return "unterminated character class.";
                    string characterClass = pattern.Substring(i + 1, end - i - 1);
                    if (characterClass.StartsWith("!", StringComparison.Ordinal))
                    {
                        characterClass = "^" + characterClass.Substring(1);
                    }

                    builder.Append("[").Append(characterClass).Append("]");
                    i = end;
                    continue;
                }

                if (value == '{')
                {
                    int end = pattern.IndexOf('}', i + 1);
                    if (end < 0) return "unterminated brace alternative.";
                    string[] alternatives = pattern.Substring(i + 1, end - i - 1).Split(',');
                    if (alternatives.Length < 2) return "brace alternative needs at least two values.";

                    builder.Append("(?:");
                    for (int alternativeIndex = 0; alternativeIndex < alternatives.Length; alternativeIndex++)
                    {
                        if (alternativeIndex > 0) builder.Append("|");
                        builder.Append(Regex.Escape(alternatives[alternativeIndex]));
                    }

                    builder.Append(")");
                    i = end;
                    continue;
                }

                if (value == '/')
                {
                    builder.Append("/");
                    continue;
                }

                builder.Append(Regex.Escape(value.ToString()));
            }

            return null;
        }

        #endregion
    }
}
