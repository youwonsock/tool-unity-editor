using System;
using UnityEngine;

namespace Common.FlowField.Editor
{
    /// <summary>
    /// Selects display coordinates without changing the underlying grid.  A
    /// selection is an implicit sequence, so a full-volume repaint does not
    /// need a second array containing every displayed index.
    /// </summary>
    internal readonly struct FlowFieldVolumeDisplaySelection
    {
        internal const int DefaultFullVolumeLimit = 8192;
        internal const int DefaultSliceLimit = 4096;

        private readonly int _sampleX;
        private readonly int _sampleY;
        private readonly int _sampleZ;
        private readonly int _fixedCoordinate;
        private readonly FlowFieldVolumeSliceAxis _sliceAxis;

        internal FlowFieldVolumeGizmoMode Mode { get; }
        internal int DisplayCount { get; }

        private FlowFieldVolumeDisplaySelection(
            FlowFieldVolumeGizmoMode mode,
            FlowFieldVolumeSliceAxis sliceAxis,
            int fixedCoordinate,
            int sampleX,
            int sampleY,
            int sampleZ,
            int displayCount)
        {
            Mode = mode;
            _sliceAxis = sliceAxis;
            _fixedCoordinate = fixedCoordinate;
            _sampleX = sampleX;
            _sampleY = sampleY;
            _sampleZ = sampleZ;
            DisplayCount = displayCount;
        }

        internal static bool TryCreate(
            FlowFieldGridSpace grid,
            FlowFieldVolumeGizmoMode mode,
            FlowFieldVolumeSliceAxis sliceAxis,
            int requestedSlice,
            int maxDisplayCells,
            out FlowFieldVolumeDisplaySelection selection)
        {
            selection = default;
            if (!grid.IsValid || grid.SpaceMode != FlowFieldSpaceMode.Volume3D
                || maxDisplayCells <= 0
                || !Enum.IsDefined(typeof(FlowFieldVolumeGizmoMode), mode)
                || !Enum.IsDefined(typeof(FlowFieldVolumeSliceAxis), sliceAxis))
                return false;

            int fixedCoordinate = NormalizeSlice(grid, sliceAxis, requestedSlice);
            int limit = mode == FlowFieldVolumeGizmoMode.Slice
                ? Math.Min(maxDisplayCells, DefaultSliceLimit)
                : Math.Min(maxDisplayCells, DefaultFullVolumeLimit);

            int stride = FindSmallestStride(grid, mode, sliceAxis, limit);
            if (stride <= 0)
                return false;

            int sampleX = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.X
                ? 1
                : ComputeSampleCount(grid.Width, stride);
            int sampleY = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.Y
                ? 1
                : ComputeSampleCount(grid.Height, stride);
            int sampleZ = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.Z
                ? 1
                : ComputeSampleCount(grid.Depth, stride);
            long displayCount = (long)sampleX * sampleY * sampleZ;
            if (displayCount <= 0L || displayCount > limit || displayCount > int.MaxValue)
                return false;

            selection = new FlowFieldVolumeDisplaySelection(
                mode,
                sliceAxis,
                fixedCoordinate,
                sampleX,
                sampleY,
                sampleZ,
                (int)displayCount);
            return true;
        }

        internal bool TryGetCoordinate(
            FlowFieldGridSpace grid,
            int ordinal,
            out int x,
            out int y,
            out int z,
            out int flatIndex)
        {
            x = 0;
            y = 0;
            z = 0;
            flatIndex = -1;
            if (!grid.IsValid || ordinal < 0 || ordinal >= DisplayCount)
                return false;

            long remaining = ordinal;
            int sampleXPosition = (int)(remaining % _sampleX);
            remaining /= _sampleX;
            int sampleZPosition = (int)(remaining % _sampleZ);
            int sampleYPosition = (int)(remaining / _sampleZ);

            x = MapSampleCoordinate(sampleXPosition, _sampleX, grid.Width);
            y = MapSampleCoordinate(sampleYPosition, _sampleY, grid.Height);
            z = MapSampleCoordinate(sampleZPosition, _sampleZ, grid.Depth);
            if (Mode == FlowFieldVolumeGizmoMode.Slice)
            {
                if (_sliceAxis == FlowFieldVolumeSliceAxis.X)
                    x = _fixedCoordinate;
                else if (_sliceAxis == FlowFieldVolumeSliceAxis.Y)
                    y = _fixedCoordinate;
                else
                    z = _fixedCoordinate;
            }

            if (!grid.IsLocalInBounds(x, y, z))
                return false;
            flatIndex = grid.ToFlatIndex(x, y, z);
            return true;
        }

        internal static int ComputeSampleCount(int dimension, int stride)
        {
            if (dimension <= 0 || stride <= 0)
                return 0;
            if (dimension == 1)
                return 1;

            long ceil = ((long)dimension + stride - 1L) / stride;
            long count = Math.Max(2L, ceil);
            return (int)Math.Min((long)dimension, count);
        }

        internal static int MapSampleCoordinate(int position, int sampleCount, int dimension)
        {
            if (dimension <= 1 || sampleCount <= 1)
                return 0;
            return (int)((long)position * (dimension - 1L) / (sampleCount - 1L));
        }

        private static int FindSmallestStride(
            FlowFieldGridSpace grid,
            FlowFieldVolumeGizmoMode mode,
            FlowFieldVolumeSliceAxis sliceAxis,
            int limit)
        {
            int largestDimension = Math.Max(grid.Width, Math.Max(grid.Height, grid.Depth));
            int low = 1;
            int high = Math.Max(1, largestDimension);
            if (!IsWithinLimit(grid, mode, sliceAxis, high, limit))
                return 0;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (IsWithinLimit(grid, mode, sliceAxis, middle, limit))
                    high = middle;
                else
                    low = middle + 1;
            }
            return low;
        }

        private static bool IsWithinLimit(
            FlowFieldGridSpace grid,
            FlowFieldVolumeGizmoMode mode,
            FlowFieldVolumeSliceAxis sliceAxis,
            int stride,
            int limit)
        {
            int sampleX = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.X
                ? 1
                : ComputeSampleCount(grid.Width, stride);
            int sampleY = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.Y
                ? 1
                : ComputeSampleCount(grid.Height, stride);
            int sampleZ = mode == FlowFieldVolumeGizmoMode.Slice
                && sliceAxis == FlowFieldVolumeSliceAxis.Z
                ? 1
                : ComputeSampleCount(grid.Depth, stride);
            return (long)sampleX * sampleY * sampleZ <= limit;
        }

        private static int NormalizeSlice(
            FlowFieldGridSpace grid,
            FlowFieldVolumeSliceAxis axis,
            int requestedSlice)
        {
            int length = axis == FlowFieldVolumeSliceAxis.X
                ? grid.Width
                : axis == FlowFieldVolumeSliceAxis.Y ? grid.Height : grid.Depth;
            if (length <= 1)
                return 0;
            return requestedSlice < 0
                ? length / 2
                : Math.Max(0, Math.Min(length - 1, requestedSlice));
        }
    }
}
