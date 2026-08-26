using UnityEngine;

namespace Supercent.Common.FlowField
{
    /// <summary>
    /// Solver들이 공유하는 그래프 탐색 헬퍼 모음.
    /// </summary>
    internal static class FlowFieldGraphTraversal
    {
        private const float DISTANCE_TIE_EPSILON = 0.000001f;

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
            int bestDistanceSqr = int.MaxValue;
            for (int index = 0; index < grid.CellCount; index++)
            {
                if (!surface.IsSurfaceValid(index))
                    continue;

                grid.FromFlatIndex(index, out int x, out int z);
                int dx = x - requestedGoalX;
                int dz = z - requestedGoalZ;
                int distanceSqr = dx * dx + dz * dz;
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

            int orthogonalX = grid.ToFlatIndex(currentX + dx, currentZ);
            int orthogonalZ = grid.ToFlatIndex(currentX, currentZ + dz);
            return IsCellTraversable(surface, workspace, orthogonalX)
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

        public static int GetTransitionCost(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            int from,
            int to)
        {
            float distance = Vector3.Distance(
                surface.GetCellCenter(grid, from),
                surface.GetCellCenter(grid, to));
            return Mathf.Max(1, Mathf.RoundToInt(distance * 1000f));
        }

        public static Vector3 NormalizeOrZero(Vector3 direction)
            => direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                ? direction.normalized
                : Vector3.zero;
    }
}
