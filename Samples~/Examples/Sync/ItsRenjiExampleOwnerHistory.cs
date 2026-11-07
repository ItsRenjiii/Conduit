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
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon.Common.Interfaces;

namespace ItsRenji.Examples.Sync
{
    /// <summary>
    /// One client owns the state and everyone else asks it to change. Any player can request a move; the
    /// owner validates the request and is the only client that ever writes. History travels as synced
    /// state rather than as events, so a late joiner can undo moves made before they arrived.
    ///
    /// Two rules this example exists to show. Requests are NOT gated on ownership at the point the input
    /// arrives, because a check there makes the control dead for everyone except the owner; the check
    /// belongs where the state is mutated. And a network endpoint is untrusted input, so the handler
    /// checks the caller rather than assuming its own button was the one that called it.
    ///
    /// Read the caller check for what it is: a COHERENCE check, not a permission one. Ownership can be
    /// taken by anybody, so a caller who wants past it takes ownership first and then calls. It keeps a
    /// stale or confused client from writing, and it is the right guard for a game whose worst case is a
    /// wrong move. Anything whose worst case is the WRONG PERSON writing needs an identity list instead,
    /// which conduit-networking.md covers along with both of the rules above.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ItsRenjiExampleOwnerHistory : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleOwnerHistory]</color>";

        /// <summary>How many moves can be undone, on top of the starting position.</summary>
        private const int MAXIMUM_MOVES = 64;

        /// <summary>How many bytes one recorded position occupies.</summary>
        private const int STATE_SIZE = 16;

        #endregion

        #region Serialized Fields

        [Header("Diagnostics")]
        [Tooltip("Writes each accepted move and undo to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Synced State

        /// <summary>The position every client displays.</summary>
        [UdonSynced] private byte[] _currentState;

        /// <summary>Every recorded position laid end to end, oldest first.</summary>
        [UdonSynced] private byte[] _stateHistory;

        /// <summary>How many positions are recorded. Entry zero is the starting position.</summary>
        [UdonSynced] private int _historyCount;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// Allocates both synced arrays. A synced array left null does not merely sync as empty, it stops
        /// the whole behaviour syncing, so UNRELATED fields quietly stop arriving too.
        /// </summary>
        private void Start()
        {
            _currentState = new byte[STATE_SIZE];
            _stateHistory = new byte[STATE_SIZE * (MAXIMUM_MOVES + 1)];

            if (Networking.IsOwner(gameObject))
            {
                RecordCurrentState();
                RequestSerialization();
            }

            ApplyDisplay();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Asks the owner to make a move. Deliberately free of an ownership check: any player may ask, and
        /// the owner decides. Target Owner resolves to the owner of THIS behaviour's GameObject.
        /// </summary>
        /// <param name="from">Index the move starts at.</param>
        /// <param name="to">Index the move ends at.</param>
        public void _RequestMove(int from, int to)
        {
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(_OwnerApplyMove), from, to);
        }

        /// <summary>Asks the owner to undo the most recent move.</summary>
        public void _RequestUndo()
        {
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(_OwnerUndo));
        }

        /// <summary>Returns how many moves can still be undone.</summary>
        /// <returns>The number of undoable moves, excluding the starting position.</returns>
        public int _GetUndoDepth()
        {
            return _historyCount <= 1 ? 0 : _historyCount - 1;
        }

        #endregion

        #region Network Endpoints

        /// <summary>
        /// Applies a move on the owner. This is reachable by any client in the instance, so it validates
        /// the caller and its own arguments before touching state. The attribute re-opens the method
        /// despite the underscore, which is the point: the prefix is a default, not a lock.
        /// </summary>
        /// <param name="from">Index the move starts at.</param>
        /// <param name="to">Index the move ends at.</param>
        [NetworkCallable]
        public void _OwnerApplyMove(int from, int to)
        {
            if (!Networking.IsOwner(gameObject)) return;
            if (!IsCallerAllowed()) return;
            if (!IsMoveLegal(from, to)) return;
            if (_historyCount > MAXIMUM_MOVES) return;

            byte carried = _currentState[from];
            _currentState[from] = _currentState[to];
            _currentState[to] = carried;

            RecordCurrentState();
            ApplyDisplay();
            RequestSerialization();
            LogDebug($"Move {from} to {to} accepted, depth {_historyCount}.");
        }

        /// <summary>Steps one position back on the owner, if there is anything to step back to.</summary>
        [NetworkCallable]
        public void _OwnerUndo()
        {
            if (!Networking.IsOwner(gameObject)) return;
            if (!IsCallerAllowed()) return;
            if (_historyCount <= 1) return;

            _historyCount--;
            System.Array.Copy(_stateHistory, (_historyCount - 1) * STATE_SIZE, _currentState, 0, STATE_SIZE);

            ApplyDisplay();
            RequestSerialization();
            LogDebug($"Undo accepted, depth {_historyCount}.");
        }

        #endregion

        #region VRChat Events

        /// <summary>
        /// Redraws from the received state. History is never appended here: the owner already recorded the
        /// move, and recording it again on each receiver would double every entry.
        /// </summary>
        public override void OnDeserialization()
        {
            ApplyDisplay();
        }

        #endregion

        #region Internal

        /// <summary>
        /// Answers whether the call is coherent: a direct local call passes, and a network call must come
        /// from the current owner. Without the local allowance the owner's own button would be refused by
        /// its own guard. This is NOT a permission check, because ownership is takeable.
        /// </summary>
        /// <returns>True when the call should be honoured.</returns>
        private bool IsCallerAllowed()
        {
            if (!NetworkCalling.InNetworkCall) return true;

            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (caller == null || !caller.IsValid()) return false;

            VRCPlayerApi owner = Networking.GetOwner(gameObject);
            if (owner == null || !owner.IsValid()) return false;

            return caller.playerId == owner.playerId;
        }

        /// <summary>Answers whether both indices fall inside the state array.</summary>
        /// <param name="from">Index the move starts at.</param>
        /// <param name="to">Index the move ends at.</param>
        /// <returns>True when the move is within bounds.</returns>
        private bool IsMoveLegal(int from, int to)
        {
            if (_currentState == null) return false;

            return from >= 0 && from < _currentState.Length
                && to >= 0 && to < _currentState.Length;
        }

        /// <summary>Appends the current position to the history.</summary>
        private void RecordCurrentState()
        {
            if (_historyCount > MAXIMUM_MOVES) return;

            System.Array.Copy(_currentState, 0, _stateHistory, _historyCount * STATE_SIZE, STATE_SIZE);
            _historyCount++;
        }

        /// <summary>Reflects the current position in the world. Replace the body with the real display.</summary>
        private void ApplyDisplay()
        {
            LogDebug($"Display updated, history depth {_historyCount}.");
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
