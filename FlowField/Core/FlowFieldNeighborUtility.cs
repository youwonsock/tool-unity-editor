namespace Supercent.Common.FlowField
{
    internal static class FlowFieldNeighborUtility
    {
        public const int Count = 8;
        public static readonly int[] DeltaX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        public static readonly int[] DeltaZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        public static bool IsDiagonal(int directionIndex)
            => directionIndex >= 4;

        public static int FindDirectionIndex(int deltaX, int deltaZ)
        {
            for (int i = 0; i < Count; i++)
            {
                if (DeltaX[i] == deltaX && DeltaZ[i] == deltaZ)
                    return i;
            }

            return -1;
        }
    }
}
