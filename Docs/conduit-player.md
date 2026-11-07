# VRCPlayerApi - reading and driving a player

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before reading a player property, changing movement or voice, or writing
> anything that treats players differently from one another.**

This is the Udon API surface, and nothing else goes in it. `conduit-vrc-input.md` covers driving the
player through Conduit's own verbs in play mode; `conduit-networking.md` owns ownership and sync;
`conduit-audio.md` owns the voice and avatar-audio setters. AUTHORING the station or pickup a player
interacts with is `conduit-world-components.md` - only the API side is here - and the SIZE of a volume a
player fails to trigger is `conduit-scene-geometry.md`, even where the reason is a player fact recorded
below.

## Identity flags, and which of them you may trust

- **Validate before every use: `player != null && player.IsValid()`.** A `VRCPlayerApi` handed to an
  event can be stale by the time a delayed callback runs, and a player who left mid-frame is the normal
  case, not the edge case. `Utilities.IsValid(x)` is the equivalent for components and objects.
- **`isVRCPlus` is evaluated per-client, so it must never be synced.** Each client reads it locally
  against the `VRCPlayerApi` it holds; putting one client's answer in an `[UdonSynced]` field broadcasts
  that player's value as everyone's. That is a correctness bug wearing the costume of a bandwidth saving.
  `VRCSDKBaseVRCPlayerApi.__get_isVRCPlus__SystemBoolean` is in the registry from SDK 3.10.3.
  (SDK 3.10.5)
- **And it is not settled in `OnPlayerJoined`.** That event fires during the network handshake, before
  account and persistence data land, so a read there can return a value that is simply not yet true. Gate
  account-tied reads behind `OnPlayerRestored`, which fires after persistence has loaded, or behind a flag
  you set there. The same caution applies to anything else sourced from the player's account rather than
  from the instance.
- **`isMaster` is a session role, NOT an authorization boundary** - see `conduit-networking.md`, which
  owns why, and what to gate on instead.
- The rest read as they say: `displayName`, `playerId`, `isLocal`, `isInstanceOwner`, `isUserInVR`,
  `isSuspended` (tabbed out or headset off), `isGrounded`.

## Per-player events beyond join and leave

- **Five more player events exist and no inherited table has them all:** `OnPlayerRespawn`,
  `OnPlayerSuspendChanged` - the event form of the `isSuspended` flag above, firing when someone tabs out
  or lifts the headset - `OnAvatarChanged`, `OnAvatarEyeHeightChanged(player, previousHeightMeters)` and
  `OnMasterTransferred`. (SDK 3.10.5 registry)
- **`OnVRCPlusMassGift(VRCPlayerApi gifter, int numGifts)` fires when somebody gift-bombs the instance**,
  which is the one player event a world can turn into a thank-you: name the gifter, count the gifts, run a
  celebration. (`UdonSharpBehaviour.cs:471`, Udon event `_onVRCPlusMassGift`, SDK 3.10.5)
- **And it is testable - ClientSim fires it from a button, which is dead while you are the only
  player.** The settings window draws a row per player in `AllPlayers` with a "Simulate VRC+ Gift" button
  that sends the event with `numGifts` hardcoded to 10, naming THAT row's player as the gifter. Every row
  is disabled while `AllPlayers.Count == 1`, so spawn a remote player first or the control greys out and
  reads as broken rather than as unmet. (`ClientSimSettingsWindow.cs:449-453`, `ClientSimPlayer.cs:484`,
  SDK 3.10.5)
- **`isVRCPlus` is simulated too, so a supporter-only feature is testable without a subscription - but
  set it before pressing Play.** The settings window's "Is VRC+" toggle drives
  `VRCPlayerApi._isVRCPlusDelegate`, and it sits in the group ClientSim locks the moment it starts, beside
  local player name, start height, language, is-master and is-instance-owner. Mid-session the whole group
  greys out, which reads as broken rather than as too late.
  (`ClientSimSettingsWindow.cs:407-415`, `ClientSimMain.cs:665`, SDK 3.10.5)
- **A CharacterController has its own player-hit event, and it points the opposite way from
  `OnPlayerCollisionEnter`.** `OnControllerColliderHitPlayer(ControllerColliderPlayerHit hit)` fires when
  a CharacterController on YOUR object moves INTO a player, and carries `player`, `moveDirection`,
  `moveLength`, `normal` and `point` - everything a knockback needs, and none of which the collision
  event gives you, since that one hands over only the player. It fires on the object owning the
  controller, never on the thing hit; `OnControllerColliderHit` is the non-player twin and takes no
  `override`. (`VRC.SDK3.ControllerColliderPlayerHit`, SDK 3.10.5)

