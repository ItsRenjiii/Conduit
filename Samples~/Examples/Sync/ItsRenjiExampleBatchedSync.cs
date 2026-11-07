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
    /// Collapses a burst of state changes into one packet. Several events landing in the same moment each
    /// ask for a sync, and without batching each one costs its own packet carrying nearly the same
    /// payload. Scheduling is idempotent, so the first request opens a window and the rest are free.
    ///
    /// The whole behaviour serializes together regardless of how many values moved, which is what makes
    /// this worth doing. conduit-networking.md covers why frequency, not size, is the constraint that
    /// actually bites.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ItsRenjiExampleBatchedSync : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleBatchedSync]</color>";

        /// <summary>How long a batch window stays open, in seconds. Short enough to read as instant.</summary>
        private const float BATCH_DELAY_SECONDS = 0.2f;

        #endregion

        #region Serialized Fields

        [Header("Diagnostics")]
        [Tooltip("Writes each flushed batch to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Synced State

        /// <summary>How many slots are currently occupied.</summary>
        [UdonSynced] private int _playerCount;

        /// <summary>One bit per slot, set when that slot has declared itself ready.</summary>
        [UdonSynced] private int _readyFlags;

        /// <summary>Which round the session is on.</summary>
        [UdonSynced] private int _roundNumber;

        #endregion

        #region Private State

        /// <summary>True while a flush is already scheduled, which is what makes scheduling idempotent.</summary>
        private bool _isSyncPending;

        #endregion

        #region Public API

        /// <summary>Records that a slot has been taken, and opens or joins the current batch window.</summary>
        /// <param name="slotIndex">Index of the slot that was taken.</param>
        public void _OnSlotTaken(int slotIndex)
        {
            if (!Networking.IsOwner(gameObject)) return;

            _playerCount++;
            _readyFlags &= ~(1 << slotIndex);
            ScheduleFlush();
        }

        /// <summary>Records that a slot has declared itself ready.</summary>
        /// <param name="slotIndex">Index of the slot that became ready.</param>
        public void _OnSlotReady(int slotIndex)
        {
            if (!Networking.IsOwner(gameObject)) return;

            _readyFlags |= 1 << slotIndex;
            ScheduleFlush();
        }

        /// <summary>Advances the round and clears the per-round state.</summary>
        public void _AdvanceRound()
        {
            if (!Networking.IsOwner(gameObject)) return;

            _roundNumber++;
            _playerCount = 0;
            _readyFlags = 0;
            ScheduleFlush();
        }

        /// <summary>
        /// Delayed callback that closes the batch window and sends one packet. It is public because Udon
        /// dispatches delayed events by name, and it carries the underscore so a remote client cannot.
        /// </summary>
        public void _FlushBatch()
        {
            _isSyncPending = false;

            if (!Networking.IsOwner(gameObject)) return;

            RequestSerialization();
            LogDebug($"Flushed round {_roundNumber}, {_playerCount} player(s).");
        }

        #endregion

        #region VRChat Events

        /// <summary>Applies the batched values once they arrive.</summary>
        public override void OnDeserialization()
        {
            LogDebug($"Received round {_roundNumber}, {_playerCount} player(s), ready flags {_readyFlags}.");
        }

        #endregion

        #region Internal

        /// <summary>
        /// Opens a batch window when one is not already open. Calling it repeatedly inside the window is
        /// deliberately free, which is what collapses the burst.
        /// </summary>
        private void ScheduleFlush()
        {
            if (_isSyncPending) return;

            _isSyncPending = true;
            SendCustomEventDelayedSeconds(nameof(_FlushBatch), BATCH_DELAY_SECONDS);
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
