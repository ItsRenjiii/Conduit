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
    /// Installs the session-start hook that hands one Claude session's messaging address and credential to
    /// the bridge, so work outliving its own call can report to the exact conversation that asked.
    /// </summary>
    /// <remarks>
    /// This is one harness's mechanism, not a shared abstraction. Claude exports its session id, inbox
    /// address and credential only to processes it spawns; a SessionStart hook is such a process, so the
    /// hook receives the live values and posts them to the bridge once per session. Delivery afterwards is
    /// Unity's own, through <see cref="ConduitAgentInbox"/>, and needs nothing resident.
    /// <para>
    /// This replaced a spawned stdio MCP child that held a delivery stream and wrote the inbox from inside
    /// itself. That child cost a Node process for every open conversation, on a runtime a Unity project has
    /// no reason to carry, and what it did in JavaScript was already implemented in C# and reached by the
    /// registration route. A hook costs one short-lived process per session start and adds no runtime
    /// dependency: Windows resolves it with PowerShell, macOS and Linux with sh.
    /// </para>
    /// <para>
    /// The registration itself is identical on every platform and only the shell differs. What the address
    /// turns out to be concerns <see cref="ConduitAgentInbox"/> alone, which opens a Windows named pipe or
    /// a unix domain socket according to what that session's own record reports.
    /// </para>
    /// <para>
    /// Connect and Remove alone own the entry, and both also clear the retired child's MCP entry. Other
    /// harnesses keep their own native or plugin routes, and this class never writes configuration for a
    /// client the owner did not connect.
    /// </para>
    /// </remarks>
    internal static class ConduitClaudeSessionHook
    {
        #region Constants

        /// <summary>The only client whose session inbox this configures.</summary>
        private const string CLIENT = "Claude Desktop";

        /// <summary>Lifecycle event the registration runs on, which is once per session.</summary>
        private const string HOOK_EVENT = "SessionStart";

        /// <summary>
        /// Tag identifying Conduit's own hook entry among the owner's.
        /// </summary>
        /// <remarks>
        /// Deliberately the same tag the pre-child hook used. The job is identical, so an entry left by an
        /// older Conduit is UPDATED in place rather than joined by a second one that would register the
        /// same session twice per start.
        /// </remarks>
        private const string HOOK_TAG = "conduit-agent-session";

        /// <summary>Registration script used where the shell is PowerShell.</summary>
        private const string WINDOWS_SCRIPT = "register-session.ps1";

        /// <summary>Registration script used where the shell is sh.</summary>
        private const string UNIX_SCRIPT = "register-session.sh";

        /// <summary>Project-local MCP key the retired child was installed under.</summary>
        private const string RETIRED_ENTRY_KEY = "conduit-session";

        /// <summary>Environment marker that distinguished the retired child from an owner-written server.</summary>
        private const string RETIRED_ENTRY_MARKER = "CONDUIT_CLAUDE_COMPANION";

        #endregion

        #region Paths

        /// <summary>Whether this client uses the session-start hook.</summary>
        /// <param name="client">Client display name.</param>
        /// <returns>True for Claude Desktop only.</returns>
        private static bool Handles(string client)
        {
            return string.Equals(client, CLIENT, StringComparison.Ordinal);
        }

        /// <summary>Whether this editor runs on the platform delivery is implemented for.</summary>
        /// <returns>True on Windows.</returns>
        private static bool IsWindows()
        {
            return Path.DirectorySeparatorChar == '\\';
        }

        /// <summary>Project-local settings file the hook is registered in.</summary>
        /// <returns>Absolute settings path.</returns>
        private static string SettingsPath()
        {
            return Path.Combine(ProjectRoot(), ".claude", "settings.local.json");
        }

        /// <summary>Claude's machine-local project registry, read only to clear the retired child entry.</summary>
        /// <returns>Absolute config path.</returns>
        private static string RetiredConfigPath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        }

        /// <summary>Canonical project key used by Claude's per-project server map.</summary>
        /// <returns>Absolute project path with forward separators.</returns>
        private static string ProjectRoot()
        {
            return Directory.GetCurrentDirectory().Replace('\\', '/');
        }

        /// <summary>The registration script shipped inside this Conduit install for this platform.</summary>
        /// <returns>Project-relative script path, or an empty string when Conduit cannot be found.</returns>
        private static string ScriptPath()
        {
            string root = ConduitSkills.ConduitRoot();
            if (root.Length == 0) return string.Empty;
            return root + "/Harness/" + (IsWindows() ? WINDOWS_SCRIPT : UNIX_SCRIPT);
        }

        #endregion

        #region Install and remove

        /// <summary>
        /// Installs the session-start hook that registers each Claude session's own return address.
        /// </summary>
        /// <remarks>
        /// The hook runs once per session and exits, so nothing stays resident and a restarted session
        /// registers by starting normally. It also clears the retired child's MCP entry, which would
        /// otherwise keep pointing at a source file this version no longer ships and report a failing
        /// server at the top of every session.
        /// </remarks>
        /// <param name="client">Client display name.</param>
        /// <returns>What changed, or null when this client uses another mechanism.</returns>
        internal static string Install(string client)
        {
            if (!Handles(client)) return null;

            // Cleared first and regardless of platform: an entry naming a deleted script is worse than no
            // return path at all, and it is the one thing every upgrading project carries.
            bool removedRetired;
            string retiredError;
            if (!RemoveRetiredEntry(out removedRetired, out retiredError))
            {
                return "the retired Claude session companion could not be removed: " + retiredError;
            }

            string retiredNote = removedRetired
                ? " and removed the retired Node session companion"
                : string.Empty;

            if (ConduitHttpTransport.Port == 0)
            {
                return "the Claude session hook was NOT installed: the bridge is not listening" + retiredNote;
            }

            string scriptPath = ScriptPath();
            if (scriptPath.Length == 0 || !File.Exists(scriptPath))
            {
                return "the Claude session hook was NOT installed: its registration script is missing from "
                    + "this Conduit install" + retiredNote;
            }

            JObject settings;
            string error;
            if (!TryRead(SettingsPath(), out settings, out error))
            {
                return "the Claude session hook was NOT installed: " + error + retiredNote;
            }

            JObject wanted = HookEntry(
                HookCommand(Path.GetFullPath(scriptPath), ConduitHttpTransport.DescriptorFile));

            bool changed = ApplyHookEntry(settings, wanted);
            if (changed && !TryWrite(SettingsPath(), settings, out error))
            {
                return "the Claude session hook could not be written: " + error + retiredNote;
            }

            if (!changed && !removedRetired) return "the Claude session hook was already installed correctly";

            return (changed ? "installed" : "kept") + " the Claude session hook" + retiredNote
                + " - it registers each session as that session starts";
        }

        /// <summary>Removes Conduit's hook and the retired child entry without touching other settings.</summary>
        /// <param name="client">Client display name.</param>
        /// <returns>What changed, or null when this client uses another mechanism.</returns>
        internal static string Remove(string client)
        {
            if (!Handles(client)) return null;

            bool removedHook;
            string error;
            if (!RemoveHook(out removedHook, out error))
            {
                return "the Claude session hook could not be removed: " + error;
            }

            bool removedRetired;
            if (!RemoveRetiredEntry(out removedRetired, out error))
            {
                return "the Claude session hook was removed, but the retired Node companion entry was left "
                    + "behind: " + error;
            }

            if (!removedHook && !removedRetired) return "no Claude session hook to remove";
            return "removed the Claude session hook"
                + (removedRetired ? " and the retired Node session companion" : string.Empty);
        }

        /// <summary>Refreshes an installed hook without creating one for a client that never opted in.</summary>
        /// <remarks>
        /// Reinstalls only where something of Conduit's is already present, so the descriptor path and
        /// script location follow a moved project or a moved Conduit. A project carrying the retired child
        /// entry counts as present, which is what migrates it without the owner pressing anything.
        /// </remarks>
        /// <param name="client">Client whose installed route should be checked.</param>
        internal static void RefreshInstalled(string client)
        {
            if (!Handles(client)) return;

            try
            {
                if (!HasHook() && !HasRetiredEntry()) return;
                Install(client);
            }
            catch (Exception)
            {
                // The build verbs report a missing return stream before starting long work.
            }
        }

        #endregion

        #region Hook entries

        /// <summary>Builds the shell command the hook runs once per session start.</summary>
        /// <remarks>
        /// Every path is quoted because a Unity project path routinely contains a space, and an unquoted
        /// one arrives as two arguments. The descriptor is passed as a PATH rather than as its contents, so
        /// the transport bearer inside it never reaches a command line that any process on the machine
        /// could read; the script opens it at runtime instead.
        /// </remarks>
        /// <param name="scriptPath">Absolute registration script path.</param>
        /// <param name="descriptorPath">Absolute transport descriptor path.</param>
        /// <returns>Command line for this platform's shell.</returns>
        private static string HookCommand(string scriptPath, string descriptorPath)
        {
            if (IsWindows())
            {
                // NoProfile keeps an owner's profile script out of a session-start path, and the explicit
                // policy is what lets a shipped script run on a machine whose default forbids one.
                return "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + scriptPath + "\" \""
                    + descriptorPath + "\"";
            }

            return "sh \"" + scriptPath + "\" \"" + descriptorPath + "\"";
        }

        /// <summary>Builds Conduit's tagged hook entry.</summary>
        /// <param name="command">Command line the hook runs.</param>
        /// <returns>Entry ready for the client's hook array.</returns>
        private static JObject HookEntry(string command)
        {
            return new JObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["_conduit"] = HOOK_TAG,
            };
        }

        /// <summary>
        /// Puts Conduit's entry into the settings, replacing its own and leaving every other entry alone.
        /// </summary>
        /// <param name="settings">Settings root, modified in place.</param>
        /// <param name="wanted">Entry that should be present.</param>
        /// <returns>True when the settings changed.</returns>
        private static bool ApplyHookEntry(JObject settings, JObject wanted)
        {
            var hooks = settings["hooks"] as JObject;
            if (hooks == null)
            {
                hooks = new JObject();
                settings["hooks"] = hooks;
            }

            var groups = hooks[HOOK_EVENT] as JArray;
            if (groups == null)
            {
                groups = new JArray();
                hooks[HOOK_EVENT] = groups;
            }

            // An existing entry of ours is replaced where it sits, so an owner's grouping and matchers are
            // preserved and no second registration is added beside it.
            foreach (JToken group in groups)
            {
                var entries = group["hooks"] as JArray;
                if (entries == null) continue;

                for (int index = 0; index < entries.Count; index++)
                {
                    var entry = entries[index] as JObject;
                    if (entry == null) continue;
                    if (!string.Equals((string)entry["_conduit"], HOOK_TAG, StringComparison.Ordinal)) continue;

                    if (JToken.DeepEquals(entry, wanted)) return false;
                    entries[index] = wanted;
                    return true;
                }
            }

            groups.Add(new JObject { ["hooks"] = new JArray { wanted } });
            return true;
        }

        /// <summary>Reports whether Conduit's hook is installed.</summary>
        /// <returns>True when a tagged entry exists.</returns>
        private static bool HasHook()
        {
            JObject settings;
            string error;
            if (!File.Exists(SettingsPath()) || !TryRead(SettingsPath(), out settings, out error)) return false;

            var hooks = settings["hooks"] as JObject;
            return hooks != null && ContainsTaggedEntry(hooks);
        }

        /// <summary>Removes every tagged entry and prunes only containers emptied by removal.</summary>
        /// <param name="changed">Receives whether anything was removed.</param>
        /// <param name="error">Receives why cleanup failed.</param>
        /// <returns>True when the settings file is correct afterwards.</returns>
        private static bool RemoveHook(out bool changed, out string error)
        {
            changed = false;
            error = null;

            string path = SettingsPath();
            if (!File.Exists(path)) return true;

            JObject settings;
            if (!TryRead(path, out settings, out error)) return false;

            var hooks = settings["hooks"] as JObject;
            if (hooks == null) return true;

            changed = RemoveTaggedEntries(hooks);
            if (!changed) return true;
            if (hooks.Count == 0) settings.Remove("hooks");

            // A file Conduit emptied is deleted rather than left as an empty object, which reads as a
            // setting the owner chose.
            if (settings.Count == 0)
            {
                try { File.Delete(path); return true; }
                catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; return false; }
            }

            return TryWrite(path, settings, out error);
        }

        /// <summary>Checks every hook event for Conduit's tag.</summary>
        /// <param name="hooks">Hook event map.</param>
        /// <returns>True when any tagged entry exists.</returns>
        private static bool ContainsTaggedEntry(JObject hooks)
        {
            foreach (JProperty eventProperty in hooks.Properties())
            {
                var groups = eventProperty.Value as JArray;
                if (groups == null) continue;

                foreach (JToken group in groups)
                {
                    var entries = group["hooks"] as JArray;
                    if (entries == null) continue;

                    foreach (JToken token in entries)
                    {
                        if (string.Equals(
                                (string)token["_conduit"], HOOK_TAG, StringComparison.Ordinal)) return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Removes every tagged entry and only groups or event arrays made empty by removal.</summary>
        /// <param name="hooks">Hook event map to clean.</param>
        /// <returns>True when at least one entry was removed.</returns>
        private static bool RemoveTaggedEntries(JObject hooks)
        {
            bool removed = false;
            var emptyEvents = new JArray();

            foreach (JProperty eventProperty in hooks.Properties())
            {
                var groups = eventProperty.Value as JArray;
                if (groups == null) continue;

                for (int group = groups.Count - 1; group >= 0; group--)
                {
                    var entries = (groups[group] as JObject) == null
                        ? null
                        : groups[group]["hooks"] as JArray;
                    if (entries == null) continue;

                    for (int index = entries.Count - 1; index >= 0; index--)
                    {
                        var entry = entries[index] as JObject;
                        if (entry == null) continue;
                        if (!string.Equals(
                                (string)entry["_conduit"], HOOK_TAG, StringComparison.Ordinal)) continue;

                        entries.RemoveAt(index);
                        removed = true;
                    }

                    if (entries.Count == 0) groups.RemoveAt(group);
                }

                if (groups.Count == 0) emptyEvents.Add(eventProperty.Name);
            }

            foreach (JToken eventName in emptyEvents) hooks.Remove((string)eventName);
            return removed;
        }

        #endregion

        #region Retired child migration

        /// <summary>Reports whether the retired child's MCP entry is still registered for this project.</summary>
        /// <returns>True when a migration is needed.</returns>
        private static bool HasRetiredEntry()
        {
            JObject config;
            string error;
            if (!File.Exists(RetiredConfigPath())
                || !TryRead(RetiredConfigPath(), out config, out error)) return false;

            JObject servers = ConduitConnectPlan.ServerMap(config, ProjectRoot(), "mcpServers", false);
            return servers != null && IsRetiredEntry(servers[RETIRED_ENTRY_KEY] as JObject);
        }

        /// <summary>Removes the retired child's MCP entry, leaving an owner-written server of that name.</summary>
        /// <param name="changed">Receives whether the entry was removed.</param>
        /// <param name="error">Receives why removal failed.</param>
        /// <returns>True when the config is correct afterwards.</returns>
        private static bool RemoveRetiredEntry(out bool changed, out string error)
        {
            changed = false;
            error = null;

            string path = RetiredConfigPath();
            if (!File.Exists(path)) return true;

            JObject config;
            if (!TryRead(path, out config, out error)) return false;

            JObject servers = ConduitConnectPlan.ServerMap(config, ProjectRoot(), "mcpServers", false);
            var existing = servers == null ? null : servers[RETIRED_ENTRY_KEY] as JObject;
            if (!IsRetiredEntry(existing)) return true;

            servers.Remove(RETIRED_ENTRY_KEY);
            changed = true;
            return TryWrite(path, config, out error);
        }

        /// <summary>Checks the retired child's environment marker on an entry.</summary>
        /// <param name="entry">Candidate entry.</param>
        /// <returns>True only for the entry Conduit itself wrote.</returns>
        private static bool IsRetiredEntry(JObject entry)
        {
            var environment = entry == null ? null : entry["env"] as JObject;
            return environment != null
                && string.Equals(
                    (string)environment[RETIRED_ENTRY_MARKER], "1", StringComparison.Ordinal);
        }

        #endregion

        #region JSON files

        /// <summary>Reads a JSON object, treating an absent file as an empty one.</summary>
        /// <param name="path">Absolute file path.</param>
        /// <param name="value">Receives parsed content.</param>
        /// <param name="error">Receives why it could not be read.</param>
        /// <returns>True when the file is absent or valid.</returns>
        private static bool TryRead(string path, out JObject value, out string error)
        {
            value = null;
            error = null;

            if (!File.Exists(path))
            {
                value = new JObject();
                return true;
            }

            string raw;
            try { raw = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception ex) { error = "it could not be read (" + ex.Message + ")"; return false; }

            try { value = JObject.Parse(raw); return true; }
            catch (Exception ex)
            {
                error = "it is not valid JSON (" + ex.Message + "), so it was left untouched";
                return false;
            }
        }

        /// <summary>Writes one JSON object with readable formatting and no byte-order mark.</summary>
        /// <param name="path">Absolute file path.</param>
        /// <param name="value">Content to store.</param>
        /// <param name="error">Receives why it could not be written.</param>
        /// <returns>True on success.</returns>
        private static bool TryWrite(string path, JObject value, out string error)
        {
            error = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(
                    path, value.ToString(Formatting.Indented) + Environment.NewLine,
                    new UTF8Encoding(false));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        #endregion
    }
}
