# The inspector framework and its worked examples

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before writing ANY custom inspector, and open the matching example BEFORE
> writing the control - not after it misbehaves.**
>
> **This file owns the EXAMPLES and the UI Toolkit traps they solved, and nothing else goes in it.** The
> project's editor CONVENTIONS - namespaces, folders, the mandatory editor per behaviour, ShaderGUI - are
> `conduit-editors.md`; general code form is `conduit-code-style.md`; and what an example's code does is
> in that code, which is the point of pointing you at it rather than describing it here.

Conduit's own `Samples~/Examples/Inspector/` folder holds a complete, working UI Toolkit inspector
framework and ten demo behaviours that exercise every attribute in it. Between them they cover
essentially everything a custom editor in these projects ever needs: grouping, validation, conditional
fields, previews, pickers, collections, reactive values, actions and long-running work.

Read the examples in place, inside Conduit's package folder. Unity does not import a `Samples~` folder,
so they are plain files on disk and never compile into the project. The Package Manager's **Import**
button copies them into `Assets/Samples/` for a project that wants to run the demos.

## Never move an example out of Conduit, and never reference one from outside it

- **Nothing under Conduit's `Samples~/Examples/` folder is ever moved, relocated or "promoted" into a
  product, for any reason.** These are Conduit's references: they ship with Conduit to every project, and
  moving one takes the reference away from every future session in every other project to solve a problem
  in one. There is no argument that justifies it - not shipping, not duplication, not tidiness.
- **Nothing outside `Samples~/Examples/` ever references an example - not its code, not its stylesheet, not
  its assets.** Conduit does not ship inside a product, so a window that adds the example's `.uss` to its
  own root, derives from its types or loads its textures ships broken, and it takes the reference away
  from every other project exactly as moving it would. Write your own file, with your own class prefix.
  (Re-affirmed by the user, in caps.)
- **A product that needs this functionality gets its OWN copy in its own folder**, and the example stays
  exactly where it is. Conduit does not ship inside any asset, so a product referencing an example
  directly would ship broken; that is a reason to write the product's own, never a reason to move ours.
- **When you write your own version of an example, reproduce every part of it - a partial copy reads
  as broken, not as a variant.** Open the example's own rule for the part you are building and carry its
  VALUES across - box sizes, alignment, margins, colour roles, states - then change only what your case
  genuinely differs on. Half a header is a defect on sight, not a restrained version of one.
- **Re-namespace the copy into the product's own namespace before Unity imports it.** Two copies of a
  type in one namespace is a duplicate-definition error across the whole project, and it lands the
  instant the file is written, not when you get round to it. Change the namespace on disk FIRST and the
  project never goes red; import first and every script in the assembly stops compiling until you fix
  it. The class NAMES can stay as they are - a different namespace is enough - and keeping them keeps the
  copy diff-able against the example, so a fix made in one can be carried to the other.
- **A prefixed filename does not rename the class inside it.** Once the copy's files are prefixed, the
  base class is still `ItsRenjiInspector`; a new editor written against the FILE's name fails to compile,
  and because every editor in the product derives from it, they all fail at once and the real cause is
  buried under a page of duplicate-definition noise. Read the class declaration, never the file name.
- **Match the namespace the product already uses**, rather than inventing a parallel one, or the product
  ends up with two naming schemes and every new file has to pick a side.

## Build inspectors from these, never from priors

- **Never write an inspector control from memory when an example here already draws it.** Open the
  example, copy its approach, and keep its oddities. Priors about UI Toolkit are the single largest
  source of wasted time in this area: they produce code that compiles, looks right, and then behaves
  wrongly in ways that take hours to pin down.
- **Never reach for IMGUI.** The framework is UI Toolkit throughout and contains no IMGUI islands.
  Defaulting to IMGUI because a control seems hard is the failure this whole reference exists to stop.
- **Do not restyle from scratch.** `Editor/ItsRenjiInspector.uss` carries the entire visual language and
  a role-token block at the top; a reskin is an edit to those tokens, not to the rules beneath them.

## An editor that owns a tool, not a property sheet

- **Override `BuildCustomSections(root)` for what the attributes cannot express, and ONLY for that.** An
  async search, a live preview, transient state that is never serialized: things a serialized field does
  not model. It runs after the fields and buttons, so a section can read the values above it.
- **Do not use it to lay fields out.** Grouping, notices, conditions and buttons are attributes, and
  reaching for this to place a field is the IMGUI habit coming back through another door. Build the
  section from the same USS classes as everything else (`ir-card`, `ir-note`, `ir-button`) so the panel
  still reads as one thing.

## The framework

| File | Holds |
| --- | --- |
| `Runtime/ItsRenjiInspectorAttributes.cs` | Every attribute (53), each `[Conditional("UNITY_EDITOR")]` so none reaches a build |
| `Runtime/ItsRenjiInspectorValidation.cs` | `ValidationResult` and `MessageSeverity`: a check's own severity, message and offered fix |
| `Editor/ItsRenjiInspector.cs` | The layout engine and the base class every branded editor derives from |
| `Editor/ItsRenjiInspectorPickers.cs` | Scene, asset, animator-parameter, material-property and component pickers, and button parameters |
| `Editor/ItsRenjiInspectorSources.cs` | The `$member` resolver that lets an attribute read a value off the behaviour |
| `Editor/ItsRenjiInspectorStarfield.cs` | The generated starfield tile, drawn rather than shipped as art |
| `Editor/ItsRenjiInspector.uss` | The whole visual language, role tokens first |

