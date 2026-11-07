# Code style - correctness, docs, naming, layout, logging

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before writing or editing ANY code in this project.** It applies to every
> edit regardless of subject, so it is the one subject file that is almost always in scope.
>
> **It owns HOW code is written - form, naming, documentation, layout, logging - and nothing about what
> any API can DO.** Being almost always in scope makes this the easiest file in the set to dump a fact
> into, so the test is strict: a capability, a limit or a platform behaviour belongs to its own subject
> (`conduit-udonsharp.md`, `conduit-networking.md`, `conduit-performance.md` and the rest) no matter how
> much style advice surrounds it. Custom inspectors are `conduit-editors.md`. The style of these
> DOCUMENTS, as opposed to of code, is `conduit-doc-style.md`.

## 1. Correctness & hygiene first
- Do it correctly and completely every time — never a quick hack or shortcut. Cutting corners causes
  chaos and bugs later; solve the actual problem properly and robustly.
- **`Update` is the last resort, not the default.** In Udon the per-frame cost is not just the work you
  write, it is the Udon VM event dispatch itself, and you pay it on every behaviour that defines
  `Update` even when the method early-returns on its first line. So N objects with an `Update` cost N
  dispatches per frame before doing anything. Prefer: driving work from the event that actually changes
  something; one manager that ticks once and loops over an array instead of N behaviours each ticking
  themselves; and `SendCustomEventDelayedSeconds` for anything that does not need per-frame resolution.
  When something genuinely must run every frame, run it on the ONE object that owns the work (typically
  the local player's) and never on the remote copies.
- Be efficient and memory-clean, especially in hot paths (`Update`, loops, event handlers): cache
  references, reuse buffers, avoid per-frame allocations, boxing, and LINQ that thrash the garbage
  collector. Udon has no safety net — `Dispose()` what implements it, `Destroy()` textures you create,
  null out cache arrays when done, and keep strong references to anything that must survive across
  frames so it isn't collected mid-operation. Clean up in `OnDestroy`.
- Be safe and defensive: validate inputs, null-check before use, guard edge cases over assuming the
  happy path. Leave no dead code behind.

## 2. Documentation — truthful, current, on everything
- Put an XML `<summary>` on every class, field (including every private state field - a one-line
  summary), and method. Explain intent and usage, not a restatement of the name. Document `<param>` for
  each argument and `<returns>` where a method returns a value.
- Every comment and `<summary>` must describe what the code actually does. When you change code, update
  its comments in the same edit - never leave a stale, outdated, or aspirational description behind. A
  wrong comment is worse than none.
- Inline comments explain the why and the non-obvious, never the obvious.
- **A summary or tooltip describes the code - never how to wire it in the inspector.** Banned outright:
  "Assign…", "Wire its On Click to…", "Leave this empty", "Leave off for release", "Fill it with…", "Match
  this to…", "Drop this on…", and the word "OPTIONAL". A shipped asset settles every reference before
  release, so text that hedges or instructs documents the author's TODO list rather than the code, and it
  invalidates the file it sits in. HelpBoxes are the OPPOSITE case and belong to `conduit-editors.md`.
- **Never name a group after the act of connecting it.** "Core Wiring", "Physical Wiring", "validates
  the wiring" - a foldout, section divider or summary is named for what the group HOLDS, and a summary
  says which references it checks rather than calling them "the wiring".
- **Anything that toggles is described for both positions, never one.** "What a player may switch off",
  "volumes a player can switch off", "colliders a player can pass through" each state one half of a
  control that does two things, and each reads as false the moment the toggle sits the other way - the
  reader is left thinking the other direction is somebody else's feature. Name what the toggle GOVERNS,
  not the act of moving it one way: "what each player controls for themselves". Applies to labels,
  summaries, notes, foldout names and section headings alike.
- **A Conduit convention NEVER appears in a product script.** "Set Navigation to None", trigger
  colliders, kinematic bodies and the rest live in these documents; restating them in shipped code puts a
  copy where it cannot be maintained and where no customer needed it.
- **Never shout - not in user-facing text, and not in comments or summaries either.** Every string a
  user reads - a label, a status, a list row, a button - is sentence case: "Door open", not "DOOR OPEN",
  and letting capitals reach product copy makes the asset shout at its customer. Comments and summaries
  are ordinary prose for the same reason: professional code does not shout at the person reading it, and
  a comment earns attention by being accurate rather than loud. The shouting-caps device belongs to these
  convention documents only, where it stops an agent skimming a rule. It is not a code convention.
- **Never transform the case of authored data.** A GameObject name, a display name, anything an author
  typed is DATA and reaches the screen as written. No emphasis argument exists for a name, so a `ToUpper()`
  in a name helper is not the caps convention leaking - it is an unrequested visual style imposed on someone
  else's content, and it DESTROYS the original casing instead of merely presenting it differently.
- **Shouting reaches the UI three ways - check all three when a screen is shouting.** Literal defaults on
  serialized string fields, a `ToUpper()` in a name or label helper, and All Caps set as a font style on the
  TMP itself. Fixing one and declaring it done has already happened; the other two survive it.
- **A panel never overwrites authored colours.** Do not assign `.color` on a label to carry state. The
  author sets colours in the inspector, and a script that tints on every redraw silently destroys that - black
  text on a black background renders invisible and reads as a wiring fault, not a styling one. State that
  needs showing is SAID, in the text.
- No em or en dashes anywhere in code files: comments, summaries, log strings, inspector help
  text. Use a plain hyphen or restructure the sentence. Box-drawing characters in section
  dividers are a different codepoint and are fine.
- **The one exception: a non-ASCII character that is part of a remote API's wire format is data, not
  decoration.** Where a service uses a literal character as a delimiter token - an arrow such as U+2192,
  or a zero-width space - it discards everything up to and including that character, so replacing it with
  an ASCII lookalike changes the REQUEST rather than the formatting and the result is silently wrong
  rather than an error. Keep it verbatim, and write it as an escape such as
  `\u200b` so it stays visible in source, and comment why it is there. A token or url
  string is the only place such a character belongs in a code file.
- Serialized-field order when a runtime script uses inspector headers: `[Header("…")]` first, then the
  `/// <summary>`, then the field line. When a custom editor draws branded foldouts for those same groups,
  remove the runtime `[Header("…")]` attributes so the inspector does not show duplicate section headers.

## 3. Naming
- Private fields (and private statics): `_camelCase`.
- Constants: `private const UPPER_SNAKE_CASE` for primitives; `static readonly` for arrays / `Color`
  values that can't be const. Comment non-obvious values.
- Serialized inspector fields: either `public camelCase` or `[SerializeField] private _camelCase`.
  Public state read by other scripts uses `PascalCase`, usually `[HideInInspector]`.
- Methods: `PascalCase`. Public methods that are Udon event targets - UI button hooks, network
  callbacks, `SendCustomEvent` / `SendCustomEventDelayed` targets - are prefixed with `_`
  (e.g. `_RetryRequest`, `_DelayedInit`). VRChat callbacks use `public override`. Locals: `camelCase`.
  **That prefix is a SECURITY boundary, not a style choice: an unprefixed public method can be fired
  remotely by any client in the instance, and an exploit doing so is in circulation. See
  `conduit-udonsharp.md`.**

## 4. Layout & structure
- **Every type goes in a namespace - never the global namespace.** C# resolves a simple type name against the
  types declared in each enclosing namespace BEFORE that file's `using` directives, so a global-namespace type
  shadows a same-named framework type across the whole assembly. A template class named `PlayerSettings` broke
  every editor script calling `UnityEditor.PlayerSettings` with CS0117, and the error names the innocent caller.
- **A namespace segment shadows exactly the same way - never name one after a Unity type.** Lookup walks
  outward from the enclosing namespace, so declaring `ItsRenji.Tools` makes bare `Tools` resolve to it from
  inside EVERY `ItsRenji.*` file: `Tools.current` in Conduit's own editor code stopped compiling with CS0234,
  and, as with the type case, the errors name the innocent caller and not the new namespace. `Tools`, `Editor`,
  `Debug`, `Random`, `Object`, `Selection`, `Undo` and `Application` are all live hazards. Name the segment for
  the work (`ItsRenji.Workshop`), never for the toolbox.
- 4-space indent, Allman braces. Short guard clauses stay on one line: `if (x == null) return;` One
  blank line between members. Guard clauses / early returns up top, then the work.
- Organise every file with `#region` blocks grouped by responsibility (Constants, Serialized Fields,
  Private State, Update Loop Handlers, network callbacks, etc.).
- Member order: Unity lifecycle (`Start`/`Update`/`OnDestroy`) → public API → `public override` hooks
  → private helpers. Cache `GetComponent<>()` / child lookups into fields in `Start()`.
- **Do not reach for `[Header("…")]`.** Every behaviour here ships a branded custom editor
  (`conduit-editors.md`), and its `[Card]` and `[Fold]` attributes own the visible grouping, so a
  `[Header]` alongside them renders a second time or fights the layout. It is correct only on a type that
  genuinely has no custom editor. `[Tooltip("…")]` is unaffected and still reaches the label untouched.
- Use box-drawing section dividers to group related fields:
  `// ── Cached UI references ───────────────────────────`
- Vertically align runs of similar assignments and trailing comments; annotate magic values inline:
  `rect = new Rect(0f, 0.5f, 1f, 0.5f);    // top half of the atlas`

## 5. Logging
- Define a colour-coded prefix constant (ask the user which colour and tag they'd like first) and route logs through it,
  string-interpolated: `private const string LOG_PREFIX = "<color=#HEX>[YourTag]</color>";`
- Gate verbose/diagnostic logs behind a serialized `bool enableDebugLogging`:
  `if (enableDebugLogging) Debug.Log($"{LOG_PREFIX} …");`. Real errors use
  `Debug.LogError($"{LOG_PREFIX} …")` and are not gated.
- The gate is absolute for anything that ships: no ungated `Debug.Log` in any script, ever, including "rare" or
  client-only diagnostics - volume logging lags a live world and an always-chatty asset is not sellable. This is
  the owner's standing rule, restated after a store-diagnostics pass shipped ungated logs anyway. The debug bool
  is the test switch: tick it on the scene instance for a diagnostic upload, untick for release. The only
  ungated calls are real errors, e.g. one-shot `Start()` configuration LogErrors.
