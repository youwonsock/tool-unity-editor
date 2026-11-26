using System;
using System.Collections.Generic;

namespace Common.FlowField
{
    /// <summary>
    /// The Surface2D scratch of one additional Goal: its own workspace, solve
    /// backend and Goal tracker, so its solve runs beside the others'. The
    /// builder keeps it from one build to the next while the Goal exists.
    /// </summary>
    internal sealed class FlowFieldSurfaceGoalScratch : IDisposable
    {
        internal readonly FlowFieldSurfaceWorkspace Workspace = new FlowFieldSurfaceWorkspace();
        internal readonly FlowFieldSurfaceBaseFieldBuild Solve = new FlowFieldSurfaceBaseFieldBuild();
        internal readonly FlowFieldSurfaceGoalTracker Tracker = new FlowFieldSurfaceGoalTracker();

        public void Dispose()
        {
            Solve.Dispose();
            Workspace.Release();
        }
    }

    internal enum FlowFieldSurfaceGoalBatchPhase
    {
        // Published, rejected or dropped.
        None,
        Prepare,
        Solving,
        Composing,
        Publish,
        Rejected,
    }

    /// <summary>
    /// One build of the due additional Goals on the published Surface2D field.
    /// Each Goal is prepared on its own workspace and solved on a worker
    /// thread beside the others, then composed with the Modifiers and
    /// published as a Goal layer over the scene and agent layers the build
    /// began with. The builder runs its stages.
    /// </summary>
    internal sealed class FlowFieldSurfaceGoalBatch : IFlowFieldBuild
    {
        internal sealed class Item
        {
            internal FlowFieldGoalSet.Entry Entry;
            internal int RequestRevision;
            internal FlowFieldSurfaceGoalScratch Scratch;
            // A solve was started; Solved once it reported or none was needed.
            internal bool Solving;
            internal bool Solved;
        }

        private readonly FlowFieldSurfaceBuilder _owner;

        internal FlowFieldSurfaceGoalBatchPhase Phase = FlowFieldSurfaceGoalBatchPhase.Prepare;
        // The published field the Goals are built on, and what it stood for.
        internal readonly FlowFieldField Base;
        internal readonly int BaseStamp;
        internal readonly int FieldEpoch;
        internal readonly List<Item> Items = new List<Item>(4);
        // The Goal being composed, and whether its composition began.
        internal int ComposeIndex;
        internal bool ComposeBegun;
        internal FlowFieldBuildBudget FrameBudget = FlowFieldBuildBudget.Unlimited;

        internal FlowFieldSurfaceGoalBatch(FlowFieldSurfaceBuilder owner, FlowFieldField baseField, int baseStamp, int fieldEpoch)
        {
            _owner = owner;
            Base = baseField;
            BaseStamp = baseStamp;
            FieldEpoch = fieldEpoch;
        }

        public bool IsLive => Phase != FlowFieldSurfaceGoalBatchPhase.None;

        public bool IsWaitingForWorker => Phase == FlowFieldSurfaceGoalBatchPhase.Solving;

        public bool BuildsAdditionalGoals => true;

        public FlowFieldBuildStep Step(in FlowFieldBuildBudget budget, bool waitForWorker)
            => _owner.StepGoalBatch(this, in budget, waitForWorker);

        public bool Publish() => _owner.PublishGoalBatch(this);

        // CancelWork: the solves stop and nothing publishes.
        internal void Drop()
        {
            for (int i = 0; i < Items.Count; i++)
                Items[i].Scratch.Solve.Cancel();
            Phase = FlowFieldSurfaceGoalBatchPhase.None;
        }
    }
}
