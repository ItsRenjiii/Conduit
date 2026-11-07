# Diagnosing & designing

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before diagnosing a bug, or designing anything before you build it.**
>
> Both sequences below are enforcement contracts, deliberately kept whole: every step must produce a
> visible artifact in your output, so a skipped step is visible. Do not compress them.
>
> **This file owns the SEQUENCES and nothing else goes in it.** Every past failure quoted below is here
> only because it demonstrates a STEP - the prime suspect that was wrong, the elimination that needed a
> measured value. **A new war story is not a reason to add one.** The FACT a diagnosis produced belongs
> to whichever subject owns it, and this file gains an example only when the sequence itself has a gap
> the story exposes. Otherwise it becomes a museum of bugs that every session pays to scroll past.

- Instrument, don't alter. When diagnosing, add temporary logging / diagnostics around the suspect code
  - never change logic or behaviour while you're still finding the cause. Editing logic mid-investigation
  introduces new variables and hides the real fault.
- Work the scientific method: form a hypothesis, add instrumentation to confirm or refute it, read the
  actual output, then act on the evidence. Don't guess-and-patch - a lucky guess that only masks the
  symptom costs far more time later than working out the real cause. Fix the root cause, not the symptom.
- Clean up deliberately and in the loop. Only once the problem is genuinely fixed AND the user has
  confirmed it, ask whether to remove the instrumentation. If they accept, do a dedicated clean-up pass
  to strip the temporary logging / diagnostics (keep any permanent, gated debug logging that's part of
  the design).
- **Enumerate, don't search - a grep only ever finds the names you already guessed.** When the question
  is "does this exist" or "what is this called", a keyword search silently returns nothing for a member
  that is simply spelled differently, and nothing reads exactly like absence. `udon-exposure` answers from
  the SDK's own registry of 35,222 definitions, `type-find | members` reads a resolved type's real
  surface, and `udon-program | methods|externs|symbols` reads what a behaviour actually compiled to -
  none of which require knowing the spelling in advance. This is not a tie-break between equivalent
  tools: a fragment search that misses produces a confident wrong answer, and the enumerating tool
  produces a list.
- **A file under `Library/`, `Temp/` or `obj/` is BUILD OUTPUT, never source, however exactly it matches.**
  Unity and the SDK stage whole copies of `Assets/` there during a build, so a wide search can return the
  file you wanted frozen at an earlier shape, carrying parts the product no longer has. Search `Assets/`
  and `Packages/` as widely as the question needs - just never let a build directory answer it.
- **Enumerating answers whether it exists; only running it answers what it does.** A registry gives a
  member's name and signature and says nothing about behaviour, so an API that no-ops in silence, or that
  sets a flag the update loop can never observe, reads as perfectly ordinary right up until a value comes
  back. Choose the scratch location through `conduit-project-workflow.md`. A `[MenuItem]` under `Assets/`
  is callable through `menu` the moment it compiles and answers in seconds; clean it up through that
  workflow once it has. Reach for one the moment you are about to write
  "should" or "probably" about behaviour, and never write a mechanism into code before a value has come
  back from the editor.
- **Reading is the cheap half - always pay it first.** A read is processed in one pass and costs
  seconds; a wrong assumption written into code costs a compile cycle at best and a debugging session at
  worst, and the user pays for both. **So the trade is never "is this read worth it" - it is "am I about
  to assert something I could have just looked at".** Never skim a source file to save effort either:
  reading half of something and generalising from it is how a confident claim gets built on the half that
  was not read. The compiler and the registry are the judges available for free; consult them before
  writing, not after the error.
- **The dev tooling on this machine is readable evidence, not a black box - and that is how a blocked
  setup step becomes exact instructions.** When the question is "does that tool have such a control",
  "what is this option called" or "where does the user click", read the application instead of guessing or
  handing the question back. The VRChat package managers are the worked case: VCC is a Photino/WebView2
  app whose whole UI ships as a plain JS bundle beside its executable, so one search of it confirmed a
  package-search box - a question that would otherwise have cost a round trip and been answered "I think
  so". **The payoff is not the fact, it is that you can now tell the user exactly what to press** when
  the step is theirs to perform. Read the tool, cite what it says, and never write a machine-specific
  path into these documents when you do (`conduit-doc-style.md`).
- **The one exception is the VRChat client itself, which is encrypted and readable only through its
  log** - `conduit-testing.md` covers what that log carries and why the binary answers nothing.
- **Never explain a surprising result by blaming the bridge.** "The edit silently failed to apply" ends a
  diagnosis instead of advancing it - read the value back through Conduit and keep eliminating. Two
  neighbours of the same shape: Udon never runs code on a disabled GameObject, and stale compiled Udon
  does not exist.

