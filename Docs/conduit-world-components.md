# VRChat world components: what they are and how they are authored

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before adding a VRC_ component to a scene, and before telling anyone a Unity
> component will work in a world.**

This file owns the AUTHORING side: which components exist, what their inspector fields mean, and what a
component needs beside it to work at all. The Udon side lives elsewhere and is not repeated here -
`conduit-udonsharp.md` owns the scripting surfaces of `VRC_Pickup`, `VRCObjectSync`,
`VRCCameraSettings`, `VRCBillboard`, `VRCVelocityTracker` and `VRCPlacementZone`, `conduit-player.md` owns stations and `VRCPlayerApi`, `conduit-dynamics.md` owns
Contacts and PhysBones, `conduit-audio.md` owns `VRC_SpatialAudioSource`, `conduit-layers.md` owns the
mirror's reflect mask, and `conduit-vrchat-ui.md` owns `VRCUiShape` and canvases.

## Only whitelisted components run, and a custom MonoBehaviour is not one of them

- **A component that is not on VRChat's whitelist does not run in an uploaded world.** It is not an
  error and not a warning at runtime - it simply does nothing. The authoritative list is VRChat's
  whitelisted-world-components page; the commonly used subset is physics bodies and colliders, joints,
  renderers and mesh filters, Camera, Light, the probe family, LineRenderer and TrailRenderer, particles,
  LODGroup, occlusion areas, AudioSource and the audio filters, Canvas and RectTransform, Animator,
  PlayableDirector, the NavMesh family, VideoPlayer, Terrain, Cloth, WindZone and the Tilemap family.
- **Your own MonoBehaviours never run in a world either** - Udon is the only scripting that ships. Keep
  editor-side helpers out of the upload with the `EditorOnly` tag or `IEditorOnly`, which
  `conduit-udonsharp.md` covers.
- **Cloth is completely disabled on Android**, so a cloth-driven effect that works on PC is simply
  absent on Quest rather than cheaper. Design the Android look without it.
- **Three components third-party references name do not exist: `VRCWorldSettings`,
  `AvatarScalingSettings` AND `VRCDroneInteractable`.** None of the three resolves in any of the 285
  loaded assemblies. The first two are offered as the VRCWorld prefab's movement-speed and avatar-scale
  settings, and the third as a drone-vehicle component. **All three capabilities are real and none of them
  is a component** - movement speed and eye-height limits are `VRCPlayerApi` calls, and drones are the
  `VRCDroneApi` surface, all in `conduit-player.md`. Looking for a component is the wasted search this
  bullet exists to stop, and it is the recurring shape of these references' errors. (SDK 3.10.5)

## PlayableDirector, and the one clip that deletes your whole Timeline

- **A PlayableDirector is destroyed outright if its Timeline has a Control Track clip pointing at a
  prefab.** VRChat walks every director during validation, and the first `ControlPlayableAsset` it finds
  with a non-null `prefabGameObject` makes it `Destroy` the WHOLE director and log `PlayableDirector
  containing prefab removed`. There is no Build Panel entry for it, so a Timeline that plays perfectly in
  the editor can arrive in the uploaded world with no director at all - and ONE Control clip takes the
  entire sequence with it. Drive objects that ALREADY EXIST in the scene from a Control track; never
  instantiate a prefab from one. (`WorldValidation.cs:723-748`, SDK 3.10.5)

## The scene descriptor's capacity fields are not one number

- **Maximum Capacity is a hard limit and Recommended Capacity is a visibility one.** Nobody joins past
  Maximum. Past RECOMMENDED the instance stops appearing in public listings while direct joins still
  work - so a world that "stopped showing up when it got busy" is behaving exactly as authored.
- **Set both deliberately.** Recommended is the count the world is pleasant and performant at; Maximum is
  the count it can survive. Older SDKs had a bug where leaving Recommended unset made the effective
  maximum twice the stated value, so an inherited scene is worth re-checking rather than assuming.
- `conduit-assets-prefabs.md` owns the descriptor's other fields, their serialized names, and the respawn
  height trap.

## VRC_Pickup

- **It needs a Collider AND a Rigidbody on the same object, or it cannot be grabbed at all.** This is the
  first thing to check on a pickup that ignores the player, ahead of anything in script - and the second
  is `Pickupable`, which is a plain bool on the component and switches grabbing off entirely while every
  other setting still looks correct.
- **`Pickup Orientation` decides how it sits in the hand: `Any` uses the grab point, `Grip` and `Gun` snap
  to an authored transform.** Choosing Grip or Gun without assigning `Exact Grip` / `Exact Gun` is the
  usual cause of an object that holds at a strange angle - the mode is selected and the anchor is null.
- **`Proximity` is the grab distance in metres** and defaults to about 2; `Throw Velocity Boost Min Speed`
  and `Scale` shape how hard a thrown object leaves the hand. These are the authored feel of a pickup and
  are worth setting deliberately rather than left at defaults on anything a player throws.
- **A pickup that falls through the floor wants Rigidbody Collision Detection set to Continuous**, not a
  bigger collider. A fast small body tunnels through thin geometry under Discrete detection.
- `conduit-udonsharp.md` owns `AutoHold`, `OutlineRenderers` and the theft rule.

## VRC_Station

