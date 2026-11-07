# VRChat Dynamics in worlds - Contacts, PhysBones and VRC Constraints

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before adding a Contact, a PhysBone or a VRC Constraint, or before concluding
> one of them cannot do something.**
>
> **This file owns the three Dynamics families and nothing else goes in it.** A plain Unity collider or
> trigger volume is `conduit-scene-geometry.md`, which owns sizing and placement. **A Dynamics COLLISION
> TAG is not a Unity tag** - Unity's tags and layers are `conduit-layers.md`, and the shared word is the
> whole reason that needs saying. What a Build Panel severity means is `conduit-build-validation.md`,
> even though warnings from these components are quoted here.

VRChat Dynamics became available to WORLDS in SDK 3.10.0. Everything below was read from this project's
Udon node registry at SDK 3.10.5, and the constraint and contact surfaces re-read at 3.10.6-beta.2.
Re-derive it there after an SDK update rather than trusting this list or a remembered one.

## The rule that spans the whole family

- **A runtime field write does not take effect until `ApplyConfigurationChanges()` is called.** Setting
  `radius`, `height`, `size`, `position`, `rotation` or `shapeType` changes the component and NOT the live
  volume, so the object reads back exactly what you wrote while behaving as though you wrote nothing -
  which is indistinguishable from the write having failed. Batch every field, then call it ONCE.
- **It is on TEN types, not just contacts:** all six VRC Constraints, `VRCContactSender`,
  `VRCContactReceiver`, `VRCPhysBone` and `VRCPhysBoneCollider`. If you are writing Dynamics fields at
  runtime, this rule applies.
- **Two separate 1024-shape ceilings apply to the world: one for PhysBones plus PhysBone colliders, and
  one for contacts.** Both read `VRC.Dynamics.CollisionScene.MAX_SHAPES_WORLD`, which is literally 1024,
  and the Build Panel warns per ceiling that only that many can be ACTIVE in the client at one time.
  Avatars draw on a separate and far larger pool - `MAX_SHAPES_NONWORLD` is 8192 - so a crowded instance
  does not eat the world's share. (`VRCSdkControlPanelWorldBuilder.cs:599-612`, SDK 3.10.5)
- **Both are WARNINGS, not build blocks, and the SDK does not say what happens to shape 1025** - so treat
  1024 as a design budget to stay under rather than as behaviour you can reason about.

## Contacts

- **Senders emit, Receivers detect, and the UdonBehaviour must sit on the SAME GameObject as the
  Receiver.** Events do not bubble to a parent.
- **There is no contact stay event - only `OnContactEnter` and `OnContactExit` exist.** The registry has
  exactly `Event_OnContactEnter` and `Event_OnContactExit`. Continuous held-contact behaviour is therefore
  yours to drive, and the supported way is `CalculateProximity`, not an `Update` that re-tests geometry.
- **`VRCContactReceiver.CalculateProximity(sender)` returns 0.0-1.0 IMMEDIATELY**, with two overloads -
  one taking a `ContactSenderProxy` (the thing an event hands you) and one taking a scene
  `VRCContactSender`. That is the whole answer to "how close is it right now" without avatar parameters
  and without waiting for an event. On a Box receiver with Use Face Proximity it measures toward the
  positive-Z face; otherwise toward the receiver centre.
- **A contact accepts at most 16 collision tags - `VRC.Dynamics.ContactBase.MAX_COLLISION_TAGS` is
  literally 16.** Read it yourself with `type-find VRC.Dynamics.ContactBase | constants`; the same type
  carries `MAX_SIZE = 6`, which is where the 6 m shape ceiling comes from. Extra tags are not an error,
  they simply never match, so a receiver that stopped responding after someone added a tag is the tell.
  `UpdateCollisionTags(string[])` and `UpdateContentTypes(DynamicsUsageFlags)` change both at runtime on
  either component. (SDK 3.10.5)
- **Check `isValid` on BOTH proxies before reading anything off them.** `info.contactSender` and
  `info.contactReceiver` are proxies, not components, and `sender.usage` separates
  `DynamicsUsage.Avatar` (a real player touched it, and `sender.player` is then meaningful) from
  `DynamicsUsage.World` (a world sender, no player).
- **The two payloads are NOT the same: `ContactEnterInfo` carries `enterVelocity` and `contactPoint`,
  `ContactExitInfo` does not.** Anything that needs impact strength or position must capture it on enter;
  it is gone by exit.
- **Shape ceilings, after GameObject scale is applied:** `MAX_SIZE` is 6, which is read as capsule height
  and box axes maxing at 6 m with sphere and capsule RADIUS at 3 m - consistent
  with one diameter cap, though only the single constant is verified here.
