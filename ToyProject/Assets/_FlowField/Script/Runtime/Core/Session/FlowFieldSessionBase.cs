using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Common execution boundary for the Surface2D and Volume3D sessions.
    /// Mode-specific sessions own their requests, versions and result data.
    /// This base owns the shared lifecycle rules: the published state, the fault
    /// sequence, command deferral and the generation that retires queued events
    /// and late solve callbacks.
    /// </summary>
    internal abstract class FlowFieldSessionBase : IDisposable, IFlowFieldBuildOperation
    {
        private protected readonly FlowFieldExecutionBoundary Boundary;
        private bool _baseDisposed;
        private bool _runtimeScheduled;
        private FlowFieldRuntimeState _publishedState = FlowFieldRuntimeState.Uninitialized;

        private protected FlowFieldSessionBase(FlowFieldExecutionBoundary boundary)
        {
            Boundary = boundary ?? new FlowFieldExecutionBoundary();
        }

        internal event Action FieldCommitted;
        internal event Action<Exception> Failed;
        internal event Action<FlowFieldRuntimeState> StateChanged;
        internal event Action<Exception> EventFailed;

        internal abstract FlowFieldSpaceMode SpaceMode { get; }
        internal FlowFieldBakeMode BakeMode { get; private protected set; }
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
        private protected bool CanPump => !Disposed
            && (Lifecycle == FlowFieldSessionLifecycle.Active || Lifecycle == FlowFieldSessionLifecycle.Building);
        internal abstract bool IsReady { get; }
        internal abstract bool HasPendingRequest { get; }
        internal Exception Fault { get; private protected set; }
        internal string LastError => Fault?.Message;
        internal int Revision { get; private protected set; }
        private protected bool Disposed { get; set; }
        // Advances when the session stops being what earlier work was for:
        // Release, Suspend and a fault. Queued events and late solve callbacks
        // from before that point are dropped.
        private protected int LifetimeGeneration { get; private set; }
        internal abstract bool TrySample(Vector3 worldPosition, out FlowFieldSample sample);
        internal abstract FlowFieldSample Sample(Vector3 worldPosition);
        internal abstract FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition);
        internal abstract bool TryGetFieldInfo(out FlowFieldFieldInfo info);

        // Registration commands issued from Modifier code wait until that
        // operation has finished. The mode-specific sessions keep their
        // different area-cache and rebuild policies behind the Now methods.
        internal bool RegisterModifierCommand(IFlowFieldVectorModifier modifier)
            => Boundary.TryDefer(() => RunDeferred(() => RegisterModifierNow(modifier)))
                || RegisterModifierNow(modifier);

        internal bool UnregisterModifierCommand(IFlowFieldVectorModifier modifier)
            => Boundary.TryDefer(() => RunDeferred(() => UnregisterModifierNow(modifier)))
                || UnregisterModifierNow(modifier);

        internal void NotifyModifierChangedCommand(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change)
        {
            if (!Boundary.TryDefer(() => RunDeferred(() =>
                {
                    NotifyModifierChangedNow(modifier, change);
                    return true;
                })))
                NotifyModifierChangedNow(modifier, change);
        }

        internal bool RegisterObstacleCommand(Collider collider)
            => Boundary.TryDefer(() => RunDeferred(() => RegisterObstacleNow(collider)))
                || RegisterObstacleNow(collider);

        internal bool UnregisterObstacleCommand(Collider collider)
            => Boundary.TryDefer(() => RunDeferred(() => UnregisterObstacleNow(collider)))
                || UnregisterObstacleNow(collider);

        // The session may have stopped by the time a deferred command runs, and
        // stopping already cleared what the command would have changed.
        private void RunDeferred(Func<bool> command)
        {
            if (IsInitialized)
                command();
        }

        private protected abstract bool RegisterModifierNow(IFlowFieldVectorModifier modifier);
        private protected abstract bool UnregisterModifierNow(IFlowFieldVectorModifier modifier);
        private protected abstract void NotifyModifierChangedNow(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change);
        private protected abstract bool RegisterObstacleNow(Collider collider);
        private protected abstract bool UnregisterObstacleNow(Collider collider);
        internal abstract void NotifyObstacleRegionDirtyCommand(Bounds worldBounds);

        internal abstract bool IsWaitingForExternalCompletion { get; }
        internal bool IsBuilding => CanPump && (IsRebuilding || HasPendingRequest);
        internal abstract void PumpInternal(in FlowFieldBuildBudget budget);

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

        private protected void DetachRuntimeScheduler()
        {
            if (!_runtimeScheduled)
                return;
            FlowFieldBuildScheduler.Unregister(this);
            _runtimeScheduled = false;
        }

        // Lifecycle commands with the same contract in both modes. Mode
        // specific Init/request creation stays on the concrete sessions.
        internal abstract void Suspend();
        internal abstract void Release();
        internal abstract void RetryFault();

        internal void ReportFault(Exception exception)
        {
            if (IsInitialized)
                EnterFault(exception ?? new InvalidOperationException("FlowField session failed."));
        }

        private protected void EnterFault(Exception exception)
        {
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            AbandonWorkForFault();
            Fault = exception;
            ApplyFaultedState();
            Publish(FlowFieldExecutionBoundary.EventKind.Failed, exception: exception);
        }

        private protected abstract void AbandonWorkForFault();
        private protected abstract void ApplyFaultedState();

        private protected void AdvanceLifetimeGeneration()
        {
            unchecked { LifetimeGeneration++; }
        }

        private protected void SetLifecycle(FlowFieldSessionLifecycle next)
        {
            if (Lifecycle == next)
                return;
            Lifecycle = next;
            PublishRuntimeState();
        }

        private protected void PublishRuntimeState()
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

        protected void NotifyFieldCommitted() => Publish(FlowFieldExecutionBoundary.EventKind.FieldCommitted);

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

        protected void MarkBaseDisposed()
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

        public abstract void Dispose();
    }
}
