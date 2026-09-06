using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Common execution boundary for the Surface2D and Volume3D sessions.
    /// Mode-specific sessions own their arrays and algorithms; this base owns
    /// the event boundary and generation tokens shared by both pipelines.
    /// </summary>
    internal abstract class FlowFieldSessionBase : IDisposable
    {
        private bool _baseDisposed;
        private long _lifetimeGeneration;
        private long _publishedResultId;
        private FlowFieldBuildRequest _latestAcceptedRequest;
        private bool _hasLatestAcceptedRequest;

        internal event Action<bool> FieldCommitted;
        internal event Action<Exception> Failed;
        internal event Action<FlowFieldRuntimeState> StateChanged;
        internal event Action<Exception> EventFailed;

        internal long LifetimeGeneration => _lifetimeGeneration;
        internal long PublishedResultId => _publishedResultId;
        internal bool IsBaseDisposed => _baseDisposed;
        internal bool HasLatestAcceptedRequest => _hasLatestAcceptedRequest;
        internal FlowFieldBuildRequest LatestAcceptedRequest => _latestAcceptedRequest;

        internal abstract FlowFieldSpaceMode SpaceMode { get; }
        internal abstract FlowFieldBakeMode BakeMode { get; }
        internal abstract FlowFieldRuntimeState State { get; }
        internal abstract bool IsInitialized { get; }
        internal abstract bool IsFaulted { get; }
        internal abstract bool IsReady { get; }
        internal abstract bool IsRebuilding { get; }
        internal abstract string LastError { get; }
        internal abstract int Revision { get; }
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

        internal void AcceptBuildRequest(in FlowFieldBuildRequest request)
        {
            if (_baseDisposed)
                throw new ObjectDisposedException(GetType().Name);
            _latestAcceptedRequest = request;
            _hasLatestAcceptedRequest = true;
        }

        protected void ClearAcceptedRequest()
            => _hasLatestAcceptedRequest = false;

        protected void InvalidateBaseLifetime()
        {
            unchecked
            {
                _lifetimeGeneration++;
            }
        }

        protected void NotifyFieldCommitted(bool changed)
        {
            if (_baseDisposed || !changed)
                return;

            unchecked
            {
                _publishedResultId++;
            }
            InvokeUserEvent(() => FieldCommitted?.Invoke(true));
        }

        protected void NotifyFailed(Exception exception)
        {
            if (_baseDisposed)
                return;
            InvokeUserEvent(() => Failed?.Invoke(exception));
        }

        protected void NotifyStateChanged(FlowFieldRuntimeState state)
        {
            if (_baseDisposed)
                return;
            InvokeUserEvent(() => StateChanged?.Invoke(state));
        }

        protected void MarkBaseDisposed()
        {
            if (_baseDisposed)
                return;
            _baseDisposed = true;
            InvalidateBaseLifetime();
            FieldCommitted = null;
            Failed = null;
            StateChanged = null;
            EventFailed = null;
        }

        protected void InvokeUserEvent(Action callback)
        {
            try
            {
                callback?.Invoke();
            }
            catch (Exception exception)
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
        }

        public abstract void Dispose();
    }
}
