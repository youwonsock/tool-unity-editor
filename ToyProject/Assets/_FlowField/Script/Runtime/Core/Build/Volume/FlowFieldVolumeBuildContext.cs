using System;
using UnityEngine;

namespace Common.FlowField
{
    internal enum FlowFieldVolumeBuildPhase
    {
        Allocate,
        StaticData,
        Obstacles,
        Topology,
        Goal,
        Bfs,
        Directions,
        Escape,
        Modifiers,
        Complete,
    }

    /// <summary>
    /// Private workspace and continuation cursors for one Volume3D build.
    /// </summary>
    internal sealed class FlowFieldVolumeBuildContext
    {
        internal readonly FlowFieldVolumeRequest Request;
        internal readonly int Generation;
        internal readonly int SafetyEpoch;
        internal int Count => Request.Grid.CellCount;
        internal bool[] Blocked;
        internal uint[] Topology;
        internal FlowFieldGoalFlags[] GoalFlags;
        internal int[] Next;
        internal Vector3[] Directions;
        internal float[] Speeds;
        internal Vector3[] FinalDirections;
        internal float[] FinalSpeeds;
        internal Vector3[] EscapeDirections;
        internal int[] Costs;
        internal int[] Queue;
        internal bool[] Influence;
        internal readonly FlowFieldModifierWorkItem[] Modifiers;
        internal FlowFieldVolumeBuildPhase Phase;
        internal int Cursor;
        internal int Head;
        internal int Tail;
        internal int GoalIndex = -1;
        internal int RequestedGoalIndex = -1;
        internal long NearestGoalDistance = long.MaxValue;
        internal int GoalStage;
        internal double GoalRadiusSqr;
        internal int EscapeStage;
        internal int EscapeHead;
        internal int EscapeTail;
        internal bool GpuAttempted;
        internal bool GpuPending;
        internal Exception GpuFailed;
        internal Unity.Collections.NativeArray<GpuFlowCell> GpuResult;
        internal bool GpuResultPending;
        internal int GpuValidationCursor;
        internal int GpuFallbackResetCursor;
        internal int AllocationStage;
        internal FlowFieldVolumeInteriorOffsets InteriorOffsets;

        internal FlowFieldVolumeBuildContext(
            in FlowFieldVolumeRequest request,
            int generation,
            int safetyEpoch,
            FlowFieldModifierWorkItem[] modifiers)
        {
            Request = request;
            Generation = generation;
            SafetyEpoch = safetyEpoch;
            Modifiers = modifiers ?? Array.Empty<FlowFieldModifierWorkItem>();
            Phase = FlowFieldVolumeBuildPhase.Allocate;
        }

        internal void Dispose()
        {
            // Managed staging arrays are reclaimed by the runtime once no
            // build references them. The GPU readback NativeArray belongs
            // to FlowFieldComputeSolver and is intentionally not disposed
            // here; its callback storage must remain valid until the
            // solver consumes or releases it.
            GpuResult = default;
            GpuResultPending = false;
            GpuPending = false;
        }
    }

    /// <summary>
    /// Flat-index offsets of the 26 neighbors and of the corner cells a
    /// diagonal move passes, valid for cells not on the grid boundary. For
    /// those cells every neighbor is in bounds, so traversal reduces to
    /// Blocked lookups and matches <see cref="FlowFieldVolumeNavigation.CanTraverse"/>.
    /// </summary>
    internal sealed class FlowFieldVolumeInteriorOffsets
    {
        private readonly int[] _neighbor = new int[FlowFieldNeighborUtility.VolumeCount];
        private readonly int[] _cornerStart = new int[FlowFieldNeighborUtility.VolumeCount + 1];
        private readonly int[] _corner;

        internal FlowFieldVolumeInteriorOffsets(FlowFieldGridSpace grid)
        {
            var corners = new System.Collections.Generic.List<int>(96);
            for (int direction = 0; direction < FlowFieldNeighborUtility.VolumeCount; direction++)
            {
                int dx = FlowFieldNeighborUtility.DeltaX[direction];
                int dy = FlowFieldNeighborUtility.DeltaY[direction];
                int dz = FlowFieldNeighborUtility.DeltaZ[direction];
                _neighbor[direction] = Offset(grid, dx, dy, dz);
                _cornerStart[direction] = corners.Count;
                int maxXStep = dx == 0 ? 0 : 1;
                int maxYStep = dy == 0 ? 0 : 1;
                int maxZStep = dz == 0 ? 0 : 1;
                // Same enumeration as CanTraverse: every proper, non-empty
                // subset of the move's axes.
                for (int sx = 0; sx <= maxXStep; sx++)
                for (int sy = 0; sy <= maxYStep; sy++)
                for (int sz = 0; sz <= maxZStep; sz++)
                {
                    if (sx == 0 && sy == 0 && sz == 0
                        || sx == maxXStep && sy == maxYStep && sz == maxZStep)
                        continue;
                    corners.Add(Offset(grid, sx == 1 ? dx : 0, sy == 1 ? dy : 0, sz == 1 ? dz : 0));
                }
            }
            _cornerStart[FlowFieldNeighborUtility.VolumeCount] = corners.Count;
            _corner = corners.ToArray();
        }

        internal uint TopologyMask(bool[] blocked, int index)
        {
            uint mask = 0u;
            for (int direction = 0; direction < FlowFieldNeighborUtility.VolumeCount; direction++)
            {
                if (blocked[index + _neighbor[direction]])
                    continue;
                bool open = true;
                for (int corner = _cornerStart[direction]; corner < _cornerStart[direction + 1]; corner++)
                {
                    if (blocked[index + _corner[corner]])
                    {
                        open = false;
                        break;
                    }
                }
                if (open)
                    mask |= 1u << direction;
            }
            return mask;
        }

        private static int Offset(FlowFieldGridSpace grid, int dx, int dy, int dz)
            => dx + grid.Width * (dz + grid.Depth * dy);
    }
}
