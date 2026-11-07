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
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Project-local source history, independent of Unity imports and Library. Revision ids remain immutable
    /// while exact-byte records are compressed into one atomically replaced ZIP pack per source. Legacy loose
    /// snapshots migrate when that source next changes. Reads bound decompression and verify payload hashes.
    /// Capture is best effort and never throws into the source transaction. All store work runs off-thread.
    /// </summary>
    internal sealed class ConduitFileHistory
    {
        #region Limits and state

        /// <summary>Default number of retained versions of each source path.</summary>
        internal const int DEFAULT_VERSIONS = 20;

        /// <summary>Default ceiling including snapshot headers and the settings file.</summary>
        internal const long DEFAULT_BYTES = 256L * 1024 * 1024;

        /// <summary>Bytes in the unit displayed by the storage control.</summary>
        internal const long MIB_BYTES = 1024L * 1024;

        /// <summary>Largest supported user-selected history budget in MiB.</summary>
        internal const int MAX_BUDGET_MIB = 1024;

        /// <summary>Signals a possible store change; subscribers may be called on a worker thread.</summary>
        internal static event Action Changed;

        /// <summary>Allocation bound when opening a snapshot, independent of editable retention settings.</summary>
        private const int MAX_SNAPSHOT_BYTES = 512 * 1024 * 1024;

        /// <summary>Maximum encoded metadata length; source bytes follow it without base64 expansion.</summary>
        private const int MAX_HEADER_BYTES = 16384;

        /// <summary>Serializes store writers in this managed domain without an unbounded lock wait.</summary>
        private static readonly object _gate = new object();

        /// <summary>Canonical project root, captured on the editor thread.</summary>
        private readonly string _projectRoot;

        /// <summary>History root, which is always outside Assets and survives deletion of Library.</summary>
        private readonly string _root;

        /// <summary>Resolved history folder for read-back and diagnostics.</summary>
        internal string Root => _root;

        /// <summary>Owner-editable retention configuration.</summary>
        internal sealed class Settings
        {
            /// <summary>Last N versions to retain per path; identical consecutive captures normally reuse one.</summary>
            internal int VersionsPerFile = DEFAULT_VERSIONS;

            /// <summary>Maximum total bytes in the history store.</summary>
            internal long MaxBytes = DEFAULT_BYTES;
        }

        /// <summary>An independently readable snapshot, including its provenance.</summary>
        internal sealed class Snapshot
        {
            /// <summary>Versioned header with path, time, hashes and initiating operation.</summary>
            internal JObject Metadata;

            /// <summary>Exact captured version bytes, populated only for a content read.</summary>
            internal byte[] Bytes;
        }

        /// <summary>Creates a handle without touching the filesystem.</summary>
        /// <param name="projectRoot">Absolute project folder.</param>
        internal ConduitFileHistory(string projectRoot)
        {
            _projectRoot = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            _root = Path.Combine(_projectRoot, ".conduit", "history");
        }

        #endregion

        #region Capture

        /// <summary>
        /// Saves exact bytes in a compressed per-source pack. Every replacement pack is staged and verified
        /// before publication; retention counts compressed disk usage and removes oldest versions first.
        /// A history failure never changes the source writer's commit or rollback.
        /// </summary>
        /// <param name="assetPath">Validated project-relative source path.</param>
        /// <param name="original">Exact bytes already held by the writer.</param>
        /// <param name="operation">Initiating verb.</param>
        /// <param name="client">Authenticated harness, or empty when unavailable.</param>
        /// <param name="jobId">Initiating durable job.</param>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <param name="state">Before-attempted-write or after-write provenance.</param>
        /// <returns>Saved or reused revision evidence, or a specific storage warning.</returns>
        internal string Capture(string assetPath, byte[] original, string operation, string client,
            string jobId, CancellationToken cancellation, string state = "before-attempted-write")
        {
            bool entered = false;
            bool published = false;
            PackPlan capturePlan = null;
            string revision = null;
            var plans = new Dictionary<string, PackPlan>(PathComparer);
            try
            {
                entered = Monitor.TryEnter(_gate, 1000);
                if (!entered) throw new IOException("history writer is busy");
                cancellation.ThrowIfCancellationRequested();
                string path = CanonicalPath(assetPath);
                if (state != "before-attempted-write" && state != "after-write")
                    throw new ArgumentException("unsupported history version state");
                if (original == null || original.LongLength > MAX_SNAPSHOT_BYTES)
                    throw new IOException("version bytes are unavailable or exceed the snapshot allocation limit");
                EnsureLocalPath(_root);
                Directory.CreateDirectory(_root);
                string lockPath = Path.Combine(_root, "writer.lock");
                EnsureLocalPath(lockPath);
                using (var writerLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                {
                    Settings settings = ReadSettings(true);
                    List<VersionRecord> records = Inventory(cancellation, out long total);
                    string directory = DirectoryFor(path);
                    string digest = Digest(original);
                    VersionRecord latest = records.FindLast(record => PathComparer.Equals(record.Directory, directory));
                    bool reuse = false;
                    if (latest != null)
                    {
                        try { reuse = (string)ReadRecord(latest, true, cancellation).Metadata["sha256"] == digest; }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception) { /* Corrupt bytes must never qualify as an identical saved version. */ }
                    }
                    if (reuse && total <= settings.MaxBytes && WithinVersionLimit(records, settings.VersionsPerFile)
                        && Directory.GetFiles(directory, "*.snapshot").Length == 0)
                        return "History already contains these exact bytes: " + path + " revision " + latest.Revision + ".";

                    VersionRecord current = latest;
                    if (!reuse)
                    {
                        DateTime now = DateTime.UtcNow;
                        revision = now.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
                        current = new VersionRecord
                        {
                            Directory = directory,
                            Revision = revision,
                            Pending = new Snapshot
                            {
                                Bytes = original,
                                Metadata = new JObject
                                {
                                    ["format"] = 1, ["revision"] = revision, ["path"] = path,
                                    ["capturedUtc"] = now.ToString("O", CultureInfo.InvariantCulture),
                                    ["operation"] = operation, ["client"] = client ?? string.Empty,
                                    ["jobId"] = jobId ?? string.Empty, ["state"] = state,
                                    ["byteCount"] = original.LongLength, ["sha256"] = digest
                                }
                            }
                        };
                        records.Add(current);
                        records.Sort((left, right) => string.CompareOrdinal(left.Revision, right.Revision));
                    }
                    revision = current.Revision;
                    var kept = new HashSet<VersionRecord>(records);
                    var counts = new Dictionary<string, int>(PathComparer);
                    counts[directory] = 1;
                    var changed = new HashSet<string>(PathComparer) { directory };
                    for (int i = records.Count - 1; i >= 0; i--)
                    {
                        VersionRecord record = records[i];
                        if (ReferenceEquals(record, current)) continue;
                        counts.TryGetValue(record.Directory, out int count);
                        counts[record.Directory] = ++count;
                        if (count <= settings.VersionsPerFile) continue;
                        kept.Remove(record);
                        changed.Add(record.Directory);
                    }
                    // Independent packs are small in ordinary source work; staging bounds memory to one version at a time.
                    foreach (string folder in changed) RebuildPackPlan(folder, records, kept, plans, cancellation);
                    foreach (VersionRecord record in records)
                    {
                        if (ProjectedBytes(total, plans) <= settings.MaxBytes) break;
                        if (ReferenceEquals(record, current) || !kept.Remove(record)) continue;
                        RebuildPackPlan(record.Directory, records, kept, plans, cancellation);
                    }
                    if (ProjectedBytes(total, plans) > settings.MaxBytes)
                        throw new IOException("compressed version and non-prunable files exceed maxBytes; no versions were pruned");

                    cancellation.ThrowIfCancellationRequested();
                    capturePlan = plans[directory];
                    CommitPack(capturePlan);
                    published = true;
                    foreach (PackPlan plan in plans.Values)
                    {
                        if (PathComparer.Equals(plan.Directory, directory)) continue;
                        cancellation.ThrowIfCancellationRequested();
                        CommitPack(plan);
                    }
                    return "History " + (reuse ? "packed" : "saved") + ": " + path + " revision " + revision
                        + " (" + original.Length + " original bytes, sha256 " + digest + "; "
                        + (records.Count - kept.Count) + " older snapshot(s) pruned).";
                }
            }
            catch (Exception error)
            {
                published |= capturePlan != null && capturePlan.Published;
                return "History warning for " + assetPath + ": " + error.Message + ". "
                    + (published ? "Revision " + revision + " was saved, but history maintenance is incomplete. "
                        : "No new recoverable revision was confirmed; existing packs were not replaced before staging completed. ")
                    + "Edit/rollback semantics are unchanged.";
            }
            finally
            {
                foreach (PackPlan plan in plans.Values)
                    if (plan.Stage != null) { try { File.Delete(plan.Stage); } catch { } }
                if (entered) { Monitor.Exit(_gate); NotifyChanged(); }
            }
        }

        #endregion

        #region Inspection

        /// <summary>Lists packed and legacy headers newest first, keeping revision ids stable during packing.</summary>
        /// <param name="assetPath">Source whose history is requested.</param>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <returns>Version metadata and explicit read errors, without changing the store.</returns>
        internal JObject List(string assetPath, CancellationToken cancellation)
        {
            string path = CanonicalPath(assetPath);
            string directory = DirectoryFor(path);
            var versions = new JArray();
            var errors = new JArray();
            var records = new List<VersionRecord>();
            try { records = DirectoryRecords(directory, cancellation); }
            catch (Exception error) { errors.Add(error.Message); }
            records.Sort((left, right) => string.CompareOrdinal(right.Revision, left.Revision));
            foreach (VersionRecord record in records)
            {
                cancellation.ThrowIfCancellationRequested();
                try { versions.Add(ReadRecord(record, false, cancellation).Metadata); }
                catch (Exception error) { errors.Add(record.Revision + ": " + error.Message); }
            }
            var result = new JObject
            {
                ["path"] = path, ["store"] = ".conduit/history", ["versions"] = versions, ["errors"] = errors,
                ["note"] = "Versions are compressed into per-file packs; legacy snapshots stay readable and migrate on the next write to that source. Before-attempted-write and after-write identify provenance. After-write means saved bytes, not a successful compile. Any retained older or newer revision can be restored. Payload hashes are verified on content reads."
            };
            try
            {
                Settings settings = ReadSettings(false);
                result["retention"] = new JObject { ["versionsPerFile"] = settings.VersionsPerFile, ["maxBytes"] = settings.MaxBytes };
            }
            catch (Exception error) { result["settingsError"] = error.Message; }
            return result;
        }

        /// <summary>Reads and verifies a revision from its packed entry or legacy loose snapshot.</summary>
        /// <param name="assetPath">Expected source path.</param>
        /// <param name="revision">Exact stored revision id.</param>
        /// <param name="includeBytes">Whether to decompress and hash-check the payload.</param>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <returns>Validated metadata and, when requested, exact original bytes.</returns>
        internal Snapshot Read(string assetPath, string revision, bool includeBytes, CancellationToken cancellation)
        {
            if (!IsRevision(revision)) throw new ArgumentException("revision must be an exact id returned by file-history");
            string path = CanonicalPath(assetPath);
            VersionRecord record = DirectoryRecords(DirectoryFor(path), cancellation).Find(item => item.Revision == revision);
            if (record == null) throw new FileNotFoundException("history revision was not found: " + revision);
            Snapshot snapshot = ReadRecord(record, includeBytes, cancellation);
            if (!string.Equals((string)snapshot.Metadata["path"], path, PathComparison))
                throw new InvalidDataException("snapshot source does not match the requested path");
            return snapshot;
        }

        #endregion

        #region Settings and retention

        /// <summary>A read-only storage measurement and the owner settings it was measured against.</summary>
        internal sealed class Usage
        {
            /// <summary>Actual file bytes currently in the whole history folder.</summary>
            internal long Bytes;

            /// <summary>Owner's configured ceiling, not the current consumption.</summary>
            internal long MaxBytes;

            /// <summary>Number of retained revisions across packs and legacy snapshots.</summary>
            internal int Versions;

            /// <summary>Per-file retention configured independently of the byte ceiling.</summary>
            internal int VersionsPerFile;

            /// <summary>Whole-MiB floor protecting all bytes currently in the store.</summary>
            internal int MinimumMiB => (int)Math.Max(1L, (Bytes + MIB_BYTES - 1) / MIB_BYTES);
        }

        /// <summary>Measures the store without creating it or pruning any file.</summary>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <returns>Current consumption, snapshot count and settings.</returns>
        internal Usage ReadUsage(CancellationToken cancellation)
        {
            if (!Monitor.TryEnter(_gate, 1000)) throw new IOException("File history is busy. Try again shortly.");
            try { return MeasureUsage(cancellation); }
            finally { Monitor.Exit(_gate); }
        }

        /// <summary>Changes only the byte budget, refusing a ceiling below fresh on-disk usage without pruning.</summary>
        /// <param name="mib">Requested whole MiB, between one and the supported maximum.</param>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <returns>Storage measurement after the atomic settings update.</returns>
        internal Usage SetLimitMiB(int mib, CancellationToken cancellation)
        {
            if (mib < 1 || mib > MAX_BUDGET_MIB) throw new ArgumentOutOfRangeException(nameof(mib));
            if (!Monitor.TryEnter(_gate, 1000)) throw new IOException("File history is busy. Try again shortly.");
            string temporary = null;
            try
            {
                cancellation.ThrowIfCancellationRequested();
                EnsureLocalPath(_root);
                Directory.CreateDirectory(_root);
                string lockPath = Path.Combine(_root, "writer.lock");
                EnsureLocalPath(lockPath);
                using (var writerLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                {
                    Usage usage = MeasureUsage(cancellation);
                    long limit = mib * MIB_BYTES;
                    if (limit < usage.Bytes)
                        throw new InvalidOperationException("Saved versions already need " + usage.MinimumMiB + " MiB. Choose that amount or more.");
                    if (limit == usage.MaxBytes) return usage;
                    string settings = Path.Combine(_root, "settings.json");
                    byte[] bytes = Encoding.UTF8.GetBytes(new JObject
                    {
                        ["versionsPerFile"] = usage.VersionsPerFile,
                        ["maxBytes"] = limit
                    }.ToString(Formatting.Indented) + "\n");
                    long previousSize = File.Exists(settings) ? new FileInfo(settings).Length : 0;
                    if (usage.Bytes - previousSize + bytes.Length > limit)
                        throw new InvalidOperationException("Choose one more MiB to leave room for the history settings.");
                    temporary = Path.Combine(_root, "settings-" + Guid.NewGuid().ToString("N") + ".tmp");
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush(true);
                    }
                    cancellation.ThrowIfCancellationRequested();
                    if (File.Exists(settings)) File.Replace(temporary, settings, null);
                    else File.Move(temporary, settings);
                    temporary = null;
                    return MeasureUsage(cancellation);
                }
            }
            finally
            {
                if (temporary != null) { try { File.Delete(temporary); } catch { } }
                Monitor.Exit(_gate);
                NotifyChanged();
            }
        }

        /// <summary>Reads usage while the caller holds the store's managed writer gate.</summary>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <returns>Read-only usage and owner settings.</returns>
        private Usage MeasureUsage(CancellationToken cancellation)
        {
            Settings settings = ReadSettings(false);
            List<VersionRecord> files = Inventory(cancellation, out long bytes, false);
            return new Usage { Bytes = bytes, Versions = files.Count, MaxBytes = settings.MaxBytes, VersionsPerFile = settings.VersionsPerFile };
        }

        /// <summary>Notifies observers without letting UI failures affect a source write or settings commit.</summary>
        private static void NotifyChanged()
        {
            try { Changed?.Invoke(); } catch { }
        }

        /// <summary>Checks per-source counts before reusing an unchanged version without a retention pass.</summary>
        /// <param name="files">Inventoried revisions.</param>
        /// <param name="limit">Maximum revisions per source.</param>
        /// <returns>Whether every source already meets the retention count.</returns>
        private static bool WithinVersionLimit(List<VersionRecord> files, int limit)
        {
            var counts = new Dictionary<string, int>(PathComparer);
            foreach (VersionRecord file in files)
            {
                counts.TryGetValue(file.Directory, out int count);
                counts[file.Directory] = count + 1;
                if (count >= limit) return false;
            }
            return true;
        }

        /// <summary>Reads owner settings or creates the default file on the first capture.</summary>
        /// <param name="create">True only while holding the writer lock.</param>
        /// <returns>Strictly validated retention values.</returns>
        internal Settings ReadSettings(bool create)
        {
            string file = Path.Combine(_root, "settings.json");
            EnsureLocalPath(file);
            if (!File.Exists(file))
            {
                if (create)
                {
                    byte[] defaults = Encoding.UTF8.GetBytes(new JObject
                    {
                        ["versionsPerFile"] = DEFAULT_VERSIONS,
                        ["maxBytes"] = DEFAULT_BYTES
                    }.ToString(Formatting.Indented) + "\n");
                    using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    {
                        stream.Write(defaults, 0, defaults.Length);
                        stream.Flush(true);
                    }
                }
                return new Settings();
            }
            if (new FileInfo(file).Length > 4096) throw new InvalidDataException("history settings.json exceeds 4096 bytes");
            JObject values = JObject.Parse(File.ReadAllText(file));
            foreach (JProperty property in values.Properties())
                if (property.Name != "versionsPerFile" && property.Name != "maxBytes")
                    throw new InvalidDataException("unknown history setting: " + property.Name);
            if (values["versionsPerFile"]?.Type != JTokenType.Integer || values["maxBytes"]?.Type != JTokenType.Integer)
                throw new InvalidDataException("history settings require integer versionsPerFile and maxBytes");
            long versions = (long)values["versionsPerFile"];
            long bytes = (long)values["maxBytes"];
            if (versions < 1 || versions > 1000 || bytes < 1024 || bytes > 16L * 1024 * 1024 * 1024)
                throw new InvalidDataException("versionsPerFile must be 1..1000 and maxBytes 1024..17179869184");
            return new Settings { VersionsPerFile = (int)versions, MaxBytes = bytes };
        }

        /// <summary>Counts actual store bytes and inventories revisions inside packs and legacy files.</summary>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <param name="total">Actual physical file bytes, including metadata and unknown files.</param>
        /// <param name="cleanStages">True only while capture owns the cross-process writer lock.</param>
        /// <returns>Unique revisions sorted oldest first.</returns>
        private List<VersionRecord> Inventory(CancellationToken cancellation, out long total, bool cleanStages = true)
        {
            total = 0;
            var records = new List<VersionRecord>();
            EnsureLocalPath(_root);
            if (!Directory.Exists(_root)) return records;
            foreach (string file in Directory.EnumerateFiles(_root))
            {
                cancellation.ThrowIfCancellationRequested();
                EnsureLocalPath(file);
                total = checked(total + new FileInfo(file).Length);
            }
            foreach (string directory in Directory.EnumerateDirectories(_root))
            {
                cancellation.ThrowIfCancellationRequested();
                EnsureLocalPath(directory);
                if (!IsDigest(Path.GetFileName(directory)) || Directory.GetDirectories(directory).Length != 0)
                    throw new IOException("unexpected directory in history store: " + Path.GetFileName(directory));
                foreach (string file in Directory.EnumerateFiles(directory))
                {
                    cancellation.ThrowIfCancellationRequested();
                    EnsureLocalPath(file);
                    if (cleanStages && file.EndsWith(".tmp", StringComparison.Ordinal)
                        && IsRevision(Path.GetFileNameWithoutExtension(file))) { File.Delete(file); continue; }
                    total = checked(total + new FileInfo(file).Length);
                }
                records.AddRange(DirectoryRecords(directory, cancellation));
            }
            records.Sort((left, right) => string.CompareOrdinal(left.Revision, right.Revision));
            return records;
        }

        #endregion

        #region Path and integrity helpers

        /// <summary>Filesystem path comparison follows the platform's ordinary casing convention.</summary>
        private static StringComparison PathComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        /// <summary>Canonicalizes a source identity within Assets or Packages without following links.</summary>
        /// <param name="assetPath">Project-relative path, already admitted by the bridge.</param>
        /// <returns>Canonical project-relative path.</returns>
        internal string CanonicalPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath) || Path.IsPathRooted(assetPath) || assetPath.IndexOf(':') >= 0)
                throw new ArgumentException("history requires a project-relative source path");
            string full = Path.GetFullPath(Path.Combine(_projectRoot, assetPath));
            string prefix = _projectRoot + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, PathComparison)) throw new ArgumentException("path escapes the project");
            string relative = full.Substring(prefix.Length).Replace('\\', '/');
            if (!relative.StartsWith("Assets/", PathComparison) && !relative.StartsWith("Packages/", PathComparison))
                throw new ArgumentException("history only covers Assets/ and project-local embedded Packages/");
            EnsureLocalPath(full);
            return relative;
        }

        /// <summary>Rejects symlinks and reparse points below the project root, including a redirected store.</summary>
        /// <param name="path">Resolved path whose existing ancestors must stay local.</param>
        internal void EnsureLocalPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(_projectRoot + Path.DirectorySeparatorChar, PathComparison))
                throw new IOException("history path escapes the project");
            for (string current = full; !string.Equals(current, _projectRoot, PathComparison); current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("history refuses a symlink or reparse point: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        /// <summary>Uses a path digest for portable filenames without copying a source tree into the store.</summary>
        /// <param name="path">Canonical project-relative path.</param>
        /// <returns>The file's snapshot directory.</returns>
        private string DirectoryFor(string path)
        {
            string identity = Path.DirectorySeparatorChar == '\\' ? path.ToLowerInvariant() : path;
            return Path.Combine(_root, Digest(Encoding.UTF8.GetBytes(identity)));
        }

        /// <summary>Computes an exact-byte SHA-256 digest.</summary>
        /// <param name="bytes">Bytes to fingerprint.</param>
        /// <returns>Lowercase hexadecimal digest.</returns>
        internal static string Digest(byte[] bytes)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
        }

        /// <summary>Validates a digest without accepting separators or path syntax.</summary>
        /// <param name="value">Candidate digest.</param>
        /// <returns>True only for 64 lowercase hexadecimal characters.</returns>
        internal static bool IsDigest(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (char character in value)
                if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f')) return false;
            return true;
        }

        /// <summary>Admits only timestamp-plus-random revision ids produced by this store.</summary>
        /// <param name="value">Candidate revision.</param>
        /// <returns>Whether it has the exact stored identifier shape.</returns>
        private static bool IsRevision(string value)
        {
            if (value == null || value.Length != 52 || value[19] != '-') return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (i == 19) continue;
                char character = value[i];
                if (i < 19 ? character < '0' || character > '9'
                    : !(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f')) return false;
            }
            return true;
        }

        #endregion
        #region Packed storage

        /// <summary>Platform-consistent path keys for pack plans and source directories.</summary>
        private static StringComparer PathComparer => Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        /// <summary>A revision's physical container, or bytes waiting for their first publication.</summary>
        private sealed class VersionRecord
        {
            /// <summary>Hashed source directory.</summary>
            internal string Directory;
            /// <summary>Stable timestamp-plus-random revision id.</summary>
            internal string Revision;
            /// <summary>Pack or loose snapshot filename.</summary>
            internal string File;
            /// <summary>Entry name inside a pack, or null for a loose snapshot.</summary>
            internal string Entry;
            /// <summary>Only the new capture owns an in-memory snapshot.</summary>
            internal Snapshot Pending;
        }

        /// <summary>One independently staged replacement pack and its unchanged on-disk inputs.</summary>
        private sealed class PackPlan
        {
            /// <summary>Hashed source directory.</summary>
            internal string Directory;
            /// <summary>Final archive location.</summary>
            internal string Destination;
            /// <summary>Temporary archive, or null when this source has no retained revisions.</summary>
            internal string Stage;
            /// <summary>Physical size of all managed input containers being replaced.</summary>
            internal long PreviousBytes;
            /// <summary>Actual compressed archive size.</summary>
            internal long PackedBytes;
            /// <summary>True once the replacement is published, even if loose-file cleanup then fails.</summary>
            internal bool Published;
            /// <summary>Legacy snapshots to retire only after their packed replacement is published.</summary>
            internal List<string> Loose = new List<string>();
        }

        /// <summary>Enumerates one source without unpacking payloads; packed copies win over leftover loose duplicates.</summary>
        /// <param name="directory">Hashed source directory.</param>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <returns>One record per revision, from both storage generations.</returns>
        private List<VersionRecord> DirectoryRecords(string directory, CancellationToken cancellation)
        {
            EnsureLocalPath(directory);
            var records = new Dictionary<string, VersionRecord>(StringComparer.Ordinal);
            if (!Directory.Exists(directory)) return new List<VersionRecord>();
            foreach (string file in Directory.EnumerateFiles(directory, "*.snapshot"))
            {
                cancellation.ThrowIfCancellationRequested();
                EnsureLocalPath(file);
                string revision = Path.GetFileNameWithoutExtension(file);
                if (IsRevision(revision)) records.Add(revision, new VersionRecord { Directory = directory, Revision = revision, File = file });
            }
            string pack = Path.Combine(directory, "versions.pack");
            EnsureLocalPath(pack);
            if (File.Exists(pack))
            {
                using (var stream = OpenHistoryRead(pack))
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        string revision = Path.GetFileNameWithoutExtension(entry.FullName);
                        if (!IsRevision(revision) || entry.FullName != revision + ".snapshot" || !seen.Add(revision))
                            throw new InvalidDataException("pack contains an invalid or duplicate revision entry");
                        records[revision] = new VersionRecord { Directory = directory, Revision = revision, File = pack, Entry = entry.FullName };
                    }
                }
            }
            return new List<VersionRecord>(records.Values);
        }

        /// <summary>Opens a container while allowing atomic replacement by another captured operation.</summary>
        /// <param name="path">Validated container filename.</param>
        /// <returns>A readable stream owned by the caller.</returns>
        private static FileStream OpenHistoryRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }

        /// <summary>Reads one bounded record without requiring a seekable decompression stream.</summary>
        /// <param name="record">Container and revision identity.</param>
        /// <param name="includeBytes">Whether to read and integrity-check the full payload.</param>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <returns>The independently verified snapshot.</returns>
        private Snapshot ReadRecord(VersionRecord record, bool includeBytes, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (record.Pending != null) return record.Pending;
            EnsureLocalPath(record.File);
            using (var stream = OpenHistoryRead(record.File))
            {
                if (record.Entry == null) return DecodeRecord(record, stream, stream.Length, includeBytes, cancellation);
                using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, true))
                    return ReadPackedRecord(record, archive, includeBytes, cancellation);
            }
        }

        /// <summary>Reads an entry from an already-open archive so a pack pass parses its directory only once.</summary>
        /// <param name="record">Expected packed revision.</param>
        /// <param name="archive">Open archive owned by the caller.</param>
        /// <param name="includeBytes">Whether to verify the payload as well as the header.</param>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <returns>The validated snapshot.</returns>
        private Snapshot ReadPackedRecord(VersionRecord record, ZipArchive archive, bool includeBytes, CancellationToken cancellation)
        {
            ZipArchiveEntry entry = archive?.GetEntry(record.Entry);
            if (entry == null) throw new FileNotFoundException("packed revision was removed during this read");
            using (var payload = entry.Open()) return DecodeRecord(record, payload, entry.Length, includeBytes, cancellation);
        }

        /// <summary>Shares the exact-byte legacy record format between loose snapshots and compressed entries.</summary>
        /// <param name="record">Expected revision and directory.</param>
        /// <param name="stream">Raw record stream.</param>
        /// <param name="length">Uncompressed record length, checked before allocating.</param>
        /// <param name="includeBytes">Whether to load and hash the content.</param>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        /// <returns>The validated record.</returns>
        private Snapshot DecodeRecord(VersionRecord record, Stream stream, long length, bool includeBytes, CancellationToken cancellation)
        {
            if (length < 6 || length > MAX_SNAPSHOT_BYTES + MAX_HEADER_BYTES + 4L)
                throw new InvalidDataException("snapshot exceeds its uncompressed allocation limit");
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                int size = reader.ReadInt32();
                if (size < 2 || size > MAX_HEADER_BYTES || length < size + 4L)
                    throw new InvalidDataException("invalid snapshot header length");
                JObject metadata = JObject.Parse(new UTF8Encoding(false, true).GetString(reader.ReadBytes(size)));
                long byteCount = length - size - 4L;
                string path = CanonicalPath((string)metadata["path"]);
                if ((int?)metadata["format"] != 1 || (string)metadata["revision"] != record.Revision
                    || !PathComparer.Equals(DirectoryFor(path), record.Directory)
                    || (long?)metadata["byteCount"] != byteCount || byteCount > MAX_SNAPSHOT_BYTES
                    || !IsDigest((string)metadata["sha256"]))
                    throw new InvalidDataException("snapshot metadata does not match its path, revision or payload length");
                var snapshot = new Snapshot { Metadata = metadata };
                if (!includeBytes) return snapshot;
                snapshot.Bytes = new byte[(int)byteCount];
                for (int offset = 0; offset < snapshot.Bytes.Length;)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = stream.Read(snapshot.Bytes, offset, Math.Min(65536, snapshot.Bytes.Length - offset));
                    if (count <= 0) throw new EndOfStreamException("snapshot payload is truncated");
                    offset += count;
                }
                if (stream.ReadByte() != -1 || Digest(snapshot.Bytes) != (string)metadata["sha256"])
                    throw new InvalidDataException("snapshot payload failed its SHA-256 integrity check");
                cancellation.ThrowIfCancellationRequested();
                return snapshot;
            }
        }

        /// <summary>Stages a complete compressed replacement; existing versions remain untouched on every failure.</summary>
        /// <param name="directory">Source whose retained set changed.</param>
        /// <param name="records">All known revisions including the pending capture.</param>
        /// <param name="kept">Revisions selected by retention.</param>
        /// <param name="plans">Owned stages, retained for cleanup even if building fails.</param>
        /// <param name="cancellation">Bounded worker cancellation.</param>
        private void RebuildPackPlan(string directory, List<VersionRecord> records, HashSet<VersionRecord> kept,
            Dictionary<string, PackPlan> plans, CancellationToken cancellation)
        {
            EnsureLocalPath(directory);
            if (!plans.TryGetValue(directory, out PackPlan plan))
            {
                plan = new PackPlan { Directory = directory, Destination = Path.Combine(directory, "versions.pack") };
                EnsureLocalPath(plan.Destination);
                if (File.Exists(plan.Destination)) plan.PreviousBytes += new FileInfo(plan.Destination).Length;
                if (Directory.Exists(directory))
                    foreach (string file in Directory.EnumerateFiles(directory, "*.snapshot"))
                    {
                        EnsureLocalPath(file);
                        if (!IsRevision(Path.GetFileNameWithoutExtension(file))) continue;
                        plan.Loose.Add(file);
                        plan.PreviousBytes += new FileInfo(file).Length;
                    }
                plans.Add(directory, plan);
            }
            if (plan.Stage != null) { File.Delete(plan.Stage); plan.Stage = null; }
            plan.PackedBytes = 0;
            List<VersionRecord> versions = records.FindAll(record => kept.Contains(record) && PathComparer.Equals(record.Directory, directory));
            if (versions.Count == 0) return;
            Directory.CreateDirectory(directory);
            plan.Stage = Path.Combine(directory, DateTime.UtcNow.Ticks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var input = File.Exists(plan.Destination) ? OpenHistoryRead(plan.Destination) : null)
            using (var previous = input == null ? null : new ZipArchive(input, ZipArchiveMode.Read))
            using (var output = new FileStream(plan.Stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
                    foreach (VersionRecord record in versions)
                    {
                        Snapshot snapshot = record.Entry == null ? ReadRecord(record, true, cancellation)
                            : ReadPackedRecord(record, previous, true, cancellation);
                        byte[] header = Encoding.UTF8.GetBytes(snapshot.Metadata.ToString(Formatting.None));
                        if (header.Length > MAX_HEADER_BYTES) throw new IOException("snapshot metadata exceeds its limit");
                        ZipArchiveEntry entry = archive.CreateEntry(record.Revision + ".snapshot", CompressionLevel.Optimal);
                        using (var payload = entry.Open())
                        using (var writer = new BinaryWriter(payload, Encoding.UTF8, true))
                        {
                            writer.Write(header.Length);
                            writer.Write(header);
                            for (int offset = 0; offset < snapshot.Bytes.Length; offset += 65536)
                            {
                                cancellation.ThrowIfCancellationRequested();
                                writer.Write(snapshot.Bytes, offset, Math.Min(65536, snapshot.Bytes.Length - offset));
                            }
                        }
                    }
                output.Flush(true);
                plan.PackedBytes = output.Length;
            }
            // Read back the completed container before it can replace the only copy of legacy history.
            using (var stream = OpenHistoryRead(plan.Stage))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
                foreach (VersionRecord record in versions)
                    ReadPackedRecord(new VersionRecord { Directory = directory, Revision = record.Revision,
                        File = plan.Stage, Entry = record.Revision + ".snapshot" }, archive, true, cancellation);
        }

        /// <summary>Calculates final physical usage rather than summing uncompressed version lengths.</summary>
        /// <param name="total">Measured store bytes before staging.</param>
        /// <param name="plans">All proposed replacements.</param>
        /// <returns>Store bytes after successful publication and removal of replaced containers.</returns>
        private static long ProjectedBytes(long total, Dictionary<string, PackPlan> plans)
        {
            foreach (PackPlan plan in plans.Values) total = checked(total - plan.PreviousBytes + plan.PackedBytes);
            return total;
        }

        /// <summary>Atomically publishes a verified pack, then removes only its replaced loose snapshots.</summary>
        /// <param name="plan">Fully prepared replacement; no source assets are touched.</param>
        private void CommitPack(PackPlan plan)
        {
            EnsureLocalPath(plan.Destination);
            if (plan.Stage != null)
            {
                if (File.Exists(plan.Destination)) File.Replace(plan.Stage, plan.Destination, null);
                else File.Move(plan.Stage, plan.Destination);
                plan.Stage = null;
            }
            else if (File.Exists(plan.Destination)) File.Delete(plan.Destination);
            plan.Published = true;
            foreach (string file in plan.Loose) { EnsureLocalPath(file); File.Delete(file); }
        }

        #endregion
    }
}

