# Shader authoring and the Quest platform

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before writing or editing a `.shader`, `.hlsl` or `.cginc`, and before telling
> anyone that a shader feature will not work on Quest.**
>
> **This file owns what goes INSIDE a shader, and nothing else goes in it.** What the SCENE costs to
> render - batching, culling, LOD, texture budgets - is `conduit-rendering-cost.md`; the bake and its
> probes are `conduit-lighting.md`; a ShaderGUI's LAYOUT is `conduit-editors.md`, and wiring a material
> asset is `conduit-assets-prefabs.md`. A Build Panel warning quoted here belongs to
> `conduit-build-validation.md`, which owns what a severity means and whether an Auto Fix is safe.

## Shader model

- **Never drop `#pragma target` to 3.0 "for Quest". It is a training-data reflex from the GLES2 era and
  it is wrong.** Quest runs Vulkan / GLES3.2 on hardware that meets shader model 5.0. Lowering the target
  buys no compatibility and silently strips the features the shader was built around.
- **This one has been got wrong for years, across projects, and the user has had to correct it repeatedly.**
  The damage is doubled: the shader loses capability AND ships with warnings telling the author their
  working setup is broken. Treat any impulse to lower a target, or to caveat Quest support, as the tell.
- **Read what a shipped Quest-compatible shader targets before warning about a target level.** A vendor's
  own Android build is the strongest evidence available, and it is one grep away. Measured on a widely
  shipped shader family: EVERY renderer in it is `#pragma target 5.0`, including the variant written
  specifically for mobile - so even the mobile build does not lower the target.
- **Match the target to the features the shader uses, never to a platform guess.** Raise it when a feature
  needs it; leave it where the author put it otherwise.

## What is actually mobile-constrained

- **GrabPass is the real restriction, not the shader model.** A grab needs a full screen copy, which is
  what tiled mobile renderers cannot do cheaply. Screen-space effects that read the frame buffer fall in
  the same bucket.
- **Guard mobile-restricted features with `!defined(SHADER_API_MOBILE)`, the way the vendors do**, rather
  than lowering the target or deleting the feature. Example from a shipped shader:
  `#define GRABPASS_ENABLED defined(_GRABPASS_ON) && !defined(SHADER_API_MOBILE)`
- **A ShaderLab `GrabPass { }` block sits at SubShader level and CANNOT be removed by a
  `shader_feature`.** A `SHADER_API_MOBILE` define only stops the shader SAMPLING the grab; whether the
  capture is still scheduled is a separate question that needs a device measurement, not an assumption.
- **Post-processing is disabled outright on Android - that one IS a platform restriction.** Bloom,
  depth of field, colour grading, SSAO and SSR are simply unavailable in an Android build, so an effect
  authored through the post-processing stack is not "expensive on Quest", it does not run. Bake what you
  can into the lighting (AO especially) and design the Android look without the stack rather than tuning
  it down. This is the shape the `#pragma target` reflex WANTS to be and is not.
- **World SHADERS, by contrast, are not BLOCKED on Android - but the SDK keeps a world mobile whitelist
  and WARNS about everything outside it, which is a different thing.** `WorldValidation.ShaderWhiteList`
  holds thirteen names: eleven `VRChat/Mobile/...` shaders plus `FX/MirrorReflection` and `UI/Default`.
  On an Android or iOS build target the Build Panel walks the scene's root objects and raises one YELLOW
  warning per shader outside that list - `World uses unsupported shader 'X'. This could cause low
  performance or future compatibility issues.` **Yellow does not block, there is no fix callback, and the
  wording is "could", so this is a performance hint and not a permission.** The cost is real and must be
  profiled on the device; the permission is not in question. Never substitute this warning for a
  measurement, and never tear out a working shader because of it.
  (`WorldValidation.cs:531-546`, `VRCSdkControlPanelWorldBuilder.cs:693-705`, SDK 3.10.5)