- **Advanced Proximity Parameters (SDK 3.10.6) give Udon NOTHING new.** The receiver's
  `advancedProximityParameters` writes sender type, local position, velocity and proximity speed into
  ANIMATOR parameters (`param_Sender*` fields of type `IAnimParameterAccess`); the receiver's Udon surface
  gained no member for any of them. A world reads impact data from `ContactEnterInfo` as above.
  (SDK 3.10.6-beta.2 registry)
- **Material Contacts (SDK 3.10.6) are the shader-side route instead:** `writeToMaterials` with up to four
  `shaderTargetRenderers` writes the nearest eight senders' world transforms and data into those
  renderers' material properties, read in the shader through the SDK's `VRCContacts.cginc`. It is authored
  on the receiver, not scripted - neither field is in the Udon registry. (SDK 3.10.6-beta.2)
- **Receivers with no volume, very thin box receivers and very small sphere senders detect more reliably
  from SDK 3.10.6.** A contact that flickered on those shapes in older SDKs is worth re-testing before
  rebuilding it. (VRChat SDK release notes, 3.10.6-beta.1)

## PhysBones

- **Four events, all real: `OnPhysBoneGrabbed`, `OnPhysBoneReleased`, `OnPhysBonePosed`,
  `OnPhysBoneUnPosed`.** Posed and UnPosed are the separate "a player parked it in a position" pair, not
  synonyms for grab and release.
- **State and control: `IsGrabbed`, `IsPosed`, `ReleaseGrabs()`, `ReleasePoses()`** - the two release
  calls act on the LOCAL client, so a world that must force-release for everyone sends an event and lets
  each client release its own.
- **A PhysBone reports its own deflection - `Angle`, `Squish` AND `Stretch` are readable floats.** They
  sit beside `IsGrabbed` and `IsPosed` as read-only state, so a lever, dial or pull-cord reads the value
  the physics already produced instead of unwrapping `localEulerAngles` by hand - which is cheaper and
  stays correct under grab movement that a transform reading has to correct for.
  (SDK 3.10.5 registry)
- **The whole tuning surface is reachable, and EVERY scalar has a matching CURVE.** `pull`, `spring`,
  `stiffness`, `gravity`, `gravityFalloff`, `immobile`, `radius`, `maxAngleX` / `maxAngleZ`, `maxSquish`,
  `maxStretch`, `stretchMotion`, `grabMovement` and `limitRotation`, plus `limitType`, `immobileType` and
  `snapToHand` - and beside each one a `pullCurve`, `springCurve`, `radiusCurve`, `gravityCurve`,
  `limitRotationX/Y/ZCurve` and so on, each an `AnimationCurve` shaping that value ALONG the chain. Every
  runtime write still needs `ApplyConfigurationChanges()`. (SDK 3.10.5 registry)
- **But `allowCollision`, `allowGrabbing` AND `allowPosing` are not bools - they are
  `VRCPhysBoneBase.AdvancedBool`, whose members are `False = 0`, `True = 1` AND `Other = 2`.** Assigning
  a plain `true` does not compile, and the third member is the point: `Other` is a real third state, so a
  test written as "if it is not True it must be False" is wrong. The names read like bools, which is
  exactly why this earns a line. (SDK 3.10.5)
- **There are no PhysBone-collider enter/exit callbacks.** A PhysBone collider is a physics influence,
  not a trigger, so a world that needs "something touched this" uses CONTACTS. Expecting collider events
  here is the commonest wrong turn on this component.
- **A PhysBone whose root transform has no parent raises a Build Panel warning** - yellow, non-blocking,
  with a Select button and no fix. The test is `GetRootTransform().parent == null` over every
  `VRCPhysBoneBase` in the scene, so it is the ROOT transform that needs the parent and not the component's
  own object. Parent it under something appropriate rather than dismissing the alert.
  (`VRCSdkControlPanelWorldBuilder.cs:579-597`, SDK 3.10.5)
- **PhysBone components have been dependency-sorted automatically since SDK 3.8.0**, so parent-child
  chains evaluate in the right order and the old manual-ordering workarounds are obsolete.
- **But a cycle defeats that sort, and the only place it is reported is the client log.** When bones and
  colliders reference each other in a loop the client cannot order them and says so:
  `Cyclic dependency found for component VRCPhysBone on '<bone>'. As a result some components may run out
  of order, usually meaning a collider being run one frame behind.` **A collider running a frame behind is
  a real, visible artefact** - intermittent pass-through and jitter that looks like a tuning problem and is
  not - so do not read "sorting is automatic" as "ordering can never be wrong". The fix is to break the
  loop, and the warning names the component to start from. Nothing surfaces in the Unity console
  (`conduit-testing.md` has the log path). (Client log)
