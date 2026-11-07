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
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// ItsRenji Conduit control panel. Shows bridge status and one-click wires this project's MCP endpoint
    /// into each supported AI client by merging an entry into that client's own config, at the root of the
    /// file or under its per-project key as that client expects, and never overwriting a config it cannot
    /// parse.
    /// </summary>
    internal sealed class ConduitWindow : EditorWindow
    {
        /// <summary>
        /// This window's own stylesheet, loaded by GUID so dragging the folder cannot silently strip the
        /// skin. It is the window's OWN sheet on purpose: the inspector examples are reference material and
        /// nothing outside them is allowed to depend on them, so the look is shared and the file is not.
        /// </summary>
        private const string STYLE_GUID = "ec6c280f91140af4c9415d258367972f";

        /// <summary>Name fallback if the GUID ever fails to resolve, so a rename degrades rather than breaks.</summary>
        private const string STYLE_NAME = "ConduitWindow";

        /// <summary>
        /// The brand mark, by GUID and never by Resources.Load. A load by name would be ambiguous the moment
        /// a product in the same project ships a logo of its own under that name, and would pick one of them
        /// arbitrarily; this is Conduit's own copy and resolves to exactly it.
        /// </summary>
        private const string LOGO_GUID = "4abc445bff2f9fa4198cabbf5b8eaa14";

        /// <summary>Name fallback for the brand mark, searched only inside Conduit's own folder.</summary>
        private const string LOGO_NAME = "ConduitLogo";

        /// <summary>Cached brand mark, so the asset is resolved once rather than on every rebuild.</summary>
        private static Texture2D _logo;

        /// <summary>How often the connection and skill chips are re-read from disk, in milliseconds.</summary>
        private const long POLL_INTERVAL_MS = 2000;

        /// <summary>
        /// MCP config key for THIS project's server entry - the sanitised project name. Each open
        /// project registers under its own key, so a single client lists one toolset per project and
        /// an agent can drive several projects in parallel by targeting each server by name.
        /// </summary>
        private static string ServerName => ProjectName();

        /// <summary>The supported AI clients, in display order; one card is drawn per entry.</summary>
        private static readonly string[] _clients = { "Claude Desktop", "Cursor", "Antigravity", "Codex", "OpenCode" };

        /// <summary>File name of the console bridge, used to retire an entry that still launches it.</summary>
        private const string BRIDGE_EXECUTABLE_NAME = "ItsRenji.Conduit.StdioBridge";

        /// <summary>Cached stylesheet, so the asset is resolved once rather than on every rebuild.</summary>
        private static StyleSheet _styleSheet;

        /// <summary>The live client cards, kept so the poll can update them without rebuilding the tree.</summary>
        private readonly List<ClientCard> _cards = new List<ClientCard>();

        #region File history state

        /// <summary>Storage budget control, bounded by measured usage.</summary>
        private SliderInt _historyLimit;

        /// <summary>Measured storage and retained version count.</summary>
        private Label _historyUsage;

        /// <summary>Current minimum, pending save or actionable read failure.</summary>
        private Label _historyMessage;

        /// <summary>Explicitly saves the slider's pending choice.</summary>
        private Button _historySave;

        /// <summary>Most recently measured store state; null until a successful read.</summary>
        private ConduitFileHistory.Usage _historyData;

        /// <summary>Filesystem operation running off the editor thread.</summary>
        private Task<ConduitFileHistory.Usage> _historyTask;

        /// <summary>Cooperative cancellation for the active filesystem operation.</summary>
        private CancellationTokenSource _historyCancellation;

        /// <summary>Editor time beyond which the pending operation stops holding the controls.</summary>
        private double _historyDeadline;

        /// <summary>Set by store notifications, including those arriving on a worker.</summary>
        private int _historyRefreshRequested;

        /// <summary>True when the pending worker is saving an explicit user choice.</summary>
        private bool _historySaving;

        /// <summary>User's pending storage choice in MiB, preserved across card rebuilds.</summary>
        private int _historyDraftMiB = 256;

        /// <summary>Whether the user has moved the slider since its last successful save.</summary>
        private bool _historyHasDraft;

        #endregion


        /// <summary>
        /// The elements of one client card that carry state, held together so a poll can refresh them
        /// without rebuilding the panel. Rebuilding on a timer would fight anything the user is doing.
        /// </summary>
        private sealed class ClientCard
        {
            /// <summary>Client display name this card stands for.</summary>
            public string Client;

            /// <summary>Lit while this client holds a live stream, which is the only proof of attachment.</summary>
            public Label ChipConnected;

            /// <summary>Lit while an entry exists that does not carry this client's current credentials.</summary>
            public Label ChipOutOfDate;

            /// <summary>Lit while no server entry exists in this client's config.</summary>
            public Label ChipOffline;

            /// <summary>Lit while Connect has written credentials this client has never presented.</summary>
            public Label ChipRestart;

            /// <summary>Lit while the config is loaded and correct but no connection is being held.</summary>
            public Label ChipDropped;

            /// <summary>Lit while a document is newer than the skill generated from it.</summary>
            public Label ChipSkills;

            /// <summary>Connect, which becomes Update skills when only the conventions are stale.</summary>
            public Button Connect;

            /// <summary>Remove, enabled only while an entry exists.</summary>
            public Button Remove;
        }

        /// <summary>Menu entry point: opens (or focuses) the Conduit window with its minimum size.</summary>
        [MenuItem("ItsRenji/Conduit")]
        private static void Open()
        {
            var window = GetWindow<ConduitWindow>(false, "ItsRenji Conduit", true);
            window.minSize = new Vector2(540f, 440f);
            window.Show();
        }

        /// <summary>Builds the whole panel once, then leaves a poll to keep the state chips honest.</summary>
        private void CreateGUI()
        {
            Rebuild();
        }

        /// <summary>Subscribes to history changes without polling the filesystem.</summary>
        private void OnEnable()
        {
            ConduitFileHistory.Changed += RequestHistoryRefresh;
            EditorApplication.update += TickHistory;
            RequestHistoryRefresh();
        }

        /// <summary>Refreshes externally changed settings when the user returns to this window.</summary>
        private void OnFocus()
        {
            RequestHistoryRefresh();
        }

        /// <summary>Releases callbacks and cancels outstanding history work when the window closes or reloads.</summary>
        private void OnDisable()
        {
            ConduitFileHistory.Changed -= RequestHistoryRefresh;
            EditorApplication.update -= TickHistory;
            CancelHistoryTask();
        }

        /// <summary>
        /// Rebuilds the panel from scratch. Called on open and after any action that changes what the
        /// window is reporting, because a connect or a remove changes half the controls at once.
        /// </summary>
        private void Rebuild()
        {
            _cards.Clear();

            VisualElement root = rootVisualElement;
            root.Clear();
            root.AddToClassList("cw-root");

            StyleSheet sheet = LoadStyleSheet();
            if (sheet != null && !root.styleSheets.Contains(sheet))
            {
                root.styleSheets.Add(sheet);
            }

            // The night runs the full height of the window, not just behind the header, so every card reads
            // as a lit surface lying on it. The field is drawn rather than shipped as art: see the starfield
            // for why a soft glow would band on an eight bit display.
            root.style.backgroundImage = new StyleBackground(ConduitWindowStarfield.Tile);

            ScrollView scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1f;
            root.Add(scroll);

            scroll.Add(BuildHeader());
            scroll.Add(BuildStatusCard());
            scroll.Add(BuildHistoryCard());

            foreach (string client in _clients)
            {
                scroll.Add(BuildClientCard(client));
            }

            scroll.Add(BuildAboutCard());

            RefreshState();

            // The chips read two files per client off disk, so they are polled rather than bound: the
            // answer only changes when the user presses a button here or edits a document elsewhere.
            root.schedule.Execute(RefreshState).Every(POLL_INTERVAL_MS);
        }

        /// <summary>Builds the brand title block that sits on the night, above every card.</summary>
        /// <returns>Header element.</returns>
        private static VisualElement BuildHeader()
        {
            VisualElement header = new VisualElement();
            header.AddToClassList("cw-header");

            Texture2D logo = LoadLogo();
            if (logo != null)
            {
                Image mark = new Image { image = logo, scaleMode = ScaleMode.ScaleToFit };
                mark.AddToClassList("cw-header__logo");
                header.Add(mark);
            }

            Label title = new Label("ItsRenji Conduit");
            title.AddToClassList("cw-header__title");
            header.Add(title);

            Label subtitle = new Label("Unity agent bridge, MCP native");
            subtitle.AddToClassList("cw-header__subtitle");
            header.Add(subtitle);

            return header;
        }

        /// <summary>Builds the bridge / server status card.</summary>
        /// <summary>
        /// Describes the tool surface using the number a client actually receives, which is the published
        /// catalogue count rather than the verb count.
        ///
        /// The two differ by exactly two, because conduit-run and job_status are published tools that are
        /// not bridge verbs. Reporting the verb count under the word "tools" made this window disagree with
        /// the descriptor, with /health and with the client's own list, and a status window that disagrees
        /// with the thing it reports on is worse than one that shows nothing.
        ///
        /// Before a catalogue is published there is no client-facing number to report, so the verb count is
        /// shown under its own name rather than dressed up as a tool count.
        /// </summary>
        /// <returns>A sentence for the Commands row.</returns>
        private static string PublishedToolSummary()
        {
            if (!ConduitMcpCatalog.IsPublished)
            {
                return ConduitBridge.CommandCount + " verbs, catalogue not published yet";
            }

            return ConduitMcpCatalog.ToolCount + " tools (" + ConduitBridge.CommandCount + " verbs)";
        }

        /// <returns>Status card element.</returns>
        private VisualElement BuildStatusCard()
        {
            VisualElement card = Card("Status");

            int port = ConduitHttpTransport.Port;

            card.Add(Row("Bridge", "Active"));
            card.Add(Row("Project", ConduitBridge.ProjectKindLabel()));
            card.Add(Row("Commands", PublishedToolSummary()));
            card.Add(Row("Unity", Application.unityVersion));
            card.Add(Row("Config key", ServerName));
            card.Add(Row("Endpoint", port > 0 ? ConduitConnectPlan.EndpointUrl(port) : "not bound"));
            card.Add(Row("Identity", Masked(ConduitHttpTransport.Identity)));
            card.Add(Row("Bearer", Masked(ConduitHttpTransport.Token)));

            VisualElement actions = Actions();
            actions.Add(Button("Refresh agent tools", () => ShowNotification(new GUIContent(ConduitBridge.BumpToolsEpoch()))));
            card.Add(actions);
            card.Add(Note("Refresh after editing the bridge - connected agents reload their tool list, no client restart.", true));

            return card;
        }

        /// <summary>
        /// The live values a connection artifact is built from. Every supported client reaches this editor over
        /// the loopback endpoint directly, so the snapshot is the port, the identity, the bearer and the project
        /// root a per-project config keys its entry by; a missing one becomes a stated refusal in the plan
        /// rather than a blank inside an artifact that gets applied.
        /// </summary>
        /// <returns>The snapshot to build a plan from.</returns>
        internal static ConduitConnectSnapshot LiveSnapshot()
        {
            // Every client is issued its own bearer here rather than on demand deeper in, so the plan, the
            // Copy buttons and the file the window writes all carry the same one for a given client.
            var clientTokens = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string client in ConduitConnectPlan.Clients)
            {
                clientTokens[client] = ConduitHttpTransport.TokenForClient(client);
            }

            return new ConduitConnectSnapshot
            {
                ServerName   = ServerName,
                Port         = ConduitHttpTransport.Port,
                Identity     = ConduitHttpTransport.Identity,
                Token        = ConduitHttpTransport.Token,
                ProjectRoot  = ProjectKey(),
                ClientTokens = clientTokens,
            };
        }

        /// <summary>
        /// Renders a value for display without putting the whole of it on screen. The bearer is a secret and
        /// the identity is long, and both are available in full from their copy buttons.
        /// </summary>
        /// <param name="value">The value to mask.</param>
        /// <returns>A leading fragment and the length, or a note that there is nothing published yet.</returns>
        private static string Masked(string value)
        {
            if (string.IsNullOrEmpty(value)) return "not published";
            return value.Substring(0, Math.Min(8, value.Length)) + "... (" + value.Length + " chars)";
        }

        /// <summary>Builds one client card with its status chip and actions.</summary>
        /// <param name="client">Client display name the card stands for.</param>
        /// <returns>Client card element.</returns>
        private VisualElement BuildClientCard(string client)
        {
            string configPath = ConfigPath(client);
            bool skillsSupported = ConduitSkills.Supported(client);

            VisualElement card = new VisualElement();
            card.AddToClassList("cw-card");

            VisualElement head = new VisualElement();
            head.AddToClassList("cw-card__head");
            card.Add(head);

            Label name = new Label(client);
            name.AddToClassList("cw-card__heading");
            head.Add(name);

            // One chip per condition, each hidden unless its own condition holds. A single chip that swaps its
            // text has to pick a winner, and the precedence order doing the picking is hand-written and can be
            // wrong, which silently drops a state the user needed. These cannot hide one another: two lighting
            // at once is a contradiction made visible rather than swallowed.
            Label chipConnected = Chip(head, "connected", "cw-chip--on");
            // "not set up" rather than "not connected": this reports whether an ENTRY EXISTS, while the green
            // reports whether a client is attached. They are two axes, and naming them as two ends of one
            // reads as a contradiction whenever both are true, which they legitimately can be.
            Label chipOffline = Chip(head, "not set up", "cw-chip--off");
            Label chipOutOfDate = Chip(head, "connection out of date", "cw-chip--warn");
            Label chipRestart = Chip(head, "restart to connect", "cw-chip--warn");
            Label chipDropped = Chip(head, "not connected", "cw-chip--off");
            Label chipSkills = Chip(head, "skills out of date", "cw-chip--warn");

            // Shown with forward slashes. The value itself keeps the separators the OS gave it and is only
            // ever used to open or write the file; this is the display copy. Backslashes are what the drawn
            // text was mangling, which is why the one path row that already forward-slashed its value was the
            // only one rendering cleanly.
            Label path = new Label(configPath.Replace('\\', '/'));
            path.AddToClassList("cw-path");
            card.Add(path);

            VisualElement actions = Actions();

            Button connect = Button("Connect", () =>
            {
                Connect(client, configPath);
                Rebuild();
            });
            connect.AddToClassList("cw-button--primary");
            actions.Add(connect);

            Button remove = Button("Remove", () =>
            {
                RemoveConduit(client, configPath);
                Rebuild();
            });
            actions.Add(remove);

            actions.Add(Compact("Copy", () =>
            {
                EditorGUIUtility.systemCopyBuffer = Snippet(client);
                ShowNotification(new GUIContent("Config copied"));
            }));

            actions.Add(Compact("Open MCP", () => Reveal(configPath)));

            Button skills = Compact("Open skills", () => Reveal(ConduitSkills.SkillsRoot(client)));
            skills.SetEnabled(skillsSupported);
            actions.Add(skills);

            actions.Add(Compact("Download", () =>
            {
                string url = DownloadUrl(client);
                if (!string.IsNullOrEmpty(url)) Application.OpenURL(url);
            }));

            card.Add(actions);

            _cards.Add(new ClientCard
            {
                Client        = client,
                ChipConnected = chipConnected,
                ChipOutOfDate = chipOutOfDate,
                ChipOffline   = chipOffline,
                ChipRestart   = chipRestart,
                ChipDropped   = chipDropped,
                ChipSkills    = chipSkills,
                Connect       = connect,
                Remove        = remove,
            });
            return card;
        }

        #region File history card

        /// <summary>Builds the user-facing version-history budget card using the window's own styles.</summary>
        /// <returns>A card with measured usage and a guarded whole-MiB slider.</returns>
        private VisualElement BuildHistoryCard()
        {
            VisualElement card = Card("File history");
            card.name = "conduit-file-history";
            card.Add(Note("Conduit keeps versions of the scripts and shaders it changes. Your agent can bring back an earlier version, or return to a newer saved version.", false));
            card.Add(Note("Saved versions are compressed to use less space. Choose how much disk space to keep for history, up to 1 GiB. Older versions make room for new ones when a limit is reached.", true));
            _historyUsage = Note("Checking saved versions...", false);
            _historyUsage.name = "history-usage";
            _historyUsage.AddToClassList("cw-history-usage");
            card.Add(_historyUsage);
            _historyLimit = new SliderInt("Storage limit (MiB)", 1, ConduitFileHistory.MAX_BUDGET_MIB)
            {
                name = "history-limit-mib",
                showInputField = true,
                tooltip = "MiB measures disk space. This limit cannot be lower than the space your saved history already uses."
            };
            _historyLimit.AddToClassList("cw-history-slider");
            _historyLimit.SetValueWithoutNotify(_historyDraftMiB);
            _historyLimit.RegisterValueChangedCallback(change =>
            {
                int minimum = _historyData == null ? 1 : _historyData.MinimumMiB;
                _historyDraftMiB = Mathf.Clamp(change.newValue, minimum, ConduitFileHistory.MAX_BUDGET_MIB);
                _historyLimit.SetValueWithoutNotify(_historyDraftMiB);
                _historyHasDraft = true;
                RenderHistoryState();
            });
            card.Add(_historyLimit);
            _historyMessage = Note("Checking how much space your saved history needs.", true);
            _historyMessage.name = "history-message";
            card.Add(_historyMessage);
            VisualElement actions = Actions();
            _historySave = Button("Save limit", () => StartHistoryTask(_historyDraftMiB));
            _historySave.AddToClassList("cw-button--primary");
            actions.Add(_historySave);
            actions.Add(Compact("Check again", RequestHistoryRefresh));
            card.Add(actions);
            RenderHistoryState();
            RequestHistoryRefresh();
            return card;
        }

        /// <summary>Marks history stale without touching Unity from the notifying worker thread.</summary>
        private void RequestHistoryRefresh()
        {
            Interlocked.Exchange(ref _historyRefreshRequested, 1);
        }

        /// <summary>Starts one bounded measurement or an explicit budget save without blocking the editor.</summary>
        /// <param name="mib">Requested budget, or null for a read-only refresh.</param>
        private void StartHistoryTask(int? mib)
        {
            if (_historyTask != null) return;
            if (mib.HasValue && (_historyData == null || mib.Value < _historyData.MinimumMiB))
            {
                RequestHistoryRefresh();
                return;
            }
            var store = new ConduitFileHistory(Path.GetDirectoryName(Application.dataPath));
            _historyCancellation = new CancellationTokenSource();
            CancellationToken token = _historyCancellation.Token;
            _historySaving = mib.HasValue;
            _historyDeadline = EditorApplication.timeSinceStartup + 10d;
            _historyTask = Task.Run(() => mib.HasValue ? store.SetLimitMiB(mib.Value, token) : store.ReadUsage(token));
            RenderHistoryState();
        }

        /// <summary>Consumes completed workers and store-change notifications only while this window exists.</summary>
        private void TickHistory()
        {
            if (_historyLimit == null) return;
            if (_historyTask != null)
            {
                if (!_historyTask.IsCompleted)
                {
                    if (EditorApplication.timeSinceStartup < _historyDeadline) return;
                    CancelHistoryTask();
                    _historyData = null;
                    RenderHistoryState();
                    _historyMessage.text = "Checking history took too long. Press Check again to retry.";
                    return;
                }
                var completed = _historyTask;
                bool wasSaving = _historySaving;
                _historyTask = null;
                _historySaving = false;
                _historyCancellation.Dispose();
                _historyCancellation = null;
                if (completed.IsFaulted || completed.IsCanceled)
                {
                    string reason = completed.Exception?.GetBaseException().Message ?? "The operation was cancelled.";
                    _historyData = null;
                    RenderHistoryState();
                    _historyMessage.text = wasSaving ? "The limit could not be saved. Check again to see the current limit." : "Couldn't read file history. Press Check again to retry.";
                    _historyMessage.tooltip = reason;
                    Interlocked.Exchange(ref _historyRefreshRequested, 0);
                    return;
                }
                _historyData = completed.Result;
                if (wasSaving) _historyHasDraft = false;
                if (!_historyHasDraft) _historyDraftMiB = (int)((_historyData.MaxBytes + ConduitFileHistory.MIB_BYTES - 1) / ConduitFileHistory.MIB_BYTES);
                RenderHistoryState();
            }
            if (_historyTask == null && Interlocked.Exchange(ref _historyRefreshRequested, 0) != 0)
                StartHistoryTask(null);
        }

        /// <summary>Renders all loading, unavailable, pending and saved states without replacing the controls.</summary>
        private void RenderHistoryState()
        {
            if (_historyLimit == null) return;
            bool known = _historyData != null;
            bool busy = _historyTask != null;
            int minimum = known ? _historyData.MinimumMiB : 1;
            _historyLimit.lowValue = minimum;
            _historyLimit.highValue = Math.Max(minimum, ConduitFileHistory.MAX_BUDGET_MIB);
            _historyDraftMiB = Mathf.Clamp(_historyDraftMiB, minimum, Math.Max(minimum, ConduitFileHistory.MAX_BUDGET_MIB));
            _historyLimit.SetValueWithoutNotify(_historyDraftMiB);
            _historyLimit.SetEnabled(known && !busy && minimum <= ConduitFileHistory.MAX_BUDGET_MIB);
            _historySave?.SetEnabled(known && !busy && minimum <= ConduitFileHistory.MAX_BUDGET_MIB
                && _historyDraftMiB * ConduitFileHistory.MIB_BYTES != _historyData.MaxBytes);
            if (_historyUsage == null || _historyMessage == null) return;
            _historyUsage.text = known
                ? string.Format("Using {0:0.##} MiB of {1:0.##} MiB. {2} saved {3}.",
                    _historyData.Bytes / (double)ConduitFileHistory.MIB_BYTES,
                    _historyData.MaxBytes / (double)ConduitFileHistory.MIB_BYTES,
                    _historyData.Versions, _historyData.Versions == 1 ? "version" : "versions")
                : busy ? "Checking saved versions..." : "History usage is unavailable.";
            _historyMessage.tooltip = string.Empty;
            _historyMessage.text = busy
                ? _historySaving ? "Saving your limit..." : "Checking how much space your saved history needs."
                : known ? minimum > ConduitFileHistory.MAX_BUDGET_MIB
                    ? "Your saved history already exceeds 1 GiB. Existing versions have been left untouched."
                    : "Minimum: " + minimum + " MiB to protect existing saved versions. Up to " + _historyData.VersionsPerFile + " versions per file."
                : "Press Check again to read the current history settings.";
            _historyLimit.MarkDirtyRepaint();
        }

        /// <summary>Cancels work and observes its eventual result without blocking shutdown or losing exceptions.</summary>
        private void CancelHistoryTask()
        {
            var task = _historyTask;
            var cancellation = _historyCancellation;
            _historyTask = null;
            _historyCancellation = null;
            _historySaving = false;
            if (cancellation == null) return;
            cancellation.Cancel();
            if (task == null) { cancellation.Dispose(); return; }
            task.ContinueWith(finished =>
            {
                var ignored = finished.Exception;
                cancellation.Dispose();
            }, TaskScheduler.Default);
        }

        #endregion

        /// <summary>Builds the card explaining what Connect actually does.</summary>
        /// <returns>About card element.</returns>
        private static VisualElement BuildAboutCard()
        {
            VisualElement card = Card("What connecting does");

            card.Add(Note(
                "Writes an MCP server entry named after this project (" + ServerName + ") into that client's " +
                "config, merged rather than overwritten, and installs the Conduit conventions as agent skills " +
                "in the skills folder that client reads inside this project, so the right conventions load " +
                "themselves when the agent starts that kind of work.", false));

            card.Add(Note(
                "Each project registers its own entry and its own skills, so one client can drive several " +
                "projects in parallel. The entry points straight at this editor, so it starts no process of " +
                "its own and a project whose editor is closed refuses the connection rather than leaving one " +
                "running. Edit a conduit document and the card offers Update skills. Restart the client after " +
                "connecting.", true));

            return card;
        }

        /// <summary>
        /// Re-reads connection and skill state from disk and updates the chips and buttons in place. Called
        /// on build and on a schedule, because the answer changes when a document is edited outside Unity.
        /// </summary>
        private void RefreshState()
        {
            foreach (ClientCard card in _cards)
            {
                if (card.ChipOffline == null) continue;

                string configPath = ConfigPath(card.Client);
                bool registered = IsConnected(card.Client, configPath);
                bool current = registered && IsCurrent(card.Client, configPath);
                bool attached = IsAttached(card.Client);
                bool supported = ConduitSkills.Supported(card.Client);
                bool skillsOk = !supported || (ConduitSkills.IsInstalled(card.Client) && !ConduitSkills.IsStale(card.Client));

                // Every chip rests on POSITIVE evidence. Green is a stream held right now, which is the only
                // thing that proves a client is there, and its loss is the only thing that proves one left.
                //
                // "restart to connect" means exactly one thing: Connect has written credentials that the
                // running client has not picked up. It is therefore driven by whether that client has ever
                // PRESENTED its current bearer, which proves it loaded that config and cannot decay, and
                // never by whether a stream is open. Those answer different questions, and a client can be
                // connected and working with no stream at all - which is what a client that attached before
                // streams were held across reloads looks like. Telling that one to restart is a false
                // instruction, the original confident green's mistake pointing the other way.
                bool loadedCurrentConfig = ConduitHttpTransport.HasPresentedCurrentToken(card.Client);

                // The four connection chips COVER every combination, so a card is never blank: a card with
                // nothing on it reports nothing, and "I cannot tell" is a thought the reader cannot have on
                // the reader's behalf. They stay independent rather than becoming a first-match ladder, so
                // two lighting at once shows a contradiction instead of a precedence rule hiding one.
                Light(card.ChipOffline, !registered);
                Light(card.ChipOutOfDate, registered && !current);
                Light(card.ChipRestart, registered && current && !loadedCurrentConfig);
                Light(card.ChipConnected, registered && current && loadedCurrentConfig && attached);
                Light(card.ChipDropped, registered && current && loadedCurrentConfig && !attached);
                Light(card.ChipSkills, registered && !skillsOk);

                // Stays enabled when the MCP entry exists but the skills are missing or stale, so the
                // conventions can be refreshed after editing a document without removing the client.
                // Each fill is carried only while its button does something, so the colour stays worth
                // reading: green while there is work to do here, red while there is an entry to destroy,
                // and the grey disabled rule everywhere else.
                // Enabled whenever pressing it would change something on disk. Gating only on the entry
                // EXISTING is what greys the button out at the exact moment the credentials it wrote have
                // been superseded, which leaves re-Connecting impossible without removing the entry first.
                bool connectActive = !registered || !current || !skillsOk;
                card.Connect.text = registered && current && !skillsOk ? "Update skills" : "Connect";
                card.Connect.SetEnabled(connectActive);
                card.Connect.EnableInClassList("cw-button--ok", connectActive);

                card.Remove.SetEnabled(registered);
                card.Remove.EnableInClassList("cw-button--danger", registered);
            }
        }

        /// <summary>Builds one state chip, created hidden so it occupies nothing until its condition holds.</summary>
        /// <param name="head">Card header row the chip is added to.</param>
        /// <param name="text">The condition, in the words a reader of the window would use.</param>
        /// <param name="stateClass">Chip state class carrying its colour.</param>
        /// <returns>The chip, hidden.</returns>
        private static Label Chip(VisualElement head, string text, string stateClass)
        {
            Label chip = new Label(text);
            chip.AddToClassList("cw-chip");
            chip.AddToClassList(stateClass);
            chip.style.display = DisplayStyle.None;
            head.Add(chip);
            return chip;
        }

        /// <summary>
        /// Shows or hides one chip. Hidden is <c>DisplayStyle.None</c> rather than a transparent colour, so an
        /// unlit chip takes no space in the row and cannot be mistaken for a dimmed state that means something.
        /// </summary>
        /// <param name="chip">The chip to light or hide.</param>
        /// <param name="lit">Whether the chip's condition currently holds.</param>
        private static void Light(VisualElement chip, bool lit)
        {
            if (chip == null) return;

            chip.style.display = lit ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Builds an empty card carrying one heading.</summary>
        /// <param name="heading">Heading text drawn at the top of the card.</param>
        /// <returns>The card element, ready to have content added.</returns>
        private static VisualElement Card(string heading)
        {
            VisualElement card = new VisualElement();
            card.AddToClassList("cw-card");

            Label label = new Label(heading);
            label.AddToClassList("cw-card__heading");
            card.Add(label);

            return card;
        }

        /// <summary>Builds one label / value status row.</summary>
        /// <param name="key">Left-hand label, drawn in the fixed-width column.</param>
        /// <param name="value">Right-hand value.</param>
        /// <returns>The row element.</returns>
        private static VisualElement Row(string key, string value)
        {
            VisualElement row = new VisualElement();
            row.AddToClassList("cw-row");

            Label caption = new Label(key);
            caption.AddToClassList("cw-row__label");
            row.Add(caption);

            Label text = new Label(value);
            text.AddToClassList("cw-row__value");
            row.Add(text);

            return row;
        }

        /// <summary>Builds the horizontal strip a card's buttons sit in.</summary>
        /// <returns>The actions row.</returns>
        private static VisualElement Actions()
        {
            VisualElement actions = new VisualElement();
            actions.AddToClassList("cw-actions");
            return actions;
        }

        /// <summary>Builds a button carrying this window's own styling.</summary>
        /// <param name="text">Button label.</param>
        /// <param name="action">What the press runs.</param>
        /// <returns>The button.</returns>
        private static Button Button(string text, Action action)
        {
            Button button = new Button(action) { text = text };
            button.AddToClassList("cw-button");
            return button;
        }

        /// <summary>Builds a button that keeps its own width rather than sharing the row evenly.</summary>
        /// <param name="text">Button label.</param>
        /// <param name="action">What the press runs.</param>
        /// <returns>The button.</returns>
        private static Button Compact(string text, Action action)
        {
            Button button = Button(text, action);
            button.AddToClassList("cw-button--compact");
            return button;
        }

        /// <summary>Builds a block of explanatory copy.</summary>
        /// <param name="text">The copy.</param>
        /// <param name="tight">Whether to draw it in the quieter, smaller variant.</param>
        /// <returns>The note element.</returns>
        private static Label Note(string text, bool tight)
        {
            Label note = new Label(text);
            note.AddToClassList("cw-note");
            if (tight) note.AddToClassList("cw-note--tight");
            return note;
        }

        /// <summary>
        /// Resolves the brand mark by GUID, so a moved folder cannot quietly drop it, and by name inside
        /// Conduit's own folder as a fallback, so a reassigned GUID degrades rather than breaks. The fallback
        /// searches only Conduit's folder, so another product's logo of the same name is never picked.
        /// </summary>
        /// <returns>The mark, or null when it cannot be found.</returns>
        private static Texture2D LoadLogo()
        {
            if (_logo != null) return _logo;

            string path = AssetDatabase.GUIDToAssetPath(LOGO_GUID);
            if (!string.IsNullOrEmpty(path))
            {
                _logo = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }

            if (_logo != null) return _logo;

            string root = ConduitSkills.ConduitRoot();
            if (root.Length == 0) return null;

            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D " + LOGO_NAME, new[] { root }))
            {
                string candidatePath = AssetDatabase.GUIDToAssetPath(guid);
                if (!candidatePath.EndsWith("/" + LOGO_NAME + ".png", System.StringComparison.Ordinal)) continue;

                _logo = AssetDatabase.LoadAssetAtPath<Texture2D>(candidatePath);
                if (_logo != null) break;
            }

            return _logo;
        }

        /// <summary>Resolves this window's stylesheet, by GUID first and by name as a fallback.</summary>
        /// <returns>The stylesheet, or null when it cannot be found at all.</returns>
        private static StyleSheet LoadStyleSheet()
        {
            if (_styleSheet != null) return _styleSheet;

            string path = AssetDatabase.GUIDToAssetPath(STYLE_GUID);
            if (!string.IsNullOrEmpty(path))
            {
                _styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            }

            if (_styleSheet != null) return _styleSheet;

            foreach (string guid in AssetDatabase.FindAssets("t:StyleSheet " + STYLE_NAME))
            {
                StyleSheet candidate = AssetDatabase.LoadAssetAtPath<StyleSheet>(AssetDatabase.GUIDToAssetPath(guid));
                if (candidate != null)
                {
                    _styleSheet = candidate;
                    break;
                }
            }

            return _styleSheet;
        }

        /// <summary>
        /// The current Unity project's folder name, sanitised into a safe MCP server key: lowercase,
        /// with every run of non-alphanumeric characters collapsed to a single dash. This is the
        /// per-project config key, so an agent sees a distinct toolset named after each open project.
        /// </summary>
        /// <returns>A dash-separated slug, or "unity-project" if the name has no usable characters.</returns>
        private static string ProjectName()
        {
            string root = Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
            string raw = new DirectoryInfo(root).Name;

            var builder = new StringBuilder(raw.Length);
            foreach (char c in raw.ToLowerInvariant())
            {
                builder.Append(char.IsLetterOrDigit(c) ? c : '-');
            }

            string slug = builder.ToString().Trim('-');
            while (slug.Contains("--"))
            {
                slug = slug.Replace("--", "-");
            }

            return string.IsNullOrEmpty(slug) ? "unity-project" : slug;
        }

        /// <summary>
        /// This project's root spelled with forward slashes, which is the key the client actually looks under.
        /// The separator is not cosmetic: a per-project config is a dictionary keyed by an absolute path, so a
        /// different spelling of one path is a different key and a server registered under it is never read.
        /// Measured rather than assumed - a config written under the OS spelling was ignored while the client
        /// went on presenting the credential from the forward-slash entry. Reads tolerate either spelling
        /// through <see cref="ConduitConnectPlan.ServerMap"/>; writes use this one.
        /// </summary>
        /// <returns>The absolute project root.</returns>
        private static string ProjectKey()
        {
            return (Path.GetDirectoryName(Application.dataPath) ?? string.Empty).Replace('\\', '/');
        }

        /// <summary>The current user's home (profile) directory.</summary>
        /// <returns>The absolute path to the user profile folder.</returns>
        private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>Resolves each client's on-disk config file path on this OS.</summary>
        /// <param name="client">The client display name to resolve a config path for.</param>
        /// <returns>The absolute config file path, or null for an unrecognised client.</returns>
        internal static string ConfigPath(string client)
        {
            switch (client)
            {
                // The app's own claude_desktop_config.json is deliberately not this path. That file's schema
                // describes a command to spawn plus its arguments and environment, and carries no endpoint
                // field at all, so an entry naming one is discarded when the app starts and reported there as
                // an invalid server. The sessions the app runs read their servers from this file instead,
                // keyed per project, and it takes the endpoint entry unchanged.
                case "Claude Desktop": return Path.Combine(Home(), ".claude.json");
                case "Cursor": return Path.Combine(Home(), ".cursor", "mcp.json");
                case "Antigravity": return Path.Combine(Home(), ".gemini", "config", "mcp_config.json");
                case "Codex": return Path.Combine(Home(), ".codex", "config.toml");

                // Global, like every other entry here. This client also reads a project-level config, and
                // using that would have made it the one client whose server exists only while its workspace
                // happens to be this project. Scoping is unchanged by the move: the entry is NAMED after the
                // project and points straight at this editor, so a client opened on anything else finds
                // nothing listening rather than the wrong project.
                //
                // Three files are read from that folder - config.json, opencode.json, then opencode.jsonc -
                // and merged in that order, so this one is read. It is preferred over the jsonc because the
                // jsonc is the file the client patches itself and may carry comments a JSON rewrite strips.
                // Merged last, a jsonc that later grew its own mcp block could shadow this entry.
                case "OpenCode": return Path.Combine(Home(), ".config", "opencode", "opencode.json");

                default: return null;
            }
        }

        /// <summary>Whether the client stores its config as TOML (Codex) rather than JSON.</summary>
        /// <param name="client">The client display name to test.</param>
        /// <returns>True for Codex; false for the JSON-based clients.</returns>
        private static bool IsToml(string client) => client == "Codex";

        /// <summary>Official download page for each supported client, opened by the card's Download button.</summary>
        /// <param name="client">The client display name to look up a download URL for.</param>
        /// <returns>The client's download page URL, or null for an unrecognised client.</returns>
        private static string DownloadUrl(string client)
        {
            switch (client)
            {
                case "Claude Desktop": return "https://claude.ai/download";
                case "Cursor": return "https://cursor.com";
                case "Antigravity": return "https://antigravity.google/download";
                case "Codex": return "https://openai.com/codex/";

                case "OpenCode": return "https://opencode.ai/download";

                default: return null;
            }
        }

        /// <summary>True if the client's config already references the Conduit server.</summary>
        /// <param name="client">The client display name (selects TOML vs JSON parsing).</param>
        /// <param name="configPath">Path to the client's config file.</param>
        /// <returns>True if this project's server entry is present; false if absent or unreadable.</returns>
        private static bool IsConnected(string client, string configPath)
        {
            try
            {
                if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return false;
                string text = File.ReadAllText(configPath);
                if (IsToml(client)) return text.Contains("[mcp_servers." + ServerName + "]");
                var root = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
                JObject servers = ConduitConnectPlan.ServerMap(
                    root,
                    ConduitConnectPlan.ProjectKeyFor(client, LiveSnapshot()),
                    ConduitConnectPlan.ServerMapKeyFor(client),
                    false);
                return servers != null && servers[ServerName] != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Whether this client is holding a live stream on the transport right now. Read at the moment it is
        /// asked, so it reports a socket that exists rather than a request that once arrived.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>True while that client holds a stream.</returns>
        private static bool IsAttached(string client)
        {
            foreach (string name in ConduitHttpTransport.AttachedClients())
            {
                if (string.Equals(name, client, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the entry on disk carries what this client would be written TODAY: the live endpoint and
        /// the bearer now issued to it. An entry that merely exists proves nothing - a port rebound or a
        /// bearer reissued leaves a config that parses perfectly and authenticates against nothing.
        /// Matched against the entry's own text so the check does not have to know each client's key names.
        /// </summary>
        /// <param name="client">Client display name (selects TOML vs JSON parsing).</param>
        /// <param name="configPath">Path to the client's config file.</param>
        /// <returns>True when the written entry matches the live transport and this client's bearer.</returns>
        private static bool IsCurrent(string client, string configPath)
        {
            try
            {
                if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath)) return false;

                string bearer = ConduitHttpTransport.TokenForClient(client);
                string url = ConduitConnectPlan.EndpointUrl(ConduitHttpTransport.Port);
                if (string.IsNullOrEmpty(bearer) || string.IsNullOrEmpty(url)) return false;

                string text = File.ReadAllText(configPath);
                if (IsToml(client)) return text.Contains(bearer) && text.Contains(url);

                var root = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
                JObject servers = ConduitConnectPlan.ServerMap(
                    root,
                    ConduitConnectPlan.ProjectKeyFor(client, LiveSnapshot()),
                    ConduitConnectPlan.ServerMapKeyFor(client),
                    false);

                var entry = servers == null ? null : servers[ServerName];
                if (entry == null) return false;

                string written = entry.ToString();
                return written.Contains(bearer) && written.Contains(url);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The copy-paste config snippet for a client (JSON or TOML).</summary>
        /// <param name="client">The client display name (selects TOML vs JSON formatting).</param>
        /// <returns>The server entry as a TOML block (Codex) or an indented JSON object (Claude Desktop / Cursor).</returns>
        private static ConduitConnectEntry PlanEntry(string client)
        {
            foreach (ConduitConnectEntry entry in ConduitConnectPlan.Build(LiveSnapshot()))
            {
                if (entry.Client == client) return entry;
            }

            return null;
        }

        /// <summary>
        /// Removes any server entry that launches the console bridge, whatever key it sits under. A project
        /// that moves to the endpoint must stop launching a process for a transport nothing uses any more, and
        /// the bridge was registered under its own key rather than this project's, so assigning this project's
        /// entry would otherwise leave the old one running alongside it.
        /// </summary>
        /// <param name="servers">The mcpServers object to clean.</param>
        private static void RemoveLegacyBridgeEntries(JObject servers)
        {
            var stale = new List<string>();
            foreach (JProperty property in servers.Properties())
            {
                JObject entry = property.Value as JObject;
                string command = entry == null ? null : (string)entry["command"];
                if (!string.IsNullOrEmpty(command) &&
                    command.IndexOf(BRIDGE_EXECUTABLE_NAME, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    stale.Add(property.Name);
                }
            }

            foreach (string name in stale) servers.Remove(name);
        }

        /// <summary>Returns the text with this project's TOML server block removed, if it has one.</summary>
        /// <param name="text">The whole config file.</param>
        /// <param name="serverName">This project's server key.</param>
        /// <returns>The text without that block.</returns>
        private static string StripTomlBlock(string text, string serverName)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var lines = new List<string>(text.Split('\n'));
            int start = lines.FindIndex(line => line.Trim() == "[mcp_servers." + serverName + "]");
            if (start < 0) return text;

            int end = start + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;
            lines.RemoveRange(start, end - start);
            return string.Join("\n", lines);
        }

        /// <summary>The config entry for a client, as text, for the card's Copy button.</summary>
        /// <param name="client">The client display name.</param>
        /// <returns>The entry, or the stated reason there is none.</returns>
        private static string Snippet(string client)
        {
            ConduitConnectEntry planned = PlanEntry(client);
            if (planned == null) return string.Empty;

            return string.IsNullOrEmpty(planned.Artifact) ? planned.Reason : planned.Artifact;
        }

        /// <summary>Merges the Conduit entry into the client's config, preserving everything else.</summary>
        /// <param name="client">The client display name (selects TOML vs JSON merge).</param>
        /// <param name="configPath">Path to the client's config file; created (with parents) if missing.</param>
        private void Connect(string client, string configPath)
        {
            try
            {
                string directory = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                ConduitConnectEntry planned = PlanEntry(client);

                // Refused rather than written. An entry that could not be built means there is nothing
                // truthful to put in the file, and writing anything at all would report a connection that
                // does not exist.
                if (planned == null || string.IsNullOrEmpty(planned.Artifact))
                {
                    throw new InvalidOperationException(planned == null
                        ? client + " is not in the connection plan."
                        : planned.Reason);
                }

                if (IsToml(client))
                {
                    string existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;

                    // The block is REPLACED rather than skipped when already present. Appending only when
                    // absent would leave a stale command sitting in the file, so a project that moved to the
                    // endpoint would keep launching the server it no longer talks to.
                    string trimmed = StripTomlBlock(existing, ServerName).TrimEnd();
                    File.WriteAllText(configPath, trimmed + (trimmed.Length > 0 ? "\n\n" : string.Empty) + Snippet(client));
                }
                else
                {
                    string projectKey = ConduitConnectPlan.ProjectKeyFor(client, LiveSnapshot());
                    string text = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
                    var root = string.IsNullOrWhiteSpace(text) ? new JObject() : JObject.Parse(text);
                    string mapKey = ConduitConnectPlan.ServerMapKeyFor(client);
                    JObject servers = ConduitConnectPlan.ServerMap(root, projectKey, mapKey, true);

                    RemoveLegacyBridgeEntries(servers);

                    // Taken out of the planned artifact rather than rebuilt here, so the entry written to the
                    // config is byte-for-byte the one the gate checked and the card offers to copy. Rebuilding
                    // it would be a second construction of the same thing, free to drift from the first. It is
                    // read back out through the same navigation that built it, so a client whose file nests its
                    // servers cannot be written correctly and then read as absent.
                    JObject fragment = ConduitConnectPlan.ServerMap(JObject.Parse(planned.Artifact), projectKey, mapKey, false);
                    servers[ServerName] = fragment[ServerName];

                    File.WriteAllText(configPath, root.ToString(Formatting.Indented));
                }
                ShowNotification(new GUIContent(client + " connected"));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Conduit", "Could not update " + client + " config automatically:\n\n" + ex.Message + "\n\nUse Copy and paste the entry in manually instead.", "OK");
                return;
            }

            // The session hook goes in on the same press because Claude exports its exact session return
            // path only to processes it spawned, and a session-start hook is one of them. Kept ahead of the
            // skills guard: a client with no skills folder can still need the hook, and a step after that
            // return would silently never run.
            try
            {
                string hook = ConduitClaudeSessionHook.Install(client);
                if (hook != null) ShowNotification(new GUIContent(hook));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Conduit", "MCP was configured for " + client
                    + ", but its session hook could not be installed:\n\n" + ex.Message, "OK");
            }

            // The other client-specific half of the same job: this harness publishes no address a
            // child adapter can use, so an in-harness plugin connects outward instead. Its own try keeps a
            // plugin failure separate from an MCP configuration that already succeeded.
            try
            {
                string plugin = ConduitOpenCodePlugin.Install(client);
                if (plugin != null) ShowNotification(new GUIContent(plugin));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Conduit", "MCP was configured for " + client + ", but its plugin could not be installed:\n\n" + ex.Message, "OK");
            }

            // The same button installs the skills, deliberately: an agent holding the tools but not the
            // conventions is the exact case these documents exist to prevent. Kept in its own try so a
            // skills failure is never reported as an MCP failure.
            if (!ConduitSkills.Supported(client))
            {
                return;
            }

            try
            {
                ShowNotification(new GUIContent(ConduitSkills.Install(client)));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Conduit", "MCP was configured for " + client + ", but the skills could not be written:\n\n" + ex.Message, "OK");
            }
        }

        /// <summary>Removes the Conduit entry from a client's config, leaving everything else intact.</summary>
        /// <param name="client">The client display name (selects TOML vs JSON merge).</param>
        /// <param name="configPath">Path to the client's config file.</param>
        private void RemoveConduit(string client, string configPath)
        {
            try
            {
                if (!File.Exists(configPath)) return;
                if (IsToml(client))
                {
                    var lines = new List<string>(File.ReadAllLines(configPath));
                    int start = lines.FindIndex(line => line.Trim() == "[mcp_servers." + ServerName + "]");
                    if (start >= 0)
                    {
                        int end = start + 1;
                        while (end < lines.Count && !lines[end].TrimStart().StartsWith("[")) end++;
                        lines.RemoveRange(start, end - start);
                        File.WriteAllText(configPath, string.Join("\n", lines).TrimEnd() + "\n");
                    }
                }
                else
                {
                    var root = JObject.Parse(File.ReadAllText(configPath));
                    JObject servers = ConduitConnectPlan.ServerMap(
                        root,
                        ConduitConnectPlan.ProjectKeyFor(client, LiveSnapshot()),
                        ConduitConnectPlan.ServerMapKeyFor(client),
                        false);
                    if (servers != null && servers[ServerName] != null)
                    {
                        servers.Remove(ServerName);
                        File.WriteAllText(configPath, root.ToString(Formatting.Indented));
                    }
                }

                ShowNotification(new GUIContent(client + " removed"));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Conduit", "Could not update " + client + " config:\n\n" + ex.Message, "OK");
            }

            // Mirror of Connect: the tools and the conventions arrive and leave together, so a removed
            // client cannot be left with stale skills describing a bridge it no longer has.
            try
            {
                if (ConduitSkills.Supported(client))
                {
                    ConduitSkills.Remove(client);
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Conduit", "The MCP entry was removed, but the skills could not be deleted:\n\n" + ex.Message, "OK");
            }

            // The session hook leaves with the direct MCP entry, so a removed project cannot keep
            // registering itself against a bridge it no longer has an entry for.
            try
            {
                ConduitClaudeSessionHook.Remove(client);
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Conduit", "The MCP entry was removed, but its session hook "
                    + "could not be deleted:\n\n" + ex.Message, "OK");
            }

            // Mirror of Connect for the plugin client. Leaving one registered is worse than leaving a stale
            // skill: the harness would keep loading it at every launch and it would keep reaching for a
            // bridge this client no longer has an entry for.
            try
            {
                string plugin = ConduitOpenCodePlugin.Remove(client);
                if (plugin != null) ShowNotification(new GUIContent(plugin));
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Conduit", "The MCP entry was removed, but its plugin could not be deleted:\n\n" + ex.Message, "OK");
            }
        }

        /// <summary>Reveals the config file in the OS file browser, falling back to its parent folder if absent.</summary>
        /// <param name="configPath">The config file path to reveal; ignored when null or empty.</param>
        private static void Reveal(string configPath)
        {
            if (string.IsNullOrEmpty(configPath)) return;

            // Directory.Exists is checked too: the skills buttons pass a FOLDER, and File.Exists is false
            // for one, which would otherwise reveal its parent instead of the folder itself.
            if (File.Exists(configPath) || Directory.Exists(configPath)) EditorUtility.RevealInFinder(configPath);
            else EditorUtility.RevealInFinder(Path.GetDirectoryName(configPath));
        }
    }
}
