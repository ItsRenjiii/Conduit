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

namespace ItsRenji.Examples.Patterns
{
    /// <summary>
    /// One-to-many local notification, standing in for the C# events and delegates Udon does not have.
    /// Listeners register themselves and implement a method by name; raising walks the list and calls it.
    ///
    /// Everything here is LOCAL. Nothing crosses the network, so this is not a substitute for a synced
    /// field, and a listener reacting to a world-state change should be reading synced state rather than
    /// waiting to be told (conduit-networking.md).
    ///
    /// The re-entrancy guard is the part worth copying. A handler that registers, unregisters or raises
    /// during a raise would mutate the list mid-walk, and Udon keeps locals on a shared heap with no call
    /// stack, so the nested call overwrites the outer one's variables rather than merely confusing it.
    /// Dispatch is by name and therefore unchecked, so a renamed handler fails silently.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiExampleEventBus : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's gated logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleEventBus]</color>";

        /// <summary>How many listeners the bus can hold.</summary>
        private const int MAXIMUM_LISTENERS = 32;

        #endregion

        #region Serialized Fields

        [Header("Diagnostics")]
        [Tooltip("Writes registrations and raises to the log. Leave this off for release.")]
        [SerializeField] private bool _enableDebugLogging;

        #endregion

        #region Private State

        /// <summary>Registered listeners, packed into the first entries of the array.</summary>
        private UdonSharpBehaviour[] _listeners;

        /// <summary>How many entries of the listener array are in use.</summary>
        private int _listenerCount;

        /// <summary>True while a raise is walking the list, which makes mutation unsafe.</summary>
        private bool _isRaising;

        #endregion

        #region Unity Lifecycle

        /// <summary>Allocates the listener array once, so registration never allocates.</summary>
        private void Start()
        {
            _listeners = new UdonSharpBehaviour[MAXIMUM_LISTENERS];
            _listenerCount = 0;
        }

        #endregion

        #region Public API

        /// <summary>Adds a listener, ignoring a null, a duplicate, or an add made during a raise.</summary>
        /// <param name="listener">Behaviour to notify.</param>
        public void _Register(UdonSharpBehaviour listener)
        {
            if (listener == null || _listeners == null) return;
            if (_isRaising) return;
            if (_listenerCount >= MAXIMUM_LISTENERS) return;

            for (int i = 0; i < _listenerCount; i++)
            {
                if (_listeners[i] == listener) return;
            }

            _listeners[_listenerCount] = listener;
            _listenerCount++;
            LogDebug($"Registered, {_listenerCount} listener(s).");
        }

        /// <summary>Removes a listener, closing the gap so the array stays packed.</summary>
        /// <param name="listener">Behaviour to stop notifying.</param>
        public void _Unregister(UdonSharpBehaviour listener)
        {
            if (listener == null || _listeners == null) return;
            if (_isRaising) return;

            for (int i = 0; i < _listenerCount; i++)
            {
                if (_listeners[i] != listener) continue;

                for (int j = i; j < _listenerCount - 1; j++)
                {
                    _listeners[j] = _listeners[j + 1];
                }

                _listenerCount--;
                _listeners[_listenerCount] = null;
                LogDebug($"Unregistered, {_listenerCount} listener(s).");
                return;
            }
        }

        /// <summary>
        /// Calls a named method on every listener. A listener that has been destroyed is skipped and
        /// dropped, so the list cleans itself rather than accumulating dead entries.
        /// </summary>
        /// <param name="eventName">Name of the public method each listener implements.</param>
        public void _Raise(string eventName)
        {
            if (_listeners == null || _isRaising) return;

            _isRaising = true;

            int writeIndex = 0;
            for (int i = 0; i < _listenerCount; i++)
            {
                UdonSharpBehaviour listener = _listeners[i];
                if (listener == null) continue;

                _listeners[writeIndex] = listener;
                writeIndex++;
                listener.SendCustomEvent(eventName);
            }

            for (int i = writeIndex; i < _listenerCount; i++)
            {
                _listeners[i] = null;
            }

            _listenerCount = writeIndex;
            _isRaising = false;
        }

        /// <summary>Returns how many listeners are currently registered.</summary>
        /// <returns>The listener count.</returns>
        public int _GetListenerCount()
        {
            return _listenerCount;
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
