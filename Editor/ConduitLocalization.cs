/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// The Conduit window's languages and its translated text. The strings live in a data file beside the
    /// flags rather than in code, so a translation is an edit to one table and the source stays ASCII.
    ///
    /// Every string falls back to English when its translation is missing, so a part-translated language is
    /// usable rather than broken. The choice is stored in the editor's per-user preferences, so it survives a
    /// restart and follows the user into every project on this machine.
    /// </summary>
    internal static class ConduitLocalization
    {
        #region Constants

        /// <summary>Editor preference holding the chosen language code.</summary>
        private const string LANGUAGE_PREF = "ItsRenji.Conduit.Language";

        /// <summary>
        /// Editor preference holding the chosen flag. Kept apart from the language because two flags can share
        /// one language, and the picker shows the flag the user actually chose.
        /// </summary>
        private const string FLAG_PREF = "ItsRenji.Conduit.LanguageFlag";

        /// <summary>The language every string falls back to.</summary>
        private const string ENGLISH = "en";

        /// <summary>The string table, relative to Conduit's own folder.</summary>
        private const string TABLE_PATH = "Editor/Localization/conduit-strings.json";

        /// <summary>The flag images, relative to Conduit's own folder.</summary>
        private const string FLAG_FOLDER = "Editor/Localization/Flags";

        #endregion

        #region Types

        /// <summary>One language the window can be shown in.</summary>
        internal sealed class Language
        {
            /// <summary>Code the strings are keyed by, such as "ja".</summary>
            public string Code;

            /// <summary>The language's name written in itself, as the picker shows it.</summary>
            public string Name;

            /// <summary>Flag image names that select this language, in picker order.</summary>
            public string[] Flags;
        }

        #endregion

        #region State

        /// <summary>Languages read from the table, in picker order.</summary>
        private static List<Language> _languages;

        /// <summary>Translations by key, then by language code.</summary>
        private static Dictionary<string, Dictionary<string, string>> _strings;

        /// <summary>Flag images already loaded, by name.</summary>
        private static readonly Dictionary<string, Texture2D> _flags = new Dictionary<string, Texture2D>(StringComparer.Ordinal);

        #endregion

        #region Public surface

        /// <summary>Every language the window offers, in picker order. Empty when the table could not be read.</summary>
        internal static IReadOnlyList<Language> Languages
        {
            get
            {
                Load();
                return _languages;
            }
        }

        /// <summary>
        /// The language in use: the saved choice when the table offers it, otherwise English. A saved code the
        /// table no longer carries falls back rather than leaving the window in a language it cannot show.
        /// </summary>
        internal static string CurrentCode
        {
            get
            {
                string saved = EditorPrefs.GetString(LANGUAGE_PREF, ENGLISH);
                return Find(saved) != null ? saved : ENGLISH;
            }
        }

        /// <summary>The flag the picker shows for the language in use.</summary>
        internal static string CurrentFlag
        {
            get
            {
                Language language = Find(CurrentCode);
                if (language == null || language.Flags.Length == 0) return null;

                string saved = EditorPrefs.GetString(FLAG_PREF, string.Empty);
                return Array.IndexOf(language.Flags, saved) >= 0 ? saved : language.Flags[0];
            }
        }

        /// <summary>Stores a language and the flag it was chosen under, so both survive a restart.</summary>
        /// <param name="code">Language code to show the window in.</param>
        /// <param name="flag">Flag the user chose it under.</param>
        internal static void Choose(string code, string flag)
        {
            if (Find(code) == null) return;

            EditorPrefs.SetString(LANGUAGE_PREF, code);
            EditorPrefs.SetString(FLAG_PREF, flag ?? string.Empty);
        }

        /// <summary>
        /// The text for a key in the language in use, falling back to English when the translation is missing.
        /// A key the table does not hold returns itself, so a missing entry is visible rather than blank.
        /// </summary>
        /// <param name="key">String key, such as "button.connect".</param>
        /// <returns>The text to show.</returns>
        internal static string T(string key)
        {
            Load();
            if (!_strings.TryGetValue(key, out Dictionary<string, string> texts)) return key;

            if (texts.TryGetValue(CurrentCode, out string text) && !string.IsNullOrEmpty(text)) return text;
            return texts.TryGetValue(ENGLISH, out string english) && !string.IsNullOrEmpty(english) ? english : key;
        }

        /// <summary>Formats a translated string with its values, using invariant number formatting.</summary>
        /// <param name="key">String key.</param>
        /// <param name="args">Values for the string's numbered placeholders.</param>
        /// <returns>The formatted text.</returns>
        internal static string Format(string key, params object[] args)
        {
            return string.Format(CultureInfo.InvariantCulture, T(key), args);
        }

        /// <summary>Loads one flag image from Conduit's own folder.</summary>
        /// <param name="name">Flag image name without its extension.</param>
        /// <returns>The flag, or null when it is not there.</returns>
        internal static Texture2D Flag(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_flags.TryGetValue(name, out Texture2D cached) && cached != null) return cached;

            string root = ConduitSkills.ConduitRoot();
            if (root.Length == 0) return null;

            Texture2D flag = AssetDatabase.LoadAssetAtPath<Texture2D>(root + "/" + FLAG_FOLDER + "/" + name + ".png");
            _flags[name] = flag;
            return flag;
        }

        #endregion

        #region Loading

        /// <summary>Finds a language by its code.</summary>
        /// <param name="code">Language code.</param>
        /// <returns>The language, or null.</returns>
        private static Language Find(string code)
        {
            Load();
            foreach (Language language in _languages)
            {
                if (language.Code == code) return language;
            }

            return null;
        }

        /// <summary>
        /// Reads the string table once. It is loaded through the asset database rather than from disk because a
        /// package installed from Git sits in the package cache, where its Packages path is not a real folder.
        /// An unreadable table leaves both collections empty, so every key shows as itself and the problem is
        /// visible in the window instead of failing silently.
        /// </summary>
        private static void Load()
        {
            if (_strings != null) return;

            _languages = new List<Language>();
            _strings = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

            string root = ConduitSkills.ConduitRoot();
            TextAsset table = root.Length == 0 ? null : AssetDatabase.LoadAssetAtPath<TextAsset>(root + "/" + TABLE_PATH);
            if (table == null)
            {
                Debug.LogError("[Conduit] The window's string table was not found at " + root + "/" + TABLE_PATH + ".");
                return;
            }

            JObject data;
            try
            {
                data = JObject.Parse(table.text);
            }
            catch (Exception ex)
            {
                Debug.LogError("[Conduit] The window's string table could not be read: " + ex.Message);
                return;
            }

            if (data["languages"] is JArray languages)
            {
                foreach (JToken entry in languages)
                {
                    var flags = new List<string>();
                    if (entry["flags"] is JArray flagNames)
                    {
                        foreach (JToken flag in flagNames) flags.Add((string)flag);
                    }

                    _languages.Add(new Language { Code = (string)entry["code"], Name = (string)entry["name"], Flags = flags.ToArray() });
                }
            }

            if (data["strings"] is JObject strings)
            {
                foreach (JProperty key in strings.Properties())
                {
                    var texts = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (key.Value is JObject byLanguage)
                    {
                        foreach (JProperty text in byLanguage.Properties()) texts[text.Name] = (string)text.Value;
                    }

                    _strings[key.Name] = texts;
                }
            }
        }

        #endregion
    }
}
