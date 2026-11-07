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
    /// The panel reading the behaviour back while you edit it: note text and units worked out by a
    /// method, slider ends taken from other fields, a method run on every edit, and members that are
    /// not fields at all. One of a set of demos, each covering a single concern.
    /// <para>
    /// A dollar sign in front of a name in an attribute means "read this off the behaviour" rather
    /// than "use this text", which is what lets one note say two different things.
    /// </para>
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiDemoReactive : UdonSharpBehaviour
    {
        #region Text worked out while you look

        /// <summary>Carries a note whose text the behaviour works out.</summary>
        [Card("Text worked out while you look")]
        [Title("Read off the behaviour")]
        [Note("$DescribeState")]
        public bool reactiveSwitch = true;

        /// <summary>Carries a note that comes and goes with the switch, and a unit read off a member.</summary>
        [Card("Text worked out while you look")]
        [Note("This one only appears while the switch above is on.", MessageSeverity.Warning, "reactiveSwitch")]
        [Unit("$UnitName")]
        public float reactiveValue = 1f;

        #endregion

        #region Bounds from the behaviour

        /// <summary>Bounded by two other fields rather than by fixed numbers.</summary>
        [Card("Bounds from the behaviour")]
        [Note("The ends of this slider are the two fields under it. Move them and it follows.")]
        [Clamp("lowestAllowed", "highestAllowed")]
        public float betweenBounds = 5f;

        /// <summary>Lower end of the slider above.</summary>
        [Card("Bounds from the behaviour")]
        [Row("Bounds")]
        [LabelText("Low")]
        public float lowestAllowed;

        /// <summary>Upper end of the slider above.</summary>
        [Card("Bounds from the behaviour")]
        [Row("Bounds")]
        [LabelText("High")]
        public float highestAllowed = 10f;

        #endregion

        #region Reacting to an edit

        /// <summary>Runs a method on the behaviour every time it is edited.</summary>
        [Card("Reacting to an edit")]
        [Note("Drag this and the readout under it is rewritten by a method on the script.")]
        [OnValueChanged("DoubleIntoReadout")]
        [Clamp(0f, 50f)]
        public float doubled = 4f;

        /// <summary>Written by the field above rather than by hand.</summary>
        [Card("Reacting to an edit")]
        [DisplayAsString]
        public float doubledResult = 8f;

        #endregion

        #region Nothing serialized here

        /// <summary>The field the property below writes through to, shown so the two can be watched together.</summary>
        [Card("Nothing serialized here")]
        [LabelText("The field itself")]
        public string subject = "Edit either of these";

        #endregion

        #region Actions

        // For the inspector alone. The guard keeps these out of the Udon program, which is why they can
        // return and do whatever the editor needs. It reaches only members Unity does not serialize: a
        // serialized FIELD behind this guard still exists on the C# proxy, so every proxy push hunts for
        // an Udon variable the compiler was never given and logs an error for as long as it is selected.
#if !COMPILER_UDONSHARP && UNITY_EDITOR

        /// <summary>
        /// Worked out rather than stored. It has no setter, so the inspector shows it and leaves it
        /// alone: nothing here is serialized, and nothing about it is saved.
        /// </summary>
        [ShowInInspector]
        [Card("Nothing serialized here")]
        [Note("Neither of these is a serialized field. The first is worked out and can only be read. The second is a property with a setter, so the inspector writes straight through it into the field above.")]
        private float DoubledAgain
        {
            get { return doubled * 2f; }
        }

        /// <summary>A property with a setter, so the inspector edits it like any other field.</summary>
        [ShowInInspector]
        [Card("Nothing serialized here")]
        [LabelText("Through a property")]
        private string Headline
        {
            get { return subject; }
            set { subject = value; }
        }

        /// <summary>Text the note at the top of the card shows.</summary>
        /// <returns>A sentence describing the switch.</returns>
        private string DescribeState()
        {
            return reactiveSwitch
                ? "This sentence is written by a method, and the switch below is on."
                : "Same method, and the switch below is off.";
        }

        /// <summary>Unit shown over the reactive value.</summary>
        /// <returns>The unit name.</returns>
        private string UnitName()
        {
            return reactiveSwitch ? "metres" : "feet";
        }

        /// <summary>Writes twice the dragged value into the readout beneath it.</summary>
        private void DoubleIntoReadout()
        {
            doubledResult = doubled * 2f;
        }

#endif

        #endregion
    }
}
