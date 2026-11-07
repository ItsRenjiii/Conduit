# Conduit TODO - open work

> Conduit conventions - subject file. Index: `conduit-instructions.md`.
> **Read this file before picking up work on Conduit itself, and when one of the known conditions below
> shows up while you are working.**
>
> **This file owns what is UNFINISHED, and nothing else goes in it.** The moment something is settled its
> RULE moves to the subject that owns it and the entry is deleted - never annotated as done. The reverse
> applies just as hard: a subject file may carry a two-line warning about an open condition, but the
> investigation lives here, and a subject file pointing at this one for an entry that does not exist is a
> dangling pointer. How to choose and write a fix is `conduit-bridge-authoring.md`; this file only says
> what is still wrong.

This is the backlog: open bugs in the bridge, undiagnosed conditions, unanswered questions, and wanted
verbs.

**Rules for this file.** An entry earns its place by being actionable: it says what is wrong or unknown,
what has already been observed, and what to do next. **Delete an entry the moment it lands** - do not
annotate it as done, do not keep a "previously listed and now BUILT" trophy shelf. Implement anything here
to the bar in `conduit-bridge-authoring.md`: truthful specific feedback, never report success for a no-op,
atomic validation before mutation. Reconnect the MCP and exercise the real path once before calling it done.

**Verify an entry still reproduces before fixing it.** Entries outlive the behaviour they describe. A
clearing pass found two already fixed, one describing a whole missing verb for a gap that had since closed -
building it would have shipped a second way to do something the bridge already did - and one whose stated
cause was wrong. Reproduce first; if it does not reproduce, delete the entry and record what is actually
true instead.

**An entry must carry its exact reproduction - the call, its target, and the response verbatim.** Write a
command line a reader can paste to get a yes or no in one attempt. A stated conclusion ("X cannot reach Y")
drops the conditions that produced it, which is precisely what the rule above needs, so the entry can be
neither confirmed nor retired and survives on faith instead of evidence.

---

### Typed tools escape backslashes and pipes that no handler unescapes

**A typed call to a verb `ClassifyParseMode` files under `escaped-pipe-sections` sends every backslash
doubled and every pipe as `\|`, and nothing reverses either.** `ConduitToolCatalog.ParseSignature` runs
each value through `ConduitSectionCodec.EncodeSection`, and the `udon-exposure` and `editor-window-key`
repairs call it directly; `ConduitSectionCodec.Decode` has no caller, and `SplitPipeArgs` cuts at every
pipe. Verbs in the other modes, `project-search`, and anything sent through `conduit-run` are unaffected.

- Reproduce with typed `gameobject-component-get`, target `Probe\Path`, component `Transform`: it replies
  `GameObject not found: Probe//Path`, where `conduit-run gameobject-component-get Probe\Path | Transform`
  replies `GameObject not found: Probe/Path`. Typed target `Probe|Path` replies `GameObject not found: Probe/`.
- Expect JSON sections to change silently: the backslash of a JSON escape is doubled too, so `\n` arrives as
  a literal backslash and `n` and `\\` as two backslashes, with no error; only `\"` fails loudly, by ending
  its string early.
- Choose the direction before fixing, because the two consistent states break different things. Dropping
  the escape layer delivers every typed value as sent and leaves a pipe inside a value splitting its
  section, as raw text does now. Routing every sectioned parser through `Decode` protects typed pipes but
  changes the raw dialect, where a `\\` or `\|` - a JSON escape, a regex escape, a UNC path - collapses.
- Keep `project-search` on its own builder, `RepairProjectSearch`, either way: its handler reads the whole
  first section as a regex and finds the options at the last pipe, so it must keep receiving raw text.

### `gameobject-component-get` throws on a Canvas

**Reading a `Canvas` fails with an index error, while writing the same component works.**

- Reproduce: `gameobject-component-get <object holding a root world-space Canvas> | Canvas` replies
  `tool 'gameobject-component-get' failed: Index was outside the bounds of the array.` Observed on two root
  world-space canvases, in edit mode and in play mode alike.
- The component resolves: `gameobject-component-modify <same object> | Canvas | {"m_SortingOrder":10}`
  replied `Set m_SortingOrder = 10 (was 1)`, so the failure is in the read's serialisation, not the lookup.
- Next: reproduce on a fresh `GameObject > UI > Canvas`, take the stack from the editor log, and check which
  serialised property of `Canvas` the reader indexes past the end of.

### `object-get-data` given a component's instance id returns its GameObject

**`object-get-data <id>` with an id `gameobject-component-list` printed for a component answers with the
owning GameObject instead, and does not say so.**

- Reproduce: `gameobject-component-list <object holding a Canvas>` prints `{"type":"UnityEngine.Canvas",
  "instanceID":N}`; `object-get-data N` replies `{"object":{... "type":"UnityEngine.GameObject"}` with the
  GameObject's fields (`m_Layer`, `m_Name`, `m_IsActive`), and no Canvas field anywhere.
