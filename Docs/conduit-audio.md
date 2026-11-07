# World audio, spatialization and voice

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before adding an AudioSource, writing a voice zone, or judging how a world
> sounds.**

`conduit-video.md` owns BUNDLED video and the grid-crop technique. This file owns audio: spatialization,
the Steam Audio switch, voice and avatar-audio control, and why the editor cannot answer any of it.

## The editor plays the wrong backend

- **VRChat spatializes with Steam Audio and Unity Play Mode spatializes with ONSP, so what you hear in
  the editor is not what ships.** ClientSim's helper literally derives from the old plugin -
  `public class ClientSimSpatialAudioHelper : ONSPAudioSource` - so every Play Mode listening test is
  performed on the backend VRChat replaced. Tune and judge audio in-client (Build & Test, or an uploaded
  instance); the editor is for wiring, not for mixing.
  (`ClientSimSpatialAudioHelper.cs:8`, SDK 3.10.5)
- **Steam Audio is the shipped default for everyone, with no opt-out and nothing to configure.** It
  replaced ONSP for all players on all platforms in CLIENT release 2025.4.2 (December 2025) - a client
  version, not an SDK version, so no upgrade on your side triggered it and none can revert it. There is no
  world-descriptor setting for the spatializer.
- **Authoring did not change: keep using `VRC_SpatialAudioSource`** and VRChat converts it to Steam Audio
  when the world loads. The conversion is not guaranteed inaudible, and VRChat added SERVER-side tuning in
  2025.4.2p1, so a world tuned purely by ear is worth re-checking occasionally rather than treated as
  settled.
- **Three symptoms are VRChat BUGS to report, not things to tune around:** levels changing drastically,
  falloff curves not being respected, and ranges not applying the same. These are the creator's to absorb:
  direction and audible-distance cues, and minor shifts in tonality, EQ or mixing. Deciding which of the
  two you have is the whole triage, so check against that split before re-authoring anything.
- **Steam Audio's own occlusion, reflections and raytracing are NOT exposed to world creators**, and
  installing the Steam Audio Unity package does not unlock them: no Steam Audio component appears on
  VRChat's allowlisted world components, and an unlisted component does nothing in an uploaded world.
  Unity Reverb Zones and Audio Mixer effects are untouched by the swap and still work, and so does manual
  occlusion done by scripting volume.

## Pairing an AudioSource

- **Every world `AudioSource` wants a `VRC_SpatialAudioSource` on the SAME GameObject**, or the Build
  Panel raises it. Add it deliberately rather than pressing Auto Fix - `conduit-build-validation.md`
  covers why that particular fix is one of the dangerous ones.
- **`autoSpatializeAudioSources` on the scene descriptor is DEPRECATED - turn it off and pair every
  source by hand.** The Build Panel raises a warning for any scene that still has it set, and VRChat's own
  text is the instruction: "This has been deprecated, press 'Fix' to disable. Also, please add
  VRC_SpatialAudioSource to all your audio sources." Its fix does nothing but set the flag false, which
  makes it one of the few Auto Fixes safe to press (`conduit-build-validation.md`). So the flag is never a
  reason to skip pairing - it is a legacy setting with a documented instruction to stop relying on it.
  (`VRCSdkControlPanelWorldBuilder.cs:570-576`, SDK 3.10.5)
- **The missing-spatial-audio Auto Fix is louder and wider than the AudioSource it is added to:** it
  writes `AudioManagerSettings.RoomAudioGain` (10 dB) and `RoomAudioMaxRange` (80 m) on a world source, or
  the avatar pair (Far 40 m) under an avatar, and reports only that a warning cleared. **To clear the
  warning without that, use Gain `0 dB`**, set Far from the AudioSource's own `maxDistance` or the
  intended audible range, leave Near at 0 m, and leave spatialization OFF for audio that is deliberately 2D.
  (`AddVRCSpatialToBareAudioSource`, SDK 3.10.5)
- **Preserve what was authored:** `volume`, `spatialBlend`, `rolloffMode`, `minDistance`, `maxDistance`
  and any custom curve. Turn on Use AudioSource Volume Curve when a 3D rolloff was tuned by hand, or the
  companion overrides it.
- **Near is where attenuation STARTS and Far is where it reaches silence.** Volume is held flat out to
  Near, falls across the gap, and is inaudible at Far - so Far is the audible range and Near is the size
  of the full-volume bubble. Raising Near does not make a sound louder, it makes it stay loud further
  out, which is why "I cannot hear it" is almost always Far and "it is too loud up close" is almost never
  Near.
- **Volumetric Radius measures from the SURFACE of a sphere, not from a point**, which is the right
  control for a waterfall or a crowd. Reach for it instead of inflating Far, which only makes a point
  source audible from further away rather than making it feel large.

## What the clip costs before it ever plays

