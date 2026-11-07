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

namespace ItsRenji.Inspector.Demo
{
    /// <summary>
    /// Fields that appear, vanish or grey out according to the value of another field. One of a set of
    /// demos, each covering a single concern. Nothing here runs in a world.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiDemoConditions : UdonSharpBehaviour
    {
        #region Switches

        /// <summary>Drives the three fields that react to a plain switch.</summary>
        [Card("Switches")]
        [Note("Everything under Results reacts to these two while you edit them.")]
        public bool advanced;

        /// <summary>Drives the field that reacts to one particular value.</summary>
        [Card("Switches")]
        [Choices("Simple", "Balanced", "Expert")]
        public int mode;

        #endregion

        #region Results

        /// <summary>Appears only while the switch above is on.</summary>
        [Card("Results")]
        [ShowIf("advanced")]
        public float shownWhileAdvanced = 1f;

        /// <summary>Disappears while the switch above is on.</summary>
        [Card("Results")]
        [HideIf("advanced")]
        public float hiddenWhileAdvanced = 1f;

        /// <summary>Stays in place but greys out while the switch above is off.</summary>
        [Card("Results")]
        [EnableIf("advanced")]
        public float editableWhileAdvanced = 1f;

        /// <summary>Appears only at one particular value of the choice above.</summary>
        [Card("Results")]
        [ShowIf("mode", 2)]
        public float shownInExpertMode = 1f;

        #endregion
    }
}
