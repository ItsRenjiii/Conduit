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
using VRC.Dynamics;

namespace ItsRenji.Examples.Contacts
{
    /// <summary>
    /// Reacting to a VRC Contact Receiver. The behaviour must sit on the SAME object as the receiver,
    /// because contact events do not bubble to a parent.
    ///
    /// There is no stay event: only enter and exit exist, so held-contact behaviour is yours to drive.
    /// This example counts overlapping senders rather than tracking a single boolean, which is what makes
    /// two hands on one receiver behave correctly instead of the first release cancelling both. For a
    /// continuous closeness value, ask CalculateProximity rather than re-testing geometry in Update.
    /// conduit-dynamics.md owns this subject, including the sixteen-tag ceiling.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiExampleContactReceiver : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleContactReceiver]</color>";

        #endregion

        #region Serialized Fields

        [Header("Targets")]
        [Tooltip("Objects shown while at least one sender is inside the volume.")]
        [SerializeField] private GameObject[] _activeObjects;

        [Header("Audio")]
        [Tooltip("Source for the contact sounds. Pair it with a VRC_SpatialAudioSource.")]
        [SerializeField] private AudioSource _audioSource;

        [Tooltip("Clip played when the first sender enters.")]
        [SerializeField] private AudioClip _enterClip;

        [Tooltip("Clip played when the last sender leaves.")]
        [SerializeField] private AudioClip _exitClip;

        [Header("Filtering")]
        [Tooltip("Accept contacts from avatars only, ignoring senders placed in the world.")]
        [SerializeField] private bool _avatarsOnly;

        [Header("Diagnostics")]
        [Tooltip("Writes contact changes to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Private State

        /// <summary>How many senders are currently inside the volume.</summary>
        private int _contactCount;

        /// <summary>Whether the visual state is currently the active one.</summary>
        private bool _isActive;

        #endregion

        #region Unity Lifecycle

        /// <summary>Applies the idle state on load.</summary>
        private void Start()
        {
            ApplyVisualState();
        }

        #endregion

        #region VRChat Events

        /// <summary>Runs once when a sender enters the volume.</summary>
        /// <param name="info">Details of the contact, including where it happened.</param>
        public override void OnContactEnter(ContactEnterInfo info)
        {
            if (!IsAccepted(info.contactSender)) return;

            _contactCount++;

            if (!_isActive)
            {
                _isActive = true;
                ApplyVisualState();
                PlayClip(_enterClip);
            }

            LogDebug($"Sender entered, count {_contactCount}.");
        }

        /// <summary>
        /// Runs once when a sender leaves. Only the exit info is available here, so anything needing the
        /// impact point or velocity has to have captured it on enter.
        /// </summary>
        /// <param name="info">Details of the departing contact.</param>
        public override void OnContactExit(ContactExitInfo info)
        {
            if (!IsAccepted(info.contactSender)) return;

            _contactCount--;

            // Clamped because an enter can be missed, and a negative count would never return to zero.
            if (_contactCount < 0)
            {
                _contactCount = 0;
            }

            if (_contactCount == 0 && _isActive)
            {
                _isActive = false;
                ApplyVisualState();
                PlayClip(_exitClip);
            }

            LogDebug($"Sender left, count {_contactCount}.");
        }

        #endregion

        #region Public API

        /// <summary>Returns whether anything is currently inside the volume.</summary>
        /// <returns>True while at least one sender overlaps.</returns>
        public bool _IsContacted()
        {
            return _isActive;
        }

        /// <summary>Returns how many senders currently overlap the volume.</summary>
        /// <returns>The current sender count.</returns>
        public int _GetContactCount()
        {
            return _contactCount;
        }

        /// <summary>
        /// Clears the count and returns to idle. Worth calling from OnPlayerLeft, since a player who
        /// disconnects mid-contact never produces the exit that would have balanced their enter.
        /// </summary>
        public void _ResetContacts()
        {
            _contactCount = 0;

            if (_isActive)
            {
                _isActive = false;
                ApplyVisualState();
            }
        }

        #endregion

        #region Internal

        /// <summary>
        /// Answers whether a sender should count. The proxy is checked for validity first, because it is a
        /// lightweight stand-in rather than a component and can be stale.
        /// </summary>
        /// <param name="sender">The sender that raised the event.</param>
        /// <returns>True when the contact should be acted on.</returns>
        private bool IsAccepted(ContactSenderProxy sender)
        {
            if (!sender.isValid) return false;

            return !_avatarsOnly || sender.usage == DynamicsUsage.Avatar;
        }

        /// <summary>Shows or hides the target objects for the current state.</summary>
        private void ApplyVisualState()
        {
            if (_activeObjects == null) return;

            for (int i = 0; i < _activeObjects.Length; i++)
            {
                if (_activeObjects[i] != null)
                {
                    _activeObjects[i].SetActive(_isActive);
                }
            }
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
