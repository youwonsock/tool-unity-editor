using System;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;

namespace Common.FlowField
{
    /// <summary>
    /// Deterministic Volume3D graph operations. No Physics, callbacks, publication or GPU ownership.
    /// </summary>
    internal static class FlowFieldVolumeNavigation
    {
        internal const int CellsPerStep = 64;
        internal const int ArrayElementsPerStep = 4096;
        internal const int Unreachable = int.MaxValue;

        internal static bool StepTopology(BuildContext build)
        {
            int end = Math.Min(build.Cursor + CellsPerStep, build.Count);
            FlowFieldGridSpace grid = build.Request.Grid;
            FlowFieldVolumeInteriorOffsets interior = build.InteriorOffsets ??= new FlowFieldVolumeInteriorOffsets(grid);
            int lastX = grid.Width - 1;
            int lastY = grid.Height - 1;
            int lastZ = grid.Depth - 1;
            for (; build.Cursor < end; build.Cursor++)
            {
                int index = build.Cursor;
                if (build.Blocked[index])
                {
                    build.Next[index] = -2;
                    build.GoalFlags[index] = FlowFieldGoalFlags.None;
                    build.Directions[index] = Vector3.zero;
                    build.Speeds[index] = 0f;
                    continue;
                }

                grid.FromFlatIndex(index, out int x, out int y, out int z);
                if (x > 0 && x < lastX && y > 0 && y < lastY && z > 0 && z < lastZ)
                {
                    build.Topology[index] = interior.TopologyMask(build.Blocked, index);
                    continue;
                }
                uint mask = 0u;
                for (int direction = 0; direction < FlowFieldNeighborUtility.VolumeCount; direction++)
                {
                    if (CanTraverse(build, index, x, y, z, direction, out _))
                        mask |= 1u << direction;
                }
                build.Topology[index] = mask;
            }
            return build.Cursor >= build.Count;
        }

        internal static bool StepGoal(BuildContext build)
        {
            switch (build.GoalStage)
            {
                case 0:
                {
                    int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
                    for (; build.Cursor < end; build.Cursor++)
                        build.Influence[build.Cursor] = false;
                    if (build.Cursor < build.Count)
                        return false;

                    build.Cursor = 0;
                    build.GoalIndex = -1;
                    build.Head = 0;
                    build.Tail = 0;
                    if (!build.Request.HasGoal)
                    {
                        build.GoalStage = 4;
                        return true;
                    }

                    Vector3 clamped = build.Request.Grid.ClampWorldXYZ(build.Request.GoalWorld);
                    build.Request.Grid.TryWorldToLocalClamped(clamped, out int x, out int y, out int z);
                    build.RequestedGoalIndex = build.Request.Grid.ToFlatIndex(x, y, z);
                    if (!build.Blocked[build.RequestedGoalIndex])
                    {
                        build.GoalIndex = build.RequestedGoalIndex;
                        build.GoalStage = 2;
                        build.Cursor = 0;
                    }
                    else
                    {
                        build.NearestGoalDistance = long.MaxValue;
                        build.GoalStage = 1;
                    }
                    break;
                }
                case 1:
                {
                    build.Request.Grid.FromFlatIndex(
                        build.RequestedGoalIndex,
                        out int requestedX,
                        out int requestedY,
                        out int requestedZ);
                    int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
                    for (; build.Cursor < end; build.Cursor++)
                    {
                        int index = build.Cursor;
                        if (build.Blocked[index])
                            continue;
                        build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                        long dx = x - requestedX;
                        long dy = y - requestedY;
                        long dz = z - requestedZ;
                        long distance = dx * dx + dy * dy + dz * dz;
                        if (distance < build.NearestGoalDistance
                            || distance == build.NearestGoalDistance
                                && (build.GoalIndex < 0 || index < build.GoalIndex))
                        {
                            build.NearestGoalDistance = distance;
                            build.GoalIndex = index;
                        }
                    }
                    if (build.Cursor < build.Count)
                        return false;
                    build.Cursor = 0;
                    build.GoalStage = build.GoalIndex >= 0 ? 2 : 4;
                    break;
                }
                case 2:
                {
                    if (build.GoalIndex < 0)
                    {
                        build.GoalStage = 4;
                        return true;
                    }

                    build.Request.Grid.FromFlatIndex(
                        build.GoalIndex,
                        out int goalX,
                        out int goalY,
                        out int goalZ);
                    build.GoalRadiusSqr = (double)build.Request.GoalInfluenceRadius
                        * build.Request.GoalInfluenceRadius;
                    int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
                    for (; build.Cursor < end; build.Cursor++)
                    {
                        int index = build.Cursor;
                        if (build.Blocked[index])
                            continue;
                        build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                        long dx = x - goalX;
                        long dy = y - goalY;
                        long dz = z - goalZ;
                        double distanceSqr = (double)(dx * dx + dy * dy + dz * dz)
                            * build.Request.Grid.CellSize
                            * build.Request.Grid.CellSize;
                        build.Influence[index] = build.Request.GoalInfluenceRadius <= 0f
                            || distanceSqr <= build.GoalRadiusSqr;
                    }
                    if (build.Cursor < build.Count)
                        return false;

                    build.Influence[build.GoalIndex] = true;
                    build.Costs[build.GoalIndex] = 0;
                    build.Queue[build.Tail++] = build.GoalIndex;
                    build.GoalStage = 4;
                    return true;
                }
                default:
                    return true;
            }

            return false;
        }

