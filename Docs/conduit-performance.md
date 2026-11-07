# Udon's cost model, and writing for it

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before writing an `Update`, a cross-behaviour call, or anything that processes
> more than a handful of items at once.**
>
> **This file owns what UDON costs to RUN, and nothing else goes in it.** What the SCENE costs to render
> is `conduit-rendering-cost.md`, which draws the same line from its own side; what a SYNC costs in
> bandwidth is `conduit-networking.md`. Whether a member exists or compiles at all is
> `conduit-udonsharp.md` - this file only answers what it costs once it does. A quality or camera setter
> listed here is here for its performance lever; the surface itself belongs to its own subject.

## What Udon actually costs

- **Udon runs on the order of 200x to 1000x slower than the equivalent C#.** That is UdonSharp's own
  documentation, not a community estimate, and it is why the SHAPE of a behaviour matters here far more
  than it would in a normal Unity project: work that is free in C# is not free in Udon, and a loop that
  would never appear in a profile elsewhere can own a frame. Measure before optimising, but assume the
  budget is small. (UdonSharp performance pointers)

## Method visibility is a cost, not only a security boundary

- **EVERY `public` method on a behaviour makes that behaviour's event dispatch slower.** Udon resolves a
  called method by NAME, so the lookup scales with how many public methods the behaviour has - UdonSharp's
  docs put it plainly: "the fewer public methods you have, the better performance-wise" and "prefer to
  keep methods as private if they are not being called from other scripts". A method left public "for
  consistency" or "for later", with no Inspector wiring, no `SendCustomEvent` caller and no
  `[NetworkCallable]`, is pure cost for nothing.
- **This points the same way as the security rule, which is why it is worth doing.** `conduit-networking.md`
  says every unprefixed public method is a remote entry point; this says every public method is also a
  dispatch tax. Private is both faster and safer, so the default is private and `public` is something a
  method has to earn.

## Cross-behaviour calls, and the free way to split a file

- **A call into ANOTHER behaviour costs about 1.5x a call inside the same one** - benchmarked at roughly
  1.04 ms against 0.68 ms per thousand calls - because Udon routes it through `SendCustomEvent`
  internally. That is a reason to keep a tight loop inside one behaviour, not a reason to build one
  god-behaviour: reserve cross-behaviour calls for genuinely separate systems.
- **`partial` classes work in UdonSharp and cost nothing - this is the way to split a large behaviour.**
  Verified here: a `public partial class X : UdonSharpBehaviour` in one file, a `public partial class X`
  in a second holding a private field and a private method, and `Start()` calling across - compiles
  clean, and `udon-program | methods` shows the second file's method compiled INTO the program with its
  externs intact. It is benchmarked at the same 0.68 ms as a single file. So a 113-field controller
  becomes `Thing.cs`, `Thing.UI.cs`, `Thing.Sync.cs` with no runtime penalty at all.
  (SDK 3.10.5)
- **The one gotcha is the ordinary C# one:** every declaration needs the `partial` keyword, and missing it
  on the primary file fails with `CS0260: Missing partial modifier on declaration of type 'X'; another
  partial declaration of this type exists` - a Roslyn error, so it never reaches UdonSharp.

## Update, and not running it

- **A disabled component beats an `Update` that returns early, by about six times.** Across 100 inactive
  gimmicks the early-return form measured 0.0745 ms against 0.0122 ms for a disabled handler: the guard
  is cheap, but Udon paying to DISPATCH `Update` into 100 behaviours is not. Put the per-frame work in a
  small dedicated behaviour and toggle its `enabled` from the manager, rather than guarding a loop that
  runs regardless.
- **Many behaviours each running a short self-rescheduling `SendCustomEventDelayedSeconds` loop is
  worse than one `Update`.** Their firings converge onto the same frames and produce spikes that a
  steady-state average hides completely. Give periodic work to ONE coordinator that accumulates
  `Time.deltaTime`, and disable that coordinator when idle. A sparse, single-instance, low-frequency timer
  is still the right use of a delayed event.
- **`PostLateUpdate` runs AFTER the camera's final pose is resolved**, so anything that must track the
  viewer - billboards, head-locked UI, HUD overlays - belongs there and is one frame stale in `Update`.
  `conduit-udonsharp.md` covers the `EventTiming` argument that places a DELAYED event the same way.

## Slicing heavy work across frames

- **`System.Diagnostics.Stopwatch` is fully exposed**, which is the missing piece given Udon has no
  coroutines and no `async`: the registry carries the constructor, `Start`, `Stop`, `Reset`, `Restart`,
  `StartNew`, `Elapsed` (a `TimeSpan`), `ElapsedMilliseconds`, `ElapsedTicks`, `IsRunning`, `Frequency`
  and `IsHighResolution`. (SDK 3.10.5)
- **The pattern: do one unit of work, check elapsed against a budget, and either continue or hand the
  frame back** with `SendCustomEventDelayedFrames(nameof(_Continue), 1)`. VR targets 90 FPS, so a frame is
  about 11.1 ms; a budget near 20 ms tolerates a small overrun without dropping two frames in a row.
  Check AFTER each unit, not before, and keep units small so the overshoot stays bounded.
