using System;
using System.Runtime.InteropServices;

namespace Common.FlowField
{
    /// <summary>
    /// Stores bake arrays as byte[] fields. Unity writes a byte[] as one hex
    /// string, while float and Vector3 arrays take one YAML line per element.
    /// The bytes are the raw little-endian IEEE 754 values, which is the byte
    /// order of every platform Unity supports.
    /// </summary>
    internal static class FlowFieldBakeBlob
    {
        internal static byte[] Pack<T>(T[] values) where T : unmanaged
            => values == null || values.Length == 0
                ? Array.Empty<byte>()
                : MemoryMarshal.AsBytes(new ReadOnlySpan<T>(values)).ToArray();

        /// <summary>
        /// Returns null when the byte count is not a whole number of
        /// elements, so the owner's length validation rejects the asset.
        /// </summary>
        internal static T[] Unpack<T>(byte[] bytes) where T : unmanaged
        {
            if (bytes == null || bytes.Length == 0)
                return Array.Empty<T>();
            if (bytes.Length % Marshal.SizeOf<T>() != 0)
                return null;
            return MemoryMarshal.Cast<byte, T>(bytes).ToArray();
        }
    }
}
