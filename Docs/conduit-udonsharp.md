# UdonSharp & VRChat behaviours

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before writing or editing an UdonSharp behaviour, or adding a U# component.**
>
> **This file owns what a U# behaviour may CONTAIN and what Udon can CALL, and nothing else goes in it.**
> It is the largest file in the set and therefore the easiest to keep adding to, so the test is strict.
> Sync, ownership and network events are `conduit-networking.md`, however much a synced field looks like
> a language feature. How to EDIT a script through the bridge is `conduit-compile-console.md`; how to
> write it well is `conduit-code-style.md`; what it COSTS to run is `conduit-performance.md`. Authoring a
> VRC_ component in the scene is `conduit-world-components.md` - only its Udon SURFACE is here. The
> `VRCPlayerApi` surface is `conduit-player.md`, and tweening is `conduit-vrctween.md`.

## Creating and adding behaviours

- **Create a new U# behaviour with `create-usharp`, never by writing the `.cs` directly** - Udon
  overwrites a hand-written behaviour file with a stub. The class name must match the file name. It accepts
  `Assets/` and project-local embedded packages; registry, cache and external local packages are refused.
  Editing existing scripts is fine.
- **A behaviour under a Unity `.asmdef` ALSO needs a U# Assembly Definition, or it compiles as C# and never
  becomes Udon.** `Assets/Create/U# Assembly Definition` writes the asset; point its `sourceAssembly` at the
  `.asmdef` that owns the scripts. Without it the compiler refuses with "Script '<path>' does not belong to a
  U# assembly, have you made a U# assembly definition for the assembly the script is a part of?" An `.asmdef`
  changes the C# assembly and registers nothing with UdonSharp. This bites only a behaviour moved into a
  package or a deliberately bounded assembly; a plain `Assets/` script needs neither.
  (`UdonSharpCompilerV1.cs:403`, SDK 3.10.5)
- **Assembly version defines now work in UdonSharp (SDK 3.10.5+), and they are the only honest way to
  ship one asset across SDK versions.** Add a Version Define Expression to the `.asmdef` and gate the code
  at compile time with `#if`. Without this, adopting ANY newer API in something distributed breaks every
  customer still on an older SDK - and it breaks at THEIR compile, not yours, which is the worst place to
  find out. Anything version-gated that ships outside this project gets the define, not a comment saying
  "requires 3.10.5". (VRChat SDK release notes, 3.10.5-beta.1)
- **An `.asmdef`'s `Auto Referenced` is a COMPILE-REFERENCE policy, not build inclusion, and it decides
  whether buyers can call your code at all.** ON means `Assembly-CSharp` - where an ordinary world script
  lives - automatically references the assembly, so a customer's own script can use your types with no
  setup. OFF means they must create their own `.asmdef` and reference yours explicitly, or nothing
  compiles against it. **For anything prefab-first or meant to be easy to consume, leave it ON**; turn it
  off only when the package deliberately wants a hard boundary, and say so in its documentation, because
  the failure it produces for a buyer is a type that "does not exist".
- **Always `gameobject-component-list` the target before `gameobject-component-add`** (and the parent too,
  when a prefab or another agent may have restructured things). The component may already exist or have
  been deliberately moved. A duplicate is worse than it looks: by-NAME component operations (modify,
  set-reference, get, the editor menu helpers) resolve to the FIRST component of the type, so wiring
  silently lands on one duplicate while the other runs unconfigured, logging errors that look like ghosts.
- **`gameobject-component-add` adds U# types through
  `UdonSharpComponentExtensions.AddUdonSharpComponent(GameObject, Type)`, not raw `AddComponent`.** Raw
  `AddComponent` creates a proxy whose `_udonSharpBackingUdonBehaviour` stays null through both `refresh`
  and `domain-refresh`, and UdonSharp's assembly-reload path begins from existing UdonBehaviours, so it can
  never discover or repair that proxy. The extension runs `RunBehaviourSetup`, creates the backing
  UdonBehaviour, and links its program source. Conduit calls it by reflection so non-UdonSharp projects
  keep working. (UdonSharp 1.x)
- **UdonSharp refuses to set up a newly added behaviour on a connected prefab instance.** Add it to the
  source prefab asset in Prefab Mode, save, and let the instance inherit it. A scene-only added-component
  override can show the proxy inspector while shipping no live Udon program. Before wiring UnityEvents or
  entering play mode, verify the proxy's `_udonSharpBackingUdonBehaviour` is non-null and that the same
  GameObject lists a `VRC.Udon.UdonBehaviour`.
- **And check that backing behaviour's `programSource` TOO - a null one runs nothing and says nothing.**
  The three states are distinguishable and only one of them reports itself: no program asset at all gives
  "The associated script cannot be loaded"; an asset that exists with `programSource` UNSET gives a
  component that sits there, fires no event, logs nothing and fails no compile; both present and wired is
  the healthy case. The silent middle state is the one to test for after any programmatic component
  creation, because it looks identical to working code in every inspector.
- **A behaviour just added reads `_syncMethod: 0 (Unknown)` on its backing UdonBehaviour, and that is not a
  fault.** `UdonSharpUndo.AddComponent` leaves the SDK default, and UdonSharp stamps the program's sync mode in
  `RunAllUpdates` - on scene open, on entering and leaving play mode, and after a build. Measured Unknown
  straight after creation and None after one play round trip. (`UdonSharpEditorManager.UpdateSyncModes`, SDK 3.10.5)
- **Moving or reparenting an existing U# behaviour orphans references to its backing UdonBehaviour.**
  Button `OnClick` persistent calls and serialized `UdonSharpBehaviour` fields pointing at it go null,
  because the backing is re-linked on the new object. Re-wire after any move.
- **REMOVING a U# behaviour from a scene object is a PAIR operation, the same way adding one is.**
  `gameobject-component-destroy` on the proxy destroys ONLY the proxy; the backing
  `VRC.Udon.UdonBehaviour` stays on the object, and once its program asset is gone it presents as an Udon
  behaviour with nothing in it, which reads to the owner as a broken object rather than a leftover.
  Destroy both, then `gameobject-component-list` the object to prove it holds nothing but its Transform.
  `delete-usharp` is about ASSETS - it removes the `.cs` and the program asset and never touches a scene
  instance, so deleting the script does not clean the scene up for you.

## Proxy vs backing: the two traps

- **`gameobject-component-get` reads EVERY UdonSharpBehaviour-typed reference as `{"instanceID": 0}`, so a
  wired U# reference looks unwired.** Any proxy field or array whose element type derives from
  UdonSharpBehaviour serializes as instanceID 0, and `[HideInInspector]` fields are absent from
  `serializedProperties` entirely - there is no bridge read that shows such wiring directly. **Do NOT
  conclude a U#-to-U# reference is missing from this read.** Verify with an editor-side comparison (log
  the live field) or read the backing `VRC.Udon.UdonBehaviour` and check its serialized reference list.
  Plain Unity types (RectTransform, Image, Button) read their real instanceIDs fine.
- **Editor tooling ends at `ApplyModifiedProperties()` in edit mode and pushes what it changed in play
  mode.** UdonSharp carries each proxy to its program at build (`ProxySerializationPolicy.PreBuildSerialize`),
  but a program ALREADY RUNNING has read its variables, so an inspector or bridge edit made in play mode is
  written into it field by field (next rule). Unpushed, the inspector and Play Mode disagree. (UdonSharp 1.x)