- **`Stopwatch` is wall-clock and keeps counting between frames - reset it when the work resumes.**
  Forget to, and the elapsed time includes the whole inter-frame gap, so the budget reads as instantly
  exhausted and the job advances exactly one unit per frame forever. That presents as "the slicing works
  but it is unusably slow", which is a very hard symptom to attribute back to the timer.

## Cancelling a scheduled callback

- **`SendCustomEventDelayedSeconds` AND `DelayedFrames` cannot be cancelled - they return void and hand
  back no handle, so every event they schedule WILL fire.** The control is therefore a guard the target
  checks on arrival: a `_shouldRun` bool, or a generation counter the scheduler bumps so a stale callback
  recognises itself and returns. A self-rescheduling loop is stopped by letting the next firing run and do
  nothing.
- **But that is not the only scheduler - `VRCTween.DelayedCall` is cancellable and is the right tool
  when the callback must not run.** `VRCTween.DelayedCall(IUdonEventReceiver, string, float)` returns a
  `VRCTweenHandle`, and `VRCTweenHandle.Kill()` stops it before it fires. Reach for it whenever the
  callback has irreversible side effects - a network event, audio, a state change - because a soft guard
  still ENTERS the method and only then returns, which is fine for a debounce and not fine for anything
  that must genuinely not happen. `conduit-vrctween.md` owns the handle API.
  (SDK 3.10.5)

## Struct copies allocate

- **Every time Udon MOVES a struct between two variables it boxes it on the managed heap: about 28 bytes
  for a `Vector3`, more for `WheelHit`.** Measured per op in a `FixedUpdate` probe: `_v = _v3b` allocated
  56 KB over 2,000 copies; a U# method returning a `Vector3` cost 56 bytes a call (two copies); an `out
  Vector3` helper 84 bytes (three). Int, float and bool copies, `Vector3`/`Vector2`/`Quaternion` maths,
  transform reads, array get and set, struct member sets and engine calls (`GetGroundHit`,
  `AddForceAtPosition`, the friction curve get/set, `ProjectOnPlane`) allocated nothing. (SDK 3.10.5)
- **The cause is the `COPY` instruction: an extern writes its result straight into the destination slot,
  while a plain move compiles to `PUSH src, PUSH dst, COPY`.** U# emits that `COPY` for every struct
  passed as a parameter to, returned from or written through `out` by a method of the behaviour; for
  `a = b` between variables; for a ternary whose branch is a plain variable or constant (`x = c ? v :
  Vector3.zero`); for a struct literal assigned to a local; for a struct LEFT operand kept in a temp
  because a U# call follows it in the same expression (`_tensor * Scale()`); and for writing one FIELD of a
  struct variable (`v.y = 0f`). A property setter (`hit.force = x`) works in place and copies nothing, and
  `v = new Vector3(v.x, 0f, v.z)` replaces the field write: the constructor writes straight into `v`.
- **Never use `Vector3.Set(...)` on a FIELD to avoid a copy - it compiles, runs without error and changes
  nothing.** Traffic cars placed through `_placePosition.Set(...)` stayed at the stale value and were
  despawned the frame they spawned (0-1 of 15 alive); `_placePosition = new Vector3(x, y, z)` restored all
  15. (SDK 3.10.5)
- **Keep the hot path free of them.** Have helpers write results into fields, or hand back float pairs,
  instead of returning or `out`-ing a struct; pass an index and read the array element inside rather than
  passing the element; replace `a = b` with `a = new Vector3(b.x, b.y, b.z)`; branch with `if` so each
  side is an extern; hoist a U# call out of a struct expression into a local float first. A vehicle
  physics step went from about 13.5 KB of garbage a step to about 0.6 KB this way, and ran about 10%
  faster, with every formula unchanged. A copy on a branch that rarely runs costs nothing until it runs,
  so clear the per-frame path first.
- **Find them in the compiled program, not the source:** `udon-program <behaviour> | raw`, then count
  `COPY` instructions whose source symbol's `.data` type is a struct (`%UnityEngineVector3` and the
  like), grouped by the `# Type.Method(` comment above them. The symbol names map straight back to C#:
  `__0_hit__param` is a parameter, `__0___0_Foo__ret` a return value, `__lcl_x_...` a local.

## Caching, and what cannot be cached

- **Hash animator parameter names ONCE with `Animator.StringToHash` and use the integer overloads.** The
  string overload resolves the name on every call. **The cache must be an INSTANCE field assigned in
  `Start()`** - `static` fields do not compile on a behaviour and a field initializer runs at compile
  time, both covered in `conduit-udonsharp.md`. Rule of thumb: hash any name used more than once a second.
- **Cache `Networking.LocalPlayer` and `GetComponent` results in `Start()`** rather than re-fetching them
  per frame.
