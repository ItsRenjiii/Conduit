/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Commands for driving Unity's own editor windows: listing what is open, enumerating the controls
    /// inside one BY LABEL, and delivering real mouse and keyboard events to them.
    ///
    /// The controls come from Unity's IMGUI Debugger data (`GUIViewDebuggerHelper`), which records every
    /// draw instruction a view issues, each carrying its rect, its GUIStyle and its GUIContent. That is what
    /// makes an IMGUI window addressable at all: IMGUI rebuilds its controls every frame and keeps no
    /// retained tree, so there is nothing to query the way UI Toolkit can be queried - but the draw
    /// instructions ARE the controls, complete with their text. Screenshots are deliberately not used for
    /// this: they cost image tokens on every look and shut out non-multimodal callers. When the look of a
    /// window is itself the question, screenshot-window captures it.
    ///
    /// Clicks go through `EditorWindow.SendEvent` so the window's own hit testing decides what was hit,
    /// rather than reflecting onto the method behind a button and skipping its disabled state and layout.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Editor Window Commands

        /// <summary>
        /// Lists the editor windows currently open, with the screen rectangle each occupies and the editor's
        /// DPI scale.
        /// </summary>
        /// <param name="args">Optional case-insensitive filter matched against type name and title.</param>
        /// <returns>A compact JSON array of open windows.</returns>
        private static string EditorWindowListCommand(string args)
        {
            string filter = (args ?? string.Empty).Trim().Trim('"');
            EditorWindow focused = EditorWindow.focusedWindow;

            var matched = new List<EditorWindow>();
            foreach (EditorWindow window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null)
                {
                    continue;
                }

                string typeName = window.GetType().Name;
                string title = window.titleContent == null ? string.Empty : window.titleContent.text;
                if (filter.Length > 0
                    && typeName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                matched.Add(window);
            }

            var builder = new StringBuilder();
            builder.Append("{\"count\":").Append(matched.Count).Append(",\"windows\":[");
            for (var i = 0; i < matched.Count; i++)
            {
                EditorWindow window = matched[i];
                Rect position = window.position;
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"type\":").Append(JsonQuote(window.GetType().FullName));
                builder.Append(",\"name\":").Append(JsonQuote(window.GetType().Name));
                builder.Append(",\"title\":").Append(JsonQuote(window.titleContent == null ? string.Empty : window.titleContent.text));
                builder.Append(",\"screenX\":").Append(JsonNumber(position.x));
                builder.Append(",\"screenY\":").Append(JsonNumber(position.y));
                builder.Append(",\"width\":").Append(JsonNumber(position.width));
                builder.Append(",\"height\":").Append(JsonNumber(position.height));
                builder.Append(",\"focused\":").Append(window == focused ? "true" : "false");
                builder.Append(",\"pixelsPerPoint\":").Append(JsonNumber(EditorGUIUtility.pixelsPerPoint));
                builder.Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        /// <summary>
        /// Lists the labelled controls inside an editor window, so a caller can click one by name instead of
        /// hunting for it in a screenshot.
        /// </summary>
        /// <param name="args">Window selector, optionally followed by a pipe and a style/text filter.</param>
        /// <returns>A JSON list of controls with style, text and window-local rect.</returns>
        private static string EditorWindowControlsCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length == 0 || sections[0].Trim().Length == 0)
            {
                return "Usage: editor-window-controls <window> [| filter]";
            }

            EditorWindow window;
            string error = TryFindEditorWindow(sections[0], out window);
            if (error != null)
            {
                return error;
            }

            List<EditorControl> controls;
            error = CaptureControls(window, out controls);
            if (error != null)
            {
                return error;
            }

            string filter = sections.Length > 1 ? sections[1].Trim().Trim('"') : string.Empty;
            var builder = new StringBuilder();
            builder.Append("{\"window\":").Append(JsonQuote(window.GetType().Name));
            builder.Append(",\"controls\":[");

            var shown = 0;
            for (var i = 0; i < controls.Count; i++)
            {
                EditorControl control = controls[i];
                if (filter.Length > 0
                    && control.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && control.Style.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (shown > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"index\":").Append(i);
                builder.Append(",\"source\":").Append(JsonQuote(control.Source));
                builder.Append(",\"style\":").Append(JsonQuote(control.Style));
                builder.Append(",\"text\":").Append(JsonQuote(control.Text));
                builder.Append(",\"enabled\":").Append(control.Enabled ? "true" : "false");
                builder.Append(",\"x\":").Append(JsonNumber(control.Rect.x));
                builder.Append(",\"y\":").Append(JsonNumber(control.Rect.y));
                builder.Append(",\"width\":").Append(JsonNumber(control.Rect.width));
                builder.Append(",\"height\":").Append(JsonNumber(control.Rect.height));
                builder.Append('}');
                shown++;
            }

            builder.Append("],\"count\":").Append(shown).Append('}');
            return builder.ToString();
        }

        /// <summary>
        /// Clicks a control in an editor window, addressed either by its LABEL or by a window-local
        /// position, by sending the MouseDown/MouseUp pair the editor itself would.
        /// </summary>
        /// <param name="args">Window selector, then a label (optionally `Label#index`) or an `x y` pair.</param>
        /// <returns>A report naming what was clicked and where.</returns>
        private static string EditorWindowClickCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: editor-window-click <window> | <label>   (or: <window> | <x> <y>)";
            }

            EditorWindow window;
            string error = TryFindEditorWindow(sections[0], out window);
            if (error != null)
            {
                return error;
            }

            var tokens = SplitCommandTokens(sections[1]);
            float x;
            float y;
            string what;

            // Two numbers means a raw position; anything else is treated as a label, which is the form that
            // survives a window being resized or re-laid-out.
            if (tokens.Length >= 2 && float.TryParse(tokens[0], out x) && float.TryParse(tokens[1], out y))
            {
                what = "position";
            }
            else
            {
                EditorControl control;
                error = ResolveControlByLabel(window, sections[1].Trim().Trim('"'), out control);
                if (error != null)
                {
                    return error;
                }

                // A control that has never been laid out carries a zero or NaN rect - the case for an
                // overlay switched on moments ago. Clicking its "centre" would fire at a meaningless point,
                // so refuse and say why rather than click somewhere arbitrary.
                // Refused rather than clicked: a disabled control still draws and still matches by label, so
                // clicking it looks like a successful no-op and sends the caller hunting for the wrong fault.
                if (!control.Enabled)
                {
                    return "'" + control.Text + "' [" + control.Style + "] is DISABLED, so clicking it would do nothing. "
                           + "Whatever greys it out has to change first.";
                }

                Rect bounds = control.Rect;
                if (float.IsNaN(bounds.x) || float.IsNaN(bounds.y)
                    || float.IsNaN(bounds.width) || float.IsNaN(bounds.height)
                    || bounds.width <= 0f || bounds.height <= 0f)
                {
                    return "'" + control.Text + "' has no usable rect yet (" + bounds
                           + "). It has not been laid out - if it was just shown, let the window repaint and try again.";
                }

                x = bounds.center.x;
                y = bounds.center.y;
                what = "'" + control.Text + "' [" + control.Style + "]";
            }

            Rect position = window.position;
            if (x < 0f || y < 0f || x > position.width || y > position.height)
            {
                return "Refused: (" + JsonNumber(x) + ", " + JsonNumber(y) + ") is outside this window ("
                       + JsonNumber(position.width) + " x " + JsonNumber(position.height) + " points).";
            }

            var button = 0;
            if (what == "position" && tokens.Length > 2 && !int.TryParse(tokens[2], out button))
            {
                return "Mouse button must be an integer (0 left, 1 right, 2 middle). Got: " + tokens[2];
            }

            window.Focus();
            var point = new Vector2(x, y);
            bool downUsed = window.SendEvent(MouseEvent(EventType.MouseDown, point, button));
            bool upUsed = window.SendEvent(MouseEvent(EventType.MouseUp, point, button));
            window.Repaint();

            return "Clicked " + what + " in " + window.GetType().Name
                   + " at window-local (" + JsonNumber(x) + ", " + JsonNumber(y) + ")."
                   + Environment.NewLine + "MouseDown consumed: " + downUsed + ", MouseUp consumed: " + upUsed;
        }

        /// <summary>
        /// Sends a key press to an open editor window, for controls that respond to the keyboard: text
        /// fields, list navigation, and window shortcut keys.
        /// </summary>
        /// <param name="args">Window selector, then the key name, with an optional modifier list.</param>
        /// <returns>A report naming the resolved window and whether the key was consumed.</returns>
        private static string EditorWindowKeyCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 2)
            {
                return "Usage: editor-window-key <window> | <key> [shift|ctrl|alt|cmd ...]";
            }

            EditorWindow window;
            string error = TryFindEditorWindow(sections[0], out window);
            if (error != null)
            {
                return error;
            }

            var tokens = SplitCommandTokens(sections[1]);
            if (tokens.Length == 0)
            {
                return "Missing key name. Usage: editor-window-key <window> | <key> [shift|ctrl|alt|cmd ...]";
            }

            KeyCode key;
            if (!TryParseEnum(tokens[0], out key))
            {
                return "Unknown key '" + tokens[0] + "'. Use a UnityEngine.KeyCode name, e.g. Return, Escape, A, Delete.";
            }

            EventModifiers modifiers = EventModifiers.None;
            for (var i = 1; i < tokens.Length; i++)
            {
                switch (tokens[i].ToLowerInvariant())
                {
                    case "shift":   modifiers |= EventModifiers.Shift;   break;
                    case "ctrl":
                    case "control": modifiers |= EventModifiers.Control; break;
                    case "alt":     modifiers |= EventModifiers.Alt;     break;
                    case "cmd":
                    case "command": modifiers |= EventModifiers.Command; break;
                    default:
                        return "Unknown modifier '" + tokens[i] + "'. Use shift, ctrl, alt, or cmd.";
                }
            }

            window.Focus();
            bool downUsed = window.SendEvent(new Event { type = EventType.KeyDown, keyCode = key, modifiers = modifiers });
            bool upUsed = window.SendEvent(new Event { type = EventType.KeyUp, keyCode = key, modifiers = modifiers });
            window.Repaint();

            return "Sent " + key + (modifiers == EventModifiers.None ? string.Empty : " (" + modifiers + ")")
                   + " to " + window.GetType().Name + "."
                   + Environment.NewLine + "KeyDown consumed: " + downUsed + ", KeyUp consumed: " + upUsed;
        }

        /// <summary>
        /// Lists a window's overlays with their identity and display state. Overlays are a third UI system,
        /// separate from both IMGUI and the window's UI Toolkit tree, so they need their own reader: an
        /// overlay is NOT reachable from `rootVisualElement`, and a hidden one still exists and can be
        /// switched back on.
        /// </summary>
        /// <param name="args">Window selector, optionally followed by a pipe and a name/id filter.</param>
        /// <returns>A JSON list of overlays.</returns>
        private static string EditorWindowOverlaysCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length == 0 || sections[0].Trim().Length == 0)
            {
                return "Usage: editor-window-overlays <window> [| filter]";
            }

            EditorWindow window;
            string error = TryFindEditorWindow(sections[0], out window);
            if (error != null)
            {
                return error;
            }

            List<object> overlays;
            error = GetOverlays(window, out overlays);
            if (error != null)
            {
                return error;
            }

            string filter = sections.Length > 1 ? sections[1].Trim().Trim('"') : string.Empty;
            var builder = new StringBuilder();
            builder.Append("{\"window\":").Append(JsonQuote(window.GetType().Name)).Append(",\"overlays\":[");

            var shown = 0;
            foreach (object overlay in overlays)
            {
                string id = OverlayString(overlay, "id");
                string display = OverlayString(overlay, "displayName");
                if (filter.Length > 0
                    && id.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && display.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (shown > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"id\":").Append(JsonQuote(id));
                builder.Append(",\"displayName\":").Append(JsonQuote(display));
                builder.Append(",\"displayed\":").Append(OverlayBool(overlay, "displayed"));
                builder.Append(",\"collapsed\":").Append(OverlayBool(overlay, "collapsed"));
                builder.Append(",\"floating\":").Append(OverlayBool(overlay, "floating"));
                builder.Append('}');
                shown++;
            }

            builder.Append("],\"count\":").Append(shown).Append('}');
            return builder.ToString();
        }

        /// <summary>
        /// Turns an overlay on or off, or collapses/floats it, reporting the value before and after so the
        /// caller can see the change actually landed rather than trusting a bare success.
        /// </summary>
        /// <param name="args">Window selector, overlay id or name, then `property value`.</param>
        /// <returns>A report with before and after values.</returns>
        private static string EditorWindowOverlaySetCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return "Usage: editor-window-overlay-set <window> | <overlayIdOrName> | <displayed|collapsed|floating> <true|false>";
            }

            EditorWindow window;
            string error = TryFindEditorWindow(sections[0], out window);
            if (error != null)
            {
                return error;
            }

            List<object> overlays;
            error = GetOverlays(window, out overlays);
            if (error != null)
            {
                return error;
            }

            string wanted = sections[1].Trim().Trim('"');
            object target = null;
            foreach (object overlay in overlays)
            {
                if (string.Equals(OverlayString(overlay, "id"), wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(OverlayString(overlay, "displayName"), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    target = overlay;
                    break;
                }
            }

            if (target == null)
            {
                return "No overlay '" + wanted + "' on " + window.GetType().Name
                       + ". Run editor-window-overlays to see what is there.";
            }

            var tokens = SplitCommandTokens(sections[2]);
            if (tokens.Length < 2)
            {
                return "Missing property and value. Usage: ... | <displayed|collapsed|floating> <true|false>";
            }

            string property = tokens[0];
            bool value;
            if (!bool.TryParse(tokens[1], out value))
            {
                return "Value must be true or false. Got: " + tokens[1];
            }

            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo info = target.GetType().GetProperty(property, All);
            if (info == null || info.PropertyType != typeof(bool) || !info.CanWrite)
            {
                return "'" + property + "' is not a writable boolean on this overlay. Use displayed, collapsed, or floating.";
            }

            var before = (bool)info.GetValue(target, null);
            info.SetValue(target, value, null);
            var after = (bool)info.GetValue(target, null);
            window.Repaint();

            return "Overlay '" + OverlayString(target, "displayName") + "' on " + window.GetType().Name
                   + ": " + property + " " + before + " -> " + after
                   + (before == after && before != value
                       ? Environment.NewLine + "The overlay refused the change; it may be unavailable in this context."
                       : string.Empty);
        }

        /// <summary>
        /// Reports the Scene view's tool, pivot, snapping and display state. Read straight from the editor's
        /// own state rather than scraped from the Tools and Grid overlays, because the overlays are only a
        /// view onto these values and reading the source cannot go stale or mis-parse.
        /// </summary>
        /// <param name="args">Unused.</param>
        /// <returns>A JSON object of tool, snap and scene view state.</returns>
        private static string SceneToolStateCommand(string args)
        {
            var builder = new StringBuilder();
            builder.Append("{\"tool\":").Append(JsonQuote(Tools.current.ToString()));
            builder.Append(",\"pivotMode\":").Append(JsonQuote(Tools.pivotMode.ToString()));
            builder.Append(",\"pivotRotation\":").Append(JsonQuote(Tools.pivotRotation.ToString()));

            // handlePosition is Infinity when nothing is selected, which would serialise as a nonsense
            // number, so the empty selection is reported as null instead.
            bool hasSelection = Selection.activeTransform != null;
            builder.Append(",\"hasSelection\":").Append(hasSelection ? "true" : "false");
            if (hasSelection)
            {
                Vector3 handle = Tools.handlePosition;
                Vector3 euler = Tools.handleRotation.eulerAngles;
                builder.Append(",\"handlePosition\":").Append(JsonVector3(handle));
                builder.Append(",\"handleRotation\":").Append(JsonVector3(euler));
            }
            else
            {
                builder.Append(",\"handlePosition\":null,\"handleRotation\":null");
            }

            builder.Append(",\"snap\":{\"gridSnapEnabled\":").Append(EditorSnapSettings.gridSnapEnabled ? "true" : "false");
            builder.Append(",\"move\":").Append(JsonVector3(EditorSnapSettings.move));
            builder.Append(",\"rotate\":").Append(JsonNumber(EditorSnapSettings.rotate));
            builder.Append(",\"scale\":").Append(JsonNumber(EditorSnapSettings.scale)).Append('}');

            SceneView view = SceneView.lastActiveSceneView;
            if (view == null)
            {
                builder.Append(",\"sceneView\":null}");
                return builder.ToString();
            }

            builder.Append(",\"sceneView\":{\"in2DMode\":").Append(view.in2DMode ? "true" : "false");
            builder.Append(",\"orthographic\":").Append(view.orthographic ? "true" : "false");
            builder.Append(",\"showGrid\":").Append(view.showGrid ? "true" : "false");
            builder.Append(",\"drawGizmos\":").Append(view.drawGizmos ? "true" : "false");
            builder.Append(",\"pivot\":").Append(JsonVector3(view.pivot));
            builder.Append(",\"rotation\":").Append(JsonVector3(view.rotation.eulerAngles));
            builder.Append(",\"size\":").Append(JsonNumber(view.size)).Append("}}");
            return builder.ToString();
        }

        /// <summary>
        /// Closes one open editor window, reporting what it closed and whether that edited the docked layout.
        /// </summary>
        /// <remarks>
        /// The type, title and docked state are read BEFORE the close because a closed window cannot describe
        /// itself, and the outcome is confirmed by looking for a live window with the same instance id: a
        /// destroyed <see cref="UnityEngine.Object"/> compares equal to null rather than to the window it was,
        /// so testing the reference afterwards would report every attempt as a success. Closing a DOCKED tab
        /// removes it from the layout Unity saves rather than hiding it, so the result says so instead of
        /// leaving a caller to discover it later.
        /// </remarks>
        /// <param name="args">Window selector, matched as the other window verbs match it.</param>
        /// <returns>What was closed, or what could not be.</returns>
        private static string EditorWindowCloseCommand(string args)
        {
            string selector = (args ?? string.Empty).Trim();
            if (selector.Length == 0)
            {
                return "Usage: editor-window-close <window>";
            }

            EditorWindow window;
            string error = TryFindEditorWindow(selector, out window);
            if (error != null)
            {
                return error;
            }

            string typeName = window.GetType().Name;
            string title = window.titleContent == null ? string.Empty : (window.titleContent.text ?? string.Empty);
            string named = typeName + (title.Length > 0 ? " (\"" + title + "\")" : string.Empty);

            // Refused rather than attempted, because Unity answers an unsaved window's close with a modal save
            // prompt, and that blocks the editor thread this call needs in order to answer at all.
            if (window.hasUnsavedChanges)
            {
                string prompt = (window.saveChangesMessage ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
                return "Refusing to close " + named + ": it reports unsaved changes, so closing it opens a save "
                    + "prompt that blocks the editor until somebody answers it"
                    + (prompt.Length > 0 ? " - \"" + prompt + "\"" : string.Empty)
                    + ". Save or discard it first.";
            }

            bool wasDocked = window.docked;
            int instanceId = window.GetInstanceID();

            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                return "Could not close " + named + ": " + ex.Message;
            }

            foreach (EditorWindow open in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (open == null || open.GetInstanceID() != instanceId) continue;

                return "Asked " + named + " to close and it is still open - it may be holding a prompt open, "
                    + "or refusing to close.";
            }

            if (!wasDocked)
            {
                return "Closed " + named + ".";
            }

            return "Closed " + named + ". It was DOCKED, so it is GONE FROM THE SAVED EDITOR LAYOUT rather than "
                + "just hidden - reopen it from the Window menu, and expect its tab to come back at the end of "
                + "the strip.";
        }

        #endregion

        #region Editor Window Helpers

        /// <summary>
        /// Maps each IMGUI draw instruction index to whether it was drawn enabled, by walking the unified
        /// instruction stream where the flag actually lives.
        /// </summary>
        /// <param name="helper">The GUIViewDebuggerHelper type, already in a debugging session.</param>
        /// <param name="all">Binding flags for the internal members.</param>
        /// <returns>Draw-instruction index to enabled state; missing entries default to enabled.</returns>
        private static Dictionary<int, bool> CaptureEnabledFlags(Type helper, BindingFlags all)
        {
            var map = new Dictionary<int, bool>();
            Type unifiedType = FindLoadedType("UnityEditor.IMGUIInstruction");
            MethodInfo getUnified = helper.GetMethod("GetUnifiedInstructions", all);
            if (unifiedType == null || getUnified == null)
            {
                return map;
            }

            FieldInfo typeField = unifiedType.GetField("type", all);
            FieldInfo enabledField = unifiedType.GetField("enabled", all);
            FieldInfo indexField = unifiedType.GetField("typeInstructionIndex", all);
            if (typeField == null || enabledField == null || indexField == null)
            {
                return map;
            }

            object unified = Activator.CreateInstance(typeof(List<>).MakeGenericType(unifiedType));
            getUnified.Invoke(null, new[] { unified });

            foreach (object instruction in (IEnumerable)unified)
            {
                // Matched on the name rather than the enum value: the InstructionType members are internal
                // and have been renamed between versions, but the draw entries have always contained "Draw".
                string kind = typeField.GetValue(instruction).ToString();
                if (kind.IndexOf("Draw", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var index = (int)indexField.GetValue(instruction);
                if (index >= 0)
                {
                    map[index] = (bool)enabledField.GetValue(instruction);
                }
            }

            return map;
        }

        /// <summary>Reads a window's overlays, or explains why it has none to read.</summary>
        /// <param name="window">Window to inspect.</param>
        /// <param name="overlays">The overlay objects, in canvas order.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string GetOverlays(EditorWindow window, out List<object> overlays)
        {
            overlays = new List<object>();
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            PropertyInfo canvasProperty = window.GetType().GetProperty("overlayCanvas", All);
            object canvas = canvasProperty == null ? null : canvasProperty.GetValue(window, null);
            if (canvas == null)
            {
                return "'" + window.GetType().Name + "' has no overlay canvas; only Scene-view-like windows host overlays.";
            }

            PropertyInfo overlaysProperty = canvas.GetType().GetProperty("overlays", All);
            var items = overlaysProperty == null ? null : overlaysProperty.GetValue(canvas, null) as IEnumerable;
            if (items == null)
            {
                return "The overlay canvas on this Unity version does not expose an 'overlays' collection.";
            }

            foreach (object overlay in items)
            {
                if (overlay != null)
                {
                    overlays.Add(overlay);
                }
            }

            return null;
        }

        /// <summary>Reads a string property from an overlay, tolerating a member that is absent.</summary>
        private static string OverlayString(object overlay, string property)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo info = overlay.GetType().GetProperty(property, All);
            object value = info == null ? null : info.GetValue(overlay, null);
            return value == null ? string.Empty : value.ToString();
        }

        /// <summary>Reads a boolean property from an overlay as JSON, defaulting to false when absent.</summary>
        private static string OverlayBool(object overlay, string property)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            PropertyInfo info = overlay.GetType().GetProperty(property, All);
            if (info == null || info.PropertyType != typeof(bool))
            {
                return "false";
            }

            return (bool)info.GetValue(overlay, null) ? "true" : "false";
        }

        /// <summary>One labelled control found in a window, from either UI source.</summary>
        private struct EditorControl
        {
            /// <summary>GUIStyle name for IMGUI, or the element's type name for UI Toolkit.</summary>
            public string Style;

            /// <summary>Visible text, which is what a caller addresses the control by.</summary>
            public string Text;

            /// <summary>Window-local rectangle in GUI points.</summary>
            public Rect Rect;

            /// <summary>Which UI system drew it: "imgui" or "uitoolkit".</summary>
            public string Source;

            /// <summary>
            /// Whether the control was interactive when drawn. A disabled control still draws, still has a
            /// rect, and still matches by label, so without this a caller cannot tell "I clicked and nothing
            /// happened" from "that button is greyed out".
            /// </summary>
            public bool Enabled;
        }

        /// <summary>
        /// Captures the window's labelled controls by recording one repaint through Unity's IMGUI Debugger
        /// helper. Everything here is reached by reflection because the helper is internal; a Unity version
        /// that renames it degrades to a clear error rather than a crash.
        /// </summary>
        /// <param name="window">Window to inspect.</param>
        /// <param name="controls">Controls carrying text, in draw order.</param>
        /// <returns>Null on success, or a user-facing error.</returns>
        private static string CaptureControls(EditorWindow window, out List<EditorControl> controls)
        {
            controls = new List<EditorControl>();

            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                                     | BindingFlags.Static | BindingFlags.Instance;

            Type helper = FindLoadedType("UnityEditor.GUIViewDebuggerHelper");
            Type instructionType = FindLoadedType("UnityEditor.IMGUIDrawInstruction");
            FieldInfo parentField = typeof(EditorWindow).GetField("m_Parent", All);
            if (helper == null || instructionType == null || parentField == null)
            {
                return "This Unity version does not expose the IMGUI Debugger helper, so window controls cannot be listed.";
            }

            object view = parentField.GetValue(window);
            if (view == null)
            {
                return "Window '" + window.GetType().Name + "' has no host view, so it cannot be inspected.";
            }

            MethodInfo debugWindow = helper.GetMethod("DebugWindow", All);
            MethodInfo getInstructions = helper.GetMethod("GetDrawInstructions", All);
            MethodInfo stopDebugging = helper.GetMethod("StopDebugging", All);
            if (debugWindow == null || getInstructions == null || stopDebugging == null)
            {
                return "The IMGUI Debugger helper is present but its methods have changed, so window controls cannot be listed.";
            }

            try
            {
                debugWindow.Invoke(null, new[] { view });

                // The recorder fills during a repaint, so force one synchronously: reading the buffer before
                // the window has redrawn returns nothing at all.
                MethodInfo repaintNow = view.GetType().GetMethod("RepaintImmediately", All);
                if (repaintNow != null)
                {
                    repaintNow.Invoke(view, null);
                }
                else
                {
                    window.Repaint();
                }

                object list = Activator.CreateInstance(typeof(List<>).MakeGenericType(instructionType));
                getInstructions.Invoke(null, new[] { list });

                FieldInfo rectField = instructionType.GetField("rect", All);
                FieldInfo contentField = instructionType.GetField("usedGUIContent", All);
                FieldInfo styleField = instructionType.GetField("usedGUIStyle", All);
                if (rectField == null || contentField == null)
                {
                    return "IMGUIDrawInstruction no longer exposes rect/usedGUIContent on this Unity version.";
                }

                // A draw instruction says WHAT was drawn but not whether it was interactive. The enabled
                // flag lives on the parallel unified-instruction stream, linked back by typeInstructionIndex,
                // so the two have to be correlated to tell a live button from a greyed-out one.
                Dictionary<int, bool> enabledByIndex = CaptureEnabledFlags(helper, All);

                var drawIndex = 0;
                foreach (object instruction in (IEnumerable)list)
                {
                    int index = drawIndex++;
                    var content = contentField.GetValue(instruction) as GUIContent;
                    string text = content == null || content.text == null ? string.Empty : content.text;
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    bool enabled;
                    var style = styleField == null ? null : styleField.GetValue(instruction) as GUIStyle;
                    controls.Add(new EditorControl
                    {
                        Style = style == null ? string.Empty : style.name,
                        Text = text,
                        Rect = (Rect)rectField.GetValue(instruction),
                        Source = "imgui",
                        Enabled = !enabledByIndex.TryGetValue(index, out enabled) || enabled,
                    });
                }
            }
            finally
            {
                stopDebugging.Invoke(null, null);
            }

            // UI Toolkit is walked as well, not instead: the two systems coexist in one window and each is
            // invisible to the other's inspection. Scene-view OVERLAYS are the clearest case - they are
            // VisualElements and emit no IMGUI draw instructions at all, so an IMGUI-only reader reports a
            // window full of overlays as empty.
            CollectVisualElements(window.rootVisualElement, controls);

            // Overlays are a third system again, reachable from neither of the above: an overlay's contents
            // hang off its own contentRoot, not the window's tree. Only displayed overlays are walked -
            // a hidden one has never been laid out, so its elements carry zero or NaN rects.
            List<object> overlays;
            if (GetOverlays(window, out overlays) == null)
            {
                foreach (object overlay in overlays)
                {
                    PropertyInfo displayed = overlay.GetType().GetProperty("displayed", All);
                    if (displayed != null && !(bool)displayed.GetValue(overlay, null))
                    {
                        continue;
                    }

                    PropertyInfo content = overlay.GetType().GetProperty("contentRoot", All);
                    var root = content == null ? null : content.GetValue(overlay, null) as VisualElement;
                    if (root != null)
                    {
                        CollectVisualElements(root, controls, "overlay:" + OverlayString(overlay, "displayName"));
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Walks a UI Toolkit tree adding every element that carries text or a name, which is what a caller
        /// could address it by. Elements with neither are containers and are skipped.
        /// </summary>
        /// <param name="element">Element to inspect, along with its children.</param>
        /// <param name="controls">Accumulator shared with the IMGUI pass.</param>
        /// <param name="source">Source tag recorded on each control, naming which UI system it came from.</param>
        private static void CollectVisualElements(VisualElement element, List<EditorControl> controls, string source = "uitoolkit")
        {
            if (element == null)
            {
                return;
            }

            var textElement = element as TextElement;
            string text = textElement == null || textElement.text == null ? string.Empty : textElement.text;
            string name = element.name ?? string.Empty;
            if (text.Length > 0 || name.Length > 0)
            {
                controls.Add(new EditorControl
                {
                    Style = element.GetType().Name,
                    Text = text.Length > 0 ? text : name,
                    Rect = element.worldBound,
                    Source = source,
                    Enabled = element.enabledInHierarchy,
                });
            }

            foreach (VisualElement child in element.Children())
            {
                CollectVisualElements(child, controls, source);
            }
        }

        /// <summary>
        /// Finds one control by its label. A bare label that matches several controls is REFUSED with the
        /// candidates listed rather than resolved to the first, because repeated labels are the norm in a
        /// window with several cards; append `#index` to choose.
        /// </summary>
        /// <param name="window">Window to search.</param>
        /// <param name="selector">Label text, optionally suffixed with `#index`.</param>
        /// <param name="control">The resolved control when exactly one matches.</param>
        /// <returns>Null on success, or a user-facing error listing the candidates.</returns>
        private static string ResolveControlByLabel(EditorWindow window, string selector, out EditorControl control)
        {
            control = default(EditorControl);

            var wanted = selector;
            var ordinal = -1;
            int hash = selector.LastIndexOf('#');
            if (hash > 0 && int.TryParse(selector.Substring(hash + 1), out ordinal))
            {
                wanted = selector.Substring(0, hash).Trim();
            }

            List<EditorControl> controls;
            string error = CaptureControls(window, out controls);
            if (error != null)
            {
                return error;
            }

            var matches = new List<EditorControl>();
            foreach (EditorControl candidate in controls)
            {
                if (string.Equals(candidate.Text, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 0)
            {
                return "No control labelled '" + wanted + "' in " + window.GetType().Name
                       + ". Run editor-window-controls to see what is there.";
            }

            if (ordinal >= 0)
            {
                if (ordinal >= matches.Count)
                {
                    return "'" + wanted + "' has " + matches.Count + " matches, so #" + ordinal + " is out of range (0-"
                           + (matches.Count - 1) + ").";
                }

                control = matches[ordinal];
                return null;
            }

            if (matches.Count > 1)
            {
                var listed = new StringBuilder();
                for (var i = 0; i < matches.Count; i++)
                {
                    listed.Append(Environment.NewLine).Append("  ").Append(wanted).Append('#').Append(i)
                          .Append(" at y=").Append(JsonNumber(matches[i].Rect.y));
                }

                return "'" + wanted + "' matches " + matches.Count + " controls. Append #index to choose:" + listed;
            }

            control = matches[0];
            return null;
        }

        /// <summary>
        /// Resolves an open editor window by type name (short or full) or by window title. An ambiguous
        /// selector is refused with the candidates listed rather than resolved to whichever came first.
        /// </summary>
        /// <param name="selector">Type name or title to match, case-insensitive.</param>
        /// <param name="window">The resolved window when exactly one matches.</param>
        /// <returns>Null on success, or a user-facing lookup error.</returns>
        private static string TryFindEditorWindow(string selector, out EditorWindow window)
        {
            window = null;
            string wanted = (selector ?? string.Empty).Trim().Trim('"');
            if (wanted.Length == 0)
            {
                return "Missing window selector. Use a type name or title from editor-window-list.";
            }

            var matches = new List<EditorWindow>();
            foreach (EditorWindow candidate in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (candidate == null)
                {
                    continue;
                }

                Type type = candidate.GetType();
                string title = candidate.titleContent == null ? string.Empty : candidate.titleContent.text;
                if (string.Equals(type.Name, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(type.FullName, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(title, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(candidate);
                }
            }

            if (matches.Count == 0)
            {
                return "No open editor window matches '" + wanted + "'. Run editor-window-list to see what is open.";
            }

            if (matches.Count > 1)
            {
                var names = new StringBuilder();
                for (var i = 0; i < matches.Count; i++)
                {
                    if (i > 0)
                    {
                        names.Append(", ");
                    }

                    names.Append(matches[i].GetType().FullName);
                }

                return "'" + wanted + "' matches " + matches.Count + " open windows (" + names
                       + "). Use the full type name to disambiguate.";
            }

            window = matches[0];
            return null;
        }

        /// <summary>Builds a mouse event carrying the fields IMGUI needs to treat it as a genuine click.</summary>
        /// <param name="type">MouseDown or MouseUp.</param>
        /// <param name="position">Window-local position in points.</param>
        /// <param name="button">Mouse button index; 0 is left.</param>
        /// <returns>An event ready to hand to EditorWindow.SendEvent.</returns>
        private static Event MouseEvent(EventType type, Vector2 position, int button)
        {
            return new Event
            {
                type = type,
                mousePosition = position,
                button = button,
                clickCount = 1,
                modifiers = EventModifiers.None,
            };
        }

        /// <summary>Case-insensitive enum parse without depending on the generic Enum.TryParse overload.</summary>
        /// <typeparam name="T">Enum type to parse into.</typeparam>
        /// <param name="text">Text to parse.</param>
        /// <param name="value">Parsed value on success, default otherwise.</param>
        /// <returns>True when the text names a member of the enum.</returns>
        private static bool TryParseEnum<T>(string text, out T value) where T : struct
        {
            foreach (string name in Enum.GetNames(typeof(T)))
            {
                if (string.Equals(name, text, StringComparison.OrdinalIgnoreCase))
                {
                    value = (T)Enum.Parse(typeof(T), name);
                    return true;
                }
            }

            value = default(T);
            return false;
        }

        #endregion
            /// <summary>
        /// Routes the EditorWindow verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchEditorWindow(string verb, string command, string[] parts)
        {
            if (verb == "editor-window-list")
            {
                return EditorWindowListCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "editor-window-overlays")
            {
                return EditorWindowOverlaysCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "editor-window-overlay-set")
            {
                return EditorWindowOverlaySetCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "scene-tool-state")
            {
                return SceneToolStateCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "editor-window-controls")
            {
                return EditorWindowControlsCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "editor-window-click")
            {
                return EditorWindowClickCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "editor-window-close")
            {
                return EditorWindowCloseCommand(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "editor-window-key")
            {
                return EditorWindowKeyCommand(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }
    }
}
