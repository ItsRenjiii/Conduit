/*
SPDX-License-Identifier: LicenseRef-PolyForm-Shield-1.0.0
Copyright (c) 2026 ItsRenji. All rights reserved.

Licensed under the PolyForm Shield License 1.0.0. See LICENSE.md in the Conduit package, or
https://polyformproject.org/licenses/shield/1.0.0
*/

using UnityEngine;

namespace ItsRenji.Conduit
{
    /// <summary>
    /// The night the Conduit window's cards sit on: a tile of faint stars, built once per editor session
    /// and repeated behind the panel.
    /// <para>
    /// Every star is a hard single pixel with no falloff around it. That is deliberate and it is the whole
    /// reason the sky is drawn rather than shipped as art: a soft glow fading into a dark ground is exactly
    /// what bands into visible rings on an eight bit display, and a starfield is nothing but a field of
    /// small glows. Hard points cannot band.
    /// </para>
    /// <para>
    /// This is the window's OWN field, not a reference to the inspector examples'. Those are reference
    /// material and nothing outside them may depend on them, so the technique is shared and the file is not.
    /// </para>
    /// </summary>
    internal static class ConduitWindowStarfield
    {
        #region Constants

        /// <summary>Side of the repeating tile in pixels.</summary>
        private const int TILE_SIZE = 256;

        /// <summary>Stars in one tile. Sparse on purpose: a dense field reads as noise, not as a sky.</summary>
        private const int STAR_COUNT = 104;

        /// <summary>
        /// Seed the field is scattered with. Fixed, so the sky is the same one every session: a field that
        /// reshuffled on each domain reload would read as static rather than as a surface.
        /// </summary>
        private const int SCATTER_SEED = 20260913;

        /// <summary>Share of stars drawn at the faintest of the three brightnesses, as a percentage.</summary>
        private const int FAINTEST_SHARE = 62;

        /// <summary>Share of stars drawn at the faintest or the middle brightness, as a percentage.</summary>
        private const int MIDDLE_SHARE = 90;

        /// <summary>Alpha of the faintest stars, which most of the field is drawn at.</summary>
        private const byte ALPHA_FAINT = 34;

        /// <summary>Alpha of the middle brightness.</summary>
        private const byte ALPHA_MID = 80;

        /// <summary>Alpha of the few brightest stars.</summary>
        private const byte ALPHA_BRIGHT = 144;

        #endregion

        #region Private State

        /// <summary>The built tile, kept for the session.</summary>
        private static Texture2D _tile;

        #endregion

        #region Public API

        /// <summary>The repeating star tile, built on first use.</summary>
        public static Texture2D Tile
        {
            get
            {
                if (_tile == null) _tile = Build();
                return _tile;
            }
        }

        #endregion

        #region Building

        /// <summary>Scatters one tile of stars.</summary>
        /// <returns>The tile.</returns>
        private static Texture2D Build()
        {
            Texture2D tile = new Texture2D(TILE_SIZE, TILE_SIZE, TextureFormat.RGBA32, false)
            {
                name = "Conduit Starfield",
                hideFlags = HideFlags.HideAndDontSave,
                wrapMode = TextureWrapMode.Repeat,

                // Point sampling keeps every star exactly one hard pixel. Bilinear would smear each one
                // into the soft falloff this whole approach exists to avoid.
                filterMode = FilterMode.Point,
            };

            Color32[] pixels = new Color32[TILE_SIZE * TILE_SIZE];
            Color32 nothing = new Color32(0, 0, 0, 0);
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = nothing;
            }

            System.Random scatter = new System.Random(SCATTER_SEED);
            for (int i = 0; i < STAR_COUNT; i++)
            {
                int x = scatter.Next(TILE_SIZE);
                int y = scatter.Next(TILE_SIZE);
                int roll = scatter.Next(100);

                // Three brightnesses weighted towards the faintest, because an even spread reads flat.
                byte alpha = roll < FAINTEST_SHARE ? ALPHA_FAINT : roll < MIDDLE_SHARE ? ALPHA_MID : ALPHA_BRIGHT;

                // Warm white rather than white, so the sky belongs to the same palette as the leather.
                pixels[(y * TILE_SIZE) + x] = new Color32(255, 247, 232, alpha);
            }

            tile.SetPixels32(pixels);
            tile.Apply(false, false);
            return tile;
        }

        #endregion
    }
}