- **Push one changed field with `SetProgramVariable`, never the whole proxy with `CopyProxyToUdon`.** In play
  mode `CopyProxyToUdon` writes the proxy's PRIVATE fields too (`UdonSharpBehaviourFormatterEmitter` passes
  `includeNonSerialized` as `EditorApplication.isPlaying`), and a proxy never runs: a dose sent to a behaviour
  with a pushing inspector open on it was gone within the frame. Diff against what was last pushed. (SDK 3.10.5)
- **A UnityEvent persistent call must target the BACKING `UdonBehaviour`, never the proxy.** A listener
  pointed at the proxy resolves to an object the running program is not, so the press reaches nothing and
  the inspector still looks wired. This is the same backing-is-the-real-target rule as the orphaning above,
  seen at author time instead of after a move.
- **Nothing routinely copies Udon back into the proxy, so an unpushed write is never at risk.** The
  only `CopyUdonToProxy` callers are the one-time legacy serialization upgrade for prefabs and scenes -
  gated on `NeedsSerializationUpgrade`, and followed by UdonSharp's own comment that "the C# side owns
  the data from this point on" - and the default inspector's Send Custom Event button, which is disabled
  outside play mode.
- **Getting this wrong is not free: the push reflects over the WHOLE behaviour.** Calling it per
  serialized change copies all 113 fields of a controller, or every url, title and sprite of a 200-track
  playlist, on every keystroke.
- **Changing a serialized field's DEFAULT in code does NOT change instances already in a scene.** The
  default applies once, when the component is first added; after that the scene owns the value. So editing
  `public string x = "new";` and watching it compile proves nothing about what actually runs - the old
  string is still serialized on the object and still drives the UI. **After changing any default, write the
  new value to the live component too**, or the change ships only for people who add the behaviour fresh.
  The tell is a value on screen that appears nowhere in the source.

## Language and structure limits

- `[UdonBehaviourSyncMode(…)]` on the class. Fail fast in `Start()`: null-check critical serialized
  references and `Debug.LogError` + `return` if any are missing.
- **The sync mode is not a free choice and it constrains the behaviour's whole design** - arrays only under
  Manual, interpolation only under Continuous, never both on one behaviour. `conduit-networking.md` owns
  the modes, the budgets and the syncable type list; read it before picking one.
- **Udon dispatches `Start` AFTER physics has already stepped, so anything `Start` "repairs" must ALSO
  be authored correctly in the scene.** A SpringJoint left at `connectedAnchor` (0,0,0) with
  `autoConfigureConnectedAnchor` OFF drags its pickup to the connected body's ORIGIN first; `Start` then
  captures that DRIFTED pose as the rest position and locks it there. The tell is a handle that sits right
  in edit mode and moves on play. Leave `autoConfigureConnectedAnchor` ON so Unity derives the anchor from
  the authored pose, and re-derives it whenever the handle is moved.
- **A field initializer is executed once at compile time, by reflection - it is not per-instance and it
  is not even per-run.** UdonSharp invokes the initializer expression during compilation and bakes the
  result into the program, which lands in one of two shapes. A call Unity guards to the main thread
  **fails the build**: `private int _rng = Random.Range(0, 100);` returns
  `TargetInvocationException ... RandomRangeInt can only be called from the main thread`, because the
  compile runs off the main thread - **and the diagnostic's line number is WRONG**, here pointing at a
  closing brace twenty-five lines below the field, so the message names neither the field nor the concept.
  Anything NOT thread-guarded is worse, because it succeeds: the expression is folded to a single constant
  that every instance shares and that only changes when the behaviour is recompiled. Field initializers
  hold compile-time constants; every runtime value is assigned in `Start()`. (SDK 3.10.5)
- **`[DefaultExecutionOrder(n)]` works on a U# behaviour**, so ordering between behaviours is declarable
  rather than something to engineer around with delayed events. (SDK 3.10.5)
- **Below SDK 3.10.6, a named argument that skips an optional parameter is silently DROPPED.** Given
  `void M(int a, int b = 0, int c = 0)`, the call `M(1, c: 3)` compiled as `M(1)` with no error or
  warning. 3.10.6 compiles it correctly; anything that ships to older SDKs passes every argument
  positionally. (VRChat SDK release notes, 3.10.6-beta.1)
- **Below SDK 3.10.6, a relational comparison between two enum values can return the wrong answer.**
  `Test.A >= Test.A` returned `false` while the same comparison cast to `int` was correct. 3.10.6 fixes the
  enum form; code that must run on older SDKs compares `(int)` casts. (VRChat SDK release notes,
  3.10.6-beta.1)
- **Udon arithmetic is unchecked and wraps, exactly like ordinary C#.** Measured in play mode:
  `int.MaxValue + 1` returns `-2147483648`, no exception and no warning. So a bit-packing or hashing
  routine that RELIES on wrap-around is safe, and nothing needs masking to dodge an overflow trap.
  (Play mode, SDK 3.10.5)
- **A struct's mutating methods do nothing through Udon, and nothing errors.** Measured in play mode:
  `v = new Vector3(3,4,0)` then `v.Normalize()` still reads `3,4,0`, while `w = w.normalized` reads
  `0.6,0.8,0`. Both forms COMPILE clean, so the only signal is a wrong number somewhere downstream.
  Use the value-returning property and assign the result, every time. (Play mode, SDK 3.10.5)
- **No `static` fields on behaviours**, including `static readonly` (e.g. a cached `Shader.PropertyToID`) -
  "Static fields are not yet supported on user defined types". Both forms confirmed against the compiler,
  each erroring on its own declaration line. Use `const` or an instance field. **Static METHODS are fine**,
  both calls to whitelisted static methods and a `private static` method declared on your own behaviour -
  it is specifically FIELDS that are blocked.
- **`List<T>` cannot be constructed and exposes no members - it is a SIGNATURE TYPE ONLY.** `new List<int>()`
  fails the whitelist stage with "Method is not exposed to Udon", and the registry carries
  `Type_SystemCollectionsGenericListSystemInt32` with no constructor, no `Add` and no indexer. So
  `VRCPlayerApi.GetPlayersWithTag` returns a `List<int>` a world has no way to read. Use
  `VRC.SDK3.Data.DataList`, which exposes three constructors, or a plain array. "Blocked" understates it:
  the type is reachable and still unusable. (SDK 3.10.5)
- **There are no user-defined structs, and the `object[]` pseudo-struct workaround compiles and then
  kills the behaviour.** Packing one typed array per "field" into an `object[]` and reinterpreting it as
  `(MyBehaviour)(object)buffer` passes the UdonSharp compiler with no error and no warning; in play mode
  that line raises `An exception occurred during Udon execution, this UdonBehaviour will be halted` plus
  `Udon runtime exception detected!`. Udon has no `try`/`catch`, so the behaviour is dead from there on,
  and the clean compile is exactly what makes the technique look viable. Use parallel typed arrays indexed
  by slot instead: same job, no cast, and they serialize, sync and show in the Inspector.
  (Compile probe + play mode, SDK 3.10.5)
- **Trust the registry for what Udon can call; `new PhysicMaterial[n]` is the one known exception.** It is
  listed (`UnityEnginePhysicMaterialArray.__ctor`), compiles and runs in ClientSim, yet the VRChat client threw
  "not implemented yet" on it and halted the behaviour; an `object[]` holding the materials, compared as
  `(object)material`, works everywhere. This is one entry among thousands that hold: do not re-check other
  entries against it or raise it as a caveat, only avoid a call the client log names. (Client log, SDK 3.10.6-beta.2)
