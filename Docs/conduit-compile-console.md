# Compiling & reading the console

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before triggering a compile, reading the console, or finishing a change.**
>
> **This file owns the MACHINERY of compiling and reading output, and nothing else goes in it.** What a
> verb ACCEPTS is published with the verb itself; why a member is not callable from Udon belongs to
> `conduit-udonsharp.md` even when you found it in a compile error; how to WRITE the code you are
> compiling belongs to `conduit-code-style.md`; and an open bug in a verb belongs to `conduit-todo.md`.
> A console message is not automatically this file's subject - ask what the message is ABOUT.

## A Conduit verb finishes the job itself

**A verb that blocks has ALREADY done the whole job by the time it returns - the compile, the domain
reload, and its own post-compile report.** Read what it returned and move on. Chasing one with `reimport`,
`domain-refresh` or a console read re-asks the question it just answered, and each of those is a tool call
and tokens the user pays for. (`status` is hard stop 3.) The verbs that behave this way are listed under
"Blocking verbs" below.

**The `console-clear-logs` -> `domain-refresh` -> read-the-newest-entry flow is for work that did NOT go
through a verb** - an external editor, the user's own edit, a focus-triggered refresh. A clean tail or a
`…finished` line means done; move on. There is no polling step and no second opinion.

`domain-refresh` is the single correct finaliser there: it is lighter than `reimport` and it also settles
newly added serialized fields. Skip it entirely if the user has already refocused or reconnected, because
that reload has already happened.

