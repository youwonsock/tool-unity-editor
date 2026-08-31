using System;
using UnityEngine;

namespace Common.FlowField
{
    internal static class FlowFieldObstacleMaskBuilder
    {
        private const int INITIAL_OVERLAP_CAPACITY = 32;
        private const int MAX_OVERLAP_HITS = 4096;

        public static bool Build(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
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

#if UNITY_EDITOR
            if (syncTransformsBeforeQuery)
                Physics.SyncTransforms();
#endif

            Array.Clear(destination, 0, destination.Length);
            if (obstacleLayer.value == 0)
                return true;

            if (!FlowFieldGridSpace.IsFinite(checkHeight) || checkHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(checkHeight));
            if (!FlowFieldGridSpace.IsFinite(centerOffset))
                throw new ArgumentOutOfRangeException(nameof(centerOffset));
            if (!FlowFieldGridSpace.IsFinite(clearance) || clearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(clearance));
            float halfHeight = checkHeight * 0.5f;
            float halfXZ = grid.CellSize * 0.5f + clearance;
            Vector3 cellHalfExtents = new Vector3(halfXZ, halfHeight, halfXZ);
            if (!surface.TryGetHeightRange(out float minSurfaceY, out float maxSurfaceY))
                return true;

            int hitCount = QueryOverlappingObstacles(
                grid,
                obstacleLayer,
                minSurfaceY + centerOffset,
                maxSurfaceY + centerOffset,
                halfHeight,
                clearance,
                ref overlapBuffer);
            if (hitCount <= 0)
                return true;

            Vector3 boundsExpand = new Vector3(halfXZ * 2f, 0f, halfXZ * 2f);
            for (int hitIndex = 0; hitIndex < hitCount; hitIndex++)
            {
                Collider obstacle = overlapBuffer[hitIndex];
                if (obstacle == null)
                    continue;

                Bounds candidateBounds = obstacle.bounds;
                candidateBounds.Expand(boundsExpand);
                if (!grid.TryGetOverlappingCells(
                        candidateBounds,
                        out int minX,
                        out int maxX,
                        out int minZ,
                        out int maxZ))
                    continue;

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        int index = grid.ToFlatIndex(x, z);
                        if (!surface.IsSurfaceValid(index) || destination[index])
                            continue;

                        Vector3 center = surface.GetCellCenter(grid, index);
                        center.y += centerOffset;
                        destination[index] = Physics.CheckBox(
                            center,
                            cellHalfExtents,
                            Quaternion.identity,
                            obstacleLayer,
                            QueryTriggerInteraction.Ignore);
                    }
                }
            }

