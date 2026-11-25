using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The Surface2D cells a Modifier's Influence Collider touches, in the obstacle check box.</summary>
    internal static class FlowFieldSurfaceModifierMaskBuilder
    {
        /// <summary>
        /// Writes the cells the Influence Collider touches; every other cell is
        /// cleared. Returns the rectangle of cells it queried: no cell outside
        /// it is set. Invalid when it queried none.
        /// </summary>
        public static FlowFieldCellRect Build(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            Collider influenceCollider,
            float checkHeight,
            float centerOffset,
            bool[] destination,
            ref Collider[] overlapBuffer)
        {
            FlowFieldSurfaceMaskArguments.ValidateTarget(grid, surface, destination);

            Array.Clear(destination, 0, destination.Length);
            if (!FlowFieldModifierInfluence.IsUsableTrigger(influenceCollider))
                return FlowFieldCellRect.Invalid;

            Bounds colliderBounds = influenceCollider.bounds;
            if (!FlowFieldGridSpace.IsFinite(checkHeight) || checkHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(checkHeight));
            float halfHeight = checkHeight * 0.5f;
            if (!FlowFieldGridSpace.IsFinite(centerOffset))
                throw new ArgumentOutOfRangeException(nameof(centerOffset));
            if (!surface.TryGetHeightRange(out float minSurfaceY, out float maxSurfaceY))
                return FlowFieldCellRect.Invalid;

            float checkMinY = minSurfaceY + centerOffset - halfHeight;
            float checkMaxY = maxSurfaceY + centerOffset + halfHeight;
            if (colliderBounds.max.y < checkMinY || colliderBounds.min.y > checkMaxY)
                return FlowFieldCellRect.Invalid;

            ResolveCandidateRange(
                grid,
                colliderBounds,
                out int minX,
                out int maxX,
                out int minZ,
                out int maxZ);
            if (minX > maxX || minZ > maxZ)
                return FlowFieldCellRect.Invalid;

            float halfXZ = FlowFieldOverlapUtility.CellHalfExtent(grid.CellSize);
            Vector3 halfExtents = new Vector3(halfXZ, halfHeight, halfXZ);
            int layerMask = 1 << influenceCollider.gameObject.layer;

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int index = grid.ToFlatIndex(x, z);
                    if (!surface.IsSurfaceValid(index))
                        continue;

                    Vector3 center = surface.GetCellCenter(grid, index);
                    center.y += centerOffset;
                    destination[index] = FlowFieldOverlapUtility.OverlapsTarget(
                        center,
                        halfExtents,
                        layerMask,
                        influenceCollider,
                        QueryTriggerInteraction.Collide,
                        ref overlapBuffer);
                }
            }
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

        private static void ResolveCandidateRange(
            FlowFieldGridSpace grid,
            Bounds bounds,
            out int minX,
            out int maxX,
            out int minZ,
            out int maxZ)
        {
            float inverseCellSize = 1f / grid.CellSize;
            minX = Mathf.Clamp(
                Mathf.FloorToInt((bounds.min.x - grid.Origin.x) * inverseCellSize) - 1,
                0,
                grid.Width - 1);
            maxX = Mathf.Clamp(
                Mathf.FloorToInt((bounds.max.x - grid.Origin.x) * inverseCellSize),
                0,
                grid.Width - 1);
            minZ = Mathf.Clamp(
                Mathf.FloorToInt((bounds.min.z - grid.Origin.z) * inverseCellSize) - 1,
                0,
                grid.Depth - 1);
            maxZ = Mathf.Clamp(
                Mathf.FloorToInt((bounds.max.z - grid.Origin.z) * inverseCellSize),
                0,
                grid.Depth - 1);

            float gridMaxX = grid.Origin.x + grid.WorldSizeX;
            float gridMaxZ = grid.Origin.z + grid.WorldSizeZ;
            if (bounds.max.x < grid.Origin.x
                || bounds.min.x > gridMaxX
                || bounds.max.z < grid.Origin.z
                || bounds.min.z > gridMaxZ)
            {
                minX = 1;
                maxX = 0;
                minZ = 1;
                maxZ = 0;
            }
        }
    }
}
