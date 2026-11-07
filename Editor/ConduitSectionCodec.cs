/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

// The pipe-section codec for the generic argument mode.
//
// Verified byte-for-byte against a frozen oracle before being lifted here: 8 of 8 corpus cases, compared as
// UTF-8 hex so a difference cannot hide behind display escaping. The cases that matter are the awkward ones -
// an intentionally empty middle slot, JSON carrying quotes, JSON carrying a literal pipe, escaped quotes
// beside Windows backslashes, a backslash immediately before a pipe, a trailing backslash, and Unicode mixed
// with both.
using System;
using System.Collections.Generic;
using System.Text;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// Encodes and decodes the pipe-delimited sections a bridge command is built from. Double quotes are
    /// never syntax here and remain payload, which is what keeps JSON that works today byte-identical.
    /// </summary>
    internal static class ConduitSectionCodec
    {
        #region Constants

        /// <summary>Separator written between encoded sections. The surrounding spaces are load-bearing.</summary>
        private const string SEPARATOR = " | ";

        #endregion

        #region Encoding

        /// <summary>
        /// Escapes one section value. Only backslash and pipe are reserved; everything else, quotes included,
        /// passes through untouched.
        /// </summary>
        /// <param name="value">Raw section value.</param>
        /// <returns>The escaped section.</returns>
        internal static string EncodeSection(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var builder = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                if (c == '\\') builder.Append("\\\\");
                else if (c == '|') builder.Append("\\|");
                else builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>Joins values into one wire string.</summary>
        /// <param name="values">Section values in order.</param>
        /// <returns>The encoded argument string.</returns>
        internal static string Encode(IList<string> values)
        {
            var parts = new string[values.Count];
            for (var i = 0; i < values.Count; i++) parts[i] = EncodeSection(values[i]);
            return string.Join(SEPARATOR, parts);
        }

        #endregion

        #region Decoding

        /// <summary>
        /// Splits on unescaped pipes and unescapes each section. An unrecognised backslash sequence is
        /// preserved exactly as written, so a legacy Windows path that was never escaped still round-trips.
        /// </summary>
        /// <param name="encoded">The wire string.</param>
        /// <returns>Decoded section values, each trimmed as the current splitter trims.</returns>
        internal static List<string> Decode(string encoded)
        {
            var sections = new List<string>();
            if (encoded == null) return sections;

            var current = new StringBuilder();
            for (var i = 0; i < encoded.Length; i++)
            {
                var c = encoded[i];
                if (c == '\\' && i + 1 < encoded.Length)
                {
                    var next = encoded[i + 1];
                    if (next == '\\') { current.Append('\\'); i++; continue; }
                    if (next == '|') { current.Append('|'); i++; continue; }

                    current.Append('\\'); // unrecognised escape, preserved as written
                    continue;
                }

                if (c == '|') { sections.Add(current.ToString().Trim()); current.Length = 0; continue; }

                current.Append(c);
            }

            sections.Add(current.ToString().Trim());
            return sections;
        }

        #endregion
    }
}
