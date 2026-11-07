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
    /// UdonSharp authoring: script creation, deletion, program-asset linking and reimport.
    /// </summary>
    internal static partial class ConduitBridge
    {
        /// <summary>
        /// Requests an AssetDatabase refresh and script compilation when not entering or in Play Mode,
        /// withholding the response until the resulting compile and domain reload settle.
        /// </summary>
        /// <returns>A post-compile report, a confirmation line with editor status, or a Play Mode refusal.</returns>
        private static string RefreshAssetDatabase()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Refusing to refresh AssetDatabase while Play Mode is active or changing." + Environment.NewLine + FormatEditorStatus();
            }

            // Deferring BEFORE triggering matters: the refresh can start the compile immediately, and the
            // pending-response file has to already exist for the post-reload completion to find it.
            // The watchdog releases the response when a refresh finds nothing to recompile, so a no-op
            // refresh still answers instead of hanging.
            var deferred = TryDeferUntilReload(string.Empty);

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            CompilationPipeline.RequestScriptCompilation();

            return deferred
                ? string.Empty // withheld; CompletePendingDeferred writes the real result post-reload
                : "Requested AssetDatabase refresh and script compilation." + Environment.NewLine + FormatEditorStatus();
        }

        /// <summary>Reimports a project asset and defers script compile results when needed.</summary>
        /// <param name="assetPath">Asset path under Assets or Packages.</param>
        /// <returns>Read-back evidence of what the import produced, a queued report, or a validation error.</returns>
        private static string ReimportAsset(string assetPath)
        {
            assetPath = NormalizeAssetPath(assetPath);
            if (string.IsNullOrEmpty(assetPath))
            {
                return "Missing asset path. Usage: reimport <assetPath>";
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Refusing to reimport while Play Mode is active or changing." + Environment.NewLine + FormatEditorStatus();
            }

            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) &&
                !assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return "Asset path must start with Assets/ or Packages/: " + assetPath;
            }

            // A folder placed on disk from outside the editor is imported like a file, which registers it and
            // gives it a .meta; refusing it left a folder no verb could make the AssetDatabase see.
            if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null && !File.Exists(assetPath) && !Directory.Exists(assetPath))
            {
                return "AssetDatabase could not find asset: " + assetPath;
            }

            // Whether the AssetDatabase already held this path decides whether the caller is being told about
            // a first import or a refresh of one it already had, and the two are indistinguishable once the
            // import has run. This verb does import a path Unity has never seen: a file placed under Assets/
            // from outside the editor was measured going from absent to a TextAsset with its own guid and
            // .meta on a single call, so the distinction is a real one rather than a formality.
            var knownBefore = !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath));

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);

            // The import is synchronous, so the result is readable now. It is read back rather than assumed
            // because the previous response said only that a reimport had been requested, which cost every
            // caller a second call to find out what the import had actually produced, or whether it had.
            var evidence = ImportEvidence(assetPath, knownBefore);

            if (PathTriggersScriptCompile(assetPath))
            {
                // Block this single request until the compile + domain reload settles, then return the
                // post-compile Console error report (see BuildPostCompileReport) so the caller gets the
                // outcome inline instead of polling status and reading the console separately.
                // The evidence travels as the argument of the internal note command, never as the command
                // itself: this takes a command to re-run after the reload, so a bare summary is dispatched as
                // a verb and answers "Unknown command" instead of reporting the import.
                if (TryDeferUntilReload("__deferred-note " + evidence))
                {
                    CompilationPipeline.RequestScriptCompilation();
                    return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
                }

                CompilationPipeline.RequestScriptCompilation();
            }

            return evidence + Environment.NewLine + FormatEditorStatus();
        }

        /// <summary>
        /// Describes what an import produced, read back from the AssetDatabase rather than assumed.
        /// </summary>
        /// <param name="assetPath">Path that was imported.</param>
        /// <param name="knownBefore">Whether the AssetDatabase already held this path before the import.</param>
        /// <returns>One line naming the asset's type, name and guid, or why none could be read back.</returns>
        private static string ImportEvidence(string assetPath, bool knownBefore)
        {
            var lead = knownBefore ? "Reimported" : "Imported, new to the project";
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);

            if (asset == null)
            {
                // A path Unity accepted while producing no asset is the case worth naming: the file is on
                // disk and nothing failed, yet there is nothing usable at that path, which a bare success
                // line would have hidden.
                return lead + ": " + assetPath + Environment.NewLine +
                    "No main asset came back from the import, so no importer here yields one.";
            }

            return lead + ": " + assetPath + " -> " + asset.GetType().FullName + " '" + asset.name +
                "', guid " + (string.IsNullOrEmpty(guid) ? "(none)" : guid);
        }

        /// <summary>
        /// Finds the UdonSharpProgramAsset whose source script is the given C# file. Matching is done on the
        /// program asset's own sourceCsScript link rather than on a same-name convention, so a plain C#
        /// script is correctly reported as having none and an unrelated asset that merely shares a file name
        /// is never mistaken for its pair.
        /// </summary>
        /// <param name="csPath">Asset path of the C# script.</param>
        /// <returns>The linked program asset's path, or null when the script has no U# program asset.</returns>
        private static string FindUdonSharpProgramAssetPath(string csPath)
        {
            Type programAssetType = FindLoadedType("UdonSharp.UdonSharpProgramAsset");
            if (programAssetType == null)
            {
                return null;
            }

            MonoScript monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(csPath);
            if (monoScript == null)
            {
                return null;
            }

            FieldInfo sourceField = programAssetType.GetField("sourceCsScript", BindingFlags.Public | BindingFlags.Instance);
            if (sourceField == null)
            {
                return null;
            }

            string searchRoot = "Assets";
            if (csPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                string[] pathParts = csPath.Split('/');
                if (pathParts.Length > 1) searchRoot = "Packages/" + pathParts[1];
            }

            foreach (string guid in AssetDatabase.FindAssets("t:UdonSharpProgramAsset", new[] { searchRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                UnityEngine.Object candidate = AssetDatabase.LoadMainAssetAtPath(path);
                if (candidate == null || !programAssetType.IsInstanceOfType(candidate))
                {
                    continue;
                }

                if ((UnityEngine.Object)sourceField.GetValue(candidate) == monoScript)
                {
                    return path;
                }
            }

            return null;
        }

        /// <summary>
        /// Deletes a UdonSharp behaviour's C# script together with its linked UdonSharpProgramAsset, so the
        /// pair goes in one call instead of leaving an orphaned program asset behind.
        /// </summary>
        /// <param name="args">Asset path for the UdonSharp C# script.</param>
        /// <returns>A deferred response placeholder, a confirmation naming both assets, or a validation error.</returns>
        private static string DeleteUdonSharpScript(string args)
        {
            string raw = (args ?? string.Empty).Trim();
            int pipe = raw.IndexOf('|');
            if (pipe >= 0)
            {
                raw = raw.Substring(0, pipe).Trim();
            }

            string csPath = NormalizeAssetPath(raw);
            if (string.IsNullOrEmpty(csPath))
            {
                return "Missing path. Usage: delete-usharp <Assets/Path/Name.cs | Packages/embedded.id/Path/Name.cs>";
            }

            if (!csPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                csPath += ".cs";
            }

            string pathError;
            if (!TryValidateWritableScriptAssetPath(csPath, out pathError))
            {
                return pathError;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Refusing while Play Mode is active or changing." + Environment.NewLine + FormatEditorStatus();
            }

            if (AssetDatabase.LoadAssetAtPath<MonoScript>(csPath) == null)
            {
                return "No script found at " + csPath + ".";
            }

            // Resolved BEFORE the script is deleted: the link is read off the program asset's sourceCsScript
            // field, and that comparison needs the MonoScript to still exist.
            string programAssetPath = FindUdonSharpProgramAssetPath(csPath);

            if (!AssetDatabase.DeleteAsset(csPath))
            {
                return "Failed to delete script: " + csPath;
            }

            string report;
            if (programAssetPath == null)
            {
                report = "Deleted " + csPath + ". No UdonSharpProgramAsset was linked to it, so nothing else was removed.";
            }
            else if (AssetDatabase.DeleteAsset(programAssetPath))
            {
                report = "Deleted " + csPath + " and its linked program asset " + programAssetPath + ".";
            }
            else
            {
                report = "Deleted " + csPath + " but FAILED to delete its linked program asset " + programAssetPath + " - remove it by hand.";
            }

            // Deleting a script recompiles, and UdonSharp rebuilds every U# program from it, so withhold the
            // response until that settles and carry the summary through with it.
            if (TryDeferUntilReload("__deferred-note " + report))
            {
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            return report + Environment.NewLine + FormatEditorStatus();
        }

        /// <summary>Creates or links a UdonSharp script and matching UdonSharpProgramAsset through reflection.</summary>
        /// <param name="args">Asset path for the UdonSharp C# script.</param>
        /// <returns>A confirmation line, deferred response placeholder, or validation error.</returns>
        private static string CreateUdonSharpScript(string args)
        {
            string raw = (args ?? string.Empty).Trim();
            int pipe = raw.IndexOf('|');
            if (pipe >= 0)
            {
                raw = raw.Substring(0, pipe).Trim();
            }

            string csPath = NormalizeAssetPath(raw);
            if (string.IsNullOrEmpty(csPath))
            {
                return "Missing path. Usage: create-usharp <Assets/Path/Name.cs | Packages/embedded.id/Path/Name.cs>";
            }

            if (!csPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                csPath += ".cs";
            }

            string pathError;
            if (!TryValidateWritableScriptAssetPath(csPath, out pathError))
            {
                return pathError;
            }

            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return "Refusing while Play Mode is active or changing." + Environment.NewLine + FormatEditorStatus();
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

            string dir = (Path.GetDirectoryName(csPath) ?? "Assets").Replace('\\', '/');
            string fileName = Path.GetFileNameWithoutExtension(csPath);
            // Match the U# menu's sanitisation so the class name is valid and matches the file name.
            string className = fileName.Replace(" ", string.Empty).Replace("#", "Sharp");
            string assetPath = dir + "/" + className + ".asset";

            bool wroteScript = false;
            if (!File.Exists(csPath))
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(csPath, UdonSharpTemplateFor(className), System.Text.Encoding.UTF8);
                wroteScript = true;
            }

            AssetDatabase.ImportAsset(csPath, ImportAssetOptions.ForceSynchronousImport);
            MonoScript monoScript = AssetDatabase.LoadAssetAtPath<MonoScript>(csPath);
            if (monoScript == null)
            {
                // The new source needs a full compile before its MonoScript exists, so the link is queued
                // for the post-reload pass.
                string queueError;
                if (!TryQueuePendingUdonSharpCreate(csPath, out queueError))
                {
                    return "Imported " + csPath + " but could not queue its post-compile program-asset link: " + queueError + Environment.NewLine + FormatEditorStatus();
                }

                string sourceState = wroteScript ? "Created" : "Imported";
                string queuedReport = sourceState + " U# source " + csPath + " and completed its program-asset link after the compile.";

                // Withhold the response until that compile settles. Returning now would report a script the
                // caller cannot yet use AND leave a compile in flight, and the editor serves one command at
                // a time, so the caller's next command just queues behind it and times out.
                if (TryDeferUntilReload("__deferred-note " + queuedReport))
                {
                    CompilationPipeline.RequestScriptCompilation();
                    return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
                }

                CompilationPipeline.RequestScriptCompilation();
                return sourceState + " U# source " + csPath + ". Program-asset linking is queued for automatic completion after Unity compiles the MonoScript." + Environment.NewLine + FormatEditorStatus();
            }

            FieldInfo sourceField = programAssetType.GetField("sourceCsScript", BindingFlags.Public | BindingFlags.Instance);
            if (sourceField == null)
            {
                return "Script ready at " + csPath + " but UdonSharpProgramAsset has no 'sourceCsScript' field (UdonSharp version mismatch) - cannot link the program asset.";
            }

            // Shared with create-usharp-batch so the two verbs cannot drift on how a program asset is bound.
            string ready = LinkUdonSharpProgramAsset(csPath, className, assetPath, monoScript, programAssetType, sourceField, wroteScript);

            AssetDatabase.SaveAssets();
            // Targeted import of just this .cs + a compile request - deliberately NOT AssetDatabase.Refresh(),
            // which does a full-project refresh and native-plugin reload (a native-crash vector in large
            // VRChat projects). UdonSharp recompiles its whole program set from a single U# script reimport,
            // so this links + compiles the new behaviour without the heavy refresh.
            AssetDatabase.ImportAsset(csPath, ImportAssetOptions.ForceSynchronousImport);

            // Same reason as the queued path above: this leaves a compile running, and the caller cannot
            // issue anything - not even status - until it settles, so hold the response until it has.
            if (TryDeferUntilReload("__deferred-note " + ready))
            {
                CompilationPipeline.RequestScriptCompilation();
                return string.Empty; // withheld; CompletePendingDeferred writes the real result post-reload
            }

            CompilationPipeline.RequestScriptCompilation();
            return ready + Environment.NewLine + FormatEditorStatus();
        }

        /// <summary>
        /// Builds the source for a new UdonSharp behaviour, preferring UdonSharp's own configured template so
        /// a project's customised template is honoured, and falling back to the stock one it ships with.
        /// SHARED with create-usharp-batch so a new behaviour is identical whichever verb made it.
        /// </summary>
        /// <param name="className">Sanitised class name to substitute into the template.</param>
        /// <returns>Complete C# source for the new behaviour.</returns>
        private static string UdonSharpTemplateFor(string className)
        {
            string template = null;
            Type settingsType = FindLoadedType("UdonSharpEditor.UdonSharpSettings");
            MethodInfo tmplMethod = settingsType?.GetMethod("GetProgramTemplateString", BindingFlags.Public | BindingFlags.Static);
            if (tmplMethod != null)
            {
                try { template = tmplMethod.Invoke(null, new object[] { className }) as string; }
                catch { template = null; }
            }

            if (!string.IsNullOrEmpty(template))
            {
                return template;
            }

            return
                "using UdonSharp;" + Environment.NewLine +
                "using UnityEngine;" + Environment.NewLine +
                "using VRC.SDKBase;" + Environment.NewLine +
                "using VRC.Udon;" + Environment.NewLine + Environment.NewLine +
                "public class " + className + " : UdonSharpBehaviour" + Environment.NewLine +
                "{" + Environment.NewLine +
                "    void Start()" + Environment.NewLine +
                "    {" + Environment.NewLine +
                "    }" + Environment.NewLine +
                "}" + Environment.NewLine;
        }

        /// <summary>
        /// Creates or relinks the UdonSharpProgramAsset binding one already-imported U# source, and returns
        /// the caller's success line. SHARED with create-usharp-batch.
        /// <para>
        /// Deliberately saves no assets and requests NO compile: each caller fires exactly one of each for
        /// its whole unit of work, and a compile per behaviour is the cost the batch verb exists to remove.
        /// </para>
        /// </summary>
        /// <param name="csPath">Normalized asset path of the imported U# source.</param>
        /// <param name="className">Sanitised class name reported back to the caller.</param>
        /// <param name="assetPath">Asset path the program asset should live at.</param>
        /// <param name="monoScript">Loaded MonoScript for the source; must not be null.</param>
        /// <param name="programAssetType">Resolved UdonSharp.UdonSharpProgramAsset type.</param>
        /// <param name="sourceField">Resolved public 'sourceCsScript' field on that type.</param>
        /// <param name="wroteScript">True when the caller authored the source rather than adopting an existing file.</param>
        /// <returns>The ready line describing what was linked.</returns>
        private static string LinkUdonSharpProgramAsset(
            string csPath,
            string className,
            string assetPath,
            MonoScript monoScript,
            Type programAssetType,
            FieldInfo sourceField,
            bool wroteScript)
        {
            bool createdAsset = false;
            UnityEngine.Object existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
            UnityEngine.Object programAsset = (existing != null && programAssetType.IsInstanceOfType(existing)) ? existing : null;
            if (programAsset == null)
            {
                programAsset = ScriptableObject.CreateInstance(programAssetType);
                sourceField.SetValue(programAsset, monoScript);
                AssetDatabase.CreateAsset(programAsset, assetPath);
                createdAsset = true;
            }
            else
            {
                sourceField.SetValue(programAsset, monoScript);
                EditorUtility.SetDirty(programAsset);
            }

            string scriptState = wroteScript ? "created from template" : "kept existing file";
            string assetState = createdAsset ? "created" : "relinked";
            return "U# behaviour ready: " + csPath + " (class " + className + ", " + scriptState +
                "); program asset " + assetState + " at " + assetPath + ".";
        }
            /// <summary>
        /// Routes the UdonSharpAuthoring verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchUdonSharpAuthoring(string verb, string command, string[] parts)
        {
            if (verb == "refresh")
            {
                return RefreshAssetDatabase();
            }

            if (verb == "reimport")
            {
                return ReimportAsset(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "reimport-customer-dll" || verb == "reimport-dll")
            {
                return ReimportAsset("Packages/dev.itsrenji.assetsecurity.customer/Editor/dev.itsrenji.assetsecurity.customer.editor.dll");
            }

            if (verb == "create-usharp" || verb == "create-udonsharp" || verb == "usharp-create")
            {
                return ScriptWriteRefusedInPlayMode(verb) ?? CreateUdonSharpScript(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "delete-usharp" || verb == "delete-udonsharp" || verb == "usharp-delete")
            {
                return ScriptWriteRefusedInPlayMode(verb) ?? DeleteUdonSharpScript(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
