# Changelog

All notable changes to Conduit are recorded here. Versions follow [Semantic Versioning](https://semver.org/).

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