## The required diagnostic sequence (mandatory - output every checkpoint)

Diagnosis in this project follows a fixed sequence, and every step produces a visible artifact in your
output. A conclusion reached without these artifacts is not a diagnosis; it is a guess with confidence.
Two real failures this procedure exists to prevent: the **font theory** (a Cyrillic-initial name blanked
on one label; the obvious answer was fonts/fallback atlases, burned many tokens, and was wrong - the real
cause was TMP Ellipsis overflow interacting with a VRChat-client-only text pipeline) and the **cover
theory** (an assistance button seemed dead until other buttons had been pressed; the obvious answer was
"something is covering it", and was wrong - the real cause was a cooldown timestamp defaulting to 0
against Time.time, silently swallowing every press for the first 30 seconds after world load). In both
cases the plausible answer was a trap and only systematic elimination found the mechanism. This applies
to every bug class: UI, logic, rendering, timing, networking.

**Step 1 - Name the prime suspect, then distrust it.** Write out literally: "The common answer here
would be X." X is now your prime suspect for being WRONG, not right: bugs that survive long enough to
reach you are disproportionately the ones the obvious answer does not fix, and the reporter has usually
already considered X. You may not accept X until you have either ruled out the competitors from Step 2
or produced direct observed evidence for X itself. Concluding X because it is the typical/documented
cause is the exact failure this section exists to stop.

**And write the worked examples without naming the author's own work** - a product, world, domain or
project path here ships to every customer and means nothing to the reader. Keep the mechanism, the
magnitude and the observed value; generalise only the noun. `conduit-doc-style.md` owns the rule,
including the case where a name is the ANSWER and must stay.

**Step 2 - List competing hypotheses.** Write at least three mechanically distinct hypotheses spanning
different layers: asset/data, scene wiring/geometry, code logic/state, timing/lifecycle, environment
difference (editor vs client, this instance vs that one). If you cannot name three, you do not yet
understand the system well enough to conclude anything - go read it first.

**Step 3 - State the discriminating fact.** Identify the single strongest fact that splits the
hypothesis space and write out exactly what it eliminates. "Works in Unity play mode, fails in the
VRChat client" eliminates every hypothesis living in shared assets or scene data, because both
environments load those identically. "A shorter string fails while a longer one renders" eliminates
length/capacity causes. Facts the user has already handed you rank highest: "the other project uses the
identical font and works" or "the system next to it doesn't have this issue" is disconfirming evidence,
and continuing to pursue a hypothesis it kills is an ERROR, not persistence. Re-read the user's report and
list every constraint it contains before concluding anything.

**Step 4 - Eliminate by inspection, never by reasoning.** Every elimination must cite the concrete value
you observed through a tool: the component you read, the RectTransform rect you measured, the collider
bounds, the navigation mode, the serialized reference, the UnityEvent target, the console line. "The
button probably isn't covered" is not an elimination; "the button spans local x 178-278, the input field
ends at x -43, no overlap, and its Image is a raycast target" is. If you did not read or measure the thing,
you have not ruled it out - you have narrated a guess. Record each one as:
`RULED OUT: <hypothesis> - observed <value> via <tool / file:line>.`

