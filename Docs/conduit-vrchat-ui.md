# VRChat world-space UI

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before building or fixing world-space UI: canvases, ScrollRects, buttons,
> input fields, or reparenting anything under a canvas.**
>
> **This file owns uGUI INSIDE A WORLD - what a player sees and touches on a canvas - and nothing else
> goes in it.** "UI" names two unrelated things here and that is the trap: an INSPECTOR built with UI
> Toolkit is `conduit-inspector-examples.md` and `conduit-editors.md`, and nothing about it belongs in
> this file. DRIVING a control in play mode through the bridge is `conduit-vrc-input.md`; an EDITOR
> WINDOW's own controls are `conduit-project-workflow.md` and `conduit-editors.md`; and the layer and
> collision setup a canvas depends on is `conduit-layers.md`, with only the canvas-specific requirement
> stated here. **The word "input" in a fact is not evidence it belongs to any particular file** - a
> world-space input FIELD is this file, driving one is not.

## Setting up a world-space canvas for VRChat

Do this BEFORE any styling, and do not infer it - a canvas missing these looks perfect in the editor and
is dead to the pointer in VRChat.

- **Render Mode World Space, plus `VRC.SDK3.Components.VRCUiShape`.** Without the shape the UI renders
  normally and is completely non-interactive in VRChat, with no error anywhere.
- **And the Render Mode itself must be World Space - Screen Space and Overlay fail the same silent way.**
  `VRCUiShape` requires a World Space canvas; on Screen Space or Overlay it raises a runtime error inside
  the client that you never see, and the panel renders perfectly while accepting no input at all. Set the
  render mode BEFORE adding the shape, because the symptom afterwards is identical to a missing shape, a
  missing collider and a wrong layer, and all four are usually chased in the wrong order.
- **A BoxCollider whose size MATCHES the canvas rect**, with a thin Z (1 unit). The pointer needs a
  physics hit before the GraphicRaycaster can resolve which graphic.
- **Resize the collider every time the canvas size changes.** The rect and the collider are one control
  surface in two components and nothing keeps them in step; a stale collider leaves dead margins or
  catches presses off the panel, and both read as a broken button rather than a stale number.
- **Layer `Walkthrough` (17)**, on the canvas and its children.
- **Set that collider's `Exclude Layers` to `Pickup`.** `Walkthrough` excludes `Player` and
  `PlayerLocal` ONLY - it still collides with `Pickup`, so the panel is a solid wall to any thrown object.
  A collider with no Rigidbody of its own belongs to the nearest Rigidbody UP the hierarchy, so a panel
  mounted on a hinged door hands that impulse straight to the door and swings it. `excludeLayers` is
  per-collider, so it stops this without touching the layer, the global collision matrix, or the pointer.
- **The canvas Z axis must face AWAY from the viewer, or every label renders mirrored.** A canvas at
  identity is read from its -Z side, so a panel approached from +Z needs a 180 degree Y rotation.
- **Mount the canvas under a plain GameObject at scale 1 that carries its position and facing, and keep
  the 0.001-0.005 scale on the canvas.** Moving or resizing the whole UI is then one ordinary Transform
  edit at a usable sensitivity, and a builder rebuilding the canvas never fights a hand placement. Putting
  the tiny scale on the mount instead makes every scale drag jump wildly. At 0.001 one canvas unit is one
  millimetre, so every layout number is a real measurement.
- **Leave an EventSystem in the scene**, and set every Button's Navigation to None (see Buttons below).
- **`There can be only one active Event System.` in play mode is EXPECTED - never delete yours to
  silence it.** ClientSim adds its own unconditionally, so in the editor there are always two; VRChat
  does not, so the warning never appears in the real client. Removing the scene's EventSystem clears
  the message and leaves the UI dead everywhere that matters. It is one of the guaranteed ClientSim
  noises listed in `conduit-compile-console.md`.
