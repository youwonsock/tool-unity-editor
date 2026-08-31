using System;
using UnityEngine;

namespace Common.FlowField
{
    internal static class FlowFieldModifierMaskBuilder
    {
        public const string TriggerRequiredMessage = "Influence Collider는 Trigger여야 합니다.";
        public const string ConvexMeshRequiredMessage = "Trigger MeshCollider는 Convex여야 합니다.";

        public static bool Build(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            Collider influenceCollider,
            float checkHeight,
            float centerOffset,
            bool[] destination,
            ref Collider[] overlapBuffer,
            bool syncTransformsBeforeQuery = false)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Grid is invalid.", nameof(grid));
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            if (!surface.HasValidData)
                throw new ArgumentException("Surface bake data is invalid.", nameof(surface));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (destination.Length != grid.CellCount)
                throw new ArgumentException("Destination length must match the grid cell count.", nameof(destination));

            Array.Clear(destination, 0, destination.Length);
            if (!IsUsableTrigger(influenceCollider))
                return true;

            if (syncTransformsBeforeQuery)
                Physics.SyncTransforms();

            Bounds colliderBounds = influenceCollider.bounds;
            if (!FlowFieldGridSpace.IsFinite(checkHeight) || checkHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(checkHeight));
            float halfHeight = checkHeight * 0.5f;
            if (!FlowFieldGridSpace.IsFinite(centerOffset))
                throw new ArgumentOutOfRangeException(nameof(centerOffset));
            if (!surface.TryGetHeightRange(out float minSurfaceY, out float maxSurfaceY))
                return true;

            float checkMinY = minSurfaceY + centerOffset - halfHeight;
            float checkMaxY = maxSurfaceY + centerOffset + halfHeight;
            if (colliderBounds.max.y < checkMinY || colliderBounds.min.y > checkMaxY)
                return true;

            ResolveCandidateRange(
                grid,
                colliderBounds,
                out int minX,
                out int maxX,
                out int minZ,
                out int maxZ);
            if (minX > maxX || minZ > maxZ)
                return true;

            Vector3 halfExtents = new Vector3(grid.CellSize * 0.5f, halfHeight, grid.CellSize * 0.5f);
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

            return true;
        }

        public static bool IsUsableTrigger(Collider influenceCollider)
            => influenceCollider != null
                && influenceCollider.enabled
                && influenceCollider.gameObject.activeInHierarchy
                && influenceCollider.isTrigger
                && (!(influenceCollider is MeshCollider meshCollider) || meshCollider.convex);

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
