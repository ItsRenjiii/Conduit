/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Keyboard input for the running game. On Windows a key is posted as a real WM_KEYDOWN and WM_KEYUP to the
    /// game view's own native window, the same message a physical key produces, so UnityEngine.Input reads it -
    /// Input.GetKey, GetKeyDown and the axes built on keys - and it does so whichever application has the
    /// foreground, because a posted message goes to that window and nowhere else. An IMGUI event sent to the
    /// game view, the only route elsewhere, reaches editor GUI code but never UnityEngine.Input. Keys this
    /// verb holds are released when play mode ends and before a domain reload, so none is left stuck down.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Game view key constants

        /// <summary>Window message of a key going down.</summary>
        private const uint WM_KEYDOWN = 0x0100;

        /// <summary>Window message of a key coming up.</summary>
        private const uint WM_KEYUP = 0x0101;

        /// <summary>MapVirtualKey mode turning a virtual key into its scan code.</summary>
        private const uint MAPVK_VK_TO_VSC = 0;

        /// <summary>Win32 class name of the native window behind every docked editor view.</summary>
        private const string GUI_VIEW_WINDOW_CLASS = "UnityGUIViewWndClass";

        /// <summary>Fewest player frames a pressed key stays down, so at least one frame's input sampling sees it.</summary>
        private const int GAME_VIEW_KEY_MIN_FRAMES = 2;

        /// <summary>Longest hold a press accepts, in seconds, so a typo cannot hold a key for hours.</summary>
        private const float GAME_VIEW_KEY_MAX_SECONDS = 600f;

        #endregion

        #region Game view key interop

        /// <summary>Callback EnumWindows and EnumChildWindows call for each window.</summary>
        private delegate bool EnumWindowsCallback(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsCallback callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        /// <summary>A window rectangle in screen pixels, as Win32 returns it.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            /// <summary>Left edge.</summary>
            public int Left;

            /// <summary>Top edge.</summary>
            public int Top;

            /// <summary>Right edge, exclusive.</summary>
            public int Right;

            /// <summary>Bottom edge, exclusive.</summary>
            public int Bottom;
        }

        #endregion

        #region Game view key state

        /// <summary>One key this verb is holding down in the game view.</summary>
        private sealed class HeldGameViewKey
        {
            /// <summary>Key as the caller named it, for reports.</summary>
            public KeyCode Key;

            /// <summary>Native window the key went down in, which its release must go to.</summary>
            public IntPtr Window;

            /// <summary>Virtual key code sent in wParam.</summary>
            public uint VirtualKey;

            /// <summary>lParam of the key-down message; the key-up adds the previous-state and transition bits.</summary>
            public uint KeyData;

            /// <summary>Editor time the key may be released at, or a negative value to hold until released.</summary>
            public double ReleaseTime;

            /// <summary>Player frame the key may be released at.</summary>
            public int ReleaseFrame;
        }

        /// <summary>Keys currently held down by game-view-key.</summary>
        private static readonly List<HeldGameViewKey> _heldGameViewKeys = new List<HeldGameViewKey>();

        /// <summary>Windows found by the current enumeration of this process' windows.</summary>
        private static readonly List<IntPtr> _enumeratedWindows = new List<IntPtr>();

        /// <summary>Process id the window enumeration keeps to.</summary>
        private static uint _enumeratingProcessId;

        /// <summary>Whether the release hooks are subscribed in this domain.</summary>
        private static bool _gameViewKeyHooksInstalled;

        #endregion

        #region Game view key verb

        /// <summary>
        /// Presses, holds or releases a key in the running game view. Arguments after the key may come in any
        /// order: a phase (press, the default, holds for the given seconds and then releases; down holds until
        /// an up; up releases) and a hold length in seconds for a press, where 0 means the shortest press the
        /// game can read.
        /// </summary>
        /// <param name="args">Key name, then optional seconds and phase.</param>
        /// <returns>What was posted and to which window, or why nothing was.</returns>
        private static string PostGameViewKey(string args)
        {
            string[] parts = (args ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1)
            {
                return "Usage: game-view-key <key> [seconds=0] [phase:down|up|press]";
            }

            string phase = "press";
            float seconds = 0f;
            for (int i = 1; i < parts.Length; i++)
            {
                string token = parts[i].Trim().ToLowerInvariant();
                float parsed;
                if (token == "down" || token == "up" || token == "press")
                {
                    phase = token;
                }
                else if (float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed) && parsed >= 0f)
                {
                    seconds = parsed;
                }
                else
                {
                    return "Unrecognised argument '" + parts[i] + "': expected a phase (down, up or press) or a hold length in seconds.";
                }
            }

            if (seconds > GAME_VIEW_KEY_MAX_SECONDS)
            {
                return "A press holds at most " + GAME_VIEW_KEY_MAX_SECONDS.ToString("0", CultureInfo.InvariantCulture) + " s; use down and a later up for anything open-ended.";
            }

            KeyCode key;
            if (!TryParseGameViewKey(parts[0], out key))
            {
                return "Unknown key '" + parts[0] + "'. Use a UnityEngine.KeyCode name such as W, Space, LeftShift, UpArrow, Alpha1 or F5.";
            }

            uint virtualKey;
            uint scanKey;
            bool extended;
            if (!TryGetVirtualKey(key, out virtualKey, out scanKey, out extended))
            {
                return "Key " + key + " cannot be posted to the game view: Alt keys are system keys on Windows, and mouse and joystick codes are not keys. Supported: letters, digits, F1-F15, arrows, the editing keys, Space, Return, Escape, Tab, Backspace, Shift, Control, the keypad and punctuation.";
            }

            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                return "game-view-key posts real key messages only in the Windows editor; on this platform nothing reaches UnityEngine.Input, so no key was sent.";
            }

            if (!EditorApplication.isPlaying)
            {
                return "Play mode is not running, so nothing in the game reads keys; no key was sent.";
            }

            string warning = phase != "press" && seconds > 0f ? Environment.NewLine + "Ignored the " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s hold: only a press takes one." : string.Empty;
            HeldGameViewKey held = FindHeldGameViewKey(key);

            if (phase == "up")
            {
                if (held == null)
                {
                    IntPtr window = FindGameViewWindow();
                    if (window == IntPtr.Zero) return "No open game view has a native window, so no key could be released.";

                    uint data = BuildKeyData(scanKey, extended);
                    PostKeyMessage(window, WM_KEYUP, virtualKey, data | (1u << 30) | (1u << 31));
                    return "Posted a release of " + key + " to the game view window 0x" + window.ToString("X") + "; game-view-key was not holding it." + warning;
                }

                ReleaseHeldGameViewKey(held);
                return "Released " + key + " in the game view." + warning;
            }

            if (held == null)
            {
                IntPtr window = FindGameViewWindow();
                if (window == IntPtr.Zero) return "No open game view has a native window, so no key was sent. Open the Game view and try again.";

                uint data = BuildKeyData(scanKey, extended);
                if (!PostKeyMessage(window, WM_KEYDOWN, virtualKey, data))
                {
                    return "Posting " + key + " to the game view window 0x" + window.ToString("X") + " failed with Win32 error " + Marshal.GetLastWin32Error() + "; no key is held.";
                }

                held = new HeldGameViewKey { Key = key, Window = window, VirtualKey = virtualKey, KeyData = data };
                _heldGameViewKeys.Add(held);
                InstallGameViewKeyHooks();
            }

            if (phase == "down")
            {
                held.ReleaseTime = -1d;
                return "Holding " + key + " down in the game view (window 0x" + held.Window.ToString("X") + ") until game-view-key " + key + " up, or until play mode ends." + warning;
            }

            held.ReleaseTime = EditorApplication.timeSinceStartup + seconds;
            held.ReleaseFrame = Time.frameCount + GAME_VIEW_KEY_MIN_FRAMES;
            return seconds > 0f
                ? "Holding " + key + " in the game view (window 0x" + held.Window.ToString("X") + ") for " + seconds.ToString("0.###", CultureInfo.InvariantCulture) + " s; it releases by itself. The call returns now, before the release."
                : "Pressed " + key + " in the game view (window 0x" + held.Window.ToString("X") + "); it releases after " + GAME_VIEW_KEY_MIN_FRAMES + " frames.";
        }

        #endregion

        #region Game view key helpers

        /// <summary>Parses a key name as a KeyCode, ignoring case, so w and W both name the W key.</summary>
        /// <param name="name">Key name.</param>
        /// <param name="key">The key, when the name is one.</param>
        /// <returns>True when the name is a defined KeyCode.</returns>
        private static bool TryParseGameViewKey(string name, out KeyCode key)
        {
            key = KeyCode.None;
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0 || char.IsDigit(trimmed[0])) return false;    // a bare number is an enum value, not a key name

            return Enum.TryParse(trimmed, true, out key) && Enum.IsDefined(typeof(KeyCode), key) && key != KeyCode.None;
        }

        /// <summary>
        /// Maps a key to the Windows virtual key its message carries, the virtual key its scan code is read
        /// from, and whether it is an extended key. Shift and Control carry the generic code with the left or
        /// right key's scan code, as the keyboard driver sends them.
        /// </summary>
        /// <param name="key">Key to map.</param>
        /// <param name="virtualKey">Virtual key for wParam.</param>
        /// <param name="scanKey">Virtual key to take the scan code from.</param>
        /// <param name="extended">Whether lParam marks it as an extended key.</param>
        /// <returns>False for a key that cannot be posted, such as Alt or a mouse button.</returns>
        private static bool TryGetVirtualKey(KeyCode key, out uint virtualKey, out uint scanKey, out bool extended)
        {
            extended = false;
            virtualKey = 0;
            int code = (int)key;

            if (key >= KeyCode.A && key <= KeyCode.Z) virtualKey = (uint)('A' + (code - (int)KeyCode.A));
            else if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) virtualKey = (uint)('0' + (code - (int)KeyCode.Alpha0));
            else if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) virtualKey = (uint)(0x60 + (code - (int)KeyCode.Keypad0));
            else if (key >= KeyCode.F1 && key <= KeyCode.F15) virtualKey = (uint)(0x70 + (code - (int)KeyCode.F1));
            else
            {
                switch (key)
                {
                    case KeyCode.Space: virtualKey = 0x20; break;
                    case KeyCode.Return: virtualKey = 0x0D; break;
                    case KeyCode.KeypadEnter: virtualKey = 0x0D; extended = true; break;
                    case KeyCode.Escape: virtualKey = 0x1B; break;
                    case KeyCode.Tab: virtualKey = 0x09; break;
                    case KeyCode.Backspace: virtualKey = 0x08; break;
                    case KeyCode.Delete: virtualKey = 0x2E; extended = true; break;
                    case KeyCode.Insert: virtualKey = 0x2D; extended = true; break;
                    case KeyCode.Home: virtualKey = 0x24; extended = true; break;
                    case KeyCode.End: virtualKey = 0x23; extended = true; break;
                    case KeyCode.PageUp: virtualKey = 0x21; extended = true; break;
                    case KeyCode.PageDown: virtualKey = 0x22; extended = true; break;
                    case KeyCode.UpArrow: virtualKey = 0x26; extended = true; break;
                    case KeyCode.DownArrow: virtualKey = 0x28; extended = true; break;
                    case KeyCode.LeftArrow: virtualKey = 0x25; extended = true; break;
                    case KeyCode.RightArrow: virtualKey = 0x27; extended = true; break;
                    case KeyCode.LeftShift: virtualKey = 0x10; scanKey = 0xA0; return true;
                    case KeyCode.RightShift: virtualKey = 0x10; scanKey = 0xA1; return true;
                    case KeyCode.LeftControl: virtualKey = 0x11; scanKey = 0xA2; return true;
                    case KeyCode.RightControl: virtualKey = 0x11; scanKey = 0xA3; extended = true; return true;
                    case KeyCode.Comma: virtualKey = 0xBC; break;
                    case KeyCode.Period: virtualKey = 0xBE; break;
                    case KeyCode.Minus: virtualKey = 0xBD; break;
                    case KeyCode.Equals: virtualKey = 0xBB; break;
                    case KeyCode.Slash: virtualKey = 0xBF; break;
                    case KeyCode.Semicolon: virtualKey = 0xBA; break;
                    case KeyCode.Quote: virtualKey = 0xDE; break;
                    case KeyCode.LeftBracket: virtualKey = 0xDB; break;
                    case KeyCode.RightBracket: virtualKey = 0xDD; break;
                    case KeyCode.Backslash: virtualKey = 0xDC; break;
                    case KeyCode.BackQuote: virtualKey = 0xC0; break;
                    case KeyCode.KeypadPlus: virtualKey = 0x6B; break;
                    case KeyCode.KeypadMinus: virtualKey = 0x6D; break;
                    case KeyCode.KeypadMultiply: virtualKey = 0x6A; break;
                    case KeyCode.KeypadDivide: virtualKey = 0x6F; extended = true; break;
                    case KeyCode.KeypadPeriod: virtualKey = 0x6E; break;
                }
            }

            scanKey = virtualKey;
            return virtualKey != 0;
        }

        /// <summary>Builds the lParam of a key-down: a repeat count of one, the scan code and the extended-key bit.</summary>
        /// <param name="scanKey">Virtual key to read the scan code from.</param>
        /// <param name="extended">Whether the key is an extended key.</param>
        /// <returns>The key-down lParam.</returns>
        private static uint BuildKeyData(uint scanKey, bool extended)
        {
            uint scanCode = MapVirtualKey(scanKey, MAPVK_VK_TO_VSC) & 0xFF;
            return 1u | (scanCode << 16) | (extended ? 1u << 24 : 0u);
        }

        /// <summary>Posts one key message to a window.</summary>
        /// <param name="window">Window to post to.</param>
        /// <param name="message">WM_KEYDOWN or WM_KEYUP.</param>
        /// <param name="virtualKey">Virtual key for wParam.</param>
        /// <param name="keyData">lParam bits.</param>
        /// <returns>True when Windows accepted the message.</returns>
        private static bool PostKeyMessage(IntPtr window, uint message, uint virtualKey, uint keyData)
        {
            return PostMessage(window, message, (IntPtr)virtualKey, (IntPtr)unchecked((int)keyData));
        }

        /// <summary>Finds the key this verb is holding, if it holds it.</summary>
        /// <param name="key">Key to look for.</param>
        /// <returns>The held key, or null.</returns>
        private static HeldGameViewKey FindHeldGameViewKey(KeyCode key)
        {
            for (int i = 0; i < _heldGameViewKeys.Count; i++)
            {
                if (_heldGameViewKeys[i].Key == key) return _heldGameViewKeys[i];
            }

            return null;
        }

        /// <summary>Posts a held key's release to the window it went down in and forgets it.</summary>
        /// <param name="held">Key to release.</param>
        private static void ReleaseHeldGameViewKey(HeldGameViewKey held)
        {
            PostKeyMessage(held.Window, WM_KEYUP, held.VirtualKey, held.KeyData | (1u << 30) | (1u << 31));
            _heldGameViewKeys.Remove(held);
            if (_heldGameViewKeys.Count == 0) RemoveGameViewKeyHooks();
        }

        /// <summary>Releases every key this verb holds.</summary>
        private static void ReleaseAllGameViewKeys()
        {
            while (_heldGameViewKeys.Count > 0) ReleaseHeldGameViewKey(_heldGameViewKeys[_heldGameViewKeys.Count - 1]);
        }

        /// <summary>Subscribes the timed release and the release on leaving play mode and before a reload.</summary>
        private static void InstallGameViewKeyHooks()
        {
            if (_gameViewKeyHooksInstalled) return;

            _gameViewKeyHooksInstalled = true;
            EditorApplication.update += TickHeldGameViewKeys;
            EditorApplication.playModeStateChanged += ReleaseGameViewKeysOnPlayModeChange;
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseAllGameViewKeys;
        }

        /// <summary>Unsubscribes the release hooks once nothing is held.</summary>
        private static void RemoveGameViewKeyHooks()
        {
            if (!_gameViewKeyHooksInstalled) return;

            _gameViewKeyHooksInstalled = false;
            EditorApplication.update -= TickHeldGameViewKeys;
            EditorApplication.playModeStateChanged -= ReleaseGameViewKeysOnPlayModeChange;
            AssemblyReloadEvents.beforeAssemblyReload -= ReleaseAllGameViewKeys;
        }

        /// <summary>Releases every held key as play mode is left, so none carries into the next session.</summary>
        /// <param name="change">The play mode change.</param>
        private static void ReleaseGameViewKeysOnPlayModeChange(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode) ReleaseAllGameViewKeys();
        }

        /// <summary>Releases each pressed key once its hold time and its fewest frames have both passed.</summary>
        private static void TickHeldGameViewKeys()
        {
            double now = EditorApplication.timeSinceStartup;
            int frame = Time.frameCount;
            for (int i = _heldGameViewKeys.Count - 1; i >= 0; i--)
            {
                HeldGameViewKey held = _heldGameViewKeys[i];
                if (held.ReleaseTime < 0d || now < held.ReleaseTime || frame < held.ReleaseFrame) continue;

                ReleaseHeldGameViewKey(held);
            }
        }

        /// <summary>
        /// Finds the native window of the open game view: the smallest visible editor view window of this
        /// process whose rectangle holds the centre of the game view, converted from points to pixels.
        /// </summary>
        /// <returns>The window, or zero when no game view is open or none matches.</returns>
        private static IntPtr FindGameViewWindow()
        {
            Type gameViewType = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
            if (gameViewType == null) return IntPtr.Zero;

            UnityEngine.Object[] views = Resources.FindObjectsOfTypeAll(gameViewType);
            if (views.Length == 0) return IntPtr.Zero;

            float pixelsPerPoint = EditorGUIUtility.pixelsPerPoint;
            Rect position = ((EditorWindow)views[0]).position;
            int centreX = Mathf.RoundToInt((position.x + position.width / 2f) * pixelsPerPoint);
            int centreY = Mathf.RoundToInt((position.y + position.height / 2f) * pixelsPerPoint);

            _enumeratedWindows.Clear();
            _enumeratingProcessId = (uint)Process.GetCurrentProcess().Id;
            EnumWindows(CollectProcessWindow, IntPtr.Zero);

            IntPtr best = IntPtr.Zero;
            long bestArea = long.MaxValue;
            StringBuilder className = new StringBuilder(64);
            foreach (IntPtr window in _enumeratedWindows)
            {
                className.Length = 0;
                GetClassName(window, className, className.Capacity);
                if (className.ToString() != GUI_VIEW_WINDOW_CLASS || !IsWindowVisible(window)) continue;

                NativeRect rect;
                if (!GetWindowRect(window, out rect)) continue;
                if (centreX < rect.Left || centreX >= rect.Right || centreY < rect.Top || centreY >= rect.Bottom) continue;

                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area < bestArea)
                {
                    bestArea = area;
                    best = window;
                }
            }

            _enumeratedWindows.Clear();
            return best;
        }

        /// <summary>Collects a top-level window of this process and its child windows.</summary>
        /// <param name="window">Window being enumerated.</param>
        /// <param name="lParam">Unused.</param>
        /// <returns>True to keep enumerating.</returns>
        private static bool CollectProcessWindow(IntPtr window, IntPtr lParam)
        {
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            if (processId != _enumeratingProcessId) return true;

            _enumeratedWindows.Add(window);
            EnumChildWindows(window, CollectChildWindow, IntPtr.Zero);
            return true;
        }

        /// <summary>Collects a child window of one of this process' windows.</summary>
        /// <param name="window">Window being enumerated.</param>
        /// <param name="lParam">Unused.</param>
        /// <returns>True to keep enumerating.</returns>
        private static bool CollectChildWindow(IntPtr window, IntPtr lParam)
        {
            _enumeratedWindows.Add(window);
            return true;
        }

        #endregion
    }
}
