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
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ItsRenji.Inspector.Editors
{
    /// <summary>
    /// Base for every branded ItsRenji inspector. Draws the shared header and lays the inspected object's
    /// serialized fields out from the attributes they carry, so an inspector normally needs no drawing code
    /// of its own: a behaviour states its own layout beside its fields and this reads it back.
    /// <para>
    /// Built on UI Toolkit rather than IMGUI because the house look depends on rounded corners, hairline
    /// borders, translucent fills and hover states, none of which IMGUI can draw. The three places that
    /// still host IMGUI are the ones with no UI Toolkit equivalent at all: the rendered asset preview, the
    /// turnable mesh viewport, and an inspected asset's own header and preview.
    /// </para>
    /// </summary>
    public abstract partial class ItsRenjiInspector : UnityEditor.Editor
    {
        #region Constants

        /// <summary>
        /// GUID of the stylesheet carrying the whole visual language, so a restyle is one file.
        /// <para>
        /// Held as a GUID rather than a path on purpose. A path constant breaks the moment anyone drags
        /// the folder, and it breaks SILENTLY: no compile error, no warning, just every inspector in the
        /// product quietly falling back to the editor's own skin. A GUID lives in the asset's .meta and
        /// survives both a move and a rename, which is the only thing here that is actually stable.
        /// </para>
        /// </summary>
        private const string STYLE_GUID = "b32ec48f162429740a9057bc4f46a08e";

        /// <summary>
        /// Asset name of that stylesheet, used only to find it again if the GUID ever stops resolving -
        /// which happens when the file is deleted and re-created rather than moved, because that is a
        /// new asset with a new GUID.
        /// </summary>
        private const string STYLE_NAME = "ItsRenjiInspector";

        /// <summary>Resources name of the shared brand mark.</summary>
        private const string LOGO_RESOURCE = "ItsRenjiLogo";

        /// <summary>Separates nested group names, so "Shape/Corners" sits inside "Shape".</summary>
        private const char GROUP_SEPARATOR = '/';

        /// <summary>Joins the parts of a group key. Never legal inside a tab or group name.</summary>
        private const char KEY_SEPARATOR = '|';

        /// <summary>Marks the row part of a group key so a row can never be mistaken for a card.</summary>
        private const string ROW_KEY = "row";

        /// <summary>Unity's own script reference, which the branded header replaces.</summary>
        private const string SCRIPT_PROPERTY = "m_Script";

        /// <summary>Name UdonSharp gives the violet bar it parents to a U# behaviour's host element.</summary>
        private const string HOST_SCRIPT_BAR = "UdonSharpScriptBar";

        /// <summary>Prefix of the editor preference remembering which tab was last open.</summary>
        private const string TAB_PREF_PREFIX = "ItsRenji.Inspector.Tab.";

        /// <summary>Prefix of the editor preference remembering whether a fold was left open.</summary>
        private const string FOLD_PREF_PREFIX = "ItsRenji.Inspector.Fold.";

        /// <summary>Tolerance used when a condition compares floating point values.</summary>
        private const float CONDITION_EPSILON = 0.0001f;

        /// <summary>Pixels one indent step moves a field in by.</summary>
        private const float INDENT_STEP = 15f;

        /// <summary>
        /// The switch on a slider that decides whether it drags an out-of-range value into range. It is
        /// internal to the engine, so it is read once here and simply left alone on a Unity version that
        /// has renamed it.
        /// </summary>
        private static readonly PropertyInfo SLIDER_CLAMPED = typeof(Slider).GetProperty(
            "clamped",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>The same switch on a whole-number slider.</summary>
        private static readonly PropertyInfo SLIDER_INT_CLAMPED = typeof(SliderInt).GetProperty(
            "clamped",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        /// <summary>
        /// How often the elements that cannot be driven from a change event re-read what they show, in
        /// milliseconds. Matches the interval the reference implementation polls at.
        /// </summary>
        private const long POLL_INTERVAL = 100;

        #endregion

        #region Private State

        /// <summary>Group content containers built so far, keyed by tab, path and row.</summary>
        private readonly Dictionary<string, VisualElement> _groups = new Dictionary<string, VisualElement>();

        /// <summary>Tab pages built so far, keyed by tab name.</summary>
        private readonly Dictionary<string, VisualElement> _tabPages = new Dictionary<string, VisualElement>();

        /// <summary>Tab buttons in the strip, in the same order as the page names.</summary>
        private readonly List<Button> _tabButtons = new List<Button>();

        /// <summary>Tab names in the order the first field asking for each was declared.</summary>
        private readonly List<string> _tabNames = new List<string>();

        /// <summary>Tab currently shown, held so the strip can re-assert it on its own schedule.</summary>
        private string _activeTab;

        /// <summary>Stylesheet found once and shared by every inspector built on this base.</summary>
        private static StyleSheet _styleSheet;

        /// <summary>
        /// Everything that has to be re-evaluated when a value changes: required notices, validation,
        /// conditions and previews. Driven from one serialized-object subscription rather than one per
        /// field, so a change made by code (an action button) refreshes the panel exactly like a typed one.
        /// </summary>
        private readonly List<Action> _refreshers = new List<Action>();

        /// <summary>
        /// Authored values of each scripted-behaviour target as its running program last received them, by
        /// field name. Taken when the inspector is built and kept level with the proxy outside play mode, so
        /// in play mode an edit shows up as the difference and nothing else is written.
        /// </summary>
        private readonly Dictionary<UnityEngine.Object, Dictionary<string, object>> _pushedValues =
            new Dictionary<UnityEngine.Object, Dictionary<string, object>>();

        /// <summary>Authored fields found per behaviour type, so each type is reflected over once.</summary>
        private static readonly Dictionary<Type, List<FieldInfo>> _authoredFields = new Dictionary<Type, List<FieldInfo>>();

        /// <summary>Strip holding the tab buttons, or null while no field has asked for a tab.</summary>
        private VisualElement _tabStrip;

        /// <summary>Element the tab pages are added to, or null while there are no tabs.</summary>
        private VisualElement _tabHost;

        /// <summary>
        /// The masthead panel under the header: the fields that belong to no tab and no card, and the
        /// navigation itself. Dropped again when a behaviour turns out to have nothing to put in it.
        /// </summary>
        private VisualElement _chrome;

        #endregion

        #region Inspector Identity

        /// <summary>Name shown under the brand mark. Defaults to the type name split into words.</summary>
        protected virtual string Title => ObjectNames.NicifyVariableName(target.GetType().Name);

        /// <summary>One line saying what the behaviour is for, shown under the title.</summary>
        protected virtual string Subtitle => string.Empty;

        /// <summary>Type the attributes' member names are resolved against.</summary>
        private Type TargetType => target.GetType();

        #endregion

        #region Unity Editor Lifecycle

        /// <summary>Builds the branded inspector and keeps the backing Udon program in step with it.</summary>
        /// <returns>Root element for the inspector.</returns>
        public override VisualElement CreateInspectorGUI()
        {
            ResetLayoutState();

            VisualElement root = new VisualElement();
            root.AddToClassList("ir-root");

            StyleSheet sheet = LoadStyleSheet();
            if (sheet != null) root.styleSheets.Add(sheet);

            // The night runs the full height of the panel, not just the roof, so every card reads as a
            // lit surface lying on it. The starfield is drawn rather than shipped as art, so the stars
            // stay hard single pixels and cannot band.
            root.style.backgroundImage = new StyleBackground(ItsRenjiInspectorStarfield.Tile);

            // The roof: the mark and the masthead panel, over the same night as everything below them.
            VisualElement masthead = new VisualElement();
            masthead.AddToClassList("ir-masthead");
            root.Add(masthead);

            masthead.Add(BuildHeader());

            _chrome = new VisualElement();
            _chrome.AddToClassList("ir-card");
            _chrome.AddToClassList("ir-chrome");
            masthead.Add(_chrome);

            BuildFields(root);
            BuildButtons(root);
            BuildCustomSections(root);
            SelectTab(EditorPrefs.GetString(TAB_PREF_PREFIX + target.GetType().FullName, string.Empty));
            RunRefreshers();

            // A behaviour with no loose fields and no tabs has nothing to put in the masthead, and an
            // empty panel of leather reads worse than none at all.
            if (_chrome.childCount == 0) masthead.Remove(_chrome);

            // The values the program was last given, so a push in play mode can tell an edit from the rest.
            RememberPushedValues();

            // A UI Toolkit field writes straight through to the serialized object, so the proxy has to be
            // pushed on change or the running program keeps the value it started with.
            root.TrackSerializedObjectValue(serializedObject, _ => OnSerializedObjectChanged());

            root.RegisterCallback<AttachToPanelEvent>(_ => BleedToPanelEdges(root));

            return root;
        }

        /// <summary>
        /// Takes the padding off the hosts this panel sits in, so the panel reaches both edges of the
        /// window, and hides the violet bar UdonSharp paints down the left of a U# behaviour's block.
        /// <para>
        /// Two separate insets have to go. Unity's inspector element pads left and right, and UdonSharp's
        /// editor parents an absolutely positioned child named UdonSharpScriptBar to the element above
        /// that, widening its left padding to 4 to make room for a 3px violet strip. UdonSharp's own
        /// source calls the bar jank and notes that it bleeds into inspectors drawing their own chrome,
        /// which is exactly this case. Both hosts are ancestors of ours and a stylesheet only reaches
        /// descendants, which is why this is set here rather than in the sheet.
        /// </para>
        /// </summary>
        /// <param name="root">Root of this inspector.</param>
        private static void BleedToPanelEdges(VisualElement root)
        {
            for (VisualElement parent = root.parent; parent != null; parent = parent.parent)
            {
                if (!parent.ClassListContains("unity-inspector-element")) continue;

                parent.style.paddingLeft = 0;
                parent.style.paddingRight = 0;
                HideHostScriptBar(parent.parent);
                return;
            }
        }

        /// <summary>
        /// Hides UdonSharp's left edge script bar on the element hosting this component and closes the
        /// padding reserved for it. Does nothing when no such bar is there, which covers every inspector
        /// drawn for a plain MonoBehaviour and any SDK version that stops adding one.
        /// <para>
        /// The bar is hidden rather than removed so UdonSharp's own detach handler still finds its
        /// element and disposes of it, which leaves ownership of it where it started.
        /// </para>
        /// </summary>
        /// <param name="host">Element wrapping this component's inspector, or null at the top of the tree.</param>
        private static void HideHostScriptBar(VisualElement host)
        {
            if (host == null) return;

            foreach (VisualElement child in host.Children())
            {
                if (child.name != HOST_SCRIPT_BAR) continue;

                child.style.display = DisplayStyle.None;
                host.style.paddingLeft = 0;
                return;
            }
        }

        /// <summary>
        /// Finds the stylesheet by GUID, falling back to a search by name. Cached because every selection
        /// builds an inspector and neither lookup is worth repeating.
        /// </summary>
        /// <returns>The stylesheet, or null if the project no longer holds one.</returns>
        private static StyleSheet LoadStyleSheet()
        {
            if (_styleSheet != null) return _styleSheet;

            string path = AssetDatabase.GUIDToAssetPath(STYLE_GUID);
            if (!string.IsNullOrEmpty(path)) _styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(path);
            if (_styleSheet != null) return _styleSheet;

            // The GUID resolved to nothing, so the sheet was re-created rather than moved. Find it by
            // name instead, and take the first match: two of them would be a duplicate to delete, not a
            // choice to make here.
            string[] found = AssetDatabase.FindAssets("t:StyleSheet " + STYLE_NAME);
            for (int i = 0; i < found.Length; i++)
            {
                string candidate = AssetDatabase.GUIDToAssetPath(found[i]);
                _styleSheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(candidate);
                if (_styleSheet != null) return _styleSheet;
            }

            return null;
        }

        /// <summary>
        /// Adds anything the attributes cannot express. Called once per rebuild, after the fields and
        /// buttons and before the refreshers run, so a section added here can read the values above it.
        /// <para>
        /// This exists for a TOOL rather than for layout. A behaviour whose inspector is a property
        /// sheet needs nothing here: grouping, notices, conditions and buttons are all attributes, and
        /// reaching for this to lay out fields is the IMGUI habit arriving by another door. It is for an
        /// editor that genuinely owns something the attributes do not model - an async search, a live
        /// preview, transient state that is never serialized - and such a section is built from the same
        /// USS classes as everything else (`ir-card`, `ir-note`, `ir-button`) so it reads as one panel.
        /// </para>
        /// </summary>
        /// <param name="root">Root of this inspector, to append sections to.</param>
        protected virtual void BuildCustomSections(VisualElement root)
        {
        }

        /// <summary>Drops the layout built for a previous selection so a rebuild starts clean.</summary>
        private void ResetLayoutState()
        {
            _groups.Clear();
            _tabPages.Clear();
            _tabButtons.Clear();
            _tabNames.Clear();
            _refreshers.Clear();
            _tabStrip = null;
            _tabHost = null;
            _chrome = null;
            _activeTab = null;
        }

        /// <summary>Pushes the edit to Udon and re-evaluates everything that reads a value.</summary>
        private void OnSerializedObjectChanged()
        {
            PushToUdon();
            RunRefreshers();
        }

        /// <summary>Runs every registered refresher, skipping a target that has been destroyed.</summary>
        private void RunRefreshers()
        {
            if (target == null) return;

            for (int i = 0; i < _refreshers.Count; i++)
            {
                _refreshers[i]();
            }
        }

        #endregion

        #region Header

        /// <summary>Builds the brand mark, title and subtitle block.</summary>
        /// <returns>Header element.</returns>
        private VisualElement BuildHeader()
        {
            VisualElement header = new VisualElement();
            header.AddToClassList("ir-header");

            Texture2D logo = Resources.Load<Texture2D>(LOGO_RESOURCE);
            if (logo != null)
            {
                Image mark = new Image { image = logo, scaleMode = ScaleMode.ScaleToFit };
                mark.AddToClassList("ir-header__logo");
                header.Add(mark);
            }

            Label title = new Label(Title);
            title.AddToClassList("ir-header__title");
            header.Add(title);

            if (!string.IsNullOrEmpty(Subtitle))
            {
                Label subtitle = new Label(Subtitle);
                subtitle.AddToClassList("ir-header__subtitle");
                header.Add(subtitle);
            }

            return header;
        }

        #endregion

        #region Field Layout

        /// <summary>
        /// Collects the visible serialized fields, puts them in drawing order, and places each one where
        /// its attributes say. Tabs, cards, folds and rows are created as they are first named, so the
        /// order the fields end up in is the order the groups appear in.
        /// </summary>
        /// <param name="root">Element receiving the laid-out fields.</param>
        private void BuildFields(VisualElement root)
        {
            List<SerializedProperty> properties = new List<SerializedProperty>();
            List<MemberInfo> members = new List<MemberInfo>();
            List<int> keys = new List<int>();

            SerializedProperty property = serializedObject.GetIterator();
            bool enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (property.propertyPath == SCRIPT_PROPERTY) continue;

                FieldInfo field = FindField(property.name);
                OrderAttribute order = GetAttribute<OrderAttribute>(field);

                properties.Add(property.Copy());
                members.Add(field);
                keys.Add(order == null ? 0 : order.Order);
            }

            CollectShownMembers(properties, members, keys);

            int[] sequence = BuildDrawOrder(keys);
            for (int i = 0; i < sequence.Length; i++)
            {
                int index = sequence[i];

                VisualElement host = ResolveHost(root, members[index]);
                VisualElement block = BuildBlock(properties[index], members[index]);

                host.Add(block);
                ApplyConditions(block, members[index]);
            }
        }

        /// <summary>
        /// Adds the members that carry no serialized value but were asked for anyway, so a computed
        /// property can sit beside the fields it is worked out from and order itself among them.
        /// </summary>
        /// <param name="properties">Serialized properties collected so far, with a null per member.</param>
        /// <param name="members">Members collected so far.</param>
        /// <param name="keys">Order key per entry.</param>
        private void CollectShownMembers(
            List<SerializedProperty> properties,
            List<MemberInfo> members,
            List<int> keys)
        {
            List<string> seen = new List<string>();
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i] != null) seen.Add(members[i].Name);
            }

            Type type = TargetType;
            while (type != null && type != typeof(MonoBehaviour) && type != typeof(object))
            {
                MemberInfo[] candidates = type.GetMembers(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                for (int i = 0; i < candidates.Length; i++)
                {
                    MemberInfo candidate = candidates[i];

                    if (!(candidate is FieldInfo) && !(candidate is PropertyInfo)) continue;
                    if (candidate.GetCustomAttribute<ShowInInspectorAttribute>() == null) continue;
                    if (seen.Contains(candidate.Name)) continue;
                    seen.Add(candidate.Name);

                    OrderAttribute order = GetAttribute<OrderAttribute>(candidate);

                    properties.Add(null);
                    members.Add(candidate);
                    keys.Add(order == null ? 0 : order.Order);
                }

                type = type.BaseType;
            }
        }

        /// <summary>
        /// Sorts field indices by their order key, keeping fields that share a key in the order they were
        /// declared. Comparing the index as the tie-break is what makes the sort stable, which the
        /// framework's own Sort does not promise.
        /// </summary>
        /// <param name="keys">Order key per field, in declaration order.</param>
        /// <returns>Field indices in the order they should be drawn.</returns>
        private static int[] BuildDrawOrder(List<int> keys)
        {
            int[] sequence = new int[keys.Count];
            for (int i = 0; i < sequence.Length; i++)
            {
                sequence[i] = i;
            }

            Array.Sort(sequence, (left, right) => keys[left] == keys[right]
                ? left.CompareTo(right)
                : keys[left].CompareTo(keys[right]));

            return sequence;
        }

        /// <summary>
        /// Builds the whole run of elements one field owns: its separators, its guidance, the control and
        /// any notice or preview beneath it. Keeping them in one block is what lets a condition hide or
        /// grey the field together with everything that explains it.
        /// </summary>
        /// <param name="property">Property being drawn.</param>
        /// <param name="field">Backing field, or null when it could not be resolved.</param>
        /// <returns>The block element.</returns>
        private VisualElement BuildBlock(SerializedProperty property, MemberInfo field)
        {
            VisualElement block = new VisualElement();
            block.AddToClassList("ir-block");

            IndentAttribute indent = GetAttribute<IndentAttribute>(field);
            if (indent != null) block.style.marginLeft = indent.Steps * INDENT_STEP - 1f;

            if (GetAttribute<AccentAttribute>(field) != null) block.AddToClassList("ir-block--accent");

            AddDivider(block, field);
            AddGap(block, field);
            AddTitle(block, field);
            AddEyebrow(block, field);
            AddNote(block, field);
            AddWarning(block, field);

            VisualElement control = BuildControl(block, property, field);

            ApplyTint(control, field);
            ApplyListSettings(control, field);
            ApplyInlineProperty(control, field);
            HookValueChanged(control, property, field);
            ApplyPlayStateVisibility(block, field);

            AddRequired(block, control, property, field);
            AddRequiredGet(block, property, field);
            AddOriginCheck(block, property, field);
            AddValidation(block, field);
            AddInteractivePreview(block, property, field);
            AddInlineEditor(block, property, field);

            return block;
        }

        /// <summary>
        /// Builds the control itself and adds it to the block, putting it on a line of its own alongside
        /// whatever shares that line: an inline button, a unit, or a square asset preview.
        /// </summary>
        /// <param name="block">Element receiving the control.</param>
        /// <param name="property">Property being drawn.</param>
        /// <param name="field">Backing field, or null.</param>
        /// <returns>The control, which is what a required notice marks.</returns>
        private VisualElement BuildControl(VisualElement block, SerializedProperty property, MemberInfo field)
        {
            LabelTextAttribute labelText = GetAttribute<LabelTextAttribute>(field);
            ValueSource<string> labelSource = labelText == null
                ? null
                : ValueSource<string>.ForText(TargetType, labelText.Text);

            string plain = property == null
                ? ObjectNames.NicifyVariableName(field.Name)
                : property.displayName;

            string label = labelSource == null
                ? plain
                : labelSource.GetValue(target, labelText.Text);

            VisualElement control = BuildBoundControl(property, field, label);
            control.AddToClassList("ir-field");

            if (labelSource != null) TrackLabelText(control, labelSource, label);

            if (GetAttribute<HideLabelAttribute>(field) != null) control.AddToClassList("ir-field--nolabel");

            ApplyEditability(control, field);

            LabelWidthAttribute labelWidth = GetAttribute<LabelWidthAttribute>(field);
            if (labelWidth != null) BindLabelWidth(control, labelWidth.Width);

            VisualElement body = WrapUnit(control, GetAttribute<UnitAttribute>(field));

            List<VisualElement> trailing = BuildTrailing(property, field);
            if (trailing.Count == 0)
            {
                block.Add(body);
                return control;
            }

            VisualElement line = new VisualElement();
            line.AddToClassList("ir-line");
            body.AddToClassList("ir-line__field");
            line.Add(body);

            for (int i = 0; i < trailing.Count; i++)
            {
                line.Add(trailing[i]);
            }

            block.Add(line);
            return control;
        }

        /// <summary>Builds everything that shares the field's own line, in the order it is drawn.</summary>
        /// <param name="property">Property being drawn.</param>
        /// <param name="field">Backing field, or null.</param>
        /// <returns>Elements to place after the control, which may be empty.</returns>
        private List<VisualElement> BuildTrailing(SerializedProperty property, MemberInfo field)
        {
            List<VisualElement> trailing = new List<VisualElement>();

            InlineButtonAttribute inline = GetAttribute<InlineButtonAttribute>(field);
            if (inline != null)
            {
                Button button = BuildInlineButton(inline);
                if (button != null) trailing.Add(button);
            }

            PreviewAttribute preview = GetAttribute<PreviewAttribute>(field);
            if (preview != null && property != null && property.propertyType == SerializedPropertyType.ObjectReference)
            {
                AssetThumbnail thumbnail = new AssetThumbnail(property.Copy(), preview.Height);
                _refreshers.Add(thumbnail.Refresh);
                trailing.Add(thumbnail);
            }

            return trailing;
        }

        /// <summary>
        /// Lays the unit over the right-hand end of the field rather than beside it, so naming what a
        /// number measures costs the number none of its width.
        /// </summary>
        /// <param name="control">Control the unit belongs to.</param>
        /// <param name="unit">Attribute carrying the unit, or null.</param>
        /// <returns>The control, or a container holding the control and the unit over it.</returns>
        private VisualElement WrapUnit(VisualElement control, UnitAttribute unit)
        {
            if (unit == null) return control;

            VisualElement container = new VisualElement();
            container.AddToClassList("ir-unit");
            container.Add(control);

            Label suffix = new Label();

            // The label sits over the field, so it must not swallow the clicks meant for the field.
            suffix.pickingMode = PickingMode.Ignore;
            suffix.AddToClassList("ir-unit__suffix");
            container.Add(suffix);

            ValueSource<string> text = ValueSource<string>.ForText(TargetType, unit.Suffix);
            _refreshers.Add(() => suffix.text = text.GetValue(target, unit.Suffix));

            return container;
        }

        /// <summary>Keeps a label in step with the member its text was read from.</summary>
        /// <param name="control">Control carrying the label.</param>
        /// <param name="source">Where the text comes from.</param>
        /// <param name="fallback">Text used when the member cannot be read.</param>
        private void TrackLabelText(VisualElement control, ValueSource<string> source, string fallback)
        {
            _refreshers.Add(() =>
            {
                Label label = control.Q<Label>(className: "unity-base-field__label");
                if (label != null) label.text = source.GetValue(target, fallback);
            });
        }

        /// <summary>Creates and binds the field's editing control.</summary>
        /// <param name="property">Property being drawn.</param>
        /// <param name="field">Backing field, or null.</param>
        /// <param name="label">Label the control shows.</param>
        /// <returns>The bound control.</returns>
        private VisualElement BuildBoundControl(SerializedProperty property, MemberInfo field, string label)
        {
            // A member asked for by attribute alone has no serialized value behind it, so it is read and
            // written through the member itself rather than bound to a property.
            if (property == null) return BuildMemberControl(field, label);

            TableAttribute table = GetAttribute<TableAttribute>(field);
            if (table != null && property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                return BuildTable(property, label, table);
            }

            if (GetAttribute<DisplayAsStringAttribute>(field) != null)
            {
                return BuildReadout(label, field);
            }

            if (GetAttribute<SceneFieldAttribute>(field) != null
                && property.propertyType == SerializedPropertyType.String)
            {
                return BuildSceneField(property, label);
            }

            AssetDropdownAttribute assets = GetAttribute<AssetDropdownAttribute>(field);
            if (assets != null && property.propertyType == SerializedPropertyType.ObjectReference)
            {
                return BuildAssetDropdown(property, field, label, assets);
            }

            AnimatorParameterAttribute animator = GetAttribute<AnimatorParameterAttribute>(field);
            if (animator != null && IsNameOrId(property))
            {
                return BuildAnimatorParameter(property, label, animator);
            }

            MaterialPropertyAttribute shaderProperty = GetAttribute<MaterialPropertyAttribute>(field);
            if (shaderProperty != null && IsNameOrId(property))
            {
                return BuildMaterialProperty(property, label, shaderProperty);
            }

            if (GetAttribute<LayerFieldAttribute>(field) != null
                && property.propertyType == SerializedPropertyType.Integer)
            {
                LayerField layers = new LayerField(label);
                layers.BindProperty(property.Copy());
                return layers;
            }

            if (GetAttribute<TagFieldAttribute>(field) != null
                && property.propertyType == SerializedPropertyType.String)
            {
                TagField tags = new TagField(label);
                tags.BindProperty(property.Copy());
                return tags;
            }

            DropdownAttribute dropdown = GetAttribute<DropdownAttribute>(field);
            if (dropdown != null && field != null)
            {
                return BuildDropdown(label, field, dropdown);
            }

            ChoicesAttribute choices = GetAttribute<ChoicesAttribute>(field);
            if (choices != null && property.propertyType == SerializedPropertyType.Enum)
            {
                return BuildChoices(property, label, property.enumDisplayNames, true);
            }

            if (choices != null
                && choices.Options != null
                && choices.Options.Length > 0
                && property.propertyType == SerializedPropertyType.Integer)
            {
                return BuildChoices(property, label, choices.Options, false);
            }

            MinMaxClampAttribute range = GetAttribute<MinMaxClampAttribute>(field);
            if (range != null && property.propertyType == SerializedPropertyType.Vector2)
            {
                return BuildRange(property, label, range);
            }

            ClampAttribute clamp = GetAttribute<ClampAttribute>(field);

            if (clamp != null && property.propertyType == SerializedPropertyType.Integer)
            {
                return BuildIntegerSlider(property, label, clamp);
            }

            if (clamp != null && property.propertyType == SerializedPropertyType.Float)
            {
                return BuildFloatSlider(property, label, clamp);
            }

            if (property.propertyType == SerializedPropertyType.Color)
            {
                return BuildSwatch(property, label);
            }

            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                Type referenced = MemberType(field);
                if (IsProgramBehaviour(referenced)) return BuildBehaviourReference(property, label, referenced);
            }

            if (property.propertyType == SerializedPropertyType.Boolean)
            {
                return BuildToggle(property, label);
            }

            PropertyField plain = new PropertyField(property.Copy(), label);
            plain.Bind(serializedObject);
            return plain;
        }

        /// <summary>
        /// True when a type is a scripted-behaviour proxy that ships its own property drawer. Matched by
        /// walking the base chain for the name rather than by referencing the type, so this framework
        /// still compiles and runs in a project that does not have that package at all.
        /// </summary>
        /// <param name="type">Field type being drawn.</param>
        /// <returns>Whether the reference needs drawing by hand.</returns>
        private static bool IsProgramBehaviour(Type type)
        {
            for (Type walk = type; walk != null; walk = walk.BaseType)
            {
                if (walk.Name == "UdonSharpBehaviour") return true;
            }

            return false;
        }

        /// <summary>Draws a bool as a well with a tick inside it.</summary>
        /// <param name="property">Bool being drawn.</param>
        /// <param name="label">Label shown beside the field.</param>
        /// <returns>The toggle.</returns>
        private VisualElement BuildToggle(SerializedProperty property, string label)
        {
            Toggle toggle = WithTick(new Toggle(label));
            toggle.AddToClassList("unity-base-field__aligned");
            toggle.BindProperty(property.Copy());
            return toggle;
        }

        /// <summary>
        /// Gives one toggle the tick its well cannot draw for itself.
        /// <para>
        /// Unity's checkmark is ONE element, so a stylesheet can give it a well or a tick and never both.
        /// Filling the well with the accent colour reads as decoration rather than as a control, and
        /// replacing the well with a bare tick leaves a toggle that is ON by default looking like nothing
        /// at all. UI Toolkit has no ::before to add a second shape with, so the tick is a real child and
        /// the stylesheet only reveals it while the toggle is checked.
        /// </para>
        /// <para>
        /// EVERY toggle is passed through here, including one a product builds by hand for a tool of its
        /// own. One that misses it still recolours its outline when checked, so it looks deliberate and
        /// simply never shows a tick - which is how the table cells, the button parameters and a product's
        /// own toggle all shipped tickless while the attributed fields looked right.
        /// </para>
        /// </summary>
        /// <param name="toggle">Toggle being given its tick.</param>
        /// <returns>The same toggle, so this can wrap a constructor call.</returns>
        public static Toggle WithTick(Toggle toggle)
        {
            if (toggle == null) return null;

            VisualElement checkmark = toggle.Q<VisualElement>(null, "unity-toggle__checkmark");
            if (checkmark == null) return toggle;
            if (checkmark.Q<VisualElement>(null, "ir-tick") != null) return toggle;

            VisualElement tick = new VisualElement();
            tick.AddToClassList("ir-tick");

            // Decoration sitting over the well, so it must not swallow the click that toggles it.
            tick.pickingMode = PickingMode.Ignore;
            checkmark.Add(tick);

            return toggle;
        }

        /// <summary>
        /// Draws a reference to another scripted behaviour.
        /// <para>
        /// A plain PropertyField cannot be used for one. Those types ship a custom PropertyDrawer, and
        /// Unity hosts ANY custom drawer inside an IMGUIContainer, which paints from the editor's own
        /// skin and cannot be reached by a stylesheet - so the field lands as a black slab in the middle
        /// of a light panel. The same trap as the colour field, arriving through a different door, and
        /// it bites hardest here because behaviours reference each other constantly. Building the field
        /// directly keeps it in UI Toolkit; the value written is the same proxy reference either way.
        /// </para>
        /// </summary>
        /// <param name="property">Reference property being edited.</param>
        /// <param name="label">Label shown beside the field.</param>
        /// <param name="referenced">Type the field accepts.</param>
        /// <returns>The reference field.</returns>
        private VisualElement BuildBehaviourReference(SerializedProperty property, string label, Type referenced)
        {
            ObjectField field = new ObjectField(label)
            {
                objectType = referenced,
                allowSceneObjects = true,
            };

            field.AddToClassList("unity-base-field__aligned");
            field.BindProperty(property.Copy());
            return field;
        }

        /// <summary>
        /// Builds the colour swatch, which exists because Unity's own colour field cannot be styled.
        /// Its input is an IMGUIContainer, and IMGUI paints from the editor's skin, so no rule in the
        /// sheet reaches inside it: on the leather it stayed a light block with a black hover while
        /// everything around it changed. This draws the swatch itself and hands the click to the same
        /// colour picker Unity would have opened.
        /// </summary>
        /// <param name="property">Colour property being edited.</param>
        /// <param name="label">Label shown beside the swatch.</param>
        /// <returns>The swatch field.</returns>
        private VisualElement BuildSwatch(SerializedProperty property, string label)
        {
            SerializedProperty bound = property.Copy();

            // The Unity field classes are kept so this lines its label up with every ordinary field
            // above and below it. Only the input is ours.
            VisualElement row = new VisualElement();
            row.AddToClassList("unity-base-field");
            row.AddToClassList("unity-base-field__aligned");
            row.AddToClassList("ir-swatch-field");

            Label caption = new Label(label);
            caption.AddToClassList("unity-base-field__label");
            row.Add(caption);

            VisualElement well = new VisualElement();
            well.AddToClassList("unity-base-field__input");
            well.AddToClassList("ir-swatch");
            row.Add(well);

            VisualElement fill = new VisualElement();
            fill.AddToClassList("ir-swatch__fill");
            fill.style.backgroundColor = bound.colorValue;
            well.Add(fill);

            // The value can change from outside this control, through an undo or another inspector, so
            // the fill is tracked rather than written once.
            row.TrackPropertyValue(bound, changed => fill.style.backgroundColor = changed.colorValue);

            well.RegisterCallback<PointerDownEvent>(_ => ShowColourPicker(bound, fill));

            return row;
        }

        /// <summary>
        /// Opens Unity's own colour picker on a property. Reached by reflection because the picker type
        /// is internal, which is also why the swatch above exists rather than a plain colour field.
        /// </summary>
        /// <param name="property">Property the chosen colour is written to.</param>
        /// <param name="fill">Element showing the colour, repainted as the picker moves.</param>
        private void ShowColourPicker(SerializedProperty property, VisualElement fill)
        {
            Type picker = Type.GetType("UnityEditor.ColorPicker, UnityEditor");
            if (picker == null) return;

            MethodInfo show = picker.GetMethod(
                "Show",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(Action<Color>), typeof(Color), typeof(bool), typeof(bool) },
                null);
            if (show == null) return;

            Action<Color> chosen = colour =>
            {
                if (property.serializedObject == null) return;

                property.serializedObject.Update();
                property.colorValue = colour;
                property.serializedObject.ApplyModifiedProperties();

                fill.style.backgroundColor = colour;
                OnSerializedObjectChanged();
            };

            show.Invoke(null, new object[] { chosen, property.colorValue, true, false });
        }

        /// <summary>Builds the bounded slider for a whole number.</summary>
        /// <param name="property">Property being edited.</param>
        /// <param name="label">Label the slider shows.</param>
        /// <param name="clamp">Attribute carrying the bounds.</param>
        /// <returns>The slider.</returns>
        private VisualElement BuildIntegerSlider(SerializedProperty property, string label, ClampAttribute clamp)
        {
            Func<float> low = BoundOf(clamp.MinimumMember, clamp.Minimum);
            Func<float> high = BoundOf(clamp.MaximumMember, clamp.Maximum);

            SliderInt slider = new SliderInt(label, Mathf.RoundToInt(low()), Mathf.RoundToInt(high()));
            slider.showInputField = true;

            SerializedProperty tracked = property.Copy();
            slider.RegisterValueChangedCallback(evt => WriteInteger(tracked, evt.newValue));
            _refreshers.Add(() => RefreshIntegerSlider(
                slider,
                tracked,
                Mathf.RoundToInt(low()),
                Mathf.RoundToInt(high())));

            return slider;
        }

        /// <summary>
        /// Returns how one bound of a slider is worked out: a fixed number, or whatever the behaviour
        /// currently says. A bound that names a member accepts either a whole or a decimal number,
        /// because both read naturally as a limit.
        /// </summary>
        /// <param name="member">Member naming the bound, or null when the bound is fixed.</param>
        /// <param name="fixedValue">Bound used when no member was named or none could be read.</param>
        /// <returns>A reader for the bound.</returns>
        private Func<float> BoundOf(string member, float fixedValue)
        {
            if (string.IsNullOrEmpty(member)) return () => fixedValue;

            ValueSource<float> asFloat = ValueSource<float>.ForMember(TargetType, member);
            if (asFloat.Error == null) return () => asFloat.GetValue(target, fixedValue);

            ValueSource<int> asInteger = ValueSource<int>.ForMember(TargetType, member);
            if (asInteger.Error == null) return () => asInteger.GetValue(target, Mathf.RoundToInt(fixedValue));

            return () => fixedValue;
        }

        /// <summary>
        /// Builds the table an array and its companion columns are drawn as: a heading row, one row per
        /// entry with a delete beside it, and an add underneath.
        /// </summary>
        /// <param name="property">The spine array, which supplies the first column.</param>
        /// <param name="label">Name shown over the table.</param>
        /// <param name="table">Attribute naming the arrays supplying the further columns.</param>
        /// <returns>The table element.</returns>
        private VisualElement BuildTable(SerializedProperty property, string label, TableAttribute table)
        {
            string spinePath = property.propertyPath;

            VisualElement root = new VisualElement();
            root.AddToClassList("ir-table");

            Label caption = new Label(label);
            caption.AddToClassList("ir-table__caption");
            root.Add(caption);

            VisualElement head = new VisualElement();
            head.AddToClassList("ir-table__head");
            root.Add(head);

            VisualElement body = new VisualElement();
            root.Add(body);

            Button add = new Button(() => AddTableRow(spinePath, table)) { text = "Add" };
            add.AddToClassList("ir-button");
            add.AddToClassList("ir-table__add");
            root.Add(add);

            // The row count the body was last built for, held in a cell so the refresher can carry it
            // between runs and rebuild only when the table actually changed shape.
            int[] builtFor = { -1 };

            _refreshers.Add(() => RefreshTable(head, body, spinePath, table, builtFor));
            return root;
        }

        /// <summary>Squares the columns off against the spine and rebuilds the rows when the count moved.</summary>
        /// <param name="head">Heading row.</param>
        /// <param name="body">Element holding the rows.</param>
        /// <param name="spinePath">Path of the spine array.</param>
        /// <param name="table">Attribute naming the further columns.</param>
        /// <param name="builtFor">Row count the body was last built for.</param>
        private void RefreshTable(
            VisualElement head,
            VisualElement body,
            string spinePath,
            TableAttribute table,
            int[] builtFor)
        {
            SerializedProperty spine = serializedObject.FindProperty(spinePath);
            if (spine == null || !spine.isArray) return;

            SerializedProperty[] columns = ResolveColumns(table);
            SyncColumnSizes(spine.arraySize, columns);

            if (builtFor[0] == spine.arraySize) return;
            builtFor[0] = spine.arraySize;

            BuildTableHead(head, spine, columns, table.Headings);
            BuildTableBody(body, spine, columns, spinePath, table);
        }

        /// <summary>Finds the arrays supplying the further columns.</summary>
        /// <param name="table">Attribute naming them.</param>
        /// <returns>One property per column, with a null where a name matched nothing.</returns>
        private SerializedProperty[] ResolveColumns(TableAttribute table)
        {
            string[] names = table.Columns == null ? new string[0] : table.Columns;
            SerializedProperty[] columns = new SerializedProperty[names.Length];

            for (int i = 0; i < names.Length; i++)
            {
                columns[i] = serializedObject.FindProperty(names[i]);
            }

            return columns;
        }

        /// <summary>Holds every column at the spine's length.</summary>
        /// <param name="size">Length the spine currently has.</param>
        /// <param name="columns">Columns being squared off.</param>
        private void SyncColumnSizes(int size, SerializedProperty[] columns)
        {
            bool changed = false;

            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i] == null || !columns[i].isArray) continue;
                if (columns[i].arraySize == size) continue;

                columns[i].arraySize = size;
                changed = true;
            }

            // Data written before a column existed comes back short. Squaring it off is a repair rather
            // than an edit the author made, so it does not earn an undo step of its own.
            if (changed) serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Builds the heading row over the table.</summary>
        /// <param name="head">Heading row being filled.</param>
        /// <param name="spine">The spine array.</param>
        /// <param name="columns">The further columns.</param>
        /// <param name="headings">Headings to use instead of the arrays' own names, or null.</param>
        private static void BuildTableHead(
            VisualElement head,
            SerializedProperty spine,
            SerializedProperty[] columns,
            string[] headings)
        {
            head.Clear();

            Label first = new Label(spine.displayName);
            first.AddToClassList("ir-table__spine");
            head.Add(first);

            for (int i = 0; i < columns.Length; i++)
            {
                string heading = headings != null && i < headings.Length && !string.IsNullOrEmpty(headings[i])
                    ? headings[i]
                    : (columns[i] == null ? "Missing" : columns[i].displayName);

                Label label = new Label(heading);
                label.AddToClassList("ir-table__cell");
                head.Add(label);
            }

            VisualElement spacer = new VisualElement();
            spacer.AddToClassList("ir-table__remove");
            head.Add(spacer);
        }

        /// <summary>Builds one row per entry.</summary>
        /// <param name="body">Element receiving the rows.</param>
        /// <param name="spine">The spine array.</param>
        /// <param name="columns">The further columns.</param>
        /// <param name="spinePath">Path of the spine array.</param>
        /// <param name="table">Attribute naming the further columns.</param>
        private void BuildTableBody(
            VisualElement body,
            SerializedProperty spine,
            SerializedProperty[] columns,
            string spinePath,
            TableAttribute table)
        {
            body.Clear();
            if (spine.arraySize == 0) return;

            SerializedProperty cursor = spine.GetArrayElementAtIndex(0);
            SerializedProperty[] cells = new SerializedProperty[columns.Length];

            for (int i = 0; i < columns.Length; i++)
            {
                cells[i] = columns[i] == null || columns[i].arraySize == 0
                    ? null
                    : columns[i].GetArrayElementAtIndex(0);
            }

            for (int row = 0; row < spine.arraySize; row++)
            {
                int index = row;

                VisualElement line = new VisualElement();
                line.AddToClassList("ir-table__row");
                body.Add(line);

                PropertyField first = new PropertyField(cursor.Copy(), string.Empty);
                first.AddToClassList("ir-table__spine");
                first.Bind(serializedObject);
                line.Add(first);

                for (int i = 0; i < cells.Length; i++)
                {
                    line.Add(BuildTableCell(cells[i]));
                }

                Button remove = new Button(() => RemoveTableRow(spinePath, table, index)) { text = "X" };
                remove.AddToClassList("ir-button");
                remove.AddToClassList("ir-table__remove");
                line.Add(remove);

                // Advancing each cursor beats indexing back into the array, which re-walks it from the
                // root every time and turns a long table quadratic.
                cursor.Next(false);
                for (int i = 0; i < cells.Length; i++)
                {
                    if (cells[i] != null) cells[i].Next(false);
                }
            }
        }

        /// <summary>Builds one cell, drawn as a centred tick where the column holds switches.</summary>
        /// <param name="cell">Property the cell edits, or null when the column is missing.</param>
        /// <returns>The cell element.</returns>
        private VisualElement BuildTableCell(SerializedProperty cell)
        {
            VisualElement holder = new VisualElement();
            holder.AddToClassList("ir-table__cell");

            if (cell == null) return holder;

            if (cell.propertyType == SerializedPropertyType.Boolean)
            {
                Toggle toggle = WithTick(new Toggle());
                toggle.AddToClassList("ir-table__toggle");
                toggle.BindProperty(cell.Copy());
                holder.Add(toggle);
                return holder;
            }

            PropertyField field = new PropertyField(cell.Copy(), string.Empty);
            field.Bind(serializedObject);
            holder.Add(field);

            return holder;
        }

        /// <summary>Adds one row to the spine and to every column with it.</summary>
        /// <param name="spinePath">Path of the spine array.</param>
        /// <param name="table">Attribute naming the further columns.</param>
        private void AddTableRow(string spinePath, TableAttribute table)
        {
            SerializedProperty spine = serializedObject.FindProperty(spinePath);
            if (spine == null || !spine.isArray) return;

            spine.arraySize = spine.arraySize + 1;

            SerializedProperty[] columns = ResolveColumns(table);
            for (int i = 0; i < columns.Length; i++)
            {
                if (columns[i] != null && columns[i].isArray) columns[i].arraySize = spine.arraySize;
            }

            serializedObject.ApplyModifiedProperties();
            OnSerializedObjectChanged();
        }

        /// <summary>Removes one row from the spine and from every column with it.</summary>
        /// <param name="spinePath">Path of the spine array.</param>
        /// <param name="table">Attribute naming the further columns.</param>
        /// <param name="index">Row being removed.</param>
        private void RemoveTableRow(string spinePath, TableAttribute table, int index)
        {
            SerializedProperty spine = serializedObject.FindProperty(spinePath);
            if (spine == null || !spine.isArray) return;

            DeleteRow(spine, index);

            SerializedProperty[] columns = ResolveColumns(table);
            for (int i = 0; i < columns.Length; i++)
            {
                DeleteRow(columns[i], index);
            }

            serializedObject.ApplyModifiedProperties();
            OnSerializedObjectChanged();
        }

        /// <summary>Removes one entry from an array.</summary>
        /// <param name="array">Array being shortened.</param>
        /// <param name="index">Entry being removed.</param>
        private static void DeleteRow(SerializedProperty array, int index)
        {
            if (array == null || !array.isArray) return;
            if (index < 0 || index >= array.arraySize) return;

            // Deleting an entry that still holds a reference only clears the reference, so a filled row
            // would take two presses to remove. Clearing it first makes one press remove one row.
            SerializedProperty element = array.GetArrayElementAtIndex(index);
            if (element.propertyType == SerializedPropertyType.ObjectReference)
            {
                element.objectReferenceValue = null;
            }

            array.DeleteArrayElementAtIndex(index);
        }

        /// <summary>Draws a value as plain text, for something the behaviour reports rather than takes.</summary>
        /// <param name="label">Label the readout shows.</param>
        /// <param name="field">Backing field the value is read from, or null.</param>
        /// <returns>The readout element.</returns>
        private VisualElement BuildReadout(string label, MemberInfo field)
        {
            VisualElement row = new VisualElement();
            row.AddToClassList("ir-readout");

            Label caption = new Label(label);
            caption.AddToClassList("unity-base-field__label");
            row.Add(caption);

            Label value = new Label();
            value.AddToClassList("ir-readout__value");
            row.Add(value);

            _refreshers.Add(() =>
            {
                object current = ReadMember(field, target);
                value.text = current == null ? "Null" : current.ToString();
            });

            return row;
        }

        /// <summary>
        /// Builds a control for a member with no serialized value behind it, choosing the control from
        /// the type it holds and falling back to a plain readout for anything else, or for anything
        /// that cannot be written.
        /// </summary>
        /// <param name="member">Member being drawn.</param>
        /// <param name="label">Label the control shows.</param>
        /// <returns>The control.</returns>
        private VisualElement BuildMemberControl(MemberInfo member, string label)
        {
            Type type = MemberType(member);
            bool writable = CanWriteMember(member) && GetAttribute<ReadOnlyFieldAttribute>(member) == null;

            if (type == null || !writable) return BuildReadout(label, member);

            VisualElement control = BuildValueControl(
                type,
                label,
                () => ReadMember(member, target),
                value => WriteMemberValue(member, value));

            return control ?? BuildReadout(label, member);
        }

        /// <summary>Reports whether a property can hold either a name or the number standing for one.</summary>
        /// <param name="property">Property being tested.</param>
        /// <returns>True when it holds text or a whole number.</returns>
        private static bool IsNameOrId(SerializedProperty property)
        {
            return property.propertyType == SerializedPropertyType.String
                || property.propertyType == SerializedPropertyType.Integer;
        }

        /// <summary>Writes a member's value on every inspected object, undoably.</summary>
        /// <param name="member">Member being written.</param>
        /// <param name="value">Value to store.</param>
        private void WriteMemberValue(MemberInfo member, object value)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                UnityEngine.Object item = targets[i];
                if (item == null) continue;

                Undo.RecordObject(item, "Set " + ObjectNames.NicifyVariableName(member.Name));
                WriteMember(member, item, value);
                EditorUtility.SetDirty(item);
            }

            serializedObject.Update();
            PushToUdon();
            RunRefreshers();
        }

        /// <summary>Reads a field or a property.</summary>
        /// <param name="member">Member being read.</param>
        /// <param name="owner">Object it is read from.</param>
        /// <returns>The value, or null when it cannot be read.</returns>
        private static object ReadMember(MemberInfo member, object owner)
        {
            FieldInfo field = member as FieldInfo;
            if (field != null) return field.GetValue(field.IsStatic ? null : owner);

            PropertyInfo property = member as PropertyInfo;
            if (property == null || !property.CanRead) return null;

            MethodInfo getter = property.GetGetMethod(true);
            return property.GetValue(getter != null && getter.IsStatic ? null : owner);
        }

        /// <summary>Writes a field or a property.</summary>
        /// <param name="member">Member being written.</param>
        /// <param name="owner">Object it is written on.</param>
        /// <param name="value">Value to store.</param>
        private static void WriteMember(MemberInfo member, object owner, object value)
        {
            FieldInfo field = member as FieldInfo;
            if (field != null)
            {
                field.SetValue(field.IsStatic ? null : owner, value);
                return;
            }

            PropertyInfo property = member as PropertyInfo;
            if (property == null || !property.CanWrite) return;

            MethodInfo setter = property.GetSetMethod(true);
            property.SetValue(setter != null && setter.IsStatic ? null : owner, value);
        }

        /// <summary>Returns the type a member holds.</summary>
        /// <param name="member">Member being asked about.</param>
        /// <returns>The type, or null when the member is neither a field nor a property.</returns>
        private static Type MemberType(MemberInfo member)
        {
            FieldInfo field = member as FieldInfo;
            if (field != null) return field.FieldType;

            PropertyInfo property = member as PropertyInfo;
            return property == null ? null : property.PropertyType;
        }

        /// <summary>Reports whether a member can be written at all.</summary>
        /// <param name="member">Member being asked about.</param>
        /// <returns>True when a value can be stored through it.</returns>
        private static bool CanWriteMember(MemberInfo member)
        {
            FieldInfo field = member as FieldInfo;
            if (field != null) return !field.IsInitOnly && !field.IsLiteral;

            PropertyInfo property = member as PropertyInfo;
            return property != null && property.CanWrite;
        }

        /// <summary>Draws a field as a list of the values a member offers.</summary>
        /// <param name="label">Label the list shows.</param>
        /// <param name="field">Backing field the chosen value is written to.</param>
        /// <param name="dropdown">Attribute naming the member offering the values.</param>
        /// <returns>The list element.</returns>
        private VisualElement BuildDropdown(string label, MemberInfo field, DropdownAttribute dropdown)
        {
            ValueSource<IEnumerable> source = ValueSource<IEnumerable>.ForMember(
                TargetType,
                dropdown.ValuesMember);

            DropdownField picker = new DropdownField(label);
            List<object> values = new List<object>();

            picker.RegisterValueChangedCallback(evt =>
            {
                int index = picker.choices.IndexOf(evt.newValue);
                if (index < 0 || index >= values.Count) return;

                WriteFieldValue(field, values[index]);
            });

            _refreshers.Add(() => RefreshDropdown(picker, values, source, field));
            return picker;
        }

        /// <summary>Re-reads the values on offer and marks whichever one the field currently holds.</summary>
        /// <param name="picker">List being refreshed.</param>
        /// <param name="values">Backing values, kept in step with the names shown.</param>
        /// <param name="source">Member offering the values.</param>
        /// <param name="field">Backing field the chosen value is written to.</param>
        private void RefreshDropdown(
            DropdownField picker,
            List<object> values,
            ValueSource<IEnumerable> source,
            MemberInfo field)
        {
            values.Clear();
            List<string> names = new List<string>();

            IEnumerable offered = source.GetValue(target, null);
            if (offered != null)
            {
                foreach (object item in offered)
                {
                    values.Add(item);
                    names.Add(item == null ? "None" : item.ToString());
                }
            }

            picker.choices = names;

            object current = ReadMember(field, target);
            int index = values.FindIndex(candidate => Equals(candidate, current));
            picker.SetValueWithoutNotify(index >= 0 ? names[index] : string.Empty);
        }

        /// <summary>
        /// Writes a value straight to the backing field rather than through the serialized property,
        /// because the values a dropdown offers can be of any type at all.
        /// </summary>
        /// <param name="field">Field being written.</param>
        /// <param name="value">Value to store.</param>
        private void WriteFieldValue(MemberInfo field, object value)
        {
            if (field == null) return;

            WriteMemberValue(field, value);
        }

        /// <summary>Puts the property's value into a bounded whole-number slider.</summary>
        /// <param name="slider">Slider being refreshed.</param>
        /// <param name="property">Property being shown.</param>
        /// <param name="low">Lowest value the slider allows.</param>
        /// <param name="high">Highest value the slider allows.</param>
        private static void RefreshIntegerSlider(SliderInt slider, SerializedProperty property, int low, int high)
        {
            SetClamped(SLIDER_INT_CLAMPED, slider, false);

            slider.lowValue = low;
            slider.highValue = high;

            int current = property.intValue;
            SetClamped(SLIDER_INT_CLAMPED, slider, current >= low && current <= high);
            slider.SetValueWithoutNotify(current);
        }

        /// <summary>Writes a whole number back to its property.</summary>
        /// <param name="property">Property being written.</param>
        /// <param name="value">Value to store.</param>
        private void WriteInteger(SerializedProperty property, int value)
        {
            property.intValue = value;
            property.serializedObject.ApplyModifiedProperties();
            OnSerializedObjectChanged();
        }

        /// <summary>Builds the bounded slider for a decimal number.</summary>
        /// <param name="property">Property being edited.</param>
        /// <param name="label">Label the slider shows.</param>
        /// <param name="clamp">Attribute carrying the bounds.</param>
        /// <returns>The slider.</returns>
        private VisualElement BuildFloatSlider(SerializedProperty property, string label, ClampAttribute clamp)
        {
            Func<float> low = BoundOf(clamp.MinimumMember, clamp.Minimum);
            Func<float> high = BoundOf(clamp.MaximumMember, clamp.Maximum);

            Slider slider = new Slider(label, low(), high());
            slider.showInputField = true;

            SerializedProperty tracked = property.Copy();
            slider.RegisterValueChangedCallback(evt => WriteFloat(tracked, evt.newValue));
            _refreshers.Add(() => RefreshFloatSlider(slider, tracked, low(), high()));

            return slider;
        }

        /// <summary>Puts the property's value into a bounded slider.</summary>
        /// <param name="slider">Slider being refreshed.</param>
        /// <param name="property">Property being shown.</param>
        /// <param name="low">Lowest value the slider allows.</param>
        /// <param name="high">Highest value the slider allows.</param>
        private static void RefreshFloatSlider(Slider slider, SerializedProperty property, float low, float high)
        {
            SetClamped(SLIDER_CLAMPED, slider, false);

            slider.lowValue = low;
            slider.highValue = high;

            float current = property.floatValue;
            SetClamped(SLIDER_CLAMPED, slider, current >= low && current <= high);
            slider.SetValueWithoutNotify(current);
        }

        /// <summary>Writes a decimal number back to its property.</summary>
        /// <param name="property">Property being written.</param>
        /// <param name="value">Value to store.</param>
        private void WriteFloat(SerializedProperty property, float value)
        {
            property.floatValue = value;
            property.serializedObject.ApplyModifiedProperties();
            OnSerializedObjectChanged();
        }

        /// <summary>
        /// Turns a slider's own clamping on or off. Left on, a slider quietly drags a value that was
        /// already out of range back inside it the first time the inspector is opened, which edits data
        /// nobody asked to edit.
        /// </summary>
        /// <param name="clamped">The engine's switch, or null on a version that has renamed it.</param>
        /// <param name="slider">Slider being changed.</param>
        /// <param name="value">True to let the slider clamp.</param>
        private static void SetClamped(PropertyInfo clamped, VisualElement slider, bool value)
        {
            if (clamped == null) return;

            clamped.SetValue(slider, value);
        }

        /// <summary>
        /// Builds the low number, the two-handled slider and the high number that together edit one range.
        /// The handles are kept in order on every write, so the low end can never cross the high end.
        /// </summary>
        /// <param name="property">Property holding the range as a Vector2.</param>
        /// <param name="label">Label the control shows.</param>
        /// <param name="range">Attribute carrying the bounds.</param>
        /// <returns>The range control.</returns>
        private VisualElement BuildRange(SerializedProperty property, string label, MinMaxClampAttribute range)
        {
            VisualElement field = new VisualElement();
            field.AddToClassList("ir-range");

            Label caption = new Label(label);
            caption.AddToClassList("unity-base-field__label");
            field.Add(caption);

            VisualElement body = new VisualElement();
            body.AddToClassList("ir-range__body");
            field.Add(body);

            FloatField low = new FloatField();
            low.AddToClassList("ir-range__value");
            body.Add(low);

            MinMaxSlider slider = new MinMaxSlider(range.Minimum, range.Maximum, range.Minimum, range.Maximum);
            slider.AddToClassList("ir-range__slider");
            body.Add(slider);

            FloatField high = new FloatField();
            high.AddToClassList("ir-range__value");
            body.Add(high);

            SerializedProperty tracked = property.Copy();

            low.RegisterValueChangedCallback(evt => ApplyRange(tracked, range, evt.newValue, tracked.vector2Value.y));
            high.RegisterValueChangedCallback(evt => ApplyRange(tracked, range, tracked.vector2Value.x, evt.newValue));
            slider.RegisterValueChangedCallback(evt => ApplyRange(tracked, range, evt.newValue.x, evt.newValue.y));

            _refreshers.Add(() => RefreshRange(low, slider, high, tracked));
            return field;
        }

        /// <summary>Writes a range back, holding both ends inside the bounds and in order.</summary>
        /// <param name="property">Property holding the range.</param>
        /// <param name="range">Attribute carrying the bounds.</param>
        /// <param name="low">Proposed low end.</param>
        /// <param name="high">Proposed high end.</param>
        private void ApplyRange(SerializedProperty property, MinMaxClampAttribute range, float low, float high)
        {
            low = Mathf.Clamp(low, range.Minimum, Mathf.Min(range.Maximum, high));
            high = Mathf.Clamp(high, Mathf.Max(range.Minimum, low), range.Maximum);

            property.vector2Value = new Vector2(low, high);
            property.serializedObject.ApplyModifiedProperties();
            OnSerializedObjectChanged();
        }

        /// <summary>Puts the property's current range into all three of its controls.</summary>
        /// <param name="low">Field showing the low end.</param>
        /// <param name="slider">Slider showing both ends.</param>
        /// <param name="high">Field showing the high end.</param>
        /// <param name="property">Property holding the range.</param>
        private static void RefreshRange(
            FloatField low,
            MinMaxSlider slider,
            FloatField high,
            SerializedProperty property)
        {
            Vector2 value = property.vector2Value;

            low.SetValueWithoutNotify(value.x);
            high.SetValueWithoutNotify(value.y);
            slider.SetValueWithoutNotify(value);
        }

        /// <summary>Builds the strip of named buttons standing in for a whole number.</summary>
        /// <param name="property">Property holding the chosen index.</param>
        /// <param name="label">Label the strip shows.</param>
        /// <param name="options">Option names, in value order.</param>
        /// <param name="isEnum">True when the property is an enum rather than a plain whole number.</param>
        /// <returns>The strip element.</returns>
        private VisualElement BuildChoices(
            SerializedProperty property,
            string label,
            string[] options,
            bool isEnum)
        {
            VisualElement field = new VisualElement();
            field.AddToClassList("ir-choices");

            Label caption = new Label(label);

            // Unity's own label class rather than one of ours, so the strip lines up with the ordinary
            // fields above and below it and honours a label width set on the same field.
            caption.AddToClassList("unity-base-field__label");
            field.Add(caption);

            VisualElement strip = new VisualElement();
            strip.AddToClassList("ir-choices__strip");
            field.Add(strip);

            SerializedProperty tracked = property.Copy();
            List<Button> buttons = new List<Button>();

            for (int i = 0; i < options.Length; i++)
            {
                int value = i;
                Button option = new Button(() => SetChoice(tracked, value, isEnum)) { text = options[i] };
                option.AddToClassList("ir-choice");
                strip.Add(option);
                buttons.Add(option);
            }

            _refreshers.Add(() => RefreshChoices(buttons, tracked, isEnum));
            return field;
        }

        /// <summary>Writes the chosen option back to the property.</summary>
        /// <param name="property">Property holding the choice.</param>
        /// <param name="value">Index that was chosen.</param>
        /// <param name="isEnum">True when the property is an enum rather than a plain whole number.</param>
        private void SetChoice(SerializedProperty property, int value, bool isEnum)
        {
            // An enum's index and its numeric value are the same thing only while the members run from
            // zero with no gaps, so the index is written as an index.
            if (isEnum)
            {
                property.enumValueIndex = value;
            }
            else
            {
                property.intValue = value;
            }

            property.serializedObject.ApplyModifiedProperties();
            OnSerializedObjectChanged();
        }

        /// <summary>Marks whichever option the property currently holds.</summary>
        /// <param name="buttons">Buttons in the strip.</param>
        /// <param name="property">Property holding the choice.</param>
        /// <param name="isEnum">True when the property is an enum rather than a plain whole number.</param>
        private static void RefreshChoices(List<Button> buttons, SerializedProperty property, bool isEnum)
        {
            int value = isEnum ? property.enumValueIndex : property.intValue;
            for (int i = 0; i < buttons.Count; i++)
            {
                buttons[i].EnableInClassList("ir-choice--active", i == value);
            }
        }

        /// <summary>Applies whichever of the attributes stop a field being edited.</summary>
        /// <param name="control">Control being locked.</param>
        /// <param name="field">Backing field, or null.</param>
        private static void ApplyEditability(VisualElement control, MemberInfo member)
        {
            if (GetAttribute<ReadOnlyFieldAttribute>(member) != null)
            {
                control.SetEnabled(false);
                control.AddToClassList("ir-field--readonly");
                return;
            }

            bool editOnly = GetAttribute<EditModeOnlyAttribute>(member) != null;
            bool playOnly = GetAttribute<PlayModeOnlyAttribute>(member) != null;
            if (!editOnly && !playOnly) return;

            // Polled rather than driven from a play-mode callback because a callback has to be unhooked
            // again, and the schedule stops by itself the moment the element leaves the panel.
            ApplyPlayStateLock(control, editOnly);
            control.schedule.Execute(() => ApplyPlayStateLock(control, editOnly)).Every(POLL_INTERVAL);
        }

        /// <summary>Locks or releases a field according to whether play mode is running.</summary>
        /// <param name="control">Control being locked.</param>
        /// <param name="lockWhilePlaying">True to lock while playing, false to lock while stopped.</param>
        private static void ApplyPlayStateLock(VisualElement control, bool lockWhilePlaying)
        {
            bool locked = EditorApplication.isPlaying == lockWhilePlaying;
            control.SetEnabled(!locked);
            control.EnableInClassList("ir-field--readonly", locked);
        }

        /// <summary>Shows or hides a field according to whether play mode is running.</summary>
        /// <param name="block">Block being shown or hidden.</param>
        /// <param name="field">Backing field, or null.</param>
        private static void ApplyPlayStateVisibility(VisualElement block, MemberInfo member)
        {
            bool hideInPlay = GetAttribute<HideInPlayModeAttribute>(member) != null;
            bool onlyInPlay = GetAttribute<ShowInPlayModeAttribute>(member) != null
                || GetAttribute<HideInEditModeAttribute>(member) != null;

            if (!hideInPlay && !onlyInPlay) return;

            ApplyPlayStateDisplay(block, hideInPlay, onlyInPlay);
            block.schedule.Execute(() => ApplyPlayStateDisplay(block, hideInPlay, onlyInPlay)).Every(POLL_INTERVAL);
        }

        /// <summary>Sets a block's visibility from the current play state.</summary>
        /// <param name="block">Block being shown or hidden.</param>
        /// <param name="hideInPlay">True to hide it while playing.</param>
        /// <param name="onlyInPlay">True to hide it while stopped.</param>
        private static void ApplyPlayStateDisplay(VisualElement block, bool hideInPlay, bool onlyInPlay)
        {
            bool playing = EditorApplication.isPlaying;
            bool shown = !(hideInPlay && playing) && !(onlyInPlay && !playing);

            block.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>
        /// Tints a field. The palette here is the dark one only, because this inspector has no light
        /// theme to serve: the brand is dark mode throughout.
        /// </summary>
        /// <param name="control">Control being tinted.</param>
        /// <param name="field">Backing field, or null.</param>
        private void ApplyTint(VisualElement control, MemberInfo member)
        {
            TintAttribute tint = GetAttribute<TintAttribute>(member);
            if (tint == null) return;

            Color chosen = new Color(tint.Red, tint.Green, tint.Blue, tint.Alpha);
            ValueSource<Color> source = string.IsNullOrEmpty(tint.ColourMember)
                ? null
                : ValueSource<Color>.ForMember(TargetType, tint.ColourMember);

            _refreshers.Add(() => ApplyTintColour(
                control,
                source == null ? chosen : source.GetValue(target, chosen)));
        }

        /// <summary>Spreads one tint across a control's label, its well, its mark and its buttons.</summary>
        /// <param name="control">Control being tinted.</param>
        /// <param name="value">Colour to tint with.</param>
        private static void ApplyTintColour(VisualElement control, Color value)
        {
            float hue;
            float saturation;
            float brightness;
            Color.RGBToHSV(value, out hue, out saturation, out brightness);

            Color text = Color.HSVToRGB(hue, saturation, brightness);
            Color well = Color.HSVToRGB(hue, saturation * 0.7f, brightness * 0.165f);
            Color face = Color.HSVToRGB(hue, saturation * 0.7f, brightness * 0.4f);

            control.Query<Label>().ForEach(label => label.style.color = text);
            control.Query(className: "unity-base-field__input").ForEach(input => input.style.color = text);
            control.Query(className: "unity-base-text-field__input")
                .ForEach(input => input.style.backgroundColor = well);
            control.Query(className: "unity-toggle__checkmark")
                .ForEach(mark => mark.style.unityBackgroundImageTintColor = value);
            control.Query<Button>().ForEach(item =>
            {
                item.style.color = text;
                item.style.backgroundColor = face;
            });
        }

        /// <summary>Settles how a list draws itself, once its own controls exist to be found.</summary>
        /// <param name="control">Control holding the list.</param>
        /// <param name="field">Backing field, or null.</param>
        private static void ApplyListSettings(VisualElement control, MemberInfo member)
        {
            ListSettingsAttribute settings = GetAttribute<ListSettingsAttribute>(member);
            if (settings == null) return;

            control.RegisterCallback<GeometryChangedEvent>(_ => ConfigureList(control, settings));
        }

        /// <summary>Applies the list settings to the list a property field built.</summary>
        /// <param name="control">Control holding the list.</param>
        /// <param name="settings">Settings being applied.</param>
        private static void ConfigureList(VisualElement control, ListSettingsAttribute settings)
        {
            ListView list = control.Q<ListView>();
            if (list == null) return;

            list.reorderable = settings.Draggable;
            list.showFoldoutHeader = !settings.AlwaysExpanded;

            // The footer carries both buttons together, so hiding one of them is done by the button.
            VisualElement add = list.Q(className: "unity-list-view__add-button");
            if (add != null) add.style.display = settings.ShowAdd ? DisplayStyle.Flex : DisplayStyle.None;

            VisualElement remove = list.Q(className: "unity-list-view__remove-button");
            if (remove != null) remove.style.display = settings.ShowRemove ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Opens a struct's own fields into the card instead of leaving them behind a fold.</summary>
        /// <param name="control">Control holding the struct.</param>
        /// <param name="field">Backing field, or null.</param>
        private static void ApplyInlineProperty(VisualElement control, MemberInfo member)
        {
            if (GetAttribute<InlinePropertyAttribute>(member) == null) return;

            control.AddToClassList("ir-field--inline");
            control.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                Foldout foldout = control.Q<Foldout>();
                if (foldout != null) foldout.value = true;
            });
        }

        /// <summary>Calls the behaviour's own method whenever the field beside it is edited.</summary>
        /// <param name="control">Control being watched.</param>
        /// <param name="property">Property being watched.</param>
        /// <param name="field">Backing field, or null.</param>
        private void HookValueChanged(VisualElement control, SerializedProperty property, MemberInfo member)
        {
            OnValueChangedAttribute changed = GetAttribute<OnValueChangedAttribute>(member);
            if (changed == null || property == null) return;

            ActionSource action = ActionSource.Resolve(TargetType, changed.MethodName);
            if (action.Error != null) return;

            SerializedProperty tracked = property.Copy();
            control.TrackPropertyValue(tracked, _ => RunMemberAction(
                action,
                "Change " + property.displayName,
                changed.Records));
        }

        /// <summary>
        /// Holds one control's label at a set width. A bound field builds its children after it is
        /// created and resolves its styles after its first layout, so the width is reapplied on each of
        /// the three events that can undo it rather than only on the first.
        /// </summary>
        /// <param name="control">Control carrying the label.</param>
        /// <param name="width">Label width in pixels.</param>
        private static void BindLabelWidth(VisualElement control, float width)
        {
            control.RegisterCallback<AttachToPanelEvent>(_ => ApplyLabelWidth(control, width));
            control.RegisterCallback<CustomStyleResolvedEvent>(_ => ApplyLabelWidth(control, width));
            control.RegisterCallback<GeometryChangedEvent>(_ => ApplyLabelWidth(control, width));
        }

        /// <summary>Sets the width of a control's label once its children exist to be found.</summary>
        /// <param name="control">Control carrying the label.</param>
        /// <param name="width">Label width in pixels.</param>
        private static void ApplyLabelWidth(VisualElement control, float width)
        {
            if (control == null) return;

            Label label = control.Q<Label>(className: "unity-base-field__label");
            if (label == null) return;

            label.style.minWidth = width;
            label.style.width = width;
        }

        #endregion

        #region Decorations

        /// <summary>Adds the hairline rule opening a field.</summary>
        /// <param name="parent">Element receiving the rule.</param>
        /// <param name="member">Field or property the attributes sit on, or null.</param>
        private static void AddDivider(VisualElement parent, MemberInfo member)
        {
            if (GetAttribute<DividerAttribute>(member) == null) return;

            VisualElement rule = new VisualElement();
            rule.AddToClassList("ir-divider");
            parent.Add(rule);
        }

        /// <summary>Sets the breathing room around a field.</summary>
        /// <param name="block">Block being spaced.</param>
        /// <param name="member">Field or property the attributes sit on, or null.</param>
        private static void AddGap(VisualElement block, MemberInfo member)
        {
            GapAttribute gap = GetAttribute<GapAttribute>(member);
            if (gap == null) return;

            block.style.marginTop = gap.Height;
            block.style.marginBottom = gap.After;
        }

        /// <summary>Adds the accent label opening a run of fields.</summary>
        /// <param name="parent">Element receiving the label.</param>
        /// <param name="member">Field or property the attributes sit on, or null.</param>
        private static void AddEyebrow(VisualElement parent, MemberInfo member)
        {
            EyebrowAttribute eyebrow = GetAttribute<EyebrowAttribute>(member);
            if (eyebrow == null) return;

            // The eyebrow is set in capitals by the house style rather than by the text the author wrote,
            // which is why the transform lives here and not in the attribute.
            Label label = new Label(eyebrow.Text.ToUpperInvariant());
            label.AddToClassList("ir-eyebrow");
            parent.Add(label);
        }

        /// <summary>Adds the section heading opening a run of fields.</summary>
        /// <param name="parent">Element receiving the heading.</param>
        /// <param name="member">Field or property the attributes sit on, or null.</param>
        private void AddTitle(VisualElement parent, MemberInfo member)
        {
            TitleAttribute title = GetAttribute<TitleAttribute>(member);
            if (title == null) return;

            Label label = new Label();
            label.AddToClassList("ir-title");
            parent.Add(label);

            if (title.Line)
            {
                VisualElement rule = new VisualElement();
                rule.AddToClassList("ir-title__line");
                parent.Add(rule);
            }

            ValueSource<string> text = ValueSource<string>.ForText(TargetType, title.Text);
            _refreshers.Add(() => label.text = text.GetValue(target, title.Text));
        }

        /// <summary>Adds the guidance note above a field.</summary>
        /// <param name="parent">Element receiving the note.</param>
        /// <param name="member">Field or property the attributes sit on, or null.</param>
        private void AddNote(VisualElement parent, MemberInfo member)
        {
            NoteAttribute note = GetAttribute<NoteAttribute>(member);
            if (note == null) return;

            AddMessage(parent, note.Text, note.Severity, note.VisibleIf);
        }

        /// <summary>Adds the standing warning above a field.</summary>
        /// <param name="parent">Element receiving the warning.</param>
        /// <param name="member">Field or property the attributes sit on, or null.</param>
        private void AddWarning(VisualElement parent, MemberInfo member)
        {
            WarnAttribute warning = GetAttribute<WarnAttribute>(member);
            if (warning == null) return;

            AddMessage(parent, warning.Text, MessageSeverity.Warning, null);
        }

        /// <summary>
        /// Adds one message, whose text may be fixed or read off the behaviour, and which may come and go
        /// with a condition of its own.
        /// </summary>
        /// <param name="parent">Element receiving the message.</param>
        /// <param name="text">Text shown, or a member name behind a dollar sign.</param>
        /// <param name="severity">How loudly the message is drawn.</param>
        /// <param name="visibleIf">Bool member deciding whether it is shown, or null.</param>
        private void AddMessage(VisualElement parent, string text, MessageSeverity severity, string visibleIf)
        {
            Label label = new Label();
            label.AddToClassList(SeverityClass(severity));
            parent.Add(label);

            ValueSource<string> source = ValueSource<string>.ForText(TargetType, text);
            ValueSource<bool> visible = string.IsNullOrEmpty(visibleIf)
                ? null
                : ValueSource<bool>.ForMember(TargetType, visibleIf);

            _refreshers.Add(() =>
            {
                string message = source.GetValue(target, text);
                bool shown = !string.IsNullOrEmpty(message) && (visible == null || visible.GetValue(target, true));

                label.text = message;
                label.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            });
        }

        /// <summary>Returns the style class that draws a message at one severity.</summary>
        /// <param name="severity">Severity being drawn.</param>
        /// <returns>Style class name.</returns>
        private static string SeverityClass(MessageSeverity severity)
        {
            switch (severity)
            {
                case MessageSeverity.Warning:
                    return "ir-warn";

                case MessageSeverity.Error:
                    return "ir-alert";

                default:
                    return "ir-note";
            }
        }

        /// <summary>Builds the small button that sits on the field's own line.</summary>
        /// <param name="inline">Attribute naming the method, its label and its width.</param>
        /// <returns>The button, or null when the method cannot be resolved.</returns>
        private Button BuildInlineButton(InlineButtonAttribute inline)
        {
            MethodInfo method = FindMethod(inline.MethodName);
            if (method == null || method.GetParameters().Length != 0) return null;

            Button button = new Button(() => InvokeAction(method))
            {
                text = string.IsNullOrEmpty(inline.Label)
                    ? ObjectNames.NicifyVariableName(method.Name)
                    : inline.Label
            };
            button.AddToClassList("ir-button");
            button.AddToClassList("ir-button--inline");

            if (inline.ButtonWidth > 0f) button.style.width = inline.ButtonWidth;

            if (!string.IsNullOrEmpty(inline.IsChecked))
            {
                ValueSource<bool> pressed = ValueSource<bool>.ForMember(TargetType, inline.IsChecked);
                _refreshers.Add(() => button.EnableInClassList(
                    "ir-button--checked",
                    pressed.GetValue(target, false)));
            }

            return button;
        }

        #endregion

        #region Validation

        /// <summary>Adds the missing-value notice and keeps it in step with the field.</summary>
        /// <param name="block">Element receiving the notice.</param>
        /// <param name="control">Control marked while the value is missing.</param>
        /// <param name="property">Property being tested.</param>
        /// <param name="field">Backing field, or null.</param>
        private void AddRequired(
            VisualElement block,
            VisualElement control,
            SerializedProperty property,
            MemberInfo field)
        {
            RequiredAttribute required = GetAttribute<RequiredAttribute>(field);
            if (required == null || property == null) return;

            VisualElement notice = new VisualElement();
            notice.AddToClassList("ir-notice");
            block.Add(notice);

            Label alert = new Label();
            alert.AddToClassList("ir-alert");
            notice.Add(alert);

            ActionSource fix = string.IsNullOrEmpty(required.FixAction)
                ? null
                : ActionSource.Resolve(TargetType, required.FixAction);

            if (fix != null && fix.Error == null)
            {
                ActionSource resolved = fix;
                Button button = new Button(() => RunMemberAction(resolved, "Fix"))
                {
                    text = string.IsNullOrEmpty(required.FixActionName) ? "Fix" : required.FixActionName
                };
                button.AddToClassList("ir-button");
                button.AddToClassList("ir-button--inline");
                notice.Add(button);
            }

            ValueSource<string> consequence = ValueSource<string>.ForText(TargetType, required.Consequence);
            SerializedProperty tracked = property.Copy();

            _refreshers.Add(() =>
            {
                bool missing = IsEmpty(tracked);

                alert.text = BuildRequiredMessage(tracked, consequence.GetValue(target, required.Consequence));
                notice.style.display = missing ? DisplayStyle.Flex : DisplayStyle.None;
                control.EnableInClassList("ir-field--missing", missing);
            });
        }

        /// <summary>Runs one of the behaviour's own methods on every inspected object, undoably.</summary>
        /// <param name="action">Method being run.</param>
        /// <param name="undoName">Name the step is recorded under.</param>
        /// <param name="scope">How much of the scene the action is recorded against.</param>
        private void RunMemberAction(ActionSource action, string undoName, UndoScope scope = UndoScope.Component)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                UnityEngine.Object item = targets[i];
                if (item == null) continue;

                RecordForUndo(item, undoName, scope);
                action.Invoke(item);
                EditorUtility.SetDirty(item);
            }

            serializedObject.Update();
            PushToUdon();
            RunRefreshers();
        }

        /// <summary>Reports whether a required property has been left unset.</summary>
        /// <param name="property">Property being tested.</param>
        /// <returns>True when the field carries nothing usable.</returns>
        private static bool IsEmpty(SerializedProperty property)
        {
            if (property.isArray && property.propertyType != SerializedPropertyType.String)
            {
                return property.arraySize == 0;
            }

            if (property.propertyType == SerializedPropertyType.ObjectReference)
            {
                return property.objectReferenceValue == null;
            }

            if (property.propertyType == SerializedPropertyType.String)
            {
                return string.IsNullOrWhiteSpace(property.stringValue);
            }

            return false;
        }

        /// <summary>Builds the sentence shown while a required field is empty.</summary>
        /// <param name="property">Property being reported.</param>
        /// <param name="consequence">What stops working while the value is missing, or null.</param>
        /// <returns>Message for the notice.</returns>
        private static string BuildRequiredMessage(SerializedProperty property, string consequence)
        {
            string label = ObjectNames.NicifyVariableName(property.name);
            return string.IsNullOrEmpty(consequence)
                ? label + " is required."
                : label + " is empty, so " + consequence + ".";
        }

        /// <summary>Adds the notice reporting a reference that came from the wrong place.</summary>
        /// <param name="block">Element receiving the notice.</param>
        /// <param name="property">Property being tested.</param>
        /// <param name="field">Backing field, or null.</param>
        private void AddOriginCheck(VisualElement block, SerializedProperty property, MemberInfo member)
        {
            bool assetOnly = GetAttribute<AssetOnlyAttribute>(member) != null;
            bool sceneOnly = GetAttribute<SceneOnlyAttribute>(member) != null;

            if (!assetOnly && !sceneOnly) return;
            if (property == null) return;
            if (property.propertyType != SerializedPropertyType.ObjectReference) return;

            Label alert = new Label(assetOnly
                ? "This has to be a project asset, and the one set is an object in the scene."
                : "This has to be an object in the scene, and the one set is a project asset.");
            alert.AddToClassList("ir-alert");
            block.Add(alert);

            SerializedProperty tracked = property.Copy();
            _refreshers.Add(() => RefreshOriginCheck(alert, tracked, assetOnly));
        }

        /// <summary>Shows the origin notice only while the reference actually came from the wrong place.</summary>
        /// <param name="alert">Notice being shown or hidden.</param>
        /// <param name="property">Property holding the reference.</param>
        /// <param name="assetOnly">True when a project asset was wanted, false when a scene object was.</param>
        private static void RefreshOriginCheck(Label alert, SerializedProperty property, bool assetOnly)
        {
            UnityEngine.Object value = property.objectReferenceValue;
            if (value == null)
            {
                alert.style.display = DisplayStyle.None;
                return;
            }

            bool isAsset = AssetDatabase.Contains(value);
            bool wrong = assetOnly ? !isAsset : isAsset;
            alert.style.display = wrong ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Adds the notice driven by one of the behaviour's own checking methods.</summary>
        /// <param name="block">Element receiving the notice.</param>
        /// <param name="field">Backing field, or null.</param>
        private void AddValidation(VisualElement block, MemberInfo member)
        {
            ValidateAttribute validate = GetAttribute<ValidateAttribute>(member);
            if (validate == null) return;

            MethodInfo method = FindMethod(validate.MethodName);
            if (method == null) return;
            if (method.GetParameters().Length != 0) return;

            bool detailed = method.ReturnType == typeof(ValidationResult);
            if (!detailed && method.ReturnType != typeof(string)) return;

            VisualElement notice = new VisualElement();
            notice.AddToClassList("ir-notice");
            block.Add(notice);

            Label alert = new Label();
            notice.Add(alert);

            Button fix = new Button { text = "Fix" };
            fix.AddToClassList("ir-button");
            fix.AddToClassList("ir-button--inline");
            notice.Add(fix);

            _refreshers.Add(() => RefreshValidation(notice, alert, fix, method, detailed));
        }

        /// <summary>
        /// Runs a checking method and shows whatever it reports, at the severity it asked for and with
        /// its own fix offered beside it when it supplied one.
        /// </summary>
        /// <param name="notice">Row holding the message and its fix.</param>
        /// <param name="alert">Label carrying the message.</param>
        /// <param name="fix">Button running the reported fix.</param>
        /// <param name="method">Checking method.</param>
        /// <param name="detailed">True when the method reports a full result rather than a bare sentence.</param>
        private void RefreshValidation(
            VisualElement notice,
            Label alert,
            Button fix,
            MethodInfo method,
            bool detailed)
        {
            string message;
            MessageSeverity severity = MessageSeverity.Error;
            Action action = null;
            string fixLabel = "Fix";

            if (detailed)
            {
                ValidationResult result = (ValidationResult)method.Invoke(target, null);

                message = result.IsValid ? null : result.Message;
                severity = result.Severity;
                action = result.Fix;
                if (!string.IsNullOrEmpty(result.FixLabel)) fixLabel = result.FixLabel;
            }
            else
            {
                message = method.Invoke(target, null) as string;
            }

            bool failed = !string.IsNullOrEmpty(message);
            notice.style.display = failed ? DisplayStyle.Flex : DisplayStyle.None;

            alert.text = failed ? message : string.Empty;
            alert.EnableInClassList("ir-note", SeverityClass(severity) == "ir-note");
            alert.EnableInClassList("ir-warn", SeverityClass(severity) == "ir-warn");
            alert.EnableInClassList("ir-alert", SeverityClass(severity) == "ir-alert");

            Action reported = action;
            fix.text = fixLabel;
            fix.clickable = new Clickable(() => RunReportedFix(reported));
            fix.style.display = failed && reported != null ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Runs the fix a checking method offered.</summary>
        /// <param name="action">Action being run, or null.</param>
        private void RunReportedFix(Action action)
        {
            if (action == null) return;

            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] != null) Undo.RecordObject(targets[i], "Fix");
            }

            action();

            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] != null) EditorUtility.SetDirty(targets[i]);
            }

            serializedObject.Update();
            PushToUdon();
            RunRefreshers();
        }

        #endregion

        #region Previews

        /// <summary>Adds the viewport that can be dragged to turn the referenced mesh.</summary>
        /// <param name="block">Element receiving the viewport.</param>
        /// <param name="property">Property being previewed.</param>
        /// <param name="field">Backing field, or null.</param>
        private void AddInteractivePreview(VisualElement block, SerializedProperty property, MemberInfo member)
        {
            InteractivePreviewAttribute preview = GetAttribute<InteractivePreviewAttribute>(member);
            if (preview == null || property == null) return;
            if (property.propertyType != SerializedPropertyType.ObjectReference) return;

            MeshViewport viewport = new MeshViewport(property.Copy(), preview);
            block.Add(viewport);

            _refreshers.Add(viewport.OnValueChanged);
        }

        /// <summary>Adds the referenced asset's own inspector underneath the field.</summary>
        /// <param name="block">Element receiving the nested inspector.</param>
        /// <param name="property">Property holding the reference.</param>
        /// <param name="field">Backing field, or null.</param>
        private void AddInlineEditor(VisualElement block, SerializedProperty property, MemberInfo member)
        {
            InlineEditorAttribute inline = GetAttribute<InlineEditorAttribute>(member);
            if (inline == null || property == null) return;
            if (property.propertyType != SerializedPropertyType.ObjectReference) return;

            block.Add(new InlineAssetEditor(property.Copy(), inline));
        }

        #endregion

        #region Conditions

        /// <summary>Applies whichever of the conditional attributes a field carries.</summary>
        /// <param name="block">Block being shown, hidden or greyed.</param>
        /// <param name="field">Backing field, or null.</param>
        private void ApplyConditions(VisualElement block, MemberInfo member)
        {
            ShowIfAttribute show = GetAttribute<ShowIfAttribute>(member);
            if (show != null) BindCondition(block, show.ConditionField, show.ExpectedValue, true, true);

            HideIfAttribute hide = GetAttribute<HideIfAttribute>(member);
            if (hide != null) BindCondition(block, hide.ConditionField, hide.ExpectedValue, false, true);

            EnableIfAttribute enable = GetAttribute<EnableIfAttribute>(member);
            if (enable != null) BindCondition(block, enable.ConditionField, enable.ExpectedValue, true, false);
        }

        /// <summary>Registers one condition so the block re-evaluates whenever a value changes.</summary>
        /// <param name="block">Block the condition governs.</param>
        /// <param name="conditionField">Serialized field the condition reads.</param>
        /// <param name="expected">Value the condition field must hold, or null for a plain set test.</param>
        /// <param name="matchShows">True when a match satisfies the condition, false when it denies it.</param>
        /// <param name="affectsVisibility">True to hide the block, false to grey it out.</param>
        private void BindCondition(
            VisualElement block,
            string conditionField,
            object expected,
            bool matchShows,
            bool affectsVisibility)
        {
            SerializedProperty condition = serializedObject.FindProperty(conditionField);

            // A condition naming a field that does not exist leaves the block alone rather than hiding it,
            // because a silently missing field is far harder to spot than one that simply never reacts.
            if (condition == null) return;

            SerializedProperty tracked = condition.Copy();
            _refreshers.Add(() => RefreshCondition(block, tracked, expected, matchShows, affectsVisibility));
        }

        /// <summary>Shows, hides or greys a block from its condition's current value.</summary>
        /// <param name="block">Block being governed.</param>
        /// <param name="condition">Property the condition reads.</param>
        /// <param name="expected">Value the condition field must hold, or null for a plain set test.</param>
        /// <param name="matchShows">True when a match satisfies the condition.</param>
        /// <param name="affectsVisibility">True to hide the block, false to grey it out.</param>
        private static void RefreshCondition(
            VisualElement block,
            SerializedProperty condition,
            object expected,
            bool matchShows,
            bool affectsVisibility)
        {
            bool satisfied = Matches(condition, expected) == matchShows;

            if (affectsVisibility)
            {
                block.style.display = satisfied ? DisplayStyle.Flex : DisplayStyle.None;
                return;
            }

            block.SetEnabled(satisfied);
            block.EnableInClassList("ir-block--disabled", !satisfied);
        }

        /// <summary>Tests a condition property against the value the attribute asked for.</summary>
        /// <param name="condition">Property the condition reads.</param>
        /// <param name="expected">Value to match, or null for a plain set test.</param>
        /// <returns>True when the property matches.</returns>
        private static bool Matches(SerializedProperty condition, object expected)
        {
            if (expected == null) return IsSet(condition);

            try
            {
                switch (condition.propertyType)
                {
                    case SerializedPropertyType.Boolean:
                        return condition.boolValue == Convert.ToBoolean(expected);

                    case SerializedPropertyType.Integer:
                        return condition.intValue == Convert.ToInt32(expected);

                    case SerializedPropertyType.Float:
                        return Mathf.Abs(condition.floatValue - Convert.ToSingle(expected)) < CONDITION_EPSILON;

                    case SerializedPropertyType.String:
                        return string.Equals(condition.stringValue, expected.ToString(), StringComparison.Ordinal);

                    case SerializedPropertyType.Enum:
                        return MatchesEnum(condition, expected);

                    default:
                        return IsSet(condition);
                }
            }
            catch (Exception)
            {
                // A value of the wrong type is an authoring mistake in the attribute, and leaving the field
                // visible is the reading that lets it be seen and fixed.
                return true;
            }
        }

        /// <summary>Tests an enum condition against either a member name or its numeric value.</summary>
        /// <param name="condition">Property the condition reads.</param>
        /// <param name="expected">Member name or value to match.</param>
        /// <returns>True when the property matches.</returns>
        private static bool MatchesEnum(SerializedProperty condition, object expected)
        {
            string name = expected as string;
            if (name == null) return condition.intValue == Convert.ToInt32(expected);

            string[] names = condition.enumNames;
            int index = condition.enumValueIndex;
            if (index < 0 || index >= names.Length) return false;

            return string.Equals(names[index], name, StringComparison.Ordinal);
        }

        /// <summary>Reports whether a condition property holds anything meaningful.</summary>
        /// <param name="condition">Property being tested.</param>
        /// <returns>True when the value counts as set.</returns>
        private static bool IsSet(SerializedProperty condition)
        {
            switch (condition.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    return condition.boolValue;

                case SerializedPropertyType.ObjectReference:
                    return condition.objectReferenceValue != null;

                case SerializedPropertyType.String:
                    return !string.IsNullOrEmpty(condition.stringValue);

                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Enum:
                    return condition.intValue != 0;

                case SerializedPropertyType.Float:
                    return Mathf.Abs(condition.floatValue) > CONDITION_EPSILON;

                default:
                    return true;
            }
        }

        #endregion

        #region Tabs

        /// <summary>Returns the page a tabbed field belongs to, building the strip on first use.</summary>
        /// <param name="root">Root element the strip and pages are added to.</param>
        /// <param name="tabName">Tab name, or null for a field that sits outside the tabs.</param>
        /// <returns>Element the field's group should be added to.</returns>
        private VisualElement ResolveTabPage(VisualElement root, string tabName)
        {
            if (string.IsNullOrEmpty(tabName)) return root;

            if (_tabStrip == null)
            {
                // The navigation lives in the masthead beside the loose fields; only the pages it
                // switches between sit below it.
                _tabStrip = new VisualElement();
                _tabStrip.AddToClassList("ir-tabs");
                _chrome.Add(_tabStrip);

                // The reference implementation puts every live element on a poll, and its tab group is
                // no exception. That tick is not only about re-asserting state: a class change settles
                // on a later style pass than the click that set it, and an editor window paints only
                // when something asks it to, so with nothing ticking the live tab is correct in the tree
                // while the screen still shows the previous one until an unrelated event forces a
                // redraw. The poll is what carries the mark to the screen there, and here.
                _tabStrip.schedule.Execute(SyncTabMarks).Every(POLL_INTERVAL);

                _tabHost = new VisualElement();
                root.Add(_tabHost);
            }

            VisualElement page;
            if (_tabPages.TryGetValue(tabName, out page)) return page;

            page = new VisualElement();
            _tabHost.Add(page);
            _tabPages[tabName] = page;
            _tabNames.Add(tabName);

            string selected = tabName;
            Button button = new Button(() => SelectTab(selected)) { text = tabName };
            button.AddToClassList("ir-tab");
            _tabStrip.Add(button);
            _tabButtons.Add(button);

            return page;
        }

        /// <summary>Shows one tab and remembers it, falling back to the first when the name is unknown.</summary>
        /// <param name="tabName">Tab to show.</param>
        private void SelectTab(string tabName)
        {
            if (_tabNames.Count == 0) return;
            if (string.IsNullOrEmpty(tabName) || !_tabPages.ContainsKey(tabName)) tabName = _tabNames[0];

            _activeTab = tabName;
            EditorPrefs.SetString(TAB_PREF_PREFIX + target.GetType().FullName, tabName);

            SyncTabMarks();
        }

        /// <summary>
        /// Puts every page and every button in step with the live tab. Idempotent on purpose: it runs
        /// once from the click and then on the strip's own schedule, where state that has not moved
        /// costs nothing and state that has not reached the screen yet is carried there. Each write is
        /// guarded so a tick that changes nothing does not dirty the panel a hundred times a second.
        /// <para>
        /// A button that really did change is marked for repaint by hand. Swapping the class settles
        /// the rule but does not always mark the element's visuals dirty, and the mark is a border
        /// colour rather than a layout, so nothing else asks for the paint: the tab reads as live in
        /// the tree while the screen still shows the old one until a pointer crossing it forces the
        /// redraw. Marking only on a real change keeps the idle tick free.
        /// </para>
        /// </summary>
        private void SyncTabMarks()
        {
            if (_activeTab == null) return;

            for (int i = 0; i < _tabNames.Count; i++)
            {
                bool active = string.Equals(_tabNames[i], _activeTab, StringComparison.Ordinal);
                DisplayStyle wanted = active ? DisplayStyle.Flex : DisplayStyle.None;

                VisualElement page = _tabPages[_tabNames[i]];
                if (page.style.display.value != wanted) page.style.display = wanted;

                if (_tabButtons[i].ClassListContains("ir-tab--active") == active) continue;

                _tabButtons[i].EnableInClassList("ir-tab--active", active);
                _tabButtons[i].MarkDirtyRepaint();
            }
        }

        #endregion

        #region Groups

        /// <summary>
        /// Returns the content container of a group the attributes already built, so a custom section
        /// can add to the card its fields are in rather than opening a second one beside it. Nothing is
        /// created here: a path no field asked for returns null, because a misspelling should leave the
        /// section where it was rather than conjure an empty card for it to hide in.
        /// </summary>
        /// <param name="path">Group path, spelled as the Card or Fold attribute spells it.</param>
        /// <param name="tabName">Tab the group sits on, or null when the behaviour has no tabs.</param>
        /// <returns>The group's content element, or null when no field built that group.</returns>
        protected VisualElement FindGroup(string path, string tabName = null)
        {
            VisualElement group;
            return _groups.TryGetValue(tabName + KEY_SEPARATOR + path, out group) ? group : null;
        }

        /// <summary>
        /// Moves a group below its siblings. Order attributes sort FIELDS, so they cannot put a group
        /// underneath a section that BuildCustomSections appended after them; this can, and is meant to
        /// be the last call in that override.
        /// </summary>
        /// <param name="path">Group path, spelled as the Card or Fold attribute spells it.</param>
        /// <param name="tabName">Tab the group sits on, or null when the behaviour has no tabs.</param>
        protected void SendGroupToBack(string path, string tabName = null)
        {
            VisualElement content = FindGroup(path, tabName);
            if (content == null) return;

            // A fold stores its BODY against the path, so the thing to move is the fold around it. A
            // card is its own content and moves directly.
            VisualElement block = content.ClassListContains("ir-fold__content") ? content.parent : content;
            if (block == null || block.parent == null) return;

            VisualElement parent = block.parent;
            parent.Remove(block);
            parent.Add(block);
        }

        /// <summary>Returns the element a field's control should be added to.</summary>
        /// <param name="root">Root element groups are added to.</param>
        /// <param name="field">Backing field, or null.</param>
        /// <returns>Tab page, card, fold body or row the field belongs in.</returns>
        private VisualElement ResolveHost(VisualElement root, MemberInfo member)
        {
            TabAttribute tab = GetAttribute<TabAttribute>(member);
            CardAttribute card = GetAttribute<CardAttribute>(member);
            FoldAttribute fold = GetAttribute<FoldAttribute>(member);
            RowAttribute row = GetAttribute<RowAttribute>(member);

            string tabName = tab == null ? null : tab.TabName;
            string groupPath = fold != null ? fold.FoldName : (card == null ? null : card.CardName);

            // A field with neither a tab nor a card belongs to the masthead, beside the navigation,
            // rather than sitting loose on the leather with nothing around it.
            VisualElement group = tabName == null && groupPath == null
                ? _chrome
                : ResolveGroup(root, tabName, groupPath, fold != null, fold == null || fold.OpenByDefault);

            return row == null ? group : ResolveRow(group, tabName, groupPath, row.RowName);
        }

        /// <summary>
        /// Returns the content container of a group, building it and any parent group on first use. A
        /// parent named only as part of a nested path is built as a plain card, so "Shape/Corners" gives
        /// a Shape card whether or not a field ever asked for one directly.
        /// </summary>
        /// <param name="root">Root element groups are added to.</param>
        /// <param name="tabName">Tab the group sits on, or null.</param>
        /// <param name="path">Full group path, or null for a field that sits outside any group.</param>
        /// <param name="collapsible">True to build the leaf as a fold rather than a card.</param>
        /// <param name="openByDefault">Whether a new fold starts open.</param>
        /// <returns>Element the field should be added to.</returns>
        private VisualElement ResolveGroup(
            VisualElement root,
            string tabName,
            string path,
            bool collapsible,
            bool openByDefault)
        {
            VisualElement page = ResolveTabPage(root, tabName);
            if (string.IsNullOrEmpty(path)) return page;

            string key = tabName + KEY_SEPARATOR + path;
            VisualElement existing;
            if (_groups.TryGetValue(key, out existing)) return existing;

            int separator = path.LastIndexOf(GROUP_SEPARATOR);
            string parentPath = separator < 0 ? null : path.Substring(0, separator);
            string heading = separator < 0 ? path : path.Substring(separator + 1);

            VisualElement parent = ResolveGroup(root, tabName, parentPath, false, true);
            VisualElement content = collapsible
                ? BuildFold(parent, heading, key, openByDefault)
                : BuildCard(parent, heading);

            _groups[key] = content;
            return content;
        }

        /// <summary>Builds one card, adds it to its parent and returns it.</summary>
        /// <param name="parent">Element receiving the card.</param>
        /// <param name="heading">Card heading.</param>
        /// <returns>The card, which is also its content container.</returns>
        private static VisualElement BuildCard(VisualElement parent, string heading)
        {
            VisualElement card = new VisualElement();
            card.AddToClassList("ir-card");

            Label label = new Label(heading);
            label.AddToClassList("ir-card__heading");
            card.Add(label);

            parent.Add(card);
            return card;
        }

        /// <summary>Builds one collapsible card, adds it to its parent and returns its body.</summary>
        /// <param name="parent">Element receiving the fold.</param>
        /// <param name="heading">Fold heading.</param>
        /// <param name="stateKey">Key the open state is remembered against.</param>
        /// <param name="openByDefault">Whether the fold starts open the first time it is seen.</param>
        /// <returns>The fold's content container.</returns>
        private static VisualElement BuildFold(
            VisualElement parent,
            string heading,
            string stateKey,
            bool openByDefault)
        {
            VisualElement fold = new VisualElement();
            fold.AddToClassList("ir-card");
            fold.AddToClassList("ir-fold");

            VisualElement head = new VisualElement();
            head.AddToClassList("ir-fold__head");

            VisualElement chevron = new VisualElement();
            chevron.AddToClassList("ir-fold__chevron");
            head.Add(chevron);

            Label label = new Label(heading);
            label.AddToClassList("ir-card__heading");
            label.AddToClassList("ir-fold__title");
            head.Add(label);

            VisualElement content = new VisualElement();
            content.AddToClassList("ir-fold__content");

            string preference = FOLD_PREF_PREFIX + stateKey;
            ApplyFoldState(fold, content, EditorPrefs.GetBool(preference, openByDefault));

            head.RegisterCallback<ClickEvent>(_ =>
            {
                bool open = !EditorPrefs.GetBool(preference, openByDefault);
                EditorPrefs.SetBool(preference, open);
                ApplyFoldState(fold, content, open);
            });

            fold.Add(head);
            fold.Add(content);
            parent.Add(fold);

            return content;
        }

        /// <summary>Opens or closes a fold.</summary>
        /// <param name="fold">Fold being changed.</param>
        /// <param name="content">The fold's body.</param>
        /// <param name="open">True to show the body.</param>
        private static void ApplyFoldState(VisualElement fold, VisualElement content, bool open)
        {
            fold.EnableInClassList("ir-fold--closed", !open);
            content.style.display = open ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>Returns the horizontal strip a field shares with its neighbours.</summary>
        /// <param name="group">Group the strip sits in.</param>
        /// <param name="tabName">Tab the group sits on, or null.</param>
        /// <param name="groupPath">Group path, or null.</param>
        /// <param name="rowName">Row name shared by the fields on it.</param>
        /// <returns>The row element.</returns>
        private VisualElement ResolveRow(VisualElement group, string tabName, string groupPath, string rowName)
        {
            string key = string.Concat(
                tabName,
                KEY_SEPARATOR,
                groupPath,
                KEY_SEPARATOR,
                ROW_KEY,
                KEY_SEPARATOR,
                rowName);

            VisualElement existing;
            if (_groups.TryGetValue(key, out existing)) return existing;

            VisualElement row = new VisualElement();
            row.AddToClassList("ir-row");
            group.Add(row);
            _groups[key] = row;

            return row;
        }

        #endregion

        #region Action Buttons

        /// <summary>
        /// Draws a button for every method carrying one, walking the inheritance chain so a behaviour
        /// inherits its base class's actions.
        /// </summary>
        /// <param name="root">Root element buttons without a named card are added to.</param>
        private void BuildButtons(VisualElement root)
        {
            List<string> drawn = new List<string>();

            Type type = target.GetType();
            while (type != null && type != typeof(MonoBehaviour) && type != typeof(object))
            {
                MethodInfo[] methods = type.GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];

                    ButtonAttribute attribute = method.GetCustomAttribute<ButtonAttribute>();
                    if (attribute == null) continue;
                    if (drawn.Contains(method.Name)) continue;
                    drawn.Add(method.Name);

                    ResolveButtonHost(root, attribute.CardName).Add(BuildActionButton(method, attribute));
                }

                type = type.BaseType;
            }
        }

        /// <summary>
        /// Returns the element a button is drawn into: the card of that name wherever it was built, so a
        /// button lands beside the fields it acts on, and a new card at the end when no field named one.
        /// </summary>
        /// <param name="root">Root element used when the button names no card.</param>
        /// <param name="cardPath">Card the button asked for, or null.</param>
        /// <returns>Element receiving the button.</returns>
        private VisualElement ResolveButtonHost(VisualElement root, string cardPath)
        {
            if (string.IsNullOrEmpty(cardPath)) return root;

            foreach (KeyValuePair<string, VisualElement> entry in _groups)
            {
                int separator = entry.Key.IndexOf(KEY_SEPARATOR);
                if (separator < 0) continue;

                string path = entry.Key.Substring(separator + 1);
                if (string.Equals(path, cardPath, StringComparison.Ordinal)) return entry.Value;
            }

            return ResolveGroup(root, null, cardPath, false, true);
        }

        /// <summary>Runs one action on every inspected object, undoably, and republishes the result.</summary>
        /// <param name="method">Method being called.</param>
        private void InvokeAction(MethodInfo method)
        {
            InvokeAction(method, null);
        }

        /// <summary>
        /// Records an object before an action changes it. An action that reaches past the component into
        /// the children has to be recorded against the whole hierarchy, or undo restores the component
        /// and leaves everything the action touched below it exactly as it was left.
        /// </summary>
        /// <param name="item">Object being changed.</param>
        /// <param name="name">Name the step is recorded under.</param>
        /// <param name="scope">How much of the scene to record.</param>
        private static void RecordForUndo(UnityEngine.Object item, string name, UndoScope scope)
        {
            Component component = item as Component;

            if (scope == UndoScope.Hierarchy && component != null)
            {
                Undo.RegisterFullObjectHierarchyUndo(component.gameObject, name);
                return;
            }

            Undo.RecordObject(item, name);
        }

        /// <summary>Runs one action with its arguments on every inspected object, undoably.</summary>
        /// <param name="method">Method being called.</param>
        /// <param name="arguments">Arguments collected for it, or null when it takes none.</param>
        /// <param name="scope">How much of the scene the action is recorded against.</param>
        private void InvokeAction(MethodInfo method, object[] arguments, UndoScope scope = UndoScope.Component)
        {
            for (int i = 0; i < targets.Length; i++)
            {
                UnityEngine.Object item = targets[i];
                if (item == null) continue;

                RecordForUndo(item, ObjectNames.NicifyVariableName(method.Name), scope);

                try
                {
                    method.Invoke(item, arguments);
                }
                catch (Exception exception)
                {
                    // A button that throws must not take the whole inspector down with it, and the
                    // exception says far more in the console than a blank panel does.
                    Debug.LogException(exception);
                }

                EditorUtility.SetDirty(item);
            }

            serializedObject.Update();
            PushToUdon();
            RunRefreshers();
        }

        #endregion

        #region Reflection and Udon

        /// <summary>
        /// Finds the backing field for a serialized property, walking up the inheritance chain because a
        /// behaviour's serialized fields are frequently declared on a base class.
        /// </summary>
        /// <param name="fieldName">Serialized field name.</param>
        /// <returns>The field, or null when it cannot be resolved.</returns>
        private FieldInfo FindField(string fieldName)
        {
            Type type = target.GetType();
            while (type != null)
            {
                FieldInfo field = type.GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null) return field;

                type = type.BaseType;
            }

            return null;
        }

        /// <summary>Finds a method named by an attribute, walking up the inheritance chain.</summary>
        /// <param name="methodName">Method name.</param>
        /// <returns>The method, or null when it cannot be resolved.</returns>
        private MethodInfo FindMethod(string methodName)
        {
            if (string.IsNullOrEmpty(methodName)) return null;

            Type type = target.GetType();
            while (type != null)
            {
                MethodInfo method = type.GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method != null) return method;

                type = type.BaseType;
            }

            return null;
        }

        /// <summary>Returns one attribute from a field, tolerating a field that could not be resolved.</summary>
        /// <typeparam name="T">Attribute type.</typeparam>
        /// <param name="member">Field or property the attributes sit on, or null.</param>
        /// <returns>The attribute, or null.</returns>
        private static T GetAttribute<T>(MemberInfo member) where T : Attribute
        {
            return member == null ? null : member.GetCustomAttribute<T>();
        }

        /// <summary>
        /// Carries inspector edits into a RUNNING Udon program: every authored field that differs from what
        /// the program last received is written on its own, converted to the type the program declares for
        /// it. Reached by reflection so this framework carries no UdonSharp or Udon reference and stays
        /// usable in a project that does not have them.
        /// <para>
        /// Only the changed fields are written, because the running program owns everything else - its
        /// private state, and any authored field it has changed itself since play began. UdonSharp's
        /// CopyProxyToUdon is not used: in play mode it also writes the proxy's private fields, and a proxy
        /// never runs, so every push would reset the program's counters, caches and state to their
        /// defaults. Leaving play mode undoes the edit like any other, because the proxy is restored with
        /// the scene. Outside play mode nothing is pushed, since UdonSharp carries the proxy to the program
        /// at build; the remembered values follow the edit instead, so play starts level with them.
        /// </para>
        /// </summary>
        private void PushToUdon()
        {
            if (!Application.isPlaying)
            {
                RememberPushedValues();
                return;
            }

            for (int i = 0; i < targets.Length; i++)
            {
                UnityEngine.Object item = targets[i];
                if (item == null) continue;

                Dictionary<string, object> pushed;
                if (!_pushedValues.TryGetValue(item, out pushed)) continue;

                UnityEngine.Object backing = BackingBehaviour(item);
                if (backing == null) continue;

                foreach (FieldInfo field in AuthoredFields(item.GetType()))
                {
                    object value = field.GetValue(item);
                    object previous;
                    pushed.TryGetValue(field.Name, out previous);
                    if (ValuesMatch(value, previous)) continue;

                    try
                    {
                        if (WriteProgramVariable(backing, field.Name, value)) pushed[field.Name] = CopyValue(value);
                    }
                    catch (Exception exception)
                    {
                        // A value the program refuses says far more in the console than a field left stale.
                        Debug.LogException(exception);
                    }
                }
            }
        }

        /// <summary>
        /// Remembers each scripted-behaviour target's authored values as the ones its program holds, which
        /// stays true until an edit in play mode moves one of them.
        /// </summary>
        private void RememberPushedValues()
        {
            _pushedValues.Clear();
            for (int i = 0; i < targets.Length; i++)
            {
                UnityEngine.Object item = targets[i];
                if (item == null || !IsProgramBehaviour(item.GetType())) continue;

                Dictionary<string, object> values = new Dictionary<string, object>();
                foreach (FieldInfo field in AuthoredFields(item.GetType())) values[field.Name] = CopyValue(field.GetValue(item));

                _pushedValues[item] = values;
            }
        }

        /// <summary>
        /// The fields a scripted behaviour serializes, which are the ones its program declares as variables:
        /// public or marked SerializeField or SerializeReference, never NonSerialized or readonly, from the
        /// type itself up to but not including UdonSharpBehaviour, whose own fields are its bookkeeping.
        /// </summary>
        /// <param name="type">Behaviour type.</param>
        /// <returns>The authored fields, found once per type.</returns>
        private static List<FieldInfo> AuthoredFields(Type type)
        {
            List<FieldInfo> fields;
            if (_authoredFields.TryGetValue(type, out fields)) return fields;

            fields = new List<FieldInfo>();
            for (Type walk = type; walk != null && walk.Name != "UdonSharpBehaviour"; walk = walk.BaseType)
            {
                FieldInfo[] declared = walk.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (FieldInfo field in declared)
                {
                    if (field.IsNotSerialized || field.IsInitOnly) continue;

                    bool serialized = field.IsPublic ||
                        field.IsDefined(typeof(SerializeField), true) ||
                        field.IsDefined(typeof(SerializeReference), true);
                    if (serialized) fields.Add(field);
                }
            }

            _authoredFields[type] = fields;
            return fields;
        }

        /// <summary>Finds the Udon behaviour a scripted-behaviour proxy runs as.</summary>
        /// <param name="proxy">Proxy behaviour.</param>
        /// <returns>The backing Udon behaviour, or null when there is none or UdonSharp is absent.</returns>
        private static UnityEngine.Object BackingBehaviour(UnityEngine.Object proxy)
        {
            Type utility = Type.GetType("UdonSharpEditor.UdonSharpEditorUtility, UdonSharp.Editor");
            Type proxyType = Type.GetType("UdonSharp.UdonSharpBehaviour, UdonSharp.Runtime");
            if (utility == null || proxyType == null) return null;

            MethodInfo find = utility.GetMethod(
                "GetBackingUdonBehaviour",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { proxyType },
                null);
            if (find == null) return null;

            return find.Invoke(null, new object[] { proxy }) as UnityEngine.Object;
        }

        /// <summary>Writes one variable into a running program, converted to the type the program declares.</summary>
        /// <param name="backing">Udon behaviour running the program.</param>
        /// <param name="name">Variable name, which is the field's name.</param>
        /// <param name="value">Value as the proxy holds it.</param>
        /// <returns>False when the program declares no such variable, so nothing was written.</returns>
        private static bool WriteProgramVariable(UnityEngine.Object backing, string name, object value)
        {
            Type backingType = backing.GetType();
            MethodInfo declared = backingType.GetMethod("GetProgramVariableType", new[] { typeof(string) });
            MethodInfo write = backingType.GetMethod("SetProgramVariable", new[] { typeof(string), typeof(object) });
            if (declared == null || write == null) return false;

            Type programType = declared.Invoke(backing, new object[] { name }) as Type;
            if (programType == null) return false;

            write.Invoke(backing, new[] { name, ToProgramValue(value, programType) });
            return true;
        }

        /// <summary>
        /// Converts a proxy's value to the form its program holds: a scripted behaviour becomes the Udon
        /// behaviour it runs as, an enum the program keeps as a number becomes that number, an array is
        /// rebuilt with the program's element type, and a curve is copied so the two never share one.
        /// </summary>
        /// <param name="value">Value as the proxy holds it.</param>
        /// <param name="programType">Type the program declares for it.</param>
        /// <returns>The value the program can hold.</returns>
        private static object ToProgramValue(object value, Type programType)
        {
            if (value == null) return null;

            // A destroyed object compares equal to null without being a null reference.
            UnityEngine.Object unityObject = value as UnityEngine.Object;
            if (!ReferenceEquals(unityObject, null) && unityObject == null) return null;

            Array array = value as Array;
            if (array != null)
            {
                Type element = programType.IsArray ? programType.GetElementType() : typeof(object);
                Array converted = Array.CreateInstance(element, array.Length);
                for (int i = 0; i < array.Length; i++) converted.SetValue(ToProgramValue(array.GetValue(i), element), i);

                return converted;
            }

            if (unityObject != null && IsProgramBehaviour(unityObject.GetType())) return BackingBehaviour(unityObject);
            if (value is AnimationCurve) return CopyValue(value);

            if (value.GetType().IsEnum && programType.IsPrimitive) return Convert.ChangeType(value, programType);

            return value;
        }

        /// <summary>Whether two authored values are the same, comparing arrays and curves by content.</summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>True when they match.</returns>
        private static bool ValuesMatch(object left, object right)
        {
            AnimationCurve leftCurve = left as AnimationCurve;
            AnimationCurve rightCurve = right as AnimationCurve;
            if (leftCurve != null && rightCurve != null) return CurvesMatch(leftCurve, rightCurve);

            Array leftArray = left as Array;
            Array rightArray = right as Array;
            if (leftArray == null && rightArray == null) return object.Equals(left, right);
            if (leftArray == null || rightArray == null || leftArray.Length != rightArray.Length) return false;

            for (int i = 0; i < leftArray.Length; i++)
            {
                if (!object.Equals(leftArray.GetValue(i), rightArray.GetValue(i))) return false;
            }

            return true;
        }

        /// <summary>Whether two curves have the same keys and wrap modes.</summary>
        /// <param name="left">One curve.</param>
        /// <param name="right">The other curve.</param>
        /// <returns>True when they match.</returns>
        private static bool CurvesMatch(AnimationCurve left, AnimationCurve right)
        {
            if (left.preWrapMode != right.preWrapMode || left.postWrapMode != right.postWrapMode) return false;

            Keyframe[] leftKeys = left.keys;
            Keyframe[] rightKeys = right.keys;
            if (leftKeys.Length != rightKeys.Length) return false;

            for (int i = 0; i < leftKeys.Length; i++)
            {
                if (!leftKeys[i].Equals(rightKeys[i])) return false;
            }

            return true;
        }

        /// <summary>Copies an authored value so a later edit cannot change the copy underneath it.</summary>
        /// <param name="value">Value as the proxy holds it.</param>
        /// <returns>A shallow copy of an array, a new curve with the same keys, or the value itself.</returns>
        private static object CopyValue(object value)
        {
            Array array = value as Array;
            if (array != null) return array.Clone();

            AnimationCurve curve = value as AnimationCurve;
            if (curve == null) return value;

            AnimationCurve copy = new AnimationCurve(curve.keys);
            copy.preWrapMode = curve.preWrapMode;
            copy.postWrapMode = curve.postWrapMode;
            return copy;
        }

        #endregion

        #region Asset Thumbnail

        /// <summary>
        /// The square asset preview that sits beside a reference field. A rendered preview is produced on
        /// a worker and arrives over several frames, so this asks again until the one it is waiting for
        /// has finished; a single read would show the placeholder for good.
        /// </summary>
        private sealed class AssetThumbnail : VisualElement
        {
            /// <summary>Multiple of a standard row height used when no size was asked for.</summary>
            private const float DEFAULT_SIZE_IN_ROWS = 4f;

            /// <summary>How long to wait before asking again for a preview still being rendered.</summary>
            private const long RETRY_DELAY = 100;

            /// <summary>Property holding the reference being shown.</summary>
            private readonly SerializedProperty _property;

            /// <summary>The art itself.</summary>
            private readonly Image _art;

            /// <summary>Shown in place of the art while the reference is empty.</summary>
            private readonly Label _empty;

            /// <summary>Builds the thumbnail for one reference field.</summary>
            /// <param name="property">Property holding the reference.</param>
            /// <param name="height">Side of the square, or zero for the default.</param>
            public AssetThumbnail(SerializedProperty property, float height)
            {
                _property = property;
                float size = height > 0f ? height : EditorGUIUtility.singleLineHeight * DEFAULT_SIZE_IN_ROWS;

                AddToClassList("ir-thumbnail");
                style.width = size;
                style.height = size;

                _art = new Image { scaleMode = ScaleMode.ScaleToFit };
                _art.AddToClassList("ir-thumbnail__art");
                Add(_art);

                _empty = new Label("None");
                _empty.AddToClassList("ir-thumbnail__empty");
                Add(_empty);

                Refresh();
            }

            /// <summary>Puts the current reference's art in the square.</summary>
            public void Refresh()
            {
                UnityEngine.Object value = _property.objectReferenceValue;

                _empty.style.display = value == null ? DisplayStyle.Flex : DisplayStyle.None;
                _art.style.display = value == null ? DisplayStyle.None : DisplayStyle.Flex;

                if (value == null)
                {
                    _art.image = null;
                    return;
                }

                Sprite sprite = value as Sprite;
                if (sprite != null)
                {
                    // Sprites are frequently packed, so only the sprite's own rectangle of the sheet is
                    // the right thing to show.
                    _art.image = sprite.texture;
                    _art.sourceRect = sprite.textureRect;
                    return;
                }

                _art.sourceRect = new Rect();

                Texture texture = value as Texture;
                if (texture != null)
                {
                    _art.image = texture;
                    return;
                }

                _art.image = AssetPreview.GetAssetPreview(value);

                if (AssetPreview.IsLoadingAssetPreview(value.GetInstanceID()))
                {
                    schedule.Execute(Refresh).ExecuteLater(RETRY_DELAY);
                }
            }

        }

        #endregion

        #region Mesh Viewport

        /// <summary>
        /// The viewport that renders a mesh and turns it as it is dragged. It owns its own render scene
        /// rather than borrowing the asset's editor, and renders that scene into a texture of its own so
        /// the result can be shown as an ordinary image and driven by ordinary pointer events.
        /// </summary>
        private sealed class MeshViewport : VisualElement
        {
            /// <summary>Degrees turned per pixel dragged. Negative so the model follows the pointer.</summary>
            private const float ROTATION_SENSITIVITY = -0.5f;

            /// <summary>Camera distance gained per unit scrolled.</summary>
            private const float ZOOM_SENSITIVITY = 0.1f;

            /// <summary>Closest the camera may be pulled in, as a multiple of the mesh extents.</summary>
            private const float ZOOM_DISTANCE_MIN = 2f;

            /// <summary>Furthest the camera may be pushed out, as a multiple of the mesh extents.</summary>
            private const float ZOOM_DISTANCE_MAX = 10f;

            /// <summary>Narrowest the viewport is allowed to render at.</summary>
            private const float MINIMUM_WIDTH = 50f;

            /// <summary>Camera distance a freshly shown mesh starts at.</summary>
            private const float DEFAULT_CAMERA_DISTANCE = 4f;

            /// <summary>Brightness of both preview lights.</summary>
            private const float LIGHT_INTENSITY = 1.3f;

            /// <summary>Vertical field of view the preview camera renders with.</summary>
            private const float CAMERA_FIELD_OF_VIEW = 30f;

            /// <summary>Shaders tried in order for the material the mesh is drawn with.</summary>
            private static readonly string[] PREVIEW_SHADERS =
            {
                "Universal Render Pipeline/Lit",
                "Standard",
                "Legacy Shaders/Diffuse",
            };

            /// <summary>Material every viewport in the editor session shares.</summary>
            private static Material _sharedMaterial;

            /// <summary>Property holding the reference being previewed.</summary>
            private readonly SerializedProperty _property;

            /// <summary>Height of the viewport in pixels.</summary>
            private readonly float _height;

            /// <summary>Width in pixels, or a negative number to fill the card.</summary>
            private readonly float _width;

            /// <summary>How the model reacts to being dragged.</summary>
            private readonly PreviewRotation _rotation;

            /// <summary>The element the render is shown in.</summary>
            private readonly Image _surface;

            /// <summary>Texture the preview camera renders into.</summary>
            private RenderTexture _target;

            /// <summary>Whether a drag is turning the model right now.</summary>
            private bool _turning;

            /// <summary>Scene the mesh is rendered in, alive only while the element is on a panel.</summary>
            private PreviewRenderUtility _scene;

            /// <summary>Mesh currently being shown, or null when the reference holds none.</summary>
            private Mesh _mesh;

            /// <summary>Orientation the mesh is drawn at.</summary>
            private Quaternion _orientation = Quaternion.identity;

            /// <summary>Pitch and yaw the clamped rotation accumulates into.</summary>
            private Vector2 _direction = new Vector2(-20f, 0f);

            /// <summary>Camera distance as a multiple of the mesh extents.</summary>
            private float _distance = DEFAULT_CAMERA_DISTANCE;

            /// <summary>Builds the viewport for one reference field.</summary>
            /// <param name="property">Property holding the reference.</param>
            /// <param name="attribute">Attribute carrying the size and rotation style.</param>
            public MeshViewport(SerializedProperty property, InteractivePreviewAttribute attribute)
            {
                _property = property;
                _height = attribute.Height;
                _width = attribute.Width;
                _rotation = attribute.Rotation;

                AddToClassList("ir-stage");

                _surface = new Image { scaleMode = ScaleMode.ScaleToFit };
                _surface.style.height = _height;
                Add(_surface);

                _surface.RegisterCallback<PointerDownEvent>(OnPointerDown);
                _surface.RegisterCallback<PointerMoveEvent>(OnPointerMove);
                _surface.RegisterCallback<PointerUpEvent>(OnPointerUp);
                _surface.RegisterCallback<WheelEvent>(OnWheel);
                _surface.RegisterCallback<GeometryChangedEvent>(_ => Render());

                style.display = DisplayStyle.None;
                style.height = _height;

                // A negative width means "as wide as the card", which is simply not setting one.
                if (_width > 0f) style.width = _width;

                RegisterCallback<AttachToPanelEvent>(OnAttachToPanel);
                RegisterCallback<DetachFromPanelEvent>(OnDetachFromPanel);
            }

            /// <summary>Stands the preview scene up and reads the reference for the first time.</summary>
            /// <param name="evt">Attach event, unused.</param>
            private void OnAttachToPanel(AttachToPanelEvent evt)
            {
                _scene = new PreviewRenderUtility();

                _scene.lights[0].intensity = LIGHT_INTENSITY;
                _scene.lights[0].transform.rotation = Quaternion.Euler(40f, 40f, 0f);
                _scene.lights[1].intensity = LIGHT_INTENSITY;

                _scene.cameraFieldOfView = CAMERA_FIELD_OF_VIEW;
                _scene.camera.nearClipPlane = 0.1f;
                _scene.camera.farClipPlane = 100f;
                _scene.camera.backgroundColor = Color.black;
                _scene.camera.clearFlags = CameraClearFlags.Color;

                ReadMesh();
                RefreshVisibility();
            }

            /// <summary>Tears the preview scene down. It holds a camera and lights that must be released.</summary>
            /// <param name="evt">Detach event, unused.</param>
            private void OnDetachFromPanel(DetachFromPanelEvent evt)
            {
                if (_scene != null) _scene.Cleanup();
                _scene = null;

                ReleaseTarget();
            }

            /// <summary>Re-reads the reference after the inspected object changed.</summary>
            public void OnValueChanged()
            {
                // Nothing to render into until the element is on a panel, and attaching reads the
                // reference for itself, so a change arriving before that is simply not this one's work.
                if (_scene == null) return;

                ReadMesh();
                RefreshVisibility();
            }

            /// <summary>Shows the viewport only while there is a mesh for it to render.</summary>
            private void RefreshVisibility()
            {
                style.display = _mesh != null ? DisplayStyle.Flex : DisplayStyle.None;
                if (_mesh != null) Render();
            }

            /// <summary>
            /// Resolves the mesh the reference stands for, resetting the view each time it changes. A
            /// reference to anything with no mesh in it clears the viewport rather than leaving the
            /// previous model in view under the new reference's name.
            /// </summary>
            private void ReadMesh()
            {
                UnityEngine.Object value = _property.objectReferenceValue;
                if (value == null)
                {
                    _mesh = null;
                    return;
                }

                _orientation = Quaternion.Euler(_direction);
                _distance = DEFAULT_CAMERA_DISTANCE;
                _mesh = ResolveMesh(value);

                if (_mesh != null) FrameMesh();
            }

            /// <summary>Finds the mesh inside whatever the reference points at.</summary>
            /// <param name="value">Referenced object.</param>
            /// <returns>The mesh, or null when the reference carries none.</returns>
            private static Mesh ResolveMesh(UnityEngine.Object value)
            {
                Mesh mesh = value as Mesh;
                if (mesh != null) return mesh;

                GameObject gameObject = value as GameObject;
                if (gameObject == null) return null;

                MeshFilter filter = gameObject.GetComponentInChildren<MeshFilter>();
                return filter == null ? null : filter.sharedMesh;
            }

            /// <summary>Puts the camera far enough back to hold the whole mesh in view.</summary>
            private void FrameMesh()
            {
                Bounds bounds = _mesh.bounds;

                // A mesh of a single point has no extents to scale the camera distance by.
                if (bounds.size == Vector3.zero) bounds = new Bounds(Vector3.zero, Vector3.one);

                float magnitude = bounds.extents.magnitude;
                _scene.camera.transform.position = bounds.center + Vector3.back * (magnitude * _distance);
                _scene.camera.transform.LookAt(bounds.center);
            }

            /// <summary>Renders one frame of the preview into the texture the image shows.</summary>
            private void Render()
            {
                if (_mesh == null || _scene == null) return;

                float available = _width < 0f ? _surface.resolvedStyle.width : _width;
                int width = Mathf.RoundToInt(Mathf.Max(available, MINIMUM_WIDTH));
                int height = Mathf.RoundToInt(Mathf.Max(_height, 1f));
                if (width <= 0 || height <= 0) return;

                EnsureTarget(width, height);

                // Rendered through a texture of our own rather than the utility's own begin and end pair,
                // because those two are IMGUI calls and this viewport is an ordinary image.
                _scene.camera.targetTexture = _target;
                _scene.DrawMesh(_mesh, Matrix4x4.TRS(Vector3.zero, _orientation, Vector3.one), Material, 0);
                _scene.camera.Render();
                _scene.camera.targetTexture = null;

                _surface.image = _target;
                _surface.MarkDirtyRepaint();
            }

            /// <summary>Makes sure the render texture matches the size being drawn at.</summary>
            /// <param name="width">Width in pixels.</param>
            /// <param name="height">Height in pixels.</param>
            private void EnsureTarget(int width, int height)
            {
                if (_target != null && _target.width == width && _target.height == height) return;

                ReleaseTarget();
                _target = new RenderTexture(width, height, 24) { hideFlags = HideFlags.HideAndDontSave };
            }

            /// <summary>Frees the render texture, which nothing else will collect for us.</summary>
            private void ReleaseTarget()
            {
                if (_target == null) return;

                _target.Release();
                UnityEngine.Object.DestroyImmediate(_target);
                _target = null;
            }

            /// <summary>Starts a drag.</summary>
            /// <param name="evt">Event being handled.</param>
            private void OnPointerDown(PointerDownEvent evt)
            {
                _turning = true;

                // Capturing keeps the drag alive when the pointer leaves the small viewport.
                _surface.CapturePointer(evt.pointerId);
                evt.StopPropagation();
            }

            /// <summary>Turns the model while a drag is in progress.</summary>
            /// <param name="evt">Event being handled.</param>
            private void OnPointerMove(PointerMoveEvent evt)
            {
                if (!_turning) return;

                Turn(evt.deltaPosition * ROTATION_SENSITIVITY);
                Render();
                evt.StopPropagation();
            }

            /// <summary>Ends a drag.</summary>
            /// <param name="evt">Event being handled.</param>
            private void OnPointerUp(PointerUpEvent evt)
            {
                _turning = false;
                _surface.ReleasePointer(evt.pointerId);
                evt.StopPropagation();
            }

            /// <summary>Moves the camera in and out.</summary>
            /// <param name="evt">Event being handled.</param>
            private void OnWheel(WheelEvent evt)
            {
                _distance = Mathf.Clamp(
                    _distance + evt.delta.y * ZOOM_SENSITIVITY,
                    ZOOM_DISTANCE_MIN,
                    ZOOM_DISTANCE_MAX);

                FrameMesh();
                Render();
                evt.StopPropagation();
            }

            /// <summary>Applies one drag to the model's orientation.</summary>
            /// <param name="movement">Pitch and yaw in degrees.</param>
            private void Turn(Vector2 movement)
            {
                float pitch = movement.y;
                float yaw = movement.x;

                switch (_rotation)
                {
                    case PreviewRotation.Clamped:
                        _direction.x = Mathf.Clamp(pitch + _direction.x, -90f, 90f);
                        _direction.y += yaw;
                        _orientation = Quaternion.Euler(_direction.x, 0f, 0f) * Quaternion.Euler(0f, _direction.y, 0f);
                        break;

                    case PreviewRotation.Freeform:
                        _orientation = Quaternion.Euler(pitch, yaw, 0f) * _orientation;
                        break;
                }

                _orientation = Quaternion.Normalize(_orientation);
            }

            /// <summary>The material every preview mesh is drawn with, built once per editor session.</summary>
            private static Material Material
            {
                get
                {
                    if (_sharedMaterial != null) return _sharedMaterial;

                    Shader shader = ResolveShader();
                    if (shader == null) return null;

                    _sharedMaterial = new Material(shader)
                    {
                        hideFlags = HideFlags.HideAndDontSave,
                        color = new Color(0.78f, 0.83f, 0.86f),
                    };

                    return _sharedMaterial;
                }
            }

            /// <summary>Finds the first of the preview shaders this project actually has.</summary>
            /// <returns>The shader, or null when the project has none of them.</returns>
            private static Shader ResolveShader()
            {
                for (int i = 0; i < PREVIEW_SHADERS.Length; i++)
                {
                    Shader shader = Shader.Find(PREVIEW_SHADERS[i]);
                    if (shader != null) return shader;
                }

                return null;
            }
        }

        #endregion

        #region Inline Asset Editor

        /// <summary>
        /// Draws a referenced asset's own inspector in place, through UI Toolkit's own nested inspector.
        /// The header and the preview are ours rather than Unity's, because Unity's are IMGUI calls.
        /// </summary>
        private sealed class InlineAssetEditor : VisualElement
        {
            /// <summary>How often the reference is re-read, in milliseconds.</summary>
            private const long POLL_INTERVAL = 100;


            /// <summary>Property holding the reference being edited.</summary>
            private readonly SerializedProperty _property;

            /// <summary>Which parts of the asset's inspector are drawn.</summary>
            private readonly InlineEditorMode _mode;

            /// <summary>Height of the asset's own preview, when that part is drawn.</summary>
            private readonly float _previewHeight;

            /// <summary>Editor currently drawing the referenced asset, or null.</summary>
            private UnityEditor.Editor _editor;

            /// <summary>Asset the current editor was built for.</summary>
            private UnityEngine.Object _editorTarget;

            /// <summary>Builds the nested inspector for one reference field.</summary>
            /// <param name="property">Property holding the reference.</param>
            /// <param name="attribute">Attribute carrying the mode and preview height.</param>
            public InlineAssetEditor(SerializedProperty property, InlineEditorAttribute attribute)
            {
                _property = property;
                _mode = attribute.Mode;
                _previewHeight = attribute.PreviewHeight;

                AddToClassList("ir-stage");
                AddToClassList("ir-stage--editor");
                style.display = DisplayStyle.None;

                RegisterCallback<AttachToPanelEvent>(_ => Sync());
                RegisterCallback<DetachFromPanelEvent>(_ => DestroyEditor());

                // The reference can change without this element hearing about it, so it is re-read on a
                // timer rather than waiting for a notification that may never come.
                schedule.Execute(Sync).Every(POLL_INTERVAL);
            }

            /// <summary>Builds, replaces or drops the nested editor to match the current reference.</summary>
            private void Sync()
            {
                UnityEngine.Object value = _property.objectReferenceValue;

                if (value == null)
                {
                    if (_editor != null)
                    {
                        DestroyEditor();
                        Clear();
                    }

                    style.display = DisplayStyle.None;
                    return;
                }

                if (_editor == null || _editorTarget != value)
                {
                    DestroyEditor();

                    _editor = UnityEditor.Editor.CreateEditor(value);
                    _editorTarget = value;

                    // A nested inspector honours the collapsed state the asset was last left in, which
                    // reads as an empty panel. Opening it is what makes the fields appear at all.
                    ExpandInInspector(value);
                    Rebuild();
                }

                style.display = DisplayStyle.Flex;
            }

            /// <summary>Puts the parts the mode asks for into the element.</summary>
            private void Rebuild()
            {
                Clear();

                if ((_mode & InlineEditorMode.Header) != 0) Add(BuildAssetHeader());
                if ((_mode & InlineEditorMode.Gui) != 0) Add(new InspectorElement(_editor));
                if ((_mode & InlineEditorMode.Preview) != 0) Add(BuildAssetPreview());
            }

            /// <summary>
            /// Builds the asset's icon, name and kind. Unity's own header is an IMGUI call, so this is a
            /// plainer one that says the same three things.
            /// </summary>
            /// <returns>The header element.</returns>
            private VisualElement BuildAssetHeader()
            {
                VisualElement header = new VisualElement();
                header.AddToClassList("ir-asset-header");

                Image icon = new Image { scaleMode = ScaleMode.ScaleToFit };
                icon.AddToClassList("ir-asset-header__icon");
                icon.image = AssetPreview.GetMiniThumbnail(_editorTarget);
                header.Add(icon);

                Label name = new Label(_editorTarget == null ? "None" : _editorTarget.name);
                name.AddToClassList("ir-asset-header__name");
                header.Add(name);

                Label kind = new Label(_editorTarget == null ? string.Empty : _editorTarget.GetType().Name);
                kind.AddToClassList("ir-asset-header__kind");
                header.Add(kind);

                return header;
            }

            /// <summary>
            /// Builds the asset's preview as a still image. Unity's own preview is an IMGUI call that can
            /// be turned; this is the rendered thumbnail of the same asset, which cannot.
            /// </summary>
            /// <returns>The preview element.</returns>
            private VisualElement BuildAssetPreview()
            {
                Image preview = new Image { scaleMode = ScaleMode.ScaleToFit };
                preview.AddToClassList("ir-asset-preview");
                preview.style.height = _previewHeight;

                RefreshAssetPreview(preview);
                return preview;
            }

            /// <summary>Fills the preview, asking again while the render is still being made.</summary>
            /// <param name="preview">Element being filled.</param>
            private void RefreshAssetPreview(Image preview)
            {
                if (_editorTarget == null) return;

                preview.image = AssetPreview.GetAssetPreview(_editorTarget);

                if (AssetPreview.IsLoadingAssetPreview(_editorTarget.GetInstanceID()))
                {
                    preview.schedule.Execute(() => RefreshAssetPreview(preview)).ExecuteLater(POLL_INTERVAL);
                }
            }

            /// <summary>Releases the nested editor, which is an object Unity will not collect for us.</summary>
            private void DestroyEditor()
            {
                if (_editor != null) DestroyImmediate(_editor);

                _editor = null;
                _editorTarget = null;
            }

            /// <summary>
            /// Marks an asset as expanded in the inspector. The pair of methods that do this are internal
            /// to the editor assembly, so they are reached by reflection rather than by taking a
            /// dependency on Unity's internal API bridge; the nested editor simply stays as the user left
            /// it if a future version renames them.
            /// </summary>
            /// <param name="value">Asset being expanded.</param>
            private static void ExpandInInspector(UnityEngine.Object value)
            {
                Type utility = typeof(EditorApplication).Assembly.GetType("UnityEditorInternal.InternalEditorUtility");
                if (utility == null) return;

                MethodInfo get = utility.GetMethod(
                    "GetIsInspectorExpanded",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                MethodInfo set = utility.GetMethod(
                    "SetIsInspectorExpanded",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (get == null || set == null) return;

                bool expanded = (bool)get.Invoke(null, new object[] { value });
                if (expanded) return;

                set.Invoke(null, new object[] { value, true });
            }
        }

        #endregion
    }
}