- **`DataDictionary` and `DataList` can be PRE-SIZED, which matters because growing one reallocates.**
  `new DataDictionary(capacity)` exists alongside the empty constructor, and
  `DataDictionary.EnsureCapacity(int)` grows it in one step instead of repeatedly on insert.
  `StringBuilder.EnsureCapacity(int)` is exposed too. Size them when the count is known - a dictionary
  filled from a parsed JSON payload knows its size before the loop starts.
  (SDK 3.10.5)
- **`System.Text.StringBuilder`, `System.Text.RegularExpressions.Regex`, seeded `System.Random` and
  `System.Type` are all callable from a behaviour**, as are `[FieldChangeCallback(nameof(Prop))]` and
  `[RecursiveMethod]`. Confirmed by compile, so none of them needs re-checking. (SDK 3.10.5)
- **Unity callbacks take NO `override`; VRChat callbacks require it.** `public void OnTriggerEnter(Collider)`
  compiles, and the same signature marked `override` fails with "CS0115: no suitable method found to
  override", while `public override void OnPlayerJoined(VRCPlayerApi)` is the required form. The two sit
  together in most behaviours, which is what makes the wrong one easy to copy. (SDK 3.10.5)
- **`Start()` never runs on a GameObject that is inactive at load**, so a behaviour deliberately parked
  inactive - a pooled object, a gimmick disabled for performance, a conditionally shown panel - reaches
  its first external call with every cached reference still null. The symptom is a `NullReferenceException`
  the moment something activates or calls it, on code that "obviously works". Put initialisation in a
  private `Initialize()` guarded by an `_initialized` flag, and call it from `OnEnable`, from `Start`, AND
  at the top of any public entry point, so whichever happens first wins and the rest are free.
  **And the same guard covers a second, separate cause: `Start()` order between behaviours is not
  defined.** Behaviour A's `Start` can call into behaviour B before B's own `Start` has run, so a
  perfectly active object still hands back nulls. That is why the guard belongs on every public entry
  point and not only on the activation path - and why `[DefaultExecutionOrder(n)]` above is the
  declarative fix when the ordering genuinely matters rather than merely needing to be survivable.
- **A `VRCPlayerApi` held in a field goes stale when that player leaves.** Store the `playerId` instead
  and resolve it through `VRCPlayerApi.GetPlayerById` at the point of use, validating the result - a
  reference captured on entry and used later is one disconnect away from being invalid.
- **`Awake()` does not exist in Udon - `Event_Awake` is absent from the registry entirely.** A method
  named `Awake` compiles as an ordinary private method that NOTHING ever calls, so initialisation written
  there simply never runs and the behaviour presents as unconfigured. `Start()` is the one-time
  initialisation hook. (SDK 3.10.5)
- **`SendCustomEventDelayedSeconds` / `DelayedFrames` take an optional `EventTiming`**, so a delayed call
  can be landed where it belongs rather than always in `Update`. The enum is
  `Update = 0`, `LateUpdate = 1`, `PostLateUpdate = 2`, `FixedUpdate = 3`: use `FixedUpdate` for anything
  that must line up with a physics step and `PostLateUpdate` for camera follow and post-IK corrections,
  which are exactly the cases where an `Update`-timed callback lands one frame stale.
  (`VRC.Udon.Common.Enums.EventTiming`, SDK 3.10.5)
- **`SendCustomEvent` cannot dispatch to an `abstract` method - abstract classes compile, string
  dispatch to them does not.** An abstract method has no concrete body in the compiled program, so there
  is nothing for Udon to find by name and the call silently reaches nothing. A typed call through an
  abstract-typed REFERENCE works normally, so the shape that survives is: hold the base type, call the
  method directly, and use `SendCustomEvent` only for concrete methods. This is how a callback interface
  gets rebuilt without `interface` - an abstract base plus direct typed calls, never name dispatch.
- **Recursion without `[RecursiveMethod]` compiles clean - the attribute is an emit switch, not a
  check.** A plain recursive method built and linked here with no attribute, no error and no warning, so
  "the compiler will tell me" is false. `IsRecursiveMethodEmit` is literally
  `CurrentEmitMethod?.HasAttribute<RecursiveMethodAttribute>()`, and ONLY when that is true does UdonSharp
  allocate a `RecursiveStackValue` - a global `object[]` on the program heap - plus an int stack pointer,
  and emit the pushes and pops around the call. Without it a method's parameters and locals are FIXED heap
  slots, one copy each, so the inner call writes the very slots the outer call is still using.
  (`EmitContext.cs:84, 241-242, 323-354`, SDK 3.10.5)
- **And it has to go on every method in the cycle, including cycles that look nothing like recursion.**
  A calling B calling A is the same overwrite with no recursive-looking call at either site, and so is a
  listener that calls back into its broadcaster - which is the mechanism behind the re-entry rule below,
  seen from the compiler's end. Mark every method on the cycle, or break the cycle.
- **Guard any broadcast loop against re-entry.** A listener whose handler calls back into the
  broadcaster mutates the subscriber array WHILE it is being iterated, or recurses without bound - and
  Udon has no call stack to protect you. A single `_isEmitting` bool set around the loop turns that into a
  dropped call instead of corruption. It is the same shared-heap re-entrancy that bites an input field
  writing its own text, seen from the other end.
- **`typeof` does not work on YOUR OWN behaviours: "Cannot use typeof on user-defined types".** So
  `GetComponent(typeof(MyBehaviour))` fails to compile while `GetComponent(typeof(VRCPickup))` is fine -
  the typeof form is only for Unity and VRChat types. Fetch a user behaviour through the generic overload
  instead: `GetComponent<MyBehaviour>()`. The trap is that the two calls sit side by side in the same
  scan loop and only the user-defined one errors.
- **A component whose type is NOT on the Udon whitelist can still be detected, by NAME.** `typeof` refuses
  an unexposed type, so `GetComponent` cannot ask for one - but `gameObject.GetComponents(typeof(Component))`
  returns everything on the object and `parts[i].GetType().Name` reads each one's type name as a plain
  string. A behaviour can therefore answer "is component X on me" for a type Udon has never heard of.
  Both halves are in the registry (`GetComponents__SystemType__UnityEngineComponentArray`,
  `SystemType.__get_Name`). This is the general escape hatch whenever a tell lives in a type Udon cannot name.
- **`GetComponentsInChildren` IS exposed.** Six forms exist on `Component`, including the generic `T[]` return and
  the `System.Type` forms, so searching a hierarchy is a normal thing to do. Two of the six fill a
  `List<T>` and are therefore unusable for the reason `List<T>` always is; the array-returning ones are
  fine. **`FindObjectOfType`, `Invoke` and `InvokeRepeating` really are absent** - that part of the same
  table is correct, and `SendCustomEventDelayedSeconds` replaces the last two.
  (SDK 3.10.5)
- **To tell the editor from the VRChat client at runtime, look for `ClientSimUdonHelper` on your own
  GameObject.** ClientSim attaches that component to every object carrying an UdonBehaviour, and only ever
  in the editor, so the rule above identifies it by name; cache the answer, it cannot change. Every obvious
  route is a dead end: `Application`, `Application.isEditor`, `RuntimePlatform` and `SystemInfo` are absent
  from the Udon registry ENTIRELY, and `#if UNITY_EDITOR` is wrong because UdonSharp compiles under that
  define and the same program is what uploads. **And do NOT reach for ClientSim's own root object.**
  `__ClientSimSystem` genuinely exists and the editor can see it, but `ClientSimMain` moves it to the
  DontDestroyOnLoad scene and Udon's `GameObject.Find` cannot reach that scene - measured in one frame, the
  same call returning the active scene's objects and null for that one. A component on your own object
  needs no scene search at all. (SDK 3.10.5-beta.1)
