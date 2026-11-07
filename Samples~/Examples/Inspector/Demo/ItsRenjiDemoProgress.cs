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

namespace ItsRenji.Inspector.Demo
{
    /// <summary>
    /// Work that takes long enough to need reporting, and the two things that makes necessary: progress
    /// a reader can trust, and a way to stop it. One of a set of demos, each covering a single concern.
    /// Nothing here runs in a world.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiDemoProgress : UdonSharpBehaviour
    {
        #region The job

        /// <summary>Number of steps the demo job works through.</summary>
        [Card("The job")]
        [Note("Start the job below, then carry on using Unity. The panel keeps responding, the same task shows in the status bar, and either cancel stops it.")]
        [Clamp(10f, 400f)]
        [Unit("steps")]
        public int steps = 120;

        /// <summary>Milliseconds each step is held for, so the run lasts long enough to watch.</summary>
        [Card("The job")]
        [Clamp(0f, 200f)]
        [Unit("ms per step")]
        public int stepDuration = 40;

        /// <summary>Whether the job opens with a pass whose total is not known yet.</summary>
        [Card("The job")]
        [Note("An opening pass with no total yet. The bar stays empty and the text carries a live count, because a bar that invents a percentage is worse than one admitting it does not know.")]
        [LabelText("Start with an unknown total")]
        public bool surveyFirst = true;

        #endregion
    }
}
