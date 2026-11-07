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
    /// <summary>Stands in for a real setting so the choice strip has an enum to draw.</summary>
    public enum DemoSize
    {
        /// <summary>Smallest setting.</summary>
        Small,

        /// <summary>Middle setting.</summary>
        Medium,

        /// <summary>Largest setting.</summary>
        Large,
    }

    /// <summary>
    /// Fields whose value is chosen from a list rather than typed: Unity's own pickers, lists the
    /// behaviour supplies, assets found in the project, and lists built from whatever another field
    /// points at. One of a set of demos, each covering a single concern.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiDemoPickers : UdonSharpBehaviour
    {
        #region Unity's pickers

        /// <summary>Drawn as Unity's own layer picker.</summary>
        [Card("Unity's pickers")]
        [Note("Unity's layer and tag pickers, on a plain whole number and a plain string.")]
        [LayerField]
        public int chosenLayer;

        /// <summary>Drawn as Unity's own tag picker.</summary>
        [Card("Unity's pickers")]
        [TagField]
        public string chosenTag = "Untagged";

        #endregion

        #region Lists the behaviour supplies

        /// <summary>Chosen from a list the behaviour supplies.</summary>
        [Card("Lists the behaviour supplies")]
        [Note("The values on offer come from a property on this script, not from the attribute.")]
        [Dropdown("PresetNames")]
        public string preset = "Balanced";

        /// <summary>Enum drawn as the strip of choices, named from the enum itself.</summary>
        [Card("Lists the behaviour supplies")]
        [Note("An enum needs no option names: the strip reads them off the enum.")]
        [Choices]
        public DemoSize sizeLevel = DemoSize.Medium;

        #endregion

        #region From the project

        /// <summary>Path to a scene, chosen rather than typed.</summary>
        [Card("From the project")]
        [Note("A scene path, the matching assets in the project, and a component fetched off this object.")]
        [SceneField]
        public string scenePath = "";

        /// <summary>Chosen from the materials the project holds.</summary>
        [Card("From the project")]
        [AssetDropdown("t:Material")]
        public Material pickedMaterial;

        /// <summary>Fetched from this object rather than dragged onto it.</summary>
        [Card("From the project")]
        [RequiredGet(InChildren = true)]
        public MeshRenderer ownRenderer;

        /// <summary>Chosen from one folder, with a way out to anything else.</summary>
        [Card("From one folder, or anywhere")]
        [Note("The list holds the materials under the project's Assets folder. Choose Custom and a slot opens for a material from anywhere else, such as one inside a package; drop a listed one into that slot and the list picks it up again by name.")]
        [AssetDropdown("t:Material", SearchInFolders = new[] { "Assets" }, AllowCustom = true)]
        public Material bundledMaterial;

        #endregion

        #region From another field

        /// <summary>Animator whose parameters the field below lists.</summary>
        [Card("From another field")]
        [Note("These two read their options off the fields above them. Assign an animator and a material and the lists fill themselves.")]
        public Animator demoAnimator;

        /// <summary>Chosen from the animator's own parameters.</summary>
        [Card("From another field")]
        [AnimatorParameter("demoAnimator")]
        public string animatorParameter = "";

        /// <summary>Chosen from the shader properties the picked material declares.</summary>
        [Card("From another field")]
        [MaterialProperty("pickedMaterial")]
        public string shaderProperty = "";

        #endregion

        #region Actions

        // For the inspector alone. The guard keeps these out of the Udon program, which is why they can
        // return and do whatever the editor needs. It reaches only members Unity does not serialize: a
        // serialized FIELD behind this guard still exists on the C# proxy, so every proxy push hunts for
        // an Udon variable the compiler was never given and logs an error for as long as it is selected.
#if !COMPILER_UDONSHARP && UNITY_EDITOR

        /// <summary>Values offered by the preset list.</summary>
        private string[] PresetNames
        {
            get { return new[] { "Minimal", "Balanced", "Full", "Custom" }; }
        }

#endif

        #endregion
    }
}
