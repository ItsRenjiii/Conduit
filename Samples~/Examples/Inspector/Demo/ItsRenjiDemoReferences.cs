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
    /// Fields that point at something else: where a reference is allowed to come from, an action that
    /// fills one in, and the previews that show what a reference actually is rather than only its
    /// name. One of a set of demos, each covering a single concern. Nothing here runs in a world.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiDemoReferences : UdonSharpBehaviour
    {
        #region Where a reference may come from

        /// <summary>Reference that has to come from the project.</summary>
        [Card("Where a reference may come from")]
        [Note("Point either of these at the wrong kind of thing and it says so in place.")]
        [AssetOnly]
        public GameObject mustBeAPrefab;

        /// <summary>Reference that has to come from the scene.</summary>
        [Card("Where a reference may come from")]
        [SceneOnly]
        public Transform mustBeInTheScene;

        #endregion

        #region An action for one field

        /// <summary>Filled by the small button on its own line.</summary>
        [Card("An action for one field")]
        [Note("A button on the field's own line, for the action that fills in that one field.")]
        [InlineButton("FindFirstRenderer", "Find")]
        public MeshRenderer foundRenderer;

        /// <summary>Sat beside a button that reads as pressed while the field below it is on.</summary>
        [Card("An action for one field")]
        [Note("This button lights up while the value it toggles is on.")]
        [InlineButton("ToggleLocked", "Lock", 0f, "locked")]
        public float strength = 0.8f;

        /// <summary>Toggled by the button above, and what lights that button up.</summary>
        [Card("An action for one field")]
        [ReadOnlyField]
        public bool locked;

        #endregion

        #region Previews

        /// <summary>Mesh shown in a viewport that can be dragged to turn it.</summary>
        [Card("Turnable preview")]
        [Note("Drag inside the frame to turn it, and hold shift while scrolling to zoom. A mesh asset, or a prefab or scene object with a mesh filter under it.")]
        [InteractivePreview(200f)]
        public Mesh previewedMesh;

        /// <summary>Object whose first mesh is shown, turning freely rather than staying upright.</summary>
        [Card("Turnable preview")]
        [InteractivePreview(200f, -1f, PreviewRotation.Freeform)]
        public GameObject previewedObject;

        /// <summary>Drawn with its own inspector underneath the field.</summary>
        [Card("Edited in place")]
        [Note("The asset's own inspector, drawn here so it can be changed without leaving this one. This one draws the header and the preview as well as the fields.")]
        [InlineEditor(InlineEditorMode.FullEditor, 120f)]
        public Material editedMaterial;

        /// <summary>Sprite reference drawn with its art beneath it.</summary>
        [Card("Flat preview")]
        [Note("A flat preview shows the art a reference points at rather than only its name.")]
        [Preview(72f)]
        public Sprite previewSprite;

        /// <summary>Texture reference drawn at the default preview height.</summary>
        [Card("Flat preview")]
        [Preview]
        public Texture2D previewTexture;

        #endregion

        #region Actions

        // For the inspector alone. The guard keeps these out of the Udon program, which is why they can
        // return and do whatever the editor needs. It reaches only members Unity does not serialize: a
        // serialized FIELD behind this guard still exists on the C# proxy, so every proxy push hunts for
        // an Udon variable the compiler was never given and logs an error for as long as it is selected.
#if !COMPILER_UDONSHARP && UNITY_EDITOR

        /// <summary>Points the renderer field at the first one found under this object.</summary>
        private void FindFirstRenderer()
        {
            foundRenderer = GetComponentInChildren<MeshRenderer>();
        }

        /// <summary>Turns the flag the inline button reads.</summary>
        private void ToggleLocked()
        {
            locked = !locked;
        }

#endif

        #endregion
    }
}
