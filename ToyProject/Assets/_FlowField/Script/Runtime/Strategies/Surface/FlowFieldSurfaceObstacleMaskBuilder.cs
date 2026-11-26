using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The Surface2D obstacle masks: the static and the registered Colliders a cell's check box overlaps.</summary>
    internal static class FlowFieldSurfaceObstacleMaskBuilder
    {
        private const int InitialOverlapCapacity = 32;
        private const int MaxOverlapHits = FlowFieldOverlapUtility.MaxOverlapHits;

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
            IFlowFieldObstacleView registeredObstacles,
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

        /// <summary>
        /// Writes only the registered obstacles into the cells of
        /// <paramref name="region"/>, each Collider by its own layer whether or
        /// not the obstacle layer holds it; those cells start empty and the
        /// other cells keep their values. A destroyed Collider is skipped.
        /// </summary>
        public static void BuildRegistered(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool[] destination,
            FlowFieldCellRect region,
            IFlowFieldObstacleView registered,
            ref Collider[] overlapBuffer)
        {
            FlowFieldSurfaceMaskArguments.ValidateTarget(grid, surface, destination);
            if (registered == null)
                throw new ArgumentNullException(nameof(registered));
            FlowFieldSurfaceMaskArguments.ValidateObstacleBox(checkHeight, centerOffset, clearance);
            region = FlowFieldCellRect.Intersect(region, FlowFieldCellRect.Full(grid));
            if (!region.IsValid)
                return;
            for (int z = region.MinZ; z <= region.MaxZ; z++)
                Array.Clear(destination, grid.ToFlatIndex(region.MinX, z), region.MaxX - region.MinX + 1);
            Vector3 cellHalfExtents = CellHalfExtents(grid, checkHeight, clearance);
            var policy = new RegisteredObstaclePolicy { TargetBuffer = overlapBuffer };
            for (int i = 0; i < registered.Count; i++)
            {
                Collider obstacle = registered.GetCollider(i);
                if (!policy.Accepts(obstacle))
                    continue;
                Bounds bounds = obstacle.bounds;
                if (!FlowFieldCellRect.Intersect(Footprint(grid, bounds, cellHalfExtents), region).IsValid)
                    continue;
                MarkCells(grid, surface, region, centerOffset, cellHalfExtents,
                    1 << obstacle.gameObject.layer, destination, obstacle, bounds, ref policy);
            }
            overlapBuffer = policy.TargetBuffer;
        }

        /// <summary>
        /// The cells an obstacle with these bounds can block: those whose query
        /// box reaches the bounds. Invalid when none of the grid's cells can.
        /// </summary>
        internal static FlowFieldCellRect Footprint(
            FlowFieldGridSpace grid,
            Bounds obstacleBounds,
            float checkHeight,
            float clearance)
            => Footprint(grid, obstacleBounds, CellHalfExtents(grid, checkHeight, clearance));

        private static FlowFieldCellRect Footprint(FlowFieldGridSpace grid, Bounds obstacleBounds, Vector3 cellHalfExtents)
        {
            obstacleBounds.Expand(new Vector3(cellHalfExtents.x * 2f, 0f, cellHalfExtents.z * 2f));
            if (!grid.TryGetOverlappingCells(
                    obstacleBounds,
                    out int minX,
                    out int maxX,
                    out int minZ,
                    out int maxZ))
                return FlowFieldCellRect.Invalid;
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
            FlowFieldSurfaceMaskArguments.ValidateTarget(grid, surface, destination);
            region = FlowFieldCellRect.Intersect(region, FlowFieldCellRect.Full(grid));
            if (!region.IsValid)
                return;

            for (int z = region.MinZ; z <= region.MaxZ; z++)
                Array.Clear(destination, grid.ToFlatIndex(region.MinX, z), region.MaxX - region.MinX + 1);
            if (obstacleLayer.value == 0)
                return;

            FlowFieldSurfaceMaskArguments.ValidateObstacleBox(checkHeight, centerOffset, clearance);
            float halfHeight = checkHeight * 0.5f;
            Vector3 cellHalfExtents = CellHalfExtents(grid, checkHeight, clearance);
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

            for (int hitIndex = 0; hitIndex < hitCount; hitIndex++)
            {
                Collider obstacle = overlapBuffer[hitIndex];
                if (policy.Accepts(obstacle))
                    MarkCells(grid, surface, region, centerOffset, cellHalfExtents,
                        obstacleLayer, destination, obstacle, obstacle.bounds, ref policy);
            }
        }

        // A cell's query box: its own footprint grown by the clearance, Check Height tall.
        private static Vector3 CellHalfExtents(FlowFieldGridSpace grid, float checkHeight, float clearance)
        {
            float halfXZ = FlowFieldOverlapUtility.CellHalfExtent(grid.CellSize, clearance);
            return new Vector3(halfXZ, checkHeight * 0.5f, halfXZ);
        }

        // Blocks the open cells of the region whose query box overlaps the obstacle.
        private static void MarkCells<TPolicy>(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldCellRect region,
            float centerOffset,
            Vector3 cellHalfExtents,
            LayerMask queryLayer,
            bool[] destination,
            Collider obstacle,
            Bounds obstacleBounds,
            ref TPolicy policy)
            where TPolicy : struct, IObstaclePolicy
        {
            FlowFieldCellRect footprint = Footprint(grid, obstacleBounds, cellHalfExtents);
            if (!footprint.IsValid)
                return;
            int minX = Math.Max(footprint.MinX, region.MinX);
            int maxX = Math.Min(footprint.MaxX, region.MaxX);
            int minZ = Math.Max(footprint.MinZ, region.MinZ);
            int maxZ = Math.Min(footprint.MaxZ, region.MaxZ);

            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    int index = grid.ToFlatIndex(x, z);
                    if (!surface.IsSurfaceValid(index) || destination[index])
                        continue;

                    Vector3 center = surface.GetCellCenter(grid, index);
                    center.y += centerOffset;
                    destination[index] = policy.Overlaps(center, cellHalfExtents, queryLayer, obstacle);
                }
            }
        }

        private interface IObstaclePolicy
        {
            bool Accepts(Collider obstacle);
            bool Overlaps(Vector3 center, Vector3 halfExtents, LayerMask queryLayer, Collider obstacle);
        }

        private struct AnyObstaclePolicy : IObstaclePolicy
        {
            public bool Accepts(Collider obstacle) => obstacle != null;

            public bool Overlaps(Vector3 center, Vector3 halfExtents, LayerMask queryLayer, Collider obstacle)
                => Physics.CheckBox(
                    center,
                    halfExtents,
                    Quaternion.identity,
                    queryLayer,
                    QueryTriggerInteraction.Ignore);
        }

        private struct StaticObstaclePolicy : IObstaclePolicy
        {
            public Collider[] TargetBuffer;
            public IFlowFieldObstacleView Registered;

            // GameObject.isStatic is always false in a Player, so it cannot
            // decide this. A Collider without a Rigidbody is what the physics
            // engine itself treats as static.
            public bool Accepts(Collider obstacle)
                => obstacle != null
                    && obstacle.attachedRigidbody == null
                    && !Registered.Contains(obstacle);

            public bool Overlaps(Vector3 center, Vector3 halfExtents, LayerMask queryLayer, Collider obstacle)
                => FlowFieldOverlapUtility.OverlapsTarget(
                    center,
                    halfExtents,
                    queryLayer,
                    obstacle,
                    QueryTriggerInteraction.Ignore,
                    ref TargetBuffer);
        }

        // A registered obstacle. The cheap CheckBox skips the cells nothing
        // overlaps before the query that looks for this Collider.
        private struct RegisteredObstaclePolicy : IObstaclePolicy
        {
            public Collider[] TargetBuffer;

            public bool Accepts(Collider obstacle) => obstacle != null;

            public bool Overlaps(Vector3 center, Vector3 halfExtents, LayerMask queryLayer, Collider obstacle)
                => FlowFieldOverlapUtility.OverlapsSolidTarget(center, halfExtents, queryLayer, obstacle, ref TargetBuffer);
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

            int count = FlowFieldOverlapUtility.OverlapBox(
                center,
                halfExtents,
                obstacleLayer,
                ref overlapBuffer,
                InitialOverlapCapacity,
                MaxOverlapHits);
            if (count < 0)
                throw new InvalidOperationException(
                    $"Obstacle overlap count reached the limit of {MaxOverlapHits}.");
            return count;
        }

    }
}
