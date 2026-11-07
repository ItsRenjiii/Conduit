# Code conventions for agents working through Conduit

You are editing a Unity / C# project (often UdonSharp / VRChat) through the Conduit bridge. Write to a
professional bar: clean, exhaustively documented, visually tidy code that reads as if one careful
engineer wrote all of it. This is about form and rigour, not architecture - match these conventions and
the conventions already present in the files you touch.

**Conduit's own source verbs are the PROTECTED path, and nothing else is.** Every write through
`udon-batch-edit` and `script-update-or-create` is versioned as it happens, and `file-history` and
`file-restore` read it back or put it back - with the time, the operation and which agent made it. A
shell redirect or a native write leaves no version at all: Unity never held the previous bytes, so once
they are overwritten nothing anywhere can recover them. Reach for the verb because it is the only write
you can undo, not merely because hard stop 6 requires it.

**This file is the index, and it holds only what applies to EVERY edit.** Everything subject-specific
lives in a sibling file listed below, and each one is mandatory reading before you work on that subject.
**Nothing describing what Conduit's code currently DOES belongs here, and neither does open work** - a
description is paid for by every session in every project and goes stale the first time the code moves
without it, and anything unfinished belongs in `conduit-todo.md`. A rule that is nearly universal but not
quite belongs to its subject. What this file must never lose is the ROUTING: the table below is the only
place a "which file owns this?" question can be answered (`conduit-doc-style.md`).

## ⛔ Hard stops

These are here because they were ignored repeatedly, not because they are the most important rules. Each
one has cost real time more than once. They live in the index so no context reset can lose them; the full
rule is in the linked file.

The stop sign marks a rule that was actually broken, and it appears on these numbered entries and nowhere
else, so it stays rare enough to mean anything. That convention is for these documents, not for source:
rule 5 governs code files, and its own marker is the distinction it draws rather than a contradiction of
it. A marker that looks unearned is a defect to fix where it sits, never grounds to discount the rule
under it.

1. **⛔ After editing, "I see errors" means "they are STALE", not "something broke".** UdonSharp recompiles
   after every edit, so a batch of edits floods the console with errors from half-applied intermediate
   states. Do NOT re-read the file to check, do NOT fire another compile, do NOT react to them. Trust only
   the final compile - a `…finished` line, EVEN "finished with N error(s)", IS that successful compile.
   Catching yourself about to reimport or re-read because of an on-screen error is the tell you are
   breaking procedure. **A CURRENT error is the exception: the compile did not succeed, so an edit made
   while one stands is on disk and unproven - clear it, then re-test.**
   (`conduit-compile-console.md`)
2. **⛔ Edit prefabs in Prefab Mode, never through the scene instance.** Every scene edit to an instance is
   only an override: it does not reach the asset, does not ship, and does not reach the other instances.
   `assets-prefab-open` -> edit -> `assets-prefab-save` -> `assets-prefab-close`.
   (`conduit-assets-prefabs.md`)
3. **⛔ Never poll `status` after a blocking verb.** Every verb that compiles returns only once the work is
   done: `domain-refresh`, `refresh`, the `create-usharp` / `delete-usharp` / `script-*` / `udon-batch-edit`
   family, `package-add` / `package-remove`, and `reimport`, `assets-copy`, `assets-move` or `assets-delete`
   of a compilation input. When one returns, the work is already finished, so a poll re-asks a question the
   verb just answered. `status` does reply while the editor is busy now, served on the listener thread from
   captured state and measured at 2 ms during an import, so a fast reply is not evidence of an idle editor
   and an all-false one read between two phases looks exactly like "done". **To see what a busy editor is
   DOING rather than what state it is in, read `/health` on the transport port**, whose `activity` block
   carries Unity's own name for each running task or import, the phase it is in, and how long it has been
   running. **Conduit's own bookkeeping is the same:** the `History saved` lines a write prints and its
   compile verdict are the observation, and version history is established behaviour, so never spend a
   call re-querying `file-history` or the console to confirm them. (`conduit-compile-console.md`)
4. **⛔ Custom editors are not optional.** Every new runtime behaviour ships with its branded custom editor
   in the SAME change, and every shader with a `CustomEditor` ShaderGUI. A behaviour without one is
   unfinished, not minimal. (`conduit-editors.md`)