- **Never nest a Canvas inside the world-space Canvas - it kills raycast propagation to `VRCUiShape`
  and the EventSystem never receives the click.** A Button under a child Canvas is silently dead: it
  renders, it highlights in the editor, and it does nothing in VRChat. Build the whole panel out of plain
  RectTransforms under the ONE canvas. This is the first thing to check when a button does nothing.

## Visibility

- **You cannot detect a world-space canvas being drawn from UdonSharp.** Three separate reasons, so do
  not go looking for a way round one of them: `UdonBehaviour` does define `OnBecameVisible` (it runs
  `_onBecameVisible`), but **UdonSharp never injects it**, so a U# method by that name is silently dead;
  both it and `OnWillRenderObject` are `Renderer` callbacks, and uGUI draws through `CanvasRenderer`,
  which is not a Renderer; and neither reports OCCLUSION in any case - Unity's visibility signals are
  frustum-only, so "hidden behind something" is not available at any price.
- **Do not build a visibility gate for a panel that has no `Update`.** Unity already frustum-culls the
  canvas, and a panel driven by `SendCustomEventDelayedSeconds` chains costs zero Udon dispatch per frame,
  so the only saving left is a handful of draw calls while it is on screen. A proximity trigger does work,
  but for a SOLD asset it means every buyer fitting a bounds volume per unit - friction that outweighs it.

## A HUD showing another camera's view

A head-following HUD is ordinary world geometry, so a full-screen camera view behind it (a garage shot, a
cutscene, a spectator view) is a layer split between two cameras, not a UI feature.

- **Leave the HUD's layer out of the world camera's culling mask, or that camera films the HUD.** The HUD
  floats in front of the player's head, so any camera pointed at the player - a car with its driver, say -
  sees the menu panels mirrored in the middle of its shot, and its own view canvas inside it. Moving the
  HUD out of the way from that camera's `OnPreCull` and back in `OnPostRender` was tried and did not take
  effect; the mask did.
- **Narrow the player's own camera to the HUD's layer while the view is up, or the scene around them
  draws over and through it.** `VRCCameraSettings.ScreenCamera.CullingMask` is settable from Udon: save it
  when the view opens, set it to the HUD's layer, put the saved mask back when it closes. Glass and
  geometry between the player's eyes and the HUD otherwise show over the view. The full list of the
  screen camera's readable and writable properties is in `conduit-udonsharp.md`.
- **Size the view from the screen camera, not from a guess.** `ScreenCamera.FieldOfView` and `.Aspect`
  give the player's real field of view and aspect: copy both onto the world camera (`Camera.aspect`
  included) and size the view canvas to `2 * distance * tan(fov / 2)` high at its distance from the eyes,
  so the shot lines up with the screen edge for any FOV setting.
- **Show the player as a mirror does: keep MirrorReflection (18), drop PlayerLocal (10).** The local
  avatar is drawn twice, a head-hidden first-person copy on PlayerLocal and a whole one on
  MirrorReflection, so a world camera filming the player wants the second.

## Canvas objects: creation and reparenting

- **`gameobject-create`'s parent argument does not reparent.** Passing a parent path still creates the
  object at the SCENE ROOT with layer 0 and a plain `Transform`. The working sequence for a UI object is
  `gameobject-create` -> `gameobject-set-parent` -> `gameobject-component-add RectTransform` -> fix
  `m_LocalScale` / `m_LocalPosition.z` -> set the layer to the canvas's layer. Adding a `RectTransform` to
  an object that has a plain `Transform` does work and replaces it.
- **Position the container FIRST, then reparent into it.** `gameobject-set-parent` preserves WORLD
  position, so reparenting into a container that is not yet positioned writes a compensating offset onto
  the child - and under a world-space canvas that offset is enormous. A container still sitting at local
  z -500 made Unity write local z +500 onto each child to hold their world z; correcting the container
  afterwards carried the +500 along, rendering the text 2.5 metres behind the panel at canvas scale 0.005
  while every anchor, size and colour read correct.
