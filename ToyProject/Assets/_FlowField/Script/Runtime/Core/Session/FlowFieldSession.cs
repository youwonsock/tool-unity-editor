using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Shared owner for runtime and editor field state.  Stage algorithms stay
    /// in their existing files; this class owns only state, resource lifetime,
    /// request coalescing and the solve backend boundary.
    /// </summary>
    internal sealed partial class FlowFieldSession : FlowFieldSessionBase
    {
        private readonly FlowFieldSessionState _state = new FlowFieldSessionState();
        private readonly FlowFieldFieldStore _fieldStore = new FlowFieldFieldStore();
        private readonly FlowFieldObstaclePipeline _obstaclePipeline = new FlowFieldObstaclePipeline();
        private readonly FlowFieldGoalTracker _goalTracker = new FlowFieldGoalTracker();
        private readonly FlowFieldRequestInbox<FlowFieldSessionRequest> _requests
            = new FlowFieldRequestInbox<FlowFieldSessionRequest>();
        private readonly FlowFieldSurfaceBaseStage _baseStage;
        private readonly FlowFieldSurfaceSolveStage _solveStage;
        private readonly FlowFieldSurfaceComposeStage _composeStage = new FlowFieldSurfaceComposeStage();
        private FlowFieldModifierPipeline _modifierPipeline;
        private FlowFieldSessionRequest? _committedRequest;
        private Vector3? _latestDefaultDirection;
        private FlowFieldSessionSourceKind _sourceKind;
        // Only the Manager can tell that its current settings are valid, so the
        // scheduler builds nothing until it has submitted them. Dirty input waits.
        private bool _submittedForScheduler;

        internal FlowFieldGridSpace StagingGrid => _state.Grid;
        internal bool StagingSurfaceReady => _state.SurfaceReady;
        internal FlowFieldDirtyFlags DirtyFlags => _state.DirtyFlags;
        internal FlowFieldCellRect DirtyFinalRegion => _state.DirtyFinalRegion;
        internal FlowFieldCellRect DirtyObstacleRegion => _state.DirtyObstacleRegion;
        internal Vector3 ResolvedDefaultDirection => _state.ResolvedDefaultDirection;
        // Editor and bake adapters request an explicit snapshot only at their
        // boundary. Runtime sampling reads the compact store directly.
        internal FlowFieldWorkspace CommittedWorkspace => _fieldStore.CreateWorkspaceSnapshot();
        internal FlowFieldReadView CommittedView => _fieldStore.CreateReadView();
        internal FlowFieldGoalTracker GoalTracker => _goalTracker;
        internal FlowFieldSurfaceData CommittedSurface => _fieldStore.Surface;
        internal FlowFieldGridSpace CommittedGrid => _fieldStore.Grid;
        internal override FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Surface2D;
        internal override bool IsReady => (Lifecycle == FlowFieldSessionLifecycle.Active
                || Lifecycle == FlowFieldSessionLifecycle.Building)
            && !_state.ConfigurationStale
            && _state.SurfaceReady
            && _fieldStore.IsValid
            && HasCompatibleCommittedSource();
        internal bool ConfigurationStale => _state.ConfigurationStale;
        // Stale publications stay hidden until commit. A validated Submit
        // clears these unsubmitted flags, allowing that rebuild to be pumped.
        internal bool RequiresExplicitRebuild => _state.ConfigurationStale
            && _state.GridMarkedUnsubmitted;

        private bool HasCompatibleCommittedSource()
            => !_committedRequest.HasValue
                || !_requests.HasLatest
                || _requests.Latest.HasSameSource(_committedRequest.Value);

        internal bool HasSubmittedRequest => _requests.HasLatest;
        internal override bool HasPendingRequest => _requests.Pending;

        /// <summary>
        /// Advances a pending Surface2D request at the owning execution
        /// boundary. Request submission only records/coalesces input; it does
        /// not recursively enter the build pipeline.
        /// </summary>
        internal bool Pump()
        {
            // A direct caller (a bake, a test) waits for the worker thread. The
            // scheduler reports nothing until a solve has finished.
            bool built = PumpFrame(FlowFieldBuildBudget.Unlimited);
            _solveStage.Poll(wait: true);
            return built;
        }

        // Building without composing means a solve is running elsewhere. Input
        // nobody has submitted yet waits for the Manager in the same way.
        internal override bool IsWaitingForExternalCompletion
            => !_composeStage.IsRunning
                && (Lifecycle == FlowFieldSessionLifecycle.Building
                    || _requests.Pending && !_submittedForScheduler);

        internal override void PumpInternal(in FlowFieldBuildBudget budget)
            => PumpFrame(in budget, requireSubmit: true);

        private bool PumpFrame(in FlowFieldBuildBudget budget, bool requireSubmit = false)
        {
            if (!CanPump)
                return false;
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            _composeStage.FrameBudget = budget;
            try
            {
                if (_composeStage.IsRunning)
                {
                    // Input can only change between frames, so checking here
                    // covers everything that arrived since the last piece.
                    if (RejectOutdatedWork())
                        _composeStage.Clear();
                    else
                        ContinueCompose();
                }
                else
                {
                    _solveStage.Poll(wait: false);
                }
                return !_composeStage.IsRunning
                    && Lifecycle != FlowFieldSessionLifecycle.Building
                    && CanPump
                    && _requests.Pending
                    && (_submittedForScheduler || !requireSubmit)
                    && BuildPending();
            }
            catch (Exception exception)
            {
                EnterFault(exception);
                return false;
            }
            finally
            {
                _composeStage.FrameBudget = FlowFieldBuildBudget.Unlimited;
            }
        }

        internal bool HasPendingObstacleProbe => _state.ObstacleProbePending;

        /// <summary>
        /// Records a final-only scalar as soon as the Unity façade observes
        /// it. This closes the small window in which an inspector change can
        /// arrive after a solve starts but before its callback: the callback
        /// must compose the freshly solved base with the newest default, even
        /// when no replacement request has been submitted yet.
        /// </summary>
        internal void ObserveDefaultDirection(Vector3 direction)
        {
            if (!FlowFieldGridSpace.IsFinite(direction)
                || direction.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                return;
            _latestDefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(direction);
        }

        /// <summary>
        /// Reports a non-solver failure (for example an invalid live Modifier
        /// configuration) through the same one-shot Faulted transition used
        /// by solve failures. The caller deliberately stays outside the
        /// solver exception boundary.
        /// </summary>
        internal override void RetryFault()
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (Lifecycle != FlowFieldSessionLifecycle.Faulted)
                return;
            InvalidateCallbacks();
            Fault = null;
            // The fault may have interrupted a Goal that was prepared but not
            // solved. Forget it so the Goal stage cannot report it as unchanged.
            _goalTracker.ForgetLastBuild();
            // A retry is a new build attempt, not a re-publication of the
            // field that was available before the fault.  Keep the public
            // state in Building until the retry commits successfully; this
            // also preserves the contract that Faulted/initial retry paths
            // report IsReady=false rather than emitting a transient Ready.
            _state.ConfigurationStale = true;
            _state.DirtyFlags |= FlowFieldDirtyFlags.All;
            _state.DirtyFinalRegion = _state.Grid.IsValid
                ? FlowFieldCellRect.Full(_state.Grid)
                : FlowFieldCellRect.Invalid;
            _state.DirtyObstacleRegion = _state.Grid.IsValid
                ? FlowFieldCellRect.Full(_state.Grid)
                : FlowFieldCellRect.Invalid;
            _requests.Pending = false;
            SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        internal bool HasSameBaseInputs(in FlowFieldSessionRequest request)
            => _requests.HasLatest && _requests.Latest.HasSameBaseInputs(request);

        internal bool HasSameFinalInputs(in FlowFieldSessionRequest request)
            => _requests.HasLatest && _requests.Latest.HasSameFinalInputs(request);

        internal FlowFieldDirtyFlags GetInputDirtyFlags(in FlowFieldSessionRequest request)
            => !_requests.HasLatest
                ? FlowFieldDirtyFlags.All
                : _requests.Latest.GetInputDirtyFlags(request);

        internal bool ObserveGrid(in FlowFieldGridSpace grid) => _baseStage.ObserveGrid(grid);

        internal FlowFieldSession(
            IFlowFieldSurfaceSource surfaceSource = null,
            IFlowFieldSolveBackend solveBackend = null,
            Func<IFlowFieldSolveBackend> solveBackendFactory = null,
            FlowFieldExecutionBoundary boundary = null)
            : base(boundary)
        {
            _solveStage = new FlowFieldSurfaceSolveStage(solveBackend, solveBackendFactory);
            _baseStage = new FlowFieldSurfaceBaseStage(
                _state,
                _obstaclePipeline,
                _goalTracker,
                surfaceSource ?? FlowFieldRaycastSurfaceSource.Instance);
        }

        internal void Initialize(
            FlowFieldBakeMode bakeMode,
            FlowFieldSessionSourceKind sourceKind,
            FlowFieldSolvePolicy solvePolicy)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (Lifecycle == FlowFieldSessionLifecycle.Active
                || Lifecycle == FlowFieldSessionLifecycle.Building)
                throw new InvalidOperationException("FlowField session is already initialized.");
            if (Lifecycle == FlowFieldSessionLifecycle.Faulted)
                throw new InvalidOperationException("FlowField session is faulted. Retry the pending request or Release before Initialize.", Fault);
            if (Lifecycle == FlowFieldSessionLifecycle.Suspended
                && (BakeMode != bakeMode
                    || _sourceKind != sourceKind
                    || _solveStage.Policy != solvePolicy))
            {
                throw new InvalidOperationException(
                    "FlowField bake mode, source and solve policy are fixed until Release.");
            }

            BakeMode = bakeMode;
            _sourceKind = sourceKind;
            _modifierPipeline ??= new FlowFieldModifierPipeline(new FlowFieldModifierRegistry());
            _solveStage.Prepare(sourceKind, solvePolicy);
            Fault = null;
            _state.ConfigurationStale = false;
            MarkDirty(FlowFieldDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        internal void MarkDirty(
            FlowFieldDirtyFlags flags,
            FlowFieldCellRect finalRegion,
            FlowFieldCellRect obstacleRegion)
        {
            if (flags == FlowFieldDirtyFlags.None)
                return;
            if ((flags & (FlowFieldDirtyFlags.Grid | FlowFieldDirtyFlags.StaticObstacles)) != 0)
                _obstaclePipeline.DiscardStagedDynamicProbe();
            // A base or final-input change always requires final composition.
            // Adding the derived flag at the point where the input is
            // versioned keeps Submit from counting the same change twice when
            // a Manager marks dirty and then submits the latest request.
            if (IsBaseDirty(flags)
                || (flags & (FlowFieldDirtyFlags.DefaultDirection
                    | FlowFieldDirtyFlags.ModifierArea
                    | FlowFieldDirtyFlags.ModifierValue)) != 0)
            {
                flags |= FlowFieldDirtyFlags.FinalRegion;
            }
            _state.DirtyFlags |= flags;
            if (finalRegion.IsValid)
                _state.ExpandFinalDirty(finalRegion);
            if (obstacleRegion.IsValid)
                _state.ExpandObstacleDirty(obstacleRegion);
            if ((flags & FlowFieldDirtyFlags.Grid) != 0)
                _state.GridMarkedUnsubmitted = true;
            // Dirty observations reuse the latest accepted request. Initial
            // setup has no request yet and must wait for its first Submit.
            if (_requests.HasLatest)
                _requests.Pending = true;
        }

        /// <summary>The owner cleared the Goal. A solve still running for the old one must not publish.</summary>
        internal void ObserveGoalRemoved() => _state.GoalRemoved = true;

        internal void MarkConfigurationStale()
        {
            if (!IsInitialized || _state.ConfigurationStale)
                return;
            _state.ConfigurationStale = true;
            _submittedForScheduler = false;
            MarkDirty(FlowFieldDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            // RuntimeState is derived from the lifecycle plus readiness. A
            // stale configuration therefore becomes Building immediately,
            // and observers receive the same single transition they would see
            // when a normal rebuild actually starts.
            PublishRuntimeState();
        }

        internal bool Submit(in FlowFieldSessionRequest request)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSession));
            if (!IsInitialized)
                throw new InvalidOperationException("FlowField session is not initialized.");
            if (request.SourceKind != _sourceKind)
                throw new InvalidOperationException("FlowField request source does not match the active session.");

            FlowFieldDirtyFlags inputFlags = FlowFieldDirtyFlags.None;
            if (_requests.HasLatest && !request.HasSameInputs(_requests.Latest))
                inputFlags = _requests.Latest.GetInputDirtyFlags(request);
            ObserveDefaultDirection(request.DefaultDirection);

            // A staged obstacle probe is valid only for the exact Surface,
            // obstacle settings and registered collider set that produced it.
            // If a newer request changes one of those inputs, discard the
            // candidate before merging the request so the next build probes
            // against the new input instead of consuming stale scratch data.
            if ((inputFlags & (FlowFieldDirtyFlags.Grid
                    | FlowFieldDirtyFlags.StaticObstacles
                    | FlowFieldDirtyFlags.DynamicObstacles
                    | FlowFieldDirtyFlags.Escape)) != 0)
            {
                _obstaclePipeline.DiscardStagedDynamicProbe();
            }

            FlowFieldDirtyFlags combinedFlags = inputFlags | request.DirtyFlags;
            _state.DirtyFlags |= combinedFlags;
            if ((combinedFlags & FlowFieldDirtyFlags.FinalRegion) != 0)
            {
                _state.ExpandFinalDirty(
                    request.DirtyFinalRegion.IsValid
                        ? request.DirtyFinalRegion
                        : FlowFieldCellRect.Full(_state.Grid));
            }
            if (request.DirtyObstacleRegion.IsValid)
                _state.ExpandObstacleDirty(request.DirtyObstacleRegion);

            _requests.Accept(request);
            _submittedForScheduler = true;
            _state.GridMarkedUnsubmitted = false;
            _state.GoalRemoved = !request.Goal.IsValid;
            return true;
        }

        private protected override bool RegisterModifierNow(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result;
            using (Boundary.EnterUserCode())
                result = _modifierPipeline.Registry.Register(modifier);
            ApplyModifierRegistryResult(result);
            return result.FinalDirty;
        }

        private protected override bool UnregisterModifierNow(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result = _modifierPipeline.Registry.Unregister(modifier);
            ApplyModifierRegistryResult(result);
            return result.FinalDirty;
        }

        internal FlowFieldModifierRegistryResult DetectModifierChanges()
        {
            EnsureModifierServices();
            FlowFieldModifierRegistryResult result;
            using (Boundary.EnterUserCode())
                result = _modifierPipeline.Registry.DetectChanges();
            ApplyModifierRegistryResult(result);
            return result;
        }

        private protected override void NotifyModifierChangedNow(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            EnsureModifierServices();
            if ((change & (FlowFieldModifierChange.Value | FlowFieldModifierChange.Priority)) != 0)
            {
                FlowFieldModifierRegistryResult result;
                using (Boundary.EnterUserCode())
                    result = _modifierPipeline.Registry.MarkDirty(modifier, (change & FlowFieldModifierChange.Value) != 0);
                ApplyModifierRegistryResult(result);
            }
            if ((change & FlowFieldModifierChange.Area) != 0)
                ApplyModifierRegistryResult(_modifierPipeline.Registry.MarkAreaDirty(modifier));
        }

        private void EnsureModifierServices()
        {
            if (_modifierPipeline == null)
                throw new InvalidOperationException("FlowField modifier services are not initialized.");
        }

        private void ApplyModifierRegistryResult(FlowFieldModifierRegistryResult result)
        {
            FlowFieldDirtyFlags flags = FlowFieldDirtyFlags.None;
            if (result.AreaDirty)
                flags |= FlowFieldDirtyFlags.ModifierArea;
            if (result.ValueDirty)
                flags |= FlowFieldDirtyFlags.ModifierValue;
            if (result.FinalDirty)
                flags |= FlowFieldDirtyFlags.FinalRegion;
            if (flags != FlowFieldDirtyFlags.None)
                MarkDirty(flags, result.DirtyRegion, FlowFieldCellRect.Invalid);
        }

        private void InvalidateCallbacks()
        {
            AdvanceLifetimeGeneration();
            _solveStage.Cancel();
            _composeStage.Clear();
        }

        private protected override void AbandonWorkForFault() => InvalidateCallbacks();

        private protected override void ApplyFaultedState()
        {
            _state.DirtyFlags = FlowFieldDirtyFlags.None;
            SetLifecycle(FlowFieldSessionLifecycle.Faulted);
        }

        internal override void Suspend()
        {
            if (Disposed
                || Lifecycle == FlowFieldSessionLifecycle.Uninitialized
                || Lifecycle == FlowFieldSessionLifecycle.Released
                || Lifecycle == FlowFieldSessionLifecycle.Faulted
                || Lifecycle == FlowFieldSessionLifecycle.Suspended)
                return;
            InvalidateCallbacks();
            _solveStage.DetachBackend();
            _baseStage.LoadedSnapshot = null;

            _committedRequest = null;
            _fieldStore.Clear();

            _state.BaseComposed = false;
            _state.LastComposedRegion = FlowFieldCellRect.Invalid;
            _state.HasObstacleMask = false;
            _obstaclePipeline.DiscardStagedDynamicProbe();
            _state.Surface = null;
            _state.Workspace.ClearAll();
            // The complete rebuild on resume supersedes any probe that was
            // waiting for an in-flight callback. Keep the registered
            // colliders themselves, but do not run a duplicate probe against
            // the same pose after activation.
            _state.ObstacleProbePending = false;
            _state.DirtyFlags = FlowFieldDirtyFlags.All;
            _state.ConfigurationStale = false;
            SetLifecycle(FlowFieldSessionLifecycle.Suspended);
        }

        internal override void Release()
        {
            if (Disposed)
                return;
            ReleaseResources();
        }

        private void ReleaseResources()
        {
            InvalidateCallbacks();
            DetachRuntimeScheduler();
            _solveStage.DetachBackend();
            _modifierPipeline?.Clear();
            _modifierPipeline = null;
            _obstaclePipeline.ClearDynamicObstacles();
            _goalTracker.Clear();
            _baseStage.Clear();

            _committedRequest = null;
            _fieldStore.Clear();
            _state.Workspace.Release();
            _state.Grid = default;
            _state.Surface = null;

            _state.HasObstacleMask = false;
            _state.BaseComposed = false;
            _state.LastComposedRegion = FlowFieldCellRect.Invalid;
            _state.DirtyFlags = FlowFieldDirtyFlags.All;
            _state.DirtyFinalRegion = FlowFieldCellRect.Invalid;
            _state.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            _state.ResolvedDefaultDirection = Vector3.zero;
            _requests.Clear();
            _submittedForScheduler = false;
            _state.ObstacleProbePending = false;
            _state.GridMarkedUnsubmitted = false;
            _state.GoalRemoved = false;
            _latestDefaultDirection = null;
            Fault = null;
            SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        internal void DisposePermanently()
        {
            if (Disposed)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            // Disposal is terminal before Released subscribers run, so they
            // cannot recreate resources or recursively dispose this owner.
            Disposed = true;
            ReleaseResources();
            // A directly injected backend is deliberately kept alive across
            // Release so the same Session can be initialized again. Permanent
            // disposal is the ownership boundary where it is finally freed.
            _solveStage.DisposeInjected();
            MarkBaseDisposed();
            SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        public override void Dispose()
            => DisposePermanently();

        private const FlowFieldDirtyFlags BaseDirtyMask = FlowFieldDirtyFlags.Grid
            | FlowFieldDirtyFlags.StaticObstacles
            | FlowFieldDirtyFlags.DynamicObstacles
            | FlowFieldDirtyFlags.Escape
            | FlowFieldDirtyFlags.Goal;

        private static bool IsBaseDirty(FlowFieldDirtyFlags flags) => (flags & BaseDirtyMask) != 0;
    }

}
