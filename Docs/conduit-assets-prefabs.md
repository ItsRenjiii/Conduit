# Assets, prefabs & scenes

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before moving, renaming or deleting an asset, editing a prefab, or standing up
> a scene.**
>
> **This file owns the MECHANICS of assets, prefabs and scenes - how they are moved, imported, edited and
> stood up - and nothing else goes in it.** How to SCOPE a change, and what a prefab-ready hierarchy
> should look like, are `conduit-project-workflow.md`. The VRC_ components you then put in the scene are
> `conduit-world-components.md`; only the scene DESCRIPTOR itself is here. Compiling after an asset change
> is `conduit-compile-console.md`. A rule that is already a hard stop is never restated here.

## Asset moves are editor operations, always

- **Keep the `Assets/` folder clean and keep the user in the loop.** When creating scripts or assets, ask
  what they want the root called - `Assets/user-chosen-name-folder/user-chosen-asset-name/` - then a clean
  tree beneath it. They have to be able to navigate the project without guessing.
- **Manage assets through the editor** (Conduit's asset tools / `AssetDatabase`), never raw filesystem
  moves, renames, or deletes. A stray file operation orphans the `.meta` and breaks every prefab, scene,
  and inspector reference pointing at that asset.
- **Moving the `.meta` alongside the asset does NOT make a filesystem move safe.** Believing it does is
  exactly how the rule above gets talked past. Carrying the `.meta` preserves the GUID, so every reference
  still resolves and an integrity check comes back completely clean - which reads as proof the move was
  fine. It is not proof of anything: **the GUIDs are only half the state.** Unity's Library artifact
  database still describes the OLD paths, and a mass move under a live editor leaves it describing a tree
  that no longer exists. Renaming a whole product folder on the filesystem and then calling `refresh` threw
  `The file 'MemoryStream' is corrupted! Remove it and launch unity again! [Position out of bounds!]` and
  needed a full restart.
  The reasoning that leads here is "20 `assets-move` calls each trigger an import, so one fast filesystem
  pass is cheaper". **The import churn IS the editor doing the bookkeeping** - skipping it is skipping the
  work, not optimising it. Use `assets-move` per asset and per folder. A currently open scene is the worst
  case: unload it first, or move it with `assets-move` so the editor re-resolves its own open-scene path.

## Importer settings are a separate object from the asset

- **Set import settings with `assets-importer-modify`, never by hand-editing the `.meta`.** `assets-modify`
  and `assets-get-data` reach the imported ASSET (the `Texture2D`); `textureType`, `spriteMode`,
  compression, mip generation and max size live on its `AssetImporter`, which they cannot touch. Read the
  current values and the real names with `assets-importer-get` FIRST - guessing a name is how meta-editing
  starts. Both verbs are importer-type agnostic, so they cover models and audio the same way.
- **A PNG copied into `Assets/` imports as `m_TextureType: 0` (Default), which a uGUI `Image` cannot use.**
  It must be set to `8` (Sprite) with `m_SpriteMode: 1` before it will show in the UI at all. Names are the
  SERIALIZED ones (`m_TextureType`); the `.meta` spelling (`textureType`) is accepted as an alias.
- **Nested and ARRAY settings work too, via the full SerializedProperty path.** `assets-importer-get` only
  lists top-level names, so this is invisible unless you know to try it:
  `m_PlatformSettings.Array.data[0].m_TextureCompression` sets the default platform's compression. That
  one matters for any smooth gradient or ramp texture - DXT bands visibly on an alpha ramp, and 0 is
  uncompressed.

## Rewriting a generated asset in place

- **Rewrite a generated `Mesh` or `Texture2D` asset through its own API, never with
  `EditorUtility.CopySerialized` over it.** Copying over keeps the GUID, so every reference holds, but it
  changes only the in-memory asset: the copy the graphics card holds is not refreshed, and calling
  `Apply()` / `UploadMeshData()` afterwards did not refresh it either. Renderers keep drawing the PREVIOUS
  data. For a mesh: `Clear()`, then `SetVertices` / `SetNormals` / `SetUVs` / `SetTriangles` / `bounds`.
  For a texture: `Reinitialize(w, h, format, false)`, `SetPixels`, `Apply`. (Unity 2022.3.22f1, D3D11)
- **It stays hidden until the new data actually differs.** A baker that re-wrote identical vertices on
  every run looked fine for many runs. The first bake that changed them drew the old vertices with the new
  bone data: an exploded crowd in the scene, a speck in an isolated render. **The discriminator:** the
  asset's data, read back on the CPU (`mesh.vertices`, `GetPixels`), is correct. The same asset renders
  wrong. `Object.Instantiate` copies of it render right. Reimporting the `.asset` did not clear it.

## Edit prefabs in Prefab Mode, never through the scene instance

Hard stop 2 carries the rule and the loop; what it does not carry is that Prefab Mode is also the FASTER
route - the stage is the authoring surface and the save is one call - so there is no speed argument for
the override path either. The override also quietly DIVERGES from what the prefab contains, which is why
the two can disagree indefinitely without anything reporting it.

**Do NOT re-run `assets-prefab-create` over an existing prefab path to "push" scene changes back into
it.** That replaces the asset rather than editing it, and it is how you get hung calls (the replace path
can raise a modal) plus churned nesting and references. Two such calls at an existing path both timed out
while the asset still held the OLD value and the scene looked correct.

**The tell that you are in this trap: the scene looks right, but `gameobject-component-get` against the
PREFAB (or a grep of the `.prefab` file) still reports the old value.** When the fix belongs to a nested
prefab, fix the SOURCE prefab and let the parent inherit it - verify by opening the parent prefab and
reading the value back, rather than adding an override to the parent.

## Prefab linkage

- **`assets-prefab-unpack` defaults to `outermost` for a reason - passing `completely` silently destroys
  every nested prefab link inside.** `outermost` unpacks the outer prefab and LEAVES nested instances
  linked; `completely` flattens the lot into plain objects. Reach for `outermost` unless the explicit goal
  is to sever the children too. Burned twice on the same asset: rows re-linked as instances were flattened
  back into baked copies by an unpack done only to restructure the ROOT, then re-saved over the asset.
  **The unpack response reports the damage - read that number.** "Nested prefab instances still linked
  inside it: 0" where the prefab had nested instances means the links are already gone. Verify after any
  unpack/recreate cycle with `grep -c m_SourcePrefab <prefab>` and expect one per nested instance.
  A CHILD target is resolved up through `PrefabUtility.GetOutermostPrefabInstanceRoot`, so you may pass
  one. The verb refuses when the target is not part of a prefab instance and when a Prefab Mode stage is
  open. Unpacking deliberately severs the prefab link, so it does not contradict the Prefab Mode rule -
  that rule governs editing an object that IS still an instance.
- **When a prefab edit "does not take" on some consumer, check the consumer actually LINKS the source
  prefab before debugging the edit itself.** A prefab hand-made from cloned (non-instance) objects BAKES
  copies: zero `PrefabInstance` blocks and zero `m_SourcePrefab` lines in its file, so every later fix to
  the source silently never reaches it - presenting as "the bug is still there" against a source asset
  that is verifiably correct. **The one-line discriminator is `grep -c m_SourcePrefab <consumer.prefab>`;
  0 means nothing inherits.** Repair: replace each baked copy with a `PrefabUtility.InstantiatePrefab`
  instance in Prefab Mode, carry the config over, then run the product's reconcile menu item to rebuild
  wiring.
- **Creating a prefab from a scene object nulls every reference pointing outside it.** A UnityEvent keeps
  its method name and string argument and loses only the TARGET, so the asset reads as fully wired and does
  nothing, while the connected scene instance keeps working from an override. **The tell is
  `m_Target: {fileID: 0}` beside a non-empty `m_MethodName`.** Put the behaviour a prefab's own buttons call on
  its ROOT and hand it the scene references at run time, the way a row behaviour takes its manager.
- **Companion trap: a scene instance can PIN an old value as an instance override**, masking the corrected
  prefab value beneath it. A bridge write can update the override's value but cannot REMOVE the override,
  so after fixing the value, tell the user which field to right-click > Revert so it follows the prefab
  again.

## Scenes

- **Standing up a NEW world scene: copy the VRC default scene, do not hand-build the setup.**
  `Assets/Scenes/VRCDefaultWorldScene.unity` is the SDK's ready-to-go world scene - VRCWorld descriptor,
  reference camera assigned, floor, directional light, all wired. `assets-copy` it into the product's
  Scenes folder under the new name and it is immediately playable. Do not rebuild those objects by hand or
  copy them object-by-object across scenes; a hand-copied descriptor drags along leftovers (udonProducts
  entries, DynamicMaterials lists) from whatever scene it came from. (User-taught.)
- **A respawn height at or above the floor is an infinite respawn loop.** The player respawns, is
  already below the threshold, and respawns again - unrecoverable, and it reads as the world being
  broken rather than as one number. Put it well under the lowest reachable surface.
- **Read the descriptor by its SERIALIZED names, which do not match the Inspector labels**, or a bridge
  write silently targets nothing: `spawns`, `spawnRadius`, `spawnOrder`, `spawnOrientation`,
  `RespawnHeightY`, `ObjectBehaviourAtRespawnHeight`, `ForbidUserPortals`, `interactThruLayers`,
  `autoSpatializeAudioSources`, `gravity` (a `Vector3` - world gravity lives HERE, not in Physics
  settings), `layerCollisionArr` (the collision matrix, stored on the descriptor) and `DrawDistance`.
  **And `ReferenceCamera` is a `GameObject`, not a `Camera`** - handing it a Camera reference is the
  obvious move and the wrong type. (SDK 3.10.5)
- **Give a world 2-3 spawn points, not one, and lift them about 0.1 m off the floor.** Simultaneous
  joiners on a single spawn land inside one another, and a spawn flush with the floor can catch on its
  collider. Players face the spawn transform's +Z.
- **`Spawn Order` decides which spawn a joiner gets, and the four options behave differently:** `First`
  always uses spawn 0, `Sequential` cycles through them in order, `Random` picks one, and `Demo` treats
  the spawn point as the centre of the room-scale play area. A world that puts everyone on top of each
  other is usually `First` where `Sequential` or `Random` was wanted. **`Respawn Height` defaults to -100**
  - anything that falls below it respawns, so a deliberately deep pit needs that number lowered or players
  vanish out of it.
- **Every spawn must sit within 1000 units of the world origin on all three axes, and the SDK names the
  consequence.** `WORLD_MAX_SPAWN_OFFSET` is 1000 and the check is `Mathf.Abs` on x, y AND z; past it the
  Build Panel warns that this "can cause issues with Menus and PhysBones". So a far-flung spawn is not a
  vague floating-point worry - it breaks the quick menu and avatar physics for whoever lands there. Move
  the WORLD toward the origin rather than the spawn away from it.
  (`VRCSdkControlPanelWorldBuilder.cs:57, 943-945`, SDK 3.10.5)
- **Nonstandard gravity is PERMITTED, warned about, and never blocked - and the player stays upright
  through all of it.** The Build Panel raises a yellow warning when gravity has an x or z component,
  points upward, or is approximately zero, offering only to open Project Settings; VRChat's own message
  says it plainly - "player orientation is always 'upwards' so things don't always behave as you intend".
  Sideways gravity therefore gives you falling geometry and an upright player, not a wall to walk on.
  **Note it validates `Physics.gravity` from Project Settings**, while the descriptor carries its own
  `gravity` field, and the DESCRIPTOR is the one that governs - ClientSim applies it in play mode as
  well, so the warning points at a setting that is not the one actually in force.
  (`VRCSdkControlPanelWorldBuilder.cs:543-554`, SDK 3.10.5)
- **The reference camera's own `Camera` component must be disabled, or its settings never apply.** The
  descriptor reads that camera as a TEMPLATE for the player's camera; an enabled one is instead a second
  live camera in the scene, so the settings you meant to apply do not reach the player and the view may
  render twice. It is assigned and disabled correctly in the default scene, which is another reason to
  copy that scene rather than wire one by hand - and the thing to check first when clip planes, clear
  flags or post-processing "are set but do nothing".
- **And a post-process volume parented under the reference camera never runs, for exactly that reason.**
  The SDK raises a warning naming the mechanism - "This Camera is disabled at runtime" - so a volume
  childed to Main Camera dies with it. Put the volume on an ordinary scene GameObject; only the camera's
  own SETTINGS are read as a template, not its children.
  (`VRCSdkControlPanelWorldBuilder.cs:213-216`, SDK 3.10.5)

## Editing a `.unitypackage` by hand

Match Unity's container exactly or it fails silently. A package is gzipped **GNU** tar, one directory per
asset GUID holding `asset`, `asset.meta`, `pathname`, and an optional `preview.png`. Python's
`tarfile`/`gzip` defaults produce a file Unity will not open, and the failure gives no error at all -
double-clicking just focuses the editor with no import dialog, which reads like a broken file association
rather than a malformed archive.

Three defaults must ALL be overridden - fixing only the magic is not enough:

- magic at byte 257 must be `ustar  \0` (GNU), not `tarfile`'s POSIX `ustar\000`
- the stream must end in **exactly two zero blocks**, not `tarfile`'s padding to a 10240-byte record
- the gzip header must carry FNAME `archtemp.tar` with XFL 4 and OS 0, not `gzip`'s output filename with
  XFL 2 and OS 0xff

Unity's own entries use mode `0000777`, uid/gid `0`, checksum formatted `%06o\0 `, and NUL uname/gname.
Build the header blocks and gzip container by hand, copying every field from a real Unity-exported entry,
and reuse the original tar bytes up to their trailing zero blocks so existing members are never
re-encoded. Prefer `AssetDatabase.ExportPackage` from an editor script whenever the file list can be
expressed there; hand-editing is for adding an asset to a package you can no longer re-export.
