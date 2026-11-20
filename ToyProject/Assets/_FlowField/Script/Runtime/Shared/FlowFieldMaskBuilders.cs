using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal static class FlowFieldObstacleMaskBuilder
    {
        private const int InitialOverlapCapacity = 32;
        private const int MaxOverlapHits = 4096;

        /// <summary>
        /// Writes every obstacle Collider of the layer into the cells of
        /// <paramref name="region"/>; the other cells keep their values.
        /// </summary>
        public static void Build(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool[] destination,
            FlowFieldCellRect region,
            ref Collider[] overlapBuffer)
        {
            var policy = new AnyObstaclePolicy();
            Scan(
                grid,
                surface,
                obstacleLayer,
                checkHeight,
                centerOffset,
                clearance,
                destination,
                region,
                ref overlapBuffer,
                ref policy);
        }

        /// <summary>
        /// Writes the static obstacles, Colliders without a Rigidbody that are
        /// not registered, into the cells of <paramref name="region"/>; the
        /// other cells keep their values.
        /// </summary>
        public static void BuildStatic(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool[] destination,
            FlowFieldCellRect region,
            HashSet<Collider> registeredObstacles,
            ref Collider[] overlapBuffer,
            ref Collider[] targetOverlapBuffer)
        {
            var policy = new StaticObstaclePolicy
            {
                TargetBuffer = targetOverlapBuffer,
                Registered = registeredObstacles,
            };
            Scan(
                grid,
                surface,
                obstacleLayer,
                checkHeight,
                centerOffset,
                clearance,
                destination,
                region,
                ref overlapBuffer,
                ref policy);
            targetOverlapBuffer = policy.TargetBuffer;
        }

        // A struct constraint keeps the per-cell query free of delegate calls.
        private static void Scan<TPolicy>(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool[] destination,
            FlowFieldCellRect region,
            ref Collider[] overlapBuffer,
            ref TPolicy policy)
            where TPolicy : struct, IObstaclePolicy
        {
            FlowFieldMaskArguments.ValidateTarget(grid, surface, destination);
            region = FlowFieldCellRect.Intersect(region, FlowFieldCellRect.Full(grid));
            if (!region.IsValid)
                return;

            for (int z = region.MinZ; z <= region.MaxZ; z++)
                Array.Clear(destination, grid.ToFlatIndex(region.MinX, z), region.MaxX - region.MinX + 1);
            if (obstacleLayer.value == 0)
                return;

            FlowFieldMaskArguments.ValidateObstacleBox(checkHeight, centerOffset, clearance);
            float halfHeight = checkHeight * 0.5f;
            float halfXZ = FlowFieldOverlapUtility.CellHalfExtent(grid.CellSize, clearance);
            Vector3 cellHalfExtents = new Vector3(halfXZ, halfHeight, halfXZ);
            if (!surface.TryGetHeightRange(out float minSurfaceY, out float maxSurfaceY))
                return;

            int hitCount = QueryOverlappingObstacles(
                grid,
                region,
                obstacleLayer,
                minSurfaceY + centerOffset,
                maxSurfaceY + centerOffset,
                halfHeight,
                clearance,
                ref overlapBuffer);
            if (hitCount <= 0)
                return;

            Vector3 boundsExpand = new Vector3(halfXZ * 2f, 0f, halfXZ * 2f);
            for (int hitIndex = 0; hitIndex < hitCount; hitIndex++)
            {
                Collider obstacle = overlapBuffer[hitIndex];
                if (!policy.Accepts(obstacle))
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
                minX = Math.Max(minX, region.MinX);
                maxX = Math.Min(maxX, region.MaxX);
                minZ = Math.Max(minZ, region.MinZ);
                maxZ = Math.Min(maxZ, region.MaxZ);

                for (int z = minZ; z <= maxZ; z++)
                {
                    for (int x = minX; x <= maxX; x++)
                    {
                        int index = grid.ToFlatIndex(x, z);
                        if (!surface.IsSurfaceValid(index) || destination[index])
                            continue;

                        Vector3 center = surface.GetCellCenter(grid, index);
                        center.y += centerOffset;
                        destination[index] = policy.Overlaps(center, cellHalfExtents, obstacleLayer, obstacle);
                    }
                }
            }
        }

        private interface IObstaclePolicy
        {
            bool Accepts(Collider obstacle);
            bool Overlaps(Vector3 center, Vector3 halfExtents, LayerMask obstacleLayer, Collider obstacle);
        }

        private struct AnyObstaclePolicy : IObstaclePolicy
        {
            public bool Accepts(Collider obstacle) => obstacle != null;

            public bool Overlaps(Vector3 center, Vector3 halfExtents, LayerMask obstacleLayer, Collider obstacle)
                => Physics.CheckBox(
                    center,
                    halfExtents,
                    Quaternion.identity,
                    obstacleLayer,
                    QueryTriggerInteraction.Ignore);
        }

        private struct StaticObstaclePolicy : IObstaclePolicy
        {
            public Collider[] TargetBuffer;
            public HashSet<Collider> Registered;

            // GameObject.isStatic is always false in a Player, so it cannot
            // decide this. A Collider without a Rigidbody is what the physics
            // engine itself treats as static.
            public bool Accepts(Collider obstacle)
                => obstacle != null
                    && obstacle.attachedRigidbody == null
                    && !Registered.Contains(obstacle);

            public bool Overlaps(Vector3 center, Vector3 halfExtents, LayerMask obstacleLayer, Collider obstacle)
                => FlowFieldOverlapUtility.OverlapsTarget(
                    center,
                    halfExtents,
                    obstacleLayer,
                    obstacle,
                    QueryTriggerInteraction.Ignore,
                    ref TargetBuffer);
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

        // Every Collider whose query box can reach a cell of the region.
        private static int QueryOverlappingObstacles(
            FlowFieldGridSpace grid,
            FlowFieldCellRect region,
            LayerMask obstacleLayer,
            float minCenterY,
            float maxCenterY,
            float halfHeight,
            float clearance,
            ref Collider[] overlapBuffer)
        {
            FlowFieldOverlapUtility.EnsureCapacity(
                ref overlapBuffer, InitialOverlapCapacity, InitialOverlapCapacity, MaxOverlapHits);
            // The same arithmetic as the grid's world size, so the whole grid
            // queries exactly its own box.
            float sizeX = (float)((double)(region.MaxX - region.MinX + 1) * grid.CellSize);
            float sizeZ = (float)((double)(region.MaxZ - region.MinZ + 1) * grid.CellSize);
            Vector3 center = grid.Origin + new Vector3(
                (float)((double)region.MinX * grid.CellSize) + sizeX * 0.5f,
                0f,
                (float)((double)region.MinZ * grid.CellSize) + sizeZ * 0.5f);
            center.y = (minCenterY + maxCenterY) * 0.5f;
            Vector3 halfExtents = new Vector3(
                sizeX * 0.5f + clearance,
                (maxCenterY - minCenterY) * 0.5f + halfHeight,
                sizeZ * 0.5f + clearance);

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

                if (overlapBuffer.Length >= MaxOverlapHits)
                {
                    throw new InvalidOperationException(
                        $"Obstacle overlap count reached the configured capacity of {MaxOverlapHits}.");
                }

                FlowFieldOverlapUtility.EnsureCapacity(
                    ref overlapBuffer, overlapBuffer.Length * 2, InitialOverlapCapacity, MaxOverlapHits);
            }
        }

    }

    internal static class FlowFieldModifierMaskBuilder
    {
        /// <summary>
        /// The one rule for an Influence Collider: present, a trigger, and
        /// convex when it is a MeshCollider. False with the first broken part.
        /// </summary>
        public static bool TryValidateInfluenceCollider(Collider collider, out string reason)
        {
            reason = string.Empty;
            if (collider == null)
                reason = "Influence Collider is required.";
            else if (!collider.isTrigger)
                reason = "Influence Collider must be a trigger.";
            else if (collider is MeshCollider meshCollider && !meshCollider.convex)
                reason = "A trigger MeshCollider must be convex.";
            return reason.Length == 0;
        }

        public static void Build(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            Collider influenceCollider,
            float checkHeight,
            float centerOffset,
            bool[] destination,
            ref Collider[] overlapBuffer)
        {
            FlowFieldMaskArguments.ValidateTarget(grid, surface, destination);

            Array.Clear(destination, 0, destination.Length);
            if (!IsUsableTrigger(influenceCollider))
                return;

            Bounds colliderBounds = influenceCollider.bounds;
            if (!FlowFieldGridSpace.IsFinite(checkHeight) || checkHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(checkHeight));
            float halfHeight = checkHeight * 0.5f;
            if (!FlowFieldGridSpace.IsFinite(centerOffset))
                throw new ArgumentOutOfRangeException(nameof(centerOffset));
            if (!surface.TryGetHeightRange(out float minSurfaceY, out float maxSurfaceY))
                return;

            float checkMinY = minSurfaceY + centerOffset - halfHeight;
            float checkMaxY = maxSurfaceY + centerOffset + halfHeight;
            if (colliderBounds.max.y < checkMinY || colliderBounds.min.y > checkMaxY)
                return;

            ResolveCandidateRange(
                grid,
                colliderBounds,
                out int minX,
                out int maxX,
                out int minZ,
                out int maxZ);
            if (minX > maxX || minZ > maxZ)
                return;

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
        }

        // A valid Influence Collider that is enabled in the scene.
        public static bool IsUsableTrigger(Collider influenceCollider)
            => TryValidateInfluenceCollider(influenceCollider, out _)
                && influenceCollider.enabled
                && influenceCollider.gameObject.activeInHierarchy;

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
    /// Non-allocating overlap queries against one target collider.
    /// A full buffer grows up to MaxOverlapHits; more hits than that throw, because a partial result would miss colliders.
    /// </summary>
    internal static class FlowFieldOverlapUtility
    {
        private const int InitialOverlapCapacity = 8;
        private const int MaxOverlapHits = 256;
        private const float CellQuerySkin = 0.0001f;

        /// <summary>
        /// Half extent of a cell's overlap box. Unity's overlap queries count a
        /// face-touching collider as an overlap; removing a tiny numerical skin
        /// keeps a collider aligned with the cell edges out of its neighbors
        /// while the configured clearance still applies to real intersections.
        /// </summary>
        internal static float CellHalfExtent(float cellSize, float clearance = 0f)
            => Mathf.Max(0.000001f, cellSize * 0.5f + clearance - CellQuerySkin);

        public static bool OverlapsTarget(
            Vector3 center,
            Vector3 halfExtents,
            int layerMask,
            Collider target,
            QueryTriggerInteraction triggerInteraction,
            ref Collider[] overlapBuffer)
        {
            EnsureCapacity(ref overlapBuffer, InitialOverlapCapacity, InitialOverlapCapacity, MaxOverlapHits);
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

                if (overlapBuffer.Length >= MaxOverlapHits)
                    throw new System.InvalidOperationException(
                        $"Target overlap count reached the configured capacity of {MaxOverlapHits}.");

                EnsureCapacity(ref overlapBuffer, overlapBuffer.Length * 2, InitialOverlapCapacity, MaxOverlapHits);
            }
        }

        /// <summary>
        /// Grows a NonAlloc query buffer to at least <paramref name="minimumCapacity"/>,
        /// clamped to [<paramref name="initialCapacity"/>, <paramref name="maxCapacity"/>].
        /// </summary>
        internal static void EnsureCapacity(
            ref Collider[] overlapBuffer,
            int minimumCapacity,
            int initialCapacity,
            int maxCapacity)
        {
            int cappedCapacity = Mathf.Min(
                Mathf.Max(initialCapacity, minimumCapacity),
                maxCapacity);
            if (overlapBuffer != null && overlapBuffer.Length >= cappedCapacity)
                return;

            overlapBuffer = new Collider[cappedCapacity];
        }
    }

    /// <summary>
    /// Argument checks shared by the Surface2D obstacle and modifier mask builders.
    /// </summary>
    internal static class FlowFieldMaskArguments
    {
        internal static void ValidateTarget(FlowFieldGridSpace grid, FlowFieldSurfaceData surface, bool[] destination)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Grid is invalid.", nameof(grid));
            if (!surface.IsValid)
                throw new ArgumentException("Surface bake data is invalid.", nameof(surface));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (destination.Length != grid.CellCount)
                throw new ArgumentException("Destination length must match the grid cell count.", nameof(destination));
        }

        internal static void ValidateObstacleBox(float checkHeight, float centerOffset, float clearance)
        {
            if (!FlowFieldGridSpace.IsFinite(checkHeight) || checkHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(checkHeight));
            if (!FlowFieldGridSpace.IsFinite(centerOffset))
                throw new ArgumentOutOfRangeException(nameof(centerOffset));
            if (!FlowFieldGridSpace.IsFinite(clearance) || clearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(clearance));
        }
    }
}
