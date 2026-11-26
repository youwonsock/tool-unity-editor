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
        internal uint[] GroundTopology;
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

        /// <summary>
        /// Keeps the arrays of the replaced field that the next one does not
        /// share. An agent layer an additional Goal's field still reads
        /// (<paramref name="agentHeld"/>) keeps its arrays, Volume3D's Goal
        /// topology with them, since it is the same array.
        /// </summary>
        internal void Retire(FlowFieldField replaced, FlowFieldField next, bool agentHeld = false)
        {
            if (replaced == null)
                return;
            // Volume3D's ground topology is its Goal topology, kept once below.
            bool sharedTopology = ReferenceEquals(replaced.Agent.GroundTopology, replaced.Goal.Topology);
            if (!agentHeld)
            {
                Keep(ref Blocked, replaced.Agent.Blocked, next.Agent.Blocked);
                if (!sharedTopology)
                    Keep(ref GroundTopology, replaced.Agent.GroundTopology, next.Agent.GroundTopology);
                Keep(ref EscapeDirections, replaced.Agent.EscapeDirections, next.Agent.EscapeDirections);
            }
            if (!agentHeld || !sharedTopology)
                Keep(ref Topology, replaced.Goal.Topology, next.Goal.Topology);
            Keep(ref GoalFlags, replaced.Goal.Flags, next.Goal.Flags);
            Keep(ref Next, replaced.Goal.NextCells, next.Goal.NextCells);
            Keep(ref Directions, replaced.Goal.BaseDirections, next.Goal.BaseDirections);
            Keep(ref Speeds, replaced.Goal.BaseSpeeds, next.Goal.BaseSpeeds);
            Keep(ref FinalDirections, replaced.Goal.FinalDirections, next.Goal.FinalDirections);
            Keep(ref FinalSpeeds, replaced.Goal.FinalSpeeds, next.Goal.FinalSpeeds);
            Keep(ref ModifierInfluence, replaced.Goal.ModifierInfluence, next.Goal.ModifierInfluence);
        }

        /// <summary>
        /// An additional Goal's layer was replaced: its own arrays, the
        /// distances included, come back here. The topology and the agent
        /// arrays belong to the published field it was built on.
        /// </summary>
        internal void RetireGoal(FlowFieldGoalLayer replaced, FlowFieldGoalLayer next)
        {
            if (replaced == null)
                return;
            Keep(ref GoalFlags, replaced.Flags, next.Flags);
            Keep(ref Next, replaced.NextCells, next.NextCells);
            Keep(ref Directions, replaced.BaseDirections, next.BaseDirections);
            Keep(ref Speeds, replaced.BaseSpeeds, next.BaseSpeeds);
            Keep(ref FinalDirections, replaced.FinalDirections, next.FinalDirections);
            Keep(ref FinalSpeeds, replaced.FinalSpeeds, next.FinalSpeeds);
            Keep(ref Costs, replaced.Distances, next.Distances);
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
            GroundTopology = null;
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
