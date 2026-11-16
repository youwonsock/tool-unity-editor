using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// A published field, the same shape in both space modes. Once published it
    /// never changes; a new publication replaces the whole object, and the
    /// arrays it replaced go back to <see cref="FlowFieldSpareArrays"/>. A reader
    /// outside the session may therefore use a field only until the next pump.
    /// Arrays a mode does not keep are null.
    /// </summary>
    internal class FlowFieldField
    {
        internal readonly FlowFieldGridSpace Grid;
        // Surface2D only: heights, normals and which cells have a surface.
        internal readonly FlowFieldSurfaceData Surface;
        internal readonly bool[] Blocked;
        // Surface2D uses 8 bits, Volume3D 26.
        internal readonly uint[] Topology;
        internal readonly FlowFieldGoalFlags[] GoalFlags;
        internal readonly int[] NextCells;
        internal readonly Vector3[] BaseDirections;
        internal readonly float[] BaseSpeeds;
        internal readonly Vector3[] FinalDirections;
        internal readonly float[] FinalSpeeds;
        // Volume3D only; Surface2D reconstructs escape vectors when asked.
        internal readonly Vector3[] EscapeDirections;
        // Surface2D only: the cells a Modifier touched.
        internal readonly bool[] ModifierInfluence;
        internal readonly bool HasActiveGoal;
        internal readonly int GoalIndex;

        internal int Capacity => Blocked.Length;

        internal FlowFieldField(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            bool[] blocked,
            uint[] topology,
            FlowFieldGoalFlags[] goalFlags,
            int[] nextCells,
            Vector3[] baseDirections,
            float[] baseSpeeds,
            Vector3[] finalDirections,
            float[] finalSpeeds,
            Vector3[] escapeDirections,
            bool[] modifierInfluence,
            bool hasActiveGoal,
            int goalIndex)
        {
            Grid = grid;
            Surface = surface;
            Blocked = blocked;
            Topology = topology;
            GoalFlags = goalFlags;
            NextCells = nextCells;
            BaseDirections = baseDirections;
            BaseSpeeds = baseSpeeds;
            FinalDirections = finalDirections;
            FinalSpeeds = finalSpeeds;
            EscapeDirections = escapeDirections;
            ModifierInfluence = modifierInfluence;
            HasActiveGoal = hasActiveGoal;
            GoalIndex = goalIndex;
        }
    }

    /// <summary>A published field together with the request it was built for.</summary>
    internal sealed class FlowFieldField<TRequest> : FlowFieldField where TRequest : struct
    {
        internal readonly TRequest Request;

        internal FlowFieldField(
            in TRequest request,
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            bool[] blocked,
            uint[] topology,
            FlowFieldGoalFlags[] goalFlags,
            int[] nextCells,
            Vector3[] baseDirections,
            float[] baseSpeeds,
            Vector3[] finalDirections,
            float[] finalSpeeds,
            Vector3[] escapeDirections,
            bool[] modifierInfluence,
            bool hasActiveGoal,
            int goalIndex)
            : base(
                grid,
                surface,
                blocked,
                topology,
                goalFlags,
                nextCells,
                baseDirections,
                baseSpeeds,
                finalDirections,
                finalSpeeds,
                escapeDirections,
                modifierInfluence,
                hasActiveGoal,
                goalIndex)
        {
            Request = request;
        }
    }
}