- **To POSITION a RectTransform, write `m_AnchoredPosition` - writing `m_LocalPosition` silently
  reverts.** A RectTransform DERIVES `m_LocalPosition.x/y` from anchoredPosition, anchors, pivot and
  sizeDelta on every layout rebuild, so a localPosition write is overwritten moments later. The trap is
  that it reports success honestly: the value IS applied when written and reads back changed, then the
  next layout pass restores the old one. Worse, `z` is NOT derived, so the z of the same write STICKS -
  leaving a half-applied transform that looks like the command partly worked. Observed: a nav button
  written to localPosition `(-144, 0, -0.001)` kept the z and snapped back to the reparent's
  `y = -808.72`, putting it under the world floor while the report said it had moved.
- **After any reparent under a canvas, read back `m_LocalPosition`, `m_LocalScale` AND `m_LocalRotation`.**
  `gameobject-set-parent` preserves the WORLD transform, so it writes a compensating value into all three
  and `m_AnchoredPosition` exposes none of them - anchoredPosition is 2D, and the z, the scale and the
  rotation all ride along invisibly underneath it. Observed: an empty created at the scene root came back
  with local scale 200 under a 0.005 canvas and 1000 under a 0.001 one, with local z equal to the canvas's
  world z (319.97 under a canvas at z 0.32), and - because a VRChat canvas carries the mandatory
  180-degree Y rotation - with a 180-degree LOCAL rotation that leaves the child facing backwards and
  mirrored. Reset all three: scale 1, z 0, rotation identity `{"x":0,"y":0,"z":0,"w":1}`.

- **Set `Canvas.additionalShaderChannels` for any vertex channel beyond position/colour/uv0 - it defaults
  to `None` and silently strips the rest.** A mesh effect that writes uv1 (Unity's stock `PositionAsUV1`,
  which a rounded-corner UI shader typically needs) has its data dropped before the shader ever sees it. The
  tell is a torn wedge or triangle rather than a blank quad, so it reads as a broken shader rather than a
  one-field canvas setting. Enable `TexCoord1`.

## Showing and hiding a panel

- **A `CanvasGroup` has three fields and they must move together.** `alpha` controls visibility,
  `interactable` whether controls respond, and `blocksRaycasts` whether the panel swallows pointer hits.
  Fading `alpha` alone leaves an invisible panel still eating every click aimed at whatever is behind it -
  which reads as the thing behind being broken, not as the hidden thing being in the way. Hide: alpha 0,
  both bools false. Show: raycasts on as the fade starts, `interactable` on when it finishes.
- **A CanvasGroup fade is the right way to swap panels**, because it needs no object toggling and so does
  not re-run anybody's `OnEnable`.

## Text

- **Turn `Raycast Target` OFF on every TMP label, always.** A label is not a control, and leaving it on
  makes it swallow pointer hits meant for the button, card or scroll area behind it - which presents as a
  dead button rather than a stray label, so it is hunted in the wrong place entirely. The only text that
  keeps it on is text that IS the click target.
- **Match the product's own font asset and size, never TMP's default.** A new `TextMeshProUGUI` arrives on
  `LiberationSans SDF` at size 36, which on a 0.001 canvas is enormous and visibly not the rest of the
  panel. Read an existing label in the same canvas and copy its `m_fontAsset`, `m_sharedMaterial` and
  `m_fontSize` rather than picking a number.
- **A `Dynamic` atlas population mode font asset works in an uploaded VRChat world - never bake a CJK face
  as a static atlas.** Dynamic rasterises only the glyphs actually used, so a full CJK set costs nothing in
  the shipped package, while a static bake of the same set dominates its download size. Do not reach for a
  static atlas, a character subset, or a per-language font swap to solve a problem that does not exist, and
  do not warn that dynamic is unproven in the client - it ships in a released product. (User-taught.)