- **Global Collision is Sphere and Capsule only, and Udon cannot toggle it.** Udon reads and writes a
  world collider's `shapeType`, `radius`, `height`, `position` and `rotation`; whether a collider is
  global is authored, not scripted.
- **PhysBones on an object from `Instantiate()` may not sync** - which is the general instantiation rule
  in `conduit-networking.md` showing up again. Pre-place them or pool them.

## VRC Constraints

- **Six types, all in `VRC.SDK3.Dynamics.Constraint.Components`:** `VRCPositionConstraint`,
  `VRCRotationConstraint`, `VRCScaleConstraint`, `VRCParentConstraint`, `VRCAimConstraint`,
  `VRCLookAtConstraint`. Prefer them over Unity's built-ins for new work - Unity's are permitted in worlds
  but expose far less to Udon.
- **Edit sources through the helper methods (SDK 3.10.6+), never through the `Sources` list.**
  `AddSource(VRCConstraintSource)`, `RemoveSource(int)`, `RemoveSource(VRCConstraintSource)` (returns
  bool), `GetSource(int)`, `SetSource(int, VRCConstraintSource)`, `GetSources(VRCConstraintSource[])`
  (fills the array, returns the count), `SetSources(VRCConstraintSource[])`, `ClearSources()` and a
  read-only `SourceCount` are all in the registry. VRChat reports that changing `Sources` directly has
  reliability issues; it stays whitelisted only for worlds that already use it. Below 3.10.6 none of these
  exist and `Sources` is the only route. (SDK 3.10.6-beta.2 registry, VRChat SDK release notes)
- **`SetSourceWeight` and `AffectsPositionX/Y/Z`, `AffectsRotationX/Y/Z`, `AffectsScaleX/Y/Z` are still
  NOT reachable from Udon.** The axis flags are fields on the C# type, so `type-find` lists them, but the
  registry has no node for them; per-source weight is `GetSource` -> change `Weight` -> `SetSource`.
  (SDK 3.10.6-beta.2)
- **The rest of the surface**, get and set unless noted: `IsActive`, `GlobalWeight`, `Locked`,
  `FreezeToWorld`, `RebakeOffsetsWhenUnfrozen`, `SolveInLocalSpace`, `SmoothingFactor` (3.10.6+),
  `Sources`, `TargetTransform` (read only), and the type's own at-rest/offset pair - `PositionAtRest` /
  `PositionOffset` on the position constraint, `RotationAtRest` / `RotationOffset` on the rotation and aim
  constraints, and so on. Methods are `ActivateConstraint()`, `ZeroConstraint()` and
  `ApplyConfigurationChanges()`; `TryBakeCurrentOffsets` and `GetEffectiveTargetTransform` exist on the
  type and are not exposed.
- **`SmoothingFactor` above zero makes the constraint ease toward its target over time instead of
  snapping**, which is the built-in answer to a jittery follow before writing a Lerp in `Update`.
  (SDK 3.10.6)
- **In an SDK project, a VRC constraint now follows a `TargetTransform` change made from C# during play
  mode** (SDK 3.10.6); older SDKs ignored it until re-entering play mode.
- **The aim constraint's extras are named `AimAxis`, `UpAxis`, `WorldUpVector`, `WorldUpTransform` and
  `WorldUp`** (of type `VRCConstraintBase.WorldUpType`). Unity's spellings - `AimVector`, `UpVector`,
  `WorldUpType` - are all three compile-time failures here.
- **`Sources` is a `VRCConstraintSourceKeyableList`, and unlike `List<T>` it compiles and runs from
  Udon** - indexer both ways, `Count`, `Add`, `Insert`, `Remove`, `RemoveAt`, `Clear`, `Contains`,
  `IndexOf`, `CopyTo`, `SetLength`, `MaxFlatLength`, two constructors and an
  `IEnumerator<VRCConstraintSource>`. Use it only for code that must ship to SDKs below 3.10.6; the helper
  methods above replace it.
- **`VRCConstraintSource` is a STRUCT with PascalCase PROPERTIES, not lowercase fields:**
  `SourceTransform`, `Weight`, `ParentPositionOffset`, `ParentRotationOffset`, all get and set, plus two
  constructors - `(Transform, float)` and `(Transform, float, Vector3, Vector3)`. Because it is a struct,
  read it out with `GetSource`, modify the copy, and write the whole thing back with `SetSource`;
  mutating a returned copy in place changes nothing.
- **The counterexample worth remembering:** "it is a collection type, therefore Udon cannot use it" is
  wrong as a general rule. Check the type in the registry - `List<T>` is the exception rather than the
  pattern (`conduit-udonsharp.md` covers that one).
