/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Dependency-free project text search that runs on the request thread and bounded worker tasks.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Limits

        /// <summary>Read buffer retained by each active worker.</summary>
        private const int PROJECT_SEARCH_BUFFER_BYTES = 128 * 1024;

        /// <summary>Default ceiling for one searchable file.</summary>
        private const long PROJECT_SEARCH_DEFAULT_MAX_FILE_BYTES = 16L * 1024L * 1024L;

        /// <summary>Hard ceiling for one searchable file.</summary>
        private const long PROJECT_SEARCH_MAX_FILE_BYTES = 64L * 1024L * 1024L;

        /// <summary>Default number of returned matches.</summary>
        private const int PROJECT_SEARCH_DEFAULT_MAX_RESULTS = 200;

        /// <summary>Hard ceiling for returned matches.</summary>
        private const int PROJECT_SEARCH_MAX_RESULTS = 5000;

        /// <summary>Hard ceiling for matching paths retained by a complete count operation.</summary>
        private const int PROJECT_SEARCH_MAX_CANDIDATE_FILES = 100000;

        /// <summary>Default wall-clock limit for one search.</summary>
        private const int PROJECT_SEARCH_DEFAULT_TIMEOUT_MS = 10000;

        /// <summary>Hard wall-clock limit for one search.</summary>
        private const int PROJECT_SEARCH_MAX_TIMEOUT_MS = 60000;

        /// <summary>Largest explicit worker count accepted from a caller.</summary>
        private const int PROJECT_SEARCH_MAX_THREADS = 64;

        /// <summary>Automatic worker ceiling, matching the current ripgrep directory-search default.</summary>
        private const int PROJECT_SEARCH_AUTO_THREAD_CEILING = 12;

        /// <summary>Longest source-line excerpt admitted to a tool response.</summary>
        private const int PROJECT_SEARCH_MAX_EXCERPT_CHARS = 400;

        #endregion

        #region Defaults

        /// <summary>Project folders searched when the caller does not provide paths.</summary>
        private static readonly string[] ProjectSearchDefaultPaths =
        {
            "Assets",
            "Packages",
            "ProjectSettings",
        };

        /// <summary>Text-like extensions searched by default.</summary>
        private static readonly string[] ProjectSearchDefaultExtensions =
        {
            ".asmdef", ".asmref", ".cginc", ".compute", ".cs", ".hlsl", ".json", ".md",
            ".shader", ".txt", ".toml", ".uss", ".uxml", ".xml", ".yaml", ".yml",
        };

        /// <summary>Generated and version-control folders never descended into.</summary>
        private static readonly HashSet<string> ProjectSearchExcludedDirectories = new HashSet<string>(
            new[] { ".git", ".svn", "Library", "Temp", "Logs", "obj" },
            StringComparer.OrdinalIgnoreCase);

        #endregion

        #region Command entry

        /// <summary>
        /// Handles request-thread commands before they can queue behind Unity's main thread.
        /// </summary>
        /// <param name="verb">Effective bridge verb.</param>
        /// <param name="command">Complete command line.</param>
        /// <param name="result">Command result when handled.</param>
        /// <returns>True when this method handled the verb.</returns>
        internal static bool TryExecuteThreadFreeCommand(string verb, string command, out string result)
        {
            if (TryExecuteThreadFree(verb, out result))
            {
                return true;
            }

            if (verb == "project-search")
            {
                result = ProjectSearchCommand(GetCommandRemainder(command, verb));
                return true;
            }

            if (verb == "project-files")
            {
                result = ProjectFilesCommand(GetCommandRemainder(command, verb));
                return true;
            }

            result = null;
            return false;
        }

        /// <summary>Searches bounded project files without touching Unity APIs or its main thread.</summary>
        /// <param name="args">Pattern, optionally inside one pair of quotes, then optional JSON settings after the last pipe.</param>
        /// <returns>Compact JSON containing matches, measured totals, and bounded warnings.</returns>
        private static string ProjectSearchCommand(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return "Usage: project-search <pattern> [| optionsJson]";
            }

            string patternText = args.Trim();
            string optionsText = string.Empty;
            int optionsSeparator = patternText.LastIndexOf('|');
            if (optionsSeparator >= 0)
            {
                string possibleOptions = patternText.Substring(optionsSeparator + 1).Trim();
                if (possibleOptions.StartsWith("{", StringComparison.Ordinal))
                {
                    optionsText = possibleOptions;
                    patternText = patternText.Substring(0, optionsSeparator).Trim();
                }
            }

            // One enclosing pair of quotes is framing, never part of the pattern. The typed builder always
            // sends exactly one pair, so a pattern that itself starts or ends with a quote or with whitespace
            // arrives intact.
            string pattern = patternText;
            if (pattern.Length >= 2 && pattern[0] == '"' && pattern[pattern.Length - 1] == '"')
            {
                pattern = pattern.Substring(1, pattern.Length - 2);
            }

            if (pattern.Length == 0)
            {
                return "project-search pattern cannot be empty.";
            }

            ProjectSearchSettings settings;
            string settingsError = TryParseProjectSearchSettings(optionsText, out settings);
            if (settingsError != null)
            {
                return settingsError;
            }

            if (settings.SmartCase && !settings.CaseSensitiveExplicit)
            {
                settings.CaseSensitive = ProjectSearchPatternHasUppercase(pattern);
            }

            bool requiresExpression = settings.Regex ||
                settings.Word ||
                settings.WholeLine ||
                settings.Multiline;
            Regex expression = null;
            if (requiresExpression)
            {
                try
                {
                    string expressionPattern = settings.Regex ? pattern : Regex.Escape(pattern);
                    if (settings.Word)
                    {
                        expressionPattern = "(?<!\\w)(?:" + expressionPattern + ")(?!\\w)";
                    }

                    if (settings.WholeLine)
                    {
                        expressionPattern = "^(?:" + expressionPattern + ")$";
                    }

                    RegexOptions options = RegexOptions.CultureInvariant;
                    if (!settings.CaseSensitive) options |= RegexOptions.IgnoreCase;
                    if (settings.Multiline || settings.WholeLine) options |= RegexOptions.Multiline;
                    if (settings.DotAll) options |= RegexOptions.Singleline;
                    expression = new Regex(
                        expressionPattern,
                        options,
                        TimeSpan.FromMilliseconds(Math.Min(1000, settings.TimeoutMs)));
                }
                catch (Exception ex)
                {
                    return "Invalid project-search pattern: " + ex.Message;
                }
            }

            List<ProjectSearchRoot> roots;
            string rootError = TryResolveProjectSearchRoots(settings.Paths, out roots);
            if (rootError != null)
            {
                return rootError;
            }

            int workerCount = ResolveProjectSearchWorkerCount(settings.Threads, roots.Count);
            bool utf8FastPath = settings.EncodingName == "auto" ||
                settings.EncodingName == "utf8" ||
                settings.EncodingName == "utf-8";
            byte[] literal = requiresExpression || !settings.CaseSensitive || !utf8FastPath
                ? null
                : Encoding.UTF8.GetBytes(pattern);
            int[] shifts = literal == null ? null : BuildProjectSearchShiftTable(literal);

            return ExecuteProjectSearch(
                pattern,
                settings,
                roots,
                workerCount,
                literal,
                shifts,
                expression);
        }

        /// <summary>Lists bounded project files without reading their contents.</summary>
        /// <param name="args">Optional JSON settings, with or without a leading pipe.</param>
        /// <returns>Compact JSON containing selected file paths and measured totals.</returns>
        private static string ProjectFilesCommand(string args)
        {
            string optionsText = string.IsNullOrWhiteSpace(args) ? string.Empty : args.Trim();
            if (optionsText.StartsWith("|", StringComparison.Ordinal))
            {
                optionsText = optionsText.Substring(1).Trim();
            }

            ProjectSearchSettings settings;
            string settingsError = TryParseProjectSearchSettings(optionsText, out settings);
            if (settingsError != null) return settingsError;

            settings.ListFiles = true;
            settings.Output = ProjectSearchOutputMode.Files;
            settings.BeforeContext = 0;
            settings.AfterContext = 0;

            List<ProjectSearchRoot> roots;
            string rootError = TryResolveProjectSearchRoots(settings.Paths, out roots);
            if (rootError != null) return rootError;

            int workerCount = ResolveProjectSearchWorkerCount(settings.Threads, roots.Count);
            return ExecuteProjectSearch(
                string.Empty,
                settings,
                roots,
                workerCount,
                null,
                null,
                null);
        }

        /// <summary>Reports whether a pattern contains an uppercase Unicode character.</summary>
        private static bool ProjectSearchPatternHasUppercase(string pattern)
        {
            for (int i = 0; i < pattern.Length; i++)
            {
                if (char.IsUpper(pattern[i])) return true;
            }

            return false;
        }

        #endregion

        #region Settings

        /// <summary>Shape of the result payload requested by a caller.</summary>
        private enum ProjectSearchOutputMode
        {
            /// <summary>Return one row per text match.</summary>
            Matches,

            /// <summary>Return each matching file once.</summary>
            Files,

            /// <summary>Return total and per-file match counts.</summary>
            Count,
        }

        /// <summary>Validated options for one project search.</summary>
        private sealed class ProjectSearchSettings
        {
            /// <summary>Project-relative roots or files.</summary>
            internal string[] Paths;

            /// <summary>Admitted lower-case file extensions, including their leading dots.</summary>
            internal HashSet<string> Extensions;

            /// <summary>Whether regular-expression matching is enabled.</summary>
            internal bool Regex;

            /// <summary>Whether text comparisons preserve case.</summary>
            internal bool CaseSensitive;

            /// <summary>Whether case sensitivity was explicitly supplied.</summary>
            internal bool CaseSensitiveExplicit;

            /// <summary>Whether lowercase patterns select case-insensitive matching.</summary>
            internal bool SmartCase;

            /// <summary>Whether matches must have non-word boundaries.</summary>
            internal bool Word;

            /// <summary>Whether a match must consume a complete line.</summary>
            internal bool WholeLine;

            /// <summary>Whether one expression may span line boundaries.</summary>
            internal bool Multiline;

            /// <summary>Whether a regex dot may match newline characters.</summary>
            internal bool DotAll;

            /// <summary>Requested result payload shape.</summary>
            internal ProjectSearchOutputMode Output;

            /// <summary>Context lines returned before a match.</summary>
            internal int BeforeContext;

            /// <summary>Context lines returned after a match.</summary>
            internal int AfterContext;

            /// <summary>Selected text decoder name.</summary>
            internal string EncodingName;

            /// <summary>Selected text decoder.</summary>
            internal Encoding TextEncoding;

            /// <summary>Whether NUL bytes are treated as searchable text.</summary>
            internal bool TreatBinaryAsText;

            /// <summary>Whether selected paths are returned without reading file contents.</summary>
            internal bool ListFiles;

            /// <summary>Whether Unity .meta files are admitted.</summary>
            internal bool IncludeMeta;

            /// <summary>Whether dot-prefixed files and directories are admitted.</summary>
            internal bool IncludeHidden;

            /// <summary>Whether hierarchical .gitignore, .ignore, and .rgignore files are applied.</summary>
            internal bool RespectIgnore;

            /// <summary>Maximum directory depth below each requested root.</summary>
            internal int MaxDepth;

            /// <summary>Ordered caller-supplied include and exclude globs.</summary>
            internal List<ProjectSearchPathRule> GlobRules;

            /// <summary>Whether caller globs contain a positive inclusion rule.</summary>
            internal bool HasPositiveGlobs;

            /// <summary>Requested workers, or zero for automatic selection.</summary>
            internal int Threads;

            /// <summary>Maximum returned match rows.</summary>
            internal int MaxResults;

            /// <summary>Maximum admitted file length.</summary>
            internal long MaxFileBytes;

            /// <summary>Maximum wall-clock search duration.</summary>
            internal int TimeoutMs;
        }

        /// <summary>Absolute search root paired with its project-relative display path.</summary>
        private sealed class ProjectSearchRoot
        {
            /// <summary>Absolute filesystem path.</summary>
            internal string FullPath;

            /// <summary>Project-relative path returned to the caller.</summary>
            internal string RelativePath;

            /// <summary>Whether this root names one file rather than a directory.</summary>
            internal bool IsFile;
        }

        /// <summary>Parses and bounds the optional project-search settings object.</summary>
        /// <param name="raw">Raw JSON object, or blank for defaults.</param>
        /// <param name="settings">Validated settings on success.</param>
        /// <returns>Null on success, or a specific refusal.</returns>
        private static string TryParseProjectSearchSettings(string raw, out ProjectSearchSettings settings)
        {
            settings = new ProjectSearchSettings
            {
                Paths = (string[])ProjectSearchDefaultPaths.Clone(),
                Extensions = new HashSet<string>(ProjectSearchDefaultExtensions, StringComparer.OrdinalIgnoreCase),
                Regex = false,
                CaseSensitive = true,
                CaseSensitiveExplicit = false,
                SmartCase = false,
                Word = false,
                WholeLine = false,
                Multiline = false,
                DotAll = false,
                Output = ProjectSearchOutputMode.Matches,
                BeforeContext = 0,
                AfterContext = 0,
                EncodingName = "auto",
                TextEncoding = new UTF8Encoding(false, false),
                TreatBinaryAsText = false,
                ListFiles = false,
                IncludeMeta = false,
                IncludeHidden = false,
                RespectIgnore = true,
                MaxDepth = int.MaxValue,
                GlobRules = new List<ProjectSearchPathRule>(),
                HasPositiveGlobs = false,
                Threads = 0,
                MaxResults = PROJECT_SEARCH_DEFAULT_MAX_RESULTS,
                MaxFileBytes = PROJECT_SEARCH_DEFAULT_MAX_FILE_BYTES,
                TimeoutMs = PROJECT_SEARCH_DEFAULT_TIMEOUT_MS,
            };

            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            JObject value;
            try
            {
                value = JObject.Parse(raw);
            }
            catch (Exception ex)
            {
                return "Invalid project-search options JSON: " + ex.Message;
            }

            var known = new HashSet<string>(
                new[]
                {
                    "paths", "extensions", "globs", "regex", "caseSensitive", "smartCase",
                    "word", "wholeLine", "multiline", "dotAll", "output", "context",
                    "beforeContext", "afterContext", "encoding", "text", "includeMeta",
                    "includeHidden", "respectIgnore", "maxDepth", "threads", "maxResults",
                    "maxFileBytes", "timeoutMs",
                },
                StringComparer.Ordinal);

            foreach (JProperty property in value.Properties())
            {
                if (!known.Contains(property.Name))
                {
                    return "Unknown project-search option '" + property.Name + "'.";
                }
            }

            string arrayError = TryReadProjectSearchStringArray(value, "paths", out string[] paths);
            if (arrayError != null) return arrayError;
            if (paths != null)
            {
                if (paths.Length == 0) return "project-search paths cannot be empty.";
                settings.Paths = paths;
            }

            arrayError = TryReadProjectSearchStringArray(value, "extensions", out string[] extensions);
            if (arrayError != null) return arrayError;
            if (extensions != null)
            {
                settings.Extensions.Clear();
                foreach (string extensionValue in extensions)
                {
                    string extension = extensionValue.Trim();
                    if (extension.Length == 0) return "project-search extensions cannot contain an empty value.";
                    if (extension == "*")
                    {
                        settings.Extensions.Clear();
                        break;
                    }

                    if (!extension.StartsWith(".", StringComparison.Ordinal)) extension = "." + extension;
                    settings.Extensions.Add(extension);
                }
            }

            arrayError = TryReadProjectSearchStringArray(value, "globs", out string[] globs);
            if (arrayError != null) return arrayError;
            if (globs != null)
            {
                string globError = TryCompileProjectSearchGlobs(
                    globs,
                    out settings.GlobRules,
                    out settings.HasPositiveGlobs);
                if (globError != null) return globError;
            }

            string scalarError = TryReadProjectSearchBool(value, "regex", ref settings.Regex);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchBool(value, "caseSensitive", ref settings.CaseSensitive);
            if (scalarError != null) return scalarError;
            settings.CaseSensitiveExplicit = value["caseSensitive"] != null;
            scalarError = TryReadProjectSearchBool(value, "smartCase", ref settings.SmartCase);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchBool(value, "word", ref settings.Word);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchBool(value, "wholeLine", ref settings.WholeLine);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchBool(value, "multiline", ref settings.Multiline);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchBool(value, "dotAll", ref settings.DotAll);
            if (scalarError != null) return scalarError;
            if (settings.DotAll && !settings.Multiline)
            {
                return "project-search dotAll requires multiline.";
            }

            JToken outputToken = value["output"];
            if (outputToken != null)
            {
                if (outputToken.Type != JTokenType.String)
                {
                    return "project-search option 'output' must be matches, files, or count.";
                }

                string output = ((string)outputToken).Trim().ToLowerInvariant();
                if (output == "matches") settings.Output = ProjectSearchOutputMode.Matches;
                else if (output == "files") settings.Output = ProjectSearchOutputMode.Files;
                else if (output == "count") settings.Output = ProjectSearchOutputMode.Count;
                else return "project-search option 'output' must be matches, files, or count.";
            }

            int context = 0;
            scalarError = TryReadProjectSearchInt(value, "context", 0, 100, ref context);
            if (scalarError != null) return scalarError;
            if (value["context"] != null)
            {
                settings.BeforeContext = context;
                settings.AfterContext = context;
            }

            scalarError = TryReadProjectSearchInt(
                value, "beforeContext", 0, 100, ref settings.BeforeContext);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchInt(
                value, "afterContext", 0, 100, ref settings.AfterContext);
            if (scalarError != null) return scalarError;
            if (settings.Output != ProjectSearchOutputMode.Matches &&
                (settings.BeforeContext > 0 || settings.AfterContext > 0))
            {
                return "project-search context is available only when output is matches.";
            }

            JToken encodingToken = value["encoding"];
            if (encodingToken != null)
            {
                if (encodingToken.Type != JTokenType.String)
                {
                    return "project-search option 'encoding' must be auto, utf8, utf16le, utf16be, utf32le, utf32be, ascii, or latin1.";
                }

                settings.EncodingName = ((string)encodingToken).Trim().ToLowerInvariant();
                switch (settings.EncodingName)
                {
                    case "auto":
                    case "utf8":
                    case "utf-8":
                        settings.TextEncoding = new UTF8Encoding(false, false);
                        break;
                    case "utf16le":
                    case "utf-16le":
                        settings.TextEncoding = Encoding.Unicode;
                        break;
                    case "utf16be":
                    case "utf-16be":
                        settings.TextEncoding = Encoding.BigEndianUnicode;
                        break;
                    case "utf32le":
                    case "utf-32le":
                        settings.TextEncoding = new UTF32Encoding(false, true, false);
                        break;
                    case "utf32be":
                    case "utf-32be":
                        settings.TextEncoding = new UTF32Encoding(true, true, false);
                        break;
                    case "ascii":
                        settings.TextEncoding = Encoding.ASCII;
                        break;
                    case "latin1":
                    case "latin-1":
                        settings.TextEncoding = Encoding.GetEncoding(28591);
                        break;
                    default:
                        return "project-search option 'encoding' must be auto, utf8, utf16le, utf16be, utf32le, utf32be, ascii, or latin1.";
                }
            }

            scalarError = TryReadProjectSearchBool(
                value, "text", ref settings.TreatBinaryAsText);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchBool(value, "includeMeta", ref settings.IncludeMeta);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchBool(value, "includeHidden", ref settings.IncludeHidden);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchBool(value, "respectIgnore", ref settings.RespectIgnore);
            if (scalarError != null) return scalarError;

            scalarError = TryReadProjectSearchInt(
                value, "maxDepth", 0, 1000, ref settings.MaxDepth);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchInt(
                value, "threads", 0, PROJECT_SEARCH_MAX_THREADS, ref settings.Threads);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchInt(
                value, "maxResults", 1, PROJECT_SEARCH_MAX_RESULTS, ref settings.MaxResults);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchLong(
                value, "maxFileBytes", 1, PROJECT_SEARCH_MAX_FILE_BYTES, ref settings.MaxFileBytes);
            if (scalarError != null) return scalarError;
            scalarError = TryReadProjectSearchInt(
                value, "timeoutMs", 100, PROJECT_SEARCH_MAX_TIMEOUT_MS, ref settings.TimeoutMs);
            return scalarError;
        }

        /// <summary>Reads one optional JSON string array.</summary>
        private static string TryReadProjectSearchStringArray(
            JObject value,
            string name,
            out string[] result)
        {
            result = null;
            JToken token = value[name];
            if (token == null) return null;
            if (token.Type != JTokenType.Array)
            {
                return "project-search option '" + name + "' must be an array of strings.";
            }

            var strings = new List<string>();
            foreach (JToken item in (JArray)token)
            {
                if (item.Type != JTokenType.String)
                {
                    return "project-search option '" + name + "' must contain only strings.";
                }

                strings.Add((string)item);
            }

            result = strings.ToArray();
            return null;
        }

        /// <summary>Reads one optional JSON boolean.</summary>
        private static string TryReadProjectSearchBool(JObject value, string name, ref bool result)
        {
            JToken token = value[name];
            if (token == null) return null;
            if (token.Type != JTokenType.Boolean)
            {
                return "project-search option '" + name + "' must be true or false.";
            }

            result = (bool)token;
            return null;
        }

        /// <summary>Reads one optional bounded JSON integer.</summary>
        private static string TryReadProjectSearchInt(
            JObject value,
            string name,
            int minimum,
            int maximum,
            ref int result)
        {
            JToken token = value[name];
            if (token == null) return null;
            if (token.Type != JTokenType.Integer)
            {
                return "project-search option '" + name + "' must be an integer.";
            }

            long parsed = (long)token;
            if (parsed < minimum || parsed > maximum)
            {
                return "project-search option '" + name + "' must be between "
                    + minimum + " and " + maximum + ".";
            }

            result = (int)parsed;
            return null;
        }

        /// <summary>Reads one optional bounded JSON long integer.</summary>
        private static string TryReadProjectSearchLong(
            JObject value,
            string name,
            long minimum,
            long maximum,
            ref long result)
        {
            JToken token = value[name];
            if (token == null) return null;
            if (token.Type != JTokenType.Integer)
            {
                return "project-search option '" + name + "' must be an integer.";
            }

            long parsed = (long)token;
            if (parsed < minimum || parsed > maximum)
            {
                return "project-search option '" + name + "' must be between "
                    + minimum + " and " + maximum + ".";
            }

            result = parsed;
            return null;
        }

        #endregion

        #region Root validation

        /// <summary>Resolves requested paths and proves every one remains inside the project root.</summary>
        private static string TryResolveProjectSearchRoots(
            string[] paths,
            out List<ProjectSearchRoot> roots)
        {
            roots = new List<ProjectSearchRoot>();
            string projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string projectPrefix = projectRoot + Path.DirectorySeparatorChar;
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            foreach (string rawPath in paths)
            {
                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    return "project-search paths cannot contain an empty value.";
                }

                string normalized = rawPath.Trim().Trim('"');
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(
                        Path.IsPathRooted(normalized)
                            ? normalized
                            : Path.Combine(projectRoot, normalized));
                }
                catch (Exception ex)
                {
                    return "Invalid project-search path '" + rawPath + "': " + ex.Message;
                }

                bool isProjectRoot = string.Equals(fullPath, projectRoot, comparison);
                if (!isProjectRoot && !fullPath.StartsWith(projectPrefix, comparison))
                {
                    return "project-search path escapes the project root: " + rawPath;
                }

                bool isFile = File.Exists(fullPath);
                if (!isFile && !Directory.Exists(fullPath))
                {
                    return "project-search path does not exist: " + rawPath;
                }

                string relative = isProjectRoot
                    ? "."
                    : fullPath.Substring(projectPrefix.Length).Replace('\\', '/');
                roots.Add(new ProjectSearchRoot
                {
                    FullPath = fullPath,
                    RelativePath = relative,
                    IsFile = isFile,
                });
            }

            return null;
        }

        /// <summary>Resolves automatic concurrency without encoding any development-machine topology.</summary>
        private static int ResolveProjectSearchWorkerCount(int requested, int rootCount)
        {
            if (requested > 0) return requested;

            int available = Math.Max(1, Environment.ProcessorCount);
            int automatic = Math.Min(available, PROJECT_SEARCH_AUTO_THREAD_CEILING);
            return Math.Max(1, Math.Min(automatic, Math.Max(rootCount, 2) * 4));
        }

        #endregion

        #region Search execution

        /// <summary>Runs the bounded traversal and materializes lines only from files with a byte-level hit.</summary>
        private static string ExecuteProjectSearch(
            string pattern,
            ProjectSearchSettings settings,
            List<ProjectSearchRoot> roots,
            int workerCount,
            byte[] literal,
            int[] shifts,
            Regex expression)
        {
            var stopwatch = Stopwatch.StartNew();
            DateTime deadlineUtc = DateTime.UtcNow.AddMilliseconds(settings.TimeoutMs);
            var cancellation = new CancellationTokenSource();
            cancellation.CancelAfter(settings.TimeoutMs);
            var directories = new BlockingCollection<ProjectSearchDirectory>(
                new ConcurrentStack<ProjectSearchDirectory>());
            var candidates = new ConcurrentQueue<string>();
            var errors = new ConcurrentQueue<string>();
            string projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            List<ProjectSearchPathRule> projectRules = settings.RespectIgnore
                ? LoadProjectSearchIgnoreRules(
                    projectRoot,
                    ".",
                    new List<ProjectSearchPathRule>(),
                    errors)
                : new List<ProjectSearchPathRule>();
            long filesScanned = 0;
            long bytesScanned = 0;
            long skippedFiles = 0;
            int candidateCount = 0;
            int pendingDirectories = 0;
            int truncatedFlag = 0;

            cancellation.Token.Register(() =>
            {
                try { directories.CompleteAdding(); }
                catch (InvalidOperationException) { }
            });

            foreach (ProjectSearchRoot root in roots)
            {
                if (cancellation.IsCancellationRequested) break;

                if (root.IsFile)
                {
                    ScanProjectSearchCandidate(
                        root.FullPath,
                        settings,
                        pattern,
                        literal,
                        shifts,
                        expression,
                        cancellation,
                        candidates,
                        errors,
                        ref filesScanned,
                        ref bytesScanned,
                        ref skippedFiles,
                        ref candidateCount,
                        ref truncatedFlag);
                    continue;
                }

                Interlocked.Increment(ref pendingDirectories);
                try
                {
                    directories.Add(
                        new ProjectSearchDirectory
                        {
                            FullPath = root.FullPath,
                            RelativePath = root.RelativePath,
                            Depth = 0,
                            IgnoreRules = projectRules,
                        },
                        cancellation.Token);
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Decrement(ref pendingDirectories);
                    break;
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Decrement(ref pendingDirectories);
                    break;
                }
            }

            if (pendingDirectories == 0)
            {
                try { directories.CompleteAdding(); }
                catch (InvalidOperationException) { }
            }

            var workers = new Task[workerCount];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = Task.Run(() =>
                {
                    while (!cancellation.IsCancellationRequested)
                    {
                        ProjectSearchDirectory directory;
                        try
                        {
                            if (!directories.TryTake(out directory, 25, cancellation.Token))
                            {
                                if (directories.IsCompleted) break;
                                continue;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        try
                        {
                            List<ProjectSearchPathRule> ignoreRules = settings.RespectIgnore
                                ? LoadProjectSearchIgnoreRules(
                                    directory.FullPath,
                                    directory.RelativePath,
                                    directory.IgnoreRules,
                                    errors)
                                : directory.IgnoreRules;

                            foreach (FileSystemInfo entry in
                                new DirectoryInfo(directory.FullPath).EnumerateFileSystemInfos())
                            {
                                if (cancellation.IsCancellationRequested) break;

                                FileAttributes attributes;
                                try { attributes = entry.Attributes; }
                                catch (Exception ex)
                                {
                                    AddProjectSearchError(errors, entry.FullName, ex);
                                    continue;
                                }

                                if ((attributes & FileAttributes.ReparsePoint) != 0)
                                {
                                    Interlocked.Increment(ref skippedFiles);
                                    continue;
                                }

                                bool hidden = entry.Name.StartsWith(".", StringComparison.Ordinal);
                                var childDirectory = entry as DirectoryInfo;
                                string relativePath = directory.RelativePath == "."
                                    ? entry.Name
                                    : directory.RelativePath + "/" + entry.Name;

                                if (childDirectory != null)
                                {
                                    if (ProjectSearchExcludedDirectories.Contains(childDirectory.Name) ||
                                        (!settings.IncludeHidden && hidden) ||
                                        directory.Depth >= settings.MaxDepth)
                                    {
                                        Interlocked.Increment(ref skippedFiles);
                                        continue;
                                    }

                                    Interlocked.Increment(ref pendingDirectories);
                                    try
                                    {
                                        directories.Add(
                                            new ProjectSearchDirectory
                                            {
                                                FullPath = childDirectory.FullName,
                                                RelativePath = relativePath,
                                                Depth = directory.Depth + 1,
                                                IgnoreRules = ignoreRules,
                                            },
                                            cancellation.Token);
                                    }
                                    catch (InvalidOperationException)
                                    {
                                        Interlocked.Decrement(ref pendingDirectories);
                                        break;
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        Interlocked.Decrement(ref pendingDirectories);
                                        break;
                                    }

                                    continue;
                                }

                                var file = entry as FileInfo;
                                if (file == null ||
                                    (!settings.IncludeHidden && hidden) ||
                                    !ShouldSearchProjectFile(file.Name, settings) ||
                                    !ProjectSearchGlobsAdmit(
                                        relativePath,
                                        false,
                                        settings.GlobRules,
                                        settings.HasPositiveGlobs) ||
                                    (settings.RespectIgnore &&
                                        !ProjectSearchIgnoreAdmits(relativePath, false, ignoreRules)))
                                {
                                    Interlocked.Increment(ref skippedFiles);
                                    continue;
                                }

                                ScanProjectSearchCandidate(
                                    file.FullName,
                                    settings,
                                    pattern,
                                    literal,
                                    shifts,
                                    expression,
                                    cancellation,
                                    candidates,
                                    errors,
                                    ref filesScanned,
                                    ref bytesScanned,
                                    ref skippedFiles,
                                    ref candidateCount,
                                    ref truncatedFlag);
                            }
                        }
                        catch (Exception ex)
                        {
                            AddProjectSearchError(errors, directory.FullPath, ex);
                        }
                        finally
                        {
                            if (Interlocked.Decrement(ref pendingDirectories) == 0)
                            {
                                try { directories.CompleteAdding(); }
                                catch (InvalidOperationException) { }
                            }
                        }
                    }
                });
            }

            bool workersCompleted = true;
            try
            {
                workersCompleted = Task.WaitAll(workers, settings.TimeoutMs + 1000);
            }
            catch (AggregateException ex)
            {
                AddProjectSearchError(errors, "worker", ex.Flatten().InnerException);
            }

            bool timedOut = !workersCompleted || DateTime.UtcNow >= deadlineUtc;
            cancellation.Cancel();
            if (!workersCompleted)
            {
                try
                {
                    workersCompleted = Task.WaitAll(workers, 1000);
                }
                catch (AggregateException ex)
                {
                    workersCompleted = true;
                    AddProjectSearchError(errors, "worker", ex.Flatten().InnerException);
                }
            }

            var candidatePaths = candidates.ToArray();
            Array.Sort(candidatePaths, StringComparer.OrdinalIgnoreCase);
            var results = new JArray();
            long matchCount = 0;

            if (settings.Output == ProjectSearchOutputMode.Files)
            {
                for (int i = 0; i < candidatePaths.Length && results.Count < settings.MaxResults; i++)
                {
                    results.Add(new JObject
                    {
                        ["path"] = ProjectSearchDisplayPath(candidatePaths[i]),
                    });
                }
            }
            else if (settings.Output == ProjectSearchOutputMode.Count)
            {
                for (int i = 0; i < candidatePaths.Length; i++)
                {
                    if (DateTime.UtcNow >= deadlineUtc)
                    {
                        timedOut = true;
                        break;
                    }

                    try
                    {
                        long fileMatches = CountProjectSearchFileMatches(
                            candidatePaths[i],
                            pattern,
                            settings,
                            expression,
                            deadlineUtc);
                        matchCount += fileMatches;
                        if (results.Count < settings.MaxResults)
                        {
                            results.Add(new JObject
                            {
                                ["path"] = ProjectSearchDisplayPath(candidatePaths[i]),
                                ["count"] = fileMatches,
                            });
                        }
                        else
                        {
                            Interlocked.Exchange(ref truncatedFlag, 1);
                        }
                    }
                    catch (Exception ex)
                    {
                        AddProjectSearchError(errors, candidatePaths[i], ex);
                    }
                }
            }
            else
            {
                for (int i = 0; i < candidatePaths.Length && results.Count < settings.MaxResults; i++)
                {
                    if (DateTime.UtcNow >= deadlineUtc)
                    {
                        timedOut = true;
                        break;
                    }

                    MaterializeProjectSearchFile(
                        candidatePaths[i],
                        pattern,
                        settings,
                        expression,
                        results,
                        errors,
                        deadlineUtc);
                }
            }

            if (settings.Output != ProjectSearchOutputMode.Count &&
                results.Count >= settings.MaxResults &&
                candidatePaths.Length > 0)
            {
                Interlocked.Exchange(ref truncatedFlag, 1);
            }

            stopwatch.Stop();
            if (workersCompleted)
            {
                cancellation.Dispose();
                directories.Dispose();
            }

            var errorArray = new JArray();
            string error;
            while (errorArray.Count < 8 && errors.TryDequeue(out error))
            {
                errorArray.Add(error);
            }

            var response = new JObject
            {
                ["pattern"] = pattern,
                ["mode"] = settings.Regex ? "regex" : "literal",
                ["output"] = settings.Output.ToString().ToLowerInvariant(),
                ["caseSensitive"] = settings.CaseSensitive,
                ["elapsedMs"] = stopwatch.ElapsedMilliseconds,
                ["workers"] = workerCount,
                ["completed"] = !timedOut,
                ["timedOut"] = timedOut,
                ["truncated"] = Volatile.Read(ref truncatedFlag) != 0,
                ["filesScanned"] = Interlocked.Read(ref filesScanned),
                ["bytesScanned"] = Interlocked.Read(ref bytesScanned),
                ["candidateFiles"] = candidatePaths.Length,
                ["matchingFiles"] = candidatePaths.Length,
                ["matchCount"] = settings.Output == ProjectSearchOutputMode.Count
                    ? (JToken)matchCount
                    : JValue.CreateNull(),
                ["skippedFiles"] = Interlocked.Read(ref skippedFiles),
                ["results"] = results,
                ["errors"] = errorArray,
            };

            return response.ToString(Formatting.None);
        }

        /// <summary>Searches one file and queues its path when at least one match survives validation.</summary>
        private static void ScanProjectSearchCandidate(
            string path,
            ProjectSearchSettings settings,
            string pattern,
            byte[] literal,
            int[] shifts,
            Regex expression,
            CancellationTokenSource cancellation,
            ConcurrentQueue<string> candidates,
            ConcurrentQueue<string> errors,
            ref long filesScanned,
            ref long bytesScanned,
            ref long skippedFiles,
            ref int candidateCount,
            ref int truncatedFlag)
        {
            if (cancellation.IsCancellationRequested) return;

            try
            {
                if (settings.ListFiles)
                {
                    Interlocked.Increment(ref filesScanned);
                    int listed = Interlocked.Increment(ref candidateCount);
                    if (listed <= settings.MaxResults)
                    {
                        candidates.Enqueue(path);
                    }
                    else
                    {
                        Interlocked.Exchange(ref truncatedFlag, 1);
                        cancellation.Cancel();
                    }

                    return;
                }

                var info = new FileInfo(path);
                if (info.Length > settings.MaxFileBytes)
                {
                    Interlocked.Increment(ref skippedFiles);
                    return;
                }

                long fileBytes;
                bool matched = literal != null
                    ? ProjectFileContainsLiteral(
                        path,
                        literal,
                        shifts,
                        settings.TreatBinaryAsText,
                        cancellation.Token,
                        out fileBytes)
                    : ProjectFileContainsText(path, pattern, settings, expression, cancellation.Token, out fileBytes);

                Interlocked.Increment(ref filesScanned);
                Interlocked.Add(ref bytesScanned, fileBytes);
                if (!matched) return;

                int count = Interlocked.Increment(ref candidateCount);
                if (settings.Output == ProjectSearchOutputMode.Count)
                {
                    if (count <= PROJECT_SEARCH_MAX_CANDIDATE_FILES)
                    {
                        candidates.Enqueue(path);
                    }
                    else
                    {
                        Interlocked.Exchange(ref truncatedFlag, 1);
                        cancellation.Cancel();
                    }

                    return;
                }

                if (count <= settings.MaxResults)
                {
                    candidates.Enqueue(path);
                }

                if (count >= settings.MaxResults)
                {
                    Interlocked.Exchange(ref truncatedFlag, 1);
                    cancellation.Cancel();
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AddProjectSearchError(errors, path, ex);
            }
        }

        /// <summary>Checks whether a filename is admitted by the requested extension policy.</summary>
        private static bool ShouldSearchProjectFile(string name, ProjectSearchSettings settings)
        {
            if (!settings.IncludeMeta &&
                name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (settings.Extensions.Count == 0) return true;
            return settings.Extensions.Contains(Path.GetExtension(name));
        }

        #endregion

        #region File scanning

        /// <summary>Builds the Boyer-Moore-Horspool skip table shared by all literal workers.</summary>
        private static int[] BuildProjectSearchShiftTable(byte[] pattern)
        {
            var shifts = new int[256];
            for (int i = 0; i < shifts.Length; i++) shifts[i] = pattern.Length;
            for (int i = 0; i < pattern.Length - 1; i++)
            {
                shifts[pattern[i]] = pattern.Length - 1 - i;
            }

            return shifts;
        }

        /// <summary>Scans one file as bytes, preserving block-boundary matches and refusing NUL data.</summary>
        private static bool ProjectFileContainsLiteral(
            string path,
            byte[] pattern,
            int[] shifts,
            bool treatBinaryAsText,
            CancellationToken cancellation,
            out long bytesRead)
        {
            bytesRead = 0;
            bool matched = false;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(PROJECT_SEARCH_BUFFER_BYTES + pattern.Length);
            try
            {
                int carry = 0;
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    PROJECT_SEARCH_BUFFER_BYTES,
                    FileOptions.SequentialScan))
                {
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int read = stream.Read(buffer, carry, PROJECT_SEARCH_BUFFER_BYTES - carry);
                        if (read <= 0) break;

                        int total = carry + read;
                        bytesRead += read;
                        for (int i = carry; i < total; i++)
                        {
                            if (!treatBinaryAsText && buffer[i] == 0) return false;
                        }

                        if (!matched && ProjectSearchBufferContains(buffer, total, pattern, shifts))
                        {
                            matched = true;
                        }

                        carry = Math.Min(pattern.Length - 1, total);
                        if (carry > 0)
                        {
                            Buffer.BlockCopy(buffer, total - carry, buffer, 0, carry);
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return matched;
        }

        /// <summary>Runs a scalar skip-table search over one complete byte block.</summary>
        private static bool ProjectSearchBufferContains(
            byte[] buffer,
            int length,
            byte[] pattern,
            int[] shifts)
        {
            int last = pattern.Length - 1;
            int cursor = 0;
            int limit = length - pattern.Length;
            while (cursor <= limit)
            {
                int patternIndex = last;
                while (patternIndex >= 0 &&
                    buffer[cursor + patternIndex] == pattern[patternIndex])
                {
                    patternIndex--;
                }

                if (patternIndex < 0) return true;
                cursor += shifts[buffer[cursor + last]];
            }

            return false;
        }

        /// <summary>Runs the slower Unicode text path used for regex or case-insensitive matching.</summary>
        private static bool ProjectFileContainsText(
            string path,
            string pattern,
            ProjectSearchSettings settings,
            Regex expression,
            CancellationToken cancellation,
            out long bytesRead)
        {
            if (settings.Multiline)
            {
                string content;
                bool readable = TryReadProjectSearchText(
                    path,
                    settings.TextEncoding,
                    settings.TreatBinaryAsText,
                    cancellation,
                    out content,
                    out bytesRead);
                return readable && expression.IsMatch(content);
            }

            bytesRead = 0;
            bool matched = false;
            StringComparison comparison = settings.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                PROJECT_SEARCH_BUFFER_BYTES,
                FileOptions.SequentialScan))
            using (var reader = new StreamReader(
                stream,
                settings.TextEncoding,
                true,
                PROJECT_SEARCH_BUFFER_BYTES,
                true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (!settings.TreatBinaryAsText && line.IndexOf('\0') >= 0) return false;
                    if (!matched)
                    {
                        matched = expression != null
                            ? expression.IsMatch(line)
                            : line.IndexOf(pattern, comparison) >= 0;
                    }
                }

                bytesRead = stream.Position;
            }

            return matched;
        }

        /// <summary>Reads one bounded text file with cancellation and optional NUL detection.</summary>
        private static bool TryReadProjectSearchText(
            string path,
            Encoding encoding,
            bool treatBinaryAsText,
            CancellationToken cancellation,
            out string content,
            out long bytesRead)
        {
            content = string.Empty;
            bytesRead = 0;
            char[] buffer = ArrayPool<char>.Shared.Rent(PROJECT_SEARCH_BUFFER_BYTES / 2);
            try
            {
                var builder = new StringBuilder();
                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    PROJECT_SEARCH_BUFFER_BYTES,
                    FileOptions.SequentialScan))
                using (var reader = new StreamReader(
                    stream,
                    encoding,
                    true,
                    PROJECT_SEARCH_BUFFER_BYTES,
                    true))
                {
                    while (true)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int read = reader.Read(buffer, 0, buffer.Length);
                        if (read <= 0) break;

                        for (int i = 0; i < read; i++)
                        {
                            if (!treatBinaryAsText && buffer[i] == '\0') return false;
                        }

                        builder.Append(buffer, 0, read);
                    }

                    bytesRead = stream.Position;
                }

                content = builder.ToString();
                return true;
            }
            finally
            {
                ArrayPool<char>.Shared.Return(buffer);
            }
        }

        /// <summary>Returns one project-relative forward-slash path.</summary>
        private static string ProjectSearchDisplayPath(string path)
        {
            string projectRoot = Path.GetFullPath(Directory.GetCurrentDirectory())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = projectRoot + Path.DirectorySeparatorChar;
            StringComparison comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return path.StartsWith(prefix, comparison)
                ? path.Substring(prefix.Length).Replace('\\', '/')
                : path.Replace('\\', '/');
        }

        /// <summary>Counts every match in one candidate file for count output.</summary>
        private static long CountProjectSearchFileMatches(
            string path,
            string pattern,
            ProjectSearchSettings settings,
            Regex expression,
            DateTime deadlineUtc)
        {
            long count = 0;
            if (settings.Multiline)
            {
                string content;
                long ignoredBytes;
                if (!TryReadProjectSearchText(
                    path,
                    settings.TextEncoding,
                    settings.TreatBinaryAsText,
                    CancellationToken.None,
                    out content,
                    out ignoredBytes))
                {
                    return 0;
                }

                foreach (Match ignored in expression.Matches(content))
                {
                    if (DateTime.UtcNow >= deadlineUtc) break;
                    count++;
                }

                return count;
            }

            StringComparison comparison = settings.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                PROJECT_SEARCH_BUFFER_BYTES,
                FileOptions.SequentialScan))
            using (var reader = new StreamReader(
                stream,
                settings.TextEncoding,
                true,
                PROJECT_SEARCH_BUFFER_BYTES,
                true))
            {
                string line;
                while (DateTime.UtcNow < deadlineUtc && (line = reader.ReadLine()) != null)
                {
                    if (expression != null)
                    {
                        foreach (Match ignored in expression.Matches(line)) count++;
                        continue;
                    }

                    int cursor = 0;
                    while (true)
                    {
                        int matchIndex = line.IndexOf(pattern, cursor, comparison);
                        if (matchIndex < 0) break;
                        count++;
                        cursor = matchIndex + Math.Max(1, pattern.Length);
                    }
                }
            }

            return count;
        }

        /// <summary>Reads matching candidate files again only to produce bounded line excerpts.</summary>
        private static void MaterializeProjectSearchFile(
            string path,
            string pattern,
            ProjectSearchSettings settings,
            Regex expression,
            JArray results,
            ConcurrentQueue<string> errors,
            DateTime deadlineUtc)
        {
            string displayPath = ProjectSearchDisplayPath(path);
            StringComparison comparison = settings.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            try
            {
                if (settings.Multiline)
                {
                    MaterializeProjectSearchMultiline(
                        path,
                        displayPath,
                        expression,
                        settings,
                        results,
                        deadlineUtc);
                    return;
                }

                if (settings.BeforeContext > 0 || settings.AfterContext > 0)
                {
                    MaterializeProjectSearchWithContext(
                        path,
                        displayPath,
                        pattern,
                        expression,
                        settings,
                        results,
                        deadlineUtc);
                    return;
                }

                using (var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    PROJECT_SEARCH_BUFFER_BYTES,
                    FileOptions.SequentialScan))
                using (var reader = new StreamReader(
                    stream,
                    settings.TextEncoding,
                    true,
                    PROJECT_SEARCH_BUFFER_BYTES,
                    true))
                {
                    int lineNumber = 0;
                    string line;
                    while (results.Count < settings.MaxResults &&
                        DateTime.UtcNow < deadlineUtc &&
                        (line = reader.ReadLine()) != null)
                    {
                        lineNumber++;
                        if (expression != null)
                        {
                            foreach (Match match in expression.Matches(line))
                            {
                                results.Add(ProjectSearchResult(
                                    displayPath,
                                    lineNumber,
                                    match.Index + 1,
                                    BuildProjectSearchExcerpt(line, match.Index)));
                                if (results.Count >= settings.MaxResults) break;
                            }

                            continue;
                        }

                        int cursor = 0;
                        while (results.Count < settings.MaxResults)
                        {
                            int matchIndex = line.IndexOf(pattern, cursor, comparison);
                            if (matchIndex < 0) break;

                            results.Add(ProjectSearchResult(
                                displayPath,
                                lineNumber,
                                matchIndex + 1,
                                BuildProjectSearchExcerpt(line, matchIndex)));
                            cursor = matchIndex + Math.Max(1, pattern.Length);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AddProjectSearchError(errors, path, ex);
            }
        }

        /// <summary>Materializes ordinary line matches with bounded before and after context.</summary>
        private static void MaterializeProjectSearchWithContext(
            string path,
            string displayPath,
            string pattern,
            Regex expression,
            ProjectSearchSettings settings,
            JArray results,
            DateTime deadlineUtc)
        {
            var lines = new List<string>();
            using (var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                PROJECT_SEARCH_BUFFER_BYTES,
                FileOptions.SequentialScan))
            using (var reader = new StreamReader(
                stream,
                settings.TextEncoding,
                true,
                PROJECT_SEARCH_BUFFER_BYTES,
                true))
            {
                string line;
                while ((line = reader.ReadLine()) != null) lines.Add(line);
            }

            StringComparison comparison = settings.CaseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            for (int lineIndex = 0;
                lineIndex < lines.Count &&
                results.Count < settings.MaxResults &&
                DateTime.UtcNow < deadlineUtc;
                lineIndex++)
            {
                string line = lines[lineIndex];
                if (expression != null)
                {
                    foreach (Match match in expression.Matches(line))
                    {
                        JObject result = ProjectSearchResult(
                            displayPath,
                            lineIndex + 1,
                            match.Index + 1,
                            BuildProjectSearchExcerpt(line, match.Index));
                        AttachProjectSearchContext(result, lines, lineIndex, settings);
                        results.Add(result);
                        if (results.Count >= settings.MaxResults) break;
                    }

                    continue;
                }

                int cursor = 0;
                while (results.Count < settings.MaxResults)
                {
                    int matchIndex = line.IndexOf(pattern, cursor, comparison);
                    if (matchIndex < 0) break;

                    JObject result = ProjectSearchResult(
                        displayPath,
                        lineIndex + 1,
                        matchIndex + 1,
                        BuildProjectSearchExcerpt(line, matchIndex));
                    AttachProjectSearchContext(result, lines, lineIndex, settings);
                    results.Add(result);
                    cursor = matchIndex + Math.Max(1, pattern.Length);
                }
            }
        }

        /// <summary>Adds bounded context rows around one match result.</summary>
        private static void AttachProjectSearchContext(
            JObject result,
            IList<string> lines,
            int lineIndex,
            ProjectSearchSettings settings)
        {
            if (settings.BeforeContext > 0)
            {
                var before = new JArray();
                int start = Math.Max(0, lineIndex - settings.BeforeContext);
                for (int i = start; i < lineIndex; i++)
                {
                    before.Add(ProjectSearchContextLine(i + 1, lines[i]));
                }

                result["before"] = before;
            }

            if (settings.AfterContext > 0)
            {
                var after = new JArray();
                int end = Math.Min(lines.Count, lineIndex + settings.AfterContext + 1);
                for (int i = lineIndex + 1; i < end; i++)
                {
                    after.Add(ProjectSearchContextLine(i + 1, lines[i]));
                }

                result["after"] = after;
            }
        }

        /// <summary>Builds one bounded context-line object.</summary>
        private static JObject ProjectSearchContextLine(int lineNumber, string text)
        {
            string bounded = text.Length <= PROJECT_SEARCH_MAX_EXCERPT_CHARS
                ? text
                : text.Substring(0, PROJECT_SEARCH_MAX_EXCERPT_CHARS) + "...";
            return new JObject
            {
                ["line"] = lineNumber,
                ["text"] = bounded,
            };
        }

        /// <summary>Materializes matches whose expressions can cross line boundaries.</summary>
        private static void MaterializeProjectSearchMultiline(
            string path,
            string displayPath,
            Regex expression,
            ProjectSearchSettings settings,
            JArray results,
            DateTime deadlineUtc)
        {
            string content;
            long ignoredBytes;
            if (!TryReadProjectSearchText(
                path,
                settings.TextEncoding,
                settings.TreatBinaryAsText,
                CancellationToken.None,
                out content,
                out ignoredBytes))
            {
                return;
            }

            var lineStarts = new List<int> { 0 };
            var contextLines = new List<string>(
                content.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.None));
            for (int i = 0; i < content.Length; i++)
            {
                if (content[i] == '\n' && i + 1 < content.Length)
                {
                    lineStarts.Add(i + 1);
                }
            }

            foreach (Match match in expression.Matches(content))
            {
                if (results.Count >= settings.MaxResults || DateTime.UtcNow >= deadlineUtc) break;

                int lineIndex = lineStarts.BinarySearch(match.Index);
                if (lineIndex < 0) lineIndex = ~lineIndex - 1;
                lineIndex = Math.Max(0, lineIndex);
                int lineStart = lineStarts[lineIndex];
                int lineEnd = lineIndex + 1 < lineStarts.Count
                    ? lineStarts[lineIndex + 1] - 1
                    : content.Length;
                if (lineEnd > lineStart && content[lineEnd - 1] == '\r') lineEnd--;

                string line = content.Substring(lineStart, Math.Max(0, lineEnd - lineStart));
                JObject result = ProjectSearchResult(
                    displayPath,
                    lineIndex + 1,
                    match.Index - lineStart + 1,
                    BuildProjectSearchExcerpt(line, match.Index - lineStart));
                AttachProjectSearchContext(result, contextLines, lineIndex, settings);
                results.Add(result);
            }
        }

        /// <summary>Builds one compact result object.</summary>
        private static JObject ProjectSearchResult(
            string path,
            int line,
            int column,
            string excerpt)
        {
            return new JObject
            {
                ["path"] = path,
                ["line"] = line,
                ["column"] = column,
                ["text"] = excerpt,
            };
        }

        /// <summary>Keeps one line bounded while retaining text around the first match.</summary>
        private static string BuildProjectSearchExcerpt(string line, int matchIndex)
        {
            if (line.Length <= PROJECT_SEARCH_MAX_EXCERPT_CHARS) return line;

            int half = PROJECT_SEARCH_MAX_EXCERPT_CHARS / 2;
            int start = Math.Max(0, matchIndex - half);
            int length = Math.Min(PROJECT_SEARCH_MAX_EXCERPT_CHARS, line.Length - start);
            string excerpt = line.Substring(start, length);
            if (start > 0) excerpt = "..." + excerpt;
            if (start + length < line.Length) excerpt += "...";
            return excerpt;
        }

        /// <summary>Records at most a small bounded set of path-specific failures.</summary>
        private static void AddProjectSearchError(
            ConcurrentQueue<string> errors,
            string path,
            Exception exception)
        {
            if (errors.Count >= 8) return;
            errors.Enqueue(path + ": " + exception.Message);
        }

        #endregion
    }
}