- Next: resolve a component id to the component, or refuse with a message naming what it resolved to, so a
  read of the wrong object can never pass for the right one.

### `assets-importer-modify` switching a texture to Sprite leaves its NPOT scaling, so no Sprite is made

**Writing `m_TextureType` 8 through the importer leaves `m_NPOTScale` at ToNearest, and a non-power-of-two
image then imports with no Sprite sub-asset.**

- Reproduce on a 64 x 40 PNG imported as Default: `assets-importer-modify <png> | {"m_TextureType":8,
  "m_SpriteMode":1,"m_AlphaIsTransparency":1,"m_EnableMipMap":0}` reports the four settings changed and
  reimports; `set-reference <object> | Image | m_Sprite | <png>` then replies `Asset at <png> is Texture2D,
  not assignable to field type Sprite.`, and `assets-importer-get` shows `"m_NPOTScale":1`.
- After `assets-importer-modify <png> | {"m_NPOTScale":0}` the same `set-reference` replies
  `Set Image.m_Sprite on <object>: null -> <name> (Sprite #<id>).`
- Next: when a write makes `m_TextureType` Sprite, apply the dependent settings Unity's
  `TextureImporter.textureType` setter applies (NPOT scale None among them), or warn that the NPOT setting
  blocks the Sprite.

### ProBuilder writes report the value set and never the value before it

**The `probuilder-*` writes say what they set but not what was there, so a reply cannot show whether the call
changed anything.** Every other write verb reads each value before the write and again afterwards. These were
left out only because ProBuilder was absent where that change was compiled, so `CONDUIT_PROBUILDER` compiled
them out and nothing could be built or tested.

- Read in `ConduitTools.ProBuilder.cs`: `ProBuilderFaceMaterialCommand` replies `Assigned '<material>' to N
  face(s)` without the faces' previous materials, `ProBuilderSmoothingSetCommand` `Set smoothing group G on N
  face(s)` without their previous groups, `ProBuilderVertexColorSetCommand` `Set vertex colour #rrggbb on N of
  M vertex position(s)` without the previous colours; the authored-size, face-mode and vertex-move replies
  are the same shape. The X-Ray toggle already reports `was`.
- Reproduce in a project with ProBuilder: `probuilder-smoothing-set <mesh> | 1 | 0` twice gives the same reply
  both times, although the second call changed nothing.
- Next: read each face's or vertex's value before the write and again after `RebuildProBuilderMesh`, and
  report old -> new or already at value, as `gameobject-component-modify` does.

### Wanted: a versioned edit verb for text assets that are not C#

**Give shaders, includes, stylesheets and documents the same versioned exact-string edit path `.cs` files
have, so their history lands in Conduit's own pruned store instead of the client's.** `udon-batch-edit` and
`script-update-or-create` are built around compilation inputs, so a `.shader`, `.hlsl`, `.cginc`, `.uss` or
`.md` change goes through the client's native file tools, whose checkpoint feature copies the whole file
into the user's profile drive before every edit and never prunes it.

- Observed (Claude Code, 2026-10-17): `C:\Users\<user>\.claude\file-history` held 15.66 GB, of which
  326 copies were one scene file (`VehicleTest.unity`, about 48 MB each); the rest totalled about 100 MB.
  The user keeps client cleanup disabled on purpose, so the client never reclaims it.
- Next: confirm whether `udon-batch-edit` already accepts these extensions (send one exact-string edit to a
  scratch `.shader` and read the reply); if it refuses, add the extensions or a sibling verb with the same
  rules (each file read first, atomic all-or-nothing, version history with pruning, shader reimport rather
  than a script compile).

### Wanted: a scene-safe path for the text edits agents make to `.unity` files

**Find out which scene changes still send agents to the native file tools, and give each one a verb that
edits inside Unity and saves through Unity.** Every native edit of a scene copies the whole scene into the
client's checkpoint store (the 326 copies above), and a text edit of serialised YAML can also leave a scene
Unity no longer loads cleanly.

- Next: list the cases from transcripts (GUID or reference swaps, bulk renames, removing broken components,
  moving objects between scenes) and check each against the existing verbs (`assets-replace-references`,
  `rename-object`, `gameobject-remove-missing-scripts`, `gameobject-set-parent`); add a verb only for a case
  none of them covers.

### Wanted: EasyRoads3D verbs, as a soft dependency like ProBuilder

**Add `easyroads-*` verbs that lay roads and junctions through the EasyRoads3D v3 Pro scripting API
(`EasyRoads3Dv3.ERRoadNetwork`, `ERRoad`, `ERConnection`), compiled only when that assembly is present.** Road
networks are currently built with scratch editor code. Facts about EasyRoads itself belong to the project using it,
not to these documents. The one rule a verb must carry: check that a road's cross-section matches a crossing's before
joining them and say so, since EasyRoads refuses a mismatched join with a bare `false`.
