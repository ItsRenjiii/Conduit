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
using System.Reflection;
using UnityEngine;

namespace ItsRenji.Inspector.Editors
{
    /// <summary>
    /// A value an attribute was given, which may be the value itself or the name of something on the
    /// behaviour that produces it. A name is written with a leading dollar sign, so
    /// <c>[Note("Fixed text")]</c> says exactly that while <c>[Note("$Status")]</c> reads a field, a
    /// property or a parameterless method called Status each time the inspector refreshes.
    /// <para>
    /// This is what lets one attribute cover both the constant case and the case where the text, the
    /// bound or the condition is only known while looking at a particular object.
    /// </para>
    /// </summary>
    /// <typeparam name="T">Type the source produces.</typeparam>
    internal sealed class ValueSource<T>
    {
        #region Constants

        /// <summary>Marks an attribute argument as the name of a member rather than a literal value.</summary>
        public const char MEMBER_PREFIX = '$';

        /// <summary>Members searched for, in the order the reference implementation searches them.</summary>
        private const BindingFlags MEMBER_FLAGS =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        #endregion

        #region Private State

        /// <summary>Value returned when this source is a plain constant.</summary>
        private readonly T _constant;

        /// <summary>True when this source is a plain constant rather than a member lookup.</summary>
        private readonly bool _isConstant;

        /// <summary>Field the value is read from, or null.</summary>
        private readonly FieldInfo _field;

        /// <summary>Property the value is read from, or null.</summary>
        private readonly PropertyInfo _property;

        /// <summary>Method the value is read from, or null.</summary>
        private readonly MethodInfo _method;

        #endregion

        #region Construction

        /// <summary>Builds a source that always returns the same value.</summary>
        /// <param name="constant">Value to return.</param>
        private ValueSource(T constant)
        {
            _constant = constant;
            _isConstant = true;
        }

        /// <summary>Builds a source that reads one member.</summary>
        /// <param name="field">Field to read, or null.</param>
        /// <param name="property">Property to read, or null.</param>
        /// <param name="method">Method to call, or null.</param>
        /// <param name="error">Why nothing could be resolved, or null.</param>
        private ValueSource(FieldInfo field, PropertyInfo property, MethodInfo method, string error)
        {
            _field = field;
            _property = property;
            _method = method;
            Error = error;
        }

        /// <summary>Why the named member could not be resolved, or null when there is nothing wrong.</summary>
        public string Error { get; }

        #endregion

        #region Lookup

        /// <summary>
        /// Reads an argument that is text unless it names a member. This is the form every attribute
        /// taking a sentence uses, so ordinary text needs no ceremony.
        /// </summary>
        /// <param name="ownerType">Type the member would live on.</param>
        /// <param name="expression">Literal text, or a member name behind a dollar sign.</param>
        /// <returns>The source.</returns>
        public static ValueSource<T> ForText(Type ownerType, string expression)
        {
            if (expression == null || expression.Length == 0 || expression[0] != MEMBER_PREFIX)
            {
                return new ValueSource<T>((T)(object)expression);
            }

            return ForMember(ownerType, expression.Substring(1));
        }

        /// <summary>Reads an argument that always names a member, with or without the dollar sign.</summary>
        /// <param name="ownerType">Type the member lives on.</param>
        /// <param name="expression">Member name.</param>
        /// <returns>The source.</returns>
        public static ValueSource<T> ForMember(Type ownerType, string expression)
        {
            if (expression != null && expression.Length > 0 && expression[0] == MEMBER_PREFIX)
            {
                expression = expression.Substring(1);
            }

            if (ownerType == null || string.IsNullOrEmpty(expression))
            {
                return new ValueSource<T>(null, null, null, "No member was named.");
            }

            for (Type type = ownerType; type != null; type = type.BaseType)
            {
                FieldInfo field = type.GetField(expression, MEMBER_FLAGS | BindingFlags.DeclaredOnly);
                if (field != null && typeof(T).IsAssignableFrom(field.FieldType))
                {
                    return new ValueSource<T>(field, null, null, null);
                }

                PropertyInfo property = type.GetProperty(expression, MEMBER_FLAGS | BindingFlags.DeclaredOnly);
                if (property != null && property.CanRead && typeof(T).IsAssignableFrom(property.PropertyType))
                {
                    return new ValueSource<T>(null, property, null, null);
                }

                MethodInfo method = type.GetMethod(
                    expression,
                    MEMBER_FLAGS | BindingFlags.DeclaredOnly,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method != null && typeof(T).IsAssignableFrom(method.ReturnType))
                {
                    return new ValueSource<T>(null, null, method, null);
                }
            }

            return new ValueSource<T>(
                null,
                null,
                null,
                ownerType.Name + " has no field, property or parameterless method called " + expression
                + " returning " + typeof(T).Name + ".");
        }

