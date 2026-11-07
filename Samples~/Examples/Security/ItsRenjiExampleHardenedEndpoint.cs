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
using VRC.SDK3.UdonNetworkCalling;

namespace ItsRenji.Examples.Security
{
    /// <summary>
    /// One network endpoint, written for the fact that ANY client in the instance can call it whenever it
    /// likes, with whatever arguments it likes, as often as it likes. A hacked client doing exactly that
    /// is in circulation, so this is the ordinary case rather than the paranoid one.
    ///
    /// The underscore prefix stops the LEGACY dispatch path reaching a method, but [NetworkCallable]
    /// re-opens it: the prefix is a default, not a lock, and an audit that greps for unprefixed methods
    /// alone misses every attributed one. What actually protects this method is the four checks below.
    ///
    /// And the rate argument is not one of them. It paces ONE SENDER, so N callers deliver up to N times
    /// the limit, and VRChat's own documentation says a rate-limited event does not prevent other queues
    /// draining. The receiver's own bounds are the only thing that caps the work.
    /// conduit-networking.md owns the full model.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class ItsRenjiExampleHardenedEndpoint : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleHardenedEndpoint]</color>";

        /// <summary>Queue depth this endpoint expects from a single sender.</summary>
        private const int MAXIMUM_QUEUED_EVENTS = 5;

        /// <summary>Highest slot index this endpoint will act on, which bounds the work it can be made to do.</summary>
        private const int MAXIMUM_SLOT = 15;

        #endregion

        #region Serialized Fields

        [Header("Permission")]
        [Tooltip("The list that decides whose calls are honoured.")]
        [SerializeField] private ItsRenjiExampleAllowList _allowList;

        [Header("Targets")]
        [Tooltip("One object per slot. The endpoint can only ever reach these, whatever it is sent.")]
        [SerializeField] private GameObject[] _slotObjects;

        [Header("Diagnostics")]
        [Tooltip("Writes refused and accepted calls to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Network Endpoint

        /// <summary>
        /// Sets a slot on or off. Every guard below is load bearing, and they are ordered cheapest first so
        /// a spamming caller is rejected before anything expensive runs.
        ///
        /// It is IDEMPOTENT by construction: being called ten times with the same arguments leaves the
        /// world exactly as one call would. That is what makes it survivable rather than merely guarded,
        /// because pacing cannot be relied on to stop repeats.
        /// </summary>
        /// <param name="slot">Slot to change. Anything out of range is refused rather than clamped.</param>
        /// <param name="isOn">The state the slot should end up in.</param>
        [NetworkCallable(MAXIMUM_QUEUED_EVENTS)]
        public void _SetSlot(int slot, bool isOn)
        {
            // 1. This method exists only to be called remotely, so a local call is a wiring mistake.
            //    A method that is ALSO called locally must do the opposite and allow the local path, or
            //    its own button is swallowed by its own guard.
            if (!NetworkCalling.InNetworkCall)
            {
                Debug.LogError($"{LOG_PREFIX} _SetSlot is a network endpoint and was called locally.", this);
                return;
            }

            // 2. The caller has to resolve before anything can be decided about them.
            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (!Utilities.IsValid(caller)) return;

            // 3. Authorize the CALLER against a list. Not against ownership, which anybody can take first.
            if (_allowList == null || !_allowList._IsAllowed(caller))
            {
                LogDebug("Refused a call from a player who is not permitted.");
                return;
            }

            // 4. Bound the arguments. The caller chooses them, so the range is the receiver's to enforce
            //    and an out-of-range value is refused rather than clamped into a neighbouring slot.
            if (slot < 0 || slot > MAXIMUM_SLOT) return;
            if (_slotObjects == null || slot >= _slotObjects.Length) return;
            if (_slotObjects[slot] == null) return;

            _slotObjects[slot].SetActive(isOn);
            LogDebug($"Slot {slot} set to {isOn}.");
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