- **Never put behaviours with different `UdonBehaviourSyncMode` values on the same GameObject.** Keep
  behaviours on purpose-specific objects instead of stacking unrelated managers on a shared root - a UI
  manager on its UI object, a queue/state manager on its own object.
- **Plain C# is stripped from VRChat uploads.** Usable for in-editor testing, but it will not ship. A
  STANDALONE editor script - an Editor-folder class, a MonoBehaviour tool - goes under `#if UNITY_EDITOR`.
  **That guard is not sufficient for a member ON a U# behaviour - see the next rule.**
- **To hide a field or method on a U# behaviour from Udon, guard it with `#if !COMPILER_UDONSHARP && UNITY_EDITOR`
  - `UNITY_EDITOR` alone does nothing, because it is defined during the UdonSharp compile too.** This is how an
  editor-authored value (a theme asset, a `TMP_FontAsset`, anything only the inspector touches) lives on the
  product's own behaviour instead of being exiled to a companion MonoBehaviour. Without the guard the failure
  arrives in one of two shapes: a type Udon cannot represent fails at assembly with
  `TypeResolverException: Type referenced by 'TMProTMP_FontAsset' could not be resolved`, while a type it
  half-accepts assembles and then throws on EVERY proxy push with
  `Type X not compatible with serializer VariableValueStorage`1[System.Object[]]` - the second only surfaces
  when something calls `CopyProxyToUdon`, so it can pass a clean compile and break later in an inspector button.
  Read such a field through a fresh `new SerializedObject(product)`, not the inspector's own serializedObject,
  which is pushed to the backing behaviour where the field does not exist.
- **Guard `OnValidate` with `#if !COMPILER_UDONSHARP && UNITY_EDITOR` - the one member that guard keeps OUT of
  the program rather than hiding a type Udon cannot represent.** Udon never dispatches it: the event table is
  built from Udon's `Event_` node definitions and there is no `Event_OnValidate`, so `UdonBehaviour`'s
  `RunEvent("_onValidate")` reaches nothing and only the proxy's plain C# ever runs. Guarded it still fires on
  every inspector change, keeps an uncallable method out of every program, and can use editor-only APIs, which
  is what makes it the place to report a missing reference the author is still wiring. Pass `this` as
  `Debug.LogWarning`'s context argument, so clicking the console entry selects the offending object -
  which is the whole point of reporting it at author time. (SDK 3.10.4)
### The event surface, and reading it rather than remembering it

- **Ask the registry before concluding an event does not exist - `udon-exposure Event_` returns all
  155 of them in one call.** That is the entire dispatch surface, and it is the only list still correct
  after an SDK update. (SDK 3.10.5)
- **Being in the registry is not being RAISED: ClientSim never raises `OnDisable` or `OnDestroy` when
  play mode stops, so no restore may depend on either.** Measured here - a behaviour logging in all three
  exported `_start`, `_onDisable` and `_onDestroy` as entry points under `udon-program | exports`, and
  leaving play mode produced neither of the last two while `Start` had logged on entry. A behaviour that
  captures an original in `Start` and puts it back in `OnDestroy` therefore keeps the PLAY-MODE value, and
  that is the mechanism by which a `sharedMaterial` write reaches disk on the next scene save. Restore
  from a call the world makes itself, and treat both events as unavailable in the editor.
  (ClientSim, SDK 3.10.5)
- **Events essentially no reference mentions:** `OnJointBreak` and `OnJointBreak2D`; the whole 2D physics
  family (`OnCollisionEnter2D` and friends); `OnParticleTrigger` beside `OnParticleCollision`; the camera
  pass events `OnPreCull`, `OnPreRender` and `OnPostRender`; all seven `OnMouse*` events;
  `OnControllerColliderHit` and its player twin `OnControllerColliderHitPlayer` (`conduit-player.md`);
  `OnVariableChange`; and `OnScreenUpdate`. Reach for the list rather than designing a workaround for
  something that is already an event.
- **`Event_Custom_1_Parameters` through `_8_Parameters` are where `[NetworkCallable]`'s eight-parameter
  ceiling comes from** - the dispatch surface itself stops at eight, so that limit is structural rather
  than a policy the attribute chose. `conduit-networking.md` owns the attribute's own rules.

### What the compiler actually refuses, and what it does not

Every row measured against this project's compiler. The message matters as much as the verdict: several
refusals arrive as an internal exception naming no construct at all, so the error on screen will not always
point at the line that caused it. (SDK 3.10.5)

| Construct | What UdonSharp says |
| --- | --- |
| Implementing an `interface` | `NotImplementedException: Interfaces are not yet handled by U#` |
| Generic method declaration | `Generic method declarations on UdonSharpBehaviours are not currently supported` |
| `try` / `catch` / `finally` | `NotSupportedException: Try/Catch/Finally is not supported ... Udon does not have a way to handle exceptions` |
| Lambda | `does not currently support node type 'SimpleLambdaExpression'` |
| LINQ | `Method is not exposed to Udon: 'values.Count()'` |
| `yield return` | `does not currently support node type 'YieldReturnStatement'` |
| `StartCoroutine` | `Method is not exposed to Udon: 'StartCoroutine(...)'` |
| Local function | `Local method declarations are not currently supported by UdonSharp` |
| Pattern matching (`is T x`) | `does not currently support node type 'IsPatternExpression'` |
| Null-conditional `?.` | `does not currently support node type 'ConditionalAccessExpression'` |
| Anonymous type | `does not currently support node type 'AnonymousObjectCreationExpression'` |
| `await` | `does not currently support node type 'AwaitExpression'` |
| C# `event` or delegate field | `InvalidOperationException: Failed to construct symbol for type` |
| `onClick.AddListener` | `NotImplementedException: The method or operation is not implemented.` |
| INVOKING a user-defined operator | `InvalidCastException: Specified cast is not valid.` |
| Multidimensional array `T[,]` | `Multidimensional arrays are not yet supported by UdonSharp, consider using jagged arrays instead` |
| `unsafe` / pointers | `CS0227` from Roslyn - it never reaches UdonSharp at all |

**And `?.` is the reverse mistake - it READS as supported and does not compile at all.** `x?.y` is refused
outright, so every null check is written long-hand. `??` is fine, which is the trap: the two sit together
in ordinary C# and only one of them survives the compile.

**Constructs confirmed to COMPILE, all in one clean build here** - worth knowing because inherited tables
disagree about several: JAGGED arrays (`int[][]`, including `new int[2][]` and per-row assignment), `out`
parameters, `ref` parameters, `params T[]`, null-coalescing `??`, and the ternary operator.
(SDK 3.10.5)

**Four things that read as blocked and are not**, each compiled clean here: method
overloading; DECLARING an interface, where only implementing one fails; `GetComponent<UdonBehaviour>()`,
so the `(UdonBehaviour)GetComponent(typeof(…))` cast those references mandate is not required; and DECLARING
a user-defined operator, where only invoking it breaks. Design against the measured list, never an
inherited one.

