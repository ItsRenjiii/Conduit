# VRCTween

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before writing any tween, before reaching for DOTween, and before hand-rolling
> a Lerp in Update.**
>
> **This file owns VRCTween and nothing else goes in it.** An AnimationClip or an AnimatorController is
> `conduit-animator.md` - both animate things over time, which is the whole confusion. Why an `Update` is
> expensive in the first place is `conduit-performance.md`; only the tween-shaped alternative belongs
> here. Sync and ownership are `conduit-networking.md`, and the networking section below is the local
> pattern ONLY - it takes synced intent as given and says nothing about how to sync it.

VRCTween is VRChat's Udon-safe facade over a subset of DOTween, introduced in SDK 3.10.4. It is the
ONLY tweening available to Udon: DOTween itself ships in `com.vrchat.base` but is NOT whitelisted, so a
`DG.Tweening` call passes the Roslyn stage and is then rejected by the second stage with
`Method is not exposed to Udon`.

This document was written against the DLL actually installed in this project, then cross-checked against
VRChat's published documentation. The installed DLL is the authority for implementation details; the
official documentation is the authority for the supported public contract.

## Sources

Installed files inspected:

- `Packages/com.vrchat.worlds/package.json`
- `Packages/com.vrchat.worlds/Runtime/VRCSDK/Plugins/VRCSDK3.dll`
  - product commit: `e4b3f37706c622368b21d809e52611542ac6f142`
  - SHA-256: `8F78271672236E2DD7E582F064A36693821A56F836678F183511AFFFC7BDFCD1`
- `Packages/com.vrchat.base/Runtime/VRCSDK/Plugins/DOTween/DOTween.dll`
  - SHA-256: `48EA88B9FC7D95FC04AE333E7DF4FDEED865EC39629382E5524F5CDC32E7DD4E`
- `Packages/com.vrchat.base/Runtime/VRCSDK/Plugins/DOTween/DOTween.XML`

Installed types decompiled and inspected completely:

- `VRC.SDK3.Components.VRCTween`
- `VRC.SDK3.Components.VRCTweenHandle`
- `VRC.SDK3.Components.VRCTweenRunner`
- `VRC.SDK3.Components.VRCTweenPostLateUpdater`
- `VRC.SDK3.Components.VRCTweenSafety`
- the four VRCTween enums, plus the corresponding DOTween enums and relevant control code

Official documentation:

