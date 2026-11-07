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
    /// The shape a synced toggle wants: one synced bool behind a field-change callback, visuals applied
    /// on every arrival, and side effects held back until the first sync has landed so a late joiner does
    /// not replay the world's whole history in a single frame. Conduit's rules for this subject are in
    /// conduit-networking.md.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ItsRenjiExampleSyncedToggle : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleSyncedToggle]</color>";

        #endregion

        #region Serialized Fields

        [Header("Targets")]
        [Tooltip("Objects shown while the toggle is on and hidden while it is off.")]
        [SerializeField] private GameObject[] _controlledObjects;

        [Header("Audio")]
        [Tooltip("Source for the toggle sound. Pair it with a VRC_SpatialAudioSource in the scene.")]
        [SerializeField] private AudioSource _audioSource;

        [Tooltip("Clip played when the toggle changes, once this behaviour is past its first sync.")]
        [SerializeField] private AudioClip _toggleClip;

        [Header("Diagnostics")]
        [Tooltip("Writes state changes to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Synced State

        /// <summary>Whether the toggle is on. Read and write it through the IsOn property, never directly.</summary>
        [UdonSynced, FieldChangeCallback(nameof(IsOn))]
        private bool _isOn;

        #endregion

        #region Private State

        /// <summary>True once Start has finished, so the setter cannot touch a reference it has not cached.</summary>
        private bool _isInitialized;

        /// <summary>True once this client has state of its own, which is the late-joiner boundary.</summary>
        private bool _hasReceivedState;

        #endregion

        #region Public API

        /// <summary>
        /// The toggle state. Assigning it runs the setter locally and remotely, which is why the reaction
        /// lives here rather than in OnDeserialization: the value has already landed by the time it runs.
        /// </summary>
        public bool IsOn
        {
            get => _isOn;
            set
            {
                _isOn = value;

                // An owner can serialize at about the moment a late joiner arrives, so this setter can run
                // before Start has returned. Nothing below may touch a reference Start has not cached.
                if (!_isInitialized) return;

                ApplyVisualState();

                // Skipped until this client has state of its own, or a joiner plays every toggle the world
                // has ever performed at once.
                if (_hasReceivedState)
                {
                    PlayToggleClip();
                }
            }
        }

        /// <summary>
        /// Sets the toggle from another behaviour on this client. The underscore keeps it off the legacy
        /// network-event path, so only local code can reach it.
        /// </summary>
        /// <param name="isOn">The state to move to.</param>
        public void _SetState(bool isOn)
        {
            TakeOwnership();

            // A local action is current by definition, so its side effects are wanted.
            _hasReceivedState = true;

            IsOn = isOn;
            RequestSerialization();
        }

        /// <summary>Returns the current toggle state for another behaviour on this client.</summary>
        /// <returns>True while the toggle is on.</returns>
        public bool _GetState()
        {
            return _isOn;
        }

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// Draws the authored default. A late joiner overwrites this the moment its first sync lands, and
        /// the instance creator keeps it, which is why the visual state is applied here and not left to
        /// OnDeserialization alone.
        /// </summary>
        private void Start()
        {
            ApplyVisualState();
            _isInitialized = true;
        }

        #endregion

        #region VRChat Events

        /// <summary>Toggles on interaction, taking ownership first so the write actually travels.</summary>
        public override void Interact()
        {
            _SetState(!_isOn);
        }

        /// <summary>
        /// Marks the late-joiner boundary. The value itself already arrived through the property setter,
        /// so there is nothing to apply here.
        /// </summary>
        public override void OnDeserialization()
        {
            if (!_hasReceivedState)
            {
                _hasReceivedState = true;
                LogDebug("First sync received, so side effects are live from here.");
                return;
            }

            LogDebug($"Sync received, state is {_isOn}.");
        }

        #endregion

        #region Internal

        /// <summary>Takes ownership when this client does not already hold it, so a synced write travels.</summary>
        private void TakeOwnership()
        {
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }
        }

        /// <summary>Shows or hides the controlled objects. Safe at any point, and free of side effects.</summary>
        private void ApplyVisualState()
        {
            if (_controlledObjects == null) return;

            for (int i = 0; i < _controlledObjects.Length; i++)
            {
                if (_controlledObjects[i] != null)
                {
                    _controlledObjects[i].SetActive(_isOn);
                }
            }
        }

        /// <summary>Plays the toggle clip when both the source and the clip are wired.</summary>
        private void PlayToggleClip()
        {
            if (_audioSource != null && _toggleClip != null)
            {
                _audioSource.PlayOneShot(_toggleClip);
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
