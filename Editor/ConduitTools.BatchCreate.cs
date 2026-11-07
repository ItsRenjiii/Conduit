/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// The create-usharp-batch command: N new UdonSharp behaviours from one folder and one list of names,
    /// written and linked in a single atomic call for ONE compile instead of one compile per behaviour.
    /// <para>
    /// This is the creation counterpart to udon-batch-edit and shares its contract: every name is resolved
    /// and validated before a single byte is written, one failure anywhere refuses the WHOLE batch with
    /// nothing created, and UdonSharp's auto-compile watcher is suppressed for the duration so intermediate
    /// writes cannot fire program compiles against stale assemblies.
    /// </para>
    /// <para>
    /// Template generation and program-asset linking are SHARED with the single-path create-usharp (both
    /// live beside it in ConduitBridge.cs) rather than copied here, so the two verbs cannot drift apart on
    /// what a new behaviour looks like or how its program asset is bound.
    /// </para>
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Batch Create Command

        /// <summary>
        /// Creates N UdonSharp behaviours in one call. Resolves every name against the folder, validates the
        /// whole set, then writes, imports and links them together and requests exactly one compile. The
        /// response is withheld until that compile and its domain reload settle.
        /// </summary>
        /// <param name="args">Pipe-separated folder and name list: <c>&lt;folder&gt; | &lt;Name1, Name2&gt;</c>.</param>
        /// <returns>Per-behaviour evidence and the post-compile report, or a precise refusal.</returns>
        private static string CreateUdonSharpBatchCommand(string args)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Refusing to create scripts while Play Mode is active or changing." + Environment.NewLine + FormatEditorStatus();
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                return "Unity is busy (compiling/importing). Run 'wait-idle' then retry." + Environment.NewLine + FormatEditorStatus();
            }

            Type programAssetType = FindLoadedType("UdonSharp.UdonSharpProgramAsset");
            if (programAssetType == null)
            {
                return "UdonSharp not found (UdonSharp.UdonSharpProgramAsset). Is the VRChat Worlds SDK + UdonSharp installed?";
            }

            FieldInfo sourceField = programAssetType.GetField("sourceCsScript", BindingFlags.Public | BindingFlags.Instance);
            if (sourceField == null)
            {
                return "UdonSharpProgramAsset has no 'sourceCsScript' field (UdonSharp version mismatch); cannot link program assets. Nothing was created.";
            }

            List<BatchCreatePlan> plans;
            string parseError = TryBuildBatchCreatePlans(args, out plans);
            if (parseError != null)
            {
                return parseError;
            }

            // ── Mutation phase: suppress the U# watcher, write everything, import once, link once ───
            bool suppressed = SuppressAutoCompileForBatch();

            var written = new List<BatchCreatePlan>();
            foreach (BatchCreatePlan plan in plans)
            {
                try
                {
                    string directory = Path.GetDirectoryName(plan.CsPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Encoding.UTF8 (with BOM) deliberately matches what single-path create-usharp writes,
                    // so a behaviour's bytes do not depend on which verb made it.
                    File.WriteAllText(plan.CsPath, UdonSharpTemplateFor(plan.ClassName), Encoding.UTF8);
                    written.Add(plan);
                }
                catch (Exception ex)
                {
                    string rollback = RollBackCreatedScripts(written);
                    if (suppressed)
                    {
                        RestoreAutoCompileFromStash("batch create write failure");
                    }

                    return "I/O failure writing " + plan.CsPath + ": " + ex.Message + ". " + rollback;
                }
            }

            // One asset-editing scope around every import, so the N sources land as a single import pass
            // rather than N separate synchronous ones. The MonoScripts are only loadable after Stop.
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (BatchCreatePlan plan in written)
                {
                    AssetDatabase.ImportAsset(plan.CsPath, ImportAssetOptions.ForceSynchronousImport);
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            var evidence = new List<string>();
            foreach (BatchCreatePlan plan in written)
            {
                MonoScript monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(plan.CsPath);
                if (monoScript == null)
                {
                    // A MonoScript normally resolves the moment its .cs is imported - compilation is only
                    // needed for GetClass(). If one does not, the batch cannot bind that program asset, and
                    // a half-linked batch is worse than none: roll the whole thing back and say so.
                    string rollback = RollBackCreatedScripts(written);
                    if (suppressed)
                    {
                        RestoreAutoCompileFromStash("batch create link failure");
                    }

                    return "Imported " + plan.CsPath + " but Unity did not produce its MonoScript, so its program asset could not be linked. " +
                        rollback + " Create this one with 'create-usharp " + plan.CsPath + "', which queues the link for after the next compile.";
                }

                evidence.Add(LinkUdonSharpProgramAsset(plan.CsPath, plan.ClassName, plan.AssetPath, monoScript, programAssetType, sourceField, true));
            }

            AssetDatabase.SaveAssets();

            string suppressionNote = suppressed
                ? " UdonSharp auto-compile was suppressed for the batch and restores after the reload."
                : string.Empty;
            // ONE line, deliberately. TryDeferUntilReload persists the note as a single line beneath the
            // response path, so a multi-line summary arrives back TRUNCATED at its first newline and every
            // evidence line is silently lost - which is exactly why udon-batch-edit joins with "; " too.
            string summary = "Created " + written.Count + " U# behaviour(s)." + suppressionNote + " " +
                string.Join(" ", evidence);

            // One deliberate compile for the whole batch, and the response is withheld until it and the
            // domain reload settle - the editor serves one command at a time, so returning early would
            // leave the caller's next command queued behind a compile it does not know about.
            if (suppressed)
            {
                ArmAutoCompileRestoreFallback();
            }

            if (TryDeferUntilReload("__deferred-note " + summary))
            {
                CompilationPipeline.RequestScriptCompilation();
                return string.Empty; // Withheld; the deferred completion returns the summary plus the post-compile report.
            }

            CompilationPipeline.RequestScriptCompilation();
            return summary + Environment.NewLine + FormatEditorStatus();
        }

        #endregion

        #region Batch Create Planning

        /// <summary>One behaviour the batch intends to create: where its source goes and what it is called.</summary>
        private sealed class BatchCreatePlan
        {
            /// <summary>Normalized writable project path of the .cs to write.</summary>
            public string CsPath;

            /// <summary>Sanitised class name, which is also the program asset's file name.</summary>
            public string ClassName;

            /// <summary>Normalized writable project path of the program asset created beside the source.</summary>
            public string AssetPath;
        }

        /// <summary>
        /// Resolves the folder and name list into a validated plan per behaviour. EVERY name is checked
        /// before the caller writes anything, so a bad name at the end of the list cannot leave half a batch
        /// of behaviours on disk.
        /// </summary>
        /// <param name="args">Raw command remainder, optionally pipe-separated into folder and names.</param>
        /// <param name="plans">Receives one validated plan per behaviour on success.</param>
        /// <returns>Null when every name validated, otherwise the refusal to return verbatim.</returns>
        private static string TryBuildBatchCreatePlans(string args, out List<BatchCreatePlan> plans)
        {
            plans = new List<BatchCreatePlan>();

            const string usage = "Usage: create-usharp-batch <folder> | <Name1, Name2, ...>  " +
                "A name may instead be a full Assets/... or Packages/<embedded-id>/... path, which overrides the folder.";

            // The signature is "create-usharp-batch <folder> | <names>". With no pipe at all, treat the whole
            // remainder as names, which only works when each one is a full writable project path.
            string payload = (args ?? string.Empty).Trim();
            int pipe = payload.IndexOf('|');
            string folderSection = pipe >= 0 ? payload.Substring(0, pipe).Trim() : string.Empty;
            string nameSection = pipe >= 0 ? payload.Substring(pipe + 1).Trim() : payload;

            if (nameSection.Length == 0)
            {
                return usage;
            }

            string baseFolder = folderSection.Length > 0 ? NormalizeAssetPath(folderSection).TrimEnd('/') : string.Empty;
            if (baseFolder.Length > 0)
            {
                string folderError;
                if (!TryValidateWritableScriptAssetPath(baseFolder + "/__ConduitPathProbe.cs", out folderError))
                {
                    return folderError + " Nothing was created.";
                }
            }

            string[] rawNames = nameSection.Split(
                new[] { ',', ';', '\r', '\n', '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (rawNames.Length == 0)
            {
                return usage;
            }

            var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < rawNames.Length; i++)
            {
                string raw = rawNames[i].Trim().Trim('"');
                if (raw.Length == 0)
                {
                    continue;
                }

                string position = "name " + (i + 1) + " ('" + raw + "')";

                string csPath = raw.Replace('\\', '/');
                bool isFullProjectPath = csPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                    csPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase);
                if (!isFullProjectPath)
                {
                    if (baseFolder.Length == 0)
                    {
                        return "Refused at " + position + ": no folder was given and this is not a full writable project path. " +
                            usage + " Nothing was created.";
                    }

                    csPath = baseFolder + "/" + csPath;
                }

                csPath = NormalizeAssetPath(csPath);
                if (!csPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    csPath += ".cs";
                }

                string pathError;
                if (!TryValidateWritableScriptAssetPath(csPath, out pathError))
                {
                    return "Refused at " + position + ": " + pathError + " Nothing was created.";
                }

                // Same sanitisation as the U# menu and single-path create-usharp, so the class name matches
                // the file name and the program asset sits beside it under that name.
                string className = (Path.GetFileNameWithoutExtension(csPath) ?? string.Empty)
                    .Replace(" ", string.Empty)
                    .Replace("#", "Sharp");

                if (!IsValidClassIdentifier(className))
                {
                    return "Refused at " + position + ": '" + className + "' is not a valid C# class name. " +
                        "Use a letter or underscore first, then letters, digits or underscores. Nothing was created.";
                }

                string directory = (Path.GetDirectoryName(csPath) ?? "Assets").Replace('\\', '/');
                string assetPath = directory + "/" + className + ".asset";

                if (File.Exists(csPath))
                {
                    return "Refused at " + position + ": " + csPath + " already exists. This verb only CREATES; " +
                        "use 'create-usharp " + csPath + "' to relink an existing file, or udon-batch-edit to change one. Nothing was created.";
                }

                if (File.Exists(assetPath))
                {
                    return "Refused at " + position + ": a program asset already exists at " + assetPath + ". Nothing was created.";
                }

                if (!seenSources.Add(csPath))
                {
                    return "Refused at " + position + ": " + csPath + " appears twice in this batch. Nothing was created.";
                }

                if (!seenAssets.Add(assetPath))
                {
                    return "Refused at " + position + ": class name '" + className + "' collides with another name in this batch at " +
                        assetPath + ". Nothing was created.";
                }

                plans.Add(new BatchCreatePlan { CsPath = csPath, ClassName = className, AssetPath = assetPath });
            }

            if (plans.Count == 0)
            {
                return usage;
            }

            return null;
        }

        /// <summary>True when the text is a legal C# identifier, so the generated class will compile.</summary>
        /// <param name="className">Candidate class name, already sanitised.</param>
        /// <returns>True when the name is usable as a class identifier.</returns>
        private static bool IsValidClassIdentifier(string className)
        {
            if (string.IsNullOrEmpty(className))
            {
                return false;
            }

            if (!char.IsLetter(className[0]) && className[0] != '_')
            {
                return false;
            }

            for (int i = 1; i < className.Length; i++)
            {
                if (!char.IsLetterOrDigit(className[i]) && className[i] != '_')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Deletes the sources and program assets a failed batch had already created, so a refusal really
        /// does leave the project as it was found.
        /// </summary>
        /// <param name="written">Plans whose source files reached disk.</param>
        /// <returns>A sentence describing what was undone, for the refusal message.</returns>
        private static string RollBackCreatedScripts(List<BatchCreatePlan> written)
        {
            if (written == null || written.Count == 0)
            {
                return "Nothing was created.";
            }

            var failures = new List<string>();
            foreach (BatchCreatePlan plan in written)
            {
                try
                {
                    if (File.Exists(plan.AssetPath))
                    {
                        AssetDatabase.DeleteAsset(plan.AssetPath);
                    }

                    if (File.Exists(plan.CsPath) && !AssetDatabase.DeleteAsset(plan.CsPath))
                    {
                        File.Delete(plan.CsPath);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(plan.CsPath + " (" + ex.Message + ")");
                }
            }

            if (failures.Count > 0)
            {
                return "Rolled back " + (written.Count - failures.Count) + " of " + written.Count +
                    " created file(s); could not remove: " + string.Join("; ", failures) + ".";
            }

            return "Rolled back all " + written.Count + " created file(s).";
        }

        #endregion
    }
}