## Drones are a real, writable API

- **A world can drive a drone, not just watch one.** `VRCDroneApi` exposes `GetPlayer()`,
  `GetPosition()`, `GetRotation()`, `GetVelocity()` and `IsDeployed()` for reading, and **`SetVelocity`
  and two `TeleportTo(Vector3, Quaternion[, bool])` overloads for writing** - so a wind volume, a no-fly
  zone or a race gate can actually move somebody's drone rather than merely detect it. The `TryGet`
  variants of position, rotation and velocity exist beside the plain ones.
  (SDK 3.10.5 registry)
- **Reach one either way: `OnDroneTriggerEnter` / `Stay` / `Exit` hand you a `VRCDroneApi`** from a
  trigger collider on the behaviour's own GameObject, and `VRCPlayerApi.GetDrone()` goes from a player to
  their drone directly. The trigger events fire on the local client only, like the player ones.

## Movement

- **Every movement setter has a matching GETTER, so a zone can save and restore instead of guessing.**
  `GetRunSpeed`, `GetWalkSpeed`, `GetStrafeSpeed`, `GetJumpImpulse` and `GetGravityStrength` all exist
  beside their `Set` twins. **This is the same rule as the voice setters in `conduit-audio.md`, and it
  matters for the same reason:** a trigger volume that writes hardcoded "normal" values on exit undoes
  every other volume the player is still standing in, while one that captured the previous values on entry
  restores exactly what it found. (SDK 3.10.5)
- **Documented defaults are walk 2.0, run 4.0, strafe 2.0, gravity 1.0**, confirmed by play-mode read-back
  of the getters above. They are client-side rather than readable SDK constants, so capture the live values
  before a zone overwrites them rather than hardcoding this row. (Play mode, SDK 3.10.5)
- **But never read the jump default out of Play Mode - ClientSim always answers 0.**
  `ClientSimPlayerLocomotionData` seeds `walkSpeed`, `strafeSpeed`, `runSpeed` and `gravityStrength` with
  real values and then declares `public float jumpSpeed;` with NO initializer, so `GetJumpImpulse()` hands
  back C#'s zero in the editor whatever the client actually does. The four correct neighbours are what make
  it convincing - a reading of 0 here is the simulator, not the world, and the real value has to come from
  a client. (`ClientSimPlayerLocomotionData.cs:8-15`, SDK 3.10.5)
- **These calls are LOCAL: they change what the calling client's own player does.** They need no
  ownership and no syncing, and they should be driven from `OnPlayerTriggerEnter` / `Exit` guarded on
  `player.isLocal`. Calling them for a remote player does nothing useful.
- **`GetVelocity()` and `SetVelocity(Vector3)` both exist, and they are the launcher, the conveyor and the
  wind volume.** Setting a speed changes how fast the player WALKS; setting velocity throws them. It is a
  local call like the rest, so drive it from `OnPlayerTriggerEnter` guarded on `player.isLocal`.
  (SDK 3.10.5 registry)
- **`Immobilize(true)` stops walking without touching the speeds, and it does NOT stop a jump.** The
  speeds survive it, unlike zeroing them, so it suits a cutscene or a seat; a hold that must keep a player
  on the spot also sets `SetJumpImpulse(0)` and restores what `GetJumpImpulse` read. Immobilized in
  ClientSim, one jump press left the ground at 2.67 m/s. (`ClientSimPlayerController`, SDK 3.10.5)
- **`TeleportTo` moves the local player only - calling it on a remote player does nothing.** ClientSim
  says so outright ("Teleporting remote players will do nothing"), and there is no error in the client.
  To move somebody else you send them an event and let THEIR client teleport THEM. That constraint is
  also a technique: every client teleporting its own player to the same authored coordinate puts them in
  the same place without syncing a single transform.
- **`TeleportTo` takes an optional `SpawnOrientation`** (`Default`,
  `AlignPlayerWithSpawnPoint`, `AlignRoomWithSpawnPoint`); the room-aligned form is the one that matters
  in VR, where the play space orientation is not the avatar's.

## Raw input: `UnityEngine.Input` is partly exposed, and the gap matters

- **The Udon `Input*` EVENTS are edge-driven; `UnityEngine.Input` is how you POLL a continuous axis.**
  `InputMoveHorizontal` and friends hand you a value when VRChat decides to, which is the wrong shape for
  "how far is the thumbstick pushed right now" - a scroll view or a custom cursor wants the poll.
