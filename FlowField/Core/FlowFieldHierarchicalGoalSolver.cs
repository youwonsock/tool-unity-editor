using System;
using UnityEngine;

namespace Supercent.Common.FlowField
{
    internal static class FlowFieldHierarchicalGoalSolver
    {
        private const int UNREACHABLE = int.MaxValue;

        public static bool BuildHierarchicalGoal(
            FlowFieldGridSpace fineGrid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldCoarseTopologyData coarse,
            FlowFieldWorkspace workspace,
            int requestedGoalX,
            int requestedGoalZ,
            float influenceRadius,
            int fineRingCoarseRadius,
            out int resolvedGoalIndex)
        {
            resolvedGoalIndex = -1;
            if (!fineGrid.IsValid
                || surface == null
                || !surface.HasValidData
                || workspace == null
                || workspace.Capacity != fineGrid.CellCount)
                return false;

            if (workspace.HasBlockedCells)
            {
                return FlowFieldSolver.BuildGoal(
                    fineGrid,
                    surface,
                    workspace,
                    requestedGoalX,
                    requestedGoalZ,
                    influenceRadius,
                    out resolvedGoalIndex);
            }

            if (coarse == null || !coarse.HasValidData)
            {
                return FlowFieldSolver.BuildGoal(
                    fineGrid,
                    surface,
                    workspace,
                    requestedGoalX,
                    requestedGoalZ,
                    influenceRadius,
                    out resolvedGoalIndex);
            }

            workspace.ClearGoal();
            int surfaceAnchorIndex = FlowFieldGraphTraversal.FindNearestSurfaceAnchor(
                fineGrid,
                surface,
                requestedGoalX,
                requestedGoalZ);
            if (surfaceAnchorIndex < 0)
                return false;

            fineGrid.FromFlatIndex(surfaceAnchorIndex, out int anchorX, out int anchorZ);
            if (!coarse.TryFineToCoarse(anchorX, anchorZ, out int goalCoarseX, out int goalCoarseZ))
                return false;

            BuildFineInfluenceMask(
                fineGrid,
                surface,
                workspace,
                surfaceAnchorIndex,
                influenceRadius,
                coarse,
                goalCoarseX,
                goalCoarseZ,
                fineRingCoarseRadius);

            resolvedGoalIndex = FlowFieldGraphTraversal.FindNearestWalkableGoal(
                fineGrid,
                surface,
                workspace,
                surfaceAnchorIndex,
                useDistanceTieEpsilon: false);
            if (resolvedGoalIndex < 0)
                return false;

            BuildCoarseIntegration(coarse, workspace, goalCoarseX, goalCoarseZ);
            ApplyCoarseDirectionsToFine(
                fineGrid,
                surface,
                coarse,
                workspace,
                workspace.Costs,
                goalCoarseX,
                goalCoarseZ);

            BuildFineIntegrationInMask(fineGrid, surface, workspace, resolvedGoalIndex);
            BuildFineGoalDirectionsInMask(fineGrid, surface, workspace, resolvedGoalIndex);
            return true;
        }

        private static void BuildFineInfluenceMask(
            FlowFieldGridSpace fineGrid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldWorkspace workspace,
            int anchorIndex,
            float influenceRadius,
            FlowFieldCoarseTopologyData coarse,
            int goalCoarseX,
            int goalCoarseZ,
            int fineRingCoarseRadius)
        {
            fineRingCoarseRadius = Mathf.Max(0, fineRingCoarseRadius);
            Vector3 center = surface.GetCellCenter(fineGrid, anchorIndex);
            float radiusSqr = influenceRadius > 0f
                ? influenceRadius * influenceRadius
                : float.PositiveInfinity;

            for (int index = 0; index < fineGrid.CellCount; index++)
            {
                if (!surface.IsSurfaceValid(index))
                {
                    workspace.InfluenceMask[index] = false;
                    continue;
                }

                fineGrid.FromFlatIndex(index, out int x, out int z);
                if (!coarse.TryFineToCoarse(x, z, out int cx, out int cz))
                {
                    workspace.InfluenceMask[index] = false;
                    continue;
                }

                int dcx = Mathf.Abs(cx - goalCoarseX);
                int dcz = Mathf.Abs(cz - goalCoarseZ);
                bool inFineRing = dcx <= fineRingCoarseRadius && dcz <= fineRingCoarseRadius;
                bool inSphere = influenceRadius <= 0f
                    || (surface.GetCellCenter(fineGrid, index) - center).sqrMagnitude <= radiusSqr;
                workspace.InfluenceMask[index] = inFineRing && inSphere;
            }

            workspace.InfluenceMask[anchorIndex] = true;
        }

        private static void BuildCoarseIntegration(
            FlowFieldCoarseTopologyData coarse,
            FlowFieldWorkspace workspace,
            int goalCoarseX,
            int goalCoarseZ)
        {
            int count = coarse.CoarseCellCount;
            for (int i = 0; i < count; i++)
                workspace.Costs[i] = UNREACHABLE;

            int goalIndex = coarse.ToFlatIndex(goalCoarseX, goalCoarseZ);
            if (!coarse.IsWalkable(goalIndex))
            {
                int best = -1;
                int bestDist = int.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    if (!coarse.IsWalkable(i))
                        continue;
                    coarse.FromFlatIndex(i, out int x, out int z);
                    int dist = (x - goalCoarseX) * (x - goalCoarseX) + (z - goalCoarseZ) * (z - goalCoarseZ);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = i;
                    }
                }

