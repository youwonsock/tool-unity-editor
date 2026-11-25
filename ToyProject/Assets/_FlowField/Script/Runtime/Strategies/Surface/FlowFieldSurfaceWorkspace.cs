using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The managed arrays of a Surface2D build. The builder stages every build
    /// in one workspace and publishes it into the compact field store, so a
    /// build that spans frames never exposes partly written cells. A bake
    /// loads into it and a bake export copies the store into a new one.
    /// </summary>
    internal sealed class FlowFieldSurfaceWorkspace
    {
        public bool[] Blocked { get; private set; }
        public bool[] ObstacleScratch { get; private set; }
        public bool[] StaticBlocked { get; private set; }
        public bool[] DynamicBlocked { get; private set; }
        // Every obstacle Collider the sweep made the session watch; part of DynamicBlocked.
        public bool[] SweptBlocked { get; private set; }
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
        // Final eight-neighbor topology after Surface, obstacle, influence and
        // diagonal-corner checks. The solve job consumes this mask.
        internal byte[] TopologyMasks { get; private set; }
        internal int ResolvedGoalIndex { get; private set; } = -1;
        internal bool HasActiveGoal { get; private set; }

        public int Capacity => Blocked == null ? 0 : Blocked.Length;

        // Set while a scheduled Surface solve job holds pinned pointers into
        // these arrays. The Job safety system cannot see managed arrays, so
        // main-thread writers check this in development builds instead.
        internal bool SolveInFlight { get; set; }

        /// <summary>Logs an assertion when the main thread writes while a solve job runs.</summary>
        [System.Diagnostics.Conditional("UNITY_ASSERTIONS")]
        internal void AssertWritable(string writer)
        {
            if (SolveInFlight)
                UnityEngine.Debug.LogAssertion(
                    $"[FlowField] {writer} wrote the Surface workspace while its solve job was running.");
        }

        public bool Resize(int cellCount)
        {
            AssertWritable(nameof(Resize));
            if (cellCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(cellCount), cellCount, "Workspace capacity must be positive.");

            if (Capacity == cellCount)
                return false;

            Release();
            Blocked = new bool[cellCount];
            ObstacleScratch = new bool[cellCount];
            StaticBlocked = new bool[cellCount];
            DynamicBlocked = new bool[cellCount];
            SweptBlocked = new bool[cellCount];
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
                NextCells[i] = FlowFieldNextCell.DefaultDirection;
            ResolvedGoalIndex = -1;
            HasActiveGoal = false;
            return true;
        }

        public void Release()
        {
            AssertWritable(nameof(Release));
            Blocked = null;
            ObstacleScratch = null;
            StaticBlocked = null;
            DynamicBlocked = null;
            SweptBlocked = null;
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
        }

        public void RebuildCombinedBlocked()
        {
            AssertWritable(nameof(RebuildCombinedBlocked));
            int count = Capacity;
            for (int i = 0; i < count; i++)
                Blocked[i] = StaticBlocked[i] || DynamicBlocked[i];
        }

        public void ClearGoal()
        {
            AssertWritable(nameof(ClearGoal));
            if (GoalDirections == null)
                return;

            Array.Clear(GoalDirections, 0, GoalDirections.Length);
            Array.Clear(GoalFlags, 0, GoalFlags.Length);
            Array.Clear(InfluenceMask, 0, InfluenceMask.Length);
            Array.Clear(Costs, 0, Costs.Length);
            for (int i = 0; i < NextCells.Length; i++)
                NextCells[i] = FlowFieldNextCell.DefaultDirection;
            ResolvedGoalIndex = -1;
            HasActiveGoal = false;
        }

        internal void SetResolvedGoal(int index)
        {
            AssertWritable(nameof(SetResolvedGoal));
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
            AssertWritable(nameof(LoadBakedGoal));
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
                else if (next == FlowFieldNextCell.Unreachable)
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
            AssertWritable(nameof(ClearAll));
            if (Capacity <= 0)
                return;

            Array.Clear(Blocked, 0, Capacity);
            Array.Clear(ObstacleScratch, 0, Capacity);
            Array.Clear(StaticBlocked, 0, Capacity);
            Array.Clear(DynamicBlocked, 0, Capacity);
            Array.Clear(SweptBlocked, 0, Capacity);
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