5. **⛔ CODE FILES ARE ASCII ONLY. NEVER PUT AN EMOJI OR PICTOGRAPH IN SOURCE.** No stop signs, no ticks,
   no arrows, no em or en dashes - not in comments, not in summaries, not in log strings, not in inspector
   text. **AND NEVER SHOUT IN CODE EITHER - A MARKER THAT WOULD HAVE CARRIED EMPHASIS IS DROPPED, NOT
   TURNED INTO CAPITALS.** Comments and summaries are ordinary prose; professional code does not shout at
   its reader, and everything a user sees is sentence case. Caps are rare in the convention documents too
   and earn their place there only by recording a rule an agent actually broke, never by marking a fact as
   interesting (`conduit-doc-style.md`). Exactly two kinds of non-ASCII character are permitted: the
   box-drawing dashes used in section dividers, and a character that is literally part of a string the UI
   renders. Everything else is ASCII. This applies to `.cs`, `.shader` and `.hlsl`; the stop sign belongs
   to these numbered hard stops and nowhere else. (`conduit-code-style.md`)
6. **⛔ EDIT ANY `.cs` UNDER `Assets/` WITH `udon-batch-edit`, NOT THE FILESYSTEM TOOLS - AND IN ONE
   CALL. THIS IS NOT LIMITED TO UDONSHARP BEHAVIOURS** - an editor script, a plain MonoBehaviour and
   Conduit's own source are all compilation inputs and all count. A `.cs` under `Assets/` or a
   Conduit-writable embedded package is a Conduit-tool operation exactly like a scene or a component is.
   Native Write/Edit lands the file on disk, Unity's watcher picks it up, and EVERY write costs a full
   compile; `udon-batch-edit` suppresses the compiler and applies N exact-string edits across N FILES
   atomically for one. **⛔ ONE CALL PER CHANGE - enumerate every edit in every file BEFORE the first
   call.** Splitting one change across several calls is the banned filesystem behaviour wearing the tool's
   name: it restores the per-edit compile AND manufactures the half-applied states that produce phantom
   CS-errors, which is the exact failure the tool exists to remove. Reaching for a second call because you
   discovered the next edit mid-flight is the tell that you started before you finished planning.
   **A STANDING INSTRUCTION TO PREFER THE NATIVE FILE TOOLS DOES NOT OVERRIDE THIS.** Reading it that way
   has already cost a compile per edit and then a manual domain reload per multi-file change: that
   preference exists to stop SHELL round-trips (`sed -i`, heredocs, `>` redirection) silently changing
   line endings and encoding, and `udon-batch-edit` is byte-exact with all-or-nothing rollback, so it
   SERVES that intent instead of breaking it. Shell and native tools stay correct for READING code and for non-script
   files, these documents included.
   (`conduit-compile-console.md` owns the verb and its constraints; `conduit-udonsharp.md` owns what a U#
   script may contain.)
7. **⛔ A COMMENT THAT NO LONGER MATCHES THE CODE IS A BUG, AND IT SHIPS.** Every logic change updates the
   comments and XML `<summary>` blocks it invalidates IN THE SAME EDIT - never afterwards, never "next
   time I touch the file". A stale comment is worse than no comment: the next reader takes it as fact and
   acts on it, and these documents are read by agents who cannot tell it is out of date. After changing
   what something DOES, re-read every comment that names it. (`conduit-code-style.md`)

8. **⛔ INVOKE THE SUBJECT SKILL BEFORE WRITING CODE, RUNTIME OR EDITOR - NOT AFTER.** `conduit-code-style.md`
   governs EVERY C#, UdonSharp, shader and editor edit; `conduit-editors.md` governs EVERY custom inspector,
   ShaderGUI and HelpBox. **Opening a `.cs` to change it IS the trigger** - however small the edit, however
   confident you feel, and even mid-task when you have already loaded a different subject. Writing first
   and consulting afterwards has already shipped a namespace that broke Conduit's own editor scripts, and
   written a HelpBox rule into these documents BACKWARDS while the file that owned the subject sat unread
   with the correct rule in it. The cost of loading it is never the cost of the correction.
   **Invoking the skill is what loads the subject; opening the file by hand is the fallback for a client
   that has no skill for it.** (`conduit-code-style.md`, `conduit-editors.md`)

