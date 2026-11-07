# Custom editors & inspectors

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before writing a custom inspector, a ShaderGUI, or any editor tooling.**
>
> **This file owns the CONVENTIONS and the tooling rules - what an editor must be, where it lives, and
> how it must behave - and nothing else goes in it.** The worked examples and the UI Toolkit traps they
> already solved are `conduit-inspector-examples.md`, and a control question goes there FIRST. uGUI
> inside a WORLD is `conduit-vrchat-ui.md`: "UI" names two unrelated things in this project, and nothing
> a player touches on a canvas belongs in this file. General code form is `conduit-code-style.md`. A rule
> that is already a hard stop is never restated here.

- **Open `conduit-inspector-examples.md` BEFORE writing any inspector control.** A complete working
  framework and ten worked examples ship in Conduit's own `Samples~/Examples/Inspector/` folder, covering
  grouping, validation, conditions, previews, pickers, collections, reactive values and actions. Writing
  one of those from priors instead of opening the example is the most expensive habit in this subject.
- **Custom editors are not optional** - hard stop 4 carries the rule, and it is not restated here. What
  the stop cannot carry is why it is a stop at all: it was re-affirmed after four behaviours shipped
  bare, in the owner's words, "never have been optional for years of us working together".
- Editor scripts use a `.Editors` namespace with `[CustomEditor(typeof(X))]`, and must live in an Editor
  folder. Same doc discipline as runtime code. A product may use a product-scoped editor namespace
  (`ItsRenji.<Product>.Editors`) so its inspectors do not collide with another product's editor types.
- **An editor tool finds its own product folder - never write an asset path into it.** A buyer may move
  or rename the folders a product was dropped into, and a written-in `Assets/<vendor>/<product>/...` then
  sends the tool hunting prefabs that are gone and writing generated files into someone else's directory.
  Find the product from the tool's OWN script - `FindAssets("<ToolType> t:MonoScript")`, take the path
  ending in that file name, walk up out of `Editor/` - cache it, and hang every other path off that root.
  (Re-affirmed by the user: "i've said it before".)
- **Derive from `ItsRenjiInspector` and write no drawing code.** The layout comes from attributes on the
  behaviour, so an editor is a `[CustomEditor]`, a `Title` and a `Subtitle` - nothing else. Do NOT call
  `DrawDefaultInspector()` and do NOT add an `OnInspectorGUI`. The runtime script's `[Header]` attributes
  come off, because `[Card]` and `[Fold]` own the grouping now.
- **Build an editor from the framework, its examples and the product's shared bases - NEVER by copying a
  sibling tool.** A sibling is one agent's interpretation and carries its drift; copied, the drift
  compounds, and an audit found windows with no background, no ticks and hand-rolled headers this way. An
  `EditorWindow` derives from the product's shared window base the way an inspector derives from
  `ItsRenjiInspector`, and a missing control is added to the base, not to one window.
- **Never hand-roll a brand header, and never bake a colour into an editor.** No `EditorGUI.DrawRect`,
  no `GUIStyle` carrying a `textColor`, no `Color32` literal. Colour lives in the role tokens at the top
  of `Samples~/Examples/Inspector/Editor/ItsRenjiInspector.uss` and NOWHERE else, which is what makes a reskin an
  edit to one block instead of a hunt through every editor in the product.
- **IMGUI is never the fallback when a control looks hard.** The framework has no IMGUI islands and a
  new one is a defect, not a shortcut. A Unity control whose own input is an IMGUIContainer cannot be
  styled from USS at all and is REPLACED rather than fought - see the colour swatch in
  `conduit-inspector-examples.md`.
- **Guidance is `[Note]`, `[Warn]`, `[Required]` and `[Validate]`, never a hand-drawn box**, and it is for
  the user's next action only: validation errors, consequence warnings before a costly click, empty
  states, and required workflow steps (a naming convention a button depends on). Never use one to explain
  how the script works - that lives in the code's summaries, and an inspector full of mechanism prose
  buries the notices that actually need reading.
