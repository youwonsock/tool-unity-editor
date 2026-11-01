using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Arrays of the field a new publication replaced, reused by the next build.
    /// Allocating a 100³ build every time a Goal moves collected often enough to
    /// exceed the frame budget. An array still shared with the new field, or a
    /// field whose arrays were handed out as a view, stays out of here.
    /// </summary>
    internal sealed class FlowFieldVolumeSpareArrays
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

        internal bool IsEmpty => Blocked == null && Topology == null && GoalFlags == null && Next == null
            && Directions == null && Speeds == null && FinalDirections == null && FinalSpeeds == null
            && EscapeDirections == null && Costs == null && Queue == null && Influence == null;

        internal long Bytes => (Blocked?.Length ?? 0)
            + (Influence?.Length ?? 0)
            + 4L * (Topology?.Length ?? 0)
            + (GoalFlags?.Length ?? 0)
            + 4L * (Next?.Length ?? 0)
            + 12L * (Directions?.Length ?? 0)
            + 4L * (Speeds?.Length ?? 0)
            + 12L * (FinalDirections?.Length ?? 0)
            + 4L * (FinalSpeeds?.Length ?? 0)
            + 12L * (EscapeDirections?.Length ?? 0)
            + 4L * (Costs?.Length ?? 0)
            + 4L * (Queue?.Length ?? 0);

        /// <summary>
        /// Takes the spare array when it has the requested size and clears it,
        /// which leaves it in the same state as a new allocation.
        /// </summary>
        internal static T[] Rent<T>(ref T[] spare, int count)
        {
            T[] array = spare;
            spare = null;
            if (array == null || array.Length != count)
                return new T[count];
            Array.Clear(array, 0, count);
            return array;
        }

        internal void Retire(FlowFieldVolumeFieldData replaced, FlowFieldVolumeFieldData next)
        {
            if (replaced == null || replaced.HandedOut)
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
        }

        private static void Keep<T>(ref T[] slot, T[] replaced, T[] current)
        {
            if (!ReferenceEquals(replaced, current))
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
        }
    }
}
