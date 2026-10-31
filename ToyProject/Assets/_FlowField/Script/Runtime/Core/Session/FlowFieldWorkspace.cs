using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Managed backing for FlowField rebuild. GPU readback storage is owned by the
    /// compute solver; a Manager owns one staging workspace and one committed
    /// workspace so asynchronous builds cannot expose partially written data.
    /// </summary>
    internal sealed class FlowFieldWorkspace
    {
        public bool[] Blocked { get; private set; }
        public bool[] ObstacleScratch { get; private set; }
        public bool[] StaticBlocked { get; private set; }
        public bool[] DynamicBlocked { get; private set; }
        public Vector3[] EscapeDirections { get; private set; }
        public Vector3[] GoalDirections { get; private set; }
        public FlowFieldGoalFlags[] GoalFlags { get; private set; }
        public Vector3[] FinalDirections { get; private set; }
        public float[] FinalSpeedMultipliers { get; private set; }
        internal Vector3[] BaseDirections { get; private set; }
        internal float[] BaseSpeedMultipliers { get; private set; }
        public bool[] ModifierInfluence { get; private set; }
        internal bool[] InfluenceMask { get; private set; }
        internal int[] Costs { get; private set; }
        internal int[] Queue { get; private set; }
        internal int[] NextCells { get; private set; }
        // Final eight-neighbour topology after Surface, obstacle, influence and
        // diagonal-corner checks. Managed and GPU backends consume this same mask.
        internal byte[] TopologyMasks { get; private set; }
        internal int ResolvedGoalIndex { get; private set; } = -1;
        internal bool HasActiveGoal { get; private set; }

        public int Capacity => Blocked == null ? 0 : Blocked.Length;
        public bool HasBlockedCells { get; private set; }

        public bool Resize(int cellCount)
        {
            if (cellCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellCount), cellCount, "Workspace capacity must be positive.");

            if (Capacity == cellCount)
                return false;

            Release();
            Blocked = new bool[cellCount];
            ObstacleScratch = new bool[cellCount];
            StaticBlocked = new bool[cellCount];
            DynamicBlocked = new bool[cellCount];
            EscapeDirections = new Vector3[cellCount];
            GoalDirections = new Vector3[cellCount];
            GoalFlags = new FlowFieldGoalFlags[cellCount];
            FinalDirections = new Vector3[cellCount];
            FinalSpeedMultipliers = new float[cellCount];
            BaseDirections = new Vector3[cellCount];
            BaseSpeedMultipliers = new float[cellCount];
            ModifierInfluence = new bool[cellCount];
            InfluenceMask = new bool[cellCount];
            Costs = new int[cellCount];
            Queue = new int[cellCount];
            NextCells = new int[cellCount];
            TopologyMasks = new byte[cellCount];
            for (int i = 0; i < cellCount; i++)
                NextCells[i] = -1;
            ResolvedGoalIndex = -1;
            HasActiveGoal = false;
            return true;
        }

        public void Release()
        {
            Blocked = null;
            ObstacleScratch = null;
            StaticBlocked = null;
            DynamicBlocked = null;
            EscapeDirections = null;
            GoalDirections = null;
            GoalFlags = null;
            FinalDirections = null;
            FinalSpeedMultipliers = null;
            BaseDirections = null;
            BaseSpeedMultipliers = null;
            ModifierInfluence = null;
            InfluenceMask = null;
            Costs = null;
            Queue = null;
            NextCells = null;
            TopologyMasks = null;
            ResolvedGoalIndex = -1;
            HasActiveGoal = false;
            HasBlockedCells = false;
        }

        public void RebuildCombinedBlocked()
        {
            int count = Capacity;
            HasBlockedCells = false;
            for (int i = 0; i < count; i++)
            {
                Blocked[i] = StaticBlocked[i] || DynamicBlocked[i];
                HasBlockedCells |= Blocked[i];
            }
        }

        public void ClearGoal()
        {
            if (GoalDirections == null)
                return;

            Array.Clear(GoalDirections, 0, GoalDirections.Length);
            Array.Clear(GoalFlags, 0, GoalFlags.Length);
            Array.Clear(InfluenceMask, 0, InfluenceMask.Length);
            Array.Clear(Costs, 0, Costs.Length);
            for (int i = 0; i < NextCells.Length; i++)
                NextCells[i] = -1;
            ResolvedGoalIndex = -1;
            HasActiveGoal = false;
        }

        internal void SetResolvedGoal(int index)
        {
            if (index < 0 || index >= Capacity)
                throw new ArgumentOutOfRangeException(nameof(index));
            ResolvedGoalIndex = index;
            HasActiveGoal = true;
        }

        internal void LoadBakedGoal(
            bool hasGoal,
            int resolvedGoalIndex,
            int[] nextCells)
        {
            if (nextCells == null || nextCells.Length != Capacity)
                throw new ArgumentException("Baked Goal NextCell data must match the workspace capacity.", nameof(nextCells));
            if (hasGoal && (resolvedGoalIndex < 0 || resolvedGoalIndex >= Capacity))
                throw new ArgumentOutOfRangeException(nameof(resolvedGoalIndex));

            Array.Clear(GoalFlags, 0, GoalFlags.Length);
            Array.Clear(InfluenceMask, 0, InfluenceMask.Length);
            for (int index = 0; index < Capacity; index++)
            {
                if (!hasGoal)
                    continue;

                int next = nextCells[index];
                if (next >= 0)
                {
                    InfluenceMask[index] = true;
                    GoalFlags[index] = FlowFieldGoalFlags.Directed;
                    if (next == index)
                        GoalFlags[index] |= FlowFieldGoalFlags.Anchor;
                }
                else if (next == -3)
                {
                    InfluenceMask[index] = true;
                    GoalFlags[index] = FlowFieldGoalFlags.Unreachable;
                }
            }

            HasActiveGoal = hasGoal;
            ResolvedGoalIndex = hasGoal ? resolvedGoalIndex : -1;
        }

        public void ClearAll()
        {
            if (Capacity <= 0)
                return;

            Array.Clear(Blocked, 0, Capacity);
            Array.Clear(ObstacleScratch, 0, Capacity);
            Array.Clear(StaticBlocked, 0, Capacity);
            Array.Clear(DynamicBlocked, 0, Capacity);
            HasBlockedCells = false;
            Array.Clear(EscapeDirections, 0, Capacity);
            ClearGoal();
            Array.Clear(TopologyMasks, 0, Capacity);
            Array.Clear(BaseDirections, 0, Capacity);
            Array.Clear(BaseSpeedMultipliers, 0, Capacity);
            Array.Clear(FinalDirections, 0, Capacity);
            Array.Clear(FinalSpeedMultipliers, 0, Capacity);
            Array.Clear(ModifierInfluence, 0, Capacity);
            ResolvedGoalIndex = -1;
            HasActiveGoal = false;
        }
    }
}
