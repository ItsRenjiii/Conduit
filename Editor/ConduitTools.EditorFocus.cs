/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Runtime.InteropServices;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Brings the editor to the operating system foreground and gives the foreground back afterwards. Unity renders
    /// no new frames while it is in the background, so canvases, the Inspector and any capture of a rendered frame
    /// (the Frame Debugger among them) go stale until the editor is in front.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Editor Focus State

        /// <summary>Window that had the foreground when <c>editor-focus front</c> last took it, given back by <c>editor-focus restore</c>.</summary>
        private static IntPtr _editorFocusPrevious;

        #endregion

        #region Editor Focus Commands

        /// <summary>
        /// front records the window holding the foreground and brings the editor in front of it; restore gives the
        /// foreground back to that window. Both report the foreground window before and after, as read back.
        /// </summary>
        /// <param name="args">"front" or "restore".</param>
        /// <returns>What the foreground was and is now, or why nothing changed.</returns>
        private static string EditorFocusCommand(string args)
        {
            string action = (args ?? string.Empty).Trim().ToLowerInvariant();
            if (action != "front" && action != "restore") return "Usage: editor-focus front|restore. Nothing changed.";
            if (!EditorForeground.Supported)
            {
                return "editor-focus is not supported here: bringing a window to the foreground is implemented for the Windows editor only. Nothing changed.";
            }

            IntPtr before = EditorForeground.Current();
            IntPtr editor = EditorForeground.EditorWindow();
            if (action == "front")
            {
                if (before == editor)
                {
                    return "The editor already has the foreground (window " + editor + "). Nothing changed, and nothing new was recorded for restore.";
                }

                _editorFocusPrevious = before;
                EditorForeground.Bring(editor);
                IntPtr after = EditorForeground.Current();
                return "Foreground was window " + before + ", now " + after
                    + (after == editor ? " (the editor)." : ", which is not the editor: the operating system refused the change.")
                    + " Window " + before + " is recorded for editor-focus restore. Unity reports itself active from its next editor update, not within this call.";
            }

            if (_editorFocusPrevious == IntPtr.Zero)
            {
                return "No window was recorded by editor-focus front, so there is nothing to give the foreground back to. Nothing changed.";
            }

            IntPtr target = _editorFocusPrevious;
            EditorForeground.Bring(target);
            IntPtr now = EditorForeground.Current();
            if (now == target) _editorFocusPrevious = IntPtr.Zero;
            return "Foreground was window " + before + ", now " + now
                + (now == target ? ", the window recorded by editor-focus front." : "; the recorded window " + target + " did not take it, and stays recorded (it may have closed).");
        }

        /// <summary>Routes the editor focus verb.</summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchEditorFocus(string verb, string command, string[] parts)
        {
            if (verb == "editor-focus")
            {
                return EditorFocusCommand(GetCommandRemainder(command, parts[0]));
            }

            return null;
        }

        #endregion

        #region Editor Foreground

        /// <summary>
        /// Operating system foreground control for the editor's main window. Windows refuses a plain
        /// SetForegroundWindow from a background process, so the call attaches to the input queue of the thread that
        /// owns the current foreground first, which is the documented way to be allowed the change.
        /// </summary>
        internal static class EditorForeground
        {
#if UNITY_EDITOR_WIN
            /// <summary>ShowWindow command that restores a minimised window.</summary>
            private const int SW_RESTORE = 9;

            [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
            [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
            [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
            [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
            [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);
            [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool doAttach);
            [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

            /// <summary>True: this platform implements foreground control.</summary>
            internal static bool Supported => true;

            /// <summary>The window holding the foreground now.</summary>
            /// <returns>Its handle, or zero when none.</returns>
            internal static IntPtr Current()
            {
                return GetForegroundWindow();
            }

            /// <summary>The editor's main window.</summary>
            /// <returns>Its handle, or zero when the process has none yet.</returns>
            internal static IntPtr EditorWindow()
            {
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                {
                    return process.MainWindowHandle;
                }
            }

            /// <summary>Brings a window to the foreground, restoring it first when minimised.</summary>
            /// <param name="window">Window to bring forward.</param>
            /// <returns>What SetForegroundWindow returned; read <see cref="Current"/> for the real outcome.</returns>
            internal static bool Bring(IntPtr window)
            {
                if (window == IntPtr.Zero) return false;
                if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
                uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
                uint thisThread = GetCurrentThreadId();
                bool attached = foregroundThread != 0 && foregroundThread != thisThread && AttachThreadInput(thisThread, foregroundThread, true);
                BringWindowToTop(window);
                bool result = SetForegroundWindow(window);
                if (attached) AttachThreadInput(thisThread, foregroundThread, false);
                return result;
            }
#else
            /// <summary>False: foreground control is implemented for the Windows editor only.</summary>
            internal static bool Supported => false;

            /// <summary>Always zero where foreground control is not implemented.</summary>
            /// <returns>Zero.</returns>
            internal static IntPtr Current()
            {
                return IntPtr.Zero;
            }

            /// <summary>Always zero where foreground control is not implemented.</summary>
            /// <returns>Zero.</returns>
            internal static IntPtr EditorWindow()
            {
                return IntPtr.Zero;
            }

            /// <summary>Does nothing where foreground control is not implemented.</summary>
            /// <param name="window">Ignored.</param>
            /// <returns>False.</returns>
            internal static bool Bring(IntPtr window)
            {
                return false;
            }
#endif
        }

        #endregion
    }
}