                if (best < 0)
                    return;
                goalIndex = best;
            }

            int head = 0;
            int tail = 0;
            workspace.Costs[goalIndex] = 0;
            workspace.Queue[tail++] = goalIndex;
            while (head < tail)
            {
                int current = workspace.Queue[head++];
                coarse.FromFlatIndex(current, out int cx, out int cz);
                int nextCost = workspace.Costs[current] + 1;
                for (int dir = 0; dir < 4; dir++)
                {
                    if (!coarse.HasConnection(current, dir))
                        continue;

                    int nx = cx + FlowFieldNeighborUtility.DeltaX[dir];
                    int nz = cz + FlowFieldNeighborUtility.DeltaZ[dir];
                    if (nx < 0 || nx >= coarse.CoarseWidth || nz < 0 || nz >= coarse.CoarseDepth)
                        continue;

                    int neighbor = coarse.ToFlatIndex(nx, nz);
                    if (!coarse.IsWalkable(neighbor) || workspace.Costs[neighbor] != UNREACHABLE)
                        continue;

                    workspace.Costs[neighbor] = nextCost;
                    workspace.Queue[tail++] = neighbor;
                }
            }
        }

        private static void ApplyCoarseDirectionsToFine(
            FlowFieldGridSpace fineGrid,
            FlowFieldSurfaceBakeData surface,
            FlowFieldCoarseTopologyData coarse,
            FlowFieldWorkspace workspace,
            int[] coarseCosts,
            int goalCoarseX,
            int goalCoarseZ)
        {
            for (int index = 0; index < fineGrid.CellCount; index++)
            {
                if (!surface.IsSurfaceValid(index) || workspace.Blocked[index])
                    continue;
                if (workspace.InfluenceMask[index])
                    continue;

                fineGrid.FromFlatIndex(index, out int x, out int z);
                if (!coarse.TryFineToCoarse(x, z, out int cx, out int cz))
                    continue;

                int coarseIndex = coarse.ToFlatIndex(cx, cz);
                if (!coarse.IsWalkable(coarseIndex) || coarseCosts[coarseIndex] == UNREACHABLE)
                    continue;

                int bestNeighbor = -1;
                int bestCost = UNREACHABLE;
                for (int dir = 0; dir < 4; dir++)
                {
                    if (!coarse.HasConnection(coarseIndex, dir))
                        continue;

                    int nx = cx + FlowFieldNeighborUtility.DeltaX[dir];
                    int nz = cz + FlowFieldNeighborUtility.DeltaZ[dir];
                    if (nx < 0 || nx >= coarse.CoarseWidth || nz < 0 || nz >= coarse.CoarseDepth)
                        continue;

                    int neighbor = coarse.ToFlatIndex(nx, nz);
                    int cost = coarseCosts[neighbor];
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        bestNeighbor = neighbor;
                    }
                }

                Vector3 direction;
                if (bestNeighbor < 0 || bestCost >= coarseCosts[coarseIndex])
                {
                    // Point toward goal coarse cell center
                    float goalWorldX = fineGrid.Origin.x
                        + (goalCoarseX * coarse.CoarseMultiplier + coarse.CoarseMultiplier * 0.5f)
                        * fineGrid.CellSize;
                    float goalWorldZ = fineGrid.Origin.z
                        + (goalCoarseZ * coarse.CoarseMultiplier + coarse.CoarseMultiplier * 0.5f)
                        * fineGrid.CellSize;
                    Vector3 cell = surface.GetCellCenter(fineGrid, index);
                    direction = new Vector3(goalWorldX - cell.x, 0f, goalWorldZ - cell.z);
                }
                else
                {
                    coarse.FromFlatIndex(bestNeighbor, out int bx, out int bz);
                    float targetX = fineGrid.Origin.x
                        + (bx * coarse.CoarseMultiplier + coarse.CoarseMultiplier * 0.5f) * fineGrid.CellSize;
                    float targetZ = fineGrid.Origin.z
                        + (bz * coarse.CoarseMultiplier + coarse.CoarseMultiplier * 0.5f) * fineGrid.CellSize;
                    Vector3 cell = surface.GetCellCenter(fineGrid, index);
                    direction = new Vector3(targetX - cell.x, 0f, targetZ - cell.z);
                }

                if (direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                {
                    workspace.GoalDirections[index] = direction.normalized;
                    workspace.GoalFlags[index] = FlowFieldGoalFlags.Directed;
                }
            }
        }

        private static void BuildFineIntegrationInMask(
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
            if (!workspace.InfluenceMask[goalIndex] || workspace.Blocked[goalIndex])
                return;

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

        private static void BuildFineGoalDirectionsInMask(
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
    }
}
