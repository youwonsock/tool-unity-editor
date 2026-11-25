using System;

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

        public const int VolumeCornerCount = 72;

        /// <summary>
        /// The Volume3D corner rule: a 2- or 3-axis diagonal step is open only
        /// when every cell it passes beside is open too. Entries
        /// [VolumeCornerStart[d], VolumeCornerStart[d + 1]) of
        /// <see cref="VolumeCornerDirections"/> are the directions from the
        /// start cell to those cells for direction d: none for a face step,
        /// two for a 2-axis diagonal and six (three faces, three edges) for a
        /// 3-axis diagonal. The Volume3D solve copies the table into its job
        /// data, as it does the deltas.
        /// </summary>
        public static readonly int[] VolumeCornerStart =
        {
            0, 0, 0, 0, 0, 2, 4, 6,
            8, 8,
            8, 10, 12, 14,
            16, 18, 20, 22,
            24, 30, 36, 42, 48, 54, 60, 66,
            VolumeCornerCount,
        };

        public static readonly int[] VolumeCornerDirections =
        {
            2, 0, 3, 0, 2, 1, 3, 1,
            8, 0, 8, 1, 9, 0, 9, 1,
            2, 8, 2, 9, 3, 8, 3, 9,
            2, 8, 14, 0, 4, 10,
            3, 8, 16, 0, 5, 10,
            2, 9, 15, 0, 4, 12,
            3, 9, 17, 0, 5, 12,
            2, 8, 14, 1, 6, 11,
            3, 8, 16, 1, 7, 11,
            2, 9, 15, 1, 6, 13,
            3, 9, 17, 1, 7, 13,
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