- [VRCTween overview](https://creators.vrchat.com/worlds/udon/vrctween/)
- [Built-in tween types](https://creators.vrchat.com/worlds/udon/vrctween/tween-types/)
- [Virtual tweens](https://creators.vrchat.com/worlds/udon/vrctween/virtual-tweens/)
- [Settings and control](https://creators.vrchat.com/worlds/udon/vrctween/settings/)
- [SDK 3.10.4 release notes](https://creators.vrchat.com/releases/release-3-10-4/)

VRCTween was introduced in SDK 3.10.4. This project is already on the later
`3.10.5-beta.1` package.

---

## 1. What VRCTween actually is

VRCTween is VRChat's Udon-safe facade over a selected subset of DOTween. You use the public
types in:

```csharp
using VRC.SDK3.Components;
```

Do not write Udon code directly against `DG.Tweening`. VRCTween is the supported exposed
surface for UdonSharp and Udon Graph.

On the first VRCTween call, VRChat lazily creates a hidden GameObject named
`_VRCTweenRunner`. In play mode it is marked `DontDestroyOnLoad`. The runner initializes
DOTween with safe mode enabled and error-only logging, owns a dictionary of active tweens,
and assigns monotonically increasing integer handle IDs beginning at 1.

Normal creation flow:

1. Validate target, duration, and value.
2. Create the corresponding DOTween tween.
3. Apply the requested ease.
4. disable DOTween auto-kill;
5. install VRChat's completion callback;
6. store it in the runner and return a `VRCTweenHandle`.

Tweens **auto-play immediately** unless you chain `.Pause()` during the creation frame.
Most settings must therefore be chained directly onto the creation expression, before the
next Unity update.

When a finite tween completes, VRChat sends its optional Udon completion event, kills the
underlying DOTween tween, and removes it from the runner. Infinite tweens and tweens sitting
rewound at position zero stay alive until killed.

On scene unload the runner kills every tracked tween and clears DOTween's cached tweens.

VRCTween is **entirely local**. Creating a tween on one player does not create it on anyone
else and does not grant networking or ownership semantics.

---

## 2. Smallest correct UdonSharp example

```csharp
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components;

public class DoorTween : UdonSharpBehaviour
{
    [SerializeField] private Transform door;
    [SerializeField] private Vector3 openLocalPosition;

    private VRCTweenHandle _move;

    public void Open()
    {
        _move.Kill();

        _move = door.TweenLocalPosition(
                openLocalPosition,
                0.6f,
                VRCTweenEase.OutCubic)
            .OnComplete(this, nameof(OnDoorOpened));

        if (!_move.IsValid)
            Debug.LogError("VRCTween rejected the door tween inputs.");
    }

    public void OnDoorOpened()
    {
        Debug.Log("Door opened locally.");
    }

    private void OnDestroy()
    {
        _move.Kill();
    }
}
```

Callback methods invoked through `OnComplete`, `OnRewind`, `DelayedCall`, or a virtual
tween's update event must be public Udon custom events.

---

## 3. Complete creation API

Every creation method returns `VRCTweenHandle`. No parameter shown below is optional.

### Transform and GameObject

All seven methods exist as extensions on both `GameObject` and `Transform`:

```csharp
TweenPosition(Vector3 to, float duration, VRCTweenEase easeType)
TweenLocalPosition(Vector3 to, float duration, VRCTweenEase easeType)
TweenRotation(Vector3 to, float duration, VRCTweenEase easeType)
TweenLocalRotation(Vector3 to, float duration, VRCTweenEase easeType)
TweenScale(Vector3 to, float duration, VRCTweenEase easeType)
TweenPath(Vector3[] waypoints, float duration, VRCTweenPathType pathType,
          bool closePath, int resolution, VRCTweenEase easeType)
TweenLocalPath(Vector3[] waypoints, float duration, VRCTweenPathType pathType,
               bool closePath, int resolution, VRCTweenEase easeType)
```

Exact behaviour:

- `TweenPosition`: writes world `Transform.position`; snapping is disabled.
- `TweenLocalPosition`: writes `Transform.localPosition`; snapping is disabled.
- `TweenRotation`: takes world Euler angles and uses DOTween's `RotateMode.Fast`, normally
  choosing the shortest rotation route.
- `TweenLocalRotation`: the local Euler equivalent, also `RotateMode.Fast`.
- `TweenScale`: writes `Transform.localScale`; Unity has no writable world-scale property.
- paths move position only. They do not orient the object to the path.
- paths use full 3D mode, no axis locks, and no snapping.
- a path needs at least two waypoints. The transform's position when the tween starts is the
  path start; the supplied array contains the destinations to travel through.
- `closePath=true` closes the path back to its starting point.
- path resolution is clamped to 1-50. Resolution has no useful effect on a linear path;
  higher Catmull-Rom resolution is smoother but costs more CPU. DOTween recommends 10 as a
  general default and often 5 for undramatic curves.

### Static virtual values and delayed actions

```csharp
VRCTween.TweenFloat(
    float from, float to, float duration,
    IUdonEventReceiver callback, string variableName, string onUpdate,
    VRCTweenEase easeType)

VRCTween.TweenInt(
    int from, int to, float duration,
    IUdonEventReceiver callback, string variableName, string onUpdate,
    VRCTweenEase easeType)

VRCTween.TweenColor(
    Color from, Color to, float duration,
    IUdonEventReceiver callback, string variableName, string onUpdate,
    VRCTweenEase easeType)

VRCTween.TweenVector3(
    Vector3 from, Vector3 to, float duration,
    IUdonEventReceiver callback, string variableName, string onUpdate,
    VRCTweenEase easeType)

VRCTween.DelayedCall(
    IUdonEventReceiver callback, string eventName, float delay)

VRCTween.DelayedSetActive(
    GameObject target, bool value, float delay)
```

Virtual-tween variables must be public fields on the receiving UdonBehaviour and must have
the exact expected type. `[System.NonSerialized]` is sensible for these transient values.

Each virtual update does this in order:

1. calculate the new value;
2. call `SetProgramVariable<T>(variableName, value)`;
3. call `SendCustomEvent(onUpdate)`.

The installed implementation only performs steps 2 and 3 when `onUpdate` is non-null and
non-empty. An empty update-event name therefore means the variable is not updated either.

`TweenInt` is internally a float tween and applies `Mathf.RoundToInt` every update.
`TweenColor` and `TweenVector3` internally tween a normalized float from 0 to 1 and feed it
to `Color.Lerp` or `Vector3.Lerp`. Those Unity `Lerp` functions clamp the interpolation
factor, so Back/Elastic/custom-curve overshoot is flattened for these two virtual types.
Float and int virtual tweens can overshoot.

`DelayedCall` and `DelayedSetActive` explicitly use unscaled time. Ordinary tweens use
scaled time; VRCTween exposes no general `SetUpdate(..., isIndependentUpdate)` option.

`DelayedSetActive` checks that its target still exists when the delay expires. If destroyed,
it skips `SetActive` and completes cleanly.

### AudioSource

```csharp
audioSource.TweenVolume(float to, float duration, VRCTweenEase easeType)
audioSource.TweenPitch(float to, float duration, VRCTweenEase easeType)
```

The wrapper accepts any finite destination. It does not enforce conventional AudioSource
ranges; Unity may clamp or interpret values itself.

### Unity UI

```csharp
canvasGroup.TweenFade(float to, float duration, VRCTweenEase easeType)
graphic.TweenColor(Color to, float duration, VRCTweenEase easeType)
graphic.TweenFade(float to, float duration, VRCTweenEase easeType)
slider.TweenValue(float to, float duration, VRCTweenEase easeType)
rectTransform.TweenAnchorPos(Vector2 to, float duration, VRCTweenEase easeType)
rectTransform.TweenSizeDelta(Vector2 to, float duration, VRCTweenEase easeType)
```

`Graphic` includes `Image`, legacy `Text`, `RawImage`, and derived UI graphics. Anchor and
size tweens have snapping disabled. The wrapper does not clamp alpha, slider value, anchor,
or size destinations; the Unity component may impose its own rules.

### SpriteRenderer

```csharp
spriteRenderer.TweenColor(Color to, float duration, VRCTweenEase easeType)
spriteRenderer.TweenFade(float to, float duration, VRCTweenEase easeType)
```

### Renderer material properties

```csharp
renderer.TweenColor(Color to, float duration, VRCTweenEase easeType)
renderer.TweenColor(string property, Color to, float duration, VRCTweenEase easeType)
renderer.TweenFloat(string property, float to, float duration, VRCTweenEase easeType)
```

The no-property colour overload uses `_Color`.

Renderer tweens use one `MaterialPropertyBlock`; they do **not** instantiate or mutate
`renderer.material`. At creation, the initial value comes from the existing property block
if that block already has the property, otherwise from `renderer.sharedMaterial`. Every
update re-reads the current block, changes only the requested property, and writes it back.

Consequences:

- the shader property must exist and the renderer needs a usable shared material;
- multiple tweens targeting the same property fight; the final updater that frame wins;
- external code writing the same property block/property also fights the tween;
- different properties are normally preserved because the block is re-read every update;
- the overload does not select a per-material index on a multi-material renderer.

The custom renderer-colour implementation uses `Color.Lerp`, so overshooting eases are
clamped for it.

### Light

```csharp
light.TweenIntensity(float to, float duration, VRCTweenEase easeType)
light.TweenColor(Color to, float duration, VRCTweenEase easeType)
```

Light colour is implemented through `Color.Lerp`, so Back/Elastic/custom overshoot is
clamped. Intensity is an ordinary float interpolation.

### Global and per-object cleanup

```csharp
VRCTween.KillAll();
gameObject.KillAllTweens();
transform.KillAllTweens();
```

Per-object cleanup is not recursive. It kills tweens whose stored GameObject is exactly the
target, plus component tweens whose component is on exactly that GameObject.

Per-object cleanup **does include** transform, UI, audio, sprite, renderer, light, and
`DelayedSetActive` tweens. It **does not include** virtual tweens or `DelayedCall`, because
those runner entries do not store a GameObject/component target. Keep their handles and
kill them individually, or use the global `VRCTween.KillAll()` with care.

---

## 4. VRCTweenHandle

`VRCTweenHandle` is a readonly value-type wrapper around an internal integer ID. A default
handle has ID zero. Equality and hash code compare IDs; `ToString()` produces
`VRCTweenHandle(<id>)`.

### State properties

```csharp
bool  IsValid
bool  IsPlaying
bool  IsActive
float Elapsed
float Duration
bool  IsBackwards
```

Important distinctions:

- `IsValid` only means the ID is non-zero. It remains true after that tween completes or is
  killed. It is mainly a creation-validation check.
- `IsActive` asks whether the runner still has an active underlying tween.
- `IsPlaying` is false for paused, completed, killed, and invalid handles.
- `Elapsed` and `Duration` include loops but exclude delay. Infinite duration is positive
  infinity. Dead/invalid handles return zero for both.
- `IsBackwards` reports the underlying direction even if paused.

### Control methods

```csharp
void Play()
VRCTweenHandle Pause()
void Kill()
void Complete()
void Restart()
void Flip()
void PlayBackwards()
void PlayForwards()
void Goto(float to, bool andPlay)
```

- `Play`: resume without changing direction.
- `Pause`: pause and return the handle, which makes creation-time chaining convenient.
- `Kill`: stop and remove without firing `OnComplete`.
- `Complete`: jump to the final state and invoke completion callbacks. The normal completion
  cleanup then removes it.
- `Restart`: return to the beginning and play forwards, including any configured delay.
- `Flip`: toggle direction without changing play/pause state.
- `PlayBackwards` / `PlayForwards`: set direction and start playing.
- `Goto`: seek in seconds across the full loop timeline, excluding delay. NaN/Infinity is
  ignored; finite values are clamped from zero through total duration. `andPlay=false`
  leaves it paused, while true plays after seeking.

### Chainable settings

```csharp
OnComplete(IUdonEventReceiver callback, string eventName)
OnRewind(IUdonEventReceiver callback, string eventName)
SetLoops(int loops, VRCTweenLoopType loopType)
SetDelay(float delay)
SetUpdate(VRCTweenUpdateType updateType)
From()
SetSpeedBased()
SetEase(AnimationCurve curve)
SetEase(VRCTweenEase easeType)
SetDuration(float duration)
ChangeEndValue(Vector3 newEndValue, bool snapStartValue)
ChangeEndValue(Vector2 newEndValue, bool snapStartValue)
ChangeEndValue(Color newEndValue, bool snapStartValue)
ChangeEndValue(float newEndValue, bool snapStartValue)
```

Every method in this subsection returns the same handle.

Apply `OnComplete`, `OnRewind`, loops, delay, update type, `From`, and speed-based mode in the
same expression/frame that creates the tween. DOTween locks most of those settings when the
tween starts. `SetEase`, `SetDuration`, and compatible `ChangeEndValue` calls are designed to
work while active.

---

## 5. Callbacks and lifecycle details

### OnComplete

There is one stored completion callback per handle. Calling `OnComplete` again replaces the
previous callback; it does not append another.

The callback is sent before VRChat kills/removes a naturally completed tween. This permits a
completion callback to call `Restart()` on the same handle.

An infinite tween never completes naturally, so its `OnComplete` will not fire unless
`Complete()` is used and DOTween accepts completion of that configuration.

Special trap: `DelayedCall(callback, eventName, delay)` stores its supplied callback in the
same single completion-callback slot used by `.OnComplete(...)`. Chaining another
`.OnComplete(...)` onto a `DelayedCall` replaces the event passed to `DelayedCall`; it does
not add a second event.

`DelayedSetActive` performs the SetActive action inside the underlying delayed callback and
then enters VRChat's normal completion path, so adding `OnComplete` to it does not replace
the SetActive action.

### OnRewind

There is one rewind callback per handle. It fires when backwards playback reaches position
zero. It does not fire merely because `Restart()` moved the tween to the beginning.

Playing backwards to zero does not auto-kill the tween. It remains available for a later
`PlayForwards()`.

### Target destruction

VRCTween's runner is global, not a component on your animated object. Destroying the target
does not destroy the runner. DOTween safe mode protects many missing-target updates, but the
clean pattern is still to kill long-lived/infinite tweens before destroying their target.
Keep handles for virtual/delayed operations because per-GameObject cleanup cannot find them.

### Installed-beta Restart edge

Implementation detail in this exact `3.10.5-beta.1` DLL: `Restart()` unconditionally sets a
runner flag named `restartedInCallback`. At the next completion, that flag causes the normal
kill/remove step to be skipped once, even if the `Restart()` was not actually called from a
completion callback. This appears intended to preserve a tween restarted by its callback,
but the flag is broader than its name implies.

Do not build correctness around that internal quirk. Explicitly `Kill()` reusable handles
when finished, and re-check this behaviour after an SDK update.

---

## 6. Update timing and timescale

```csharp
public enum VRCTweenUpdateType
{
    Update = 0,
    LateUpdate = 1,
    FixedUpdate = 2,
    PostLateUpdate = 3
}
```

The first three values cast directly to DOTween `Normal`, `Late`, and `Fixed` update modes.
`PostLateUpdate` casts to DOTween's `Manual` mode. VRChat adds a hidden component with
`[DefaultExecutionOrder(30999)]`; its `LateUpdate()` calls:

```csharp
DOTween.ManualUpdate(Time.deltaTime, Time.unscaledDeltaTime);
```

That makes PostLateUpdate useful for writing after ordinary LateUpdate camera/IK/constraint
logic. It is not metaphysically "last": a user script with a later execution order can still
run after 30999.

Ordinary tweens are time-scale dependent. Delayed calls are created as time-scale
independent. Changing a normal tween to PostLateUpdate changes its phase, not its scaled-time
status.

`FixedUpdate` still writes Transform/component properties; VRCTween has no Rigidbody or
Rigidbody2D tween API. It is not a substitute for physics-authoritative movement.

---

## 7. Ease types

```csharp
public enum VRCTweenEase
{
    None = 0,
    Linear,
    InSine, OutSine, InOutSine,
    InQuad, OutQuad, InOutQuad,
    InCubic, OutCubic, InOutCubic,
    InQuart, OutQuart, InOutQuart,
    InQuint, OutQuint, InOutQuint,
    InExpo, OutExpo, InOutExpo,
    InCirc, OutCirc, InOutCirc,
    InElastic, OutElastic, InOutElastic,
    InBack, OutBack, InOutBack,
    InBounce, OutBounce, InOutBounce
}
```

The numeric values align exactly with the corresponding DOTween ease enum through
`InOutBounce`.

Meaning of the families:

- `Linear`: constant progress.
- `In*`: accelerates into the movement.
- `Out*`: decelerates into the endpoint.
- `InOut*`: accelerates then decelerates.
- Sine is gentle; Quad/Cubic/Quart/Quint become progressively stronger.
- Expo is aggressive; Circ is rounded.
- Back overshoots once; Elastic oscillates; Bounce bounces.

`VRCTweenEase.None` is not listed in the public ease table, but it exists in the installed
enum. It casts to DOTween `Ease.Unset`; DOTween's evaluator falls back to **OutQuad** for that
value. Use an explicit ease rather than depending on `None`.

`SetEase(AnimationCurve)` uses curve time 0-1 as tween progress and curve value as the
interpolation factor. Curves can overshoot unless the target implementation itself uses a
clamping interpolation such as the virtual Color/Vector3, renderer-colour, and light-colour
implementations described above.

---

## 8. Loops, paths, From, and speed-based motion

### Loop types

```csharp
public enum VRCTweenLoopType
{
    Restart = 0,
    Yoyo = 1,
    Incremental = 2
}
```

- `Restart`: each cycle begins from the original start.
- `Yoyo`: alternating forward/backwards cycles.
- `Incremental`: each cycle adds the previous change and continues onward.

`SetLoops(-1, ...)` means infinite. Values below -1 are normalized to -1. A value of zero is
normalized to one. Positive values are the total number of cycles, so one means one normal
playthrough.

### Path types

```csharp
public enum VRCTweenPathType
{
    Linear = 0,
    CatmullRom = 1
}
```

DOTween's experimental CubicBezier path type is not exposed by VRCTween.

### From

`From()` swaps the apparent direction so the target starts at the requested `to` value and
animates to the value it had when the tween was created. Call it immediately after creation.
The supported public contract describes this for non-virtual tweens. Do not rely on it for
virtual/delayed operations.

### SetSpeedBased

Call `SetSpeedBased()` immediately after creation. The creation method's `duration` argument
then represents units per second rather than seconds. For a movement path this gives
consistent travel speed regardless of path length.

For `TweenFloat` and `TweenInt`, the units are value units. Virtual Color and Vector3 are
internally normalized 0-to-1 tweens, so speed-based mode affects normalized interpolation
progress rather than actual colour/vector distance. That makes it unintuitive for those two
virtual types.

---

## 9. ChangeEndValue and tween reuse

`ChangeEndValue(newValue, snapStartValue)` retargets a compatible active DOTween tweener and
rewinds its timeline without pausing it. With `snapStartValue=true`, the current value becomes
the new start, preventing a jump back to the original starting point.

Publicly documented safe pattern:

```csharp
private VRCTweenHandle _move;

private void Start()
{
    _move = transform.TweenLocalPosition(
            Vector3.zero, 1f, VRCTweenEase.OutQuad)
        .SetLoops(-1, VRCTweenLoopType.Restart)
        .Pause();
}

public void MoveTo(Vector3 destination, float seconds)
{
    _move.ChangeEndValue(destination, true)
        .SetDuration(seconds)
        .SetEase(VRCTweenEase.OutCubic);
    _move.Restart();
}

private void OnDestroy()
{
    _move.Kill();
}
```

The infinite loop keeps the handle from normal completion cleanup. Pause it immediately,
then explicitly kill it when finished.

Compatibility is determined by the underlying DOTween end-value type, not merely by which
overloads exist on `VRCTweenHandle`:

| Tween | Useful new value | Status |
|---|---|---|
| position/local position/scale | `Vector3` | supported |
| world/local rotation | `Vector3` Euler | supported |
| RectTransform anchor/size | `Vector2` | supported |
| Graphic/Sprite colour | `Color` | supported |
| audio volume/pitch | `float` | supported |
| CanvasGroup fade, Slider | `float` | supported |
| virtual float/int | `float` | supported; int callback rounds |
| path | none exposed | unsupported |
| virtual Color/Vector3 | captured destination behind normalized float | unsupported |
| renderer/light colour | captured destination behind normalized float | unsupported |
| delayed calls | sequence rather than tweener | unsupported |

The official docs conservatively say renderer and light tweens are unsupported. In the
installed source, renderer-float and light-intensity happen to use float tweeners and the
generic call reaches them, but that is not a supported contract. Recreate those tweens if
you need to retarget them.

Fade tweens for `Graphic` and `SpriteRenderer` are internally colour tweeners. Passing a
float to `ChangeEndValue` does not match their actual end-value type. Recreate a fade rather
than assuming the float overload can retarget it.

For hot redirection paths, reuse substantially reduces allocations. For ordinary door/UI
animations, fresh tweens are simpler and the official docs say that tens of concurrent
tweens are fine.

---

## 10. Exact validation behaviour

Invalid values are rejected silently so an exception does not break the containing Udon
program. Creation returns `default(VRCTweenHandle)`, for which `IsValid` is false.

### Creation checks

- Duration must be finite and >= 0. Zero is allowed.
- Delay must be finite and >= 0.
- Position, scale, path waypoints, and virtual Vector3 endpoints must be finite and each
  component must have absolute value <= **520093.7**.
- The position limit is derived from a 0.062-metre precision budget and the float mantissa.
- Rotation Euler values only need to be finite; large finite angles are allowed.
- Vector2 anchor/size values only need to be finite; the position magnitude cap is not used.
- Float endpoints must be finite.
- Paths require a non-null array with at least two safe waypoints.
- Transform overloads return an invalid handle for null transforms without creating a tween.
- Component/target creation calls reject null targets.
- Virtual tweens reject a null callback receiver.

### Configuration checks

- invalid SetDelay/SetDuration values are ignored;
- `Goto` ignores non-finite values and clamps finite values into the valid timeline;
- `ChangeEndValue` rejects non-finite float/Vector2 and unsafe Vector3 values;
- actions against a dead/unknown handle silently do nothing.

### Source-level gap: Color values

The installed runner does **not** validate Color components for finiteness at creation or in
`ChangeEndValue`. Graphic, SpriteRenderer, renderer, light, and virtual Color calls can
therefore accept a Color containing NaN or Infinity even though the broad documentation says
unsafe values are rejected. Treat this as a beta implementation gap and validate colours in
your own Udon code.

The wrapper also does not verify shader property existence, component-specific value ranges,
or whether callback variable/event names are correct.

---

## 11. Networking pattern

VRCTween does not synchronize. For a network-consistent animation, synchronize compact
intent/state - not the transform every frame:

- start server timestamp;
- duration/speed;
- start/end or path identity;
- loop/ease mode;
- any authoritative completed state.

Each client creates the same local tween and seeks to the correct time with `Goto`.

```csharp
[UdonSynced] private double _startServerTime;
[UdonSynced] private bool _doorOpen;

private VRCTweenHandle _doorTween;

private void ApplyDoorState()
{
    _doorTween.Kill();

    Vector3 destination = _doorOpen ? openLocalPosition : closedLocalPosition;
    _doorTween = door.TweenLocalPosition(
        destination, 1.0f, VRCTweenEase.InOutSine);

    float elapsed = (float)(Networking.GetServerTimeInSeconds() - _startServerTime);
    _doorTween.Goto(elapsed, true);
}
```

The exact Udon serialization/ownership logic is project-specific. Be aware that
`VRCObjectSync`, synced transforms, Animators, constraints, physics, and other scripts can
all overwrite the same Transform. Choose one authority and one write phase. `PostLateUpdate`
can win against ordinary LateUpdate writers locally, but it does not solve network authority.

---

## 12. Practical patterns

### Reversible UI fade

```csharp
private VRCTweenHandle _fade;

private void Start()
{
    _fade = panel.TweenFade(1f, 0.25f, VRCTweenEase.OutQuad)
        .OnRewind(this, nameof(OnPanelHidden))
        .Pause();
}

public void ShowPanel()
{
    panel.gameObject.SetActive(true);
    _fade.PlayForwards();
}

public void HidePanel()
{
    _fade.PlayBackwards();
}

public void OnPanelHidden()
{
    panel.gameObject.SetActive(false);
}
```

If the initial CanvasGroup alpha is not the intended start, set it before constructing the
tween.

### Cancelable timer

```csharp
private VRCTweenHandle _timer;

public void Arm()
{
    _timer.Kill();
    _timer = VRCTween.DelayedCall(this, nameof(OnTimer), 5f);
}

public void Cancel()
{
    _timer.Kill();
}

public void OnTimer()
{
    // Fired in unscaled time.
}
```

Do not chain a different `OnComplete` onto this handle; it replaces `OnTimer`.

### Renderer property without material instantiation

```csharp
renderer.TweenFloat("_DissolveAmount", 1f, 0.8f, VRCTweenEase.InOutSine);
renderer.TweenColor("_EmissionColor", emission, 0.8f, VRCTweenEase.OutQuad);
```

### Virtual camera FOV

```csharp
[System.NonSerialized] public float tweenFov;

public void BeginFovTween()
{
    VRCTween.TweenFloat(
        camera.fieldOfView,
        85f,
        0.4f,
        this,
        nameof(tweenFov),
        nameof(ApplyFov),
        VRCTweenEase.OutCubic);
}

public void ApplyFov()
{
    camera.fieldOfView = tweenFov;
}
```

### Sequence-like choreography

VRCTween does not expose DOTween `Sequence`, `Append`, or `Join`. Chain stages through public
Udon events:

```csharp
public void Begin()
{
    panel.TweenFade(1f, 0.2f, VRCTweenEase.OutQuad)
        .OnComplete(this, nameof(MovePanel));
}

public void MovePanel()
{
    panelTransform.TweenAnchorPos(Vector2.zero, 0.35f, VRCTweenEase.OutCubic)
        .OnComplete(this, nameof(Finished));
}

public void Finished() { }
```

---

## 13. Common traps

1. **Assuming it networks.** Every player runs an independent local tween.
2. **Configuring it a frame later.** Loops, delay, update type, From, and speed-based mode
   may already be locked. Chain them immediately.
3. **Using `IsValid` as "still alive".** Use `IsActive` for that.
4. **Starting a replacement without killing the old writer.** Both tweens update and fight.
5. **Expecting target cleanup to include children.** `KillAllTweens()` is exact-object only.
6. **Expecting target cleanup to find virtual tweens or DelayedCall.** It cannot; retain the
   handles.
7. **Adding two OnComplete callbacks.** The later one replaces the earlier one.
8. **Adding OnComplete to DelayedCall.** It replaces the timer event supplied at creation.
9. **Empty virtual update event.** The installed runner then skips both the variable write
   and event.
10. **Non-public callback event.** Udon custom-event dispatch cannot call it.
11. **Wrong virtual field type/name.** The wrapper does not preflight it.
12. **Bad shader property/shared material.** Renderer creation does not validate these.
13. **Expecting Back/Elastic overshoot from clamped Lerp implementations.** Virtual Color,
    virtual Vector3, renderer colour, and light colour flatten it.
14. **Treating FixedUpdate as physics movement.** VRCTween writes Transform values, not
    Rigidbody movement APIs.
15. **Animator/constraint/network conflicts.** Multiple systems writing the same property
    produce last-writer behaviour.
16. **Forgetting infinite/rewound cleanup.** They remain active until killed.
17. **Depending on `VRCTweenEase.None`.** In this build it falls back to OutQuad; say what
    you mean explicitly.
18. **Assuming every ChangeEndValue overload fits every tween.** It must match the actual
    underlying end-value type.
19. **Creating hundreds in one frame.** VRChat recommends staggering large batches. Normal
    interaction-scale sets of roughly 5-50 are fine.
20. **Assuming beta internals are permanent.** Re-check this document when upgrading the SDK.

---

## 14. Features DOTween has that VRCTween does not expose

The installed wrapper intentionally exposes only a subset. There is no public VRCTween API
for:

- DOTween Sequences, `Append`, `Join`, or intervals;
- relative tweens;
- Rigidbody/Rigidbody2D movement;
- path look-at/orientation, axis constraints, or CubicBezier paths;
- snapping controls;
- per-tween time scale or a general unscaled-update toggle;
- tween IDs/strings, DOTween target filtering, or arbitrary predicates;
- generic getters/setters or lambdas from Udon;
- arbitrary OnUpdate delegates (virtual tweens provide the Udon-event equivalent);
- blendable tweens;
- punch/shake tweens;
- Quaternion end-value overloads;
- direct Vector2 virtual tweens;
- custom overshoot/amplitude/period parameters for preset eases.

Use callbacks, virtual tweens, or ordinary Udon code where the wrapper does not cover a use
case. Do not assume an API is Udon-safe merely because it exists in the bundled DOTween DLL.

---

## 15. Udon Graph

VRCTween is exposed to Udon Graph as well as UdonSharp:

- search for `VRCTween TweenPosition` or the desired creation node;
- capture the `VRCTweenHandle` output;
- use the `VRCTweenHandle` control/configuration nodes;
- pass the current UdonBehaviour and a custom-event name to callback nodes;
- create a public custom event matching that name.

The same lifecycle, validation, local-only networking, and callback-replacement rules apply.

---

## 16. Upgrade checklist

When the project updates beyond `3.10.5-beta.1`, re-check at minimum:

- package version and `VRCSDK3.dll` hash;
- `VRCTweenRunner` creation methods and validation;
- PostLateUpdate execution order/manual update;
- target tracking used by `KillAllTweensOnTarget`;
- the completion/Restart cleanup flag;
- delayed-call time-scale behaviour;
- Color finiteness validation;
- the official `ChangeEndValue` support matrix;
- new tween methods/enums in the public facade.

The public documentation lags the inspected package, which is a later beta. That is why this
file separates supported documentation from source-level observations.
