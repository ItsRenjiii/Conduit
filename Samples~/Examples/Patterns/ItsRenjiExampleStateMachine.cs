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

namespace ItsRenji.Examples.Patterns
{
    /// <summary>
    /// A timed state machine that every client agrees on: Idle, then Active for a while, then Cooldown,
    /// then Idle again. The owner drives the timers and the state travels as a synced value, so nobody
    /// re-derives the sequence locally and drifts.
    ///
    /// Three things make it survive a real instance. The delayed callbacks re-check both ownership and the
    /// current state, because a delayed event always fires and cannot be cancelled: the control is a guard
    /// on arrival, not a cancellation. Side effects are held back until this client has state of its own,
    /// so a late joiner sees the right visuals without hearing every transition it missed. And a new owner
    /// resumes the timer after a handover, or the machine would stall in a timed state forever.
    ///
    /// Where a callback must genuinely NOT run, a soft guard is the wrong tool: VRCTween.DelayedCall
    /// returns a handle that can be killed (conduit-performance.md, conduit-vrctween.md).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class ItsRenjiExampleStateMachine : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleStateMachine]</color>";

        /// <summary>Resting state, waiting to be triggered.</summary>
        private const int STATE_IDLE = 0;

        /// <summary>Running state, which ends on its own timer.</summary>
        private const int STATE_ACTIVE = 1;

        /// <summary>Recovery state, which returns to idle on its own timer.</summary>
        private const int STATE_COOLDOWN = 2;

        #endregion

        #region Serialized Fields

        [Header("Timing")]
        [Tooltip("How long the active state lasts, in seconds.")]
        [SerializeField] private float _activeDuration = 3f;

        [Tooltip("How long the cooldown lasts before the machine can run again, in seconds.")]
        [SerializeField] private float _cooldownDuration = 2f;

        [Header("Visuals")]
        [Tooltip("Objects shown while idle.")]
        [SerializeField] private GameObject[] _idleObjects;

        [Tooltip("Objects shown while active.")]
        [SerializeField] private GameObject[] _activeObjects;

        [Tooltip("Objects shown while cooling down.")]
        [SerializeField] private GameObject[] _cooldownObjects;

        [Header("Audio")]
        [Tooltip("Source for the transition sounds. Pair it with a VRC_SpatialAudioSource.")]
        [SerializeField] private AudioSource _audioSource;

        [Tooltip("Clip played on entering the active state.")]
        [SerializeField] private AudioClip _activateClip;

