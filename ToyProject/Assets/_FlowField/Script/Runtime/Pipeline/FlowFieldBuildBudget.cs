using System;

namespace Common.FlowField
{
    /// <summary>How much of a frame's main-thread budget a build step may still use.</summary>
    internal readonly struct FlowFieldBuildBudget
    {
        // For a caller that pumps directly and waits for the result.
        internal static readonly FlowFieldBuildBudget Unlimited = new FlowFieldBuildBudget(double.MaxValue, () => 0d);

        private readonly double _deadlineMilliseconds;
        private readonly Func<double> _elapsedMilliseconds;

        internal FlowFieldBuildBudget(
            double deadlineMilliseconds,
            Func<double> elapsedMilliseconds)
        {
            _deadlineMilliseconds = Math.Max(0d, deadlineMilliseconds);
            _elapsedMilliseconds = elapsedMilliseconds
                ?? throw new ArgumentNullException(nameof(elapsedMilliseconds));
        }

        internal bool IsExpired => _elapsedMilliseconds() >= _deadlineMilliseconds;
    }
}
