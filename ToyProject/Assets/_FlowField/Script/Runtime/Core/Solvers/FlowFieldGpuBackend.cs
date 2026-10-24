using System;

namespace Common.FlowField
{
    /// <summary>
    /// Surface2D asynchronous GPU/managed BFS boundary. The backend owns
    /// the solver and its native resources; callers only own the request
    /// lifetime and decide how a completed result is committed.
    /// A backend must check request.IsCurrent before writing staging arrays
    /// or starting a fallback for a delayed result.
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
