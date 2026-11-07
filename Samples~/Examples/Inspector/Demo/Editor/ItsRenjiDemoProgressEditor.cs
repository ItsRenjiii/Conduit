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

using System.Collections;
using ItsRenji.Inspector.Demo;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ItsRenji.Inspector.Editors
{
    /// <summary>
    /// Draws the progress demo, and owns the one thing attributes cannot express: work that happens over
    /// time. It keeps its own file because it carries a tool rather than a property sheet.
    /// <para>
    /// This is the reference for reporting a long job, and four things separate one from a spinner. The
    /// work runs on an editor coroutine, so the editor stays usable throughout. It reports into
    /// UnityEditor.Progress, which belongs to the EDITOR and therefore survives everything this panel
    /// does. Its state is DATA rather than something living inside the elements that draw it, so a panel
    /// rebuilt mid-run picks the run back up instead of losing it. And cancel genuinely stops the work
    /// and says that nothing was kept.
    /// </para>
    /// </summary>
    [CustomEditor(typeof(ItsRenjiDemoProgress))]
    public sealed class ItsRenjiDemoProgressEditor : ItsRenjiInspector
    {
        #region Nested Types

        /// <summary>
        /// What the running job is doing. A plain value, held away from the elements that render it,
        /// which is what lets a rebuilt panel carry on showing a run it did not start.
        /// </summary>
        private struct ProgressState
        {
            /// <summary>True while a job is in flight.</summary>
            public bool Running;

            /// <summary>What is happening right now.</summary>
            public string Text;

            /// <summary>Completed share, or a negative value when the total is unknown.</summary>
            public float Fraction;
        }

        #endregion

        #region Private State

        // Static on purpose. An editor instance is destroyed and rebuilt whenever the selection changes,
        // and the job outlives it, so everything the job needs to survive that lives here rather than on
        // the instance.

        /// <summary>Current state of the running job.</summary>
        private static ProgressState _state;

        /// <summary>Job in flight, so a second press cannot start one beside it.</summary>
        private static Unity.EditorCoroutines.Editor.EditorCoroutine _routine;

        /// <summary>Editor background task the job reports into, or -1 when nothing is running.</summary>
        private static int _progressId = -1;

        /// <summary>Set by either cancel route, read by the job between steps.</summary>
        private static bool _cancelRequested;

        /// <summary>How the last job ended, shown until the next one starts.</summary>
        private static string _outcome = string.Empty;

        /// <summary>Progress block, rebuilt with the panel and re-fed from the state.</summary>
        private static VisualElement _progress;

        /// <summary>Line naming what the job is doing right now.</summary>
        private static Label _progressLabel;

        /// <summary>Filled portion of the progress track.</summary>
        private static VisualElement _progressFill;

        /// <summary>Line reporting how the last job ended.</summary>
        private static Label _outcomeLabel;

        /// <summary>Button that starts a job, disabled while one runs.</summary>
        private static Button _run;

        #endregion

        #region Identity

        /// <summary>Name shown under the brand mark.</summary>
        protected override string Title => "Progress";

        /// <summary>One line saying what this demo covers.</summary>
        protected override string Subtitle => "A long job, reported honestly and stoppable.";

        #endregion

        #region Sections

        /// <summary>Adds the job and its progress beneath the attributed settings.</summary>
        /// <param name="root">Root of this inspector.</param>
        protected override void BuildCustomSections(VisualElement root)
        {
            ItsRenjiDemoProgress demo = (ItsRenjiDemoProgress)target;

            VisualElement card = new VisualElement();
            card.AddToClassList("ir-card");

            Label heading = new Label("Run the job");
            heading.AddToClassList("ir-card__heading");
            card.Add(heading);

            _run = new Button { text = "Start" };
            _run.AddToClassList("ir-button");
            _run.AddToClassList("ir-button--accent");
            _run.clicked += () =>
            {
                if (_routine != null) return;
                _routine = EditorCoroutineUtility.StartCoroutineOwnerless(RunJob(demo));
            };
            card.Add(_run);

            card.Add(BuildProgress());
            root.Add(card);

            // Rendered last, so a panel built while a job is already running joins that job rather than
            // showing an idle card over work which is still going on.
            RenderProgress();
        }

        /// <summary>Builds the progress block the running job reports into.</summary>
        /// <returns>The block.</returns>
        private static VisualElement BuildProgress()
        {
            _progress = new VisualElement();
            _progress.AddToClassList("ir-progress");

            _progressLabel = new Label();
            _progressLabel.AddToClassList("ir-progress__label");
            _progress.Add(_progressLabel);

            VisualElement row = new VisualElement();
            row.AddToClassList("ir-row");

            VisualElement track = new VisualElement();
            track.AddToClassList("ir-progress__track");
            track.style.flexGrow = 1f;

            _progressFill = new VisualElement();
            _progressFill.AddToClassList("ir-progress__fill");
            track.Add(_progressFill);
            row.Add(track);

            Button cancel = new Button { text = "Cancel" };
            cancel.AddToClassList("ir-button");
            cancel.AddToClassList("ir-button--inline");
            cancel.clicked += () => _cancelRequested = true;
            row.Add(cancel);

            _progress.Add(row);

            _outcomeLabel = new Label();
            _outcomeLabel.AddToClassList("ir-note");

            VisualElement block = new VisualElement();
            block.Add(_progress);
            block.Add(_outcomeLabel);
            return block;
        }

        #endregion

        #region The Job

        /// <summary>
        /// Works through the job without blocking the editor. Every value it needs is copied out of the
        /// behaviour first, so deleting the object mid-run ends the job rather than throwing on a
        /// destroyed reference.
        /// </summary>
        /// <param name="demo">Behaviour the settings are read from.</param>
        /// <returns>Editor coroutine enumerator.</returns>
        private static IEnumerator RunJob(ItsRenjiDemoProgress demo)
        {
            int steps = demo.steps;
            float stepDelay = demo.stepDuration / 1000f;
            bool survey = demo.surveyFirst;
            int completed = 0;

            try
            {
                BeginProgress("Demo job");

                if (survey)
                {
                    for (int i = 0; i < 25; i++)
                    {
                        // No fraction, because the total is genuinely unknown at this point. The bar
                        // stays empty and the live count is what shows the job has not hung.
                        ReportProgress("Surveying (" + (i + 1) + " found)", -1f);
                        if (_cancelRequested) yield break;

                        yield return new EditorWaitForSeconds(0.04f);
                    }
                }

                for (int i = 0; i < steps; i++)
                {
                    ReportProgress("Step " + (i + 1) + " of " + steps, (float)i / steps);
                    if (_cancelRequested) yield break;

                    yield return new EditorWaitForSeconds(stepDelay);
                    completed++;
                }
            }
            finally
            {
                // Fail closed: a job that did not finish says so, rather than leaving the reader to read
                // a half-finished run as a complete one.
                _outcome = completed == steps
                    ? "Finished " + completed + " step(s)."
                    : "Stopped after " + completed + " step(s). Nothing was kept.";

                EndProgress();
                _routine = null;
            }
        }

        #endregion

        #region Progress

        /// <summary>Opens the job's progress, both as an editor task and in the card.</summary>
        /// <param name="title">Name the background task carries in the editor's task list.</param>
        private static void BeginProgress(string title)
        {
            _cancelRequested = false;
            _outcome = string.Empty;
            _state = new ProgressState { Running = true, Text = "Starting", Fraction = -1f };

            // The task belongs to the editor rather than to this panel, so it keeps reporting and stays
            // cancellable through a selection change, a rebuild, or this inspector being closed.
            _progressId = UnityEditor.Progress.Start(title, "Starting");
            UnityEditor.Progress.RegisterCancelCallback(_progressId, () =>
            {
                _cancelRequested = true;
                return true;
            });

            RenderProgress();
        }

        /// <summary>Records what the job is doing and shows it in both places.</summary>
        /// <param name="message">What is happening right now.</param>
        /// <param name="fraction">Completed share, or a negative value when the total is unknown.</param>
        private static void ReportProgress(string message, float fraction)
        {
            _state.Text = message;
            _state.Fraction = fraction;

            if (_progressId != -1)
            {
                UnityEditor.Progress.Report(
                    _progressId,
                    fraction < 0f ? 0f : Mathf.Clamp01(fraction),
                    message);
            }

            RenderProgress();
        }

        /// <summary>Closes the job's progress and clears any pending cancel.</summary>
        private static void EndProgress()
        {
            _cancelRequested = false;
            _state = new ProgressState();

            if (_progressId != -1)
            {
                UnityEditor.Progress.Remove(_progressId);
                _progressId = -1;
            }

            RenderProgress();
        }

        /// <summary>
        /// Draws the current state into the card, when there is a card to draw into. Everything it needs
        /// comes from the state, which is what makes this equally the way a running job updates and the
        /// way a freshly built panel joins one already in progress.
        /// </summary>
        private static void RenderProgress()
        {
            if (_progress == null || _progress.panel == null) return;

            _progress.style.display = _state.Running ? DisplayStyle.Flex : DisplayStyle.None;
            _progressLabel.text = _state.Text ?? string.Empty;
            _progressFill.style.width = Length.Percent(
                _state.Fraction < 0f ? 0f : Mathf.Clamp01(_state.Fraction) * 100f);

            _outcomeLabel.text = _outcome;
            _outcomeLabel.style.display =
                string.IsNullOrEmpty(_outcome) ? DisplayStyle.None : DisplayStyle.Flex;

            if (_run != null) _run.SetEnabled(!_state.Running);
        }

        #endregion
    }
}
