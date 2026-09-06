using System;

namespace Common.FlowField
{
    /// <summary>
    /// Mode-neutral asynchronous GPU/managed BFS boundary.  The backend owns
    /// the solver and its native resources; callers only own the request
    /// lifetime and decide how a completed result is committed.
    /// </summary>
    internal interface IFlowFieldGpuBackend : IDisposable
    {
        bool SupportsGpu { get; }

        bool StartBfs(
            in FlowFieldBfsRequest request,
            Action<FlowFieldBfsRequest> completed,
            Action<FlowFieldBfsRequest, Exception> failed,
            bool allowManagedFallback = true);
    }
}