        internal static bool StepManagedBfs(BuildContext build)
        {
            int processed = 0;
            FlowFieldGridSpace grid = build.Request.Grid;
            while (build.Head < build.Tail && processed++ < CellsPerStep)
            {
                int current = build.Queue[build.Head++];
                grid.FromFlatIndex(current, out int x, out int y, out int z);
                int currentCost = build.Costs[current];
                for (int direction = 0; direction < FlowFieldNeighborUtility.VolumeCount; direction++)
                {
                    if ((build.Topology[current] & (1u << direction)) == 0u)
                        continue;
                    if (!TryNeighbor(grid, x, y, z, direction, out int neighbor))
                        continue;
                    if (!build.Influence[neighbor])
                        continue;
                    int candidate = currentCost + 1;
                    if (candidate < build.Costs[neighbor])
                    {
                        build.Costs[neighbor] = candidate;
                        build.Queue[build.Tail++] = neighbor;
                    }
                }
            }
            return build.Head >= build.Tail;
        }

        internal static bool StepGpuFallbackReset(BuildContext build)
        {
            int end = Math.Min(
                build.GpuFallbackResetCursor + ArrayElementsPerStep,
                build.Count);
            for (; build.GpuFallbackResetCursor < end; build.GpuFallbackResetCursor++)
                build.Costs[build.GpuFallbackResetCursor] = Unreachable;

            if (build.GpuFallbackResetCursor < build.Count)
                return false;

            build.Head = 0;
            build.Tail = 0;
            if (build.GoalIndex >= 0)
            {
                build.Costs[build.GoalIndex] = 0;
                build.Queue[build.Tail++] = build.GoalIndex;
            }
            build.GpuFailed = null;
            build.GpuFallbackResetCursor = 0;
            build.Cursor = 0;
            return true;
        }

