# Driving the player and handling pickups in play mode

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before driving the player, grabbing or placing a pickup, or exercising a
> world gimmick in play mode.**
>
> **This file is about driving Conduit, and nothing else goes in it.** It holds how the bridge verbs
> behave and which play-mode reads can be trusted. A fact about a VRChat COMPONENT or API - what a field
> means, which members exist, how a feature behaves in the client - belongs to that subject's file even
> when you discovered it while driving a pickup: `conduit-udonsharp.md` owns VRC component surfaces,
> `conduit-player.md` the `VRCPlayerApi`, `conduit-networking.md` sync and ownership. This has been got
> wrong repeatedly; when in doubt the answer is NOT this file.

## Pickups

- **Check `VRCPickup.AutoHold` before driving a grab.** `Auto Detect` resolves to NO auto-hold when the
  pickup has no Use events, so `vrc-input grab down` must stay down for the whole carry and `grab up` is
  the drop. With auto-hold on, one `press` toggles instead. The world's own on-screen prompt ("Hold to
  Grab") is a second read of the same fact. (SDK 3.10.4)
- **Never conclude a pickup is unheld from `currentlyHeldBy` - ClientSim NEVER populates it, so it is
  null even while the object is demonstrably in hand.** Settled by reading the live C# field through
  reflection on a held pickup: it is null there too, so this is a simulator gap and no bridge read can
  close it. The serialized property does exist and the field is declared `UnityEngine.Component`; both
  simply stay empty. Use the transform instead: a held pickup sits at a fixed offset from `cameraPos` and
  tracks the player through walking and looking. (SDK 3.10.4)
- **Measure the hold offset once, then COMPUTE the placement instead of nudging toward it.** Grab, read
  `cameraPos` and the pickup transform, and decompose the delta onto the camera forward/right/up axes. The
  offset is stable to the millimetre across movement, so a predicted position lands first try. One observed
  desktop value: 0.60 m forward, 0.10 m right, 0.08 m below eye.
- **When an interaction depends on where the OBJECT is, move the player until the OBJECT is there - aiming
  never moves it.** `vrc-look-at` points the camera, and the held pickup stays at its own offset, typically a
  metre short of whatever the reticle is on. Trigger volumes, snap zones and collider contacts need this;
  carrying, throwing, and Use-event pickups do not.
- **Combine look with a step when placing below eye level, because look alone cannot reach.** Pitching down
  rotates the whole offset vector, so the hand drops AND retracts toward the player: at -55 degrees pitch a
  0.60 m forward hold pulls back to 0.28 m. Take the drop from the pitch, then walk in the shortfall.
- **Confirm a scripted placement by the transform, not the screenshot.** A world script that catches a drop
  rewrites rotation to clean axis-aligned values and locks position to the target plane; a plain physics drop
  keeps the messy hand-held rotation. Compare the pickup transform either side of `grab up`.

## Driving and looking

- **Close the ClientSim menu first thing after entering play - `vrc-menu close`.** It opens with play mode
  every time and sits in front of the world, so anything driven before it is closed does not reach what you
  aimed at.
- **Verify a single-player behaviour here, never defer it to Build and Test.** Grabs and drops, sitting,
  teleports, respawns and jumps all run through ClientSim once the menu is closed, and editor code can read
  ClientSim's own movement state back: `player.GetClientSimPlayer().locomotionData.GetImmobilized()`
  (`VRC.SDK3.ClientSim`). The cases that genuinely need a client are the ones `conduit-testing.md` lists.
- **Exercise the world's own objects through its own controls, never a stand-in.** Press its real buttons,
  hold the pickups it hands out, sit in its seats: a fixture cube proves only the fixture, and the real
  object's own handlers - what it does when it is dropped - go untested.
- **Click a world-space UI control with `vrc-look-at <control>` then `vrc-input use press`.** `use press`
  holds the button down for two frames so the UI click edge lands, which reaches uGUI buttons behind a
  VRCUiShape as well as VRChat interacts. `game-view-click` cannot: it sends a raw Game View click without
  aiming the player at anything. (SDK 3.10.5-beta.1)
- **Aiming at a control inside a world-space canvas reports the CANVAS in `aimHit`, not the control.** The
  canvas' own VRCUiShape is what the ray meets, so `interactable no` on it says nothing about the button
  behind it - do not read that as the control being unreachable and hand the test back.
- **`vrc-drive` is ASYNCHRONOUS - it returns immediately with `driverActive: True`.** Pass `[seconds]` so it
  stops itself and read `vrc-player-state` once afterwards; `vrc-drive-stop` is for open-ended runs, not for
  ending a timed one. This is the opposite of the blocking compile verbs in `conduit-compile-console.md`.
- **`vrc-drive` moves the CharacterController itself, beneath every gate ClientSim puts on input, so it
  cannot test one.** `TickVrcDriver` calls `CharacterController.Move` directly and walks through
  `Immobilize`, an open menu and a station lock alike: driven 2 s while immobilized, the player covered 4 m.
  Test a gate with `vrc-input`, which enters ClientSim's own input path, or read its flag (above). (SDK 3.10.5)
- **Press a ClientSim action - jump, use, grab, drop - with `vrc-input`, never `game-view-key`.**
  `game-view-key` posts window messages that only `UnityEngine.Input` reads, while ClientSim takes its
  actions from the Input System (`ClientSimInputActionBased`, actions `JumpLeft` / `JumpRight`), so
  `game-view-key Space` never becomes `InputJump`. Observed seated in a car: Space did nothing,
  `vrc-input jump press` reached the world's `InputJump` at once, and `game-view-key W` still drove the
  car, which reads `Input.GetKey`. Use `game-view-key` only for the world's own `Input.GetKey` reads.
- **`vrc-look` takes ABSOLUTE yaw and pitch matching the `lookYaw` / `lookPitch` fields** that
  `vrc-player-state` prints, so read, adjust, and write back. Pitch is positive up. Use `vrc-look-delta`
  for a relative nudge.
- **The 70-down / 80-up pitch limit is ClientSim's, NOT VRChat's - never state it as a world fact or
  diagnose against it.** `ClientSimDesktopTrackingRotator` clamps head X to `MINIMUM_X_ANGLE -80` /
  `MAXIMUM_X_ANGLE 70`; the real client looks further, so a player reports aiming at something play mode
  cannot reach. It bounds what `vrc-look` can drive in the editor and nothing else, and any conclusion
  built on it - "that surface is unreachable from close up" - is false in VRChat. (SDK 3.10.5-beta.1)
- **Trust `vrc-look-at`'s reported `ray error` instead of taking a screenshot to check aim.** It resolves a
  GameObject (rect centre for a UI element) or a world point and reports the residual angle; 0 deg means the
  reticle is on target and a screenshot adds nothing.
- **Read `ray error 0` plus a DIFFERENT, farther `aimHit` as proof the target is not raycastable.** The
  reticle is on it and the ray went straight through, so either the GameObject is inactive or it is a
  trigger volume rather than an interact. Check `activeSelf` before pressing anything: a button disabled
  until payment, and a pad that wants a carried object instead of a keypress, both read exactly this way.
- **Read `aimHit` from `vrc-player-state` to identify what is under the reticle**, including its distance,
  whether it is a UI shape, and whether it is interactable. It is cheaper and more precise than reading a
  screenshot, which cannot give you a path.