            return true;
        }

        public static bool BuildStatic(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool[] destination,
            ref Collider[] overlapBuffer,
            ref Collider[] targetOverlapBuffer,
            out int excludedColliderCount,
            bool syncTransformsBeforeQuery = false)
        {
            excludedColliderCount = 0;
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

#if UNITY_EDITOR
            if (syncTransformsBeforeQuery)
                Physics.SyncTransforms();
#endif

            Array.Clear(destination, 0, destination.Length);
            if (obstacleLayer.value == 0)
                return true;

            if (!FlowFieldGridSpace.IsFinite(checkHeight) || checkHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(checkHeight));
            if (!FlowFieldGridSpace.IsFinite(centerOffset))
                throw new ArgumentOutOfRangeException(nameof(centerOffset));
            if (!FlowFieldGridSpace.IsFinite(clearance) || clearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(clearance));
            float halfHeight = checkHeight * 0.5f;
            float halfXZ = grid.CellSize * 0.5f + clearance;
            Vector3 cellHalfExtents = new Vector3(halfXZ, halfHeight, halfXZ);
            if (!surface.TryGetHeightRange(out float minSurfaceY, out float maxSurfaceY))
                return true;

            int hitCount = QueryOverlappingObstacles(
                grid,
                obstacleLayer,
                minSurfaceY + centerOffset,
                maxSurfaceY + centerOffset,
                halfHeight,
                clearance,
                ref overlapBuffer);
            if (hitCount <= 0)
                return true;

            Vector3 boundsExpand = new Vector3(halfXZ * 2f, 0f, halfXZ * 2f);
            for (int hitIndex = 0; hitIndex < hitCount; hitIndex++)
            {
                Collider obstacle = overlapBuffer[hitIndex];
                if (!IsStaticBakeCollider(obstacle))
                {
                    if (obstacle != null)
                        excludedColliderCount++;
                    continue;
                }

                Bounds candidateBounds = obstacle.bounds;
                candidateBounds.Expand(boundsExpand);
                if (!grid.TryGetOverlappingCells(
                        candidateBounds,
                        out int minX,
                        out int maxX,
                        out int minZ,
                        out int maxZ))
                    continue;

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        int index = grid.ToFlatIndex(x, z);
                        if (!surface.IsSurfaceValid(index) || destination[index])
                            continue;

                        Vector3 center = surface.GetCellCenter(grid, index);
                        center.y += centerOffset;
                        destination[index] = FlowFieldOverlapUtility.OverlapsTarget(
                            center,
                            cellHalfExtents,
                            obstacleLayer,
                            obstacle,
                            QueryTriggerInteraction.Ignore,
                            ref targetOverlapBuffer);
                    }
                }
            }

            return true;
        }

        public static bool AreEqual(bool[] left, bool[] right)
        {
            if (left == null)
                throw new ArgumentNullException(nameof(left));
            if (right == null)
                throw new ArgumentNullException(nameof(right));
            if (left.Length != right.Length)
                throw new ArgumentException("Compared obstacle masks must have equal lengths.", nameof(right));

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                    return false;
            }

            return true;
        }

        private static int QueryOverlappingObstacles(
            FlowFieldGridSpace grid,
            LayerMask obstacleLayer,
            float minCenterY,
            float maxCenterY,
            float halfHeight,
            float clearance,
            ref Collider[] overlapBuffer)
        {
            ResizeOverlapBuffer(ref overlapBuffer, INITIAL_OVERLAP_CAPACITY);
            Vector3 center = grid.Origin + new Vector3(grid.WorldSizeX * 0.5f, 0f, grid.WorldSizeZ * 0.5f);
            center.y = (minCenterY + maxCenterY) * 0.5f;
            Vector3 halfExtents = new Vector3(
                grid.WorldSizeX * 0.5f + clearance,
                (maxCenterY - minCenterY) * 0.5f + halfHeight,
                grid.WorldSizeZ * 0.5f + clearance);

            while (true)
            {
                int count = Physics.OverlapBoxNonAlloc(
                    center,
                    halfExtents,
                    overlapBuffer,
                    Quaternion.identity,
                    obstacleLayer,
                    QueryTriggerInteraction.Ignore);
                if (count < overlapBuffer.Length)
                {
                    return count;
                }

                if (overlapBuffer.Length >= MAX_OVERLAP_HITS)
                {
                    throw new InvalidOperationException(
                        $"Obstacle overlap count reached the configured capacity of {MAX_OVERLAP_HITS}.");
                }

                ResizeOverlapBuffer(ref overlapBuffer, overlapBuffer.Length * 2);
            }
        }

        private static bool IsStaticBakeCollider(Collider collider)
            => collider != null
                && collider.gameObject.isStatic
                && collider.attachedRigidbody == null;

        private static void ResizeOverlapBuffer(ref Collider[] overlapBuffer, int minimumCapacity)
        {
            int cappedCapacity = Mathf.Min(Mathf.Max(INITIAL_OVERLAP_CAPACITY, minimumCapacity), MAX_OVERLAP_HITS);
            if (overlapBuffer != null && overlapBuffer.Length >= cappedCapacity)
                return;

            overlapBuffer = new Collider[cappedCapacity];
        }
    }
}
