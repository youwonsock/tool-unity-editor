using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Surface2D strategy of a <see cref="FlowFieldSession"/>: a floor
    /// surface from downward raycasts, 8-neighbour navigation and region
    /// recomposition. Stage algorithms stay in their own files; this class owns
    /// the staging state, request coalescing and the solve backend boundary.
    /// </summary>
    internal sealed partial class FlowFieldSurfaceBuilder : IFlowFieldSpaceBuilder, IFlowFieldSampleSafety
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
        // Configuration input that arrived while a build ran. The next build
        // submits it before it begins.
        private FlowFieldManagerConfig _owedConfig;
        private bool _configOwed;

        internal FlowFieldGridSpace StagingGrid => _state.Grid;
        internal bool StagingSurfaceReady => _state.SurfaceReady;
        internal FlowFieldSurfaceDirtyFlags DirtyFlags => _state.DirtyFlags;
        internal FlowFieldCellRect DirtyFinalRegion => _state.DirtyFinalRegion;
        internal FlowFieldCellRect DirtyObstacleRegion => _state.DirtyObstacleRegion;
        internal Vector3 ResolvedDefaultDirection => _state.ResolvedDefaultDirection;
        // Editor and bake adapters request an explicit snapshot only at their
        // boundary. Runtime sampling reads the compact store directly.
        internal FlowFieldSurfaceWorkspace CommittedWorkspace => _fieldStore.CreateWorkspaceSnapshot();
        internal FlowFieldSurfaceGoalTracker GoalTracker => _goalTracker;
        internal FlowFieldSurfaceData CommittedSurface => _fieldStore.Surface;
        internal FlowFieldGridSpace CommittedGrid => _fieldStore.Grid;
        public FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Surface2D;
        public bool IsReady => (_session.Lifecycle == FlowFieldSessionLifecycle.Active
                || _session.Lifecycle == FlowFieldSessionLifecycle.Building)
            && _state.SurfaceReady
            && _fieldStore.IsValid
            && HasCompatibleCommittedSource();

        private bool HasCompatibleCommittedSource()
            => !_committedRequest.HasValue
                || !_requests.HasLatest
                || _requests.Latest.HasSameSource(_committedRequest.Value);

        internal bool HasSubmittedRequest => _requests.HasLatest;
        public bool HasPendingRequest => _requests.Pending;

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
            => _session.RunPipeline(FlowFieldBuildBudget.Unlimited, scheduled: false, waitForWorker: true);

        // Building without composing means a solve is running elsewhere.
        public bool IsWaitingForExternalCompletion
            => !_composeStage.IsRunning
                && _session.Lifecycle == FlowFieldSessionLifecycle.Building
                && _phase == BuildPhase.WaitingSolve;

        public bool HasActiveBuild => _phase != BuildPhase.None;

        // The base stages run in one frame so their Physics reads form one
        // snapshot, so one build begins per pump whatever the budget says.
        public bool BeginsAfterBudget => true;

        public int MaxBuildsBegunPerPump => 1;

        // Input that arrived during a build is built next, so the session
        // reports Ready only once nothing is owed, as in Volume3D.
        public bool StaysBuildingWhileOwed => true;

        public bool HasOwedInput => _requests.Pending || _configOwed;

        public bool TryBeginBuild(bool scheduled)
        {
            if (_configOwed)
            {
                _configOwed = false;
                Submit(CreateCurrentRequest(in _owedConfig));
            }
            if (!_requests.Pending)
                return false;
            _phase = BuildPhase.Begin;
            return true;
        }

        public FlowFieldBuildStep StepBuild(in FlowFieldBuildBudget budget, bool waitForWorker)
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

        public void PublishBuild()
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
        /// Starts a stopped or faulted session again from nothing. Registered
        /// obstacles and Modifiers stay; the caller submits a new request.
        /// </summary>
        public void Restart()
        {
            if (_session.Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSurfaceBuilder));
            if (_session.Lifecycle != FlowFieldSessionLifecycle.Suspended
                && _session.Lifecycle != FlowFieldSessionLifecycle.Faulted)
                return;
            ClearComputedState();
            // A fault may have interrupted a Goal that was prepared but not
            // solved. Forget it so the Goal stage cannot report it as unchanged.
            _goalTracker.ForgetLastBuild();
            Start(_session.BakeMode, _sourceKind);
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

        // The session that runs this builder. Lifecycle, events and the build
        // loop live there.
        private readonly FlowFieldSession _session;

        internal FlowFieldSurfaceBuilder(
            FlowFieldSession session,
            IFlowFieldSurfaceSource surfaceSource = null,
            IFlowFieldSurfaceSolveBackend solveBackend = null,
            Func<IFlowFieldSurfaceSolveBackend> solveBackendFactory = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _solveStage = new FlowFieldSurfaceSolveStage(solveBackend, solveBackendFactory);
            _baseStage = new FlowFieldSurfaceBaseStage(
                _state,
                _obstaclePipeline,
                _goalTracker,
                surfaceSource ?? FlowFieldRaycastSurfaceSource.Instance);
        }

        internal void Initialize(
            FlowFieldBakeMode bakeMode,
            FlowFieldSurfaceSourceKind sourceKind)
        {
            if (_session.Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSurfaceBuilder));
            if (_session.Lifecycle == FlowFieldSessionLifecycle.Active
                || _session.Lifecycle == FlowFieldSessionLifecycle.Building)
                throw new InvalidOperationException("FlowField session is already initialized.");
            if (_session.Lifecycle == FlowFieldSessionLifecycle.Faulted)
                throw new InvalidOperationException("FlowField session is faulted. Retry the pending request or Release before Initialize.", _session.Fault);
            if (_session.Lifecycle == FlowFieldSessionLifecycle.Suspended
                && (_session.BakeMode != bakeMode
                    || _sourceKind != sourceKind))
            {
                throw new InvalidOperationException(
                    "FlowField bake mode and source are fixed until Release.");
            }

            Start(bakeMode, sourceKind);
        }

        private void Start(FlowFieldBakeMode bakeMode, FlowFieldSurfaceSourceKind sourceKind)
        {
            _session.BakeMode = bakeMode;
            _sourceKind = sourceKind;
            _modifierPipeline ??= new FlowFieldSurfaceModifierPipeline(new FlowFieldSurfaceModifierRegistry());
            _solveStage.Prepare(sourceKind);
            _session.Fault = null;
            MarkDirty(FlowFieldSurfaceDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            _session.SetLifecycle(FlowFieldSessionLifecycle.Active);
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
            if (finalRegion.IsValid)
                _state.ExpandFinalDirty(finalRegion);
            if (obstacleRegion.IsValid)
                _state.ExpandObstacleDirty(obstacleRegion);
            // Dirty observations reuse the latest accepted request. Initial
            // setup has no request yet and must wait for its first Submit.
            if (_requests.HasLatest)
                _requests.Pending = true;
        }

        /// <summary>The owner cleared the Goal. A solve still running for the old one must not publish.</summary>
        internal void ObserveGoalRemoved() => _state.GoalRemoved = true;

        internal bool Submit(in FlowFieldSurfaceRequest request)
        {
            if (_session.Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldSurfaceBuilder));
            if (!_session.IsInitialized)
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
            _state.GoalRemoved = !request.Goal.IsValid;
            // A new grid or source hides the published field at once.
            _session.PublishRuntimeState();
            return true;
        }

        public bool RegisterModifierNow(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldSurfaceModifierRegistryResult result;
            using (_session.Boundary.EnterUserCode())
                result = _modifierPipeline.Registry.Register(modifier);
            ApplyModifierRegistryResult(result);
            return result.FinalDirty;
        }

        public bool UnregisterModifierNow(IFlowFieldVectorModifier modifier)
        {
            EnsureModifierServices();
            FlowFieldSurfaceModifierRegistryResult result = _modifierPipeline.Registry.Unregister(modifier);
            // A retry builds everything again.
            if (!_session.IsFaulted)
                ApplyModifierRegistryResult(result);
            return result.FinalDirty;
        }

        public void NotifyModifierChangedNow(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            EnsureModifierServices();
            if (!_modifierPipeline.Registry.Contains(modifier))
                throw new InvalidOperationException("Modifier is not registered.");
            // Nothing polls a Modifier, so the notification is where an
            // invalid configuration is found. It faults, as in Volume3D.
            FlowFieldSurfaceModifierRegistryResult result;
            try
            {
                using (_session.Boundary.EnterUserCode())
                    result = _modifierPipeline.Registry.MarkDirty(modifier, (change & FlowFieldModifierChange.Value) != 0);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
            {
                _session.ReportFault(exception);
                return;
            }
            ApplyModifierRegistryResult(result);
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
            _session.AdvanceLifetimeGeneration();
            _solveStage.Cancel();
            _composeStage.Clear();
            _phase = BuildPhase.None;
        }

        public void AbandonWorkForFault() => InvalidateCallbacks();

        public void ApplyFaultedState()
        {
            _state.DirtyFlags = FlowFieldSurfaceDirtyFlags.None;
            _session.SetLifecycle(FlowFieldSessionLifecycle.Faulted);
        }

        public void Stop()
        {
            if (_session.Disposed
                || _session.Lifecycle != FlowFieldSessionLifecycle.Active
                && _session.Lifecycle != FlowFieldSessionLifecycle.Building)
                return;
            ClearComputedState();
            _session.SetLifecycle(FlowFieldSessionLifecycle.Suspended);
        }

        // Drops every build, the published field and the computed Surface,
        // keeping registered obstacles and Modifiers.
        private void ClearComputedState()
        {
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
            // The complete rebuild on restart supersedes any probe that was
            // waiting for an in-flight callback. Keep the registered
            // colliders themselves, but do not run a duplicate probe against
            // the same pose after activation.
            _state.ObstacleProbePending = false;
            _state.DirtyFlags = FlowFieldSurfaceDirtyFlags.All;
            _configOwed = false;
            _session.Fault = null;
        }

        public void Release()
        {
            if (_session.Disposed)
                return;
            ReleaseResources();
        }

        private void ReleaseResources()
        {
            InvalidateCallbacks();
            _session.DetachRuntimeScheduler();
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
            _configOwed = false;
            _state.ObstacleProbePending = false;
            _state.GoalRemoved = false;
            _latestDefaultDirection = null;
            _session.Fault = null;
            _session.SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        internal void DisposePermanently()
        {
            if (_session.Disposed)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = _session.Boundary.EnterOperation();
            // Disposal is terminal before Released subscribers run, so they
            // cannot recreate resources or recursively dispose this owner.
            _session.Disposed = true;
            ReleaseResources();
            // A directly injected backend is deliberately kept alive across
            // Release so the same Session can be initialized again. Permanent
            // disposal is the ownership boundary where it is finally freed.
            _solveStage.DisposeInjected();
            _session.MarkBaseDisposed();
            _session.SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        public void Dispose()
            => DisposePermanently();

        private const FlowFieldSurfaceDirtyFlags BaseDirtyMask = FlowFieldSurfaceDirtyFlags.Grid
            | FlowFieldSurfaceDirtyFlags.StaticObstacles
            | FlowFieldSurfaceDirtyFlags.DynamicObstacles
            | FlowFieldSurfaceDirtyFlags.Escape
            | FlowFieldSurfaceDirtyFlags.Goal;

        private static bool IsBaseDirty(FlowFieldSurfaceDirtyFlags flags) => (flags & BaseDirtyMask) != 0;
    }

}
