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
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Shared helpers used by the Unity-MCP-compatible Conduit commands for compact JSON output,
    /// Unity object resolution, and light argument parsing.
    /// </summary>
    internal static partial class ConduitBridge
    {
        #region Limits
        /// <summary>
        /// Keeps JSON arrays from flooding the command response while still returning enough context
        /// for MCP-side inspection and follow-up calls.
        /// </summary>
        private const int DefaultJsonResultLimit = 100;

        /// <summary>
        /// How many array elements a before/after evidence token spells out before summarising the rest, so a
        /// twelve-slot registry stays readable in a modify report without truncating the count itself.
        /// </summary>
        private const int ArrayDescriptionElementLimit = 8;

        #endregion

        #region Command tokenising

        /// <summary>
        /// Converts loose command text into pipe-delimited sections; this is the bridge convention
        /// used when a tool needs one free-form value after a target.
        /// </summary>
        /// <param name="args">The raw command text after the verb.</param>
        /// <returns>Trimmed sections, preserving empty middle values when supplied.</returns>
        private static string[] SplitCommandSections(string args)
        {
            return SplitPipeArgs(args);
        }

        /// <summary>
        /// Parses whitespace and comma separated tokens from a single command section, preserving quoted
        /// values so asset paths and object names can contain spaces.
        /// </summary>
        /// <param name="section">The section to split into tokens.</param>
        /// <returns>Non-empty argument tokens.</returns>
        private static string[] SplitCommandTokens(string section)
        {
            return SplitCommandTokens(section, true);
        }

        /// <summary>
        /// Parses tokens from command text, preserving quoted values so asset paths and object names can
        /// contain spaces. Commas separate tokens inside a pipe section, but must NOT separate them on the
        /// top-level command line, where an argument may legitimately contain one.
        /// </summary>
        /// <param name="section">The section to split into tokens.</param>
        /// <param name="splitOnCommas">True to treat a comma as a separator alongside whitespace.</param>
        /// <returns>Non-empty argument tokens.</returns>
        private static string[] SplitCommandTokens(string section, bool splitOnCommas)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                return new string[0];
            }

            var tokens = new List<string>();
            var token = new StringBuilder();
            var inQuotes = false;
            var escaping = false;

            foreach (var ch in section)
            {
                if (escaping)
                {
                    token.Append(ch);
                    escaping = false;
                    continue;
                }

                if (inQuotes && ch == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (ch == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && (char.IsWhiteSpace(ch) || (splitOnCommas && ch == ',')))
                {
                    if (token.Length > 0)
                    {
                        tokens.Add(token.ToString());
                        token.Length = 0;
                    }

                    continue;
                }

                token.Append(ch);
            }

            if (escaping)
            {
                token.Append('\\');
            }

            if (token.Length > 0)
            {
                tokens.Add(token.ToString());
            }

            return tokens.ToArray();
        }

        /// <summary>
        /// Reports whether a token is a plain positive integer, used to tell an optional trailing size
        /// argument apart from a path segment when a section is parsed from the right.
        /// </summary>
        /// <param name="token">The token to test.</param>
        /// <returns>True when the token parses as an integer greater than zero.</returns>
        private static bool IsPositiveIntegerToken(string token)
        {
            int value;
            return int.TryParse(token, out value) && value > 0;
        }

        /// <summary>
        /// Reports whether a tree argument asks for the name-only rendering, so a depth and a mode can share
        /// the same optional slot in either order.
        /// </summary>
        /// <param name="token">The argument token to test.</param>
        /// <returns>True when the token selects names-only output.</returns>
        private static bool IsNameTreeToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var value = token.Trim().Trim('"').ToLowerInvariant();
            return value == "names" || value == "name" || value == "compact";
        }

        #endregion

        #region UdonSharp interop

        /// <summary>Authored fields found per UdonSharp proxy type, so a command reflects over each type once.</summary>
        private static readonly Dictionary<Type, List<FieldInfo>> _udonSharpAuthoredFields = new Dictionary<Type, List<FieldInfo>>();

        /// <summary>
        /// Reads the authored field values of a UdonSharp proxy behaviour, so a command can tell afterwards
        /// which of them it changed. Take it immediately before the write and hand it to
        /// <see cref="SyncUdonSharpProxy"/> after.
        /// </summary>
        /// <param name="component">Object about to be written, which may be a UdonSharp proxy behaviour.</param>
        /// <returns>Each authored field's value by name, or null outside play mode and for anything that is not a UdonSharp proxy.</returns>
        private static Dictionary<string, object> CaptureUdonSharpAuthoredValues(UnityEngine.Object component)
        {
            if (component == null || !UnityEngine.Application.isPlaying || !IsUdonSharpProxyType(component.GetType()))
            {
                return null;
            }

            var values = new Dictionary<string, object>();
            foreach (var field in UdonSharpAuthoredFields(component.GetType()))
            {
                values[field.Name] = CopyUdonSharpValue(field.GetValue(component));
            }

            return values;
        }

        /// <summary>
        /// Pushes the authored fields a command changed on a UdonSharp proxy behaviour into its backing
        /// UdonBehaviour, so a program which is ALREADY RUNNING sees the change. A no-op outside play mode.
        /// </summary>
        /// <remarks>
        /// Only the fields that differ from <paramref name="before"/> are written, each through
        /// SetProgramVariable and converted to the type the running program declares for it, so the program
        /// keeps its private state and any other field it has changed itself. UdonSharp's CopyProxyToUdon is
        /// not used: in play mode it also writes the proxy's private fields, and a proxy never runs, so it
        /// would reset every counter, cache and state the program holds. Nothing is pushed while authoring:
        /// the C# proxy owns the authored data and UdonSharp carries it to the program at build, and on a
        /// prefab instance a push would land as an override on every variable it touched. Resolved through
        /// reflection so Conduit still compiles and runs in projects that do not have UdonSharp installed; a
        /// no-op for every non-UdonSharp object.
        /// </remarks>
        /// <param name="component">Component that may be a UdonSharp proxy behaviour; ignored otherwise.</param>
        /// <param name="before">Authored values captured just before the write, or null when nothing can be pushed.</param>
        private static void SyncUdonSharpProxy(UnityEngine.Object component, Dictionary<string, object> before)
        {
            if (component == null || before == null || !UnityEngine.Application.isPlaying)
            {
                return;
            }

            try
            {
                var backing = GetUdonSharpBackingBehaviour(component);
                if (backing == null)
                {
                    return;
                }

                var backingType = backing.GetType();
                var declared = backingType.GetMethod("GetProgramVariableType", new[] { typeof(string) });
                var write = backingType.GetMethod("SetProgramVariable", new[] { typeof(string), typeof(object) });
                if (declared == null || write == null)
                {
                    return;
                }

                foreach (var field in UdonSharpAuthoredFields(component.GetType()))
                {
                    var value = field.GetValue(component);
                    object previous;
                    before.TryGetValue(field.Name, out previous);
                    if (UdonSharpValuesMatch(value, previous))
                    {
                        continue;
                    }

                    try
                    {
                        // A field the program declares no variable for has nothing to receive the value.
                        var programType = declared.Invoke(backing, new object[] { field.Name }) as Type;
                        if (programType != null)
                        {
                            write.Invoke(backing, new[] { field.Name, ToUdonProgramValue(value, programType) });
                        }
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning("[Conduit] The running program did not take '" + field.Name + "': " +
                            exception.GetBaseException().Message);
                    }
                }
            }
            catch (Exception)
            {
                // UdonSharp absent or its editor API changed - the proxy write already stands.
            }
        }

        /// <summary>Whether a type is a UdonSharp proxy behaviour, found without a compile-time dependency on the package.</summary>
        /// <param name="type">Type to test.</param>
        /// <returns>True when UdonSharp.UdonSharpBehaviour is among its bases.</returns>
        private static bool IsUdonSharpProxyType(Type type)
        {
            for (var current = type; current != null; current = current.BaseType)
            {
                if (current.FullName == "UdonSharp.UdonSharpBehaviour")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The fields a UdonSharp proxy type serializes, which are the ones its program declares as variables:
        /// public or marked SerializeField or SerializeReference, never NonSerialized or readonly, from the type
        /// itself up to but not including UdonSharpBehaviour, whose own fields are UdonSharp's bookkeeping.
        /// </summary>
        /// <param name="type">Proxy behaviour type.</param>
        /// <returns>The authored fields, found once per type.</returns>
        private static List<FieldInfo> UdonSharpAuthoredFields(Type type)
        {
            List<FieldInfo> fields;
            if (_udonSharpAuthoredFields.TryGetValue(type, out fields))
            {
                return fields;
            }

            fields = new List<FieldInfo>();
            for (var current = type; current != null && current.FullName != "UdonSharp.UdonSharpBehaviour"; current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.IsNotSerialized || field.IsInitOnly)
                    {
                        continue;
                    }

                    if (field.IsPublic || field.IsDefined(typeof(SerializeField), true) || field.IsDefined(typeof(SerializeReference), true))
                    {
                        fields.Add(field);
                    }
                }
            }

            _udonSharpAuthoredFields[type] = fields;
            return fields;
        }

        /// <summary>Finds the UdonBehaviour a UdonSharp proxy runs as.</summary>
        /// <param name="proxy">Proxy behaviour.</param>
        /// <returns>The backing UdonBehaviour, or null when there is none or UdonSharp is absent.</returns>
        private static UnityEngine.Object GetUdonSharpBackingBehaviour(UnityEngine.Object proxy)
        {
            var utilityType = FindLoadedType("UdonSharpEditor.UdonSharpEditorUtility");
            if (utilityType == null)
            {
                return null;
            }

            foreach (var method in utilityType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "GetBackingUdonBehaviour")
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 1 && parameters[0].ParameterType.IsInstanceOfType(proxy))
                {
                    return method.Invoke(null, new object[] { proxy }) as UnityEngine.Object;
                }
            }

            return null;
        }

        /// <summary>
        /// Converts a proxy's value to the form its program holds: a UdonSharp behaviour becomes the
        /// UdonBehaviour it runs as, an enum the program keeps as a number becomes that number, an array is
        /// rebuilt with the program's element type, and a curve is copied so the two never share one.
        /// </summary>
        /// <param name="value">Value as the proxy holds it.</param>
        /// <param name="programType">Type the program declares for it.</param>
        /// <returns>The value the program can hold.</returns>
        private static object ToUdonProgramValue(object value, Type programType)
        {
            if (value == null)
            {
                return null;
            }

            // A destroyed object compares equal to null without being a null reference.
            var unityObject = value as UnityEngine.Object;
            if (!ReferenceEquals(unityObject, null) && unityObject == null)
            {
                return null;
            }

            var array = value as Array;
            if (array != null)
            {
                var element = programType.IsArray ? programType.GetElementType() : typeof(object);
                var converted = Array.CreateInstance(element, array.Length);
                for (var i = 0; i < array.Length; i++)
                {
                    converted.SetValue(ToUdonProgramValue(array.GetValue(i), element), i);
                }

                return converted;
            }

            if (unityObject != null && IsUdonSharpProxyType(unityObject.GetType()))
            {
                return GetUdonSharpBackingBehaviour(unityObject);
            }

            if (value is AnimationCurve)
            {
                return CopyUdonSharpValue(value);
            }

            if (value.GetType().IsEnum && programType.IsPrimitive)
            {
                return Convert.ChangeType(value, programType, CultureInfo.InvariantCulture);
            }

            return value;
        }

        /// <summary>Whether two authored values are the same, comparing arrays and curves by content.</summary>
        /// <param name="left">One value.</param>
        /// <param name="right">The other value.</param>
        /// <returns>True when they match.</returns>
        private static bool UdonSharpValuesMatch(object left, object right)
        {
            var leftCurve = left as AnimationCurve;
            var rightCurve = right as AnimationCurve;
            if (leftCurve != null && rightCurve != null)
            {
                return UdonSharpCurvesMatch(leftCurve, rightCurve);
            }

            var leftArray = left as Array;
            var rightArray = right as Array;
            if (leftArray == null && rightArray == null)
            {
                return object.Equals(left, right);
            }

            if (leftArray == null || rightArray == null || leftArray.Length != rightArray.Length)
            {
                return false;
            }

            for (var i = 0; i < leftArray.Length; i++)
            {
                if (!object.Equals(leftArray.GetValue(i), rightArray.GetValue(i)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether two curves have the same keys and wrap modes.</summary>
        /// <param name="left">One curve.</param>
        /// <param name="right">The other curve.</param>
        /// <returns>True when they match.</returns>
        private static bool UdonSharpCurvesMatch(AnimationCurve left, AnimationCurve right)
        {
            if (left.preWrapMode != right.preWrapMode || left.postWrapMode != right.postWrapMode)
            {
                return false;
            }

            var leftKeys = left.keys;
            var rightKeys = right.keys;
            if (leftKeys.Length != rightKeys.Length)
            {
                return false;
            }

            for (var i = 0; i < leftKeys.Length; i++)
            {
                if (!leftKeys[i].Equals(rightKeys[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Copies an authored value so a later write cannot change the copy underneath it.</summary>
        /// <param name="value">Value as the proxy holds it.</param>
        /// <returns>A shallow copy of an array, a new curve with the same keys, or the value itself.</returns>
        private static object CopyUdonSharpValue(object value)
        {
            var array = value as Array;
            if (array != null)
            {
                return array.Clone();
            }

            var curve = value as AnimationCurve;
            if (curve == null)
            {
                return value;
            }

            var copy = new AnimationCurve(curve.keys);
            copy.preWrapMode = curve.preWrapMode;
            copy.postWrapMode = curve.postWrapMode;
            return copy;
        }

        /// <summary>
        /// Adds a component through UdonSharp's setup API when the requested type is an UdonSharp behaviour.
        /// </summary>
        /// <remarks>
        /// A raw <see cref="GameObject.AddComponent(Type)"/> call creates only the visible UdonSharp proxy.
        /// It does not create or link the backing UdonBehaviour, and later asset refreshes cannot discover a
        /// proxy-only component because UdonSharp's scene refresh begins from existing UdonBehaviours. The
        /// reflection path keeps Conduit optional in projects without UdonSharp while preserving the package's
        /// required setup operation where it is installed.
        /// </remarks>
        /// <param name="gameObject">GameObject that should own the new component.</param>
        /// <param name="componentType">Concrete component type to add.</param>
        /// <returns>The newly added component with its Udon backing prepared when applicable.</returns>
        private static Component AddComponentWithUdonSharpSetup(GameObject gameObject, Type componentType)
        {
            var isUdonSharpBehaviour = false;
            for (var type = componentType; type != null; type = type.BaseType)
            {
                if (type.FullName == "UdonSharp.UdonSharpBehaviour")
                {
                    isUdonSharpBehaviour = true;
                    break;
                }
            }

            if (!isUdonSharpBehaviour)
            {
                return gameObject.AddComponent(componentType);
            }

            var extensionsType = FindLoadedType("UdonSharpEditor.UdonSharpComponentExtensions");
            if (extensionsType == null)
            {
                throw new InvalidOperationException("UdonSharp is installed, but its component setup API is unavailable.");
            }

            var addMethod = extensionsType.GetMethod(
                "AddUdonSharpComponent",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(GameObject), typeof(Type) },
                null);
            if (addMethod == null)
            {
                throw new MissingMethodException("UdonSharp component setup API signature was not found.");
            }

            return (Component)addMethod.Invoke(null, new object[] { gameObject, componentType });
        }

        #endregion

        #region JSON writers

        /// <summary>
        /// Wraps a flat <c>{"field": value}</c> payload in the serialization root key that
        /// <see cref="EditorJsonUtility.FromJsonOverwrite"/> requires (for example
        /// <c>{"MonoBehaviour": {...}}</c>), leaving already-wrapped payloads untouched.
        /// </summary>
        /// <remarks>
        /// EditorJsonUtility round-trips a Unity object under a single root key matching its serialized
        /// class. A flat field object handed straight to FromJsonOverwrite matches nothing and is silently
        /// ignored, which is why component edits appeared to do nothing. The key is read back from the live
        /// object's own ToJson so this stays correct for every Unity object type, not just MonoBehaviour.
        /// </remarks>
        /// <param name="target">Object whose serialization root key should wrap the payload.</param>
        /// <param name="json">Caller-supplied JSON, flat or already wrapped.</param>
        /// <returns>JSON ready for <see cref="EditorJsonUtility.FromJsonOverwrite"/>.</returns>
        private static string WrapJsonForOverwrite(UnityEngine.Object target, string json)
        {
            var payload = (json ?? string.Empty).Trim();
            if (target == null || payload.Length == 0)
            {
                return payload;
            }

            var reference = EditorJsonUtility.ToJson(target, false);
            var firstQuote = reference.IndexOf('"');
            var secondQuote = firstQuote >= 0 ? reference.IndexOf('"', firstQuote + 1) : -1;
            if (firstQuote < 0 || secondQuote <= firstQuote)
            {
                return payload;
            }

            var rootKey = reference.Substring(firstQuote + 1, secondQuote - firstQuote - 1);

            // Leave an already-wrapped payload (and the rare field literally named after the root key) alone.
            if (payload.StartsWith("{\"" + rootKey + "\"", StringComparison.Ordinal))
            {
                return payload;
            }

            return "{\"" + rootKey + "\":" + payload + "}";
        }

        /// <summary>
        /// Quotes a string for JSON responses without depending on package-level JSON libraries.
        /// </summary>
        /// <param name="value">The string value to encode, or null.</param>
        /// <returns>A JSON string literal or the JSON null token.</returns>
        private static string JsonQuote(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            foreach (var c in value)
            {
                if (c == '\\') builder.Append("\\\\");
                else if (c == '"') builder.Append("\\\"");
                else if (c == '\n') builder.Append("\\n");
                else if (c == '\r') builder.Append("\\r");
                else if (c == '\t') builder.Append("\\t");
                else if (char.IsControl(c)) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else builder.Append(c);
            }

            builder.Append('"');
            return builder.ToString();
        }

        /// <summary>
        /// Formats a float using invariant culture so JSON numeric output survives non-English editors.
        /// </summary>
        /// <param name="value">The value to render.</param>
        /// <returns>A compact invariant numeric token.</returns>
        private static string JsonNumber(float value)
        {
            return value.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Formats a double using invariant culture so timing/profiler values are machine readable.
        /// </summary>
        /// <param name="value">The value to render.</param>
        /// <returns>A compact invariant numeric token.</returns>
        private static string JsonNumber(double value)
        {
            return value.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Encodes a vector as a small JSON object because Unity vectors are common command payloads.
        /// </summary>
        /// <param name="value">The vector to encode.</param>
        /// <returns>A JSON object with x, y, and z numeric properties.</returns>
        private static string JsonVector3(Vector3 value)
        {
            return "{\"x\":" + JsonNumber(value.x) +
                ",\"y\":" + JsonNumber(value.y) +
                ",\"z\":" + JsonNumber(value.z) + "}";
        }

        /// <summary>
        /// Encodes bounds as center and size objects so screenshot and selection responses can be reused.
        /// </summary>
        /// <param name="bounds">The bounds to encode.</param>
        /// <returns>A JSON object with center and size vector properties.</returns>
        private static string JsonBounds(Bounds bounds)
        {
            return "{\"center\":" + JsonVector3(bounds.center) + ",\"size\":" + JsonVector3(bounds.size) + "}";
        }

        /// <summary>
        /// Encodes a Unity object reference using Unity 2022's int instance ID identity model.
        /// </summary>
        /// <param name="unityObject">The object to encode.</param>
        /// <returns>A compact JSON reference object.</returns>
        private static string JsonObjectRef(UnityEngine.Object unityObject)
        {
            if (unityObject == null)
            {
                return "null";
            }

            var path = AssetDatabase.GetAssetPath(unityObject);
            return "{\"instanceID\":" + unityObject.GetInstanceID() +
                ",\"name\":" + JsonQuote(unityObject.name) +
                ",\"type\":" + JsonQuote(unityObject.GetType().FullName) +
                ",\"assetPath\":" + JsonQuote(path) + "}";
        }

        /// <summary>
        /// Encodes a GameObject reference with hierarchy path and transform values for follow-up commands.
        /// </summary>
        /// <param name="gameObject">The scene or prefab object to encode.</param>
        /// <returns>A compact JSON object for the GameObject.</returns>
        private static string JsonGameObject(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return "null";
            }

            return "{\"instanceID\":" + gameObject.GetInstanceID() +
                ",\"name\":" + JsonQuote(gameObject.name) +
                ",\"path\":" + JsonQuote(GetGameObjectPath(gameObject)) +
                ",\"activeSelf\":" + (gameObject.activeSelf ? "true" : "false") +
                ",\"activeInHierarchy\":" + (gameObject.activeInHierarchy ? "true" : "false") +
                ",\"tag\":" + JsonQuote(gameObject.tag) +
                ",\"layer\":" + gameObject.layer +
                ",\"position\":" + JsonVector3(gameObject.transform.position) +
                ",\"rotationEuler\":" + JsonVector3(gameObject.transform.eulerAngles) +
                ",\"scale\":" + JsonVector3(gameObject.transform.localScale) +
                ",\"scene\":" + JsonQuote(gameObject.scene.path) +
                ",\"context\":" + JsonQuote(GameObjectContext(gameObject)) + "}";
        }

        /// <summary>
        /// Classifies where a GameObject currently lives so a caller can tell, at a glance, whether an edit
        /// landed on the prefab being edited or on a scene instance: "prefabStage" when it belongs to the
        /// open prefab editing stage, "prefabAsset" when it is a persistent prefab-asset object, otherwise
        /// "scene". Resolves the prefab stage via its fully-qualified type so this stays free of extra usings.
        /// </summary>
        /// <param name="gameObject">The GameObject to classify.</param>
        /// <returns>One of "prefabStage", "prefabAsset", or "scene".</returns>
        private static string GameObjectContext(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return "scene";
            }

            if (UnityEditor.SceneManagement.PrefabStageUtility.GetPrefabStage(gameObject) != null)
            {
                return "prefabStage";
            }

            return EditorUtility.IsPersistent(gameObject) ? "prefabAsset" : "scene";
        }

        /// <summary>
        /// Encodes a component reference with enough information to address it again by owner and type.
        /// </summary>
        /// <param name="component">The component to encode.</param>
        /// <returns>A compact JSON object for the component.</returns>
        private static string JsonComponent(Component component)
        {
            if (component == null)
            {
                return "null";
            }

            return "{\"instanceID\":" + component.GetInstanceID() +
                ",\"type\":" + JsonQuote(component.GetType().FullName) +
                ",\"gameObject\":" + JsonGameObject(component.gameObject) + "}";
        }

        /// <summary>
        /// Encodes scene identity and state data in the same int/hash-free style Unity 2022 exposes.
        /// </summary>
        /// <param name="scene">The scene to encode.</param>
        /// <returns>A compact JSON object for the scene.</returns>
        private static string JsonScene(Scene scene)
        {
            return "{\"name\":" + JsonQuote(scene.name) +
                ",\"path\":" + JsonQuote(scene.path) +
                ",\"buildIndex\":" + scene.buildIndex +
                ",\"isLoaded\":" + (scene.isLoaded ? "true" : "false") +
                ",\"isDirty\":" + (scene.isDirty ? "true" : "false") +
                ",\"rootCount\":" + scene.rootCount + "}";
        }

        /// <summary>
        /// Encodes a string list as a JSON array.
        /// </summary>
        /// <param name="values">The string values to encode.</param>
        /// <returns>A JSON array.</returns>
        private static string JsonStringArray(IEnumerable<string> values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var first = true;
            foreach (var value in values)
            {
                if (!first)
                {
                    builder.Append(',');
                }

                first = false;
                builder.Append(JsonQuote(value));
            }

            builder.Append(']');
            return builder.ToString();
        }

        #endregion

        #region Object lookup

        /// <summary>
        /// Resolves an asset input by path, GUID, or int instance ID.
        /// </summary>
        /// <param name="input">The asset path, GUID, or instance ID supplied by a command.</param>
        /// <param name="asset">The resolved Unity asset object.</param>
        /// <param name="assetPath">The resolved project-relative asset path.</param>
        /// <returns>An error string when resolution fails, otherwise null.</returns>
        private static string TryFindAssetObject(string input, out UnityEngine.Object asset, out string assetPath)
        {
            asset = null;
            assetPath = string.Empty;
            if (string.IsNullOrWhiteSpace(input))
            {
                return "Missing asset path, GUID, or instance ID.";
            }

            input = NormalizeAssetPath(input);
            int instanceId;
            if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out instanceId))
            {
                asset = EditorUtility.InstanceIDToObject(instanceId);
                assetPath = asset == null ? string.Empty : AssetDatabase.GetAssetPath(asset);
                return asset == null ? "No asset found for instance ID: " + instanceId : null;
            }

            if (!input.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) &&
                !input.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                var guidPath = AssetDatabase.GUIDToAssetPath(input);
                if (!string.IsNullOrEmpty(guidPath))
                {
                    input = guidPath;
                }
            }

            assetPath = NormalizeAssetPath(input);
            asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return asset == null ? "Asset not found: " + input : null;
        }

        /// <summary>
        /// Resolves a Unity object using scene object lookup first and asset lookup second.
        /// </summary>
        /// <param name="input">A scene path/name/ID or asset path/GUID/ID.</param>
        /// <param name="unityObject">The resolved Unity object.</param>
        /// <returns>An error string when no object can be found, otherwise null.</returns>
        private static string TryFindAnyUnityObject(string input, out UnityEngine.Object unityObject)
        {
            unityObject = null;
            if (string.IsNullOrWhiteSpace(input))
            {
                return "Missing Unity object target.";
            }

            GameObject gameObject;
            var sceneError = TryFindGameObject(input, out gameObject);
            if (sceneError == null)
            {
                unityObject = gameObject;
                return null;
            }

            UnityEngine.Object asset;
            string assetPath;
            var assetError = TryFindAssetObject(input, out asset, out assetPath);
            if (assetError == null)
            {
                unityObject = asset;
                return null;
            }

            return sceneError + " Also failed as asset: " + assetError;
        }

        /// <summary>
        /// Finds a component on a GameObject by instance ID, type full name, type short name, or index.
        /// </summary>
        /// <param name="gameObject">The GameObject that owns the component.</param>
        /// <param name="selector">The component selector supplied by a command.</param>
        /// <param name="component">The resolved component.</param>
        /// <returns>An error string when no component can be resolved, otherwise null.</returns>
        private static string TryFindComponent(GameObject gameObject, string selector, out Component component)
        {
            component = null;
            if (gameObject == null)
            {
                return "Missing GameObject for component lookup.";
            }

            if (string.IsNullOrWhiteSpace(selector))
            {
                return "Missing component selector. Use a type name, component index, or instance ID.";
            }

            selector = selector.Trim().Trim('"');
            var components = gameObject.GetComponents<Component>();
            int numeric;
            if (int.TryParse(selector, NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                foreach (var candidate in components)
                {
                    if (candidate != null && candidate.GetInstanceID() == numeric)
                    {
                        component = candidate;
                        return null;
                    }
                }

                if (numeric >= 0 && numeric < components.Length)
                {
                    component = components[numeric];
                    return component == null ? "Component at index " + numeric + " is missing." : null;
                }
            }

            foreach (var candidate in components)
            {
                if (candidate == null)
                {
                    continue;
                }

                var type = candidate.GetType();
                if (string.Equals(type.FullName, selector, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(type.Name, selector, StringComparison.OrdinalIgnoreCase))
                {
                    component = candidate;
                    return null;
                }
            }

            return "Component not found on " + GetGameObjectPath(gameObject) + ": " + selector;
        }

        /// <summary>
        /// Resolves a type by full name, short name, or assembly-qualified name for add/list/reflection tools.
        /// </summary>
        /// <param name="typeName">The requested type name.</param>
        /// <returns>The resolved type, or null when it cannot be found.</returns>
        private static Type FindTypeLoose(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            typeName = typeName.Trim().Trim('"');
            var direct = Type.GetType(typeName, false) ?? FindLoadedType(typeName);
            if (direct != null)
            {
                return direct;
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
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

                    if (string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        #endregion

        #region Parsing and dirtying

        /// <summary>
        /// Parses a bool command token using the bridge's friendly yes/no/on/off language.
        /// </summary>
        /// <param name="text">The input token.</param>
        /// <param name="value">The parsed bool value.</param>
        /// <returns>True when parsing succeeds.</returns>
        private static bool TryParseBoolLoose(string text, out bool value)
        {
            value = false;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            text = text.Trim().Trim('"').ToLowerInvariant();
            if (text == "true" || text == "1" || text == "yes" || text == "on" || text == "enabled")
            {
                value = true;
                return true;
            }

            if (text == "false" || text == "0" || text == "no" || text == "off" || text == "disabled")
            {
                value = false;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Marks an object's scene dirty when the object belongs to an editable scene, and does nothing in play mode, where
        /// no change is saved and Unity throws on the call. Every verb writes before it marks, so a throw here used to
        /// report a change that had already landed as failed.
        /// </summary>
        /// <param name="gameObject">The object whose scene should be saved later.</param>
        private static void MarkGameObjectSceneDirty(GameObject gameObject)
        {
            if (gameObject != null && gameObject.scene.IsValid() && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            }
        }

        #endregion
    }
}
