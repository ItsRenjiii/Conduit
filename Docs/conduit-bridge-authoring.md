# Extending Conduit itself

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before adding or changing a Conduit bridge command or the MCP server.**

Conduit is your own tooling. **Hold it to a HIGHER bar than asset code, not a lower one.** A
happy-path-only or oversimplified command does not fail once and move on - it silently misleads every
future session, and you waste time rediscovering the gap and fixing your own mistake. The recurring
failure has been skimping here; don't.

**This file holds RULES, not descriptions of what the code currently does.** What a verb accepts and
returns is published by the catalogue, which is generated from the verb's own signature and cannot drift
from it; how a class works belongs in that class's summary, where somebody about to change it is already
reading. A description copied to here is paid for by every session that loads this document and goes
stale the first time the code moves without it. If a line answers "what does this do" rather than "what
must I obey", it belongs in the source or the catalogue (`conduit-doc-style.md`).

**Conduit's own source lives in one folder alongside these documents - scope a search for it there, and
resolve where that folder actually sits rather than assuming a path.** It ships embedded under `Assets/`
in some installs and as a package in others. A project-wide sweep is two orders of magnitude more files
and also reaches build staging, which hands back Conduit at an older shape (`conduit-diagnostics.md`).

**Open bugs, undiagnosed conditions and wanted verbs live in `conduit-todo.md`.** Check it before starting
bridge work, and delete an entry when it lands rather than marking it done.

**Prove the mechanism in scratch before it becomes a verb.** Choose its folder through the scratch-work
procedure in `conduit-project-workflow.md`. A `[MenuItem]` under `Assets/` is callable through the `menu`
verb the moment it compiles, so disproving a guess is cheap -
do it there, then port the proven code. The guesses are often wrong: reading a window's controls looked
solved twice (UI Toolkit tree, then positional probing) before scratch tests showed the first returns
almost nothing and the second locks the editor for minutes, and a reflection target sat in a different
namespace than every prior session assumed. Iteration cost is not the reason to work in scratch -
deploying to the bridge is just as fast. The reason is that an unproven mechanism written straight into
the bridge ships a guess as product behaviour. **Scratch is still main-thread editor code: bound every
wait and keep blocking work off that thread** (hard stop 14).

**Use Roslyn to manipulate C# source, never a regex - and it already ships in the project.** UdonSharp is
a C# compiler, so every VRChat project carries `Microsoft.CodeAnalysis.dll` and
`Microsoft.CodeAnalysis.CSharp.dll` under
`Packages/com.vrchat.worlds/Integrations/UdonSharp/Runtime/Plugins/` - no external SDK and no dependency
on which Unity versions happen to be installed. A member's `FullSpan` carries its doc comment, its
attributes AND its preprocessor trivia, which is exactly what a pattern cannot see: splitting these files
on line ranges cut nine `#region` directives away from their `#endregion`, and every one of those files
failed to compile. Classify members by NAME, never by line position - a positional split silently files
whatever happens to sit inside a range under that range's heading.

**A bridge limitation is a TODO, not a licence to build a workaround into the product.** When the bridge
cannot reach something you need in order to verify your own work, the answer is an entry in
`conduit-todo.md` - never an extra command, menu item, or code path added so the agent can reach it.
Scaffolding shipped as a feature gives users a second way to do the same thing, and the two drift.
To actually get it verified: drive the editor through computer use, or just ask - the user is at the
machine and a press they report IS the verification.

**NOTHING IS EVER A HARD DEPENDENCY - an optional package being ABSENT must not break the build.**
Conduit ships into projects that have none of what you are looking at, and a direct `using` on an
optional package takes the WHOLE editor assembly down there rather than only the verbs that need it. The
VRChat SDK and UdonSharp are reached by reflection for exactly this reason; ProBuilder was not, and so
shipped broken for every project that had not installed it. Two routes, by size of the surface:
reflection where it is small, or a `versionDefines` entry in Conduit's `.asmdef` gating the code with
`#if` where it is large. **The guard covers every seam that names those verbs** - the dispatcher call,
the catalogue's signatures and notes, any schema repair, the gate's expected count - because a catalogue
still publishing a verb whose handler compiled out is the same defect wearing a different hat. Two
mechanical facts worth having: an unresolved asmdef reference is TOLERATED rather than an error, so the
optional assembly is listed and the define decides; and the absent path is testable by pointing the
version expression at a range that cannot match, so proving it needs nothing uninstalled.

## The contract every command must meet

