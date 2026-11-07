/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Keeps the installed agent skills in step with the Conduit documents automatically, so editing a
    /// conduit-*.md no longer needs the window's Update skills button pressed by hand. The button stays
    /// as the manual fallback and as the only way to install a NEW client: this class only refreshes
    /// clients that are already installed, so it can never plant a skills folder nobody asked for.
    /// <para>
    /// Three triggers feed one debounced sync, and every one of them is a notification rather than a
    /// timer: every domain reload (all code work ends in one), every import of a Conduit document (bridge
    /// writes and focus scans), and a filesystem watcher that catches a document edited straight on disk
    /// while nothing compiles. Skills are written OUTSIDE Assets, so a sync can never trigger an import,
    /// a reload, or the watcher itself.
    /// </para>
    /// </summary>
    internal static class ConduitSkillsAutoSync
    {
        #region Constants

        /// <summary>Clients the window can install; only the ones actually installed are refreshed.</summary>
        private static readonly string[] Clients = { "Claude Desktop", "Codex", "Cursor", "OpenCode" };

        /// <summary>Seconds a sync request is held so an edit batch regenerates once, not once per file.</summary>
        private const double DEBOUNCE_SECONDS = 2d;

        #endregion

        #region Private State

        /// <summary>Editor time at which a pending debounced sync fires, or 0 when none is pending.</summary>
        private static double _pendingSyncAt;

        /// <summary>Watches the Conduit folder for document edits made outside the asset pipeline.</summary>
        private static FileSystemWatcher _watcher;

        /// <summary>
        /// Set by the watcher's thread and cleared by the editor update, which is the whole reason it
        /// exists: a request has to be raised on the main thread because it reads editor time.
        /// </summary>
        private static volatile bool _diskChangePending;

        #endregion

        #region Triggers

        /// <summary>Arms the update hook and runs one sync after every domain reload.</summary>
        [InitializeOnLoadMethod]
        private static void OnDomainLoad()
        {
            // Import workers load this assembly too; see ConduitBridge's static constructor. Skills must
            // never regenerate from a worker: it would write the very files the main editor is writing.
            if (UnityEngine.Application.isBatchMode) return;

            EditorApplication.update -= OnEditorUpdate;
            EditorApplication.update += OnEditorUpdate;

            // The watcher holds a native handle, so the one from the previous domain is released before
            // another is armed; a reload otherwise leaks one per compile.
            AssemblyReloadEvents.beforeAssemblyReload -= DisposeWatcher;
            AssemblyReloadEvents.beforeAssemblyReload += DisposeWatcher;
            ArmWatcher();

            // Every compile ends in a reload, so this alone keeps skills current for any session that
            // touches code. The delay lets the editor finish waking up before files are statted.
            RequestSync();
        }

        /// <summary>
        /// Watches the Conduit folder for a document edited straight on disk, which no reload or import
        /// reports. Failing to arm costs only this one trigger, so it degrades rather than throwing: the
        /// reload and import triggers still carry every edit made through the editor.
        /// </summary>
        private static void ArmWatcher()
        {
            DisposeWatcher();

            // Resolved here, on the main thread: locating the root reads the asset database, which a
            // watcher callback may not touch. The callbacks need no root, because the watcher is already
            // scoped to this folder and only has to recognise which KIND of file changed.
            string assetRoot = ConduitSkills.ConduitRoot();
            if (assetRoot.Length == 0) return;

            try
            {
                // Unity runs with the project folder as the working directory, so the asset path resolves
                // against it.
                string absoluteRoot = Path.GetFullPath(assetRoot);
                if (!Directory.Exists(absoluteRoot)) return;

                _watcher = new FileSystemWatcher(absoluteRoot)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
                };

                _watcher.Changed += OnWatcherEvent;
                _watcher.Created += OnWatcherEvent;
                _watcher.Deleted += OnWatcherEvent;
                _watcher.Renamed += OnWatcherEvent;

                // An overflowed buffer means changes were dropped, so the only safe reading is that
                // something relevant moved. Syncing on it costs one staleness check and cannot miss.
                _watcher.Error += OnWatcherError;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception error)
            {
                DisposeWatcher();
                Debug.LogWarning("[Conduit] Skills auto-sync could not watch " + assetRoot
                    + ", so a document edited outside the editor will not resync on its own: " + error.Message);
            }
        }

        /// <summary>Releases the watcher and its native handle.</summary>
        private static void DisposeWatcher()
        {
            if (_watcher == null) return;

            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            catch (Exception)
            {
                // A watcher whose directory has gone throws on teardown; the handle is released anyway.
            }

            _watcher = null;
        }

        /// <summary>
        /// Flags a relevant on-disk change. Runs on a thread pool thread, so it does nothing but compare
        /// strings and set a flag - every Unity API, editor time included, belongs to the main thread.
        /// </summary>
        /// <param name="sender">The watcher raising the event.</param>
        /// <param name="args">The changed path.</param>
        private static void OnWatcherEvent(object sender, FileSystemEventArgs args)
        {
            if (args == null || string.IsNullOrEmpty(args.FullPath)) return;
            if (IsSyncRelevantPath(args.FullPath)) _diskChangePending = true;
        }

        /// <summary>Treats a watcher failure as a possible missed change rather than as nothing.</summary>
        /// <param name="sender">The watcher raising the error.</param>
        /// <param name="args">The error carried.</param>
        private static void OnWatcherError(object sender, ErrorEventArgs args)
        {
            _diskChangePending = true;
        }

        /// <summary>
        /// Whether a filesystem path is one whose change should regenerate skills: a Conduit document, or
        /// anything under a Skills folder, which is vendored source copied wholesale. Pure string work,
        /// because this is reached from the watcher's thread.
        /// </summary>
        /// <param name="fullPath">Absolute path reported by the watcher.</param>
        /// <returns>True when a sync should be requested.</returns>
        private static bool IsSyncRelevantPath(string fullPath)
        {
            string normalised = fullPath.Replace('\\', '/');
            return normalised.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                || normalised.IndexOf("/Skills/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Requests a sync soon, folding repeated requests into one regeneration.</summary>
        internal static void RequestSync()
        {
            _pendingSyncAt = EditorApplication.timeSinceStartup + DEBOUNCE_SECONDS;
        }

        /// <summary>Turns a watcher flag into a debounced request, then runs that request once it is due.</summary>
        private static void OnEditorUpdate()
        {
            // Never stat or write while the editor is busy; the reload that follows re-arms everything.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;

            // The watcher cannot raise the request itself, because a request reads editor time. Repeated
            // flags push the deadline forward, which is what makes an edit batch regenerate once.
            if (_diskChangePending)
            {
                _diskChangePending = false;
                RequestSync();
            }

            if (_pendingSyncAt <= 0d || EditorApplication.timeSinceStartup < _pendingSyncAt) return;

            _pendingSyncAt = 0d;
            SyncInstalledClients();
        }

        #endregion

        #region Sync

        /// <summary>
        /// Regenerates the skills of every client whose installed copies are older than the documents.
        /// Staleness and regeneration both reuse the window's own code path, so the automatic result is
        /// byte-identical to a manual Update skills press.
        /// </summary>
        private static void SyncInstalledClients()
        {
            foreach (string client in Clients)
            {
                // Opted-in is ANY installed skill, never all of them: a subject added to the table is
                // missing by definition, and gating on the complete set would skip the client entirely
                // and freeze the sync for the subjects it already has.
                if (!ConduitSkills.Supported(client) || !ConduitSkills.HasAnyInstalled(client)) continue;

                // Done before skill staleness checks so an installed hook follows source, runtime
                // and descriptor moves even when every convention is current. It also migrates the retired
                // Node companion, but never creates a hook for a client that did not opt in.
                ConduitClaudeSessionHook.RefreshInstalled(client);

                // The record is checked beside staleness because a RETIRED subject changes neither: every
                // remaining skill is present and no document's write time moved, so the install that prunes
                // it would otherwise never be reached.
                if (!ConduitSkills.IsStale(client)
                    && ConduitSkills.IsInstalled(client)
                    && !ConduitSkills.ManifestOutOfDate(client)) continue;

                string report = ConduitSkills.Install(client);
                Debug.Log("[Conduit] Skills auto-synced for " + client + ": " + report);
            }
        }

        #endregion
    }

    /// <summary>
    /// Import-time trigger for the skills auto-sync: a Conduit document arriving through the asset
    /// pipeline (a bridge write, a focus scan) requests a sync without waiting for the idle poll.
    /// </summary>
    internal sealed class ConduitSkillsDocWatcher : AssetPostprocessor
    {
        /// <summary>Requests a sync when any imported or moved asset is a Conduit document or skill source.</summary>
        /// <param name="importedAssets">Paths imported this batch.</param>
        /// <param name="deletedAssets">Paths deleted this batch.</param>
        /// <param name="movedAssets">Destination paths of moved assets.</param>
        /// <param name="movedFromAssetPaths">Source paths of moved assets.</param>
        private static void OnPostprocessAllAssets(
            string[] importedAssets,
            string[] deletedAssets,
            string[] movedAssets,
            string[] movedFromAssetPaths)
        {
            if (TouchesConduitDocs(importedAssets) || TouchesConduitDocs(movedAssets))
            {
                ConduitSkillsAutoSync.RequestSync();
            }
        }

        /// <summary>Returns whether any path in a batch is part of the Conduit documents or skill sources.</summary>
        /// <param name="paths">Asset paths from the import batch.</param>
        /// <returns>True when a sync-relevant path is present.</returns>
        private static bool TouchesConduitDocs(string[] paths)
        {
            if (paths == null) return false;

            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;

                // Located rather than written down, so a renamed, moved or packaged Conduit still recognises
                // its own documents instead of silently never syncing again.
                string root = ConduitSkills.ConduitRoot();
                if (root.Length == 0) continue;

                bool insideConduit = path.StartsWith(root + "/", System.StringComparison.OrdinalIgnoreCase);
                if (!insideConduit) continue;

                // Documents become generated skills; anything under Skills/ is vendored source that is
                // copied wholesale. Either kind going stale is what the sync exists to catch.
                if (path.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase)
                    || path.IndexOf("/Skills/", System.StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
