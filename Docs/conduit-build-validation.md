# Build Panel validations and what Auto Fix actually does

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file in full before answering a Build Panel question, and ALWAYS before pressing Auto Fix or
> telling an owner to press it.**
>
> **This file owns the PANEL, the FIXES and the UPLOAD, and nothing else goes in it.** A Build Panel alert
> is not automatically this file's subject - ask what the alert is ABOUT, because the rule for fixing it
> belongs to the subject that owns the content. The values an added `VRC_SpatialAudioSource` must carry are
> `conduit-audio.md`; a mask rebuilt after a layer rewrite is `conduit-layers.md`; the triangle, texture and
> ASTC budgets are `conduit-rendering-cost.md` and this file owns only the 100 MB gate and the absence of a
> ranking; the bake is `conduit-lighting.md`. **Build & Test as an INSTRUMENT is `conduit-testing.md`** -
> what the panel blocks and what an upload does are here, what you drive afterwards to prove the world
> works is there. An open bug in `vrc-validate` goes in `conduit-todo.md`.

## The severity model

- **Three severities, one build consequence.** `VRCSdkControlPanelBuilder` publishes
  `OnGUIError`, `OnGUIWarning`, `OnGUIInformation` and `OnGUILink`, each taking the offending object and
  optional `show` and `fix` actions. RED (`OnGUIError`) blocks Build & Test and Build & Upload. YELLOW and
  WHITE do not block by colour alone. Report severity and build consequence SEPARATELY - "this is a warning"
  and "this stops the upload" are different answers and only one of them is usually true.
  (`VRCSdkControlPanelBuilder.cs:184`, SDK 3.10.5)
- **Some red states are produced before the validation list runs**, through the builder-availability path
  rather than `OnGUIError`. An SDK that did not load, a missing scene descriptor, or multiple pipeline
  managers block the build without appearing as ordinary validation entries, so "the validation list is
  empty" is not the same as "this world builds".
- **The blocking errors no other file covers:** more than one `VRC_SceneDescriptor`; any `VRC_EventHandler`
  in the scene; SDK2 and SDK3 components together; and no valid spawn point (`WORLD_SPAWN_EMPTY_ERROR`),
  which also RETURNS and so skips every later spawn check. Each is an `OnGUIError` that stops both builds,
  and only the Event Handler one offers a fix - it deletes every handler in the scene.
- **An iOS build target errors only for an account WITHOUT iOS permission**, because the check is gated on
  `ApiUserPlatforms.CurrentUserPlatforms.SupportsiOS`. It is an entitlement message rather than a
  statement that VRChat has no iOS, so it will not reproduce on an account that has it.

## Auto Fix has no safety contract

- **An Auto Fix button means a callback exists, not that the mutation is safe.** The signature is
  `OnGUIWarning(Object subject, string output, System.Action show, System.Action fix)` - `fix` is an
  arbitrary action supplied by whoever wrote that check. Nothing validates it against the world's authored
  intent, and several shipped ones rewrite state far wider than the alert implies. Read what the specific
  fix does before pressing it; a cleared warning is not evidence of a correct world.
- **The fixes worth refusing outright, each read from the installed SDK.** `WorldBuilder.cs` below is
  `VRCSdkControlPanelWorldBuilder.cs` in the worlds package and `ControlPanelBuilder.cs` is
  `VRCSdkControlPanelBuilder.cs` in the base package; the line numbers are a shortcut to the named symbol
  and not the locator, per the re-derive section at the end of this file.

| Fix | What it actually does | Source |
| --- | --- | --- |
| Built-in mesh referenced by Udon | Clones meshes into a `<scene>_MeshClones` folder AND rewires Udon public variables | `WorldBuilder.cs:1183` |
| Oversized textures | Sets importer `maxTextureSize` to `MAX_SDK_TEXTURE_SIZE` (literally 8192), reserializes and reimports | `WorldBuilder.cs:905`, `ControlPanelBuilder.cs:65` |
| Box mipmap filtering | Sets `mipmapFilter = KaiserFilter` on every affected importer, reserializes | `WorldBuilder.cs:929` |
| Billboard particle roll | Sets `allowRoll = false` on ALL matching renderers at once | `WorldBuilder.cs:826, 855` |
| Deprecated ONSP audio | Copies the ONSP gain, near, far, spatialization and inverse-square flags onto `VRC_SpatialAudioSource`, then destroys the ONSP component | `WorldBuilder.cs:625`, `ConvertONSPAudioSource` |
| Missing spatial audio | Adds `VRC_SpatialAudioSource` carrying the SDK's own room or avatar gain and range constants, not the AudioSource's authored range | `WorldBuilder.cs:639`, `AddVRCSpatialToBareAudioSource` |
| Super-sampled UI | Substitutes `VRCSuperSampledUIMaterial.mat` where `AssetDatabase.IsMainAsset` is false, else changes a project material's shader IN PLACE under `Undo.RecordObject` | `WorldBuilder.cs:778-803` |
| Android texture format | Sets `EditorUserBuildSettings.androidBuildSubtarget = ASTC`, triggering a full reimport | `WorldBuilder.cs:3426` |
| Automatic lightmap generation | Sets `giWorkflowMode = OnDemand` | `ControlPanelBuilder.cs:596` |
| Layers and collision setup | Rewrites PROJECT-WIDE layer and collision configuration | `WorldBuilder.cs:2676` |

