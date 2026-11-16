using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Shared owner for runtime and editor field state.  Stage algorithms stay
    /// in their existing files; this class owns only state, resource lifetime,
    /// request coalescing and the solve backend boundary.
    /// </summary>
    internal sealed partial class FlowFieldSurfaceSession : FlowFieldSessionBase, IFlowFieldSampleSafety
    {
        private readonly FlowFieldSurfaceState _state = new FlowFieldSurfaceState();
        private readonly FlowFieldSurfaceFieldStore _fieldStore = new FlowFieldSurfaceFieldStore();
        private readonly FlowFieldSurfaceObstaclePipeline _obstaclePipeline = new FlowFieldSurfaceObstaclePipeline();
        private readonly FlowFieldSurfaceGoalTracker _goalTracker = new FlowFieldSurfaceGoalTracker();
        private readonly FlowFieldRequestInbox<FlowFieldSurfaceRequest> _requests
            = new FlowFieldRequestInbox<FlowFieldSurfaceRequest>();
        private readonly FlowFieldSurfaceBaseStage _baseStage;
        private readonly FlowFieldSurfaceSolveStage _solveStage;
        private readonly FlowFieldSurfaceComposeStage _composeStage = new FlowFieldSurfaceComposeStage();
        private FlowFieldSurfaceModifierPipeline _modifierPipeline;
        private FlowFieldSurfaceRequest? _committedRequest;
        private Vector3? _latestDefaultDirection;
        private FlowFieldSurfaceSourceKind _sourceKind;
        // Only the Manager can tell that its current settings are valid, so the
        // scheduler builds nothing until it has submitted them. Dirty input waits.
        private bool _submittedForScheduler;

        internal FlowFieldGridSpace StagingGrid => _state.Grid;
        internal bool StagingSurfaceReady => _state.SurfaceReady;
        internal FlowFieldSurfaceDirtyFlags DirtyFlags => _state.DirtyFlags;
        internal FlowFieldCellRect DirtyFinalRegion => _state.DirtyFinalRegion;
        internal FlowFieldCellRect DirtyObstacleRegion => _state.DirtyObstacleRegion;
        internal Vector3 ResolvedDefaultDirection => _state.ResolvedDefaultDirection;
        // Editor and bake adapters request an explicit snapshot only at their
        // boundary. Runtime sampling reads the compact store directly.
        internal FlowFieldSurfaceWorkspace CommittedWorkspace => _fieldStore.CreateWorkspaceSnapshot();
        internal FlowFieldReadView CommittedView => _fieldStore.CreateReadView();
        internal FlowFieldSurfaceGoalTracker GoalTracker => _goalTracker;
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

        // Where the active build stands. A build begins with the synchronous
        // base stages, may wait for its solve on a worker thread, composes the
        // final field over as many frames as the budget needs, and publishes.
        private enum BuildPhase
        {
            None,
            Begin,
            WaitingSolve,
            Composing,
            Publish,
            Rejected,
        }

        private BuildPhase _phase;
        private FlowFieldSurfaceRequest _commitRequest;
        private bool _commitResultChanged;
        private bool _commitIncludeBase;
        private FlowFieldCellRect _commitRegion;

        /// <summary>
        /// Advances a pending Surface2D request at the owning execution
        /// boundary. A direct caller (a bake, a test) waits for the worker
        /// thread; the scheduler never does.
        /// </summary>
        internal void Pump()
            => RunPipeline(FlowFieldBuildBudget.Unlimited, scheduled: false, waitForWorker: true);

        // Building without composing means a solve is running elsewhere. Input
        // nobody has submitted yet waits for the Manager in the same way.
        internal override bool IsWaitingForExternalCompletion
            => !_composeStage.IsRunning
                && (Lifecycle == FlowFieldSessionLifecycle.Building
                    || _requests.Pending && !_submittedForScheduler);

        private protected override bool HasActiveBuild => _phase != BuildPhase.None;

        // The base stages run in one frame so their Physics reads form one
        // snapshot, so one build begins per pump whatever the budget says.
        private protected override bool BeginsAfterBudget => true;

        private protected override int MaxBuildsBegunPerPump => 1;

        // Only the Manager can tell that its current settings are valid, so the
        // scheduler builds nothing until it has submitted them.
        private protected override bool TryBeginBuild(bool scheduled)
        {
            if (Lifecycle == FlowFieldSessionLifecycle.Building
                || !_requests.Pending
                || scheduled && !_submittedForScheduler)
                return false;
            _phase = BuildPhase.Begin;
            return true;
        }

        private protected override FlowFieldBuildStep StepBuild(in FlowFieldBuildBudget budget, bool waitForWorker)
        {
            _composeStage.FrameBudget = budget;
            try
            {
                switch (_phase)
                {
                    case BuildPhase.Begin:
                        _phase = BuildPhase.None;
                        BuildPending();
                        break;
                    case BuildPhase.WaitingSolve:
                        // A finished solve reports through its callback, which
                        // moves the build on.
                        _solveStage.Poll(waitForWorker);
                        break;
                    case BuildPhase.Composing:
                        // Input can only change between frames, so checking here
                        // covers everything that arrived since the last piece.
                        if (RejectOutdatedWork())
                        {
                            _composeStage.Clear();
                            _phase = BuildPhase.Rejected;
                        }
                        else
                        {
                            ContinueCompose();
                        }
                        break;
                }

                switch (_phase)
                {
                    case BuildPhase.WaitingSolve:
                        return FlowFieldBuildStep.Waiting;
                    case BuildPhase.Composing:
                        return FlowFieldBuildStep.Yield;
                    case BuildPhase.Publish:
                        return FlowFieldBuildStep.Completed;
                    default:
                        _phase = BuildPhase.None;
                        return FlowFieldBuildStep.Rejected;
                }
            }
            finally
            {
                _composeStage.FrameBudget = FlowFieldBuildBudget.Unlimited;
            }
        }

        private protected override void PublishBuild()
        {
            _phase = BuildPhase.None;
            Commit(_commitRequest, _commitResultChanged, _commitIncludeBase, _commitRegion);
        }

        /// <summary>Marks the build ready to publish with what Commit needs.</summary>
        private void QueueCommit(
            in FlowFieldSurfaceRequest request,
            bool resultChanged,
            bool includeBase,
            FlowFieldCellRect finalRegion)
        {
            _commitRequest = request;
            _commitResultChanged = resultChanged;
            _commitIncludeBase = includeBase;
            _commitRegion = finalRegion;
            _phase = BuildPhase.Publish;
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
                throw new ObjectDisposedException(nameof(FlowFieldSurfaceSession));
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
            _state.DirtyFlags |= FlowFieldSurfaceDirtyFlags.All;
            _state.DirtyFinalRegion = _state.Grid.IsValid
                ? FlowFieldCellRect.Full(_state.Grid)
                : FlowFieldCellRect.Invalid;
            _state.DirtyObstacleRegion = _state.Grid.IsValid
                ? FlowFieldCellRect.Full(_state.Grid)
                : FlowFieldCellRect.Invalid;
            _requests.Pending = false;
            SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        internal bool HasSameBaseInputs(in FlowFieldSurfaceRequest request)
            => _requests.HasLatest && _requests.Latest.HasSameBaseInputs(request);

        internal bool HasSameFinalInputs(in FlowFieldSurfaceRequest request)
            => _requests.HasLatest && _requests.Latest.HasSameFinalInputs(request);

        internal FlowFieldSurfaceDirtyFlags GetInputDirtyFlags(in FlowFieldSurfaceRequest request)
            => !_requests.HasLatest
                ? FlowFieldSurfaceDirtyFlags.All
                : _requests.Latest.GetInputDirtyFlags(request);

        internal bool ObserveGrid(in FlowFieldGridSpace grid) => _baseStage.ObserveGrid(grid);

        internal FlowFieldSurfaceSession(
            IFlowFieldSurfaceSource surfaceSource = null,
            IFlowFieldSurfaceSolveBackend solveBackend = null,
            Func<IFlowFieldSurfaceSolveBackend> solveBackendFactory = null,
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
            FlowFieldSurfaceSourceKind sourceKind,
            FlowFieldSurfaceSolvePolicy solvePolicy)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSurfaceSession));
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
            _modifierPipeline ??= new FlowFieldSurfaceModifierPipeline(new FlowFieldSurfaceModifierRegistry());
            _solveStage.Prepare(sourceKind, solvePolicy);
            Fault = null;
            _state.ConfigurationStale = false;
            MarkDirty(FlowFieldSurfaceDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        internal void MarkDirty(
            FlowFieldSurfaceDirtyFlags flags,
            FlowFieldCellRect finalRegion,
            FlowFieldCellRect obstacleRegion)
        {
            if (flags == FlowFieldSurfaceDirtyFlags.None)
                return;
            if ((flags & (FlowFieldSurfaceDirtyFlags.Grid | FlowFieldSurfaceDirtyFlags.StaticObstacles)) != 0)
                _obstaclePipeline.DiscardStagedDynamicProbe();
            // A base or final-input change always requires final composition.
            // Adding the derived flag at the point where the input is
            // versioned keeps Submit from counting the same change twice when
            // a Manager marks dirty and then submits the latest request.
            if (IsBaseDirty(flags)
                || (flags & (FlowFieldSurfaceDirtyFlags.DefaultDirection
                    | FlowFieldSurfaceDirtyFlags.ModifierArea
                    | FlowFieldSurfaceDirtyFlags.ModifierValue)) != 0)
            {
                flags |= FlowFieldSurfaceDirtyFlags.FinalRegion;
            }
            _state.DirtyFlags |= flags;
            // The submitted request no longer covers this input.
            _submittedForScheduler = false;
            if (finalRegion.IsValid)
                _state.ExpandFinalDirty(finalRegion);
            if (obstacleRegion.IsValid)
                _state.ExpandObstacleDirty(obstacleRegion);
            if ((flags & FlowFieldSurfaceDirtyFlags.Grid) != 0)
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
            MarkDirty(FlowFieldSurfaceDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            // RuntimeState is derived from the lifecycle plus readiness. A
            // stale configuration therefore becomes Building immediately,
            // and observers receive the same single transition they would see
            // when a normal rebuild actually starts.
            PublishRuntimeState();
        }

        internal bool Submit(in FlowFieldSurfaceRequest request)
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSurfaceSession));
            if (!IsInitialized)
                throw new InvalidOperationException("FlowField session is not initialized.");
            if (request.SourceKind != _sourceKind)
                throw new InvalidOperationException("FlowField request source does not match the active session.");

            FlowFieldSurfaceDirtyFlags inputFlags = FlowFieldSurfaceDirtyFlags.None;
            if (_requests.HasLatest && !request.HasSameInputs(_requests.Latest))
                inputFlags = _requests.Latest.GetInputDirtyFlags(request);
            ObserveDefaultDirection(request.DefaultDirection);

            // A staged obstacle probe is valid only for the exact Surface,
            // obstacle settings and registered collider set that produced it.
            // If a newer request changes one of those inputs, discard the
            // candidate before merging the request so the next build probes
            // against the new input instead of consuming stale scratch data.
            if ((inputFlags & (FlowFieldSurfaceDirtyFlags.Grid
                    | FlowFieldSurfaceDirtyFlags.StaticObstacles
                    | FlowFieldSurfaceDirtyFlags.DynamicObstacles
                    | FlowFieldSurfaceDirtyFlags.Escape)) != 0)
            {
                _obstaclePipeline.DiscardStagedDynamicProbe();
            }

            FlowFieldSurfaceDirtyFlags combinedFlags = inputFlags | request.DirtyFlags;
            _state.DirtyFlags |= combinedFlags;
            if ((combinedFlags & FlowFieldSurfaceDirtyFlags.FinalRegion) != 0)
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
            FlowFieldSurfaceModifierRegistryResult result;
            using (Boundary.EnterUserCode())
                result = _modifierPipeline.Registry.Register(modifier);
            ApplyModifierRegistryResult(result);
            return result.FinalDirty;
        }

        private protected override bool UnregisterModifierNow(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldSurfaceModifierRegistryResult result = _modifierPipeline.Registry.Unregister(modifier);
            // A retry builds everything again.
            if (!IsFaulted)
                ApplyModifierRegistryResult(result);
            return result.FinalDirty;
        }

        internal FlowFieldSurfaceModifierRegistryResult DetectModifierChanges()
        {
            EnsureModifierServices();
            FlowFieldSurfaceModifierRegistryResult result;
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
                FlowFieldSurfaceModifierRegistryResult result;
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

        private void ApplyModifierRegistryResult(FlowFieldSurfaceModifierRegistryResult result)
        {
            FlowFieldSurfaceDirtyFlags flags = FlowFieldSurfaceDirtyFlags.None;
            if (result.AreaDirty)
                flags |= FlowFieldSurfaceDirtyFlags.ModifierArea;
            if (result.ValueDirty)
                flags |= FlowFieldSurfaceDirtyFlags.ModifierValue;
            if (result.FinalDirty)
                flags |= FlowFieldSurfaceDirtyFlags.FinalRegion;
            if (flags != FlowFieldSurfaceDirtyFlags.None)
                MarkDirty(flags, result.DirtyRegion, FlowFieldCellRect.Invalid);
        }

        private void InvalidateCallbacks()
        {
            AdvanceLifetimeGeneration();
            _solveStage.Cancel();
            _composeStage.Clear();
            _phase = BuildPhase.None;
        }

        private protected override void AbandonWorkForFault() => InvalidateCallbacks();

        private protected override void ApplyFaultedState()
        {
            _state.DirtyFlags = FlowFieldSurfaceDirtyFlags.None;
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
            _state.DirtyFlags = FlowFieldSurfaceDirtyFlags.All;
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
            _state.DirtyFlags = FlowFieldSurfaceDirtyFlags.All;
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

        private const FlowFieldSurfaceDirtyFlags BaseDirtyMask = FlowFieldSurfaceDirtyFlags.Grid
            | FlowFieldSurfaceDirtyFlags.StaticObstacles
            | FlowFieldSurfaceDirtyFlags.DynamicObstacles
            | FlowFieldSurfaceDirtyFlags.Escape
            | FlowFieldSurfaceDirtyFlags.Goal;

        private static bool IsBaseDirty(FlowFieldSurfaceDirtyFlags flags) => (flags & BaseDirtyMask) != 0;
    }

}