## Which example to open

All under `Samples~/Examples/Inspector/Demo/`, one concern each, with their editors in `Demo/Editor/`.

| Example | Open it for |
| --- | --- |
| `ItsRenjiDemoLayout` | Tabs, cards, nested cards, folds, rows, gaps, dividers, indents, draw order, and a field ABOVE the tab strip |
| `ItsRenjiDemoGuidance` | Notes, warnings, required values, and `Validate` - a check method reporting its own severity and offering a fix |
| `ItsRenjiDemoValues` | Clamped sliders, min-max ranges, units, choice strips, relabelling, label width, read-only, hidden labels, edit-mode-only |
| `ItsRenjiDemoConditions` | `ShowIf`, `HideIf`, `EnableIf`, including matching one particular value rather than a bool |
| `ItsRenjiDemoReferences` | Asset-only and scene-only references, inline buttons on a field's own line, flat previews, the turnable mesh preview, and an asset's own inspector drawn in place |
| `ItsRenjiDemoTypes` | Every field type drawn plain, and proof that Unity's own attributes keep working inside our cards |
| `ItsRenjiDemoReactive` | `$member` resolvers for note text and units, slider ends read off other fields, `OnValueChanged`, and properties shown with `ShowInInspector` |
| `ItsRenjiDemoPickers` | Layer and tag pickers, a list the behaviour supplies, enum choice strips, scene paths, asset dropdowns with a custom slot, fetched components, and lists built from another field |
| `ItsRenjiDemoStates` | Play-mode visibility and locking, tints, list settings, inline arrays, and the multi-column table |
| `ItsRenjiDemoProgress` | Work that takes time: an editor coroutine so nothing blocks, a `UnityEditor.Progress` task that outlives the panel, an in-card bar with cancel, a run with no known total, and progress held as DATA so a rebuilt panel rejoins a job already going |

## What these already solved - do not re-derive it

- **A class change does NOT always repaint in an editor window, and the failure looks like broken
  logic.** The class lands, the style resolves a beat later, and nothing asks for another paint, so the
  control is correct in the tree while the screen shows the old state until an unrelated event forces a
  redraw. Mark the element with `MarkDirtyRepaint()` when its state really changed - see the tab strip's
  `SyncTabMarks`, which also polls on a schedule the way the reference implementation does.
- **USS gives a tie to whichever rule is written LAST, and a pseudo-class weighs the same as a class.**
  So a bare `:hover` outranks a `--active` state class, and hovering the live tab, choice or checked
  button silently strips its state. Every state class in the sheet is restated after its hover rule.
- **`-unity-background-image-tint-color` MULTIPLIES, so it can only ever darken an opaque sprite.**
  Unity's checkmark is one opaque sprite carrying both the well and the tick; tinting it produced a
  near-black box. Drop the image and draw the state instead.
- **A control whose input is an `IMGUIContainer` cannot be styled from USS at all** - IMGUI paints from
  the editor's skin. `ColorField` is one, which is why the framework draws its own swatch and hands the
  click to `UnityEditor.ColorPicker.Show`. **Check before styling rather than after it looks wrong:**
  `editor-window-controls` reports `source` per control as `imgui` or `uitoolkit`, so one call answers it
  for every control in the window - far cheaper than sampling pixels and guessing why one field is dark.
- **Any custom `PropertyDrawer` forces `PropertyField` into an IMGUIContainer, and a reference typed
  as an UdonSharpBehaviour HAS one.** So a behaviour-to-behaviour reference - the commonest field in
  these products - lands as a black slab in the middle of a light panel while every field around it is
  fine. It is invisible in a demo built from `Transform` and `Material` references, which are plain
  Unity types and stay UI Toolkit. The framework detects it and builds the `ObjectField` itself; the
  value written is the same proxy reference either way. See `IsProgramBehaviour` and
  `BuildBehaviourReference`.
- **Give every interactive surface a hover of your own.** One without it keeps the editor's, which was
  picked for a dark skin and lands as a bruise on a light panel.
- **Load an asset a script depends on by GUID, never by a path constant.** A path breaks the moment
  anyone drags the folder, and it breaks SILENTLY - no compile error, no warning, just every inspector
  falling back to the editor's skin. See `STYLE_GUID` and `LoadStyleSheet`.
- **When you copy code that holds a GUID into another product, repoint the GUID at that product's own
  copy of the asset.** A GUID is stable, which is exactly the hazard here: the copy keeps resolving, so
  it silently binds to the ORIGINAL's asset and every test in the source project passes. It fails only
  once shipped, where the original does not exist - which is the worst possible place to find it. Copy
  the asset, take the new GUID from its `.meta`, and update the constant and any name-based fallback in
  the same change.
- **A progress bar living inside its own elements DIES when the panel is rebuilt, and the work carries
  on with nothing left to stop it.** An inspector is rebuilt on every selection change, so the bar, its
  text and its cancel button all go. Hold the state as a VALUE, render that, and render it again as the
  last step of building the panel, which is what lets a fresh panel rejoin a running job. Report into
  `UnityEditor.Progress` as well: that task belongs to the editor and survives regardless. Unity's
  `DisplayCancelableProgressBar` is no answer either, being MODAL - it blocks the whole editor and
  undoes the point of working asynchronously. See `ItsRenjiDemoProgress`.
- **UdonSharp paints a violet bar down the left of every U# inspector, from outside your subtree** - see
  `conduit-editors.md` for how the framework hides it.
