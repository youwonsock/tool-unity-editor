using System;

namespace Common.FlowField
{
    internal enum FlowFieldBuildOwner
    {
        Runtime = 0,
        EditorBake = 1,
        EditorPreview = 2,
        Test = 3,
    }

    internal enum FlowFieldBuildOperationState
    {
        Pending = 0,
        Running = 1,
        Waiting = 2,
        Completed = 3,
        Faulted = 4,
        Cancelled = 5,
    }

    internal readonly struct FlowFieldBuildBudget
    {
        internal double DeadlineMilliseconds { get; }
        internal Func<double> ElapsedMilliseconds { get; }

        internal FlowFieldBuildBudget(
            double deadlineMilliseconds,
            Func<double> elapsedMilliseconds)
        {
            DeadlineMilliseconds = Math.Max(0d, deadlineMilliseconds);
            ElapsedMilliseconds = elapsedMilliseconds
                ?? throw new ArgumentNullException(nameof(elapsedMilliseconds));
        }

        internal bool IsExpired => ElapsedMilliseconds() >= DeadlineMilliseconds;

        internal double RemainingMilliseconds
            => Math.Max(0d, DeadlineMilliseconds - ElapsedMilliseconds());
    }

    internal interface IFlowFieldBuildOperation
    {
        FlowFieldBuildOwner Owner { get; }
        FlowFieldBuildOperationState OperationState { get; }
        bool IsWaitingForExternalCompletion { get; }
        bool IsBuilding { get; }
        float Progress { get; }
        Exception Failure { get; }

        void PumpInternal(in FlowFieldBuildBudget budget);
        void Cancel();
        void DisposeOperation();
    }
}