        [Header("Diagnostics")]
        [Tooltip("Writes transitions to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Synced State

        /// <summary>Which state the machine is in, shared by every client.</summary>
        [UdonSynced] private int _currentState = STATE_IDLE;

        #endregion

        #region Private State

        /// <summary>True once Start has finished.</summary>
        private bool _isInitialized;

        /// <summary>True once this client has state of its own, which is the late-joiner boundary.</summary>
        private bool _hasReceivedState;

        /// <summary>True while a transition timer is outstanding, so handovers cannot stack timers.</summary>
        private bool _isTimerPending;

        #endregion

        #region Unity Lifecycle

        /// <summary>Draws the starting state without playing anything.</summary>
        private void Start()
        {
            ApplyVisuals();
            _isInitialized = true;
        }

        #endregion

        #region Public API

        /// <summary>Starts the machine, and does nothing unless it is currently idle.</summary>
        public void _Activate()
        {
            if (_currentState != STATE_IDLE) return;

            TakeOwnership();
            _hasReceivedState = true;
            EnterState(STATE_ACTIVE);
        }

        /// <summary>Forces the machine back to idle from any state.</summary>
        public void _ForceIdle()
        {
            TakeOwnership();
            _isTimerPending = false;
            EnterState(STATE_IDLE);
        }

        /// <summary>Returns the current state as one of the state constants.</summary>
        /// <returns>The current state.</returns>
        public int _GetState()
        {
            return _currentState;
        }

        /// <summary>
        /// Ends the active state. Public because Udon dispatches delayed events by name; it re-checks
        /// everything because a delayed event always arrives, even when it is no longer wanted.
        /// </summary>
        public void _OnActiveElapsed()
        {
            if (!Networking.IsOwner(gameObject)) return;
            if (_currentState != STATE_ACTIVE) return;

            _isTimerPending = false;
            EnterState(STATE_COOLDOWN);
        }

        /// <summary>Ends the cooldown, under the same re-checks as the active timer.</summary>
        public void _OnCooldownElapsed()
        {
            if (!Networking.IsOwner(gameObject)) return;
            if (_currentState != STATE_COOLDOWN) return;

            _isTimerPending = false;
            EnterState(STATE_IDLE);
        }

        #endregion

        #region VRChat Events

        /// <summary>Starts the machine on interaction.</summary>
        public override void Interact()
        {
            _Activate();
        }

        /// <summary>Applies the received state, holding back audio until this client is current.</summary>
        public override void OnDeserialization()
        {
            bool isFirstSync = !_hasReceivedState;
            _hasReceivedState = true;

            ApplyVisuals();

            if (!isFirstSync)
            {
                PlayTransitionClip();
            }

            LogDebug(isFirstSync ? "First sync applied, audio suppressed." : $"State {_currentState} applied.");
        }

        /// <summary>
        /// Picks up a timer the previous owner was running. Without this the machine stalls in a timed
        /// state, because the departed owner's delayed event went with them.
        /// </summary>
        /// <param name="player">The client that became owner.</param>
        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            if (!Networking.IsOwner(gameObject) || _isTimerPending) return;

            if (_currentState == STATE_ACTIVE)
            {
                _isTimerPending = true;
                SendCustomEventDelayedSeconds(nameof(_OnActiveElapsed), _activeDuration);
                LogDebug("Resumed the active timer after a handover.");
            }
            else if (_currentState == STATE_COOLDOWN)
            {
                _isTimerPending = true;
                SendCustomEventDelayedSeconds(nameof(_OnCooldownElapsed), _cooldownDuration);
                LogDebug("Resumed the cooldown timer after a handover.");
            }
        }

        #endregion

        #region Internal

        /// <summary>Moves to a state, publishes it, and schedules whatever leaves it again.</summary>
        /// <param name="state">The state to enter.</param>
        private void EnterState(int state)
        {
            _currentState = state;
            RequestSerialization();

            ApplyVisuals();
            PlayTransitionClip();
            LogDebug($"Entered state {state}.");

            if (state == STATE_ACTIVE)
            {
                _isTimerPending = true;
                SendCustomEventDelayedSeconds(nameof(_OnActiveElapsed), _activeDuration);
            }
            else if (state == STATE_COOLDOWN)
            {
                _isTimerPending = true;
                SendCustomEventDelayedSeconds(nameof(_OnCooldownElapsed), _cooldownDuration);
            }
            else
            {
                _isTimerPending = false;
            }
        }

        /// <summary>Takes ownership when this client does not already hold it.</summary>
        private void TakeOwnership()
        {
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }
        }

        /// <summary>Shows the objects belonging to the current state and hides the rest.</summary>
        private void ApplyVisuals()
        {
            SetActive(_idleObjects, _currentState == STATE_IDLE);
            SetActive(_activeObjects, _currentState == STATE_ACTIVE);
            SetActive(_cooldownObjects, _currentState == STATE_COOLDOWN);
        }

        /// <summary>Sets the active state of every non-null entry in an array.</summary>
        /// <param name="objects">Objects to switch.</param>
        /// <param name="isActive">Whether they should be on.</param>
        private void SetActive(GameObject[] objects, bool isActive)
        {
            if (objects == null) return;

            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] != null)
                {
                    objects[i].SetActive(isActive);
                }
            }
        }

        /// <summary>Plays the transition clip when entering the active state.</summary>
        private void PlayTransitionClip()
        {
            if (!_isInitialized) return;
            if (_currentState != STATE_ACTIVE) return;
            if (_audioSource == null || _activateClip == null) return;

            _audioSource.PlayOneShot(_activateClip);
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