        internal static bool StepDirections(BuildContext build)
        {
            int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
            FlowFieldGridSpace grid = build.Request.Grid;
            int goalX = 0, goalY = 0, goalZ = 0;
            if (build.GoalIndex >= 0)
                grid.FromFlatIndex(build.GoalIndex, out goalX, out goalY, out goalZ);
            Vector3 defaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(build.Request.DefaultDirection);
            for (; build.Cursor < end; build.Cursor++)
            {
                int index = build.Cursor;
                if (build.Blocked[index])
                {
                    build.Next[index] = -2;
                    build.GoalFlags[index] = FlowFieldGoalFlags.None;
                    build.Directions[index] = Vector3.zero;
                    build.Speeds[index] = 0f;
                    continue;
                }

                // The Goal itself is the only anchor. A cell whose successor
                // happens to be the Goal remains an ordinary directed cell.
                // StepTopology runs before Goal resolution, so this check
                // deliberately lives here instead of in topology creation.
                if (build.GoalIndex == index)
                {
                    build.Next[index] = index;
                    build.GoalFlags[index] = FlowFieldGoalFlags.Directed
                        | FlowFieldGoalFlags.Anchor;
                    build.Directions[index] = Vector3.zero;
                    build.Speeds[index] = 0f;
                    continue;
                }

                Vector3 direction = defaultDirection;
                int next = -1;
                bool goalCellIsUnreachable = build.GoalIndex >= 0
                    && build.Influence[index]
                    && build.Costs[index] == Unreachable;
                if (build.GoalIndex >= 0
                    && build.Influence[index]
                    && build.Costs[index] != Unreachable)
                {
                    grid.FromFlatIndex(index, out int x, out int y, out int z);
                    int bestScore = int.MinValue;
                    for (int candidateDirection = 0; candidateDirection < FlowFieldNeighborUtility.VolumeCount; candidateDirection++)
                    {
                        if ((build.Topology[index] & (1u << candidateDirection)) == 0u
                            || !TryNeighbor(grid, x, y, z, candidateDirection, out int neighbor)
                            || build.Costs[neighbor] != build.Costs[index] - 1)
                            continue;
                        int score = AlignmentScore(goalX - x, goalY - y, goalZ - z, candidateDirection);
                        if (next < 0 || score > bestScore || score == bestScore && neighbor < next)
                        {
                            next = neighbor;
                            bestScore = score;
                        }
                    }

                    if (next >= 0)
                        direction = DirectionBetweenCells(grid, index, next);
                    else
                    {
                        goalCellIsUnreachable = true;
                        direction = Vector3.zero;
                        next = -3;
                    }
                }

                if (goalCellIsUnreachable)
                {
                    direction = Vector3.zero;
                    next = -3;
                }

                build.Next[index] = next;
                build.GoalFlags[index] = next >= 0
                    ? FlowFieldGoalFlags.Directed
                    : build.GoalIndex >= 0 && build.Influence[index]
                        ? FlowFieldGoalFlags.Unreachable
                        : FlowFieldGoalFlags.None;
                build.Directions[index] = direction;
                build.Speeds[index] = direction.sqrMagnitude > 0.00000001f ? 1f : 0f;
            }
            return build.Cursor >= build.Count;
        }

        internal static bool StepEscape(BuildContext build)
        {
            if (build.EscapeStage == 0)
            {
                int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
                for (; build.Cursor < end; build.Cursor++)
                {
                    int index = build.Cursor;
                    build.Costs[index] = build.Blocked[index] ? Unreachable : 0;
                    build.EscapeDirections[index] = Vector3.zero;
                    if (!build.Blocked[index])
                        build.Queue[build.EscapeTail++] = index;
                }
                if (build.Cursor < build.Count)
                    return false;
                build.Cursor = 0;
                build.EscapeStage = 1;
            }

            int processed = 0;
            FlowFieldGridSpace grid = build.Request.Grid;
            while (build.EscapeHead < build.EscapeTail && processed++ < CellsPerStep)
            {
                int current = build.Queue[build.EscapeHead++];
                grid.FromFlatIndex(current, out int x, out int y, out int z);
                int nextCost = build.Costs[current] + 1;
                for (int face = 0; face < FlowFieldNeighborUtility.VolumeFaceDirections.Length; face++)
                {
                    int direction = FlowFieldNeighborUtility.VolumeFaceDirections[face];
                    if (!TryNeighbor(grid, x, y, z, direction, out int neighbor)
                        || !build.Blocked[neighbor]
                        || build.Costs[neighbor] != Unreachable)
                        continue;
                    build.Costs[neighbor] = nextCost;
                    build.EscapeDirections[neighbor] = DirectionBetweenCells(
                        grid,
                        neighbor,
                        current);
                    build.Queue[build.EscapeTail++] = neighbor;
                }
            }
            return build.EscapeHead >= build.EscapeTail;
        }

