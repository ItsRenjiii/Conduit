/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Safe reflection/introspection commands: checking that a type exists at all, reading constants
    /// and enum members, finding methods, and sketching JSON schema data.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Reflection Constants

        /// <summary>Usage line for the type discovery command.</summary>
        private const string TypeFindUsage =
            "Usage: type-find <nameFragment> [| members|constants|enum|all]";

        /// <summary>How many matching type names a scan lists before summarising the rest.</summary>
        private const int TypeFindMatchLimit = 100;

        #endregion

        #region Reflection Commands

        /// <summary>
        /// Finds a type's constructors and methods by optional name filter, without invoking any of them.
        /// <para>
        /// Constructors are listed under the runtime's own name for them, <c>.ctor</c>, so an ordinary
        /// name filter reaches them. They belong here because choosing the wrong overload is not a
        /// compile error: one may read state the caller has already changed, and one may be deprecated.
        /// </para>
        /// </summary>
        /// <param name="args">Type name plus optional member-name filter.</param>
        /// <returns>A compact JSON array of matching constructors and methods.</returns>
        private static string ReflectionMethodFindCommand(string args)
        {
            var tokens = SplitCommandTokens(args);
            if (tokens.Length == 0)
            {
                return "Usage: reflection-method-find <typeName> [methodName]";
            }

            var type = FindTypeLoose(tokens[0]);
            if (type == null)
            {
                return "Type not found: " + tokens[0];
            }

            var filter = tokens.Length > 1 ? tokens[1] : string.Empty;
            const BindingFlags lookup =
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            var builder = new StringBuilder();
            var count = 0;
            builder.Append('[');

            // Constructors come first because they are what a caller needs before anything else on the
            // type. A constructor has no return type, so it reports the type it produces, which keeps
            // one shape across both kinds rather than making the consumer branch. The name comes from
            // the member rather than a literal, so what is published is what the runtime calls it.
            foreach (var constructor in type.GetConstructors(lookup))
            {
                if (count >= DefaultJsonResultLimit) break;

                // A static constructor is the type initialiser. The runtime runs it, nothing can call it,
                // and it carries no arguments to choose between - so listing it only pads the answer to
                // the question this verb exists to answer.
                if (constructor.IsStatic) continue;
                if (!MatchesMemberFilter(constructor.Name, filter)) continue;

                AppendReflectedMember(
                    builder, count > 0, constructor.Name, constructor, TypeDisplayName(type),
                    false, constructor.IsPublic, constructor.GetParameters());
                count++;
            }

            foreach (var method in type.GetMethods(lookup))
            {
                if (count >= DefaultJsonResultLimit) break;
                if (!MatchesMemberFilter(method.Name, filter)) continue;

                AppendReflectedMember(
                    builder, count > 0, method.Name, method, TypeDisplayName(method.ReturnType),
                    method.IsStatic, method.IsPublic, method.GetParameters());
                count++;
            }

            builder.Append(']');
            return builder.ToString();
        }

        /// <summary>
        /// Produces a lightweight JSON schema approximation for public fields and properties.
        /// </summary>
        /// <param name="args">The type name to inspect.</param>
        /// <returns>A compact JSON schema-like object.</returns>
        private static string TypeGetJsonSchemaCommand(string args)
        {
            var type = FindTypeLoose(args);
            if (type == null)
            {
                return "Type not found: " + args;
            }

            var builder = new StringBuilder();
            builder.Append("{\"title\":").Append(JsonQuote(type.FullName))
                .Append(",\"type\":\"object\",\"properties\":{");
            var count = 0;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonQuote(field.Name)).Append(':').Append(JsonSchemaForType(field.FieldType));
                count++;
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonQuote(property.Name)).Append(':').Append(JsonSchemaForType(property.PropertyType));
                count++;
            }

            builder.Append("}}");
            return builder.ToString();
        }

        /// <summary>
        /// Answers whether a type exists in the loaded assemblies, and reads what one holds.
        /// <para>
        /// The bare call is the existence check that belongs before concluding an API is unavailable:
        /// it lists every loaded type whose name contains the fragment, with the assembly holding it.
        /// A section then reads one resolved type: constants and static field values, enum members
        /// with their numbers, the public instance surface, or all of those together.
        /// </para>
        /// </summary>
        /// <param name="args">Name fragment, then an optional section after a pipe.</param>
        /// <returns>A readable report, or an error naming what could not be resolved.</returns>
        private static string TypeFindCommand(string args)
        {
            var sections = SplitCommandSections(args);
            if (sections.Length == 0 || string.IsNullOrWhiteSpace(sections[0]))
            {
                return TypeFindUsage;
            }

            var fragment = sections[0].Trim().Trim('"');
            var section = sections.Length > 1 ? sections[1].Trim().ToLowerInvariant() : string.Empty;
            if (section.Length > 0 && section != "members" && section != "constants" && section != "enum" && section != "all")
            {
                return "Unknown section '" + section + "'. " + TypeFindUsage;
            }

            // The bare call answers "does it exist" and must answer it completely, so the exact-name
            // fast path is skipped on purpose: several loaded types can share a short name, and
            // resolving to the first would hide the rest - the exact miss this verb exists to prevent.
            if (section.Length == 0)
            {
                return ReportTypeMatches(fragment);
            }

            // A caller asking for a section already knows the type, so the cheap exact lookup goes
            // first and the full scan only runs when it misses.
            var type = FindTypeLoose(fragment);
            if (type == null)
            {
                int walked;
                int suppressed;
                var matches = ScanLoadedTypes(fragment, out walked, out suppressed);
                if (matches.Count == 0)
                {
                    return NoTypeMatchesMessage(fragment, walked);
                }

                if (matches.Count > 1 || suppressed > 0)
                {
                    return "'" + fragment + "' matches " + (matches.Count + suppressed)
                         + " types; name one fully to read its " + section + ":\n"
                         + FormatTypeMatchLines(matches, suppressed).TrimEnd();
                }

                type = matches[0];
            }

            var builder = new StringBuilder();
            builder.Append(TypeDisplayName(type)).Append("  (").Append(type.Assembly.GetName().Name).Append(')').AppendLine();

            if (section == "enum" || (section == "all" && type.IsEnum))
            {
                AppendEnumReport(builder, type);
            }

            // An enum's literals are its members with their numbers, already printed above, so the
            // all view does not repeat them as constants.
            if (section == "constants" || (section == "all" && !type.IsEnum))
            {
                AppendConstantsReport(builder, type);
            }

            if (section == "members" || section == "all")
            {
                AppendMembersReport(builder, type);
            }

            return builder.ToString().TrimEnd();
        }

        #endregion

        #region Reflection Helpers

        /// <summary>
        /// Tests a member name against the optional filter, which matches as a case-insensitive substring
        /// so both ".ctor" and "ctor" reach a constructor.
        /// </summary>
        /// <param name="name">Member name being considered.</param>
        /// <param name="filter">Filter text, or empty to accept every member.</param>
        /// <returns>True when the member should be listed.</returns>
        private static bool MatchesMemberFilter(string name, string filter)
        {
            return string.IsNullOrEmpty(filter)
                || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Appends one constructor or method to the JSON array, carrying whether it is deprecated and
        /// what its attribute says to use instead. A deprecated overload compiles with a warning the
        /// bridge caller never sees, so saying nothing about it reads as approval.
        /// </summary>
        /// <param name="builder">Array being built.</param>
        /// <param name="separate">Whether a comma is needed before this entry.</param>
        /// <param name="name">Name to publish the member under.</param>
        /// <param name="member">The reflected member, read for its declaring type and attributes.</param>
        /// <param name="producesType">What calling it yields: a method's return type, or the constructed type.</param>
        /// <param name="isStatic">Whether the member is static.</param>
        /// <param name="isPublic">Whether the member is public.</param>
        /// <param name="parameters">The member's parameters, in declaration order.</param>
        private static void AppendReflectedMember(
            StringBuilder builder, bool separate, string name, MemberInfo member, string producesType,
            bool isStatic, bool isPublic, ParameterInfo[] parameters)
        {
            if (separate)
            {
                builder.Append(',');
            }

            var obsolete = ReadObsoleteAttribute(member);
            builder.Append("{\"name\":").Append(JsonQuote(name))
                .Append(",\"declaringType\":")
                .Append(JsonQuote(member.DeclaringType == null ? string.Empty : member.DeclaringType.FullName))
                .Append(",\"isStatic\":").Append(isStatic ? "true" : "false")
                .Append(",\"isPublic\":").Append(isPublic ? "true" : "false")
                .Append(",\"isObsolete\":").Append(obsolete != null ? "true" : "false")
                .Append(",\"returnType\":").Append(JsonQuote(producesType));

            if (obsolete != null && !string.IsNullOrEmpty(obsolete.Message))
            {
                builder.Append(",\"obsoleteMessage\":").Append(JsonQuote(obsolete.Message));
            }

            builder.Append(",\"parameters\":[");
            for (var i = 0; i < parameters.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"name\":").Append(JsonQuote(parameters[i].Name))
                    .Append(",\"type\":").Append(JsonQuote(TypeDisplayName(parameters[i].ParameterType)))
                    .Append('}');
            }

            builder.Append("]}");
        }

        /// <summary>
        /// Reads a member's Obsolete attribute defensively, because materialising an attribute from a
        /// partially loadable assembly can throw, and one unreadable member must not lose the listing.
        /// </summary>
        /// <param name="member">The constructor or method to inspect.</param>
        /// <returns>The attribute, or null when the member is current or could not be read.</returns>
        private static ObsoleteAttribute ReadObsoleteAttribute(MemberInfo member)
        {
            try
            {
                return Attribute.GetCustomAttribute(member, typeof(ObsoleteAttribute), false) as ObsoleteAttribute;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Maps common CLR/Unity primitive-ish types to simple JSON schema fragments.
        /// </summary>
        /// <param name="type">The reflected member type.</param>
        /// <returns>A compact JSON schema fragment.</returns>
        private static string JsonSchemaForType(Type type)
        {
            if (type == typeof(string) || type == typeof(char))
            {
                return "{\"type\":\"string\"}";
            }

            if (type == typeof(bool))
            {
                return "{\"type\":\"boolean\"}";
            }

            if (type.IsPrimitive || type == typeof(decimal))
            {
                return "{\"type\":\"number\"}";
            }

            if (type.IsArray)
            {
                return "{\"type\":\"array\",\"items\":" + JsonSchemaForType(type.GetElementType()) + "}";
            }

            return "{\"type\":\"object\",\"clrType\":" + JsonQuote(type.FullName) + "}";
        }

        /// <summary>
        /// Answers the bare existence question: every loaded type whose name contains the fragment.
        /// </summary>
        /// <param name="fragment">Case-insensitive name fragment.</param>
        /// <returns>The match list, or a plain statement that nothing matches.</returns>
        private static string ReportTypeMatches(string fragment)
        {
            int walked;
            int suppressed;
            var matches = ScanLoadedTypes(fragment, out walked, out suppressed);
            if (matches.Count == 0)
            {
                return NoTypeMatchesMessage(fragment, walked);
            }

            var builder = new StringBuilder();
            builder.Append("Types matching '").Append(fragment).Append("': ").Append(matches.Count + suppressed)
                .Append("  (walked ").Append(walked).Append(" assemblies)").AppendLine();
            builder.Append(FormatTypeMatchLines(matches, suppressed));
            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// Walks every loaded assembly for types whose full name contains the fragment.
        /// </summary>
        /// <param name="fragment">Case-insensitive name fragment.</param>
        /// <param name="walked">Set to how many assemblies were walked.</param>
        /// <param name="suppressed">Set to how many matches were dropped beyond the listing cap.</param>
        /// <returns>Matching types in full-name order, capped at the listing limit.</returns>
        private static List<Type> ScanLoadedTypes(string fragment, out int walked, out int suppressed)
        {
            var matches = new List<Type>();
            walked = 0;
            suppressed = 0;

            // Every loaded assembly is walked on purpose: filtering by assembly name has already cost
            // a real answer (a UnityEngine type missed by a VRC-only filter), so the cap sits on the
            // result list instead.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                walked++;
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // A partially loadable assembly still surrenders the types that did load.
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null)
                    {
                        continue;
                    }

                    // The full name ends with the short name, so one containment test covers both
                    // spellings the caller might try.
                    var name = type.FullName ?? type.Name;
                    if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    matches.Add(type);
                }
            }

            matches.Sort((a, b) => string.CompareOrdinal(TypeDisplayName(a), TypeDisplayName(b)));
            if (matches.Count > TypeFindMatchLimit)
            {
                suppressed = matches.Count - TypeFindMatchLimit;
                matches.RemoveRange(TypeFindMatchLimit, suppressed);
            }

            return matches;
        }

        /// <summary>
        /// States plainly that the scan completed and found nothing, which is the opposite of failing.
        /// </summary>
        /// <param name="fragment">The fragment that matched nothing.</param>
        /// <param name="walked">How many assemblies the scan walked.</param>
        /// <returns>The no-match statement.</returns>
        private static string NoTypeMatchesMessage(string fragment, int walked)
        {
            return "No type matches '" + fragment + "' in any loaded assembly (walked " + walked + ").";
        }

        /// <summary>
        /// Renders match lines in the spelling the next call can consume, nested types with a plus.
        /// </summary>
        /// <param name="matches">Types to list.</param>
        /// <param name="suppressed">How many further matches the cap dropped.</param>
        /// <returns>One indented line per type, plus a suppression note when the cap bit.</returns>
        private static string FormatTypeMatchLines(List<Type> matches, int suppressed)
        {
            var builder = new StringBuilder();
            foreach (var match in matches)
            {
                builder.Append("  ").Append(TypeDisplayName(match))
                    .Append("  (").Append(match.Assembly.GetName().Name).Append(')').AppendLine();
            }

            if (suppressed > 0)
            {
                builder.Append("  ... and ").Append(suppressed).AppendLine(" more; narrow the fragment.");
            }

            return builder.ToString();
        }

        /// <summary>
        /// A type's full name in the form FindTypeLoose resolves, so output can feed the next call.
        /// </summary>
        /// <param name="type">The type to name.</param>
        /// <returns>The full name, or the short name for the rare type without one.</returns>
        private static string TypeDisplayName(Type type)
        {
            return type.FullName ?? type.Name;
        }

        /// <summary>
        /// Appends an enum's members with the number behind each one, and the underlying type.
        /// </summary>
        /// <param name="builder">Report being built.</param>
        /// <param name="type">The type the caller asked about.</param>
        private static void AppendEnumReport(StringBuilder builder, Type type)
        {
            if (!type.IsEnum)
            {
                builder.Append("  not an enum (").Append(type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class")
                    .AppendLine("); constants or members apply instead.");
                return;
            }

            var underlying = Enum.GetUnderlyingType(type);
            var names = Enum.GetNames(type);
            var values = Enum.GetValues(type);
            builder.Append("  enum : ").Append(underlying.Name).Append(", ").Append(names.Length).AppendLine(" members");
            for (var i = 0; i < names.Length; i++)
            {
                // UInt64 is the one underlying type whose values overflow a signed read.
                var raw = values.GetValue(i);
                var number = underlying == typeof(ulong)
                    ? Convert.ToUInt64(raw).ToString(CultureInfo.InvariantCulture)
                    : Convert.ToInt64(raw).ToString(CultureInfo.InvariantCulture);
                builder.Append("    ").Append(names[i]).Append(" = ").AppendLine(number);
            }
        }

        /// <summary>
        /// Appends every const and static field with its value, private ones included. A null static
        /// prints as null on purpose: emptiness is itself an answer, such as a hook the client only
        /// injects at runtime.
        /// </summary>
        /// <param name="builder">Report being built.</param>
        /// <param name="type">The resolved type.</param>
        private static void AppendConstantsReport(StringBuilder builder, Type type)
        {
            var fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (fields.Length == 0)
            {
                builder.AppendLine("  no const or static fields");
                return;
            }

            foreach (var field in fields)
            {
                builder.Append("  ").Append(field.IsLiteral ? "const  " : "static ")
                    .Append(field.Name).Append(" : ").Append(field.FieldType.Name)
                    .Append(" = ").AppendLine(ReadStaticFieldValue(field));
            }
        }

        /// <summary>
        /// Reads one static field defensively, so a throwing type initialiser costs that line alone.
        /// </summary>
        /// <param name="field">The const or static field to read.</param>
        /// <returns>The rendered value, or an unreadable marker naming the exception.</returns>
        private static string ReadStaticFieldValue(FieldInfo field)
        {
            try
            {
                var value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
                return DescribeFieldValue(value);
            }
            catch (Exception ex)
            {
                return "<unreadable: " + ex.GetType().Name + ">";
            }
        }

        /// <summary>
        /// Renders a field value for the report: null stays visible, strings stay quoted.
        /// </summary>
        /// <param name="value">The value read from the field.</param>
        /// <returns>Report text for the value.</returns>
        private static string DescribeFieldValue(object value)
        {
            if (value == null)
            {
                return "null";
            }

            var text = value as string;
            if (text != null)
            {
                return JsonQuote(text);
            }

            var formattable = value as IFormattable;
            return formattable != null ? formattable.ToString(null, CultureInfo.InvariantCulture) : value.ToString();
        }

        /// <summary>
        /// Appends the public instance surface: fields, properties, then methods, with accessor and
        /// operator methods folded away as special names.
        /// </summary>
        /// <param name="builder">Report being built.</param>
        /// <param name="type">The resolved type.</param>
        private static void AppendMembersReport(StringBuilder builder, Type type)
        {
            var wrote = false;
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                builder.Append("  field    ").Append(field.Name).Append(" : ").AppendLine(field.FieldType.Name);
                wrote = true;
            }

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                builder.Append("  property ").Append(property.Name).Append(" : ").Append(property.PropertyType.Name)
                    .Append("  (").Append(property.CanRead ? (property.CanWrite ? "get/set" : "get") : "set").AppendLine(")");
                wrote = true;
            }

            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            {
                if (method.IsSpecialName)
                {
                    continue;
                }

                builder.Append("  method   ").Append(method.Name).Append('(');
                var parameters = method.GetParameters();
                for (var i = 0; i < parameters.Length; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(parameters[i].ParameterType.Name);
                }

                builder.Append(") : ").AppendLine(method.ReturnType.Name);
                wrote = true;
            }

            if (!wrote)
            {
                builder.AppendLine("  no public instance members");
            }
        }

        #endregion
    }
}