- **Truthful, specific feedback - never report success for a no-op.** A command returns exactly what it
  changed (which fields, which references, resolved to what), a real error string when it could not do
  what was asked, and a warning for inputs it ignored instead of swallowing them. **A blanket "Modified
  component X." returned when nothing changed is the canonical bug** - the caller trusts it and moves on
  broken. Make every response something you could act on without a follow-up read.
- **EVERY verb carries a note saying what it does, even where that is one sentence.** A caller sees the
  name, the signature and the note and nothing else, so a verb without one publishes as "Unity editor
  bridge command X" and teaches a reader nothing they could not already guess. Where behaviour is not
  readable off the name - which coordinate frame a vector is in, a filter's syntax, what an optional
  confirm token accepts - the note says so, because silence there reads as "obvious" and is wrong.
- **NAME every alternation group in the signature, as `[| section:members|constants|all]`.** The published
  property takes that name. Without one the generator invents `option`, then `option2`, which appear
  nowhere in the signature the caller is reading, so the property gets guessed and the call is refused.
  A name a reader cannot predict from what they are looking at is the same defect as no name at all.
- **Apply atomically.** Resolve and validate every part of a payload before mutating anything; if one
  field cannot be resolved, return an error and change nothing rather than half-applying.
- **Only a project-local embedded package is writable; registry, cache and external local packages are
  refused.** The gate is Unity's own package registration plus a resolved-path match, because neither can
  be faked by planting a file in a folder - and an opt-in marker file cannot be a precondition in a tool
  that ships to other people. A VPM update still overwrites whatever was edited in a managed package, so
  treat a fix there as temporary.
- **Accept the same value shapes the read side emits**, so a value read back can be written straight back:
  `{"instanceID":N}`, `{"guid":"…"}`, a quoted scene path / name or asset path, and null/none to clear.
  Diverging the read and write representations is exactly how silent write failures sneak in.
- **Reuse the resolved helpers, don't reinvent per command:** `ResolveObjectReference`,
  `FindSerializedField`, `TryFindGameObject`, `SyncUdonSharpProxy`, the `Json*` writers,
  `SplitCommandSections`. A one-off shortcut that skips them is how behaviour drifts between commands.
- **A command that mutates a component ends at `ApplyModifiedProperties()`, and in play mode pushes what it
  changed.** Take `CaptureUdonSharpAuthoredValues(component)` before the write and pass it to
  `SyncUdonSharpProxy(component, before)` after: only the fields that differ are written, each with
  `SetProgramVariable`, and it is a no-op in edit mode, on non-U# objects and without UdonSharp. See `conduit-udonsharp.md`.
- **A new blocking verb keeps its own specifics via the internal `__deferred-note <summary>` rerun
  command**, which prints the summary and then the post-compile report. Use that shape rather than falling
  back to the generic report and losing what the command actually did. A multi-line summary is safe - it
  rides the ledger's JSON recipe, which escapes a newline like any other character.
- **A verb that compiles defers before the import**: `TryDeferUntilReload`, import, return `string.Empty` -
  the shape in `assets-delete`. `Refresh(ForceSynchronousImport)` schedules the compile, it does not run
  it, so returning straight after it is too early.
- **Ask `PathTriggersScriptCompile` before mutating a path**, because a deleted folder cannot afterwards
  be inspected for what it held.
- **Publish every verb under BOTH spellings - the raw hyphen name and its snake_case alias.** A verb that
  `unity-tool-list` names and `tools/list` carries under neither form has no route from a typed client at
  all, and the caller's next move is a one-off editor script for something the bridge already does. That is
  a defect in the exposure layer, not stale client metadata.

## How a blocking verb answers, and how its answer survives a reload

- **A durable verb blocks the LISTENER thread; the MAIN thread must stay free to drain it.**
  `AnswerWhenTerminal` waits `TOOL_WAIT_MS` while the editor drains and executes the job on the main
  thread, so the wait holds up that one caller and nothing else. Block the MAIN thread on a durable
  response and it deadlocks against the only thread that could finish it: the client's own timeout fires
  first and the caller gets an EMPTY result rather than an error, because the response was never written.
  A test or tool driving the endpoint from `[MenuItem]` code must pump the drain on a worker, never call
  and wait inline.
- **The result is the answer; the job handle is not.** A verb that returns its own plumbing makes every
  caller ask a second time. The handle comes back ONLY when the wait genuinely runs out, which leaves the
  job running and still readable instead of losing it.
- **Read the job id from `_meta.conduitJob.jobId`, never by parsing the prose.** The text is written for a
  reader and is free to be reworded; a consumer regexing it breaks silently the first time it is.
