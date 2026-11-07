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

namespace ItsRenji.Examples.Sync
{
    /// <summary>
    /// Caps how often a continuously moving value is sent, without losing the value it settles on. A
    /// slider drag produces a change every frame; sending each one starves the whole instance, and simply
    /// dropping them loses the value the player actually chose.
    ///
    /// The first change opens a window and schedules the send. Changes during the window only update the
    /// local value. When the window closes the latest value goes out, and if the value was still moving a
    /// further window opens, which is what guarantees the final write is transmitted rather than dropped.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ItsRenjiExampleRateLimitedSync : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleRateLimitedSync]</color>";

        /// <summary>Shortest gap between two sends, in seconds.</summary>
        private const float SYNC_COOLDOWN_SECONDS = 0.15f;

        #endregion

        #region Serialized Fields

        [Header("Diagnostics")]
        [Tooltip("Writes each applied value to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Synced State

        /// <summary>The value as last sent. The local copy below is what the world reads.</summary>
        [UdonSynced] private float _syncedValue;

        #endregion

        #region Private State

        /// <summary>The live value, always current on the owner even mid-window.</summary>
        private float _localValue;

        /// <summary>True while a send is already scheduled.</summary>
        private bool _isWindowOpen;

        /// <summary>Counts every change, so a window can tell whether the value moved while it was open.</summary>
        private int _changeCounter;

        /// <summary>The change count captured when the current window opened.</summary>
        private int _changeCounterAtWindowOpen;

        #endregion

        #region Public API

        /// <summary>
        /// Records a new value. Call this as often as the input changes; the pacing is handled here.
        /// </summary>
        /// <param name="value">The value the control now sits at.</param>
        public void _SetValue(float value)
        {
            if (!Networking.IsOwner(gameObject)) return;

            _localValue = value;
            _changeCounter++;

            // A window is already open, so the newer value will be picked up when it closes.
            if (_isWindowOpen) return;

            _isWindowOpen = true;
            _changeCounterAtWindowOpen = _changeCounter;
            SendCustomEventDelayedSeconds(nameof(_OnWindowClosed), SYNC_COOLDOWN_SECONDS);
        }

        /// <summary>Returns the live value rather than the last one sent.</summary>
        /// <returns>The current local value.</returns>
        public float _GetValue()
        {
            return _localValue;
        }

        /// <summary>
        /// Delayed callback that closes the window and sends. Public because Udon dispatches delayed
        /// events by name; the underscore keeps a remote client from calling it.
        /// </summary>
        public void _OnWindowClosed()
        {
            _isWindowOpen = false;
            SendLatest();

            // The value kept moving while the window was open, so one more window carries the final write.
            if (_changeCounter != _changeCounterAtWindowOpen)
            {
                _isWindowOpen = true;
                _changeCounterAtWindowOpen = _changeCounter;
                SendCustomEventDelayedSeconds(nameof(_OnWindowClosed), SYNC_COOLDOWN_SECONDS);
            }
        }

        #endregion

        #region VRChat Events

        /// <summary>Takes the received value as the new local value and applies it.</summary>
        public override void OnDeserialization()
        {
            _localValue = _syncedValue;
            ApplyValue();
        }

        #endregion

        #region Internal

        /// <summary>Copies the live value into the synced field and asks for a send.</summary>
        private void SendLatest()
        {
            if (!Networking.IsOwner(gameObject)) return;

            _syncedValue = _localValue;
            RequestSerialization();
            LogDebug($"Sent {_localValue:F3}.");
        }

        /// <summary>Drives whatever the value controls. Replace the body with the real effect.</summary>
        private void ApplyValue()
        {
            LogDebug($"Applied {_localValue:F3}.");
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
