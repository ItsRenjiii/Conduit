/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Poses an AnimationClip onto a live scene object without entering play mode, so an authored clip can
    /// actually be looked at.
    /// <para>
    /// This is the same mechanism Unity's animation window uses. The pose is written straight onto the
    /// object and stays until animation mode is stopped, which is what makes a scene view screenshot of it
    /// possible; stopping restores every value the clip touched.
    /// </para>
    /// <para>
    /// The sample deliberately stays posed rather than capturing and tidying up in one call, so the caller
    /// can frame, screenshot, and sample another time without re-posing. That leaves the editor in
    /// animation mode, where ordinary edits are reverted too, so every response says so and names the verb
    /// that leaves it.
    /// </para>
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Animation Preview Constants

        /// <summary>Usage line for the sampling command.</summary>
        private const string AnimationSampleUsage =
            "Usage: animation-clip-sample <target> | <clipPath> | <timeSeconds>";

        #endregion

        #region Animation Preview Commands

        /// <summary>
        /// Poses a clip onto a scene object at one time and reports what the clip drives there.
        /// </summary>
        /// <param name="args">Target, clip asset path, and the time in seconds.</param>
        /// <returns>What was posed, or an error naming what could not be resolved.</returns>
        private static string AnimationClipSampleCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length < 3)
            {
                return AnimationSampleUsage;
            }

            GameObject target;
            var findError = TryFindGameObject(sections[0].Trim(), out target);
            if (findError != null)
            {
                return findError;
            }

            var clipPath = sections[1].Trim().Trim('"');
            var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
            if (clip == null)
            {
                var any = AssetDatabase.LoadAssetAtPath<Object>(clipPath);
                return any == null
                    ? "No asset at " + clipPath + "."
                    : clipPath + " is a " + any.GetType().Name + ", not an AnimationClip.";
            }

            float time;
            if (!float.TryParse(sections[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out time))
            {
                return "Time must be a number in seconds. " + AnimationSampleUsage;
            }

            AnimationMode.StartAnimationMode();
            AnimationMode.BeginSampling();
            AnimationMode.SampleAnimationClip(target, clip, time);
            AnimationMode.EndSampling();
            SceneView.RepaintAll();

            var builder = new StringBuilder();
            builder.Append("Posed ").Append(clip.name).Append(" onto ").Append(GetGameObjectPath(target))
                .Append(" at ").Append(time.ToString("0.###", CultureInfo.InvariantCulture))
                .Append("s of ").Append(clip.length.ToString("0.###", CultureInfo.InvariantCulture)).AppendLine("s.");

            if (time > clip.length)
            {
                builder.AppendLine("  NOTE: past the end of the clip, so every curve holds its last key.");
            }

            AppendSampledBindings(builder, target, clip, time);

            builder.AppendLine("The editor is now in ANIMATION MODE and this pose is live: screenshot it, or sample another time.");
            builder.Append("Ordinary edits are reverted while it lasts - leave with animation-clip-sample-stop.");
            return builder.ToString();
        }

        /// <summary>
        /// Leaves animation mode, which restores everything a sample posed.
        /// </summary>
        /// <param name="args">Unused.</param>
        /// <returns>What it did, or a statement that nothing was posed.</returns>
        private static string AnimationClipSampleStopCommand(string args)
        {
            if (!AnimationMode.InAnimationMode())
            {
                return "Not in animation mode; nothing was posed, so nothing was restored.";
            }

            AnimationMode.StopAnimationMode();
            SceneView.RepaintAll();
            return "Left animation mode. Every value a sample posed is back to what the scene holds.";
        }

        #endregion

        #region Animation Preview Reporting

        /// <summary>
        /// Reports what the clip drives at this time, and warns about curves aimed at paths the target does
        /// not have. A clip whose paths do not match poses NOTHING, and silence there reads as success.
        /// </summary>
        /// <param name="builder">Report being built.</param>
        /// <param name="target">Object the clip was posed onto.</param>
        /// <param name="clip">Clip being sampled.</param>
        /// <param name="time">Time sampled, in seconds.</param>
        private static void AppendSampledBindings(StringBuilder builder, GameObject target, AnimationClip clip, float time)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);

            if (bindings.Length == 0 && objectBindings.Length == 0)
            {
                builder.AppendLine("  The clip has no curves, so nothing moved.");
                return;
            }

            var missing = 0;
            var shown = 0;

            foreach (var binding in bindings)
            {
                var child = string.IsNullOrEmpty(binding.path) ? target.transform : target.transform.Find(binding.path);
                if (child == null)
                {
                    missing++;
                    continue;
                }

                if (shown >= DefaultJsonResultLimit)
                {
                    continue;
                }

                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                var value = curve == null ? 0f : curve.Evaluate(time);

                builder.Append("  ")
                    .Append(string.IsNullOrEmpty(binding.path) ? "<root>" : binding.path)
                    .Append("  ").Append(binding.propertyName)
                    .Append(" = ").AppendLine(value.ToString("0.###", CultureInfo.InvariantCulture));
                shown++;
            }

            foreach (var binding in objectBindings)
            {
                var child = string.IsNullOrEmpty(binding.path) ? target.transform : target.transform.Find(binding.path);
                if (child == null)
                {
                    missing++;
                    continue;
                }

                builder.Append("  ")
                    .Append(string.IsNullOrEmpty(binding.path) ? "<root>" : binding.path)
                    .Append("  ").Append(binding.propertyName).AppendLine("  (object reference curve)");
            }

            if (shown >= DefaultJsonResultLimit)
            {
                builder.Append("  ... ").Append(bindings.Length - shown).AppendLine(" more float curve(s) not listed.");
            }

            if (missing > 0)
            {
                builder.Append("  WARNING: ").Append(missing)
                    .Append(" curve(s) drive a path this object does not have, so those posed nothing. ")
                    .AppendLine("The clip was authored against a different hierarchy.");
            }
        }

        #endregion
    }
}