- **A verb that causes a domain reload must NOT answer from the old domain.** Check
  `ConduitReloadResponseHandoff.HandedOff` and return without writing: the new domain owns the only valid
  response path and will write the terminal result once the job really finishes.
- **Never simplify that handoff.** Both halves are load-bearing - it duplicates the kernel handle AND
  detaches the managed socket before the old listener closes - and removing either silently loses the
  response rather than failing visibly. Its own summary says why; read that before touching it. It is also
  platform specific and falls back to the ordinary durable-job path, so **no verb may depend on the
  handoff succeeding.**
- **A signature's angle-bracket group is published as a literal enum, so never spell an argument's
  accepted KINDS that way.** `udon-program <sceneObject|assetPath>` published an enum of the words
  "sceneObject" and "assetPath" and refused every real target with `$.option: value is not in the
  advertised enum` before the verb ran. One placeholder naming the kinds is the correct form, as in
  `<targetOrAssetPath>` or `<nameOrPathOrId>`. **An alternation is a closed enum ONLY when every branch is
  a literal** - `<replace|append|index>` is a real enum, `<keysJson|null>` is a payload with a sentinel,
  and exposing the latter as an enum blocks the payload client-side before it can reach the bridge.
- **Rename an argument in the bridge's own signature, never in a catalogue repair.** The description
  carries the signature verbatim and a client can build from it, so a repair that renames a schema
  property publishes two contracts and the tool refuses its own documented call. A repair may retype what
  the signature names; it may not rename it.

## Reporting work that outlives its own call

- **Never keep a client-to-conversation map; take the conversation as a parameter.** Nothing on an
  incoming request identifies a conversation, so a map assumes one session per harness and answers the
  wrong one the moment a second opens. Where a harness DOES name it in `_meta`, fill it from the request
  instead of asking - and publish that harness the verb without the parameter, because `tools/list` is
  built per caller and a published parameter a model cannot answer is worse than no parameter at all.
- **An optional return address is the shape to avoid.** It is silently absent exactly when a caller forgot
  it, so the failure is an hour of work reporting to nobody.
- **Pick the delivery shape from what the harness EXPOSES, and prefer the one running OUTWARD** - from
  inside the harness to the bridge - **but NEVER at the price of a RUNTIME the machine has no reason to
  carry.** Nothing outward has to be discoverable from the editor, which is what makes it the
  cross-platform choice. Read as absolute, though, it bought a resident Node process per open
  conversation, gated on `node` being on PATH, to perform an inbox write that already shipped in C# - so
  the platform-specific ingress got written anyway, in a second language, and the preference paid for
  nothing. **A one-shot that hands over an address and exits is outward too**: a session-start hook
  leaves nothing resident, adds no dependency, and the write is one class per platform.
- **An installed artifact shared across projects is reference counted, and the client's own config is the
  count.** Remove must unregister only once no Conduit entry remains, or the first project's Remove ends
  delivery for every other project silently.
- **Confirm an inter-process endpoint by BEHAVIOUR, never by its name**, and match names on an anchored
  pattern rather than a prefix. Sibling services publish endpoints sharing the leading words and
  implementing something else: a loosened match reached 5 endpoints where 3 carried the protocol, and the
  extra two are precisely the ones that accept a connection and never answer (hard stop 14).
- **Prove an INTERVAL with a wait, never with work.** What needs proving is that something fires once the
  interval elapses, so manufacturing the interval out of real editor work proves the same thing at the
  editor's expense: a 182k-triangle ProBuilder unwrap held the main thread for six minutes, leaving
  `/health` the only thing still answering and the queue draining into nothing. Call the send directly
  from a `[MenuItem]`, or lower the interval.
- **Report acceptance, never receipt, and keep "unknown" distinct from "refused".** What happens after a
  frame is written belongs to the harness, which may queue, hold for approval, decline, expire or drop it.
  Only a refusal is safe to retry - retrying an unknown puts the message in the conversation twice.
- **A delivery surface gated behind a preview flag or an authorisation the owner has not granted is not a
  route to implement.** Being able to reach it and being allowed to ship it are different questions, and
  the second is the owner's.

## Writing values and references

- **Wire `UnityEngine.Object` references through `SerializedObject` /
  `SerializedProperty.objectReferenceValue` + `ApplyModifiedProperties`, never through
  `EditorJsonUtility.FromJsonOverwrite`**, which applies only value data and silently drops a reference
  given as an instanceID, path or guid.