- **The audio one is the quiet danger, with one exception.** On a 3D source the fix writes the SDK's own
  room gain and range and reports only that a warning cleared, so pair that by hand - `conduit-audio.md`
  owns the values. On a source `IsAudioSource2D` reads as 2D it adds the component DISABLED, which is what
  a deliberately 2D sound wanted anyway.
- **The ONSP check reports ONE source however many carry it** - the loop `break`s at the first hit, so
  fixing that one and rebuilding raises the same warning again for the next. Treat the single entry as
  "at least one", never as a count, and sweep the scene for `ONSPAudioSource` instead.
- **Layer setup is project-wide and can move custom layers down the list**, so re-check every layer
  assignment and every hard-coded mask afterwards - see `conduit-layers.md` for why a mask built from a name
  fails silently.
- **The super-sampled UI warning has a safe manual answer, which is why its Auto Fix is never needed.**
  The SDK ships the material the fix wants at
  `Packages/com.vrchat.worlds/Editor/VRCSDK/SDK3/VRCSuperSampledUIMaterial.mat` (confirmed present).
  Assign it yourself to the uGUI `Graphic`s still on `UI/Default`, and the alert clears without the fix
  reaching a material somebody authored deliberately. (SDK 3.10.5)

## Mutations the SDK makes with NO alert at all

The table above is the mutations you can SEE and choose. Validation ALSO rewrites content silently,
outside the alert list entirely, so **a clean Build Panel is not evidence that the uploaded world matches
the scene you built**:

- **A PlayableDirector whose Timeline controls a prefab is destroyed** - the whole component, and one
  Control clip is enough (`conduit-world-components.md`).
- **A Unity `VideoPlayer` set to direct audio output has an `AudioSource` added to its GameObject** and
  every audio track retargeted onto it (`conduit-video.md`).
- **Neither produces a Build Panel entry**, so the way to catch either is the log line it writes or the
  uploaded world itself - never a re-read of the validation list.

## Gating a build from your own code

- **`IVRCSDKBuildRequestedCallback` can ABORT the upload, and that is the supported place to enforce your
  own rules.** It lives in `VRC.SDKBase.Editor.BuildPipeline`, goes on an editor-side class in an `Editor`
  folder (never on a scene component), and **returning `false` from `OnBuildRequested` stops the build**.
  `VRCSDKRequestedBuildType` distinguishes `Avatar` from `Scene`; a world build requests `Scene`, so gate
  on that and return `true` for anything else. Use it for the checks the SDK cannot know about - an unwired
  reference, a scene-specific invariant, a placeholder url that must never ship.
- **There are TWO overloads, and the second one is the useful one:**
  `OnBuildRequested(VRCSDKRequestedBuildType)` and
  `OnBuildRequested(VRCSDKRequestedBuildType, BuildTarget)`. The second hands you the PLATFORM, which is
  what a rule that differs between PC and Android needs. Third-party references document only the first.
  (SDK 3.10.5)
- **`IPreprocessCallbackBehaviour.OnPreprocess()` RETURNS `bool`, NOT `void` - VRChat's own documentation
  page is wrong.** The interface in the loaded assembly declares `OnPreprocess() : Boolean` alongside
  `PreprocessOrder : Int32`, so writing the `void` form the official page shows does not compile against
  the interface. The meaning of the return value is undocumented; return `true` unless you have
  established otherwise. This one goes on a `MonoBehaviour` on the content itself, so pair it with
  `IEditorOnly` to keep the helper out of the component whitelist scan.
  (`VRC.SDKBase.IPreprocessCallbackBehaviour`, SDK 3.10.5)

## After the upload

- **An existing instance keeps running the old build - uploading does not update it.** Only instances
  created AFTER the upload carry the new world, so re-entering the instance you were already testing in
  shows the bug you just fixed, unchanged. That reads as "the fix did not work" and sends people back to
  re-debug working code. Close the instance and make a new one before concluding anything about an upload.
- **The one hard ceiling is 100 MB: an Android world build larger than that AFTER compression cannot be
  uploaded OR accessed.** It is the only enforced size gate in the list, so it is the only one worth
  treating as a wall. Triangle counts, draw calls and material counts have NO upload gate at all.
- **VRChat has no world performance RANKING - do not invent a threshold.** Avatars get the
  Excellent/Good/Medium/Poor badge; worlds get nothing, and the SDK blocks no upload on polygon count or
  draw calls. VRChat's only published world figure is a recommendation to budget roughly 250,000 triangles
  for a whole Quest world, and a "target" quoted anywhere below that is community guesswork. Quote the
  recommendation as a recommendation, and set the real budget from a measurement on the device.
