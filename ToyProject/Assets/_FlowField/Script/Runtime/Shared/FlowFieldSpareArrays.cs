using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Arrays of the field a new publication replaced, reused by the next build
    /// in either space mode. Allocating a 100³ build every time a Goal moves
    /// collected often enough to exceed the frame budget. An array still shared
    /// with the new field stays out of here.
    /// </summary>
    internal sealed class FlowFieldSpareArrays
    {
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
        internal bool[] ModifierInfluence;

        /// <summary>
        /// Takes the spare array when it has the requested size, or allocates
        /// one. A spare still holds the field it came from and is not cleared:
        /// every caller writes each element before anything reads it.
        /// </summary>
        internal static T[] Take<T>(ref T[] spare, int count)
        {
            T[] array = spare;
            spare = null;
            return array != null && array.Length == count ? array : new T[count];
        }

        internal void Retire(FlowFieldField replaced, FlowFieldField next)
        {
            if (replaced == null)
                return;
            Keep(ref Blocked, replaced.Blocked, next.Blocked);
            Keep(ref Topology, replaced.Topology, next.Topology);
            Keep(ref GoalFlags, replaced.GoalFlags, next.GoalFlags);
            Keep(ref Next, replaced.NextCells, next.NextCells);
            Keep(ref Directions, replaced.BaseDirections, next.BaseDirections);
            Keep(ref Speeds, replaced.BaseSpeeds, next.BaseSpeeds);
            Keep(ref FinalDirections, replaced.FinalDirections, next.FinalDirections);
            Keep(ref FinalSpeeds, replaced.FinalSpeeds, next.FinalSpeeds);
            Keep(ref EscapeDirections, replaced.EscapeDirections, next.EscapeDirections);
            Keep(ref ModifierInfluence, replaced.ModifierInfluence, next.ModifierInfluence);
        }

        // A mode that does not keep an array leaves the spare it has alone.
        private static void Keep<T>(ref T[] slot, T[] replaced, T[] current)
        {
            if (replaced != null && !ReferenceEquals(replaced, current))
                slot = replaced;
        }

        internal void Clear()
        {
            Blocked = null;
            Topology = null;
            GoalFlags = null;
            Next = null;
            Directions = null;
            Speeds = null;
            FinalDirections = null;
            FinalSpeeds = null;
            EscapeDirections = null;
            Costs = null;
            Queue = null;
            Influence = null;
            ModifierInfluence = null;
        }
    }
}
