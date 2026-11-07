/*
Copyright (c) 2026 ItsRenji.

Conduit example code, licensed for reuse.

Permission is granted, free of charge, to any person obtaining a copy of this file to use, copy,
modify, adapt and redistribute it, in source or compiled form, as part of their own projects and
products, including commercially, with or without attribution.

Copy it into your own folder and namespace rather than referencing it where it sits. Conduit does
not ship inside a built product, so anything pointing at this path ships broken.

This grant covers the files under Conduit's Examples folder only and does not extend to the rest
of Conduit.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT
NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE, AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM,
DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace ItsRenji.Examples.Movement
{
    /// <summary>
    /// A trigger volume that changes how the local player moves, and puts back exactly what it found.
    ///
    /// The point of this example is the restore. Every movement setter has a matching getter, so a zone
    /// can CAPTURE the incoming values on entry and replay them on exit. A zone that writes hardcoded
    /// "normal" numbers instead silently undoes every other zone the player is still standing in, and the
    /// bug only shows up where two volumes overlap, which is the hardest place to notice it.
    ///
    /// These calls are local: they change the calling client's own player, need no ownership and must
    /// never be synced. A seated player's collider is disabled, so this volume will not fire for anybody
    /// sitting in a station. conduit-player.md owns both points.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiExampleMovementZone : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleMovementZone]</color>";

        #endregion

        #region Serialized Fields

        [Header("Movement Inside The Zone")]
        [Tooltip("Walking speed applied while the player is inside.")]
        [SerializeField] private float _walkSpeed = 2f;

        [Tooltip("Running speed applied while the player is inside.")]
        [SerializeField] private float _runSpeed = 4f;

        [Tooltip("Strafing speed applied while the player is inside.")]
        [SerializeField] private float _strafeSpeed = 2f;

        [Tooltip("Jump impulse applied while the player is inside. Zero removes jumping.")]
        [SerializeField] private float _jumpImpulse = 3f;

        [Tooltip("Gravity multiplier applied while the player is inside.")]
        [SerializeField] private float _gravityStrength = 1f;

        [Header("Options")]
        [Tooltip("Stop the player moving entirely, without disturbing the speeds above.")]
        [SerializeField] private bool _immobilize;

        [Header("Diagnostics")]
        [Tooltip("Writes entry and exit to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Private State

        /// <summary>True once the incoming values have been captured, so exit cannot restore garbage.</summary>
        private bool _hasCapturedState;

        /// <summary>Walking speed as found on entry.</summary>
        private float _previousWalkSpeed;

        /// <summary>Running speed as found on entry.</summary>
        private float _previousRunSpeed;

        /// <summary>Strafing speed as found on entry.</summary>
        private float _previousStrafeSpeed;

        /// <summary>Jump impulse as found on entry.</summary>
        private float _previousJumpImpulse;

        /// <summary>Gravity multiplier as found on entry.</summary>
        private float _previousGravityStrength;

        #endregion

        #region VRChat Events

        /// <summary>Captures what the player arrived with, then applies this zone's values.</summary>
        /// <param name="player">The player who entered the volume.</param>
        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid() || !player.isLocal) return;

            CaptureCurrentState(player);
            ApplyZoneState(player);
            LogDebug("Entered, previous movement captured.");
        }

        /// <summary>Puts back whatever the player arrived with.</summary>
        /// <param name="player">The player who left the volume.</param>
        public override void OnPlayerTriggerExit(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid() || !player.isLocal) return;

            RestoreCapturedState(player);
            LogDebug("Exited, previous movement restored.");
        }

        #endregion

        #region Public API

        /// <summary>Applies this zone's movement to the local player without needing the trigger.</summary>
        public void _ApplyToLocalPlayer()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid()) return;

            CaptureCurrentState(localPlayer);
            ApplyZoneState(localPlayer);
        }

        /// <summary>Restores the captured movement to the local player without needing the trigger.</summary>
        public void _RestoreLocalPlayer()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid()) return;

            RestoreCapturedState(localPlayer);
        }

        #endregion

        #region Internal

        /// <summary>
        /// Reads the player's current movement so exit can put it back. Capturing only once means a player
        /// who re-enters without having left does not record this zone's own values as the originals.
        /// </summary>
        /// <param name="player">The local player.</param>
        private void CaptureCurrentState(VRCPlayerApi player)
        {
            if (_hasCapturedState) return;

            _previousWalkSpeed = player.GetWalkSpeed();
            _previousRunSpeed = player.GetRunSpeed();
            _previousStrafeSpeed = player.GetStrafeSpeed();
            _previousJumpImpulse = player.GetJumpImpulse();
            _previousGravityStrength = player.GetGravityStrength();
            _hasCapturedState = true;
        }

        /// <summary>Applies this zone's authored movement.</summary>
        /// <param name="player">The local player.</param>
        private void ApplyZoneState(VRCPlayerApi player)
        {
            player.SetWalkSpeed(_walkSpeed);
            player.SetRunSpeed(_runSpeed);
            player.SetStrafeSpeed(_strafeSpeed);
            player.SetJumpImpulse(_jumpImpulse);
            player.SetGravityStrength(_gravityStrength);
            player.Immobilize(_immobilize);
        }

        /// <summary>Replays the captured movement, and does nothing when there is nothing captured.</summary>
        /// <param name="player">The local player.</param>
        private void RestoreCapturedState(VRCPlayerApi player)
        {
            if (!_hasCapturedState) return;

            player.SetWalkSpeed(_previousWalkSpeed);
            player.SetRunSpeed(_previousRunSpeed);
            player.SetStrafeSpeed(_previousStrafeSpeed);
            player.SetJumpImpulse(_previousJumpImpulse);
            player.SetGravityStrength(_previousGravityStrength);
            player.Immobilize(false);
            _hasCapturedState = false;
        }

        /// <summary>Writes a diagnostic line when debug logging is switched on.</summary>
        /// <param name="message">Text to log.</param>
        private void LogDebug(string message)
        {
            if (_enableDebugLogging)
            {
                Debug.Log($"{LOG_PREFIX} {message}");
            }
        }

        #endregion
    }
}
