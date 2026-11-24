using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Common.TransformPath
{
    /// <summary>
    /// Compares by reference. UnityEngine.Object's equality calls every destroyed
    /// object equal, so a dictionary keyed by components uses this instead.
    /// </summary>
    internal sealed class PathReferenceComparer<T> : IEqualityComparer<T> where T : class
    {
        public static readonly PathReferenceComparer<T> Instance = new PathReferenceComparer<T>();

        private PathReferenceComparer() { }

        public bool Equals(T left, T right) => ReferenceEquals(left, right);

        public int GetHashCode(T value) => RuntimeHelpers.GetHashCode(value);
    }
}