**Step 5 - Trace the logic path to the mechanism.** Only after physical/scene causes are eliminated,
walk the real execution path: input -> handler -> every gate, guard, and early return -> effect. State
the exact line and condition that produces the failure and why it fires under the reported conditions
(e.g. "`_lastAssistanceRequestTime` defaults to 0, `Time.time` starts near 0 at load, so
`Time.time - 0 < 30` swallows every press for the first 30 seconds"). A root cause is a line and a
condition. "Probably a race" or "some interference" is not a root cause.

**Step 6 - Residue check before declaring done.** The mechanism must explain EVERY reported symptom,
especially the asymmetries: why it fails here but not there, why this input but not that one, why the
workaround the user stumbled on appears to work. One unexplained symptom means the diagnosis is
incomplete or wrong. Then sweep for the same pattern elsewhere in the codebase (other timestamps
compared against Time.time from a default of 0, other labels set to Ellipsis, other instances of the
same gate) and fix or flag what you find. Record:
`RESIDUE: <symptom> -> <how the mechanism explains it>` for each symptom, and
`SWEEP: <where else checked> -> <result>.`

A response that skips a checkpoint is visibly incomplete against this list; that is by design. Before
writing the words "root cause", apply the one-line self-check: **if my conclusion were deleted, would my
listed observations force any competent reader to reconstruct the same mechanism? If not, I have not
diagnosed - keep eliminating.**

## Before you build: the required design sequence (mandatory - output every checkpoint)

Sibling to the diagnostic sequence above. That procedure stops you concluding a wrong cause; this one
stops you building on a wrong assumption. Same enforcement model: every step produces a visible artifact in your output, so a
skipped step is visible, and a plan without these artifacts is not a design - it is a guess with
scaffolding. Real failures this section exists to prevent:

- **The fighting-registries design.** "One control panel per operator" naively means N synced behaviours
  all writing the same state. It compiles, demos fine alone, and collapses in a live instance. The
  correct shape was one synced brain plus N unsynced shells reading it - found by asking the Step 4
  questions first, not by debugging the collapse later.
- **The identical change.** A rotation "fix" replaced `LookRotation(-forward)` with
  `bodyRotation * Euler(0,180,0)`: mathematically the same rotation. The change changed nothing and
  cost a full in-headset test cycle to discover.
- **The wrong-layer edit.** A serialized reference wired on one scene instance as an override instead
  of on the source prefab asset: worked on the single instance tested, silently null on the other fifteen.
- **The assumed API.** Recommending a Unity/TMP member that is not Udon-whitelisted (e.g.
  `TMP_InputField.DeactivateInputField`, which is not exposed). It compiles in your head and fails on
  upload.
- **The inherited fact.** A "platform limitation" stated by an earlier session or another model,
  repeated as established truth. Real case: the claim "VRChat only supports Static TMP atlases"
  circulated through agent memory into an earlier draft of this very document before the user corrected
  it - Dynamic atlases work fine. A claim's presence in memory, in docs, or in a previous answer is NOT
  evidence; it enters Step 3 as UNVERIFIED like everything else, and a user correction kills it
  permanently.
- **The SDK-source impossibility.** Concluding a capability is unavailable by reading SDK source, when a
  shim in the project changes the answer. Real case: `ClientSimMain` hooks an inert AVPro stub
  unconditionally, which reads as proof video cannot be exercised in play mode - but the hook is a
  MULTICAST delegate and the ArchiTech VideoPlayerShim package takes it over, so play mode resolves
  through VRChat's own yt-dlp and behaves like the client (`conduit-video.md`). **SDK source proves what
  the SDK does, never what
  this project can do**, because the shims that decide it are not in the SDK. Check the project first.

**Step 1 - State the default design, then distrust it.** Write out literally: "The obvious way to
build this is X." X comes from generic Unity/C# priors, and this environment is not generic: the Udon
whitelist, sync modes, client-vs-editor divergence, and the product's existing patterns all veto
standard approaches routinely. X may not be adopted until its load-bearing assumptions pass Step 3.

**Step 2 - Enumerate what actually decides it.** Write the constraints that pick a winner: platform
(is the API Udon-exposed? does the VRChat client behave like play mode here?), networking and ownership
(synced or local, who owns it, what a late joiner sees), the layer the change must live in to propagate
(prefab asset vs scene instance vs code), and what already exists to reuse - search before adding; a
near-duplicate helper is a defect, not a convenience.

**Step 3 - Verify every load-bearing assumption by inspection.** For each assumption the design stands
on, record one of:
`VERIFIED: <assumption> - <evidence: whitelist entry, component read back, existing working use, or a
value a scratch probe returned>` or `UNVERIFIED: <assumption> - risk if wrong: <consequence>.`
An UNVERIFIED with a high consequence is a blocker to resolve before building, not a footnote. "It
should work" is Step 1 material, never Step 3 evidence.

**Step 4 - Answer the state-and-propagation questions explicitly.** Where does each piece of state
live, who writes it, and how does a change reach every consumer? One synced writer or many? Does the
edit belong on the prefab asset, the scene instance, or in code - and will it reach all sixteen copies
or just the one you tested? What happens at t=0 with default field values (a timestamp defaulting to 0
against Time.time silently blocked a button for 30 seconds after load; the diagnostic sequence above
covers that case), and what does a late
joiner see?

**Step 5 - Diff the proposal against current behaviour.** Before writing code, state the observable
difference it will make and confirm the new behaviour is not equivalent to the old (the
identical-change trap). If you cannot name what will observably change, you are holding a no-op or an
unrequested refactor, not a fix.

**Step 6 - Preview the failure modes.** Write the top ways the design breaks and how it survives each:
remote clients and sync timing, re-entrancy (Udon's shared-locals heap means a handler that writes its
own field re-enters synchronously - `conduit-udonsharp.md` owns the rule, `conduit-vrchat-ui.md` the
input-field case), unwired optional references, load-order and t=0 defaults, and
the user pressing things in an order you did not plan. A mode you cannot answer goes back into Step 3
as an UNVERIFIED.

Self-check line before building: **can I name the observable difference this change makes, the layer it
must live in to reach every instance, and the evidence behind each assumption it stands on? If any of
the three is missing, I am not ready to build - keep verifying.**
