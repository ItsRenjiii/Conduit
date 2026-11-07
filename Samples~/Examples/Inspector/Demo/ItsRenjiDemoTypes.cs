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
    /// Every field type the panel draws, with no attributes on most of them, so the plain case can be
    /// checked as easily as the decorated one. The last card shows that Unity's own attributes keep
    /// working untouched inside ours. One of a set of demos, each covering a single concern.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiDemoTypes : UdonSharpBehaviour
    {
        #region Primitives

        /// <summary>Plain toggle.</summary>
        [Card("Primitives")]
        public bool aBool = true;

        /// <summary>Plain whole number.</summary>
        [Card("Primitives")]
        public int anInt = 7;

        /// <summary>Plain decimal number.</summary>
        [Card("Primitives")]
        public float aFloat = 1.5f;

        /// <summary>Plain text.</summary>
        [Card("Primitives")]
        public string aString = "Text";

        #endregion

        #region Structs

        /// <summary>Colour swatch and its picker.</summary>
        [Card("Structs")]
        public Color aColour = Color.cyan;

        /// <summary>Two component vector.</summary>
        [Card("Structs")]
        public Vector2 aVector2 = Vector2.one;

        /// <summary>Three component vector.</summary>
        [Card("Structs")]
        public Vector3 aVector3 = Vector3.up;

        #endregion

        #region References

        /// <summary>Scene reference to a transform.</summary>
        [Card("References")]
        public Transform aTransform;

        /// <summary>Scene reference to a whole object.</summary>
        [Card("References")]
        public GameObject aGameObject;

        /// <summary>Project reference to an asset.</summary>
        [Card("References")]
        public AudioClip aClip;

        #endregion

        #region Arrays

        /// <summary>Resizable list of text.</summary>
        [Card("Arrays")]
        public string[] someStrings = new string[0];

        /// <summary>Resizable list of scene references.</summary>
        [Card("Arrays")]
        public Transform[] someTransforms = new Transform[0];

        #endregion

        #region Unity's own attributes

        /// <summary>Drawn by Unity's own tooltip attribute, which this inspector leaves alone.</summary>
        [Card("Unity's own attributes")]
        [Note("Nothing here replaces Unity's attributes. They keep working inside the cards.")]
        [Tooltip("Unity's own tooltip attribute, reaching the label untouched.")]
        public string withATooltip = "Hover my label";

        /// <summary>Drawn by Unity's own text area attribute.</summary>
        [Card("Unity's own attributes")]
        [TextArea(3, 6)]
        public string overMultipleLines = "Unity's text area attribute, drawn by Unity's own drawer inside one of our cards.";

        #endregion
    }
}