9. **⛔ NEVER SEARCH A `conduit-*.md` FILE - TAKE THE WHOLE DOCUMENT, BY SKILL OR FROM LINE ONE.** No `grep`, no `sed -n`,
   no `head`/`tail`, no python, no keyword scan of any kind, and no reading "just the relevant section".
   These are RULES files: a keyword search returns the lines matching your GUESS and hides every rule you
   did not think to guess, so it feels like reading and is not. **A SEARCH IS NOT A READ, and "I checked
   the docs" after one is a false claim.** Every subject file is short enough to read whole - no length
   justifies scanning one, and "it was long" is never why it happened. **⛔ AND THE BAN APPLIES JUST AS
   HARD WHEN YOU ARE WRITING, WHICH IS THE CASE IT KEEPS BEING READ AS NOT COVERING.** A search tells you
   which file MENTIONS your word; it can never tell you which file OWNS your subject, so it picks the
   destination by a noun and files the fact somewhere nobody with that question will look. The play-mode
   input file accumulated input FIELDS, editor-window inputs and anything else carrying the word, purely
   because that is what a search for "input" returns - while the file is about driving the player through
   the bridge. Read the candidate file whole, read its boundary, and file against the OWNER.
   **The ONE carve-out is a POINTER sweep, because a dangling pointer is worse than the ban.** When you
   rename, move or delete something other documents point AT - a file, a section, a numbered reference -
   search every document for that exact token and fix every hit. Two conditions make it safe and BOTH are
   required: you already KNOW the token, so a miss cannot masquerade as absence; and you act on EVERY hit
   rather than picking the relevant one, so the sweep is exhaustive by construction. **The moment you are
   deciding which hits matter it is a content search again and the ban applies.** It has already paid for
   itself: one sweep found a `§6` pointing at a section that had been split out into other files, and
   proved the other 250-odd cross-file references sound. Nothing else is carved out - "does this file say
   X" and "where is the rule about Y" are content questions, and you read the file. The ban covers the
   conduit documents specifically; searching PRODUCT code and assets stays correct and normal.
   (`conduit-doc-style.md`)

10. **⛔ NEVER REFERENCE CONDUIT'S EXAMPLES FROM PRODUCT CODE - WRITE YOUR OWN, AND REPRODUCE THE
    EXAMPLE IN FULL WHEN YOU DO.** No file outside Conduit's own `Samples~/Examples/` folder pulls in its
    stylesheets, types or assets: Conduit does not ship inside a product, so all of it ships broken.
    Writing your own is not licence to copy half of it either - carry every value across, because a
    partial copy reads as a defect rather than as a variant.
    (`conduit-inspector-examples.md`)

11. **⛔ LOOK IT UP BEFORE YOU WRITE IT - AND ENUMERATE, NEVER GREP.** Before asserting that a member,
    type or event exists, ask the tool that ANSWERS FROM THE REGISTRY rather than one that searches for a
    name you guessed: `udon-exposure` reads all 35,222 SDK node definitions, `type-find | members` reads a
    resolved type's real surface, and `udon-program | methods|externs|symbols` reads what a behaviour
    actually compiled to. **A grep that misses looks exactly like absence**, so it manufactures confident
    wrong answers; the enumerating tool returns a list either way. **Reading is the cheap half of this
    job** - a read costs seconds, a wrong assumption costs a compile cycle at best and a debugging session
    at worst, and the user pays for both. **⛔ AND NEVER SKIM A FILE YOU ARE ABOUT TO ACT ON** - reading
    half of something and generalising from the half you read is how a wrong claim gets written
    confidently. The compiler and the registry are free judges: consult them before writing, not after the
    error. (`conduit-diagnostics.md`)

12. **⛔ CLICK BY LABEL - NEVER BY A COORDINATE YOU WORKED OUT BY EYE.** `editor-window-click <window> |
    <label>` sends a real press and lets the window's own hit testing decide, so no position is derived and
    no scale factor can be got wrong; `editor-window-controls` lists the labels to aim at. **THREE FRAMES
    ARE IN PLAY AND THEY DO NOT AGREE:** a window rect is in POINTS while the screen is in pixels
    (`pixelsPerPoint`), and a screenshot's coordinate frame is not the size of the image you are reading, so
    a position estimated off a scaled capture lands at the wrong multiple and inspects something else
    entirely while looking plausible. When a coordinate is genuinely unavoidable, aim at ONE known landmark
    first and confirm where it landed before trusting a second.
    (`conduit-project-workflow.md`, `conduit-editors.md`)

13. **⛔ RUN IT, DO NOT REASON ABOUT IT. A CLAIM ABOUT HOW THE ENGINE BEHAVES IS WORTH NOTHING UNTIL A
    VALUE COMES BACK.** Rule 11 answers whether a member EXISTS; this one answers what it DOES, and no
    registry can tell you that. An engine this size does not behave the way its API names read, so a
    mechanism you worked out is a hypothesis with nothing behind it however obvious it feels - and from the
    inside a fluent wrong mechanism is indistinguishable from a right one, which is exactly why the returned
    value is the only thing separating them. Specimens, each held confidently and each false: the serialized
    layer array is not `m_Layers`, and `FindProperty` returns nothing under that name; a tag sits on an
    object as a table entry rather than as the literal string, so renaming a tag carries its objects with it
    while remove-then-add silently untags every one of them; a lifecycle flag cannot be polled from the
    update loop for any work that blocks that loop, because a 1.63 second import ran with `isUpdating` true
    from start to finish and not one tick observed it; `AddTag` no-ops in silence on a duplicate, on an
    unknown remove and on an empty name rather than throwing. **Probing costs seconds.** A `[MenuItem]` in a
    scratch folder under `Assets/` is callable through `menu` the moment it compiles and answers what no
    amount of reading will; delete the scratch once it has answered. The tell that you owe one is reaching
    for "should", "probably" or "presumably" about behaviour, or writing a mechanism into code before any
    value has come back from the editor. A guess costs a compile cycle at best, ships a defect at worst, and
    the owner pays for every one. (`conduit-bridge-authoring.md`, `conduit-diagnostics.md`; scratch-folder
    selection: `conduit-project-workflow.md`.)