- **And that whitelist has a typo that makes the SDK warn about the SDK's own recommended shader.** The
  supersampled UI shader declares itself `VRChat/Mobile/Worlds/Supersampled UI` and the Build Panel looks
  it up under that exact spelling, while the whitelist lists `VRChat/Mobile/World/Supersampled UI` -
  SINGULAR. So a world that took the SDK's own supersampled-UI advice gets warned at on every Android
  build, and the warning means nothing. (`VRChat-Mobile-SupersampledUnlit.shader:4`,
  `VRCSdkControlPanelWorldBuilder.cs:708`, `WorldValidation.cs:543`, SDK 3.10.5)
- **Fog shader stripping must stay Custom if the world changes Fog Mode at runtime.** The Build Panel
  warns whenever `CheckFogSettings` sees Custom, and its Auto Fix calls
  `EnvConfig.SetFogSettings(FogStrippingMode.Automatic)` - correct for most worlds, and wrong for
  one that switches fog mode from Udon, because Automatic strips the variants it would switch TO. A
  stripped variant is not a build error; it is an effect that silently does not appear in the uploaded
  world. This is the one Auto Fix whose safety depends on a fact only the author knows
  (`conduit-build-validation.md`). (`VRCSdkControlPanelWorldBuilder.cs:558-567`, SDK 3.10.5)
- **Alpha is the real mobile cost, and it compounds.** Tiled mobile GPUs are fill-rate bound, so
  overlapping transparent surfaces are shaded repeatedly and the cost grows with the overlap, not with the
  object count. Cutout beats Transparent wherever the look survives it.
- **Blend modes are fixed-function and run everywhere.** Premultiplied alpha is `Blend` state, not a grab,
  so a transparency mode built on `Blend [_SrcBlend] [_DstBlend]` is not a Quest concern.

## Invalid HDR pixels and bloom flashes

- **Capture an actual failing frame before isolating an intermittent flash.** A clean screenshot cannot
  establish its absence. In a paused Built-in Forward capture, one white point in the first bloom
  downsample expanded into a large white orb; disabling MSAA did not remove it. (Unity 2022.3.22f1)
- **Compare `PostProcessLayer.stopNaNPropagation` off/on/off at the same paused view.** Removal and return
  discriminate invalid scene pixels from ordinary finite glare. The installed post-processing
  `CopyStd.shader` filters `AnyIsNan`; this control does not identify the producing shader or operation.
- **Isolate the receiving material's extra-light pass while preserving geometry and shadow casters.**
  `Material.SetShaderPassEnabled` takes the LightMode tag `ForwardAdd`, not a pass named `FORWARD_ADD`.
  The latter was a no-op control. Restore the pass after comparing the captured frame.
- **Check each implicated shader family and its shared layer passes.** Separate captured flashes
  originated in terrain and coated-mesh extra-light paths. Keeping only the mesh guards while removing
  terrain guards reintroduced the flash; both protections were needed for the successful retest.
- **Classify non-finite values by their float bits when fast-math compromises a floating test.**
  For `float4 value`, `any((asuint(value) & 0x7fffffffu) >= 0x7f800000u)` detects NaN and infinity.
  FXC warned that an `isnan` value could not be NaN; bit tests were used instead. Use `precise` where
  needed to preserve the measured arithmetic, and validate the compiled path. (Unity 2022.3.22f1/D3D11)
- **Guard incoming lighting and final output after weighting and fog.** Give an invalid additive
  contribution a finite zero result before HDR blending; retain finite HDR values and existing shader
  features. Multiplying an invalid result by zero does not sanitize it: `0 * NaN` remains NaN.
- **Inspect native spotlight sampling before assigning the fault to custom material maths.** In Unity
  2022.3.22f1, `AutoLight.cginc`'s `UNITY_LIGHT_ATTENUATION` evaluates the shadow before its front-of-light
  multiplier; `UnityShadowLibrary.cginc`'s soft `UnitySampleShadowmap` divides by projection `w` before
  `UnitySampleShadowmap_PCF3x3Tent`. Those source facts alone do not prove which operation produced NaN.
- **Retain the working validity guards when removing diagnostic colours.** Retest moving through the
  failing angles with soft shadows, authored bloom/HDR and material features restored, and the camera
  NaN filter back at its baseline. Shader containment succeeded with that filter off and soft shadows on;
  a successful guard establishes containment, not the exact arithmetic source of the invalid value.

