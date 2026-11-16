using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Immutable metadata for the currently published field. It deliberately
    /// exposes no mutable session, workspace, or backing arrays.
    /// </summary>
    public readonly struct FlowFieldFieldInfo
    {
        public bool IsValid { get; }
        public FlowFieldGridSpace Grid { get; }
        public Bounds WorldBounds { get; }
        public FlowFieldSpaceMode SpaceMode { get; }
        public FlowFieldBakeMode BakeMode { get; }
        public int Revision { get; }
        public bool HasRequestedGoal { get; }
        public Vector3 RequestedGoalWorld { get; }
        public float GoalInfluenceRadius { get; }
        public bool HasResolvedGoal { get; }
        public Vector3 ResolvedGoalWorld { get; }

        internal FlowFieldFieldInfo(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldSpaceMode spaceMode,
            FlowFieldBakeMode bakeMode,
            int revision,
            bool hasRequestedGoal,
            Vector3 requestedGoalWorld,
            float goalInfluenceRadius,
            bool hasResolvedGoal,
            Vector3 resolvedGoalWorld)
        {
            IsValid = grid.IsValid;
            Grid = grid;
            WorldBounds = worldBounds;
            SpaceMode = spaceMode;
            BakeMode = bakeMode;
            Revision = revision;
            HasRequestedGoal = hasRequestedGoal;
            RequestedGoalWorld = requestedGoalWorld;
            GoalInfluenceRadius = goalInfluenceRadius;
            HasResolvedGoal = hasResolvedGoal;
            ResolvedGoalWorld = resolvedGoalWorld;
        }

        internal static FlowFieldFieldInfo Invalid => default;
    }
}
