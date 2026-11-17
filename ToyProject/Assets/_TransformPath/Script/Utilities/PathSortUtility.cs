using System;
using System.Collections.Generic;

namespace Common.TransformPath
{
    internal static class PathSortUtility
    {
        /// <summary>
        /// Stable in-place insertion sort. Equal items keep their order, and an
        /// almost sorted list, such as a queue between two frames, takes one pass.
        /// It allocates nothing. Returns whether any item moved.
        /// </summary>
        public static bool StableInsertionSort<T>(IList<T> items, Comparison<T> comparison)
        {
            bool moved = false;
            for (int i = 1; i < items.Count; i++)
            {
                T current = items[i];
                int previous = i - 1;
                while (previous >= 0 && comparison(items[previous], current) > 0)
                {
                    items[previous + 1] = items[previous];
                    previous--;
                    moved = true;
                }
                items[previous + 1] = current;
            }
            return moved;
        }
    }
}
