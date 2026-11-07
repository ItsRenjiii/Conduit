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
    /// Sending to ONE player when the platform has no way to address one.
    ///
    /// SendCustomNetworkEvent targets only All, Owner, Others and Self. There is no "this specific
    /// player" and no overload that takes a VRCPlayerApi, so the only route is to BROADCAST and have every
    /// client except the intended one throw the message away. Carry the recipient's playerId as the first
    /// parameter, use a sentinel for a genuine broadcast, and return immediately when neither matches.
    ///
    /// The cost is honest and worth stating: every client in the instance receives and discards the
    /// message so that one client can act on it. That makes this right for rare, small messages - handing
    /// a late joiner the current state, answering one player's request - and wrong for anything frequent.
    ///
    /// This is also the piece that pays for choosing network events over synced variables: a synced
    /// variable replays to a late joiner for free, and an event never does, so the state has to be sent
    /// deliberately. conduit-networking.md covers that trade.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class ItsRenjiExampleTargetedMessage : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleTargetedMessage]</color>";

        /// <summary>Queue depth this endpoint expects from a single sender.</summary>
        private const int MAXIMUM_QUEUED_EVENTS = 5;

        /// <summary>Recipient id meaning every client should act, rather than exactly one.</summary>
        private const int RECIPIENT_EVERYONE = -1;

        #endregion

        #region Serialized Fields

        [Header("Diagnostics")]
        [Tooltip("Writes sent and received messages to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Private State

        /// <summary>The message this client currently holds.</summary>
        private string _message = string.Empty;

        /// <summary>The local player, cached once.</summary>
        private VRCPlayerApi _localPlayer;

        #endregion

        #region Unity Lifecycle

        /// <summary>Caches the local player, which every recipient test needs.</summary>
        private void Start()
        {
            _localPlayer = Networking.LocalPlayer;
        }

        #endregion

        #region Public API

        /// <summary>Sends to every client in the instance.</summary>
        /// <param name="message">The message to publish.</param>
        public void _SendToEveryone(string message)
        {
            _message = message;
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(_OnMessage), RECIPIENT_EVERYONE, message);
        }

        /// <summary>
        /// Sends to one player. Everyone receives it; everyone but the recipient discards it.
        /// </summary>
        /// <param name="player">The player who should act on it.</param>
        /// <param name="message">The message to deliver.</param>
        public void _SendToPlayer(VRCPlayerApi player, string message)
        {
            if (!Utilities.IsValid(player)) return;

            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(_OnMessage), player.playerId, message);
            LogDebug($"Broadcast a message addressed to one player.");
        }

        /// <summary>Returns the message this client last accepted.</summary>
        /// <returns>The current message.</returns>
        public string _GetMessage()
        {
            return _message;
        }

        #endregion

        #region Network Endpoint

        /// <summary>
        /// Receives a message and acts only when it is addressed here. The recipient test comes before any
        /// work, so a targeted message costs every other client nothing but the delivery itself.
        /// </summary>
        /// <param name="recipientId">Player id to act, or RECIPIENT_EVERYONE for a broadcast.</param>
        /// <param name="message">The message being delivered.</param>
        [NetworkCallable(MAXIMUM_QUEUED_EVENTS)]
        public void _OnMessage(int recipientId, string message)
        {
            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (NetworkCalling.InNetworkCall && !Utilities.IsValid(caller)) return;

            if (recipientId != RECIPIENT_EVERYONE && recipientId != _localPlayer.playerId) return;

            _message = message;
            LogDebug($"Accepted a message addressed to this client.");
        }

        #endregion

        #region Internal

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
