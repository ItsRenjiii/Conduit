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

using System;
using UnityEngine;
using UnityEngine.Rendering;

namespace ItsRenji.Inspector
{
    /*
    Every attribute here is marked Conditional on UNITY_EDITOR. The classes still ship, but the compiler
    drops the USES of them from any assembly built without that symbol, so a world build carries none of
    this metadata on its fields. Nothing reads these at run time, so there is nothing to lose by it.
    */

    /// <summary>
    /// Base for every ItsRenji field attribute. These live in the runtime assembly rather than an
    /// editor one so they can sit directly on the serialized fields of an UdonSharp behaviour, and they
    /// reference nothing from the editor so none of it follows them into a world build.
    /// </summary>
    public abstract class ItsRenjiInspectorAttribute : PropertyAttribute
    {
    }

    #region Layout

    /// <summary>
    /// Places a field on a named tab. Tabs appear in the order their first field is declared, and the
    /// selected tab is remembered per behaviour type between selections.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class TabAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Tab this field belongs to.</summary>
        public string TabName { get; private set; }

        /// <summary>Places one field on a named tab.</summary>
        /// <param name="tabName">Tab the field belongs to.</param>
        public TabAttribute(string tabName)
        {
            TabName = tabName;
        }
    }

    /// <summary>
    /// Groups a field into a named card: a bordered panel with its own heading, always open. Nested cards
    /// are written with a forward slash, so "Shape/Corners" sits inside "Shape".
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class CardAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Card this field belongs to, with a forward slash separating nested names.</summary>
        public string CardName { get; private set; }

        /// <summary>Groups one field into a named card.</summary>
        /// <param name="cardName">Card the field belongs to.</param>
        public CardAttribute(string cardName)
        {
            CardName = cardName;
        }
    }

    /// <summary>
    /// Groups a field into a collapsible card. Behaves like <see cref="CardAttribute"/> in every other way,
    /// including nesting, and each fold remembers whether it was left open.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class FoldAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Fold this field belongs to, with a forward slash separating nested names.</summary>
        public string FoldName { get; private set; }

        /// <summary>Whether the fold starts open the first time it is seen.</summary>
        public bool OpenByDefault { get; private set; }

        /// <summary>Groups one field into a collapsible card.</summary>
        /// <param name="foldName">Fold the field belongs to.</param>
        /// <param name="openByDefault">Whether the fold starts open.</param>
        public FoldAttribute(string foldName, bool openByDefault = true)
        {
            FoldName = foldName;
            OpenByDefault = openByDefault;
        }
    }

    /// <summary>Lays a field beside the others sharing the same row name rather than beneath them.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class RowAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Row this field shares with its neighbours.</summary>
        public string RowName { get; private set; }

        /// <summary>Lays one field into a shared row.</summary>
        /// <param name="rowName">Row the field belongs to.</param>
        public RowAttribute(string rowName)
        {
            RowName = rowName;
        }
    }

    /// <summary>
    /// Draws a small uppercase accent label above a field, opening a run of related fields without
    /// spending a whole card on them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class EyebrowAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Text shown in the accent label.</summary>
        public string Text { get; private set; }

        /// <summary>Draws an accent label above a field.</summary>
        /// <param name="text">Text shown in the label.</param>
        public EyebrowAttribute(string text)
        {
            Text = text;
        }
    }

    /// <summary>Adds vertical breathing room above a field.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class GapAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Space above the field in pixels.</summary>
        public float Height { get; private set; }

        /// <summary>Space below the field in pixels.</summary>
        public float After { get; private set; }

        /// <summary>Adds space around a field.</summary>
        /// <param name="height">Space above the field in pixels.</param>
        /// <param name="after">Space below the field in pixels.</param>
        public GapAttribute(float height = 8f, float after = 0f)
        {
            Height = height;
            After = after;
        }
    }

    /// <summary>Draws a hairline rule above a field.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class DividerAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>Steps a field in from the left, showing that it belongs under the one above it.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class IndentAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Number of steps to indent by.</summary>
        public int Steps { get; private set; }

        /// <summary>Indents one field.</summary>
        /// <param name="steps">Number of steps to indent by.</param>
        public IndentAttribute(int steps = 1)
        {
            Steps = steps;
        }
    }

    /// <summary>
    /// Overrides where a field is drawn relative to the others. Fields sort by this value and then by the
    /// order they were declared in, which is how a derived class puts its own fields above an inherited
    /// block without the base class knowing anything about it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class OrderAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Sort key. Lower is drawn earlier, and an unmarked field counts as zero.</summary>
        public int Order { get; private set; }

        /// <summary>Sets where one field is drawn.</summary>
        /// <param name="order">Sort key, lower drawn earlier.</param>
        public OrderAttribute(int order)
        {
            Order = order;
        }
    }

    #endregion

    #region Guidance

    /// <summary>
    /// Shows guidance above a field. The text may be written out, or given as a member name behind a
    /// dollar sign to have it worked out while the object is being looked at.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class NoteAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Text shown in the note, or a member name behind a dollar sign.</summary>
        public string Text { get; private set; }

        /// <summary>How loudly the note is drawn.</summary>
        public MessageSeverity Severity { get; private set; }

        /// <summary>Name of a bool member deciding whether the note is shown, or null to always show it.</summary>
        public string VisibleIf { get; private set; }

        /// <summary>Shows a note above a field.</summary>
        /// <param name="text">Text shown, or a member name behind a dollar sign.</param>
        /// <param name="severity">How loudly the note is drawn.</param>
        /// <param name="visibleIf">Bool member deciding whether the note is shown.</param>
        public NoteAttribute(
            string text,
            MessageSeverity severity = MessageSeverity.Info,
            string visibleIf = null)
        {
            Text = text;
            Severity = severity;
            VisibleIf = visibleIf;
        }
    }

    /// <summary>Shows a standing warning above a field, for a consequence worth reading before editing.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class WarnAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Text shown in the warning.</summary>
        public string Text { get; private set; }

        /// <summary>Shows a warning above a field.</summary>
        /// <param name="text">Text shown in the warning.</param>
        public WarnAttribute(string text)
        {
            Text = text;
        }
    }

    /// <summary>
    /// Marks a value the behaviour cannot work without. An empty field reports itself in place rather than
    /// waiting for a console error at run time.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class RequiredAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>What stops working while the value is missing, or a member name behind a dollar sign.</summary>
        public string Consequence { get; private set; }

        /// <summary>Name of a method that fills the value in, offered as a button beside the notice.</summary>
        public string FixAction { get; private set; }

        /// <summary>Text on the button running the fix.</summary>
        public string FixActionName { get; private set; }

        /// <summary>Marks a field as required.</summary>
        /// <param name="consequence">What stops working while the value is missing.</param>
        /// <param name="fixAction">Method that fills the value in.</param>
        /// <param name="fixActionName">Text on the button running that method.</param>
        public RequiredAttribute(string consequence = null, string fixAction = null, string fixActionName = null)
        {
            Consequence = consequence;
            FixAction = fixAction;
            FixActionName = fixActionName;
        }
    }

    /// <summary>
    /// Checks a field with a method of the behaviour's own, for the rules that are more than "not empty".
    /// The method takes nothing and returns the problem as a sentence, or null when the value is fine.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class ValidateAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Name of the parameterless method returning the problem, or null when there is none.</summary>
        public string MethodName { get; private set; }

        /// <summary>Checks a field with one of the behaviour's methods.</summary>
        /// <param name="methodName">Method returning the problem, or null when the value is fine.</param>
        public ValidateAttribute(string methodName)
        {
            MethodName = methodName;
        }
    }

    #endregion

    #region Conditions

    /// <summary>Hides a field unless another serialized field on the same object matches a value.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class ShowIfAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Serialized field the condition reads.</summary>
        public string ConditionField { get; private set; }

        /// <summary>Value the condition field must hold, or null to mean "anything set".</summary>
        public object ExpectedValue { get; private set; }

        /// <summary>Hides a field unless a condition holds.</summary>
        /// <param name="conditionField">Serialized field the condition reads.</param>
        /// <param name="expectedValue">Value the condition field must hold.</param>
        public ShowIfAttribute(string conditionField, object expectedValue = null)
        {
            ConditionField = conditionField;
            ExpectedValue = expectedValue;
        }
    }

    /// <summary>Hides a field while another serialized field on the same object matches a value.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class HideIfAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Serialized field the condition reads.</summary>
        public string ConditionField { get; private set; }

        /// <summary>Value the condition field must hold, or null to mean "anything set".</summary>
        public object ExpectedValue { get; private set; }

        /// <summary>Hides a field while a condition holds.</summary>
        /// <param name="conditionField">Serialized field the condition reads.</param>
        /// <param name="expectedValue">Value the condition field must hold.</param>
        public HideIfAttribute(string conditionField, object expectedValue = null)
        {
            ConditionField = conditionField;
            ExpectedValue = expectedValue;
        }
    }

    /// <summary>
    /// Greys a field out unless a condition holds. Preferred over hiding when the reader still benefits
    /// from seeing that the setting exists.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class EnableIfAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Serialized field the condition reads.</summary>
        public string ConditionField { get; private set; }

        /// <summary>Value the condition field must hold, or null to mean "anything set".</summary>
        public object ExpectedValue { get; private set; }

        /// <summary>Greys a field out unless a condition holds.</summary>
        /// <param name="conditionField">Serialized field the condition reads.</param>
        /// <param name="expectedValue">Value the condition field must hold.</param>
        public EnableIfAttribute(string conditionField, object expectedValue = null)
        {
            ConditionField = conditionField;
            ExpectedValue = expectedValue;
        }
    }

    /// <summary>
    /// Locks a field while play mode is running, for a value that is read once at startup and so cannot
    /// be changed usefully after it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class EditModeOnlyAttribute : ItsRenjiInspectorAttribute
    {
    }

    #endregion

    #region Values

    /// <summary>
    /// Draws a numeric field as a slider between two bounds. Named Clamp rather than Range so it cannot
    /// collide with Unity's own attribute of that name.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class ClampAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Lowest value the slider allows.</summary>
        public float Minimum { get; private set; }

        /// <summary>Highest value the slider allows.</summary>
        public float Maximum { get; private set; }

        /// <summary>Member naming the lowest value, or null when the bound is fixed.</summary>
        public string MinimumMember { get; private set; }

        /// <summary>Member naming the highest value, or null when the bound is fixed.</summary>
        public string MaximumMember { get; private set; }

        /// <summary>Draws a numeric field as a bounded slider.</summary>
        /// <param name="minimum">Lowest value allowed.</param>
        /// <param name="maximum">Highest value allowed.</param>
        public ClampAttribute(float minimum, float maximum)
        {
            Minimum = minimum;
            Maximum = maximum;
        }

        /// <summary>Draws a numeric field as a slider whose bounds come from the behaviour itself.</summary>
        /// <param name="minimumMember">Member naming the lowest value.</param>
        /// <param name="maximumMember">Member naming the highest value.</param>
        public ClampAttribute(string minimumMember, string maximumMember)
        {
            MinimumMember = minimumMember;
            MaximumMember = maximumMember;
        }
    }

    /// <summary>
    /// Draws a Vector2 as one slider with two handles, where x is the low end of a range and y the high
    /// end. The pair is edited as the one thing it represents instead of as two unrelated numbers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class MinMaxClampAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Lowest value either handle can reach.</summary>
        public float Minimum { get; private set; }

        /// <summary>Highest value either handle can reach.</summary>
        public float Maximum { get; private set; }

        /// <summary>Draws a Vector2 as a range slider.</summary>
        /// <param name="minimum">Lowest value either handle can reach.</param>
        /// <param name="maximum">Highest value either handle can reach.</param>
        public MinMaxClampAttribute(float minimum, float maximum)
        {
            Minimum = minimum;
            Maximum = maximum;
        }
    }

    /// <summary>
    /// Draws a whole number as a strip of named buttons, one per option, where the value is the index of
    /// the chosen one. Reads as a choice rather than as a number that happens to have meanings.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class ChoicesAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Option names, in value order starting at zero.</summary>
        public string[] Options { get; private set; }

        /// <summary>Draws a whole number as a strip of named buttons.</summary>
        /// <param name="options">Option names, in value order starting at zero.</param>
        public ChoicesAttribute(params string[] options)
        {
            Options = options;
        }
    }

    /// <summary>Shows the unit a number is measured in beside its field.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class UnitAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Unit shown beside the value.</summary>
        public string Suffix { get; private set; }

        /// <summary>Shows the unit a number is measured in.</summary>
        /// <param name="suffix">Unit shown beside the value.</param>
        public UnitAttribute(string suffix)
        {
            Suffix = suffix;
        }
    }

    /// <summary>Shows a field without allowing it to be edited, for values the behaviour owns.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class ReadOnlyFieldAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>Replaces the label Unity derives from the field name.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class LabelTextAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Label shown instead of the field name.</summary>
        public string Text { get; private set; }

        /// <summary>Replaces a field's label.</summary>
        /// <param name="text">Label shown instead of the field name.</param>
        public LabelTextAttribute(string text)
        {
            Text = text;
        }
    }

    /// <summary>Widens or narrows one field's label, for a card whose names are longer than usual.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class LabelWidthAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Label width in pixels.</summary>
        public float Width { get; private set; }

        /// <summary>Sets one field's label width.</summary>
        /// <param name="width">Label width in pixels.</param>
        public LabelWidthAttribute(float width)
        {
            Width = width;
        }
    }

    /// <summary>
    /// Drops a field's label entirely and gives the space to the value, for a field whose meaning is
    /// already carried by the card or the note above it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class HideLabelAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>Marks a field with the brand accent, for the one setting in a card that matters most.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class AccentAttribute : ItsRenjiInspectorAttribute
    {
    }

    #endregion

    #region References

    /// <summary>How a turnable preview reacts to being dragged.</summary>
    public enum PreviewRotation
    {
        /// <summary>Pitch is held between straight up and straight down, so the model never rolls over.</summary>
        Clamped,

        /// <summary>Every drag turns the model from where it currently is, with no limit.</summary>
        Freeform,
    }

    /// <summary>Which parts of a referenced asset's own inspector are drawn in place.</summary>
    [Flags]
    public enum InlineEditorMode
    {
        /// <summary>The asset's fields.</summary>
        Gui = 1,

        /// <summary>The asset's inspector header, with its name and icon.</summary>
        Header = 2,

        /// <summary>The asset's own preview, drawn beneath its fields.</summary>
        Preview = 4,

        /// <summary>Fields and preview.</summary>
        GuiAndPreview = Gui | Preview,

        /// <summary>Header and fields.</summary>
        GuiAndHeader = Gui | Header,

        /// <summary>Everything the asset's own inspector would draw.</summary>
        FullEditor = Gui | Header | Preview,
    }

    /// <summary>Shows the art a sprite or texture reference points at, rather than only its name.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class PreviewAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Side of the square preview in pixels, or zero for four rows of the default height.</summary>
        public float Height { get; private set; }

        /// <summary>Whether the reference field itself is drawn beside the preview.</summary>
        public bool DrawField { get; private set; }

        /// <summary>Shows a reference's art beside its field.</summary>
        /// <param name="height">Side of the square preview in pixels.</param>
        /// <param name="drawField">Whether the reference field is drawn beside the preview.</param>
        public PreviewAttribute(float height = 0f, bool drawField = true)
        {
            Height = height;
            DrawField = drawField;
        }
    }

    /// <summary>
    /// Shows a mesh in a viewport that can be dragged to turn it and scrolled to zoom, which is the only
    /// way to judge a shape from the inspector. Reads a Mesh directly, or the first mesh under a
    /// GameObject. Costs a rendered preview, so it is worth it on the references an author really studies.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class InteractivePreviewAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Height of the viewport in pixels.</summary>
        public float Height { get; private set; }

        /// <summary>Width of the viewport in pixels, or a negative number to fill the card.</summary>
        public float Width { get; private set; }

        /// <summary>How the model reacts to being dragged.</summary>
        public PreviewRotation Rotation { get; private set; }

        /// <summary>Shows a mesh in a viewport that can be turned.</summary>
        /// <param name="height">Height of the viewport in pixels.</param>
        /// <param name="width">Width of the viewport, or a negative number to fill the card.</param>
        /// <param name="rotation">How the model reacts to being dragged.</param>
        public InteractivePreviewAttribute(
            float height = 200f,
            float width = -1f,
            PreviewRotation rotation = PreviewRotation.Clamped)
        {
            Height = height;
            Width = width;
            Rotation = rotation;
        }
    }

    /// <summary>
    /// Draws the referenced asset's own inspector underneath the field, so a material or a settings asset
    /// can be edited without leaving the behaviour that points at it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class InlineEditorAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Which parts of the asset's inspector are drawn.</summary>
        public InlineEditorMode Mode { get; private set; }

        /// <summary>Height of the asset's own preview, when that part is drawn.</summary>
        public float PreviewHeight { get; private set; }

        /// <summary>Draws a referenced asset's own inspector in place.</summary>
        /// <param name="mode">Which parts of the asset's inspector are drawn.</param>
        /// <param name="previewHeight">Height of the asset's own preview.</param>
        public InlineEditorAttribute(InlineEditorMode mode = InlineEditorMode.Gui, float previewHeight = 50f)
        {
            Mode = mode;
            PreviewHeight = previewHeight;
        }
    }

    /// <summary>Reports a reference that points into the scene where a project asset was wanted.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class AssetOnlyAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>Reports a reference that points at a project asset where a scene object was wanted.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class SceneOnlyAttribute : ItsRenjiInspectorAttribute
    {
    }

    #endregion

    #region Presentation

    /// <summary>
    /// Opens a run of fields with a heading larger than an eyebrow, ruled off underneath. Where the
    /// eyebrow is a whisper, this is a section break inside a card.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class TitleAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Text shown, or a member name behind a dollar sign.</summary>
        public string Text { get; private set; }

        /// <summary>Whether a rule is drawn under the heading.</summary>
        public bool Line { get; private set; }

        /// <summary>Opens a run of fields with a heading.</summary>
        /// <param name="text">Text shown, or a member name behind a dollar sign.</param>
        /// <param name="line">Whether a rule is drawn under the heading.</param>
        public TitleAttribute(string text, bool line = true)
        {
            Text = text;
            Line = line;
        }
    }

    /// <summary>
    /// Draws a value as plain text with no editing control at all, for something the behaviour reports
    /// rather than something an author sets.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class ShowInInspectorAttribute : Attribute
    {
    }

    /// <summary>
    /// Shows a value the inspector would otherwise never see: a property, or a field that is not
    /// serialized. Read each time the panel refreshes, and written back when it can be written.
    /// <para>
    /// Nothing about it is stored, so it is a window onto the behaviour rather than a setting. On an
    /// UdonSharp behaviour the member wants the editor-only guard, like any other member the program
    /// itself has no use for.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class DisplayAsStringAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>Tints a field's label and its control, for a setting that wants picking out by colour.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class TintAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Red part of the tint, from zero to one.</summary>
        public float Red { get; private set; }

        /// <summary>Green part of the tint, from zero to one.</summary>
        public float Green { get; private set; }

        /// <summary>Blue part of the tint, from zero to one.</summary>
        public float Blue { get; private set; }

        /// <summary>Opacity of the tint, from zero to one.</summary>
        public float Alpha { get; private set; }

        /// <summary>Member naming a colour, or null when the tint is fixed.</summary>
        public string ColourMember { get; private set; }

        /// <summary>Tints a field a fixed colour.</summary>
        /// <param name="red">Red part, from zero to one.</param>
        /// <param name="green">Green part, from zero to one.</param>
        /// <param name="blue">Blue part, from zero to one.</param>
        /// <param name="alpha">Opacity, from zero to one.</param>
        public TintAttribute(float red, float green, float blue, float alpha = 1f)
        {
            Red = red;
            Green = green;
            Blue = blue;
            Alpha = alpha;
        }

        /// <summary>Tints a field a colour the behaviour decides.</summary>
        /// <param name="colourMember">Member naming the colour.</param>
        public TintAttribute(string colourMember)
        {
            ColourMember = colourMember;
            Alpha = 1f;
        }
    }

    /// <summary>
    /// Settles how a list is drawn: whether its entries can be dragged into a new order, whether the
    /// add and remove buttons are offered, and whether it starts open.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class ListSettingsAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Whether entries can be dragged into a new order.</summary>
        public bool Draggable { get; private set; }

        /// <summary>Whether the add button is offered.</summary>
        public bool ShowAdd { get; private set; }

        /// <summary>Whether the remove button is offered.</summary>
        public bool ShowRemove { get; private set; }

        /// <summary>Whether the list starts open rather than folded away.</summary>
        public bool AlwaysExpanded { get; private set; }

        /// <summary>Settles how a list is drawn.</summary>
        /// <param name="draggable">Whether entries can be reordered by dragging.</param>
        /// <param name="showAdd">Whether the add button is offered.</param>
        /// <param name="showRemove">Whether the remove button is offered.</param>
        /// <param name="alwaysExpanded">Whether the list starts open.</param>
        public ListSettingsAttribute(
            bool draggable = true,
            bool showAdd = true,
            bool showRemove = true,
            bool alwaysExpanded = false)
        {
            Draggable = draggable;
            ShowAdd = showAdd;
            ShowRemove = showRemove;
            AlwaysExpanded = alwaysExpanded;
        }
    }

    /// <summary>Draws a whole number as Unity's layer picker.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class LayerFieldAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>Draws a string as Unity's tag picker.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class TagFieldAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>
    /// Draws a field as a list of the values a member offers, rather than as a free control. The member
    /// returns the values, and each one is shown by whatever its own text form is.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class DropdownAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Member returning the values on offer.</summary>
        public string ValuesMember { get; private set; }

        /// <summary>Draws a field as a list of offered values.</summary>
        /// <param name="valuesMember">Member returning the values on offer.</param>
        public DropdownAttribute(string valuesMember)
        {
            ValuesMember = valuesMember;
        }
    }

    /// <summary>Calls a method on the behaviour whenever the field beside it is edited.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class OnValueChangedAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Parameterless method called after the value changes.</summary>
        public string MethodName { get; private set; }

        /// <summary>How much of the scene the method is recorded against for undo.</summary>
        public UndoScope Records { get; set; }

        /// <summary>Calls a method whenever the field is edited.</summary>
        /// <param name="methodName">Parameterless method to call.</param>
        public OnValueChangedAttribute(string methodName)
        {
            MethodName = methodName;
        }
    }

    /// <summary>
    /// Draws a struct's own fields straight into the card instead of behind a fold of their own, for a
    /// small value whose parts read better as siblings of the fields around them.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class InlinePropertyAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>
    /// Draws an array as a table, with one further array supplying each extra column. The arrays are
    /// held at the same length, and adding or removing a row acts on all of them at once.
    /// <para>
    /// Columns are separate arrays rather than fields of a row type because Udon cannot serialize an
    /// array of user structs, so parallel arrays are the only shape a table can actually take in a
    /// behaviour that has to run in a world.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class TableAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Names of the arrays supplying the further columns, in the order they are drawn.</summary>
        public string[] Columns { get; private set; }

        /// <summary>Headings shown over the columns, or null to use the arrays' own names.</summary>
        public string[] Headings { get; set; }

        /// <summary>Draws an array as a table.</summary>
        /// <param name="columns">Names of the arrays supplying the further columns.</param>
        public TableAttribute(params string[] columns)
        {
            Columns = columns;
        }
    }

    #endregion

    #region Play State

    /// <summary>Hides a field while play mode is running.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class HideInPlayModeAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>Shows a field only while play mode is running.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class ShowInPlayModeAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>Hides a field unless play mode is running.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class HideInEditModeAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>Locks a field unless play mode is running, for something only meaningful while running.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class PlayModeOnlyAttribute : ItsRenjiInspectorAttribute
    {
    }

    #endregion

    /// <summary>How much of the scene an action is recorded against, so undo puts all of it back.</summary>
    public enum UndoScope
    {
        /// <summary>Only the behaviour itself changed.</summary>
        Component,

        /// <summary>The object and everything under it changed.</summary>
        Hierarchy,
    }

    #region Pickers

    /// <summary>
    /// Draws a string that holds a scene's asset path as a scene picker, so the path is chosen rather
    /// than typed and cannot drift when the scene is moved.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class SceneFieldAttribute : ItsRenjiInspectorAttribute
    {
    }

    /// <summary>
    /// Draws a reference as a list of the matching assets in the project, which beats the object picker
    /// when the answer is always one of a small known set.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class AssetDropdownAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Search filter the assets are found with, in the project window's own syntax.</summary>
        public string Filter { get; private set; }

        /// <summary>Folders searched, or null to search the whole project.</summary>
        public string[] SearchInFolders { get; set; }

        /// <summary>
        /// Whether the list ends with an entry that opens a slot for an asset from anywhere else. Worth
        /// having whenever the listed assets are a convenience rather than the only allowed answers.
        /// </summary>
        public bool AllowCustom { get; set; }

        /// <summary>Draws a reference as a list of matching assets.</summary>
        /// <param name="filter">Search filter, in the project window's own syntax.</param>
        public AssetDropdownAttribute(string filter)
        {
            Filter = filter;
        }
    }

    /// <summary>
    /// Draws a string or a whole number as a list of an animator's parameters, so a name is chosen from
    /// what the controller actually has rather than typed and silently mismatched.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class AnimatorParameterAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Member holding the animator whose parameters are offered.</summary>
        public string AnimatorMember { get; private set; }

        /// <summary>Kind of parameter offered, when only one kind makes sense.</summary>
        public AnimatorControllerParameterType ParameterType { get; private set; }

        /// <summary>Whether the list is narrowed to one kind of parameter.</summary>
        public bool HasParameterType { get; private set; }

        /// <summary>Offers every parameter of an animator.</summary>
        /// <param name="animatorMember">Member holding the animator.</param>
        public AnimatorParameterAttribute(string animatorMember)
        {
            AnimatorMember = animatorMember;
        }

        /// <summary>Offers the parameters of an animator that are of one kind.</summary>
        /// <param name="animatorMember">Member holding the animator.</param>
        /// <param name="parameterType">Kind of parameter offered.</param>
        public AnimatorParameterAttribute(string animatorMember, AnimatorControllerParameterType parameterType)
        {
            AnimatorMember = animatorMember;
            ParameterType = parameterType;
            HasParameterType = true;
        }
    }

    /// <summary>
    /// Draws a string or a whole number as a list of a material's shader properties, so the name is
    /// chosen from what the shader actually declares.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class MaterialPropertyAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Member holding the material whose properties are offered.</summary>
        public string MaterialMember { get; private set; }

        /// <summary>Kind of property offered, when only one kind makes sense.</summary>
        public ShaderPropertyType PropertyType { get; private set; }

        /// <summary>Whether the list is narrowed to one kind of property.</summary>
        public bool HasPropertyType { get; private set; }

        /// <summary>Offers every property of a material's shader.</summary>
        /// <param name="materialMember">Member holding the material.</param>
        public MaterialPropertyAttribute(string materialMember)
        {
            MaterialMember = materialMember;
        }

        /// <summary>Offers the properties of a material's shader that are of one kind.</summary>
        /// <param name="materialMember">Member holding the material.</param>
        /// <param name="propertyType">Kind of property offered.</param>
        public MaterialPropertyAttribute(string materialMember, ShaderPropertyType propertyType)
        {
            MaterialMember = materialMember;
            PropertyType = propertyType;
            HasPropertyType = true;
        }
    }

    /// <summary>
    /// Reports a component reference that does not point at one of the components actually on this
    /// object, and offers to fetch the right one. Saves the round of dragging that a reference to a
    /// component of the same object is otherwise worth.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class RequiredGetAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Whether a component on this object itself counts.</summary>
        public bool IncludeSelf { get; set; }

        /// <summary>Whether a component under this object counts.</summary>
        public bool InChildren { get; set; }

        /// <summary>Whether a component above this object counts.</summary>
        public bool InParents { get; set; }

        /// <summary>Marks a reference as one the object can fetch for itself.</summary>
        public RequiredGetAttribute()
        {
            IncludeSelf = true;
        }
    }

    #endregion

    #region Actions

    /// <summary>Draws a button that calls one parameterless method on the inspected behaviour.</summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class ButtonAttribute : Attribute
    {
        /// <summary>Text shown on the button, or null to use the method name.</summary>
        public string Label { get; private set; }

        /// <summary>Card the button is drawn into, or null to draw it at the end of the inspector.</summary>
        public string CardName { get; private set; }

        /// <summary>Whether the button carries the brand accent rather than the neutral treatment.</summary>
        public bool Accent { get; private set; }

        /// <summary>Height of the button in pixels, or zero for the default.</summary>
        public float Height { get; private set; }

        /// <summary>
        /// How much of the scene the action is recorded against for undo. An action that repaints or
        /// rebuilds everything under the object wants the whole hierarchy, or undo puts back only the
        /// component and leaves the children as the action left them.
        /// </summary>
        public UndoScope Records { get; set; }

        /// <summary>Draws a button for one method.</summary>
        /// <param name="label">Text shown on the button.</param>
        /// <param name="cardName">Card the button is drawn into.</param>
        /// <param name="accent">Whether the button carries the brand accent.</param>
        /// <param name="height">Height of the button in pixels, or zero for the default.</param>
        public ButtonAttribute(
            string label = null,
            string cardName = null,
            bool accent = false,
            float height = 0f)
        {
            Label = label;
            CardName = cardName;
            Accent = accent;
            Height = height;
        }
    }

    /// <summary>
    /// Draws a small button on the same line as a field, for the action that fills that one field in.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    [System.Diagnostics.Conditional("UNITY_EDITOR")]
    public sealed class InlineButtonAttribute : ItsRenjiInspectorAttribute
    {
        /// <summary>Name of the parameterless method the button calls.</summary>
        public string MethodName { get; private set; }

        /// <summary>Text shown on the button, or null to use the method name.</summary>
        public string Label { get; private set; }

        /// <summary>Width of the button in pixels, or zero to size it to its text.</summary>
        public float ButtonWidth { get; private set; }

        /// <summary>Name of a bool member deciding whether the button reads as pressed, or null.</summary>
        public string IsChecked { get; private set; }

        /// <summary>Draws a small button beside one field.</summary>
        /// <param name="methodName">Parameterless method the button calls.</param>
        /// <param name="label">Text shown on the button.</param>
        /// <param name="buttonWidth">Width of the button, or zero to size it to its text.</param>
        /// <param name="isChecked">Bool member deciding whether the button reads as pressed.</param>
        public InlineButtonAttribute(
            string methodName,
            string label = null,
            float buttonWidth = 0f,
            string isChecked = null)
        {
            MethodName = methodName;
            Label = label;
            ButtonWidth = buttonWidth;
            IsChecked = isChecked;
        }
    }

    #endregion
}