- **Delete a notice that is neither of those - do not reword it.** One explaining mechanism, or handing
  out general "wire this to that" setup guidance that no button depends on, is inspector hand-holding in a
  product the author finishes before release. Deleting it is the fix; only a notice tied to a real
  validation failure, a real consequence, or a real button earns its space.
- **A status display always says something: a blank state is not neutral, it is unreadable.** Silence
  reports nothing and cannot be told apart from a state nobody drew, so "cannot tell" is a label the reader
  gets to see, never an absence. Make the states COVER every combination of their inputs so blank is
  unreachable, and keep them independent indicators rather than a first-match ladder - two lighting at once
  shows a contradiction that precedence would have hidden.
- **The duplicate-header rule applies to SHADERS too:** when a `.shader` declares a `CustomEditor`
  (ShaderGUI), that editor owns all visible grouping, so the Properties block must not carry `[Header]` or
  `[Folder]` drawer attributes for the same sections - they render twice or fight the editor's own layout.
- **Read the live window with `editor-window-controls` before writing a probe script or sweeping
  screenshots.** It returns every control in an editor window, IMGUI and UI Toolkit alike, with each
  one's type, name, enabled state and RECT - and the rect is the valuable part, because it turns "that
  button is the wrong colour" into a screen rectangle whose pixels can be sampled for an exact value.
  It does NOT descend into a composite control's internals (a ListView reports as one entry), so for a
  part it does not name, take the parent's rect from it and sample the pixels; do not guess Unity's
  internal class names. Pair it with `editor-window-list` for the window's own screen rect, and read
  every rect as POINTS - hard stop 12 owns the coordinate frames and why they disagree.