- **Allocation inside `Update` is a real cost in Udon, and the player list is where it usually
  happens.** `new VRCPlayerApi[VRCPlayerApi.GetPlayerCount()]` before every `GetPlayers(...)` builds a
  fresh array every frame for a count that barely moves. Keep ONE buffer as an instance field, grow it
  only when the count exceeds its length, and grow it with headroom so the reallocation is rare. String
  building in a loop is the same shape - `result += x` allocates a new string per iteration, which is what
  `StringBuilder` is for (`conduit-udonsharp.md`).
- **But "the Udon heap" is not where those arrays live, and per-frame allocation cannot exhaust it.**
  `VRC.Udon.Common.UdonHeap` is a table of VARIABLE SLOTS addressed by `UInt32` and sized at compile time
  - `DEFAULT_CAPACITY` 512, `MAXIMUM_CAPACITY` 1,048,576. A runtime `new int[10000]` puts ONE reference
  into ONE slot that already exists, so the ceiling counts what a behaviour DECLARES and nothing real
  approaches it. Allocate less because it costs time, never because the heap will fill.
  (`VRC.Udon.Common.UdonHeap`, SDK 3.10.5)

## Rendering quality is adjustable from Udon

- **`VRCQualitySettings` is a real, sizeable surface and almost nobody uses it.** READ: `AntiAliasing`,
  `LODBias`, `MaximumLODLevel`, `PixelLightCount`, `ShadowCascades`, `ShadowDistance`, `ShadowResolution`,
  `ShadowCascade2Split`, `ShadowCascade4Split`, `ShadowmaskMode`, `RealtimeReflectionProbes`,
  `VSyncCount`. **WRITE:** `RealtimeReflectionProbes`, `ShadowmaskMode`, `ShadowCascade2Split`,
  `ShadowCascade4Split`, plus `SetShadowDistance(float)`, the four-float cascade overload, and
  `ResetShadowDistance()`. `OnVRCQualitySettingsChanged` fires when the player changes their settings.
  That is enough to drop shadow distance in a heavy room and restore it on the way out, rather than
  budgeting for the worst case everywhere. (SDK 3.10.5 registry)
- **The player's CAMERA is writable too, which is the bigger lever.** `VRCCameraSettings.ScreenCamera`
  exposes setters for `CullingMask`, `LayerCullDistances`, `LayerCullSpherical`, `NearClipPlane`,
  `FarClipPlane`, `UseOcclusionCulling`, `ClearFlags`, `BackgroundColor`, `DepthTextureMode`, `AllowHDR`
  and `AllowMSAA`. **`LayerCullDistances` is the one to reach for first**: a per-layer distance array
  culls small props far sooner than the far plane without touching the far plane, so detail disappears at
  10 m while the skybox still reaches the horizon. `CullingMask` hides whole layers from the player's view
  outright - build that mask by NUMBER, per `conduit-layers.md`. (SDK 3.10.5 registry)
- **The two newest writes silently do nothing on an older client.**
  `RealtimeReflectionProbes` and `ShadowmaskMode` need client 2026.3.2 or newer, and below it the call is
  a no-op with no compile error and no runtime signal - so the world must still look correct when the
  write does not take. `ShadowmaskMode` is PC only regardless. (VRChat SDK release notes, 3.10.5-beta.1)

## Finding objects

- **`GameObject.Find` is not banned, but it is O(scene) and its cost is entirely about WHEN you call it.**
  A `[SerializeField]` wired in the Inspector costs nothing at runtime; a `Find` cached once in `Start()`
  costs one scan at load, and is the right answer for a target the Inspector cannot reach; a `Find` inside
  `Update` scans the whole scene every frame.
- **The stronger argument against `Find` is not speed, it is silence.** It couples behaviour to a string
  literal, so renaming the target returns `null` with no error and no warning. A serialized reference
  breaks VISIBLY in the Inspector as `Missing`, before anyone joins the world.

## Two string-keyed APIs that fail silently

- **`SetProgramVariable(string, object)` on `IUdonEventReceiver` writes a field on another behaviour by
  NAME, and a mismatched name simply does nothing.** No error, no warning - the receiver just sees the
  old value, which usually reads as null. It is genuinely useful for handing a value across before a
  `SendCustomEvent`, but the field name is an unchecked contract between two files: keep the literal in a
  `const` next to the field it targets. (SDK 3.10.5)
- **`SendCustomEvent` has the same shape** - a method name that no longer matches is not a compile error.
  `nameof(...)` costs nothing and turns both into checked references wherever the target is visible.

## Two more shapes worth knowing

- **Platform detection: `IsUserInVR()` is stable, `InputManager.GetLastUsedInputMethod()` is not.** The
  latter returns the last DEVICE the player used and changes mid-session when they pick up a controller,
  so it is fine for adapting a prompt and wrong for a one-time branch in `Start()`. Both are exposed.
- **There is no usable `Predicate<T>`, so filter an array by hand:** allocate a temp array of the input's
  length, walk once packing matches with a count cursor, then `Array.Copy` into a final array of exactly
  `count`. State this as impractical rather than blocked - the exposure table moves between SDKs, and the
  manual loop is the faster shape regardless.
