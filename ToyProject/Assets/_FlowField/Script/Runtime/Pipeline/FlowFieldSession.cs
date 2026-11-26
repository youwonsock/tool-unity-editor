using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Why a clamp produced a position or not.</summary>
    internal enum FlowFieldClampStatus
    {
        Clamped,
        NotInitialized,
        NonFinite,
        // No request has laid out a grid yet.
        NoGrid,
    }

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
    internal sealed partial class FlowFieldSession : IDisposable
    {
        internal readonly FlowFieldExecutionBoundary Boundary;
        private readonly IFlowFieldSpaceBuilder _builder;
        private FlowFieldSafety _safety;
        // The settings the latest request was built from; the uncertain-region
        // margin uses their Obstacle Clearance.
        private FlowFieldManagerConfig _config;
        private bool _hasConfig;
        // Set once Dispose published its last event; nothing publishes after it.
        private bool _publishingClosed;
        private bool _runtimeScheduled;
        private FlowFieldRuntimeState _publishedState = FlowFieldRuntimeState.Uninitialized;
        private readonly FlowFieldObstacleSweep _sweep;

        internal FlowFieldSession(
            FlowFieldExecutionBoundary boundary,
            Func<FlowFieldSession, IFlowFieldSpaceBuilder> createBuilder)
        {
            Boundary = boundary ?? new FlowFieldExecutionBoundary();
            _builder = createBuilder?.Invoke(this)
                ?? throw new ArgumentNullException(nameof(createBuilder));
            _sweep = new FlowFieldObstacleSweep(ApplySweptChange);
        }

        internal static FlowFieldSession CreateSurface(FlowFieldExecutionBoundary boundary = null)
            => new FlowFieldSession(boundary, session => new FlowFieldSurfaceBuilder(session, null));

        internal static FlowFieldSession CreateVolume(FlowFieldExecutionBoundary boundary = null)
            => new FlowFieldSession(boundary, session => new FlowFieldVolumeBuilder(session));

        /// <summary>
        /// An isolated Surface2D bake session on the ground the bake scanned.
        /// The bake reads its export from <paramref name="builder"/>; its
        /// input still goes through the session (<see cref="SubmitPrepared"/>).
        /// </summary>
        internal static FlowFieldSession CreateSurfaceBake(IFlowFieldSurfaceSource ground, out FlowFieldSurfaceBuilder builder)
        {
            FlowFieldSurfaceBuilder created = null;
            var session = new FlowFieldSession(null, owner => created = new FlowFieldSurfaceBuilder(owner, ground));
            builder = created;
            return session;
        }

        /// <summary>An isolated Volume3D bake session; see <see cref="CreateSurfaceBake"/>.</summary>
        internal static FlowFieldSession CreateVolumeBake(out FlowFieldVolumeBuilder builder)
        {
            FlowFieldVolumeBuilder created = null;
            var session = new FlowFieldSession(null, owner => created = new FlowFieldVolumeBuilder(owner));
            builder = created;
            return session;
        }

        private FlowFieldModifierRegistry _modifiers;

        /// <summary>The registered Modifiers, the same in both modes.</summary>
        internal FlowFieldModifierRegistry Modifiers
            => _modifiers ??= new FlowFieldModifierRegistry(_builder.CreateModifierArea);

        /// <summary>The registered dynamic obstacles, the same in both modes.</summary>
        internal FlowFieldObstacleRegistry Obstacles { get; } = new FlowFieldObstacleRegistry();

        // Colliders unregistered while still in the scene, where they were and
        // in which frame (see RecheckReleasedColliders).
        private readonly System.Collections.Generic.List<ReleasedCollider> _releasedInScene
            = new System.Collections.Generic.List<ReleasedCollider>(4);

        private readonly struct ReleasedCollider
        {
            internal readonly Collider Collider;
            internal readonly Bounds Bounds;
            internal readonly int Frame;

            internal ReleasedCollider(Collider collider, Bounds bounds, int frame)
            {
                Collider = collider;
                Bounds = bounds;
                Frame = frame;
            }
        }

        /// <summary>
        /// Whether the unregistered obstacle sweep runs in this session: the
        /// settings enable it, the runtime scheduler pumps the session and too
        /// many Colliders have not stopped it. A bake and the Editor preview
        /// pump their sessions themselves, so they never sweep.
        /// </summary>
        internal bool SweepsObstacles => _runtimeScheduled && _sweep.IsRunning;

        /// <summary>Regions an obstacle change made uncertain. Surface2D compares them in XZ.</summary>
        internal FlowFieldSafety Safety
            => _safety ??= new FlowFieldSafety(planar: _builder.SpaceMode == FlowFieldSpaceMode.Surface2D);

        internal event Action FieldCommitted;
        internal event Action<Exception> Failed;
        internal event Action<FlowFieldRuntimeState> StateChanged;
        internal event Action<Exception> EventFailed;

        internal FlowFieldSpaceMode SpaceMode => _builder.SpaceMode;
        internal FlowFieldBakeMode BakeMode { get; private set; }
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
        internal bool HasActiveBuild => _builder.HasActiveBuild;
        internal bool HasOwedInput => _builder.HasOwedInput;
        internal Exception Fault { get; private set; }
        internal string LastError => Fault?.Message;
        internal int Revision { get; private set; }
        internal bool Disposed { get; private set; }

        /// <summary>
        /// Counts on from the session this one replaces after a Space Mode
        /// change, so a Manager never shows the same Revision for two fields.
        /// </summary>
        internal void ContinueRevision(int revision) => Revision = revision;
        // Advances when the session stops being what earlier work was for:
        // Stop, Restart, Release and a fault, which must also drop a late
        // solve callback. Queued events and late solve callbacks
        // from before that point are dropped.
        internal int LifetimeGeneration { get; private set; }

        // ----- Lifecycle -----------------------------------------------------
        // The same transitions in both modes. The builder only cancels its
        // work, drops what it computed and frees what it holds.

        /// <summary>Starts a new or released session with no field yet.</summary>
        internal void Initialize(FlowFieldBakeMode bakeMode)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (IsFaulted)
                throw new InvalidOperationException(
                    "FlowField session is faulted. Retry the pending request or Release before Initialize.", Fault);
            if (IsInitialized)
                throw new InvalidOperationException("FlowField session is already initialized.");
            BakeMode = bakeMode;
            Fault = null;
            Safety.ClearRegions();
            _builder.Initialize(bakeMode);
            SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        /// <summary>A running session drops its work and its field and waits.</summary>
        internal void Stop()
        {
            if (!CanPump)
                return;
            AbandonWork();
            DetachRuntimeScheduler();
            _builder.DropComputedState();
            // The restart reads the whole scene again wherever the sweep runs
            // (FlowFieldObstacleSweep), so the sweep starts over too.
            _sweep.Clear();
            SetLifecycle(FlowFieldSessionLifecycle.Suspended);
        }

        /// <summary>
        /// A stopped or faulted session starts again from nothing. Registered
        /// obstacles and Modifiers stay; the latest request builds again.
        /// </summary>
        internal void Restart()
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (Lifecycle != FlowFieldSessionLifecycle.Suspended && !IsFaulted)
                return;
            AbandonWork();
            _builder.DropComputedState();
            Fault = null;
            SetLifecycle(FlowFieldSessionLifecycle.Building);
        }

        /// <summary>
        /// After a restart, reads every registered Modifier again: Priority
        /// edits made while the session was faulted or stopped never arrived.
        /// Throws for an invalid Modifier; the caller reports the fault.
        /// </summary>
        internal void ObserveModifiers()
        {
            PruneDestroyedModifiers();
            using (Boundary.EnterUserCode())
                Modifiers.ObserveAll();
        }

        // A registered Modifier destroyed without being unregistered leaves the
        // registry like a destroyed obstacle: before the build that is owed
        // begins, when a build ends (it may have been destroyed while the build
        // ran), and before a restart reads every Modifier again. Its influence
        // leaves with it. Nothing is polled while no build runs or is owed.
        private void PruneDestroyedModifiers()
        {
            if (_modifiers == null)
                return;
            for (int index = _modifiers.Count - 1; index >= 0; index--)
            {
                IFlowFieldVectorModifier modifier = _modifiers.Entries[index].Modifier;
                if (FlowFieldVectorUtility.IsMissingModifier(modifier))
                    UnregisterModifierNow(modifier);
            }
        }

        /// <summary>Clears everything, registrations included. Initialize may follow.</summary>
        internal void Release()
        {
            if (Disposed)
                return;
            ReleaseState();
            SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        internal void ReportFault(Exception exception)
        {
            if (IsInitialized)
                EnterFault(exception ?? new InvalidOperationException("FlowField session failed."));
        }

        private void EnterFault(Exception exception)
        {
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            AbandonWork();
            Fault = exception;
            SetLifecycle(FlowFieldSessionLifecycle.Faulted);
            Publish(FlowFieldExecutionBoundary.EventKind.Failed, exception: exception);
        }

        /// <summary>Released for good: nothing can Initialize this session again.</summary>
        public void Dispose()
        {
            if (Disposed)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            // Disposal is terminal before Released subscribers run, so they
            // cannot recreate resources or recursively dispose this owner.
            Disposed = true;
            ReleaseState();
            SetLifecycle(FlowFieldSessionLifecycle.Released);
            ClosePublishing();
        }

        // Work begun before this point must not publish, and its queued
        // events and late solve callbacks are dropped.
        private void AbandonWork()
        {
            unchecked { LifetimeGeneration++; }
            _builder.CancelWork();
            Safety.BuildAbandoned();
        }

        private void ReleaseState()
        {
            AbandonWork();
            DetachRuntimeScheduler();
            Obstacles.Clear();
            _releasedInScene.Clear();
            _modifiers?.Clear();
            _builder.ReleaseResources();
            Safety.ClearRegions();
            _sweep.Reset();
            Fault = null;
        }

        private void SetLifecycle(FlowFieldSessionLifecycle next)
        {
            if (Lifecycle == next)
                return;
            Lifecycle = next;
            PublishRuntimeState();
        }

        private void PublishRuntimeState()
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

        private void NotifyFieldCommitted() => Publish(FlowFieldExecutionBoundary.EventKind.FieldCommitted);

        private void Publish(
            FlowFieldExecutionBoundary.EventKind kind,
            FlowFieldRuntimeState state = default,
            Exception exception = null)
        {
            if (!_publishingClosed)
                Boundary.Publish(new FlowFieldExecutionBoundary.QueuedEvent(
                    this,
                    kind,
                    LifetimeGeneration,
                    state,
                    exception));
        }

        private void ClosePublishing()
        {
            if (_publishingClosed)
                return;
            Publish(FlowFieldExecutionBoundary.EventKind.Disposed);
            _publishingClosed = true;
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
    }
}