14. **⛔ NEVER LEAVE SAFETY OUT OF CODE. EVERY WAIT IS BOUNDED, EVERY INPUT IS NARROWED TO WHAT YOU
    EXPECT, AND NOTHING THAT CAN BLOCK RUNS ON THE MAIN THREAD.** Safety is part of writing the code, not
    a pass added to it afterwards, and there is no category of code exempt from that - a probe, a
    throwaway, a scratch menu item and a diagnostic all run in the same editor and hang it just as
    completely as product code does. **A timeout on one operation is not a timeout on the next one:**
    connecting with a limit and then reading without one is unbounded, because the limit protected the
    call that was never going to hang. An external endpoint that accepts and stays silent is the ordinary
    case, not the pathological one. **And narrow the input before you touch it** - matching a prefix where
    an exact pattern was available means contacting things that do not implement what you are about to
    ask for. Specimen: a synchronous read against an inter-process endpoint that accepted the connection
    and never answered, called from a `[MenuItem]`, wedged the editor until the process was killed from
    outside; the name filter had been loosened from an exact pattern to a prefix, so 5 endpoints were
    reached where 3 implement the protocol, and two of them could not have answered. **This qualifies
    rule 13:** a scratch probe is still the right instrument, and it is still main-thread code, so it
    carries bounds and runs its blocking work on a worker. Losing the editor costs the owner far more
    than the guess the probe was written to settle.
    (`conduit-bridge-authoring.md`, `conduit-code-style.md`)

