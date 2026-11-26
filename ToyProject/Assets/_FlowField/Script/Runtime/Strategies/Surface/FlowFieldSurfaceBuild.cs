namespace Common.FlowField
{
    /// <summary>
    /// Where a Surface2D build stands. A build begins with the synchronous
    /// base stages, may wait for its solve on a worker thread, composes the
    /// final field over as many frames as the budget needs, and publishes.
    /// </summary>
    internal enum FlowFieldSurfaceBuildPhase
    {
        // Published, rejected or dropped, and while the base stages run.
        None,
        Begin,
        WaitingSolve,
        Composing,
        Publish,
        Rejected,
    }

    /// <summary>What a completed Surface2D build publishes.</summary>
    internal readonly struct FlowFieldSurfaceCommit
    {
        internal readonly FlowFieldSurfaceRequest Request;
        // The composed cells may differ from the published ones.
        internal readonly bool ResultChanged;
        // The base arrays were rebuilt, so they are published too.
        internal readonly bool IncludeBase;
        internal readonly FlowFieldCellRect Region;

        internal FlowFieldSurfaceCommit(
            in FlowFieldSurfaceRequest request,
            bool resultChanged,
            bool includeBase,
            FlowFieldCellRect region)
        {
            Request = request;
            ResultChanged = resultChanged;
            IncludeBase = includeBase;
            Region = region;
        }
    }

    /// <summary>
    /// One Surface2D build: its phase, the composition in progress, what it
    /// publishes and the frame budget it runs on. The builder runs its stages
    /// and keeps the staging, the solve backend and the Modifier composition,
    /// so one build runs at a time.
    /// </summary>
    internal sealed class FlowFieldSurfaceBuild : IFlowFieldBuild
    {
        private readonly FlowFieldSurfaceBuilder _owner;

        internal FlowFieldSurfaceBuildPhase Phase = FlowFieldSurfaceBuildPhase.Begin;
        // How long this frame may run. A solve completion that arrives from a
        // poll composes with it too.
        internal FlowFieldBuildBudget FrameBudget = FlowFieldBuildBudget.Unlimited;
        internal FlowFieldSurfaceCommit Commit;

        internal FlowFieldSurfaceBuild(FlowFieldSurfaceBuilder owner) => _owner = owner;

        // The composition in progress and what it publishes once it finishes.
        internal FlowFieldSurfaceRequest ComposeRequest { get; private set; }
        internal bool ComposeRebuildBase { get; private set; }
        internal bool ComposeResultChanged { get; private set; }

        public bool IsLive => Phase != FlowFieldSurfaceBuildPhase.None;

        public bool IsWaitingForWorker => Phase == FlowFieldSurfaceBuildPhase.WaitingSolve;

        public FlowFieldBuildStep Step(in FlowFieldBuildBudget budget, bool waitForWorker)
            => _owner.StepBuild(this, in budget, waitForWorker);

        public bool Publish() => _owner.PublishBuild(this);

        internal void BeginCompose(in FlowFieldSurfaceRequest request, bool rebuildBase, bool resultChanged)
        {
            ComposeRequest = request;
            ComposeRebuildBase = rebuildBase;
            ComposeResultChanged = resultChanged;
            Phase = FlowFieldSurfaceBuildPhase.Composing;
        }

        // CancelWork: a solve callback that arrives later finds the build no
        // longer waiting.
        internal void Drop() => Phase = FlowFieldSurfaceBuildPhase.None;
    }
}
