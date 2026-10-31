using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Common execution boundary for the Surface2D and Volume3D sessions.
    /// Mode-specific sessions own their requests, versions and result data.
    /// This base owns the shared lifecycle rules: the published state, the fault
    /// sequence and the generation that invalidates outstanding work.
    /// </summary>
    internal abstract class FlowFieldSessionBase : IDisposable
    {
        private bool _baseDisposed;
        private FlowFieldRuntimeState _publishedState = FlowFieldRuntimeState.Uninitialized;

        internal event Action FieldCommitted;
        internal event Action<Exception> Failed;
        internal event Action<FlowFieldRuntimeState> StateChanged;
        internal event Action<Exception> EventFailed;

        internal abstract FlowFieldSpaceMode SpaceMode { get; }
        internal FlowFieldBakeMode BakeMode { get; private protected set; }
        internal virtual FlowFieldRuntimeState State => _publishedState;
        internal abstract bool IsInitialized { get; }
        internal abstract bool IsFaulted { get; }
        internal abstract bool IsReady { get; }
        internal abstract bool IsRebuilding { get; }
        internal Exception Fault { get; private protected set; }
        internal string LastError => Fault?.Message;
        internal string GpuFallbackReason { get; private protected set; }
        internal int Revision { get; private protected set; }
        private protected bool Disposed { get; set; }
        // External calls compare this after they return. Every change of the
        // session's lifetime advances it, which abandons the older work.
        private protected int OperationGeneration { get; private set; }
        internal abstract bool TrySample(Vector3 worldPosition, out FlowFieldSample sample);
        internal abstract FlowFieldSample Sample(Vector3 worldPosition);
        internal abstract FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition);
        internal abstract bool TryGetFieldInfo(out FlowFieldFieldInfo info);

        // Modifier registration is a command boundary shared by both
        // spatial sessions. The sessions keep their different area-cache and
        // rebuild policies behind these methods so the Unity-facing Manager
        // does not duplicate a Surface/Volume branch for every command.
        internal abstract bool RegisterModifierCommand(IFlowFieldVectorModifier modifier);
        internal abstract bool UnregisterModifierCommand(IFlowFieldVectorModifier modifier);
        internal abstract void NotifyModifierChangedCommand(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change);
        internal abstract bool RegisterObstacleCommand(Collider collider);
        internal abstract bool UnregisterObstacleCommand(Collider collider);
        internal abstract void NotifyObstacleRegionDirtyCommand(Bounds worldBounds);

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

        // Outstanding work ends before user code runs, and a subscriber of the
        // state change may already have started a newer attempt.
        private protected void EnterFault(Exception exception)
        {
            AbandonWorkForFault();
            int generation = OperationGeneration;
            Fault = exception;
            ApplyFaultedState();
            if (!Disposed && generation == OperationGeneration)
                NotifyFailed(exception);
        }

        private protected abstract void AbandonWorkForFault();
        private protected abstract void ApplyFaultedState();

        private protected void AdvanceOperationGeneration()
        {
            unchecked { OperationGeneration++; }
        }

        private protected void PublishState(FlowFieldRuntimeState state)
        {
            if (_publishedState == state)
                return;
            _publishedState = state;
            NotifyStateChanged(state);
        }

        protected void NotifyFieldCommitted()
        {
            if (_baseDisposed)
                return;

            try { FieldCommitted?.Invoke(); }
            catch (Exception exception) { ReportEventFailure(exception); }
        }

        protected void NotifyFailed(Exception exception)
        {
            if (_baseDisposed)
                return;
            try { Failed?.Invoke(exception); }
            catch (Exception callbackException) { ReportEventFailure(callbackException); }
        }

        protected void NotifyStateChanged(FlowFieldRuntimeState state)
        {
            if (_baseDisposed)
                return;
            try { StateChanged?.Invoke(state); }
            catch (Exception exception) { ReportEventFailure(exception); }
        }

        protected void MarkBaseDisposed()
        {
            if (_baseDisposed)
                return;
            _baseDisposed = true;
            FieldCommitted = null;
            Failed = null;
            StateChanged = null;
            EventFailed = null;
        }

        // Subscriber exceptions are isolated per event. Each Notify method
        // catches inline instead of wrapping the call in a closure.
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
