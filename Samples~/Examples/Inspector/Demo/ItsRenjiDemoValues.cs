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
    /// How a value is presented: bounded as a slider, named with a unit, drawn as the choice it stands
    /// for, relabelled, widened, held read only or hidden behind its own note. One of a set of demos,
    /// each covering a single concern. Nothing here runs in a world.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiDemoValues : UdonSharpBehaviour
    {
        #region Numbers

        /// <summary>Bounded value drawn as a slider.</summary>
        [Card("Numbers")]
        [Note("Clamp turns a number into a bounded slider, and a unit names what the number measures.")]
        [Clamp(0f, 1f)]
        public float mix = 0.5f;

        /// <summary>Bounded whole number, drawn as a slider with its unit beside it.</summary>
        [Card("Numbers")]
        [Clamp(1f, 32f)]
        [Unit("steps")]
        public int steps = 8;

        /// <summary>Unbounded number that still says what it measures.</summary>
        [Card("Numbers")]
        [Unit("seconds")]
        public float delay = 2.5f;

        /// <summary>A pair of numbers that mean one range, drawn as one control.</summary>
        [Card("Numbers")]
        [MinMaxClamp(0f, 60f)]
        [LabelText("Skip range")]
        public Vector2 skipRange = new Vector2(5f, 45f);

        /// <summary>Marked with the brand accent as the one that matters most here.</summary>
        [Card("Numbers")]
        [Accent]
        [LabelText("The one that matters")]
        public float headlineValue = 1f;

        #endregion

        #region Choices

        /// <summary>Whole number standing for a choice, drawn as the choice.</summary>
        [Card("Choices")]
        [Note("A whole number drawn as the thing it stands for, rather than as a number to look up.")]
        [Choices("None", "Tight", "Normal", "Loose")]
        public int spacingStep = 2;

        #endregion

        #region Labels

        /// <summary>Shows a label that owes nothing to the field name.</summary>
        [Card("Labels")]
        [LabelText("Nothing like the field name")]
        public string relabelled = "The label came from the attribute";

        /// <summary>Shows one field widening its own label without touching its neighbours.</summary>
        [Card("Labels")]
        [LabelWidth(230f)]
        public string fieldWithAnUnusuallyLongName = "Wider label, same card";

        /// <summary>Shown for reading, never for editing.</summary>
        [Card("Labels")]
        [ReadOnlyField]
        public string reportedState = "Shown, never edited";

        /// <summary>Drawn with no label at all.</summary>
        [Card("Labels")]
        [Note("The field below has no label, because this note already says what it is.")]
        [HideLabel]
        public string withoutALabel = "No label, so the value gets the whole width";

        #endregion

        #region Locked while playing

        /// <summary>Locked for as long as play mode is running.</summary>
        [Card("Locked while playing")]
        [Note("Read once at startup, so it greys itself out the moment play mode begins and comes back when it stops.")]
        [EditModeOnly]
        public int startupCount = 16;

        #endregion
    }
}
