using System;

namespace Common.FlowField
{
    /// <summary>
    /// Runs the Surface2D solve on a worker thread, off the frame that asked
    /// for it.
    /// </summary>
    internal sealed class FlowFieldSurfaceSolveStage
    {
        private FlowFieldSurfaceBaseFieldBuild _backend;

        // A direct owner that needs the result at once waits for it through Poll.
        internal void Prepare(FlowFieldSurfaceSourceKind sourceKind)
        {
            if (_backend == null && sourceKind == FlowFieldSurfaceSourceKind.SceneBuild)
                _backend = new FlowFieldSurfaceBaseFieldBuild();
        }

        internal bool Start(in FlowFieldSurfaceBfsRequest request, Action<FlowFieldSurfaceBfsRequest> completed)
            => _backend.StartBfs(request, completed);

        internal void Poll(bool wait) => _backend?.Poll(wait);

        internal void Cancel() => _backend?.Cancel();

        internal void DetachBackend()
        {
            _backend?.Dispose();
            _backend = null;
        }
    }
}
