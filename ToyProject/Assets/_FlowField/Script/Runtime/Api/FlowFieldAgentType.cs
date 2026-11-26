using System;

namespace Common.FlowField
{
    /// <summary>
    /// An agent type of a Manager: the obstacle clearance, and in Surface2D the slope and step
    /// height, its fields are built for. <see cref="Default"/> is the Manager's own Obstacle
    /// Clearance, Max Slope and Max Step Height; the Manager's Additional Agent Types follow it,
    /// found by name with <see cref="IFlowFieldProvider.TryGetAgentType"/>. A found type names the
    /// type of the sessions Init made; after an Init that makes them again (other types or Space
    /// Mode) or for another Manager, it names no type.
    /// </summary>
    public readonly struct FlowFieldAgentType : IEquatable<FlowFieldAgentType>
    {
        /// <summary>The name <see cref="IFlowFieldProvider.TryGetAgentType"/> takes for <see cref="Default"/>.</summary>
        public const string DefaultName = "Default";

        internal readonly int Index;
        // The sessions it was found for; 0 for Default.
        internal readonly int Stamp;

        internal FlowFieldAgentType(int index, int stamp)
        {
            Index = index;
            Stamp = stamp;
        }

        /// <summary>The Manager's own settings, which every call without an agent type reads.</summary>
        public static FlowFieldAgentType Default => default;

        /// <summary>Whether this is <see cref="Default"/>.</summary>
        public bool IsDefault => Index == 0;

        /// <summary>Whether both name the same agent type.</summary>
        public bool Equals(FlowFieldAgentType other) => Index == other.Index && Stamp == other.Stamp;

        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is FlowFieldAgentType other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => unchecked(Index * 397 ^ Stamp);

        /// <summary>Whether both name the same agent type.</summary>
        public static bool operator ==(FlowFieldAgentType left, FlowFieldAgentType right) => left.Equals(right);

        /// <summary>Whether they name different agent types.</summary>
        public static bool operator !=(FlowFieldAgentType left, FlowFieldAgentType right) => !left.Equals(right);

        /// <inheritdoc/>
        public override string ToString() => IsDefault ? DefaultName : "AgentType(" + Index + ")";
    }
}
