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

namespace ItsRenji.Examples.Security
{
    /// <summary>
    /// The root of trust the rest of this folder builds on: which players may drive privileged behaviour.
    /// Every other example here asks this one, rather than each carrying its own idea of who counts.
    ///
    /// The list is AUTHORED and never changes at runtime, which is a deliberate simplification and also
    /// the most defensible version. It is baked into the world, so it is identical on every client, it is
    /// available in Start with nothing to wait for, and there is no write path for anybody to attack. A
    /// RUNTIME-EDITABLE list is a materially harder problem and is the reason the neighbouring examples
    /// exist: it becomes synced state, so it arrives late on a joiner and has to be validated, and the
    /// question of who may edit the list is itself the same question one level down.
    ///
    /// This is not identity. A display name is user-changeable, so an authored list is a convenience
    /// for a small known group, never proof of who somebody is. Anything that genuinely must not be forged
    /// belongs off the instance. conduit-networking.md covers why, and what this can and cannot carry.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiExampleAllowList : UdonSharpBehaviour
    {
        #region Serialized Fields

        [Header("Permission")]
        [Tooltip("Display names permitted to drive behaviour that asks this list. Authored, never edited at runtime.")]
        [SerializeField] private string[] _allowedDisplayNames;

        #endregion

        #region Public API

        /// <summary>Answers whether a player is on the list.</summary>
        /// <param name="player">Player to test. An invalid player is never allowed.</param>
        /// <returns>True when the player is permitted.</returns>
        public bool _IsAllowed(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || _allowedDisplayNames == null) return false;

            for (int i = 0; i < _allowedDisplayNames.Length; i++)
            {
                if (_allowedDisplayNames[i] == player.displayName) return true;
            }

            return false;
        }

        /// <summary>Answers whether this client's own player is on the list.</summary>
        /// <returns>True when the local player is permitted.</returns>
        public bool _IsLocalAllowed()
        {
            return _IsAllowed(Networking.LocalPlayer);
        }

        /// <summary>
        /// Finds any permitted player currently in the instance. Used when a privileged role has to be
        /// handed to somebody and the current holder does not qualify.
        /// </summary>
        /// <returns>A permitted player, or null when none is present.</returns>
        public VRCPlayerApi _GetAllowedPlayerInInstance()
        {
            int playerCount = VRCPlayerApi.GetPlayerCount();
            VRCPlayerApi[] players = new VRCPlayerApi[playerCount];
            VRCPlayerApi.GetPlayers(players);

            for (int i = 0; i < players.Length; i++)
            {
                if (_IsAllowed(players[i])) return players[i];
            }

            return null;
        }

        #endregion
    }
}