- **Never write a non-material asset through a whole-object JSON overwrite.** `FromJsonOverwrite` REBUILDS
  the object, and rebuilding moves fields nobody supplied: setting a RenderTexture's `m_Height` also reset
  `m_ColorFormat`, and setting an AnimationClip's `m_SampleRate` also flipped `m_Legacy` TRUE, which
  silently takes a clip out of the Animator system. Both reported success. Write field by field through
  `SerializedProperty`, which cannot reach a field the caller did not name.
- **A material's properties are unreachable by JSON overwrite** (`m_SavedProperties`), so they go through
  the material API and the result is saved to disk.
- **Take an array's element type from REFLECTION, not from inspecting element zero.** Reflection is the
  only thing that answers correctly for an array that is currently EMPTY - exactly the case when first
  wiring one up. Native `m_` arrays have no reflectable C# field and fall back to the live element kind, so
  those need one existing entry.
- **Reference evidence must include the INSTANCE ID**, because comparing by name alone misclassified real
  changes as already-at-value whenever two objects shared a name. When a reference write reports
  already-at-value against expectation, read the component back and compare the GUID/assetPath in
  `serializedProperties` - that is the ground truth.
- **Decode payload strings through `DecodeJsonEscapes`, where the string is CONSUMED** - the key loop and
  `Unquote`, never in `TrySplitFlatJsonObject`, which must keep every value as its raw JSON token so
  numbers, objects and arrays reach their own parsers untouched. An UNRECOGNISED escape is left exactly as
  written rather than guessed at, so a Windows path inside a string survives. Route any new
  payload-reading verb through the same helper rather than trimming quotes itself.
- **A UnityEvent persistent-call target cannot be wired by a value or reference write** - use
  `unity-event-get` and `unity-event-set-string`. **The target must be the backing `UdonBehaviour`, not
  the UdonSharp proxy**, and an explicit listener component selector is required so the wrong behaviour
  cannot be grabbed silently.
- **Treat raw `EditorJsonUtility` output as lossy for nested object references** - it reports valid
  scene/component references as `{"instanceID":0}`, and every element of a wired ARRAY renders as 0, so a
  fully wired array is indistinguishable from an empty one. Read references from `serializedProperties`
  and UnityEvents from the structured section instead.
- **Never walk a `SerializedObject` with `NextVisible` alone when reporting or resolving fields.** It
  honours INSPECTOR visibility, and a component whose editor draws a field by hand marks that field
  hidden: `Joint.m_ConnectedBody` is absent from the visible walk's 18 properties and present in the full
  walk's 25. Combined with the lossy `instanceID 0` above, that made every joint read claim an unwired
  hinge, so a real scene fault was "ruled out" against a value the bridge invented. Reuse
  `TopLevelReportableProperties`.
- **Reflection over C# fields does NOT see native serialized fields.** `Joint` exposes `connectedBody` as
  an extern property, so there is no `m_ConnectedBody` FieldInfo and a reflection-only lookup reports a
  field that plainly exists as "not found". Resolve through reflection FIRST (a managed field also states
  the type a reference must satisfy), then fall back to `SerializedObject.FindProperty` and take the type
  from the property's `PPtr<$Type>` spelling via `FindTypeLoose`.

## Deploying a bridge change

- **An edit to an EXISTING verb's handler goes live as soon as Unity finishes compiling - test it
  immediately, do not wait for a reconnect.** The server forwards a raw command string and Unity runs the
  freshly loaded assembly, so the next call already executes the new code. **The MCP endpoint is C# inside
  the editor too**, so protocol- and transport-level changes deploy exactly the same way and nothing
  outside Unity has to be restarted.
- **A NEW verb is callable immediately through `conduit-run`, and the server announces it unprompted**;
  typed wrappers arrive at the next reconnect. A changed catalogue also reaches a connected client without
  one, because the handshake declares `tools.listChanged: true` and a differing catalogue revision pushes
  `notifications/tools/list_changed` down every held stream. `mcp-refresh-tools` is the on-demand form.
- **A client arms its refetch from the capability it saw at ITS handshake**, so a client that connected
  while the handshake said false ignores every notice until it reconnects. That declaration, not the
  client, was the whole history of "this client ignores the notification".
- **Codex replaces tool wrappers on process restart and resume, but not from a live list-change notice.**
  Changing a signature under a running wrapper leaves it stale and its typed call fails validation;
  restarting or resuming the task replaces it. (Codex `0.154.0-alpha.6.2`)
