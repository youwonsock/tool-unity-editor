using System;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
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
                throw new System.ArgumentException("Topology mask requires valid Surface data.", nameof(surface));
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
                    ? FlowFieldNextCell.Blocked
                    : FlowFieldNextCell.DefaultDirection;
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
