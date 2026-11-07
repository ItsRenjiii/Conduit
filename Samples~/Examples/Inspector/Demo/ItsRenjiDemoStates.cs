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
    /// Fields that change with the editor's own state, and the collection controls: play mode, a tint,
    /// list settings, and a table built from one array per column. One of a set of demos, each
    /// covering a single concern. Nothing here runs in a world.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiDemoStates : UdonSharpBehaviour
    {
        #region Play mode

        /// <summary>Disappears once play mode starts.</summary>
        [Card("Play mode")]
        [Note("Enter play mode and watch these three.")]
        [HideInPlayMode]
        public string hiddenWhilePlaying = "Gone once you press play";

        /// <summary>Appears only once play mode starts.</summary>
        [Card("Play mode")]
        [ShowInPlayMode]
        public string shownWhilePlaying = "Here only while playing";

        /// <summary>Editable only once play mode starts.</summary>
        [Card("Play mode")]
        [PlayModeOnly]
        public float liveOnlyValue = 1f;

        #endregion

        #region Colour

        /// <summary>Tinted to pick it out of its card.</summary>
        [Card("Colour")]
        [Note("A tint picks one setting out of the card around it.")]
        [Tint(1f, 0.55f, 0.35f)]
        public float tinted = 1f;

        #endregion

        #region Lists

        /// <summary>Reorderable, with the remove button withheld.</summary>
        [Card("Lists")]
        [Note("Reorderable by dragging, and the remove button is withheld.")]
        [ListSettings(true, true, false)]
        public string[] guardedList = new string[0];

        /// <summary>Opened straight into the card with no fold of its own.</summary>
        [Card("Lists")]
        [Note("Opened into the card with no fold around it.")]
        [InlineProperty]
        public int[] openList = new int[0];

        #endregion

        #region Table

        /// <summary>Spine of the table: the object each row is about.</summary>
        [Card("Table")]
        [Note("One array per column, held at the same length. Add and remove act on every column at once, which is how a table is built when Udon will not serialize an array of structs.")]
        [Table("rowEnabled", "rowLocked", "rowHidden", Headings = new[] { "On", "Locked", "Hidden" })]
        public GameObject[] rowObjects = new GameObject[0];

        /// <summary>Enable column of the table, drawn as a tick rather than as a field of its own.</summary>
        [HideInInspector] public bool[] rowEnabled = new bool[0];

        /// <summary>Locked column of the table.</summary>
        [HideInInspector] public bool[] rowLocked = new bool[0];

        /// <summary>Hidden column of the table.</summary>
        [HideInInspector] public bool[] rowHidden = new bool[0];

        #endregion

        #region Actions

        // For the inspector alone. The guard keeps these out of the Udon program, which is why they can
        // return and do whatever the editor needs. It reaches only members Unity does not serialize: a
        // serialized FIELD behind this guard still exists on the C# proxy, so every proxy push hunts for
        // an Udon variable the compiler was never given and logs an error for as long as it is selected.
#if !COMPILER_UDONSHARP && UNITY_EDITOR

        /// <summary>Sets the table to a given number of rows, all switched the same way.</summary>
        /// <param name="rows">How many rows the table should end up with.</param>
        /// <param name="switchedOn">What the On column of every new row is set to.</param>
        [Button("Fill the table", "Actions")]
        private void FillTable(int rows, bool switchedOn)
        {
            rows = Mathf.Max(0, rows);

            rowObjects = new GameObject[rows];
            rowEnabled = new bool[rows];
            rowLocked = new bool[rows];
            rowHidden = new bool[rows];

            for (int i = 0; i < rows; i++)
            {
                rowEnabled[i] = switchedOn;
            }
        }

        /// <summary>
        /// Numbers every child. Recorded against the whole object, so one undo puts every child's name
        /// back rather than restoring the component and leaving the children renamed.
        /// </summary>
        [Button("Number the children", "Actions", Records = UndoScope.Hierarchy)]
        private void NumberTheChildren()
        {
            for (int i = 0; i < transform.childCount; i++)
            {
                transform.GetChild(i).name = "Child " + (i + 1);
            }
        }

#endif

        #endregion
    }
}
