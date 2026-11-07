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

namespace ItsRenji.Examples.Security
{
    /// <summary>
    /// State where the WRITER matters, not just the value. Every Sync example answers "what is the value";
    /// this one answers "who was allowed to set it", which is a different architecture rather than an
    /// extra check bolted onto the same one.
    ///
    /// Why none of it uses [UdonSynced]: a synced variable hands the receiver the data and NOTHING about
    /// its origin, and the object's owner is not evidence because ownership can be taken by anybody. Only a
    /// network call carries identity, in NetworkCalling.CallingPlayer. So the state travels as an event and
    /// each receiver judges the sender. The price is late-joiner replay, which synced variables give free
    /// and events never do, so this behaviour sends the state to joiners itself.
    ///
    /// It raises the cost of interference. It is NOT access control, for the reason written out in
    /// ItsRenjiExampleOwnerGuard. conduit-networking.md owns the whole subject.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class ItsRenjiExampleAuthorizedState : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleAuthorizedState]</color>";

        /// <summary>Queue depth this endpoint expects, declared on the attribute and used when pacing sends.</summary>
        private const int MAXIMUM_QUEUED_EVENTS = 5;

        /// <summary>How long to wait before retrying a send that would join a backlog, in seconds.</summary>
        private const float SEND_RETRY_SECONDS = 0.5f;

        /// <summary>Receiver id meaning every client should act on the message.</summary>
        private const int RECEIVER_EVERYONE = -1;

        #endregion

        #region Serialized Fields

        [Header("Permission")]
        [Tooltip("The list that decides who may write this state.")]
        [SerializeField] private ItsRenjiExampleAllowList _allowList;

        [Header("Validation Retry")]
        [Tooltip("How long to wait before re-judging a sender who could not be judged yet, in seconds.")]
        [SerializeField] private float _retryDelaySeconds = 5f;

        [Tooltip("How many times to re-judge before dropping the payload unapplied.")]
        [SerializeField] private int _maximumAttempts = 4;

