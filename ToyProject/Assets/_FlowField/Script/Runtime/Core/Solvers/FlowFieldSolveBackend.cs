using System;

namespace Common.FlowField
{
    /// <summary>
    /// Runs a Surface2D solve away from the frame that asked for it. The backend
    /// owns the running work; the session owns the request lifetime and decides
    /// how a completed result is committed.
    /// </summary>
    internal interface IFlowFieldSolveBackend : IDisposable
    {
        bool StartBfs(in FlowFieldBfsRequest request, Action<FlowFieldBfsRequest> completed);

        /// <summary>Reports a finished solve. With wait set, it first waits for one to finish.</summary>
        void Poll(bool wait);

        /// <summary>Stops a running solve before its workspace is used again.</summary>
        void Cancel();
    }
}
