using System;
using UnityEngine;

namespace Supercent.Common.FlowField
{
    [Flags]
    internal enum FlowFieldGoalFlags : byte
    {
        None = 0,
        Directed = 1 << 0,
        Anchor = 1 << 1,
    }

    internal static class FlowFieldSolver
    {
        private const int UNREACHABLE = int.MaxValue;

        public static bool BuildEscapeDirections(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace)
        {
            if (!IsWorkspaceValid(grid, surface, workspace))
                return false;

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

        public static bool BuildGoal(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            int requestedGoalX,
            int requestedGoalZ,
            float influenceRadius,
            out int resolvedGoalIndex)
        {
            resolvedGoalIndex = -1;
            if (!IsWorkspaceValid(grid, surface, workspace)
                || !grid.IsLocalInBounds(requestedGoalX, requestedGoalZ))
                return false;

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
            {
                workspace.Costs[index] = UNREACHABLE;
                workspace.HeapPositions[index] = -1;
            }

            workspace.HeapCount = 0;
            workspace.Costs[goalIndex] = 0;
            FlowFieldDijkstraHeap.InsertOrDecrease(workspace, goalIndex);
            while (workspace.HeapCount > 0)
            {
                int current = FlowFieldDijkstraHeap.Pop(workspace);
                grid.FromFlatIndex(current, out int currentX, out int currentZ);
                int currentCost = workspace.Costs[current];
                for (int directionIndex = 0; directionIndex < FlowFieldNeighborUtility.Count; directionIndex++)
                {
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

                    int transitionCost = FlowFieldGraphTraversal.GetTransitionCost(grid, surface, current, neighbor);
                    if (currentCost > UNREACHABLE - transitionCost)
                        continue;

                    int candidate = currentCost + transitionCost;
                    if (candidate >= workspace.Costs[neighbor])
                        continue;

                    workspace.Costs[neighbor] = candidate;
                    FlowFieldDijkstraHeap.InsertOrDecrease(workspace, neighbor);
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
                if (!FlowFieldGraphTraversal.IsCellTraversable(surface, workspace, index)
                    || workspace.Costs[index] == UNREACHABLE)
                    continue;

                workspace.GoalFlags[index] = FlowFieldGoalFlags.Directed;
                if (index == goalIndex)
                {
                    workspace.GoalFlags[index] |= FlowFieldGoalFlags.Anchor;
                    workspace.GoalDirections[index] = Vector3.zero;
                    continue;
                }

                grid.FromFlatIndex(index, out int x, out int z);
                int bestIndex = -1;
                int bestTotalCost = UNREACHABLE;
                for (int directionIndex = 0; directionIndex < FlowFieldNeighborUtility.Count; directionIndex++)
                {
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

                    int transitionCost = FlowFieldGraphTraversal.GetTransitionCost(grid, surface, index, neighbor);
                    if (neighborCost > UNREACHABLE - transitionCost)
                        continue;

                    int totalCost = neighborCost + transitionCost;
                    if (totalCost < bestTotalCost
                        || totalCost == bestTotalCost && neighbor < bestIndex)
                    {
                        bestTotalCost = totalCost;
                        bestIndex = neighbor;
                    }
                }

                if (bestIndex < 0)
                    continue;

                Vector3 direction = surface.GetCellCenter(grid, bestIndex)
                    - surface.GetCellCenter(grid, index);
                workspace.GoalDirections[index] = FlowFieldGraphTraversal.NormalizeOrZero(direction);
            }
        }

        private static bool IsWorkspaceValid(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace)
            => grid.IsValid
                && surface != null
                && surface.HasValidData
                && workspace != null
                && workspace.Capacity == grid.CellCount;
    }
}
