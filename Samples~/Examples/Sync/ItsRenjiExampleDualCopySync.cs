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
    /// Keeps the value the world reads separate from the value the network carries, so game logic never
    /// has to ask who owns the object. The local copy is read and written freely; the synced copy is
    /// touched only in the two serialization hooks, which makes every networking line in the file
    /// auditable in one place. conduit-networking.md covers why a non-owner write is silently discarded.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ItsRenjiExampleDualCopySync : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleDualCopySync]</color>";

        #endregion

        #region Serialized Fields

        [Header("Diagnostics")]
        [Tooltip("Writes each applied value to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Synced State

        /// <summary>Transport copy. Written only in OnPreSerialization and read only in OnDeserialization.</summary>
        [UdonSynced] private float _syncedVolume;

        #endregion

        #region Private State

        /// <summary>The value every reader in the world uses, on owner and non-owner alike.</summary>
        private float _volume = 0.5f;

        /// <summary>True when the local copy has moved since the last packet was built.</summary>
        private bool _isDirty;

        #endregion

        #region Public API

        /// <summary>
        /// Moves the volume and asks for a sync. Ownership is taken first, because a synced write from a
        /// non-owner changes only that client and is replaced the moment the real owner serializes.
        /// </summary>
        /// <param name="volume">The new volume.</param>
        public void _SetVolume(float volume)
        {
            if (Mathf.Approximately(volume, _volume)) return;

            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }

            _volume = volume;
            _isDirty = true;
            ApplyVolume();
            RequestSerialization();
        }

        /// <summary>Returns the value the world should be reading.</summary>
        /// <returns>The current local volume.</returns>
        public float _GetVolume()
        {
            return _volume;
        }

        #endregion

        #region VRChat Events

        /// <summary>Copies local to synced immediately before the packet is built.</summary>
        public override void OnPreSerialization()
        {
            if (!_isDirty) return;

            _syncedVolume = _volume;
            _isDirty = false;
        }

        /// <summary>Copies synced to local so the rest of the world reads the new value.</summary>
        public override void OnDeserialization()
        {
            _volume = _syncedVolume;
            ApplyVolume();
        }

        #endregion

        #region Internal

        /// <summary>Drives whatever the value controls. Replace the body with the real effect.</summary>
        private void ApplyVolume()
        {
            LogDebug($"Volume applied: {_volume:F2}");
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
