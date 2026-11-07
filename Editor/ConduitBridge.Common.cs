/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;


namespace ItsRenji.Conduit
{
    /// <summary>
    /// Shared helpers: parsing, path normalisation, object lookup and reflection.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Asset paths
        /// <summary>
        /// Validates a writable script path under project Assets or a project-local embedded package.
        /// </summary>
        /// <param name="assetPath">Normalized project-relative script path.</param>
        /// <param name="error">Specific refusal when the destination is not writable project content.</param>
        /// <returns>True when a script command may mutate the destination.</returns>
        private static bool TryValidateWritableScriptAssetPath(string assetPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                error = "Script path is empty.";
                return false;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(assetPath);
            }
            catch (Exception ex)
            {
                error = "Script path is invalid: " + ex.Message;
                return false;
            }

            if (assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                string assetsRoot = Path.GetFullPath("Assets").TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string assetsPrefix = assetsRoot + Path.DirectorySeparatorChar;
                if (fullPath.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase)) return true;

                error = "Script path escapes the project Assets folder: " + assetPath;
                return false;
            }

            if (!assetPath.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                error = "Script path must be under Assets/ or a writable embedded Packages/<id>/ root: " + assetPath;
                return false;
            }

            string[] parts = assetPath.Split('/');
            if (parts.Length < 3 || string.IsNullOrWhiteSpace(parts[1]))
            {
                error = "Package script path must include a package id and file path: " + assetPath;
                return false;
            }

            string packageRoot = "Packages/" + parts[1];
            string packageDiskRoot = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string packagePrefix = packageDiskRoot + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                error = "Script path escapes its package root: " + assetPath;
                return false;
            }

            if (!Directory.Exists(packageRoot) || !File.Exists(packageRoot + "/package.json"))
            {
                error = "Package root is not a physical project package with package.json: " + packageRoot;
                return false;
            }

            UnityEditor.PackageManager.PackageInfo matchedPackage = null;
            UnityEditor.PackageManager.PackageInfo[] registeredPackages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            foreach (UnityEditor.PackageManager.PackageInfo package in registeredPackages)
            {
                if (!string.Equals(package.assetPath, packageRoot, StringComparison.OrdinalIgnoreCase)) continue;
                matchedPackage = package;
                break;
            }

            if (matchedPackage == null || matchedPackage.source != UnityEditor.PackageManager.PackageSource.Embedded)
            {
                error = "Package is not registered as an embedded package: " + packageRoot;
                return false;
            }

            string resolvedRoot = Path.GetFullPath(matchedPackage.resolvedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(resolvedRoot, packageDiskRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "Package resolves outside its project-local folder and will not be modified: " + packageRoot;
                return false;
            }

            return true;
        }

        /// <summary>Normalizes an asset path to Unity's forward-slash project-relative form.</summary>
        /// <param name="assetPath">Raw asset path, optionally absolute or quoted.</param>
        /// <returns>Normalized asset path, or an empty string when the input is blank.</returns>
        private static string NormalizeAssetPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return string.Empty;
            }

            assetPath = assetPath.Trim().Trim('"').Replace('\\', '/');
            var projectRoot = Directory.GetCurrentDirectory().Replace('\\', '/').TrimEnd('/');
            if (assetPath.StartsWith(projectRoot + "/", StringComparison.OrdinalIgnoreCase))
            {
                assetPath = assetPath.Substring(projectRoot.Length + 1);
            }

            return assetPath;
        }

        /// <summary>Converts a possibly project-relative path into a normalized absolute filesystem path.</summary>
        /// <param name="path">Raw path, optionally quoted or relative to the project root.</param>
        /// <returns>Normalized absolute path, or an empty string when the input is blank.</returns>
        private static string NormalizeProjectRelativePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            path = path.Trim().Trim('"').Replace('\\', '/');
            var projectRoot = Directory.GetCurrentDirectory().Replace('\\', '/').TrimEnd('/');
            if (!Path.IsPathRooted(path))
            {
                path = projectRoot + "/" + path;
            }

            return path;
        }

        /// <summary>Highest line count one script-read slice returns in a single response.</summary>
        private const int MAX_SCRIPT_READ_LINES = 500;

        #endregion

        #region Command arguments

        /// <summary>Returns the trimmed text after the command verb in a raw command line.</summary>
        /// <param name="command">Raw command line.</param>
        /// <param name="verb">Command verb at the start of the line.</param>
        /// <returns>Text after the verb, or an empty string when no remainder exists.</returns>
        private static string GetCommandRemainder(string command, string verb)
        {
            if (string.IsNullOrEmpty(command) || command.Length <= verb.Length)
            {
                return string.Empty;
            }

            return command.Substring(verb.Length).Trim();
        }

        /// <summary>
        /// Returns the text after the command verb with leading whitespace removed and trailing whitespace
        /// left alone, for a verb whose last section is a file's whole content.
        /// </summary>
        /// <remarks>
        /// <see cref="GetCommandRemainder"/> trims both ends, which every token-parsing verb wants and which
        /// silently cost a written file its final newline: the content arrived here already stripped, so no
        /// amount of care in the handler could put it back.
        /// </remarks>
        /// <param name="command">Raw command line.</param>
        /// <param name="verb">Command verb at the start of the line.</param>
        /// <returns>Text after the verb, or an empty string when no remainder exists.</returns>
        private static string GetVerbatimCommandRemainder(string command, string verb)
        {
            if (string.IsNullOrEmpty(command) || command.Length <= verb.Length)
            {
                return string.Empty;
            }

            return command.Substring(verb.Length).TrimStart();
        }

        /// <summary>Splits a pipe-separated command argument string into trimmed sections.</summary>
        /// <param name="args">Raw argument text.</param>
        /// <returns>Trimmed pipe sections, or an empty array for blank input.</returns>
        private static string[] SplitPipeArgs(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return new string[0];
            }

            var raw = args.Split(new[] { '|' }, StringSplitOptions.None);
            var pieces = new List<string>();
            foreach (var piece in raw)
            {
                pieces.Add(piece.Trim());
            }

            return pieces.ToArray();
        }

        /// <summary>Parses a positive integer argument with NO upper bound. Use the three-argument overload to bound it.</summary>
        /// <param name="value">Text to parse.</param>
        /// <param name="fallback">Value returned when parsing fails or the value is below one.</param>
        /// <returns>Parsed positive integer, or <paramref name="fallback"/>.</returns>
        private static int ParsePositiveInt(string value, int fallback)
        {
            return ParsePositiveInt(value, fallback, int.MaxValue);
        }

        /// <summary>
        /// Parses a positive integer argument and bounds it by an explicit ceiling.
        /// <para>
        /// The ceiling is per CALL SITE deliberately. This helper once ended in a blanket
        /// Math.Min(parsed, 500), which is correct for a log COUNT and silently WRONG for every other kind
        /// of argument sharing it: script-read returned line 500 for ANY start line above 500, an explicit
        /// screenshot size of 1920x1080 rendered 500x500, and the profiler's own default capacity of 2000
        /// was cut to 500. All three failed silently and only for EXPLICIT values, because the fallback
        /// path skipped the clamp - which is exactly why it went unnoticed. Pass the limit the call site
        /// really has, or take the unbounded overload above.
        /// </para>
        /// </summary>
        /// <param name="value">Text to parse.</param>
        /// <param name="fallback">Value returned when parsing fails or the value is below one.</param>
        /// <param name="max">Highest value this call site can accept.</param>
        /// <returns>The parsed or fallback value, never above <paramref name="max"/>.</returns>
        private static int ParsePositiveInt(string value, int fallback, int max)
        {
            int parsed;
            if (!int.TryParse(value, out parsed) || parsed < 1)
            {
                return Math.Min(fallback, max);
            }

            return Math.Min(parsed, max);
        }

        #endregion

        #region Scene object lookup

        /// <summary>Reports whether a target string means the scene root or no parent.</summary>
        /// <param name="target">Target string to inspect.</param>
        /// <returns>True when the target is blank or one of the root aliases.</returns>
        private static bool IsRootTarget(string target)
        {
            if (string.IsNullOrWhiteSpace(target))
            {
                return true;
            }

            target = target.Trim().Trim('"').ToLowerInvariant();
            return target == "/" || target == "root" || target == "scene" || target == "none" || target == "null";
        }

        /// <summary>
        /// Resolves a GameObject by instance id, path, or name. Path and name lookups search the open prefab
        /// stage alone when one is open, otherwise every loaded scene. A target may be scene-qualified as
        /// SceneName:/Path/To/Object or SceneName:ObjectName, where the scene is a name, path or opened-scene
        /// index; the text before the first colon counts as a scene only when it resolves to one, because an
        /// object name may itself contain a colon. An exact path or a name shared by several objects is refused
        /// with every match listed, never resolved to whichever comes first.
        /// </summary>
        /// <param name="target">Instance id, path, name, or scene-qualified path or name.</param>
        /// <param name="gameObject">Resolved GameObject when lookup succeeds.</param>
        /// <returns>Null on success, or a user-facing lookup error.</returns>
        private static string TryFindGameObject(string target, out GameObject gameObject)
        {
            gameObject = null;
            if (string.IsNullOrWhiteSpace(target))
            {
                return "Missing GameObject target. Use a path like /Root/Child, a scene-qualified path like SceneName:/Root/Child, or an instance id from scene-tree.";
            }

            target = target.Trim().Trim('"').Replace('\\', '/');

            int instanceId;
            if (int.TryParse(target, out instanceId))
            {
                var unityObject = EditorUtility.InstanceIDToObject(instanceId);
                if (unityObject is GameObject)
                {
                    gameObject = (GameObject)unityObject;
                    return null;
                }

                if (unityObject is Component)
                {
                    gameObject = ((Component)unityObject).gameObject;
                    return null;
                }

                return "No GameObject found for instance id: " + instanceId;
            }

            // A target may be qualified as SceneName:/Path or SceneName:Name. Only a prefix that resolves to a
            // scene counts, since an object name may contain a colon. An open prefab stage is the whole search
            // space and takes no qualification.
            var stageObjects = PrefabStageGameObjects();
            var lookup = target;
            var qualified = false;
            var searchScene = default(Scene);
            var colon = target.IndexOf(':');
            if (stageObjects == null && colon > 0)
            {
                var sceneText = target.Substring(0, colon).Trim();
                var remainder = target.Substring(colon + 1).Trim();
                string sceneAmbiguity;
                var prefixScene = FindSceneLoose(sceneText, out sceneAmbiguity);
                if (sceneAmbiguity != null)
                {
                    return sceneAmbiguity + Environment.NewLine + "Qualify the target with the scene path or index, or use the instance id.";
                }

                if (prefixScene.IsValid())
                {
                    if (!prefixScene.isLoaded)
                    {
                        return "Scene '" + prefixScene.name + "' is listed but not loaded, so '" + target + "' cannot be resolved. scene-open it additive first.";
                    }

                    if (remainder.Length == 0)
                    {
                        return "Target '" + target + "' names no object after the scene. Use SceneName:/Path/To/Object or SceneName:ObjectName.";
                    }

                    searchScene = prefixScene;
                    qualified = true;
                    lookup = remainder;
                }
            }

            var normalizedTarget = NormalizeObjectPathForCompare(lookup);
            var pathMatches = new List<GameObject>();
            var nameMatches = new List<GameObject>();
            IEnumerable<GameObject> candidates;
            if (stageObjects != null) candidates = stageObjects;
            else if (qualified) candidates = EnumerateSceneGameObjects(searchScene);
            else candidates = EnumerateLoadedScenesGameObjects();

            foreach (var candidate in candidates)
            {
                if (NormalizeObjectPathForCompare(GetGameObjectPath(candidate)) == normalizedTarget)
                {
                    pathMatches.Add(candidate);
                }

                if (candidate.name == lookup)
                {
                    nameMatches.Add(candidate);
                }
            }

            if (pathMatches.Count == 1)
            {
                gameObject = pathMatches[0];
                return null;
            }

            if (pathMatches.Count > 1)
            {
                return DescribeAmbiguousMatches("path", lookup, pathMatches);
            }

            if (nameMatches.Count == 1)
            {
                gameObject = nameMatches[0];
                return null;
            }

            if (nameMatches.Count > 1)
            {
                return DescribeAmbiguousMatches("name", lookup, nameMatches);
            }

            return "GameObject not found: " + target + (qualified ? " (searched scene " + searchScene.name + ")" : string.Empty);
        }

        /// <summary>Enumerates every GameObject under every root of one scene, including inactive children.</summary>
        /// <param name="scene">The scene to walk; an invalid or unloaded scene yields nothing.</param>
        /// <returns>Sequence of the scene's GameObjects.</returns>
        private static IEnumerable<GameObject> EnumerateSceneGameObjects(Scene scene)
        {
            if (!scene.IsValid() || !scene.isLoaded) yield break;

            foreach (var root in scene.GetRootGameObjects())
            {
                var transforms = root.GetComponentsInChildren<Transform>(true);
                foreach (var transform in transforms)
                {
                    yield return transform.gameObject;
                }
            }
        }

        /// <summary>Enumerates every GameObject in every loaded scene; scenes that are listed but unloaded are skipped.</summary>
        /// <returns>Sequence of GameObjects across all loaded scenes, in scene order.</returns>
        private static IEnumerable<GameObject> EnumerateLoadedScenesGameObjects()
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var gameObject in EnumerateSceneGameObjects(scene))
                {
                    yield return gameObject;
                }
            }
        }

        /// <summary>Lists every GameObject in the open prefab stage, including inactive children.</summary>
        /// <returns>The stage's GameObjects, or null when no prefab stage is open.</returns>
        private static GameObject[] PrefabStageGameObjects()
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null || stage.prefabContentsRoot == null) return null;

            var transforms = stage.prefabContentsRoot.GetComponentsInChildren<Transform>(true);
            var result = new GameObject[transforms.Length];
            for (var i = 0; i < transforms.Length; i++)
            {
                result[i] = transforms[i].gameObject;
            }

            return result;
        }

        /// <summary>
        /// Enumerates the GameObjects of the current editing context for a target lookup: the open prefab
        /// stage's contents when a prefab is open in isolation (so path and name resolution operate on the
        /// prefab being edited, matching what the Hierarchy shows), otherwise every loaded scene. Without the
        /// stage rule a path like "Root/Child" resolved against the scene instance while a prefab stage was
        /// open, so an edit meant for the prefab silently became an unsaved scene-instance override.
        /// </summary>
        /// <returns>Sequence of GameObjects in the open prefab stage, or in all loaded scenes when none is open.</returns>
        private static IEnumerable<GameObject> EnumerateContextGameObjects()
        {
            var stageObjects = PrefabStageGameObjects();
            if (stageObjects != null) return stageObjects;

            return EnumerateLoadedScenesGameObjects();
        }

        /// <summary>
        /// Enumerates the GameObjects a scene-scoped search covers: the open prefab stage's contents when a
        /// prefab is open in isolation, otherwise the one scene the caller named.
        /// </summary>
        /// <param name="scene">The loaded scene to search when no prefab stage is open.</param>
        /// <returns>Sequence of GameObjects in the open prefab stage, or in that scene when none is open.</returns>
        private static IEnumerable<GameObject> EnumerateContextGameObjects(Scene scene)
        {
            var stageObjects = PrefabStageGameObjects();
            if (stageObjects != null) return stageObjects;

            return EnumerateSceneGameObjects(scene);
        }

        /// <summary>
        /// Describes what a scene-scoped search covered, for its response text: the open prefab stage when one
        /// is open, otherwise the named scene.
        /// </summary>
        /// <param name="scene">The scene the caller named.</param>
        /// <returns>"open prefab stage" or "scene Name".</returns>
        private static string DescribeContext(Scene scene)
        {
            return PrefabStageUtility.GetCurrentPrefabStage() != null ? "open prefab stage" : "scene " + scene.name;
        }

        /// <summary>Builds the refusal for a path or name that matches more than one GameObject, listing every match.</summary>
        /// <param name="kind">"path" or "name", whichever the target matched.</param>
        /// <param name="text">The path or name that was looked up.</param>
        /// <param name="matches">Every GameObject it matched.</param>
        /// <returns>The multi-line refusal, one [instanceId] Scene:/path row per match.</returns>
        private static string DescribeAmbiguousMatches(string kind, string text, List<GameObject> matches)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Ambiguous GameObject " + kind + " '" + text + "': " + matches.Count + " objects match. " +
                (kind == "path"
                    ? "Qualify it with the scene (SceneName:/Path/To/Object), or use an instance id when the matches share a scene."
                    : "Use a full path, qualify it with the scene (SceneName:/Path/To/Object), or use an instance id."));
            foreach (var match in matches)
            {
                builder.AppendLine("- [" + match.GetInstanceID() + "] " + GetGameObjectScenePath(match));
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Names the current editing context for search/reference responses: "prefabStage" when a prefab is
        /// open in isolation, otherwise "scene".
        /// </summary>
        /// <returns>"prefabStage" when a prefab stage is open, otherwise "scene".</returns>
        private static string CurrentContextLabel()
        {
            return PrefabStageUtility.GetCurrentPrefabStage() != null ? "prefabStage" : "scene";
        }

        /// <summary>Builds a slash-prefixed scene hierarchy path for a GameObject.</summary>
        /// <param name="gameObject">GameObject whose transform hierarchy should be formatted.</param>
        /// <returns>Slash-prefixed hierarchy path.</returns>
        private static string GetGameObjectPath(GameObject gameObject)
        {
            var names = new List<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            return "/" + string.Join("/", names.ToArray());
        }

        /// <summary>Builds a scene-qualified path, SceneName:/Root/Child, which is the form a target accepts back.</summary>
        /// <param name="gameObject">GameObject whose scene and hierarchy should be formatted.</param>
        /// <returns>The scene name, a colon, then the slash-prefixed hierarchy path.</returns>
        private static string GetGameObjectScenePath(GameObject gameObject)
        {
            return gameObject.scene.name + ":" + GetGameObjectPath(gameObject);
        }

        /// <summary>Normalizes a scene object path for path comparisons.</summary>
        /// <param name="path">Raw scene path or name.</param>
        /// <returns>Path with quotes, backslashes, and leading slashes removed.</returns>
        private static string NormalizeObjectPathForCompare(string path)
        {
            path = path.Trim().Trim('"').Replace('\\', '/');
            while (path.StartsWith("/", StringComparison.Ordinal))
            {
                path = path.Substring(1);
            }

            return path;
        }

        /// <summary>
        /// Measures what a GameObject draws, in tiers, and returns the first tier that finds anything: shape renderers
        /// (mesh, skinned, sprite and the like) that are active, enabled and non-empty; then particle, trail and line
        /// renderers on the same terms, since their bounds measure what they emitted rather than the object; then
        /// active, enabled colliders; then every renderer including inactive ones, for a target that is itself
        /// inactive; then a unit box at the transform. Inactive and empty renderers are left out of the first tiers
        /// because they report stale or zero-size bounds at the world origin: a car's switched-off light flares
        /// measured 16 to 43 m and its idle particle systems sat at (0, 0, 0), dragging a framing off the car.
        /// </summary>
        /// <param name="gameObject">GameObject whose descendants should be measured.</param>
        /// <returns>The first tier's bounds, or the unit fallback.</returns>
        private static Bounds GetGameObjectBounds(GameObject gameObject)
        {
            Bounds bounds;
            if (TryEncapsulateRenderers(gameObject, false, true, false, out bounds)) return bounds;
            if (TryEncapsulateRenderers(gameObject, false, false, true, out bounds)) return bounds;

            var hasBounds = false;
            bounds = new Bounds(gameObject.transform.position, Vector3.zero);
            foreach (var collider in gameObject.GetComponentsInChildren<Collider>(false))
            {
                if (collider == null || !collider.enabled)
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = collider.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(collider.bounds);
                }
            }

            if (hasBounds) return bounds;
            if (TryEncapsulateRenderers(gameObject, true, true, true, out bounds)) return bounds;
            return new Bounds(gameObject.transform.position, Vector3.one);
        }

        /// <summary>Encapsulates the bounds of a GameObject's renderers that pass the given filters.</summary>
        /// <param name="gameObject">GameObject whose descendants should be measured.</param>
        /// <param name="includeHidden">True to count inactive, disabled and empty renderers too.</param>
        /// <param name="shapes">True to count shape renderers: everything that is not a particle, trail or line renderer.</param>
        /// <param name="effects">True to count particle, trail and line renderers.</param>
        /// <param name="bounds">Combined bounds of every renderer counted.</param>
        /// <returns>True when at least one renderer was counted.</returns>
        private static bool TryEncapsulateRenderers(GameObject gameObject, bool includeHidden, bool shapes, bool effects, out Bounds bounds)
        {
            var hasBounds = false;
            bounds = new Bounds(gameObject.transform.position, Vector3.zero);
            foreach (var renderer in gameObject.GetComponentsInChildren<Renderer>(includeHidden))
            {
                if (renderer == null)
                {
                    continue;
                }

                var isEffect = renderer is ParticleSystemRenderer || renderer is TrailRenderer || renderer is LineRenderer;
                if (isEffect ? !effects : !shapes)
                {
                    continue;
                }

                var rendererBounds = renderer.bounds;
                if (!includeHidden && (!renderer.enabled || rendererBounds.extents == Vector3.zero))
                {
                    continue;
                }

                if (!hasBounds)
                {
                    bounds = rendererBounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(rendererBounds);
                }
            }

            return hasBounds;
        }

        #endregion

        #region Vectors and numbers

        /// <summary>Parses exactly three invariant-culture float tokens into a Vector3.</summary>
        /// <param name="text">Text containing x, y, and z separated by spaces, tabs, or commas.</param>
        /// <param name="value">Parsed Vector3, or zero when parsing fails.</param>
        /// <returns>True when all three values parse successfully.</returns>
        private static bool TryParseVector3(string text, out Vector3 value)
        {
            value = Vector3.zero;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var parts = text.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
            {
                return false;
            }

            float x;
            float y;
            float z;
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
            {
                return false;
            }

            value = new Vector3(x, y, z);
            return true;
        }

        /// <summary>Parses all space, tab, or comma separated tokens into invariant-culture floats.</summary>
        /// <param name="text">Raw numeric list.</param>
        /// <returns>Parsed float array, or an empty array when input is blank or any token fails.</returns>
        private static float[] ParseFloatList(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new float[0];
            }

            var pieces = text.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
            var values = new List<float>();
            foreach (var piece in pieces)
            {
                float value;
                if (!float.TryParse(piece, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                {
                    return new float[0];
                }

                values.Add(value);
            }

            return values.ToArray();
        }

        /// <summary>Parses one invariant-culture float token.</summary>
        /// <param name="text">Text to parse.</param>
        /// <param name="value">Parsed value, or zero when parsing fails.</param>
        /// <returns>True when parsing succeeds.</returns>
        private static bool TryParseFloat(string text, out float value)
        {
            value = 0f;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Formats a Vector3 with invariant-culture components rounded to three decimals.</summary>
        /// <param name="value">Vector to format.</param>
        /// <returns>Parenthesized x, y, z text.</returns>
        private static string FormatVector3(Vector3 value)
        {
            return "(" +
                value.x.ToString("0.###", CultureInfo.InvariantCulture) + ", " +
                value.y.ToString("0.###", CultureInfo.InvariantCulture) + ", " +
                value.z.ToString("0.###", CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>Local transform vector kind used by the shared transform setter.</summary>
        private enum TransformVectorKind
        {
            /// <summary>Local position vector.</summary>
            Position,

            /// <summary>Local Euler rotation vector.</summary>
            Rotation,

            /// <summary>Local scale vector.</summary>
            Scale
        }

        #endregion

        #region JSON escaping

        /// <summary>
        /// Escapes a string for the small hand-built JSON payloads returned by discovery commands.
        /// </summary>
        /// <param name="value">Raw string value to escape.</param>
        /// <returns>Escaped JSON string content without surrounding quotes.</returns>
        private static string JsonEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        #endregion

        #region Reflection

        /// <summary>Reads an instance field by name, walking base types so private members of a base class resolve too.</summary>
        /// <param name="target">Object instance containing the field.</param>
        /// <param name="fieldName">Field name to read.</param>
        /// <returns>Field value, or null when the target or field is unavailable.</returns>
        private static object GetInstanceFieldValue(object target, string fieldName)
        {
            if (target == null)
            {
                return null;
            }

            for (var type = target.GetType(); type != null; type = type.BaseType)
            {
                var field = type.GetField(fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    return field.GetValue(target);
                }
            }

            return null;
        }

        /// <summary>Finds the first loaded scene component whose runtime type full name matches exactly.</summary>
        /// <param name="fullTypeName">Full type name to match.</param>
        /// <returns>Matching component, or null when none is loaded in a valid scene.</returns>
        private static Component FindRuntimeComponent(string fullTypeName)
        {
            foreach (var component in Resources.FindObjectsOfTypeAll<Component>())
            {
                if (component == null || component.gameObject == null)
                {
                    continue;
                }

                if (!component.gameObject.scene.IsValid())
                {
                    continue;
                }

                if (component.GetType().FullName == fullTypeName)
                {
                    return component;
                }
            }

            return null;
        }

        /// <summary>Searches loaded AppDomain assemblies for a type by full name.</summary>
        /// <param name="fullTypeName">Full type name to find.</param>
        /// <returns>Matching type, or null when no loaded assembly exposes it.</returns>
        private static Type FindLoadedType(string fullTypeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = null;
                try
                {
                    type = assembly.GetType(fullTypeName, false);
                }
                catch
                {
                    // Some dynamic assemblies can throw during type lookup.
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        /// <summary>Invokes a public or non-public instance method by name when it exists.</summary>
        /// <param name="target">Object instance containing the method.</param>
        /// <param name="methodName">Method name to invoke.</param>
        /// <param name="args">Arguments passed to the reflected method.</param>
        /// <returns>Method return value, or null when the target or method is unavailable.</returns>
        private static object InvokeInstanceMethod(object target, string methodName, object[] args)
        {
            if (target == null)
            {
                return null;
            }

            var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                return null;
            }

            return method.Invoke(target, args);
        }

        /// <summary>Invokes a reflected method and returns its Vector3 result when available.</summary>
        /// <param name="target">Object instance containing the method.</param>
        /// <param name="methodName">Method name to invoke.</param>
        /// <param name="fallback">Value returned when the method is missing or does not return a Vector3.</param>
        /// <returns>Reflected Vector3 return value, or <paramref name="fallback"/>.</returns>
        private static Vector3 InvokeVector3(object target, string methodName, Vector3 fallback)
        {
            var result = InvokeInstanceMethod(target, methodName, null);
            return result is Vector3 ? (Vector3)result : fallback;
        }

        /// <summary>Finds a public or non-public static method and throws when it is missing.</summary>
        /// <param name="type">Type to inspect.</param>
        /// <param name="name">Static method name.</param>
        /// <returns>Matching reflected method.</returns>
        private static MethodInfo GetStaticMethod(Type type, string name)
        {
            var method = type.GetMethod(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(type.FullName, name);
            }

            return method;
        }

        /// <summary>Finds a public or non-public instance field and optionally throws when it is missing.</summary>
        /// <param name="type">Type to inspect.</param>
        /// <param name="name">Field name.</param>
        /// <param name="required">Whether a missing field should throw.</param>
        /// <returns>Matching field, or null when optional and missing.</returns>
        private static FieldInfo GetField(Type type, string name, bool required = true)
        {
            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null && required)
            {
                throw new MissingFieldException(type.FullName, name);
            }

            return field;
        }

        #endregion
    }
}
