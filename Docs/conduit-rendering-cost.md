# What a world costs to render, and how to cut it

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before optimising a scene, changing batching or culling settings, or telling
> anyone a world is too heavy.**

`conduit-performance.md` owns UDON's cost - what the scripting pays. This file owns the other half: what
the render thread and the GPU pay for the scene itself. `conduit-lighting.md` owns the bake,
`conduit-shaders.md` owns what a shader costs and what Android actually restricts,
`conduit-world-components.md` owns the mirror, and `conduit-build-validation.md` owns the 100 MB Android
ceiling and the fact that VRChat gates NO upload on any number in this file.

> **The numbers here are settled VRChat and Unity world-building practice, not guesses and not claims to
> treat with suspicion.** They are starting points, and the way to move one is to profile on the target
> device rather than to argue with it. Everything structural - what batching requires, what the engine
> culls for free, which render pipeline you are in - is engine behaviour and does not move at all.

## Measure, change ONE thing, measure again

- **Never stack optimisations before re-measuring.** Three changes and one improved number tells you
  nothing about which change helped, and the usual outcome is that a change which made things WORSE is
  kept because it shipped alongside one that helped. One change, one measurement, every time.
- **Conduit takes the measurement without leaving the session, and the user never reads a window for
  you.** The `profiler-*` verbs, `frame-debugger-capture` and `editor-focus` cover recording, ranges,
  markers, counters, threads, memory and draw events; `unity-tool-list profiler` lists them.
- **Never call a cost from a mean, and never from frames of two sessions.** A cost is a difference
  between two states recorded in one session, reported with its 95% interval from `profiler-compare` or
  `profiler-isolate`; "not distinguishable" means unproven, whatever the means say.
- **Run an A/A comparison first, interleaved: one state's frames split into alternating chunks
  (`a1..a2+a3..a4` against the chunks between) must come back not distinguishable.** Two back-to-back
  ranges are not one state in a world with moving content: traffic passing showed as +92k triangles and
  a provable render-thread difference, while the same frames interleaved passed on every figure. A
  triangle or batch count that differs between the sides means the state changed inside them.
- **After any change, wait with `profiler-wait-settled` before marking the new state, never a fixed
  number of frames or seconds.** Settling took 10 to 14 s after entering play and 4 to 5 s after one
  toggle on the same machine, and differs on every other. Settled means positive evidence: the windows
  differ by less than 5% of the figure's level. "Not distinguishable" alone passes a window too noisy to
  tell, as it did once with main work still falling 3.7 ms.
- **Label each state with `profiler-mark` as it starts and ends, and compare label ranges.** Frame numbers
  copied off an earlier reply drift into the neighbouring state.
- **Measure one object's cost with `profiler-isolate`, never by toggling it by hand.** It alternates the
  object, its renderers or one component, settles after every switch, puts everything back as found and
  reads it back. Two cycles do not average out traffic; use more cycles where things move through view.
