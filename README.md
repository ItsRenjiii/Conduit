<div align="center">

# Conduit

### Give your agent the whole Unity editor, then teach it VRChat.

**Author. Inspect. Compile. Test. Validate. Build. Launch.
Without leaving the agent session.**

[![GitHub release](https://img.shields.io/github/v/release/ItsRenjiii/Conduit?display_name=tag&sort=semver)](https://github.com/ItsRenjiii/Conduit/releases/latest)
[![VPM release](https://img.shields.io/vpm/v/com.itsrenji.conduit?repository_url=https%3A%2F%2Fitsrenjilabs.github.io%2Fvpm%2Findex.json)](https://itsrenjilabs.github.io/vpm/)
[![Unity 2022.3](https://img.shields.io/badge/Unity-2022.3_LTS-black?logo=unity&logoColor=white)](#compatibility)
[![VRChat Worlds and Avatars](https://img.shields.io/badge/VRChat-Worlds_%26_Avatars-blue?logo=vrchat&logoColor=white)](#compatibility)
[![MCP](https://img.shields.io/badge/MCP-native-orange)](#it-works-inside-unity)

[![Setup: one click](https://img.shields.io/badge/setup-one_click-brightgreen)](#connect-an-agent)
[![Claude](https://img.shields.io/badge/Claude-D97757?logo=claude&logoColor=white)](#supported-clients)
[![Cursor](https://img.shields.io/badge/Cursor-black?logo=cursor&logoColor=white)](#supported-clients)
[![Antigravity](https://img.shields.io/badge/Antigravity-4285F4?logo=google&logoColor=white)](#supported-clients)
[![Codex](https://img.shields.io/badge/Codex-black)](#supported-clients)
[![OpenCode](https://img.shields.io/badge/OpenCode-555555)](#supported-clients)

Conduit is an MCP-native Unity bridge for VRChat creators, built for avatars and worlds alike.

It gives AI coding agents a typed, inspectable route through Unity, the VRChat SDK, animators, shaders, materials, assets, prefabs and profiling. In a world project it goes further, through UdonSharp, ClientSim, ProBuilder and the real build pipeline.

**225 core bridge verbs. 74 additional ProBuilder verbs. 33 project-aware engineering skills.**

<a href="https://itsrenjilabs.github.io/vpm/"><img src="https://img.shields.io/badge/add_to-VCC-1f6feb?logo=vrchat&logoColor=white" alt="Add to VCC" width="170"></a>

</div>

## Contents

* [Why Conduit exists](#why-conduit-exists)
* [What makes Conduit different](#what-makes-conduit-different)
* [One pixel, a screen full of white](#one-pixel-a-screen-full-of-white)
* [VRChat is part of the architecture](#vrchat-is-part-of-the-architecture)
* [Built so the agent cannot bluff](#built-so-the-agent-cannot-bluff)
* [It survives Unity being Unity](#it-survives-unity-being-unity)
* [A broad Unity tool surface](#a-broad-unity-tool-surface)
* [More than a tool catalogue](#more-than-a-tool-catalogue)
* [One button, across projects and agents](#one-button-across-projects-and-agents)
* [Install](#install)
* [Connect an agent](#connect-an-agent)
* [Compatibility](#compatibility)
* [What ships with Conduit](#what-ships-with-conduit)

## One request can cross the whole pipeline

```text
"Build this interaction, wire it into the scene, test it,
then launch it through VRChat Build & Test."

Conduit
  -> loads the project's own engineering rules
  -> creates or edits UdonSharp atomically
  -> waits through compilation and domain reload
  -> authors components, references, UI, animators, assets, and geometry
  -> drives the ClientSim player and exercises the interaction
  -> runs VRChat SDK validation
  -> builds the world bundle
  -> reports every SDK build stage back to the requesting agent
  -> returns the produced .vrcw path and launched test client
```

That is one continuous workflow.

The agent does not lose the job when Unity recompiles, does not need the user to relay Console output, and does not have to guess whether the thing it changed actually compiled, validated, or built.

## Why Conduit exists

Coding agents can already write substantial Unity code.

The harder part is everything around the code.

Unity has editor state, serialized references, importers, assets, prefabs, components, compilation, domain reloads, scene state, animation systems, profiler data, build pipelines, and tooling that exists only inside the editor.

VRChat adds another layer: UdonSharp compilation, compiled Udon programs, networking and ownership rules, SDK exposure, ClientSim, world metadata, upload validation, Quest constraints, and Build & Test.

Conduit gives the agent a persistent route through that environment instead of leaving it on the filesystem and asking the user to do the rest.

## Built around the work, not the demo

<table>
<tr>
<td width="33%" valign="top">

<strong>Author</strong><br><br>

Create and edit scenes, prefabs, GameObjects, components, materials, shaders, AnimatorControllers, AnimationClips, AvatarMasks, UnityEvents, tags, layers, packages, UdonSharp behaviours, and ProBuilder geometry.

</td>
<td width="33%" valign="top">

<strong>Understand</strong><br><br>

Inspect serialized objects, reverse references, importer settings, shader properties, component schemas, compiled Udon programs, SDK exposure, profiler frames, editor windows, and the live scene hierarchy.

</td>
<td width="33%" valign="top">

<strong>Prove</strong><br><br>

Compile once, read the current Console verdict, drive ClientSim, operate world-space controls, capture targeted views, run SDK validation, and launch real VRChat Build & Test with asynchronous stage reporting.

</td>
</tr>
</table>

## What makes Conduit different

### It works inside Unity

Conduit's Unity tool surface runs inside the Unity editor itself over local loopback HTTP.

There is no Python or Node sidecar sitting between the agent and Unity and pretending to be the editor. Tool calls reach Unity's actual APIs, assets, components, SDK integrations, and main-thread state.

### It asks Unity instead of asking the model to remember Unity

A search that misses can look exactly like an API that does not exist.

Conduit gives the agent authoritative ways to check.

It can inspect a resolved type's real members, query VRChat's actual Udon node registry, and read the program Unity really compiled from an UdonSharp behaviour.

The answer comes from the project, Unity, or the SDK - not from a model's memory of them.

### It keeps working through compiles and reloads

Compilation and domain reload are normal parts of Unity development.

Conduit treats them as part of the workflow rather than as a dead agent connection.

Long-running work is recorded durably, resumed after reload, and returned to the agent session that asked for it. Build operations, SDK work, imports, and compilation can outlive the Unity domain that started them without silently becoming somebody else's problem.

### It remembers what it changed

Every change an agent makes to your source is recorded, with the time it happened and which agent made it.

You can read that history back, see exactly what a file used to contain, and put any version of it back.

No Unity MCP has ever done this, and Unity cannot do it for you. Its undo covers the scene and the inspector, never files on disk.

It needs no Git, and works in projects that have no version control at all.

### It tests the work, not just the source

The agent can control the ClientSim player, move through the world, look around, crouch, grab, drop, press Use, inspect state, and read screenshots back from the editor.

It can then run VRChat validation, build the world, launch Build & Test, and receive the final bundle path or failure.

### It brings the engineering knowledge with the tools

Giving an agent hundreds of Unity operations is not enough.

Conduit also installs project-aware guidance covering the Unity and VRChat failure modes those tools are expected to operate inside.

The bridge gives the agent hands.

The knowledge layer teaches it how to use them.

### It does not hide behind arbitrary C# execution

Conduit deliberately does not expose an `execute arbitrary C#` tool.

Normal operations use explicit, typed verbs with validation and read-back evidence so it stays visible what the agent asked Unity to do and what Unity actually did.

---

# One pixel, a screen full of white

During Conduit's development, a vehicle world had a bug blocking alpha testing.

At night, turning on the headlights could produce violent white flashes across the camera. It depended on the viewing angle and did not happen every frame. The apparent culprit was a volumetric lighting effect.

The developer caught a flash and paused the game.

Conduit gave the agent access to that exact failing state: the live camera, spotlight, renderers, runtime material instances, shader passes, and Unity's own Frame Debugger. The agent could read values, change one condition, check what Unity actually applied, capture the result, and restore the original setting while the simulation stayed paused.

Turning off the volumetric effects left the flash on screen. Disabling the Unity spotlight's soft shadows removed it.

Through Conduit's editor-window controls, the agent operated the Frame Debugger and followed the image through rendering and post-processing. A single white point in the first bloom downsample became the large white orb in the final image. A controlled comparison with Unity's NaN filter removed and restored the flash at the same paused view.

That gave the investigation a specific failure to pursue: an invalid lighting value reaching the HDR image.

Material pass controls then isolated separate failures in the coated vehicle surfaces and the terrain. Both extra-light paths needed protection. Keeping the vehicle repair while removing the terrain guard brought the ground flash back; the next paused comparison exposed that regression directly.

Conduit's protected source edits recorded the shader revisions and applied the changes atomically. Its compile result established that the edits compiled. The developer's moving-camera retest established that the flashing had stopped, with soft shadows, bloom, HDR and the shaders' material features retained.

The precise arithmetic operation that first generated the invalid value was not measured. The rendering paths, the propagation into bloom, and the working protection were established by the captured comparisons.

That is the practical advantage over a bridge limited to file edits and component setters.

Conduit connects live-state inspection, native editor tools, reversible experiments, source history and compilation in the same agent session. The developer catches the intermittent failure and judges the result. The agent can carry out the editor investigation around them.

The resulting diagnostic guidance now lives in Conduit's lighting and shader skills, so the next agent can start with the lesson already available.

---

# VRChat is part of the architecture

Conduit was built for VRChat creation rather than treating VRChat as a handful of extra Unity commands. It detects whether a project is an avatar or a world project and offers the tools that project can use.

### Avatars

In an avatar project the agent works through the same typed tools:

* author AnimatorControllers end to end: layers, states, transitions, blend trees, parameters, avatar masks and clips
* read and write any component, VRChat Dynamics included, with references resolved properly
* edit shaders and get a compiled verdict for every variant the change reaches
* set materials, importers, prefabs and the hierarchy, then check the result in a screenshot

Avatars are still built and uploaded through the VRChat SDK panel.

### UdonSharp lifecycle

Create an individual behaviour or an atomic batch, wait for Unity and UdonSharp to compile it, link the produced program assets, and delete source plus linked program assets together when required.

### Atomic source editing

Conduit can validate exact replacements across multiple C# files against their original contents before writing a single byte.

If the batch is valid, it is applied together for one compile.

UdonSharp auto-compilation is suppressed during the batch and the user's original setting is restored after success, reload, or a killed editor.

### Shaders get a real compiler verdict

Unity prints nothing when a shader compiles cleanly, and its import checks only the variant with no keywords. A compute shader's kernels do not compile until first use at all.

So Conduit's shader verbs compile the change themselves. Edit or write ShaderLab, HLSL, CGINC or compute sources, and every shader the change reaches is compiled for the editor's graphics API: every pass and stage, or every kernel, with no keywords and with each keyword the changed files test.

Edit an include and every shader and compute shader that uses it is compiled and reported. Errors come back by file, line and variant.

Shader edits work in Play Mode and hot-reload into the running scene. They are versioned like every other source edit.

### Inspect what actually compiled

Conduit can read the compiled Udon program behind a behaviour, including:

* exported variables
* methods
* externs
* symbols
* raw assembly

An agent can audit the program Unity produced instead of assuming the source compiled into what it expected.

### Query the real SDK exposure surface

`udon-exposure` reads the VRChat SDK's actual Udon node registry.

The currently verified SDK exposes **35,222 definitions** to Conduit's index.

The complete sorted surface can also be dumped as a baseline and diffed after an SDK update to see what was added or removed.

### Author VRChat worlds directly

Inspect and edit:

* world upload metadata
* content warnings
* player capacity
* thumbnails
* VRC components
* serialized references
* UnityEvents
* project settings
* SDK-facing configuration

### Test as a player

Through ClientSim, the agent can:

* teleport
* look
* move
* run
* crouch
* grab
* drop
* press Use
* inspect resulting state
* capture screenshots

That makes it possible to exercise an interaction instead of merely checking whether its code compiled.

### Use the real build pipeline

Conduit can:

* run VRChat SDK validation
* build the world
* launch Build & Test
* reload the last test build

Long-running SDK stages report back to the agent session that initiated them, including the final `.vrcw` path or failure result.

### Bring VRChat engineering knowledge into the session

Conduit's project skills cover areas including:

`UdonSharp` `Networking` `Ownership` `Late Joiners` `VRCTween` `World UI`
`VRChat Components` `Dynamics` `Player API` `NavMesh & Physics` `Audio`
`Video` `Lighting` `Udon Performance` `Rendering Cost` `ProBuilder`
`Animator Authoring` `Scene Geometry` `Layers` `Build Validation` `Testing`
`Play-mode Input` `Prefabs` `Shaders` `Creator Economy` `Inspectors`
`Diagnostics` `Bridge Authoring`

---

# Built so the agent cannot bluff

Conduit's normal path is deliberately explicit.

Every bridge verb publishes its own JSON Schema.

Required values, enums, numbers, arrays, and object shapes are validated before work reaches Unity.

Mutating operations return evidence of what actually happened, such as:

* old and new values
* created asset GUIDs
* matched references
* resolved objects
* compilation results
* Console verdicts
* produced files
* SDK stage results

Bad input can be refused before it changes the project.

File and asset operations stay inside writable project boundaries.

Destructive or ambiguous operations require explicit confirmation where appropriate.

Atomic source batches validate every edit before writing and then apply completely or write nothing.

A command becomes one Unity undo group whenever Unity can safely represent it that way.

Broken bridge coverage remains visible instead of being silently bypassed with generated editor code.

This is an ergonomics and evidence boundary, not a sandbox. An agent capable of authoring project scripts can still write editor code. Conduit's goal is to make the normal route constrained, inspectable, and honest about what it did.

`conduit-run` remains available as a cold-start fallback when a newly added Conduit verb is newer than a client's cached tool list. It can invoke only registered Conduit verbs, and the inner verb is still classified before admission, so the wrapper cannot bypass editor-state blocks, validation, durable-job handling, or safety rules.

---

# It survives Unity being Unity

```text
Claude / Cursor / Antigravity / Codex / OpenCode
                         |
              MCP Streamable HTTP
                         |
                   127.0.0.1
                         |
         +----------------+----------------+
         |                                 |
   typed tool catalogue             server event stream
         |                                 |
   durable job ledger          progress + tool-list changes
         |                                 |
   Unity main-thread queue  <--------------+
         |
   Unity Editor / VRChat SDK / UdonSharp / ClientSim
```

Compilation, imports, SDK jobs, and managed-domain reloads are treated as expected editor events.

Work that triggers a reload is recorded before the reload and completed from the new editor domain.

If a call outlives its immediate wait budget, `job_status` reads the same durable record instead of starting the operation again.

Catalogue revisions can also push tool-list change notices to connected clients after bridge edits.

Long-running work can return to an active agent loop or wake the requesting session when supported by that client.

Bridge health and activity remain readable while Unity's main thread is compiling or importing.

<details>
<summary><strong>Transport and reload architecture</strong></summary>

<br>

The in-editor endpoint supports four MCP revisions across both single-endpoint transport generations:

* stateless `2026-07-28`, including `server/discover` and a held `subscriptions/listen` POST stream
* session-based `2025-11-25`
* session-based `2025-06-18`
* session-based `2025-03-26`

The session-based revisions use GET event streams.

Claude additionally receives a project-local session-start hook. It runs once as a session begins, hands that session's own messaging address and credential to the bridge, and exits. Conduit then writes notices into that exact session itself, so nothing of its own stays resident and no language runtime is required.

OpenCode receives an in-harness JavaScript plugin for the equivalent session-specific return path. It uses the harness's authenticated client handle and does not implement the Unity tool surface itself.

On Windows, Conduit can preserve an accepted MCP response socket across a managed-domain reload.

The old domain duplicates and detaches its kernel handle, Unity `SessionState` carries the handoff, and the new domain can write the durable terminal result back onto the original call.

If capture fails, Conduit falls back to the ordinary durable-job path rather than losing the operation.

</details>

---

# A broad Unity tool surface

Conduit currently publishes **225 core bridge verbs**.

When ProBuilder is installed, another **74 modelling verbs** appear automatically for a total of **299 typed bridge verbs** in the verified VRChat Worlds configuration.

There is no alternate build or configuration switch required.

| Area                        | Published verbs | Highlights                                                                                     |
| --------------------------- | --------------: | ---------------------------------------------------------------------------------------------- |
| ProBuilder                  |              74 | Shapes, faces, edges, vertices, UVs, materials, lightmap UVs, validation, export               |
| Profiler                    |              28 | Recording, marks, frame ranges, A/B comparison, marker timelines, isolation, memory, rendering |
| Assets and prefabs          |              25 | Importers, references, orphan detection, replacement, prefab mode, materials, shaders          |
| Animation                   |              22 | Controllers, layers, states, transitions, blend trees, masks, clips, live sampling             |
| Editor operations           |              21 | Selection, tags, layers, undo, windows, overlays, controls, key input, focus                   |
| Scene and scene view        |              20 | Scene lifecycle, hierarchy, framing, orbiting, picking screenshot pixels into world space      |
| VRChat                      |              17 | Player control, world metadata, validation, Build, Build & Test, test reload                   |
| GameObjects and components  |              15 | Create, duplicate, inspect, modify, traverse hierarchies, repair missing scripts               |
| UdonSharp and compiled Udon |               7 | Atomic authoring, program-asset lifecycle, compiled-program inspection, exposure registry      |
| Shader sources              |               2 | Versioned edits and writes with a compiled verdict per variant, in Play Mode too               |
| Other Unity systems         |              68 | Scripts, Console, packages, screenshots, Frame Debugger, reflection, UnityEvents, search, lifecycle, discovery |

Server-owned infrastructure tools are published per authenticated client, so Conduit reports the invariant bridge surface rather than pretending every connection has one universal `tools/list` total.

---

# More than a tool catalogue

For clients with a project skill directory, pressing **Connect** installs more than an MCP endpoint.

Conduit ships a set of project-specific engineering documents. One acts as the routing index and is injected as the MCP server's own instructions, so it is present in every session. The rest install as focused subject skills that load when the agent enters that kind of work, instead of forcing one enormous Unity manual into context.

This is the kind of thing they contain:

* `SetOwner` arbitrates nothing, and `OnOwnershipRequest` runs on both clients at once
* ClientSim cannot validate networking at all, and there are places where it actively lies
* baked ambient occlusion is the only AO a Quest world gets
* animator float-equality conditions never fire, and blend-tree thresholds are silently recomputed on assignment
* a station tells you *that* someone is seated, but never *who*
* `PlayURL` never propagates, so every client must play from synced state
* user layers 22-31 must be referenced by number, and a failed name lookup silently yields a mask pointing at layer 31

None of that is in a general model's memory of Unity, and most of it is the difference between a world that works and a world that works until somebody else joins.

## Examples ship with it too

Working code ships alongside the guidance:

* a complete UI Toolkit inspector framework, with demos exercising every attribute in it
* named network-sync patterns including slot pools, packed sync, rate limiting, and clogged-retry
* Udon security patterns covering allow-lists, owner guards, authorised state, and targeted messaging
* interaction, movement, persistence, and contact examples

The intent is not merely to tell the agent that a pattern exists. It can read a working one shipped with the project.

<details>
<summary><strong>How skill synchronisation works</strong></summary>

<br>

Installed skills are generated from the documentation shipped with the project.

They regenerate automatically after:

* a Unity domain reload
* an imported documentation change
* a direct disk edit detected by the filesystem watcher

Changes are folded through a two-second debounce.

Generated output is written outside `Assets` so the synchronisation process cannot trigger its own Unity import loop.

Automatic synchronisation updates clients that already have Conduit installed.

**Update skills** remains available as a manual fallback, while **Connect** performs the initial installation.

</details>

---

# One button, across projects and agents

**Connect** is an operation, not a page of configuration instructions.

Conduit knows each supported client's real configuration shape and can merge the project into it without replacing unrelated settings.

It creates missing parent directories where necessary, installs project skills, and configures supported asynchronous return-path integration.

**Remove** deletes Conduit's own entry and installed integration without wiping the rest of the client's configuration.

Stale entries from previous bridge versions are pruned as part of the merge.

Each Unity project receives its own:

* sanitised MCP server name
* project identity
* credentials
* skill installation
* connection state

That means one agent can connect to several open Unity projects and target them independently instead of moving one global MCP connection around.

The reverse also works.

Claude, Cursor, Antigravity, Codex, and OpenCode can connect to the same editor at the same time.

Each connection receives a persisted per-client bearer so requests and asynchronous results stay attributable to the harness that initiated them, including across domain reloads.

The Conduit Unity window exposes that state directly:

* bridge status
* project status
* published command summary
* endpoint
* masked identity and credentials
* connected state
* stale state
* skills-stale state

---

# Install

Conduit is a Unity package, `com.itsrenji.conduit`. It installs into `Packages/`, not `Assets/`, so it never mixes with your project's own files and updates in place.

**From the VRChat Creator Companion (recommended)**

1. Open the [ItsRenjiLabs listing](https://itsrenjilabs.github.io/vpm/) and press **Add to VCC**, or in VCC go to **Settings > Packages > Add Repository** and enter `https://itsrenjilabs.github.io/vpm/index.json`.
2. Open your project in VCC, find **Conduit** and press **+** to add it.

VCC then offers each new release as an update. If the project has an older copy of Conduit under `Assets/ItsRenji/Conduit`, VCC removes it during the install.

**From Git (Unity Package Manager)**

1. Open **Window > Package Manager**.
2. Press **+** and choose **Add package from git URL...**
3. Enter `https://github.com/ItsRenjiii/Conduit.git` and press **Add**.

To pin a release, add its tag: `https://github.com/ItsRenjiii/Conduit.git#v1.0.2`.

**From a local copy**

Press **+** in the Package Manager, choose **Add package from disk...**, and pick Conduit's `package.json`.

**Examples**

The examples ship inside the package as a sample. Agents read them in place, so nothing has to be imported for the guidance to work. To run the demos in your project, select Conduit in the Package Manager, open **Samples**, and press **Import** next to **Examples**.

**Requirements**

Unity 2022.3. The VRChat Worlds SDK and ProBuilder are optional: Conduit detects them and adds their tools when they are present. Newtonsoft JSON is installed automatically.

---

# Connect an agent

1. Install Conduit and allow Unity to compile.
2. Open **ItsRenji > Conduit** from Unity's main menu.
3. Press **Connect** for Claude, Cursor, Antigravity, Codex, or OpenCode.
4. Restart the client once so it loads the new project-scoped MCP entry.
5. Ask the agent to call `unity-tool-list` to read the live command map.

Conduit merges itself into the client's existing configuration rather than replacing it.

Every Unity project receives its own server key, identity, credentials, and skills, so multiple projects can remain connected without routing a command into the wrong editor.

## Supported clients

| Client      | Direct MCP |  Project skills  | Asynchronous return path |
| ----------- | :--------: | :--------------: | :----------------------: |
| Claude      |     Yes    |        Yes       |           Yes\*          |
| Cursor      |     Yes    |        Yes       |            Yes           |
| Antigravity |     Yes    | Client-dependent |            Yes           |
| Codex       |     Yes    |        Yes       |            Yes           |
| OpenCode    |     Yes    |        Yes       |            Yes           |

\* Claude's asynchronous return path is a session-start hook, resolved with PowerShell on Windows and `sh` on macOS and Linux. Both ship with the operating system, so nothing has to be installed to make it work.

Conduit's connection planner owns each client's configuration shape, transport spelling, headers, and timeout requirements.

Users do not need to hand-edit JSON or TOML.

---

# Compatibility

The current development baseline is verified against:

| Component         | Verified version    | Notes                                                                        |
| ----------------- | ------------------- | ---------------------------------------------------------------------------- |
| Unity             | 2022.3.22f1         | VRChat's Unity 2022 LTS line                                                 |
| VRChat Worlds SDK | 3.10.5              | Enables Udon, world authoring, ClientSim, validation, and build tools        |
| ProBuilder        | 5.2.4               | Optional; adds 74 bridge verbs when present                                  |
| Operating system  | Windows             | Current supported development and harness-integration target                 |

Generic Unity authoring tools remain available in non-VRChat projects, including VRChat avatar projects.

VRChat Worlds-only tools are hidden when the Worlds SDK is absent, so the command catalogue reflects what the open project can actually support. UdonSharp authoring, compiled-Udon inspection and the VRChat player and build verbs are removed, leaving **202 core bridge verbs** in an avatar or plain Unity project.

---

# What ships with Conduit

* The in-editor MCP server and typed Unity bridge.
* **225 core bridge verbs.**
* **74 optional ProBuilder verbs.**
* Direct Unity, UdonSharp, VRChat SDK, ClientSim, profiler, animation, scene, asset, prefab, and build-pipeline access.
* Connection planning and one-click configuration/removal for supported file-based clients.
* Per-client credentials and live connection-state diagnostics.
* Domain-reload-safe durable jobs.
* Asynchronous agent notifications and long-running result delivery where supported.
* Project-specific engineering documentation, installed as subject skills alongside an always-injected routing index.
* Example scripts covering inspector tooling, networking, Udon security, interaction, movement, persistence, and contacts.
* Automated verification gates across the HTTP transport, reload recovery, the durable job ledger, catalogue integrity, connection planning, and native inbox routing.

---

# The short version

Conduit is not just a way for an agent to write Unity files.

It lets the agent participate in the actual editor workflow:

**understand the project -> author the change -> compile it -> inspect what Unity produced -> wire it into the scene -> test it as a player -> validate it through VRChat -> build it -> launch it.**

And it brings the project-specific engineering knowledge and examples needed to do that work without starting from zero every session.

If Conduit saves you an afternoon, a star helps other VRChat developers find it.

---

# License

Copyright (c) 2026 ItsRenji. All rights reserved.

Conduit is source-available under the [PolyForm Shield License 1.0.0](LICENSE.md). You may use, change and share it for any purpose, including building and selling your own Unity projects and VRChat worlds, except providing a product that competes with Conduit or with any product ItsRenji provides using it.

Files under `Samples~/Examples/` carry their own licence in the header of each file.

Conduit does not accept pull requests. To report a problem, suggest a feature, or ask about licensing outside these terms, open an issue on this repository.