- **What is exposed**: `GetAxis(string)`, `GetAxisRaw(string)`, `GetButton` / `GetButtonDown` /
  `GetButtonUp`, `GetKey` / `GetKeyDown` / `GetKeyUp` (both `KeyCode` and `string` overloads),
  `GetMouseButton` / `Down` / `Up`, `GetJoystickNames()`, `anyKey`, `anyKeyDown`, `inputString` and
  `imeIsSelected`. (SDK 3.10.5)
- **What is not: `mousePosition`, `mouseScrollDelta`, `touches` and `GetTouch` are all ABSENT.** So a
  world can read which mouse BUTTON is held and never where the cursor is - screen-space pointing has to
  come from the head ray instead. The scroll wheel is reachable, but as `GetAxis("Mouse ScrollWheel")`
  rather than through `mouseScrollDelta`.
- **An axis NAME is a string contract with no compile check**, so a wrong one silently reads zero forever.
  The right-hand thumbstick is `Oculus_CrossPlatform_SecondaryThumbstickVertical` across most headsets in
  VRChat. Apply a dead zone - a resting stick does not read exactly zero.

## Tracking and rigs

- **Hand-mounted UI and objects must anchor to `VRCPlayerApi.GetTrackingData(LeftHand/RightHand)`**, NOT
  `GetBonePosition`/`GetBoneRotation(HumanBodyBones.LeftHand)`. Unity's Humanoid rig standardises *which*
  bone is the hand but not its local axis orientation, so raw hand-bone rotation varies per avatar rig - a
  fixed hand-local offset that sits right on one avatar lands sideways on another. The controller tracking
  frame is normalised identically across avatars, so one calibrated offset holds everywhere (the same
  reason head-locked UI off `GetTrackingData(Head)` is avatar-stable). Tune hand offsets from in-world
  sliders, not the editor inspector.
- **`GetBonePosition` returns `Vector3.zero` when the bone is unavailable, not null** - on desktop, on a
  non-humanoid avatar, or for a rig missing that bone. Zero is a legal position, so a fingertip check that
  does not test for it silently tracks the world origin. Test both bones against zero before using either.
- **There is NO fingertip bone**, so a finger-touch UI extrapolates one: `Vector3.LerpUnclamped` from
  `IndexIntermediate` to `IndexDistal` at roughly 1.8 lands about where the tip is. `LerpUnclamped` is the
  point - a clamped lerp can never reach past the distal bone.
- **`PlayHapticEventInHand` fires for the LOCAL player in VR only**, so it needs no ownership and is a
  no-op on desktop rather than an error - do not build a "did it buzz" check on it.
- **`TrackingDataType` is `Head`, `LeftHand`, `RightHand`, `Origin`** - `Origin` being the play space,
  which is what a room-scale calculation needs and what the head does not give you.
- **Avatar scale is readable and settable**: `GetAvatarEyeHeightAsMeters`, `SetAvatarEyeHeightByMeters`,
  `SetAvatarEyeHeightByMultiplier`, and the minimum/maximum pair with getters. `OnAvatarEyeHeightChanged`
  hands you the PREVIOUS height, so a behaviour that scales with the player can compute the delta without
  caching it itself.

## Player tags are LOCAL, and the bulk read is unusable

- **`SetPlayerTag` / `GetPlayerTag` / `ClearPlayerTags` are local-only and sync nothing.** A tag set on
  one client is invisible to every other client, so tags are a local bookkeeping device and never a way to
  share state. They look exactly like shared state at the call site, which is the trap.
  (SDK 3.10.5)
- **AND `GetPlayersWithTag` returns a `List<int>` Udon cannot read** - no indexer, no `Add`, no
  iteration, the signature-only shape covered in `conduit-udonsharp.md`. The bulk query therefore cannot
  be consumed at all; loop `VRCPlayerApi.GetPlayers` and test each player's tag instead.

## Stations, and the player who becomes invisible to triggers

- **A seated player's `PlayerLocal` capsule is effectively disabled, so `OnPlayerTriggerEnter` / `Stay`
  / `Exit` do not fire for them at all.** A zone that works perfectly for someone walking is simply dead
  for someone sitting, and worse, `OnPlayerTriggerExit` FIRES when a player sits down inside the zone -
  so the world is told they left a place they are still in. **Design as though this is PERMANENT, because
  it effectively is.** It sits on VRChat's own public tracker as four separate entries, the oldest years
  old and several marked tracked, one of them saying plainly that it has "been untouched for years" - and
  another asks for these events to be deprecated outright rather than fixed. It is also CLIENT behaviour,
  so no SDK version dates it and upgrading the SDK cannot resolve it. Never wait it out, and never work
  around it in geometry. (VRChat public issue tracker)
