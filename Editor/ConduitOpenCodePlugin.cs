/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Installs the in-harness plugin one client needs so work outlasting a call can report back to the
    /// exact session that asked for it.
    /// </summary>
    /// <remarks>
    /// This is one harness's mechanism, the counterpart to the session-start hook another uses. This one
    /// must be a plugin because the harness publishes no session inbox at all: only an in-process plugin
    /// receives the authenticated client handle that can inject a notice into the conversation, so there is
    /// no address a hook could hand over and nothing Unity could write to.
    /// <para>
    /// A plugin rather than anything else because only a plugin is handed an authenticated handle to the
    /// harness's own control plane, which is what performs the injection. That control plane's password is
    /// generated per launch and delivered to its sidecar over IPC, never through the environment, so
    /// nothing outside the harness can authenticate to it or learn its port.
    /// </para>
    /// <para>
    /// ONE copy is installed for the machine and it serves every project, which is a resource decision
    /// rather than a convenience. The harness imports every registered plugin at startup whether or not
    /// the project behind it is open, so a copy per project would cost a module for every project ever
    /// connected - and each copy would register the same tool name, leaving whichever loaded last
    /// answering for all of them. The plugin therefore reads the bridges out of the harness's own config
    /// and attaches only to the editors that are actually listening.
    /// </para>
    /// <para>
    /// Because the copy is shared, REMOVAL IS REFERENCE COUNTED, and the config is the count. Conduit
    /// already writes one MCP entry per project and Remove takes that project's entry away, so the plugin
    /// is wanted exactly while at least one such entry remains. Deleting it on the first project's Remove
    /// would silently break delivery for every other project that is still connected.
    /// </para>
    /// </remarks>
    internal static class ConduitOpenCodePlugin
    {
        #region Constants

        /// <summary>The only client this installs for; every other client's Connect ignores it.</summary>
        private const string CLIENT = "OpenCode";

        /// <summary>Folder Conduit owns inside the client's config directory.</summary>
        private const string INSTALL_FOLDER = "conduit";

        /// <summary>File name the plugin is installed under.</summary>
        private const string PLUGIN_FILE = "plugin.mjs";

        /// <summary>
        /// Spec registered in the client's config, relative because the client resolves a path-shaped
        /// plugin entry against the directory of the config that declared it.
        /// </summary>
        /// <remarks>
        /// Installing one directory below the config is also what lets the plugin import the harness's own
        /// plugin package at all: that package resolves from the module folder beside the config, which a
        /// file one level down reaches. A deeper location relies on how far that resolution walks.
        /// </remarks>
        private const string PLUGIN_SPEC = "./" + INSTALL_FOLDER + "/" + PLUGIN_FILE;

        /// <summary>Name of the source file shipped inside the package.</summary>
        private const string SOURCE_FILE = "opencode-plugin.mjs";

        /// <summary>
        /// Header that marks an MCP entry as a Conduit bridge, lowercased for comparison.
        /// </summary>
        /// <remarks>
        /// Counting these is what makes removal safe: the value identifies the project, so several entries
        /// carrying this header means several projects are still connected and the shared plugin is still
        /// wanted.
        /// </remarks>
        private const string IDENTITY_HEADER = "x-conduit-project";

        #endregion

        #region Paths

        /// <summary>Whether this client is the one that takes a plugin.</summary>
        /// <param name="client">Client display name as the window's cards spell it.</param>
        /// <returns>True for the single client this installs for.</returns>
        internal static bool Handles(string client)
        {
            return string.Equals(client, CLIENT, StringComparison.Ordinal);
        }

        /// <summary>The client's own config directory.</summary>
        /// <returns>Absolute path.</returns>
        private static string ConfigDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opencode");
        }

        /// <summary>The config file the plugin is registered in.</summary>
        /// <remarks>
        /// The same file the MCP entry is written to, and for the same reason: the client merges several
        /// config files and this is the one it reads before the variant it patches itself, so a comment-
        /// carrying file is never rewritten by this and a later edit to it cannot shadow what is written
        /// here.
        /// </remarks>
        /// <returns>Absolute path.</returns>
        private static string ConfigPath()
        {
            return Path.Combine(ConfigDir(), "opencode.json");
        }

        /// <summary>Folder the plugin is installed into.</summary>
        /// <returns>Absolute path.</returns>
        private static string InstallDir()
        {
            return Path.Combine(ConfigDir(), INSTALL_FOLDER);
        }

        /// <summary>The plugin source that ships inside the package.</summary>
        /// <returns>Project-relative path, or an empty string when Conduit's own folder cannot be found.</returns>
        private static string SourcePath()
        {
            string root = ConduitSkills.ConduitRoot();
            return root.Length == 0 ? string.Empty : root + "/Harness/" + SOURCE_FILE;
        }

        #endregion

        #region Install and remove

        /// <summary>
        /// Installs and registers the shared plugin, reporting what actually changed.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>What happened, or null when this client takes no plugin.</returns>
        internal static string Install(string client)
        {
            if (!Handles(client)) return null;

            string source = SourcePath();
            if (source.Length == 0 || !File.Exists(source))
            {
                return "the OpenCode plugin was NOT installed: its source is missing from this Conduit "
                    + "install, so there was nothing to copy";
            }

            bool fileChanged;
            try
            {
                Directory.CreateDirectory(InstallDir());

                // Compared before writing so an unchanged reinstall reports honestly rather than claiming a
                // copy, and so a running harness is not told to restart for a file identical to the one it
                // already loaded. Copied verbatim: the plugin finds its bridges in the harness's config, so
                // there is nothing per project to write into it.
                byte[] wanted = File.ReadAllBytes(source);
                fileChanged = !File.Exists(Path.Combine(InstallDir(), PLUGIN_FILE))
                    || !SameBytes(File.ReadAllBytes(Path.Combine(InstallDir(), PLUGIN_FILE)), wanted);
                if (fileChanged) File.WriteAllBytes(Path.Combine(InstallDir(), PLUGIN_FILE), wanted);
            }
            catch (Exception ex)
            {
                return "the OpenCode plugin was NOT installed: " + ex.GetType().Name + ": " + ex.Message;
            }

            string registrationError;
            bool registrationChanged;
            if (!Register(out registrationChanged, out registrationError))
            {
                return "the OpenCode plugin file was written but NOT registered: " + registrationError;
            }

            if (!fileChanged && !registrationChanged)
            {
                return "the OpenCode plugin was already installed and registered";
            }

            // The restart is stated because it is the one thing left for the owner to do and nothing else
            // reveals it: the harness imports its plugins at startup, so a newly registered one does
            // nothing at all until then, and a session started before it would silently never report back.
            return (registrationChanged ? "registered" : "updated")
                + " the OpenCode plugin - restart OpenCode once so it loads";
        }

        /// <summary>
        /// Unregisters and deletes the shared plugin, but ONLY once no project is still connected.
        /// </summary>
        /// <remarks>
        /// Called after this project's own MCP entry has already been removed, so the remaining count
        /// answers for the other projects rather than including this one.
        /// </remarks>
        /// <param name="client">Client display name.</param>
        /// <returns>What happened, or null when this client takes no plugin.</returns>
        internal static string Remove(string client)
        {
            if (!Handles(client)) return null;

            JObject config;
            string readError;
            if (!TryReadConfig(out config, out readError))
            {
                return "the OpenCode plugin was left in place: " + readError;
            }

            // The other projects' entries are the reference count. Leaving the plugin registered while any
            // remain is the whole point: it is shared, so removing it here would take their delivery with
            // it, silently and with nothing for them to notice it by.
            int stillConnected = ConduitBridgeEntryCount(config);
            if (stillConnected > 0)
            {
                return "this project's OpenCode entry was removed; the shared plugin stays because "
                    + stillConnected + " other project" + (stillConnected == 1 ? " is" : "s are")
                    + " still connected to it";
            }

            string unregisterError;
            bool unregistered;
            if (!Unregister(config, out unregistered, out unregisterError))
            {
                return "the OpenCode plugin could not be unregistered: " + unregisterError;
            }

            bool removedFiles = false;
            string installDir = InstallDir();
            try
            {
                if (Directory.Exists(installDir))
                {
                    // Refused unless the path is exactly the folder this class builds. A recursive delete
                    // driven by a path composed elsewhere is how a cleanup reaches a folder full of somebody
                    // else's work, and the check costs nothing.
                    string expected = Path.GetFullPath(Path.Combine(ConfigDir(), INSTALL_FOLDER));
                    if (!string.Equals(Path.GetFullPath(installDir), expected, StringComparison.OrdinalIgnoreCase))
                    {
                        return "the OpenCode plugin registration was removed, but its folder was left alone: "
                            + "the path did not resolve to Conduit's own folder";
                    }

                    Directory.Delete(installDir, true);
                    removedFiles = true;
                }
            }
            catch (Exception ex)
            {
                return "the OpenCode plugin registration was removed but its folder could not be deleted: "
                    + ex.Message;
            }

            if (!unregistered && !removedFiles) return "no OpenCode plugin to remove";

            return "removed the OpenCode plugin and its registration - no project was still using it";
        }

        #endregion

        #region Registration

        /// <summary>Counts the MCP entries that are Conduit bridges, whichever project each belongs to.</summary>
        /// <param name="config">Parsed client config.</param>
        /// <returns>How many Conduit bridges are registered.</returns>
        private static int ConduitBridgeEntryCount(JObject config)
        {
            var servers = config["mcp"] as JObject;
            if (servers == null) return 0;

            int count = 0;
            foreach (var pair in servers)
            {
                var entry = pair.Value as JObject;
                if (entry == null) continue;

                var headers = entry["headers"] as JObject;
                if (headers == null) continue;

                foreach (var header in headers)
                {
                    if (!string.Equals(header.Key, IDENTITY_HEADER, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    count++;
                    break;
                }
            }

            return count;
        }

        /// <summary>
        /// Adds the plugin spec to the client's config, preserving everything already in the file.
        /// </summary>
        /// <param name="changed">Receives whether the file was modified.</param>
        /// <param name="error">Receives why it could not be done.</param>
        /// <returns>True when the spec is registered afterwards.</returns>
        private static bool Register(out bool changed, out string error)
        {
            changed = false;

            JObject config;
            if (!TryReadConfig(out config, out error)) return false;

            var plugins = config["plugin"] as JArray;
            if (plugins == null)
            {
                plugins = new JArray();
                config["plugin"] = plugins;
            }

            foreach (JToken entry in plugins)
            {
                if (string.Equals(SpecOf(entry), PLUGIN_SPEC, StringComparison.Ordinal)) return true;
            }

            // Appended, never assigned. Plugins with nothing to do with Conduit live in this same array and
            // replacing it would unregister all of them.
            plugins.Add(PLUGIN_SPEC);
            if (!TryWriteConfig(config, out error)) return false;

            changed = true;
            return true;
        }

        /// <summary>Removes only Conduit's own spec, leaving every unrelated plugin registered.</summary>
        /// <param name="config">Config already read by the caller, so it is not read twice.</param>
        /// <param name="changed">Receives whether the file was modified.</param>
        /// <param name="error">Receives why it could not be done.</param>
        /// <returns>True when the spec is absent afterwards.</returns>
        private static bool Unregister(JObject config, out bool changed, out string error)
        {
            changed = false;
            error = null;

            var plugins = config["plugin"] as JArray;
            if (plugins == null) return true;

            for (int index = plugins.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(SpecOf(plugins[index]), PLUGIN_SPEC, StringComparison.Ordinal)) continue;
                plugins.RemoveAt(index);
                changed = true;
            }

            if (!changed) return true;

            // The key goes only once nothing is left in it, so the file returns to the shape it had before
            // Conduit touched it rather than keeping an empty array behind.
            if (plugins.Count == 0) config.Remove("plugin");

            return TryWriteConfig(config, out error);
        }

        /// <summary>
        /// Reads one plugin entry's spec, which the client accepts either bare or with options beside it.
        /// </summary>
        /// <param name="entry">One entry of the config's plugin array.</param>
        /// <returns>The spec, or null when the entry is neither shape.</returns>
        private static string SpecOf(JToken entry)
        {
            if (entry == null) return null;
            if (entry.Type == JTokenType.String) return (string)entry;

            var pair = entry as JArray;
            if (pair != null && pair.Count > 0 && pair[0].Type == JTokenType.String) return (string)pair[0];

            return null;
        }

        #endregion

        #region Config file

        /// <summary>Reads the config, refusing rather than guessing at one that does not parse.</summary>
        /// <param name="config">Receives the parsed config, or an empty object when the file is absent.</param>
        /// <param name="error">Receives why it could not be read.</param>
        /// <returns>True on success.</returns>
        private static bool TryReadConfig(out JObject config, out string error)
        {
            config = null;
            error = null;

            if (!File.Exists(ConfigPath()))
            {
                config = new JObject();
                return true;
            }

            string raw;
            try { raw = File.ReadAllText(ConfigPath(), Encoding.UTF8); }
            catch (Exception ex) { error = "it could not be read (" + ex.Message + ")"; return false; }

            // Refused rather than replaced. This config is shared - every other project's bridge writes its
            // own entry here too, as does anything unrelated - and the harness refuses to load a malformed
            // file as well, so the owner ends up with one broken file rather than a replaced one.
            try { config = JObject.Parse(raw); return true; }
            catch (Exception ex)
            {
                error = "it is not valid JSON (" + ex.Message + "), so it was left untouched";
                return false;
            }
        }

        /// <summary>Writes the config back, preserving readable formatting.</summary>
        /// <param name="config">Config to store.</param>
        /// <param name="error">Receives the failure reason.</param>
        /// <returns>True on success.</returns>
        private static bool TryWriteConfig(JObject config, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(ConfigDir());
                File.WriteAllText(
                    ConfigPath(), config.ToString(Formatting.Indented) + Environment.NewLine,
                    new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>Compares two files byte for byte.</summary>
        /// <param name="left">First content.</param>
        /// <param name="right">Second content.</param>
        /// <returns>True when identical.</returns>
        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index]) return false;
            }

            return true;
        }

        #endregion
    }
}