- **Claude Code keeps an already-loaded tool card stale for the whole session, even after a fresh
  discovery and refetch.** ToolSearch reads a local deferred-tool index and never hits the wire - a
  request counter was unchanged across it - so a renamed tool stays stale until a genuinely new session,
  while a tool first loaded after the rename is fresh. Conduit cannot invalidate that index; do not spend
  time trying. (Claude Code `2.1.271`)
- **Quote whitespace-bearing token arguments** in a typed wrapper before sending the raw command, and
  preserve quoted values Unity-side. Asset paths such as `Assets/Scenes/Drink System.unity` are common;
  never let a wrapper rebuild one as bare whitespace-delimited text that truncates at the first space.
- **An omitted middle optional must keep its slot whenever a LATER argument is supplied.** The wrapper
  rebuilds the command positionally, so dropping the separator slides every following argument one place
  forward and the bridge reads it as a different parameter - silently, because the call still returns
  success. `gameobject-create <name> [| primitive] [| parent]` sent without a primitive put the PARENT in
  the primitive slot. Emit the slot as an EMPTY section, and for a whitespace-delimited one emit its
  declared default. (`parseSignature.build`)
- **A whitespace-delimited optional that something can follow DECLARES its default, as `[name=value]`.**
  That default is its only empty form, because a bare token cannot be blank - without one the builder must
  refuse the whole call the moment a later argument is supplied, and the caller can do nothing about it.
  Declare it ONLY where the handler's own fallback IS that literal value, never where the fallback is
  computed or means "whatever is active": a declared default that disagrees with the handler silently
  changes what the call does, which is worse than the refusal it replaced.
- **Sections split on `" | "`, so an empty one is written `|  |` with TWO spaces.** `" | | "` arrives as a
  single section carrying a pipe, so a separator's trailing space is load-bearing and any whitespace
  normalisation across it silently rejoins the sections.
- **A status code the protocol permits is not an implementation. This has now cost the same capability
  TWICE**, once on DELETE and once on a versioned GET, and both times the store or counter that should
  have moved read zero at every measurement while the refusal looked compliant. **The tell is a refusal
  justified by what the spec permits rather than by what the caller can do instead** - if the answer to
  "what should it use now" is a route it may never call, the refusal is the bug.
- **A refusal must also be truthful about its own KIND, because the client routes on it.** Reporting a
  server-side failure as a method refusal tells a caller the method is wrong, so it abandons a route that
  works. Method refusals are 405 with an `Allow` naming what the route really serves; a server-side
  failure is a 5xx with a reason.
- **No MCP notification can put a message in front of an AGENT.** The modern era serves eight kinds and
  every one is a "refetch, your cache is stale" signal: `cancelled`, `progress`, `roots/list_changed`,
  `tasks/status`, `resources/updated`, `resources/list_changed`, `tools/list_changed` and
  `prompts/list_changed`. `notifications/message` is absent from the client's table and no handler for it
  exists, so declaring the `logging` capability and sending one is accepted, counted as delivered, and
  never seen; the modern revision made logging REQUEST-SCOPED, riding a request's own response stream.
  **`notifications/progress` is the only content-carrying channel and it needs an in-flight request
  holding a `progressToken`**, so it reaches a caller for exactly as long as the tool wait. Work that
  outlives its own call therefore has no MCP route home at all - `Reporting work that outlives its own
  call` owns the channels that do, and none of them is the caller coming back to read the job. Verify against the client binary's
  string table rather than its docs, which describe none of this. (Claude Code `2.1.275`)

## Editor-side traps

- **Nothing wakes on a TIMER to look for work - every trigger is a notification.** A periodic wake holds
  the processor out of its deep sleep states and spends power to observe nothing, and no interval is ever
  right: short enough to feel responsive wastes more, long enough to be cheap adds back the latency it was
  meant to remove. DEBOUNCE a burst of notifications into one pass rather than rate-limiting with a poll.
- **A `FileSystemWatcher` callback arrives on a THREAD POOL thread and may not touch a Unity API at all**,
  `EditorApplication.timeSinceStartup` included. Set a flag there, do nothing else, and let the editor
  update read it and raise the work. Dispose the watcher on `beforeAssemblyReload` as well, or every
  compile leaks the native handle the previous domain was holding.
- **`assets-prefab-save` / `assets-prefab-close` must go through `PrefabStage.SavePrefab()` by
  reflection.** `PrefabUtility.SaveAsPrefabAsset` writes the file without clearing Prefab Mode's dirty
  baseline, so closing afterwards raises a modal that blocks the editor thread and times out the bridge.
  Close defaults to saving, accepts explicit `false` to discard without a prompt, and must abort the close
  when save or discard fails.
