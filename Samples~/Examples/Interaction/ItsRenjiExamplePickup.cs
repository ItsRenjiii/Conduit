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
    /// Reacting to a pickup being grabbed, dropped and used. The object needs a Rigidbody and a Collider
    /// beside VRC_Pickup, or nothing fires at all.
    ///
    /// Ownership is the part worth reading. VRC_ObjectSync transfers ownership to the grabbing player by
    /// itself; VRC_Pickup alone does NOT, so a pickup without ObjectSync needs the explicit transfer done
    /// in OnPickup below. Leaving that call in is harmless either way, because it is a no-op when this
    /// client already owns the object.
    ///
    /// Two authored fields decide more than they look like they do. AutoHold is a four-member enum, not a
    /// checkbox, and Auto Detect resolves to no auto-hold when the pickup has no Use events. Outline
    /// Renderers decides what the hover highlight draws around, which matters whenever the grab collider
    /// is an invisible proxy rather than the visible part. conduit-udonsharp.md covers both.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class ItsRenjiExamplePickup : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExamplePickup]</color>";

        #endregion

        #region Serialized Fields

        [Header("Audio")]
        [Tooltip("Source for grab, drop and use sounds. Pair it with a VRC_SpatialAudioSource.")]
        [SerializeField] private AudioSource _audioSource;

        [Tooltip("Clip played when this player grabs the object.")]
        [SerializeField] private AudioClip _pickupClip;

        [Tooltip("Clip played when this player drops the object.")]
        [SerializeField] private AudioClip _dropClip;

        [Tooltip("Clip played when this player presses Use while holding the object.")]
        [SerializeField] private AudioClip _useClip;

        [Header("Diagnostics")]
        [Tooltip("Writes grab, drop and use events to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Private State

        /// <summary>The pickup on this object, cached once so no handler pays a lookup.</summary>
        private VRC_Pickup _pickup;

        #endregion

        #region Unity Lifecycle

        /// <summary>Caches the pickup and reports a missing one, since every handler depends on it.</summary>
        private void Start()
        {
            _pickup = (VRC_Pickup)GetComponent(typeof(VRC_Pickup));

            if (_pickup == null)
            {
                Debug.LogError($"{LOG_PREFIX} No VRC_Pickup on this object, so nothing here can run.", this);
            }
        }

        #endregion

        #region VRChat Events

        /// <summary>
        /// Runs on the grabbing player's client. Takes ownership for the case where the object carries no
        /// VRC_ObjectSync and would otherwise stay owned by somebody else while this player holds it.
        /// </summary>
        public override void OnPickup()
        {
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }

            PlayClip(_pickupClip);
            LogDebug("Picked up.");
        }

        /// <summary>
        /// Runs on the dropping player's client. Ownership is deliberately NOT handed back: the last
        /// holder stays owner until somebody else grabs it.
        /// </summary>
        public override void OnDrop()
        {
            PlayClip(_dropClip);
            LogDebug("Dropped.");
        }

        /// <summary>
        /// Runs when this player presses Use while holding. On desktop this needs AutoHold set to Yes, or
        /// the press drops the object instead of reaching here.
        /// </summary>
        public override void OnPickupUseDown()
        {
            PlayClip(_useClip);
            LogDebug("Use pressed.");
        }

        /// <summary>Runs when this player releases Use while holding.</summary>
        public override void OnPickupUseUp()
        {
            LogDebug("Use released.");
        }

        #endregion

        #region Public API

        /// <summary>Drops the object out of this player's hand, and does nothing if they are not holding it.</summary>
        public void _ForceDrop()
        {
            if (_pickup == null) return;

            _pickup.Drop();
            LogDebug("Force dropped.");
        }

        /// <summary>Returns whether this client's player is holding the object.</summary>
        /// <returns>True while held by the local player.</returns>
        public bool _IsHeld()
        {
            return _pickup != null && _pickup.IsHeld;
        }

        #endregion

        #region Internal

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
