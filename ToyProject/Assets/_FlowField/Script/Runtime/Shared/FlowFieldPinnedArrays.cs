using Unity.Collections.LowLevel.Unsafe;

namespace Common.FlowField
{
    /// <summary>
    /// Pins managed arrays so a Burst job can read and write them. The published
    /// field keeps its managed arrays, so sampling and baking never see a
    /// native type. A job must have finished before <see cref="Release"/>.
    /// </summary>
    internal sealed class FlowFieldPinnedArrays
    {
        private readonly ulong[] _handles;
        private int _count;

        internal FlowFieldPinnedArrays(int capacity)
        {
            _handles = new ulong[capacity];
        }

        internal unsafe T* Pin<T>(T[] array) where T : unmanaged
        {
            void* pointer = UnsafeUtility.PinGCArrayAndGetDataAddress(array, out _handles[_count]);
            _count++;
            return (T*)pointer;
        }

        internal void Release()
        {
            for (int i = 0; i < _count; i++)
                UnsafeUtility.ReleaseGCObject(_handles[i]);
            _count = 0;
        }
    }
}
