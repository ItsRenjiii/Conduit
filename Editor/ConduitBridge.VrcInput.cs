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
    /// ClientSim driving: player input, stance, movement, look and aim verbs.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Driver state

        /// <summary>Last editor time at which the ClientSim drive helper updated movement.</summary>
        private static double _lastVrcDriverTickTime;

        /// <summary>Editor time at which the active ClientSim drive command should stop.</summary>
        private static double _vrcDriverEndTime;

        /// <summary>Active ClientSim forward movement axis.</summary>
        private static float _vrcDriverForward;

        /// <summary>Active ClientSim strafe movement axis.</summary>
        private static float _vrcDriverStrafe;

        /// <summary>Active ClientSim turn rate in degrees per second.</summary>
        private static float _vrcDriverTurnDegreesPerSecond;

        /// <summary>Explicit ClientSim movement speed requested by the active drive command.</summary>
        private static float _vrcDriverSpeedMetersPerSecond;

        /// <summary>True while the ClientSim run input is held (set by <see cref="SendVrcInput"/>); lets a default-speed drive switch to run speed live.</summary>
        private static bool _vrcRunHeld;

        /// <summary>True when the active drive was given an explicit speed argument, so the run modifier must not override it.</summary>
        private static bool _vrcDriverExplicitSpeed;

        /// <summary>Game frame at which the deferred ClientSim Use release from a "use press" fires, or -1 when none is pending.</summary>
        private static int _vrcUseReleaseFrame = -1;

        #endregion

        #region Input, stance and menu
        /// <summary>Opens, closes, accepts, or reports the ClientSim menu state.</summary>
        /// <param name="args">Menu action: status, close, open, or accept-warning.</param>
        /// <returns>A confirmation line with menu state, or a validation error.</returns>
        private static string ControlVrcMenu(string args)
        {
            var action = (args ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
            if (string.IsNullOrEmpty(action))
            {
                action = "status";
            }

            var menu = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimMenu");
            if (menu == null)
            {
                return "ClientSimMenu was not found. Enter Play Mode with ClientSim enabled first.";
            }

            if (action == "status")
            {
                return FormatVrcMenuState(menu);
            }

            var before = FormatVrcMenuState(menu);

            if (action == "close")
            {
                var page = InvokeInstanceMethod(menu, "GetDisplayedPage", null);
                if (page != null && page.ToString() == "WARNING_PAGE")
                {
                    InvokeInstanceMethod(menu, "WarningAccepted", null);
                }
                else
                {
                    InvokeInstanceMethod(menu, "CloseMenu", null);
                }

                return VrcMenuChangeReport("Requested ClientSim menu close.", before, menu);
            }

            if (action == "open")
            {
                InvokeInstanceMethod(menu, "OpenMenu", null);
                return VrcMenuChangeReport("Requested ClientSim menu open.", before, menu);
            }

            if (action == "accept-warning" || action == "accept")
            {
                InvokeInstanceMethod(menu, "WarningAccepted", null);
                return VrcMenuChangeReport("Accepted ClientSim warning and requested menu close.", before, menu);
            }

            return "Usage: vrc-menu <status|close|open|accept-warning>";
        }

        /// <summary>Sends a supported ClientSim input action in down, up, or press mode.</summary>
        /// <param name="args">Input action plus optional down, up, or press mode.</param>
        /// <returns>A confirmation line with player state, or a validation error.</returns>
        private static string SendVrcInput(string args)
        {
            var parts = (args ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1)
            {
                return "Usage: vrc-input <jump|run|use|grab|drop|crouch|prone|menu|release-mouse> [down|up|press]";
            }

            var input = GetVrcInput();
            if (input == null)
            {
                return "ClientSim input was not found. Enter Play Mode with ClientSim enabled first.";
            }

            var action = parts[0].Trim().ToLowerInvariant();
            var mode = parts.Length > 1 ? parts[1].Trim().ToLowerInvariant() : "press";
            if (mode != "down" && mode != "up" && mode != "press")
            {
                return "Input mode must be down, up, or press.";
            }

            var methodName = VrcInputMethodName(action);
            if (methodName == null)
            {
                return "Unknown VRC input action. Use jump, run, use, grab, drop, crouch, prone, menu, or release-mouse.";
            }

            var before = FormatVrcPlayerState();

            // Canvas-button clicks are processed by StandaloneInputModule.Process, which returns without
            // doing ANYTHING while the editor is not the foreground OS application (StandaloneInputModule
            // Process: "!eventSystem.isFocused && ShouldIgnoreEventsOnNoFocus()", and in the editor the
            // ignore flag is effectively always true). The bridge's normal operating condition is exactly
            // that: the user reads the agent's window while the agent drives Unity. So force the focus
            // flag around every synthetic Use; with ClientSim's BaseInput override the EventSystem's
            // "mouse" is ClientSim state rather than hardware, so this cannot let stray real input in.
            if (action == "use")
            {
                EnsureVrcEventSystemFocused();
            }

            // A Use press must release on a LATER rendered frame: ClientSimBaseInput edge-detects Use
            // against a per-frame tick, so a down and up delivered inside the same editor tick leave no
            // edge for the EventSystem to see, and the click would reach Interacts but never a Canvas
            // Button. Proven in the aiming lab: down, two rendered frames, up clicks a world-space Button.
            if (action == "use" && mode == "press")
            {
                if (_vrcUseReleaseFrame >= 0)
                {
                    InvokeVrcInputMethod(input, methodName, action, false);
                    _vrcUseReleaseFrame = -1;
                }

                InvokeVrcInputMethod(input, methodName, action, true);
                _vrcUseReleaseFrame = Time.frameCount + 2;
                return VrcStateChangeReport("Sent ClientSim input use (down now, release in 2 frames so the UI click edge lands).", before);
            }

            if (mode == "down" || mode == "press")
            {
                InvokeVrcInputMethod(input, methodName, action, true);
            }

            if (mode == "up" || mode == "press")
            {
                InvokeVrcInputMethod(input, methodName, action, false);
            }

            // An explicit use down or up takes over the hold, so a scheduled press release must not fire
            // later and cut the hold short or double-release.
            if (action == "use")
            {
                _vrcUseReleaseFrame = -1;
            }

            // Remember a held run so an in-progress default-speed drive can switch to run speed.
            // A "press" is momentary (down then up), so it ends released.
            if (action == "run")
            {
                _vrcRunHeld = mode == "down";
            }

            return VrcStateChangeReport("Sent ClientSim input " + action + " (" + mode + ").", before);
        }

        /// <summary>Sets the ClientSim desktop tracking stance to standing, crouching, or prone.</summary>
        /// <param name="args">Requested stance name or supported alias.</param>
        /// <returns>A confirmation line with player state, or a validation error.</returns>
        private static string SetVrcStance(string args)
        {
            var stanceName = (args ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
            if (stanceName == "stand" || stanceName == "standing")
            {
                stanceName = "STANDING";
            }
            else if (stanceName == "crouch" || stanceName == "crouching")
            {
                stanceName = "CROUCHING";
            }
            else if (stanceName == "prone" || stanceName == "lay" || stanceName == "laying" || stanceName == "down")
            {
                stanceName = "PRONE";
            }
            else
            {
                return "Usage: vrc-stance <stand|crouch|prone>";
            }

            var tracking = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimDesktopTrackingProvider");
            if (tracking == null)
            {
                return "ClientSimDesktopTrackingProvider was not found. Enter Play Mode with ClientSim enabled first.";
            }

            var stanceType = FindLoadedType("VRC.SDK3.ClientSim.ClientSimPlayerStanceEnum");
            if (stanceType == null)
            {
                return "ClientSimPlayerStanceEnum type was not found.";
            }

            var stance = Enum.Parse(stanceType, stanceName);
            var before = FormatVrcPlayerState();
            InvokeInstanceMethod(tracking, "SetStance", new[] { stance });
            return VrcStateChangeReport("Set ClientSim stance to " + stanceName + ".", before);
        }

        /// <summary>Reads the ClientSim input object from the loaded ClientSim input manager.</summary>
        /// <returns>ClientSim input object, or null when unavailable.</returns>
        private static object GetVrcInput()
        {
            var inputManager = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimInputManager");
            return inputManager == null ? null : InvokeInstanceMethod(inputManager, "GetInput", null);
        }

        /// <summary>Maps a supported input action alias to the ClientSim input method name.</summary>
        /// <param name="action">Lowercase input action or alias.</param>
        /// <returns>ClientSim method name, or null for unknown actions.</returns>
        private static string VrcInputMethodName(string action)
        {
            if (action == "jump")
            {
                return "SendJumpEvent";
            }

            if (action == "run")
            {
                return "SendRunEvent";
            }

            if (action == "use")
            {
                return "SendUseEvent";
            }

            if (action == "grab")
            {
                return "SendGrabEvent";
            }

            if (action == "drop")
            {
                return "SendDropEvent";
            }

            if (action == "crouch" || action == "toggle-crouch")
            {
                return "SendToggleCrouchEvent";
            }

            if (action == "prone" || action == "lay" || action == "toggle-prone")
            {
                return "SendToggleProneEvent";
            }

            if (action == "menu" || action == "toggle-menu")
            {
                return "SendToggleMenuEvent";
            }

            if (action == "release-mouse" || action == "mouse")
            {
                return "SendReleaseMouseEvent";
            }

            return null;
        }

        /// <summary>Invokes the reflected ClientSim input method with the argument shape required by that action.</summary>
        /// <param name="input">ClientSim input object.</param>
        /// <param name="methodName">Method name to invoke.</param>
        /// <param name="action">Input action being sent.</param>
        /// <param name="value">Pressed/released state to pass to ClientSim.</param>
        private static void InvokeVrcInputMethod(object input, string methodName, string action, bool value)
        {
            if (action == "jump" || action == "use" || action == "grab" || action == "drop" ||
                action == "menu" || action == "toggle-menu")
            {
                var handType = FindLoadedType("VRC.Udon.Common.HandType");
                var rightHand = handType == null ? null : Enum.Parse(handType, "RIGHT");
                InvokeInstanceMethod(input, methodName, new[] { (object)value, rightHand });
                return;
            }

            InvokeInstanceMethod(input, methodName, new[] { (object)value });
        }

        /// <summary>Sends the deferred Use release scheduled by a "use press" once its game frame arrives.</summary>
        private static void TickVrcUseRelease()
        {
            if (_vrcUseReleaseFrame < 0)
            {
                return;
            }

            if (!EditorApplication.isPlaying)
            {
                _vrcUseReleaseFrame = -1;
                return;
            }

            if (Time.frameCount < _vrcUseReleaseFrame)
            {
                return;
            }

            var input = GetVrcInput();
            _vrcUseReleaseFrame = -1;
            if (input != null)
            {
                // The release completes a UI click, so it needs the same focus forcing as the down: an
                // OS focus change between the two would otherwise re-gate the EventSystem mid-press.
                EnsureVrcEventSystemFocused();
                InvokeVrcInputMethod(input, "SendUseEvent", "use", false);
            }
        }

        /// <summary>
        /// Forces the live EventSystem's private focus flag true so StandaloneInputModule processes
        /// pointer events while the editor is NOT the foreground OS application. Unity gates all UI
        /// pointer processing on application focus, which would make bridge-driven Use presses reach
        /// Interacts but silently never click a Canvas button whenever the user has another window in
        /// front - the bridge's normal operating condition. The flag is re-cleared by Unity on the next
        /// real focus change, so it is forced again at each send.
        /// </summary>
        private static void EnsureVrcEventSystemFocused()
        {
            var eventSystem = UnityEngine.EventSystems.EventSystem.current;
            if (eventSystem == null)
            {
                return;
            }

            var focusField = typeof(UnityEngine.EventSystems.EventSystem).GetField(
                "m_HasFocus", BindingFlags.Instance | BindingFlags.NonPublic);
            if (focusField != null)
            {
                focusField.SetValue(eventSystem, true);
            }
        }

        /// <summary>Formats ClientSim menu path, active flag, and displayed page.</summary>
        /// <param name="menu">ClientSim menu component to inspect.</param>
        /// <returns>Text report for the menu component.</returns>
        private static string FormatVrcMenuState(Component menu)
        {
            var builder = new StringBuilder();
            builder.AppendLine("menuPath: " + GetGameObjectPath(menu.gameObject));

            var activeField = menu.GetType().GetField("_menuIsActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (activeField != null)
            {
                builder.AppendLine("menuActive: " + Convert.ToString(activeField.GetValue(menu)));
            }

            var page = InvokeInstanceMethod(menu, "GetDisplayedPage", null);
            builder.AppendLine("displayedPage: " + (page == null ? "<unknown>" : page.ToString()));
            return builder.ToString().TrimEnd();
        }

        #endregion

        #region Movement

        /// <summary>Starts the editor update-loop helper that moves and turns the ClientSim player.</summary>
        /// <param name="args">Forward, strafe, turn-rate, optional seconds, and optional explicit speed.</param>
        /// <returns>A confirmation line with player state, or a validation error.</returns>
        private static string StartVrcDrive(string args)
        {
            var values = ParseFloatList(args);
            if (values.Length < 3 || values.Length > 5)
            {
                return "Usage: vrc-drive <forward> <strafe> <turnDegPerSec> [seconds] [speed]";
            }

            if (!EditorApplication.isPlaying)
            {
                return "Unity is not in Play Mode." + Environment.NewLine + FormatEditorStatus();
            }

            var controller = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimPlayerController");
            if (controller == null)
            {
                return "ClientSimPlayerController was not found. Enter Play Mode with ClientSim enabled first.";
            }

            var before = FormatVrcPlayerState();
            _vrcDriverForward = Mathf.Clamp(values[0], -1f, 1f);
            _vrcDriverStrafe = Mathf.Clamp(values[1], -1f, 1f);
            _vrcDriverTurnDegreesPerSecond = values[2];

            // An explicit speed argument wins outright; otherwise track the player's own walk/run
            // speed, honouring a held run input (recomputed each tick in TickVrcDriver).
            _vrcDriverExplicitSpeed = values.Length > 4;
            _vrcDriverSpeedMetersPerSecond = _vrcDriverExplicitSpeed
                ? Mathf.Max(0f, values[4])
                : GetVrcEffectiveMoveSpeed(controller);

            var seconds = values.Length > 3 ? values[3] : 1f;
            _vrcDriverEndTime = seconds <= 0f ? double.PositiveInfinity : EditorApplication.timeSinceStartup + seconds;
            _lastVrcDriverTickTime = EditorApplication.timeSinceStartup;

            return VrcStateChangeReport("Started VRC driver.", before);
        }

        /// <summary>Stops the ClientSim drive helper and clears its movement state.</summary>
        /// <returns>A confirmation line with player state.</returns>
        private static string StopVrcDrive()
        {
            var before = FormatVrcPlayerState();
            _vrcDriverForward = 0f;
            _vrcDriverStrafe = 0f;
            _vrcDriverTurnDegreesPerSecond = 0f;
            _vrcDriverSpeedMetersPerSecond = 0f;
            _vrcDriverExplicitSpeed = false;
            _vrcDriverEndTime = 0d;
            _lastVrcDriverTickTime = 0d;
            return VrcStateChangeReport("Stopped VRC driver.", before);
        }

        /// <summary>Teleports the ClientSim player to a world position with an optional yaw.</summary>
        /// <param name="args">World x/y/z position and optional yaw in degrees.</param>
        /// <returns>A confirmation line with player state, or a validation error.</returns>
        private static string TeleportVrcPlayer(string args)
        {
            var values = ParseFloatList(args);
            if (values.Length < 3 || values.Length > 4)
            {
                return "Usage: vrc-teleport <x> <y> <z> [yawDegrees]";
            }

            var controller = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimPlayerController");
            if (controller == null)
            {
                return "ClientSimPlayerController was not found. Enter Play Mode with ClientSim enabled first.";
            }

            var before = FormatVrcPlayerState();
            var position = new Vector3(values[0], values[1], values[2]);
            var yaw = values.Length > 3 ? values[3] : controller.transform.eulerAngles.y;
            var rotation = Quaternion.Euler(0f, yaw, 0f);
            var method = controller.GetType().GetMethod(
                "Teleport",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(Vector3), typeof(Quaternion), typeof(bool) },
                null);

            if (method != null)
            {
                method.Invoke(controller, new object[] { position, rotation, false });
            }
            else
            {
                controller.transform.SetPositionAndRotation(position, rotation);
                Physics.SyncTransforms();
            }

            return VrcStateChangeReport("Teleported VRC player.", before);
        }

        /// <summary>Moves and turns the ClientSim player while the drive helper is active.</summary>
        private static void TickVrcDriver()
        {
            if (!IsVrcDriverActive())
            {
                return;
            }

            if (!EditorApplication.isPlaying || EditorApplication.timeSinceStartup >= _vrcDriverEndTime)
            {
                StopVrcDriverState();
                return;
            }

            var now = EditorApplication.timeSinceStartup;
            var deltaTime = _lastVrcDriverTickTime > 0d ? (float)(now - _lastVrcDriverTickTime) : Time.deltaTime;
            _lastVrcDriverTickTime = now;
            if (deltaTime <= 0f || deltaTime > 0.2f)
            {
                deltaTime = 0.02f;
            }

            var controller = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimPlayerController");
            if (controller == null)
            {
                StopVrcDriverState();
                return;
            }

            // When no explicit speed was requested, follow the player's live walk/run speed so a
            // run toggled mid-drive takes effect immediately.
            if (!_vrcDriverExplicitSpeed)
            {
                _vrcDriverSpeedMetersPerSecond = GetVrcEffectiveMoveSpeed(controller);
            }

            if (!Mathf.Approximately(_vrcDriverTurnDegreesPerSecond, 0f))
            {
                controller.transform.rotation *= Quaternion.Euler(0f, _vrcDriverTurnDegreesPerSecond * deltaTime, 0f);
            }

            var input = new Vector3(_vrcDriverStrafe, 0f, _vrcDriverForward);
            input = Vector3.ClampMagnitude(input, 1f);
            if (input.sqrMagnitude > 0f && _vrcDriverSpeedMetersPerSecond > 0f)
            {
                var worldMove = ((controller.transform.right * input.x) + (controller.transform.forward * input.z)) *
                    _vrcDriverSpeedMetersPerSecond * deltaTime;
                var characterController = controller.GetComponent<CharacterController>();
                if (characterController != null && characterController.enabled)
                {
                    characterController.Move(worldMove);
                }
                else
                {
                    controller.transform.position += worldMove;
                }
            }

            Physics.SyncTransforms();
            InvokeInstanceMethod(controller, "NotifyPlayerMoved", null);
        }

        /// <summary>Reports whether the ClientSim drive helper currently has movement or turning work.</summary>
        /// <returns>True when the drive helper has an active end time and non-zero input.</returns>
        private static bool IsVrcDriverActive()
        {
            return _vrcDriverEndTime > 0d &&
                (!Mathf.Approximately(_vrcDriverForward, 0f) ||
                 !Mathf.Approximately(_vrcDriverStrafe, 0f) ||
                 !Mathf.Approximately(_vrcDriverTurnDegreesPerSecond, 0f));
        }

        /// <summary>Clears all ClientSim drive-helper movement, speed, and timing fields.</summary>
        private static void StopVrcDriverState()
        {
            _vrcDriverForward = 0f;
            _vrcDriverStrafe = 0f;
            _vrcDriverTurnDegreesPerSecond = 0f;
            _vrcDriverSpeedMetersPerSecond = 0f;
            _vrcDriverExplicitSpeed = false;
            _vrcDriverEndTime = 0d;
            _lastVrcDriverTickTime = 0d;
        }

        /// <summary>
        /// The drive speed the player would currently move at: run speed while the run input is held,
        /// otherwise walk speed. Used for default-speed drives so the run modifier behaves as in-game.
        /// </summary>
        /// <param name="controller">The ClientSim player controller to read locomotion speeds from.</param>
        /// <returns>Metres per second, never below a small floor so the player always inches forward.</returns>
        private static float GetVrcEffectiveMoveSpeed(Component controller)
        {
            var baseSpeed = _vrcRunHeld
                ? GetVrcLocomotionSpeed(controller, "GetRunSpeed", GetVrcLocomotionSpeed(controller, "GetWalkSpeed", 2f))
                : GetVrcLocomotionSpeed(controller, "GetWalkSpeed", 2f);
            return Mathf.Max(0.1f, baseSpeed);
        }

        /// <summary>
        /// Reads a named speed getter (e.g. "GetWalkSpeed" / "GetRunSpeed") off the controller's
        /// locomotion data via reflection, falling back when the type or method is unavailable.
        /// </summary>
        /// <param name="controller">The ClientSim player controller holding the locomotion data.</param>
        /// <param name="getterName">The parameterless speed getter to invoke on the locomotion data.</param>
        /// <param name="fallback">Value returned if anything in the reflection chain is missing.</param>
        /// <returns>The reported speed in metres per second, or <paramref name="fallback"/>.</returns>
        private static float GetVrcLocomotionSpeed(Component controller, string getterName, float fallback)
        {
            var field = controller.GetType().GetField("_playerLocomotionData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
            {
                return fallback;
            }

            var locomotion = field.GetValue(controller);
            if (locomotion == null)
            {
                return fallback;
            }

            var speed = InvokeInstanceMethod(locomotion, getterName, null);
            if (speed == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToSingle(speed, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        #endregion

        #region Look and aim

        /// <summary>Sets the ClientSim player's absolute look direction: yaw around world up, pitch positive up.</summary>
        /// <param name="args">Yaw and pitch in degrees.</param>
        /// <returns>A report with requested versus achieved angles, or a validation error.</returns>
        private static string SetVrcLook(string args)
        {
            var values = ParseFloatList(args);
            if (values.Length != 2)
            {
                return "Usage: vrc-look <yawDegrees> <pitchDegrees>";
            }

            return ApplyVrcLook(values[0], values[1]);
        }

        /// <summary>Applies a relative change to the ClientSim player's current look direction.</summary>
        /// <param name="args">Yaw and pitch deltas in degrees, positive right and up.</param>
        /// <returns>A report with requested versus achieved angles, or a validation error.</returns>
        private static string SetVrcLookDelta(string args)
        {
            var values = ParseFloatList(args);
            if (values.Length != 2)
            {
                return "Usage: vrc-look-delta <deltaYawDegrees> <deltaPitchDegrees>";
            }

            var camera = GetVrcPlayerCamera();
            if (camera == null)
            {
                return "ClientSim player camera was not found. Enter Play Mode with ClientSim enabled first.";
            }

            var current = VrcYawPitchFromForward(camera.transform.forward);
            return ApplyVrcLook(current.x + values[0], current.y + values[1]);
        }

        /// <summary>Aims the ClientSim player at a world point or at a resolved scene object.</summary>
        /// <param name="args">Either three floats forming a world point, or an object name, path, or id.</param>
        /// <returns>A report with the aim target, anchor, and residual ray error, or a lookup error.</returns>
        private static string SetVrcLookAt(string args)
        {
            var trimmed = (args ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                return "Usage: vrc-look-at <x y z | objectPathOrId>";
            }

            Vector3 point;
            string targetLabel;
            var values = ParseFloatList(trimmed);
            if (values.Length == 3)
            {
                point = new Vector3(values[0], values[1], values[2]);
                targetLabel = "point " + FormatVector3(point);
            }
            else
            {
                GameObject target;
                var error = TryFindGameObject(trimmed, out target);
                if (error != null)
                {
                    return error;
                }

                string anchorKind;
                point = GetVrcAimAnchor(target, out anchorKind);
                targetLabel = GetGameObjectPath(target) + " (" + anchorKind + ") at " + FormatVector3(point);
            }

            return AimVrcAtPoint(point, targetLabel);
        }

        /// <summary>
        /// Aims at a world point through ClientSim's own test API: yaw on the player root via the
        /// controller's LookTowardsPoint, pitch on the camera node via the tracking provider's. Both
        /// update the desktop rotator's cached targets, so the aim sticks instead of being overwritten
        /// by the rotator's per-frame reapply, and both bypass the released-mouse gate.
        /// </summary>
        /// <param name="point">World point to look at.</param>
        /// <param name="targetLabel">Human-readable description of the aim target for the report.</param>
        /// <returns>A report with the residual ray error and refreshed player state.</returns>
        private static string AimVrcAtPoint(Vector3 point, string targetLabel)
        {
            var controller = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimPlayerController");
            var tracking = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimDesktopTrackingProvider");
            var camera = GetVrcPlayerCamera();
            if (controller == null || tracking == null || camera == null)
            {
                return "ClientSim player was not found. Enter Play Mode with ClientSim enabled first.";
            }

            var before = FormatVrcPlayerState();

            // Yaw first so the provider computes its pitch delta in the post-yaw frame.
            InvokeInstanceMethod(controller, "LookTowardsPoint", new object[] { point });
            InvokeInstanceMethod(tracking, "LookTowardsPoint", new object[] { point });
            Physics.SyncTransforms();
            InvokeInstanceMethod(controller, "NotifyPlayerMoved", null);
            RefreshVrcHover();

            float rayError = Vector3.Angle(camera.transform.forward, point - camera.transform.position);
            var builder = new StringBuilder();
            builder.AppendLine("Aimed at " + targetLabel + ": ray error " +
                rayError.ToString("0.###", CultureInfo.InvariantCulture) + " deg.");
            if (rayError > 0.5f)
            {
                builder.AppendLine("Ray error is large: the pitch clamp engaged (limits 70 down, 80 up), " +
                    "the player is locked in a station, or the target is very close to the head.");
            }

            builder.AppendLine();
            builder.AppendLine("Before:");
            builder.AppendLine(before);
            builder.AppendLine();
            builder.AppendLine("After:");
            builder.Append(FormatVrcPlayerState());
            return builder.ToString().TrimEnd();
        }

        /// <summary>Sets absolute yaw and pitch, reading back the achieved angles for a truthful report.</summary>
        /// <param name="yawDegrees">Absolute yaw in degrees around world up.</param>
        /// <param name="pitchDegrees">Absolute pitch in degrees, positive up; ClientSim clamps to 70 down, 80 up.</param>
        /// <returns>A report with requested versus achieved angles and refreshed player state.</returns>
        private static string ApplyVrcLook(float yawDegrees, float pitchDegrees)
        {
            var controller = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimPlayerController");
            var tracking = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimDesktopTrackingProvider");
            var camera = GetVrcPlayerCamera();
            if (controller == null || tracking == null || camera == null)
            {
                return "ClientSim player was not found. Enter Play Mode with ClientSim enabled first.";
            }

            var before = FormatVrcPlayerState();

            // Yaw lives on the player root: the same direct write the drive helper and teleport already use.
            controller.transform.rotation = Quaternion.Euler(0f, yawDegrees, 0f);
            Physics.SyncTransforms();

            // Pitch goes through the tracking provider so the rotator's cached target updates; the far
            // point makes the camera-to-node positional offset contribute no measurable angle error.
            var farPoint = camera.transform.position + VrcDirectionFromYawPitch(yawDegrees, pitchDegrees) * 1000f;
            InvokeInstanceMethod(tracking, "LookTowardsPoint", new object[] { farPoint });
            InvokeInstanceMethod(controller, "NotifyPlayerMoved", null);
            RefreshVrcHover();

            var achieved = VrcYawPitchFromForward(camera.transform.forward);
            float yawError = Mathf.DeltaAngle(yawDegrees, achieved.x);
            float pitchError = achieved.y - pitchDegrees;

            var builder = new StringBuilder();
            builder.AppendLine("Set look to yaw " + achieved.x.ToString("0.###", CultureInfo.InvariantCulture) +
                ", pitch " + achieved.y.ToString("0.###", CultureInfo.InvariantCulture) +
                " (requested " + yawDegrees.ToString("0.###", CultureInfo.InvariantCulture) +
                " / " + pitchDegrees.ToString("0.###", CultureInfo.InvariantCulture) + ").");
            if (Mathf.Abs(pitchError) > 0.01f)
            {
                builder.AppendLine("Pitch was clamped by ClientSim (limits: 70 down, 80 up).");
            }

            if (Mathf.Abs(yawError) > 0.01f)
            {
                builder.AppendLine("Yaw did not land as requested; the player may be locked in a station.");
            }

            builder.AppendLine();
            builder.AppendLine("Before:");
            builder.AppendLine(before);
            builder.AppendLine();
            builder.AppendLine("After:");
            builder.Append(FormatVrcPlayerState());
            return builder.ToString().TrimEnd();
        }

        /// <summary>Picks the world point to aim at for a resolved object: UI rect centre first, then
        /// collider bounds centre, then child renderer bounds centre, else the transform position.</summary>
        /// <param name="target">Resolved GameObject to aim at.</param>
        /// <param name="anchorKind">Receives which anchor was used, for the report.</param>
        /// <returns>World-space aim point.</returns>
        private static Vector3 GetVrcAimAnchor(GameObject target, out string anchorKind)
        {
            var rect = target.transform as RectTransform;
            if (rect != null)
            {
                var corners = new Vector3[4];
                rect.GetWorldCorners(corners);
                anchorKind = "rect centre";
                return (corners[0] + corners[1] + corners[2] + corners[3]) * 0.25f;
            }

            var collider = target.GetComponent<Collider>();
            if (collider != null)
            {
                anchorKind = "collider centre";
                return collider.bounds.center;
            }

            var renderer = target.GetComponentInChildren<Renderer>();
            if (renderer != null)
            {
                anchorKind = "renderer centre";
                return renderer.bounds.center;
            }

            anchorKind = "transform position";
            return target.transform.position;
        }

        /// <summary>Reads the ClientSim player camera off the desktop tracking provider.</summary>
        /// <returns>The player camera, or null outside a spawned ClientSim session.</returns>
        private static Camera GetVrcPlayerCamera()
        {
            var tracking = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimDesktopTrackingProvider");
            return tracking == null ? null : GetInstanceFieldValue(tracking, "playerCamera") as Camera;
        }

        /// <summary>Forces the ClientSim raycaster to re-run its hover raycast now, so a report made in
        /// the same editor tick as an aim change describes the new aim instead of the previous frame's.
        /// This also re-projects the UI pointer position ClientSim feeds the EventSystem.</summary>
        private static void RefreshVrcHover()
        {
            var raycaster = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimPlayerRaycaster");
            if (raycaster != null)
            {
                InvokeInstanceMethod(raycaster, "Update", null);
            }
        }

        /// <summary>Describes what the ClientSim head ray currently hits: path, distance, and whether it is UI or an interact.</summary>
        /// <returns>One line of hover evidence, or the reason none is available.</returns>
        private static string DescribeVrcAimHit()
        {
            var raycaster = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimPlayerRaycaster");
            if (raycaster == null)
            {
                return "<no raycaster>";
            }

            var hover = GetInstanceFieldValue(raycaster, "_hoverRight");
            if (hover == null)
            {
                return "<none>";
            }

            var hitObject = GetInstanceFieldValue(hover, "hitObject") as GameObject;
            if (hitObject == null)
            {
                return "<no hit within raycast range>";
            }

            var distanceValue = GetInstanceFieldValue(hover, "distance");
            var distanceText = distanceValue is float distance
                ? distance.ToString("0.###", CultureInfo.InvariantCulture)
                : "?";
            var uiShape = GetInstanceFieldValue(hover, "uiShape");
            var interactable = GetInstanceFieldValue(hover, "interactable");
            return GetGameObjectPath(hitObject) + " distance " + distanceText +
                " uiShape " + (uiShape == null ? "no" : "yes") +
                " interactable " + (interactable == null ? "no" : "yes");
        }

        /// <summary>Derives yaw and pitch in degrees (pitch positive up) from a world forward vector.</summary>
        /// <param name="forward">World-space forward direction.</param>
        /// <returns>x is yaw degrees, y is pitch degrees.</returns>
        private static Vector2 VrcYawPitchFromForward(Vector3 forward)
        {
            forward.Normalize();
            float yaw = Mathf.Atan2(forward.x, forward.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Asin(Mathf.Clamp(forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            return new Vector2(yaw, pitch);
        }

        /// <summary>Builds a world direction from yaw and pitch in degrees (pitch positive up).</summary>
        /// <param name="yawDegrees">Yaw in degrees around world up.</param>
        /// <param name="pitchDegrees">Pitch in degrees, positive up.</param>
        /// <returns>Unit direction vector.</returns>
        private static Vector3 VrcDirectionFromYawPitch(float yawDegrees, float pitchDegrees)
        {
            float yaw = yawDegrees * Mathf.Deg2Rad;
            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float cosPitch = Mathf.Cos(pitch);
            return new Vector3(Mathf.Sin(yaw) * cosPitch, Mathf.Sin(pitch), Mathf.Cos(yaw) * cosPitch);
        }

        #endregion

        #region State reporting

        /// <summary>Reports a player change with the ClientSim player as it was before and as it reads back now.</summary>
        /// <param name="lead">What the verb did.</param>
        /// <param name="before">The player state before the change.</param>
        /// <returns>The lead, then the state before and after.</returns>
        private static string VrcStateChangeReport(string lead, string before)
        {
            return lead + Environment.NewLine + "Before:" + Environment.NewLine + before + Environment.NewLine +
                Environment.NewLine + "After:" + Environment.NewLine + FormatVrcPlayerState();
        }

        /// <summary>Reports a menu change with the ClientSim menu as it was before and as it reads back now.</summary>
        /// <param name="lead">What the verb did.</param>
        /// <param name="before">The menu state before the change.</param>
        /// <param name="menu">The ClientSim menu.</param>
        /// <returns>The lead, then the state before and after.</returns>
        private static string VrcMenuChangeReport(string lead, string before, Component menu)
        {
            return lead + Environment.NewLine + "Before:" + Environment.NewLine + before + Environment.NewLine +
                "After:" + Environment.NewLine + FormatVrcMenuState(menu);
        }

        /// <summary>Formats ClientSim menu, player, stance, and drive-helper state.</summary>
        /// <returns>Text report for the current ClientSim player state.</returns>
        private static string FormatVrcPlayerState()
        {
            var builder = new StringBuilder();
            builder.AppendLine(FormatEditorStatus());
            builder.AppendLine();

            var menu = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimMenu");
            builder.AppendLine(menu == null ? "menu: <not found>" : FormatVrcMenuState(menu));

            var controller = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimPlayerController");
            if (controller == null)
            {
                builder.AppendLine("player: <not found>");
            }
            else
            {
                var transform = controller.transform;
                builder.AppendLine("playerPath: " + GetGameObjectPath(controller.gameObject));
                builder.AppendLine("position: " + FormatVector3(transform.position));
                builder.AppendLine("rotationEuler: " + FormatVector3(transform.eulerAngles));
                builder.AppendLine("velocity: " + FormatVector3(InvokeVector3(controller, "GetVelocity", Vector3.zero)));
                builder.AppendLine("grounded: " + Convert.ToString(InvokeInstanceMethod(controller, "IsGrounded", null)));
            }

            var tracking = FindRuntimeComponent("VRC.SDK3.ClientSim.ClientSimDesktopTrackingProvider");
            if (tracking != null)
            {
                var stance = InvokeInstanceMethod(tracking, "GetPlayerStance", null);
                builder.AppendLine("stance: " + (stance == null ? "<unknown>" : stance.ToString()));
            }

            var camera = GetVrcPlayerCamera();
            if (camera != null)
            {
                var look = VrcYawPitchFromForward(camera.transform.forward);
                builder.AppendLine("lookYaw: " + look.x.ToString("0.###", CultureInfo.InvariantCulture) +
                    " lookPitch: " + look.y.ToString("0.###", CultureInfo.InvariantCulture) +
                    " (pitch positive up, clamp 70 down / 80 up)");
                builder.AppendLine("cameraPos: " + FormatVector3(camera.transform.position));
                builder.AppendLine("aimHit: " + DescribeVrcAimHit());
            }

            if (_vrcUseReleaseFrame >= 0)
            {
                builder.AppendLine("pendingUseRelease: frame " + _vrcUseReleaseFrame.ToString(CultureInfo.InvariantCulture));
            }

            // Surfaced because Unity gates UI pointer processing on OS focus; bridge Use sends force the
            // flag themselves, but this line explains any focus-shaped behaviour at a glance.
            builder.AppendLine("editorFocused: " + UnityEditorInternal.InternalEditorUtility.isApplicationActive);

            builder.AppendLine("driverActive: " + IsVrcDriverActive());
            if (IsVrcDriverActive())
            {
                builder.AppendLine("driverForward: " + _vrcDriverForward.ToString("0.###", CultureInfo.InvariantCulture));
                builder.AppendLine("driverStrafe: " + _vrcDriverStrafe.ToString("0.###", CultureInfo.InvariantCulture));
                builder.AppendLine("driverTurnDegPerSec: " + _vrcDriverTurnDegreesPerSecond.ToString("0.###", CultureInfo.InvariantCulture));
                builder.AppendLine("driverSpeed: " + _vrcDriverSpeedMetersPerSecond.ToString("0.###", CultureInfo.InvariantCulture));
            }

            return builder.ToString().TrimEnd();
        }

        #endregion

        #region Dispatch
            /// <summary>
        /// Routes the VrcInput verbs. Returns null when the verb belongs to another area, which is what lets
        /// <see cref="ExecuteCommand"/> try each area in turn; no verb handler returns null of its own.
        /// </summary>
        /// <param name="verb">Lowercased leading verb.</param>
        /// <param name="command">Raw command line, for verbs that take their arguments verbatim.</param>
        /// <param name="parts">Tokenized command.</param>
        /// <returns>The verb's result, or null when this area does not own the verb.</returns>
        private static string DispatchVrcInput(string verb, string command, string[] parts)
        {
            if (verb == "vrc-menu")
            {
                return ControlVrcMenu(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "vrc-player-state")
            {
                return FormatVrcPlayerState();
            }

            if (verb == "vrc-input")
            {
                return SendVrcInput(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "vrc-stance")
            {
                return SetVrcStance(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "vrc-drive")
            {
                return StartVrcDrive(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "vrc-drive-stop")
            {
                return StopVrcDrive();
            }

            if (verb == "vrc-teleport")
            {
                return TeleportVrcPlayer(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "vrc-look")
            {
                return SetVrcLook(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "vrc-look-delta")
            {
                return SetVrcLookDelta(GetCommandRemainder(command, parts[0]));
            }

            if (verb == "vrc-look-at")
            {
                return SetVrcLookAt(GetCommandRemainder(command, parts[0]));
            }


            return null;
        }

        #endregion
    }
}
