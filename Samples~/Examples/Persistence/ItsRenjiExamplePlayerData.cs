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
using VRC.SDK3.Persistence;

namespace ItsRenji.Examples.Persistence
{
    /// <summary>
    /// Saving a player's own settings across sessions. The object also needs a VRCEnablePersistence
    /// component, or nothing is stored.
    ///
    /// Two rules decide whether this works. Nothing may read or write PlayerData until OnPlayerRestored
    /// has fired: a read before that returns the default and a write is discarded, both silently, which
    /// presents as persistence that works sometimes. And the setters take NO player while the getters take
    /// one, so a read-modify-write handed the event's player argument reads THAT player and writes YOURS.
    /// The isLocal guard below is what stops one player's balance being credited from another's.
    /// conduit-udonsharp.md owns this subject, including the full list of value types.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class ItsRenjiExamplePlayerData : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExamplePlayerData]</color>";

        /// <summary>Key for the stored high score. Short keys cost less of the per-player budget.</summary>
        private const string KEY_HIGH_SCORE = "hs";

        /// <summary>Key for the stored volume preference.</summary>
        private const string KEY_VOLUME = "vol";

        /// <summary>Default high score before anything has been saved.</summary>
        private const int DEFAULT_HIGH_SCORE = 0;

        /// <summary>Default volume before anything has been saved.</summary>
        private const float DEFAULT_VOLUME = 1f;

        #endregion

        #region Serialized Fields

        [Header("Diagnostics")]
        [Tooltip("Writes loads and saves to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Private State

        /// <summary>True once the local player's stored data has arrived. Every access is gated on this.</summary>
        private bool _isDataReady;

        /// <summary>In-memory copy of the stored high score.</summary>
        private int _highScore = DEFAULT_HIGH_SCORE;

        /// <summary>In-memory copy of the stored volume.</summary>
        private float _volume = DEFAULT_VOLUME;

        #endregion

        #region VRChat Events

        /// <summary>
        /// The only safe point at which stored data may first be read. It fires for EVERY player, so the
        /// local guard is not optional.
        /// </summary>
        /// <param name="player">The player whose data was restored.</param>
        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid() || !player.isLocal) return;

            if (!PlayerData.TryGetInt(player, KEY_HIGH_SCORE, out _highScore))
            {
                _highScore = DEFAULT_HIGH_SCORE;
            }

            if (!PlayerData.TryGetFloat(player, KEY_VOLUME, out _volume))
            {
                _volume = DEFAULT_VOLUME;
            }

            _isDataReady = true;
            LogDebug($"Restored score {_highScore}, volume {_volume:F2}.");
        }

        #endregion

        #region Public API

        /// <summary>Returns whether stored data has arrived and may be used.</summary>
        /// <returns>True once OnPlayerRestored has run for the local player.</returns>
        public bool _IsDataReady()
        {
            return _isDataReady;
        }

        /// <summary>Returns the stored high score.</summary>
        /// <returns>The high score, or its default before data has arrived.</returns>
        public int _GetHighScore()
        {
            return _highScore;
        }

        /// <summary>Stores a new high score, and ignores one that is not actually higher.</summary>
        /// <param name="score">The score just achieved.</param>
        public void _SubmitScore(int score)
        {
            if (!_isDataReady) return;
            if (score <= _highScore) return;

            _highScore = score;
            PlayerData.SetInt(KEY_HIGH_SCORE, _highScore);
            LogDebug($"Stored new high score {_highScore}.");
        }

        /// <summary>Stores the player's volume preference.</summary>
        /// <param name="volume">The volume to remember.</param>
        public void _SetVolume(float volume)
        {
            if (!_isDataReady) return;

            _volume = volume;
            PlayerData.SetFloat(KEY_VOLUME, _volume);
            LogDebug($"Stored volume {_volume:F2}.");
        }

        /// <summary>Returns the stored volume preference.</summary>
        /// <returns>The volume, or its default before data has arrived.</returns>
        public float _GetVolume()
        {
            return _volume;
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
