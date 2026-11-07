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
    /// Defers a send while the instance is congested instead of adding to a queue that is already backed
    /// up. Networking.IsClogged answers whether this client is behind, and backing off is the difference
    /// between one system waiting and every system in the world getting slower.
    ///
    /// The back-off widens per attempt and gives up after a fixed number of tries, because a retry chain
    /// with no ceiling outlives the condition it was waiting on. Play mode cannot exercise any of this:
    /// see conduit-testing.md for why congestion needs real clients.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ItsRenjiExampleCloggedRetrySync : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleCloggedRetrySync]</color>";

        /// <summary>Base gap between retries, multiplied by the attempt number to widen the back-off.</summary>
        private const float RETRY_DELAY_SECONDS = 1.5f;

        /// <summary>How many attempts to make before abandoning this send.</summary>
        private const int MAXIMUM_RETRIES = 5;

        #endregion

        #region Serialized Fields

        [Header("Diagnostics")]
        [Tooltip("Writes retries and abandonments to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Synced State

        /// <summary>The score every client displays.</summary>
        [UdonSynced] private int _score;

        /// <summary>Which phase the session is in.</summary>
        [UdonSynced] private int _phase;

        #endregion

        #region Private State

        /// <summary>How many retries the current send has already used.</summary>
        private int _retryCount;

        /// <summary>True while a retry is scheduled, so attempts cannot stack.</summary>
        private bool _isRetryScheduled;

        #endregion

        #region Public API

        /// <summary>Sets the score and sends it when the network is clear.</summary>
        /// <param name="score">The score to publish.</param>
        public void _SetScore(int score)
        {
            if (!Networking.IsOwner(gameObject)) return;

            _score = score;
            TrySerialize();
        }

        /// <summary>Sets the session phase and sends it when the network is clear.</summary>
        /// <param name="phase">The phase to publish.</param>
        public void _SetPhase(int phase)
        {
            if (!Networking.IsOwner(gameObject)) return;

            _phase = phase;
            TrySerialize();
        }

        /// <summary>
        /// Delayed retry. Public because Udon dispatches delayed events by name; the underscore keeps a
        /// remote client from driving the retry loop.
        /// </summary>
        public void _RetrySerialize()
        {
            _isRetryScheduled = false;
            TrySerialize();
        }

        #endregion

        #region VRChat Events

        /// <summary>Applies the received values.</summary>
        public override void OnDeserialization()
        {
            LogDebug($"Received score {_score}, phase {_phase}.");
        }

        #endregion

        #region Internal

        /// <summary>Sends when the network is clear, and schedules a retry when it is not.</summary>
        private void TrySerialize()
        {
            if (!Networking.IsOwner(gameObject)) return;

            if (Networking.IsClogged)
            {
                ScheduleRetry();
                return;
            }

            _retryCount = 0;
            _isRetryScheduled = false;
            RequestSerialization();
        }

        /// <summary>Schedules one retry, widening the delay per attempt and stopping at the ceiling.</summary>
        private void ScheduleRetry()
        {
            if (_isRetryScheduled) return;

            if (_retryCount >= MAXIMUM_RETRIES)
            {
                // Abandoning is deliberate. The next call to a setter starts a fresh chain, and an
                // unbounded one would outlive the congestion it was waiting on.
                Debug.LogWarning($"{LOG_PREFIX} Gave up after {MAXIMUM_RETRIES} retries; congestion is persistent.");
                _retryCount = 0;
                _isRetryScheduled = false;
                return;
            }

            _retryCount++;
            _isRetryScheduled = true;
            SendCustomEventDelayedSeconds(nameof(_RetrySerialize), RETRY_DELAY_SECONDS * _retryCount);
            LogDebug($"Clogged, retry {_retryCount} of {MAXIMUM_RETRIES} scheduled.");
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