Filesystem edits to NON-CODE assets (an agent's Edit tool, an external editor) are fine - Unity imports
them automatically. A `.cs` is NOT one of those (hard stop 6).
**After replacing an existing non-code asset such as an FBX or texture, STOP: do not call `refresh`,
`assets-refresh`, `reimport`, or `domain-refresh`.** Wait for Unity's automatic import to settle instead.
Forcing another synchronous refresh can race Project Browser preview regeneration, invalidate its artifact
state, and turn an `AssetPreviews.cpp` `previewArtifactID` assertion into a native editor crash.

`domain-refresh` imports pending filesystem edits first and then reloads the domain exactly once, so the
end-of-change flow is safe for unattended multi-file batches and leaves the next focus-triggered
auto-refresh nothing to re-detect.

## Errors on screen are STALE until the final compile says otherwise

**"I see errors" after editing defaults to "they are stale", not "something broke".** This overrides any
instinct to double-check. UdonSharp recompiles after EVERY edit, so a batch of edits floods the console
with errors from half-applied intermediate states - a helper called one edit before its definition lands, a
symbol seen mid-rename. They vanish when the next edit lands.

When a change is finished and errors are showing: do NOT re-read the file to check, do NOT fire another
compile, do NOT narrate or react to them. That panic-verify loop is the single most wasteful thing done on
this bridge.

- **"finished" is the verdict.** Unity and UdonSharp only print `Compile … finished` when the build
  actually completed; a real failure terminates on the error instead. So "finished with N error(s)" means
  the compile SUCCEEDED and those N errors are history.
- **A CURRENT error means the compile did not succeed, so the edit is on disk and unproven.** "Finished
  with N error(s)" retires STALE errors; it says nothing about an error standing right now. Clear the
  error, then re-test - never carry on as though the change is in effect.
- **The report separates this compile's errors from older ones on TWO floors.** Anything logged before the
  newest "compile ... finished" line is counted as stale ("N older error(s) predate this compile"), and so
  is anything below the Console entry count recorded when the command deferred. That second floor is what
  answers for a compile that logs no finished line at all - a delete or a move - which otherwise had no
  boundary and reported the WHOLE buffer as its own. When reading the console by hand instead, apply the
  first rule: an error older than the newest "finished" line is history.
- **"Cannot be attributed to this compile" is not "your edit is fine".** With neither floor available the
  verb lists the console's errors while explicitly declining to claim them, because errors that merely
  predate the command are evidence about nothing. Read that wording as "go and look", never as a pass.
- **Know which errors can even linger.** Unity DROPS compiler diagnostics (CS errors) on the next
  successful compile, so a transient `CS0103` from a half-applied batch is gone by the time the final
  compile finishes. What survives a reload is LOGGED errors - UdonSharp's own `Debug.LogError`s and the
  reserialization flood (`conduit-udonsharp.md`). So a CS error still on screen after a clean "finished"
  is worth a second look, while a lingering logged error usually is not.
- **Clear the console before every recompile** and the problem cannot arise: the buffer then holds only
  this compile's output, so the newest entry is the whole truth.
- **Read a busy console in two steps: find, then fetch.** `summary` and `grep` are the cheap half -
  whole messages, NO stack traces, every line tagged `#n` - and `entry <n>` then spends tokens only on the
  entries that earned it. Reaching for `tail` first renders a full stack per entry and turns a handful of
  entries into an enormous response for information you almost never wanted.
- **Entry numbers are ABSOLUTE in the current buffer and `#1` is the OLDEST.** A number from `summary`,
  `grep`, `head` or `tail` means the same thing to `entry`, and stays valid until the console is cleared.
  `entry` takes several at once (`entry 3,7 12`) and reports an out-of-range number instead of failing.
- **`grep <text>` searches the MESSAGE, never the stack frames.** Searching for a type name therefore finds
  the entries ABOUT it rather than every entry whose call path merely mentions it.
- **`head` is `tail` from the oldest end.** Use it when the FIRST failure matters more than the last, which
  is most cascades - the real cause is at the top and the rest are consequences.
- **A bridge read cannot be suppressed by the console WINDOW's filters, so never diagnose an empty read as
  a view-state problem.** Every read forces the level filters on and blanks the search filter for its own
  duration. When one returns 0 against expectation, `%LOCALAPPDATA%\Unity\Editor\Editor.log` is the ground
  truth to check the reader against.
- **Read the newest entry first.** Request the smallest tail that includes it. Expand further back only
  when that newest entry is itself an error needing context.
- If you catch yourself about to reimport or re-read a file because of an on-screen error, that is the
  tell you are breaking procedure - stop.

## Udon exposure answers come from the SECOND compile stage

- **Ask `udon-exposure` BEFORE compile-probing.** It reads the SDK's node-definition registry - the
  whitelist itself - so one call answers what the one-member-per-compile probe below approximates, and it
  covers events too. Reserve the compile probe for confirming a hit actually compiles in context.
- **Fix every C# error before reading an exposure result.** UdonSharp runs Roslyn first and the Udon
  whitelist second, so a single `CS0234` from a wrong namespace suppresses the whitelist stage entirely -
  and a clean C# compile is NOT evidence that a member is callable from Udon.
- **The whitelist stage reports ONE member at a time**, so probing N candidate APIs costs up to N
  compiles. Order the probes down a single file and read the reported line: everything ABOVE it has
  already passed the whitelist, which is the cheapest way to clear many members at once.
- **`Method is not exposed to Udon: '<member>'` is the whitelist verdict; a `CS####` code is plain C#.**
  Only the first says anything about Udon exposure.

## Blocking verbs return when the work is done - never poll `status` after one

These WITHHOLD their response until the compile and domain reload have settled, then return what they did
plus the post-compile report: `domain-refresh`, `refresh` / `assets-refresh`, `create-usharp`,
`create-usharp-batch`, `delete-usharp`, `udon-batch-edit`, `script-update-or-create`, `script-delete`,
`package-add`, `package-remove`, and `reimport`, `assets-copy`, `assets-move` or `assets-delete` of a
compilation input - a `.cs`, `.dll` or `.asmdef`, or a folder holding one.

When one returns, the work is already finished and there is nothing left to wait for, so polling `status` in
that window re-asks a question the verb just answered. It no longer times out there, because `status` is
served from captured state on the listener thread, and that is the new trap: a reply saying nothing is
running is not proof the work finished, since the identical reply comes back between two phases of work
still in progress.

- Deleting a script recompiles like any other script change, and UdonSharp rebuilds every U# program from
  it. Deleting any other asset type imports synchronously and answers immediately.
- **A report showing `isUpdating: True` in its own status block is EARLY - do not trust it, read the
  console.** Reports wait for idle, not merely for the reload, because `[DidReloadScripts]` fires while
  Unity is still reserializing scenes and prefabs, which is exactly when UdonSharp logs its
  `OnBeforeSerialize` / `OnAfterDeserialize` exceptions. A report built from the reload callback once read
  an empty console and truthfully said "no errors" seconds before six arrived.

## Reading a busy editor: `/health`, never `status` and never the log

`GET /health` on the transport port is answered on the listener thread, so it replies while the main thread
is blocked. Nothing outside Unity answers the question it answers: `status` reads main-thread-only lifecycle
properties and queues behind the work being asked about, log silence cannot separate work from a hang, the
TCP connection table idles to zero on a live connection, and process CPU is shared with every other
application on the machine. Unity's own progress records are the only source that names the work.

Port, identity and bearer come from the descriptor the editor publishes at
`Library/ItsRenji/ConduitTransport/<identity>.json`. Send the identity as an `X-Conduit-Project` header and
the token as `Authorization: Bearer <token>`; without both the request is refused.

- **`activity.tasks` is what the editor is DOING.** Each entry carries Unity's `name` ("Compiling Scripts"),
  the live `description` naming the phase ("Csc Assembly-CSharp-Editor", "Running Backend"),
  `elapsedSeconds` on Unity's clock, and `responding`.
- **The phase changes between reads.** Unity pushes these records about four times a second during a
  compile, from its own clock rather than from the editor update loop, which does stop.
- **`activity.importing` is the separate answer for an ASSET IMPORT, which no task ever covers.** Unity
  registers no progress record for an import, so this comes from its import callbacks instead and carries
  `assetPath`, `assetsSeen` and a live `elapsedSeconds`.
- **Do not expect the update loop to see an import.** Every lifecycle flag is a main-thread property and an
  import owns the main thread, so a poll running on that loop is blind to it: a 1.63 second texture import
  was measured with `isUpdating` true throughout while not one update tick observed it.
- **`capturedSecondsAgo` is the age of that picture and is never added into a task's `elapsedSeconds`.**
  Near zero means the figures are current; growing while `busy` is true means Unity has stopped reporting,
  which is the one fact a summed figure would hide. `activity.importing.elapsedSeconds` is the exception
  and is computed live from a real start instant, so it keeps counting through the block.
- **`activity.lifecycle` carries every field the `status` verb reports**, held from Unity's own transition
  events rather than read on demand. Each of those states can only change on the main thread, so a held
  value cannot fall behind while that thread is busy: nothing can change it until the thread is free.
- **A terminal `status` read while `reloading` is true is not a verdict.** A compile that succeeded was
  observed reading `Failed` for the two seconds its replacement domain took to load, then `Succeeded`. The
  verdict comes from the blocking verb's own post-compile report.
- **`mainThread` answers only whether the update loop is ticking** - `lastTick`, `ageSeconds`, `ticking` -
  and carries no durations, because a tick that stops also stops any counter carried in it.
- **Read the two together.** A stale heartbeat with fresh `activity` is named work in progress; both stale
  is a wedged or reloading editor; a ticking heartbeat with no tasks is idle.
- **`clientProfiles` is what each client declared at initialize** - name, version, protocol and its
  capabilities object verbatim. It is the only place a client names itself; the bearer and the identity
  header say which project is being driven, never who is driving it.
- **`mcpRequests` counts each JSON-RPC method received since the domain loaded**, after the bearer and
  identity checks. It answers whether a client actually asked again after a tool-list notice: a
  `tools/list` that rises by one within a second of a push is a refetch, and one that does not move is a
  client that armed nothing at its handshake.
- **`toolListNotice` is the notice this domain sent at load, when one was due.** `revision` and `previous`
  name the two catalogues, `delivered` how many streams took it, and `sent` counts every notice since,
  on-demand ones included. An empty `revision` beside a `previous` means the compile changed no signature.

## Never create or edit scripts in Play Mode

**Exit Play Mode before creating, editing, or writing any UdonSharp or Unity script.** During a known
live-testing period, read `editor-application-get-state` once immediately before the first script write.
Do not repeat the check before every file unless the user says they entered Play Mode or the agent started
it; outside live testing, one check before a batch is sufficient.

**Shaders are NOT scripts for this rule.** A `.shader` or `.hlsl` edit hot-reloads live and a mid-play
edit is the CORRECT way to iterate on one - see Per-verb specifics.

Writing scripts in Play Mode can leave deferred UdonSharp compiler workers orphaned after play exits and
interfere with later editor or client work. If `isPlaying` or `isPlayingOrWillChangePlaymode` is true, stop
and exit play before the first filesystem write. Never use a mid-play script edit as the mechanism that
ends the test.

**When Play Mode is active, Unity deliberately defers script compilation until Play Mode exits.**
`isPlaying: true` together with `isCompiling: true` after a script change means the compile is waiting for
Play Mode to end; it is NOT a hang and will not become idle while play continues. Never poll it expecting
completion, never diagnose it as stuck, and never begin a compile workflow while Play Mode remains active.

Exiting play is the compile trigger. Do NOT follow the exit with `reimport`, `refresh`, or `domain-refresh` -
the request queues behind the auto-compile the exit already started and times out. Treat it as
non-bridge-triggered work: exit play first, poll `status` until idle, then read the console. Prefer exiting
play BEFORE editing editor/bridge code at all, since a mid-play domain reload tears down ClientSim.

## `status` is the wait loop for non-bridge-triggered work only

`status` is read-only: `isPlaying`, `isPlayingOrWillChangePlaymode`, `isPaused`, `isCompiling`,
`isUpdating`, and whether a new UdonSharp script still has its program-asset link queued.

Poll it only when nothing on the bridge started the work: a filesystem edit, an external editor, Unity's
auto-refresh on focus, or a play-mode transition. Poll until `isCompiling` and `isUpdating` are both false,
then read the newest console entry.

- **It answers while the editor is busy, and so does `editor-application-get-state`.** Neither needs the
  main thread: the flags come from captured state, the marker is a file test, the version is a constant.
  Both are served on the listener thread exactly as `/health` is. Measured at 2 ms mid-import, reporting
  `isUpdating: True` in a window where nothing on the editor update loop can observe that flag at all.
- **`timeSinceStartup` is reproduced from a load-time anchor rather than read.** The editor clock was
  measured advancing 2.009 s across a 2.000 s block of the main thread, matching wall time to 0.000 s, so
  it is a wall clock and an anchored offset returns what the property itself would. A gate compares the two
  on every run, so drift that theory says cannot happen would still be caught.

## Expected ClientSim noise - messages that are NOT your bug

ClientSim fabricates scene objects in play mode that VRChat itself never creates, so some console
entries are guaranteed and have nothing to do with the change you just made. Recognise these instead
of investigating them, and do NOT "fix" them - the fix is what breaks something.

- **`Could not load the program; the UdonBehaviour on '<station>' will not run.`** - logged once per
  `VRCStation` per registration pass, so two stations produce four lines.
  `ClientSimStationHelper.CheckForMissingComponents()` adds a BARE `UdonBehaviour` to any station that
  has none, purely to give it a "Sit" interact in the editor, then tries to fill it from a hardcoded
  `Assets/VRChat Examples/Prefabs/VRCChair/StationGraph.asset`. VCC projects do not ship that legacy
  folder, so the program stays null and `LoadProgram` fails. The whole block is inside `#if UNITY_EDITOR`
  and the SDK's own comment there reads `// TODO properly load udon chair program asset`.
  **Seating still works**, because `ClientSimStationHelper` performs it in C# rather than through the
  missing program - so a working station alongside this message is the expected combination, not a
  contradiction. Nothing of ours can suppress it and nothing should.

## Compile triggers are not status checks

`reimport`, `refresh`, `assets-refresh`, and script-saving commands START new import/compile work. Never
call them to nudge, confirm, try again, or make the compiler notice a file. Repeated nudges restart or
extend the work rather than speeding it up, and they create fresh noise that hides the real final state.
To verify, read the console.

## Per-verb specifics

- **`udon-batch-edit` needs NO follow-up trigger.** It blocks through its one compile and returns
  per-edit evidence plus the post-compile report, so calling `reimport` or `domain-refresh` after it is
  waste and a later focus scan has nothing to re-detect. Its own constraints are published with the verb -
  read those rather than guessing at them, because one failure anywhere refuses the whole batch with
  nothing written. Reserve `script-update-or-create` for new files and whole-file rewrites.
- **`create-usharp-batch` is MANDATORY for SEVERAL new U# behaviours**, because N separate `create-usharp`
  calls each cost their own blocking compile and domain reload while the batch costs one. `create-usharp`
  is for ONE behaviour, or to relink an existing file.
- **A `.cs` written OUTSIDE the bridge needs no explicit compile trigger** - an external editor, or the
  user's own edit. Writing the file makes Unity auto-refresh and recompile the U# programs on its own;
  poll `status` until idle, then read the console. That is how to RECOVER from a write that already
  happened, never a second way to edit (hard stop 6).
  Reserve `reimport <touched-script>` for what auto-refresh misses: files added outside the `create-usharp`
  flow, `.asmdef` / `.dll` / native-plugin changes, or a project with Auto Refresh disabled. One reimport
  COMPILES a whole batch (UdonSharp rebuilds all its programs from a single U# reimport) but imports ONLY
  the named file - the other edited files stay unimported until the next refresh, so finish a multi-file
  batch with `domain-refresh`, or a later focus scan pays a second compile for them (and a compile queued
  that way during Play Mode locks the bridge out of stopping play).
- **`reimport` is not a domain-refresh tool.** After adding a new `[SerializeField]` or serialized member,
  component read/write reflection may still show the old field list until a domain reload. Use
  `domain-refresh`, then reconnect the MCP bridge if the client still has stale tool or serialized-field
  metadata. Do not keep reimporting to fix a missing new field. Same applies when a newly added U#
  component has linked its backing UdonBehaviour but Conduit still reports stale backing metadata.
- **Deleting a UdonSharp behaviour is a PAIR operation - use `delete-usharp <path.cs>`.** It removes the
  `.cs` AND the linked `UdonSharpProgramAsset` in one call. `assets-delete` on a U# script REFUSES and
  points here, because deleting the script alone orphans the program asset. The pair is matched on the
  program asset's own `sourceCsScript` link, NOT on a same-name convention, so plain C# - MonoBehaviours,
  editor scripts - has no pair and deletes straight through `assets-delete` as normal.
- **Editing a `.shader` or `.hlsl` needs NO compile trigger and NO play-mode exit.** Unity hot-reloads
  shader source on save, live, while play mode is running - do NOT `reimport`, do NOT stop play, and do
  NOT re-enter play to see the change. Shaders are the ONE asset class where a mid-play edit is correct,
  which makes iterating a shader against a running scene effectively instant.
- **Shader imports emit no success message.** After `console-clear-logs` -> import/refresh -> idle, an
  empty console IS the positive verdict. Failures do emit errors. Do not wait for or invent a shader
  "compiled successfully" line that Unity never produces.
