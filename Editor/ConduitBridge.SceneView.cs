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
    /// Scene view camera verbs.
    /// </summary>
    internal static partial class ConduitBridge
    {
        /// <summary>Formats the currently available SceneView state.</summary>
        /// <returns>SceneView state text or an availability error.</returns>
        private static string FormatSceneViewState()
        {
            var sceneView = GetSceneView();
            if (sceneView == null)
            {
                return "No SceneView is available.";
            }

            return FormatSceneViewState(sceneView);
        }

        /// <summary>Sets the SceneView pivot to a world-space position.</summary>
        /// <param name="args">World x/y/z pivot position.</param>
        /// <returns>A confirmation line with SceneView state, or a validation error.</returns>
        private static string SetSceneViewPivot(string args)
        {
            Vector3 pivot;
            if (!TryParseVector3(args, out pivot))
            {
                return "Usage: scene-view-pivot <x> <y> <z>";
            }

            var sceneView = GetSceneView();
            if (sceneView == null)
            {
                return "No SceneView is available.";
            }

            var before = FormatSceneViewState(sceneView);
            ApplySceneView(sceneView, pivot, sceneView.rotation, sceneView.size, sceneView.orthographic);
            return SceneViewChangeReport("SceneView pivot set.", before, sceneView);
        }

        /// <summary>Sets the SceneView rotation from Euler angles.</summary>
        /// <param name="args">Euler x/y/z rotation values in degrees.</param>
        /// <returns>A confirmation line with SceneView state, or a validation error.</returns>
        private static string SetSceneViewRotation(string args)
        {
            Vector3 euler;
            if (!TryParseVector3(args, out euler))
            {
                return "Usage: scene-view-rotation <x> <y> <z>";
            }

            var sceneView = GetSceneView();
            if (sceneView == null)
            {
                return "No SceneView is available.";
            }

            var before = FormatSceneViewState(sceneView);
            ApplySceneView(sceneView, sceneView.pivot, Quaternion.Euler(euler), sceneView.size, sceneView.orthographic);
            return SceneViewChangeReport("SceneView rotation set.", before, sceneView);
        }

        /// <summary>Sets the SceneView size to a positive value.</summary>
        /// <param name="args">Positive size value.</param>
        /// <returns>A confirmation line with SceneView state, or a validation error.</returns>
        private static string SetSceneViewSize(string args)
        {
            float size;
            if (!TryParseFloat(args, out size) || size <= 0f)
            {
                return "Usage: scene-view-size <positiveSize>";
            }

            var sceneView = GetSceneView();
            if (sceneView == null)
            {
                return "No SceneView is available.";
            }

            var before = FormatSceneViewState(sceneView);
            ApplySceneView(sceneView, sceneView.pivot, sceneView.rotation, Mathf.Max(0.01f, size), sceneView.orthographic);
            return SceneViewChangeReport("SceneView size set.", before, sceneView);
        }

        /// <summary>Pans the SceneView pivot along the view's right and up axes.</summary>
        /// <param name="args">Right and up movement multipliers.</param>
        /// <returns>A confirmation line with SceneView state, or a validation error.</returns>
        private static string PanSceneView(string args)
        {
            var values = ParseFloatList(args);
            if (values.Length != 2)
            {
                return "Usage: scene-view-pan <right> <up>";
            }

            var sceneView = GetSceneView();
            if (sceneView == null)
            {
                return "No SceneView is available.";
            }

            var right = sceneView.rotation * Vector3.right;
            var up = sceneView.rotation * Vector3.up;
            var scale = Mathf.Max(0.01f, sceneView.size);
            var pivot = sceneView.pivot + (right * values[0] * scale) + (up * values[1] * scale);
            var before = FormatSceneViewState(sceneView);
            ApplySceneView(sceneView, pivot, sceneView.rotation, sceneView.size, sceneView.orthographic);
            return SceneViewChangeReport("SceneView panned.", before, sceneView);
        }

        /// <summary>Orbits the SceneView rotation by yaw and pitch deltas.</summary>
        /// <param name="args">Yaw and pitch deltas in degrees.</param>
        /// <returns>A confirmation line with SceneView state, or a validation error.</returns>
        private static string OrbitSceneView(string args)
        {
            var values = ParseFloatList(args);
            if (values.Length != 2)
            {
                return "Usage: scene-view-orbit <yawDegrees> <pitchDegrees>";
            }

            var sceneView = GetSceneView();
            if (sceneView == null)
            {
                return "No SceneView is available.";
            }

            var yaw = Quaternion.AngleAxis(values[0], Vector3.up);
            var pitch = Quaternion.AngleAxis(values[1], sceneView.rotation * Vector3.right);
            var rotation = yaw * pitch * sceneView.rotation;
            var before = FormatSceneViewState(sceneView);
            ApplySceneView(sceneView, sceneView.pivot, rotation, sceneView.size, sceneView.orthographic);
            return SceneViewChangeReport("SceneView orbited.", before, sceneView);
        }

        /// <summary>Scales the SceneView size by a positive zoom factor.</summary>
        /// <param name="args">Positive zoom factor; below one zooms in, above one zooms out.</param>
        /// <returns>A confirmation line with SceneView state, or a validation error.</returns>
        private static string ZoomSceneView(string args)
        {
            float factor;
            if (!TryParseFloat(args, out factor) || factor <= 0f)
            {
                return "Usage: scene-view-zoom <positiveFactor>. Values below 1 zoom in; values above 1 zoom out.";
            }

            var sceneView = GetSceneView();
            if (sceneView == null)
            {
                return "No SceneView is available.";
            }

            var before = FormatSceneViewState(sceneView);
            ApplySceneView(sceneView, sceneView.pivot, sceneView.rotation, Mathf.Max(0.01f, sceneView.size * factor), sceneView.orthographic);
            return SceneViewChangeReport("SceneView zoomed.", before, sceneView);
        }

        /// <summary>Moves the SceneView pivot along the view's right, up, and forward axes.</summary>
        /// <param name="args">Right, up, and forward movement values.</param>
        /// <returns>A confirmation line with SceneView state, or a validation error.</returns>
        private static string MoveSceneView(string args)
        {
            var values = ParseFloatList(args);
            if (values.Length != 3)
            {
                return "Usage: scene-view-move <right> <up> <forward>";
            }

            var sceneView = GetSceneView();
            if (sceneView == null)
            {
                return "No SceneView is available.";
            }

            var right = sceneView.rotation * Vector3.right;
            var up = sceneView.rotation * Vector3.up;
            var forward = sceneView.rotation * Vector3.forward;
            var pivot = sceneView.pivot + (right * values[0]) + (up * values[1]) + (forward * values[2]);
            var before = FormatSceneViewState(sceneView);
            ApplySceneView(sceneView, pivot, sceneView.rotation, sceneView.size, sceneView.orthographic);
            return SceneViewChangeReport("SceneView moved.", before, sceneView);
        }

        /// <summary>
        /// Frames a resolved GameObject in the SceneView the way pressing F does: SceneView.Frame fits the bounds of
        /// what the object draws (see GetGameObjectBounds) to the view, at the current rotation and projection.
        /// </summary>
        /// <param name="target">Scene path, name, or instance id to frame.</param>
        /// <returns>A confirmation line with the bounds framed and the SceneView state, or a lookup error.</returns>
        private static string FrameSceneView(string target)
        {
            GameObject gameObject;
            var error = TryFindGameObject(target, out gameObject);
            if (error != null)
            {
                return error;
            }

            var sceneView = GetSceneView();
            if (sceneView == null)
            {
                return "No SceneView is available.";
            }

            var before = FormatSceneViewState(sceneView);
            Selection.activeGameObject = gameObject;
            var bounds = GetGameObjectBounds(gameObject);
            sceneView.Frame(bounds, true);
            sceneView.Repaint();
            return SceneViewChangeReport("SceneView framed: " + GetGameObjectPath(gameObject) + Environment.NewLine
                + "bounds: centre " + FormatVector3(bounds.center) + ", size " + FormatVector3(bounds.size), before, sceneView);
        }

        /// <summary>Casts a ray through a scene-view pixel and reports the nearest thing it hits.</summary>
        /// <param name="args">Pixel x and y (top-left origin), then the image size they came from when it is not the live view's.</param>
        /// <returns>The nearest collider or render-mesh hit with its scene path, or a miss statement carrying the ray.</returns>
        private static string PickSceneView(string args)
        {
            const string usage = "Usage: scene-view-pick <x> <y> [imageWidth imageHeight]";
            const float pickRange = 10000f;

            var tokens = SplitCommandTokens(args);
            if (tokens.Length != 2 && tokens.Length != 4)
            {
                return usage;
            }

            float pixelX;
            float pixelY;
            var imageWidth = 0f;
            var imageHeight = 0f;
            if (!TryParseFloat(tokens[0], out pixelX) || !TryParseFloat(tokens[1], out pixelY)
                || (tokens.Length == 4 && (!TryParseFloat(tokens[2], out imageWidth) || !TryParseFloat(tokens[3], out imageHeight))))
            {
                return "Coordinates must be numbers. " + usage;
            }

            var sceneView = GetSceneView();
            if (sceneView == null || sceneView.camera == null)
            {
                return "No SceneView camera is available to pick through.";
            }

            var camera = sceneView.camera;
            if (imageWidth <= 0f || imageHeight <= 0f)
            {
                imageWidth = camera.pixelWidth;
                imageHeight = camera.pixelHeight;
            }

            if (pixelX < 0f || pixelX > imageWidth || pixelY < 0f || pixelY > imageHeight)
            {
                return "Pixel (" + tokens[0] + ", " + tokens[1] + ") is outside the " + imageWidth.ToString("0.#") + "x"
                     + imageHeight.ToString("0.#") + " image; pass the size of the image the coordinates came from.";
            }

            // Image pixels have a top-left origin while viewport space is bottom-up, so y flips here
            // rather than in every caller.
            var viewport = new Vector3(pixelX / imageWidth, 1f - pixelY / imageHeight, 0f);
            var ray = camera.ViewportPointToRay(viewport);

            // Edit-mode physics reports stale collider positions for freshly created or moved objects
            // until a sync, which would turn a pick into a confident wrong answer about where things are.
            Physics.SyncTransforms();

            var colliderHit = default(RaycastHit);
            var colliderFound = false;
            var triggerNames = new List<string>();
            foreach (var hit in Physics.RaycastAll(ray, pickRange))
            {
                if (hit.collider.isTrigger)
                {
                    triggerNames.Add(hit.collider.name);
                    continue;
                }

                if (!colliderFound || hit.distance < colliderHit.distance)
                {
                    colliderHit = hit;
                    colliderFound = true;
                }
            }

            // Render meshes are tested as well, because scene and UI geometry frequently has no
            // collider, and a pick that only sees physics reports such geometry as empty space.
            var meshFound = false;
            var meshDistance = float.MaxValue;
            var meshHit = default(RaycastHit);
            GameObject meshObject = null;
            var intersectRayMesh = typeof(HandleUtility).GetMethod("IntersectRayMesh", BindingFlags.NonPublic | BindingFlags.Static);
            if (intersectRayMesh != null)
            {
                foreach (var filter in UnityEngine.Object.FindObjectsOfType<MeshFilter>())
                {
                    var renderer = filter.GetComponent<Renderer>();
                    if (filter.sharedMesh == null || renderer == null || !renderer.enabled)
                    {
                        continue;
                    }

                    // A cheap bounds test keeps the per-triangle intersection off every mesh the ray
                    // cannot reach in a large scene.
                    if (!renderer.bounds.IntersectRay(ray))
                    {
                        continue;
                    }

                    var arguments = new object[] { ray, filter.sharedMesh, filter.transform.localToWorldMatrix, null };
                    if (!(bool)intersectRayMesh.Invoke(null, arguments) || !(arguments[3] is RaycastHit))
                    {
                        continue;
                    }

                    var hit = (RaycastHit)arguments[3];
                    var distance = Vector3.Distance(ray.origin, hit.point);
                    if (distance < meshDistance && distance <= pickRange)
                    {
                        meshDistance = distance;
                        meshHit = hit;
                        meshObject = filter.gameObject;
                        meshFound = true;
                    }
                }
            }

            var builder = new StringBuilder();
            var rayLine = "ray " + ray.origin.ToString("F3") + " -> " + ray.direction.ToString("F3")
                        + "  through pixel (" + tokens[0] + ", " + tokens[1] + ") of " + imageWidth.ToString("0.#") + "x" + imageHeight.ToString("0.#");

            if (!colliderFound && !meshFound)
            {
                builder.Append("Nothing under that pixel within ").Append(pickRange.ToString("0")).Append(" m.").AppendLine().Append("  ").Append(rayLine);
                if (triggerNames.Count > 0)
                {
                    builder.AppendLine().Append("  ").Append(triggerNames.Count).Append(" trigger collider(s) crossed: ").Append(string.Join(", ", triggerNames));
                }

                if (intersectRayMesh == null)
                {
                    builder.AppendLine().Append("  render meshes were not tested: HandleUtility.IntersectRayMesh did not resolve.");
                }

                return builder.ToString();
            }

            var meshWins = meshFound && (!colliderFound || meshDistance < colliderHit.distance);
            var pickedObject = meshWins ? meshObject : colliderHit.collider.gameObject;
            var pickedPoint = meshWins ? meshHit.point : colliderHit.point;
            var pickedNormal = meshWins ? meshHit.normal : colliderHit.normal;
            var pickedDistance = meshWins ? meshDistance : colliderHit.distance;

            builder.Append("Hit ").Append(GetGameObjectPath(pickedObject)).AppendLine()
                .Append("  point ").Append(pickedPoint.ToString("F3"))
                .Append("  normal ").Append(pickedNormal.ToString("F3"))
                .Append("  distance ").Append(pickedDistance.ToString("F3")).AppendLine()
                .Append("  via ").Append(meshWins ? "render mesh" : "collider '" + colliderHit.collider.GetType().Name + "'").AppendLine()
                .Append("  ").Append(rayLine);

            if (meshWins && colliderFound && colliderHit.collider.gameObject != pickedObject)
            {
                builder.AppendLine().Append("  nearest collider behind it: ").Append(GetGameObjectPath(colliderHit.collider.gameObject))
                    .Append(" at ").Append(colliderHit.distance.ToString("F3")).Append(" m");
            }
            else if (!meshWins && meshFound && meshObject != pickedObject)
            {
                builder.AppendLine().Append("  nearest render mesh behind it: ").Append(GetGameObjectPath(meshObject))
                    .Append(" at ").Append(meshDistance.ToString("F3")).Append(" m");
            }

            if (triggerNames.Count > 0)
            {
                builder.AppendLine().Append("  ").Append(triggerNames.Count).Append(" trigger collider(s) crossed: ").Append(string.Join(", ", triggerNames));
            }

            return builder.ToString();
        }

        /// <summary>Aligns the SceneView rotation to a named orthographic-style direction.</summary>
        /// <param name="direction">front, back, left, right, top, bottom, or iso.</param>
        /// <returns>A confirmation line with SceneView state, or a validation error.</returns>
        private static string AlignSceneView(string direction)
        {
            direction = (direction ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
            if (string.IsNullOrEmpty(direction))
            {
                return "Usage: scene-view-align <front|back|left|right|top|bottom|iso>";
            }

            var sceneView = GetSceneView();
            if (sceneView == null)
            {
                return "No SceneView is available.";
            }

            Quaternion rotation;
            if (direction == "front")
            {
                rotation = Quaternion.LookRotation(Vector3.forward, Vector3.up);
            }
            else if (direction == "back")
            {
                rotation = Quaternion.LookRotation(Vector3.back, Vector3.up);
            }
            else if (direction == "left")
            {
                rotation = Quaternion.LookRotation(Vector3.left, Vector3.up);
            }
            else if (direction == "right")
            {
                rotation = Quaternion.LookRotation(Vector3.right, Vector3.up);
            }
            else if (direction == "top")
            {
                rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            }
            else if (direction == "bottom")
            {
                rotation = Quaternion.LookRotation(Vector3.up, Vector3.back);
            }
            else if (direction == "iso" || direction == "isometric")
            {
                rotation = Quaternion.Euler(35.264f, 45f, 0f);
            }
            else
            {
                return "Unknown scene view alignment: " + direction;
            }

            var before = FormatSceneViewState(sceneView);
            ApplySceneView(sceneView, sceneView.pivot, rotation, sceneView.size, sceneView.orthographic);
            return SceneViewChangeReport("SceneView aligned: " + direction, before, sceneView);
        }

        /// <summary>Returns the last active SceneView, first open SceneView, or opens one if none exist.</summary>
        /// <returns>Available SceneView instance.</returns>
        private static SceneView GetSceneView()
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView != null)
            {
                return sceneView;
            }

            if (SceneView.sceneViews.Count > 0)
            {
                return SceneView.sceneViews[0] as SceneView;
            }

            return EditorWindow.GetWindow<SceneView>();
        }

        /// <summary>Applies pivot, rotation, size, and projection mode to a SceneView and repaints it.</summary>
        /// <param name="sceneView">SceneView to update.</param>
        /// <param name="pivot">World-space view pivot.</param>
        /// <param name="rotation">View rotation.</param>
        /// <param name="size">View size.</param>
        /// <param name="orthographic">Whether the view should be orthographic.</param>
        private static void ApplySceneView(SceneView sceneView, Vector3 pivot, Quaternion rotation, float size, bool orthographic)
        {
            sceneView.LookAt(pivot, rotation, Mathf.Max(0.01f, size), orthographic, true);
            sceneView.Repaint();
        }

        /// <summary>Reports a scene view change with the view as it was before and as it reads back now.</summary>
        /// <param name="lead">What the verb did.</param>
        /// <param name="before">The view's state before the change.</param>
        /// <param name="sceneView">The changed view.</param>
        /// <returns>The lead, then the state before and after.</returns>
        private static string SceneViewChangeReport(string lead, string before, SceneView sceneView)
        {
            return lead + Environment.NewLine + "before:" + Environment.NewLine + before + Environment.NewLine +
                "after:" + Environment.NewLine + FormatSceneViewState(sceneView);
        }

        /// <summary>Formats pivot, rotation, size, projection, and camera transform for a SceneView.</summary>
        /// <param name="sceneView">SceneView to inspect.</param>
        /// <returns>Text report for the SceneView state.</returns>
        private static string FormatSceneViewState(SceneView sceneView)
        {
            var euler = sceneView.rotation.eulerAngles;
            var builder = new StringBuilder();
            builder.AppendLine("pivot: " + FormatVector3(sceneView.pivot));
            builder.AppendLine("rotationEuler: " + FormatVector3(euler));
            builder.AppendLine("size: " + sceneView.size.ToString("0.###", CultureInfo.InvariantCulture));
            builder.AppendLine("orthographic: " + sceneView.orthographic);
            if (sceneView.camera != null)
            {
                builder.AppendLine("cameraPosition: " + FormatVector3(sceneView.camera.transform.position));
                builder.AppendLine("cameraEuler: " + FormatVector3(sceneView.camera.transform.eulerAngles));
            }

            return builder.ToString().TrimEnd();
        }
            /// <summary>
        /// Routes the SceneView verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchSceneView(string verb, string command, string[] parts)
        {
            if (verb == "scene-view-state" || verb == "viewport-state")
            {
                return FormatSceneViewState();
            }

            if (verb == "scene-view-pivot" || verb == "viewport-pivot")
            {
                return SetSceneViewPivot(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-view-rotation" || verb == "viewport-rotation")
            {
                return SetSceneViewRotation(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-view-size" || verb == "viewport-size")
            {
                return SetSceneViewSize(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-view-pan" || verb == "viewport-pan")
            {
                return PanSceneView(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-view-orbit" || verb == "viewport-orbit")
            {
                return OrbitSceneView(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-view-zoom" || verb == "viewport-zoom")
            {
                return ZoomSceneView(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-view-move" || verb == "viewport-move")
            {
                return MoveSceneView(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-view-frame" || verb == "viewport-frame")
            {
                return FrameSceneView(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-view-pick" || verb == "viewport-pick")
            {
                return PickSceneView(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-view-align" || verb == "viewport-align")
            {
                return AlignSceneView(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
