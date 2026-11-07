# Measuring colliders, trigger volumes and bounds

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before sizing or placing a collider, a trigger volume, or anything else that
> has to line up with model geometry.**
>
> **This file owns MEASUREMENT and PLACEMENT - where a volume sits and how big it is - and nothing else
> goes in it.** Which LAYER a collider is on, and the collision matrix that decides what it meets, are
> `conduit-layers.md`. A VRChat Dynamics contact or PhysBone collider is `conduit-dynamics.md`, not a
> plain Unity collider. RectTransform layout under a canvas is `conduit-vrchat-ui.md`, even though both
> are rectangles with sizes. A fact about the PLAYER that happens to explain a volume - a seated capsule,
> an avatar-side raycast - belongs to `conduit-player.md`.

## Measure in world space, never by eye

- **Never size a collider by eye, and never copy the mesh's local bounds straight in.** Read the
  mesh's `localAABB`, then put it through the transform chain to get WORLD numbers. A box authored from
  local values looks entirely plausible afterwards - it is still a box, still roughly the right scale -
  so nothing about the inspector tells you it is covering the wrong volume.
- **An imported model usually carries a root rotation, and it swaps axes.** FBX from most DCC tools
  arrives with a 270 degree (-90) X rotation, which maps mesh-local `(x, y, z)` to world `(x, z, -y)`:
  local HEIGHT becomes world DEPTH and back. Copy local Y and Z into a world-axis-aligned collider and
  the box is silently transposed - too short and too deep, or too tall and too shallow.
- **Get the numbers from the mesh, not from the scene view.** `gameobject-component-get` the MeshFilter
  for `m_Mesh`, then `assets-get-data` on that mesh for `localAABB`, which carries `m_Center` and
  `m_Extent`. World size is the extent doubled with the root rotation applied; world centre is the
  object's position plus the rotated local centre.
- **A collider whose object cancels the model's rotation takes WORLD numbers directly.** Where a volume
  sits at identity rotation under a rotated model root, its size field is already world-aligned, which
  is exactly the case that invites pasting local values in.

## Placing an object under a rotated root

- **`set-position` / `set-rotation` write local values, while every component read prints WORLD.**
  `gameobject-component-get`/`-add` report a `position` and `rotationEuler` in the header block that are
  the object's WORLD pose, so feeding those numbers straight back into `set-position` under a rotated
  parent puts the object somewhere else entirely. Under the usual 270 degree X root this lands the object
  at world `(x, -z, y)` of where it was wanted - plausible-looking, wrong, and silent. Convert first:
  world `(X, Y, Z)` is local `(X, -Z, Y)` under that root, and local `(x, y, z)` is world `(x, z, -y)`.
- **Confirm a placement by reading the Transform component, not the write's own echo.** `set-position`
  echoes back the local value it was handed, which cannot disagree with itself. `gameobject-component-get
  <target> | Transform` prints `m_LocalPosition` in `serialized` AND the world pose in the header, so one
  read shows both spaces and settles which one you actually changed.
- **A world-identity child of a rotated root carries the parent's rotation cancelled out.** Handles and
  volumes authored this way sit at local `(89.98, 0, 0)` under a `(270.02, 0, 0)` root, so their own
  forward/right/up ARE the world axes - which is what makes them safe to compute offsets against.

## Diagnosing a volume that does nothing

- **A trigger that never fires is a geometry fault before it is a code fault.** Confirm the box
  actually contains the place things are being put before reading the script. References, `isTrigger`
  and layers are cheap to check and, once they read correct, are rarely the cause.
- **Log the arrival BEFORE the filtering, not after.** An `OnTriggerEnter` that only logs once its
  filter passes cannot tell "the event never arrived" from "the event arrived and was rejected", which
  are opposite diagnoses. One line at the top of the handler separates them.
- **Mutual triggering is the tell that two volumes intersect.** Each volume logging the other's collider
  entering is a direct measurement of overlap, and it costs nothing to read.
- **A seated player is invisible to player triggers entirely - measure the box all you like, it will
  never fire for them.** VRChat disables the seated player's `PlayerLocal` capsule, so this is the one
  case where the geometry is correct and the event still never arrives. `conduit-player.md` owns the
  detail and the position-check answer; check it before re-measuring a volume that only fails for people
  sitting down.

## Diagnosing a joint limit that does nothing

- **A hinge limit only stops a body travelling into the forbidden arc - it does not eject one that is
  already inside.** Tightening `HingeJoint.limits` at runtime around a body that currently sits in the
  newly illegal range leaves it sitting there, so a mechanism that gates a door by shrinking its limit
  works perfectly when the door is outside the arc and does nothing at all when it is inside. Write the
  limit AND teleport the body to the boundary yourself, zeroing `velocity` and `angularVelocity`.
- **A kinematic Rigidbody ignores joint limits outright**, so unlatch before writing the gate, not after.
  A door held shut by `isKinematic = true` will read a correct limit off the joint while behaving as if
  there were none, which looks exactly like the limit not being written.
- **`localEulerAngles` reports 0 to 360, joint limits run signed.** Fold the angle back (`> 180` means
  `- 360`) before comparing, or a right-hinged door at -20 degrees reads as 340 and every test inverts.
- **Read the LIVE `m_Limits` in play mode before blaming the write.** `gameobject-component-get <pivot> |
  HingeJoint` prints the real min/max, so one read separates "the gate never wrote" from "the gate wrote
  and physics ignored it" - which are opposite fixes.

## Distance from the origin has a cost

- **Float precision degrades with distance, and the symptom is jitter rather than an error.** Past
  roughly 5,000 units from the world origin, position jitter and physics drift become observable; past
  about 100,000 a float can no longer hold sub-metre precision at all. So a design that parks content far
  out - separated rooms, a skybox rig, a hidden staging area - keeps those offsets within a few thousand
  units, and prefers rotating around a central pivot over pushing further out when it needs many of them.

## Overlap is often correct

- **Do not shrink a volume to remove an overlap until you know the overlap is wrong.** Volumes that
  represent real spaces frequently share one: a door-mounted bin occupies the cabinet interior while the
  door is shut. Dedupe by object wherever a count must not double, and let the geometry stand.
- **Decide which volume OWNS an object when volumes overlap by design.** Only one should reparent it;
  the others observe. A volume that carries nothing still reports what sits inside it.

## Verify the write

- **Read the size back and compare old to new.** `gameobject-component-modify` reports `old -> new` per
  field, and an "already at value" line means the write changed nothing - which is the answer, not a
  formality, when a measurement was supposed to change.