## Mirrors break every view-dependent effect unless you ask VRChat where the camera is

- **`_WorldSpaceCameraPos` is wrong inside a VRChat mirror - read `_VRChatMirrorCameraPos` INSTEAD.**
  VRChat sets two shader globals every frame: `float _VRChatMirrorMode`, non-zero while rendering into a
  mirror, and `float3 _VRChatMirrorCameraPos`, the mirror camera's world position. Anything derived from
  the view vector - specular, fresnel, rim, matcap, parallax, a billboard's facing - is computed against
  the player's head in the mirror pass unless it branches, so the effect looks right in the world and
  visibly wrong in the reflection, which is a bug nobody sees until someone stands at a mirror.
  **This is not folklore: the SDK's own shipped shader does exactly this.**
  (`Sample Assets/Shaders/Mobile/ToonStandard/CG/Helpers.cginc:134-148`, SDK 3.10.5)
- **The stereo case is the same branch, and it is easy to get half right.** That same helper averages
  `unity_StereoWorldSpaceCameraPos[0]` and `[1]` when `USING_STEREO_MATRICES` is defined and falls back to
  `_WorldSpaceCameraPos` otherwise - and the mirror value overrides BOTH. A shader that handles stereo but
  not mirrors still breaks in a mirror.
- **`_VRChatCameraMode` is real, and SDK source is the wrong place to look for it.** VRChat INJECTS its
  shader globals at runtime, so one shows up in source only where some shader happens to DECLARE it -
  which makes absence from the SDK no evidence of absence at all. A shipped third-party shader declares
  `float _VRChatCameraMode;` and branches on it both ways, so it is available; that is also the test to
  repeat for any injected global - search every shader tree the project has, never the SDK alone.
  **The value table is inherited and uncited** - 0 normal, 1 VR handheld, 2 desktop handheld, 3
  screenshot - though a `> 0` / `== 0` split is the usage seen in the wild and is consistent with 0
  meaning the player's own eyes. Treat `== 0` as safe and the individual non-zero values as unverified.

## Shaders get VRChat's clock, including the NETWORK one

- **The SDK ships a shader time library and nothing in these documents or any third-party reference
  mentions it: `Packages/com.vrchat.base/ShaderLibrary/VRCTime.cginc`.** `#include` it and five functions
  are available with no Udon involvement at all - no synced variable, no material property, no
  `SetFloat` from a behaviour every frame:
  `VRC_GetNetworkTimeInMilliseconds()`, `VRC_GetUTCUnixTimeInSeconds()`,
  `VRC_GetUTCTime(out h, out m, out s, out ms)`, `VRC_GetLocalTime(out h, out m, out s, out ms)` and
  `VRC_GetTimezoneOffsetSeconds()`. The four globals behind them are `_VRChatTimeUTCUnixSeconds`,
  `_VRChatTimeNetworkMs`, `_VRChatTimeEncoded1` and `_VRChatTimeEncoded2`, the last two bit-packed and
  unpacked for you by those helpers - read the fields through the FUNCTIONS rather than re-deriving the
  shifts. (`VRCTime.cginc:1-43`, SDK 3.10.5)
- **ONLY `VRC_GetNetworkTimeInMilliseconds()` is the same for every player. The rest are per-client.**
  `VRC_GetLocalTime` and `VRC_GetTimezoneOffsetSeconds` read the VIEWER'S OWN wall clock and timezone, so
  a shader animation driven by either runs at a different phase for everyone and there is no error, no
  traffic and nothing to inspect - the same per-client trap `conduit-player.md` documents for identity
  flags, arriving here in a shader. **A synced visual effect drives off the network clock; a
  "what time is it where you are" effect deliberately uses local.** UTC sits in between: agreed between
  players, but a wall clock rather than an instance clock.
- **This is how a synced shader effect avoids syncing anything.** A pulse, a sweep, a day-night cycle or a
  scrolling pattern keyed to `VRC_GetNetworkTimeInMilliseconds()` agrees across the instance by
  construction and costs ZERO bandwidth, which beats an Udon behaviour pushing a float into a material -
  and it keeps working for a late joiner with no restoration step, because the clock was never state.
  `conduit-networking.md` makes the same argument for behaviours; this is its shader-side twin.
