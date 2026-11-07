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
    /// Carries three independent values in one synced Vector3. Every synced field pays its own header, so
    /// removing fields saves more than narrowing their types does, and three small integers travel as one
    /// field rather than three. A float's 24-bit mantissa holds every integer up to 16,777,216 exactly,
    /// which is what makes the round trip lossless. conduit-networking.md owns the budget rules.
    ///
    /// Do not pack a value that needs interpolation: the components interpolate independently and every
    /// intermediate frame is meaningless.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ItsRenjiExamplePackedSync : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExamplePackedSync]</color>";

        #endregion

        #region Serialized Fields

        [Header("Diagnostics")]
        [Tooltip("Writes each unpacked value set to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Synced State

        /// <summary>The three values below, packed one per component.</summary>
        [UdonSynced] private Vector3 _packedState;

        #endregion

        #region Private State

        /// <summary>Index of the current question.</summary>
        private int _questionIndex;

        /// <summary>Which game mode is running, as a small enumerated value.</summary>
        private int _gameType;

        /// <summary>Which category the current question belongs to.</summary>
        private int _category;

        #endregion

        #region Public API

        /// <summary>
        /// Sets all three values and syncs them as one field. Ownership is taken first, because a synced
        /// write from a non-owner never leaves that client.
        /// </summary>
        /// <param name="questionIndex">Index of the question to move to.</param>
        /// <param name="gameType">The game mode to run.</param>
        /// <param name="category">The category the question belongs to.</param>
        public void _SetQuestion(int questionIndex, int gameType, int category)
        {
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }

            _questionIndex = questionIndex;
            _gameType = gameType;
            _category = category;

            ApplyState();
            RequestSerialization();
        }

        /// <summary>Returns the current question index.</summary>
        /// <returns>The index last set or received.</returns>
        public int _GetQuestionIndex()
        {
            return _questionIndex;
        }

        #endregion

        #region VRChat Events

        /// <summary>Packs the three values into the synced vector before the packet is built.</summary>
        public override void OnPreSerialization()
        {
            _packedState = new Vector3(_questionIndex, _gameType, _category);
        }

        /// <summary>Unpacks the vector back into the three values, then reacts to them.</summary>
        public override void OnDeserialization()
        {
            // RoundToInt absorbs the floating-point noise that a packed integer can pick up in transit.
            _questionIndex = Mathf.RoundToInt(_packedState.x);
            _gameType = Mathf.RoundToInt(_packedState.y);
            _category = Mathf.RoundToInt(_packedState.z);

            ApplyState();
        }

        #endregion

        #region Internal

        /// <summary>Reacts to the current values. Replace the body with the real effect.</summary>
        private void ApplyState()
        {
            LogDebug($"Question {_questionIndex}, type {_gameType}, category {_category}.");
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
