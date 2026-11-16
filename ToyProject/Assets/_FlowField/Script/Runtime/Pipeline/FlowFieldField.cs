using Unity.Collections.LowLevel.Unsafe;
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

        /// <summary>
        /// Whether two fields answer every read the same: the cells, their
        /// state and the Goal. A publication with the same content keeps the
        /// Revision, so callers see a change only when there is one.
        /// </summary>
        internal static bool HasSameContent(FlowFieldField left, FlowFieldField right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null
                || !left.Grid.MatchesBounds(right.Grid)
                || left.HasActiveGoal != right.HasActiveGoal
                || left.GoalIndex != right.GoalIndex)
                return false;
            if (!ReferenceEquals(left.Surface, right.Surface)
                && (left.Surface == null || right.Surface == null || !left.Surface.ContentEquals(right.Surface)))
                return false;
            return SameBytes(left.Blocked, right.Blocked)
                && SameBytes(left.Topology, right.Topology)
                && SameBytes(left.GoalFlags, right.GoalFlags)
                && SameBytes(left.NextCells, right.NextCells)
                && SameBytes(left.FinalDirections, right.FinalDirections)
                && SameBytes(left.FinalSpeeds, right.FinalSpeeds)
                && SameBytes(left.EscapeDirections, right.EscapeDirections)
                && SameBytes(left.ModifierInfluence, right.ModifierInfluence);
        }

        // A bitwise comparison: memcmp over each array.
        private static unsafe bool SameBytes<T>(T[] left, T[] right) where T : unmanaged
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            if (left.Length == 0)
                return true;
            fixed (T* a = left)
            fixed (T* b = right)
                return UnsafeUtility.MemCmp(a, b, (long)left.Length * sizeof(T)) == 0;
        }

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
