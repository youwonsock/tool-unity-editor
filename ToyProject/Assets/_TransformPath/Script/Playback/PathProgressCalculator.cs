using System;

namespace Common.TransformPath
{
    internal readonly struct PathProgressStep
    {
        /// <summary>The position after the step: elapsed time or distance.</summary>
        public double Position { get; }
        public float ConsumedTime { get; }
        /// <summary>Whether the step got to its limit. It then sits on the limit exactly.</summary>
        public bool Reached { get; }

        public PathProgressStep(double position, float consumedTime, bool reached)
        {
            Position = position;
            ConsumedTime = consumedTime;
            Reached = reached;
        }
    }

    /// <summary>
    /// How far a position moves toward a limit in a slice of time. The position
    /// never moves back, and a step that gets to the limit lands on it exactly,
    /// so no caller has to decide later from a rounded value whether it arrived.
    /// </summary>
    internal static class PathProgressCalculator
    {
        /// <param name="position">Elapsed time or distance so far.</param>
        /// <param name="limit">The elapsed time or distance the step may not pass.</param>
        /// <param name="rate">Position units per second; positive.</param>
        public static PathProgressStep AdvanceToward(double position, double limit, double rate, float availableTime)
        {
            if (limit <= position)
                return new PathProgressStep(position, 0f, true);
            double needed = (limit - position) / rate;
            double next = position + availableTime * rate;
            if (needed <= availableTime || next >= limit)
                return new PathProgressStep(limit, (float)Math.Min(needed, availableTime), true);
            return new PathProgressStep(next, availableTime, false);
        }
    }
}
