# Lighting a world, and the PC/Quest split

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before baking, before placing probes, and before deciding what a world's
> lighting costs on Quest.**

`conduit-shaders.md` owns shader authoring and what the Android platform actually restricts.
`conduit-build-validation.md` owns the 100 MB Android ceiling and the Auto Fix that changes lightmap
settings. This file owns the bake itself: what to set, where to put probes, and what changes between the
two platforms a VRChat world usually ships to.

> **The numbers here are settled VRChat world-building practice, not guesses and not claims to treat with
> suspicion.** They are what creators who have shipped worlds for years reach for, and an agent should use
> them as the default rather than inventing its own. They are starting points rather than thresholds: a
> world with unusual geometry or an unusual budget will land somewhere else, and the way to move a number
> is to profile on the target device, not to argue with it. Everything structural here - post-processing
> being unavailable on Android, dynamic objects taking their light from probes, what a lightmap costs in
> memory - is platform behaviour and does not move at all.

## The one rule that is not a preference

- **Post-processing is disabled outright on Android, so baked ambient occlusion is the only AO a Quest
  world gets.** SSAO, bloom, colour grading and the rest of the stack simply do not run there
  (`conduit-shaders.md`), which moves AO from a post effect into the bake. Enable Ambient Occlusion in the
  Lightmapping settings for Android rather than authoring an effect that will not exist.
- **Baked AO lands in the lightmap**, so it costs bake time and texture memory rather than frame time -
  which is the right trade on mobile hardware, and the wrong one to discover after shipping.

## Bake settings, and what changes per platform

- **Light mode decides who can see the light, not just what it costs.** Baked affects static geometry
  only and is free at runtime; Mixed lights static geometry into the lightmap while still casting on
  dynamic objects; Realtime is evaluated every frame for everything. Players, pickups and anything that
  moves are dynamic, so they take their lighting from probes rather than from the lightmap
  (`conduit-project-workflow.md` owns the rule that nothing which moves may be marked Static).
- **The PC and Android bakes are different bakes, and the split is the whole job:**

| Setting | PC | Android | Why it moves |
| --- | --- | --- | --- |
| Lightmap resolution | 10-20 texels/unit | 5-10 | Keeps lightmap textures inside 1024 and the build inside its ceiling |
| Max lightmap size | 2048 | 1024 | Quest GPU memory; oversized lightmaps stutter or fail to load |
| Directional mode | Directional or not | start Non-Directional | Directional stores a second lightmap per map |
| Indirect bounces | 3-4 | 2-3 | Bake time and light bleed, for little visible gain on mobile |
| Reflection probe resolution | 256 | 128 | Cubemap memory, six faces per probe |
| Compress lightmaps | optional | on | Build size against banding on gradients |

- **Raise RESOLUTION before raising SIZE when a bake looks poor.** Resolution costs texels where geometry
  actually needs them; size raises the memory ceiling for the whole map. A dark or blurry bake at the low
  end of the range usually wants more texels per unit, not a bigger texture.
- **Reflection probes are Baked, not Realtime.** A Realtime probe re-renders the scene into a cubemap, so
  it carries the same class of cost as a mirror. One per enclosed room and one outdoors is the shape;
  overlapping three or more in one place is paying repeatedly for the same reflection.

## Probes are how anything that moves gets lit

- **Without light probes, every dynamic object in a baked world is unlit - players included.** This is
  the commonest reason a carefully baked world has black avatars standing in it. Dynamic objects take
  `Receive GI` from probes; the lightmap is not available to them.
- **Place probes where the LIGHTING changes, not where the geometry is.** Light and shadow boundaries,
  doorways, indoor-to-outdoor transitions, and anywhere the colour of the light shifts. Open space that is
  uniformly lit needs very few.
- **Place them in three dimensions.** A single floor-height grid lights an avatar's feet and nothing else;
  probes at roughly floor, mid-body and head height are what make a player look correctly lit as they
  move. Suggested spacing, inherited: 2-3 m in corridors, 3-5 m in open interiors, 4-6 m outdoors, and
  1 m or tighter across a light/shadow boundary.
- **Do not place probes inside walls or anywhere a player cannot reach** - they cost bake time and
  contribute nothing.

## Diagnosing a bad bake

- **Blurry lightmaps: raise resolution first, then size, then check UV2.** A model with no lightmap UVs or
  overlapping ones cannot bake cleanly at any resolution; `Generate Lightmap UVs` on the importer is the
  fix when the model does not ship its own.
- **Visible seams: raise lightmap padding before anything else**, then check object scale, then the UV2
  layout. Padding exists precisely so adjacent charts stop bleeding into each other.
- **Dark dynamic objects: the probes are missing or `Receive GI` is not set to Light Probes.** See above -
  this is the probe rule showing up as a symptom.
- **A slow bake is usually resolution or Static flags**, not the lightmapper. Progressive GPU, fewer
  bounces, and taking genuinely irrelevant geometry out of Static are the levers, in that order.

## Realtime flashes and white blooms

- **Route angle-dependent camera flashes to the shader investigation before retuning the lighting.**
  Soft-shadow-triggered flashes can come from invalid HDR pixels amplified by bloom. See
  [Invalid HDR pixels and bloom flashes](conduit-shaders.md#invalid-hdr-pixels-and-bloom-flashes) for
  the captured-frame controls, shader guards and verification with soft shadows retained.

## What the platform gives you for free

- **Unity frustum-culls the whole canvas and every renderer already**, so a lighting optimisation that
  duplicates culling buys nothing (`conduit-vrchat-ui.md` makes the same point for UI).
- **`Mobile/VRChat/Lightmapped` exists and is the mobile shader to reach for on lightmapped geometry**,
  alongside the other `Mobile/` shaders. `conduit-shaders.md` owns the rule that the shader model itself
  is NOT the mobile restriction, so do not lower a target on lighting grounds.
- **Realtime and Mixed lighting are PERMITTED on Android**, not blocked. They are expensive and want
  device profiling, which is a different statement from unavailable - and
  `conduit-shaders.md` covers why an unmeasured platform warning is worse than none.
