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
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ItsRenji.Inspector.Editors
{
    /// <summary>
    /// The half of the branded inspector that picks a value out of somewhere else: a scene, the
    /// project, an animator's parameters, a shader's properties, or the object's own components. Also
    /// the arguments an action button collects before it runs.
    /// </summary>
    public abstract partial class ItsRenjiInspector
    {
        #region Picker Entries

        /// <summary>Entry that opens a slot for an asset the list does not hold.</summary>
        private const string CUSTOM_ENTRY = "Custom...";

        /// <summary>One thing a picker offers: what it is called, and the number that stands for it.</summary>
        private struct NamedId
        {
            /// <summary>Name shown in the list, and stored when the field holds text.</summary>
            public string Name;

            /// <summary>Number stored when the field holds a whole number instead of the name.</summary>
            public int Id;
        }

        #endregion

        #region Scene

        /// <summary>
        /// Draws a string holding a scene's asset path as a scene picker, so the path is chosen rather
        /// than typed and cannot quietly rot when the scene is moved.
        /// </summary>
        /// <param name="property">Property holding the path.</param>
        /// <param name="label">Label the picker shows.</param>
        /// <returns>The picker.</returns>
        private VisualElement BuildSceneField(SerializedProperty property, string label)
        {
            ObjectField field = new ObjectField(label)
            {
                objectType = typeof(SceneAsset),
                allowSceneObjects = false,
            };

            SerializedProperty tracked = property.Copy();

            field.RegisterValueChangedCallback(evt => WriteString(tracked, AssetDatabase.GetAssetPath(evt.newValue)));
            _refreshers.Add(() => field.SetValueWithoutNotify(
                AssetDatabase.LoadAssetAtPath<SceneAsset>(tracked.stringValue)));

            return field;
        }

        #endregion

        #region Assets

        /// <summary>
        /// Draws a reference as a list of the matching assets in the project. Better than the object
        /// picker when the answer is always one of a small known set, and worse when it is not, so the
        /// filter is the author's to choose.
        /// </summary>
        /// <param name="property">Property holding the reference.</param>
        /// <param name="member">Member the reference belongs to, which settles the type wanted.</param>
        /// <param name="label">Label the list shows.</param>
        /// <param name="attribute">Attribute carrying the filter and the folders searched.</param>
        /// <returns>The list.</returns>
        private VisualElement BuildAssetDropdown(
            SerializedProperty property,
            MemberInfo member,
            string label,
            AssetDropdownAttribute attribute)
        {
            Type wanted = MemberType(member);
            SerializedProperty tracked = property.Copy();

            VisualElement root = new VisualElement();
            root.AddToClassList("ir-asset-dropdown");

            DropdownField picker = new DropdownField(label);
            List<UnityEngine.Object> assets = new List<UnityEngine.Object>();
            root.Add(picker);

            ObjectField slot = new ObjectField("From anywhere")
            {
                objectType = wanted ?? typeof(UnityEngine.Object),
                allowSceneObjects = false,
            };
            slot.AddToClassList("ir-asset-dropdown__slot");
            slot.style.display = DisplayStyle.None;
            root.Add(slot);

            // Held rather than worked out from the value every refresh, because the pick is a state of
            // the panel: deriving it would snap the list shut the moment a listed asset reached the slot.
            bool[] custom = { false };

            picker.RegisterValueChangedCallback(evt =>
            {
                if (attribute.AllowCustom && string.Equals(evt.newValue, CUSTOM_ENTRY, StringComparison.Ordinal))
                {
                    custom[0] = true;
                    slot.style.display = DisplayStyle.Flex;
                    return;
                }

                custom[0] = false;

                int index = picker.choices.IndexOf(evt.newValue);
                if (index < 0 || index >= assets.Count) return;

                WriteReference(tracked, assets[index]);
            });

            slot.RegisterValueChangedCallback(evt =>
            {
                // An asset the list already holds, dropped into the slot, IS a listed entry, so the held
                // pick is released and the list shows it by name again.
                if (assets.Contains(evt.newValue)) custom[0] = false;

                WriteReference(tracked, evt.newValue);
            });

            _refreshers.Add(() => RefreshAssetDropdown(picker, slot, assets, tracked, wanted, attribute, custom));
            return root;
        }

        /// <summary>Re-reads the matching assets and marks whichever one the field holds.</summary>
        /// <param name="picker">List being refreshed.</param>
        /// <param name="assets">Assets behind the names, kept in step with them.</param>
        /// <param name="property">Property holding the reference.</param>
        /// <param name="wanted">Type the field accepts, or null when it could not be worked out.</param>
        /// <param name="slot">Field shown for an asset the list does not hold.</param>
        /// <param name="attribute">Attribute carrying the filter and the folders searched.</param>
        /// <param name="custom">Whether the reader has parked the list on the custom entry.</param>
        private static void RefreshAssetDropdown(
            DropdownField picker,
            ObjectField slot,
            List<UnityEngine.Object> assets,
            SerializedProperty property,
            Type wanted,
            AssetDropdownAttribute attribute,
            bool[] custom)
        {
            assets.Clear();
            List<string> names = new List<string>();

            // An empty entry first, so a reference can be cleared from the same list that sets it.
            assets.Add(null);
            names.Add("None");

            string[] guids = attribute.SearchInFolders == null || attribute.SearchInFolders.Length == 0
                ? AssetDatabase.FindAssets(attribute.Filter)
                : AssetDatabase.FindAssets(attribute.Filter, attribute.SearchInFolders);

            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);

                if (asset == null) continue;
                if (wanted != null && !wanted.IsInstanceOfType(asset)) continue;

                assets.Add(asset);
                names.Add(asset.name);
            }

            if (attribute.AllowCustom) names.Add(CUSTOM_ENTRY);
            picker.choices = names;

            UnityEngine.Object held = property.objectReferenceValue;
            int chosen = assets.IndexOf(held);

            // Anything the list does not hold can only be shown by the custom entry, so a value set from
            // somewhere else opens the slot by itself rather than reading as None.
            bool showSlot = attribute.AllowCustom && (custom[0] || chosen < 0);

            slot.style.display = showSlot ? DisplayStyle.Flex : DisplayStyle.None;
            slot.SetValueWithoutNotify(held);

            picker.SetValueWithoutNotify(showSlot
                ? CUSTOM_ENTRY
                : (chosen >= 0 ? names[chosen] : "None"));
        }

        #endregion

        #region Animator and Material

        /// <summary>Draws a field as the list of an animator's parameters.</summary>
        /// <param name="property">Property holding the parameter's name or its hash.</param>
        /// <param name="label">Label the list shows.</param>
        /// <param name="attribute">Attribute naming the animator and narrowing the kind offered.</param>
        /// <returns>The list.</returns>
        private VisualElement BuildAnimatorParameter(
            SerializedProperty property,
            string label,
            AnimatorParameterAttribute attribute)
        {
            ValueSource<Animator> source = ValueSource<Animator>.ForMember(TargetType, attribute.AnimatorMember);
            return BuildNameDropdown(property, label, () => AnimatorParameters(source, attribute));
        }

        /// <summary>Reads the parameters an animator currently declares.</summary>
        /// <param name="source">Where the animator comes from.</param>
        /// <param name="attribute">Attribute narrowing the kind offered.</param>
        /// <returns>One entry per parameter, or none when there is no controller to read.</returns>
        private List<NamedId> AnimatorParameters(
            ValueSource<Animator> source,
            AnimatorParameterAttribute attribute)
        {
            List<NamedId> entries = new List<NamedId>();

            Animator animator = source.GetValue(target, null);

            // Reading parameters off an animator with no controller throws, so both are checked.
            if (animator == null || animator.runtimeAnimatorController == null) return entries;

            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (attribute.HasParameterType && parameters[i].type != attribute.ParameterType) continue;

                entries.Add(new NamedId { Name = parameters[i].name, Id = parameters[i].nameHash });
            }

            return entries;
        }

        /// <summary>Draws a field as the list of a material's shader properties.</summary>
        /// <param name="property">Property holding the shader property's name or its id.</param>
        /// <param name="label">Label the list shows.</param>
        /// <param name="attribute">Attribute naming the material and narrowing the kind offered.</param>
        /// <returns>The list.</returns>
        private VisualElement BuildMaterialProperty(
            SerializedProperty property,
            string label,
            MaterialPropertyAttribute attribute)
        {
            ValueSource<Material> source = ValueSource<Material>.ForMember(TargetType, attribute.MaterialMember);
            return BuildNameDropdown(property, label, () => MaterialProperties(source, attribute));
        }

        /// <summary>Reads the properties a material's shader currently declares.</summary>
        /// <param name="source">Where the material comes from.</param>
        /// <param name="attribute">Attribute narrowing the kind offered.</param>
        /// <returns>One entry per property, or none when there is no shader to read.</returns>
        private List<NamedId> MaterialProperties(
            ValueSource<Material> source,
            MaterialPropertyAttribute attribute)
        {
            List<NamedId> entries = new List<NamedId>();

            Material material = source.GetValue(target, null);
            if (material == null || material.shader == null) return entries;

            Shader shader = material.shader;
            int count = shader.GetPropertyCount();

            for (int i = 0; i < count; i++)
            {
                if (attribute.HasPropertyType && shader.GetPropertyType(i) != attribute.PropertyType) continue;

                entries.Add(new NamedId { Name = shader.GetPropertyName(i), Id = shader.GetPropertyNameId(i) });
            }

            return entries;
        }

        /// <summary>
        /// Draws a list whose entries are read fresh every refresh, writing the chosen entry's name to a
        /// text field or its number to a whole-number one. The entries come from somewhere that can
        /// change while the inspector is open, so the list is never built once and kept.
        /// </summary>
        /// <param name="property">Property being written.</param>
        /// <param name="label">Label the list shows.</param>
        /// <param name="supply">Reads the entries currently on offer.</param>
        /// <returns>The list.</returns>
        private VisualElement BuildNameDropdown(
            SerializedProperty property,
            string label,
            Func<List<NamedId>> supply)
        {
            bool byId = property.propertyType == SerializedPropertyType.Integer;

            DropdownField picker = new DropdownField(label);
            List<NamedId> entries = new List<NamedId>();
            SerializedProperty tracked = property.Copy();

            picker.RegisterValueChangedCallback(evt =>
            {
                int index = picker.choices.IndexOf(evt.newValue);
                if (index < 0 || index >= entries.Count) return;

                if (byId) WriteInteger(tracked, entries[index].Id);
                else WriteString(tracked, entries[index].Name);
            });

            _refreshers.Add(() =>
            {
                entries.Clear();
                entries.AddRange(supply());

                List<string> names = new List<string>(entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    names.Add(entries[i].Name);
                }

                picker.choices = names;
                picker.SetValueWithoutNotify(FindChosen(entries, tracked, byId));
            });

            return picker;
        }

        /// <summary>Finds the name of whichever entry the property currently holds.</summary>
        /// <param name="entries">Entries on offer.</param>
        /// <param name="property">Property being read.</param>
        /// <param name="byId">True when the property holds the number rather than the name.</param>
        /// <returns>The entry's name, or empty when the stored value matches none of them.</returns>
        private static string FindChosen(List<NamedId> entries, SerializedProperty property, bool byId)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                bool match = byId
                    ? entries[i].Id == property.intValue
                    : string.Equals(entries[i].Name, property.stringValue, StringComparison.Ordinal);

                if (match) return entries[i].Name;
            }

            return string.Empty;
        }

        #endregion

        #region Fetching A Component

        /// <summary>
        /// Adds the notice that a component reference does not point at one of this object's own, and
        /// the button that fetches the right one.
        /// </summary>
        /// <param name="block">Element receiving the notice.</param>
        /// <param name="property">Property holding the reference.</param>
        /// <param name="member">Member the reference belongs to.</param>
        private void AddRequiredGet(VisualElement block, SerializedProperty property, MemberInfo member)
        {
            RequiredGetAttribute fetch = GetAttribute<RequiredGetAttribute>(member);
            if (fetch == null || property == null) return;
            if (property.propertyType != SerializedPropertyType.ObjectReference) return;

            Type wanted = MemberType(member);
            if (wanted == null || !typeof(Component).IsAssignableFrom(wanted)) return;

            VisualElement notice = new VisualElement();
            notice.AddToClassList("ir-notice");
            block.Add(notice);

            Label alert = new Label();
            alert.AddToClassList("ir-alert");
            notice.Add(alert);

            Button get = new Button { text = "Get" };
            get.AddToClassList("ir-button");
            get.AddToClassList("ir-button--inline");
            notice.Add(get);

            SerializedProperty tracked = property.Copy();
            get.clickable = new Clickable(() => FetchComponent(tracked, wanted, fetch));

            _refreshers.Add(() => RefreshRequiredGet(notice, alert, get, tracked, wanted, fetch));
        }

        /// <summary>Shows the notice only while the reference points somewhere it should not.</summary>
        /// <param name="notice">Row holding the message and the button.</param>
        /// <param name="alert">Label carrying the message.</param>
        /// <param name="get">Button that fetches the component.</param>
        /// <param name="property">Property holding the reference.</param>
        /// <param name="wanted">Type of component wanted.</param>
        /// <param name="fetch">Attribute saying where it may be fetched from.</param>
        private void RefreshRequiredGet(
            VisualElement notice,
            Label alert,
            Button get,
            SerializedProperty property,
            Type wanted,
            RequiredGetAttribute fetch)
        {
            List<Component> expected = ExpectedComponents(wanted, fetch);
            Component current = property.objectReferenceValue as Component;
            bool satisfied = current != null && expected.Contains(current);

            notice.style.display = satisfied ? DisplayStyle.None : DisplayStyle.Flex;
            get.style.display = expected.Count == 0 ? DisplayStyle.None : DisplayStyle.Flex;

            string label = ObjectNames.NicifyVariableName(property.name);
            alert.text = expected.Count == 0
                ? label + " wants a " + wanted.Name + ", and this object has none to fetch."
                : label + " should be one of this object's own " + wanted.Name + " components.";
        }

        /// <summary>Collects the components this reference is allowed to point at.</summary>
        /// <param name="wanted">Type of component wanted.</param>
        /// <param name="fetch">Attribute saying where it may be fetched from.</param>
        /// <returns>Every component that would satisfy the reference, nearest first.</returns>
        private List<Component> ExpectedComponents(Type wanted, RequiredGetAttribute fetch)
        {
            List<Component> found = new List<Component>();

            Component root = target as Component;
            if (root == null) return found;

            Component self = root.GetComponent(wanted);
            if (fetch.IncludeSelf && self != null) found.Add(self);

            if (fetch.InChildren)
            {
                Component[] children = root.GetComponentsInChildren(wanted, true);
                for (int i = 0; i < children.Length; i++)
                {
                    if (children[i] != self) found.Add(children[i]);
                }
            }

            if (fetch.InParents)
            {
                Component[] parents = root.GetComponentsInParent(wanted, true);
                for (int i = 0; i < parents.Length; i++)
                {
                    if (parents[i] != self) found.Add(parents[i]);
                }
            }

            return found;
        }

        /// <summary>Points the reference at the nearest component that satisfies it.</summary>
        /// <param name="property">Property holding the reference.</param>
        /// <param name="wanted">Type of component wanted.</param>
        /// <param name="fetch">Attribute saying where it may be fetched from.</param>
        private void FetchComponent(SerializedProperty property, Type wanted, RequiredGetAttribute fetch)
        {
            List<Component> expected = ExpectedComponents(wanted, fetch);
            if (expected.Count == 0) return;

            WriteReference(property, expected[0]);
        }

        #endregion

        #region Action Arguments

        /// <summary>
        /// Builds the element an action button is drawn as: the button alone when the method takes
        /// nothing, and a small block of argument fields above it when it does.
        /// </summary>
        /// <param name="method">Method the button calls.</param>
        /// <param name="attribute">Attribute carrying the label, the accent and the height.</param>
        /// <returns>The element to add.</returns>
        private VisualElement BuildActionButton(MethodInfo method, ButtonAttribute attribute)
        {
            ParameterInfo[] parameters = method.GetParameters();

            // The arguments live for as long as the panel does, so a value typed into one of them is
            // still there the next time the button is pressed.
            object[] arguments = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                arguments[i] = parameters[i].HasDefaultValue
                    ? parameters[i].DefaultValue
                    : DefaultOf(parameters[i].ParameterType);
            }

            string fallback = string.IsNullOrEmpty(attribute.Label)
                ? ObjectNames.NicifyVariableName(method.Name)
                : attribute.Label;

            ValueSource<string> labelSource = ValueSource<string>.ForText(TargetType, attribute.Label);

            Button button = new Button(() => InvokeAction(method, arguments, attribute.Records))
            {
                text = string.IsNullOrEmpty(attribute.Label)
                    ? fallback
                    : labelSource.GetValue(target, fallback)
            };
            button.AddToClassList("ir-button");
            if (attribute.Accent) button.AddToClassList("ir-button--accent");
            if (attribute.Height > 0f) button.style.height = attribute.Height;

            if (!string.IsNullOrEmpty(attribute.Label))
            {
                Button labelled = button;
                _refreshers.Add(() => labelled.text = labelSource.GetValue(target, fallback));
            }

            if (parameters.Length == 0) return button;

            VisualElement box = new VisualElement();
            box.AddToClassList("ir-arguments");

            for (int i = 0; i < parameters.Length; i++)
            {
                int slot = i;

                VisualElement control = BuildValueControl(
                    parameters[slot].ParameterType,
                    ObjectNames.NicifyVariableName(parameters[slot].Name),
                    () => arguments[slot],
                    value => arguments[slot] = value);

                if (control != null) box.Add(control);
            }

            box.Add(button);
            return box;
        }

        /// <summary>Returns what an argument starts as when the method declares no default for it.</summary>
        /// <param name="type">Type of the argument.</param>
        /// <returns>A zeroed value, or null for anything that can be null.</returns>
        private static object DefaultOf(Type type)
        {
            return type != null && type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        #endregion

        #region Plain Value Controls

        /// <summary>
        /// Builds a control for a value that has no serialized property behind it, reading and writing
        /// it through a pair of callbacks. Used for a member shown by attribute alone and for the
        /// arguments an action collects, which are the two cases with nothing to bind to.
        /// </summary>
        /// <param name="type">Type the value holds.</param>
        /// <param name="label">Label the control shows.</param>
        /// <param name="read">Reads the current value.</param>
        /// <param name="write">Stores a new value.</param>
        /// <returns>The control, or null for a type with no control to offer.</returns>
        private VisualElement BuildValueControl(Type type, string label, Func<object> read, Action<object> write)
        {
            if (type == null) return null;

            if (type == typeof(bool)) return BindValue(WithTick(new Toggle(label)), read, write);
            if (type == typeof(int)) return BindValue(new IntegerField(label), read, write);
            if (type == typeof(long)) return BindValue(new LongField(label), read, write);
            if (type == typeof(float)) return BindValue(new FloatField(label), read, write);
            if (type == typeof(double)) return BindValue(new DoubleField(label), read, write);
            if (type == typeof(string)) return BindValue(new TextField(label), read, write);
            if (type == typeof(Color)) return BindValue(new ColorField(label), read, write);
            if (type == typeof(Vector2)) return BindValue(new Vector2Field(label), read, write);
            if (type == typeof(Vector3)) return BindValue(new Vector3Field(label), read, write);
            if (type == typeof(Vector4)) return BindValue(new Vector4Field(label), read, write);
            if (type == typeof(Rect)) return BindValue(new RectField(label), read, write);
            if (type == typeof(Bounds)) return BindValue(new BoundsField(label), read, write);

            if (type.IsEnum) return BindValue(new EnumField(label, (Enum)Enum.ToObject(type, 0)), read, write);

            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                ObjectField reference = new ObjectField(label)
                {
                    objectType = type,
                    allowSceneObjects = true,
                };
                return BindValue(reference, read, write);
            }

            return null;
        }

        /// <summary>Keeps one control and one pair of callbacks in step.</summary>
        /// <typeparam name="TValue">Type the control edits.</typeparam>
        /// <param name="control">Control being bound.</param>
        /// <param name="read">Reads the current value.</param>
        /// <param name="write">Stores a new value.</param>
        /// <returns>The control.</returns>
        private VisualElement BindValue<TValue>(BaseField<TValue> control, Func<object> read, Action<object> write)
        {
            control.RegisterValueChangedCallback(evt => write(evt.newValue));

            _refreshers.Add(() =>
            {
                object current = read();
                if (current is TValue held) control.SetValueWithoutNotify(held);
            });

            return control;
        }

        #endregion

        #region Writing

        /// <summary>Writes text back to its property.</summary>
        /// <param name="property">Property being written.</param>
        /// <param name="value">Value to store.</param>
        private void WriteString(SerializedProperty property, string value)
        {
            property.stringValue = value;
            property.serializedObject.ApplyModifiedProperties();
            OnSerializedObjectChanged();
        }

        /// <summary>Writes a reference back to its property.</summary>
        /// <param name="property">Property being written.</param>
        /// <param name="value">Value to store.</param>
        private void WriteReference(SerializedProperty property, UnityEngine.Object value)
        {
            property.objectReferenceValue = value;
            property.serializedObject.ApplyModifiedProperties();
            OnSerializedObjectChanged();
        }

        #endregion
    }
}