- **It needs a Collider for `Interact` to fire**, on the same object or a child.
- **`Station Enter Player Location` and `Station Exit Player Location` decide exactly where the player is
  placed**, and a player who clips into the seat or cannot stand up is almost always one of those two
  transforms sitting inside geometry rather than an avatar problem.
- **`Player Mobility` is the whole character of the seat:** `Immobilize` for a chair, `Mobile` to let the
  player walk while stationed, `ImmobilizeForVehicle` when the station itself moves and the view should
  follow it.
- **`Can Use Station From Station` decides whether a seated player can move straight into another seat**,
  and `Controls Object` is the transform a vehicle station drives.
- **`Disable Station Exit` traps the player in the seat, and it is a real feature, not a bug.** A player
  who cannot stand up is this flag before it is anything else. It is correct for a ride or a cutscene -
  but the world then OWES them an exit, driven from Udon, or they are stuck until they leave the
  instance.
- **Stations on avatars carry extra limits** - at most six, the station descriptor must be enabled at
  upload, and entry/exit must sit within about 2 m - which matters when a world is built around visitors'
  avatar stations rather than its own.
- `conduit-player.md` owns the seated-player trigger trap and `OnStationExited` not being guaranteed.

## VRC_MirrorReflection

- **A mirror renders the whole scene again, and in VR it does it per eye.** That is roughly four times
  the rendering of the same view, before counting a second mirror. It is the single most expensive thing
  most worlds contain.
- **Ship mirrors OFF and let players turn them on.** A toggle, and ideally an auto-disable once the player
  walks away, is the accepted pattern precisely because the cost is paid every frame the mirror is live
  whether anyone is looking at it or not.
- `conduit-layers.md` owns the reflect mask, which is the other reason a mirror misbehaves.

## VRC_PortalMarker

- **A portal must sit at the root of the scene hierarchy.** Parented under anything else it does not
  work correctly, and the failure is not an error at author time.
- It takes a world id (`wrld_...`) from the VRChat website, and optionally a custom prefab for the visual.

## VRC_AvatarPedestal

- **It displays one avatar by id (`avtr_...`) and lets a player switch to it.** The display model is
  optional; the id is what does the work.

## VRC_CameraDolly (SDK 3.9+)

- **It animates the LOCAL player's VRChat camera along an authored path** - intros, tours, guided shots.
  The hierarchy is an animation object holding path objects holding point objects.
- **Press "Collect Paths & Points" on the animation object before playing or building, and again after
  every change to the paths.** The animation runs off the collected set, so an edited path that was never
  re-collected plays the old one and reports nothing.
- **`VRCCameraDollyAnimation.Import()` is the only documented scripting entry point**, and it applies to
  the calling client only. Driving it for everybody means sending a network event and letting each client
  call it (`conduit-networking.md`).
- **ClientSim does not preview it** - it needs Build & Test to be seen at all (`conduit-testing.md`).

## VRCPlacementZone (SDK 3.10.6+)

- **It controls where players may place portals, stickers, sharing pedestals and world-attached items**,
  inside a box set by `Center`, `Size` and `Rotation`. Each of the four content types takes its own
  setting: `Unaffected`, `BlockHere`, or `OnlyAllowHere`.
- **Use `BlockHere` for keep-clear areas such as a spawn, a stage or a road; `OnlyAllowHere` is the
  release notes' "only allow content of that type within a zone"** - a restriction on that type, not an
  extra area, so test it in the client before relying on what it does outside the zone.
- **New features need client 2026.4.1 or higher**, so an older client ignores the zone.
  (VRChat SDK release notes, 3.10.6-beta.1)

## VRCBillboard (SDK 3.10.6+)

- **It turns a transform to face the player's camera on desktop, or their head in VR, without an Udon
  `Update`.** `Facing` is the axis that points at the player: `Back` (-Z) for UI, text and sprites,
  `Forward` (+Z) for models with a built-in front.
- **`Allow Tilt` off keeps it upright and turning only left and right; `Smoothing Factor` is a time in
  seconds** - 0 snaps, and the object covers about 63% of the remaining angle every Smoothing Factor
  seconds.
- **Set `Up From` on anything inside a vehicle or station**, so the content stays upright relative to
  the moving object rather than the world. `Match Viewpoint Rotation` copies the view rotation instead of
  aiming, keeping it flat to the screen. (`VRCBillboardEditor` tooltips, SDK 3.10.6-beta.2)

## VRCVelocityTracker (SDK 3.10.6+)

- **Add it to any object whose velocity a behaviour reads**, rather than differencing positions in an
  `Update`. It has no authored settings to get wrong; the read calls are in `conduit-udonsharp.md`.

## Avatar features read YOUR colliders

- **Avatar-side features cast rays and queries into world geometry, so your collider and layer setup is
  a contract with other people's avatars.** `VRCRaycast` (SDK 3.10.3+) is the clearest case: avatars emit
  rays that hit world colliders, and whether a visitor's avatar behaves as its author intended depends on
  whether your geometry has a collider, what layer it is on, and whether it is a trigger.
- **So "nothing in my world scripts against this" is not a reason to leave colliders arbitrary.** Solid
  geometry wants a real collider on a sensible layer whether or not your own Udon ever queries it.
