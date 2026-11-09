# Changelog

All notable changes to Conduit are recorded here. Versions follow [Semantic Versioning](https://semver.org/).

## 1.0.5 - 2026-11-09

### Changed

- The note shown after connecting Claude, and the README, now say that a Claude session loads only the Conduit server registered for its own folder, so the session must run in the Unity project's folder to reach that editor. The other clients load every project's server in every session.

## 1.0.4 - 2026-11-09

### Changed

- The UdonSharp skill now says why behaviours with different sync modes must not share a GameObject: the GameObject carries one sync method, so a `Manual` behaviour beside a `NoVariableSync` or `Continuous` one is sent under Continuous rules and loses most of its sync budget.

## 1.0.3 - 2026-11-07

### Added

- The Conduit window is available in 13 languages: English, Japanese, Korean, Simplified and Traditional Chinese, French, German, Spanish, Italian, Brazilian Portuguese, Polish, Russian and toki pona. Pick one from the Language card at the top of the window. The choice is saved per user, survives a restart and applies in every project, and any string without a translation falls back to English.

## 1.0.2 - 2026-11-07

### Fixed

- `gameobject-component-get` no longer fails on a component with a combined flags enum, such as a Canvas. An enum is read as its index, its raw value and its name, and the value is what a write takes back.
- `object-get-data` given a component's instance id reads that component, not its GameObject.
- `assets-importer-modify` sets a texture's type through Unity's importer, so switching a non-power-of-two image to Sprite now makes a Sprite. The settings the type brings are listed in the reply.
- The ProBuilder face material, smoothing, vertex colour, vertex position and authored size writes report what each value was, read the result back after the rebuild, and say when nothing changed.
- A command that throws reports the exception type and the Conduit frames it passed through, not just its message.

## 1.0.1 - 2026-11-07

### Added

- `shader-batch-edit` and `shader-update-or-create` edit and write `.shader`, `.hlsl`, `.cginc` and `.compute` sources with the same versioned, all-or-nothing rules as the C# verbs, and work in Play Mode.
- Every shader verb returns a compiled verdict. Each shader and compute shader the change reaches, including every user of an edited include, is compiled for the editor's graphics API in every pass and stage or every kernel, with no keywords and with each keyword the changed files test. Errors and warnings come back by file, line and variant, and a ShaderLab parse error is reported as one.
- `file-restore` restores a shader source in Play Mode and returns the same verdict.
- `reimport` imports a folder placed on disk from outside the editor.

### Changed

- `udon-batch-edit` and `script-update-or-create` take C# only, and refuse a shader source with a pointer to the shader verbs.

### Fixed

- Writing into a folder that exists on disk but was not imported yet no longer creates a renamed sibling folder and fails.
- The catalogue check expects the right tool count in projects without the VRChat Worlds SDK.

## 1.0.0 - 2026-11-07

First public release.

- Ships as the `com.itsrenji.conduit` package, installable through the VRChat Creator Companion from the ItsRenjiLabs listing or by Git URL through the Unity Package Manager.
- Installing through VCC removes an older copy of Conduit under `Assets/ItsRenji/Conduit` automatically.
- 223 core bridge verbs and 74 ProBuilder verbs in a VRChat Worlds project. Avatar and plain Unity projects get the 200 core verbs that work there, with the Worlds-only verbs hidden from every discovery route.
- 33 project-aware engineering skills, installed by the Conduit window's Connect button.
- Examples ship as a Package Manager sample with their own reuse licence.
