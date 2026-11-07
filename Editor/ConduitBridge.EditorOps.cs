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
    /// Editor operations: menu execution, selection, and scene and game view capture.
    /// </summary>
    internal static partial class ConduitBridge
    {
        /// <summary>Executes a Unity Editor menu item by path.</summary>
        /// <param name="menuPath">Menu path passed to <see cref="EditorApplication.ExecuteMenuItem"/>.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string ExecuteMenu(string menuPath)
        {
            if (string.IsNullOrWhiteSpace(menuPath))
            {
                return "Missing menu path. Usage: menu <menuPath>";
            }

            menuPath = menuPath.Trim().Trim('"');
            var executed = EditorApplication.ExecuteMenuItem(menuPath);
            var status = "ExecuteMenuItem('" + menuPath + "') returned: " + executed;

            // A menu item is free to start a compile, and the reload that follows destroys the channel this
            // answer would have travelled on - which is how a menu call that SUCCEEDED came back as
            // outcome_unknown. Deferring hands the caller the post-compile report instead, exactly as every
            // other compile-triggering verb answers.
            //
            // Unlike reimport this cannot defer BEFORE the call: there is no way to know in advance whether a
            // given item compiles anything, and deferring every menu call would leave the ordinary ones
            // waiting on a reload that is never coming. Checking afterwards is safe because a compile has to
            // finish before its reload begins, so there is time to persist the deferral.
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                if (TryDeferUntilReload("__deferred-note " + status))
                {
                    return string.Empty;
                }
            }

            return status + Environment.NewLine + FormatEditorStatus();
        }

        /// <summary>Selects and pings an asset in the Unity editor.</summary>
        /// <param name="assetPath">Asset path to select.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string SelectAsset(string assetPath)
        {
            assetPath = NormalizeAssetPath(assetPath);
            if (string.IsNullOrEmpty(assetPath))
            {
                return "Missing asset path. Usage: select <assetPath>";
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
            {
                return "AssetDatabase could not find asset: " + assetPath;
            }

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
            return "Selected asset: " + assetPath;
        }

        /// <summary>Captures the active SceneView camera to a PNG file.</summary>
        /// <param name="parts">Command tokens containing optional path, width, and height.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string CaptureSceneView(string[] parts)
        {
            ParseScreenshotTail(parts, "Temp/ConduitUnitySceneView.png", out var path, out var width, out var height);

            path = NormalizeProjectRelativePath(path);
            if (string.IsNullOrEmpty(path))
            {
                return "Invalid screenshot path.";
            }

            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null && SceneView.sceneViews.Count > 0)
            {
                sceneView = SceneView.sceneViews[0] as SceneView;
            }

            if (sceneView == null || sceneView.camera == null)
            {
                return "No SceneView camera is available to capture.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            var camera = sceneView.camera;
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var renderTexture = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);

            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(texture);
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }

            return "SceneView screenshot written: " + path;
        }

        /// <summary>Captures the MainCamera, or else the first enabled camera, of one scene to a PNG file.</summary>
        /// <param name="parts">Command tokens containing the scene, then optional path, width, and height.</param>
        /// <returns>A confirmation line naming the camera used, or a validation error.</returns>
        private static string CaptureGameView(string[] parts)
        {
            UnityEngine.SceneManagement.Scene scene;
            var sceneError = TryResolveRequiredScene(parts.Length > 1 ? parts[1] : string.Empty, out scene);
            if (sceneError != null)
            {
                return sceneError;
            }

            // The tail parser reads "verb [path] [width] [height]", so the scene token is dropped before it.
            var tail = new string[parts.Length - 1];
            tail[0] = parts[0];
            Array.Copy(parts, 2, tail, 1, parts.Length - 2);
            ParseScreenshotTail(tail, "Temp/ConduitUnityGameView.png", out var path, out var width, out var height);

            path = NormalizeProjectRelativePath(path);
            if (string.IsNullOrEmpty(path))
            {
                return "Invalid screenshot path.";
            }

            Camera camera = null;
            Camera firstEnabled = null;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (candidate == null || !candidate.enabled || candidate.gameObject.scene != scene) continue;

                if (candidate.CompareTag("MainCamera"))
                {
                    camera = candidate;
                    break;
                }

                if (firstEnabled == null) firstEnabled = candidate;
            }

            if (camera == null) camera = firstEnabled;
            if (camera == null)
            {
                return "No enabled camera in scene " + scene.name + " to capture.";
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var renderTexture = new RenderTexture(width, height, 24);
            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);

            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(texture);
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }

            return "Game screenshot written: " + path + Environment.NewLine +
                "camera: " + GetGameObjectScenePath(camera.gameObject);
        }

        /// <summary>Focuses and repaints the Unity Game View window.</summary>
        /// <returns>A confirmation line or a missing-GameView error.</returns>
        private static string FocusGameView()
        {
            var gameView = GetGameView();
            if (gameView == null)
            {
                return "UnityEditor.GameView type is not available.";
            }

            gameView.Focus();
            gameView.Repaint();
            return "Game View focused.";
        }

        /// <summary>Sends a mouse move, down, and up event to the Game View at a pixel position.</summary>
        /// <param name="args">Two float values: x and y Game View coordinates.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string SendGameViewClick(string args)
        {
            var values = ParseFloatList(args);
            if (values.Length != 2)
            {
                return "Usage: game-view-click <x> <y>";
            }

            var gameView = GetGameView();
            if (gameView == null)
            {
                return "UnityEditor.GameView type is not available.";
            }

            gameView.Focus();
            var position = new Vector2(values[0], values[1]);
            gameView.SendEvent(new Event { type = EventType.MouseMove, mousePosition = position });
            gameView.SendEvent(new Event { type = EventType.MouseDown, button = 0, mousePosition = position, clickCount = 1 });
            gameView.SendEvent(new Event { type = EventType.MouseUp, button = 0, mousePosition = position, clickCount = 1 });
            gameView.Repaint();
            return "Sent Game View click at " + values[0].ToString("0.###", CultureInfo.InvariantCulture) + ", " +
                values[1].ToString("0.###", CultureInfo.InvariantCulture) + ".";
        }

        /// <summary>Finds or opens Unity's internal Game View editor window.</summary>
        /// <returns>The Game View window, or null when the internal type is unavailable.</returns>
        private static EditorWindow GetGameView()
        {
            var gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null)
            {
                return null;
            }

            return EditorWindow.GetWindow(gameViewType);
        }

        /// <summary>
        /// Parses a "[path] [width] [height]" argument tail where the path may contain spaces.
        /// Width and height are recognised only as up to two trailing integer tokens, so an unquoted
        /// path like "C:/My Folder/shot.png" is rejoined intact; surrounding quotes are also honoured.
        /// </summary>
        /// <param name="parts">The whitespace-split command tokens, including the verb at index 0.</param>
        /// <param name="defaultPath">Path to use when no path token is supplied.</param>
        /// <param name="path">Receives the resolved (de-quoted) path.</param>
        /// <param name="width">Receives the width, or 1280 when absent/invalid.</param>
        /// <param name="height">Receives the height, or 720 when absent/invalid.</param>
        private static void ParseScreenshotTail(string[] parts, string defaultPath, out string path, out int width, out int height)
        {
            width = 1280;
            height = 720;

            // Peel up to two trailing pure-integer tokens as [width] then [height] (declared order).
            var end = parts.Length;
            var trailingInts = 0;
            while (end - 1 > 0 && trailingInts < 2 && int.TryParse(parts[end - 1], out _))
            {
                end--;
                trailingInts++;
            }

            if (trailingInts == 2)
            {
                width = ParseDimension(parts[end], 1280);
                height = ParseDimension(parts[end + 1], 720);
            }
            else if (trailingInts == 1)
            {
                width = ParseDimension(parts[end], 1280);
            }

            // Everything between the verb and the dimensions is the (possibly spaced) path.
            path = end > 1
                ? string.Join(" ", parts, 1, end - 1).Trim().Trim('"')
                : defaultPath;
        }

        /// <summary>
        /// Parses a screenshot width/height token. Unlike <see cref="ParsePositiveInt"/> (capped at 500
        /// for log counts/depths), this allows full capture resolutions, clamped to 8K so a typo can't
        /// request a multi-gigabyte render texture.
        /// </summary>
        /// <param name="value">The token to parse.</param>
        /// <param name="fallback">Value returned when the token is missing or not a positive integer.</param>
        /// <returns>The pixel dimension, clamped to the range 1..7680.</returns>
        private static int ParseDimension(string value, int fallback)
        {
            int parsed;
            if (!int.TryParse(value, out parsed) || parsed < 1)
            {
                return fallback;
            }

            return Math.Min(parsed, 7680);
        }

        /// <summary>Highest pixel dimension a screenshot renders, kept well inside the texture limit.</summary>
        private const int MAX_SCREENSHOT_DIMENSION = 8192;
            /// <summary>
        /// Routes the EditorOps verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchEditorOps(string verb, string command, string[] parts)
        {
            if (verb == "editor-selection-get")
            {
                return EditorSelectionGetCommand();
            }

            if (verb == "editor-selection-set")
            {
                return EditorSelectionSetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "screenshot-camera")
            {
                return ScreenshotCameraCommand(parts);
            }

            if (verb == "screenshot-isolated")
            {
                return ScreenshotIsolatedCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "screenshot-window")
            {
                return ScreenshotWindowCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "menu")
            {
                return ExecuteMenu(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "select")
            {
                return SelectAsset(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "screenshot-scene" || verb == "screenshot-viewport")
            {
                return CaptureSceneView(parts);
            }

            if (verb == "screenshot-game")
            {
                return CaptureGameView(parts);
            }

            if (verb == "game-view-focus")
            {
                return FocusGameView();
            }

            if (verb == "game-view-click")
            {
                return SendGameViewClick(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "game-view-key")
            {
                return PostGameViewKey(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
