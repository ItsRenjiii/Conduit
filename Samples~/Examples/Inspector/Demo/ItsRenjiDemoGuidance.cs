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
    /// What the panel tells the reader: quiet notes, standing warnings, required values, and a check
    /// written as a method so it can report its own severity and offer a fix. One of a set of demos,
    /// each covering a single concern. Nothing here runs in a world.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiDemoGuidance : UdonSharpBehaviour
    {
        #region Notices

        /// <summary>Carries the quiet guidance note.</summary>
        [Card("Notices")]
        [Note("A note is quiet guidance for something that is always true.")]
        public string noted = "Read the note above";

        /// <summary>Carries the louder standing warning.</summary>
        [Card("Notices")]
        [Warn("A warning carries a consequence worth reading before the value is changed.")]
        public bool warned;

        #endregion

        #region Required

        /// <summary>Required reference, left empty so the notice and its fix can be seen.</summary>
        [Card("Required")]
        [Required("nothing is drawn", "FindAnyMaterial", "Find one")]
        public Material requiredMaterial;

        /// <summary>Required array, showing that an empty array counts as missing.</summary>
        [Card("Required")]
        [Required("there is nothing to work through")]
        public Transform[] requiredEntries = new Transform[0];

        /// <summary>Required text with no consequence given, showing the shorter sentence.</summary>
        [Card("Required")]
        [Required]
        public string requiredTitle = "";

        /// <summary>Required text that is already filled, so its notice stays hidden.</summary>
        [Card("Required")]
        [Required("nothing is shown, because this one is filled")]
        public string satisfiedTitle = "Already filled in";

        #endregion

        #region Checked by a method

        /// <summary>Checked by a method rather than by being empty or not.</summary>
        [Card("Checked by a method")]
        [Note("Required only knows about empty. A check method knows the actual rule, says how loudly to report it, and can offer to put it right.")]
        [Validate("ValidateCheckedTitle")]
        public string checkedTitle = "A title far too long for the row it has to fit on";

        #endregion

        #region Actions

        // For the inspector alone. The guard keeps these out of the Udon program, which is why they can
        // return and do whatever the editor needs. It reaches only members Unity does not serialize: a
        // serialized FIELD behind this guard still exists on the C# proxy, so every proxy push hunts for
        // an Udon variable the compiler was never given and logs an error for as long as it is selected.
#if !COMPILER_UDONSHARP && UNITY_EDITOR

        /// <summary>Puts the first material in the project into the required field.</summary>
        private void FindAnyMaterial()
        {
            string[] found = UnityEditor.AssetDatabase.FindAssets("t:Material");
            if (found.Length == 0) return;

            string path = UnityEditor.AssetDatabase.GUIDToAssetPath(found[0]);
            requiredMaterial = UnityEditor.AssetDatabase.LoadAssetAtPath<Material>(path);
        }

        /// <summary>Empties the required values so their notices can be watched appearing.</summary>
        [Button("Clear the required fields", "Actions", true)]
        private void ClearRequiredFields()
        {
            requiredMaterial = null;
            requiredEntries = new Transform[0];
            requiredTitle = string.Empty;
            satisfiedTitle = string.Empty;
        }

        /// <summary>Puts the text values back so their notices can be watched going away.</summary>
        [Button("Fill the text back in", "Actions")]
        private void FillRequiredFields()
        {
            requiredTitle = "Filled in by the action above";
            satisfiedTitle = "Already filled in";
        }

        /// <summary>Reports a checked title that is too long for the row it has to fit on.</summary>
        /// <returns>What is wrong with the title, and how to put it right.</returns>
        private ValidationResult ValidateCheckedTitle()
        {
            if (string.IsNullOrEmpty(checkedTitle)) return ValidationResult.Info("A title reads better than none.");
            if (checkedTitle.Length <= 24) return ValidationResult.Valid;

            return ValidationResult
                .Warning("That title is " + checkedTitle.Length + " characters, and only 24 fit on a row.")
                .WithFix(TrimCheckedTitle, "Trim it");
        }

        /// <summary>Cuts the checked title down to what fits.</summary>
        private void TrimCheckedTitle()
        {
            if (checkedTitle == null) return;
            if (checkedTitle.Length <= 24) return;

            checkedTitle = checkedTitle.Substring(0, 24);
        }

#endif

        #endregion
    }
}
