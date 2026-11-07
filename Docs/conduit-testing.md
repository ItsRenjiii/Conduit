# Testing a world, and what the editor cannot tell you

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before claiming a networked behaviour works, and before planning how to verify
> anything that involves a second player.**

`conduit-compile-console.md` owns compiling and reading the console. `conduit-vrc-input.md` owns driving
ClientSim through the bridge. This file owns the question those two cannot answer: **is it actually
correct in VRChat?**

## ClientSim cannot validate networking. At all.

- **Play mode has ONE real client, so every multi-player property of a design is untested there** -
  ownership transfer between real clients, sync conflicts, a genuine `OnDeserialization` from a remote
  owner, late-joiner restoration, master handoff, and `Networking.IsClogged` are all outside what the
  editor simulates. ClientSim can add simulated remote players that fire join and restore events, and
  those players perform no network synchronisation whatsoever, so an event firing proves the handler
  compiles and nothing else.
- **Those controls are the Player Buttons foldout, and its two halves have OPPOSITE enable rules.** The
  setup fields - local player name, start height, language, is-master, is-instance-owner, is-VRC+ - lock
  the moment ClientSim starts, so they are a before-play decision; the action buttons (Spawn Remote
  Player, Remove Player, Simulate VRC+ Gift) work only WHILE playing. Reaching for either at the wrong
  moment gives a greyed control and no explanation. (`ClientSimSettingsWindow.cs:407, 422`, SDK 3.10.5)
- **Spawn Remote Player takes a custom name, so display-name logic is testable in the editor** - the SDK's
  own tooltip calls it "Useful for testing udon script name detection", and an identity list keyed on
  `displayName` (`conduit-networking.md`) can be exercised without a second real client. Remove Player
  fires the leave path, so the `OnPlayerLeft` cleanup that a station's unguaranteed exit depends on
  (`conduit-player.md`) is reachable too. **Both prove LOCAL dispatch only - no sync happens.**
  (`ClientSimSettingsWindow.cs:424-465`, SDK 3.10.5)
- **And where it does simulate, it sometimes lies - which is worse than not simulating.** The specific
  measured cases are recorded against their own subjects and are worth knowing before you trust a play
  test: `DeserializationResult` carries fixed values (`conduit-networking.md`), spatial audio runs on the
  OLD backend (`conduit-audio.md`), `currentlyHeldBy` is never populated and the look-pitch limit is
  ClientSim's own (`conduit-vrc-input.md`), the duplicate EventSystem warning is ClientSim's
  (`conduit-vrchat-ui.md`), and `GetJumpImpulse()` always reads 0 because the field it answers from is the
  one locomotion value ClientSim never seeds (`conduit-player.md`). A play test that contradicts one of
  those is not evidence. **The jump case is the shape to watch for: the four values BESIDE it read
  correctly, so the wrong one arrives wearing the credibility of its neighbours.**
- **So the honest report after a play test is "the logic runs", never "the sync works."** Saying the
  second from editor evidence alone is the single easiest false claim to make in this project.

## Build & Test is the real instrument

- **Number of Clients launches that many REAL VRChat clients at once**, which is the only way to exercise
  ownership, sync and late joiners. Two is enough for ownership and propagation; three is what a
  late-joiner test needs.
- **The FIRST client to load is the instance master and the default owner of every scene object**, so a
  test that only ever uses client one is testing the owner path exclusively - which is the path that
  works. Drive the SECOND client for anything about non-owners.
- **Number of Clients = 0 IS "Build & Reload", and it is the fast loop nobody uses.** It rebuilds the
  world and moves the ALREADY-RUNNING client into the new instance, skipping the entire launch and login
  sequence. For iterating on a single-player behaviour that is a different order of speed from relaunching,
  and it is worth reaching for by default once a client is already up.

## The late-joiner test, written out because it is the one that catches most bugs

1. Launch TWO clients and change some state - toggle the synced bool, score a point, open the door.
2. Without resetting anything, launch a THIRD client into the same instance.
3. The third client must show the CURRENT state, not the initial one.

That procedure is what catches a missing `RequestSerialization`, a value only ever sent through an event,
and side effects that replay on join - which are exactly the failures `conduit-networking.md` documents
and exactly the ones no editor test can reach. Run it before believing any synced feature is finished.

## Where the evidence actually is

- **The VRChat client writes its own log**, separate from Unity's:
  `%AppData%\..\LocalLow\VRChat\VRChat\output_log_<time>.txt`. That is where a `Debug.Log` from an
  uploaded or Build & Test world lands - the Unity console never sees it.
- **And that log is a measurement instrument, not just your own prints - it is the only place sync cost
  can actually be measured.** The client periodically writes a JSON block keyed `runningTime` / `stats`
  carrying its own network telemetry, including `net_bytes` and `net_events` split per message type
  (`udon_serialization`, `udon_unreliable_serialization`, `udon_network_call`, `ownership_request`,
  `ownership_transfer`, `instantiate`, `past_events`), a `net_event_dropped` counter, and
  `net_serialize_byte_count UdonSync` in BYTES PER EVENT. Grep the log for `"runningTime"` to find the
  blocks. Several rules in `conduit-networking.md` are cited to this and nothing else could have settled
  them. **The client also reports faults Unity never shows you** - a `SendCustomNetworkEvent` naming a
  method the target does not have, an ownership call on a null player, an event for an unknown view.
- **But the client binary is not a source - do not try to read constants out of it.** VRChat ships
  IL2CPP, and `VRChat_Data/il2cpp_data/Metadata/global-metadata.dat` is ENCRYPTED: its header is not the
  IL2CPP `AF 1B B1 FA` sanity magic and it holds no readable type, method or string literals, so the
  usual metadata dump returns nothing. There are no managed client assemblies either - no
  `VRChat_Data/Managed` exists. **The SDK's DLLs under `Packages/` are readable and are the right target
  for `type-find`; the client's are not.** Its behaviour is reachable only by RUNNING it and reading the
  log above. (Client install)
- **The in-game debug overlay is RShift + Backtick + 3**, which puts the log in front of you without
  alt-tabbing to a file. `conduit-audio.md` covers the separate Audio Sources debug page reached from the
  Quick Menu.
- **A `Debug.Log` INSIDE `Update` is not just slow - the client's log rate limiter silently drops
  entries, so the output is also a lie.** You get an incomplete log with no gap marker, which is the worst
  possible thing to debug from, plus a string allocation every frame feeding steady GC pressure. **The
  editor hides both symptoms**, so a per-frame log that looks fine in play mode is lossy in the client.
  Log on the EVENT, or gate a periodic one behind a frame counter.
- **Gate every diagnostic log behind a serialized `_debugMode` bool rather than deleting them.** Logging
  costs allocation on every call and string-building runs even when nothing reads it, and anything left
  in ships its internals to any player who opens that log file. `conduit-code-style.md` owns the gated
  logging convention; this is why it matters at upload rather than only in principle.
