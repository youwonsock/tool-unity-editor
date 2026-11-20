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
    internal sealed partial class FlowFieldSurfaceBuilder : IFlowFieldSpaceBuilder, IFlowFieldLatestCells
    {
        private readonly FlowFieldSurfaceState _state = new FlowFieldSurfaceState();
        private readonly FlowFieldSurfaceFieldStore _fieldStore = new FlowFieldSurfaceFieldStore();
        private readonly FlowFieldSurfaceObstacleMasks _obstacleMasks;
        private readonly FlowFieldSurfaceGoalTracker _goalTracker = new FlowFieldSurfaceGoalTracker();
        private readonly FlowFieldRequestInbox<FlowFieldSurfaceRequest> _requests
            = new FlowFieldRequestInbox<FlowFieldSurfaceRequest>();
        private readonly FlowFieldSurfaceBaseStage _baseStage;
        private readonly FlowFieldSurfaceSolveStage _solveStage = new FlowFieldSurfaceSolveStage();
        private readonly FlowFieldSurfaceComposeStage _composeStage = new FlowFieldSurfaceComposeStage();
        private FlowFieldSurfaceModifierComposition _modifierComposition;
        private FlowFieldSurfaceRequest? _committedRequest;
        private Vector3? _latestDefaultDirection;
        private FlowFieldSurfaceSourceKind _sourceKind;
        // Configuration input that arrived while a build ran. The next build
        // submits it before it begins.
        private FlowFieldManagerConfig _owedConfig;
        private bool _configOwed;
        // Whether the staged masks were built with the obstacle sweep.
        private bool _masksSweep;

        public FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Surface2D;
        public FlowFieldField PublishedField
            => _state.SurfaceReady && _fieldStore.IsValid && HasCompatibleCommittedSource()
                ? _fieldStore.Field
                : null;

        // The latest request's grid, which the next build uses, as in Volume3D.
        public FlowFieldGridSpace LatestGrid
            => _requests.HasLatest ? _requests.Latest.SurfaceSettings.Grid : _state.Grid;

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
        /// What a Static Bake stores: the published Surface and a copy of its
        /// arrays, taken at the bake's boundary (runtime sampling reads the
        /// compact store directly). False while nothing is published.
        /// </summary>
        internal bool TryExportBakeData(out FlowFieldSurfaceData surface, out FlowFieldSurfaceWorkspace workspace)
        {
            surface = _fieldStore.Surface;
            workspace = surface != null ? _fieldStore.CreateWorkspaceSnapshot() : null;
            return workspace != null;
        }

        // Building without composing means a solve is running elsewhere.
        public bool IsWaitingForExternalCompletion
            => !_composeStage.IsRunning && _phase == BuildPhase.WaitingSolve;

        public bool HasActiveBuild => _phase != BuildPhase.None;

        // The base stages run in one frame so their Physics reads form one
        // snapshot, so one build begins per pump whatever the budget says.
        public FlowFieldBuildPolicy BuildPolicy => new FlowFieldBuildPolicy(beginsAfterBudget: true, maxBuildsBegunPerPump: 1);

        public bool HasOwedInput => _requests.Pending || _configOwed;

        public bool TryBeginBuild(out FlowFieldBuildStart start)
        {
            Bounds? invalidated = null;
            if (_configOwed)
            {
                _configOwed = false;
                invalidated = Submit(CreateCurrentRequest(in _owedConfig));
            }
            // A build with a new grid or obstacle mask queries every obstacle.
            start = new FlowFieldBuildStart((_state.DirtyFlags & ObstacleQueryMask) != 0, invalidated);
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

        public bool PublishBuild()
        {
            _phase = BuildPhase.None;
            return Commit(_commitRequest, _commitResultChanged, _commitIncludeBase, _commitRegion);
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


        /// <summary>
        /// Records the Default Direction, which only the final field uses, as
        /// soon as the settings report it. An Inspector change can arrive after
        /// a solve starts but before its callback, and the callback must compose
        /// the freshly solved base with the newest default even before a
        /// replacement request is submitted.
        /// </summary>
        internal void ObserveDefaultDirection(Vector3 direction)
        {
            if (!FlowFieldGridSpace.IsFinite(direction)
                || direction.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr)
                return;
            _latestDefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(direction);
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

        internal FlowFieldSurfaceBuilder(FlowFieldSession session, IFlowFieldSurfaceSource surfaceSource = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _obstacleMasks = new FlowFieldSurfaceObstacleMasks(session.Obstacles);
            _baseStage = new FlowFieldSurfaceBaseStage(
                _state,
                _obstacleMasks,
                _goalTracker,
                surfaceSource ?? FlowFieldRaycastSurfaceSource.Instance);
        }

        // The session set the bake mode and cleared the uncertain regions.
        public void Initialize(FlowFieldBakeMode bakeMode)
        {
            _sourceKind = bakeMode switch
            {
                FlowFieldBakeMode.StaticBaked => FlowFieldSurfaceSourceKind.StaticSnapshot,
                FlowFieldBakeMode.BakedBase => FlowFieldSurfaceSourceKind.BakedBase,
                _ => FlowFieldSurfaceSourceKind.SceneBuild,
            };
            _state.BakedBaseTouched = null;
            _modifierComposition ??= new FlowFieldSurfaceModifierComposition(_session.Modifiers);
            _solveStage.Prepare(_sourceKind);
            MarkDirty(FlowFieldSurfaceDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
        }

        internal void MarkDirty(
            FlowFieldSurfaceDirtyFlags flags,
            FlowFieldCellRect finalRegion,
            FlowFieldCellRect obstacleRegion)
        {
            if (flags == FlowFieldSurfaceDirtyFlags.None)
                return;
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
            // Obstacle work without a region rescans every cell.
            if (obstacleRegion.IsValid)
                _state.ExpandObstacleDirty(obstacleRegion);
            else if ((flags & FlowFieldSurfaceDirtyFlags.Obstacles) != 0)
                _state.ExpandObstacleDirty(FlowFieldCellRect.Full(LatestGrid));
            // Dirty observations reuse the latest accepted request. Initial
            // setup has no request yet and must wait for its first Submit.
            if (_requests.HasLatest)
                _requests.Pending = true;
        }

        /// <summary>
        /// The owner cleared the Goal. A solve still running for the old one
        /// must not publish. A StaticBaked field has no runtime Goal to clear.
        /// </summary>
        internal void ObserveGoalRemoved()
        {
            if (_sourceKind != FlowFieldSurfaceSourceKind.StaticSnapshot)
                _state.GoalRemoved = true;
        }

        /// <summary>
        /// Accepts a request. Returns the Bake Bounds when new obstacle
        /// settings make every published cell uncertain.
        /// </summary>
        internal Bounds? Submit(in FlowFieldSurfaceRequest request)
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
            Bounds? invalidated = (inputFlags & FlowFieldSurfaceDirtyFlags.StaticObstacles) != 0
                && (inputFlags & FlowFieldSurfaceDirtyFlags.Grid) == 0
                && _committedRequest.HasValue
                    ? request.SurfaceSettings.BakeBounds
                    : (Bounds?)null;

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
            // New obstacle settings rescan every cell.
            if ((inputFlags & FlowFieldSurfaceDirtyFlags.Obstacles) != 0)
                _state.ExpandObstacleDirty(FlowFieldCellRect.Full(request.SurfaceSettings.Grid));

            _requests.Accept(request);
            // A StaticBaked request carries no runtime Goal: the field loads
            // the baked one, which nothing can remove.
            _state.GoalRemoved = request.SourceKind != FlowFieldSurfaceSourceKind.StaticSnapshot
                && !request.Goal.IsValid;
            // A new grid or source hides the published field at once; the session
            // reports the state when the input is accepted.
            return invalidated;
        }

        public IFlowFieldModifierArea CreateModifierArea() => new FlowFieldSurfaceModifierArea();

        public void ApplyModifierChange(in FlowFieldModifierUpdate update)
        {
            EnsureModifierServices();
            switch (update.Kind)
            {
                case FlowFieldModifierUpdateKind.Registered:
                    ApplyModifierRegistryResult(new FlowFieldSurfaceModifierRegistryResult(
                        true, false, true, FlowFieldCellRect.Invalid));
                    return;
                case FlowFieldModifierUpdateKind.Unregistered:
                    ApplyModifierRegistryResult(new FlowFieldSurfaceModifierRegistryResult(
                        false, false, true, FlowFieldSurfaceModifierComposition.AreaOf(update.Entry).InfluenceRect));
                    return;
            }
            bool area = (update.Requested & FlowFieldModifierChange.Area) != 0;
            FlowFieldModifierChangeResult result = update.Result;
            if (!area && !result.Any)
                return;
            // A new priority order recomposes every touched cell.
            FlowFieldModifierRegistry registry = _session.Modifiers;
            FlowFieldCellRect dirty = FlowFieldCellRect.Invalid;
            for (int i = 0; i < registry.Entries.Count; i++)
            {
                FlowFieldModifierEntry entry = registry.Entries[i];
                if (result.PriorityChanged || ReferenceEquals(entry, update.Entry))
                    dirty = FlowFieldCellRect.Union(dirty, FlowFieldSurfaceModifierComposition.AreaOf(entry).InfluenceRect);
            }
            ApplyModifierRegistryResult(new FlowFieldSurfaceModifierRegistryResult(
                area, result.ValueChanged, result.Any, dirty));
        }

        private void EnsureModifierServices()
        {
            if (_modifierComposition == null)
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

        // The session advanced its generation, so a late solve callback is dropped.
        public void CancelWork()
        {
            _solveStage.Cancel();
            _composeStage.Clear();
            _phase = BuildPhase.None;
            _state.DirtyFlags = FlowFieldSurfaceDirtyFlags.None;
        }

        // Drops the published field and the computed Surface, keeping the
        // latest request, registered obstacles and Modifiers. The complete
        // rebuild queries every obstacle again.
        public void DropComputedState()
        {
            _solveStage.DetachBackend();
            _baseStage.LoadedSnapshot = null;

            _committedRequest = null;
            _fieldStore.Clear();

            _state.BaseComposed = false;
            _state.LastComposedRegion = FlowFieldCellRect.Invalid;
            _state.HasObstacleMask = false;
            _state.Surface = null;
            _state.Workspace.ClearAll();
            _configOwed = false;
            // A Goal that was prepared but not solved must not be reported as
            // unchanged by the next build.
            _goalTracker.ForgetLastBuild();
            MarkDirty(FlowFieldSurfaceDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
        }

        public void ReleaseResources()
        {
            _solveStage.DetachBackend();
            _modifierComposition?.Clear();
            _modifierComposition = null;
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
            _state.GoalRemoved = false;
            _state.BakedBaseTouched = null;
            _masksSweep = false;
            _latestDefaultDirection = null;
        }

        // Release already freed everything a Surface2D builder holds.
        public void Dispose()
        {
        }

        private const FlowFieldSurfaceDirtyFlags BaseDirtyMask = FlowFieldSurfaceDirtyFlags.Grid
            | FlowFieldSurfaceDirtyFlags.StaticObstacles
            | FlowFieldSurfaceDirtyFlags.DynamicObstacles
            | FlowFieldSurfaceDirtyFlags.Escape
            | FlowFieldSurfaceDirtyFlags.Goal;

        private const FlowFieldSurfaceDirtyFlags ObstacleQueryMask = FlowFieldSurfaceDirtyFlags.Grid
            | FlowFieldSurfaceDirtyFlags.StaticObstacles
            | FlowFieldSurfaceDirtyFlags.DynamicObstacles;

        private static bool IsBaseDirty(FlowFieldSurfaceDirtyFlags flags) => (flags & BaseDirtyMask) != 0;
    }

}
