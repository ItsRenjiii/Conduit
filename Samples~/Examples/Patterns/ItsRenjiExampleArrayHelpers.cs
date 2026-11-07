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

using UdonSharp;
using UnityEngine;

namespace ItsRenji.Examples.Patterns
{
    /// <summary>
    /// List-like operations on plain arrays, because List&lt;T&gt; cannot be constructed in Udon and exposes
    /// no members at all: it is reachable as a signature type and still unusable, so anything returning
    /// one cannot be read. DataList is the supported alternative; these helpers are for the cases that
    /// have to stay a plain array, such as a synced field.
    ///
    /// Two costs to weigh before using them. Each call allocates a new array and copies, so none of this
    /// belongs in a hot path; a pre-sized array with a separate count is the right shape there. And every
    /// public method makes this behaviour's event dispatch slower, so a helper behaviour with a wide
    /// surface is not free. There are no generics in Udon either, which is why these are per type.
    /// conduit-udonsharp.md and conduit-performance.md own those two points.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ItsRenjiExampleArrayHelpers : UdonSharpBehaviour
    {
        #region GameObject Helpers

        /// <summary>Returns a new array one longer, with the item appended.</summary>
        /// <param name="source">Array to copy from.</param>
        /// <param name="item">Item to append.</param>
        /// <returns>A new array containing the source followed by the item.</returns>
        public GameObject[] _Append(GameObject[] source, GameObject item)
        {
            if (source == null) source = new GameObject[0];

            GameObject[] result = new GameObject[source.Length + 1];
            System.Array.Copy(source, result, source.Length);
            result[source.Length] = item;
            return result;
        }

        /// <summary>Returns the index of an item, or minus one when it is absent.</summary>
        /// <param name="source">Array to search.</param>
        /// <param name="item">Item to find.</param>
        /// <returns>The index, or minus one.</returns>
        public int _IndexOf(GameObject[] source, GameObject item)
        {
            if (source == null) return -1;

            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == item) return i;
            }

            return -1;
        }

        /// <summary>Appends only when the item is not already present.</summary>
        /// <param name="source">Array to copy from.</param>
        /// <param name="item">Item to append.</param>
        /// <returns>A new array, or the source unchanged when the item was already there.</returns>
        public GameObject[] _AppendUnique(GameObject[] source, GameObject item)
        {
            return _IndexOf(source, item) >= 0 ? source : _Append(source, item);
        }

        /// <summary>Returns a new array with the entry at an index removed.</summary>
        /// <param name="source">Array to copy from.</param>
        /// <param name="index">Index to drop.</param>
        /// <returns>A new array, or the source unchanged when the index is out of range.</returns>
        public GameObject[] _RemoveAt(GameObject[] source, int index)
        {
            if (source == null || index < 0 || index >= source.Length) return source;

            GameObject[] result = new GameObject[source.Length - 1];
            System.Array.Copy(source, 0, result, 0, index);
            System.Array.Copy(source, index + 1, result, index, source.Length - index - 1);
            return result;
        }

        /// <summary>Returns a new array with the first occurrence of an item removed.</summary>
        /// <param name="source">Array to copy from.</param>
        /// <param name="item">Item to remove.</param>
        /// <returns>A new array, or the source unchanged when the item was absent.</returns>
        public GameObject[] _Remove(GameObject[] source, GameObject item)
        {
            int index = _IndexOf(source, item);
            return index < 0 ? source : _RemoveAt(source, index);
        }

        /// <summary>Returns a new array of a given length, truncating or padding as needed.</summary>
        /// <param name="source">Array to copy from.</param>
        /// <param name="length">Length of the new array.</param>
        /// <returns>A new array of the requested length.</returns>
        public GameObject[] _Resize(GameObject[] source, int length)
        {
            if (length < 0) length = 0;

            GameObject[] result = new GameObject[length];
            if (source != null)
            {
                System.Array.Copy(source, result, Mathf.Min(source.Length, length));
            }

            return result;
        }

        #endregion

        #region Integer Helpers

        /// <summary>Returns the index of a value in an int array, or minus one.</summary>
        /// <param name="source">Array to search.</param>
        /// <param name="value">Value to find.</param>
        /// <returns>The index, or minus one.</returns>
        public int _IndexOfInt(int[] source, int value)
        {
            if (source == null) return -1;

            for (int i = 0; i < source.Length; i++)
            {
                if (source[i] == value) return i;
            }

            return -1;
        }

        /// <summary>
        /// Shuffles an int array in place. Every client runs its own generator, so a shuffle used for
        /// shared state must be performed once by the owner and the RESULT synced, never re-derived per
        /// client from a shared seed.
        /// </summary>
        /// <param name="source">Array to shuffle.</param>
        public void _Shuffle(int[] source)
        {
            if (source == null) return;

            for (int i = source.Length - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                int carried = source[i];
                source[i] = source[j];
                source[j] = carried;
            }
        }

        #endregion
    }
}
