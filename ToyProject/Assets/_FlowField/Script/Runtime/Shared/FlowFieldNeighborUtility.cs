using System;
using UnityEngine;

namespace Common.FlowField
{
    internal static class FlowFieldNeighborUtility
    {
        public const int SurfaceCount = 8;
        public const int VolumeCount = 26;
        public static readonly int[] VolumeFaceDirections = { 0, 1, 2, 3, 8, 9 };
        public static readonly int[] DeltaX =
        {
            1, -1, 0, 0, 1, 1, -1, -1,
            0, 0,
            1, -1, 1, -1,
            0, 0, 0, 0,
            1, 1, 1, 1, -1, -1, -1, -1,
        };
        public static readonly int[] DeltaY =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            1, -1,
            1, 1, -1, -1,
            1, -1, 1, -1,
            1, 1, -1, -1, 1, 1, -1, -1,
        };
        public static readonly int[] DeltaZ =
        {
            0, 0, 1, -1, 1, -1, 1, -1,
            0, 0,
            0, 0, 0, 0,
            1, 1, -1, -1,
            1, -1, 1, -1, 1, -1, 1, -1,
        };

        public static bool IsDiagonal(int directionIndex)
            => Math.Abs(DeltaX[directionIndex])
                + Math.Abs(DeltaY[directionIndex])
                + Math.Abs(DeltaZ[directionIndex]) > 1;

        public static int FindDirectionIndex(int deltaX, int deltaZ)
        {
            for (int i = 0; i < SurfaceCount; i++)
            {
                if (DeltaX[i] == deltaX && DeltaZ[i] == deltaZ && DeltaY[i] == 0)
                    return i;
            }

            return -1;
        }

        public static int FindDirectionIndex(int deltaX, int deltaY, int deltaZ)
        {
            for (int i = 0; i < VolumeCount; i++)
                if (DeltaX[i] == deltaX && DeltaY[i] == deltaY && DeltaZ[i] == deltaZ)
                    return i;
            return -1;
        }
    }
}