- **A font asset whose `m_FallbackFontAssetTable` is empty renders every glyph it lacks as a box.** A brand
  face covers Latin only, so the moment any localized string arrives the panel fills with boxes and reads as
  a broken font rather than an unset field. The tell arrives early and quietly, as a console warning naming
  one character: `The character with Unicode value \U0001F3B5 was not found in the [<font>] font asset or
  any potential fallbacks`. Assign the fallback before adding a second language, not after.
- **NEVER set TMP overflow mode `Ellipsis` on a label that can hold a player name or any user-entered
  text - in the VRChat client it renders the label completely blank the moment the string contains one
  glyph outside the primary atlas.** The string, the code path and the font are all correct; it is TMP's
  ellipsis-glyph handling in the shipped version. Three things make it expensive to find. Play mode hides
  it entirely, because the editor patches dynamic atlases on demand, so the label reads right in the editor
  and empty in world. ASCII never triggers it, so every test name passes and a Cyrillic first letter blanks
  the row. And BLANK is the discriminator: a merely missing glyph renders as a box, so an empty label is
  the ellipsis path, not a coverage problem. Use Overflow (`m_overflowMode: 0`) or Truncate (`3`);
  constant-ASCII labels may keep Ellipsis. **The plausible-wrong fix is baking the font to a static atlas -
  that was tried, and it is not the cause.** (Validated in-world.)
- **A `ContentSizeFitter` sizes a TMP label to its glyphs PLUS `m_margin`, so a rect width is NEVER a text
  width.** Anything measuring the RectTransform to drive a marquee, a fade or an overflow test silently
  inherits whatever sits in those four fields, and they are set by accident easily: TMP's margin handles in
  the Scene view write them, so a label that was once stretched full width and pulled in by hand keeps that
  inset forever after the layout around it is rebuilt. Only `x` and `z` feed the width. Observed: a right
  margin of 959.84 (half the canvas width, the tell that a handle was dragged to the centre) on a label
  whose glyphs measured 207.43 reported a rect of 1177.27, and a title scroller computing its travel from
  that rect pulled the text clean off the left of its mask and then held an empty box for the full end-hold.
  The symptom reads as a broken scroller, and every number in the scroller is correct.

## Masks and materials

- **A MASKED graphic will not show a material property you just wrote - switch its GameObject off and
  on, because dirtying it does nothing.** `MaskableGraphic` never draws the material it is given: it
  hands it to `StencilMaterial`, which returns a COPY carrying the stencil comparison and caches that
  copy against the original, so the write lands on an asset nobody is drawing and `SetAllDirty` is
  served the same stale copy back. Disabling releases the cache entry and re-enabling builds a fresh
  one from the values just written. **The tell is that the value is correct on the material and absent
  on screen.**
- **Search for `Mask` up the graphic's OWN ancestry, not on the scroll viewport.** A `RectMask2D` is a
  different component that clips through the CanvasRenderer and makes no material copy, so finding one
  proves nothing and reads as an all-clear; the `Mask` causing this commonly sits on the control itself,
  a Slider or a Button, between the viewport and the graphic. `find-by-component Mask` answers it in one
  call.
- **Prefer the off/on toggle to clearing Unity's stencil cache by reflection.** Both work, but the
  toggle is ordinary API, so it runs in an uploaded world as well as in the editor; reaching into
  `StencilMaterial`'s private cache is editor-only and belongs to a shader reimport, where every cached
  copy is stale at once. Guard against re-entrancy if the toggled object's `OnEnable` does real work,
  and toggle an object BELOW any Animator so the flick does not reset its state.

## ScrollRects

- **Never compute a ScrollRect's content size from hand-copied numbers - ask the layout for it.** A
  formula like `count * cardWidth + gaps` only holds while every constant matches the live layout, and it
  silently stops holding the moment a child's real rect differs. Use
  `LayoutRebuilder.ForceRebuildLayoutImmediate(content)` then `LayoutUtility.GetPreferredSize(content, 0)`
  so Unity's own layout pass answers.
