using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Completed Volume3D navigation and composition arrays, published together by the session.
    /// </summary>
    internal sealed class FlowFieldVolumeFieldData
    {
        internal readonly FlowFieldVolumeRequest Request;
        internal FlowFieldGridSpace Grid => Request.Grid;
        internal readonly bool[] Blocked;
        internal readonly uint[] Topology;
        internal readonly FlowFieldGoalFlags[] GoalFlags;
        internal readonly int GoalIndex;
        internal readonly int[] NextCells;
        internal readonly Vector3[] BaseDirections;
        internal readonly float[] BaseSpeeds;
        internal readonly Vector3[] FinalDirections;
        internal readonly float[] FinalSpeeds;
        internal readonly Vector3[] EscapeDirections;
        internal readonly long ResultId;
        // Set when a view exposes these arrays, which then must never be reused.
        internal bool HandedOut;

        internal FlowFieldVolumeFieldData(
            in FlowFieldVolumeRequest request,
            bool[] blocked,
            uint[] topology,
            FlowFieldGoalFlags[] goalFlags,
            int[] nextCells,
            Vector3[] baseDirections,
            float[] baseSpeeds,
            Vector3[] finalDirections,
            float[] finalSpeeds,
            Vector3[] escapeDirections,
            int goalIndex,
            long resultId)
        {
            Request = request;
            // The solver/StaticBake payload has already resolved and
            // validated the goal. Do not infer it again from an Anchor;
            // an accidental non-goal anchor must never change the
            // published goal identity.
            GoalIndex = request.HasGoal ? goalIndex : -1;
            Blocked = blocked;
            Topology = topology;
            GoalFlags = goalFlags;
            NextCells = nextCells;
            BaseDirections = baseDirections;
            BaseSpeeds = baseSpeeds;
            FinalDirections = finalDirections;
            FinalSpeeds = finalSpeeds;
            EscapeDirections = escapeDirections;
            ResultId = resultId;
        }
    }
}