- **The GPU Usage module costs the render thread itself, about 0.8 ms per frame.** Keep it in the same
  state for both sides of a comparison, and switch it on only when GPU time is the question
  (`profiler-isolate`'s gpu section does both).
- **The "GC Allocated In Frame" counter is the whole editor process, never the game's garbage.** In one
  play-mode capture it averaged 1.5 MB per frame while the player loop allocated 5.4 KB, the rest of the
  editor 26 KB, and Conduit itself 993 KB. Shipping garbage is `PlayerLoop`'s totalGcBytes in
  `profiler-marker-timeline`. To see the editor's share broken down, set `profileEditor` in
  `profiler-settings` for the run (the player loop then nests inside `EditorLoop`) and switch it back after.
- **Conduit's own work is recorded under the `ItsRenji Conduit` profiler category and markers**, and the
  profiler verbs report it apart from the game and the editor (conduitSelfMsPerFrame,
  conduitGcBytesPerFrame). On the main thread it shows only while `profileEditor` is on. A large read still
  lands in one editor frame (Unity builds each frame view natively, about 0.5 ms per frame per thread), so mark
  the end of a measured range before reading it; `profiler-wait-settled` reads two frames per tick instead and
  costs about 1.2 ms a frame while it waits.
- **Deep Profile answers "which call", never "how expensive".** It instruments every managed call, so
  its own overhead dominates the numbers it reports and the relative costs it shows are not the relative
  costs that ship. Find the call with it, then measure the cost without it.
- **Deep-profile a large Udon world briefly, then stop play and ask once with a small `topN`.** Every Udon
  VM instruction becomes samples: one frame of a world with about 860 behaviours carried 6,700 markers and
  took 5 s, and three `profiler-frame-hierarchy` calls over those frames, each asking for hundreds of
  markers and each still grinding after it timed out, took the editor past 38 GB until it had to be killed.
  Wait for each answer before sending the next; a `FixedUpdate` probe with the normal profiler isolates
  a single operation's garbage without deep profiling at all (`conduit-performance.md`).
- **The bottleneck CATEGORY decides the fix, and the four point in different directions.** A high draw
  call count is a render-thread problem and wants batching and culling; high GPU time at a LOW draw call
  count is shader cost and overdraw (`conduit-shaders.md`); VRAM spikes are textures, lightmaps and video;
  a CPU spike that tracks physics is colliders, rigidbodies and Cloth. Naming the category before touching
  anything is what stops an afternoon of batching work on a fill-rate-bound world.

## You are in the Built-in Render Pipeline, and there is no other option

- **No SRP, so no SRP Batcher, no renderer features, and no URP or HDRP shaders.** A VRChat world
  project has no Universal or HD pipeline assembly loaded at all, so URP advice and URP shaders are
  INAPPLICABLE rather than merely discouraged - and an agent reaching for either has misidentified the
  project. Batching here means static batching, dynamic batching and GPU instancing, and nothing else.
  (no `UniversalRenderPipeline` type in any of 285 loaded assemblies, SDK 3.10.5)

## Draw calls

- **Static batching is the largest single win and it is a checkbox:** mark non-moving renderers Batching
  Static and Unity combines them into shared meshes at build time. **It is paid for in MEMORY** - the
  combined mesh is stored in addition to the originals - which is the trade to weigh on Android.
- **And nothing that moves may be marked static.** That rule, and the maddening symptom it produces -
  a transform that provably rotates while the mesh sits still - lives in `conduit-project-workflow.md`.
  Read it before ticking the box on anything a script touches.
- **A MATERIAL is the batch boundary, so merging materials is what actually reduces calls.** One atlas
  shared across a group collapses that group into one call; the same objects carrying three materials
  each cannot batch at all. Atlas by LOCALITY - things seen together - rather than by object type.
- **GPU instancing draws many copies of ONE mesh with ONE material in a single call, and it is enabled
  per MATERIAL rather than per object.** It is the answer for a hundred identical props that could not be
  static because they move. A different mesh or a different material breaks it.
- **Dynamic batching only ever reaches small meshes** - Unity documents the ceiling at 300 vertices, and
  fewer once a mesh carries normals, UVs and tangents - so it is not a plan, it is something that happens
  to some of your props. Do not design around it.

## Culling: what the engine gives you, and what you have to build

- **Frustum culling is automatic and needs nothing from you**, so a script that disables renderers behind
  the player buys something the engine already does. `conduit-lighting.md` and `conduit-vrchat-ui.md` make
  the same point inside their own subjects.
- **Occlusion culling is NOT automatic - it needs a bake.** Window > Rendering > Occlusion Culling, with
  sight-blocking geometry marked Occluder Static and everything hideable marked Occludee Static. In an
  enclosed world it is the largest remaining win, because it removes exactly the geometry frustum culling
  cannot: what is in front of you and behind a wall.
- **Never mark a transparent surface as an occluder.** The player can see through it and the bake
  cannot, so everything behind a window or a glass floor is culled while they are looking straight at it.
  Occludee only.
- **Smallest Occluder is the resolution knob and it is a direct trade:** larger bakes faster and culls
  less, smaller finds more occlusion and costs bake time and data. Around 5 m suits open geometry and
  rooms and corridors want less. **Smallest Hole must be SMALLER than the narrowest gap a player can see
  through**, or the bake culls what is visible through it.
- **And Udon can open and close an occlusion portal at runtime - `OcclusionPortal.open` is exposed for
  both get and set.** This is the piece almost nobody uses: a door carrying a portal stops being scenery
  and becomes a culling boundary the world only pays for while it is open. (UdonSharp compile probe,
  SDK 3.10.5)
- **LOD groups trade triangles for distance** and are worth setting up on anything large enough to be
  seen from across the world - full detail close, roughly half the triangles at mid range, a fifth beyond
  that, culled past the point where it reads as a silhouette.
- **But LOD is authoring-only - `LODGroup` members are not exposed to Udon.** `_lod.size` is rejected
  with `Method is not exposed to Udon`, so LOD distances are fixed when you author them and no script can
  adapt them per platform, per player count or per quality setting. Bias the whole scene through
  `VRCQualitySettings.LODBias` instead (`conduit-performance.md`). (UdonSharp compile probe, SDK 3.10.5)

## Textures, and the Android budget

- **ASTC is Quest's native format - override every texture to it for Android**, sized by ROLE rather than
  uniformly: around 1024 for albedo a player stands next to, 512 for normal maps and UI, 256 for distant
  scenery. Resolution dominates both build size and load time, and build size is what the 100 MB ceiling
  measures (`conduit-build-validation.md`).
- **The SDK's Auto Fix for this flips the whole project's Android subtarget and triggers a full
  reimport.** Know that before pressing it on a large project; `conduit-build-validation.md` lists what
  every shipped fix actually does.
- **Budget roughly 250,000 triangles for a whole Quest world.** That is VRChat's own recommendation and
  the only world-geometry figure they publish; it is not an upload gate, and a tighter "limit" quoted
  anywhere else is community guesswork (`conduit-build-validation.md`).
- **Per object, the shape most worlds land on** is a few thousand triangles for something a player
  handles up close, around a thousand for background dressing, and as little subdivision as the shading
  survives on floors and walls. On mobile, bake detail into textures rather than into geometry.
- **Alpha is the mobile cost that compounds, and it belongs to the shader subject** - overlapping
  transparent surfaces are shaded repeatedly and the cost grows with the OVERLAP, not the object count.
  `conduit-shaders.md` owns it, including why Cutout beats Transparent wherever the look survives.
