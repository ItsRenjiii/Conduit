/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Connection entries for the HTTP transport.
//
// Streamable HTTP is a transport of the MCP standard itself, not a per-client extra, so the entry written for
// every client is the same standard entry: an endpoint and the headers it must carry. What differs between
// clients is where the config file lives, whether it is JSON or TOML, and whether that file holds one server
// map or one per project - never whether the client can reach a local endpoint at all.
//
// Picking the file is part of getting that right, and it is where this went wrong once. An app can ship more
// than one config store: one read at startup for servers it spawns itself, another read per project by the
// sessions it runs. Writing an endpoint into a store whose schema only describes a command to spawn does not
// fail loudly - the entry is dropped at load, the window then reads the same file back and reports the client
// as not connected, and nothing anywhere says why.
//
// This ships. Nothing here may be decided by what happens to be installed on the machine that wrote it.
// An earlier revision refused a client on the grounds that its shape "has not been checked against the client
// itself" and that it "is not installed here" - both were statements about one developer's machine masquerading
// as product behaviour, and one of them was simply false. A buyer has different clients, different versions and
// none of this author's filesystem. Build to the standard and let the client reject it if it must.
//
// That mistake has a more expensive twin worth recording: an earlier revision routed one client through a
// console bridge because a document claimed it "cannot reach a local HTTP endpoint". That claim was invented
// rather than checked, and a whole sidecar process, a packaged runtime and its extraction were built on one
// sentence nobody verified.
//
// Everything here is a pure function over a snapshot. It opens no file and starts no process; the snapshot is
// supplied by the caller so a test can drive this exact code with fixed values rather than measure a copy.
using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>Whether a client's connection entry could be built, and what stopped it when it could not.</summary>
    internal enum ConduitConnectState
    {
        /// <summary>Built and safe to apply.</summary>
        Emitted,

        /// <summary>The transport is not serving, so there is no endpoint, identity or bearer to put in one.</summary>
        TransportDown,

        /// <summary>This window knows no config format or location for the client, so it has nothing to write.</summary>
        UnknownClient,
    }

    /// <summary>The shape of a built entry, which decides how the window applies it and what a check parses.</summary>
    internal enum ConduitConnectForm
    {
        /// <summary>Nothing was built.</summary>
        None,

        /// <summary>A command line the owner runs, letting the client merge its own config file.</summary>
        CliCommand,

        /// <summary>A JSON object merged into the client's config file.</summary>
        JsonFragment,

        /// <summary>A TOML block merged into the client's config file.</summary>
        TomlFragment,
    }

    /// <summary>
    /// The observed values an entry is built from. Taking these as data rather than reading them here is what
    /// lets a check drive the real builder with a transport that is down, or any other state that would
    /// otherwise need the editor arranged around it.
    /// </summary>
    internal sealed class ConduitConnectSnapshot
    {
        /// <summary>Per-project server key this project registers under.</summary>
        public string ServerName;

        /// <summary>Port the transport is bound to, or 0 when it is down.</summary>
        public int Port;

        /// <summary>Project identity digest the transport requires in its identity header.</summary>
        public string Identity;

        /// <summary>Bearer the transport requires. Never rendered into a log or a console report.</summary>
        public string Token;

        /// <summary>
        /// Bearer issued to each client, keyed by client display name, or null when none have been issued.
        /// A shared bearer proves only that somebody holds the secret, which cannot tell one attached client
        /// from another; a per-client one makes every request name its sender. A fixture snapshot leaves this
        /// null so a plan built from fixed values stays reproducible.
        /// </summary>
        public Dictionary<string, string> ClientTokens;

        /// <summary>
        /// Absolute project root, spelled with forward slashes, used as the key by a config that holds one
        /// server map per project. The separator is not cosmetic: the key is matched literally, so a
        /// backslash spelling registers a project the client never looks under.
        /// </summary>
        public string ProjectRoot;
    }

    /// <summary>One client's entry in the plan: what was built for it, or why nothing was.</summary>
    internal sealed class ConduitConnectEntry
    {
        /// <summary>Client display name this entry stands for.</summary>
        public string Client;

        /// <summary>Whether an entry was built, and what stopped it when it was not.</summary>
        public ConduitConnectState State;

        /// <summary>Shape of the entry, or None when there is none.</summary>
        public ConduitConnectForm Form;

        /// <summary>The exact text to apply or hand over, or null when nothing was built.</summary>
        public string Artifact;

        /// <summary>Why nothing was built, or null when something was.</summary>
        public string Reason;

        /// <summary>What the owner needs to know, or null when nothing is needed.</summary>
        public string Note;

        /// <summary>Endpoint the entry points at, or null when nothing was built.</summary>
        public string Url;

        /// <summary>Headers the entry carries, so a check can exercise them against the live endpoint.</summary>
        public Dictionary<string, string> Headers;
    }

    /// <summary>Builds one standard connection entry per supported client from a transport snapshot.</summary>
    internal static class ConduitConnectPlan
    {
        #region Constants

        /// <summary>Header carrying the project identity, which the transport requires on every request.</summary>
        internal const string IDENTITY_HEADER = "X-Conduit-Project";

        /// <summary>Header carrying the bearer, which the transport requires on every request.</summary>
        internal const string AUTHORIZATION_HEADER = "Authorization";

        /// <summary>Host the transport binds. It is loopback only, so no entry may name anything else.</summary>
        internal const string LOOPBACK_HOST = "127.0.0.1";

        /// <summary>Path the MCP endpoint answers on.</summary>
        internal const string MCP_PATH = "/mcp";

        /// <summary>
        /// Transport discriminator written beside the url. It is the standard's own name for this transport,
        /// and it is written for every JSON client rather than tailored per client: one that selects on the key
        /// reads it, and one that infers the transport from the url ignores it.
        /// </summary>
        internal const string HTTP_TRANSPORT = "http";

        /// <summary>
        /// Transport discriminator for a client that classifies by whether it spawns the server rather than by
        /// the protocol. Same endpoint and same headers; only the word differs.
        /// </summary>
        internal const string REMOTE_TRANSPORT = "remote";

        /// <summary>
        /// Added to the endpoint's wait budget when a client is told how long to allow for one call. The budget
        /// is how long a call is held before it answers with a job handle instead of a result, and a client
        /// whose limit equals it can abort in the instant that handle is on the wire. Sized to cover a local
        /// round trip with room spare rather than tuned to anything.
        /// </summary>
        private const int TIMEOUT_MARGIN_MS = 15000;

        /// <summary>Clients the plan answers for. Every one gets an entry, built or refused.</summary>
        internal static readonly string[] Clients = { "Claude Code", "Claude Desktop", "Cursor", "Antigravity", "Codex", "OpenCode" };

        #endregion

        #region Plan

        /// <summary>Builds the whole plan, one entry per supported client.</summary>
        /// <param name="snapshot">Observed transport state to build from.</param>
        /// <returns>One entry per client, in declared order.</returns>
        internal static List<ConduitConnectEntry> Build(ConduitConnectSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");

            var entries = new List<ConduitConnectEntry>(Clients.Length);
            var down = TransportDownReason(snapshot);

            foreach (var client in Clients)
            {
                entries.Add(down != null
                    ? Refused(client, ConduitConnectState.TransportDown, down)
                    : BuildFor(client, snapshot));
            }

            return entries;
        }

        /// <summary>
        /// Builds the entry for one client. The split is by CONFIG FORMAT, not by capability: every client here
        /// speaks the same transport, and the only question is what its config file looks like.
        /// </summary>
        /// <param name="client">Client display name to build for.</param>
        /// <param name="snapshot">Observed transport state.</param>
        /// <returns>The built entry, or a refusal carrying its reason.</returns>
        private static ConduitConnectEntry BuildFor(string client, ConduitConnectSnapshot snapshot)
        {
            snapshot = ForClient(client, snapshot);

            switch (client)
            {
                case "Claude Code":
                    return ClaudeCode(snapshot);

                // Every JSON client takes the same call. What differs between them is the map key, the url
                // field, the transport spelling and whether an enabled flag is read, and each of those is a
                // property of that client's file rather than an argument a caller has to remember correctly.
                case "Claude Desktop":
                case "Cursor":
                case "Antigravity":
                case "OpenCode":
                    return JsonClient(client, snapshot);

                case "Codex":
                    return TomlClient(client, snapshot);

                default:
                    return Refused(client, ConduitConnectState.UnknownClient,
                        "This window has no config format or location for " + client + ", so there is nothing " +
                        "it can write. The endpoint and its two headers are on the card above for any client " +
                        "that reads MCP servers from a file this window does not know.");
            }
        }

        /// <summary>
        /// Builds the Claude Code entry as a command rather than a config fragment. That client stores servers
        /// per directory and owns the merge, so handing over the command it documents avoids this code
        /// reproducing a file layout it does not control.
        /// </summary>
        /// <param name="snapshot">Observed transport state.</param>
        /// <returns>The built entry.</returns>
        private static ConduitConnectEntry ClaudeCode(ConduitConnectSnapshot snapshot)
        {
            var url = EndpointUrl(snapshot.Port);

            var artifact =
                "claude mcp add --scope local --transport http " + snapshot.ServerName + " " + url +
                " --header \"" + IDENTITY_HEADER + ": " + snapshot.Identity + "\"" +
                " --header \"" + AUTHORIZATION_HEADER + ": Bearer " + snapshot.Token + "\"";

            return new ConduitConnectEntry
            {
                Client   = "Claude Code",
                State    = ConduitConnectState.Emitted,
                Form     = ConduitConnectForm.CliCommand,
                Artifact = artifact,
                Note     = "Run it from this project's folder. Local scope binds the server to the directory it " +
                           "is added from, which is what keeps one project's tools out of another's session.",
                Url      = url,
                Headers  = HeadersFor(snapshot),
            };
        }

        /// <summary>Builds the entry for a client that stores its servers as JSON.</summary>
        /// <param name="client">Client display name.</param>
        /// <param name="snapshot">Observed transport state.</param>
        /// <returns>The built entry.</returns>
        private static ConduitConnectEntry JsonClient(string client, ConduitConnectSnapshot snapshot)
        {
            // Built through the same navigation the window merges with, so the fragment on the Copy button is
            // the shape the file actually wants rather than a second guess at it.
            var fragment = new JObject();
            ServerMap(fragment, ProjectKeyFor(client, snapshot), ServerMapKeyFor(client), true)[snapshot.ServerName] =
                JsonServerEntry(snapshot, UrlKeyFor(client), TransportTypeFor(client), DeclaresEnabledFor(client),
                    TimeoutMsFor(client));

            return new ConduitConnectEntry
            {
                Client   = client,
                State    = ConduitConnectState.Emitted,
                Form     = ConduitConnectForm.JsonFragment,
                Artifact = fragment.ToString(Formatting.Indented),
                Note     = ApplyNote(client),
                Url      = EndpointUrl(snapshot.Port),
                Headers  = HeadersFor(snapshot),
            };
        }

        /// <summary>Builds the entry for a client that stores its servers as TOML.</summary>
        /// <param name="client">Client display name.</param>
        /// <param name="snapshot">Observed transport state.</param>
        /// <returns>The built entry.</returns>
        private static ConduitConnectEntry TomlClient(string client, ConduitConnectSnapshot snapshot)
        {
            return new ConduitConnectEntry
            {
                Client   = client,
                State    = ConduitConnectState.Emitted,
                Form     = ConduitConnectForm.TomlFragment,
                Artifact = TomlBlock(snapshot),
                Note     = ApplyNote(client),
                Url      = EndpointUrl(snapshot.Port),
                Headers  = HeadersFor(snapshot),
            };
        }

        /// <summary>What the owner needs to know after one file-based client entry is applied.</summary>
        /// <param name="client">Client whose connection was written.</param>
        /// <returns>The client-specific restart and process note.</returns>
        private static string ApplyNote(string client)
        {
            if (string.Equals(client, "Claude Desktop", StringComparison.Ordinal))
            {
                return "Restart Claude after connecting. Its direct MCP entry stays process-free, and a "
                    + "session-start hook hands over the exact return path Conduit needs for work that "
                    + "finishes later and then exits, so nothing of its own is left running.";
            }

            return "Restart the client after connecting. The entry is keyed per project, which costs "
                + "nothing: a direct connection starts no process, and a project whose editor is closed "
                + "refuses the connection rather than leaving one running.";
        }

        #endregion

        #region Values

        /// <summary>
        /// The key a client nests its server map under, or null when its config holds a single map at the
        /// root. This is a property of the file a client reads, which is why it lives beside the rest of the
        /// per-client shape rather than in the window.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <param name="snapshot">Observed transport state, which carries the project root.</param>
        /// <returns>The project key, or null for a config that is not project scoped.</returns>
        internal static string ProjectKeyFor(string client, ConduitConnectSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");

            return client == "Claude Desktop" ? snapshot.ProjectRoot : null;
        }

        /// <summary>
        /// The key a client nests its server map under inside its config document. Two spellings exist in the
        /// wild and neither is a default worth assuming, so it is stated per client.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>The map key.</returns>
        internal static string ServerMapKeyFor(string client)
        {
            return client == "OpenCode" ? "mcp" : "mcpServers";
        }

        /// <summary>The field a client reads the endpoint from.</summary>
        /// <param name="client">Client display name.</param>
        /// <returns>The url field name.</returns>
        internal static string UrlKeyFor(string client)
        {
            // Same standard transport, different spelling for the same idea.
            return client == "Antigravity" ? "serverUrl" : "url";
        }

        /// <summary>
        /// The transport discriminator a client reads beside the url, or null where it reads none. The value
        /// differs as well as its presence: one client names this transport after the protocol and another
        /// after the fact that the server is not a process it spawns.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>The discriminator value, or null to omit the key.</returns>
        internal static string TransportTypeFor(string client)
        {
            switch (client)
            {
                case "OpenCode":    return REMOTE_TRANSPORT;
                case "Antigravity": return null;
                default:            return HTTP_TRANSPORT;
            }
        }

        /// <summary>
        /// Whether a client reads an enabled flag on a server entry.
        ///
        /// Written for the one client whose schema declares it. That client switches a server off only on an
        /// explicit false, so an omitted key leaves it on and the flag is not load bearing. It is written
        /// anyway: a config someone opens should say which servers are on rather than leave that to be known,
        /// and a default that later moves costs nothing to have pinned.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>True when the flag is written.</returns>
        internal static bool DeclaresEnabledFor(string client)
        {
            return client == "OpenCode";
        }

        /// <summary>
        /// How long a client is told to allow for one call, or null where it declares no limit and its own
        /// default governs.
        ///
        /// Derived from the endpoint's wait budget rather than chosen, because the failure is relational: a
        /// client that gives up first aborts a call the endpoint is still holding, the job it started runs on
        /// unseen, and the caller is told work failed that in fact completed. The one client this is written
        /// for defaults to well under that budget, which is the compile time this project actually has, so
        /// leaving the key out would lose exactly the calls that recompile. The same figure governs connecting,
        /// which is wanted: an editor mid-compile is worth waiting for, and one that is not running refuses the
        /// connection at once rather than holding it.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>The limit in milliseconds, or null to omit the key.</returns>
        internal static int? TimeoutMsFor(string client)
        {
            return client == "OpenCode" ? ConduitMcpEndpoint.TOOL_WAIT_MS + TIMEOUT_MARGIN_MS : (int?)null;
        }

        /// <summary>
        /// Navigates a config document to the server map a client keeps its entries in. One function serves
        /// the plan, which builds a fragment from an empty document, and the window, which merges into a live
        /// one and reads it back to answer whether the client is connected, so the shape written and the
        /// shape looked for cannot drift apart.
        /// </summary>
        /// <param name="root">Config document to navigate.</param>
        /// <param name="projectKey">Key to nest under, or null when the document is not project scoped.</param>
        /// <param name="create">Whether to create the missing objects on the way down.</param>
        /// <returns>The server map, or null when it is absent and creation was not asked for.</returns>
        internal static JObject ServerMap(JObject root, string projectKey, string mapKey, bool create)
        {
            if (root == null) throw new ArgumentNullException("root");

            var owner = root;

            if (!string.IsNullOrEmpty(projectKey))
            {
                var projects = root["projects"] as JObject;
                if (projects == null)
                {
                    if (!create) return null;
                    projects = new JObject();
                    root["projects"] = projects;
                }

                var project = projects[projectKey] as JObject;
                if (project == null)
                {
                    // An entry spelling this same path differently is the one the client has been using, so
                    // adopt it rather than starting a rival beside it. The exact key is tried first, so when
                    // both spellings exist the client's own wins and the other is left untouched.
                    foreach (var candidate in projects.Properties())
                    {
                        if (!SameProject(candidate.Name, projectKey)) continue;

                        project = candidate.Value as JObject;
                        if (project != null) break;
                    }
                }

                if (project == null)
                {
                    if (!create) return null;
                    project = new JObject();
                    projects[projectKey] = project;
                }

                owner = project;
            }

            if (string.IsNullOrEmpty(mapKey)) throw new ArgumentNullException("mapKey");

            var servers = owner[mapKey] as JObject;
            if (servers != null) return servers;
            if (!create) return null;

            servers = new JObject();
            owner[mapKey] = servers;
            return servers;
        }

        /// <summary>
        /// The snapshot as one client sees it, carrying the bearer issued to that client. Returned unchanged
        /// when no per-client bearer exists, so a fixture snapshot and a client whose token has not been
        /// minted yet both keep the shared one rather than being refused a config.
        /// </summary>
        /// <param name="client">Client display name to resolve a bearer for.</param>
        /// <param name="snapshot">Observed transport state.</param>
        /// <returns>The snapshot, or a copy carrying that client's own bearer.</returns>
        private static ConduitConnectSnapshot ForClient(string client, ConduitConnectSnapshot snapshot)
        {
            string token = null;
            if (snapshot.ClientTokens != null) snapshot.ClientTokens.TryGetValue(client, out token);
            if (string.IsNullOrEmpty(token) || token == snapshot.Token) return snapshot;

            return new ConduitConnectSnapshot
            {
                ServerName   = snapshot.ServerName,
                Port         = snapshot.Port,
                Identity     = snapshot.Identity,
                Token        = token,
                ProjectRoot  = snapshot.ProjectRoot,
                ClientTokens = snapshot.ClientTokens,
            };
        }

        /// <summary>
        /// Whether two config keys name the same project. A per-project config is a dictionary keyed by an
        /// absolute path, and on Windows one path has two spellings - the separators the OS writes, and
        /// forward slashes - which are one folder to the filesystem and two keys to the dictionary. Matching
        /// literally is what leaves a server registered in an entry nobody reads.
        /// </summary>
        /// <param name="left">A key already present in the document.</param>
        /// <param name="right">The key being looked for.</param>
        /// <returns>True when both name the same path.</returns>
        private static bool SameProject(string left, string right)
        {
            // Case folds on the platforms whose separator is a backslash; elsewhere a path's case is its own.
            var comparison = System.IO.Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return string.Equals(NormalizeProjectKey(left), NormalizeProjectKey(right), comparison);
        }

        /// <summary>Reduces a project key to one comparable spelling.</summary>
        /// <param name="key">Key to normalise.</param>
        /// <returns>The key with forward slashes and no trailing separator.</returns>
        private static string NormalizeProjectKey(string key)
        {
            return (key ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        }

        /// <summary>
        /// The standard server entry: transport, endpoint, and the two headers the transport requires.
        /// Returned as the entry alone rather than wrapped, so a caller merging into a live config assigns it
        /// under the server key and leaves every other server untouched.
        /// </summary>
        /// <param name="snapshot">Observed transport state.</param>
        /// <param name="urlKey">Field this client reads the endpoint from.</param>
        /// <param name="transportType">
        /// The transport discriminator to write, or null to omit the key. It is written where a client reads
        /// it and omitted where none exists, rather than sent everywhere on the assumption that a spare key
        /// is harmless.
        /// </param>
        /// <param name="declareEnabled">Whether this client reads an enabled flag on the entry.</param>
        /// <param name="timeoutMs">
        /// How long this client is told to allow for one call, or null to omit the key and leave the client's
        /// own default in charge.
        /// </param>
        /// <returns>The server entry object.</returns>
        internal static JObject JsonServerEntry(
            ConduitConnectSnapshot snapshot, string urlKey, string transportType, bool declareEnabled,
            int? timeoutMs)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            if (string.IsNullOrEmpty(urlKey)) throw new ArgumentNullException("urlKey");

            var entry = new JObject();
            if (!string.IsNullOrEmpty(transportType)) entry["type"] = transportType;
            if (declareEnabled) entry["enabled"] = true;
            if (timeoutMs.HasValue) entry["timeout"] = timeoutMs.Value;

            entry[urlKey] = EndpointUrl(snapshot.Port);
            entry["headers"] = new JObject
            {
                [AUTHORIZATION_HEADER] = "Bearer " + snapshot.Token,
                [IDENTITY_HEADER] = snapshot.Identity,
            };

            return entry;
        }

        /// <summary>
        /// The TOML block a TOML-based client stores for this project. The header table is written inline
        /// because that is the form measured against a real client; a sub-table parses identically in TOML but
        /// an entry shape is not worth being clever about.
        /// </summary>
        /// <param name="snapshot">Observed transport state.</param>
        /// <returns>The block, newline terminated.</returns>
        internal static string TomlBlock(ConduitConnectSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");

            return "[mcp_servers." + snapshot.ServerName + "]\n" +
                   "url = \"" + EndpointUrl(snapshot.Port) + "\"\n" +
                   "http_headers = { " + AUTHORIZATION_HEADER + " = \"Bearer " + snapshot.Token + "\", " +
                   "\"" + IDENTITY_HEADER + "\" = \"" + snapshot.Identity + "\" }\n";
        }

        /// <summary>The endpoint an MCP client connects to.</summary>
        /// <param name="port">Port the transport is bound to.</param>
        /// <returns>The absolute endpoint URL.</returns>
        internal static string EndpointUrl(int port)
        {
            return "http://" + LOOPBACK_HOST + ":" + port.ToString(CultureInfo.InvariantCulture) + MCP_PATH;
        }

        /// <summary>The headers every request to the transport must carry.</summary>
        /// <param name="snapshot">Observed transport state.</param>
        /// <returns>Header name to value.</returns>
        internal static Dictionary<string, string> HeadersFor(ConduitConnectSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");

            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { IDENTITY_HEADER, snapshot.Identity },
                { AUTHORIZATION_HEADER, "Bearer " + snapshot.Token },
            };
        }

        /// <summary>
        /// Why no entry can be built at all, or null when the transport has everything one needs. An entry
        /// missing any of these would be applied, look right, and answer 401 or 403 forever.
        /// </summary>
        /// <param name="snapshot">Observed transport state.</param>
        /// <returns>The reason, or null when the transport is serving.</returns>
        private static string TransportDownReason(ConduitConnectSnapshot snapshot)
        {
            if (snapshot.Port <= 0) return "The transport is not bound, so there is no endpoint to connect to yet.";
            if (Blank(snapshot.Identity)) return "The transport published no project identity, which every request has to carry.";
            if (Blank(snapshot.Token)) return "The transport published no bearer, which every request has to carry.";
            if (Blank(snapshot.ServerName)) return "This project has no server key to register under.";
            if (Blank(snapshot.ProjectRoot)) return "This project has no root path, which a per-project config entry is keyed by.";

            return null;
        }

        /// <summary>Builds an entry carrying a refusal and nothing to apply.</summary>
        /// <param name="client">Client display name.</param>
        /// <param name="state">What stopped the build.</param>
        /// <param name="reason">The reason, shown to the owner.</param>
        /// <returns>The refusal entry.</returns>
        private static ConduitConnectEntry Refused(string client, ConduitConnectState state, string reason)
        {
            return new ConduitConnectEntry
            {
                Client = client,
                State  = state,
                Form   = ConduitConnectForm.None,
                Reason = reason,
            };
        }

        /// <summary>Whether a value is absent for the purpose of building an entry from it.</summary>
        /// <param name="value">Value to test.</param>
        /// <returns>True when null, empty, or whitespace only.</returns>
        private static bool Blank(string value)
        {
            return string.IsNullOrEmpty(value) || value.Trim().Length == 0;
        }

        #endregion
    }
}