- **SDK 3.10.5 scans builds for DUPLICATE `PipelineManager` components and a GameObject can no longer
  carry two.** A scene that uploaded before can therefore fail now, and the fix is deleting the extra
  component rather than anything about the world. (VRChat SDK release notes, 3.10.5-beta.1)
- **The Blueprint ID on `VRC_PipelineManager` is what decides UPDATE versus new world**, and it is scene
  data like anything else. Same ID overwrites the existing listing; a fresh ID publishes a second world
  that shares the name and splits the audience. Check it before uploading a scene that was duplicated from
  another - a copied scene carries the original's ID, so the copy overwrites the original's listing.

## Publishing: the parts that are not a Build Panel alert

- **Content warnings are a moderation obligation, not a tagging preference - an unmarked world can be
  deleted and the account restricted.** The flags cover sexually suggestive content, adult language and
  themes, graphic violence, excessive gore and extreme horror. Mark everything that applies, and mark it
  when in doubt: over-marking costs some search visibility, under-marking can cost the world itself.
- **They are scene data, so a duplicated scene arrives carrying the original's answers.**
  `VRCSceneDescriptor` serializes `contentSex`, `contentViolence`, `contentGore`, `contentOther` and
  `NSFW` beside `releasePublic` and `capacity` - the same trap as the Blueprint ID above, and worth
  re-checking for the same reason. (`VRC.SDK3.Components.VRCSceneDescriptor` members, SDK 3.10.5)
- **A cross-platform world is two builds under one Blueprint ID - the ID is what joins them.** A fresh
  id publishes a second world rather than the Quest half of the first, and the platform you skip simply
  has no build, which is why "Quest users cannot join" is usually a missing second upload rather than a
  compatibility fault.
- **The SDK will drive both from one press rather than making you switch by hand.**
  `VRCMultiPlatformBuild` holds a platform LIST, an initial and next platform, a built count and an
  explicit `Switching` state, so the panel builds one target, switches the editor to the next, and
  continues - across the domain reloads that a platform switch forces, which is why it keeps its state in
  `SessionState`. Expect a long unattended run and a reimport, not a quick second upload.
  (`VRCMultiPlatformBuild.cs:15-69`, SDK 3.10.5)
- **"My world is not in search" is almost always release status or indexing, not a fault.** A world starts
  Private and Public is a separate change on the website, and search indexing lags the upload. Check the
  status and reach the world by direct link before investigating anything else.

## Re-deriving this after an SDK update

The catalogue above is a snapshot, and the SDK moves: every line number here differs from the same alerts in
3.10.4. Re-derive rather than trusting the numbers:

```
grep -rn "OnGUIError\|OnGUIWarning\|OnGUIInformation" Packages/com.vrchat.worlds Packages/com.vrchat.base --include=*.cs
```

Each hit carries the message text and, where a fix exists, the action immediately after it. Read the action,
not the message, when deciding whether a fix is safe.

**And read the CONDITION wrapping the call, because the message never states it - a guard can narrow an
alert to a subset of accounts, or make it dead code, and both read identically from the string.** One
specimen of each: "iOS is not supported as a build target" fires only where
`ApiUserPlatforms.CurrentUserPlatforms.SupportsiOS` is false, so it is an entitlement message rather than
a platform fact; and the multiple-`PipelineManager` `OnGUIError` can never be reached in the state it
describes, because `IsValidBuilder` refused the builder before the validation list ran - which is why that
case is documented above as a builder-availability state instead of an alert.

## Reading the validations through Conduit

- **`vrc-validate` returns every alert the SDK collected, with its severity and the object it points at**,
  so a validation answer never needs the owner reading the panel aloud. The first line is the verdict, and
  it counts build-blocking errors separately from advisories and names how many offer an Auto Fix. **It
  never presses one** - see above for why that is the contract rather than a missing feature.
- **It needs the Control Panel window, and surfaces it itself when it is shut.** The alerts exist only
  because something asked the panel's builder to draw them: the builder is reached through the SDK's own
  `VRCSdkControlPanelBuilder.TryGetBuilder<IVRCSdkWorldBuilderApi>` and the list is produced by
  `CreateValidationsGUI`, so there is no panel-free route to it and reflecting over the panel's fields
  instead is reimplementing a sanctioned accessor. A closed panel costs one window being opened, not a
  refusal, and the answer still comes back in the same call. (SDK 3.10.5)
- **The SDK emits "Everything looks good" as an INFORMATION alert**, so it is counted among the advisories
  like any other entry - 3 advisories can be two real notes plus that line. Read the entries, not the count.
- **`editor-window-close VRCSdkControlPanel` puts the panel away again** once the answer is in hand, so a
  validation check does not have to leave it covering the layout.