**Absent from the node registry entirely:** `Dictionary`, `HashSet`, `Queue` and `Stack`, and every type
under `System.IO`, `System.Net` and `System.Reflection`. `System.Threading` exposes exactly one type,
`CancellationToken`. `List<T>` is the exception and is covered above.

- **A background (unfocused) editor does not pump `EditorApplication.hierarchyChanged`** - it dispatches
  when the editor regains focus. `EditorApplication.update` DOES tick unfocused (the bridge runs on it),
  so automation that must trigger editor hooks synchronously should expose a MenuItem and call it through
  the `menu` verb rather than poking the hierarchy and waiting.

## URLs and string loading

**Sync, ownership, network events and their security model live in `conduit-networking.md`** - the sync
modes and budgets, `OnDeserialization` and late joiners, `SetOwner` and `OnOwnershipRequest`, the `_`
prefix and `[NetworkCallable]`, and instantiation. Read that file before writing anything that syncs or
sends. This section is only about reaching the outside world.

- **`new VRCUrl(...)` is not exposed to Udon, so a world can never build a url at runtime.** Not from
  an interpolated string, and not even from a plain literal - both fail to compile with `Method is not
  exposed to Udon: 'new VRCUrl(...)'` (both forms confirmed separately).
  A url reaches a world by exactly two routes: a **serialized `VRCUrl` field** authored in the inspector,
  or a **`VRCUrlInputField`** a player types into. There is no third.
  **So no runtime value - a coordinate, an id, a search term - can ever become a query parameter.**
  Anything that must vary per player has to be resolved by the SERVER behind a fixed url, or chosen from
  a set of urls baked at authoring time. Design the endpoint around that before writing
  the behaviour; discovering it afterwards invalidates the whole approach.
  **But `VRCUrl` IS a syncable type** (`[UdonSynced] private VRCUrl _url;` compiles under Manual and lands
  on the heap as `%VRCSDKBaseVRCUrl`), so a url that ONE client legitimately obtained - authored in the
  inspector, or typed into a `VRCUrlInputField` - can be handed to the whole instance. That is the shared
  queue or jukebox pattern, and it is the only way a url ever reaches a client that did not author it.
  (SDK 3.10.5)
- **VRChat's bundled yt-dlp is stripped and cannot enumerate a playlist - never build tooling on it.**
  The copy under `%LOCALAPPDATA%\..\LocalLow\VRChat\VRChat\Tools` reports a current version (2026.08.19)
  and the usual usage line, so it reads as stock yt-dlp and fails only once an option is passed. Its
  ENTIRE informational option set is `-g/--get-url`, `--get-format` and `-F/--list-formats`: no `-j`, no
  `--dump-json`, no `--flat-playlist`, no `--print`, no `--get-title`, no `--get-id`, not even
  `-o/--output`. It exists to turn ONE video url into a stream url for the player and does nothing else.
  Titles, ids and playlist contents therefore come from a SERVICE, which is also the only form a customer
  can run: an asset that needs a full yt-dlp installed per machine is not shippable. **The tell is
  `error: no such option` reported against a version number that looks current**, and the trap is reading
  that as the site changing rather than the tool being replaced.
- **Never initialise a serialized `VRCUrl` field to `VRCUrl.Empty` - use `= null` and null-check at the
  use site.** `VRCUrl.Empty` is ONE shared instance and `VRCUrl` has no public setter, so a url
  deserialized into such a field lands in the shared static and poisons every field initialised from it.
  The tell: a player autoplays at world start while scene, prefab and program read clean; a restart clears it.
- **Never bake a localhost or private url into a world - VRChat's url allowlist refuses them, so a world
  that ships one is broken on upload.** Scenes and prefabs hold PRODUCTION urls only. **The mistake is
  invisible while developing** whenever anything rewrites requests in play mode, so the editor is not
  evidence either way; any tool that generates urls should refuse a local base outright rather than
  relying on being caught later.
- **NEVER raise "Allow Untrusted URLs" as a caveat, a limitation, or a reason a feature will not ship.**
  Treat string loading and page fetching as available and design for the setting being on. This is the
  owner's standing answer and it does not change between sessions. Where a real fallback path exists, write
  it into the code and say nothing further about the setting.
- **String downloads are rate limited to one per 5 seconds, per CLIENT, and the limit is in the SDK.**
  `VRC.SDK3.StringLoading.VRCStringDownload` holds `private const float MINIMUM_DELAY_BETWEEN_REQUESTS = 5`.
  (SDK 3.10.4) **It WAITS, it does not refuse.** There is no rate-limit error code for string loading at
  all - the `TooManyRequests` / `RateLimited` members belong to `VRCImageDownloadError` and `VideoError`.
  A too-fast caller sees latency, not an error, so pacing it yourself buys nothing.

  `MAXIMUM_DOWNLOAD_SIZE_MB` is 104857600 - 100 MiB in bytes despite the name, so reads are effectively
  unlimited.

- **Registry corroboration for the rule above: there is NO `VRCUrl` constructor node AT ALL.** A search of
  all 35,222 definitions returns nothing for a `VRCUrl` constructor in any spelling, which is the second
  independent confirmation beside the compile. (SDK 3.10.5)
- **`VRCUrlInputField` is the only runtime source, and its pair is `GetUrl()` / `SetUrl(VRCUrl)`.**
  `SetUrl` only DISPLAYS a url you already hold; it does not mint one, so `SetUrl` then `GetUrl` hands
  back the same object and is not a way around the constructor. A url typed by a player is still subject
  to the trusted-domain check. (SDK 3.10.5)
- **Image loading is rate limited once per 5 seconds across the whole scene, and the queue is
  unordered.** The limit is per client and shared by every downloader in the world, not per behaviour, so
  two independent galleries starve each other. Worse, requests held by the limiter are reported to come
  back in RANDOM order - so a "download these in sequence" loop cannot assume the reply it receives
  belongs to the request it just made. Correlate on `result.Url`, never on a counter you incremented.
- **Image loading does not follow redirects.** A shortened link or any redirecting url fails outright;
  it needs the final direct url. String loading DOES follow redirects, but the destination has to be a
  trusted domain in its own right, so a redirect cannot be used to reach an untrusted host.
- **Images are capped at 2048 x 2048 and error above it**, and the format is chosen for you - RGBA32 or
  RGB64 with alpha, RGB24 or RGB48 without.
- **`IVRCImageDownload.Dispose()` frees the wrapper, not the VRAM.** The `Texture2D` already handed to a
  material stays resident, so a slideshow that disposes each download and never calls
  `Destroy(oldTexture)` leaks GPU memory for the whole session while looking correctly cleaned up.
  Destroy the previous texture before assigning the next one. **The scale is why this matters:** a
  2048x2048 RGBA32 texture is about 16 MB, mipmaps add roughly a third on top, and a world cycling one
  image every 30 seconds sheds about 320 MB across twenty cycles. The GC cannot help - it sees only the
  small managed wrapper, never the unmanaged GPU allocation behind it.
- **Pass `null` as the material and apply the texture yourself when you intend to manage its lifetime.**
  Handing `DownloadImage` a material makes ownership ambiguous exactly where it must not be; taking the
  texture in `OnImageLoadSuccess` and assigning it by hand makes "I own this and I destroy it" explicit,
  which is what the destroy-then-replace order depends on.
- **A CROSSFADE cannot destroy on arrival - the old texture is still on screen.** Destroying it when the
  new one lands produces a black flash mid-fade. Alternate two renderer slots, fade, and destroy the
  outgoing texture only once the fade has finished.
