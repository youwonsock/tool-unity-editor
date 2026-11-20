using System;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What the Surface2D base stage prepares for the solve: the escape
    /// directions out of blocked cells, and the Goal cell with its influence
    /// mask and topology masks. The solve itself runs in
    /// <see cref="FlowFieldSurfaceBaseFieldBuild"/>.
    /// </summary>
    internal static class FlowFieldSurfaceNavigationPreparation
    {
        /// <summary>
        /// The escape directions out of the workspace's blocked cells
        /// (<see cref="FlowFieldSurfaceEscapeJob"/>).
        /// </summary>
        public static void BuildEscapeDirections(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace)
            => RunEscapeJob(grid, surface, workspace, combineBlocked: false);

        /// <summary>
        /// Writes Blocked = static | dynamic mask, then the escape directions,
        /// in one job.
        /// </summary>
        public static void CombineBlockedAndBuildEscapeDirections(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace)
            => RunEscapeJob(grid, surface, workspace, combineBlocked: true);

        private static unsafe void RunEscapeJob(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            bool combineBlocked)
        {
            ValidateWorkspace(grid, surface, workspace);
            workspace.AssertWritable(nameof(BuildEscapeDirections));

            var pins = new FlowFieldPinnedArrays(FlowFieldSurfaceEscapeJob.PinnedArrayCount);
            try
            {
                var job = new FlowFieldSurfaceEscapeJob
                {
                    Blocked = pins.Pin(workspace.Blocked),
                    StaticBlocked = pins.Pin(workspace.StaticBlocked),
                    DynamicBlocked = pins.Pin(workspace.DynamicBlocked),
                    SurfaceFlags = pins.Pin(surface.Flags),
                    NeighborMasks = pins.Pin(surface.NeighborMasks),
                    Heights = pins.Pin(surface.Heights),
                    EscapeDirections = pins.Pin(workspace.EscapeDirections),
                    Costs = pins.Pin(workspace.Costs),
                    Queue = pins.Pin(workspace.Queue),
                    Width = grid.Width,
                    Depth = grid.Depth,
                    CellSize = grid.CellSize,
                    Origin = grid.Origin,
                    CombineBlocked = combineBlocked,
                    DirectionEpsilonSqr = FlowFieldVectorUtility.DirectionEpsilonSqr,
                };
                for (int direction = 0; direction < FlowFieldSurfaceEscapeJob.FaceCount; direction++)
                {
                    job.DeltaX[direction] = FlowFieldNeighborUtility.DeltaX[direction];
                    job.DeltaZ[direction] = FlowFieldNeighborUtility.DeltaZ[direction];
                }
                job.Run();
            }
            finally
            {
                pins.Release();
            }
        }

        public static bool PrepareGoal(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            int requestedGoalX,
            int requestedGoalZ,
            float influenceRadius,
            out int resolvedGoalIndex)
        {
            resolvedGoalIndex = -1;
            ValidateWorkspace(grid, surface, workspace);
            if (!grid.IsLocalInBounds(requestedGoalX, requestedGoalZ))
                throw new ArgumentOutOfRangeException(nameof(requestedGoalX), "Requested Goal cell is outside the grid.");
            if (float.IsNaN(influenceRadius) || float.IsInfinity(influenceRadius) || influenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(influenceRadius));

            workspace.ClearGoal();
            int surfaceAnchorIndex = FlowFieldSurfaceGraphTraversal.FindNearestSurfaceAnchor(
                grid,
                surface,
                requestedGoalX,
                requestedGoalZ);
            if (surfaceAnchorIndex < 0)
                return false;

            BuildInfluenceMask(grid, surface, workspace, surfaceAnchorIndex, influenceRadius);
            resolvedGoalIndex = FlowFieldSurfaceGraphTraversal.FindNearestWalkableGoal(
                grid,
                surface,
                workspace,
                surfaceAnchorIndex);
            if (resolvedGoalIndex < 0)
                return false;

            workspace.SetResolvedGoal(resolvedGoalIndex);
            FlowFieldSurfaceGraphTraversal.BuildTopologyMasks(grid, surface, workspace);
            return true;
        }

        private static void BuildInfluenceMask(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            int anchorIndex,
            float influenceRadius)
        {
            unsafe
            {
                var pins = new FlowFieldPinnedArrays(3);
                try
                {
                    new FlowFieldSurfaceInfluenceJob
                    {
                        Influence = pins.Pin(workspace.InfluenceMask),
                        SurfaceFlags = pins.Pin(surface.Flags),
                        Heights = pins.Pin(surface.Heights),
                        Width = grid.Width,
                        Depth = grid.Depth,
                        CellSize = grid.CellSize,
                        Origin = grid.Origin,
                        Anchor = surface.GetCellCenter(grid, anchorIndex),
                        Radius = influenceRadius,
                    }.Run();
                }
                finally
                {
                    pins.Release();
                }
            }

            if (influenceRadius <= 0f)
                return;

            workspace.InfluenceMask[anchorIndex] = true;
        }

        private static void ValidateWorkspace(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Surface navigation preparation requires a valid grid.", nameof(grid));
            if (surface == null || !surface.IsValid)
                throw new ArgumentException("Surface navigation preparation requires a valid surface bake.", nameof(surface));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Surface navigation preparation workspace capacity must match the grid.", nameof(workspace));
        }
    }

    /// <summary>
    /// Graph helpers the navigation preparation, the base build and the
    /// solve check share.
    /// </summary>
    internal static class FlowFieldSurfaceGraphTraversal
    {
        private const float DistanceTieEpsilon = 0.000001f;

        public static void BuildTopologyMasks(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace)
        {
            if (!grid.IsValid)
                throw new System.ArgumentException("Topology mask requires a valid grid.", nameof(grid));
            if (surface == null || !surface.IsValid)
                throw new System.ArgumentException("Topology mask requires a valid surface bake.", nameof(surface));
            if (workspace == null)
                throw new System.ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount
                || workspace.TopologyMasks == null
                || workspace.TopologyMasks.Length != grid.CellCount)
                throw new System.ArgumentException("Topology mask workspace capacity must match the grid.", nameof(workspace));
            workspace.AssertWritable(nameof(BuildTopologyMasks));

            unsafe
            {
                var pins = new FlowFieldPinnedArrays(5);
                try
                {
                    var job = new FlowFieldSurfaceTopologyJob
                    {
                        Topology = pins.Pin(workspace.TopologyMasks),
                        Blocked = pins.Pin(workspace.Blocked),
                        Influence = pins.Pin(workspace.InfluenceMask),
                        SurfaceFlags = pins.Pin(surface.Flags),
                        NeighborMasks = pins.Pin(surface.NeighborMasks),
                        Width = grid.Width,
                        Depth = grid.Depth,
                    };
                    for (int direction = 0; direction < FlowFieldNeighborUtility.SurfaceCount; direction++)
                    {
                        job.DeltaX[direction] = FlowFieldNeighborUtility.DeltaX[direction];
                        job.DeltaZ[direction] = FlowFieldNeighborUtility.DeltaZ[direction];
                    }
                    job.Run();
                }
                finally
                {
                    pins.Release();
                }
            }
        }

        /// <summary>
        /// Sets the non-goal NextCell sentinels in the Goal/base stage. Final
        /// composition only transforms vectors and modifiers; it must never
        /// rewrite navigation topology.
        /// </summary>
        internal static void ApplyNoGoalSentinels(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace)
        {
            if (!grid.IsValid || surface == null || !surface.IsValid
                || workspace == null || workspace.Capacity != grid.CellCount)
                throw new ArgumentException("No-goal sentinel input is invalid.");
            workspace.AssertWritable(nameof(ApplyNoGoalSentinels));
            for (int index = 0; index < grid.CellCount; index++)
            {
                workspace.NextCells[index] = !surface.IsSurfaceValid(index)
                    || workspace.Blocked[index]
                    ? -2
                    : -1;
            }
        }

        public static int FindNearestSurfaceAnchor(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            int requestedGoalX,
            int requestedGoalZ)
        {
            int requested = grid.ToFlatIndex(requestedGoalX, requestedGoalZ);
            if (surface.IsSurfaceValid(requested))
                return requested;

            int bestIndex = -1;
            long bestDistanceSqr = long.MaxValue;
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (!surface.IsSurfaceValid(index))
                    continue;

                grid.FromFlatIndex(index, out int x, out int z);
                long dx = x - (long)requestedGoalX;
                long dz = z - (long)requestedGoalZ;
                long distanceSqr = dx * dx + dz * dz;
                if (distanceSqr < bestDistanceSqr
                    || distanceSqr == bestDistanceSqr && index < bestIndex)
                {
                    bestIndex = index;
                    bestDistanceSqr = distanceSqr;
                }
            }

            return bestIndex;
        }

        public static int FindNearestWalkableGoal(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            int anchorIndex)
        {
            if (workspace.InfluenceMask[anchorIndex] && !workspace.Blocked[anchorIndex])
                return anchorIndex;

            Vector3 anchor = surface.GetCellCenter(grid, anchorIndex);
            int bestIndex = -1;
            float bestDistanceSqr = float.PositiveInfinity;
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (!workspace.InfluenceMask[index]
                    || !surface.IsSurfaceValid(index)
                    || workspace.Blocked[index])
                    continue;

                float distanceSqr = (surface.GetCellCenter(grid, index) - anchor).sqrMagnitude;
                bool replace = distanceSqr < bestDistanceSqr - DistanceTieEpsilon
                    || Mathf.Abs(distanceSqr - bestDistanceSqr) <= DistanceTieEpsilon
                        && index < bestIndex;
                if (replace)
                {
                    bestIndex = index;
                    bestDistanceSqr = distanceSqr;
                }
            }

            return bestIndex;
        }

        public static bool IsCellTraversable(
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            int index)
            => index >= 0
                && index < workspace.Capacity
                && surface.IsSurfaceValid(index)
                && workspace.InfluenceMask[index]
                && !workspace.Blocked[index];
    }
}
