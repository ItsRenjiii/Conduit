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

namespace ItsRenji.Examples.Interaction
{
    /// <summary>
    /// Seating a player, and the two traps that come with it.
    ///
    /// OnStationExited is NOT guaranteed when a seated player leaves the instance, so any state keyed on
    /// an occupant leaks unless OnPlayerLeft clears it as well. The pair is mandatory: the station event
    /// for the ordinary path, the leave event as the failsafe. This example tracks occupancy through both.
    ///
    /// The second trap is not visible here but decides how a seat is built: a seated player's PlayerLocal
    /// capsule is effectively disabled, so player trigger volumes never fire for them, and OnPlayerTriggerExit
    /// fires when somebody sits down INSIDE a zone. Ask the position rather than the trigger.
    /// conduit-player.md owns both, and conduit-scene-geometry.md covers the volume side.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class ItsRenjiExampleStation : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleStation]</color>";

        /// <summary>Marks that no player is seated.</summary>
        private const int NO_OCCUPANT = -1;

        #endregion

        #region Serialized Fields

        [Header("Station")]
        [Tooltip("The station to drive. It must sit on this object or a child of it.")]
        [SerializeField] private VRCStation _station;

        [Tooltip("Immobilize for a seat, Mobile to let the player walk, ImmobilizeForVehicle when the seat moves.")]
        [SerializeField] private VRCStation.Mobility _mobility = VRCStation.Mobility.Immobilize;

        [Header("Audio")]
        [Tooltip("Source for the enter and exit sounds. Pair it with a VRC_SpatialAudioSource.")]
        [SerializeField] private AudioSource _audioSource;

        [Tooltip("Clip played when a player sits down.")]
        [SerializeField] private AudioClip _enterClip;

        [Tooltip("Clip played when a player stands up.")]
        [SerializeField] private AudioClip _exitClip;

        [Header("Diagnostics")]
        [Tooltip("Writes seat changes to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Private State

        /// <summary>Player id of the occupant, or NO_OCCUPANT. Local to this client, not synced.</summary>
        private int _occupantId = NO_OCCUPANT;

        #endregion

        #region Unity Lifecycle

        /// <summary>Applies the authored mobility, and reports a missing station rather than failing quietly.</summary>
        private void Start()
        {
            if (_station == null)
            {
                Debug.LogError($"{LOG_PREFIX} No station assigned, so nothing here can run.", this);
                return;
            }

            _station.PlayerMobility = _mobility;
        }

        #endregion

        #region VRChat Events

        /// <summary>Seats the pressing player.</summary>
        public override void Interact()
        {
            if (_station == null) return;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid()) return;

            _station.UseStation(localPlayer);
        }

        /// <summary>Runs on every client when any player sits down.</summary>
        /// <param name="player">The player who sat down.</param>
        public override void OnStationEntered(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid()) return;

            _occupantId = player.playerId;
            PlayClip(_enterClip);
            LogDebug($"Seated, local: {player.isLocal}.");
        }

        /// <summary>Runs on every client when any player stands up, for the ordinary path only.</summary>
        /// <param name="player">The player who stood up.</param>
        public override void OnStationExited(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid()) return;
            if (player.playerId != _occupantId) return;

            ClearOccupant();
            PlayClip(_exitClip);
            LogDebug("Stood up.");
        }

        /// <summary>
        /// The failsafe half of the pair. A player who disconnects while seated may never produce an exit
        /// event, which would leave this seat marked occupied for the rest of the session.
        /// </summary>
        /// <param name="player">The player who left the instance.</param>
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null) return;
            if (player.playerId != _occupantId) return;

            ClearOccupant();
            LogDebug("Occupant left the instance, so the seat was released.");
        }

        #endregion

        #region Public API

        /// <summary>Returns whether anybody is seated, as far as this client has been told.</summary>
        /// <returns>True while the seat is believed occupied.</returns>
        public bool _IsOccupied()
        {
            return _occupantId != NO_OCCUPANT;
        }

        /// <summary>
        /// Stands the LOCAL player up. ExitStation acts on the calling client's own player, so ejecting
        /// somebody else means sending them an event and letting their client make this call.
        /// </summary>
        public void _ExitLocalPlayer()
        {
            if (_station == null) return;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid()) return;

            _station.ExitStation(localPlayer);
        }

        #endregion

        #region Internal

        /// <summary>Marks the seat empty.</summary>
        private void ClearOccupant()
        {
            _occupantId = NO_OCCUPANT;
        }

        /// <summary>Plays a clip when both the source and the clip are wired.</summary>
        /// <param name="clip">Clip to play.</param>
        private void PlayClip(AudioClip clip)
        {
            if (_audioSource != null && clip != null)
            {
                _audioSource.PlayOneShot(clip);
            }
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
