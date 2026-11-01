using System;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    [Flags]
    internal enum FlowFieldGoalFlags : byte
    {
        None = 0,
        Directed = 1 << 0,
        Anchor = 1 << 1,
        Unreachable = 1 << 2,
    }

    internal static class FlowFieldSolver
    {
        private const int UNREACHABLE = int.MaxValue;

        public static bool BuildEscapeDirections(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace)
        {
            ValidateWorkspace(grid, surface, workspace);

            int count = grid.CellCount;
            Array.Clear(workspace.EscapeDirections, 0, count);
            int head = 0;
            int tail = 0;
            for (int index = 0; index < count; index++)
            {
                if (!surface.IsSurfaceValid(index) || workspace.Blocked[index])
                {
                    workspace.Costs[index] = UNREACHABLE;
                    continue;
                }

                workspace.Costs[index] = 0;
                workspace.Queue[tail++] = index;
            }

            bool hasWalkableCell = tail > 0;
            while (head < tail)
            {
                int current = workspace.Queue[head++];
                grid.FromFlatIndex(current, out int currentX, out int currentZ);
                int nextCost = workspace.Costs[current] + 1;
                for (int directionIndex = 0; directionIndex < 4; directionIndex++)
                {
                    if (!surface.HasConnection(current, directionIndex))
                        continue;

                    int nx = currentX + FlowFieldNeighborUtility.DeltaX[directionIndex];
                    int nz = currentZ + FlowFieldNeighborUtility.DeltaZ[directionIndex];
                    if (!grid.IsLocalInBounds(nx, nz))
                        continue;

                    int neighbor = grid.ToFlatIndex(nx, nz);
                    if (!workspace.Blocked[neighbor] || workspace.Costs[neighbor] != UNREACHABLE)
                        continue;

                    workspace.Costs[neighbor] = nextCost;
                    Vector3 direction = surface.GetCellCenter(grid, current)
                        - surface.GetCellCenter(grid, neighbor);
                    workspace.EscapeDirections[neighbor] = FlowFieldGraphTraversal.NormalizeOrZero(direction);
                    workspace.Queue[tail++] = neighbor;
                }
            }

            return hasWalkableCell;
        }

        public static bool PrepareGoal(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
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
            int surfaceAnchorIndex = FlowFieldGraphTraversal.FindNearestSurfaceAnchor(
                grid,
                surface,
                requestedGoalX,
                requestedGoalZ);
            if (surfaceAnchorIndex < 0)
                return false;

            BuildInfluenceMask(grid, surface, workspace, surfaceAnchorIndex, influenceRadius);
            resolvedGoalIndex = FlowFieldGraphTraversal.FindNearestWalkableGoal(
                grid,
                surface,
                workspace,
                surfaceAnchorIndex);
            if (resolvedGoalIndex < 0)
                return false;

            workspace.SetResolvedGoal(resolvedGoalIndex);
            FlowFieldGraphTraversal.BuildTopologyMasks(grid, surface, workspace);
            return true;
        }


        public static bool BuildGoal(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            int requestedGoalX,
            int requestedGoalZ,
            float influenceRadius,
            out int resolvedGoalIndex)
        {
            if (!PrepareGoal(
                    grid,
                    surface,
                    workspace,
                    requestedGoalX,
                    requestedGoalZ,
                    influenceRadius,
                    out resolvedGoalIndex))
                return false;

            SolvePreparedGoal(grid, surface, workspace, resolvedGoalIndex);
            return true;
        }

        /// <summary>
        /// Solves a Goal whose influence and topology PrepareGoal already built
        /// from the current obstacle state.
        /// </summary>
        public static void SolvePreparedGoal(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            int goalIndex)
        {
            ValidateWorkspace(grid, surface, workspace);
            FlowFieldSurfaceSolve solve = FlowFieldSurfaceSolve.Create(grid, surface, workspace, goalIndex);
            try { solve.Job.Run(); }
            finally { solve.Release(); }
        }

        private static void BuildInfluenceMask(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
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
            FlowFieldWorkspace workspace)
        {
            if (!grid.IsValid)
                throw new ArgumentException("FlowField solver requires a valid grid.", nameof(grid));
            if (surface == null || !surface.IsValid)
                throw new ArgumentException("FlowField solver requires a valid surface bake.", nameof(surface));
            if (workspace == null)
                throw new ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount)
                throw new ArgumentException("FlowField solver workspace capacity must match the grid.", nameof(workspace));
        }
    }

    /// <summary>
    /// Solver들이 공유하는 그래프 탐색 헬퍼 모음.
    /// </summary>
    internal static class FlowFieldGraphTraversal
    {
        private const float DISTANCE_TIE_EPSILON = 0.000001f;

        public static void BuildTopologyMasks(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace)
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
            FlowFieldWorkspace workspace)
        {
            if (!grid.IsValid || surface == null || !surface.IsValid
                || workspace == null || workspace.Capacity != grid.CellCount)
                throw new ArgumentException("No-goal sentinel input is invalid.");
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
            FlowFieldWorkspace workspace,
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
                bool replace = distanceSqr < bestDistanceSqr - DISTANCE_TIE_EPSILON
                    || Mathf.Abs(distanceSqr - bestDistanceSqr) <= DISTANCE_TIE_EPSILON
                        && index < bestIndex;
                if (replace)
                {
                    bestIndex = index;
                    bestDistanceSqr = distanceSqr;
                }
            }

            return bestIndex;
        }

        public static bool CanTraverse(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            int current,
            int currentX,
            int currentZ,
            int directionIndex,
            out int neighbor)
        {
            neighbor = -1;
            if (!surface.HasConnection(current, directionIndex))
                return false;

            int dx = FlowFieldNeighborUtility.DeltaX[directionIndex];
            int dz = FlowFieldNeighborUtility.DeltaZ[directionIndex];
            int nx = currentX + dx;
            int nz = currentZ + dz;
            if (!grid.IsLocalInBounds(nx, nz))
                return false;

            neighbor = grid.ToFlatIndex(nx, nz);
            if (!IsCellTraversable(surface, workspace, neighbor))
                return false;

            if (!FlowFieldNeighborUtility.IsDiagonal(directionIndex))
                return true;

            int firstDirection = dx > 0 ? 0 : 1;
            int secondDirection = dz > 0 ? 2 : 3;
            int orthogonalX = grid.ToFlatIndex(currentX + dx, currentZ);
            int orthogonalZ = grid.ToFlatIndex(currentX, currentZ + dz);
            return surface.HasConnection(current, firstDirection)
                && surface.HasConnection(current, secondDirection)
                && IsCellTraversable(surface, workspace, orthogonalX)
                && IsCellTraversable(surface, workspace, orthogonalZ);
        }

        public static bool IsCellTraversable(
            FlowFieldSurfaceData surface,
            FlowFieldWorkspace workspace,
            int index)
            => index >= 0
                && index < workspace.Capacity
                && surface.IsSurfaceValid(index)
                && workspace.InfluenceMask[index]
                && !workspace.Blocked[index];

        public static Vector3 NormalizeOrZero(Vector3 direction)
            => direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                ? direction.normalized
                : Vector3.zero;

        /// <summary>
        /// Returns a deterministic integer score equivalent to minimizing the
        /// candidate cell's squared XZ distance to the Goal. The step is one
        /// of the eight immediate neighbor offsets, so the score remains
        /// within signed 32-bit range for the supported grid size.
        /// </summary>
        internal static int CalculateGoalAlignmentScore(
            int currentX,
            int currentZ,
            int goalX,
            int goalZ,
            int stepX,
            int stepZ)
        {
            int goalDeltaX = goalX - currentX;
            int goalDeltaZ = goalZ - currentZ;
            int dot = stepX * goalDeltaX + stepZ * goalDeltaZ;
            int stepLengthSqr = stepX * stepX + stepZ * stepZ;
            return 2 * dot - stepLengthSqr;
        }
    }
}
