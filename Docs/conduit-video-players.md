# URL video players - AVPro, Unity, and keeping them in sync

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before building or fixing a video player that streams a URL.**

`conduit-video.md` owns BUNDLED video - a `VideoClip` shipped inside the world, and the grid-crop
technique. This file owns the other kind: `VRCUnityVideoPlayer` and `VRCAVProVideoPlayer` playing a
`VRCUrl`, and the synchronisation that makes a jukebox or cinema work for more than one person.

> **PROVENANCE.** The API surface here was read from the registry and the type and is cited as such.
> The BEHAVIOURAL rules - that `PlayURL` does not propagate, that a seek before `OnVideoReady` is
> ignored, that a rate-limited request can vanish with no callback, that AVPro's texture flickers between
> decoder frames - are INHERITED and uncited, and **have not been verified in this project**. They are coherent and match how the API is shaped, which is why they are here - but
> test them before building on one, and replace it with what you observed.
> **They are NOT untestable, they are merely untested.** ClientSim stubs AVPro out entirely
> (`conduit-video.md`), so play mode cannot reach them - but the real client narrates this subsystem in
> its own log, with `[Video Playback]` url-resolution lines, `[AVProVideo]` playback-path and error lines,
> and the resync registration below. `conduit-testing.md` has the path. Reach for that before writing
> another bullet off a reference.

## The surface both players share

- **Everything goes through `BaseVRCVideoPlayer` because it is the only video player type Udon can
  call at all** - writing against it rather than the concrete type is not merely tidy, it is the only
  thing that compiles: `PlayURL(VRCUrl)`, `LoadURL(VRCUrl)` (preload without playing), `Play()`,
  `Pause()`, `Stop()`, `GetTime()`, `SetTime(float)`, `GetDuration()`, plus `IsPlaying`, `IsReady`,
  `Loop`, `VideoWidth` and `VideoHeight`. Callbacks are `OnVideoReady`, `OnVideoStart`, `OnVideoEnd`,
  `OnVideoLoop` and `OnVideoError(VideoError)`. (SDK 3.10.5)
- **There is no `OnVideoPause` and no `OnVideoPlay` - the registry holds exactly five video events.**
  `Event_OnVideoReady`, `Start`, `End`, `Loop` and `Error`, and nothing else. Pause and Play are the
  obvious pair to expect beside them and neither exists, so a handler written for either is never called
  and nothing says so. Track paused state from your own `Pause()` and `Play()` calls.
  (SDK 3.10.5 registry)
- **Check the built-in resync before hand-rolling drift correction - BUT `EnableAutomaticResync` is
  the only part of it Udon can touch.** `BaseVRCVideoPlayer` also declares `SetIndexMarker(float)` /
  `SetIndexMarker()` and a `SyncIndexTimeSet` / `SyncIndexPosition` pair, and the UdonSharp compiler
  rejects all three: `Method is not exposed to Udon: '_player.SetIndexMarker(0.5f)'`, and the same for
  each property. They are declared on the type and absent from the whitelist, which is what makes reading
  the C# type a trap here. `EnableAutomaticResync` compiles for get and set, so the flag
  is the entire control surface. Measure what the built-in does for your content before replacing it; a
  hand-rolled corrector that duplicates it is cost with no benefit. (UdonSharp compile probe, SDK 3.10.5)
- **AND `EnableAutomaticResync` is not a decorative property - the client has a subsystem on the other
  end of it.** Players REGISTER with a client-side `VideoPlayerOverseer`, and turning the flag off
  deregisters them: the client log says
  `Skipping registration of a video player on '<object>' with the VideoPlayerOverseer as automatic resync
  is disabled.` **So that flag decides whether VRChat manages drift for you at all** - it is the switch
  between "the platform handles it" and "I now own resync entirely", and a world that disables it without
  replacing it has simply turned drift correction off. The line appears once per player per load, which
  also makes it the cheapest way to confirm which mode a world is actually in. (Client log)

## Choosing one, and what only the Inspector can set

- **AVPro is the streaming player and Unity's is the file player, and the SDK's own type shapes say so.**
  `VRCAVProVideoPlayer` is the only one carrying `MaximumResolution` and `UseLowLatency`;
  `VRCUnityVideoPlayer` is the only one holding a native `UnityPlayer : UnityEngine.Video.VideoPlayer`.
  A live stream, an HLS or DASH manifest, or a url the client has to resolve wants AVPro; a direct
  `.mp4` or `.webm` plays on either. (SDK 3.10.5)
- **And only the Unity player plays in the editor at all** - ClientSim substitutes an inert stub for
  AVPro, so a world built on AVPro has NO play-mode video without a shim (`conduit-video.md`). That is
  the real cost of choosing it, and the reason to keep the behaviour written against
  `BaseVRCVideoPlayer` so a Unity player can stand in while you work.
- **Neither concrete type is exposed to Udon - they compile as field types and reject every member.**
  `_avpro.MaximumResolution`, `_avpro.UseLowLatency` and `_unity.UnityPlayer` each fail with
  `Method is not exposed to Udon`, while the serialized field declaration itself compiles fine - so an
  Inspector reference that looks wired is not evidence that anything can be read off it.
  (UdonSharp compile probe, SDK 3.10.5)
