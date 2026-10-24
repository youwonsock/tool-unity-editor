using System;

namespace Common.FlowField
{
    internal readonly struct FlowFieldBuildBudget
    {
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