- **A selection set through the bridge does NOT rebuild the Inspector while the editor is unfocused,
  and an unfocused editor does not repaint**, so a read taken straight after `editor-selection-set`
  reports the PREVIOUS selection and reads as though nothing changed. A compile or domain reload
  rebuilds it; `ActiveEditorTracker.sharedTracker.ForceRebuild()` from a MenuItem does too. Neither
  makes it PAINT, so a control that builds its children during layout (a ListView's rows) stays empty
  to any reader until the editor is focused. Verify styling against a window the user is looking at.
- **An inspector edit made in play mode must reach the running program - push each changed field, never
  the whole proxy.** In edit mode it ends at `ApplyModifiedProperties()`, since UdonSharp carries the proxy to
  the program at build. In play mode write each changed authored field with `SetProgramVariable`; Unity drops
  the edit on leaving play mode like any other. `conduit-udonsharp.md` owns why `CopyProxyToUdon` cannot do it.
- **UdonSharp paints a violet bar down the left of EVERY U# behaviour's inspector, from OUTSIDE your
  subtree, so no stylesheet of yours can reach it.** On attach, `UdonSharpBehaviourEditor` parents an
  absolutely positioned `VisualElement` named `UdonSharpScriptBar` (width 3, `Color32(139,127,198,220)`)
  to the `EditorElement` above your `InspectorElement`, and widens THAT element's `paddingLeft` to 4 to
  make room; over Unity's `#383838` the bar composites to `#8075B2`, which reads as a purple strip beside
  any light panel. Suspect it FIRST when an unexplained strip or gutter appears at a branded inspector's
  edge, and identify such a strip by dumping the LIVE visual tree (name, class list, rect, resolved
  colour) rather than by sampling screen pixels and guessing an owner - the owner's name is written on
  the element. Kill it from your own attach hook: from the `unity-inspector-element` ancestor take ITS
  parent, find the child named `UdonSharpScriptBar`, set `display: none` and zero that parent's
  `paddingLeft`. HIDE, never remove, so UdonSharp's own detach handler still disposes of its element; and
  never patch the SDK package, which the next SDK update overwrites.
  (`ItsRenjiInspector.HideHostScriptBar`)
- **Never block the editor to generate something - async is the default and synchronous is a hard
  block.** Anything that fetches, downloads or shells out runs off
  `EditorCoroutineUtility.StartCoroutineOwnerless` (already a project dependency), with `UnityWebRequest`
  for http and an event-driven `Process` for a tool. Choosing a synchronous call requires having SEARCHED
  for the async route and established there is none; "it was simpler", "it is usually fast" and "it is
  only a small download" are not that. A frozen editor is indistinguishable from a crashed one, and the
  author's only recourse is killing Unity and losing the scene.
- **`Process.StandardOutput.ReadToEnd()` followed by `StandardError.ReadToEnd()` deadlocks the editor
  permanently - and it is the shape almost everyone writes first.** The parent blocks until stdout closes;
  the child blocks the moment the stderr pipe buffer fills (about 4 KB) and so never closes stdout.
  Neither moves again, and on the main thread that means no repaint, no input, no cancel, only Task
  Manager. It passes every quiet test and arrives the first time the tool is chatty - for yt-dlp, one
  warning line per unavailable video. Read both streams through `OutputDataReceived` /
  `ErrorDataReceived` with `BeginOutputReadLine()` / `BeginErrorReadLine()`, poll `HasExited` from the
  coroutine instead of calling `WaitForExit()`, and give every external process a timeout that kills it.
- **A progress bar reports the REAL unit of work and names the item in flight** - never a percentage the
  code invented, never a spinner over an unknown count. Offer cancel for anything that can outlast a
  second or two, and make cancel actually stop the work rather than hide the bar.
- **`EditorUtility.DisplayCancelableProgressBar` is modal - it blocks the editor and undoes the whole
  point of working asynchronously.** The coroutine keeps ticking, so the code READS as async while the
  editor sits dead behind the dialog with nothing clickable but Cancel. Report into
  `UnityEditor.Progress` instead (`Progress.Start` / `Report` / `Remove`, plus `RegisterCancelCallback`):
  it is non-modal, it shows in the status bar, and it belongs to the EDITOR, so it survives a selection
  change, a panel rebuild and a closed window. That is what the SDK uses for U# compiles and uploads.
- **Hold progress as data, not inside the elements that draw it.** An inspector is rebuilt whenever the
  selection changes, so a bar whose text and fraction live in its own `VisualElement`s vanishes mid-run
  and takes its cancel button with it, leaving work running that nobody can stop. Keep a small state
  value (running, text, fraction), have the panel RENDER that state, and render it again as the last
  step of BUILDING the panel - which is what lets a freshly built card join a run already in progress.
  The SDK's `BuilderProgress` takes a `ProgressBarStateData` for exactly this reason.
- **Fail closed: a cancelled or failed generation leaves NOTHING behind.** Half a playlist, an object
  with some of its fields written, generated files with no owner - each is worse than no result at all,
  because the author cannot tell a partial one from a complete one and will ship it. Commit the result
  only once every part of it has succeeded, and delete whatever was already written when it has not.
- **Fill a large serialized array with a sequential `Next(false)` walk, NEVER `GetArrayElementAtIndex(i)`
  in a loop.** Indexed access re-walks the array from its root, so the loop is quadratic: 1,000 entries cost
  163 ms and 16,000 cost 38.6 s, against 13 ms and 188 ms for the sequential walk. Take element 0 once, then
  advance it - a 50,000-entry pool drops from about six minutes to under a second.
- **Build a serialized `VRCUrl` by writing its relative `url` string, never by assigning a `VRCUrl` object.**
  An editor that constructs them ends up reaching for `VRCUrl.Empty` as its "no url" value, which is a shared
  static and poisons every field initialised from it (`conduit-udonsharp.md`). Generating strings and writing
  them through `SerializedObject` keeps Undo and prefab overrides working and makes that class of bug impossible.
