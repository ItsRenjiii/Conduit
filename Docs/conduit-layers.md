# Tags and layers, layer masks and the collision matrix

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before defining a tag or layer, assigning one, writing a layer mask, or
> changing the collision matrix.**
>
> **This file owns the TABLES and the MASKS - what a tag or layer is, and how to reference one safely -
> and nothing else goes in it.** The SIZE and PLACEMENT of a collider or trigger belongs to
> `conduit-scene-geometry.md` even when the symptom looked like a layer fault; what the SDK's own layer
> setup button rewrites belongs to `conduit-build-validation.md`; and a fact about a VRC_ component that
> merely happens to carry a mask belongs to that component's file, with only the mask rule left here.

## Defining a tag or a layer

- **`editor-tag-add`, `editor-tag-remove`, `editor-tag-rename`, `editor-layer-set`, `editor-layer-remove`,
  and the two list verbs, define the tables themselves.** Assigning one to an object stays
  `gameobject-modify`. Both layer verbs take either an index or a layer's current name.
- **Rename a tag with `editor-tag-rename`, never by removing and re-adding.** A tag is held on an object as
  a table entry rather than as the literal string, so a renamed definition carries its objects with it: the
  object was measured reading back the new name immediately, with `CompareTag` against the old name
  returning false and against the new one true. Remove-then-add instead sets every one of those objects to
  Untagged and does not put them back.
- **Renaming a layer keeps every object where it is**, because an object stores the layer as an index and
  only the name changes under it. That is the opposite of the tag case and the reason the two verbs differ.
- **Overwriting an occupied layer slot is refused until the call is repeated with `| confirm`.** Renaming a
  slot changes what every object already on that index means, and nothing in Unity reports it, so the first
  call names the occupant and the object count and writes nothing. An empty slot writes straight through.
- **Removing a tag that objects carry is refused the same way**, listing the objects that would become
  Untagged. There are 32 layer slots always, so a layer is named or cleared, never added or deleted.
- **Two slots may not share a name.** That one is refused outright rather than confirmed: a name lookup
  would answer with one of them and silently ignore the other, so no confirmation makes it coherent.
- **Unity reports nothing about a tag edit, so the verbs read the table back.** Adding a duplicate,
  removing a tag that does not exist, and adding an empty name were each measured leaving the count
  unchanged without throwing. A layer write is verified against both the editor API and the bytes in
  `ProjectSettings/TagManager.asset`, because applying the property alone is not evidence it persisted.
- **Unity's seven builtin tags are not stored in the project at all**, which is why the asset's `tags`
  array reads as empty in a project that has added none, and why `editor-tag-rename` can reach a custom tag
  and refuses a builtin one. Untagged, Respawn, Finish, EditorOnly, MainCamera, Player and GameController
  are the seven.

## Which layers are yours

- **Layers 0-21 belong to VRChat and are restored at upload; 22-31 are yours.** Read from this project's
  `ProjectSettings/TagManager.asset`: 0 Default, 1 TransparentFX, 2 Ignore Raycast, 3 Item, 4 Water, 5 UI,
  8 Interactive, 9 Player, 10 PlayerLocal, 11 Environment, 12 UiMenu, 13 Pickup, 14 PickupNoEnvironment,
  15 StereoLeft, 16 StereoRight, 17 Walkthrough, 18 MirrorReflection, 19 InternalUI, 20 HardwareObjects.
  6, 7 and 21 are reserved and 22-31 are empty.
- **Never put an object on Player or PlayerLocal.** VRChat assigns those to player bodies itself.
  InternalUI and HardwareObjects are the client's menus, nameplates and controller models.
- **Renaming or repurposing a reserved layer does not survive upload**, so a world built against a renamed
  layer 3 or 21 behaves differently once published than it did in the editor.
- **And layers 3 and 21 are worse than renamed - objects on them are moved to Default at upload.** Layer
  3 is Item, which VRChat uses for things users place, and 21 is reserved. A world that parks geometry on
  either loses its layer entirely once published, which takes its collision behaviour with it and shows up
  as physics that worked in the editor and does not in the client.
