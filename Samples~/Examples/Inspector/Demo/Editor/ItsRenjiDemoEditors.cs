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

using ItsRenji.Inspector.Demo;
using UnityEditor;

namespace ItsRenji.Inspector.Editors
{
    /*
     * Every demo behaviour is drawn by the branded inspector, and there is deliberately no drawing code
     * in any of them: the whole layout comes from the attributes on the behaviour, which is the point
     * the demos are making. They are gathered in one file because each is a name and a title and
     * nothing else, and a file apiece would bury the nine behaviours that actually matter.
     */

    /// <summary>Draws the layout demo.</summary>
    [CustomEditor(typeof(ItsRenjiDemoLayout))]
    public sealed class ItsRenjiDemoLayoutEditor : ItsRenjiInspector
    {
        /// <summary>Name shown under the brand mark.</summary>
        protected override string Title => "Layout";

        /// <summary>One line saying what this demo covers.</summary>
        protected override string Subtitle => "Tabs, cards, folds, rows, spacing and draw order.";
    }

    /// <summary>Draws the guidance demo.</summary>
    [CustomEditor(typeof(ItsRenjiDemoGuidance))]
    public sealed class ItsRenjiDemoGuidanceEditor : ItsRenjiInspector
    {
        /// <summary>Name shown under the brand mark.</summary>
        protected override string Title => "Guidance";

        /// <summary>One line saying what this demo covers.</summary>
        protected override string Subtitle => "Notes, warnings, required values and checks that offer a fix.";
    }

    /// <summary>Draws the values demo.</summary>
    [CustomEditor(typeof(ItsRenjiDemoValues))]
    public sealed class ItsRenjiDemoValuesEditor : ItsRenjiInspector
    {
        /// <summary>Name shown under the brand mark.</summary>
        protected override string Title => "Values";

        /// <summary>One line saying what this demo covers.</summary>
        protected override string Subtitle => "Bounds, units, choices and how a value is labelled.";
    }

    /// <summary>Draws the conditions demo.</summary>
    [CustomEditor(typeof(ItsRenjiDemoConditions))]
    public sealed class ItsRenjiDemoConditionsEditor : ItsRenjiInspector
    {
        /// <summary>Name shown under the brand mark.</summary>
        protected override string Title => "Conditions";

        /// <summary>One line saying what this demo covers.</summary>
        protected override string Subtitle => "Fields that appear, vanish or grey out with another field.";
    }

    /// <summary>Draws the references demo.</summary>
    [CustomEditor(typeof(ItsRenjiDemoReferences))]
    public sealed class ItsRenjiDemoReferencesEditor : ItsRenjiInspector
    {
        /// <summary>Name shown under the brand mark.</summary>
        protected override string Title => "References";

        /// <summary>One line saying what this demo covers.</summary>
        protected override string Subtitle => "Where a reference may come from, and previews of what it is.";
    }

    /// <summary>Draws the field types demo.</summary>
    [CustomEditor(typeof(ItsRenjiDemoTypes))]
    public sealed class ItsRenjiDemoTypesEditor : ItsRenjiInspector
    {
        /// <summary>Name shown under the brand mark.</summary>
        protected override string Title => "Field types";

        /// <summary>One line saying what this demo covers.</summary>
        protected override string Subtitle => "Every type the panel draws, plain and undecorated.";
    }

    /// <summary>Draws the reactive demo.</summary>
    [CustomEditor(typeof(ItsRenjiDemoReactive))]
    public sealed class ItsRenjiDemoReactiveEditor : ItsRenjiInspector
    {
        /// <summary>Name shown under the brand mark.</summary>
        protected override string Title => "Reactive";

        /// <summary>One line saying what this demo covers.</summary>
        protected override string Subtitle => "Text, bounds and readouts the behaviour works out as you edit.";
    }

    /// <summary>Draws the pickers demo.</summary>
    [CustomEditor(typeof(ItsRenjiDemoPickers))]
    public sealed class ItsRenjiDemoPickersEditor : ItsRenjiInspector
    {
        /// <summary>Name shown under the brand mark.</summary>
        protected override string Title => "Pickers";

        /// <summary>One line saying what this demo covers.</summary>
        protected override string Subtitle => "Values chosen from a list rather than typed.";
    }

    /// <summary>Draws the states and collections demo.</summary>
    [CustomEditor(typeof(ItsRenjiDemoStates))]
    public sealed class ItsRenjiDemoStatesEditor : ItsRenjiInspector
    {
        /// <summary>Name shown under the brand mark.</summary>
        protected override string Title => "States";

        /// <summary>One line saying what this demo covers.</summary>
        protected override string Subtitle => "Play mode, tints, list settings and the table.";
    }
}
