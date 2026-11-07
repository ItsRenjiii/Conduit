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

using UnityEngine;

namespace ItsRenji.Inspector.Editors
{
    /// <summary>
    /// The night the panels sit on: a tile of faint stars, built once per editor session and repeated
    /// behind the inspector.
    /// <para>
    /// Every star is a hard single pixel with no falloff around it. That is deliberate and it is the
    /// whole reason this is drawn rather than shipped as art: a soft glow fading into a dark ground is
    /// exactly what bands into visible rings on an eight bit display, and a starfield is nothing but a
    /// field of small glows. Hard points cannot band.
    /// </para>
    /// </summary>
    internal static class ItsRenjiInspectorStarfield
    {
        #region Constants

        /// <summary>Side of the repeating tile in pixels.</summary>
        private const int TILE_SIZE = 256;

        /// <summary>Stars in one tile. Sparse on purpose: a dense field reads as noise, not as a sky.</summary>
        private const int STAR_COUNT = 104;

        /// <summary>
        /// Seed the field is scattered with. Fixed, so the sky is the same one every session: a field
        /// that reshuffled on each domain reload would read as static rather than as a surface.
        /// </summary>
        private const int SCATTER_SEED = 20260911;

        /// <summary>Share of stars drawn at the faintest of the three brightnesses, as a percentage.</summary>
        private const int FAINTEST_SHARE = 62;

        /// <summary>Share of stars drawn at the faintest or the middle brightness, as a percentage.</summary>
        private const int MIDDLE_SHARE = 90;

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
                name = "ItsRenji Starfield",
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
                byte alpha = roll < FAINTEST_SHARE ? (byte)34 : roll < MIDDLE_SHARE ? (byte)80 : (byte)144;

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