15. **⛔ WHATEVER YOU SWITCH ON TO MEASURE, YOU SWITCH BACK OFF - BEFORE YOU REPORT.** `profiler-start`
    is answered by `profiler-stop`, `profiler-record-start` by `profiler-record-stop` (which puts the
    profiler and the Profiler window's Record back as it found them), and a module you enabled with
    `profiler-enable-module` is disabled again; the same holds for deep profiling, debug logging and any
    editor state changed for a check. **Then confirm it** with `profiler-get-status`: `enabled` and
    `recording` both read back as they were before you started. The editor layout saves Record and turns
    recording back on every time Unity opens, so a Record left on profiles every later session. Specimen:
    exactly that cost the owner frames in play mode across days until it was traced. Leave the editor as
    you found it. (`conduit-rendering-cost.md`)

16. **⛔ NEVER SHIP A VERB THAT DOES NOTHING AND REPORTS SUCCESS, AND NEVER TRUST A REPLY YOU HAVE NOT READ BACK.
    A STUB THAT ANSWERS "DONE" IS CHEATING THE CALLER, NOT A PLACEHOLDER.** Every reply states what Unity reads
    back after the call - the state before and the state now - and says when the two disagree, never what was
    asked for. Specimen: `profiler-enable-module` stored the module name in a Conduit-side dictionary and replied
    "Set profiler module 'GPU Usage' to True" while the Profiler window still read it inactive, so a whole
    measurement ran with GPU time at 0 and was reported as GPU data. A verb that cannot do its job refuses and
    says why; and before acting on any verb's success, confirm the effect it claims through a read of the real
    state. (`conduit-bridge-authoring.md`)

## Subject files - invoke the matching skill before you start

Each entry below states what the file OWNS, when to use it, and what NOT to use it for. Match on all
three. **These descriptions are here rather than in the skills on purpose:** a skill fires by matching
its own description, so it can never route you to a subject you have not already identified, and this
index is the only document guaranteed to be in context after a compaction.

**Match on the SUBJECT, not on a noun in your task.** The commonest way to pick the wrong file is to spot
one familiar word and stop reading - a console message seen in play mode belongs to the console file, not
the play-mode input file. Read every entry before choosing; hard stop 9 owns why a search can never make
this decision for you, and every subject file states its own boundary at the top.

<!-- CONDUIT-SUBJECTS:BEGIN -->

- **`conduit-code-style.md`** - House code style for this Unity project: correctness and hygiene, XML documentation requirements, naming, file layout and structure, and gated logging. Use when writing or editing ANY C#, UdonSharp, shader, or editor code here, however small the change. Do not use for questions that do not change code.
- **`conduit-project-workflow.md`** - How to scope and carry out a change in this project: surgical edits, reuse before adding, prefab-ready hierarchy, behaviour placement, editor builders, and how to verify visually with the scene view. Use when starting any multi-step change to a scene, prefab, or asset system. Do not use for a single-file code edit.
- **`conduit-compile-console.md`** - Compiling and reading the Unity console through Conduit: the end-of-change flow, why on-screen errors are usually stale, which verbs block and must not be polled, and what each compile trigger is for. Use when triggering a compile, reading the console, interpreting errors after an edit, or finishing a change. Do not use for authoring code.
- **`conduit-udonsharp.md`** - UdonSharp behaviour rules: creating behaviours, adding U# components, proxy versus backing UdonBehaviour traps, language limits such as blocked static fields and compile-time field initializers, runtime textures, VRCUrl and string-download rate limits, persistence, and THE UDON SURFACE OF THE VRC_ WORLD COMPONENTS - what a behaviour can actually call on a pickup, an object sync or the player's camera. Use when writing or editing an UdonSharp behaviour, adding a U# component, debugging one, or asking what Udon can call on a world component. Do not use for sync, ownership or network events, which are conduit-networking, or for authoring those components in the scene, which is conduit-world-components.
- **`conduit-networking.md`** - VRChat instance networking: choosing a sync mode and its byte budget, the syncable type list, ownership transfer and why SetOwner arbitrates nothing, OnOwnershipRequest running on both clients at once, what a late joiner actually receives and the side-effect replay that causes, network events and the underscore and NetworkCallable security model, and instantiation. Use when syncing a variable, transferring ownership, sending a network event, or fixing a world that misbehaves only with a second player. Do not use for UdonSharp language rules, which are conduit-udonsharp.
- **`conduit-vrctween.md`** - VRCTween, Udon's native tweening: the complete creation API across Transform, UI, Renderer, audio and virtual values, VRCTweenHandle state and control, ease and loop types, callbacks and timescale, ChangeEndValue reuse, validation behaviour, the networking pattern, and why DOTween ships in the SDK but is not callable from Udon. Use when animating anything over time, or before hand-rolling a Lerp in Update. Do not use for AnimationClips and AnimatorControllers, which are conduit-animator.
- **`conduit-audio.md`** - World audio: why Unity Play Mode spatializes through the old ONSP backend while VRChat ships Steam Audio, pairing an AudioSource with VRC_SpatialAudioSource without letting its louder defaults through, the voice setters and their matching getters that let a voice zone save and restore instead of guessing, the set-only avatar-audio family, why there is no world-wide voice block on the scene descriptor at all, what Load Type costs a clip before it plays, and the in-client Audio Sources debug page. Use when adding or tuning audio, importing a clip, writing a voice zone, or judging how a world sounds. Do not use for bundled video, which is conduit-video.
- **`conduit-vrchat-ui.md`** - uGUI INSIDE A WORLD - what a player sees and touches on a canvas: the setup a canvas needs before it responds at all, creating and reparenting canvas objects, text and font traps, masks and stencil copies, ScrollRect sizing, button and hover states, input-field behaviour including VRCUrlInputField, and showing another camera's view behind a head-following HUD: the layer split between that camera and the player's own screen camera, whose culling mask, field of view and aspect Udon can read and set. Use when building or fixing world-space UI, a HUD over a camera view, or when a control renders correctly and does nothing. Do not use for an INSPECTOR built with UI Toolkit, which is conduit-inspector-examples and conduit-editors - "UI" names two unrelated things in this project - nor for driving a control in play mode, which is conduit-vrc-input.
- **`conduit-build-validation.md`** - VRChat Build Panel validations, and shipping a world: the three SDK severities and which of them blocks a build, why an Auto Fix button is only a callback and carries no safety contract, the shipped fixes that rewrite project-wide settings or authored audio and materials, the 100 MB Android ceiling and the absence of any world performance ranking, and why an existing instance keeps running the old build after an upload. Use when answering a Build Panel question, before pressing Auto Fix, or when uploading. Do not use for runtime behaviour code.
- **`conduit-player.md`** - The VRCPlayerApi surface: which identity flags are evaluated per-client and must never be synced, movement setters and the matching getters that let a zone restore what it found, why hand-mounted objects anchor to tracking data rather than avatar bones, player tags being local-only with an unreadable bulk query, language, reaching into another player's PlayerObject, the per-player events beyond join and leave, why a station tells you neither WHO sits in it nor WHETHER anyone does, and the drone API a world can actually drive. Use when reading a player property, changing movement or velocity, handling a station or a drone, or treating players differently from one another. Do not use for voice and avatar audio, which are conduit-audio, or for ownership, which is conduit-networking.
- **`conduit-world-components.md`** - Authoring the VRC_ world components: why a non-whitelisted component silently does nothing and Cloth is disabled on Android, why Maximum capacity is a hard limit while Recommended only hides the instance from listings, what a pickup and a station each need beside them to work at all, why a mirror costs four times the rendering in VR, portals having to sit at the scene root, the camera dolly and its Collect Paths and Points step, placement zones, billboards and velocity trackers, and why avatar-side raycasts make your collider and layer setup a contract with other people's avatars. Use when adding or configuring a VRC_ component, or before claiming a Unity component works in a world. Do not use for their Udon scripting surfaces, which are conduit-udonsharp, conduit-player and conduit-dynamics.
- **`conduit-layers.md`** - Tags and layers, layer masks and the collision matrix: defining a tag or naming a layer slot and why overwriting an occupied one needs an explicit confirm, which layers VRChat reserves and restores at upload, why user layers 22-31 must be referenced by number rather than name, and how a failed name lookup silently produces a mask pointing at layer 31. Use when defining a tag or layer, assigning one, writing a layer mask, or changing collision. Do not use for sizing or placing the collider itself, which is conduit-scene-geometry.
- **`conduit-dynamics.md`** - VRChat Dynamics in worlds: why every runtime field write needs ApplyConfigurationChanges before it takes effect, Contacts having only enter and exit events with CalculateProximity for held state, the silent 16-tag ceiling, PhysBone grab and pose events and the absence of collider callbacks, and the six VRC Constraints with their real Udon surface including the source helper methods that replace the Sources list, and what Advanced Proximity Parameters and Material Contacts do and do not give Udon. Use when adding a Contact, PhysBone or VRC Constraint, or before concluding one cannot do something. Do not use for plain Unity colliders and triggers, which are conduit-scene-geometry.
- **`conduit-scene-geometry.md`** - Measuring and placing colliders, trigger volumes and bounds against model geometry: reading a mesh's localAABB, applying an imported model's root rotation so local height does not become world depth, and diagnosing a trigger volume that never fires. Use when sizing or positioning a collider or trigger, or when a volume is not detecting what is inside it. Do not use for UI RectTransform layout, which is conduit-vrchat-ui.
- **`conduit-probuilder.md`** - Modelling world geometry with ProBuilder through Conduit's probuilder-* verbs: that what it authors ships as an ordinary mesh and works in a VRChat world with no export or conversion, that face indexes shift after every operation that adds or removes faces, and that a shape needs BOTH generated lightmap UVs and the static flags before it bakes while neither failure is visible. Use when building or editing world geometry in the editor, or when asked whether ProBuilder works in a world. Do not use for what a mesh costs to render, which is conduit-rendering-cost, or for sizing a collider against geometry, which is conduit-scene-geometry.
- **`conduit-navmesh.md`** - Which NavMesh, AI and physics-query APIs Udon actually exposes: the 28 AI and NavMesh types including runtime navmesh data, query filters and triangulation, every physics cast family in plain, All and NonAlloc forms, and how to re-derive the list after an SDK update. Use before concluding a NavMesh, agent or physics-cast API is unavailable to Udon, or before hand-rolling a substitute for one. Do not use for pickup and player input handling, which is conduit-vrc-input, or for collider sizing, which is conduit-scene-geometry.
- **`conduit-performance.md`** - Udon's cost model and writing for it: why Udon runs orders of magnitude slower than plain C#, how every public method taxes that behaviour's event dispatch, partial classes splitting a large behaviour across files at zero runtime cost, why a disabled component beats an Update that returns early, slicing heavy work across frames with Stopwatch, and the string-keyed APIs that fail silently. Use before writing an Update, a cross-behaviour call, or anything that processes many items. Do not use for sync bandwidth, which is conduit-networking, or for what the SCENE costs to render, which is conduit-rendering-cost.
- **`conduit-shaders.md`** - Shader authoring and what the Quest platform actually restricts: why lowering #pragma target for mobile is wrong, guarding GrabPass and screen-space effects with SHADER_API_MOBILE instead, and the bar for claiming a platform limitation at all. Use when writing or editing a .shader, .hlsl or .cginc, choosing a shader model or blend mode, or before warning that a shader feature will not work on Quest. Do not use for material asset wiring, which is conduit-assets-prefabs, or for ShaderGUI layout, which is conduit-editors.
- **`conduit-lighting.md`** - Baking a world's lighting and the PC versus Android split: why baked ambient occlusion is the only AO a Quest world gets, the per-platform lightmap resolution, size, bounce and reflection-probe settings, why every dynamic object including the players is unlit without light probes and where those probes belong in three dimensions, and how to read a blurry bake, a seam or a dark avatar. Use when baking, placing probes, or judging what a world's lighting costs on Quest. Do not use for shader authoring, which is conduit-shaders, or for the Android size ceiling, which is conduit-build-validation.
- **`conduit-rendering-cost.md`** - What a world costs to RENDER and how to cut it: measuring with Conduit's own profiler verbs and why Deep Profile answers which call rather than how expensive, why a VRChat world is Built-in pipeline so no URP or SRP Batcher advice applies at all, static batching and GPU instancing and why the material is the batch boundary, occlusion culling including the transparent-occluder trap and the OcclusionPortal Udon can open at runtime, LOD being authoring-only because LODGroup is not exposed to Udon, and the ASTC and triangle budgets for Android. Use when a world runs badly, or before changing batching, culling, LOD or texture settings. Do not use for Udon's own cost, which is conduit-performance, for the bake, which is conduit-lighting, or for shader cost, which is conduit-shaders.
- **`conduit-video.md`** - Bundled video in a world: what Udon can and cannot do with VideoPlayer, showing many animations from ONE decoder by cropping a grid video with RawImage.uvRect, why that beats a sprite flipbook on size, and the VP8 and alpha rules for encoding it. Use when adding video, animating a UI element, or choosing between video and a flipbook. Do not use for URL-streamed video players, which are conduit-video-players.
- **`conduit-video-players.md`** - URL video players: why BaseVRCVideoPlayer is the ONLY player type Udon can call so resolution, autoplay and AVPro's speaker are Inspector-only, which parts of VRChat's built-in resync are actually exposed, why PlayURL never propagates so every client must play from synced state, why a seek before OnVideoReady is ignored, syncing a server clock stamp instead of a position and why the milliseconds call wraps, which VideoError values are worth retrying, and stabilising AVPro's flickering texture. Use when building or fixing a player that streams a VRCUrl, or when choosing between the two players. Do not use for bundled VideoClips, which are conduit-video.
- **`conduit-testing.md`** - Verifying a world actually works: why ClientSim cannot validate networking at all and where it actively lies, using Build and Test with several real clients, the Build and Reload fast loop that reuses the running client, the three-client late-joiner procedure that catches most sync bugs, and where the VRChat client writes its own log. Use when planning how to verify a networked behaviour or before claiming one works. Do not use for compiling and reading the console, which is conduit-compile-console.
- **`conduit-vrc-input.md`** - Driving the player and handling pickups in play mode through the bridge: pickup auto-hold, measuring a hold offset and computing a placement, and which ClientSim reads cannot be trusted. Use when driving the player, grabbing or placing a pickup, or exercising a world gimmick in play mode. Do not use for editing scenes or code outside play mode.
- **`conduit-assets-prefabs.md`** - Assets, prefabs and scenes: why asset moves must go through the editor, editing prefabs in Prefab Mode rather than through a scene instance, prefab linkage and unpack modes, standing up a new world scene, and hand-editing a unitypackage. Use when moving, renaming or deleting an asset, editing a prefab, or creating a scene. Do not use for code-only edits.
- **`conduit-editors.md`** - Custom inspectors and editor tooling for this project: the mandatory branded custom editor per behaviour, editor namespaces and folders, HelpBox usage, and the ShaderGUI duplicate-header rule. Use when writing or editing a custom inspector, a ShaderGUI, or any editor script. Do not use for runtime behaviour code.
- **`conduit-inspector-examples.md`** - The working UI Toolkit inspector framework shipped in Conduit's Examples folder and the ten demo behaviours that exercise every attribute in it, with an index saying which example draws which control, and the UI Toolkit traps they already solved: deferred repaint, USS tie-breaks, tinting an opaque sprite, IMGUI-backed controls, loading an asset by GUID, and progress that survives a panel rebuild. Use BEFORE writing any inspector control, grouping, validation, picker, preview or collection UI, and instead of writing one from memory. Do not use for the project's editor CONVENTIONS - namespaces, folders, the mandatory editor per behaviour, ShaderGUI - which are conduit-editors.
- **`conduit-diagnostics.md`** - The mandatory diagnostic and design sequences for this project, each producing named checkpoints in your output. Use when diagnosing a bug whose cause you did not directly observe, when a first fix failed, or before building anything non-trivial. Do not use for a change whose mechanism you have already observed.
- **`conduit-creator-economy.md`** - VRChat Creator Economy through Udon: which VRC.Economy members are actually exposed, ownership and purchase callbacks, supporter boards via ListProductOwners, authoring the UdonProduct asset, and the listing-id versus product-id trap. Use when touching products, listings, purchases, or ownership. Do not use for general UI or networking work.
- **`conduit-animator.md`** - Authoring AnimatorControllers, AvatarMasks and AnimationClips through Conduit's native verbs instead of throwaway editor scripts, and the traps their signatures cannot tell you: the only full-graph read-back, why a parameter's type cannot be changed in place, float equality conditions that never fire, blend-tree thresholds silently recomputed on assignment, and layer and parameter arrays that come back as copies. Use when creating or editing an animator, mask, or animation clip. Do not use for runtime animation code, or to look up a verb's payload, which the catalogue publishes.
- **`conduit-bridge-authoring.md`** - The RULES for writing Conduit's own code, and nothing describing what that code currently does: the contract every command must meet, how a blocking verb answers and survives a domain reload, how work outliving its own call reports back, the traps in writing values and references, and how a change reaches a connected client. Use when adding or changing a Conduit bridge command or the MCP server. Do not use to look up what an existing verb accepts, which the catalogue publishes from the verb's own signature, or how a class works, which is that class's summary.
- **`conduit-todo.md`** - The Conduit backlog, and ONLY what is unfinished: open bridge bugs, undiagnosed conditions, unanswered questions, and wanted verbs. Use when picking up work on Conduit itself, or when one of the known open conditions appears while working. Do not use for ordinary project work.
- **`conduit-doc-style.md`** - House rules for writing the Conduit convention documents themselves: what belongs in the injected index versus a subject file, bullet length, keeping the discriminator and cutting the narration, and deleting resolved entries. Use when adding to, editing, or reorganising any conduit-*.md file. Do not use when merely reading them.
<!-- CONDUIT-SUBJECTS:END -->

The block above is GENERATED from `ConduitSkills.Subjects`, which is the single registration point for a
subject document and the same array that produces the skill descriptions. Editing it by hand is pointless -
change the array, and it regenerates on the next skills sync. Nothing else needs updating.

If a task spans two subjects, read both. If you are unsure which applies, read
`conduit-project-workflow.md` first - it routes the rest.

**The files above are the authority and are ALWAYS read first. A gap in them is a signal to keep looking -
the SDK source, the API itself, or the user - never a licence to fill it from memory.**

These same files are installed as agent SKILLS by the Conduit window (`ItsRenji/Conduit` > Connect), and
invoking the skill is how a subject is meant to be loaded: it carries the document the window generated
from source, so it is the delivery mechanism rather than a convenience on top of one. On a client that
supports skills the matching subject can also load itself when you begin that kind of work, but a skill
fires by matching its own description, so it cannot be relied on to fire for work it did not recognise -
invoke it deliberately. The table above is the fallback for clients without skill support, and it remains
authoritative: where there is no skill to invoke, you are still required to read the file.

## Conduit tool discovery

- **At the start of a new Conduit/Unity session, call `unity-tool-list` with no filter** to get the bridge
  command map rather than guessing from partial tool-search results - and **a compacted context IS a new
  session** for this purpose. Filtered calls are for narrowing nearby verbs once that map is known - their
  multi-term form is an OR filter - and the unfiltered call is the reliable cold-start escape hatch.
- **A discovery call covers the bridge's verbs plus the server's own tools, and it shows exactly the ones
  `tools/list` would serve YOU.** `help`, `commands` and `unity-tool-list` append them with a required
  argument shown `<name>` and an optional one `[name]`, read back from the schema the server validates
  against, and a FILTERED call still carries that appendix, which is the cheap way to re-read it. A tool
  gated to one harness appears for that harness ONLY - the gate exists so no other harness learns the
  capability is there at all, so never quote a gated tool's name or arguments to another harness.
- **When no typed wrapper is exposed for a verb, take the exact name from `unity-tool-list` and search for
  that exact name** so the client exposes the matching tool; `conduit-run` calls it in the meantime. A verb
  present in `unity-tool-list` and absent from `tools/list` is a defect in the exposure layer, never
  grounds for a one-off editor script (`conduit-bridge-authoring.md`).
- **Prefer direct Conduit verbs over throwaway editor scripts or shell workarounds.** Shell stays correct
  for filesystem inspection and for files Unity does not compile; Conduit owns every scene, asset,
  component, prefab, play mode, console, screenshot and VRChat operation - and UdonSharp scripts, per hard
  stop 6.
- **When the project path contains a space, never let it reach an unquoted shell variable.** `for f in
  $files`, `$(basename $d)` and a bare `$path` all word-split on it, and the failure is quiet: the command
  runs against the truncated path and reports success having done nothing, or edits the wrong file. Prefer
  the dedicated Edit / Write / Grep / Glob tools, which take a path as one value and cannot split it. When
  shell genuinely is the right tool, quote every expansion (`"$f"`) and pair `grep -rlZ` with `xargs -0`.
- **A path containing a space is safe through the typed MCP tools.** The top-level tokenizer is
  quote-aware and the verbs that subdivide a pipe section parse it from the right, so
  a path such as `D:/Worlds/My Project/Temp/board.png` arrives whole. When hand-typing a RAW bridge command, quote a
  spaced value (`reimport "Assets/Scenes/Drink System.unity"`), because an unquoted space still separates
  two arguments.