- **Construct one `VRCImageDownloader` in `Start()` and reuse it.** A `new VRCImageDownloader()` per
  download is a permanent leak: each one holds VRC internal state and any completed textures it still
  owns, and nothing disposes it. Dispose the previous `IVRCImageDownload` before overwriting the
  reference, and dispose the downloader itself in `OnDestroy`.
- **Several loaders starting together starve each other forever, and so do their retries.** Because the
  5-second limit is scene-wide, N loaders that all fire in `Start` - or all retry on the same error -
  keep colliding indefinitely. Give each instance a serialized order index and delay its first download
  by `order * 5.5f` seconds, and retry on a full base delay rather than immediately.
- **Assign textures through `renderer.material`, never `renderer.sharedMaterial`.** `sharedMaterial`
  writes the ASSET, so every object using it changes too - and in the editor that edit survives play
  stop and can be flushed to disk by a later scene save.
- **A positive mipmap bias or `FilterMode.Trilinear` does nothing without
  `TextureInfo.GenerateMipmaps = true`** - a texture with no mipmaps has only level 0, so the setting is
  silently inert and merely costs shader cycles.
- **Implement `OnImageLoadError` or the cycle stops permanently.** A refresh loop that only reschedules
  from the success path halts on the first failure and never recovers, which presents as an image that
  froze rather than as a download that failed. Log it and schedule the retry from the error handler too.
- **Pass `(IUdonEventReceiver)this` explicitly to `DownloadImage` or no event ever fires.** The registry
  carries exactly ONE overload, taking url, material, receiver and `TextureInfo` together. UdonGraph fills
  the receiver in for you and UdonSharp does not, so the omission compiles and then simply never calls
  back - which reads as a failed download rather than a wiring mistake. (SDK 3.10.5)
- **String loading and image loading have separate rate-limit queues, which is the way past the image
  limit entirely.** Both are one-per-five-seconds, but they do not share a queue - so texture data
  Base64-encoded inside a STRING download bypasses the image limiter completely, and ONE string request
  can carry a whole pack of thumbnails that `VRCImageDownloader` would take a minute to fetch. It also
  escapes the 2048x2048 cap and the separate image trusted-domain list. `Convert.FromBase64String(string)`
  is exposed, and `Texture2D.LoadRawTextureData(byte[])` takes the decoded bytes straight to the GPU with
  no PNG decode. The cost is about 33% size inflation from Base64 and the decode work itself.
  (SDK 3.10.5)
- **`Convert.FromBase64String` throws on malformed input, and Udon has no `try`/`catch` to catch it
  with.** That makes a bad server response an UNCATCHABLE exception rather than a handled error, so
  validate before calling: a valid Base64 string is a non-zero length that is a multiple of 4. This is the
  general shape of the no-exceptions rule - any API that throws must be guarded by a precondition, because
  there is no second chance.
- **Raw texture bytes must match the platform's GPU format, and the wrong one is garbage, not an
  error.** `LoadRawTextureData` uploads bytes verbatim, so PC wants `DXT1` (opaque) / `DXT5` (alpha) and
  Android wants `ETC2_RGB` / `ETC2_RGBA8` - or the ASTC family, which is what Quest hardware actually
  prefers (`conduit-build-validation.md` covers ASTC for imported textures). Branch with
  `#if UNITY_ANDROID`, since the world is compiled separately per platform, and serve a separate pack file
  per platform. **CRUNCHED VARIANTS CANNOT BE USED THIS WAY AT ALL** - `DXT1Crunched`,
  `ETC_RGB4Crunched` and friends need Unity's own decompression step first.
- **Cut the pack with PRE-COMPUTED offsets, never by scanning.** Carry each block's start and length in a
  JSON header and call `Substring` once per block; walking a 200 KB payload with repeated searches is
  quadratic and allocates hundreds of megabytes of garbage in a world that has no spare frames for it.
- **`VRCJson` deserializes EVERY number as `Double`**, so an integer comes back as `token.Double` and needs
  `(int)` - reading `.Int` on it does not do what the JSON looked like it said. Parsing is also LAZY: only
  the top level is validated, so malformed nested JSON surfaces as a failed `TryGetValue` much later
  rather than as a failed parse. Check the return of every `TryGetValue`, not just the initial
  `TryDeserializeFromJson`.

## Runtime textures and materials

- **`VRCGraphics` lives in `VRC.SDKBase`, NOT `VRC.SDK3.Rendering`.** Both `Blit(tex, rt)` and
  `Blit(tex, rt, mat)` are exposed, as are `CustomRenderTexture.Initialize()` and `.Update()`.
- **Udon exposes the whole CPU canvas path:** `new Texture2D(w, h, TextureFormat, bool)`, `filterMode`,
  `SetPixels32(Color32[])`, the block form `SetPixels32(x, y, w, h, Color32[])`, `Apply()` and
  `LoadRawTextureData(byte[])`. (SDK 3.10.5-beta.1)
- **`Texture2D.GetRawTextureData()` is NOT exposed, while `LoadRawTextureData(byte[])` IS.** A TEXTURE2D
  can be filled from bytes but never read back out, so a design that needs those pixels again has to keep
  its own copy of them. **A RENDER texture is a different question entirely - see the next bullet before
  concluding anything is write-only.**
- **Read a RenderTexture back with `VRC.SDK3.Rendering.VRCAsyncGPUReadback.Request` - it is exposed.**
  `RenderTexture.active` being unavailable in both directions does NOT make a render texture write-only. That
  inference is the trap this bullet exists to stop: the absence of one route is not the absence of all of
  them. `Request` takes the texture, a mip, an optional `TextureFormat`, and an `IUdonEventReceiver` to call
  back on; a second overload takes `x, width, y, height, z, depth`, so an eyedropper reads ONE PIXEL rather
  than the whole surface. The result arrives later at
  `public override void OnAsyncGpuReadbackComplete(VRCAsyncGPUReadbackRequest request)`, so nothing needing a
  pixel can answer in the frame it asked. Verified from the Udon extern names in
  `VRC.Udon.VRCWrapperModules.dll`. (SDK 3.10.5-beta.1)
- **Batch pixel writes behind ONE `Apply()`, or blit instead.** A block `SetPixels32` touches only its own
  region, but `Apply()` always re-uploads the ENTIRE texture, so calling it per stamp costs the whole
  buffer every time; `VRCGraphics.Blit` avoids the upload altogether.
- **Use material array properties for palettes and flag masks, never as a substitute for a texture.**
  `SetColorArray`, `SetVectorArray` and `SetFloatArray` are exposed, but Unity caps EVERY array property
  at 1023 elements and truncates silently past it.

## Tweening

- **Udon has NATIVE tweening: `VRCTween`, in `VRC.SDK3.Components`.** It covers Transform, RectTransform,
  Graphic, Renderer material, AudioSource, Light, and virtual values, with handles you build once and
  retarget rather than re-create.
- **DOTween ships in the SDK but is NOT exposed to Udon.** A `DG.Tweening` call compiles clean at the
  Roslyn stage and is then rejected by the whitelist stage with `Method is not exposed to Udon`. It is
  there for the SDK's own editor code.
- **`conduit-vrctween.md` owns this subject in full** - the complete creation API, handle state and
  control methods, ease and loop types, callback and timescale behaviour, `ChangeEndValue` reuse,
  validation behaviour, the networking pattern, and the known traps. Read it before writing a tween.

## Persistence