        [Header("Diagnostics")]
        [Tooltip("Writes accepted, retried and dropped payloads to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Private State

        /// <summary>The applied state. Only ApplyPending writes this.</summary>
        private string _state = string.Empty;

        /// <summary>The newest payload received and not yet applied.</summary>
        private string _pendingState;

        /// <summary>Who sent the pending payload, which is the whole reason this travels as an event.</summary>
        private VRCPlayerApi _pendingSender;

        /// <summary>How many times the pending payload's sender has been judged.</summary>
        private int _attempts;

        /// <summary>True while a re-judge is scheduled, so attempts cannot stack.</summary>
        private bool _isRetryScheduled;

        /// <summary>True while a deferred send is scheduled, so sends cannot stack.</summary>
        private bool _isSendScheduled;

        /// <summary>The local player, cached once.</summary>
        private VRCPlayerApi _localPlayer;

        #endregion

        #region Unity Lifecycle

        /// <summary>Caches the local player and reports a missing allow list, which nothing here works without.</summary>
        private void Start()
        {
            _localPlayer = Networking.LocalPlayer;

            if (_allowList == null)
            {
                Debug.LogError($"{LOG_PREFIX} No allow list assigned, so no sender can ever be judged.", this);
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Writes the state, if this client may. A client that may not is refused here AND would be refused
        /// again by every receiver, which is the point: the sender's own check is a courtesy, and the
        /// receiver's is the one that counts.
        /// </summary>
        /// <param name="state">The state to publish.</param>
        public void _SetState(string state)
        {
            if (_allowList == null || !_allowList._IsAllowed(_localPlayer))
            {
                LogDebug("Local player may not write this state.");
                return;
            }

            _state = state;
            ApplyState();
            ScheduleSend();
        }

        /// <summary>Returns the applied state.</summary>
        /// <returns>The current state.</returns>
        public string _GetState()
        {
            return _state;
        }

        /// <summary>
        /// Re-judges a sender who could not be judged when their payload arrived. Public because Udon
        /// dispatches delayed events by name.
        /// </summary>
        public void _RetryApply()
        {
            _isRetryScheduled = false;

            // Zero means the pending payload was applied or replaced while this was in flight.
            if (_attempts == 0) return;

            TryApplyPending();
        }

        /// <summary>Deferred send, used when the outgoing queue was not empty. Public for the same reason.</summary>
        public void _SendDeferred()
        {
            _isSendScheduled = false;
            ScheduleSend();
        }

        #endregion

        #region Network Endpoint

        /// <summary>
        /// Receives state from another client. Reachable by anyone in the instance, so it establishes that
        /// this really is a network call, that the caller resolves, and that the message is addressed here,
        /// before storing anything.
        /// </summary>
        /// <param name="receiverId">Player id this is meant for, or RECEIVER_EVERYONE for a broadcast.</param>
        /// <param name="state">The state being published.</param>
        [NetworkCallable(MAXIMUM_QUEUED_EVENTS)]
        public void _OnStateReceived(int receiverId, string state)
        {
            // This method is only ever an endpoint, so a local call is a wiring mistake rather than a use.
            if (!NetworkCalling.InNetworkCall)
            {
                Debug.LogError($"{LOG_PREFIX} _OnStateReceived is a network endpoint and was called locally.", this);
                return;
            }

            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (!Utilities.IsValid(caller)) return;

            // A broadcast reaches everybody, so a message addressed to one player is dropped by the rest.
            if (receiverId != RECEIVER_EVERYONE && receiverId != _localPlayer.playerId) return;

            // Newest payload always wins: anything still pending is superseded and its attempts reset.
            _pendingState = state;
            _pendingSender = caller;
            _attempts = 0;

            TryApplyPending();
        }

        #endregion

        #region VRChat Events

        /// <summary>
        /// Replaces the late-joiner replay that synced variables would have given. The message is addressed
        /// to the joiner, so every other client receives it and drops it.
        /// </summary>
        /// <param name="player">The player who joined.</param>
        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || player.isLocal) return;
            if (!Networking.IsOwner(gameObject)) return;
            if (_allowList == null || !_allowList._IsAllowed(_localPlayer)) return;

            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(_OnStateReceived), player.playerId, _state);
            LogDebug("Sent current state to a joining player.");
        }

        #endregion

        #region Internal

        /// <summary>
        /// Applies the pending payload once its sender can be judged, and re-schedules while it cannot.
        ///
        /// A sender can be unjudgeable on arrival whenever the permission source is itself world state a
        /// fresh client does not hold yet, so rejecting outright would lose legitimate updates on join.
        /// After the last attempt the payload is DROPPED: losing an update is the acceptable failure, and
        /// applying an unvalidated one is not.
        /// </summary>
        private void TryApplyPending()
        {
            _attempts++;

            if (_allowList == null || !_allowList._IsAllowed(_pendingSender))
            {
                if (_attempts >= _maximumAttempts)
                {
                    LogDebug($"Dropped a payload after {_attempts} attempts; its sender was never permitted.");
                    _attempts = 0;
                    _pendingState = null;
                    return;
                }

                if (!_isRetryScheduled)
                {
                    _isRetryScheduled = true;
                    SendCustomEventDelayedSeconds(nameof(_RetryApply), _retryDelaySeconds);
                }

                return;
            }

            _attempts = 0;
            _state = _pendingState;
            _pendingState = null;
            ApplyState();
        }

        /// <summary>
        /// Sends when this client's outgoing queue for the endpoint is empty, and defers when it is not.
        /// Pacing against real backlog beats pacing against a guessed interval.
        /// </summary>
        private void ScheduleSend()
        {
            if (_isSendScheduled) return;

            if (NetworkCalling.GetQueuedEvents((IUdonEventReceiver)this, nameof(_OnStateReceived)) != 0)
            {
                _isSendScheduled = true;
                SendCustomEventDelayedSeconds(nameof(_SendDeferred), SEND_RETRY_SECONDS);
                return;
            }

            // Ownership decides who answers future joiners, so the writer takes it.
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(_localPlayer, gameObject);
            }

            SendCustomNetworkEvent(NetworkEventTarget.Others, nameof(_OnStateReceived), RECEIVER_EVERYONE, _state);
        }

        /// <summary>Reacts to the applied state. Replace the body with the real effect.</summary>
        private void ApplyState()
        {
            LogDebug($"State applied: {_state}");
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
