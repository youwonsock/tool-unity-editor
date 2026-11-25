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
                throw new ArgumentException("Surface navigation preparation requires valid Surface data.", nameof(surface));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Surface navigation preparation workspace capacity must match the grid.", nameof(workspace));
        }
    }
}