        #endregion

        #region Reading

        /// <summary>Reads the current value.</summary>
        /// <param name="owner">Object the member is read from.</param>
        /// <param name="fallback">Value returned when the member cannot be read.</param>
        /// <returns>The value.</returns>
        public T GetValue(object owner, T fallback = default)
        {
            if (_isConstant) return _constant;
            if (Error != null) return fallback;

            try
            {
                if (_field != null) return (T)_field.GetValue(_field.IsStatic ? null : owner);
                if (_property != null) return (T)_property.GetValue(IsStatic(_property) ? null : owner);
                if (_method != null) return (T)_method.Invoke(_method.IsStatic ? null : owner, null);
            }
            catch (Exception exception)
            {
                // The member itself threw. Reporting it is more use than an inspector that goes blank,
                // and the fallback keeps the panel drawing.
                TargetInvocationException invocation = exception as TargetInvocationException;
                Debug.LogException(invocation != null && invocation.InnerException != null
                    ? invocation.InnerException
                    : exception);
            }

            return fallback;
        }

        /// <summary>Reports whether a property's accessor is static.</summary>
        /// <param name="property">Property being tested.</param>
        /// <returns>True when the property has no instance.</returns>
        private static bool IsStatic(PropertyInfo property)
        {
            MethodInfo getter = property.GetGetMethod(true);
            return getter != null && getter.IsStatic;
        }

        #endregion
    }

    /// <summary>
    /// A method an attribute named, resolved once and then called on demand. Used by the actions that
    /// belong to a single field rather than to the behaviour as a whole.
    /// </summary>
    internal sealed class ActionSource
    {
        /// <summary>Members searched for.</summary>
        private const BindingFlags MEMBER_FLAGS =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>Method being called.</summary>
        private readonly MethodInfo _method;

        /// <summary>Builds a source around one method.</summary>
        /// <param name="method">Method to call, or null.</param>
        /// <param name="error">Why nothing could be resolved, or null.</param>
        private ActionSource(MethodInfo method, string error)
        {
            _method = method;
            Error = error;
        }

        /// <summary>Why the named method could not be resolved, or null when there is nothing wrong.</summary>
        public string Error { get; }

        /// <summary>Name of the resolved method, or null.</summary>
        public string Name
        {
            get { return _method == null ? null : _method.Name; }
        }

        /// <summary>Finds a parameterless method by name, walking up the inheritance chain.</summary>
        /// <param name="ownerType">Type the method lives on.</param>
        /// <param name="methodName">Method name, with or without a leading dollar sign.</param>
        /// <returns>The source.</returns>
        public static ActionSource Resolve(Type ownerType, string methodName)
        {
            if (methodName != null && methodName.Length > 0 && methodName[0] == ValueSource<string>.MEMBER_PREFIX)
            {
                methodName = methodName.Substring(1);
            }

            if (ownerType == null || string.IsNullOrEmpty(methodName))
            {
                return new ActionSource(null, "No method was named.");
            }

            for (Type type = ownerType; type != null; type = type.BaseType)
            {
                MethodInfo method = type.GetMethod(
                    methodName,
                    MEMBER_FLAGS | BindingFlags.DeclaredOnly,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method != null) return new ActionSource(method, null);
            }

            return new ActionSource(null, ownerType.Name + " has no parameterless method called " + methodName + ".");
        }

        /// <summary>Calls the method on one object.</summary>
        /// <param name="owner">Object the method is called on.</param>
        public void Invoke(object owner)
        {
            if (_method == null) return;

            try
            {
                _method.Invoke(_method.IsStatic ? null : owner, null);
            }
            catch (Exception exception)
            {
                // An action that throws must not take the inspector down with it.
                TargetInvocationException invocation = exception as TargetInvocationException;
                Debug.LogException(invocation != null && invocation.InnerException != null
                    ? invocation.InnerException
                    : exception);
            }
        }
    }
}