- **Split layout control by axis - control the CROSS axis, never the scroll axis.** A vertical list is
  `childControlWidth`/`childForceExpandWidth` ON so cards fill the width, and `childControlHeight`/
  `childForceExpandHeight` OFF so the `ContentSizeFitter` can sum each card's own height. Control ON for
  the SCROLL axis makes the group DRIVE card heights and fight the fitter. Fitter: Unconstrained across,
  Preferred Size along.
- **Keep a card's `sizeDelta` and its `LayoutElement` preferred size in AGREEMENT on the scroll axis.**
  With child control OFF the group reads `sizeDelta[axis]` and ignores the `LayoutElement`, so the two
  disagreeing is what makes a row wider than every serialized number claims: cards left at Unity's
  default 100-wide rect against a `LayoutElement` of 70 gave a true row of 415 in a 295 scroll range.
- **Keep the viewport `Image` colour alpha at `1`** when the viewport uses a `Mask`, even with
  `Mask.showMaskGraphic` false. Never `Color.clear` or alpha `0` - zero-alpha viewport images can hide
  every masked child in VRChat UI.
- **Set `scrollSensitivity` to `0`** on every ScrollRect in these canvases and drive scrolling deliberately
  through the intended VRChat interaction pattern.
- **After wiring a Scrollbar into a ScrollRect from the inspector, round-trip play mode - or toggle the
  ScrollRect off and on - before testing it.** ScrollRect subscribes to the scrollbar only in `OnEnable`
  or through its `verticalScrollbar` / `horizontalScrollbar` PROPERTY setters, and an inspector write
  reaches neither, so a freshly assigned scrollbar is inert and nothing reports it. The tell is a handle
  that moves while the content stays still. `[ExecuteAlways]` on ScrollRect is what makes the re-enable
  take effect in edit mode rather than needing a build.

## Buttons

- **NEVER override `Interact()` on a canvas button.** An UdonBehaviour that overrides `Interact` AND
  owns a collider is a WORLD INTERACTABLE, so the button fires from across the room the instant a player
  looks at it and presses Use - bypassing the canvas pointer entirely and hanging a Use prompt on every
  control. Drive a canvas button from its `Button` component instead. `Interact()` stays correct on a
  PHYSICAL control such as a knob or a handle, which is why the mistake looks reasonable.
- **Set a UI Animator's Culling Mode to `Always Animate`.** OBSERVED: an Animator on a uGUI Image with
  `Cull Completely` never advanced its clip in play mode; switching the same Animator to `Always Animate`,
  changing nothing else, made it run. It presents as a broken clip or an unassigned controller, so check
  this before rebuilding either. **`CanvasRenderer` is NOT a `Renderer`, and that is the structural reason
  the setting matters:** its full member list carries `transform`, `gameObject`, `tag` and the legacy
  Component shims with no `bounds`, `isVisible`, `enabled` or `sharedMaterial` anywhere, so it derives from
  `Component`. A culling mode that decides by renderer visibility therefore has no renderer to ask about on
  a canvas element. (`UnityEngine.CanvasRenderer` members, UnityEngine.UIModule)
- **Every `Button` must use `Navigation.Mode.None`, never `Automatic`.** Automatic navigation can create
  odd focus and raycast behaviour in VRChat.
- **Hover WORKS on world-space UI in VRChat - the pointer raises enter and exit, so `Highlighted` is a
  real state worth designing.** Do not assume a VR laser behaves like a touchscreen and design only the
  pressed state; a button that reacts solely on press feels dead while the user is aiming at it. Design
  both, and make each visible at arm's length rather than relying on Unity's stock Color Tint ramp, whose
  4% highlight and 22% press dim are invisible in a headset.

## Input fields

- **VRChat input fields do NOT fire `On End Edit`.** Neither `TMP_InputField` nor `VRCUrlInputField` ever
  raises `onEndEdit` in VRChat - never wire submission to it or advise it. Always use `On Value Changed`.
  The on-screen keyboard commits the whole entered string in a single `On Value Changed`, not character by
  character, so there is nothing to debounce.
