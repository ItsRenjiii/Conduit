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
    /// Hands each player one pre-placed object out of a fixed set, and takes it back when they leave. One
    /// client coordinates and writes; every other client reads the table and applies it. The table itself
    /// is the truth, which is what lets a late joiner see the correct assignment with no extra message.
    ///
    /// This is session bookkeeping, not access control. Instance master moves when the master leaves and
    /// nothing vouches for it, so gate a privileged action on an owner-controlled role instead.
    /// conduit-networking.md covers why, and why pre-placed objects beat runtime instantiation.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ItsRenjiExampleSlotPool : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleSlotPool]</color>";

        /// <summary>How long after a handover to re-check the table, in seconds.</summary>
        private const float RECONCILE_DELAY_SECONDS = 2f;

        /// <summary>Value marking a free slot. Real player ids are always one or greater.</summary>
        private const int SLOT_FREE = 0;

        #endregion

        #region Serialized Fields

        [Header("Pool")]
        [Tooltip("One pre-placed object per slot. Runtime-instantiated objects have no network identity.")]
        [SerializeField] private GameObject[] _slotObjects;

        [Header("Diagnostics")]
        [Tooltip("Writes assignments and handovers to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Synced State

        /// <summary>Player id holding each slot, or SLOT_FREE. This table is the authority.</summary>
        [UdonSynced] private int[] _assignments;

        #endregion

        #region Private State

        /// <summary>Last applied table, used to act only on slots that actually changed.</summary>
        private int[] _appliedAssignments;

        /// <summary>Free slot indices, oldest first, so slots are reused fairly.</summary>
        private int[] _freeQueue;

        /// <summary>Read cursor into the free queue.</summary>
        private int _freeHead;

        /// <summary>Write cursor into the free queue.</summary>
        private int _freeTail;

        /// <summary>How many entries the free queue currently holds.</summary>
        private int _freeCount;

        /// <summary>True while a reconcile is already scheduled, so handovers cannot stack timers.</summary>
        private bool _isReconcileScheduled;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// Allocates the tables. The synced array is allocated even on clients that never write it,
        /// because a synced array left null stops the whole behaviour syncing rather than syncing empty.
        /// </summary>
        private void Start()
        {
            int slotCount = _slotObjects == null ? 0 : _slotObjects.Length;

            _assignments = new int[slotCount];
            _appliedAssignments = new int[slotCount];
            _freeQueue = new int[slotCount];

            RebuildFreeQueue();
            ApplyAssignments();

            if (IsCoordinator())
            {
                TakeCoordinatorOwnership();
            }
        }

        #endregion

        #region Public API

        /// <summary>Returns the slot index held by a player, or minus one when they hold none.</summary>
        /// <param name="playerId">Player id to look up.</param>
        /// <returns>The slot index, or minus one.</returns>
        public int _GetSlotForPlayer(int playerId)
        {
            return FindSlot(playerId);
        }

        /// <summary>
        /// Deferred re-check after a handover. Public because Udon dispatches delayed events by name; the
        /// underscore keeps a remote client from forcing a reconcile.
        /// </summary>
        public void _ReconcileAssignments()
        {
            _isReconcileScheduled = false;
            Reconcile();
        }

        #endregion

        #region VRChat Events

        /// <summary>Gives the joining player a slot, when this client is the one that writes.</summary>
        /// <param name="player">The player who joined.</param>
        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            // Re-apply first: a slot may have arrived for a player who was not resolvable at the time.
            ApplyAssignments();

            if (!IsCoordinator() || !Utilities.IsValid(player)) return;
            if (FindSlot(player.playerId) >= 0) return;

            AssignSlot(player.playerId);
        }

        /// <summary>Returns the leaving player's slot to the pool.</summary>
        /// <param name="player">The player who left.</param>
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (!IsCoordinator() || !Utilities.IsValid(player)) return;

            int slot = FindSlot(player.playerId);
            if (slot < 0) return;

            _assignments[slot] = SLOT_FREE;
            EnqueueFree(slot);
            ApplyAssignments();
            RequestSerialization();
            LogDebug($"Slot {slot} released.");
        }

        /// <summary>Takes over writing when this client becomes master.</summary>
        /// <param name="newMaster">The client that became master.</param>
        public override void OnMasterTransferred(VRCPlayerApi newMaster)
        {
            TakeCoordinatorOwnership();
        }

        /// <summary>Re-checks the table once this client holds both master and ownership.</summary>
        /// <param name="newOwner">The client that became owner.</param>
        public override void OnOwnershipTransferred(VRCPlayerApi newOwner)
        {
            if (!IsCoordinator()) return;

            ScheduleReconcile();
        }

        /// <summary>Applies the received table.</summary>
        public override void OnDeserialization()
        {
            ApplyAssignments();
        }

        #endregion

        #region Internal

        /// <summary>Answers whether this client both coordinates the session and owns the table.</summary>
        /// <returns>True when this client is the one allowed to write.</returns>
        private bool IsCoordinator()
        {
            return Networking.IsMaster && Networking.IsOwner(gameObject);
        }

        /// <summary>Takes ownership when this client is master, then schedules a re-check.</summary>
        private void TakeCoordinatorOwnership()
        {
            if (!Networking.IsMaster) return;

            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }

            if (IsCoordinator())
            {
                ScheduleReconcile();
            }
        }

        /// <summary>Schedules one re-check, ignoring further requests while one is pending.</summary>
        private void ScheduleReconcile()
        {
            if (_isReconcileScheduled) return;

            _isReconcileScheduled = true;
            SendCustomEventDelayedSeconds(nameof(_ReconcileAssignments), RECONCILE_DELAY_SECONDS);
        }

        /// <summary>Frees slots whose player has gone and gives a slot to anyone without one.</summary>
        private void Reconcile()
        {
            if (!IsCoordinator() || _assignments == null) return;

            bool changed = false;

            for (int i = 0; i < _assignments.Length; i++)
            {
                if (_assignments[i] == SLOT_FREE) continue;

                VRCPlayerApi holder = VRCPlayerApi.GetPlayerById(_assignments[i]);
                if (!Utilities.IsValid(holder))
                {
                    _assignments[i] = SLOT_FREE;
                    changed = true;
                }
            }

            RebuildFreeQueue();

            int playerCount = VRCPlayerApi.GetPlayerCount();
            VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
            VRCPlayerApi.GetPlayers(players);

            for (int i = 0; i < players.Length; i++)
            {
                if (!Utilities.IsValid(players[i])) continue;
                if (FindSlot(players[i].playerId) >= 0) continue;

                if (AssignSlotWithoutSending(players[i].playerId))
                {
                    changed = true;
                }
            }

            ApplyAssignments();

            if (changed)
            {
                RequestSerialization();
            }
        }

        /// <summary>Gives one player the next free slot and sends the table.</summary>
        /// <param name="playerId">Player to place.</param>
        private void AssignSlot(int playerId)
        {
            if (!AssignSlotWithoutSending(playerId)) return;

            ApplyAssignments();
            RequestSerialization();
        }

        /// <summary>Gives one player the next free slot without sending, for use inside a reconcile.</summary>
        /// <param name="playerId">Player to place.</param>
        /// <returns>True when a slot was taken.</returns>
        private bool AssignSlotWithoutSending(int playerId)
        {
            int slot = DequeueFree();
            if (slot < 0)
            {
                LogDebug($"Pool full, no slot for player {playerId}.");
                return false;
            }

            _assignments[slot] = playerId;
            LogDebug($"Slot {slot} assigned to player {playerId}.");
            return true;
        }

        /// <summary>
        /// Brings this client's objects in line with the table, acting only on slots that changed. A slot
        /// whose player cannot be resolved yet is left unapplied so a later pass can retry it.
        /// </summary>
        private void ApplyAssignments()
        {
            if (_slotObjects == null || _assignments == null || _appliedAssignments == null) return;

            int count = Mathf.Min(_assignments.Length, Mathf.Min(_appliedAssignments.Length, _slotObjects.Length));

            for (int i = 0; i < count; i++)
            {
                if (_assignments[i] == _appliedAssignments[i]) continue;

                if (_assignments[i] != SLOT_FREE)
                {
                    VRCPlayerApi holder = VRCPlayerApi.GetPlayerById(_assignments[i]);
                    if (!Utilities.IsValid(holder)) continue;
                }

                if (_slotObjects[i] != null)
                {
                    _slotObjects[i].SetActive(_assignments[i] != SLOT_FREE);
                }

                _appliedAssignments[i] = _assignments[i];
            }
        }

        /// <summary>Returns the slot a player holds, or minus one.</summary>
        /// <param name="playerId">Player id to look up.</param>
        /// <returns>Slot index, or minus one.</returns>
        private int FindSlot(int playerId)
        {
            if (_assignments == null || playerId == SLOT_FREE) return -1;

            for (int i = 0; i < _assignments.Length; i++)
            {
                if (_assignments[i] == playerId) return i;
            }

            return -1;
        }

        /// <summary>Rebuilds the free queue from the table.</summary>
        private void RebuildFreeQueue()
        {
            if (_freeQueue == null || _assignments == null) return;

            _freeHead = 0;
            _freeTail = 0;
            _freeCount = 0;

            for (int i = 0; i < _assignments.Length; i++)
            {
                if (_assignments[i] == SLOT_FREE)
                {
                    EnqueueFree(i);
                }
            }
        }

        /// <summary>Puts a slot back at the end of the free queue.</summary>
        /// <param name="slot">Slot index to return.</param>
        private void EnqueueFree(int slot)
        {
            if (_freeQueue == null || _freeQueue.Length == 0) return;
            if (slot < 0 || slot >= _freeQueue.Length) return;
            if (_freeCount >= _freeQueue.Length) return;

            _freeQueue[_freeTail] = slot;
            _freeTail = (_freeTail + 1) % _freeQueue.Length;
            _freeCount++;
        }

        /// <summary>Takes the oldest free slot, or minus one when the pool is full.</summary>
        /// <returns>Slot index, or minus one.</returns>
        private int DequeueFree()
        {
            if (_freeQueue == null || _freeQueue.Length == 0 || _freeCount <= 0) return -1;

            int slot = _freeQueue[_freeHead];
            _freeHead = (_freeHead + 1) % _freeQueue.Length;
            _freeCount--;
            return slot;
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
