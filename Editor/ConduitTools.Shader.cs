/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// The shader source verbs: shader-batch-edit and shader-update-or-create. Shader sources compile on
    /// import rather than through the script pipeline, so these verbs never request a script compile, never
    /// wait for a domain reload, and are not refused in Play Mode, where a shader edit hot-reloads into the
    /// running scene. Each one compiles what the change can affect and returns the compiler's own verdict,
    /// because Unity prints nothing when a shader compiles cleanly and an empty console is therefore not
    /// evidence of anything on its own.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Shader Constants

        /// <summary>
        /// File kinds the shader verbs write. Includes compile only through the shaders that use them, which
        /// the verdict follows; C# goes through the script verbs and is refused here.
        /// </summary>
        private static readonly string[] ShaderSourceExtensions = { ".shader", ".hlsl", ".cginc", ".compute" };

        /// <summary>Program stages a pass can carry, compiled in this order.</summary>
        private static readonly ShaderType[] ShaderStages =
            { ShaderType.Vertex, ShaderType.Fragment, ShaderType.Geometry, ShaderType.Hull, ShaderType.Domain };

        /// <summary>Most compiler messages listed for one shader before the rest are counted instead.</summary>
        private const int MAX_SHADER_MESSAGES = 20;

        #endregion

        #region Shader Commands

        /// <summary>
        /// Exact-string edits across shader sources, with the batch contract of udon-batch-edit: every edit
        /// resolved before anything is written, the read-before-edit gate, and versioned history. Instead of a
        /// script compile it imports the edited files and returns the shader verdict for every shader the
        /// import reached, including the shaders that use an edited include.
        /// </summary>
        /// <param name="args">Pipe-prefixed edits array or .json path, optionally followed by a report section.</param>
        /// <returns>Per-edit evidence and the shader verdict, or a precise refusal.</returns>
        private static string ShaderBatchEditCommand(string args)
        {
            return RunBatchEdit("shader-batch-edit", args, true);
        }

        /// <summary>
        /// Creates or overwrites one shader source file, retaining the previous version in file history, then
        /// imports it and returns the shader verdict. Byte-identical requests are refused.
        /// </summary>
        /// <param name="args">Asset path, then everything after the first pipe as the file content.</param>
        /// <returns>The write evidence and shader verdict, or a validation error.</returns>
        private static string ShaderUpdateOrCreateCommand(string args)
        {
            // Split on the first pipe only: shader source contains '|' in logical and bitwise expressions.
            int pipeIndex = args.IndexOf('|');
            if (pipeIndex < 0) return "Usage: shader-update-or-create <assetPath> | <content>";

            string assetPath = NormalizeAssetPath(args.Substring(0, pipeIndex));

            // Same trimming as script-update-or-create: the separator's whitespace goes, a trailing newline
            // stays, so a written file ends exactly as a hand-authored one does.
            string content = args.Substring(pipeIndex + 1).TrimStart().TrimEnd(' ', '\t');
            if (content.Length == 0) return "Usage: shader-update-or-create <assetPath> | <content>";

            if (!IsShaderSourceFile(assetPath))
            {
                return "Refused: shader-update-or-create writes " + string.Join(", ", ShaderSourceExtensions)
                    + " files; use script-update-or-create for C#. Got '" + assetPath + "'. Nothing was written.";
            }

            if (!TryValidateWritableScriptAssetPath(assetPath, out string pathError)) return pathError;

            bool isNewFile = !File.Exists(assetPath);
            bool hadBom = false;
            byte[] originalBytes = null;
            if (!isNewFile)
            {
                try
                {
                    originalBytes = File.ReadAllBytes(assetPath);
                    hadBom = originalBytes.Length >= 3
                        && originalBytes[0] == 0xEF
                        && originalBytes[1] == 0xBB
                        && originalBytes[2] == 0xBF;
                }
                catch (Exception)
                {
                    // Unreadable here means the write below reports the real failure, so assume no mark.
                    hadBom = false;
                }
            }

            if (FileHistoryUtf8Matches(originalBytes, content, hadBom))
                return "No change: " + assetPath + " already contains these exact bytes. No write, history capture or import occurred.";

            // The folder is created on disk and registered by the file's own import, which Unity performs for
            // every missing parent. Asking the AssetDatabase to create it instead makes a renamed sibling when
            // the folder already exists on disk but has not been imported yet.
            string directory = Path.GetDirectoryName(assetPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (isNewFile) return WriteShaderContent(assetPath, content, hadBom, "History: new file; no prior version exists.");
            return WithFileHistory(new Dictionary<string, byte[]> { [assetPath] = originalBytes },
                "shader-update-or-create", history =>
                {
                    if (!FileHistoryCurrentMatches(assetPath, originalBytes))
                        return "Refused: target changed during history preparation. Nothing was written.\n" + history;
                    return WriteShaderContent(assetPath, content, hadBom, history);
                });
        }

        /// <summary>Writes validated shader source, captures its completed version, then imports it.</summary>
        /// <param name="assetPath">Validated writable shader source path.</param>
        /// <param name="content">Requested source text.</param>
        /// <param name="hadBom">Whether to preserve a UTF-8 byte order mark.</param>
        /// <param name="history">Pre-write history evidence or warning.</param>
        /// <returns>The write evidence and shader verdict.</returns>
        private static string WriteShaderContent(string assetPath, string content, bool hadBom, string history)
        {
            Action releaseHistoryWrite = HoldFileHistoryWrite();
            try
            {
                File.WriteAllText(assetPath, content, new UTF8Encoding(hadBom));
                var completed = new Dictionary<string, byte[]> { [assetPath] = FileHistoryWrittenBytes(content, hadBom) };
                return WithFileHistory(completed, "shader-update-or-create", after =>
                {
                    // A caller that just wrote the whole file knows its content, so the write satisfies the
                    // read-before-edit gate for a follow-up shader-batch-edit.
                    RecordScriptReadHash(assetPath);
                    return "Wrote " + assetPath + "." + Environment.NewLine + history + Environment.NewLine + after
                        + Environment.NewLine + ImportShaderSources(new List<string> { assetPath });
                }, "after-write", releaseHistoryWrite);
            }
            catch
            {
                releaseHistoryWrite();
                throw;
            }
        }

        #endregion

        #region Shader Verdict

        /// <summary>True when the path ends in a shader source kind the shader verbs write.</summary>
        /// <param name="path">Asset-relative path to test.</param>
        /// <returns><c>true</c> for a .shader, .hlsl, .cginc or .compute file.</returns>
        private static bool IsShaderSourceFile(string path)
        {
            foreach (string extension in ShaderSourceExtensions)
            {
                if (path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        /// <summary>
        /// Imports shader sources synchronously, then compiles every shader and compute shader the import
        /// reached and reports the compiler's verdict for each. Importing a changed include re-imports the
        /// shaders that use it inside the same call, and Unity names them to the import callback, so the set
        /// reported is the set Unity re-imported rather than one guessed from include directives.
        /// </summary>
        /// <param name="paths">Written shader source paths.</param>
        /// <returns>The verdict, one block per compiled shader.</returns>
        private static string ImportShaderSources(IList<string> paths)
        {
            List<string> imported;
            ConduitShaderImportCapture.Begin();
            try
            {
                foreach (string path in paths)
                {
                    AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                }
            }
            finally
            {
                imported = ConduitShaderImportCapture.End();
            }

            var compiled = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in imported)
            {
                bool isShader = path.EndsWith(".shader", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith(".compute", StringComparison.OrdinalIgnoreCase);
                if (isShader && seen.Add(path)) compiled.Add(path);
            }

            if (compiled.Count == 0)
            {
                return "Shader verdict: no shader or compute shader in the project includes " + string.Join(", ", paths)
                    + ", so there is nothing to compile it against. It compiles when a shader that includes it is written.";
            }

            // The keywords a change can affect are the ones the changed files test, so only those variants
            // need compiling beyond the one with no keywords: code behind any other keyword is not in these files.
            string changedText = ReadChangedText(paths);

            var blocks = new StringBuilder();
            int failing = 0;
            int warningTotal = 0;
            int variantTotal = 0;
            foreach (string path in compiled)
            {
                blocks.AppendLine(DescribeCompiledShader(path, changedText, out int errors, out int warnings, out int variants));
                if (errors > 0) failing++;
                warningTotal += warnings;
                variantTotal += variants;
            }

            string platform = EditorShaderCompilerPlatform() + " for " + EditorUserBuildSettings.activeBuildTarget;
            string headline = failing > 0
                ? "Shader verdict: FAILED. " + failing + " of " + compiled.Count + " shader(s) have errors, so the change is on disk and not working until they are fixed."
                : "Shader verdict: " + compiled.Count + " shader(s), " + variantTotal + " variant(s) compiled with 0 errors and " + warningTotal + " warning(s).";
            string method = variantTotal == 0
                ? string.Empty
                : " Compiled here for " + platform + ": every pass and stage with no keywords on, and with each keyword the changed files test turned on by itself.";
            return headline + method + Environment.NewLine + blocks.ToString().TrimEnd();
        }

        /// <summary>Reads the written files back as one text, so keyword names can be looked up in them.</summary>
        /// <param name="paths">Written shader source paths.</param>
        /// <returns>The files' contents joined, or what could be read of them.</returns>
        private static string ReadChangedText(IList<string> paths)
        {
            var text = new StringBuilder();
            foreach (string path in paths)
            {
                try { text.AppendLine(File.ReadAllText(path)); }
                catch (Exception) { /* A file that cannot be read tests no keyword; its compile still reports. */ }
            }

            return text.ToString();
        }

        /// <summary>The keywords among a shader's that the changed files name as a whole word.</summary>
        /// <param name="names">Keyword names to look for.</param>
        /// <param name="changedText">Contents of the changed files.</param>
        /// <returns>The names found, in their original order.</returns>
        private static List<string> KeywordsTestedBy(IEnumerable<string> names, string changedText)
        {
            var tested = new List<string>();
            foreach (string name in names)
            {
                if (!string.IsNullOrEmpty(name) && Regex.IsMatch(changedText, @"\b" + Regex.Escape(name) + @"\b"))
                {
                    tested.Add(name);
                }
            }

            return tested;
        }

        /// <summary>
        /// The compiler platform matching the editor's graphics device, which is the platform Unity itself
        /// compiles for while the editor renders.
        /// </summary>
        /// <returns>The editor's shader compiler platform.</returns>
        private static ShaderCompilerPlatform EditorShaderCompilerPlatform()
        {
            switch (SystemInfo.graphicsDeviceType)
            {
                case GraphicsDeviceType.Vulkan: return ShaderCompilerPlatform.Vulkan;
                case GraphicsDeviceType.Metal: return ShaderCompilerPlatform.Metal;
                case GraphicsDeviceType.OpenGLCore: return ShaderCompilerPlatform.OpenGLCore;
                case GraphicsDeviceType.OpenGLES3: return ShaderCompilerPlatform.GLES3x;
                default: return ShaderCompilerPlatform.D3D;
            }
        }

        /// <summary>
        /// Compiles one shader or compute shader and describes the result: its error and warning counts, each
        /// message with the file and line it names and the variant that produced it, and the variants compiled.
        /// </summary>
        /// <param name="path">Asset path of a .shader or .compute file.</param>
        /// <param name="changedText">Contents of the changed files, used to pick the keyword variants.</param>
        /// <param name="errors">Number of distinct error messages found.</param>
        /// <param name="warnings">Number of distinct warning messages found.</param>
        /// <param name="variants">Number of variants compiled.</param>
        /// <returns>The shader's verdict block.</returns>
        private static string DescribeCompiledShader(string path, string changedText, out int errors, out int warnings, out int variants)
        {
            errors = 0;
            warnings = 0;
            variants = 0;
            var collected = new ShaderMessageSet(path);
            string coverage;

            var shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader != null)
            {
                // The import's own messages carry the ShaderLab parse errors, which no variant compile can reach.
                ShaderMessage[] importMessages = ShaderUtil.GetShaderMessages(shader);
                collected.Add(importMessages, null);

                // A shader whose ShaderLab did not parse is replaced by Unity's error shader, so its programs do
                // not exist yet and compiling the stand-in would only report failures with no message.
                coverage = HasShaderLabParseError(importMessages)
                    ? "the ShaderLab did not parse, so there is no program to compile until the parse error is fixed"
                    : CompileShaderVariants(shader, changedText, collected, out variants);
            }
            else
            {
                var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
                if (compute == null)
                {
                    errors = 1;
                    return "  ERRORS  " + path + " - the import produced no shader object, so it did not import as a shader.";
                }

                coverage = CompileComputeVariants(compute, changedText, collected, out variants);
            }

            errors = collected.Errors;
            warnings = collected.Warnings;
            string status = errors > 0 ? "ERRORS" : "OK";
            var lines = new List<string> { "  " + status + "  " + path + " - " + errors + " error(s), " + warnings + " warning(s); " + coverage };
            lines.AddRange(collected.Lines(MAX_SHADER_MESSAGES));
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Compiles every pass and stage of a shader for the editor's platform: once with no keywords, and once
        /// with each keyword that stage declares and the changed files test. Keywords come from the stage's own
        /// declaration, so a keyword is never turned on where the stage does not declare it.
        /// </summary>
        /// <param name="shader">The imported shader.</param>
        /// <param name="changedText">Contents of the changed files.</param>
        /// <param name="collected">Receives every message the compiles produce.</param>
        /// <param name="variants">Number of variants compiled.</param>
        /// <returns>A description of what was compiled.</returns>
        private static string CompileShaderVariants(Shader shader, string changedText, ShaderMessageSet collected, out int variants)
        {
            variants = 0;
            ShaderCompilerPlatform platform = EditorShaderCompilerPlatform();
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            ShaderData data = ShaderUtil.GetShaderData(shader);
            var testedAll = new SortedSet<string>(StringComparer.Ordinal);
            int stageCount = 0;

            for (int s = 0; s < data.SubshaderCount; s++)
            {
                ShaderData.Subshader subshader = data.GetSubshader(s);
                for (int p = 0; p < subshader.PassCount; p++)
                {
                    ShaderData.Pass pass = subshader.GetPass(p);
                    if (pass.IsGrabPass) continue;

                    var id = new PassIdentifier((uint)s, (uint)p);
                    foreach (ShaderType stage in ShaderStages)
                    {
                        if (!pass.HasShaderStage(stage)) continue;
                        stageCount++;

                        var declared = new List<string>();
                        foreach (LocalKeyword keyword in ShaderUtil.GetPassKeywords(shader, id, stage)) declared.Add(keyword.name);

                        var keywordSets = new List<string[]> { new string[0] };
                        foreach (string keyword in KeywordsTestedBy(declared, changedText))
                        {
                            keywordSets.Add(new[] { keyword });
                            testedAll.Add(keyword);
                        }

                        foreach (string[] keywords in keywordSets)
                        {
                            string variant = "subshader " + s + " pass " + p + (string.IsNullOrEmpty(pass.Name) ? string.Empty : " '" + pass.Name + "'")
                                + " " + stage.ToString().ToLowerInvariant() + (keywords.Length == 0 ? ", no keywords" : ", " + keywords[0]);
                            ShaderData.VariantCompileInfo info = pass.CompileVariant(stage, keywords, platform, target);
                            variants++;
                            collected.Add(info.Messages, variant);

                            // A failed compile always says why; this keeps one that does not from passing silently.
                            if (!info.Success && !HasError(info.Messages)) collected.AddFailure(variant);
                        }
                    }
                }
            }

            if (stageCount == 0) return "no pass has a program to compile";
            return variants + " variant(s) across " + stageCount + " pass stage(s): no keywords"
                + (testedAll.Count == 0 ? "; the changed files test none of this shader's keywords" : ", and each of " + string.Join(", ", testedAll));
        }

        /// <summary>
        /// Compiles every kernel of a compute shader for the editor's graphics device, once with no keywords and
        /// once with each keyword the changed files test. The import only reflects the kernels; they compile on
        /// first use, and querying each kernel is that first use, so the compile happens here and now.
        /// </summary>
        /// <param name="compute">The imported compute shader.</param>
        /// <param name="changedText">Contents of the changed files.</param>
        /// <param name="collected">Receives every message the compiles produce.</param>
        /// <param name="variants">Number of variants compiled.</param>
        /// <returns>A description of what was compiled.</returns>
        private static string CompileComputeVariants(ComputeShader compute, string changedText, ShaderMessageSet collected, out int variants)
        {
            variants = 0;
            List<string> kernels = ComputeKernelNames(compute, out string listError);
            if (listError != null)
            {
                collected.AddFailure(listError);
                return "no kernel could be compiled";
            }

            var keywordSets = new List<string> { null };
            var keywordNames = new List<string>();
            foreach (LocalKeyword keyword in compute.keywordSpace.keywords) keywordNames.Add(keyword.name);
            List<string> tested = KeywordsTestedBy(keywordNames, changedText);
            keywordSets.AddRange(tested);

            foreach (string keyword in keywordSets)
            {
                string variant = keyword == null ? "no keywords" : keyword;
                if (keyword != null) compute.EnableKeyword(keyword);
                try
                {
                    foreach (string kernel in kernels)
                    {
                        string failure = null;
                        try
                        {
                            compute.GetKernelThreadGroupSizes(compute.FindKernel(kernel), out uint _, out uint _, out uint _);
                        }
                        catch (Exception ex)
                        {
                            // Expected for a kernel that failed to compile; its compiler message carries the detail.
                            failure = ex.Message;
                        }

                        variants++;
                        ShaderMessage[] messages = ShaderUtil.GetComputeShaderMessages(compute);
                        collected.Add(messages, "kernel " + kernel + ", " + variant);
                        if (failure != null && !HasError(messages)) collected.AddFailure("kernel " + kernel + ", " + variant + ": " + failure);
                    }
                }
                finally
                {
                    // The keyword was turned on in memory only to compile its variant; the asset is left as found.
                    if (keyword != null) compute.DisableKeyword(keyword);
                }
            }

            return variants + " kernel variant(s) across " + kernels.Count + " kernel(s): no keywords"
                + (tested.Count == 0 ? "; the changed files test none of this shader's keywords" : ", and each of " + string.Join(", ", tested));
        }

        /// <summary>True when one compile's own messages include an error.</summary>
        /// <param name="messages">Messages a single compile returned.</param>
        /// <returns><c>true</c> when any of them is an error.</returns>
        private static bool HasError(ShaderMessage[] messages)
        {
            if (messages == null) return false;
            foreach (ShaderMessage message in messages)
            {
                if (message.severity == ShaderCompilerMessageSeverity.Error) return true;
            }

            return false;
        }

        /// <summary>True when the import reported a ShaderLab parse error, which names no compiler platform.</summary>
        /// <param name="messages">The import's messages for a shader.</param>
        /// <returns><c>true</c> when the shader's ShaderLab did not parse.</returns>
        private static bool HasShaderLabParseError(ShaderMessage[] messages)
        {
            if (messages == null) return false;
            foreach (ShaderMessage message in messages)
            {
                if (message.severity == ShaderCompilerMessageSeverity.Error && (int)message.platform < 0) return true;
            }

            return false;
        }

        /// <summary>Reads a compute shader's kernel names from Unity's own kernel list.</summary>
        /// <param name="compute">The imported compute shader.</param>
        /// <param name="error">Why the list could not be read, or null.</param>
        /// <returns>The kernel names, empty when they could not be read.</returns>
        private static List<string> ComputeKernelNames(ComputeShader compute, out string error)
        {
            var names = new List<string>();
            error = null;
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var platformMethod = typeof(ShaderUtil).GetMethod("GetComputeShaderPlatformCount", flags);
            var countMethod = typeof(ShaderUtil).GetMethod("GetComputeShaderPlatformKernelCount", flags);
            var nameMethod = typeof(ShaderUtil).GetMethod("GetComputeShaderPlatformKernelName", flags);
            if (platformMethod == null || countMethod == null || nameMethod == null)
            {
                error = "this Unity version does not expose the compute shader kernel list, so no kernel could be compiled";
                return names;
            }

            if ((int)platformMethod.Invoke(null, new object[] { compute }) == 0)
            {
                error = "the import reflected this compute shader for no platform, so it has no kernel to compile";
                return names;
            }

            // Every platform the importer reflected carries the same kernel names, so the first one lists them.
            int kernelCount = (int)countMethod.Invoke(null, new object[] { compute, 0 });
            for (int i = 0; i < kernelCount; i++) names.Add((string)nameMethod.Invoke(null, new object[] { compute, 0, i }));

            if (names.Count == 0) error = "the file declares no kernel (#pragma kernel), so there is nothing to compile";
            return names;
        }

        #endregion

        #region Shader Message Set

        /// <summary>
        /// Distinct compiler messages gathered across a shader's variant compiles. The same error usually
        /// appears in several variants, so each is kept once, with the first variant that produced it.
        /// </summary>
        private sealed class ShaderMessageSet
        {
            /// <summary>Asset path the messages belong to, used when a message names no file of its own.</summary>
            private readonly string _path;

            /// <summary>Formatted message lines, in the order they were first seen.</summary>
            private readonly List<string> _lines = new List<string>();

            /// <summary>Keys of the messages already kept.</summary>
            private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>Distinct errors kept so far.</summary>
            public int Errors { get; private set; }

            /// <summary>Distinct warnings kept so far.</summary>
            public int Warnings { get; private set; }

            /// <summary>Creates a set for one shader.</summary>
            /// <param name="path">Asset path of the shader.</param>
            public ShaderMessageSet(string path)
            {
                _path = path;
            }

            /// <summary>Adds the messages of one compile, keeping each distinct message once.</summary>
            /// <param name="messages">Messages Unity returned.</param>
            /// <param name="variant">The variant that produced them, or null for the import's own messages.</param>
            public void Add(ShaderMessage[] messages, string variant)
            {
                if (messages == null) return;
                foreach (ShaderMessage message in messages)
                {
                    bool isError = message.severity == ShaderCompilerMessageSeverity.Error;

                    // A ShaderLab parse error names no file, and a compute shader names only its own file name;
                    // both belong to the shader itself, so they get its full path like every other line.
                    string file = message.file;
                    if (string.IsNullOrEmpty(file) || (file.IndexOf('/') < 0 && _path.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase)))
                    {
                        file = _path;
                    }

                    string key = (isError ? "E" : "W") + "|" + file + "|" + message.line + "|" + message.message;
                    if (!_seen.Add(key)) continue;

                    if (isError) Errors++;
                    else Warnings++;

                    string platform = (int)message.platform < 0 ? "ShaderLab" : message.platform.ToString();
                    _lines.Add("      " + (isError ? "error" : "warning") + " " + file + ":" + message.line + " (" + platform
                        + (variant == null ? string.Empty : ", " + variant) + ") " + message.message);
                }
            }

            /// <summary>Records a compile that failed without a compiler message as an error in its own words.</summary>
            /// <param name="description">The failed variant and what Unity reported.</param>
            public void AddFailure(string description)
            {
                if (!_seen.Add("F|" + description)) return;
                Errors++;
                _lines.Add("      error " + description + " (the compile failed and Unity gave no message)");
            }

            /// <summary>The kept lines, capped, with a count of the rest.</summary>
            /// <param name="max">Most lines to list.</param>
            /// <returns>The lines to print.</returns>
            public List<string> Lines(int max)
            {
                if (_lines.Count <= max) return new List<string>(_lines);
                var shown = _lines.GetRange(0, max);
                shown.Add("      ... and " + (_lines.Count - max) + " more message(s).");
                return shown;
            }
        }

        #endregion
    }

    /// <summary>
    /// Records the paths Unity imports while a shader verb's synchronous import runs, which is how the verdict
    /// learns which shaders an edited include re-imported. It records only between Begin and End, so imports
    /// outside a shader verb are never collected.
    /// </summary>
    internal sealed class ConduitShaderImportCapture : AssetPostprocessor
    {
        /// <summary>Paths imported since Begin, or null while no capture is running.</summary>
        private static List<string> _captured;

        /// <summary>Starts recording imported paths.</summary>
        internal static void Begin()
        {
            _captured = new List<string>();
        }

        /// <summary>Stops recording and returns every path imported since Begin.</summary>
        /// <returns>The imported paths, in the order Unity reported them.</returns>
        internal static List<string> End()
        {
            List<string> result = _captured ?? new List<string>();
            _captured = null;
            return result;
        }

        /// <summary>Unity's import callback; adds the imported paths while a capture is running.</summary>
        /// <param name="importedAssets">Paths Unity imported.</param>
        /// <param name="deletedAssets">Paths Unity deleted.</param>
        /// <param name="movedAssets">Paths Unity moved to.</param>
        /// <param name="movedFromAssetPaths">Paths Unity moved from.</param>
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets,
            string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (_captured != null) _captured.AddRange(importedAssets);
        }
    }
}
