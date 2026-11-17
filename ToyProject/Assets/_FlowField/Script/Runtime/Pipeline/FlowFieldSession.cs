using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>What one step of a build left it doing.</summary>
    internal enum FlowFieldBuildStep
    {
        // More work remains and the budget ran out.
        Yield,
        // A worker thread is solving; nothing to do until it finishes.
        Waiting,
        // Newer input made the result useless; nothing is published.
        Rejected,
        // Ready to publish.
        Completed,
    }

    /// <summary>
    /// The FlowField pipeline, the same for both space modes. It owns the
    /// lifecycle, the published state, the fault sequence, command deferral,
    /// the build loop and the generation that retires queued events and late
    /// solve callbacks. What differs between Surface2D and Volume3D lives in
    /// its <see cref="IFlowFieldSpaceBuilder"/>.
    /// </summary>
    internal sealed class FlowFieldSession : IDisposable, IFlowFieldBuildOperation
    {
        internal readonly FlowFieldExecutionBoundary Boundary;
        private readonly IFlowFieldSpaceBuilder _builder;
        private bool _baseDisposed;
        private bool _runtimeScheduled;
        private FlowFieldRuntimeState _publishedState = FlowFieldRuntimeState.Uninitialized;

        internal FlowFieldSession(
            FlowFieldExecutionBoundary boundary,
            Func<FlowFieldSession, IFlowFieldSpaceBuilder> createBuilder)
        {
            Boundary = boundary ?? new FlowFieldExecutionBoundary();
            _builder = createBuilder?.Invoke(this)
                ?? throw new ArgumentNullException(nameof(createBuilder));
        }

        /// <summary>A Surface2D session. The arguments replace parts of the build for a test or a bake.</summary>
        internal static FlowFieldSession CreateSurface(
            FlowFieldExecutionBoundary boundary = null,
            IFlowFieldSurfaceSource surfaceSource = null,
            IFlowFieldSurfaceSolveBackend solveBackend = null,
            Func<IFlowFieldSurfaceSolveBackend> solveBackendFactory = null)
            => new FlowFieldSession(
                boundary,
                session => new FlowFieldSurfaceBuilder(session, surfaceSource, solveBackend, solveBackendFactory));

        internal static FlowFieldSession CreateVolume(FlowFieldExecutionBoundary boundary = null)
            => new FlowFieldSession(boundary, session => new FlowFieldVolumeBuilder(session));

        internal IFlowFieldSpaceBuilder Builder => _builder;
        internal FlowFieldSurfaceBuilder Surface => _builder as FlowFieldSurfaceBuilder;
        internal FlowFieldVolumeBuilder Volume => _builder as FlowFieldVolumeBuilder;

        internal event Action FieldCommitted;
        internal event Action<Exception> Failed;
        internal event Action<FlowFieldRuntimeState> StateChanged;
        internal event Action<Exception> EventFailed;

        internal FlowFieldSpaceMode SpaceMode => _builder.SpaceMode;
        internal FlowFieldBakeMode BakeMode { get; set; }
        internal FlowFieldSessionLifecycle Lifecycle { get; private set; }

        internal FlowFieldRuntimeState State => Lifecycle switch
        {
            FlowFieldSessionLifecycle.Active => IsReady
                ? FlowFieldRuntimeState.Ready
                : FlowFieldRuntimeState.Building,
            FlowFieldSessionLifecycle.Building => FlowFieldRuntimeState.Building,
            FlowFieldSessionLifecycle.Suspended => FlowFieldRuntimeState.Suspended,
            FlowFieldSessionLifecycle.Faulted => FlowFieldRuntimeState.Faulted,
            FlowFieldSessionLifecycle.Released => FlowFieldRuntimeState.Released,
            _ => FlowFieldRuntimeState.Uninitialized,
        };

        internal bool IsInitialized => Lifecycle == FlowFieldSessionLifecycle.Active
            || Lifecycle == FlowFieldSessionLifecycle.Building
            || Lifecycle == FlowFieldSessionLifecycle.Suspended;
        internal bool IsFaulted => Lifecycle == FlowFieldSessionLifecycle.Faulted;
        internal bool IsRebuilding => Lifecycle == FlowFieldSessionLifecycle.Building;
        internal bool CanPump => !Disposed
            && (Lifecycle == FlowFieldSessionLifecycle.Active || Lifecycle == FlowFieldSessionLifecycle.Building);
        // TrySample can succeed inside the grid.
        internal bool IsReady => CanPump && _builder.PublishedField != null;
        internal bool HasPendingRequest => _builder.HasPendingRequest;
        internal Exception Fault { get; set; }
        internal string LastError => Fault?.Message;
        internal int Revision { get; set; }
        internal bool Disposed { get; set; }
        // Advances when the session stops being what earlier work was for:
        // Stop, Restart, Release and a fault, which must also drop a late
        // solve callback. Queued events and late solve callbacks
        // from before that point are dropped.
        internal int LifetimeGeneration { get; private set; }
        // The read both modes share; it never throws.
        private FlowFieldSampleStatus TrySampleStatus(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsReady)
                return FlowFieldSampleStatus.NotReady;
            return FlowFieldSampler.Sample(_builder.PublishedField, worldPosition, _builder, out sample);
        }

        internal bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
            => TrySampleStatus(worldPosition, out sample) == FlowFieldSampleStatus.Sampled;

        internal FlowFieldSample Sample(Vector3 worldPosition)
        {
            FlowFieldSampleStatus status = TrySampleStatus(worldPosition, out FlowFieldSample sample);
            if (status != FlowFieldSampleStatus.Sampled)
                throw FlowFieldFailures.ForSample(status, nameof(worldPosition));
            return sample;
        }

        /// <summary>
        /// The nearest point inside the latest grid. Surface2D grids clamp X
        /// and Z; Volume3D grids clamp all three axes.
        /// </summary>
        internal FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            if (!IsInitialized)
                throw FlowFieldFailures.NotInitialized();
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw FlowFieldFailures.NotFinite(nameof(worldPosition));
            FlowFieldGridSpace grid = _builder.LatestGrid;
            if (!grid.IsValid)
                throw FlowFieldFailures.NotReady();
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            Vector3 clamped = volume ? grid.ClampWorldXYZ(worldPosition) : grid.ClampWorldXZ(worldPosition);
            return new FlowFieldClampResult(
                clamped,
                !Mathf.Approximately(worldPosition.x, clamped.x),
                volume && !Mathf.Approximately(worldPosition.y, clamped.y),
                !Mathf.Approximately(worldPosition.z, clamped.z));
        }
        internal bool TryGetFieldInfo(out FlowFieldFieldInfo info) => _builder.TryGetFieldInfo(out info);

        /// <summary>
        /// The published field, for display and bakes. It is valid until the
        /// next pump, after which its arrays may be reused.
        /// </summary>
        internal bool TryGetPublishedField(out FlowFieldField field)
        {
            field = IsReady ? _builder.PublishedField : null;
            return field != null;
        }

        // Registration commands issued from Modifier code wait until that
        // operation has finished. The mode-specific sessions keep their
        // different area-cache and rebuild policies behind the Now methods.
        internal bool RegisterModifierCommand(IFlowFieldVectorModifier modifier)
            => Boundary.IsInUserCode ? DeferRegisterModifier(modifier) : _builder.RegisterModifierNow(modifier);

        internal bool UnregisterModifierCommand(IFlowFieldVectorModifier modifier)
            => Boundary.IsInUserCode ? DeferUnregisterModifier(modifier) : _builder.UnregisterModifierNow(modifier);

        internal void NotifyModifierChangedCommand(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
        {
            if (Boundary.IsInUserCode)
                DeferNotifyModifierChanged(modifier, change);
            else
                _builder.NotifyModifierChangedNow(modifier, change);
        }

        internal bool RegisterObstacleCommand(Collider collider)
            => Boundary.IsInUserCode ? DeferRegisterObstacle(collider) : _builder.RegisterObstacleNow(collider);

        internal bool UnregisterObstacleCommand(Collider collider)
            => Boundary.IsInUserCode ? DeferUnregisterObstacle(collider) : _builder.UnregisterObstacleNow(collider);

        internal bool NotifyObstacleMovedCommand(Collider collider)
            => Boundary.IsInUserCode
                ? Defer(() => _builder.NotifyObstacleMovedNow(collider))
                : _builder.NotifyObstacleMovedNow(collider);

        // Kept apart so only a deferred call allocates the closure.
        private bool DeferRegisterModifier(IFlowFieldVectorModifier modifier)
            => Defer(() => _builder.RegisterModifierNow(modifier), "Modifier");

        private bool DeferUnregisterModifier(IFlowFieldVectorModifier modifier)
            => Defer(() => _builder.UnregisterModifierNow(modifier));

        private void DeferNotifyModifierChanged(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
            => Defer(() =>
            {
                _builder.NotifyModifierChangedNow(modifier, change);
                return true;
            });

        private bool DeferRegisterObstacle(Collider collider)
            => Defer(() => _builder.RegisterObstacleNow(collider), "장애물");

        private bool DeferUnregisterObstacle(Collider collider)
            => Defer(() => _builder.UnregisterObstacleNow(collider));

        private bool Defer(Func<bool> command, string registration = null)
        {
            Boundary.Defer(() => RunDeferred(command, registration));
            return true;
        }

        // The session may have stopped by the time a deferred command runs. Stopping
        // already cleared what a removal would have changed, but a registration
        // that was reported as accepted is lost, so it leaves a warning.
        private void RunDeferred(Func<bool> command, string registration = null)
        {
            if (IsInitialized)
                command();
            else if (registration != null)
                Debug.LogWarning($"[FlowField] 세션이 멈춰 대기 중이던 {registration} 등록을 처리하지 못했습니다.");
        }

        internal void NotifyObstacleRegionDirtyCommand(Bounds worldBounds)
            => _builder.NotifyObstacleRegionDirtyCommand(worldBounds);

        internal bool IsWaitingForExternalCompletion => _builder.IsWaitingForExternalCompletion;
        internal bool IsBuilding => CanPump && (IsRebuilding || HasPendingRequest);

        // ----- The build pipeline both space modes run -----------------------
        // A build begins, takes steps until it is published or rejected, and
        // the session moves between Building and Ready around it. The builder
        // only supplies the steps.

        internal void PumpInternal(in FlowFieldBuildBudget budget)
            => RunPipeline(in budget, scheduled: true, waitForWorker: false);

        /// <summary>
        /// The pipeline loop. The scheduler pumps with a frame budget and never
        /// waits; a direct owner (a bake, an Editor preview) may wait for the
        /// worker thread instead.
        /// </summary>
        internal void RunPipeline(in FlowFieldBuildBudget budget, bool scheduled, bool waitForWorker)
        {
            if (!CanPump)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            try
            {
                int begun = 0;
                while (CanPump)
                {
                    if (!_builder.HasActiveBuild)
                    {
                        FlowFieldBuildPolicy policy = _builder.BuildPolicy;
                        if (begun >= policy.MaxBuildsBegunPerPump
                            || !policy.BeginsAfterBudget && budget.IsExpired
                            || !_builder.TryBeginBuild(scheduled))
                            return;
                        begun++;
                    }

                    FlowFieldBuildStep step = _builder.StepBuild(in budget, waitForWorker);
                    if (!CanPump)
                        return;
                    switch (step)
                    {
                        case FlowFieldBuildStep.Completed:
                            _builder.PublishBuild();
                            if (!_builder.HasOwedInput)
                                SetLifecycle(FlowFieldSessionLifecycle.Active);
                            continue;
                        case FlowFieldBuildStep.Rejected:
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

        bool IFlowFieldBuildOperation.IsWaitingForExternalCompletion => IsWaitingForExternalCompletion;
        bool IFlowFieldBuildOperation.IsBuilding => IsBuilding;
        void IFlowFieldBuildOperation.PumpInternal(in FlowFieldBuildBudget budget) => PumpInternal(in budget);

        internal void AttachRuntimeScheduler()
        {
            if (Disposed)
                throw new ObjectDisposedException(GetType().Name);
            if (_runtimeScheduled)
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

        // Lifecycle commands with the same contract in both modes. Mode
        // specific Init and request creation stay on the builders.
        internal void Stop() => _builder.Stop();
        internal void Restart() => _builder.Restart();
        internal void Release() => _builder.Release();

        internal void ReportFault(Exception exception)
        {
            if (IsInitialized)
                EnterFault(exception ?? new InvalidOperationException("FlowField session failed."));
        }

        internal void EnterFault(Exception exception)
        {
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            _builder.AbandonWorkForFault();
            Fault = exception;
            _builder.ApplyFaultedState();
            Publish(FlowFieldExecutionBoundary.EventKind.Failed, exception: exception);
        }

        internal void AdvanceLifetimeGeneration()
        {
            unchecked { LifetimeGeneration++; }
        }

        internal void SetLifecycle(FlowFieldSessionLifecycle next)
        {
            if (Lifecycle == next)
                return;
            Lifecycle = next;
            PublishRuntimeState();
        }

        internal void PublishRuntimeState()
        {
            FlowFieldRuntimeState state = State;
            if (_publishedState == state)
                return;
            // A session that never started has nothing for a subscriber to end.
            bool neverStarted = _publishedState == FlowFieldRuntimeState.Uninitialized
                && state == FlowFieldRuntimeState.Released;
            _publishedState = state;
            if (!neverStarted)
                Publish(FlowFieldExecutionBoundary.EventKind.StateChanged, state);
        }

        internal void NotifyFieldCommitted() => Publish(FlowFieldExecutionBoundary.EventKind.FieldCommitted);

        private void Publish(
            FlowFieldExecutionBoundary.EventKind kind,
            FlowFieldRuntimeState state = default,
            Exception exception = null)
        {
            if (!_baseDisposed)
                Boundary.Publish(new FlowFieldExecutionBoundary.QueuedEvent(
                    this,
                    kind,
                    LifetimeGeneration,
                    state,
                    exception));
        }

        internal void MarkBaseDisposed()
        {
            if (_baseDisposed)
                return;
            Publish(FlowFieldExecutionBoundary.EventKind.Disposed);
            _baseDisposed = true;
        }

        // A handler sees an event only while the session is still in the lifetime
        // that raised it. Released ends a lifetime, so it is always delivered.
        internal void Deliver(in FlowFieldExecutionBoundary.QueuedEvent queuedEvent)
        {
            if (queuedEvent.Kind == FlowFieldExecutionBoundary.EventKind.Disposed)
            {
                FieldCommitted = null;
                Failed = null;
                StateChanged = null;
                EventFailed = null;
                return;
            }

            bool released = queuedEvent.Kind == FlowFieldExecutionBoundary.EventKind.StateChanged
                && queuedEvent.State == FlowFieldRuntimeState.Released;
            if (!released && queuedEvent.Generation != LifetimeGeneration)
                return;

            try
            {
                switch (queuedEvent.Kind)
                {
                    case FlowFieldExecutionBoundary.EventKind.StateChanged:
                        StateChanged?.Invoke(queuedEvent.State);
                        break;
                    case FlowFieldExecutionBoundary.EventKind.FieldCommitted:
                        FieldCommitted?.Invoke();
                        break;
                    case FlowFieldExecutionBoundary.EventKind.Failed:
                        Failed?.Invoke(queuedEvent.Exception);
                        break;
                }
            }
            catch (Exception exception)
            {
                ReportEventFailure(exception);
            }
        }

        private void ReportEventFailure(Exception exception)
        {
            try
            {
                EventFailed?.Invoke(exception);
            }
            catch (Exception reportingException)
            {
                Debug.LogException(reportingException);
            }
        }

        public void Dispose() => _builder.Dispose();
    }
}
