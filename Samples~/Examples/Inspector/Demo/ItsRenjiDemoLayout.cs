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
    /// Where things go: tabs, cards, folds, rows, spacing and draw order. One of a set of demos, each
    /// covering a single concern, so the example for a given attribute can be opened directly rather
    /// than hunted for. Nothing here runs in a world; the fields exist to be looked at.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiDemoLayout : UdonSharpBehaviour
    {
        #region Above the tabs

        /// <summary>Sits above the tab strip, showing what a field outside the tabs looks like.</summary>
        [Eyebrow("Getting started")]
        [Note("A field with no tab sits above the tab strip, so a behaviour can keep its headline settings visible whichever tab is open.")]
        public string panelName = "Layout demo";

        /// <summary>Second untabbed field, renamed by an attribute and opened by a rule.</summary>
        [Divider]
        [LabelText("Enabled in world")]
        public bool demoEnabled = true;

        #endregion

        #region Cards

        /// <summary>First field of the first card, which also carries the accent label.</summary>
        [Tab("Cards")]
        [Card("Cards")]
        [Eyebrow("Grouping")]
        [Note("A card is a titled panel. Every field naming the same card shares it.")]
        public string cardFirst = "First in the card";

        /// <summary>Second field in the same card, showing grouping without a repeated heading.</summary>
        [Tab("Cards")]
        [Card("Cards")]
        public int cardSecond = 2;

        /// <summary>Sits in a card nested inside the one above it.</summary>
        [Tab("Cards")]
        [Card("Cards/Nested")]
        [Note("A slash in the name nests one card inside another.")]
        public float nestedValue = 0.5f;

        /// <summary>Opens the collapsible card, which starts open.</summary>
        [Tab("Cards")]
        [Fold("Collapsible")]
        [Note("A fold is a card whose heading is the switch. Click it. Whether it was left open is remembered.")]
        public Color foldColour = Color.white;

        /// <summary>Sits in a nested fold that starts closed.</summary>
        [Tab("Cards")]
        [Fold("Collapsible/Closed to begin with", false)]
        public Vector3 foldOffset = Vector3.zero;

        #endregion

        #region Rows

        /// <summary>Left half of the shared row.</summary>
        [Tab("Rows")]
        [Card("Side by side")]
        [Note("Fields naming the same row share one line, splitting the width between them.")]
        [Row("Size")]
        [LabelText("W")]
        public float boxWidth = 16f;

        /// <summary>Right half of the shared row.</summary>
        [Tab("Rows")]
        [Card("Side by side")]
        [Row("Size")]
        [LabelText("H")]
        public float boxHeight = 9f;

        #endregion

        #region Spacing

        /// <summary>Pushed down by an explicit gap.</summary>
        [Tab("Spacing")]
        [Card("Breathing room")]
        [Gap(18f)]
        [Note("The space above this note is a gap attribute, not card padding.")]
        public int afterGap = 1;

        /// <summary>Opened by a hairline rule.</summary>
        [Tab("Spacing")]
        [Card("Breathing room")]
        [Divider]
        public int afterDivider = 2;

        /// <summary>Stepped in by one indent.</summary>
        [Tab("Spacing")]
        [Card("Stepping in")]
        [Indent]
        public int indentedOnce = 3;

        /// <summary>Stepped in by two indents.</summary>
        [Tab("Spacing")]
        [Card("Stepping in")]
        [Indent(2)]
        public int indentedTwice = 4;

        #endregion

        #region Order

        /// <summary>Declared first of the pair and drawn second.</summary>
        [Tab("Order")]
        [Card("Drawn in a set order")]
        [Order(91)]
        public string drawnSecond = "Declared first";

        /// <summary>Declared second of the pair and drawn first.</summary>
        [Tab("Order")]
        [Card("Drawn in a set order")]
        [Order(90)]
        [Note("These two are declared the other way round in the script. The order attribute decides what you see.")]
        public string drawnFirst = "Declared second";

        #endregion
    }
}
