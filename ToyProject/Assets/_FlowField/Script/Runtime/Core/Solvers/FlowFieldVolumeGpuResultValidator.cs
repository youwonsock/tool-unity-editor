using System;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    /// <summary>
    /// Validates a completed GPU chunk against its captured topology before applying it to staging.
    /// </summary>
    internal static class FlowFieldVolumeGpuResultValidator
    {
        internal static void Apply(
            BuildContext build,
            Unity.Collections.NativeArray<GpuFlowCell> result,
            int start,
            int end)
        {
            Vector3 defaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(
                build.Request.DefaultDirection);
            for (int index = start; index < end; index++)
            {
                GpuFlowCell cell = result[index];
                if (cell.NextCell < -3 || cell.NextCell >= build.Count
                    || !FlowFieldGridSpace.IsFinite(cell.Direction))
                    throw new InvalidOperationException("Volume3D GPU result contains an invalid cell.");
                if (cell.NextCell == -2 && !build.Blocked[index]
                    || cell.NextCell == -1 && (build.Blocked[index] || build.Influence[index])
                    || cell.NextCell == -3 && (build.Blocked[index] || !build.Influence[index]))
                    throw new InvalidOperationException("Volume3D GPU result contains an inconsistent sentinel.");
                if (cell.NextCell < 0
                    && cell.Direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                    throw new InvalidOperationException("Volume3D GPU sentinel cells must have zero direction.");

                if (cell.NextCell >= 0)
                {
                    if (build.Blocked[index] || !build.Influence[index])
                        throw new InvalidOperationException("Volume3D GPU directed a blocked or out-of-influence cell.");
                    if (cell.NextCell == index)
                    {
                        if (index != build.GoalIndex
                            || cell.Direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                            throw new InvalidOperationException("Volume3D GPU produced a non-goal anchor.");
                    }
                    else
                    {
                        build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                        build.Request.Grid.FromFlatIndex(cell.NextCell, out int nx, out int ny, out int nz);
                        int direction = FlowFieldNeighborUtility.FindDirectionIndex(nx - x, ny - y, nz - z);
                        if (direction < 0
                            || (build.Topology[index] & (1u << direction)) == 0u
                            || !CanTraverse(
                                build,
                                index,
                                x,
                                y,
                                z,
                                direction,
                                out int traversable)
                            || traversable != cell.NextCell
                            || !build.Influence[cell.NextCell])
                            throw new InvalidOperationException("Volume3D GPU produced a non-topological NextCell.");
                        if (cell.Direction.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                            || Mathf.Abs(cell.Direction.magnitude - 1f) > 0.001f)
                            throw new InvalidOperationException("Volume3D GPU produced an invalid direction.");
                        Vector3 expected = DirectionBetweenCells(
                            build.Request.Grid,
                            index,
                            cell.NextCell);
                        if ((cell.Direction - expected).sqrMagnitude > 1e-10f)
                            throw new InvalidOperationException("Volume3D GPU direction differs from the deterministic CPU direction.");
                    }
                }

                build.Next[index] = cell.NextCell;
                build.Directions[index] = cell.NextCell == -1
                    ? defaultDirection
                    : cell.NextCell >= 0
                        ? cell.NextCell == index
                            ? Vector3.zero
                            : DirectionBetweenCells(
                                build.Request.Grid,
                                index,
                                cell.NextCell)
                        : Vector3.zero;
                build.Speeds[index] = build.Directions[index].sqrMagnitude > 0.00000001f ? 1f : 0f;
                build.GoalFlags[index] = cell.NextCell >= 0
                    ? FlowFieldGoalFlags.Directed
                    : cell.NextCell == -3
                        ? FlowFieldGoalFlags.Unreachable
                        : FlowFieldGoalFlags.None;
                if (cell.NextCell == index)
                    build.GoalFlags[index] |= FlowFieldGoalFlags.Anchor;
            }
        }
    }
}