- **NEVER re-verify that persistence EXISTS.** `PlayerData` / `OnPlayerRestored` shipped in 2024 and
  are in every supported SDK; do not grep packages or SDK source to "confirm" the feature before
  designing with it. Member signatures are proven by the compile, like any API.
- **Nothing may touch `PlayerData` until `OnPlayerRestored` has fired.** A read before that returns the
  default rather than the saved value, and a write is discarded, both silently - which presents as
  persistence that "works sometimes", because whether the restore beat your `Start()` varies with the
  connection. Set a `_dataReady` flag in `OnPlayerRestored` and gate every read and write on it.
- **VRChat warns you BEFORE the cap, with four real Udon events**, so a world that stores a lot does not
  have to discover the limit by losing data: `OnPlayerDataStorageWarning` and
  `OnPlayerDataStorageExceeded`, plus `OnPlayerObjectStorageWarning` and `OnPlayerObjectStorageExceeded`,
  each carrying the `VRCPlayerApi` it concerns. All four are in the registry as `Event_` definitions.
  (SDK 3.10.5)
- **Setters take no player and getters take one, so a read-modify-write handed a `VRCPlayerApi` reads
  that player and writes yours.** `SetInt(string, int)` against `TryGetInt(VRCPlayerApi, string, out int)`
  - you can read anyone's data and write only your own, and no overload exists that would let you try
  otherwise. `OnPlayerRestored(VRCPlayerApi player)` fires for EVERY player, not just the local one, so an
  unguarded `TryGetInt(player, "gold", out g)` followed by `SetInt("gold", g + 1)` credits the LOCAL
  player from a REMOTE player's balance - and each call is correct on its own, so nothing reads wrong at
  the line. `if (!player.isLocal) return;` before any read-modify-write. (SDK 3.10.5)
- **There is no `DeleteKey` - a PlayerData key cannot be removed from Udon, only overwritten.**
  `PlayerData.DeleteKey(player, key)` is the obvious counterpart to the setters and no node of that name
  exists in the registry; the type's own internal delegate table holds `_getKeys`, `_hasKey`, `_getType`
  and the set/get pairs with no delete among them. So every key a world ever writes counts against that
  player's
  100 KB permanently, which makes the key SCHEMA a one-way decision - and a versioned name (`score_v2`)
  the only way to move on from one you regret. (SDK 3.10.5)
- **`HasKey(player, key)` and `GetType(player, key)` both exist, and the second is the one nobody uses.**
  `GetType` returns the `System.Type` a key currently holds, so a world can migrate its OWN saved data -
  read the type, branch, write the new shape - rather than guessing and getting a default back. Every
  `TryGet*` also has a plain `Get*` twin returning the value directly. (SDK 3.10.5)
- **PlayerObject setup is three components and exactly ONE scene instance.** `VRCPlayerObject` on the
  prefab root marks it for per-player instantiation, the `UdonBehaviour` carries the synced fields, and
  `VRC.SDK3.Components.VRCEnablePersistence` opts that behaviour's data into cloud persistence. **The
  persistence component must sit on the same GameObject as every UdonBehaviour it persists** - without it
  the PlayerObject still instantiates per player and simply saves nothing. Place one instance in the
  scene and VRChat makes the copies. (`VRC.SDK3.Components.VRCEnablePersistence`, SDK 3.10.5)
- **Persisted PlayerObject data is keyed to network IDs, and an ID is bound to a hierarchy path - so
  restructuring the prefab orphans every player's save.** The descriptor stores each id as an
  (ID, gameObject, gameObjectPath) triple and treats two entries as the same one only when `ID` AND
  `gameObjectPath` BOTH match (`VRCNetworkIDUtility.cs:19-29`), so a move or a rename breaks the pairing
  instead of following the object - and deleted objects leave stale entries behind, which is why the SDK
  ships a repair window at all (`conduit-networking.md`). Audit ids BEFORE reparenting or rebuilding a
  PlayerObject prefab; afterwards the mapping you would need to recover them is already gone.
- **Nineteen value types, not the eleven a type table lists.** Alongside bool/int/float/double/string and
  the Unity types, the registry carries `SetByte`, `SetSByte`, `SetShort`, `SetUShort`, `SetUInt`,
  `SetLong`, `SetULong` and `SetColor32`, each with its `TryGet`. Run
  `udon-exposure VRCSDK3PersistencePlayerData` before widening a value to fit a type you assumed was
  missing. (SDK 3.10.5)
- **`PlayerData` carries raw `byte[]`: `SetBytes(string, byte[])` and `GetBytes(VRCPlayerApi, string)` are
  both exposed**, so a packed structure travels in ONE key instead of a field per value. `SetString` /
  `GetString` sit alongside them. (SDK 3.10.5-beta.1)
- **`PlayerData.GetKeys` returns `IEnumerable<string>`, which Udon cannot iterate - join it, then split
  it.** `foreach` over the result fails to compile with `InvalidOperationException: Cannot get element
  type on non-array types`, which names neither `foreach` nor the type, and there is no `Length` to read
  either: the registry holds `Type_SystemCollectionsGenericIEnumerableSystemString` with no
  `GetEnumerator`, and no `IEnumerator<string>` at all - the same signature-only shape as `List<T>` above.
  Exactly two externs accept it, and they are the way out: `string.Concat(keys)` and
  `string.Join(sep, keys)`. So `string.Join("\n", PlayerData.GetKeys(player)).Split('\n')` yields the
  `string[]` that every key-listing example assumes it was handed. Both halves compiled.
  (SDK 3.10.5)
- **Budget PlayerData at 100 KB per player per world, with PlayerObject a SEPARATE 100 KB**, both measured
  AFTER VRChat's server-side compression, so easily-compressible data can exceed ~300 KB raw inside the
  100 KB budget. The budget is per player, across all keys of that store together, not per key.
  (VRChat creator docs)
- **Both budgets ARE readable at runtime - as statics on `Networking`, NOT on `PlayerData` or
  `VRCPlayerApi`.** `GetPlayerDataStorageUsage(player)`, `GetPlayerDataStorageLimit()`,
  `GetPlayerObjectStorageUsage(player)` and `GetPlayerObjectStorageLimit()` each return `int` bytes, and
  `RequestStorageUsageUpdate()` asks for a recount that arrives at the parameterless
  `public override void OnPersistenceUsageUpdated()`. Checking those two obvious types and stopping there
  is what produces the confident wrong answer that no such API exists. The figures go stale between
  updates, so drive a meter or gate a save with them and size the DESIGN against the 100 KB.
  (`UdonSharpBehaviour.cs:480` plus the node registry, compiled, SDK 3.10.5)

## The player

**The whole `VRCPlayerApi` surface lives in `conduit-player.md`** - identity flags and which of them
survive syncing, movement setters and their matching getters, tracking data versus avatar bones, player
tags, language, and reaching into a player's PlayerObject. Read it before touching a player.

## World components from Udon

- **The whole `VRCObjectSync` surface: `Respawn()`, `SetGravity(bool)`, `SetKinematic(bool)`,
  `FlagDiscontinuity()`, `TeleportTo(Transform)`, and get/set for `AllowCollisionOwnershipTransfer` and
  `ForceKinematicOnRemote`.** There is no interval or network-statistics read of any kind on it.
  (SDK 3.10.5)