namespace ItsRenji.Conduit
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;
    using UnityEditor;
    using UnityEditor.Compilation;

    /// <summary>Source version inspection, guarded restoration and best-effort before/after capture.</summary>
    internal static partial class ConduitBridge
    {
        #region Worker lifetime

        /// <summary>Maximum history preparation time before the caller receives a warning or refusal.</summary>
        private const double FILE_HISTORY_TIMEOUT_SECONDS = 15d;

        /// <summary>Prevents another durable command from interleaving with a prepared source write.</summary>
        private static object _fileHistoryWorker;

        /// <summary>Whether the transport must leave durable commands queued until history preparation finishes.</summary>
        internal static bool FileHistoryBusy => _fileHistoryWorker != null;

        /// <summary>
        /// Runs bounded history I/O off the editor thread, then resumes the owning command on that thread.
        /// Before-write work leaves source untouched if interrupted; post-write work holds a bounded reload guard
        /// until capture completes or warns. Ordinary ledger recovery reports an interrupted command.
        /// Capture failures can continue the original edit, while read and restore failures cannot mutate source.
        /// </summary>
        /// <param name="work">Pure filesystem work; no Unity API calls.</param>
        /// <param name="apply">Main-thread continuation with the original command context installed.</param>
        /// <param name="continueOnFailure">Whether a history failure must leave the existing edit path available.</param>
        /// <param name="cleanup">Idempotent cleanup of a restore stage, if any.</param>
        /// <param name="mainCleanup">Main-thread release of a source-write reload guard, if any.</param>
        /// <returns>An empty withheld response, or a refusal when there is no durable owner.</returns>
        private static string RunFileHistoryWorker(Func<CancellationToken, string> work,
            Func<string, string> apply, bool continueOnFailure, Action cleanup = null, Action mainCleanup = null)
        {
            var context = _commandContext;
            if (context == null || string.IsNullOrEmpty(context.JobId) || context.CompletingDeferred)
            {
                if (continueOnFailure)
                {
                    try { return apply("History warning: no durable command context; no snapshot was saved."); }
                    finally { mainCleanup?.Invoke(); }
                }
                return "Refused: file history requires a durable Conduit command. Nothing was written.";
            }
            if (FileHistoryBusy)
            {
                mainCleanup?.Invoke();
                return "Refused: another file history operation is pending.";
            }

            var cancellation = new CancellationTokenSource();
            Task<string> task = Task.Run(() => work(cancellation.Token));
            double deadline = EditorApplication.timeSinceStartup + FILE_HISTORY_TIMEOUT_SECONDS;
            context.Deferred = true;
            var worker = new object();
            _fileHistoryWorker = worker;
            EditorApplication.CallbackFunction tick = null;
            AssemblyReloadEvents.AssemblyReloadCallback beforeReload = null;
            Action release = () =>
            {
                EditorApplication.update -= tick;
                AssemblyReloadEvents.beforeAssemblyReload -= beforeReload;
                if (ReferenceEquals(_fileHistoryWorker, worker)) _fileHistoryWorker = null;
                mainCleanup?.Invoke();
                cancellation.Cancel();
                task.ContinueWith(finished =>
                {
                    // Observe a late fault and remove stages even if an OS operation outlived its deadline.
                    var ignored = finished.Exception;
                    cleanup?.Invoke();
                    cancellation.Dispose();
                }, TaskScheduler.Default);
            };
            beforeReload = () => release();
            tick = () =>
            {
                if (!task.IsCompleted && EditorApplication.timeSinceStartup < deadline) return;
                EditorApplication.update -= tick;
                AssemblyReloadEvents.beforeAssemblyReload -= beforeReload;
                if (ReferenceEquals(_fileHistoryWorker, worker)) _fileHistoryWorker = null;
                string result;
                Exception failure = null;
                if (!task.IsCompleted)
                {
                    cancellation.Cancel();
                    failure = new TimeoutException("history preparation exceeded 15 seconds; snapshot completion is unconfirmed");
                    result = null;
                }
                else if (task.IsCanceled)
                {
                    failure = new OperationCanceledException("history preparation was cancelled");
                    result = null;
                }
                else if (task.IsFaulted)
                {
                    failure = task.Exception.GetBaseException();
                    result = null;
                }
                else result = task.Result;

                var previous = _commandContext;
                _commandContext = context;
                context.Deferred = false;
                try
                {
                    if (!ConduitJobLedger.TryReadJob(context.JobId, out JObject job, out string readError)
                        || (string)job["state"] != "running")
                        return;
                    if (failure != null && !continueOnFailure)
                    {
                        ConduitJobLedger.Fail(context.JobId, "file_history_failed", failure.Message + ". Nothing was written.", out _);
                        return;
                    }
                    if (failure != null)
                        result = "History warning: " + failure.Message + ". The edit and its rollback are still applied normally.";
                    result = apply(result);
                    if (!context.Deferred)
                    {
                        for (int attempt = 0; attempt < 3; attempt++)
                            if (ConduitJobLedger.Complete(context.JobId, "command_completed", result, out _)) break;
                    }
                }
                catch (Exception error)
                {
                    ConduitJobLedger.Fail(context.JobId, "file_history_command_failed", error.Message, out _);
                }
                finally
                {
                    _commandContext = previous;
                    release();
                }
            };
            AssemblyReloadEvents.beforeAssemblyReload += beforeReload;
            EditorApplication.update += tick;
            return string.Empty;
        }

        /// <summary>Captures already-held file bytes before resuming the write transaction or its import phase.</summary>
        /// <param name="originals">Validated source paths and the exact bytes the writer already holds.</param>
        /// <param name="operation">Initiating verb.</param>
        /// <param name="apply">Existing mutation and rollback path, given capture evidence or warnings.</param>
        /// <param name="state">Before-attempted-write or after-write provenance.</param>
        /// <param name="mainCleanup">Releases a bounded source-write guard after post-write capture.</param>
        /// <returns>The withheld command response.</returns>
        private static string WithFileHistory(Dictionary<string, byte[]> originals, string operation,
            Func<string, string> apply, string state = "before-attempted-write", Action mainCleanup = null)
        {
            var store = new ConduitFileHistory(ProjectRootPath());
            string client = _commandContext?.Client;
            string jobId = _commandContext?.JobId;
            return RunFileHistoryWorker(token =>
            {
                var reports = new List<string>();
                foreach (var original in originals)
                    reports.Add(store.Capture(original.Key, original.Value, operation, client, jobId, token, state));
                return string.Join(Environment.NewLine, reports);
            }, apply, true, null, mainCleanup);
        }

        /// <summary>Keeps Unity from reloading between a completed source write and its bounded post-write capture.</summary>
        /// <returns>An idempotent main-thread release, also used on errors and worker deadlines.</returns>
        private static Action HoldFileHistoryWrite()
        {
            EditorApplication.LockReloadAssemblies();
            try { AssetDatabase.DisallowAutoRefresh(); }
            catch { EditorApplication.UnlockReloadAssemblies(); throw; }
            bool released = false;
            return () =>
            {
                if (released) return;
                released = true;
                try { AssetDatabase.AllowAutoRefresh(); }
                finally { EditorApplication.UnlockReloadAssemblies(); }
            };
        }

        /// <summary>Produces exactly the bytes used by the existing UTF-8 source writer, including its mark.</summary>
        /// <param name="content">Successfully written source text.</param>
        /// <param name="hadBom">Whether the writer preserved a UTF-8 mark.</param>
        /// <returns>The post-write bytes without rereading the source file.</returns>
        private static byte[] FileHistoryWrittenBytes(string content, bool hadBom)
        {
            var encoding = new UTF8Encoding(hadBom);
            byte[] text = encoding.GetBytes(content);
            if (!hadBom) return text;
            byte[] result = new byte[text.Length + 3];
            Buffer.BlockCopy(encoding.GetPreamble(), 0, result, 0, 3);
            Buffer.BlockCopy(text, 0, result, 3, text.Length);
            return result;
        }

        /// <summary>Checks for concurrent external changes between history preparation and source application.</summary>
        /// <param name="path">Validated source path.</param>
        /// <param name="expected">Bytes from the writer's original read.</param>
        /// <returns>True only when the current bytes still match, or when the original read was unavailable.</returns>
        private static bool FileHistoryCurrentMatches(string path, byte[] expected)
        {
            if (expected == null) return true;
            try { return HashBytes(File.ReadAllBytes(path)) == HashBytes(expected); }
            catch { return false; }
        }

        /// <summary>Detects a byte-identical whole-file write without losing the existing UTF-8 mark.</summary>
        /// <param name="original">Existing bytes.</param>
        /// <param name="content">Requested source text.</param>
        /// <param name="hadBom">Whether the writer will preserve a UTF-8 mark.</param>
        /// <returns>Whether the writer would reproduce precisely the original bytes.</returns>
        private static bool FileHistoryUtf8Matches(byte[] original, string content, bool hadBom)
        {
            if (original == null) return false;
            byte[] encoded = Encoding.UTF8.GetBytes(content);
            int offset = hadBom ? 3 : 0;
            if (original.Length != encoded.Length + offset) return false;
            for (int i = 0; i < encoded.Length; i++)
                if (original[i + offset] != encoded[i]) return false;
            return true;
        }

        #endregion

        #region Read and restore commands

        /// <summary>Lists source versions or reads one with an exact-byte integrity check and a current-file hash.</summary>
        /// <param name="args">Source path, optional revision, and optional content or diff view.</param>
        /// <returns>A withheld JSON history response, or input validation feedback.</returns>
        private static string FileHistoryCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 1 || sections.Length > 3 || string.IsNullOrWhiteSpace(sections[0]))
                return "Usage: file-history <assetPath> [| revision] [| view:content|diff]";
            string path = NormalizeAssetPath(sections[0]);
            if (!ValidateFileHistoryTarget(path, out string error)) return error;
            string revision = sections.Length > 1 ? Unquote(sections[1]).Trim() : string.Empty;
            string view = sections.Length > 2 ? Unquote(sections[2]).Trim() : "content";
            if ((view != "content" && view != "diff") || (sections.Length > 1 && revision.Length == 0))
                return "Refused: supply an exact revision before a content or diff view. Nothing was written.";
            var store = new ConduitFileHistory(ProjectRootPath());
            string fullPath = Path.Combine(ProjectRootPath(), path);
            return RunFileHistoryWorker(token =>
            {
                store.CanonicalPath(path);
                byte[] current = File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
                JObject result;
                if (revision.Length == 0)
                {
                    result = store.List(path, token);
                    string currentHash = current == null ? null : HashBytes(current);
                    foreach (JObject version in (JArray)result["versions"])
                        version["isCurrent"] = (string)version["sha256"] == currentHash;
                }
                else
                {
                    var snapshot = store.Read(path, revision, true, token);
                    result = (JObject)snapshot.Metadata.DeepClone();
                    if (view == "diff")
                    {
                        if (current == null) result["diff"] = "Current file is missing; read the revision with view=content.";
                        else result["diff"] = BuildFileDiff(path,
                            DecodeVerifiedUtf8(path, snapshot.Bytes, out _), DecodeVerifiedUtf8(path, current, out _))
                            ?? (HashBytes(current) == (string)snapshot.Metadata["sha256"]
                                ? "No byte differences." : "Bytes differ only in BOM or line-ending representation.");
                    }
                    else
                    {
                        try { result["content"] = DecodeVerifiedUtf8(path, snapshot.Bytes, out bool bom); result["utf8Bom"] = bom; }
                        catch (InvalidOperationException) { result["contentBase64"] = Convert.ToBase64String(snapshot.Bytes); }
                    }
                }
                result["currentSha256"] = current == null ? null : HashBytes(current);
                return result.ToString(Formatting.Indented);
            }, result => result, false);
        }

        /// <summary>Restores a verified older or newer version only when the caller's current-file hash still matches.</summary>
        /// <param name="args">Source path, stored revision and expected current SHA-256.</param>
        /// <returns>Exact restore evidence and its compile verdict, or a refusal without source mutation.</returns>
        private static string FileRestoreCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length != 3) return "Usage: file-restore <assetPath> | <revision> | <expectedCurrentSha256>";
            string path = NormalizeAssetPath(sections[0]);
            if (!ValidateFileHistoryTarget(path, out string error)) return error;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "Refused: exit Play Mode before restoring source.";
            string revision = Unquote(sections[1]).Trim();
            string expected = Unquote(sections[2]).Trim();
            if (!ConduitFileHistory.IsDigest(expected)) return "Refused: expectedCurrentSha256 must be the exact hash returned by file-history.";

            var store = new ConduitFileHistory(ProjectRootPath());
            string fullPath = Path.Combine(ProjectRootPath(), path);
            string stage = Path.Combine(ProjectRootPath(), "Temp", "ConduitRestore", Guid.NewGuid().ToString("N") + ".tmp");
            string client = _commandContext?.Client;
            string jobId = _commandContext?.JobId;
            ConduitFileHistory.Snapshot snapshot = null;
            byte[] current = null;
            bool triggersCompile = PathTriggersScriptCompile(path);
            Action cleanup = () => { try { if (File.Exists(stage)) File.Delete(stage); } catch { } };
            return RunFileHistoryWorker(token =>
            {
                snapshot = store.Read(path, revision, true, token);
                current = File.ReadAllBytes(fullPath);
                if (HashBytes(current) != expected) throw new InvalidOperationException("current file changed; read file-history again before choosing a restore");
                if (HashBytes(snapshot.Bytes) == expected) throw new InvalidOperationException("revision already matches the current file; no restore or new history is needed");
                store.EnsureLocalPath(stage);
                Directory.CreateDirectory(Path.GetDirectoryName(stage));
                using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    for (int offset = 0; offset < snapshot.Bytes.Length; offset += 65536)
                    {
                        token.ThrowIfCancellationRequested();
                        stream.Write(snapshot.Bytes, offset, Math.Min(65536, snapshot.Bytes.Length - offset));
                    }
                    stream.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                return store.Capture(path, current, "file-restore", client, jobId, token);
            }, report =>
            {
                if (!ValidateFileHistoryTarget(path, out string pathError)) return pathError;
                store.CanonicalPath(path);
                if (EditorApplication.isPlayingOrWillChangePlaymode || !FileHistoryCurrentMatches(path, current))
                    return "Refused: editor state or target changed during restore preparation. Nothing was written.\n" + report;
                bool suppressed = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && SuppressAutoCompileForBatch();
                Action releaseHistoryWrite = HoldFileHistoryWrite();
                try
                {
                    File.Replace(stage, fullPath, null);
                    RecordScriptReadHash(path);
                    string summary = "Restored " + path + " from revision " + revision + ". SHA-256 " + expected
                        + " -> " + (string)snapshot.Metadata["sha256"] + ".\n" + report;
                    return WithFileHistory(new Dictionary<string, byte[]> { [path] = snapshot.Bytes }, "file-restore", after =>
                    {
                        summary += Environment.NewLine + after;
                        if (suppressed) ArmAutoCompileRestoreFallback();
                        bool deferred = triggersCompile && TryDeferUntilReload("__deferred-note " + summary);
                        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                        if (deferred) return string.Empty;
                        return summary + Environment.NewLine + FormatEditorStatus();
                    }, "after-write", releaseHistoryWrite);
                }
                catch
                {
                    releaseHistoryWrite();
                    if (suppressed) RestoreAutoCompileFromStash("restore write failure");
                    throw;
                }
            }, false, cleanup);
        }

        /// <summary>Shares the batch writer's source-extension and registered-package boundary.</summary>
        /// <param name="path">Normalized project-relative path.</param>
        /// <param name="error">Specific refusal when the path is not a supported writable source.</param>
        /// <returns>Whether history may expose or restore this source identity.</returns>
        private static bool ValidateFileHistoryTarget(string path, out string error)
        {
            if (!TryValidateWritableScriptAssetPath(path, out error)) return false;
            if (IsBatchEditableFile(path)) return true;
            error = "Refused: file history supports .cs, .shader, .hlsl and .cginc source files.";
            return false;
        }

        #endregion
    }
}
