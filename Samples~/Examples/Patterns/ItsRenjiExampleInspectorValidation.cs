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
    /// Catching an unwired reference while the object is being authored rather than on the next play.
    ///
    /// Udon never dispatches OnValidate: UdonSharp builds its event table from Udon's own event node
    /// definitions and there is no entry for it, so the method runs only as plain C# on the proxy, only in
    /// the editor, and never in a built world. That is exactly what makes the guard below worth having,
    /// and it is the one place the guard keeps a member OUT of the program rather than hiding a type Udon
    /// cannot represent. Unguarded, the method compiles into every program as code nothing can reach.
    ///
    /// Start still does its own checking, because none of this survives into the world.
    /// conduit-udonsharp.md covers the guard and conduit-editors.md the reporting convention.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiExampleInspectorValidation : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>Colour-coded console prefix for this example's logs.</summary>
        private const string LOG_PREFIX = "<color=#19EFF3>[ExampleInspectorValidation]</color>";

        /// <summary>Fastest the target may be driven, which the inspector is held to.</summary>
        private const float MAXIMUM_SPEED = 25f;

        #endregion

        #region Serialized Fields

        /// <summary>The object this behaviour drives, and the one reference it cannot run without.</summary>
        [Tooltip("The object this behaviour drives.")]
        [SerializeField] private Transform _target;

        /// <summary>How fast the target is driven, in units per second.</summary>
        [Tooltip("How fast the target is driven, in units per second.")]
        [SerializeField] private float _speed = 5f;

        #endregion

        #region Unity Lifecycle

        /// <summary>
        /// Fails loudly on a missing reference. Still required alongside the editor check below, because
        /// nothing editor-only reaches an uploaded world.
        /// </summary>
        private void Start()
        {
            if (_target == null)
            {
                Debug.LogError($"{LOG_PREFIX} No target assigned, so this behaviour does nothing.", this);
            }
        }

        #endregion

#if !COMPILER_UDONSHARP && UNITY_EDITOR
        #region Editor Only

        /// <summary>
        /// Holds the authored values to what the behaviour can use, and says so while the author is still
        /// wiring the object. Passing this as the context argument makes clicking the console entry select
        /// the offending object, which is the whole point of reporting at author time.
        /// </summary>
        private void OnValidate()
        {
            if (_speed < 0f)
            {
                _speed = 0f;
            }
            else if (_speed > MAXIMUM_SPEED)
            {
                _speed = MAXIMUM_SPEED;
            }

            if (_target == null)
            {
                Debug.LogWarning($"{LOG_PREFIX} {name} has no target assigned.", this);
            }
        }

        #endregion
#endif
    }
}
