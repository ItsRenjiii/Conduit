# Working on a change

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before starting any multi-step change to a scene, prefab, or asset system.**
>
> **This file owns how a change is SCOPED and CARRIED OUT, and nothing else goes in it.** Being about
> "how to work" makes it a magnet for any procedure at all, so the test is what the procedure is FOR: how
> to write the code is `conduit-code-style.md`, compiling and reading the result is
> `conduit-compile-console.md`, diagnosing a cause you did not observe is `conduit-diagnostics.md`, and
> prefab mechanics - Prefab Mode, linkage, unpacking - are `conduit-assets-prefabs.md`. A rule that is
> already a hard stop in the index is never restated here.

## Scope

- **Make surgical edits: change only what the task needs.** Do not reformat, reorder members, or rewrite
  unrelated code - it bloats diffs, risks regressions, and buries the real change.
- **Reuse before adding.** Look for an existing script, helper, prefab or material before creating a new
  one. No duplicate or near-duplicate files, no `NewBehaviour` / `Script 1` / `…Copy` names; the file name
  always matches the class name.
- **Finish in a compiling state, never half-written.** Before calling it done, remove scratch artifacts:
  temp test scripts, disabled placeholder GameObjects, commented-out blocks and leftover debug logs created
  for this change. Preserve unrelated work.

## Scratch work

- **Find an existing scratch folder under `Assets/` before creating temporary scripts or assets.** Inspect
  candidates such as `Scratch` and `_Scratch`; prefer the user's designated folder, otherwise reuse a
  suitable project-owned folder. Do not use a third-party package's internal scratch folder.
- **Reuse one location across tasks.** If several suitable folders exist, choose one and keep using it;
  do not create another spelling or a new scratch folder under each product. Create one clearly named
  project scratch folder only when no suitable folder exists.
- **Keep temporary editor scripts in the scratch folder's `Editor` subfolder or a verified editor-only
  assembly.** Clean up only this change's temporary artifacts, and leave the shared scratch folder and
  other tasks' work in place. Asset creation and deletion follow `conduit-assets-prefabs.md`.

## Structure

- **Build assets as prefab-ready systems under one shared scene root.** If UI, managers, triggers, pools,
  third-party integrations, screens, cameras, and helper objects are part of the same asset, they live
  under the same named root GameObject rather than scattered as separate top-level scene objects. Use
  clear child groups per responsibility so the hierarchy drags into a prefab cleanly.
- **Put behaviours on the object that owns their responsibility.** A search behaviour on the search UI
  root, a queue manager on the queue/state object, a trigger behaviour on the trigger, third-party and
  video-player internals under their related screen or subsystem group. Do not stack unrelated managers on
  a convenient shared object just because it is easy to find.
- **Give GameObjects names that describe what that part is or does, not the whole system** - a part named
  after the whole (an engine named after the car) leaves nobody able to tell what they are looking at.
  Don't rename or reparent existing objects unasked; save scenes and assets when the change is complete,
  not mid-way.
- **Nothing that moves may be marked static** - not a door, a pickup, a lid, a turntable, a drawer, nor
  any parent or child of one. Batching Static bakes the renderer into a combined world-space mesh, so the
  transform keeps rotating while the visible geometry never moves again. **The symptom is maddening and
  points nowhere near the cause**: physics is fine, the joint is fine, the script is fine, the state
  machine reports open and closed correctly, and the pivot's rotation demonstrably changes - only the mesh
  sits still. Every joint, rigidbody, mass, damping and wiring value can be verified identical to a
  working twin and the fault is still a checkbox at the top of the inspector.
  **Check the Static flags FIRST whenever a transform provably moves and the geometry does not**, and
  clear them on the mover, its pivot, and its children.

## Builders

- **Editor builders must mirror the intended prefab hierarchy exactly.** They create or repair the shared
  root and owned child groups, parent legacy loose objects back into that root when possible, and rebuild
  only the specific generated subtree they own. **Never wipe designer-owned siblings, third-party
  internals, tuned UI layouts, or unrelated children** while rebuilding one part of an asset.
- **Before changing or rerunning a builder on a scene the user has adjusted by hand, inspect the live
  hierarchy and update the builder to match it.** The current scene layout is the source of truth until
  the user says otherwise; do not assume an older script layout is still canonical.

## Verifying visually

- **Prefer `scene-view-frame <object>` + `screenshot-scene` over teleporting the ClientSim player and
  using `screenshot-game`.** The scene view frames any object in one call from any angle, while
  player-hopping guesses at facing directions, moves the user's player around, and burns iterations. The
  user has asked for scene view more than once. `screenshot-game` is only for what the PLAYER would see -
  UI overlays, culling, camera effects - when that perspective is the thing under test.
- **Drive a world-space control from the bridge to verify it end to end** - aim with `vrc-look-at`, press
  with `vrc-input use press`, then confirm by screenshot or state read-back (`conduit-vrc-input.md`). A
  user's own in-editor test still counts as the interactive verification: when they say they tested
  something, record it and move on rather than re-running it, which is slower than they are.
- **Use Conduit's hierarchy inspection tools for live scene structure**: `scene-tree` for a whole scene,
  `gameobject-tree <target> [| maxDepth]` for one asset or subtree. Do not create throwaway editor scripts
  just to dump a hierarchy.
- **Click by label, never by a coordinate worked out by eye** - hard stop 12 in the index carries that
  rule and the three disagreeing coordinate frames behind it, and the index is always in context, so it is
  not restated here.
- **Select a docked editor window's tab before enumerating or clicking its controls.** An inactive dock tab
  may expose only tab labels or `rootVisualContainer`; that is NOT evidence that the window is unsupported
  or UI Toolkit-only. Click the tab with `editor-window-click`, rerun `editor-window-controls`, and use the
  discovered Conduit button instead of falling back to desktop automation.

## Staying in the loop

- **Confirm destructive or irreversible actions first**: deleting an asset, overwriting a prefab, or
  modifying a scene the user didn't ask you to touch.
- **Flag a concern before you act on it - don't execute-then-explain.** If a requested change looks wrong
  (it would lose meaning, collide with or duplicate an existing name, break a convention, or just seems
  off) say so first and let the user decide, even when they asked for it. They can always tell you to
  proceed; a collaborator who surfaces the problem is worth far more than one who silently does what was
  said and reports the mess afterward.
