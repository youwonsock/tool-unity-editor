using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Immutable metadata for the currently published field. It deliberately
    /// exposes no mutable session, workspace, or backing arrays.
    /// </summary>
    public readonly struct FlowFieldFieldInfo
    {
        /// <summary>Whether this describes a field; false for a default value.</summary>
        public bool IsValid { get; }
        /// <summary>The grid of the published field.</summary>
        public FlowFieldGridSpace Grid { get; }
        /// <summary>The world-space Bake Bounds the published grid was laid out from.</summary>
        public Bounds WorldBounds { get; }
        /// <summary>The space mode of the field.</summary>
        public FlowFieldSpaceMode SpaceMode { get; }
        /// <summary>The bake mode of the session.</summary>
        public FlowFieldBakeMode BakeMode { get; }
        /// <summary>The provider's <see cref="IFlowFieldProvider.Revision"/> when this was read.</summary>
        public int Revision { get; }
        /// <summary>
        /// Whether the latest request has a Goal. The requested Goal and its radius describe the
        /// latest request, which a build may not have published yet.
        /// </summary>
        public bool HasRequestedGoal { get; }
        /// <summary>The Goal position the latest request asked for, in world space.</summary>
        public Vector3 RequestedGoalWorld { get; }
        /// <summary>The latest request's Goal Influence Radius in world units; 0 reaches every cell.</summary>
        public float GoalInfluenceRadius { get; }
        /// <summary>Whether the published field resolved its Goal to a cell.</summary>
        public bool HasResolvedGoal { get; }
        /// <summary>
        /// The center of the published field's Goal cell in world space; on Surface2D at the
        /// ground height of the cell.
        /// </summary>
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
