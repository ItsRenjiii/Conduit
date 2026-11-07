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

namespace ItsRenji.Examples.Security
{
    /// <summary>
    /// Keeping a privileged role off a client that should not hold it, and the limit of being able to.
    ///
    /// Ownership decides who answers future joiners and who is treated as the writer, but it is not earned:
    /// SetOwner arbitrates nothing and succeeds locally for anybody. So the owner after a grab or after a
    /// disconnect can easily be a client with no business holding the role. This watches for that and hands
    /// ownership to a permitted player instead.
    ///
    /// READ THE LIMIT BEFORE COPYING THE PATTERN. This raises the cost of interference and is NOT access
    /// control, for two reasons that cannot be fixed here:
    ///
    /// The check is driven by the master, and master is not a trust anchor - it moves when the master
    /// leaves and nothing vouches for it - so a determined client can take ownership, become master, and
    /// drive this very path itself.
    ///
    /// And the obvious fix, having a departing owner hand the role over deliberately, DOES NOT EXIST: a
    /// client cannot send a network event as it leaves the instance, so the handover can only ever be
    /// noticed after the fact by whoever is left.
    ///
    /// Anything that genuinely must not be forged belongs off the instance entirely.
    /// conduit-networking.md owns this subject.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class ItsRenjiExampleOwnerGuard : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleOwnerGuard]</color>";

        /// <summary>How long after a change to re-check, letting the instance settle first, in seconds.</summary>
        private const float RECHECK_DELAY_SECONDS = 2f;

        #endregion

        #region Serialized Fields

        [Header("Permission")]
        [Tooltip("The list that decides who may hold this role.")]
        [SerializeField] private ItsRenjiExampleAllowList _allowList;

        [Header("Diagnostics")]
        [Tooltip("Writes ownership decisions to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Private State

        /// <summary>True while a re-check is scheduled, so events cannot stack timers.</summary>
        private bool _isRecheckScheduled;

        #endregion

        #region VRChat Events

        /// <summary>Re-checks when ownership lands somewhere new.</summary>
        /// <param name="player">The client that became owner.</param>
        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            ScheduleRecheck();
        }

        /// <summary>
        /// Re-checks when somebody leaves, because VRChat reassigns a departing owner's objects without
        /// consulting anything, and the client it picks may not be permitted.
        /// </summary>
        /// <param name="player">The player who left.</param>
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            ScheduleRecheck();
        }

        #endregion

        #region Public API

        /// <summary>
        /// Deferred re-check. Public because Udon dispatches delayed events by name; the underscore keeps
        /// a remote client from driving it.
        /// </summary>
        public void _RecheckOwner()
        {
            _isRecheckScheduled = false;

            // Only one client should act, or every client races to reassign the same object.
            if (!Networking.IsMaster) return;

            VRCPlayerApi owner = Networking.GetOwner(gameObject);
            if (!Utilities.IsValid(owner)) return;
            if (_allowList == null || _allowList._IsAllowed(owner)) return;

            VRCPlayerApi replacement = _allowList._GetAllowedPlayerInInstance();
            if (replacement == null)
            {
                // Leaving it alone is deliberate: with nobody permitted present, moving the role achieves
                // nothing and thrashing ownership is worse than an idle object.
                LogDebug("Owner is not permitted and no permitted player is present, so ownership was left alone.");
                return;
            }

            Networking.SetOwner(replacement, gameObject);
            LogDebug("Moved ownership to a permitted player.");
        }

        #endregion

        #region Internal

        /// <summary>
        /// Schedules one re-check. The delay lets a handover settle, because ownership and master can both
        /// be mid-change in the frame the event arrives, and a check run then reads values that are about
        /// to be replaced.
        /// </summary>
        private void ScheduleRecheck()
        {
            if (_isRecheckScheduled) return;

            _isRecheckScheduled = true;
            SendCustomEventDelayedSeconds(nameof(_RecheckOwner), RECHECK_DELAY_SECONDS);
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
