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
    /// Command catalogue: the canonical verb list, per-verb notes, project gating, and the help and commands renderers.
    /// </summary>
    internal static partial class ConduitBridge
    {
        /// <summary>
        /// Single source of truth for command signatures rendered by help and command discovery output.
        /// </summary>
        private static readonly string[] CommandSignatures = new[]
        {
            "ping",
            "help",
            "commands",
            "mcp-refresh-tools",
            "tail [count]",
            "head [count]",
            "entry <entries> [entries ...]",
            "grep <text>",
            "project-search <pattern> [| optionsJson]",
            "project-files [| optionsJson]",
            "summary [count]",
            "clear",
            "status",
            "play",
            "stop",
            "toggleplay",
            "pause",
            "resume",
            "togglepause",
            "domain-refresh",
            "refresh",
            "reimport <assetPath>",
            "reimport-customer-dll",
            "create-usharp <assetPath>",
            "create-usharp-batch <folder> | <names>",
            "delete-usharp <assetPath>",
            "project-info",
            "save-assets",
            "save-scenes <scene>",
            "menu <menuPath>",
            "select <assetPath>",
            "screenshot-scene [path] [width] [height]",
            "screenshot-game <scene> [path] [width] [height]",
            "game-view-focus",
            "game-view-click <x> <y>",
            "game-view-key <key> [seconds=0] [phase:down|up|press]",
            "scene-view-state",
            "scene-view-pivot <x> <y> <z>",
            "scene-view-rotation <x> <y> <z>",
            "scene-view-size <size>",
            "scene-view-pan <right> <up>",
            "scene-view-orbit <yaw> <pitch>",
            "scene-view-zoom <factor>",
            "scene-view-move <right> <up> <forward>",
            "scene-view-frame <objectPathOrId>",
            "scene-view-pick <x> <y> [imageWidth] [imageHeight]",
            "scene-view-align <view:front|back|left|right|top|bottom|iso>",
            "scene-tree <scene> [maxDepth=12] [names]",
            "find-object <nameOrPathOrId>",
            "create-empty <scene> | <name> [| parent]",
            "rename-object <target> | <newName>",
            "reparent-object <target> | <parent>",
            "set-sibling-index <target> | <index>",
            "set-position <target> | <x> <y> <z>",
            "set-rotation <target> | <x> <y> <z>",
            "set-scale <target> | <x> <y> <z>",
            "vrc-menu <action:status|close|open|accept-warning>",
            "vrc-player-state",
            "vrc-input <action:jump|run|use|grab|drop|crouch|prone|menu|release-mouse> [phase:down|up|press]",
            "vrc-stance <stance:stand|crouch|prone>",
            "vrc-drive <forward> <strafe> <turnDegPerSec> [seconds] [speed]",
            "vrc-drive-stop",
            "vrc-teleport <x> <y> <z> [yawDegrees]",
            "vrc-look <yawDegrees> <pitchDegrees>",
            "vrc-look-delta <deltaYawDegrees> <deltaPitchDegrees>",
            "vrc-look-at <targetOrPoint>",
            "vrc-validate",
            "vrc-world-info",
            "vrc-world-set <| worldJson>",
            "vrc-build <sessionId>",
            "vrc-build-test <sessionId>",
            "vrc-test-last <sessionId>",
            "vrc-build-state",
            "agent-session-list",
            "agent-notify <sessionId> | <text>",
#if CONDUIT_PROBUILDER
            "probuilder-shape-create <scene> | <shape:cube|plane|cylinder|prism|stair|curved-stair|door|pipe|cone|arch|sphere|torus|sprite> [| optionsJson]",
            "probuilder-poly-create <scene> | <name> | <points> [| optionsJson]",
            "probuilder-bezier-create <scene> | <name> | <points> [| optionsJson]",
            "probuilder-ize <target> [| optionsJson]",
            "probuilder-mesh-get <target> [| section:summary|faces|selection|colors]",
            "probuilder-select <target> | <faces>",
            "probuilder-face-extrude <target> | <distance> [| faces] [| method:vertex-normal|face-normal|individual]",
            "probuilder-face-delete <target> [| faces]",
            "probuilder-subdivide <target> [| faces]",
            "probuilder-uv-set <target> | <settingsJson> [| faces]",
            "probuilder-uv-project <target> | <projection:box|sphere|planar> [| faces] [| channel:uv0|uv2]",
            "probuilder-uv-split <target> [| vertices]",
            "probuilder-uv-collapse <target> [| vertices]",
            "probuilder-uv-sew <target> | <distance> [| vertices]",
            "probuilder-uv-stitch <target> | <faceA> | <faceB> [| channel:uv0|uv2]",
            "probuilder-uv-flip <target> | <axis:u|v> [| vertices] [| channel:uv0|uv2]",
            "probuilder-uv-mode <target> | <mode:auto|manual> [| faces]",
            "probuilder-uv-fit <target> [| vertices] [| channel:uv0|uv2]",
            "probuilder-uv-transform <target> | <settingsJson> [| vertices] [| channel:uv0|uv2]",
            "probuilder-face-material <target> | <materialPath> [| faces]",
            "probuilder-smoothing-set <target> | <group> [| faces]",
            "probuilder-vertex-color-set <target> | <color> [| elements]",
            "probuilder-grow-selection <target> [| angle] [| faces]",
            "probuilder-select-loop <target> | <kind:loop|ring> [| faces]",
            "probuilder-select-edge-loop <target> [| edges] [| reach:whole|iterative]",
            "probuilder-select-edge-ring <target> [| edges] [| reach:whole|iterative]",
            "probuilder-select-flood <target> [| angle] [| faces]",
            "probuilder-bevel-edges <target> | <amount> [| edges]",
            "probuilder-insert-edge-loop <target> [| edges]",
            "probuilder-fill-hole <target> [| vertices]",
            "probuilder-select-hole <target> [| vertices]",
            "probuilder-shrink-selection <target> [| faces]",
            "probuilder-select-material <target> | <materialPath>",
            "probuilder-select-smoothing-group <target> [| faces]",
            "probuilder-select-vertex-color <target> [| faces]",
            "probuilder-offset-elements <target> | <offset> [| elements]",
            "probuilder-cut <target> | <points> [| face]",
            "probuilder-insert-vertex-in-face <target> | <face> | <point>",
            "probuilder-insert-vertex-on-edge <target> | <edge> | <point>",
            "probuilder-set-collider <target>",
            "probuilder-set-trigger <target>",
            "probuilder-face-merge <target> [| faces]",
            "probuilder-face-detach <target> [| to:gameobject|submesh] [| faces]",
            "probuilder-face-duplicate <target> [| to:gameobject|submesh] [| faces]",
            "probuilder-face-duplicate-flip <target> [| faces]",
            "probuilder-flip-normals <target> [| faces]",
            "probuilder-conform-normals <target> [| faces]",
            "probuilder-recalculate-normals <target>",
            "probuilder-to-quads <target> [| faces]",
            "probuilder-triangulate <target> [| faces]",
            "probuilder-vert-weld <target> | <radius> [| vertices]",
            "probuilder-vert-split <target> [| vertices]",
            "probuilder-vert-translate <target> | <offset> [| vertices]",
            "probuilder-vert-set-position <target> | <position> [| vertices] [| space:local|world]",
            "probuilder-xray [state:on|off]",
            "probuilder-edge-bridge <target> | <edgeA> | <edgeB>",
            "probuilder-edge-connect <target> [| faces]",
            "probuilder-extrude-edges <target> | <distance> [| edges] [| optionsJson]",
            "probuilder-subdivide-edges <target> [| subdivisions] [| edges]",
            "probuilder-collapse-vertices <target> [| to:center|first] [| vertices]",
            "probuilder-connect-vertices <target> [| vertices]",
            "probuilder-flip-face-edge <target> [| faces]",
            "probuilder-set-pivot <target> | <where>",
            "probuilder-freeze-transform <target>",
            "probuilder-fit-to-size <target> | <size>",
            "probuilder-lightmap-uvs <target> [| mode:lightmap|nolightmap]",
            "probuilder-lightmap-static <target> [| state:on|off]",
            "probuilder-colliders-rebuild <target>",
            "probuilder-cleanup <target>",
            "probuilder-validate <target>",
            "probuilder-merge-objects <target> | <others>",
            "probuilder-mirror-objects <target> | <axes> [| mode:duplicate|inplace]",
            "probuilder-export <target> [| kind:mesh|prefab|obj|ply|stl-ascii|stl-binary] [| path]",
            "probuilder-strip <target>",
#endif
            "animation-clip-create <assetPath> [| settingsJson]",
            "animation-clip-get <clipPath>",
            "animation-clip-set <clipPath> | <settingsJson>",
            "animation-clip-set-curve <clipPath> | <targetPath> | <componentType> | <propertyName> | <keysJson|null>",
            "animator-create <assetPath>",
            "animator-get <controllerPath>",
            "animator-parameter-set <controllerPath> | <name> | <parameterType:float|int|bool|trigger> [| defaultValue]",
            "animator-parameter-remove <controllerPath> | <name>",
            "animator-layer-set <controllerPath> | <layerNameOrIndex> [| optionsJson]",
            "animator-layer-remove <controllerPath> | <layerNameOrIndex>",
            "animator-mask-create <assetPath> [| configJson]",
            "animator-mask-set <maskPath> | <configJson>",
            "animator-state-set <controllerPath> | <layerNameOrIndex> | <statePath> [| optionsJson]",
            "animator-state-remove <controllerPath> | <layerNameOrIndex> | <statePath>",
            "animator-statemachine-set <controllerPath> | <layerNameOrIndex> | <smPath> [| optionsJson]",
            "animator-statemachine-remove <controllerPath> | <layerNameOrIndex> | <smPath>",
            "animator-transition-add <controllerPath> | <layerNameOrIndex> | <from> | <to> [| optionsJson]",
            "animator-transition-set <controllerPath> | <layerNameOrIndex> | <from> | <to> | <optionsJson>",
            "animator-transition-remove <controllerPath> | <layerNameOrIndex> | <from> | <to>",
            "animator-blendtree-set <controllerPath> | <layerNameOrIndex> | <statePath> | <treeJson>",
            "assets-copy <sourcePath> | <destinationPath>",
            "assets-create-folder <parentFolder> | <name>",
            "assets-delete <assetPath>",
            "assets-find [filter] [| folder]",
            "assets-find-built-in [filter]",
            "assets-find-references <scene> <assetPath>",
            "assets-get-data <assetPath>",
            "assets-importer-get <assetPath>",
            "assets-importer-modify <assetPath> | <json>",
            "assets-material-create <assetPath> | <shaderName>",
            "assets-material-set-shader <materialPath> | <shaderNameOrPath> [| keywords]",
            "assets-material-set-texture <materialPath> | <propertyName> | <texturePath>",
            "assets-modify <assetPath> | <json>",
            "assets-move <sourcePath> | <destinationPath>",
            "assets-orphans <folder> [| scanRoot]",
            "assets-prefab-close [save]",
            "assets-prefab-create <target> | <assetPath>",
            "assets-prefab-instantiate <scene> | <prefabPath> [| parent] [| position]",
            "assets-prefab-open <targetOrPath>",
            "assets-prefab-save",
            "assets-prefab-unpack <target> [| mode:outermost|completely]",
            "assets-refresh",
            "assets-replace-references <scene> | <oldAsset> | <newAsset>",
            "assets-shader-get-data <shaderNameOrPath>",
            "assets-shader-list-all [filter]",
            "gameobject-component-add <target> | <typeName>",
            "gameobject-component-destroy <target> | <component>",
            "gameobject-component-get <target> | <component>",
            "gameobject-component-list-all [filter]",
            "gameobject-component-list <target>",
            "gameobject-remove-missing-scripts <target>",
            "gameobject-component-modify <target> | <component> | <json>",
            "set-reference <target> | <component> | <field> | <reference>",
            "unity-event-get <target> | <component> | <eventField>",
            "unity-event-set-string <target> | <component> | <eventField> | <mode:replace|append|index> | <listenerTarget> | <listenerComponent|null> | <methodName> | <stringArg> [| callState:runtimeOnly|editorAndRuntime|off]",
            "gameobject-create <scene> | <name> [| primitive] [| parent]",
            "gameobject-destroy <target>",
            "gameobject-duplicate <target>",
            "gameobject-duplicate-multi <target> | <amount>",
            "gameobject-find <target>",
            "find-by-component <scene> <componentType>",
            "gameobject-modify <target> | <property:name|tag|layer|active|position|rotation|scale> | <value>",
            "gameobject-set-parent <target> | <parent>",
            "gameobject-tree <target> [| maxDepth] [| names]",
            "object-get-data <target>",
            "object-modify <target> | <json>",
            "scene-create <assetPath>",
            "scene-get-data [scenePath] [maxDepth]",
            "scene-list-opened",
            "scene-open <assetPath> [mode:single|additive|unloaded]",
            "scene-save [scenePath]",
            "scene-set-active <sceneNameOrPathOrIndex>",
            "scene-unload <sceneNameOrPathOrIndex> [mode:keep|remove] [| changes:save|discard]",
            "script-update-or-create <assetPath> | <content>",
            "script-read <assetPath> [startLine] [count]",
            "file-history <assetPath> [| revision] [| view:content|diff]",
            "file-restore <assetPath> | <revision> | <expectedCurrentSha256>",
            "script-delete <assetPath>",
            "udon-batch-edit <| edits> [| report:full|summary]",
            "console-get-logs [count]",
            "console-clear-logs",
            "editor-application-get-state",
            "editor-application-set-state <state:play|stop|pause|resume|unpause>",
            "editor-selection-get",
            "editor-selection-set <target>",
            "editor-tag-list",
            "editor-tag-add <tag>",
            "editor-tag-remove <tag> [| confirm]",
            "editor-tag-rename <from> | <to>",
            "editor-undo [| confirm]",
            "editor-redo [| confirm]",
            "editor-layer-list",
            "editor-layer-set <indexOrName> | <name> [| confirm]",
            "editor-layer-remove <indexOrName> [| confirm]",
            "reflection-method-find <typeName> [memberName]",
            "type-get-json-schema <typeName>",
            "type-find <nameFragment> [| section:members|constants|enum|all]",
            "udon-program <targetOrAssetPath> [| section:summary|exports|methods|externs|symbols|raw]",
            "udon-program-audit <scene> [typeNameFilter]",
            "udon-exposure <nameFragment> [| mode:types|dump <path>|diff <path>]",
            "animation-clip-sample <target> | <clipPath> | <timeSeconds>",
            "animation-clip-sample-stop",
            "package-add <packageNameOrUrl>",
            "package-list [offline]",
            "package-remove <packageName>",
            "package-search <query>",
            "screenshot-camera <scene> <path> [width=1280] [height=720] [cameraName]",
            "screenshot-isolated <target> | <path> [width] [height]",
            "screenshot-window <window> [| path]",
            "editor-window-list [filter]",
            "editor-window-click <window> | <label>",
            "editor-window-controls <window> [| filter]",
            "editor-window-overlays <window> [| filter]",
            "editor-window-overlay-set <window> | <overlayIdOrName> | <aspect:displayed|collapsed|floating> <value:true|false>",
            "scene-tool-state",
            "editor-window-key <window> | <key> [modifiers:shift|ctrl|alt|cmd ...]",
            "editor-window-close <window>",
            "profiler-start",
            "profiler-stop",
            "profiler-get-status",
            "profiler-capture-frame",
            "profiler-get-memory-stats",
            "profiler-get-rendering-stats",
            "profiler-get-script-stats",
            "profiler-list-modules",
            "profiler-enable-module <moduleName> | <enabled>",
            "profiler-clear-data",
            "profiler-save-data <path>",
            "profiler-load-data <path>",
            "profiler-record-start [frames]",
            "profiler-record-stop",
            "profiler-frame-hierarchy [topN=15] [threadIndex=0] [frame]",
            "profiler-frames [count=30] [from=-] [rows=all] [| counters] [| threads]",
            "profiler-compare <rangesA> <rangesB> [blockFrames=10] [| what:figures|markers|all] [| counters] [| threads]",
            "profiler-mark <label>",
            "profiler-marks [action=list]",
            "profiler-threads [frame=-] [| filter]",
            "profiler-counters [frame=-] [| filter]",
            "profiler-markers <range> [topN=30] [sort=self] [| thread] [| filter] [| scope:game|editor]",
            "profiler-marker-timeline <range> | <marker> [| thread] [| rows:worst|all|none]",
            "profiler-settings [| settingsJson]",
            "profiler-isolate <target> [| what:object|renderers|component] [| componentType] [| cycles] [| phaseFrames] [| gpu:off|on]",
            "profiler-wait-settled [maxSeconds=30]",
            "profiler-memory-objects [topN=20] [| types]",
            "frame-debugger-capture [details=0] [| filter]",
            "editor-focus <action:front|restore>",
            "profiler-select-frame <frame>",
            "unity-tool-list [filter]"
        };

        /// <summary>
        /// Leading verbs that only function in a VRChat Worlds project, hidden from the command list and
        /// refused wherever the Worlds SDK (<c>Packages/com.vrchat.worlds</c>) is absent. The three groups
        /// are UdonSharp authoring, because Udon is a worlds-only concept and the gate prevents stub
        /// overwrites; compiled-Udon inspection, because the programs and the node registry it reads ship
        /// only in the Worlds SDK; and the <c>vrc-*</c> verbs, because they drive ClientSim and the world
        /// build from the Worlds SDK. <c>udon-batch-edit</c> is not gated: it is the versioned source edit
        /// for every script, UdonSharp or not.
        /// Generic editor lifecycle controls such as play, stop, pause, resume, and their toggle forms are
        /// not gated because they work in every Unity project. If avatar testing lands through
        /// GestureManager, broaden the <c>vrc-*</c> gate to Worlds or GestureManager.
        /// </summary>
        private static readonly HashSet<string> WorldsOnlyVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "create-usharp", "create-udonsharp", "usharp-create",
            "create-usharp-batch", "create-udonsharp-batch", "usharp-create-batch",
            "delete-usharp", "delete-udonsharp", "usharp-delete",
            "udon-program", "udon-program-audit", "udon-exposure",
            "vrc-menu", "vrc-player-state", "vrc-input", "vrc-stance",
            "vrc-drive", "vrc-drive-stop", "vrc-teleport",
            "vrc-look", "vrc-look-delta", "vrc-look-at",
            "vrc-validate", "vrc-world-info", "vrc-world-set",
            "vrc-build", "vrc-build-test", "vrc-test-last", "vrc-build-state",
        };

        /// <summary>Absolute path to the Unity project root (the folder that contains <c>Assets/</c>).</summary>
        /// <returns>Unity project root path, or an empty string when unavailable.</returns>
        private static string ProjectRootPath()
        {
            return Path.GetDirectoryName(Application.dataPath) ?? string.Empty;
        }

        /// <summary>True when an embedded VPM package folder of the given id exists under <c>Packages/</c>.</summary>
        /// <param name="packageId">Package id folder name to look for under Packages.</param>
        /// <returns>True when the embedded package folder exists.</returns>
        private static bool HasEmbeddedPackage(string packageId)
        {
            return Directory.Exists(Path.Combine(ProjectRootPath(), "Packages", packageId));
        }

        /// <summary>True for any VRChat SDK project (Worlds or Avatars) - keyed off <c>com.vrchat.base</c>.</summary>
        internal static bool IsVrchatProject => HasEmbeddedPackage("com.vrchat.base");

        /// <summary>True only for VRChat Worlds projects (<c>com.vrchat.worlds</c> present) - Udon/ClientSim capable.</summary>
        internal static bool IsWorldsProject => HasEmbeddedPackage("com.vrchat.worlds");

        /// <summary>Human-readable label for the detected project kind, surfaced in status / project-info / the window.</summary>
        /// <returns>Detected project kind label.</returns>
        internal static string ProjectKindLabel()
        {
            if (!IsVrchatProject)
            {
                return "Unity (non-VRChat)";
            }

            return IsWorldsProject ? "VRChat Worlds (Udon/UdonSharp + ClientSim)" : "VRChat Avatars";
        }

        /// <summary>
        /// The command signatures available in THIS project: the full set in a Worlds project, otherwise
        /// the set minus the Worlds-only verbs, so the generated MCP tool list matches what the project
        /// can actually do.
        /// </summary>
        /// <returns>Project-appropriate command signature list.</returns>
        private static List<string> EffectiveCommandSignatures()
        {
            if (IsWorldsProject)
            {
                return new List<string>(CommandSignatures);
            }

            var available = new List<string>(CommandSignatures.Length);
            foreach (string signature in CommandSignatures)
            {
                int space = signature.IndexOf(' ');
                string verb = space < 0 ? signature : signature.Substring(0, space);
                if (!WorldsOnlyVerbs.Contains(verb))
                {
                    available.Add(signature);
                }
            }

            return available;
        }

        /// <summary>Number of commands exposed in this project, after Worlds-only filtering.</summary>
        internal static int CommandCount => EffectiveCommandSignatures().Count;

        /// <summary>
        /// Bumps the tools-epoch sentinel a stdio server watches. Called by
        /// <see cref="RefreshToolsOnScriptReload"/> after every script reload, and it announces nothing
        /// itself: the HTTP transport compares the catalogue revision it publishes at domain load against
        /// the one the previous domain left in the descriptor, and pushes notifications/tools/list_changed to
        /// every attached stream only when they differ, so a reload that touched no signature tells a client
        /// nothing. The mcp-refresh-tools verb is the on-demand form, through
        /// <see cref="AnnounceToolsOnDemand"/>.
        /// </summary>
        /// <returns>A confirmation line, or an error if the sentinel could not be written.</returns>
        internal static string BumpToolsEpoch()
        {
            try
            {
                Directory.CreateDirectory("Temp");
                File.WriteAllText(ToolsEpochPath, Guid.NewGuid().ToString("N"));
                // The count a client receives is the published catalogue's, not this verb count: conduit-run
                // and job_status are tools without being bridge verbs, so naming the verb count "tools" here
                // disagreed with the descriptor, with health, and with the list the client is about to load.
                return "Tools epoch bumped ("
                    + (ConduitMcpCatalog.IsPublished
                        ? ConduitMcpCatalog.ToolCount + " tools, " + CommandCount + " verbs"
                        : CommandCount + " verbs, catalogue not published yet")
                    + ").";
            }
            catch (Exception ex)
            {
                return "Failed to refresh MCP tools: " + ex.Message;
            }
        }

        /// <summary>
        /// The mcp-refresh-tools verb. A caller asked for a refresh, so every attached stream is told the tool
        /// list changed whether or not the revision moved, and the reply says how many took it. Zero means
        /// nobody was attached to tell, which is a fact about the clients rather than a failure here.
        /// </summary>
        /// <returns>How many streams were told, then the sentinel result.</returns>
        internal static string AnnounceToolsOnDemand()
        {
            var bumped = BumpToolsEpoch();
            var told = ConduitHttpTransport.AnnounceToolListChanged();
            return "Told " + told + " attached stream(s) the tool list changed. " + bumped;
        }

        /// <summary>
        /// Usage guidance for verbs that need the agent to pick the right tool up front. Surfaced both in
        /// <c>help</c> and (machine-readable) in the <c>commands</c> JSON, where the MCP server folds it
        /// into the generated tool description. Project-conditional so it never references a tool the
        /// current project doesn't expose. Returns null when a verb needs no note.
        /// </summary>
        /// <param name="verb">The command's leading verb.</param>
        /// <returns>A one-line note, or null.</returns>
        private static string CommandNote(string verb)
        {
            switch (verb)
            {
                // Discovery entry points. Their notes deliberately name the tools that rank poorly on
                // keyword search (create-usharp, gameobject-tree, save-assets, scene-save) so a fresh
                // agent that searches for any of those still surfaces *this* directory tool, calls it,
                // and sees the full command list.
                case "commands":
                    return "Discovery: returns EVERY Unity editor + VRChat bridge tool as JSON (verb, args, note). In a new MCP session, call unity-tool-list with NO filter first to get the authoritative command map, then search the exact verb name if the typed wrapper is not exposed yet. Includes create-usharp, gameobject-tree, save-assets, scene-save, and the scene/asset/prefab/component/screenshot/profiler tools.";

                case "unity-tool-list":
                    return "Discovery: list all Unity editor + VRChat bridge tools. Call this with NO filter at the start of a new MCP session to get the full authoritative command map before using tool search. Optional filters narrow the list later and match any whitespace/comma-separated term. Use this to find exact verbs like create-usharp, gameobject-tree, save-assets, and scene-save when keyword search buries them.";

                case "project-search":
                    return "Dependency-free project text search that never runs filesystem work on Unity's main thread. paths[] accepts files, directories or both; defaults are Assets, Packages and ProjectSettings. The fast default is a case-sensitive UTF-8 literal search over text-like extensions, respecting hierarchical .gitignore, .ignore and .rgignore files. Options: extensions[] ([\"*\"] means all), ordered globs[] with ! exclusions, regex, caseSensitive, smartCase, word, wholeLine, multiline, dotAll, output (matches/files/count), context, beforeContext, afterContext, encoding (auto/UTF-8/16/32/ASCII/Latin-1), text for binary-as-text, includeMeta, includeHidden, respectIgnore, maxDepth, threads (0 is portable auto; explicit 1-64), maxResults, maxFileBytes and timeoutMs. Paths cannot escape the project; generated folders and reparse points are refused; every wait, file and response is bounded; JSON reports the actual workers, timing, truncation, timeout, scanned totals, matching files, optional total count, results and up to eight read errors.";

                case "project-files":
                    return "List project files without reading their contents, equivalent to the file-enumeration side of ripgrep. Uses the same paths[] file/directory scope, default Assets + Packages + ProjectSettings roots, extensions[], ordered globs[], hierarchical ignore files, includeMeta, includeHidden, respectIgnore, maxDepth, threads, maxResults and timeoutMs as project-search. Returns sorted project-relative paths with measured traversal totals; path confinement, generated-folder refusal, reparse-point refusal and bounds are identical.";

                case "vrc-build":
                    return "Build the open scene into a world bundle. TAKES YOUR OWN SESSION ID, because that is where every stage is reported and nothing on the request reveals it: a bearer names a harness, not a conversation, and several sessions of one harness share it. agent-session-list shows the ids. THIS CALL IS NOT THE RESULT: a world build runs for minutes to over an hour and crosses domain reloads, so it answers immediately with what it started and every stage is reported afterwards to the session that asked, carrying the SDK's OWN message rather than prose invented here. A stage notice says plainly that it is progress and not an outcome; the closing notice says PASSED with what was produced, or FAILED with the SDK's reason, the last stage reached, and that no bundle exists. Refuses to start when a build is already running rather than reporting one build's stages against another's job, and REFUSES BEFORE STARTING when the named session cannot be reported to, naming what is missing - an hour-long build with nowhere to report is worse than one that never started, and the caller can fix it in one call. Uploads nothing.";

                case "vrc-build-test":
                    return "Build the open scene and launch or reload a test client onto it, which is the only way to exercise anything involving a second player - see conduit-testing.md for why play mode cannot. Takes your own session id and reports its stages exactly as vrc-build does, and uploads nothing. Number of clients and the reuse of an already-running client are the panel's own settings; this drives the SDK's BuildAndTest entry point rather than reimplementing them.";

                case "vrc-test-last":
                    return "Reopen the LAST BUILD already on disk in a test client without rebuilding anything, which is the fast loop for re-testing a world you have not changed. Takes your own session id and reports its stages the same way as vrc-build. Refuses through the SDK when there is no last build, and uploads nothing.";

                case "vrc-build-state":
                    return "Read what the SDK reports right now: its build state, its upload state, and - when Conduit is reporting a build - which operation is running, who asked for it, when it started, and the last stage it reached. For checking in rather than waiting, since a build's stages arrive on their own. Says plainly when Conduit is reporting nothing, including the case where a build was started from the panel by hand: those raise the same SDK events, but Conduit only reports a build it was asked for, because only then does it know which session to tell.";

                case "agent-session-list":
                    return "List every agent session running on this machine with its process, status and working directory, and say for each whether Conduit could report to it RIGHT NOW - with the reason when it could not, so 'no credential registered' never looks like 'that session has gone'. Use it to find the session id that the build verbs take. It also names the client the current call authenticated as, and states why that is not a substitute: several sessions of one harness share one client, so a client cannot address a conversation.";

                case "agent-notify":
                    return "Send one notice to a NAMED session, which is how the reporting path gets exercised without waiting on a long build. Takes the session id because nothing on an incoming request identifies a conversation - the bearer names a harness and the client sends no session of any kind - so the caller names its own, and Conduit verifies it is actually reachable rather than trusting it. Two routes reach a session and which one is used depends on the harness, not on the caller: one writes to an address the session registered, the other writes down a stream the session's own in-process plugin is holding. NOT every harness has either yet, so a refused send can mean the route does not exist for that harness rather than that anything is misconfigured - agent-session-list says which. Reports ACCEPTED, REFUSED or UNCONFIRMED rather than claiming delivery: accepted means that session's receiver took the notice, which is transport acceptance and NOT proof the agent saw it - what happens next is the harness's own, and it may queue it, hold it for approval, decline it, expire it or drop it, none of which is visible from this end. The text is wrapped as out-of-band tool output that grants no authority, because it arrives in the conversation where a typed message would and an unwrapped report reads exactly like an instruction from the owner.";

                case "vrc-world-info":
                    return "Read the world fields an upload actually sends: name, description, capacity, recommended capacity, thumbnail path, selected platforms, and the tag list SPLIT BY KIND - content warnings, the author's own tags, and anything else - because the SDK keeps all three in one flat Tags list where a warning is indistinguishable from a tag by shape alone. Says plainly that these live in editor session state, so they survive a domain reload and are lost on an editor restart, which is the SDK's design and not a Conduit limit. Read this before vrc-world-set so a write is made against what is really there.";

                case "vrc-world-set":
                    return "Write the world fields an upload sends, from flat JSON: name, description, capacity, recommendedCapacity, thumbnailPath, tags[], contentWarnings[], debugAllowed. Content warnings take either the tag (content_sex, content_adult, content_violence, content_gore, content_horror) or the label the panel shows, and author tags are given plainly and prefixed for you. Setting one KIND of tag preserves the others, because all of them share a single list and rewriting it wholesale would silently drop the ones you did not mention. Every value is resolved and validated BEFORE anything is stored - an unknown field, a capacity below 1, a thumbnail path that is not on disk, or an unrecognised warning refuses the whole payload and changes nothing - and the report is per-field old -> new read back from the store. Content warnings are a moderation obligation rather than a preference: see conduit-build-validation.md. This does NOT repaint an already-open Build panel, which keeps its own copy until rebuilt, and it does not upload anything.";

                case "vrc-validate":
                    return "Answers 'will this scene upload' by running the VRChat SDK's own scene validations "
                        + "and returning every alert it collected, so a build question needs no human reading the "
                        + "Build panel aloud. The opening line is the verdict: only errors block a build, while "
                        + "warnings, stats and infos are advisory, and 'Everything looks good' is itself an info "
                        + "the SDK emits rather than a fault. Each alert names the object it is filed against with "
                        + "its instance id. READ ONLY: where an alert offers an Auto Fix this says so and never "
                        + "presses it, because a fix is an arbitrary callback written by whoever authored that "
                        + "check - the shipped ones variously rewrite project-wide layer and collision settings, "
                        + "replace a material in place, and reserialize texture importers - so applying one is the "
                        + "owner's decision and is made in the panel. Surfaces the Control Panel when it is closed, "
                        + "which the SDK requires before it will hand over a builder; if the SDK's remote config is "
                        + "still initialising the panel opens and this reports that, so call it once more.";

                case "create-usharp":
                    return "Use this to author a NEW UdonSharp behaviour: it writes the .cs and queues its UdonSharpProgramAsset link for completion after Unity compiles the new MonoScript. BLOCKS until that compile and domain reload settle, then returns the post-compile Console report - when it returns, the behaviour is linked and you can add the component, so do NOT poll status afterwards. Required for U# behaviours - do NOT create them with script-update-or-create, or Udon will overwrite the file with a stub. Accepts Assets/ and project-local embedded packages; registry, cache and external local packages are refused.";

                case "script-update-or-create":
                    if (IsWorldsProject)
                    {
                        return "New and overwritten scripts get completed versions in .conduit/history, alongside pre-edit versions of existing files. file-history reads these versions and file-restore can move backwards or forwards. Capture failures warn without blocking the edit. Byte-identical requests do not write or compile. Create or overwrite a C# script (.cs) and import it - works for BOTH plain C# and UdonSharp source, and writes multi-line content verbatim. The one exception is a NEW UdonSharpBehaviour: make it with create-usharp first (so its UdonSharpProgramAsset is linked), then set its body here. Editing any existing script is always fine. Only a NEW plain-C# runtime MonoBehaviour is flagged, because it is stripped from VRChat world uploads (only Udon runs at runtime). BLOCKS until the resulting compile and domain reload settle, then returns what it wrote plus the post-compile Console report - that report IS the verdict, so nothing follows this call: do NOT reimport (a second request compiles the same change twice) and do NOT read the console for it.";
                    }

                    return IsVrchatProject
                        ? "New and overwritten scripts get completed versions in .conduit/history, alongside pre-edit versions of existing files. file-history reads these versions and file-restore can move backwards or forwards. Capture failures warn without blocking the edit. Byte-identical requests do not write or compile. Create or overwrite a C# script (.cs) and import it; multi-line content is written verbatim. A NEW plain-C# runtime MonoBehaviour is stripped from VRChat world uploads (only Udon runs at runtime), so it won't ship - use create-usharp for runtime logic. The import this performs IS the compile trigger, so NEVER follow this verb with reimport: the second request compiles the same change twice and arrives while the first compile is still running, which is what makes it time out."
                        : "Create or overwrite a C# source file and block through import and compilation. New and overwritten scripts get completed versions in .conduit/history alongside existing files' pre-edit versions; use file-history and file-restore to move backwards or forwards. History failures warn without blocking the edit; byte-identical requests do not write or compile.";

                case "create-usharp-batch":
                    return "MANDATORY for creating SEVERAL new UdonSharp behaviours - never a create-usharp call per file: N behaviours from one folder plus one comma-separated name list, in ONE atomic call with ONE compile, instead of a create-usharp call and a compile each. Example: create-usharp-batch Assets/MyPack/Runtime | OvenDoor, OvenTimer, OvenLamp. A name may instead be a full Assets/... or Packages/<embedded-id>/... path, which overrides the folder. RULES, all enforced: every name is resolved and validated BEFORE anything is written, and a name that already exists, resolves outside a writable project path, is not a valid C# class name, or collides with another name in the batch refuses the WHOLE batch with nothing created; a mid-batch failure rolls back every file already created. Use create-usharp for ONE behaviour or to relink an existing file. The folder and any full path must be under Assets/ or a project-local embedded package; registry, cache and external local packages are refused. BLOCKS until the single compile and domain reload settle, then returns per-behaviour evidence plus the post-compile report - do NOT poll status afterwards. UdonSharp's auto-compile watcher is suppressed during the batch and the user's setting is restored automatically.";

                case "assets-importer-get":
                    return "Read an asset's IMPORTER settings - textureType, spriteMode, compression, maxTextureSize, mip generation - which live on a DIFFERENT object from the asset itself and are unreachable through assets-modify or assets-get-data. Works for any imported asset (TextureImporter, ModelImporter, AudioImporter, font). The setting names it prints are exactly the names assets-importer-modify accepts, so call this FIRST instead of guessing a name or hand-editing the .meta.";

                case "assets-importer-modify":
                    return "Write an asset's IMPORTER settings and reimport it - for example turning a PNG into a usable UI sprite: assets-importer-modify Assets/Icons/Foo.png | {\"m_TextureType\": 8, \"m_SpriteMode\": 1, \"m_AlphaIsTransparency\": 1}. Names are the SERIALIZED ones (m_TextureType), and the .meta YAML spelling (textureType) is accepted as an alias for the same setting. A PNG imports as m_TextureType 0 (Default), which a uGUI Image CANNOT use, and assets-modify writes the imported Texture2D rather than the importer, so this is the only route that is not hand-editing YAML in the .meta. Every setting is resolved and parsed BEFORE anything is written; an unknown name refuses the WHOLE payload with nothing applied and lists the available names. Returns per-setting before/after evidence, then saves and reimports the asset. Call assets-importer-get first to see the real names and current values.";

                case "udon-batch-edit":
                    return "STOP AND READ THIS FIRST. SEND THE EDITS ARRAY ITSELF, EACH ITEM EXACTLY THE FIELDS OF AN EDIT CALL: file_path, old_string, new_string, replace_all. IF THE CALL IS REJECTED AS MALFORMED, YOUR ESCAPING IS WRONG: FIX IT AND RESEND THE SAME BATCH THROUGH THIS TOOL. NEVER WORK AROUND THIS TOOL - NO SCRIPT THAT BUILDS THE BATCH, NO STRING HOLDING THE ARRAY, NO SPLITTING IT INTO SEVERAL CALLS, NO SHELL OR PYTHON EDIT OF THE FILE. AN AGENT DID EXACTLY THAT AND THE OWNER HAD TO CLEAN UP AFTER IT. THE .json FILE OPTION IS FOR A BATCH LARGER THAN YOUR HARNESS LETS ONE TOOL CALL CARRY, SO IT STILL GOES THROUGH IN ONE CALL INSTEAD OF SEVERAL BATCHES, WRITTEN BY HAND WITH YOUR OWN FILE TOOL; IT IS NEVER A WAY AROUND A MALFORMED CALL AND NEVER GENERATED BY A SCRIPT. Validated pre-edit bytes and successfully written after-versions are saved best-effort in .conduit/history; file-history reads them and file-restore can restore any retained version, older or newer. History failures warn without changing atomic rollback. MANDATORY for editing an existing script - a .cs under Assets/ is never edited with the filesystem tools: N exact-string replacements across any number of .cs files in ONE atomic call with ONE compile, instead of one full-file write and compile per edit. edits is a JSON array of Edit-shaped objects [{\"file_path\":\"Assets/...cs\",\"old_string\":\"exact text\",\"new_string\":\"replacement\",\"replace_all\":false}, ...], passed as the array itself and never as one string holding it; or, for a batch larger than the harness lets one tool call carry (Claude Code, Codex and other harnesses each cap an argument), the project path of a .json file holding that array, written by hand, so the batch still goes through in one call. The tool itself has NO size limit: batches of thousands of lines across many files go through in one call. A client error saying the input could not be parsed as JSON therefore means the arguments were MALFORMED - the array sent as a string, an unescaped quote, backslash or control character - and never that the batch was too big: fix the encoding and resend the same batch whole, never split it. report chooses what a success echoes: full, the default, prints every edit as a line-numbered diff with context; summary prints one line per edit instead, naming its file, its index, the lines it landed on and how many lines it removed and added, with the same History lines and post-compile report. Use summary for long or whole-file batches, whose full echo can outgrow what a client shows in one reply; file-history <path> | <pre-edit revision> | diff shows any file's change afterwards. A refusal reads the same whichever report was asked for. RULES, all enforced: every file must be under Assets/ or a project-local embedded package, and registry, cache and external local packages are refused; each file must have been read via script-read this session and be unchanged since, and a PARTIAL read satisfies that (script-read <path> <startLine> <count>), so reading just the region you intend to edit is enough to edit a file of any size and a whole-file read is never required; old_string must match byte-exactly (sole tolerance: line-ending flavour) and exactly once unless replace_all; old_string==new_string refused; overlapping edits refused; ONE failure anywhere refuses the WHOLE batch with nothing written, reporting the offending edit by index with line numbers. Cannot create files - use create-usharp / script-update-or-create. BLOCKS until the single compile and domain reload settle, then returns per-edit evidence plus the post-compile report - do NOT poll status afterwards. UdonSharp's auto-compile watcher is suppressed during the batch and the user's setting is restored automatically.";

                // Editor-lifecycle verbs - keyword-rich notes so tool search surfaces them by intent
                // (e.g. "compile", "console errors", "save scene"), not just exact verb names. Without a
                // note the generated MCP tool description is bare boilerplate and ranks poorly among 100+ tools.
                case "status":
                    return "Editor lifecycle status: whether Unity is compiling, importing/updating, in or entering play mode, idle, or still completing a queued UdonSharp program-asset link. Poll this ONLY after a compile that no bridge verb started (a filesystem edit, or Unity's auto-refresh on focus). Do NOT poll after refresh, domain-refresh, create-usharp, delete-usharp, a .cs/.dll/.asmdef reimport, or a .cs assets-delete: those block until the work is finished and return the post-compile report themselves, and polling during that window just queues behind the work and times out.";

                case "play":
                case "stop":
                case "toggleplay":
                    return "Enter or leave Unity Play Mode. The command withholds its response through any play-mode domain reload and returns only after Unity reports the matching EnteredPlayMode or EnteredEditMode callback, with final lifecycle status. Do not issue a separate state poll after it.";

                case "pause":
                case "resume":
                case "togglepause":
                    return "Pause or resume an active Unity Play Mode session. The command returns only after Unity reports the matching Paused or Unpaused callback, with final lifecycle status, and refuses pause changes outside Play Mode. Do not issue a separate state poll after it.";

                case "editor-application-set-state":
                    return "Action-form editor lifecycle control accepting play, stop, pause, resume, or unpause. It uses the same deferred settled-state response as the dedicated lifecycle verbs, so do not issue a separate state poll after it.";

                case "assets-prefab-close":
                    return "Close the active Prefab Mode stage without a modal. The optional save flag defaults to true; pass false explicitly to discard stage changes. A failed save aborts the close and returns the error.";

                case "assets-prefab-save":
                    return "Save the active Prefab Mode stage through Unity's full stage lifecycle, including clearing its internal dirty state so a later close does not open a save-confirmation modal.";

                case "reimport":
                    return "Compile/import trigger for what auto-refresh misses - a .dll, .asmdef or native-plugin change, or a FIRST import of a file placed under Assets/ from outside the editor: name one touched .cs/.dll/.asmdef and Unity will compile. It is NOT the finisher after editing scripts, because udon-batch-edit blocks through its own compile and needs no trigger. UdonSharp recompiles its whole program set from a single U# script reimport, so this replaces a full refresh for normal code work WITHOUT reloading native plugins/all assets. Use this once, not assets-refresh/refresh. This is NOT a reliable fix for newly-added serialized fields missing from Conduit reflection; use domain-refresh for that. It also performs a FIRST import, so there is no separate import verb: a file placed under Assets/ from outside the editor, with no .meta and unknown to the AssetDatabase, is imported by naming it here, synchronously. The response is read back from the AssetDatabase rather than assumed, naming the asset's resolved type, name and guid and saying whether the path was new to the project or already known, so a caller never needs a second read to learn what the import produced.";

                case "editor-window-list":
                    return "List the editor windows currently open, with each one's screen rectangle. Use it to find a window's exact type name before clicking it, and to convert a screen position from a screenshot into the window-local position editor-window-click expects.";

                case "editor-window-click":
                    return "PRESS A BUTTON in any Unity editor window BY ITS LABEL, e.g. 'Connect' or 'Apply' - no screenshot and no coordinates. Sends a real MouseDown/MouseUp pair so the window's own hit testing decides what was hit. A label matching several controls is refused with the candidates listed; append #index to pick one. An 'x y' pair is accepted instead of a label when you already know the position.";

                case "editor-window-overlays":
                    return "List a Scene view's OVERLAYS - Tools, Tool Settings, View Options, Grid and Snap, Orientation, AI Navigation, Light Settings, Physics Debug - with their id, display name and whether each is shown, collapsed or floating. Overlays are a separate UI system from both IMGUI and the window's element tree, so they do not appear in editor-window-controls unless they are currently displayed.";

                case "editor-window-overlay-set":
                    return "Show, hide, collapse or float an overlay on a Scene view, e.g. turn the AI Navigation or Light Settings overlay back on. Reports the value before and after so you can see the change landed. A hidden overlay still exists and keeps its settings.";

                case "scene-tool-state":
                    return "Read the Scene view's tool and snapping state: active tool (Move/Rotate/Scale/Rect/Transform), pivot mode and rotation, the transform handle position and rotation, grid snap enabled plus move/rotate/scale snap increments, and whether the grid and gizmos are drawn. Read from the editor's own state rather than scraped from the Tools and Grid overlays.";

                case "editor-window-controls":
                    return "LIST THE CONTROLS inside an editor window with their labels, GUIStyle and rects - buttons, labels, foldouts, help boxes. Works for IMGUI windows (inspectors, custom tool windows) by reading Unity's IMGUI draw instructions, so no screenshot and no image tokens are needed. Use it to discover what is clickable, then click by label with editor-window-click.";

                case "editor-window-key":
                    return "Send a key press (with optional shift/ctrl/alt/cmd modifiers) to an open editor window, for text fields, list navigation, and window shortcut keys. Pair with editor-window-click to focus a field first.";

                case "editor-window-close":
                    return "CLOSE an open editor window, taking the same selector editor-window-list and editor-window-controls take, so a utility panel opened only to read something can be put away instead of left covering the layout. CLOSING A DOCKED TAB IS NOT A REVERSIBLE NAVIGATION STEP - it removes that window from the editor layout Unity SAVES, so the user has to reopen it and re-place its tab. The result says when the window was docked for exactly that reason. Prefer this for windows the caller itself opened, never as a test target: pick a floating utility panel, not something out of the user's docked layout. A window reporting unsaved changes is refused rather than closed, because Unity answers that close with a modal save prompt which blocks the editor thread this call needs to answer on. Reports the type and title it closed, and says the window is STILL OPEN rather than claiming success when a close did not take, because the outcome is read back by instance id rather than assumed.";

                case "delete-usharp":
                    return "Delete a UdonSharp behaviour: removes the .cs AND the UdonSharpProgramAsset linked to it in one call, so no orphaned program asset is left behind. The pair is matched on the program asset's own sourceCsScript link, not on a same-name convention. BLOCKS until the resulting compile and domain reload settle, then reports both assets it removed. Use assets-delete for a plain C# script or any non-script asset.";

                case "assets-prefab-unpack":
                    return "Unpack a prefab INSTANCE in a scene into plain GameObjects, for deriving a new variant from an existing prefab so the restructure is direct editing instead of instance overrides. Modes: outermost (default, keeps nested prefab instances linked) or completely. Resolves a child target up to the instance root, refuses when the target is not part of a prefab instance or while Prefab Mode is open, and reports the source prefab, the mode used, and how many nested instances remain linked.";

                case "domain-refresh":
                    return "Request a Unity editor script domain reload without forcing a broad asset refresh. Use after adding serialized fields when Conduit's component reflection still shows the old field list. Refuses during Play Mode and defers its response until the reload completes.";

                case "find-by-component":
                    return "Locate objects by what they ARE: returns every GameObject (incl. inactive) in one scene that has a component of the given type (short or full name, e.g. KaraokeStageDisplay or BoxCollider). scene is required and names the scene to search: a scene name, path or opened-scene index as scene-list-opened prints them. It must be loaded, and a name shared by several scenes is refused with each candidate listed. While Prefab Mode is open the prefab stage is searched instead and the reply says so. Use this instead of dumping the whole scene-tree to find where a behaviour lives.";

                case "set-reference":
                    return "Wire an object-reference field reliably: sets (or clears with 'null') a component's serialized UnityEngine.Object field to a scene object/component or an asset (Assets/... path). Goes through SerializedProperty + UdonSharp proxy sync so scene-local and sub-asset (e.g. sprite) references actually persist, including on U# behaviours. Does NOT set UnityEvents (a Button's OnClick), value fields, or arrays.";

                case "unity-event-get":
                    return "Read a UnityEvent field such as Button.m_OnClick through SerializedProperty, including each persistent call's real target object/component, method, mode, call state, and arguments. Use this instead of trusting EditorJsonUtility's nested UnityEvent JSON.";

                case "unity-event-set-string":
                    return "Set one persistent UnityEvent string listener, e.g. Button.m_OnClick -> backing UdonBehaviour.SendCustomEvent(\"_EventName\"). Uses SerializedProperty and an explicit listener component selector so Udon targets do not silently point at the wrong behaviour.";

                case "assets-refresh":
                case "refresh":
                    return "HEAVY: full AssetDatabase.Refresh - reloads native plugins and reimports everything out of date. BLOCKS until the resulting compile and domain reload settle, then returns the post-compile Console report, so do NOT poll 'status' afterwards - when this returns, the work is already finished. Avoid for normal script create/edit work, even multi-file edits; use one 'reimport <touched-script>' instead because UdonSharp recompiles all U# programs from that. Only use full refresh when Unity must discover external filesystem changes or broad added/deleted assets.";

                case "console-get-logs":
                    return "Read recent Unity Console log entries - errors, warnings, exceptions, and messages. Use to check for compile errors or runtime output after a change.";

                case "console-clear-logs":
                    return "Clear all Unity Console log entries.";

                case "save-assets":
                    return "Save all modified (dirty) assets to disk (AssetDatabase.SaveAssets). Persist asset/material/prefab changes.";

                case "scene-save":
                    return "Save the open scene(s) to disk. Persist scene hierarchy and GameObject edits.";

                case "save-scenes":
                    return "Save ONE scene to disk, named by the required scene argument: a scene name, path or opened-scene index as scene-list-opened prints them. The scene must be loaded and must already have an asset path, and an ambiguous name is refused with each candidate listed. No other open scene is touched, so a scene you did not mean to write stays as it is. Reports Unity's own save result. scene-save with no argument saves every open scene instead.";

                case "gameobject-tree":
                    return "Print one resolved GameObject and its descendant hierarchy as recursive JSON - inspect an asset/object subtree without writing a throwaway editor script. For the whole scene use scene-tree. Add 'names' for an indented name-only tree instead, which is the one to use when you want the structure rather than each object's values: the JSON form carries a full transform and metadata payload per node and buries the shape it was called to show.";

                case "scene-tree":
                    return "Print one scene's hierarchy. scene is required: a scene name, path or opened-scene index as scene-list-opened prints them. The scene must be loaded, and an ambiguous name is refused with each candidate listed. The default rows carry path, active state, transform triplet and every component name, so add 'names' for an indented name-only tree when the question is what is nested where - that form prints one name per row, marks inactive objects '[off]', and says how many children a depth limit withheld. Both take an optional maxDepth (default 12), in either order after the scene: 'scene-tree VehicleTest names', 'scene-tree VehicleTest 4 names'.";

                case "assets-get-data":
                    return "Read an asset's serialized data/contents by asset path, GUID, or instance ID.";

                // Animator authoring family. Notes are keyword-rich (animator controller, state
                // machine, blend tree, animation clip, avatar mask, NPC locomotion) so intent
                // searches surface the right verb among 140+ tools.
                case "animator-create":
                    return "Create an AnimatorController (.controller) asset for driving Animators (NPCs, props, UI). Entry point to the animator-* family: parameters, layers, masks, states, sub-state machines, transitions, blend trees. Assign it to an Animator component with set-reference.";

                case "animator-get":
                    return "Read an AnimatorController's COMPLETE graph as JSON: parameters, layers (weight/blending/mask/ikPass), and each layer's recursive state machine with states, motions, blend trees, and every transition with its conditions. The honest read-back for animator authoring - assets-get-data cannot traverse this multi-object asset.";

                case "animator-parameter-set":
                    return "Add a controller parameter (float/int/bool/trigger) or update an existing one's default value. Type changes are refused (they silently break conditions/blend trees referencing the parameter).";

                case "animator-layer-set":
                    return "Add a layer by name or configure an existing one: defaultWeight, blendingMode (Override/Additive), avatarMask asset, ikPass, syncedLayerIndex. Handles Unity's copy-on-read layers array correctly and reports each property old -> new.";

                case "animator-mask-create":
                    return "Create an AvatarMask (.mask) asset for partial-body animation layers (e.g. upper-body-only NPC gestures). Configure humanoid body parts and generic transform paths inline or later with animator-mask-set.";

                case "animator-state-set":
                    return "Add or update a state in a layer's state machine (sub-state machines addressed as SM/State paths): motion clip, speed(+parameter), mirror(+parameter), cycleOffset(+parameter), timeParameter, writeDefaultValues, ikOnFeet, tag, node position, makeDefault. Parameter-driven modifiers validate against the controller's parameters first.";

                case "animator-transition-add":
                    return "Add a transition: state->state/SM/Exit, AnyState->state/SM, Entry->state/SM, or SM->state/SM/Exit. Options JSON sets conditions (validated against parameters INCLUDING mode-vs-type compatibility), exit time, duration, offset, interruption. See animator-transition-set/-remove for editing existing ones (address as from | to[#index]).";

                case "animator-blendtree-set":
                    return "Build a blend tree (1D, 2D directional/cartesian, or Direct) and set it as a state's motion - the core of NPC locomotion (idle/walk/run by Speed, strafing by X/Y). Children are clips or nested trees with thresholds/positions/timeScale/mirror; blend parameters must exist as Floats. Trees persist as controller sub-assets, nothing leaks.";

                case "animation-clip-create":
                    return "Create an AnimationClip (.anim) asset; optional settings JSON (frameRate, loopTime, mirror, events) applies in the same call. Author curves with animation-clip-set-curve.";

                case "animation-clip-set-curve":
                    return "Set, replace, or remove ONE curve on an AnimationClip: float keyframe curves (position/rotation/scale, blend shapes, material floats, m_IsActive toggles) or object-reference curves (sprite/material swaps, keys carry \"reference\" asset paths). null keys removes the binding. targetPath is the animated child's path relative to the Animator root.";

                case "animation-clip-get":
                    return "Read an AnimationClip's authoring view as JSON: frameRate, length, loop settings, every float and object-reference curve binding with key counts, and animation events.";

                case "animator-transition-set":
                    return "Change the settings of ONE existing transition, addressed by the state it leaves and the state it reaches. from and to name those states; when more than one transition runs between the same pair, append #N to the destination to pick one, as in Idle#1, and without the suffix an ambiguous destination is refused rather than guessed at. optionsJson carries only the fields to change. animator-get is the only full read-back of the graph, so read it there rather than inferring what a transition currently holds.";

                case "animator-transition-remove":
                    return "Delete ONE existing transition, addressed exactly as animator-transition-set addresses it, including the #N destination suffix when several transitions share a destination.";

                case "assets-material-set-shader":
                    return "Retarget a material asset onto another shader, by shader name or by shader asset path. This is the only route to a material's shader: assets-modify resolves names against the CURRENT shader's properties, so m_Shader is unreachable there. Reports which textures carried over by name, which were DROPPED because the incoming shader has no such slot, which of the new shader's slots are empty, and warns about keywords left behind from the old shader that the new one never reads (that last one renders flat while looking correctly wired). Pass an optional comma-separated keyword list to replace the keyword set in the same call.";

                case "assets-material-set-texture":
                    return "Assign a Texture or RenderTexture asset to a named shader texture property on a material, e.g. set _VideoTex on a camera-screen material to a render texture. Uses targeted material import, not a full AssetDatabase.Refresh.";

                case "assets-prefab-instantiate":
                    return "Instantiate a prefab asset into the scene named by the required scene argument (a loaded scene's name, path or opened-scene index) as a LINKED instance, keeping its connection to the asset. A parent must be in that same scene or the call is refused with nothing created. parent resolves like any other target and also takes root, scene, none, null or an empty value for the scene root; parenting keeps the prefab's own LOCAL pose rather than its world one, so the instance lands where the parent puts it. position is three space-separated numbers and is applied as a WORLD position after parenting - note that set-position writes a LOCAL one, so the same three numbers mean different places through the two verbs. Returns the new instance as JSON, including the instance id to address it by.";

                case "animation-clip-sample":
                    return "Pose an AnimationClip onto a live scene object at one time, WITHOUT entering play mode, and report every binding the clip drives there with its value at that time. This is how an authored clip gets looked at: the pose stays live, so scene-view-frame and screenshot-scene capture it, and sampling another time re-poses without any cleanup in between. WARNS when curves target a path the object does not have, because such a clip poses nothing and silence there reads as success. Leaves the editor in ANIMATION MODE, where ordinary edits are reverted as well - leave it with animation-clip-sample-stop.";

                case "animation-clip-sample-stop":
                    return "Leave animation mode, restoring every value a sample posed. Says so plainly when nothing was posed rather than reporting a success it did not perform. Call this when finished sampling: while animation mode lasts, edits made any other way are reverted too.";

                case "type-find":
                    return "Check a type EXISTS in the loaded assemblies before a design assumes it does not - the bare call lists every loaded type whose name contains the fragment, with its assembly, and says plainly when nothing matches. Sections after a pipe read one resolved type: constants (every const and static field WITH its value, private included - a null static is an answer, not a miss), enum (members with the number behind each, which is how a serialized enum int gets decoded without mutating an asset), members (the public instance surface), all. Nested types print and resolve with a plus sign, e.g. VRC.SDKBase.Networking+SyncType.";

                case "udon-program":
                    return "Read what an UdonSharp behaviour COMPILED TO, from the Udon assembly listing rather than from its C#. Sections after a pipe: summary (default), exports, methods, externs, symbols, raw. EXTERNS is the one to reach for when a behaviour runs but does not do what its source appears to say - it lists every engine call the program makes, grouped under the method that makes it. A method that appears under methods but not under exports is code Udon can never raise, which is how an event that is not really an entry point shows up. Target a scene object, a .asset program asset, or the behaviour's .cs. The listing is cached per compile, so a behaviour that has never compiled reports that rather than pretending it is empty.";

                case "udon-program-audit":
                    return "List every UdonSharp behaviour in one scene (the required scene argument: a loaded scene's name, path or opened-scene index) with its entry points, extern count and method count, flagging the ones that carry a PER-FRAME entry point (_update, _lateUpdate, _fixedUpdate, _postLateUpdate). Reads the compiled Udon assembly, so it reports what the program actually exports rather than what the C# looks like. Use it to find what genuinely costs frames in a scene: the Udon VM dispatches a per-frame entry point whether or not the method body does anything. Takes an optional case-insensitive filter on the behaviour type name.";

                case "udon-exposure":
                    return "The authoritative answer to whether Udon can call something AT ALL, read from the SDK's own node-definition registry instead of a compile probe - one call replaces the one-member-per-compile whitelist procedure, and it covers events (Event_OnPlayerJoined) and types (Type_...) alongside methods. Lists every definition name containing the fragment. The registry strips dots out of type names, so dotted type-find output is also matched through a stripped spelling and pastes straight in; a definition name is the exact extern signature spelling udon-program prints. No match means Udon does not expose it under that name. A fragment search only finds what was already suspected, so the registry also ENUMERATES: | types renders the whole surface as an overview (every exposed type with its definition count, every event by name), | dump <path> writes every definition name to a sorted baseline file, and | diff <path> compares the live registry against a baseline and reports every added and removed definition - dump once per SDK version and each update announces its own new surface, no keyword guessed and no shell tooling. An empty fragment with a section covers the whole registry; a fragment narrows any of them.";

                case "scene-view-pick":
                    return "Cast a ray through a pixel of the scene view and report the nearest thing it hits: scene path, world point, normal, distance. Coordinates are pixels with a TOP-LEFT origin, matching a screenshot-scene capture; pass the image size the pixel came from when it differs from the live view. Tests colliders AND render meshes (MeshFilters) and names which pass produced the hit, so geometry without a collider still picks; trigger colliders are crossed and reported, never picked. Syncs physics transforms first, because edit-mode physics otherwise reports stale positions for freshly moved objects. This is how a screenshot observation becomes a world position.";

                case "gameobject-component-add":
                    return "Add a component of a given type to a GameObject in the scene.";

                case "reparent-object":
                    return "Move a scene object under another parent, or back out to the scene root. parent resolves like any other target - an instance id, a path such as /Root/Child, an unambiguous name, or a scene-qualified SceneName:/Root/Child, searched across every loaded scene - and the words root, scene, none, null, a single slash, or an empty value all mean the scene root. The object KEEPS ITS WORLD POSITION, so nothing appears to move and its local numbers change instead; set-position afterwards when the local offset is what matters, because that verb writes a LOCAL position. Refuses to parent an object under itself or under one of its own children, and reports the old path and the new one so the move can be checked without a second read. A path or name matching several objects is refused with each match listed, so qualify it with the scene or use the instance id.";

                case "gameobject-set-parent":
                    return "The same operation as reparent-object, through the same handler and with the same rules for parent, world position and refusals. See that verb.";

                case "gameobject-remove-missing-scripts":
                    return "Strip MonoBehaviour components whose script no longer resolves, from a target and its children. Unity REFUSES to save a prefab that still contains one, and a missing component has neither a type nor a usable index, so gameobject-component-destroy cannot reach it - this verb is the only way to clear one. Reports the removed count per object, and says so plainly when it found nothing.";

                case "set-position":
                case "set-rotation":
                case "set-scale":
                    return "Write one transform vector on a scene object (target: an instance id, a path, a name or SceneName:/Path, searched across every loaded scene and refused when it matches several objects), as three numbers. All three verbs write LOCAL values - localPosition, localEulerAngles and localScale - so under a parent they are offsets from that parent and NOT world coordinates, and an object at 0 0 0 sits wherever its parent sits rather than at the world origin. Rotation is Euler degrees. This is the opposite convention to assets-prefab-instantiate, whose position is a WORLD one, and reparent-object preserves an object's WORLD pose while changing these local numbers underneath it. Reports the local value before and as read back after, and the world position after a set-position.";

                case "head":
                    return "Console entries from the OLDEST end, in full with stack traces, each tagged #n. The counterpart to tail: use it when the first thing that went wrong matters more than the last, since a cascade's real cause is at the top.";

                case "tail":
                    return "Console entries from the NEWEST end, in full with stack traces, each tagged #n. The usual first read after a change; head is the counterpart when the first failure matters more than the last.";

                case "summary":
                    return "Console counts by severity with the most recent entries condensed to one line each, for deciding whether anything needs reading in full before spending a tail on it.";

                case "select":
                    return "Select an asset in the Project window and ping it, so the next screenshot or manual look lands on the right thing. Changes the editor selection; it does not open or modify the asset.";

                case "screenshot-scene":
                    return "Capture the SCENE view to a png and report where it was written. This is the editor's authoring camera, so it shows gizmos and the current scene-view framing rather than what a player sees; screenshot-game is the play-mode counterpart. Pair it with scene-view-pick to turn a pixel in the image back into a world position.";

                case "screenshot-game":
                    return "Capture the GAME view to a png and report where it was written and which camera was used, which is what a camera in one scene actually renders. scene is required (a loaded scene's name, path or opened-scene index); the camera is that scene's one tagged MainCamera, otherwise its first enabled camera, so the capture does not depend on which scene is active. Needs the game view to be rendering, so in edit mode it captures whatever that view currently shows.";

                case "game-view-click":
                    return "Send a synthetic mouse click at a pixel of the game view, for driving runtime UI in play mode. Coordinates are game-view pixels, not screen pixels.";

                case "game-view-focus":
                    return "Give the game view keyboard focus inside the editor, as clicking it would. game-view-key does not need it: it posts to the game view's own window.";

                case "scene-view-state":
                    return "Read the scene view camera's current pivot, rotation, size and projection, which is what every other scene-view verb moves. Read this before and after a move rather than assuming where the camera ended up.";

                case "scene-view-pivot":
                    return "Move the point the scene view camera orbits around, in world coordinates. The camera keeps its direction and distance, so this slides the whole view.";

                case "scene-view-rotation":
                    return "Set the scene view camera's orientation as Euler degrees, keeping its pivot and distance.";

                case "scene-view-size":
                    return "Set the scene view's size value, which is how far the camera sits from its pivot - larger pulls back. scene-view-state reads the current one.";

                case "scene-view-pan":
                    return "Slide the scene view camera across its own right and up axes, moving the pivot with it.";

                case "scene-view-orbit":
                    return "Turn the scene view camera around its pivot by a yaw and a pitch in degrees, keeping its distance.";

                case "scene-view-zoom":
                    return "Multiply the scene view's distance from its pivot by a factor: below 1 moves in, above 1 pulls out.";

                case "scene-view-move":
                    return "Fly the scene view camera along its own right, up and forward axes, taking the pivot with it.";

                case "scene-view-frame":
                    return "Point the scene view at one object and pull back far enough to fit it, the same as pressing F on it. The fastest way to get a named object on screen before a screenshot.";

                case "scene-view-align":
                    return "Snap the scene view to a straight-on axis view - front, back, left, right, top, bottom - or back to the angled iso view, keeping the current pivot.";

                case "find-object":
                    return "Resolve one scene object by instance id, path, name or scene-qualified SceneName:/Root/Child (or SceneName:Name), searching every loaded scene, and report what it found, including its path, scene and id. A path or name matching more than one object is refused with each match listed as [id] Scene:/path, so qualify it with the scene or use the id. Use it to turn a name into the stable id that every other verb accepts, and to confirm a name is not ambiguous before relying on it.";

                case "create-empty":
                    return "Create an empty GameObject in the scene named by the required scene argument (a loaded scene's name, path or opened-scene index; an ambiguous name is refused), optionally under a parent. It has a Transform and nothing else. A root object lands in that scene; a parent must itself be in that scene or the call is refused and nothing is created. The parent resolves like any target (instance id, path, name or SceneName:/Path) and also takes root, scene, none or null for the scene root.";

                case "rename-object":
                    return "Rename one scene object (target syntax as find-object, including SceneName:/Path). Names are DATA and are written exactly as given, including case. Renaming changes the object's scene path, so anything addressing it by path needs the new one.";

                case "set-sibling-index":
                    return "Move one object up or down among its siblings under the same parent, which is what decides draw order for UI and the order children appear in the hierarchy. Index 0 is first.";

                case "vrc-menu":
                    return "Read or drive ClientSim's in-play VRChat menu: status reports whether it is open, open and close move it, and accept-warning dismisses the warning panel that otherwise swallows the next input. Play mode only.";

                case "vrc-input":
                    return "Press one player input in play mode - jump, run, use, grab, drop, crouch, prone, menu, or release-mouse. The phase chooses down, up, or a press that does both; hold an input by sending down and sending up later. Play mode only, and ClientSim is not the VRChat client, so what this proves is that the handler runs (conduit-testing.md).";

                case "vrc-stance":
                    return "Put the local player into standing, crouching or prone in play mode, which is what changes their eye height and what a height-gated trigger sees.";

                case "vrc-drive":
                    return "Walk the local player in play mode: forward and strafe are movement axes, turnDegPerSec spins them, and the optional seconds runs the input for that long before stopping rather than leaving it held. Without seconds the input stays applied until vrc-drive-stop. Play mode only.";

                case "vrc-teleport":
                    return "Put the local player at a WORLD position in play mode, optionally facing a yaw in degrees. The instant way to reach a far part of a world without driving there.";

                case "vrc-look":
                    return "Aim the local player's view at an ABSOLUTE yaw and pitch in degrees. vrc-look-delta is the relative form; ClientSim clamps pitch to its own limit rather than the client's (conduit-vrc-input.md).";

                case "vrc-look-delta":
                    return "Turn the local player's view BY a yaw and pitch in degrees from where it currently points, which is the shape to use when nudging aim rather than setting it.";

                case "vrc-look-at":
                    return "Aim the local player's view at a scene object or a world point, so a test does not have to work out the angles. Takes a target the same way other verbs do, or three numbers as a point.";

                case "animation-clip-set":
                    return "Change an AnimationClip's own settings - frame rate, loop time, loop pose and the wrap behaviour - without touching its curves, which are animation-clip-set-curve's job. Written field by field from the JSON, so a field nobody names keeps its value.";

                case "animator-parameter-remove":
                    return "Delete one parameter from an AnimatorController by name. Conditions on transitions that referenced it do not follow it out, so read the graph back with animator-get afterwards.";

                case "animator-layer-remove":
                    return "Delete one layer from an AnimatorController, by name or by index, taking its state machine with it. Layer indexes shift afterwards, so address later layers by NAME rather than by a number read before the removal.";

                case "animator-mask-set":
                    return "Write an AvatarMask's settings: which humanoid body parts it allows and which transform paths it includes. The mask is what decides how much of a layer's animation actually reaches the rig.";

                case "animator-state-remove":
                    return "Delete one state from a layer's state machine, addressed by its path within that machine. Transitions into and out of it go with it.";

                case "animator-statemachine-set":
                    return "Create or update a sub state machine inside a layer, addressed by its path. Sub machines are how a large graph stays readable; animator-state-set puts states inside one.";

                case "animator-statemachine-remove":
                    return "Delete one sub state machine from a layer, by path, taking the states and transitions inside it with it.";

                case "assets-copy":
                    return "Duplicate an asset to a new project path THROUGH the editor, so the copy gets its own guid and the import happens properly. Copying the file from outside Unity instead is what produces a duplicate guid and a broken reference later.";

                case "assets-create-folder":
                    return "Create one project folder under an existing parent, registered with the asset database rather than just made on disk.";

                case "assets-delete":
                    return "Delete an asset or folder through the editor, which also clears its .meta and any reference bookkeeping. BLOCKS through the resulting compile when the path is one Unity compiles, so nothing follows it; what it held cannot be inspected afterwards, so read first if that matters.";

                case "assets-find":
                    return "Search the project for assets using Unity's own filter syntax - a bare word matches names, t:Material or t:Texture2D narrows by type, l:Label by label - optionally scoped to one folder. Returns asset paths. This is the search the Project window runs, so a filter that works there works here.";

                case "assets-find-built-in":
                    return "List Unity's BUILT-IN assets, the ones with no file in the project such as the default materials, meshes and fonts. They cannot be found with assets-find because they have no project path, which is why this is a separate verb.";

                case "assets-material-create":
                    return "Create a new material asset on the named shader. assets-material-set-shader retargets an existing one and assets-material-set-texture fills its texture slots.";

                case "assets-modify":
                    return "Write named fields on an asset, FIELD BY FIELD, so a field nobody names keeps its value. Names resolve against the asset's serialized properties. A material's shader and its shader properties are NOT reachable here - use assets-material-set-shader and assets-material-set-texture, which is the trap this verb cannot warn about from inside.";

                case "assets-move":
                    return "Move or rename an asset THROUGH the editor, which carries its guid so every existing reference follows it. Moving the file outside Unity instead breaks every reference to it.";

                case "assets-prefab-create":
                    return "Save a scene object as a new prefab asset and leave the scene object linked to it as an instance. Edit the result in Prefab Mode with assets-prefab-open rather than through that instance, because instance edits are only overrides.";

                case "assets-prefab-open":
                    return "Open a prefab in PREFAB MODE, given either the asset path or a scene instance of it. This is the only place prefab edits reach the asset itself; close with assets-prefab-close, which saves by default.";

                case "assets-shader-get-data":
                    return "Read one shader's real property table - every property name, its kind and its default - which is what assets-material-set-texture and material writes have to match. Read it rather than guessing a property name like _MainTex is present.";

                case "assets-shader-list-all":
                    return "List every shader loaded in the project with its name as materials reference it, optionally filtered. Use it to find the exact shader name assets-material-create and assets-material-set-shader expect.";

                case "object-get-data":
                    return "Read one object's serialized data as JSON - the asset or GameObject itself rather than a component on it. Nested object REFERENCES come back as instanceID 0 and cannot be trusted from here; read those from a component through gameobject-component-get instead (conduit-bridge-authoring.md).";

                case "object-modify":
                    return "Write named fields on any object - a component, a GameObject, an asset - by its serialized property names, field by field, through the same writer as gameobject-component-modify: object references resolve from an instanceID, guid or path value, and each field is reported as it was and as read back after the write, with a warning for any field the write changed that the payload did not name.";

                case "file-history":
                    return "Read Conduit's persistent source version history without changing files. No revision lists newest-first metadata, time, operation, client, job id and hashes; a revision returns verified original content or view=diff against the current file. currentSha256 is the guard for file-restore. Covers .cs, .shader, .hlsl and .cginc under Assets or registered project-local embedded Packages. state distinguishes before-attempted-write from after-write; after-write confirms saved bytes, not a successful compile. isCurrent marks versions matching the current file. Versions are compressed into one atomically replaced ZIP/Deflate pack per source; this is native Conduit storage, not a Git repository or Git pack format. Legacy loose snapshots remain readable and migrate with their original revision ids on the next write to that source. Consecutive identical versions normally reuse the existing snapshot. Exact bytes live outside Assets and Library in .conduit/history; invalid UTF-8 content is returned as contentBase64. The window's storage slider goes up to 1024 MiB and cannot be saved below actual compressed disk usage. Owner settings in .conduit/history/settings.json default to versionsPerFile=20 and maxBytes=268435456; captures apply oldest-first per-file and global byte retention. Read-only inspection creates nothing.";

                case "file-restore":
                    return "Restore one verified file-history revision to its original existing source path, preserving its exact bytes and .meta. Requires expectedCurrentSha256 from file-history; refuses a changed target, corrupt snapshot, unsupported path or byte-identical no-op before any source write. Any retained older or newer version can be chosen. Current bytes are captured before restoration and the restored bytes afterwards, so both directions remain available; a history-storage failure warns without preventing restoration. Uses atomic replacement, then imports and blocks through compilation when needed. Refuses Play Mode, deleted targets, registry/cache/external packages and redirected paths.";

                case "script-read":
                    return "Read a C# file from the project, optionally a line range, with line numbers. A partial read is enough to satisfy the read-before-edit requirement that udon-batch-edit enforces, so reading just the region being changed is the normal shape for a large file.";

                case "script-delete":
                    return "Delete a C# script and its .meta through the editor. BLOCKS through the resulting compile, and any component still referencing it becomes a missing script that only gameobject-remove-missing-scripts can clear.";

                case "scene-create":
                    return "Create a new scene asset at a project path and open it. For a VRChat world the new scene still needs its descriptor and spawn set up before it will build (conduit-assets-prefabs.md).";

                case "scene-get-data":
                    return "Read one loaded scene's settings and root hierarchy as JSON, to a depth. A scene that is listed but unloaded is refused rather than read as empty, and an ambiguous scene name is refused with each candidate listed. scene-tree is the lighter read when only the shape is wanted; this one carries per-object values.";

                case "scene-open":
                    return "Open a scene asset. single closes what is open first and is the default; additive loads it ALONGSIDE the current scenes, which is how multi-scene setups are assembled; unloaded adds it to the Hierarchy without loading its contents, the state Unity's Unload Scene leaves behind. A scene already in the Hierarchy is reported, not opened twice, by unloaded.";

                case "scene-set-active":
                    return "Choose which of the open scenes is ACTIVE, which decides which scene's lighting settings apply. It no longer decides where objects land: the creation verbs take their own required scene argument. Only matters once more than one scene is loaded. A scene that is already active is reported as such, and an unloaded scene is refused because only a loaded one can be activated. Unloading the active scene hands the role to another loaded scene, so re-check it after a scene-unload.";

                case "scene-unload":
                    return "Unload one scene without touching the others. keep, the default, matches Unity's Unload Scene: the contents unload and the scene stays listed in the Hierarchy, greyed out. remove also takes it out of the Hierarchy, Unity's Remove Scene. Refuses to unload the only loaded scene. A scene with unsaved changes is left untouched unless changes says save (saved first, and nothing closes if the save fails) or discard.";

                case "game-view-key":
                    return "Press, hold or release a key in the running game, as UnityEngine.Input reads it (Input.GetKey, GetKeyDown, key-driven axes), so a world script's keyboard controls can be driven: game-view-key W 3 holds W for 3 seconds. Windows editor and play mode only; it posts real key messages to the game view's own window, so it works while another application has the foreground and needs no game-view-focus. press, the default, holds for seconds (0 is the shortest press the game can read, two frames) and releases by itself, returning at once rather than after the hold; down holds until a later up; up releases. The key is a UnityEngine.KeyCode name, any case: letters, digits, F1-F15, arrows, the editing keys, Space, Return, Escape, Tab, Backspace, Left/Right Shift and Control, the keypad and punctuation. Alt keys are refused. Every held key is released when play mode ends and before a domain reload.";

                case "gameobject-create":
                    return "Create a GameObject in the scene named by the required scene argument (a loaded scene's name, path or opened-scene index), either empty or as one of Unity's primitives - Cube, Sphere, Capsule, Cylinder, Plane, Quad - optionally under a parent, which must be in that same scene or the call is refused with nothing created. Both optional slots are positional: when a parent is given without a primitive the slot must still be sent EMPTY, or the parent is read as the primitive and the object silently lands at the scene root.";

                case "gameobject-destroy":
                    return "Destroy one scene object and everything under it. Registered with undo, so editor-undo can revert it, unlike asset deletion.";

                case "gameobject-duplicate":
                    return "Copy one scene object with its children and components, as Ctrl+D does, placing the copy beside the original under the same parent. gameobject-duplicate-multi makes several at once.";

                case "gameobject-find":
                    return "Resolve one scene object and report it, the same lookup every other verb's target uses: an instance id, a path, a name, or SceneName:/Path, searched across every loaded scene, refusing a path or name that matches several objects. Use it to confirm a name resolves to exactly one object before addressing it by that name.";

                case "gameobject-modify":
                    return "Change ONE property of a scene object: name, tag, layer, active, position, rotation or scale. Vectors take three numbers: position and rotation are WORLD space (transform.position / transform.rotation), scale is localScale; the reply gives the property before and as read back after, in world and local space for the vectors. Layer takes a name or an index, and the user layers 22-31 must be given by NUMBER because VRChat's reserved names are restored at upload (conduit-layers.md).";

                case "gameobject-component-get":
                    return "Read one component's serialized values as JSON, including its object REFERENCES resolved properly - this is the reliable way to read a reference, where object-get-data reports instanceID 0 for one that is genuinely wired.";

                case "gameobject-component-destroy":
                    return "Remove one component from a scene object. A component whose script no longer resolves cannot be reached here and needs gameobject-remove-missing-scripts instead.";

                case "gameobject-component-list-all":
                    return "List every component TYPE the editor can add, optionally filtered by name. Use it to find the exact type name gameobject-component-add expects before guessing at a spelling.";

                case "editor-selection-set":
                    return "Set the editor's selection to one object, which is what the inspector shows and what a manual look or screenshot will be about. Does not modify the object.";

                case "editor-tag-add":
                    return "Define a new tag in project settings. A duplicate, or an empty name, is a SILENT no-op rather than an error, so read editor-tag-list back rather than trusting the call to have added one.";

                case "editor-tag-remove":
                    return "Delete a tag from project settings, repeated with confirm to take effect. Every object carrying it becomes Untagged and nothing reports which ones - so to CHANGE a tag use editor-tag-rename, which carries its objects with it, rather than a remove followed by an add.";

                case "editor-tag-rename":
                    return "Rename a tag in place. An object stores a tag as a table entry rather than as the literal string, so every object carrying it FOLLOWS the rename automatically. This is the safe way to change a tag; remove-then-add silently untags every object that had it.";

                case "editor-layer-set":
                    return "Name a layer slot by index or by its current name, repeated with confirm when the slot is already occupied, because renaming an occupied slot silently changes what every mask referring to it means. VRChat reserves its own layers and restores them at upload (conduit-layers.md).";

                case "editor-layer-remove":
                    return "Clear a layer slot's name, repeated with confirm. Objects on that layer STAY on that layer number - only the name goes - and a mask built from the name afterwards silently resolves to layer 31.";

                case "editor-undo":
                    return "Revert the top of Unity's undo stack. It first NAMES what would be reverted and changes nothing until the call is repeated with confirm, because an undo cannot be inspected afterwards. The stack is shared with the editor's own history, so the top is often not the bridge's work, and a group's label is set when it opens rather than describing what merged into it later. Coverage is partial: asset and file operations go through the AssetDatabase and are not undoable at all, so an undo issued after a script write reverts whatever came BEFORE it instead.";

                case "editor-redo":
                    return "Re-apply the last reverted undo group, naming it first and changing nothing until repeated with confirm. The same partial coverage applies as for editor-undo.";

                case "reflection-method-find":
                    return "List a type's CONSTRUCTORS and METHODS by reflection, with their parameters, whether each is static, public and OBSOLETE, and what calling it yields - including the static methods and every constructor that type-find's member listing does not show. This is how an API's real call shape gets read before code is written against it. Constructors are listed under the runtime's own name for them, '.ctor', which is also what memberName filters on; a constructor reports the type it produces in returnType, since it has none of its own. An obsolete member carries obsoleteMessage naming its replacement, which matters because picking a deprecated overload is a warning the bridge caller never sees.";

                case "type-get-json-schema":
                    return "Emit a JSON schema for a type's serializable fields, which is the shape a modify verb's JSON payload has to match for that type.";

                case "package-add":
                    return "Add a Unity package by registry name or by URL, writing it into the project manifest. BLOCKS through the download, import and compile, so nothing follows it and the result IS the verdict. package-search finds the exact registry name first.";

                case "package-list":
                    return "List the packages this project actually resolved, with versions and where each came from. Pass offline to read the cached manifest without contacting the registry.";

                case "package-remove":
                    return "Remove a package from the project manifest by name. BLOCKS through the resulting compile, and anything in the project still referencing its types will fail to compile afterwards.";

                case "package-search":
                    return "Search the Unity registry for packages matching a query, reporting each one's exact name and latest version. The name it returns is what package-add expects.";

                case "screenshot-camera":
                    return "Render what a camera in one scene sees to a png. scene is required (a loaded scene's name, path or opened-scene index). cameraName picks a camera by name and is refused when no enabled camera of that name is in that scene; without it the scene's MainCamera, else its first enabled camera, is used and named in the reply. Works without entering play mode and without disturbing the scene or game views. The way to preview a specific camera's framing.";

                case "screenshot-isolated":
                    return "Render ONE object on its own to a png, with everything else hidden, so its shape and materials can be looked at without the rest of the scene in the way.";

                case "screenshot-window":
                    return "Capture an editor window - the Inspector, the Project window, a custom tool window - to a png exactly as it is drawn, so its styling and layout can be looked at. <window> is a type name or title as editor-window-list prints them. The path defaults to Temp/Conduit/Screenshots/<Type>.png, the reply gives it absolute, and the png is not imported into the project. The pane is repainted and read back from its own buffer, so the capture is correct while Unity sits behind another app; it is taken at the editor's pixel scale and includes the pane's tab strip. A docked window hidden behind another tab is refused rather than captured as whatever is in front. Reading the png costs image tokens, so use editor-window-controls when labels and rects answer the question.";

                case "profiler-enable-module":
                    return "Turn one of the Profiler window's modules on or off by its display name, as its Profiler Modules menu does, and report the state as it was and as the window reads it back now. A module already at the asked state is said to be; a name the window does not have is refused with the names it does have; a state that does not read back as asked is reported as not applied. Opens the Profiler window when none is open, and says so. A module that is off records nothing, so its numbers read empty rather than zero: GPU Usage must be on for profiler-frames to carry GPU time.";

                case "profiler-list-modules":
                    return "List the Profiler window's modules in its own order with each one's active state as the window reads it back. These are the names profiler-enable-module takes. Opens the Profiler window when none is open, and says so.";

                case "profiler-frames":
                    return "Read per-frame figures from the profiler's buffered frames. cpuMs is the whole frame, editor included. mainWorkMs and renderWorkMs are the main and render thread's shippable work: self time with editor, profiler (Profiler.*, GPUProfiler.*) and wait markers (Idle, WaitForJobGroupID, Semaphore.WaitForSignal, Gfx.WaitForGfxCommandsFromMainThread and the rest) removed; they are the figures to judge cost by. gpuMs is null on a frame without a GPU sample: every frame while GPU Usage is off, and the newest frame or two. The GPU Usage module itself adds about 0.8 ms of render-thread work, counted as overhead. Also batches, SetPass and draw calls, triangles, vertices, shadow casters and GC bytes. Returns mean, median, sd and n of every figure, and one row per frame unless rows is none. count is how many frames (default 30, up to 1000); from is the first frame as a number or a profiler-mark label, and - reads the newest complete frames. The counters section names counters to read instead of the default rendering and garbage set (none for no counters; profiler-counters lists the names), and the threads section names the threads whose work to read instead of main and render (profiler-threads lists them); a figure no frame carried is named in a warning. Each frame's two threads are walked, so a long range takes a moment; readMs says how long. To decide whether two stretches differ, use profiler-compare, not two means by eye.";

                case "profiler-compare":
                    return "Compare two ranges of buffered profiler frames, A and B, and say whether each difference is real. A range is start..end, each end a frame number or a profiler-mark label, and a side may join several ranges with plus signs (a1..a2+a3..a4) to interleave the states, so slow drift in the scene, such as traffic passing, falls on both sides; an A/A check on a scene with moving content needs that. what is figures (the default: every figure profiler-frames reads, taking the same counters and threads sections), markers (each named thread's markers by self time, main and render by default, the largest differences first with how many differ provably) or all. Consecutive frames are not independent (periodic spikes, garbage bursts), so each range is cut into blocks of blockFrames frames (default 10) and the 95% interval of B minus A comes from the spread of the block means (Welch's t). distinguishable is true only when that interval excludes zero; an interval that includes zero means the difference is not proven, whatever the means say. The ranges must be buffered and must not overlap. Record A and B in the same play session, alternating where the state can be switched, and check the method with an A/A comparison of two ranges of one state, which should come out not distinguishable. Judge cost by mainWorkMs, renderWorkMs and gpuMs, not cpuMs, which carries the editor.";

                case "profiler-mark":
                    return "Label the newest buffered profiler frame, so every range-taking verb (profiler-frames, profiler-compare, profiler-markers, profiler-marker-timeline) can take label..label instead of frame numbers read off an earlier reply. Mark each state as it starts and ends. A label already in use moves to the new frame and the reply names the old one. Labels last for the editor session, survive a script reload, and stop resolving once their frame leaves the buffer (profiler-marks says which). A label is letters, digits, dot, underscore and hyphen, and never a number.";

                case "profiler-marks":
                    return "List the profiler-mark labels with their frames and whether each frame is still buffered (action list, the default), or remove them all (action clear).";

                case "profiler-threads":
                    return "Discover the threads a buffered frame recorded. Without a filter it gives an overview: the main and render threads with their indices, and every group with its thread count and names collapsed, numbered workers counted under one name. A name or group fragment lists the matching threads with index, name and group, and * lists every one; frame is a frame number or label, and - (the default) reads the newest complete frame. The names are what profiler-frames, profiler-compare, profiler-markers and profiler-marker-timeline take; the indices are what profiler-frame-hierarchy takes.";

                case "profiler-counters":
                    return "Discover the counters a buffered frame carries - memory, physics, rendering, file and audio, about 160 in the editor. Without a filter it gives an overview by category with counts; a name or category fragment lists the matching counters with their raw values (times in nanoseconds, memory in bytes), and * lists every one. frame is a frame number or label, and - reads the newest complete frame. The names are what profiler-frames and profiler-compare take in their counters section.";

                case "profiler-markers":
                    return "Total every marker over a frame range on one thread (Main Thread by default), as a mean per frame: self and total CPU ms, self and total GPU ms (zero unless GPU Usage recorded the frames), garbage, calls and draw calls, plus the worst single frame's self ms and how many frames had the marker. Editor, profiler and wait markers are left out and their combined self time is given as noiseSelfMsPerFrame; Conduit's own work, under its ItsRenji Conduit markers, is left out too and given as conduitSelfMsPerFrame and conduitGcBytesPerFrame (on the main thread it is recorded only while profileEditor is on, since the editor loop is otherwise one sample). range is start..end with frame numbers or profiler-mark labels; sort is self, total, gpu, gc, calls, draws or max; filter keeps names containing it. scope editor lists the editor's own work by marker name instead (each EditorApplication.update hook, window repaint and the rest), with the game counted as noise; the profiler records that breakdown only while profileEditor is on in profiler-settings, so set it for the capture.";

                case "profiler-marker-timeline":
                    return "One marker's cost frame by frame over a range on one thread (Main Thread by default): self time and garbage summarised (mean, median, spread, worst frame, how many frames had it), and by default only the 10 worst frames by self time and by garbage with the frame gaps between the heaviest garbage frames, so a periodic burst shows as a repeated gap; rows all lists every frame, rows none lists none. A frame without the marker counts as zero. The marker name is exact, and when no frame has it the reply lists similar names. Use it to see a periodic spike or a garbage burst over time.";

                case "profiler-settings":
                    return "Read the profiler settings - frames buffered (frameCount), deepProfiling, allocationCallstacks (GC allocation call stack recording), profileEditor (records the editor's own work broken down rather than as one EditorLoop sample, which is how editor-only cost is told apart from shipping cost), the memory record mode, whether profiling is enabled and recording, and the buffered range - and with a JSON object write frameCount (300 to 2000), deepProfiling, allocationCallstacks and profileEditor. Every value is validated before any is applied, an unknown key refuses the whole write, deepProfiling is refused during play mode, and each value is read back with a warning where it differs.";

                case "profiler-isolate":
                    return "Measure what one object costs in play mode. It switches the target off and on in turn - what is the whole object by default, its renderers, or one component whose type is named in the next section - waits after each switch until the frames stop trending (the test profiler-wait-settled uses), records the phase, puts the target back exactly as found and reads it back, then compares the phases with and without it on the frame figures and on markers as profiler-compare does: diff is what the target as found costs, with 95% intervals. Defaults are 3 cycles of 90 frames per phase. It starts profiler recording when it was off, and with gpu on switches the GPU Usage module on for the run; it puts both back and reads them back. A phase still trending 30 seconds after its switch ends the run unmeasured. The work spans many frames, so the reply arrives when it finishes, or through job_status when the call's wait runs out. One run at a time; anything else changing during it shows up in the result.";

                case "profiler-wait-settled":
                    return "Wait until the frames recorded after the call stop trending, then report how long that took and the frame it settled at. Frame times never stop moving, so settled means positive evidence the trend has gone: the two newest windows - each at least a second of frame time and three blocks - compared on main-thread and render-thread work (block medians, 95% intervals) differ by less than 5% of each figure's own level. A provable difference is trending; an interval too wide to tell is inconclusive and the windows widen to 2 and 4 seconds. Not distinguishable alone is never accepted, because a noisy window passes that while still drifting. Use it after any change and before profiler-mark starts a state. Recording must already be on; it changes nothing. settled false means the state was still trending when maxSeconds (default 30, up to 300) ran out.";

                case "profiler-memory-objects":
                    return "Sum the memory every loaded object of the given types takes (Texture, Mesh, AudioClip, Material, AnimationClip, Shader and Font by default; any Unity object type by name, comma-separated), with the count and the editor-only share per type, and list the largest objects with their origin: a project asset (with its path), a scene or runtime object, or an editor object, which is hidden, never saved and absent from a build.";

                case "frame-debugger-capture":
                    return "Capture one frame with the Frame Debugger and report every event: counts by type, the objects drawn (hierarchy paths) with their event and draw counts (the largest 40, then one entry carrying the rest), the draw counts added up by the top two hierarchy levels (byGroup, such as /Systems/Traffic), the render passes, and for the first details draw events (chosen by a name fragment when filter is given) the shader, pass, light mode, vertex, index and instance counts, render target and batch-break reason, with the batch-break reasons counted. The Frame Debugger captures only while the editor is in front and its window exists, and in play mode only while play is paused, so on Windows the command records the window in front, brings the editor forward, opens the Frame Debugger window unfocused, pauses a running play mode, captures, resumes play, switches the Frame Debugger back off unless it was already on, closes the window it opened and hands the foreground back, reporting each step as read back. Elsewhere the editor must already be in front. The reply arrives when the capture finishes; each detail needs a rendered frame of its own, and is taken only once the Frame Debugger's data names that event.";

                case "editor-focus":
                    return "Bring the editor to the operating system foreground (front), recording the window that had it, or give the foreground back to that window (restore). Unity renders nothing in the background, so canvases, the Inspector and window screenshots stay stale until the editor is in front: front, capture, then restore. Reports the foreground window before and after, as read back. Windows editor only; elsewhere it refuses.";

                case "profiler-get-rendering-stats":
                    return "Live render statistics of the last frame the editor drew - batches, draw and SetPass calls, triangles, vertices, shadow casters, static, dynamic and instanced batches, render texture changes and count, visible skinned meshes, texture memory in use and the screen size - plus whether the editor is active and the graphics device. The statistics only move while the editor renders, which it does only in front (editor-focus). For a measured stretch rather than one frame, use profiler-frames.";

                case "profiler-select-frame":
                    return "Select a buffered profiler frame in the Profiler window, as clicking it in the chart does, so the window's panes show that frame. Reports the selection before and after as the window reads it back (-1 means it was following the newest frame) and that frame's counters. Frame numbers are the window's own Frame label numbers, the same ones every profiler verb takes and reports.";

                case "profiler-save-data":
                    return "Write the captured profiler frames to a file, so a recording survives a domain reload or can be compared against a later one.";

                case "profiler-load-data":
                    return "Load a previously saved profiler capture back into the profiler window, replacing what is held. The way to compare a recording against one taken earlier.";

#if CONDUIT_PROBUILDER
                case "probuilder-shape-create":
                    return "Build one parametric ProBuilder shape in the scene named by the required scene argument (a loaded scene's name, path or opened-scene index), which is how world geometry gets made without a modelling package. optionsJson carries that shape's own parameters plus name, parent (which must be in that same scene), position and pivot (center or corner), and every parameter has a default, so the bare call already produces a usable shape. Each shape reads only what applies to it: cube and prism take size, cylinder takes radius, height, sides and heightCuts, arch takes angle, radius, width, depth and radialCuts, and so on. The object is marked lightmap STATIC by default - a shape left non-static is absent from a bake while still rendering, which reads as a lighting fault rather than a missing flag - so pass static false for geometry that moves. Reports the new object's path, id, counts and which static flags it set.";

                case "probuilder-mesh-get":
                    return "Read a ProBuilder mesh's structure: face, vertex and triangle counts, what is currently selected, under the faces section every face's index, submesh, smoothing group and auto-UV settings, and under the colors section which vertex colours each face carries. Colours are reported in the form probuilder-vertex-color-set TAKES, converted back out of linear storage where the project uses it, so a colour read here can be written straight back. Face INDEXES are how every other probuilder verb addresses geometry, and they shift whenever faces are added or removed, so read them back here after any edit rather than reusing indexes from before it.";

                case "probuilder-select":
                    return "Set which faces a ProBuilder mesh has selected, which is what the face verbs act on when they are not given an explicit list. Takes all, none, or indexes separated by commas or spaces. This is the mesh's own selection, the same one the ProBuilder toolbar shows.";

                case "probuilder-face-extrude":
                    return "Push faces outward along a normal, which is how a flat shape gains depth. distance is in the mesh's local units and may be NEGATIVE to push inward. faces takes all, selected (the default), or an index list. method is vertex-normal (the default, matching ProBuilder's own Extrude Faces action - averaged vertex normals, so adjacent faces stay connected and a curved surface stays joined), face-normal (each face along its own surface direction, adjacent faces still connected) or individual (each face along its own direction and adjacent faces NOT connected, so they split apart). Reports how many faces were added and the counts before and after.";

                case "probuilder-face-delete":
                    return "Remove faces, leaving the mesh OPEN where they were - nothing caps the hole. faces takes all, selected (the default), or an index list. Refuses to delete every face rather than leaving an empty mesh behind; use gameobject-destroy for that.";

                case "probuilder-subdivide":
                    return "Split faces into smaller ones by connecting the midpoints of their edges, which is how a surface gains the density an edit needs before it can be shaped. faces defaults to ALL here rather than to the selection, because subdividing a whole shape is the common case.";

                case "probuilder-uv-set":
                    return "Write a face's AUTO UV settings, which is the panel a person uses rather than moving UVs by hand. settingsJson names only the fields to change: scale and offset as two numbers each, rotation in degrees, fill (Fit, Stretch, Tile), anchor (None, plus the nine edge and corner positions), useWorldSpace, flipU, flipV, swapUV. Every value is validated BEFORE any face is written, so a bad enum name changes nothing. A face using MANUAL UVs is skipped and counted in the report, because auto settings do not reach one - probuilder-uv-project is what puts a face into manual mode.";

                case "probuilder-uv-project":
                    return "Re-project a face's UVs from scratch, which is the fix for texturing that stretches across an extruded or rotated surface. box projects each face from the nearest axis and is the general-purpose choice; sphere wraps them around the mesh centre; planar is the UV editor's Planar Project. This WRITES the coordinates directly, so on UV0 the faces become manual-UV and probuilder-uv-set stops reaching them - project first, then adjust by hand, not the other way round. channel defaults to uv0; uv2 is the LIGHTMAP channel, where the manual-UV flag does not apply and a later probuilder-lightmap-uvs regenerates what you wrote.";

                case "probuilder-uv-split":
                    return "Split the named vertices' UVs into their own island - the UV editor's Split UVs. After it, moving those UVs no longer drags the neighbouring faces' with them, which is what makes a face independently placeable in the texture. vertices defaults to the current selection. It edits UVs only; positions do not move.";

                case "probuilder-uv-collapse":
                    return "Collapse the named vertices' UVs onto ONE coordinate - the UV editor's Collapse UVs. vertices defaults to the current selection. It edits UVs only; positions do not move.";

                case "probuilder-uv-sew":
                    return "Join UVs that sit close together into a continuous island - the UV editor's Sew UVs, and the counterpart to probuilder-uv-split. distance is measured in UV SPACE, so it is a fraction of the 0-1 square rather than a world measurement, and anything further apart than that is left alone - a seam still open afterwards needs a LARGER distance, not a second call. vertices defaults to the current selection. It edits UVs only; positions do not move.";

                case "probuilder-uv-stitch":
                    return "Stitch one face's UVs onto another's along their shared edge - the UV editor's Auto Stitch. Both arguments are single face indexes and the two must SHARE AN EDGE; ProBuilder reports whether it managed it, and a refusal is passed on as a refusal rather than reported as success. It edits UVs only; positions do not move.";

                case "probuilder-uv-flip":
                    return "Mirror the named vertices' UVs - the UV editor's Flip U and Flip V. The axis runs through the centre of the SELECTED UVs' own bounds, so the island flips where it already sits instead of jumping across the UV square, and the UVs are split first exactly as that action does so a flip cannot drag a neighbour along. vertices defaults to the current selection. It edits UVs only; positions do not move.";

                case "probuilder-uv-transform":
                    return "Move, rotate and scale UV coordinates - the UV editor's transform gizmo, which is the one UV action with no callable entry of its own, so the arithmetic is Conduit's and ProBuilder's own ApplyUVs writes the result. settingsJson takes offsetU, offsetV, rotation in degrees, scaleU and scaleV, all optional and all defaulting to no change, so a payload asking for nothing is REFUSED rather than reported as a move. Scale and rotation are taken about the centre of the SELECTED UVs' own bounds so the island changes shape where it sits, and the offset is applied last; that order is fixed. Shared UVs move together unless probuilder-uv-split separated them first. The coordinates are read back afterwards and the reply says how many held.";

                case "probuilder-uv-mode":
                    return "Switch faces between AUTO and MANUAL UVs - the UV editor's Set Auto UV and Set Manual UV. This flag decides whether a UV edit SURVIVES: an auto face recomputes its UVs from the auto panel on every mesh refresh, so switching back to auto DISCARDS hand editing rather than hiding it, and manual stops probuilder-uv-set reaching the faces at all. The reply counts how many read manual before and after, so a switch that changed nothing is visible. faces defaults to the current selection.";

                case "probuilder-uv-fit":
                    return "Fit the named vertices' UVs into the 0-1 square - the UV editor's Fit UVs, which is how an island that has drifted outside the texture is brought back. Fewer than three vertices describe no area and are REFUSED rather than reported as a fit, matching ProBuilder's own action. vertices defaults to the current selection. The coordinates are read back afterwards and the reply says how many held, because an auto-UV face would otherwise have them regenerated away. It edits UVs only; positions do not move.";

                case "probuilder-face-material":
                    return "Assign a material to specific faces, which is how one mesh carries several surfaces without being split into separate objects. Each distinct material becomes a submesh and therefore its own DRAW CALL, so the report names the resulting slot count; keeping it low is what makes a world batch (conduit-rendering-cost.md).";

                case "probuilder-smoothing-set":
                    return "Put faces in a smoothing group, which decides where a surface reads as curved rather than faceted. Faces sharing the same NON-ZERO group are shaded as one continuous surface where they meet; group 0 leaves every shared edge hard. This changes shading only - the geometry and its counts are untouched, so confirm it with a screenshot rather than with a count.";

                case "probuilder-grow-selection":
                    return "Widen the selection to the faces touching it. The optional angle in degrees limits the spread to neighbours within that much of the current faces, so a flat panel grows across itself without climbing over a corner; leave it out to grow regardless of angle.";

                case "probuilder-select-loop":
                    return "Select the FACE loop or ring running through the given faces, which is how a band around a shape gets picked without naming every index - ProBuilder's Select Face Loop and Select Face Ring. loop follows the faces end to end; ring crosses them. Both work from whatever is already selected unless faces are named. The edge counterparts are probuilder-select-edge-loop and probuilder-select-edge-ring, which are DIFFERENT operations rather than the same one addressed another way.";

                case "probuilder-select-edge-loop":
                    return "Select the edge loop running through the given edges - ProBuilder's Select Edge Loop - which is how the band to bevel or extrude gets picked without naming every pair. Edges are hyphenated vertex pairs separated by commas ('0-1, 2-3'), or 'faces:0,1' for every edge of those faces, and default to the current edge selection. reach defaults to whole, taking the entire loop, which is ProBuilder's own default; iterative restricts it to the edges NEIGHBOURING the ones named, so a caller steps outwards one band at a time. A loop needs neighbours with matching topology, so a triangulated surface often has none and that is reported rather than passed off as an empty selection. The result becomes the mesh's edge selection. It changes no geometry.";

                case "probuilder-select-edge-ring":
                    return "Select the edge ring CROSSING the given edges - ProBuilder's Select Edge Ring - which is the parallel-edge band an insert-edge-loop cuts across. Same edge grammar and same reach option as probuilder-select-edge-loop: whole by default, iterative for only the neighbouring edges. The result becomes the mesh's edge selection. It changes no geometry.";

                case "probuilder-select-flood":
                    return "Spread the selection across every face reachable without crossing an edge sharper than the angle, which is how one continuous surface gets picked out of a shape. The default angle is 30 degrees.";

                case "probuilder-bevel-edges":
                    return "Round edges off by an amount in local units, which is what stops geometry reading as raw blocks and gives light something to catch - ProBuilder's Bevel Edges. edges is selected (the default), vertex-index PAIRS written '0-1, 2-3', or 'faces:0,1' for every edge belonging to those faces, which is how a whole block gets its corners taken off in one call. An amount wider than the geometry can take is refused by ProBuilder and reported as changing nothing rather than silently mangling the shape.";

                case "probuilder-insert-edge-loop":
                    return "Run a new edge loop all the way around the ring the named edges belong to - ProBuilder's Insert Edge Loop. This is the standard way to add a controllable division to a box or a cylinder, because the loop follows the topology instead of cutting straight across. The new edges become the mesh's edge selection, so the next verb can act on them without naming pairs. A ring only exists across quads; a triangle fan has none and the call changes nothing.";

                case "probuilder-fill-hole":
                    return "Cap open holes with a new face - ProBuilder's Fill Hole. A hole is a closed loop of OPEN edges, which is what an extrude or a face delete leaves behind. vertices chooses where to search and defaults to ALL of them, so the bare call caps every hole in the mesh; probuilder-select-hole lists them first without changing anything. Winding is read from the loop and falls back to an unordered fit, and a hole it cannot cap is counted rather than silently skipped.";

                case "probuilder-select-hole":
                    return "List every open hole and make its edges the mesh's edge selection, WITHOUT changing geometry - ProBuilder's Select Hole. Each hole is reported with its edges as vertex-index pairs, so it names exactly what probuilder-fill-hole would cap and what probuilder-extrude-edges could pull a rim from. vertices chooses where to search and defaults to all of them.";

                case "probuilder-set-collider":
                    return "Turn geometry into a solid collision volume - ProBuilder's Set Collider. Adds a MeshCollider if the object has none, sets isTrigger false, and tints it with ProBuilder's collider material so it reads as collision in the scene view. ProBuilder's own action also attaches a ColliderBehaviour that switches the Renderer off in play mode; that is plain C# and is STRIPPED from a VRChat upload, so it is deliberately not added - the volume would otherwise look handled while shipping visible. Switch the Renderer off yourself before uploading.";

                case "probuilder-set-trigger":
                    return "Turn geometry into a trigger volume - ProBuilder's Set Trigger. Adds a MeshCollider if the object has none, sets isTrigger true, and forces the collider CONVEX, which is not cosmetic: Unity accepts a concave MeshCollider as a trigger and then never raises an event from it, so a concave trigger fails silently. It also tints the object with ProBuilder's trigger material. ProBuilder's own action attaches a TriggerBehaviour to hide the Renderer in play mode; that is plain C# and is STRIPPED from a VRChat upload, so it is deliberately not added - switch the Renderer off yourself before uploading.";

                case "probuilder-cut":
                    return "Cut a face along a path - ProBuilder's Cut tool, driven with the same data a click gives it, so ProBuilder classifies each point and rebuilds the face itself. points is 'x y z, x y z, ...' in the mesh's LOCAL space with two or more, and goes after the pipe because it carries spaces and commas. Repeat the first point as the last to cut a CLOSED shape out of the face. face defaults to the current selection and must resolve to exactly one, because a cut runs across one face. Points landing ON the face's edges are classified AddedOnEdge and cut between them; an interior path is connected out to the face's own vertices by ProBuilder, so it divides the face too; and a closed path cuts its shape out, leaving that shape plus the ring around it. The reply says how ProBuilder classified each point. Face indexes shift, so re-read them afterwards.";

                case "probuilder-insert-vertex-in-face":
                    return "Add a vertex INSIDE a face and let ProBuilder re-triangulate around it - this is what the Cut tool does for each point you click, so repeating it walks a cut across a surface. face is ONE index and point is three numbers in the mesh's LOCAL space. The extra edges it leaves are deliberate: they hold the new point in place, and merging the surrounding faces to tidy them away is what breaks the geometry (ProBuilder's cut-tool page says the same). A point that does not lie on the face changes nothing.";

                case "probuilder-insert-vertex-on-edge":
                    return "Split an edge at a chosen point, which is how a division lands somewhere exact rather than at even spacing - probuilder-subdivide-edges is the evenly-spaced form and takes many edges at once. edge is a vertex-index pair such as 0-1, and point is three numbers in the mesh's LOCAL space; the reply gives the position the vertex actually landed at, which is the point projected onto the edge.";

                case "probuilder-select-material":
                    return "Select every face drawn with a given material - ProBuilder's Select Material, and the way to find every surface sharing one before retexturing or before cutting a draw call. It changes no geometry, and reports the matching face indexes so the next verb can name them directly. A material no face uses leaves the selection alone rather than clearing it.";

                case "probuilder-vertex-color-set":
                    return "Write a vertex colour onto elements - ProBuilder's Vertex Colors window - which is how a layout gets colour-coded for prototyping without authoring a material per zone. color takes #rrggbb, #rrggbbaa, a name such as red, channels from 0 to 1 as 'r g b', or clear to drop the colour array outright. WHICH elements are named changes the result, not just the reach: a face list writes that face's own vertices and leaves a crisp boundary, while 'vertices:0,1' or 'edges:0-1' also writes their coincident twins so the colour blends outward across every face sharing them - the same split ProBuilder makes between its Face and Vertex edit modes. elements defaults to the whole mesh. In a LINEAR colour space project the stored value is the linear form, matching ProBuilder's own palette. Only a material whose shader reads vertex colour displays them, and the reply names the one this object draws with.";

                case "probuilder-select-vertex-color":
                    return "Select every face carrying a vertex colour found on the ones named - ProBuilder's Select by Colors, which is how a colour-coded zone is found again after the fact. A face matches when ANY of its vertices carries one of those colours, so a face straddling a boundary is included. Colours are compared quantised to 8 bits per channel, as ProBuilder's own action compares them, so two closer together than 1/255 count as one. A mesh carrying no colour array at all is reported rather than matched against, because the comparison has no meaning there; probuilder-vertex-color-set assigns them. It changes no geometry.";

                case "probuilder-select-smoothing-group":
                    return "Select every face sharing a smoothing group with the ones named - ProBuilder's Select Smoothing Group, which is how a curved run is found again after the fact. Faces in NO group match nothing, and the call says so rather than selecting everything; probuilder-smoothing-set is what puts faces in a group. It changes no geometry.";

                case "probuilder-offset-elements":
                    return "Move the vertices behind whichever elements are named - ProBuilder's Offset Elements, one verb covering all three element kinds. elements is a vertex list, 'faces:0,1' or 'edges:0-1, 2-3'. The offset is three numbers in the mesh's LOCAL space, matching the other vertex verbs. Only positions move, so counts and indexes still hold afterwards - but collision does not follow, so probuilder-colliders-rebuild is still needed.";

                case "probuilder-shrink-selection":
                    return "Pull a face selection in by dropping the faces on its perimeter - ProBuilder's Shrink Selection, and the counterpart to probuilder-grow-selection. It changes no geometry. Shrinking a selection whose every face is on its own perimeter would empty it, so that is refused and the selection is left alone rather than cleared.";

                case "probuilder-face-merge":
                    return "Merge several faces into ONE face, which is how an over-dense surface gets simplified after editing. Needs at least two faces. The result is a single n-gon, so run probuilder-triangulate afterwards if something downstream needs triangles.";

                case "probuilder-face-detach":
                    return "MOVE faces off the surrounding surface so they can be manipulated on their own, matching ProBuilder's Detach Faces action. to is gameobject (the default, as in ProBuilder: a separate object holding only those faces, while the original keeps the rest) or submesh (kept inside this object). Detaching EVERY face to an object is refused, because it would leave the original empty. Face indexes shift on both meshes afterwards.";

                case "probuilder-face-duplicate":
                    return "COPY faces, leaving the originals untouched, matching ProBuilder's Duplicate Faces action. to is gameobject (the default, as in ProBuilder: a separate object holding only the copies) or submesh (the copies land inside this object). For giving an existing one-sided surface a back rather than copying it, probuilder-face-duplicate-flip is the verb.";

                case "probuilder-face-duplicate-flip":
                    return "Add a copy of each named face in place with its normals REVERSED, which is what makes a one-sided wall or banner visible from behind instead of vanishing when walked around. It creates no object and no submesh, which is what separates it from probuilder-face-duplicate.";

                case "probuilder-extrude-edges":
                    return "Pull new geometry out of OPEN edges, which is how a flat plane grows a wall and how a hole gains a rim - ProBuilder's Extrude Edges. edges is selected (the default) or vertex-index PAIRS written '0-1, 2-3', because a comma alone could not separate the pairs from the indexes inside them. Options: asGroup (default true, so a run of edges lifts as one connected skirt instead of each edge pulling its own loose quad) and allowNonManifold (default false - an edge already between two faces has nowhere to extrude unless this is set). An edge that cannot extrude leaves the mesh unchanged and says so rather than reporting success.";

                case "probuilder-subdivide-edges":
                    return "Add vertices ALONG edges without changing the shape, which is what gives a later extrude, bevel or loop somewhere to grip - ProBuilder's Subdivide Edges. subdivisions is the cuts per edge, 1 by default and refused above 32, which is the range ProBuilder's own editor allows. edges is selected (the default) or vertex-index pairs written '0-1, 2-3'.";

                case "probuilder-collapse-vertices":
                    return "Merge several vertices into ONE, which is how a narrow face is reduced to an edge and how a seam is closed - ProBuilder's Collapse Vertices. to is center (the default, matching ProBuilder) or first, which collapses onto the first named vertex rather than their shared middle. Fewer than two is refused, and the reply names the surviving vertex index.";

                case "probuilder-connect-vertices":
                    return "Run a new edge ACROSS a face between vertices sitting on it, splitting that face in two - ProBuilder's Connect Vertices. This is how a face gains a cut line without the Cut tool. The vertices have to share a face and not already be joined by an edge; when nothing can be connected the mesh is left unchanged and the reply says so.";

                case "probuilder-flip-face-edge":
                    return "Turn the dividing edge inside a QUAD the other way, re-triangulating it without moving a single vertex - ProBuilder's Flip Face Edge. It is the fix for a quad whose triangle split creases a surface or shades it wrong. A triangle or an n-gon has no dividing edge, so those count as unflipped rather than failing the call.";

                case "probuilder-flip-normals":
                    return "Reverse the winding of faces, the fix for geometry that renders inside out or is invisible from the side you are standing on. Counts do not change, so judge the result from a screenshot rather than from the report.";

                case "probuilder-conform-normals":
                    return "Make neighbouring faces agree on a facing direction, taking the direction most of them already face. Use it when part of a shape came out inverted after heavy editing and flipping face by face would be guesswork. Defaults to the WHOLE mesh.";

                case "probuilder-recalculate-normals":
                    return "Recompute normals and tangents across the mesh, which is what fixes shading that went wrong during editing. Smoothing groups decide which edges come out soft, so set those first with probuilder-smoothing-set; this only rebuilds from what they already say.";

                case "probuilder-to-quads":
                    return "Pair triangles back into quads, which is what keeps a mesh workable - most operations behave better on quads, and a quad mesh subdivides predictably. Triangles with no partner are left alone rather than forced. Defaults to the whole mesh.";

                case "probuilder-triangulate":
                    return "Split faces into triangles, which some exporters and operations require. Not reversible by re-running probuilder-to-quads, since that can only pair triangles that still match up - so keep a quad version if the shape still needs editing.";

                case "probuilder-vert-weld":
                    return "Collapse vertices that sit within a radius of each other into one, which closes the seams that extruding and detaching leave behind. Reports the vertex count before and after, and says plainly when nothing was close enough rather than claiming a weld that did not happen. Defaults to the whole mesh.";

                case "probuilder-vert-split":
                    return "Separate vertices that currently share a position, which is what lets two faces move apart at a shared corner. The opposite of probuilder-vert-weld.";

                case "probuilder-vert-translate":
                    return "Move vertices by an offset in the mesh's LOCAL space, which is how a generated shape gets pushed into something of its own. A face selection implies its vertices, so selecting faces and translating does what it looks like. Counts do not change - judge the shape from a screenshot. probuilder-vert-set-position sets an exact position instead of an offset.";

                case "probuilder-vert-set-position":
                    return "Move a vertex to an EXACT position - ProBuilder's Vertex Position Editor - rather than by an offset, which is what probuilder-vert-translate does. It writes the whole COINCIDENT GROUP behind the vertex named, because moving one raw vertex and leaving its twins behind tears the surface open; the reply says how many raw positions that was. vertices takes ordinary vertex indexes, the same ones every other vertex verb and probuilder-mesh-get use. space defaults to local, the mesh's own coordinates; world converts through the object's transform, matching the editor window's World Space toggle. Naming vertices at more than one position is REFUSED rather than collapsing them all onto one point - probuilder-collapse-vertices does that deliberately. Counts do not change, so indexes still hold.";

                case "probuilder-xray":
                    return "Read or set ProBuilder's X-Ray view, which fades the selection handles that geometry is hiding. Passing nothing reports the current state. It is EDITOR-WIDE rather than per object, so this verb takes no target, and it PERSISTS across sessions as a preference. It changes what a SCENE screenshot shows only while ProBuilder is actually drawing handles - an element selection with its editor open - and never changes the mesh or how the object renders in a build.";

                case "probuilder-edge-bridge":
                    return "Build a face spanning two OPEN edges, which is how two separate openings get joined into one surface. Each edge is written as two vertex indexes, e.g. 12,13, read from probuilder-mesh-get. An edge with a face on each side is not open and is refused.";

                case "probuilder-edge-connect":
                    return "Insert edge loops through faces by connecting the midpoints of their edges, adding divisions where an edit needs them. This is the same operation probuilder-subdivide runs over a whole shape, aimed at a chosen few faces instead.";

                case "probuilder-set-pivot":
                    return "Move the object's pivot, which decides what it rotates and scales around. where takes center (the mesh's middle), selection (the middle of the selected faces), or three numbers as a WORLD position. The geometry does not move: the vertices are rewritten to compensate, so the shape looks identical and only its rotation and scaling behave differently.";

                case "probuilder-freeze-transform":
                    return "Bake the transform's scale into the mesh, leaving localScale at 1 and the shape unchanged on screen. A non-uniform scale on a renderer skews lighting and breaks batching, so freezing it is what makes scaled geometry behave before a bake or an upload.";

                case "probuilder-lightmap-uvs":
                    return "Rebuild the mesh for shipping and, by default, GENERATE ITS LIGHTMAP UVS. Geometry with no UV2 is skipped by a bake even when it is marked static, so this and the static flags are both needed before it lights - the report names the resulting UV2 count so the difference is checkable. Pass nolightmap to skip the UV generation.";

                case "probuilder-colliders-rebuild":
                    return "Rebuild the object's colliders to match its current geometry. A collider does NOT follow edited geometry on its own, so a shape that was extruded or subdivided keeps its old collision until this runs - which is why a player walks through a wall that plainly looks solid. Says so plainly when the object has no collider at all.";

                case "probuilder-cleanup":
                    return "Remove degenerate triangles and unused vertices, which accumulate through extruding, merging and deleting. Reports the counts removed, and says nothing needed removing rather than claiming a clean-up that did nothing.";

                case "probuilder-validate":
                    return "Report a mesh's faults WITHOUT changing anything: degenerate triangles, non-contiguous faces, material slot count and whether the first is null, whether lightmap UVs exist, and whether the object contributes GI. Run it before a bake or an upload - the two silent failures it catches are geometry with no UV2 and geometry that is not marked static, both of which render fine and light as though they were not there.";

                case "probuilder-merge-objects":
                    return "Merge several ProBuilder objects into the first one, which is how a draw-call count comes down once a set of pieces is finished. ProBuilder splits the result when it would exceed a mesh's vertex limit, and the report says how many objects came back. The SOURCE objects still exist afterwards - remove them with gameobject-destroy once the result looks right.";

                case "probuilder-mirror-objects":
                    return "Mirror an object across one or more axes, given as three numbers such as -1 1 1 for X. duplicate (the default) leaves the original and makes a mirrored copy; inplace mirrors the object itself. A negative axis reverses winding and ProBuilder corrects for it, so check the result with a screenshot rather than assuming which way it now faces.";

                case "probuilder-export":
                    return "Write the mesh out, leaving the scene object alone and still ProBuilder-editable, so a finished shape becomes reusable without giving up the ability to edit it. mesh (the default) and prefab write a PROJECT ASSET and derive their own path. obj, ply and the two stl forms write an INTERCHANGE FILE for another tool and so need path given - obj carries materials and writes its .mtl library beside it, ply carries vertex colours, and stl carries triangles alone with no UVs, materials or colours at all. stl-binary is several times smaller than stl-ascii for the same geometry. A path inside the project is imported afterwards and one outside it is not, and the reply says which happened. ProBuilder's own export goes through a save dialog, which would block the editor; these drive the exporters directly instead.";

                case "probuilder-strip":
                    return "Remove the ProBuilder components, leaving an ordinary MeshFilter and MeshRenderer carrying the geometry as it stands. The shape still renders and still builds, but NO probuilder verb can reach it again - so strip only when a shape is finished, and prefer probuilder-export when a reusable copy is what is wanted.";

                case "probuilder-bezier-create":
                    return "In the scene named by the required scene argument (a loaded scene's name, path or opened-scene index), sweep a tube along a bezier spline through the points given - ProBuilder's Bezier Shape - which is how a pipe, cable, handrail or curved tunnel gets built without modelling it. points is 'x y z, x y z, ...' in LOCAL space with two or more, and goes after the pipe because it carries spaces and commas. Tangents are COMPUTED so the curve passes THROUGH every point rather than near it; the BezierShape component keeps them, so an individual handle can be adjusted afterwards with gameobject-component-get and -modify. Options: radius (0.5), columns (16, the sides around the tube, minimum 3), rows (8, the divisions along it), smooth (true), closeLoop (false, which joins the last point back to the first), material, static, parent (which must be in that same scene), position. Lightmap UVs are NOT generated: run probuilder-lightmap-uvs before baking.";

                case "probuilder-poly-create":
                    return "Build geometry from a drawn FOOTPRINT rather than a parametric shape, in the scene named by the required scene argument (a loaded scene's name, path or opened-scene index): name the object, pass the outline corners as 'x y z, x y z, ...' after the pipe, and give an extrude depth, and a solid comes back. This is how an L-shaped room, a floor plan or any non-box plan gets made. Options: extrude (depth, default 1), flipNormals, holes (an ARRAY of point strings in that same grammar, each cut clean through the solid, which is how a doorway or a window is made), material (asset path), static. Corners must not be collinear or self-intersecting and every hole must sit entirely inside the outline, or it refuses and creates nothing. A shape built WITH holes then reports its two capped faces as non-contiguous in probuilder-validate, which is correct output rather than a fault. Lightmap UVs are not generated here: run probuilder-lightmap-uvs before baking.";

                case "probuilder-ize":
                    return "Convert an ORDINARY mesh into editable ProBuilder geometry in place, which is what makes an imported model or a Unity primitive reachable by every other probuilder verb. Options: quads (rebuild triangles into quads, default true), smoothing (derive smoothing groups from the normals, default true), smoothingAngle (the degree threshold, default 1). Refuses an object that is already ProBuilder geometry rather than importing over the work it carries, and refuses any topology other than triangles and quads. The source mesh ASSET is untouched - the MeshFilter is pointed at a new mesh - and lightmap UVs do not survive the import, so run probuilder-lightmap-uvs before baking.";

                case "probuilder-fit-to-size":
                    return "Scale the GEOMETRY so its bounding box measures an exact size, leaving the transform scale alone. Use this for real-world dimensions - '4 3 0.2' for a wall - because scaling the transform instead distorts children and colliders. On a Shape-tool object it sets the authored size and regenerates the shape, since vertices written there would be discarded on its next rebuild; ProBuilder clamps a zero X or Z to 0.001 so a collider can still be built, and the reply reports the MEASURED result rather than what was asked for. Collision does not follow geometry: run probuilder-colliders-rebuild.";

                case "probuilder-lightmap-static":
                    return "Turn the ContributeGI static flag on (the default) or off for one object. Geometry without it renders perfectly and is SKIPPED by a lightmap bake, so the fault reads as a lighting problem rather than a flag - probuilder-validate is what reports it and this is what fixes it. probuilder-shape-create already sets it on what it creates, so this is for geometry that arrived some other way, such as through probuilder-ize. Says so when the flag already sits in the wanted state rather than claiming a change, and warns when the object still has no lightmap UVs to bake into.";

#endif

                case "entry":
                    return "Read specific console entries IN FULL by number, e.g. 'entry 12' or 'entry 3,7 12'. Numbers come from summary, head, tail or grep and are absolute in the current buffer (#1 is oldest), staying valid until the console is cleared. This is the cheap way to read a busy console: triage with summary or grep, then pay for stack traces only on the entries that earned it. Out-of-range numbers are reported individually and do not fail the call.";

                case "grep":
                    return "List console entries whose MESSAGE contains some text, as one numbered line each, e.g. 'grep NullReference'. Case-insensitive, and it deliberately ignores stack frames so searching for a type name finds the entries ABOUT it rather than every entry whose call path mentions it. Pair with 'entry <n>' to read the matches in full.";

                case "gameobject-duplicate-multi":
                    return "Duplicate one GameObject N times through the same editor path as Ctrl+D, so prefab links, components and overrides survive. Every copy is taken from the ORIGINAL, not from the previous copy, so all N are identical siblings. Returns every new object WITH its instance id, so a following array wire needs no second lookup, and leaves the whole new set selected. Capped at 512 per call.";

                case "gameobject-component-modify":
                    return "Modify serialized fields on a component from a flat {\"field\": value} JSON payload. Writes through SerializedProperty, so it lands on MonoBehaviours, UdonSharp proxies, AND native engine components (Collider.m_IsTrigger, Rigidbody, RectTransform values); object-reference fields resolve from an instanceID/guid/path value, and ARRAYS of object references are written element by element from a JSON array (e.g. {\\\"_craftRoots\\\": [{\\\"instanceID\\\":123}, \\\"Assets/Thing.prefab\\\", null]}) - the whole array resolves before anything is written, so one bad element aborts with nothing applied. Arrays of plain values (numbers, booleans, strings, enums, vectors) are written element by element the same way. Every named field is read back after the write and reported as set (old -> new), already-at-value or not applied, with a warning for an unknown field and for any field the write changed that the payload did not name - a response that says nothing changed means nothing changed. Still NOT for UnityEvents (use unity-event-set-string) or dotted property paths.";

                case "menu":
                    return "Execute a Unity Editor menu item by its path (e.g. 'File/Save Project', 'Assets/Reimport').";

                case "assets-replace-references":
                    return "Repoint every reference to one asset onto another across the scene named by the required scene argument (a loaded scene's name, path or opened-scene index), or across the open prefab stage when one is open, so retiring an asset costs one call instead of one set-reference per referencing field. The old asset and its sub-assets count as one target, and each field receives whichever representation of the new asset matches the type that field demands, so a baked Sprite asset swaps cleanly onto an imported PNG's Sprite sub-asset. Every hit resolves before anything is written: a field the new asset cannot satisfy refuses the whole call, names what it does offer, and changes nothing. Run assets-find-references first to preview what will change.";

                case "assets-orphans":
                    return "Report-only sweep listing assets under a folder that nothing in the scanned tree references by GUID, so a cleanup pass has something to review instead of a hand-rolled grep per suspect. It NEVER deletes and never calls anything safe to delete: unreferenced is not unwanted, work in progress legitimately sits unassigned, and a GUID scan cannot see a Resources load or a path built at runtime - those are reported separately as out of scope, along with scenes, which are roots rather than dependencies. Pass a scanRoot to narrow the reference sweep when the whole project is more than you need.";

                case "assets-find-references":
                    return "Reverse-reference lookup: returns every GameObject in one scene (the required scene argument: a loaded scene's name, path or opened-scene index), or in the open prefab stage when one is open, whose components reference the given asset - material, sprite, mesh, clip, script - with the owning component and serialized field. Unity has no built-in reverse-dependency search, so use this to answer 'what uses this asset' instead of dumping the scene or grepping .unity/.prefab files for a GUID.";

                case "gameobject-component-list":
                    return "List the components actually ON one GameObject (type, instance id, enabled state) - the per-object companion to gameobject-component-list-all, which lists component TYPES across the project. Use it to see what a target has before gameobject-component-get / -modify / set-reference instead of guessing the component name.";

                // Spike-hunting workflow: record-start (bracket a reproduction) -> record-stop (find the spike
                // frames + GC/draw-call profile) -> frame-hierarchy (attribute the worst frame to a script).
                // Notes are keyword-rich so a search for "frame spike", "lag", "stutter", "GC", "draw calls",
                // or "which script is slow" surfaces them among the many bridge tools.
                case "profiler-start":
                    return "Enable Unity profiling and open the Profiler window. Answer it with profiler-stop when the measurement is done.";

                case "profiler-stop":
                    return "Disable Unity profiling AND turn the Profiler window's Record off, reporting both states as read back. Record must not be left on: the editor layout saves it and turns recording back on every time Unity opens.";

                case "profiler-get-status":
                    return "Read whether profiling is enabled ('enabled') and whether the Profiler window's Record is on ('recording'), plus binary-log and play-mode context. Both must read false before a measurement is reported as finished.";

                case "profiler-record-start":
                    return "Start a rolling per-frame capture to hunt FRAME SPIKES / hitches / stutter / lag. Records main-thread ms, GC allocation, draw calls, batches, triangles and vertices into a ring buffer (default 2000 frames, which only the profiler-record-stop summary reads; marks, profiler-compare and every per-frame read reach just the Profiler's own history, reported as historyFrames and at most 2000) and turns the Profiler window's Record on for attribution, remembering the profiler and Record state it found. Workflow: enter play mode, call this, REPRODUCE the spike (e.g. stop a song / trigger the event), then profiler-record-stop. Counters only advance in play mode.";

                case "profiler-record-stop":
                    return "Stop the active capture, put the profiler and the Profiler window's Record back as profiler-record-start found them, and report the main-thread time distribution (mean/median/p95/p99/max, fps) plus the worst SPIKE frames, each annotated with its GC allocation and draw-call/geometry load - so you can tell instantly whether a spike is GC-driven (allocation/Destroy/UnloadUnusedAssets) or rendering. The captured frames stay readable afterwards: pairs with profiler-frame-hierarchy to attribute the heaviest frame to a specific script/marker.";

                case "profiler-frame-hierarchy":
                    return "Attribute a frame spike to a SPECIFIC SCRIPT/marker. Scans the most recent buffered frames and picks the worst by REAL (shippable) cost - self-time with editor-only/idle noise removed (EditorLoop, Mono.JIT, Profiler.*, vsync/gfx waits), so the in-editor EditorLoop overhead can't bury the real spike. Returns that frame's real-work markers by exclusive self time (UdonBehaviour.ManagedUpdate, TMP GenerateText, GC.Collect, a U# class name) with per-marker GC bytes and calls, plus realCostMs vs frameTimeMs and the filtered excludedMarkers for transparency. Wait markers (Idle, WaitForJobGroupID, Semaphore.WaitForSignal, Gfx.WaitForGfxCommandsFromMainThread) and every Profiler.* and GPUProfiler.* marker count as noise, and anything nested under EditorLoop is filed under it. Needs the frame buffer populated (profiler-start or profiler-record-start) and a recent reproduction. Optional args: [topN] [threadIndex] [frame]: topN up to 1000, with realMarkerCount and topNReturned saying how many real markers the frame had and how many came back; threadIndex 0 is the main thread, and profiler-threads lists the others; a frame number reports that frame instead of the worst one.";

                default:
                    return null;
            }
        }

        /// <summary>
        /// Renders the effective command list as human-readable help text.
        /// </summary>
        /// <returns>Formatted command help text.</returns>
        private static string RenderHelp()
        {
            var sb = new StringBuilder();
            sb.Append("Commands (").Append(ProjectKindLabel()).Append("):");
            foreach (string sig in EffectiveCommandSignatures())
            {
                sb.Append(Environment.NewLine).Append("  ").Append(sig);
                int space = sig.IndexOf(' ');
                string note = CommandNote(space < 0 ? sig : sig.Substring(0, space));
                if (note != null)
                {
                    sb.Append(Environment.NewLine).Append("      -> ").Append(note);
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Renders the effective command list as machine-discoverable JSON.
        /// </summary>
        /// <returns>JSON array of command verbs, arguments, and optional notes.</returns>
        private static string RenderCommandsJson()
        {
            var signatures = EffectiveCommandSignatures();
            var sb = new StringBuilder();
            sb.Append('[');
            for (int i = 0; i < signatures.Count; i++)
            {
                string sig = signatures[i];
                int space = sig.IndexOf(' ');
                string v = space < 0 ? sig : sig.Substring(0, space);
                string a = space < 0 ? string.Empty : sig.Substring(space + 1);
                if (i > 0) sb.Append(',');
                sb.Append("{\"verb\":\"").Append(JsonEscape(v)).Append("\",\"args\":\"").Append(JsonEscape(a)).Append('"');
                string note = CommandNote(v);
                if (note != null)
                {
                    sb.Append(",\"note\":\"").Append(JsonEscape(note)).Append('"');
                }
                sb.Append('}');
            }
            sb.Append(']');
            return sb.ToString();
        }
            /// <summary>
        /// Routes the Catalog verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchCatalog(string verb, string command, string[] parts)
        {
            if (verb == "unity-tool-list")
            {
                return UnityToolListCommand(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
