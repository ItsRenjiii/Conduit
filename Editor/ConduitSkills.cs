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
using UnityEditor;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Installs the Conduit conventions as agent SKILLS beside the MCP server entry.
    ///
    /// Why skills and not just the injected instructions: the MCP `instructions` field is paid for by
    /// EVERY session in every project, so only the router belongs there. A skill costs nothing until the
    /// agent's own harness matches its description and loads it, which also removes the weakest link in
    /// the old design - an instruction telling the agent to go and read a file, which agents skip.
    ///
    /// Each generated SKILL.md INLINES its whole subject document rather than referencing it. A skill body
    /// that says "now read conduit-udonsharp.md" reintroduces exactly the compliance step the skill was
    /// meant to remove, so the trigger must BE the load.
    /// </summary>
    internal static class ConduitSkills
    {
        #region Subject Table

        /// <summary>One installable skill: the source document and the frontmatter that triggers it.</summary>
        private sealed class Subject
        {
            /// <summary>Skill folder and frontmatter name, e.g. "conduit-udonsharp".</summary>
            public string Name;

            /// <summary>Source document file name inside the Conduit folder.</summary>
            public string File;

            /// <summary>Frontmatter description - this is what decides whether the skill fires.</summary>
            public string Description;
        }

        /// <summary>
        /// Every subject document shipped as a skill. Descriptions carry BOTH a positive and a negative
        /// trigger: a description that only says when to use a skill fires on near misses, and a skill
        /// that fires on everything is the injected-instructions problem again with extra steps.
        /// </summary>
        private static readonly Subject[] Subjects =
        {
            new Subject
            {
                Name        = "conduit-code-style",
                File        = "conduit-code-style.md",
                Description = "House code style for this Unity project: correctness and hygiene, XML documentation "
                            + "requirements, naming, file layout and structure, and gated logging. Use when writing or "
                            + "editing ANY C#, UdonSharp, shader, or editor code here, however small the change. Do not "
                            + "use for questions that do not change code.",
            },
            new Subject
            {
                Name        = "conduit-project-workflow",
                File        = "conduit-project-workflow.md",
                Description = "How to scope and carry out a change in this project: surgical edits, reuse before adding, "
                            + "prefab-ready hierarchy, behaviour placement, editor builders, and how to verify visually "
                            + "with the scene view. Use when starting any multi-step change to a scene, prefab, or asset "
                            + "system. Do not use for a single-file code edit.",
            },
            new Subject
            {
                Name        = "conduit-compile-console",
                File        = "conduit-compile-console.md",
                Description = "Compiling and reading the Unity console through Conduit: the end-of-change flow, why "
                            + "on-screen errors are usually stale, which verbs block and must not be polled, and what "
                            + "each compile trigger is for. Use when triggering a compile, reading the console, "
                            + "interpreting errors after an edit, or finishing a change. Do not use for authoring code.",
            },
            new Subject
            {
                Name        = "conduit-udonsharp",
                File        = "conduit-udonsharp.md",
                Description = "UdonSharp behaviour rules: creating behaviours, adding U# components, proxy versus backing "
                            + "UdonBehaviour traps, language limits such as blocked static fields and compile-time field "
                            + "initializers, runtime textures, VRCUrl and string-download rate limits, persistence, and "
                            + "THE UDON SURFACE OF THE VRC_ WORLD COMPONENTS - what a behaviour can actually call on a "
                            + "pickup, an object sync or the player's camera. Use when writing or editing an UdonSharp "
                            + "behaviour, adding a U# component, debugging one, or asking what Udon can call on a world "
                            + "component. Do not use for sync, ownership or network events, which are conduit-networking, "
                            + "or for authoring those components in the scene, which is conduit-world-components.",
            },
            new Subject
            {
                Name        = "conduit-networking",
                File        = "conduit-networking.md",
                Description = "VRChat instance networking: choosing a sync mode and its byte budget, the syncable type "
                            + "list, ownership transfer and why SetOwner arbitrates nothing, OnOwnershipRequest running "
                            + "on both clients at once, what a late joiner actually receives and the side-effect replay "
                            + "that causes, network events and the underscore and NetworkCallable security model, and "
                            + "instantiation. Use when syncing a variable, transferring ownership, sending a network "
                            + "event, or fixing a world that misbehaves only with a second player. Do not use for "
                            + "UdonSharp language rules, which are conduit-udonsharp.",
            },
            new Subject
            {
                Name        = "conduit-vrctween",
                File        = "conduit-vrctween.md",
                Description = "VRCTween, Udon's native tweening: the complete creation API across Transform, UI, "
                            + "Renderer, audio and virtual values, VRCTweenHandle state and control, ease and loop types, "
                            + "callbacks and timescale, ChangeEndValue reuse, validation behaviour, the networking "
                            + "pattern, and why DOTween ships in the SDK but is not callable from Udon. Use when animating "
                            + "anything over time, or before hand-rolling a Lerp in Update. Do not use for AnimationClips "
                            + "and AnimatorControllers, which are conduit-animator.",
            },
            new Subject
            {
                Name        = "conduit-audio",
                File        = "conduit-audio.md",
                Description = "World audio: why Unity Play Mode spatializes through the old ONSP backend while VRChat "
                            + "ships Steam Audio, pairing an AudioSource with VRC_SpatialAudioSource without letting its "
                            + "louder defaults through, the voice setters and their matching getters that let a voice zone "
                            + "save and restore instead of guessing, the set-only avatar-audio family, why there is no "
                            + "world-wide voice block on the scene descriptor at all, what Load Type costs a clip before "
                            + "it plays, and the in-client Audio Sources debug page. Use when adding or tuning audio, "
                            + "importing a clip, writing a voice zone, or judging how a world sounds. Do not use for "
                            + "bundled video, which is conduit-video.",
            },
            new Subject
            {
                Name        = "conduit-vrchat-ui",
                File        = "conduit-vrchat-ui.md",
                Description = "uGUI INSIDE A WORLD - what a player sees and touches on a canvas: the setup a canvas needs "
                            + "before it responds at all, creating and reparenting canvas objects, text and font traps, "
                            + "masks and stencil copies, ScrollRect sizing, button and hover states, input-field "
                            + "behaviour including VRCUrlInputField, and showing another camera's view behind a "
                            + "head-following HUD: the layer split between that camera and the player's own screen camera, "
                            + "whose culling mask, field of view and aspect Udon can read and set. Use when building or "
                            + "fixing world-space UI, a HUD over a camera view, or when a "
                            + "control renders correctly and does nothing. Do not use for an INSPECTOR built with UI "
                            + "Toolkit, which is conduit-inspector-examples and conduit-editors - \"UI\" names two "
                            + "unrelated things in this project - nor for driving a control in play mode, which is "
                            + "conduit-vrc-input.",
            },
            new Subject
            {
                Name        = "conduit-build-validation",
                File        = "conduit-build-validation.md",
                Description = "VRChat Build Panel validations, and shipping a world: the three SDK severities and "
                            + "which of them blocks a build, why an Auto Fix button is only a callback and carries no "
                            + "safety contract, the shipped fixes that rewrite project-wide settings or authored audio "
                            + "and materials, the 100 MB Android ceiling and the absence of any world performance "
                            + "ranking, and why an existing instance keeps running the old build after an upload. Use "
                            + "when answering a Build Panel question, before pressing Auto Fix, or when uploading. Do "
                            + "not use for runtime behaviour code.",
            },
            new Subject
            {
                Name        = "conduit-player",
                File        = "conduit-player.md",
                Description = "The VRCPlayerApi surface: which identity flags are evaluated per-client and must never "
                            + "be synced, movement setters and the matching getters that let a zone restore what it "
                            + "found, why hand-mounted objects anchor to tracking data rather than avatar bones, "
                            + "player tags being local-only with an unreadable bulk query, language, reaching into "
                            + "another player's PlayerObject, the per-player events beyond join and leave, why a station "
                            + "tells you neither WHO sits in it nor WHETHER anyone does, and the drone API a world can actually "
                            + "drive. Use when reading a player property, changing movement or velocity, handling a "
                            + "station or a drone, or treating players differently from one another. Do not use for voice "
                            + "and avatar audio, which are conduit-audio, or for ownership, which is conduit-networking.",
            },
            new Subject
            {
                Name        = "conduit-world-components",
                File        = "conduit-world-components.md",
                Description = "Authoring the VRC_ world components: why a non-whitelisted component silently does nothing "
                            + "and Cloth is disabled on Android, why Maximum capacity is a hard limit while Recommended "
                            + "only hides the instance from listings, what a pickup and a station each need beside them "
                            + "to work at all, why a mirror costs four times the rendering in VR, portals having to sit at "
                            + "the scene root, the camera dolly and its Collect Paths and Points step, placement zones, "
                            + "billboards and velocity trackers, and why avatar-side "
                            + "raycasts make your collider and layer setup a contract with other people's avatars. Use "
                            + "when adding or configuring a VRC_ component, or before claiming a Unity component works in "
                            + "a world. Do not use for their Udon scripting surfaces, which are conduit-udonsharp, "
                            + "conduit-player and conduit-dynamics.",
            },
            new Subject
            {
                Name        = "conduit-layers",
                File        = "conduit-layers.md",
                Description = "Tags and layers, layer masks and the collision matrix: defining a tag or naming a layer "
                            + "slot and why overwriting an occupied one needs an explicit confirm, which layers VRChat "
                            + "reserves and restores at upload, why user layers 22-31 must be referenced by number rather "
                            + "than name, and how a failed name lookup silently produces a mask pointing at layer 31. Use "
                            + "when defining a tag or layer, assigning one, writing a layer mask, or changing collision. "
                            + "Do not use for sizing or placing the collider itself, which is conduit-scene-geometry.",
            },
            new Subject
            {
                Name        = "conduit-dynamics",
                File        = "conduit-dynamics.md",
                Description = "VRChat Dynamics in worlds: why every runtime field write needs "
                            + "ApplyConfigurationChanges before it takes effect, Contacts having only enter and exit "
                            + "events with CalculateProximity for held state, the silent 16-tag ceiling, PhysBone grab "
                            + "and pose events and the absence of collider callbacks, and the six VRC Constraints with "
                            + "their real Udon surface including the source helper methods that replace the Sources list, "
                            + "and what Advanced Proximity Parameters and Material Contacts do and do not give Udon. Use "
                            + "when adding a "
                            + "Contact, PhysBone or VRC Constraint, or before concluding one cannot do something. Do not "
                            + "use for plain Unity colliders and triggers, which are conduit-scene-geometry.",
            },
            new Subject
            {
                Name        = "conduit-scene-geometry",
                File        = "conduit-scene-geometry.md",
                Description = "Measuring and placing colliders, trigger volumes and bounds against model geometry: "
                            + "reading a mesh's localAABB, applying an imported model's root rotation so local height does "
                            + "not become world depth, and diagnosing a trigger volume that never fires. Use when sizing or "
                            + "positioning a collider or trigger, or when a volume is not detecting what is inside it. Do not "
                            + "use for UI RectTransform layout, which is conduit-vrchat-ui.",
            },
            new Subject
            {
                Name        = "conduit-probuilder",
                File        = "conduit-probuilder.md",
                Description = "Modelling world geometry with ProBuilder through Conduit's probuilder-* verbs: that what "
                            + "it authors ships as an ordinary mesh and works in a VRChat world with no export or "
                            + "conversion, that face indexes shift after every operation that adds or removes faces, and "
                            + "that a shape needs BOTH generated lightmap UVs and the static flags before it bakes while "
                            + "neither failure is visible. Use when building or editing world geometry in the editor, or "
                            + "when asked whether ProBuilder works in a world. Do not use for what a mesh costs to render, "
                            + "which is conduit-rendering-cost, or for sizing a collider against geometry, which is "
                            + "conduit-scene-geometry.",
            },
            new Subject
            {
                Name        = "conduit-navmesh",
                File        = "conduit-navmesh.md",
                Description = "Which NavMesh, AI and physics-query APIs Udon actually exposes: the 28 AI and NavMesh "
                            + "types including runtime navmesh data, query filters and triangulation, every physics "
                            + "cast family in plain, All and NonAlloc forms, and how to re-derive the list after an SDK "
                            + "update. Use before concluding a NavMesh, agent or physics-cast API is unavailable to "
                            + "Udon, or before hand-rolling a substitute for one. Do not use for pickup and player "
                            + "input handling, which is conduit-vrc-input, or for collider sizing, which is "
                            + "conduit-scene-geometry.",
            },
            new Subject
            {
                Name        = "conduit-performance",
                File        = "conduit-performance.md",
                Description = "Udon's cost model and writing for it: why Udon runs orders of magnitude slower than "
                            + "plain C#, how every public method taxes that behaviour's event dispatch, partial classes "
                            + "splitting a large behaviour across files at zero runtime cost, why a disabled component "
                            + "beats an Update that returns early, slicing heavy work across frames with Stopwatch, and "
                            + "the string-keyed APIs that fail silently. Use before writing an Update, a cross-behaviour "
                            + "call, or anything that processes many items. Do not use for sync bandwidth, which is "
                            + "conduit-networking, or for what the SCENE costs to render, which is "
                            + "conduit-rendering-cost.",
            },
            new Subject
            {
                Name        = "conduit-shaders",
                File        = "conduit-shaders.md",
                Description = "Shader authoring and what the Quest platform actually restricts: why lowering "
                            + "#pragma target for mobile is wrong, guarding GrabPass and screen-space effects with "
                            + "SHADER_API_MOBILE instead, and the bar for claiming a platform limitation at all. Use when "
                            + "writing or editing a .shader, .hlsl or .cginc, choosing a shader model or blend mode, or "
                            + "before warning that a shader feature will not work on Quest. Do not use for material asset "
                            + "wiring, which is conduit-assets-prefabs, or for ShaderGUI layout, which is conduit-editors.",
            },
            new Subject
            {
                Name        = "conduit-lighting",
                File        = "conduit-lighting.md",
                Description = "Baking a world's lighting and the PC versus Android split: why baked ambient occlusion is "
                            + "the only AO a Quest world gets, the per-platform lightmap resolution, size, bounce and "
                            + "reflection-probe settings, why every dynamic object including the players is unlit without "
                            + "light probes and where those probes belong in three dimensions, and how to read a blurry "
                            + "bake, a seam or a dark avatar. Use when baking, placing probes, or judging what a world's "
                            + "lighting costs on Quest. Do not use for shader authoring, which is conduit-shaders, or for "
                            + "the Android size ceiling, which is conduit-build-validation.",
            },
            new Subject
            {
                Name        = "conduit-rendering-cost",
                File        = "conduit-rendering-cost.md",
                Description = "What a world costs to RENDER and how to cut it: measuring with Conduit's own profiler "
                            + "verbs and why Deep Profile answers which call rather than how expensive, why a VRChat "
                            + "world is Built-in pipeline so no URP or SRP Batcher advice applies at all, static "
                            + "batching and GPU instancing and why the material is the batch boundary, occlusion "
                            + "culling including the transparent-occluder trap and the OcclusionPortal Udon can open "
                            + "at runtime, LOD being authoring-only because LODGroup is not exposed to Udon, and the "
                            + "ASTC and triangle budgets for Android. Use when a world runs badly, or before changing "
                            + "batching, culling, LOD or texture settings. Do not use for Udon's own cost, which is "
                            + "conduit-performance, for the bake, which is conduit-lighting, or for shader cost, which "
                            + "is conduit-shaders.",
            },
            new Subject
            {
                Name        = "conduit-video",
                File        = "conduit-video.md",
                Description = "Bundled video in a world: what Udon can and cannot do with VideoPlayer, showing many "
                            + "animations from ONE decoder by cropping a grid video with RawImage.uvRect, why that beats a "
                            + "sprite flipbook on size, and the VP8 and alpha rules for encoding it. Use when adding video, "
                            + "animating a UI element, or choosing between video and a flipbook. Do not use for URL-streamed "
                            + "video players, which are conduit-video-players.",
            },
            new Subject
            {
                Name        = "conduit-video-players",
                File        = "conduit-video-players.md",
                Description = "URL video players: why BaseVRCVideoPlayer is the ONLY player type Udon can call so "
                            + "resolution, autoplay and AVPro's speaker are Inspector-only, which parts of VRChat's "
                            + "built-in resync are actually exposed, why PlayURL never propagates so every client must "
                            + "play from synced state, why a seek before OnVideoReady is ignored, syncing a server clock "
                            + "stamp instead of a position and why the milliseconds call wraps, which VideoError values "
                            + "are worth retrying, and stabilising AVPro's flickering texture. Use when building or "
                            + "fixing a player that streams a VRCUrl, or when choosing between the two players. Do not "
                            + "use for bundled VideoClips, which are conduit-video.",
            },
            new Subject
            {
                Name        = "conduit-testing",
                File        = "conduit-testing.md",
                Description = "Verifying a world actually works: why ClientSim cannot validate networking at all and "
                            + "where it actively lies, using Build and Test with several real clients, the Build and "
                            + "Reload fast loop that reuses the running client, the three-client late-joiner procedure "
                            + "that catches most sync bugs, and where the VRChat client writes its own log. Use when "
                            + "planning how to verify a networked behaviour or before claiming one works. Do not use for "
                            + "compiling and reading the console, which is conduit-compile-console.",
            },
            new Subject
            {
                Name        = "conduit-vrc-input",
                File        = "conduit-vrc-input.md",
                Description = "Driving the player and handling pickups in play mode through the bridge: pickup auto-hold, "
                            + "measuring a hold offset and computing a placement, and which ClientSim reads cannot be "
                            + "trusted. Use when driving the player, grabbing or placing a pickup, or exercising a world "
                            + "gimmick in play mode. Do not use for editing scenes or code outside play mode.",
            },
            new Subject
            {
                Name        = "conduit-assets-prefabs",
                File        = "conduit-assets-prefabs.md",
                Description = "Assets, prefabs and scenes: why asset moves must go through the editor, editing prefabs in "
                            + "Prefab Mode rather than through a scene instance, prefab linkage and unpack modes, standing "
                            + "up a new world scene, and hand-editing a unitypackage. Use when moving, renaming or deleting "
                            + "an asset, editing a prefab, or creating a scene. Do not use for code-only edits.",
            },
            new Subject
            {
                Name        = "conduit-editors",
                File        = "conduit-editors.md",
                Description = "Custom inspectors and editor tooling for this project: the mandatory branded custom editor "
                            + "per behaviour, editor namespaces and folders, HelpBox usage, and the ShaderGUI duplicate-header "
                            + "rule. Use when writing or editing a custom inspector, a ShaderGUI, or any editor script. Do "
                            + "not use for runtime behaviour code.",
            },
            new Subject
            {
                Name        = "conduit-inspector-examples",
                File        = "conduit-inspector-examples.md",
                Description = "The working UI Toolkit inspector framework shipped in Conduit's Examples folder and the ten "
                            + "demo behaviours that exercise every attribute in it, with an index saying which example draws "
                            + "which control, and the UI Toolkit traps they already solved: deferred repaint, USS tie-breaks, "
                            + "tinting an opaque sprite, IMGUI-backed controls, loading an asset by GUID, and progress that "
                            + "survives a panel rebuild. Use BEFORE "
                            + "writing any inspector control, grouping, validation, picker, preview or collection UI, and "
                            + "instead of writing one from memory. Do not use for the project's editor CONVENTIONS - namespaces, "
                            + "folders, the mandatory editor per behaviour, ShaderGUI - which are conduit-editors.",
            },
            new Subject
            {
                Name        = "conduit-diagnostics",
                File        = "conduit-diagnostics.md",
                Description = "The mandatory diagnostic and design sequences for this project, each producing named "
                            + "checkpoints in your output. Use when diagnosing a bug whose cause you did not directly "
                            + "observe, when a first fix failed, or before building anything non-trivial. Do not use for a "
                            + "change whose mechanism you have already observed.",
            },
            new Subject
            {
                Name        = "conduit-creator-economy",
                File        = "conduit-creator-economy.md",
                Description = "VRChat Creator Economy through Udon: which VRC.Economy members are actually exposed, "
                            + "ownership and purchase callbacks, supporter boards via ListProductOwners, authoring the "
                            + "UdonProduct asset, and the listing-id versus product-id trap. Use when touching products, "
                            + "listings, purchases, or ownership. Do not use for general UI or networking work.",
            },
            new Subject
            {
                Name        = "conduit-animator",
                File        = "conduit-animator.md",
                Description = "Authoring AnimatorControllers, AvatarMasks and AnimationClips through Conduit's native verbs "
                            + "instead of throwaway editor scripts, and the traps their signatures cannot tell you: the "
                            + "only full-graph read-back, why a parameter's type cannot be changed in place, float equality "
                            + "conditions that never fire, blend-tree thresholds silently recomputed on assignment, and "
                            + "layer and parameter arrays that come back as copies. Use when creating or editing an "
                            + "animator, mask, or animation clip. Do not use for runtime animation code, or to look up a "
                            + "verb's payload, which the catalogue publishes.",
            },
            new Subject
            {
                Name        = "conduit-bridge-authoring",
                File        = "conduit-bridge-authoring.md",
                Description = "The RULES for writing Conduit's own code, and nothing describing what that code "
                            + "currently does: the contract every command must meet, how a blocking verb answers and "
                            + "survives a domain reload, how work outliving its own call reports back, the traps in "
                            + "writing values and references, and how a change reaches a connected client. Use when "
                            + "adding or changing a Conduit bridge command or the MCP server. Do not use to look up "
                            + "what an existing verb accepts, which the catalogue publishes from the verb's own "
                            + "signature, or how a class works, which is that class's summary.",
            },
            new Subject
            {
                Name        = "conduit-todo",
                File        = "conduit-todo.md",
                Description = "The Conduit backlog, and ONLY what is unfinished: open bridge bugs, undiagnosed "
                            + "conditions, unanswered questions, and wanted verbs. Use when picking up work on Conduit "
                            + "itself, or when one of the known open conditions appears while working. Do not use for "
                            + "ordinary project work.",
            },
            new Subject
            {
                Name        = "conduit-doc-style",
                File        = "conduit-doc-style.md",
                Description = "House rules for writing the Conduit convention documents themselves: what belongs in the "
                            + "injected index versus a subject file, bullet length, keeping the discriminator and cutting "
                            + "the narration, and deleting resolved entries. Use when adding to, editing, or reorganising "
                            + "any conduit-*.md file. Do not use when merely reading them.",
            },
        };

        #endregion

        #region Paths

        /// <summary>Cached result of locating Conduit's own folder, which cannot move while the editor runs.</summary>
        private static string _conduitRoot;

        /// <summary>
        /// Conduit's own folder, found from THIS script rather than written down. A written-in
        /// `Assets/&lt;vendor&gt;/&lt;product&gt;/...` is correct on exactly one machine: the folder can be renamed,
        /// moved, or shipped as a package under `Packages/`, and every one of those leaves a hardcoded path
        /// pointing at nothing while the code still compiles. Located once and cached.
        /// </summary>
        /// <returns>Project-relative path to Conduit's root folder, or an empty string when it cannot be found.</returns>
        internal static string ConduitRoot()
        {
            if (!string.IsNullOrEmpty(_conduitRoot)) return _conduitRoot;

            foreach (string guid in AssetDatabase.FindAssets("ConduitSkills t:MonoScript"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!path.EndsWith("/ConduitSkills.cs", StringComparison.Ordinal)) continue;

                // <root>/Editor/ConduitSkills.cs, so walk up out of Editor/ to reach the product root.
                string editorFolder = Path.GetDirectoryName(path);
                if (string.IsNullOrEmpty(editorFolder)) continue;

                string root = Path.GetDirectoryName(editorFolder);
                if (string.IsNullOrEmpty(root)) continue;

                _conduitRoot = root.Replace('\\', '/');
                return _conduitRoot;
            }

            return string.Empty;
        }

        /// <summary>The index document every client is pointed at, wherever Conduit is installed.</summary>
        /// <returns>Project-relative path to the index document.</returns>
        internal static string IndexDocumentPath()
        {
            string root = ConduitRoot();
            return root.Length == 0 ? string.Empty : root + "/Docs/" + INDEX_FILE;
        }

        /// <summary>
        /// Where the subject documents live. Every Subject.File and INDEX_FILE is a bare name resolved
        /// against this, so the documents can be relocated by changing this one method.
        /// </summary>
        /// <returns>Project-relative path to Conduit's Docs folder.</returns>
        private static string DocsRoot()
        {
            return Path.Combine(ConduitRoot(), "Docs").Replace('\\', '/');
        }

        /// <summary>
        /// Where a client discovers project-scoped skills. Project-scoped deliberately: the MCP entry is
        /// already registered per project (the server is named after it), and these documents are edited
        /// per project, so a user-global install would make several Unity projects overwrite one another.
        /// </summary>
        /// <param name="client">Client display name as shown on the window's cards.</param>
        /// <returns>Absolute skills folder for that client, or null when the client has no skill support.</returns>
        internal static string SkillsRoot(string client)
        {
            string project = Directory.GetCurrentDirectory().Replace('\\', '/');
            switch (client)
            {
                case "Claude Desktop": return project + "/.claude/skills";
                case "Codex":          return project + "/.codex/skills";

                // Cursor reads .claude/skills and .codex/skills as a compatibility fallback, so it would
                // work without this entry - but it is given its OWN native folder so each client's install
                // and removal stay independent. Sharing a folder would mean removing one client silently
                // stripped another's skills.
                case "Cursor":         return project + "/.cursor/skills";

                // Same reasoning as Cursor, and it reads .claude/skills and .agents/skills as well, so it
                // would find the documents without this entry. Its own folder keeps each client's install and
                // removal independent, and it means a project with only this client installed never has a
                // .claude folder created in it for a client the owner does not have.
                case "OpenCode":       return project + "/.opencode/skills";

                default: return null;
            }
        }

        /// <summary>Whether this client supports skills at all, and therefore gets the skill half of Connect.</summary>
        /// <param name="client">Client display name.</param>
        /// <returns>True when a skills folder is defined for the client.</returns>
        internal static bool Supported(string client)
        {
            return SkillsRoot(client) != null;
        }

        /// <summary>Folder holding prebuilt skills that ship inside the package rather than being generated.</summary>
        /// <returns>Project-relative path to Conduit's vendored skills folder.</returns>
        private static string VendoredRoot()
        {
            return Path.Combine(ConduitRoot(), "Skills").Replace('\\', '/');
        }

        /// <summary>
        /// Whether a skill name is one Conduit itself installs, subject or vendored. It answers from the
        /// registration table rather than from a name pattern, because the client's skills folder also holds
        /// the user's own skills and a prefix guess would eventually reach one of those.
        /// </summary>
        /// <param name="name">Skill folder and frontmatter name.</param>
        /// <returns>True only for a name this Conduit installs.</returns>
        internal static bool IsConduitSkillName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            foreach (Subject subject in Subjects)
            {
                if (string.Equals(subject.Name, name, StringComparison.Ordinal)) return true;
            }

            foreach (string vendored in VendoredNames())
            {
                if (string.Equals(vendored, name, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// Names of the vendored skill folders actually present. A folder only counts when it holds a
        /// SKILL.md, so a loose note sitting beside them is never installed as if it were a skill. Returns
        /// nothing when the vendored root does not exist, which is the ordinary case.
        /// </summary>
        /// <returns>Folder names under the vendored root that contain a SKILL.md.</returns>
        private static string[] VendoredNames()
        {
            string root = VendoredRoot();
            if (!Directory.Exists(root))
            {
                return new string[0];
            }

            var names = new List<string>();
            foreach (string directory in Directory.GetDirectories(root))
            {
                if (File.Exists(Path.Combine(directory, "SKILL.md")))
                {
                    names.Add(Path.GetFileName(directory));
                }
            }

            return names.ToArray();
        }

        /// <summary>
        /// Copies a skill tree, skipping Unity's `.meta` sidecars. Those exist because the source lives
        /// under Assets/; they are meaningless to an agent and would just be noise in the skills folder.
        /// </summary>
        /// <param name="source">Folder to copy from.</param>
        /// <param name="target">Folder to copy into; created when missing.</param>
        private static void CopyTree(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(source))
            {
                // .meta and .asset are Unity bookkeeping for the copy that lives under Assets/. Neither
                // means anything to an agent reading the skill, and a UdonSharpProgramAsset outside the
                // project is inert, so copying them would only add noise.
                if (IsUnityBookkeeping(file))
                {
                    continue;
                }

                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            }

            foreach (string directory in Directory.GetDirectories(source))
            {
                CopyTree(directory, Path.Combine(target, Path.GetFileName(directory)));
            }
        }

        /// <summary>
        /// Whether a file is Unity's own bookkeeping rather than skill content. `.meta` sidecars and
        /// `.asset` program assets exist only because the source tree lives under Assets/; Unity rewrites
        /// both on import, and neither is useful once copied out.
        /// </summary>
        /// <param name="path">File path to test.</param>
        /// <returns>True for a file that should be neither copied nor counted when detecting staleness.</returns>
        private static bool IsUnityBookkeeping(string path)
        {
            return path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Most recent write time anywhere in a tree, used to detect a vendored skill going stale.</summary>
        /// <param name="directory">Folder to scan recursively.</param>
        /// <returns>The newest UTC write time found, or DateTime.MinValue for a missing folder.</returns>
        private static DateTime NewestWrite(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return DateTime.MinValue;
            }

            DateTime newest = DateTime.MinValue;
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                // Unity rewrites .meta and .asset files on import, so counting them would mark a skill
                // stale forever: linking a program asset touches an .asset AFTER the copy was made, and
                // neither file is part of what the skill actually delivers.
                if (IsUnityBookkeeping(file))
                {
                    continue;
                }

                DateTime written = File.GetLastWriteTimeUtc(file);
                if (written > newest)
                {
                    newest = written;
                }
            }

            return newest;
        }

        #endregion

        #region Install / Remove

        /// <summary>
        /// Whether every shipped subject is present for this client. Reports false when any single skill is
        /// missing so a partially installed set does not read as connected.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>True only when all subjects that have a source document are installed.</returns>
        internal static bool IsInstalled(string client)
        {
            string root = SkillsRoot(client);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return false;
            }

            foreach (Subject subject in Subjects)
            {
                if (!File.Exists(Path.Combine(DocsRoot(), subject.File)))
                {
                    continue;
                }

                if (!File.Exists(Path.Combine(root, subject.Name, "SKILL.md")))
                {
                    return false;
                }
            }

            foreach (string name in VendoredNames())
            {
                if (!File.Exists(Path.Combine(root, name, "SKILL.md")))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Whether this client has ANY Conduit skill installed, which is what marks it as a client the
        /// user opted in. Deliberately distinct from <see cref="IsInstalled"/>, which additionally demands
        /// that EVERY shipped subject is present: adding a new subject to the table makes that false, and
        /// gating the auto-sync on it would stop the sync for the whole client - including the subjects
        /// already installed - until somebody pressed the window button.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>True when at least one Conduit skill is already installed for the client.</returns>
        internal static bool HasAnyInstalled(string client)
        {
            string root = SkillsRoot(client);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return false;
            }

            foreach (Subject subject in Subjects)
            {
                if (File.Exists(Path.Combine(root, subject.Name, "SKILL.md"))) return true;
            }

            foreach (string name in VendoredNames())
            {
                if (File.Exists(Path.Combine(root, name, "SKILL.md"))) return true;
            }

            return false;
        }

        /// <summary>
        /// Whether an installed skill is older than the document it was generated from, so the window can
        /// tell the user to re-install after the conventions are edited rather than silently serving a
        /// stale copy - the exact drift a copy-on-install design has to answer for.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>True when at least one source document is newer than its generated skill.</returns>
        internal static bool IsStale(string client)
        {
            string root = SkillsRoot(client);
            if (string.IsNullOrEmpty(root) || !IsInstalled(client))
            {
                return false;
            }

            foreach (Subject subject in Subjects)
            {
                string source = Path.Combine(DocsRoot(), subject.File);
                string target = Path.Combine(root, subject.Name, "SKILL.md");
                if (File.Exists(source) && File.Exists(target)
                    && File.GetLastWriteTimeUtc(source) > File.GetLastWriteTimeUtc(target))
                {
                    return true;
                }
            }

            foreach (string name in VendoredNames())
            {
                // Compared tree against tree, not against the copied SKILL.md: File.Copy PRESERVES the
                // source write time, so a freshly copied SKILL.md is never newer than its source and any
                // sibling document edited after it would read as permanently stale.
                string target = Path.Combine(root, name);
                if (Directory.Exists(target)
                    && NewestWrite(Path.Combine(VendoredRoot(), name)) > NewestWrite(target))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Opening marker of the generated subject block inside the index document.</summary>
        private const string INDEX_BEGIN = "<!-- CONDUIT-SUBJECTS:BEGIN -->";

        /// <summary>Closing marker of the generated subject block inside the index document.</summary>
        private const string INDEX_END = "<!-- CONDUIT-SUBJECTS:END -->";

        /// <summary>The router document, which is injected into every agent's prompt.</summary>
        private const string INDEX_FILE = "conduit-instructions.md";

        /// <summary>
        /// Rewrites the generated subject block in the index document from <see cref="Subjects"/>.
        /// <para>
        /// The index carries the FULL descriptions rather than a terse trigger list, because it is the one
        /// document guaranteed to be in context after a compaction. A skill's own description cannot route
        /// an agent to a skill it has not realised it needs - skills fire by matching that description - so
        /// the only place a "which file owns this?" question can be answered is here. Keeping the terse
        /// table here instead is what let a console-noise rule get filed against the play-mode input
        /// document.
        /// </para>
        /// </summary>
        /// <returns>The file name when it changed, otherwise null.</returns>
        private static string SyncIndexDocument()
        {
            string path = Path.Combine(DocsRoot(), INDEX_FILE);
            if (!File.Exists(path)) return null;

            string text = File.ReadAllText(path).Replace("\r\n", "\n");
            int begin = text.IndexOf(INDEX_BEGIN, StringComparison.Ordinal);
            int end = text.IndexOf(INDEX_END, StringComparison.Ordinal);
            if (begin < 0 || end < 0 || end < begin) return null;

            string rebuilt = text.Substring(0, begin + INDEX_BEGIN.Length)
                             + "\n\n" + BuildSubjectDescriptions() + "\n"
                             + text.Substring(end);

            if (rebuilt == text) return null;

            File.WriteAllText(path, rebuilt, new UTF8Encoding(false));
            return INDEX_FILE;
        }

        /// <summary>Builds the verbose per-subject descriptions written into the index document.</summary>
        /// <returns>Markdown bullet list, one entry per subject.</returns>
        private static string BuildSubjectDescriptions()
        {
            var builder = new StringBuilder();
            foreach (Subject subject in Subjects)
            {
                builder.Append("- **`").Append(subject.File).Append("`** - ")
                       .Append(subject.Description).Append('\n');
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Builds the compact sibling table embedded in every generated skill. A loaded skill has already
        /// resolved its own subject, so what it needs is a sideways pointer rather than the descriptions
        /// again.
        /// </summary>
        /// <returns>Markdown table of every subject document.</returns>
        private static string BuildLookupTable()
        {
            var builder = new StringBuilder();
            builder.Append("| Sibling subject | Owns |\n| --- | --- |\n");
            foreach (Subject subject in Subjects)
            {
                // First sentence only: the scope clause, without the use-when and do-not-use-for triggers.
                string description = subject.Description;
                int stop = description.IndexOf(". ", StringComparison.Ordinal);
                string owns = stop > 0 ? description.Substring(0, stop) : description;

                builder.Append("| `").Append(subject.File).Append("` | ").Append(owns).Append(" |\n");
            }

            return builder.ToString();
        }

        /// <summary>
        /// Record of the skill folders Conduit last installed for a client, kept beside them so it survives
        /// anything that clears the project's Library folder.
        /// </summary>
        private const string MANIFEST_FILE = ".conduit-skills";

        /// <summary>
        /// The skill names Conduit recorded installing for a client last time.
        /// <para>
        /// A retired subject is no longer in <see cref="Subjects"/>, so it is indistinguishable by name from
        /// a skill the user wrote themselves - both are simply absent from the table. This record is the only
        /// thing that separates them, which is why the prune deletes from it rather than from the folder.
        /// </para>
        /// </summary>
        /// <param name="root">The client's skills folder.</param>
        /// <returns>Recorded names, or an empty array when no record exists.</returns>
        private static string[] ReadManifest(string root)
        {
            string path = Path.Combine(root, MANIFEST_FILE);
            if (!File.Exists(path))
            {
                return new string[0];
            }

            try
            {
                var names = new List<string>();
                foreach (string line in File.ReadAllLines(path, new UTF8Encoding(false)))
                {
                    string name = line.Trim();
                    if (name.Length > 0) names.Add(name);
                }

                return names.ToArray();
            }
            catch (Exception)
            {
                // An unreadable record must not take the install down with it, and treating it as empty is
                // the safe direction: nothing is pruned rather than the wrong thing being pruned.
                return new string[0];
            }
        }

        /// <summary>Records the skill names just installed, replacing any previous record.</summary>
        /// <param name="root">The client's skills folder.</param>
        /// <param name="names">Names written by this install.</param>
        private static void WriteManifest(string root, List<string> names)
        {
            try
            {
                File.WriteAllText(
                    Path.Combine(root, MANIFEST_FILE),
                    string.Join("\n", names.ToArray()) + "\n",
                    new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // Failing to record leaves the next install unable to prune, which costs a stale folder.
                // Failing the whole install over it would cost the client every skill it came for.
            }
        }

        /// <summary>
        /// Whether a client's record disagrees with what Conduit currently ships, so an install is owed.
        /// <para>
        /// Retiring a subject moves no other document's write time and leaves every remaining skill present,
        /// so neither staleness nor completeness notices it. Without this test the install that would prune
        /// the retired folder is never reached, and a missing record is reported too so the first install
        /// after this became a rule writes one to prune against later.
        /// </para>
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>True when the record is absent, or names a skill no longer shipped.</returns>
        internal static bool ManifestOutOfDate(string client)
        {
            string root = SkillsRoot(client);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return false;
            }

            if (!File.Exists(Path.Combine(root, MANIFEST_FILE)))
            {
                return true;
            }

            foreach (string name in ReadManifest(root))
            {
                if (!IsConduitSkillName(name)) return true;
            }

            return false;
        }

        /// <summary>
        /// Writes one skill per subject document into the client's project-scoped skills folder, replacing
        /// any previous copy. Returns a report naming what was written and what was skipped, rather than a
        /// blanket success the caller cannot act on.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>A human-readable summary of the install.</returns>
        internal static string Install(string client)
        {
            string root = SkillsRoot(client);
            if (string.IsNullOrEmpty(root))
            {
                return client + " has no known skills folder; MCP was configured but no skills were installed.";
            }

            string docs = DocsRoot();
            var written = new List<string>();
            var missing = new List<string>();

            foreach (Subject subject in Subjects)
            {
                string source = Path.Combine(docs, subject.File);
                if (!File.Exists(source))
                {
                    missing.Add(subject.File);
                    continue;
                }

                string folder = Path.Combine(root, subject.Name);
                Directory.CreateDirectory(folder);

                var builder = new StringBuilder();
                builder.Append("---\n");
                builder.Append("name: ").Append(subject.Name).Append('\n');
                builder.Append("description: ").Append(Quote(subject.Description)).Append('\n');
                builder.Append("---\n\n");
                builder.Append("<!-- GENERATED by the Conduit window from ").Append(subject.File);
                builder.Append(". Edit that document, then press Connect again to regenerate. -->\n\n");
                builder.Append("> Where a fact does NOT belong to this subject, file it against the owner below\n");
                builder.Append("> rather than here. Full descriptions live in `").Append(INDEX_FILE).Append("`.\n\n");
                builder.Append(BuildLookupTable()).Append('\n');
                builder.Append(File.ReadAllText(source).Replace("\r\n", "\n").TrimEnd()).Append('\n');

                File.WriteAllText(Path.Combine(folder, "SKILL.md"), builder.ToString(), new UTF8Encoding(false));
                written.Add(subject.Name);
            }

            // Vendored skills are copied wholesale rather than generated: they are third-party reference
            // trees with their own SKILL.md and references/ layout, and inlining a 60 KB reference file into
            // one skill would load it in full on every trigger. Rules get inlined; a library gets browsed.
            foreach (string name in VendoredNames())
            {
                string target = Path.Combine(root, name);
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, true);
                }

                CopyTree(Path.Combine(VendoredRoot(), name), target);
                written.Add(name);
            }

            // A subject dropped from the table, or a vendored folder deleted, otherwise leaves its installed
            // copy in place forever: the install only ever writes, so the removal direction never self-heals
            // the way an addition does. Only a name Conduit itself recorded installing is eligible, because
            // the user's own skills live in this same folder and are absent from the table for the same
            // reason a retired subject is.
            var pruned = new List<string>();
            foreach (string name in ReadManifest(root))
            {
                if (written.Contains(name)) continue;

                string folder = Path.Combine(root, name);
                if (!Directory.Exists(folder)) continue;

                try
                {
                    Directory.Delete(folder, true);
                    string meta = folder + ".meta";
                    if (File.Exists(meta)) File.Delete(meta);
                    pruned.Add(name);
                }
                catch (Exception)
                {
                    // A folder held open elsewhere is reported as still present rather than as removed.
                }
            }

            WriteManifest(root, written);

            // The index is regenerated from the same array that produced the skills, so the router and the
            // skill descriptions cannot drift apart and adding a subject stays a single registration.
            string indexChanged = SyncIndexDocument();

            string report = "Installed " + written.Count + " skill(s) to " + root;
            if (pruned.Count > 0)
            {
                report += "; removed " + pruned.Count + " retired skill(s): " + string.Join(", ", pruned.ToArray());
            }

            if (missing.Count > 0)
            {
                report += " (skipped, document not found: " + string.Join(", ", missing.ToArray()) + ")";
            }

            if (indexChanged != null)
            {
                report += "; regenerated the subject block in " + indexChanged;
            }

            return report + ".";
        }

        /// <summary>
        /// Removes only the skills this window generated, leaving any hand-made skills in the same folder
        /// untouched, and prunes the folder afterwards only when it is genuinely empty.
        /// </summary>
        /// <param name="client">Client display name.</param>
        /// <returns>A human-readable summary of the removal.</returns>
        internal static string Remove(string client)
        {
            string root = SkillsRoot(client);
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            {
                return "No skills folder to clean for " + client + ".";
            }

            var names = new List<string>();
            foreach (Subject subject in Subjects)
            {
                names.Add(subject.Name);
            }

            names.AddRange(VendoredNames());

            int removed = 0;
            foreach (string name in names)
            {
                string folder = Path.Combine(root, name);
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                Directory.Delete(folder, true);
                string meta = folder + ".meta";
                if (File.Exists(meta))
                {
                    File.Delete(meta);
                }

                removed++;
            }

            // The record goes with the skills it describes, and it has to go before the emptiness test below
            // or it would keep an otherwise empty folder alive.
            string manifest = Path.Combine(root, MANIFEST_FILE);
            if (File.Exists(manifest))
            {
                try { File.Delete(manifest); }
                catch (Exception) { /* Leaves a stale record; the folder check below then keeps the folder. */ }
            }

            if (Directory.Exists(root) && Directory.GetFileSystemEntries(root).Length == 0)
            {
                Directory.Delete(root);
            }

            return "Removed " + removed + " Conduit skill(s) from " + root + ".";
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Emits a YAML double-quoted scalar. Descriptions are long single-line prose, and a bare scalar
        /// containing a colon would silently truncate the frontmatter and stop the skill triggering at all.
        /// </summary>
        /// <param name="value">The description text to quote.</param>
        /// <returns>A safely quoted YAML scalar.</returns>
        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        #endregion
    }
}