- **VRChat has RENAMED runtime layers before** - 19 and 20 were previously `reserved2` and `reserved3` -
  which is the historical reason the number rule below exists rather than a naming convention.

## Masks

- **Reference user layers 22-31 by number, never by name - a name lookup that misses returns -1, and
  `1 << -1` silently becomes `1 << 31`.** C# masks a shift count to its low five bits, so a failed
  `LayerMask.NameToLayer` does not produce an empty mask or an error: it produces a mask pointing at
  layer 31. Every raycast and collision test filtered through it is then wrong, and nothing anywhere
  throws. Hold user layers in a `private const int` and build masks from that.
- **A name lookup is only safe for a VRChat-defined layer, and only after checking the result.**
  `LayerMask.NameToLayer("Player")` is fine once `>= 0` is confirmed; the same call for a layer you named
  yourself is the trap above.
- **User layer names are replaced with `user0`-`user9` in the live client, while the collision matrix
  survives.** Layer 22 becomes `user0` and so on, which is why a `NameToLayer` on your own layer works in
  the editor and in ClientSim and returns -1 in VRChat. **This contradicts VRChat's own documentation**,
  which claims neither name nor matrix is overridden; the behaviour was established by dumping the layer
  table at runtime, corroborated independently on the VRChat forums, and reported upstream against the
  creator docs. Re-check it if the docs or the client change - but design to the numeric rule regardless,
  because that rule is correct under both readings.
- **Avoid layer 31 specifically, and use 22-30.** Unity's own editor preview mechanics use 31, so it is
  the one user layer with a second occupant. It is also exactly where a failed name lookup sends a mask,
  which makes a genuine layer-31 object indistinguishable from the bug above.

## Mirrors are a layer mask, and an empty-looking mirror is usually that mask

- **`VRC_MirrorReflection.m_ReflectLayers` decides everything the mirror can show**, and a mirror that
  reflects nothing is almost always this rather than the mirror. It needs at minimum **Player (9),
  PlayerLocal (10), MirrorReflection (18) and Environment (11)** - drop PlayerLocal and you vanish from
  your own mirror while everyone else appears, which reads as a bug in the mirror.
- **An object missing from a mirror while visible in the world is the same fault seen per-object:** its
  LAYER is not in that mask. Check the object's layer before touching its renderer.
- **The mask is a `LayerMask` field, so the number rule above applies to it too** - build it from numbers,
  never from `LayerMask.NameToLayer` on a user layer.

## Two layer masks that live on the scene descriptor

- **`interactThruLayers` decides which layers an interact ray passes through**, so a control that cannot
  be used through a pane of glass, a railing or a UI backing panel is this mask rather than the control.
  It is an `Int32` on `VRCSceneDescriptor`, and the Inspector labels it "Interact Passthrough".
- **`layerCollisionArr` IS the collision matrix, stored on the descriptor** as a `Boolean[]` - which is
  why the matrix travels with the scene and why "Setup Layers for VRChat" is a scene-level action with
  project-level consequences. `conduit-build-validation.md` covers what pressing it moves.

## Collision

- **The collision matrix is project settings, not scene data**, so a layer that collides correctly in one
  project silently does not in another that imported the same prefab.
- **PickupNoEnvironment exists to pass through walls**, so a pickup falling through the floor is usually
  layer 14 where 13 was meant. Walkthrough still fires triggers while letting players pass.
- **Know what the DEFAULT matrix already does before changing it.** Player collides with Environment and
  with Pickup; PlayerLocal collides with Environment; Pickup collides with Environment. Player does NOT
  collide with Player - VRChat owns that and players pass through each other - nor with PlayerLocal, and
  Walkthrough does not collide with either player layer. Most "why does this not collide" questions are
  answered by that list rather than by a matrix edit.
- **MirrorReflection (18) is a visibility layer, not a collision one - an object on it is visible only
  inside mirrors.** That is what makes it useful for a reflection-only prop, and what makes it a trap: an
  object parked there for tidiness disappears from the world itself while still showing in every mirror,
  which reads as a rendering bug rather than a layer choice.
- **A trigger volume that never fires is a geometry fault before it is a layer fault.** Measure the box
  first; see `conduit-scene-geometry.md`, which owns sizing and placement.
