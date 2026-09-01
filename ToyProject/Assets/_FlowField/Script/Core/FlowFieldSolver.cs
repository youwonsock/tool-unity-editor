using System;
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
            FlowFieldSurfaceBakeData surface,
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
            FlowFieldSurfaceBakeData surface,
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
                surfaceAnchorIndex,
                useDistanceTieEpsilon: true);
            if (resolvedGoalIndex < 0)
                return false;

            workspace.SetResolvedGoal(resolvedGoalIndex);
            FlowFieldGraphTraversal.BuildTopologyMasks(grid, surface, workspace);
            return true;
        }

        public static bool BuildGoal(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
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

            BuildIntegration(grid, surface, workspace, resolvedGoalIndex);
            BuildGoalDirections(grid, surface, workspace, resolvedGoalIndex);
            return true;
        }

        private static void BuildInfluenceMask(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            int anchorIndex,
            float influenceRadius)
        {
            if (influenceRadius <= 0f)
            {
                for (int index = 0; index < grid.CellCount; index++)
                    workspace.InfluenceMask[index] = surface.IsSurfaceValid(index);
                return;
            }

            Vector3 center = surface.GetCellCenter(grid, anchorIndex);
            float radiusSqr = influenceRadius * influenceRadius;
            for (int index = 0; index < grid.CellCount; index++)
            {
                workspace.InfluenceMask[index] = surface.IsSurfaceValid(index)
                    && (surface.GetCellCenter(grid, index) - center).sqrMagnitude <= radiusSqr;
            }

            workspace.InfluenceMask[anchorIndex] = true;
        }

        private static void BuildIntegration(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            int goalIndex)
        {
            for (int index = 0; index < grid.CellCount; index++)
                workspace.Costs[index] = UNREACHABLE;

            int head = 0;
            int tail = 0;
            workspace.Costs[goalIndex] = 0;
            workspace.Queue[tail++] = goalIndex;
            while (head < tail)
            {
                int current = workspace.Queue[head++];
                grid.FromFlatIndex(current, out int currentX, out int currentZ);
                int currentCost = workspace.Costs[current];
                for (int directionIndex = 0; directionIndex < FlowFieldNeighborUtility.Count; directionIndex++)
                {
                    if ((workspace.TopologyMasks[current] & (1 << directionIndex)) == 0)
                        continue;
                    if (!FlowFieldGraphTraversal.CanTraverse(
                            grid,
                            surface,
                            workspace,
                            current,
                            currentX,
                            currentZ,
                            directionIndex,
                            out int neighbor))
                        continue;

                    int candidate = currentCost + 1;
                    if (candidate >= workspace.Costs[neighbor])
                        continue;

                    workspace.Costs[neighbor] = candidate;
                    workspace.Queue[tail++] = neighbor;
                }
            }
        }

        private static void BuildGoalDirections(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            int goalIndex)
        {
            for (int index = 0; index < grid.CellCount; index++)
            {
                workspace.NextCells[index] = !surface.IsSurfaceValid(index)
                    ? -2
                    : workspace.Blocked[index]
                        ? -2
                        : !workspace.InfluenceMask[index]
                            ? -1
                            : -3;
                if (!FlowFieldGraphTraversal.IsCellTraversable(surface, workspace, index)
                    || workspace.Costs[index] == UNREACHABLE)
                {
                    if (FlowFieldGraphTraversal.IsCellTraversable(surface, workspace, index))
                    {
                        workspace.GoalFlags[index] = FlowFieldGoalFlags.Unreachable;
                        workspace.NextCells[index] = -3;
                    }
                    continue;
                }

                workspace.GoalFlags[index] = FlowFieldGoalFlags.Directed;
                if (index == goalIndex)
                {
                    workspace.GoalFlags[index] |= FlowFieldGoalFlags.Anchor;
                    workspace.GoalDirections[index] = Vector3.zero;
                    workspace.NextCells[index] = index;
                    continue;
                }

                grid.FromFlatIndex(index, out int x, out int z);
                int bestIndex = -1;
                int bestTotalCost = UNREACHABLE;
                for (int directionIndex = 0; directionIndex < FlowFieldNeighborUtility.Count; directionIndex++)
                {
                    if ((workspace.TopologyMasks[index] & (1 << directionIndex)) == 0)
                        continue;
                    if (!FlowFieldGraphTraversal.CanTraverse(
                            grid,
                            surface,
                            workspace,
                            index,
                            x,
                            z,
                            directionIndex,
                            out int neighbor))
                        continue;

                    int neighborCost = workspace.Costs[neighbor];
                    if (neighborCost == UNREACHABLE)
                        continue;

                    // Every edge has cost one.  Selecting exactly the previous
                    // wave makes the direction deterministic and prevents
                    // zero-cost cycles; ties are resolved by flat index.
                    if (neighborCost != workspace.Costs[index] - 1)
                        continue;

                    if (neighborCost < bestTotalCost
                        || neighborCost == bestTotalCost && (bestIndex < 0 || neighbor < bestIndex))
                    {
                        bestTotalCost = neighborCost;
                        bestIndex = neighbor;
                    }
                }

                if (bestIndex < 0)
                {
                    workspace.GoalFlags[index] = FlowFieldGoalFlags.Unreachable;
                    workspace.NextCells[index] = -3;
                    continue;
                }

                Vector3 currentPosition = surface.GetCellCenter(grid, index);
                Vector3 nextPosition = surface.GetCellCenter(grid, bestIndex);
                Vector3 normal = surface.GetSurfaceNormal(index);
                Vector3 projected = Vector3.ProjectOnPlane(nextPosition - currentPosition, normal);
                workspace.GoalDirections[index] = FlowFieldGraphTraversal.NormalizeOrZero(projected);
                workspace.NextCells[index] = bestIndex;
            }
        }

        private static void ValidateWorkspace(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace)
        {
            if (!grid.IsValid)
                throw new ArgumentException("FlowField solver requires a valid grid.", nameof(grid));
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));
            if (!surface.HasValidData)
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
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace)
        {
            if (!grid.IsValid)
                throw new System.ArgumentException("Topology mask requires a valid grid.", nameof(grid));
            if (surface == null)
                throw new System.ArgumentNullException(nameof(surface));
            if (!surface.HasValidData)
                throw new System.ArgumentException("Topology mask requires a valid surface bake.", nameof(surface));
            if (workspace == null)
                throw new System.ArgumentNullException(nameof(workspace));
            if (workspace.Capacity != grid.CellCount
                || workspace.TopologyMasks == null
                || workspace.TopologyMasks.Length != grid.CellCount)
                throw new System.ArgumentException("Topology mask workspace capacity must match the grid.", nameof(workspace));

            System.Array.Clear(workspace.TopologyMasks, 0, workspace.TopologyMasks.Length);
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (!IsCellTraversable(surface, workspace, index))
                    continue;

                grid.FromFlatIndex(index, out int x, out int z);
                byte mask = 0;
                for (int directionIndex = 0; directionIndex < FlowFieldNeighborUtility.Count; directionIndex++)
                {
                    if (CanTraverse(
                            grid,
                            surface,
                            workspace,
                            index,
                            x,
                            z,
                            directionIndex,
                            out _))
                    {
                        mask |= (byte)(1 << directionIndex);
                    }
                }

                workspace.TopologyMasks[index] = mask;
            }
        }

        public static int FindNearestSurfaceAnchor(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
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
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            int anchorIndex,
            bool useDistanceTieEpsilon)
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
                bool replace = useDistanceTieEpsilon
                    ? distanceSqr < bestDistanceSqr - DISTANCE_TIE_EPSILON
                        || Mathf.Abs(distanceSqr - bestDistanceSqr) <= DISTANCE_TIE_EPSILON
                            && index < bestIndex
                    : distanceSqr < bestDistanceSqr;
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
            FlowFieldSurfaceBakeData surface,
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
            FlowFieldSurfaceBakeData surface,
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
    }
}
