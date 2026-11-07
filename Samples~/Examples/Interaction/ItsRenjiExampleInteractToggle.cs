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

namespace ItsRenji.Examples.Interaction
{
    /// <summary>
    /// The smallest useful Interact handler: a local toggle with a cooldown so a held click cannot fire it
    /// repeatedly. Nothing here syncs, which is the right default for a purely cosmetic control such as a
    /// personal light or a private view. The moment two players must agree on the result, this becomes a
    /// synced behaviour instead: see the Sync examples and conduit-networking.md.
    ///
    /// Interact fires only on the client that pressed it, so an effect written here is that player's own.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiExampleInteractToggle : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleInteractToggle]</color>";

        #endregion

        #region Serialized Fields

        [Header("Targets")]
        [Tooltip("Objects shown while the toggle is on and hidden while it is off.")]
        [SerializeField] private GameObject[] _targetObjects;

        [Header("Audio")]
        [Tooltip("Source for the interaction sound. Pair it with a VRC_SpatialAudioSource in the scene.")]
        [SerializeField] private AudioSource _audioSource;

        [Tooltip("Clip played on each accepted interaction.")]
        [SerializeField] private AudioClip _interactClip;

        [Header("Timing")]
        [Tooltip("Shortest gap between two accepted interactions, in seconds.")]
        [SerializeField] private float _cooldownSeconds = 0.5f;

        [Header("Diagnostics")]
        [Tooltip("Writes each accepted interaction to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Private State

        /// <summary>Whether the toggle is currently on.</summary>
        private bool _isOn;

        /// <summary>When the last accepted interaction happened, as a Time.time stamp.</summary>
        private float _lastInteractTime;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// Applies the starting state, and pushes the cooldown stamp back so the first press is accepted.
        /// A stamp left at zero is compared against a Time.time that also starts near zero, which silently
        /// swallows every press for the first cooldown after the world loads.
        /// </summary>
        private void Start()
        {
            _lastInteractTime = -_cooldownSeconds;
            ApplyState();
        }

        #endregion

        #region VRChat Events

        /// <summary>Toggles for the pressing player, once the cooldown has elapsed.</summary>
        public override void Interact()
        {
            if (Time.time - _lastInteractTime < _cooldownSeconds) return;

            _lastInteractTime = Time.time;
            _isOn = !_isOn;

            ApplyState();
            PlayInteractClip();
            LogDebug($"Toggled to {_isOn}.");
        }

        #endregion

        #region Public API

        /// <summary>Sets the toggle from another behaviour on this client.</summary>
        /// <param name="isOn">The state to move to.</param>
        public void _SetState(bool isOn)
        {
            _isOn = isOn;
            ApplyState();
        }

        /// <summary>Returns the current toggle state.</summary>
        /// <returns>True while the toggle is on.</returns>
        public bool _GetState()
        {
            return _isOn;
        }

        #endregion

        #region Internal

        /// <summary>Shows or hides the target objects.</summary>
        private void ApplyState()
        {
            if (_targetObjects == null) return;

            for (int i = 0; i < _targetObjects.Length; i++)
            {
                if (_targetObjects[i] != null)
                {
                    _targetObjects[i].SetActive(_isOn);
                }
            }
        }

        /// <summary>Plays the interaction clip when both the source and the clip are wired.</summary>
        private void PlayInteractClip()
        {
            if (_audioSource != null && _interactClip != null)
            {
                _audioSource.PlayOneShot(_interactClip);
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
