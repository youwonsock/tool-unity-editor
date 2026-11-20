using System;
using UnityEngine;

namespace Common.FlowField
{
    internal struct FlowFieldCellRect
    {
        public int MinX;
        public int MaxX;
        public int MinY;
        public int MaxY;
        public int MinZ;
        public int MaxZ;
        public bool IsValid => MinX <= MaxX && MinY <= MaxY && MinZ <= MaxZ;

        public static FlowFieldCellRect Invalid => new FlowFieldCellRect
        {
            MinX = 1,
            MaxX = 0,
            MinY = 1,
            MaxY = 0,
            MinZ = 1,
            MaxZ = 0,
        };

        public static FlowFieldCellRect Full(FlowFieldGridSpace grid)
        {
            if (!grid.IsValid)
                return Invalid;

            return new FlowFieldCellRect
            {
                MinX = 0,
                MaxX = grid.Width - 1,
                MinY = 0,
                MaxY = grid.Height - 1,
                MinZ = 0,
                MaxZ = grid.Depth - 1,
            };
        }

        // The cells the bounds overlap: X and Z, and Y in a Volume3D grid.
        public static FlowFieldCellRect FromBounds(FlowFieldGridSpace grid, Bounds worldBounds)
        {
            if (grid.SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                if (!grid.TryGetOverlappingCells(
                        worldBounds,
                        out int volumeMinX,
                        out int volumeMaxX,
                        out int volumeMinY,
                        out int volumeMaxY,
                        out int volumeMinZ,
                        out int volumeMaxZ))
                    return Invalid;
                return new FlowFieldCellRect
                {
                    MinX = volumeMinX,
                    MaxX = volumeMaxX,
                    MinY = volumeMinY,
                    MaxY = volumeMaxY,
                    MinZ = volumeMinZ,
                    MaxZ = volumeMaxZ,
                };
            }

            if (!grid.TryGetOverlappingCells(
                    worldBounds,
                    out int minX,
                    out int maxX,
                    out int minZ,
                    out int maxZ))
                return Invalid;

            return new FlowFieldCellRect
            {
                MinX = minX,
                MaxX = maxX,
                MinY = 0,
                MaxY = grid.Height - 1,
                MinZ = minZ,
                MaxZ = maxZ,
            };
        }

        public FlowFieldCellRect Expand(FlowFieldGridSpace grid, int ring)
        {
            if (!IsValid || !grid.IsValid)
                return Invalid;

            return new FlowFieldCellRect
            {
                MinX = Mathf.Max(0, MinX - ring),
                MaxX = Mathf.Min(grid.Width - 1, MaxX + ring),
                MinY = Mathf.Max(0, MinY - ring),
                MaxY = Mathf.Min(grid.Height - 1, MaxY + ring),
                MinZ = Mathf.Max(0, MinZ - ring),
                MaxZ = Mathf.Min(grid.Depth - 1, MaxZ + ring),
            };
        }

        public static FlowFieldCellRect Union(FlowFieldCellRect left, FlowFieldCellRect right)
        {
            if (!left.IsValid)
                return right;
            if (!right.IsValid)
                return left;

            return new FlowFieldCellRect
            {
                MinX = Math.Min(left.MinX, right.MinX),
                MaxX = Math.Max(left.MaxX, right.MaxX),
                MinY = Math.Min(left.MinY, right.MinY),
                MaxY = Math.Max(left.MaxY, right.MaxY),
                MinZ = Math.Min(left.MinZ, right.MinZ),
                MaxZ = Math.Max(left.MaxZ, right.MaxZ),
            };
        }

        public static FlowFieldCellRect Intersect(FlowFieldCellRect left, FlowFieldCellRect right)
        {
            if (!left.IsValid || !right.IsValid)
                return Invalid;
            var rect = new FlowFieldCellRect
            {
                MinX = Math.Max(left.MinX, right.MinX),
                MaxX = Math.Min(left.MaxX, right.MaxX),
                MinY = Math.Max(left.MinY, right.MinY),
                MaxY = Math.Min(left.MaxY, right.MaxY),
                MinZ = Math.Max(left.MinZ, right.MinZ),
                MaxZ = Math.Min(left.MaxZ, right.MaxZ),
            };
            return rect.IsValid ? rect : Invalid;
        }

        /// <summary>Whether the rect covers every cell of the grid.</summary>
        public bool Covers(FlowFieldGridSpace grid)
            => IsValid
                && MinX <= 0 && MaxX >= grid.Width - 1
                && MinY <= 0 && MaxY >= grid.Height - 1
                && MinZ <= 0 && MaxZ >= grid.Depth - 1;

    }
}