- **Load Type is a memory-versus-decoder trade and the three cases split cleanly.** Streaming for long
  BGM - tiny memory, one decoder running, a small CPU cost while it plays. Decompress On Load for short
  frequent effects - full PCM in RAM, nothing to decode at trigger time. Compressed In Memory for long
  looping ambience that must start instantly without holding a stream open.
- **Getting this wrong shows up as build size and load time, not as a runtime fault**, which is why it
  survives to upload unnoticed - a few Decompress On Load music tracks will quietly eat an Android budget
  that the 100 MB ceiling then rejects (`conduit-build-validation.md`).
- **Vorbis at roughly 70% for music and 50-70% for effects is the usual starting point.** These are
  starting points to move by listening on the target platform, not thresholds.

## Voice and avatar audio from Udon

- **There is no world-wide voice setting - `VRCSceneDescriptor` carries no voice fields at all.** Go
  looking for Voice Falloff Range, Voice Near, Voice Far, Voice Volume or Voice Disable Lowpass on the
  descriptor and none of them is there: enumerating its real member list returns no voice field of any
  kind. Voice is a per-player runtime call and nothing else, so a world that wants different voice
  defaults applies them to each player as they join rather than setting them once in the Inspector.
  (`VRC.SDK3.Components.VRCSceneDescriptor` members, SDK 3.10.5)
- **All five voice settings have GETTERS as well as setters, so a voice zone can save and restore rather
  than guessing the defaults on exit.** `GetVoiceGain`, `GetVoiceDistanceNear`, `GetVoiceDistanceFar`,
  `GetVoiceVolumetricRadius` and `GetVoiceLowpass` are all in the registry beside their `Set` twins.
  **This is the whole difference between a voice zone that composes and one that does not:** a zone that
  writes hardcoded "defaults" on exit silently undoes any other zone the player is still inside, whereas
  one that captured the previous values on entry puts back exactly what it found.
  (SDK 3.10.5)
- **Gain 15 and Far 25 are READABLE SDK CONSTANTS, not client-side folklore.** `VRC.SDKBase.AudioManagerSettings`
  declares `VoiceGain = 15f` dB and `VoiceMaxRange = 25f` m - commented as half the Oculus inverse-square
  max range - alongside `MinVoiceSendDistance = 25`, `MaxVoiceSendDistancePctOfFarRange = 0.5` and
  `VoiceFadeOutDistancePctOfFarRange = 0.25`, which nothing else documents. **Near 0, Volumetric Radius 0
  and Lowpass true are NOT in that file**, so read those off a live player with the getters above rather
  than trusting a remembered row. (`AudioManagerSettings.cs:9-15`, SDK 3.10.5)
- **The avatar- and world-audio restore values are knowable too, which matters because their setters have
  no getters:** `AvatarAudioMaxGain = 10` dB, `AvatarAudioMaxRange = 40` m, `RoomAudioGain = 10` dB and
  `RoomAudioMaxRange = 80` m. That is what the set-only family below has to be restored TO, so the owning
  behaviour can hold shipped numbers instead of an invented "normal". **`LipsyncGain = 1` is a MULTIPLIER,
  not dB** - the SDK flags that exception in its own comment.
  (`AudioManagerSettings.cs:12-18`, SDK 3.10.5)
- **The avatar-audio family is set-only - there are no getters.** `SetAvatarAudioGain`,
  `SetAvatarAudioNearRadius`, `SetAvatarAudioFarRadius`, `SetAvatarAudioVolumetricRadius`,
  `SetAvatarAudioForceSpatial` and `SetAvatarAudioCustomCurve` exist with no `Get` counterparts, so the
  save-and-restore pattern above CANNOT be used for them. A behaviour that changes avatar audio owns the
  restore values itself, which means one behaviour should own them for the whole world rather than several
  zones each writing their own idea of normal. (SDK 3.10.5)
- **These are LOCAL calls that shape what the local player HEARS**, so they run on each client for itself
  and never need syncing. Drive them from `OnPlayerTriggerEnter` / `Exit` guarded on `player.isLocal`.
- **Voice was deliberately NOT converted 1:1.** Falloff curves differ, EQ and compressor tuning changed,
  and source DIRECTIONALITY is new - a speaker facing away now sounds different from one facing you, which
  no pre-2025.4.2 tuning accounted for. Treat any voice-zone numbers chosen before that client as a
  starting point to re-verify by ear.

## Finding the sources you missed

- **The in-client Audio Sources debug page lists every active `AudioSource` in the world**, and its
  `VRC/SAS` column says whether each one has a spatial audio component and whether it converted - which is
  the fastest way to catch a source that never got one. Open it with Toggle Debug UI at the bottom of the
  Quick Menu Settings page. Only the world author sees it by default; enabling World Debugging in the
  world's settings on the website lets others open it, and anyone already in the instance must rejoin
  first. (client 2026.1.2)
