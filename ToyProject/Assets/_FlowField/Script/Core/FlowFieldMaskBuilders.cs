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
            FlowFieldSurfaceData surface,
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
            if (!surface.IsValid)
                throw new ArgumentException("Surface bake data is invalid.", nameof(surface));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (destination.Length != grid.CellCount)
                throw new ArgumentException("Destination length must match the grid cell count.", nameof(destination));

            if (syncTransformsBeforeQuery)
                Physics.SyncTransforms();

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
            FlowFieldSurfaceData surface,
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
            if (!surface.IsValid)
                throw new ArgumentException("Surface bake data is invalid.", nameof(surface));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (destination.Length != grid.CellCount)
                throw new ArgumentException("Destination length must match the grid cell count.", nameof(destination));

            if (syncTransformsBeforeQuery)
                Physics.SyncTransforms();

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

    internal static class FlowFieldModifierMaskBuilder
    {
        public const string TriggerRequiredMessage = "Influence Collider는 Trigger여야 합니다.";
        public const string ConvexMeshRequiredMessage = "Trigger MeshCollider는 Convex여야 합니다.";

        public static bool Build(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            Collider influenceCollider,
            float checkHeight,
            float centerOffset,
            bool[] destination,
            ref Collider[] overlapBuffer,
            bool syncTransformsBeforeQuery = false)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Grid is invalid.", nameof(grid));
            if (!surface.IsValid)
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

    /// <summary>
    /// 특정 Collider 대상 Overlap 검증 NonAlloc 쿼리 헬퍼.
    /// 버퍼가 가득 차면 MAX_HITS까지 확장하며, 상한을 초과하면 계약 위반으로 예외를 던집니다.
    /// </summary>
    internal static class FlowFieldOverlapUtility
    {
        private const int INITIAL_OVERLAP_CAPACITY = 8;
        private const int MAX_OVERLAP_HITS = 256;

        public static bool OverlapsTarget(
            Vector3 center,
            Vector3 halfExtents,
            int layerMask,
            Collider target,
            QueryTriggerInteraction triggerInteraction,
            ref Collider[] overlapBuffer)
        {
            ResizeOverlapBuffer(ref overlapBuffer, INITIAL_OVERLAP_CAPACITY);
            while (true)
            {
                int count = Physics.OverlapBoxNonAlloc(
                    center,
                    halfExtents,
                    overlapBuffer,
                    Quaternion.identity,
                    layerMask,
                    triggerInteraction);

                for (int i = 0; i < count; i++)
                {
                    if (overlapBuffer[i] == target)
                        return true;
                }

                if (count < overlapBuffer.Length)
                    return false;

                if (overlapBuffer.Length >= MAX_OVERLAP_HITS)
                    throw new System.InvalidOperationException(
                        $"Target overlap count reached the configured capacity of {MAX_OVERLAP_HITS}.");

                ResizeOverlapBuffer(ref overlapBuffer, overlapBuffer.Length * 2);
            }
        }

        private static void ResizeOverlapBuffer(ref Collider[] overlapBuffer, int minimumCapacity)
        {
            int cappedCapacity = Mathf.Min(
                Mathf.Max(INITIAL_OVERLAP_CAPACITY, minimumCapacity),
                MAX_OVERLAP_HITS);
            if (overlapBuffer != null && overlapBuffer.Length >= cappedCapacity)
                return;

            overlapBuffer = new Collider[cappedCapacity];
        }
    }
}