- **The fix is to stop asking the trigger and start asking the position.** For an immobilised station the
  seat does not move, so one `Bounds.Contains` check against the station's transform at
  `OnStationEntered` settles it with no polling. For a station that moves, poll the seated players'
  `GetPosition()` on an interval rather than per frame. Where standard trigger events are genuinely
  needed, park a small trigger collider on the player's position each frame as a proxy - **on any layer
  except `PlayerLocal`**, which is the one that is disabled.
- **`OnStationExited` IS NOT GUARANTEED WHEN A SEATED PLAYER LEAVES THE INSTANCE.** Any state keyed on
  a seated player therefore leaks unless `OnPlayerLeft` clears it too. Treat the pair as mandatory: the
  station event for the normal path, the leave event as the failsafe.
- **`Networking.LocalPlayer.UseAttachedStation()` seats the local player in the station on the SAME
  GameObject as the calling behaviour** - which is how a seat is entered from a button or a trigger
  rather than by walking into it. It is local like every other player call. (SDK 3.10.5)
- **A player clipping into the seat or the floor after sitting is the station's `Station Enter Player
  Location` transform, not the avatar.** That transform, and its `Station Exit Player Location` twin,
  decide exactly where the player is placed on sit and on stand - so both must sit in clear space. A
  player who cannot stand up is usually an exit location buried inside geometry, and `Disable Station
  Exit` left true is the other cause.
- **A station tells you neither *who* sits in it nor *whether* anyone does.** There is no `Occupant`, and
  `VRCStation.seated` is the authored Seated setting - a field beside `PlayerMobility` that read True on a
  seat nobody had used. Capture the player in `OnStationEntered` and clear it in `OnStationExited` AND
  `OnPlayerLeft`, since the exit event is not guaranteed (above). (SDK 3.10.5 registry, ClientSim)
- **Nearly everything ELSE on a station IS writable at runtime, which nothing documents:**
  `PlayerMobility`, `disableStationExit`, `canUseStationFromStation`, `animatorController` and both
  `stationEnterPlayerLocation` and `stationExitPlayerLocation` carry setters beside their getters. So a
  seat that traps a player can release them, a vehicle can change mobility mid-ride, the exit point can be
  moved to wherever is currently clear, and the seated pose can be swapped by assigning a different
  controller. (SDK 3.10.5 registry)
- **Disabling a station's COLLIDER does not eject anybody** - it only stops new players sitting.
  Disabling the station's GAMEOBJECT does eject the occupant. `ExitStation(VRCPlayerApi)` exists but acts
  on the LOCAL player, so ejecting someone else means sending them an event and letting their own client
  make the call. (SDK 3.10.5)
- **A teleport or respawn takes a seated player out of their station, and leaving one calls
  `Immobilize(false)`** - so a hold set before `TeleportTo` or `Respawn` is gone after it; set it again
  straight after the call. In ClientSim a seated player teleported away stayed where they landed, and a
  later `ExitStation` had no one to eject. (`ClientSimPlayerStationManager.OnPlayerTeleported`, SDK 3.10.5)

## Language

- **`GetCurrentLanguage()` and `GetAvailableLanguages()` answer for the LOCAL user only**, not per remote
  player, and they are static on `VRCPlayerApi`. Codes are RFC 5646 (`en`, `ja`, `zh-CN`).
  `GetAvailableLanguages` returns a real `string[]`, so unlike the bulk reads above it can simply be
  looped. `Event_OnLanguageChanged` fires when the user changes it, so a localised world reacts rather
  than polling. (SDK 3.10.5)

## Reaching into a player's PlayerObject

- **`FindComponentInPlayerObjects` searches ONE player's PlayerObject hierarchy for the component matching
  a reference you pass in**, and it exists in both spellings:
  `player.FindComponentInPlayerObjects(Component)` and
  `Networking.FindComponentInPlayerObjects(VRCPlayerApi, Component)`. Both return a bare `Component`, so
  the result needs a cast - and it needs `Utilities.IsValid` first, because that player's PlayerObject may
  not have loaded yet. This is the supported way to get from a `VRCPlayerApi` to that player's own synced
  behaviour without maintaining a lookup table. (SDK 3.10.5)
