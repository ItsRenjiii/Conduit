/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// Builds the MCP tool catalogue from the bridge's own command signatures: a JSON Schema per verb, plus the
// builder that turns a structured arguments object back into a bridge command line.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ItsRenji.Conduit
{
    /// <summary>Generates the tool catalogue and the command builders that go with it.</summary>
    internal static class ConduitToolCatalog
    {
        #region Signature grammar

        /// <summary>Argument names the generator types as numbers rather than strings.</summary>
        private static readonly Regex _numericName = new Regex(
            @"^(x|y|z|count|index|width|height|seconds|speed|yaw|pitch|size|factor|forward|strafe|right|up|turnDegPerSec|degrees|yawDegrees|pitchDegrees|deltaYawDegrees|deltaPitchDegrees|maxDepth)$",
            RegexOptions.IgnoreCase);

        /// <summary>Leading pipe on a bracketed argument, marking a pipe-appended slot.</summary>
        private static readonly Regex _pipePrefix = new Regex(@"^\s*\|\s*");

        /// <summary>Any whitespace run, normalised between arguments.</summary>
        private static readonly Regex _whitespace = new Regex(@"\s+");

        /// <summary>Characters that cannot appear in a generated argument name.</summary>
        private static readonly Regex _nonArgumentName = new Regex(@"[^A-Za-z0-9_]+");

        /// <summary>Characters stripped to form the legacy alias of an argument name.</summary>
        private static readonly Regex _nonLegacyName = new Regex(@"[^A-Za-z0-9_]");

        /// <summary>
        /// Verbs whose handler takes everything after the first pipe verbatim to end of input. These two
        /// cannot be derived from their signatures - they are shaped exactly like any ordinary two-section
        /// verb, and only the handler source separates them.
        /// </summary>
        private static readonly HashSet<string> _opaqueTailVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "create-usharp-batch",
            "script-update-or-create",
        };

        /// <summary>
        /// Verbs whose handler reads a leading-pipe payload raw and takes only a closed option section off its
        /// end. The trailing group makes the signature read like any sectioned verb, so, as with the verbs
        /// above, only the handler source separates them.
        /// </summary>
        private static readonly HashSet<string> _rawPayloadVerbs = new HashSet<string>(StringComparer.Ordinal)
        {
            "udon-batch-edit",
        };

        /// <summary>Modifier order the editor-window-key builder emits, so equivalent calls build equal text.</summary>
        private static readonly string[] _canonicalModifiers = { "shift", "ctrl", "alt", "cmd" };

        #endregion

        #region Parsed signature

        /// <summary>One parsed signature: its schema and the command rebuilder.</summary>
        private sealed class ParsedSignature
        {
            /// <summary>Generated JSON Schema properties.</summary>
            public JObject Properties;

            /// <summary>Names that must be supplied.</summary>
            public JArray Required;

            /// <summary>Rebuilds the bridge command line from an arguments object.</summary>
            public Func<JObject, string> Build;
        }

        /// <summary>One node of a signature: a literal run or an argument placeholder.</summary>
        private sealed class SignatureNode
        {
            /// <summary>True when this node is an argument rather than literal text.</summary>
            public bool Placeholder;

            /// <summary>Literal text, when not a placeholder.</summary>
            public string Text;

            /// <summary>Argument name.</summary>
            public string Name;

            /// <summary>Accepted alternative spellings.</summary>
            public List<string> Aliases;

            /// <summary>True when the argument may be omitted.</summary>
            public bool Optional;

            /// <summary>The pipe separator prefix when this argument occupies its own section.</summary>
            public string LiteralPrefix;

            /// <summary>
            /// The value emitted when this optional must hold its slot open but the caller omitted it,
            /// declared in the signature as <c>[name=value]</c>. Empty when the signature declares none,
            /// which leaves a whitespace-delimited optional with no empty form at all.
            /// </summary>
            public string DefaultText;
        }

        #endregion

        #region Argument helpers

        /// <summary>Reads an argument by name or alias.</summary>
        /// <param name="arguments">Supplied arguments.</param>
        /// <param name="name">Primary name.</param>
        /// <param name="aliases">Alternative names.</param>
        /// <returns>The token, or null when absent.</returns>
        internal static JToken ReadValue(JObject arguments, string name, List<string> aliases)
        {
            JToken value;
            if (arguments.TryGetValue(name, out value)) return value;
            foreach (var alias in aliases)
            {
                if (arguments.TryGetValue(alias, out value)) return value;
            }

            return null;
        }

        /// <summary>Mirrors the JavaScript truthiness test: absent, null and empty string are omitted.</summary>
        /// <param name="value">Token to test.</param>
        /// <returns>True when the value should be emitted.</returns>
        internal static bool IsWanted(JToken value)
        {
            if (value == null || value.Type == JTokenType.Null) return false;
            if (value.Type != JTokenType.String) return true;
            return ((string)value).Length > 0;
        }

        /// <summary>Renders a token the way JavaScript's String() would, under invariant culture.</summary>
        /// <param name="value">Token to render.</param>
        /// <returns>Its string form.</returns>
        internal static string JavaScriptString(JToken value)
        {
            if (value.Type == JTokenType.String) return (string)value;
            if (value.Type == JTokenType.Boolean) return (bool)value ? "true" : "false";
            if (value.Type == JTokenType.Integer || value.Type == JTokenType.Float)
            {
                return ((JValue)value).ToString(CultureInfo.InvariantCulture);
            }

            return value.ToString(Formatting.None);
        }

        /// <summary>Quotes a whitespace-delimited token argument, exactly as the handler expects.</summary>
        /// <param name="text">Raw value.</param>
        /// <returns>The quoted or unchanged value.</returns>
        internal static string QuoteCommandArgument(string text)
        {
            var needsQuote = false;
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c) || c == '"' || c == ',') { needsQuote = true; break; }
            }

            if (!needsQuote) return text;
            return "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// Generates the argument name for a signature fragment. Internal rather than private so the
        /// catalogue gate can derive names with THIS sanitiser instead of a copy that would drift from it.
        /// </summary>
        /// <param name="raw">Fragment text.</param>
        /// <param name="fallback">Name used when nothing usable remains.</param>
        /// <returns>The argument name.</returns>
        internal static string SanitizeArgumentName(string raw, string fallback)
        {
            var name = _nonArgumentName.Replace((raw ?? string.Empty).Trim(), "_").Trim('_');
            return name.Length > 0 ? name : fallback;
        }

        /// <summary>The legacy alias spelling of a fragment.</summary>
        /// <param name="raw">Fragment text.</param>
        /// <returns>The alias.</returns>
        private static string LegacyArgumentName(string raw)
        {
            return _nonLegacyName.Replace((raw ?? string.Empty).Trim(), string.Empty);
        }

        /// <summary>Reads an optional value, returning null when it was not supplied.</summary>
        /// <param name="arguments">Supplied arguments.</param>
        /// <param name="name">Argument name.</param>
        /// <returns>The value as text, or null.</returns>
        private static string Optional(JObject arguments, string name)
        {
            var value = ReadValue(arguments, name, new List<string>());
            return IsWanted(value) ? JavaScriptString(value) : null;
        }

        /// <summary>Reads a required value, throwing the generator's own wording when absent.</summary>
        /// <param name="arguments">Supplied arguments.</param>
        /// <param name="name">Argument name.</param>
        /// <returns>The value as text.</returns>
        private static string Required(JObject arguments, string name)
        {
            var value = ReadValue(arguments, name, new List<string>());
            if (!IsWanted(value)) throw new InvalidOperationException("missing required argument: " + name);
            return JavaScriptString(value);
        }

        #endregion

        #region Parse-mode classification

        /// <summary>Splits a signature into its top-level literal text and the inner text of each group.</summary>
        /// <param name="argSpec">Argument specification, without a leading space.</param>
        /// <param name="groups">Receives each group's inner text, in order.</param>
        /// <returns>The literal text lying outside every group.</returns>
        private static string ScanSignature(string argSpec, List<string> groups)
        {
            var literal = new StringBuilder();
            var index = 0;
            while (index < argSpec.Length)
            {
                var character = argSpec[index];
                if (character == '<' || character == '[')
                {
                    var close = character == '<' ? '>' : ']';
                    var end = argSpec.IndexOf(close, index);
                    if (end < 0) end = argSpec.Length;
                    groups.Add(argSpec.Substring(index + 1, Math.Max(0, end - index - 1)));
                    index = end + 1;
                    continue;
                }

                literal.Append(character);
                index++;
            }

            return literal.ToString();
        }

        /// <summary>
        /// Classifies how a verb's handler parses its arguments. Four of the five modes fall out of the
        /// signature itself: a pipe outside the groups, or a group that opens with one, means the handler
        /// reads pipe sections, and a lone leading-pipe group means the payload is raw from that pipe. A raw
        /// payload followed by an option section reads as sectioned, so those verbs are named instead.
        ///
        /// The test that matters is "a pipe in the top-level literal OR a group matching the pipe prefix".
        /// Stripping group interiors to look for separators misclassifies 14 verbs whose separator lives
        /// inside an optional group, written as a bracketed leading pipe.
        /// </summary>
        /// <param name="verb">Bridge verb.</param>
        /// <param name="argSpec">Argument specification, without a leading space.</param>
        /// <returns>The parse-mode name.</returns>
        private static string ClassifyParseMode(string verb, string argSpec)
        {
            if (argSpec.Length == 0) return "no-arguments";

            var groups = new List<string>();
            var literal = ScanSignature(argSpec, groups);
            var sectioned = literal.IndexOf('|') >= 0;
            foreach (var group in groups)
            {
                if (_pipePrefix.IsMatch(group)) { sectioned = true; break; }
            }

            if (!sectioned) return "quoted-whitespace-tokens";
            if (_rawPayloadVerbs.Contains(verb)
                || (groups.Count == 1 && literal.Trim().Length == 0 && _pipePrefix.IsMatch(groups[0])))
            {
                return "raw-leading-pipe-payload";
            }

            return _opaqueTailVerbs.Contains(verb) ? "first-pipe-opaque-tail" : "escaped-pipe-sections";
        }

        #endregion

        #region Signature parsing

        /// <summary>Parses one signature into its schema and rebuilder.</summary>
        /// <param name="verb">Bridge verb.</param>
        /// <param name="argSpec">Argument specification, including its leading space.</param>
        /// <param name="encodeSections">
        /// True for a verb the classifier files under escaped pipe sections. The value is rendered exactly as
        /// the legacy builder renders it first, and the escape layer is applied on top of that. The order is
        /// load-bearing: escaping instead of quoting would change ordinary whitespace and quote semantics,
        /// while escaping on top of the legacy rendering changes nothing for a value carrying neither a
        /// backslash nor a pipe, because the codec is a no-op there. Nothing decodes the layer: no handler
        /// calls ConduitSectionCodec.Decode and the shared splitter cuts at every pipe, so a backslash in
        /// such a value reaches its handler doubled and an escaped pipe still splits its section. Node-wise
        /// encoding is equivalent to encoding a whole section, because the separators between nodes of one
        /// section are plain spaces.
        /// </param>
        /// <returns>The parsed signature.</returns>
        private static ParsedSignature ParseSignature(string verb, string argSpec, bool encodeSections)
        {
            var properties = new JObject();
            var required = new JArray();
            var nodes = new List<SignatureNode>();
            var index = 0;
            var enumCount = 0;

            while (index < argSpec.Length)
            {
                var character = argSpec[index];
                if (character == '<' || character == '[')
                {
                    var close = character == '<' ? '>' : ']';
                    var optional = character == '[';
                    var end = argSpec.IndexOf(close, index);
                    if (end < 0) end = argSpec.Length;
                    var inner = argSpec.Substring(index + 1, Math.Max(0, end - index - 1));
                    index = end + 1;

                    var literalPrefix = string.Empty;
                    var prefix = _pipePrefix.Match(inner);
                    if (prefix.Success)
                    {
                        literalPrefix = " | ";
                        inner = inner.Substring(prefix.Length);
                    }

                    // An optional that declares a DEFAULT, as "[maxDepth=12]", gains an empty form: the
                    // default. A whitespace-delimited optional otherwise has none, so the builder cannot
                    // hold its slot open and refuses the whole call as soon as a later argument is
                    // supplied - a refusal the caller can do nothing about. Only a plain placeholder takes
                    // one, because an alternation already publishes its branches.
                    var defaultText = string.Empty;
                    if (optional && inner.IndexOf('|') < 0)
                    {
                        var equals = inner.IndexOf('=');
                        if (equals > 0)
                        {
                            defaultText = inner.Substring(equals + 1).Trim();
                            inner = inner.Substring(0, equals).Trim();
                        }
                    }

                    string name;
                    string[] enumValues = null;
                    string hint = null;
                    if (inner.IndexOf('|') >= 0)
                    {
                        var raw = inner.Split('|');
                        var branchList = new List<string>();
                        foreach (var branch in raw)
                        {
                            var trimmed = branch.Trim();
                            if (trimmed.Length > 0) branchList.Add(trimmed);
                        }

                        // An alternation may DECLARE its own name, as in "[| section:members|constants|all]",
                        // and that name is what gets published. Without one the fallbacks below have to invent
                        // a name, and an invented name appears nowhere in the signature a caller is reading, so
                        // the caller cannot derive the property and guesses it instead. Every published property
                        // must be readable off the published signature; this is what makes that possible.
                        string declaredName = null;
                        if (branchList.Count > 0)
                        {
                            var colon = branchList[0].IndexOf(':');
                            if (colon > 0)
                            {
                                declaredName = branchList[0].Substring(0, colon).Trim();
                                branchList[0] = branchList[0].Substring(colon + 1).Trim();
                            }
                        }

                        var branches = branchList.ToArray();
                        var sentinel = false;
                        var placeholder = false;
                        foreach (var branch in branches)
                        {
                            if (string.Equals(branch, "null", StringComparison.OrdinalIgnoreCase)) sentinel = true;
                            if (branch.IndexOf('<') >= 0) placeholder = true;
                        }

                        // Kept as the fallback for a group that declares nothing, so an unmigrated signature
                        // still parses rather than failing the whole catalogue.
                        Func<string> invented = () =>
                        {
                            var generated = enumCount == 0
                                ? "option"
                                : "option" + (enumCount + 1).ToString(CultureInfo.InvariantCulture);
                            enumCount++;
                            return generated;
                        };

                        if (branches.Length > 1 && !sentinel && !placeholder)
                        {
                            enumValues = branches;
                            name = declaredName != null
                                ? SanitizeArgumentName(declaredName, "option")
                                : invented();
                        }
                        else if (sentinel)
                        {
                            string payload = null;
                            foreach (var branch in branches)
                            {
                                if (!string.Equals(branch, "null", StringComparison.OrdinalIgnoreCase)) { payload = branch; break; }
                            }

                            if (payload == null) payload = branches[0];
                            name = declaredName != null
                                ? SanitizeArgumentName(declaredName, "option")
                                : SanitizeArgumentName(payload, "arg" + (properties.Count + 1).ToString(CultureInfo.InvariantCulture));
                            hint = string.Join(", ", branches) + " (send the payload, or null)";
                        }
                        else
                        {
                            name = declaredName != null ? SanitizeArgumentName(declaredName, "option") : invented();
                            hint = "one of: " + string.Join(", ", branches);
                        }
                    }
                    else
                    {
                        name = SanitizeArgumentName(inner, "arg" + (properties.Count + 1).ToString(CultureInfo.InvariantCulture));
                    }

                    var aliases = new List<string>();
                    var legacyName = LegacyArgumentName(inner);
                    if (legacyName.Length > 0 && legacyName != name) aliases.Add(legacyName);

                    if (!properties.ContainsKey(name))
                    {
                        JObject property;
                        if (enumValues != null)
                        {
                            property = new JObject
                            {
                                ["type"] = "string",
                                ["enum"] = new JArray(enumValues),
                                ["description"] = "one of: " + string.Join(", ", enumValues),
                            };
                        }
                        else
                        {
                            var description = hint != null
                                ? (optional ? "(optional) " + hint : hint)
                                : (optional ? "(optional) " + name : name);
                            if (defaultText.Length > 0) description += ", default " + defaultText;
                            property = new JObject
                            {
                                ["type"] = _numericName.IsMatch(name) ? "number" : "string",
                                ["description"] = description,
                            };
                        }

                        properties[name] = property;
                    }

                    var alreadyRequired = false;
                    foreach (var token in required)
                    {
                        if ((string)token == name) { alreadyRequired = true; break; }
                    }

                    if (!optional && !alreadyRequired) required.Add(name);

                    nodes.Add(new SignatureNode
                    {
                        Placeholder = true,
                        Name = name,
                        Aliases = aliases,
                        Optional = optional,
                        LiteralPrefix = literalPrefix,
                        DefaultText = defaultText,
                    });
                }
                else
                {
                    var end = index;
                    while (end < argSpec.Length && argSpec[end] != '<' && argSpec[end] != '[') end++;
                    nodes.Add(new SignatureNode { Placeholder = false, Text = argSpec.Substring(index, end - index) });
                    index = end;
                }
            }

            Func<JObject, string> build = arguments =>
            {
                var wanted = new bool[nodes.Count];
                for (var i = 0; i < nodes.Count; i++)
                {
                    wanted[i] = nodes[i].Placeholder && IsWanted(ReadValue(arguments, nodes[i].Name, nodes[i].Aliases));
                }

                // An omitted optional argument only needs its slot held open when something later is supplied.
                var slotNeeded = new bool[nodes.Count];
                var laterWanted = false;
                for (var i = nodes.Count - 1; i >= 0; i--)
                {
                    slotNeeded[i] = laterWanted;
                    if (wanted[i]) laterWanted = true;
                }

                var output = new StringBuilder(verb);
                var separator = string.Empty;
                for (var i = 0; i < nodes.Count; i++)
                {
                    var node = nodes[i];
                    if (!node.Placeholder) { separator += node.Text; continue; }

                    var value = ReadValue(arguments, node.Name, node.Aliases);
                    if (!IsWanted(value))
                    {
                        if (!node.Optional) throw new InvalidOperationException("missing required argument: " + node.Name);
                        if (!slotNeeded[i]) { separator = string.Empty; continue; }
                        if (node.LiteralPrefix.Length == 0)
                        {
                            // A declared default IS this slot's empty form. Without one the command cannot
                            // be built at all, because a whitespace-delimited argument has no empty token
                            // and dropping it slides every later argument one place forward.
                            if (string.IsNullOrEmpty(node.DefaultText))
                            {
                                throw new InvalidOperationException(
                                    "cannot omit " + node.Name + " while a later argument is supplied: " + verb +
                                    " reads its arguments positionally and " + node.Name + " declares no default - supply it too");
                            }

                            output.Append(separator).Append(node.DefaultText);
                            separator = string.Empty;
                            continue;
                        }

                        output.Append(separator).Append(node.LiteralPrefix);
                        separator = string.Empty;
                        continue;
                    }

                    var separatorText = _whitespace.Replace(separator + node.LiteralPrefix, " ");
                    var pipeDelimited = node.LiteralPrefix.Length > 0 || separatorText.IndexOf('|') >= 0;
                    var text = JavaScriptString(value);
                    var legacyText = pipeDelimited ? text : QuoteCommandArgument(text);
                    var emitted = encodeSections ? ConduitSectionCodec.EncodeSection(legacyText) : legacyText;
                    output.Append(separatorText).Append(emitted);
                    separator = string.Empty;
                }

                return output.ToString();
            };

            return new ParsedSignature { Properties = properties, Required = required, Build = build };
        }

        #endregion

        #region Catalogue construction

        /// <summary>The catalogue this server publishes. The migrated policy is the only one that ships.</summary>
        /// <param name="commands">The bridge's command list as JSON.</param>
        /// <param name="builders">Receives each tool's command builder.</param>
        /// <returns>The tools array.</returns>
        internal static JArray BuildMigratedCatalog(JArray commands, Dictionary<string, Func<JObject, string>> builders)
        {
            var tools = BuildToolCatalog(commands, builders);

            // Repairs mutate a schema in place, so every tool owns its own before any of them run: a shared
            // instance would let one tool's repair silently rewrite another's schema.
            foreach (var tool in tools)
            {
                tool["inputSchema"] = ((JObject)tool["inputSchema"]).DeepClone();
            }

            RepairEntry(tools, builders);
            RepairNumericTypes(tools);
            RepairListenerComponent(tools, builders);
            RepairUdonExposure(tools, builders);
            RepairEditorWindowKey(tools, builders);
            RepairBatchEdits(tools, builders);
            RepairProjectSearch(builders);
            return tools;
        }

        /// <summary>Builds the tool list, one tool per bridge verb under its canonical spelling.</summary>
        /// <param name="commands">The bridge's command list as JSON.</param>
        /// <param name="builders">Receives each tool's command builder.</param>
        /// <returns>The tools array.</returns>
        private static JArray BuildToolCatalog(JArray commands, Dictionary<string, Func<JObject, string>> builders)
        {
            var list = new JArray();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            Action<string, ParsedSignature, string> addTool = (name, signature, description) =>
            {
                if (string.IsNullOrEmpty(name) || seen.Contains(name)) return;
                seen.Add(name);
                list.Add(new JObject
                {
                    ["name"] = name,
                    ["description"] = description,
                    ["inputSchema"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = signature.Properties,
                        ["required"] = signature.Required,
                        ["additionalProperties"] = false,
                    },
                });
                builders[name] = signature.Build;
            };

            foreach (var entry in commands)
            {
                var command = entry as JObject;
                if (command == null) continue;
                var verb = (string)command["verb"];
                if (string.IsNullOrEmpty(verb)) continue;

                var args = (string)command["args"] ?? string.Empty;
                var note = (string)command["note"] ?? string.Empty;
                var argSpec = args.Length > 0 ? " " + args : string.Empty;
                var encodeSections = ClassifyParseMode(verb, args.Trim()) == "escaped-pipe-sections";

                ParsedSignature signature;
                try { signature = ParseSignature(verb, argSpec, encodeSections); }
                catch { signature = ParseSignature(verb, string.Empty, encodeSections); }

                var baseText = "Unity editor bridge command \"" + verb + "\". " +
                    (args.Length > 0 ? "Signature: " + verb + " " + args : "Takes no arguments.");
                var description = note.Length > 0 ? baseText + " NOTE: " + note : baseText;

                addTool(verb, signature, description);
            }

            var runDescription =
                "Run ANY raw Conduit bridge command line, exactly as unity-tool-list and help print them " +
                "(e.g. \"ping\", \"script-read Assets/Foo.cs 1 40\", \"udon-batch-edit | [ ... ]\"). " +
                "Intended for verbs added after this session connected - a new verb is live in Unity the moment it compiles, " +
                "but this client only gains its typed tool on reconnect - and for hand-composed calls. " +
                "Quote whitespace-bearing TOKEN arguments as the signature requires; everything after a top-level \" | \" is taken verbatim.";

            var runSignature = new ParsedSignature
            {
                Properties = new JObject
                {
                    ["command"] = new JObject
                    {
                        ["type"] = "string",
                        ["description"] = "Full bridge command line, starting with the verb.",
                    },
                },
                Required = new JArray { "command" },
                Build = arguments =>
                {
                    var command = arguments["command"];
                    if (command == null || command.Type == JTokenType.Null || ((string)command ?? string.Empty).Trim().Length == 0)
                    {
                        throw new InvalidOperationException("missing required argument: command");
                    }

                    return (string)command;
                },
            };

            addTool("conduit-run", runSignature, runDescription);
            return list;
        }

        #endregion

        #region Migrated repairs

        /// <summary>True when a catalogue tool name is the verb.</summary>
        /// <param name="toolName">Catalogue tool name.</param>
        /// <param name="verb">Bridge verb.</param>
        /// <returns>True when the names match.</returns>
        private static bool IsToolFor(string toolName, string verb)
        {
            return string.Equals(toolName, verb, StringComparison.Ordinal);
        }

        /// <summary>Finds every tool for a verb and hands each one its schema.</summary>
        /// <param name="tools">Catalogue.</param>
        /// <param name="verb">Bridge verb.</param>
        /// <param name="patch">Applied to each matching tool's input schema.</param>
        private static void PatchSchema(JArray tools, string verb, Action<JObject> patch)
        {
            foreach (var tool in tools)
            {
                if (!IsToolFor((string)tool["name"], verb)) continue;
                patch((JObject)tool["inputSchema"]);
            }
        }

        /// <summary>Registers a builder under the verb.</summary>
        /// <param name="builders">Builder map.</param>
        /// <param name="verb">Bridge verb.</param>
        /// <param name="build">Replacement builder.</param>
        private static void PatchBuilder(Dictionary<string, Func<JObject, string>> builders, string verb, Func<JObject, string> build)
        {
            builders[verb] = build;
        }

        /// <summary>
        /// The required and optional placeholders of "entry" both sanitize to one name, so the generated
        /// schema carried a single scalar and the builder emitted its value twice. One integer array replaces
        /// it in place, under the name the signature already gives it: renaming it here instead would leave
        /// the published signature and the published schema naming different arguments.
        /// </summary>
        /// <param name="tools">Catalogue.</param>
        /// <param name="builders">Builder map.</param>
        private static void RepairEntry(JArray tools, Dictionary<string, Func<JObject, string>> builders)
        {
            PatchSchema(tools, "entry", schema =>
            {
                var properties = (JObject)schema["properties"];
                properties["entries"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject { ["type"] = "integer" },
                    ["minItems"] = 1,
                    ["description"] = "console entry numbers to read in full",
                };
                schema["required"] = new JArray { "entries" };
            });

            PatchBuilder(builders, "entry", arguments =>
            {
                var array = ReadValue(arguments, "entries", new List<string>()) as JArray;
                if (array == null || array.Count == 0)
                {
                    throw new InvalidOperationException("missing required argument: entries");
                }

                var parts = new string[array.Count];
                for (var i = 0; i < array.Count; i++) parts[i] = JavaScriptString(array[i]);
                return "entry " + string.Join(" ", parts);
            });
        }

        /// <summary>
        /// The batch-edit verbs take a JSON document, and declaring it as a plain string left every client
        /// with no idea what was inside it: the whole payload rendered as one escaped blob, newlines and all,
        /// so the caller could not read back what they had just sent. Declaring the real array instead means
        /// the schema describes the shape, each edit renders as its own object, and a malformed payload is
        /// rejected by the client against the schema rather than by the bridge after the fact.
        ///
        /// The command text the bridge receives is unchanged: the builder serialises the array straight back
        /// to the JSON the verb already parses, so this is a description of the contract rather than a change
        /// to it. The report option arrives from the signature as a closed enum already; the repair only says
        /// when to choose summary, and the builder appends it as the section the handler reads off the end.
        /// </summary>
        /// <param name="tools">Catalogue.</param>
        /// <param name="builders">Builder map.</param>
        private static void RepairBatchEdits(JArray tools, Dictionary<string, Func<JObject, string>> builders)
        {
            PatchSchema(tools, "udon-batch-edit", schema =>
            {
                // The signature names this argument, so it arrives as a string and is replaced in place by
                // the shape it really carries. Renaming it here instead would leave the published signature
                // and the published schema naming different arguments.
                var properties = (JObject)schema["properties"];
                var editArray = new JObject
                {
                    ["type"] = "array",
                    ["minItems"] = 1,
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JObject
                        {
                            ["file_path"] = new JObject
                            {
                                ["type"] = "string",
                                ["minLength"] = 1,
                                ["description"] = "Asset path of the file to edit, read this session and unchanged since.",
                            },
                            ["old_string"] = new JObject
                            {
                                ["type"] = "string",
                                ["minLength"] = 1,
                                ["description"] = "Exact text to find, matched byte for byte apart from line-ending flavour.",
                            },
                            ["new_string"] = new JObject
                            {
                                ["type"] = "string",
                                ["description"] = "Replacement text. Empty deletes the matched text.",
                            },
                            ["replace_all"] = new JObject
                            {
                                ["type"] = "boolean",
                                ["description"] = "Replace every occurrence instead of requiring exactly one.",
                            },
                        },
                        ["required"] = new JArray { "file_path", "old_string", "new_string" },
                        ["additionalProperties"] = false,
                    },
                };

                // A string names a project .json file holding the array, for a batch larger than the harness lets one
                // tool call carry; the handler reads it and validates it exactly as it would the array.
                properties["edits"] = new JObject
                {
                    ["description"] = "SEND THIS ARRAY ITSELF, EACH ITEM EXACTLY THE FIELDS OF AN EDIT CALL. A JSON PARSE ERROR MEANS YOUR ESCAPING IS WRONG: FIX IT AND RESEND THE SAME BATCH HERE. NEVER WORK AROUND THIS TOOL WITH A SCRIPT OR A SHELL EDIT. Exact-string replacements applied atomically across any number of files; this tool has no size limit. A string instead is the project path of a .json file holding the array, for a batch larger than your harness lets one tool call carry, so it still goes through in ONE call instead of several batches: WRITE THAT FILE BY HAND WITH YOUR OWN FILE TOOL, NEVER GENERATE IT WITH A SCRIPT.",
                    ["anyOf"] = new JArray { editArray, new JObject { ["type"] = "string", ["minLength"] = 1 } },
                };

                schema["required"] = new JArray { "edits" };

                // Generated from the signature as an optional closed enum; only its description is replaced,
                // because when to choose summary is the one decision a caller has to make.
                if (properties["report"] is JObject report)
                {
                    report["description"] =
                        "(optional) full, the default, echoes every edit as a line-numbered diff; summary returns one line per edit with the lines it landed on. Use summary for long or whole-file batches. A refusal reads the same either way.";
                }
            });

            PatchBuilder(builders, "udon-batch-edit", arguments =>
            {
                var edits = ReadValue(arguments, "edits", new List<string>());
                if (edits == null) throw new InvalidOperationException("missing required argument: edits");

                // The report closes the command, after the payload, and is left out when omitted so a call
                // without it builds exactly the text it always has.
                var report = Optional(arguments, "report");
                if (report != null && report != "full" && report != "summary")
                {
                    throw new InvalidOperationException("report must be full or summary");
                }

                var section = report == null ? string.Empty : " | " + report;
                if (edits.Type == JTokenType.String)
                {
                    return "udon-batch-edit | " + (string)edits + section;
                }

                var array = edits as JArray;
                if (array == null || array.Count == 0)
                {
                    throw new InvalidOperationException("edits must be a non-empty array of {file_path, old_string, new_string} objects, or a project .json file path");
                }

                return "udon-batch-edit | " + array.ToString(Formatting.None) + section;
            });
        }

        /// <summary>
        /// Retypes values whose handlers accept a narrower numeric shape than the signature-name heuristic emits.
        /// The command text is unchanged; only the declared type moves.
        /// </summary>
        /// <param name="tools">Catalogue.</param>
        private static void RepairNumericTypes(JArray tools)
        {
            PatchSchema(tools, "script-read", s =>
            {
                var properties = (JObject)s["properties"];
                properties["startLine"]["type"] = "integer";
                properties["count"]["type"] = "integer";
            });
            PatchSchema(tools, "gameobject-duplicate-multi", s => ((JObject)s["properties"])["amount"]["type"] = "integer");
            PatchSchema(tools, "profiler-record-start", s => ((JObject)s["properties"])["frames"]["type"] = "integer");
            PatchSchema(tools, "profiler-frame-hierarchy", s =>
            {
                var properties = (JObject)s["properties"];
                properties["topN"]["type"] = "integer";
                properties["threadIndex"]["type"] = "integer";
                properties["frame"]["type"] = "integer";
            });
            PatchSchema(tools, "profiler-frames", s => ((JObject)s["properties"])["count"]["type"] = "integer");
            PatchSchema(tools, "profiler-compare", s => ((JObject)s["properties"])["blockFrames"]["type"] = "integer");
            PatchSchema(tools, "profiler-markers", s => ((JObject)s["properties"])["topN"]["type"] = "integer");
            PatchSchema(tools, "profiler-memory-objects", s => ((JObject)s["properties"])["topN"]["type"] = "integer");
            PatchSchema(tools, "frame-debugger-capture", s => ((JObject)s["properties"])["details"]["type"] = "integer");
            PatchSchema(tools, "profiler-wait-settled", s => ((JObject)s["properties"])["maxSeconds"]["type"] = "integer");
            PatchSchema(tools, "profiler-isolate", s =>
            {
                var properties = (JObject)s["properties"];
                properties["cycles"]["type"] = "integer";
                properties["phaseFrames"]["type"] = "integer";
            });
            PatchSchema(tools, "profiler-select-frame", s => ((JObject)s["properties"])["frame"]["type"] = "integer");
            PatchSchema(tools, "animation-clip-sample", s => ((JObject)s["properties"])["timeSeconds"]["type"] = "number");

            // "size" is a scalar everywhere else, so the name-based default publishes it as a number. This
            // one is three numbers in a string, and a caller building from the schema would send a single
            // value and be refused. Retyped rather than renamed, so the name still derives from the
            // signature.
#if CONDUIT_PROBUILDER
            PatchSchema(tools, "probuilder-fit-to-size", s => ((JObject)s["properties"])["size"]["type"] = "string");
#endif
        }

        /// <summary>
        /// The listener selector's signature declares it as a payload with a null sentinel, so the name
        /// already derives; what the generated schema cannot express is that JSON null is accepted, since
        /// a sentinel publishes as a plain string. This RETYPES it to allow null and maps that null to the
        /// spelling the handler reads, without renaming anything.
        /// </summary>
        /// <param name="tools">Catalogue.</param>
        /// <param name="builders">Builder map.</param>
        private static void RepairListenerComponent(JArray tools, Dictionary<string, Func<JObject, string>> builders)
        {
            var generated = builders["unity-event-set-string"];

            PatchSchema(tools, "unity-event-set-string", schema =>
            {
                // Assigned in place, which keeps the property in its signature position: the sections are
                // positional, so a reordered schema would read as a different command to anyone building
                // from the property order rather than from the names.
                ((JObject)schema["properties"])["listenerComponent"] = new JObject
                {
                    ["anyOf"] = new JArray
                    {
                        new JObject { ["type"] = "string", ["minLength"] = 1 },
                        new JObject { ["type"] = "null" },
                    },
                    ["description"] = "component selector on the listener target, or null for no selector",
                };
            });

            PatchBuilder(builders, "unity-event-set-string", arguments =>
            {
                var supplied = arguments["listenerComponent"];
                if (supplied == null)
                {
                    throw new InvalidOperationException("missing required argument: listenerComponent");
                }

                if (supplied.Type != JTokenType.Null && JavaScriptString(supplied).Length == 0)
                {
                    throw new InvalidOperationException(
                        "listenerComponent must name a component or be null; an empty selector is ambiguous");
                }

                if (supplied.Type != JTokenType.Null) return generated(arguments);

                // The handler reads null, none and '-' identically. "none" is chosen as canonical because an
                // empty section is whitespace on the wire and reads as an accident rather than intent. The
                // generated builder would otherwise treat a JSON null as an omitted required argument.
                var rewritten = (JObject)arguments.DeepClone();
                rewritten["listenerComponent"] = "none";
                return generated(rewritten);
            });
        }

        /// <summary>
        /// The enumerating sections take no fragment, but the schema demanded one, and their argument was
        /// typed as free-form text. The fragment becomes optional and the section becomes a typed mode plus
        /// its path, with path valid if and only if the mode is dump or diff.
        /// </summary>
        /// <param name="tools">Catalogue.</param>
        /// <param name="builders">Builder map.</param>
        private static void RepairUdonExposure(JArray tools, Dictionary<string, Func<JObject, string>> builders)
        {
            PatchSchema(tools, "udon-exposure", schema =>
            {
                var properties = (JObject)schema["properties"];
                var fragment = (JObject)properties["nameFragment"];
                fragment["minLength"] = 1;
                fragment["description"] = "(optional) name fragment to search for; required unless a mode is given";

                properties["mode"] = new JObject
                {
                    ["type"] = "string",
                    ["enum"] = new JArray { "types", "dump", "diff" },
                    ["description"] = "(optional) enumerate the whole registry instead of searching it",
                };
                properties["path"] = new JObject
                {
                    ["type"] = "string",
                    ["minLength"] = 1,
                    ["description"] = "(optional) baseline file; required when mode is dump or diff",
                };

                schema["required"] = new JArray();
                schema["anyOf"] = new JArray
                {
                    new JObject { ["required"] = new JArray { "nameFragment" } },
                    new JObject { ["required"] = new JArray { "mode" } },
                };
                schema["allOf"] = new JArray
                {
                    new JObject
                    {
                        ["if"] = new JObject
                        {
                            ["properties"] = new JObject { ["mode"] = new JObject { ["enum"] = new JArray { "dump", "diff" } } },
                            ["required"] = new JArray { "mode" },
                        },
                        ["then"] = new JObject { ["required"] = new JArray { "path" } },
                    },
                    new JObject
                    {
                        ["if"] = new JObject { ["required"] = new JArray { "path" } },
                        ["then"] = new JObject
                        {
                            ["required"] = new JArray { "mode" },
                            ["properties"] = new JObject { ["mode"] = new JObject { ["enum"] = new JArray { "dump", "diff" } } },
                        },
                    },
                };
            });

            PatchBuilder(builders, "udon-exposure", arguments =>
            {
                var fragment = Optional(arguments, "nameFragment") ?? string.Empty;
                var mode = Optional(arguments, "mode");
                var path = Optional(arguments, "path");

                if (fragment.Length == 0 && mode == null)
                {
                    throw new InvalidOperationException(
                        "udon-exposure needs a name fragment unless mode is types, dump or diff");
                }

                string section = null;
                if (string.Equals(mode, "types", StringComparison.Ordinal))
                {
                    if (path != null) throw new InvalidOperationException("udon-exposure mode types takes no path");
                    section = "types";
                }
                else if (string.Equals(mode, "dump", StringComparison.Ordinal) || string.Equals(mode, "diff", StringComparison.Ordinal))
                {
                    if (path == null) throw new InvalidOperationException("udon-exposure mode " + mode + " needs a path");

                    // The handler reads this section as exactly two tokens, so a path carrying a space is
                    // quoted before the section is escaped. Both layers unwind in order on the way in.
                    section = mode + " " + QuoteCommandArgument(path);
                }
                else if (mode != null)
                {
                    throw new InvalidOperationException("unknown udon-exposure mode: " + mode);
                }

                var text = "udon-exposure";
                if (fragment.Length > 0)
                {
                    text += " " + ConduitSectionCodec.EncodeSection(QuoteCommandArgument(fragment));
                }

                if (section != null) text += " | " + ConduitSectionCodec.EncodeSection(section);
                return text;
            });
        }

        /// <summary>
        /// The trailing ellipsis in the modifier signature was captured as an enum VALUE, producing the
        /// literal option "cmd ...". Modifiers become a real set, emitted in a fixed order so two equivalent
        /// calls build identical text.
        /// </summary>
        /// <param name="tools">Catalogue.</param>
        /// <param name="builders">Builder map.</param>
        private static void RepairEditorWindowKey(JArray tools, Dictionary<string, Func<JObject, string>> builders)
        {
            PatchSchema(tools, "editor-window-key", schema =>
            {
                var properties = (JObject)schema["properties"];
                properties["modifiers"] = new JObject
                {
                    ["type"] = "array",
                    ["items"] = new JObject
                    {
                        ["type"] = "string",
                        ["enum"] = new JArray(_canonicalModifiers),
                    },
                    ["uniqueItems"] = true,
                    ["description"] = "(optional) modifier keys, emitted in the order shift ctrl alt cmd",
                };
            });

            PatchBuilder(builders, "editor-window-key", arguments =>
            {
                var window = Required(arguments, "window");
                var key = Required(arguments, "key");

                var supplied = new HashSet<string>(StringComparer.Ordinal);
                var modifiers = ReadValue(arguments, "modifiers", new List<string>()) as JArray;
                if (modifiers != null)
                {
                    foreach (var modifier in modifiers)
                    {
                        var name = JavaScriptString(modifier);
                        if (Array.IndexOf(_canonicalModifiers, name) < 0)
                        {
                            throw new InvalidOperationException("unknown modifier: " + name);
                        }

                        supplied.Add(name);
                    }
                }

                var text = "editor-window-key "
                    + ConduitSectionCodec.EncodeSection(QuoteCommandArgument(window))
                    + " | " + ConduitSectionCodec.EncodeSection(key);
                foreach (var modifier in _canonicalModifiers)
                {
                    if (supplied.Contains(modifier)) text += " " + modifier;
                }

                return text;
            });
        }

        /// <summary>
        /// project-search takes everything before its last pipe as one pattern and never tokenises it, and a
        /// regex needs its backslashes and pipes exactly as written. The generated rendering quotes the value
        /// as a whitespace token and then section-escapes it, and the handler reverses neither layer, so the
        /// pattern goes out verbatim inside the one pair of quotes the handler strips. The options always
        /// follow as their own section with no raw pipe left in them, which keeps the last pipe the separator.
        /// </summary>
        /// <param name="builders">Builder map.</param>
        private static void RepairProjectSearch(Dictionary<string, Func<JObject, string>> builders)
        {
            PatchBuilder(builders, "project-search", arguments =>
            {
                var pattern = Required(arguments, "pattern");
                var options = (Optional(arguments, "optionsJson") ?? string.Empty).Trim();
                if (options.Length == 0) options = "{}";

                JToken parsed;
                try
                {
                    parsed = JToken.Parse(options);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("optionsJson is not valid JSON: " + ex.Message);
                }

                if (parsed.Type != JTokenType.Object)
                {
                    throw new InvalidOperationException("optionsJson must be a JSON object, such as {\"regex\":true}");
                }

                // Valid JSON holds a pipe only inside a string, where its JSON escape is the same character.
                return "project-search \"" + pattern + "\" | " + options.Replace("|", "\\u007c");
            });
        }

        #endregion
    }
}