- **Guard the handler against programmatic sets.** `On Value Changed` ALSO fires when a script sets the
  field's `text` - e.g. pushing a synced value back into the field on remote clients - which re-runs the
  submit handler on every client. Compare the field text against the current synced value and return when
  unchanged. That holds regardless of keyboard focus timing, whereas an `isFocused` check can wrongly
  block the real submit if the keyboard commits after the field has lost focus.
- **Use the `WithoutNotify` setter whenever a script writes a control's value.** `Toggle.isOn` and
  `Slider.value` both raise `onValueChanged` on assignment, so pushing a restored or synced value back
  into the control re-runs the submit handler on every client that received it. `SetIsOnWithoutNotify` and
  `SetValueWithoutNotify` write the value and stay quiet. This is the same re-entrancy family as the input
  field below, and the uGUI controls give you a way out that the input field does not.
- **A handler that clears or sets its OWN field re-enters SYNCHRONOUSLY the instant it writes `text`.**
  Udon keeps locals on a shared heap with no call stack, so the nested call overwrites the outer call's
  locals mid-check - a code read as "123456" becomes "" after `text = ""` and the match silently fails.
  Read the decision into a value BEFORE the write, and set a re-entrancy busy flag around the write so the
  nested call bails at the top.
- **A world-authored URL only reaches a `VRCUrlInputField` through `SetUrl(VRCUrl)` at runtime.** Serialize
  the address as a `VRCUrl` field, keep a reference to the input field, and call `SetUrl(theUrl)` in
  `Start()`. That is how a Discord invite, a shop link, or a search-endpoint prefix gets in front of anyone.
- **An input field with no URL and no `SetUrl` call does NOTHING when pressed** - no error, no keyboard, no
  feedback - while looking perfectly configured in the inspector (interactable, text component assigned,
  navigation None, `m_Text` blank like any un-filled field). That silence reads as a broken button, so
  check that something actually put a URL in the field BEFORE investigating interaction. Make it
  impossible to ship silently: `Debug.LogError` in `Start()` when the input-field reference is wired but
  the `VRCUrl` is blank.
- **`ReadOnly` is irrelevant to whether the link works** - a ReadOnly field surfaces the URL exactly like
  an editable one (tested in the VRChat client both ways). Prefer `ReadOnly = true` for a link nobody
  should edit; leave it false only when the visitor is meant to type. It looks like the culprit when a
  field is dead, and it is not.
- **Mask an input field by writing `contentType` - both `InputField` and `VRCUrlInputField` expose it with
  a getter and a setter, alongside `asteriskChar` for the mask character.** `Password` is member 7 and
  `Pin` is 8, and `Pin` is the same masking plus integer-only validation, so a numeric door code rejects
  letters at the field instead of in the handler.
- **Look a masked field up under `contentType`, never under `Password` - the word appears in none of the
  registry's 35,222 definitions.** Enum members are not node definitions and compile to a constant load, so
  `udon-exposure Password` answers "Udon does not expose it under that name" for a feature that works.
  Search `__set_contentType__` and `__set_asteriskChar__`. (SDK registry)
- **A `TMP_InputField` cannot be masked from Udon - set its Content Type in the inspector instead.** Its
  whole writable surface is `text`, `SetTextWithoutNotify`, `readOnly`, `richText` and `enabled`, with no
  `contentType` and no `asteriskChar`, so masking is an authoring decision on TMP and a runtime one only on
  `InputField` and `VRCUrlInputField`. (SDK registry)
- **A legacy `InputField` exposes far more than `readOnly` and `interactable` - 30 setters, including
  `characterLimit`, `characterValidation`, `inputType`, `lineType`, `placeholder`, `caretPosition` and
  `selectionColor`.** `characterLimit` is independent of the content type, so a four-digit masked code is
  `contentType` plus one number rather than a validation handler.
