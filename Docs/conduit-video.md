# Bundled video in a world

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before putting video in a world, animating a UI element, or choosing between
> video and a sprite flipbook.**
>
> **This file owns BUNDLED video - a `VideoClip` shipped inside the world - and nothing else goes in it.**
> A player streaming a `VRCUrl` is `conduit-video-players.md`, which draws the same line from its side.
> The audio a video emits is `conduit-audio.md`; the `RawImage` the grid is cropped onto is
> `conduit-vrchat-ui.md`, with only the uv arithmetic here. The shim that gets AVPro a play-mode test path
> is recorded here because it is a package-install fact, but what AVPro DOES belongs to the other file.

## The three walls - read these before designing anything

- **Udon cannot touch `UnityEngine.Video.VideoPlayer` at all.** `clip`, `Play()`, `Stop()` and
  `isLooping` every one refuse with "Method is not exposed to Udon". The component IS permitted in a
  world - `WorldValidation.cs` lists it and post-processes its audio output at upload - but a behaviour
  can never address it. Design for a player that runs untouched from Play On Awake, or do not use one.
- **And the "post-processes its audio output" part silently adds a component to your GameObject.** A
  `VideoPlayer` whose `audioOutputMode` is `Direct` bypasses the client's volume controls, so VRChat
  stops it, calls `AddComponent<AudioSource>()` on the same object, switches the mode to `AudioSource`,
  points every audio track at that new source, and restarts it - logging only `VideoPlayer using DIRECT
  audio output fixed.` The uploaded world then carries an AudioSource nobody authored, on Unity's
  defaults and with no `VRC_SpatialAudioSource` beside it. **Set the output mode to AudioSource yourself
  and pair it properly** (`conduit-audio.md`) rather than letting the SDK improvise one.
  (`WorldValidation.cs:800-826`, SDK 3.10.5)
- **An AnimationClip cannot drive `VideoPlayer.m_VideoClip` EITHER.** The PPtr curve authors, saves
  and reports a key; the Animator reaches the state; the field never changes. Unity binds
  object-reference curves for a known set (`Image.m_Sprite` and friends), and an arbitrary
  `UnityEngine.Object` field has NO runtime binder. It fails silently, which is why it looks promising.
- **A probe exercising five of these members proves only the first one** - the whitelist stage
  short-circuits, so the rest read as a pass and the design gets built on nothing. One member per
  compile; `conduit-compile-console.md` owns the probing technique.

## Testing video in play mode

- **ClientSim replaces AVPro with an INERT stub, so a `VRCAVProVideoPlayer` does nothing in play mode
  by default.** `ClientSimAVProVideoStub` implements `LoadURL`, `PlayURL` and `Play` as empty bodies, so
  the call issues no HTTP request and raises no callback. The result is SILENCE rather than an error,
  which reads as a bad URL or a misconfigured component and sends diagnosis to the wrong layer entirely.
- **A shim can take the player over so video resolves through VRChat's own yt-dlp**, making play mode
  behave like the client and a real network measurement possible without an upload. It is
  **ArchiTech.VideoPlayerShim**, whose own description is "enabling both UnityVideo and AVProVideo
  playback in-editor for the VRChat SDK".
- **It is three separate VPM packages added in VCC or ALCOM - not a UnityPackage import, and installing
  ProTV alone does not do it.** You need the **VRChat Package Resolver Tool**, **ProTV**, and
  **ArchiTech.VideoPlayerShim** added SEPARATELY afterwards; both managers have a package search, so
  each is found by name rather than by package id.
  Nothing pulls the shim in - ProTV's own dependency list names the Worlds SDK and ArchiTech's SDK and
  NOT the shim, and nothing else depends on it either. **Miss any one and play mode silently stays on the
  inert stub above** - no error, just the silence that reads as a bad url.
  (`Packages/vpm-manifest.json`)
- **`ClientSimMain` hooking that stub is NOT proof AVPro cannot work in play mode.**
  `VRCAVProVideoPlayer.Initialize` is a MULTICAST delegate, so the hook being unconditional does not make
  it the only handler, and a shim registered alongside it wins. Reading that one line and concluding the
  stub is unavoidable is exactly the wrong inference, and it costs a working test path.

## Showing N animations with ONE decoder

- **Tile every animation into ONE looping video as a grid and select a cell with `RawImage.uvRect`.**
  The video is never addressed, only cropped, so no VideoPlayer API is needed at all. Switching is
  instant with no re-prepare or black frame, and the cost stays a single decoder however many
  animations the grid holds.
- **Flip the row when converting a cell number to a uv rect.** `column = cell % columns`,
  `row = cell / columns`, then `y = 1 - ((row + 1) * height)` - grids read top-down while uv space counts
  up from the bottom. Skip the flip and every cell shows the one mirrored vertically across the grid,
  which reads as a wiring fault rather than a maths one.
- **Inset every cell edge by half a texel (`0.5 / gridPixels`).** Bilinear filtering samples PAST the
  boundary, so without it a sliver of the neighbouring animation bleeds along each edge and looks like a
  rendering artefact.
- **The RenderTexture must match the video's pixel size EXACTLY.** Any rescale lands cell boundaries on
  fractions of a texel and the bleed can no longer be tuned out at any inset.

## Video beats a sprite flipbook on size, by one to two orders of magnitude

- **Prefer one grid video over flipbook atlases for looping animation.** Video predicts between frames;
  a flipbook stores every frame spatially with no inter-frame compression. 14 animations of 60 frames at
  192x192 are 0.27 MiB as one VP8 grid against 29.53 MiB of BC7/DXT5 atlas - 107 to 1.
- **Compare against BC7 or DXT5, NEVER DXT1, when the frames carry alpha.** DXT1 is 4 bits per pixel and
  has NO alpha channel, so quoting it halves the flipbook figure and understates the win against a format
  that cannot hold the content at all. The atlas side needs no measuring: fixed-rate block compression is
  exactly frames x width x height x 1 byte at 8bpp.
- **The instinct that video is the heavyweight option is wrong for short loops of flat art**, and right
  for one long clip. Decode cost scales with frame area, not with how many animations share the frame -
  a 768x768 grid is a fraction of 720p.

## Encoding for VRChat

- **VP8 only - VP9 is not supported.** Encode with `-c:v libvpx -pix_fmt yuva420p -auto-alt-ref 0`;
  libvpx's alt-ref frames break VP8 alpha, so that flag is required whenever the source is transparent.
- **PUT `-c:v libvpx` before every `-i` when the source has alpha.** ffmpeg's default `vp8` decoder
  SILENTLY DISCARDS the alpha plane, so a filter graph built without it stacks opaque frames and alpha
  is gone before any filter sees it. The downstream tell is transparent padding with opaque content -
  the fill region keeps its alpha while every real frame loses it.
- **Verify alpha by extracting it, never by looking at the result:**
  `ffmpeg -c:v libvpx -i grid.webm -vf alphaextract,format=gray -frames:v 1 alpha.png`. Silhouettes mean
  alpha is live; a solid white rectangle means it is not. Omitting the decoder flag makes this same
  command fail with "Requested planes not available", which is the trap catching you twice.
- **Transcode and Keep Alpha on the VideoClip importer are NOT required for VP8 alpha.** A clip with
  `m_EncodeAlpha` false and no transcode plays transparent perfectly well. When transparency is missing
  the ENCODE dropped it - check with `alphaextract` before touching import settings.
- **The inspector's video preview is not evidence.** It reads the file directly and will show
  transparency the runtime never receives, which points diagnosis away from the render target and the
  encode both.