- **`VRCCameraSettings` reads the player's ACTUAL camera, not a scene Camera**, through
  `VRCCameraSettings.ScreenCamera` and `.PhotoCamera`. Exposed: `Position`, `Rotation`, `Forward`, `Right`,
  `Up`, `FieldOfView`, `NearClipPlane`, `FarClipPlane`, `Aspect`, `PixelWidth`, `PixelHeight`, `CullingMask`,
  `ClearFlags`, `BackgroundColor`, `DepthTextureMode`, `LayerCullDistances`, `LayerCullSpherical`,
  `AllowHDR`, `AllowMSAA`, `UseOcclusionCulling`, `StereoEnabled`, `CameraMode`, `Active`, plus
  `GetEyePosition(StereoscopicEye)` and `GetEyeRotation(StereoscopicEye)` for per-eye poses and
  `GetCurrentCamera(...)`. That is the supported way to ask where the viewer is actually looking, and it is
  (SDK 3.10.5)
- **And it is not read-only - eleven of those properties have setters**, which is the difference
  between reading where the viewer looks and actually changing what they see. The registry carries setters for `CullingMask`, `ClearFlags`, `BackgroundColor`,
  `NearClipPlane`, `FarClipPlane`, `LayerCullDistances`, `LayerCullSpherical`, `UseOcclusionCulling`,
  `DepthTextureMode`, `AllowHDR` and `AllowMSAA`. `conduit-performance.md` covers what to do with them;
  `conduit-layers.md` covers why a `CullingMask` built from a layer NAME is the wrong way to build one.
  (SDK 3.10.5)
- **`VRC_Pickup.AutoHold` IS A FOUR-MEMBER ENUM, NOT A BOOL.** `VRC_Pickup.AutoHoldMode` reads
  `AutoDetect = 0`, `Sometimes = 1`, `No = 2`, `Yes = 3` here, and `Sometimes` is documented nowhere at
  all. Read the SERIALIZED VALUE rather than treating it as on/off. `IsGlobalAutoHoldPickup()` sits beside it for when the
  resolved answer looks inconsistent. (SDK 3.10.5)
- **`VRC_Pickup.OutlineRenderers` (a `Renderer[]`) decides what the hover highlight draws around.** It
  matters for the standard INVISIBLE-PROXY rig - a door, drawer or lever whose pickup collider is an
  invisible proxy outlines that proxy, not the visible part the player is reaching for, until this is
  pointed at the real renderers. (SDK 3.10.5)
- **A behaviour that implements `Interact()` has the same `Outline Renderers` list from SDK 3.10.6**, on
  its inspector - but only the pickup's copy is in the Udon registry, so an interactable's list is
  authored, never scripted. ClientSim uses only listed renderers that are CHILDREN of the interactable;
  any other entry is skipped. (SDK 3.10.6-beta.2 registry, `ClientSimHighlightManager`)
- **With the list empty, pickups and interactables no longer outline SKINNED mesh renderers** (client
  2026.4.1+). An object whose only visible mesh is skinned shows no hover highlight until that renderer is
  listed. (VRChat SDK release notes, 3.10.6-beta.1)
- **`VRCVelocityTracker` hands Udon a velocity with no `Update` of your own:** `GetLinearVelocity()`,
  `GetAngularVelocity()`, and the physics-step `GetFixedLinearVelocity()` / `GetFixedAngularVelocity()`,
  all `Vector3`. Nothing else on it is exposed. Use it instead of differencing positions per frame.
  (SDK 3.10.6-beta.2 registry)
- **`VRCBillboard` is scriptable both as a component and as a static service.** Get and set `Facing`
  (`VRCBillboardFacing`: `Back = 0`, `Forward`, `Up`, `Down`, `Left`, `Right`), `AllowTilt`,
  `SmoothingFactor`, `UpFrom` and `MatchViewpointRotation`; or skip the component with
  `VRCBillboard.Register(transform, facing, allowTilt, smoothingFactor, upFrom, matchViewpointRotation)` and
  `VRCBillboard.Unregister(transform)`. Passing the billboarded transform as its own `upFrom` collapses to
  world up. (SDK 3.10.6-beta.2 registry, `VRCBillboardTests`)
- **`VRCPlacementZone` is fully settable at runtime:** `Center`, `Size`, `Rotation`, and one
  `PlacementPermission` per content type - `Portals`, `Stickers`, `SharingPedestals`, `AttachedItems` -
  each `Unaffected = 0`, `BlockHere = 1` or `OnlyAllowHere = 2`. (SDK 3.10.6-beta.2 registry)
- **A pickup answers who is holding it, which a station cannot.** `currentPlayer` returns the
  `VRCPlayerApi`, `currentHand` the `PickupHand`, and `IsHeld` the bool - so a held-object system needs no
  bookkeeping of its own, unlike the seated case in `conduit-player.md`. The registry also carries
  `pickupable`, `orientation`, `ExactGrip` / `ExactGun`, `ThrowVelocityBoostMinSpeed` and `Scale`,
  `MomentumTransferMethod`, `allowManipulationWhenEquipped`, and the `InteractionText` / `UseText`
  strings, so the hover prompt is reachable from script. **Note it exposes BOTH `Proximity` and
  `proximity` as separate nodes** - pick one spelling and keep to it. (SDK 3.10.5)
- **Haptics come in two forms taking the same three floats.**
  `VRCPickup.GenerateHapticEvent(duration, amplitude, frequency)` buzzes the hand holding THAT pickup;
  `VRCPlayerApi.PlayHapticEventInHand(PickupHand, duration, amplitude, frequency)` buzzes a chosen hand
  with no pickup involved. Both are local and VR-only, so neither is a signal you can test for
  (`conduit-player.md`). `player.GetPickupInHand(PickupHand)` goes the other way and hands back whatever
  that hand is currently holding. (SDK 3.10.5)
- **`DisallowTheft` makes a second player's grab do NOTHING while the first still holds it**, with no
  message and no refusal, which reads as the pickup snapping back or as a broken grab rather than as the
  deliberate lock it is.
- **Keep dev-only content out of the upload with the `EditorOnly` TAG for whole objects and
  `VRC.SDKBase.IEditorOnly` for a single component.** Unity strips a tagged GameObject and its children from
  builds; `IEditorOnly` is a marker interface with no members that makes SDK validation skip that script
  when it scans for incompatible components. Use the tag when the whole object is a test rig, the interface
  when one setup helper has to sit beside runtime components on the same object.

## Reading the SDK DLLs

**The *SDK's* DLLs only - the VRChat client is not readable and is not worth an attempt.** The client is
IL2CPP with an ENCRYPTED `global-metadata.dat` (its header is not the IL2CPP sanity magic and it yields no
type, method or string literals) and it ships no managed assemblies at all, so no client-side constant -
sync budget, rate limit, ceiling - can be read out of the install. Client behaviour is reachable only by
RUNNING it and reading its log; `conduit-testing.md` covers what that log carries, including real network
telemetry. (Client install)

To read a constant or method body out of a VRChat SDK DLL, use PowerShell with
`System.Reflection.Metadata`: `PEReaderExtensions::GetMetadataReader($pe)` for the tables, and
`$pe.GetSectionData($rva).GetContent()` for method bodies (this PS build has no `PEReader.GetMethodBody`).
Constants live in the Constant table via `FieldDefinition.GetDefaultValue()`. Token helpers are in
`System.Reflection.Metadata.Ecma335.MetadataTokens`, NOT `System.Reflection.Metadata`. Watch PowerShell
range parsing: `$il[$i+1..($i+4)]` parses as `$i + (1..($i+4))` and fails with an `op_Addition` error -
write `$il[($i+1)..($i+4)]`.