- **So Maximum Resolution, Autoplay and Low Latency are authoring-time decisions and cannot be changed
  at runtime.** A quality button that drops the resolution cap is not buildable: ship the value the world
  needs. **NEVER offer a second player as the way round it** - no shipped player splits itself that way,
  and it is not a platform question anyway: the split is by BUILD rather than by device, a tethered
  headset runs the PC build, and standalone hardware decodes 1080p in fixed-function silicon. Suggesting
  one is importing a RENDERING budget into a DECODE question.
- **AVPro's audio leaves through `VRCAVProVideoSpeaker`, not through the AudioSource - and Udon cannot
  touch that either.** `_speaker.Mode` fails the same way, so channel routing is fixed in the Inspector.
  Its `ChannelMode` has nine members - `StereoMix`, `MonoLeft`, `MonoRight`, then `Three` through
  `Eight` - which means SEVERAL speakers can pull DIFFERENT channels out of ONE decode. That is how a
  multi-language or multi-stem track is split across objects without paying for a second video player.
  (SDK 3.10.5)
- **Unity's player has no speaker component and routes through AudioSources directly**, and it carries no
  `MaximumResolution` at all - that cap is an AVPro concept rather than a video-player one.

## Two rules that decide the whole architecture

- **`PlayURL` does not propagate - it is a purely local call.** The owner calling it starts the video
  on the owner's client only. Every other client, and every late joiner, must call `PlayURL` themselves
  from synced state. So the synced thing is never "play this now" - it is the url plus enough timing to
  reconstruct where playback should be, and each client acts on it in `OnDeserialization`. A design that
  treats `PlayURL` as a broadcast works perfectly alone and for nobody else.
- **A seek before `OnVideoReady` is silently ignored.** `SetTime` on a player that has not finished
  loading does nothing and reports nothing, so a late joiner lands at zero instead of the current
  position. Set a pending flag, call `PlayURL`, and do the `SetTime` inside `OnVideoReady`.

## Sync the CLOCK, never the position

- **Never sync playback position - it is stale the instant it arrives.** Sync the url, a server-clock
  stamp taken when playback was committed, and the playback offset at that moment. Every client then
  computes `position = offset + (serverTimeNow - stamp) * speed` for itself. This is correct by
  construction for late joiners - they run the same arithmetic with the same inputs and land in the same
  place - and it needs no periodic correction message.
- **And take that stamp from `GetServerTimeInSeconds()`, NOT `GetServerTimeInMilliseconds()`.** The
  milliseconds call returns a signed 32-bit int, so it wraps after about 24.8 days of server uptime and
  every position computed across the wrap is wildly wrong. `GetServerTimeInSeconds()` returns a `double`
  and has no such edge. An always-on world is exactly the case that hits this, and it presents as a
  player that has worked for weeks suddenly seeking to nonsense. (SDK 3.10.5 registry)
- **Guard the seek against the end of the video.** If the computed position is past `GetDuration()`, the
  video finished before this player arrived - stop rather than seeking. A live stream reports duration 0,
  so skip the check rather than treating 0 as "already over".

## Errors, and which of them are worth retrying

- **`VRC.SDK3.Components.Video.VideoError` has exactly five members:** `Unknown = 0`, `InvalidURL = 1`,
  `AccessDenied = 2`, `PlayerError = 3`, `RateLimited = 4`. Note this is NOT Unity's `VideoError`, which
  is a different ten-member enum in `UnityEngineInternal.Video` - a `using` that pulls in the wrong one
  compiles and then never matches. (SDK 3.10.5)
- **Retry only what can succeed.** `InvalidURL` and `AccessDenied` are permanent - the url is malformed
  or the domain is untrusted, and retrying is just noise. `RateLimited` wants more than five seconds of
  patience. `PlayerError` is a codec or decoder mismatch, which is the one case where switching to the
  OTHER player type is a real fix rather than a superstition.
- **And a dropped request raises no error at all.** A `PlayURL` issued inside the rate-limit window can
  be discarded with no callback of any kind - not `OnVideoError`, not `OnVideoReady` - so a retry loop
  keyed on errors waits forever. Space requests beyond five seconds rather than relying on being told.
  `conduit-udonsharp.md` covers the shared rate limiter that governs string, image and video loads.

## AVPro's texture is not stable

- **AVPro's output texture is written by the native decoder asynchronously, so between frames it can be
  blank** - which shows as flicker on the screen surface. Blit it into a `RenderTexture` you own in
  `LateUpdate` with `VRCGraphics.Blit`, and put THAT on the display material: a RenderTexture keeps its
  last contents, so a missing frame shows the previous good one instead of black. Skip the blit when the
  source texture reads null, rather than blitting null over good content.
- **Unity's own video player does not have this problem**, so the stabiliser is AVPro-only and is pure
  cost on the other one.
- **Play mode cannot exercise AVPro at all by default** - ClientSim substitutes an inert stub.
  `conduit-video.md` owns that, including the shim that gets a real test path.