        internal static bool CanTraverse(
            BuildContext build,
            int current,
            int x,
            int y,
            int z,
            int direction,
            out int neighbor)
        {
            neighbor = -1;
            int nx = x + FlowFieldNeighborUtility.DeltaX[direction];
            int ny = y + FlowFieldNeighborUtility.DeltaY[direction];
            int nz = z + FlowFieldNeighborUtility.DeltaZ[direction];
            if (!build.Request.Grid.IsLocalInBounds(nx, ny, nz))
                return false;
            neighbor = build.Request.Grid.ToFlatIndex(nx, ny, nz);
            if (build.Blocked[neighbor])
                return false;
            if (!FlowFieldNeighborUtility.IsDiagonal(direction))
                return true;

            int dx = FlowFieldNeighborUtility.DeltaX[direction];
            int dy = FlowFieldNeighborUtility.DeltaY[direction];
            int dz = FlowFieldNeighborUtility.DeltaZ[direction];
            int maxXStep = dx == 0 ? 0 : 1;
            int maxYStep = dy == 0 ? 0 : 1;
            int maxZStep = dz == 0 ? 0 : 1;
            for (int sx = 0; sx <= maxXStep; sx++)
            for (int sy = 0; sy <= maxYStep; sy++)
            for (int sz = 0; sz <= maxZStep; sz++)
            {
                if (sx == 0 && sy == 0 && sz == 0
                    || sx == maxXStep && sy == maxYStep && sz == maxZStep)
                    continue;
                int ix = x + (sx == 1 ? dx : 0);
                int iy = y + (sy == 1 ? dy : 0);
                int iz = z + (sz == 1 ? dz : 0);
                if (ix == x && iy == y && iz == z)
                    continue;
                if (!build.Request.Grid.IsLocalInBounds(ix, iy, iz)
                    || build.Blocked[build.Request.Grid.ToFlatIndex(ix, iy, iz)])
                    return false;
            }
            return true;
        }

        internal static bool TryNeighbor(
            FlowFieldGridSpace grid,
            int x,
            int y,
            int z,
            int direction,
            out int index)
        {
            int nx = x + FlowFieldNeighborUtility.DeltaX[direction];
            int ny = y + FlowFieldNeighborUtility.DeltaY[direction];
            int nz = z + FlowFieldNeighborUtility.DeltaZ[direction];
            if (!grid.IsLocalInBounds(nx, ny, nz))
            {
                index = -1;
                return false;
            }
            index = grid.ToFlatIndex(nx, ny, nz);
            return true;
        }

        /// <summary>Alignment of a step with the offset (dx, dy, dz) from the cell to the Goal.</summary>
        internal static int AlignmentScore(int dx, int dy, int dz, int direction)
        {
            int sx = FlowFieldNeighborUtility.DeltaX[direction];
            int sy = FlowFieldNeighborUtility.DeltaY[direction];
            int sz = FlowFieldNeighborUtility.DeltaZ[direction];
            return 2 * (sx * dx + sy * dy + sz * dz) - (sx * sx + sy * sy + sz * sz);
        }

        internal static Vector3 CellCenter(FlowFieldGridSpace grid, int index)
        {
            grid.FromFlatIndex(index, out int x, out int y, out int z);
            return grid.LocalToWorldCenter(x, y, z);
        }

        internal static Vector3 DirectionBetweenCells(
            FlowFieldGridSpace grid,
            int fromIndex,
            int toIndex)
        {
            grid.FromFlatIndex(fromIndex, out int fromX, out int fromY, out int fromZ);
            grid.FromFlatIndex(toIndex, out int toX, out int toY, out int toZ);
            return new Vector3(toX - fromX, toY - fromY, toZ - fromZ).normalized;
        }
    }
}
