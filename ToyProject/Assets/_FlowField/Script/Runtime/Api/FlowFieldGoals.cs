using System;

namespace Common.FlowField
{
    /// <summary>What an additional Goal from <see cref="IFlowFieldController.AddGoal"/> is for.</summary>
    public enum FlowFieldGoalKind
    {
        /// <summary>
        /// A destination many agents share, such as an exit. Shared Goals are what
        /// <see cref="IFlowFieldProvider.TrySampleNearestShared"/> chooses from.
        /// </summary>
        Shared,
        /// <summary>
        /// A destination one group follows, which may move often. Group Goals build in batches of
        /// their own, ahead of Shared Goals but never twice in a row while a Shared Goal waits, so
        /// a moving Group Goal's field never waits for every Shared Goal.
        /// </summary>
        Group,
    }

    /// <summary>
    /// An additional Goal of a Manager, besides its default Goal. Each one gets its own field on
    /// the same ground and obstacles. The default value names no Goal, and a handle stops naming
    /// its Goal once it is removed or the session is released; it never names a later Goal or a
    /// Goal of another Manager. A world Goal's handle, from <see cref="FlowFieldWorld.AddGoal"/>,
    /// names that Goal in every area of the world.
    /// </summary>
    public readonly struct FlowFieldGoalHandle : IEquatable<FlowFieldGoalHandle>
    {
        internal readonly int Id;

        internal FlowFieldGoalHandle(int id)
        {
            Id = id;
        }

        /// <summary>False for the default value, which names no Goal.</summary>
        public bool IsValid => Id > 0;

        /// <summary>Whether both name the same Goal.</summary>
        public bool Equals(FlowFieldGoalHandle other) => Id == other.Id;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is FlowFieldGoalHandle other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => Id;

        /// <summary>Whether both name the same Goal.</summary>
        public static bool operator ==(FlowFieldGoalHandle left, FlowFieldGoalHandle right) => left.Id == right.Id;

        /// <summary>Whether they name different Goals.</summary>
        public static bool operator !=(FlowFieldGoalHandle left, FlowFieldGoalHandle right) => left.Id != right.Id;

        /// <inheritdoc/>
        public override string ToString() => IsValid ? "Goal " + Id : "No Goal";
    }
}
