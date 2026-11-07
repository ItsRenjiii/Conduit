/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Screenshot commands for camera rendering, isolated object inspection and editor windows.
    /// </summary>
    internal static partial class ConduitBridge
    {
        /// <summary>
        /// Uses an editor-only high layer for isolated renders so normal scene culling can be restored.
        /// </summary>
        private const int IsolatedScreenshotLayer = 31;

        #region Screenshot Commands

        /// <summary>
        /// Captures a named camera, or the main camera, of one scene to a PNG path.
        /// </summary>
        /// <param name="parts">Command tokens containing the scene, path, optional width, height, and camera name.</param>
        /// <returns>A confirmation line naming the camera used, or a validation error.</returns>
        private static string ScreenshotCameraCommand(string[] parts)
        {
            UnityEngine.SceneManagement.Scene scene;
            var sceneError = TryResolveRequiredScene(parts.Length > 1 ? parts[1] : string.Empty, out scene);
            if (sceneError != null)
            {
                return sceneError;
            }

            if (parts.Length < 3)
            {
                return "Usage: screenshot-camera <scene> <path> [width] [height] [cameraName]";
            }

            var path = NormalizeProjectRelativePath(parts[2]);
            var width = parts.Length > 3 ? ParsePositiveInt(parts[3], 1280, MAX_SCREENSHOT_DIMENSION) : 1280;
            var height = parts.Length > 4 ? ParsePositiveInt(parts[4], 720, MAX_SCREENSHOT_DIMENSION) : 720;
            var cameraName = parts.Length > 5 ? parts[5] : string.Empty;
            string cameraError;
            var camera = FindCameraForScreenshot(scene, cameraName, out cameraError);
            if (camera == null)
            {
                return cameraError;
            }

            var rendered = RenderCameraToPng(camera, path, width, height);
            return rendered.StartsWith("Saved screenshot", StringComparison.Ordinal)
                ? rendered + Environment.NewLine + "camera: " + GetGameObjectScenePath(camera.gameObject)
                : rendered;
        }

        /// <summary>
        /// Renders one GameObject in isolation using a temporary camera and light.
        /// </summary>
        /// <param name="args">Pipe-separated target and output path, with optional width and height after the path.</param>
        /// <returns>A confirmation line or validation error.</returns>
        private static string ScreenshotIsolatedCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: screenshot-isolated <target> | <path> [width] [height]";
            }

            GameObject target;
            var error = TryFindGameObject(sections[0], out target);
            if (error != null)
            {
                return error;
            }

            var outputTokens = SplitCommandTokens(sections[1]);
            if (outputTokens.Length == 0)
            {
                return "Missing output path.";
            }

            // Parse this section from the RIGHT. The MCP server sends pipe sections unquoted on purpose (a
            // quoted JSON payload silently downgrades a modify to a no-op), so a path containing a space
            // arrives as several tokens. Taking token 0 as the path truncated it at its first space:
            // "D:/Worlds/My Project/Temp/board.png" wrote an extensionless file "My" into "D:/Worlds/" and
            // reported that as success. Only a trailing positive integer can be a size,
            // and at least one token is always left for the path, so a folder named after a number is safe.
            var sizeTokens = 0;
            while (sizeTokens < 2
                && outputTokens.Length - sizeTokens > 1
                && IsPositiveIntegerToken(outputTokens[outputTokens.Length - 1 - sizeTokens]))
            {
                sizeTokens++;
            }

            var pathTokenCount = outputTokens.Length - sizeTokens;
            var path = NormalizeProjectRelativePath(string.Join(" ", outputTokens, 0, pathTokenCount));
            var width = sizeTokens > 0 ? ParsePositiveInt(outputTokens[pathTokenCount], 1024, MAX_SCREENSHOT_DIMENSION) : 1024;
            var height = sizeTokens > 1 ? ParsePositiveInt(outputTokens[pathTokenCount + 1], 1024, MAX_SCREENSHOT_DIMENSION) : 1024;
            var bounds = GetGameObjectBounds(target);
            var changedLayers = new Dictionary<GameObject, int>();
            Camera camera = null;
            Light light = null;

            try
            {
                SetLayerRecursively(target, IsolatedScreenshotLayer, changedLayers);
                var cameraObject = new GameObject("Conduit Isolated Screenshot Camera");
                cameraObject.hideFlags = HideFlags.HideAndDontSave;
                camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
                camera.cullingMask = 1 << IsolatedScreenshotLayer;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 10000f;
                PositionCameraForBounds(camera, bounds);

                var lightObject = new GameObject("Conduit Isolated Screenshot Light");
                lightObject.hideFlags = HideFlags.HideAndDontSave;
                light = lightObject.AddComponent<Light>();
                light.type = LightType.Directional;
                light.intensity = 1.1f;
                light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

                return RenderCameraToPng(camera, path, width, height);
            }
            finally
            {
                foreach (var pair in changedLayers)
                {
                    if (pair.Key != null)
                    {
                        pair.Key.layer = pair.Value;
                    }
                }

                if (camera != null)
                {
                    UnityEngine.Object.DestroyImmediate(camera.gameObject);
                }

                if (light != null)
                {
                    UnityEngine.Object.DestroyImmediate(light.gameObject);
                }
            }
        }

        /// <summary>
        /// Captures an editor window to a PNG as it is drawn, for when the look of a window is the question.
        /// The window's pane is repainted on the spot and read back from its own buffer, so the capture does
        /// not depend on the window being uncovered on the desktop: reading the desktop instead returned solid
        /// black while Unity sat behind another application.
        /// </summary>
        /// <param name="args">Window selector, optionally followed by a pipe and an output path.</param>
        /// <returns>The saved path and pixel size, or a user-facing error.</returns>
        private static string ScreenshotWindowCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length == 0 || sections[0].Trim().Length == 0)
            {
                return "Usage: screenshot-window <window> [| path]";
            }

            EditorWindow window;
            var error = TryFindEditorWindow(sections[0], out window);
            if (error != null)
            {
                return error;
            }

            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static | BindingFlags.Instance;

            var parentField = typeof(EditorWindow).GetField("m_Parent", All);
            var view = parentField == null ? null : parentField.GetValue(window);
            if (view == null)
            {
                return "Window '" + window.GetType().Name + "' has no host view, so it cannot be captured.";
            }

            // Docked windows share one pane and only the tab in front draws, so a hidden tab would capture
            // whichever window is showing and report it under this one's name.
            var actualView = view.GetType().GetProperty("actualView", All);
            var shown = actualView == null ? null : actualView.GetValue(view, null) as EditorWindow;
            if (shown != null && shown != window)
            {
                return "Window '" + window.GetType().Name + "' is a hidden tab behind '" + shown.GetType().Name
                       + "' in its dock area, so nothing of it is drawn to capture. Bring its tab to the front first.";
            }

            var guiView = FindLoadedType("UnityEditor.GUIView");
            var grab = guiView == null ? null : guiView.GetMethod("GrabPixels", All);
            var repaintNow = view.GetType().GetMethod("RepaintImmediately", All);
            var positionProperty = view.GetType().GetProperty("position", All);
            if (grab == null || repaintNow == null || positionProperty == null)
            {
                return "This Unity version does not expose GUIView.GrabPixels and RepaintImmediately, so editor windows cannot be captured.";
            }

            var viewRect = (Rect)positionProperty.GetValue(view, null);
            var scale = EditorGUIUtility.pixelsPerPoint;
            var width = Mathf.RoundToInt(viewRect.width * scale);
            var height = Mathf.RoundToInt(viewRect.height * scale);
            if (width < 1 || height < 1 || width > MAX_SCREENSHOT_DIMENSION || height > MAX_SCREENSHOT_DIMENSION)
            {
                return "Window '" + window.GetType().Name + "' measures " + width + "x" + height
                       + " px, outside the 1-" + MAX_SCREENSHOT_DIMENSION + " px a capture supports.";
            }

            var requested = sections.Length > 1 ? sections[1].Trim() : string.Empty;
            var path = NormalizeProjectRelativePath(requested.Length > 0
                ? requested
                : "Temp/Conduit/Screenshots/" + window.GetType().Name + ".png");

            // Linear, so the buffer's bytes are copied as they are. An sRGB target in a linear-space project
            // encodes them a second time: the dark skin's 56,56,56 background came back as 129,129,129.
            var target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var previousActive = RenderTexture.active;
            try
            {
                target.Create();

                // The buffer holds whatever the pane last drew, and an editor behind another application does
                // not repaint on its own, so draw it now or the capture shows a stale frame.
                repaintNow.Invoke(view, null);
                grab.Invoke(view, new object[] { target, new Rect(0f, 0f, width, height) });

                RenderTexture.active = target;
                texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0);
                texture.Apply();
                var pixels = texture.GetPixels32();

                // The grab arrives upside down where render targets store their top row first. Observed on
                // Direct3D 11, which reports its UV origin at the top; the other APIs follow the same flag
                // but have not been observed.
                if (SystemInfo.graphicsUVStartsAtTop)
                {
                    FlipRows(pixels, width, height);
                }

                texture.SetPixels32(pixels);
                texture.Apply();

                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllBytes(path, texture.EncodeToPNG());

                var report = "Saved " + window.GetType().Name + " to " + path + " (" + width + "x" + height
                             + " px at " + JsonNumber(scale) + "x scale, the whole pane including any tab strip).";

                // A pane that drew nothing still saves a valid png, so say so rather than report a picture.
                var first = pixels[0];
                var flat = true;
                for (var i = 1; i < pixels.Length && flat; i++)
                {
                    var pixel = pixels[i];
                    flat = pixel.r == first.r && pixel.g == first.g && pixel.b == first.b && pixel.a == first.a;
                }

                if (flat)
                {
                    report += " Warning: every pixel is " + first.r + "," + first.g + "," + first.b + "," + first.a
                              + ", so the pane drew nothing to see.";
                }

                return report;
            }
            catch (Exception exception)
            {
                var cause = exception is TargetInvocationException && exception.InnerException != null
                    ? exception.InnerException
                    : exception;
                return "Capturing '" + window.GetType().Name + "' failed: " + cause.Message;
            }
            finally
            {
                RenderTexture.active = previousActive;
                target.Release();
                UnityEngine.Object.DestroyImmediate(target);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        #endregion

        #region Screenshot Helpers

        /// <summary>
        /// Finds a screenshot camera inside one scene. A name selects that enabled camera, and a name matching
        /// none in the scene is an error rather than a fallback to another camera. Without a name the scene's
        /// camera tagged MainCamera is used, then its first enabled camera.
        /// </summary>
        /// <param name="scene">The scene whose cameras are searched.</param>
        /// <param name="cameraName">The optional camera name to search for.</param>
        /// <param name="error">Why no camera was found, otherwise null.</param>
        /// <returns>The resolved camera, or null.</returns>
        private static Camera FindCameraForScreenshot(UnityEngine.SceneManagement.Scene scene, string cameraName, out string error)
        {
            error = null;
            var wanted = (cameraName ?? string.Empty).Trim().Trim('"');
            Camera tagged = null;
            Camera first = null;
            foreach (var camera in Camera.allCameras)
            {
                if (camera == null || camera.gameObject.scene != scene) continue;

                if (wanted.Length > 0)
                {
                    if (string.Equals(camera.name, wanted, StringComparison.OrdinalIgnoreCase)) return camera;
                    continue;
                }

                if (tagged == null && camera.CompareTag("MainCamera")) tagged = camera;
                if (first == null) first = camera;
            }

            if (wanted.Length > 0)
            {
                error = "No enabled camera named '" + wanted + "' in scene " + scene.name + ".";
                return null;
            }

            var chosen = tagged != null ? tagged : first;
            if (chosen == null)
            {
                error = "No enabled camera in scene " + scene.name + ". Provide a camera name or enable one.";
            }

            return chosen;
        }

        /// <summary>
        /// Renders a camera into a PNG file using a temporary RenderTexture.
        /// </summary>
        /// <param name="camera">The camera to render.</param>
        /// <param name="path">The destination PNG path.</param>
        /// <param name="width">The render width in pixels.</param>
        /// <param name="height">The render height in pixels.</param>
        /// <returns>A confirmation line or IO/render error.</returns>
        private static string RenderCameraToPng(Camera camera, string path, int width, int height)
        {
            if (camera == null)
            {
                return "Cannot render screenshot without a camera.";
            }

            width = Mathf.Clamp(width, 16, 8192);
            height = Mathf.Clamp(height, 16, 8192);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var renderTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                camera.targetTexture = renderTexture;
                RenderTexture.active = renderTexture;
                camera.Render();
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                File.WriteAllBytes(path, texture.EncodeToPNG());
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                return "Saved screenshot to " + path + ".";
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        /// <summary>
        /// Recursively moves an object tree onto the isolated render layer while remembering old layers.
        /// </summary>
        /// <param name="root">The root object to relayer.</param>
        /// <param name="layer">The temporary layer to apply.</param>
        /// <param name="changedLayers">The old layer map to restore in finally blocks.</param>
        private static void SetLayerRecursively(GameObject root, int layer, Dictionary<GameObject, int> changedLayers)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            foreach (var transform in transforms)
            {
                changedLayers[transform.gameObject] = transform.gameObject.layer;
                transform.gameObject.layer = layer;
            }
        }

        /// <summary>
        /// Places a temporary camera so the target bounds fit in frame.
        /// </summary>
        /// <param name="camera">The camera to position.</param>
        /// <param name="bounds">The target bounds.</param>
        private static void PositionCameraForBounds(Camera camera, Bounds bounds)
        {
            var radius = Mathf.Max(bounds.extents.magnitude, 0.5f);
            var direction = new Vector3(0.55f, 0.35f, -1f).normalized;
            camera.transform.position = bounds.center - direction * radius * 2.5f;
            camera.transform.LookAt(bounds.center);
            camera.fieldOfView = 30f;
        }

        /// <summary>
        /// Reverses the row order of a pixel block in place, turning a top-row-first grab into the
        /// bottom-row-first layout a Texture2D holds.
        /// </summary>
        /// <param name="pixels">Pixels, row by row.</param>
        /// <param name="width">Pixels per row.</param>
        /// <param name="height">Row count.</param>
        private static void FlipRows(Color32[] pixels, int width, int height)
        {
            var row = new Color32[width];
            for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
            {
                Array.Copy(pixels, top * width, row, 0, width);
                Array.Copy(pixels, bottom * width, pixels, top * width, width);
                Array.Copy(row, 0, pixels, bottom * width, width);
            }
        }

        #endregion
    }
}
