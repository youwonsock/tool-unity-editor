using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The build pipeline both space modes run. A build begins, takes steps
    /// until it is published or rejected, and the session moves between
    /// Building and Ready around it and records which uncertain regions it
    /// resolves. The builder begins each build and says what it queries; the
    /// build supplies its steps.
    /// </summary>
    internal sealed partial class FlowFieldSession
    {
        // Building with nothing left to build and a field to read is Ready again.
        private void LeaveBuildingIfIdle()
        {
            if (Lifecycle == FlowFieldSessionLifecycle.Building
                && !HasActiveBuild
                && !_builder.HasOwedInput
                && _builder.PublishedField != null)
                SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        internal bool IsWaitingForExternalCompletion => _activeBuild is { IsLive: true, IsWaitingForWorker: true };

        internal bool IsBuilding => CanPump && (IsRebuilding || HasPendingRequest);

        // The scheduler's pump: it shares the frame budget, never waits for
        // the worker thread and runs the unregistered-obstacle sweep.
        internal void PumpScheduled(in FlowFieldBuildBudget budget)
            => RunPipeline(in budget, waitForWorker: false, sweep: true);

        /// <summary>
        /// A direct owner (a bake, the Editor preview) runs its own session and
        /// waits for the worker thread; the scheduler pumps with a frame budget
        /// and never waits. With no budget the pending build runs to its end.
        /// </summary>
        internal void Pump() => RunPipeline(FlowFieldBuildBudget.Unlimited, waitForWorker: true, sweep: false);

        internal void Pump(double budgetMilliseconds)
        {
            System.Diagnostics.Stopwatch timer = System.Diagnostics.Stopwatch.StartNew();
            var budget = new FlowFieldBuildBudget(budgetMilliseconds, () => timer.Elapsed.TotalMilliseconds);
            Pump(in budget, waitForWorker: true);
        }

        /// <summary>
        /// A direct pump with the caller's budget, never sweeping. The Editor-only
        /// tests use a budget that expires after a set number of checks to stop a
        /// build at a chosen point.
        /// </summary>
        internal void Pump(in FlowFieldBuildBudget budget, bool waitForWorker)
            => RunPipeline(in budget, waitForWorker, sweep: false);

        private void RunPipeline(in FlowFieldBuildBudget budget, bool waitForWorker, bool sweep)
        {
            if (!CanPump)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            try
            {
                // Inside the try, so a failing sweep faults this session only.
                if (_releasedInScene.Count > 0)
                    RecheckReleasedColliders();
                if (sweep)
                    RunObstacleSweep();
                int begun = 0;
                while (CanPump)
                {
                    if (!HasActiveBuild)
                    {
                        FlowFieldBuildPolicy policy = _builder.BuildPolicy;
                        if (begun >= policy.MaxBuildsBegunPerPump
                            || !policy.BeginsAfterBudget && budget.IsExpired)
                            return;
                        if (_builder.HasPendingRequest || _builder.HasOwedInput)
                        {
                            PruneDestroyedObstacles();
                            PruneDestroyedModifiers();
                        }
                        bool started = _builder.TryBeginBuild(out FlowFieldBuildStart start, out _activeBuild);
                        if (start.InvalidatedRegion.HasValue)
                            Safety.InvalidateRegion(start.InvalidatedRegion.Value);
                        if (!started)
                        {
                            LeaveBuildingIfIdle();
                            return;
                        }
                        if (start.ResolvesUncertainRegions)
                            Safety.BeginBuild();
                        begun++;
                    }

                    FlowFieldBuildStep step = _activeBuild.Step(in budget, waitForWorker);
                    if (!CanPump)
                        return;
                    switch (step)
                    {
                        case FlowFieldBuildStep.Completed:
                            // Only a field that reads differently is a new Revision.
                            bool changed = _activeBuild.Publish();
                            Safety.BuildPublished();
                            if (changed)
                            {
                                unchecked { Revision++; }
                                NotifyFieldCommitted();
                            }
                            PublishRuntimeState();
                            PruneDestroyedModifiers();
                            if (!_builder.HasOwedInput)
                                SetLifecycle(FlowFieldSessionLifecycle.Active);
                            continue;
                        case FlowFieldBuildStep.Rejected:
                            PruneDestroyedModifiers();
                            // Owed input builds next without leaving Building.
                            if (!_builder.HasOwedInput)
                                SetLifecycle(FlowFieldSessionLifecycle.Active);
                            continue;
                        case FlowFieldBuildStep.Waiting when waitForWorker:
                            SetLifecycle(FlowFieldSessionLifecycle.Building);
                            continue;
                        default:
                            SetLifecycle(FlowFieldSessionLifecycle.Building);
                            return;
                    }
                }
            }
            catch (Exception exception)
            {
                ReportFault(exception);
            }
        }

        // A registered Collider destroyed without being unregistered leaves the
        // registry here, before the build that is owed begins, as a move away
        // from where it was last reported: its cells are uncertain until that
        // build publishes. Builders read the registry and never change it.
        // Nothing is polled while no build is owed.
        private void PruneDestroyedObstacles()
        {
            for (int index = Obstacles.Count - 1; index >= 0; index--)
            {
                Collider collider = Obstacles.GetCollider(index);
                if (collider != null)
                    continue;
                if (Obstacles.NotifyMoved(collider, out FlowFieldObstacleChange change, out bool changed) && changed)
                    ApplyObstacleChange(in change);
            }
        }

        // One sweep step a frame; the scheduler may pump a session more than once.
        private void RunObstacleSweep()
        {
            if (!SweepsObstacles)
                return;
            if (!_sweep.Step(Time.frameCount, Time.unscaledTimeAsDouble, Obstacles))
            {
                // A stopped sweep no longer watches the bounds, so the masks
                // built with it are rebuilt without it.
                ApplyObstacleChange(new FlowFieldObstacleChange(
                    FlowFieldObstacleChangeKind.RegionDirty, null, false, default, true, _sweep.SweepBounds));
            }
        }

        // An unregistered Collider appeared, moved or disappeared.
        private void ApplySweptChange(bool hasPrevious, Bounds previous, bool hasCurrent, Bounds current)
            => ApplyObstacleChange(new FlowFieldObstacleChange(
                FlowFieldObstacleChangeKind.RegionDirty, null, hasPrevious, previous, hasCurrent, current));

        // The scheduler holds sessions that build or wait for a retry. A
        // stopped session leaves it (Stop) and joins again when its restart
        // submits; a faulted one stays, idle, until its retry, Release or
        // Dispose. One that never runs, such as Init on an inactive Manager
        // whose OnDestroy Unity never calls, is never held.
        internal void AttachRuntimeScheduler()
        {
            if (Disposed)
                throw new ObjectDisposedException(GetType().Name);
            if (_runtimeScheduled || !CanPump)
                return;
            FlowFieldBuildScheduler.Register(this);
            _runtimeScheduled = true;
        }

        internal void DetachRuntimeScheduler()
        {
            if (!_runtimeScheduled)
                return;
            FlowFieldBuildScheduler.Unregister(this);
            _runtimeScheduled = false;
        }
    }
}
