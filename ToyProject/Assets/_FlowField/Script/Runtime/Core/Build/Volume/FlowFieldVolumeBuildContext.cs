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
}
