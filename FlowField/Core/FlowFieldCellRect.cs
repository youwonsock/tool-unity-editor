using System;
using UnityEngine;

namespace Supercent.Common.FlowField
{
    internal struct FlowFieldCellRect
    {
        public int MinX;
        public int MaxX;
        public int MinZ;
        public int MaxZ;
        public bool IsValid => MinX <= MaxX && MinZ <= MaxZ;

        public static FlowFieldCellRect Invalid => new FlowFieldCellRect
        {
            MinX = 1,
            MaxX = 0,
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
                MinZ = 0,
                MaxZ = grid.Depth - 1,
            };
        }

        public static FlowFieldCellRect FromBounds(FlowFieldGridSpace grid, Bounds worldBounds)
        {
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
                MinZ = Math.Min(left.MinZ, right.MinZ),
                MaxZ = Math.Max(left.MaxZ, right.MaxZ),
            };
        }

        public bool Overlaps(FlowFieldCellRect other)
        {
            if (!IsValid || !other.IsValid)
                return false;

            return MinX <= other.MaxX
                && MaxX >= other.MinX
                && MinZ <= other.MaxZ
                && MaxZ >= other.MinZ;
        }

        public int CellCountEstimate => IsValid
            ? (MaxX - MinX + 1) * (MaxZ - MinZ + 1)
            : 0;
    }
}
