using System;

namespace Common.FlowField
{
    /// <summary>
    /// Runs the Surface2D solve: inline for Editor previews and bakes, or through
    /// the backend, which keeps it off the frame that asked for it.
    /// </summary>
    internal sealed class FlowFieldSurfaceSolveStage
    {
        private readonly IFlowFieldSurfaceSolveBackend _injected;
        private readonly Func<IFlowFieldSurfaceSolveBackend> _factory;
        private IFlowFieldSurfaceSolveBackend _backend;

        internal FlowFieldSurfaceSolveStage(IFlowFieldSurfaceSolveBackend injected, Func<IFlowFieldSurfaceSolveBackend> factory)
        {
            if (injected != null && factory != null)
                throw new ArgumentException("Specify either a solve backend or a backend factory, not both.");
            _injected = injected;
            _factory = factory;
        }

        internal FlowFieldSurfaceSolvePolicy Policy { get; private set; }

        internal void Prepare(FlowFieldSurfaceSourceKind sourceKind, FlowFieldSurfaceSolvePolicy policy)
        {
            Policy = policy;
            if (_backend != null
                || sourceKind != FlowFieldSurfaceSourceKind.SceneBuild
                || policy != FlowFieldSurfaceSolvePolicy.Worker)
                return;
            _backend = _factory != null
                ? _factory()
                : _injected ?? new FlowFieldSurfaceBuildPipeline();
            if (_backend == null)
                throw new InvalidOperationException("The FlowField solve backend factory returned null.");
        }

        internal bool Start(in FlowFieldSurfaceBfsRequest request, Action<FlowFieldSurfaceBfsRequest> completed)
            => _backend.StartBfs(request, completed);

        internal void Poll(bool wait) => _backend?.Poll(wait);

        internal void Cancel() => _backend?.Cancel();

        // A directly injected backend stays reusable for Release → Initialize.
        internal void DetachBackend()
        {
            if (!ReferenceEquals(_backend, _injected))
                _backend?.Dispose();
            _backend = null;
        }

        internal void DisposeInjected() => _injected?.Dispose();
    }
}
