using System;

namespace Common.FlowField
{
    /// <summary>
    /// How long FlowField's main-thread work may run each frame, shared by every
    /// Manager in the project. It does not limit solves on worker threads, and a
    /// stretch that cannot be divided, such as Physics reads, can exceed it.
    /// </summary>
    public static class FlowFieldBuildSettings
    {
        private static double _budgetMilliseconds = 2d;

        public static double BudgetMilliseconds
        {
            get => _budgetMilliseconds;
            set
            {
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0d)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _budgetMilliseconds = value;
            }
        }
    }

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

    internal interface IFlowFieldBuildOperation
    {
        bool IsWaitingForExternalCompletion { get; }
        bool IsBuilding { get; }

        void PumpInternal(in FlowFieldBuildBudget budget);
    }
}