- **Mind the width: `_VRChatTimeNetworkMs` is a `uint` of MILLISECONDS, so it wraps about every 49.7
  days**, and any effect that subtracts two stamps across the wrap jumps. That is the same class of edge
  as the 32-bit millisecond wrap on the Udon side (`conduit-video-players.md`), and an always-on world is
  again the case that reaches it. Prefer differences over short windows to absolute values.

## Looping a playhead off `_Time`

- **Wrap the clock into ONE loop before scaling it, then clamp the frame index into the loop's own
  range.** `fmod(_Time.y * rate + offset, frames)` breaks once the editor or world has run a while: the
  sum reaches hundreds of thousands, the GPU's `fmod` loses precision there, and right at each wrap it
  returns a hair below 0 or exactly `frames`. Write
  `frac((_Time.y + offset / rate) / (frames / rate)) * frames`, clamp `floor()` of it to
  `[0, frames - 1]` and the next frame back to 0 at the end. (Unity 2022.3.22f1, Built-in)
- **The symptom is a one-frame pop per loop, never an error - and with many instances it reads as
  popcorn.** A GPU-skinned crowd reading clips stacked in one animation texture showed every person
  flashing a neighbouring clip's pose once per loop, each at their own offset. The frame index had stepped
  outside its clip and read the next clip's rows.
- **A single-instance probe can pass while the scene fails.** Rendering one person through the unfixed
  shader at quarter-frame steps over every clip showed no bad pose, because the window at the wrap is
  narrower than the step. Thousands of instances cross it constantly. Prove the fix by construction
  (clamped indices), not by a sampling probe.

## Feature switches: dynamic branch keywords still multiply on these Unity versions

- **On Unity 2022.3 before 2022.3.40 - which includes VRChat's 2022.3.22f1 - a `dynamic_branch` /
  `dynamic_branch_local` keyword is compiled into the build as a variant like any `multi_compile`.** The
  manual says dynamic branch keywords add no variants; that is the intended behaviour, and these versions
  do not have it. Unity issue UUM-48599 ("Dynamic Branching generates Shader Variants when building the
  Project"): expected none, actual "over one million"; reproduced on 2022.1.0a15 through 2023.3.0a5, fixed
  on the 2022.3 line, listed Open for 6.0.X. Unity 2019 and 2021 have no dynamic branch keywords at all,
  so the pragma is not an option there either.
- **Specimen (2026-10-31): 16 `dynamic_branch_local` switches on one car paint shader built 3,145,728
  variants for its forward pass alone (48 lighting, fog and instancing variants x 2^16)**, took over 11
  minutes just to enumerate, held about 94 GB of memory and filled the page file. The source and the
  shader's own README both said "one variant". Nothing errored; the build just never finished.
- **On these versions a feature switch is a plain float property tested with `if`, never a keyword of any
  kind.** Declare `half _UseDamage;` and branch on `_UseDamage > 0.5`: that is the uniform branch the
  dynamic keyword was meant to give, and nothing exists for the build to multiply. Use `[ToggleUI]` on the
  property, not `[Toggle(KEYWORD)]`, and switch it from a script with `SetFloat`. Keeping the old keyword
  names as macros (`#define _DAMAGE (_UseDamage > 0.5)`, after every `#include` so no Unity library sees
  them) leaves existing `if (_DAMAGE)` code untouched.
- **Read the build's own count before believing any variant claim.** The editor log prints, per pass,
  `Full variant space`, `After scriptable stripping` and the compiled and cache-hit totals; a power of two
  times the lighting variants is the signature of keywords multiplying. Re-check on Unity 6 before trusting
  dynamic branch keywords there.

## Claiming a platform limitation

- **Never state that something "does not work on Quest" without naming the construct and citing where you
  read it.** A vague platform warning attached to working code is worse than silence: it gets correct work
  torn out. Name the exact pass, pragma or API, and quote the line.
- **A platform limitation recalled from memory is UNVERIFIED.** Verify it against the shader source in the
  project, the vendor's own mobile variant, or a build - never from priors. See `conduit-diagnostics.md`
  for the evidence tiers.
