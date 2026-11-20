namespace Common.FlowField
{
    /// <summary>A composition in progress and what to publish once it finishes.</summary>
    internal sealed class FlowFieldSurfaceComposeStage
    {
        internal FlowFieldSurfaceRequest Request { get; private set; }
        internal bool RebuildBase { get; private set; }
        internal bool ResultChanged { get; private set; }
        internal bool IncludeBase { get; private set; }

        // How long this frame may run. A completion that arrives from a poll uses it too.
        internal FlowFieldBuildBudget FrameBudget = FlowFieldBuildBudget.Unlimited;

        internal void Begin(in FlowFieldSurfaceRequest request, bool rebuildBase, bool resultChanged, bool includeBase)
        {
            Request = request;
            RebuildBase = rebuildBase;
            ResultChanged = resultChanged;
            IncludeBase = includeBase;
        }

        internal void Clear()
        {
            Request = default;
        }
    }
}
