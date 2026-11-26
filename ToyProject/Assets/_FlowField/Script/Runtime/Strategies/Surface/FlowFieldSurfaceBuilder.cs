using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Surface2D strategy of a <see cref="FlowFieldSession"/>: a floor
    /// surface from downward raycasts, 8-neighbor navigation and region
    /// recomposition. Stage algorithms stay in their own files; this class owns
    /// the staging state, request coalescing and the solve backend boundary.
    /// </summary>
    internal sealed partial class FlowFieldSurfaceBuilder : IFlowFieldSpaceBuilder, IFlowFieldLatestCells
    {
        internal const string MissingBakeMessage = "Surface2D Bake Asset is missing or invalid.";

        private readonly FlowFieldSurfaceState _state = new FlowFieldSurfaceState();
        private readonly FlowFieldSurfaceFieldStore _fieldStore = new FlowFieldSurfaceFieldStore();
        private readonly FlowFieldSurfaceObstacleMasks _obstacleMasks;
        private readonly FlowFieldSurfaceGoalTracker _goalTracker = new FlowFieldSurfaceGoalTracker();
        private readonly FlowFieldRequestInbox<FlowFieldSurfaceRequest> _requests
            = new FlowFieldRequestInbox<FlowFieldSurfaceRequest>();
        private readonly FlowFieldSurfaceBaseStage _baseStage;
        // The worker-thread solve. A StaticBaked field never solves and has
        // none; dropping the computed state drops it, and the next solve
        // takes a new one.
        private FlowFieldSurfaceBaseFieldBuild _solveBackend;
        private readonly FlowFieldSurfaceComposeStage _composeStage = new FlowFieldSurfaceComposeStage();
        private FlowFieldSurfaceModifierComposition _modifierComposition;
        private FlowFieldSurfaceRequest? _committedRequest;
        private FlowFieldBakeMode _bakeMode;
        private FlowFieldSurfaceSourceKind _sourceKind;
        // Configuration input that arrived while a build ran. The next build
        // submits it before it begins.
        private FlowFieldManagerConfig _owedConfig;
        private bool _configOwed;
        // Whether the staged masks were built with the obstacle sweep.
        private bool _masksSweep;

        public FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Surface2D;
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

        // The session cleared the uncertain regions.
        public void Initialize(FlowFieldBakeMode bakeMode)
        {
            _bakeMode = bakeMode;
            _sourceKind = bakeMode switch
            {
                FlowFieldBakeMode.StaticBaked => FlowFieldSurfaceSourceKind.StaticSnapshot,
                FlowFieldBakeMode.BakedBase => FlowFieldSurfaceSourceKind.BakedBase,
                _ => FlowFieldSurfaceSourceKind.SceneBuild,
            };
            _state.BakedBaseTouched = null;
            _modifierComposition ??= new FlowFieldSurfaceModifierComposition(_session.Modifiers);
            PrepareSolveBackend();
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
                    | FlowFieldSurfaceDirtyFlags.ModifierStack)) != 0)
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
            if (request.SourceKind != _sourceKind)
                throw new InvalidOperationException("FlowField request source does not match the active session.");

            // A new grid or source drops the published field for good: readers
            // wait for a build on this source, even if the settings later
            // return to the old one. Obstacle changes that reached no cell of
            // the new grid were never applied to the old field (the session
            // skips them), so it can never be shown again.
            if (_committedRequest.HasValue && !_committedRequest.Value.HasSameSource(request))
                DropPublishedField();
            FlowFieldSurfaceDirtyFlags inputFlags = InputDirtyFlags(request);
            Bounds? invalidated = ObstacleSettingsInvalidation(request);
            // A new check box moves every Modifier's cells. Marked here, with
            // the request that has it, so an owed change is never rebuilt with
            // the old one.
            if ((inputFlags & FlowFieldSurfaceDirtyFlags.ModifierArea) != 0)
                _modifierComposition?.MarkAllAreasDirty();

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

        /// <summary>
        /// What differs from the latest accepted request, and the Default
        /// Direction when it differs from the one last composed. Every base
        /// rebuild composes with ResolvedDefaultDirection, so comparing with
        /// it catches an edit reverted after a solve applied it and skips one
        /// a solve already applied. With no request yet, Initialize or a drop
        /// has already marked everything.
        /// </summary>
        private FlowFieldSurfaceDirtyFlags InputDirtyFlags(in FlowFieldSurfaceRequest request)
        {
            if (!_requests.HasLatest)
                return FlowFieldSurfaceDirtyFlags.None;
            FlowFieldSurfaceDirtyFlags flags = request.HasSameInputs(_requests.Latest)
                ? FlowFieldSurfaceDirtyFlags.None
                : _requests.Latest.GetInputDirtyFlags(request);
            if (!FlowFieldVectorUtility.SameDefaultDirection(
                    _state.ResolvedDefaultDirection,
                    FlowFieldVectorUtility.NormalizeDefaultDirection(request.DefaultDirection)))
                flags |= FlowFieldSurfaceDirtyFlags.DefaultDirection | FlowFieldSurfaceDirtyFlags.FinalRegion;
            return flags;
        }

        /// <summary>
        /// New obstacle settings make every cell uncertain from the moment they
        /// are notified: a build that began with the previous settings may still
        /// publish, and the first build may be one of them. The comparison is
        /// with the latest accepted request on the same source; a new source
        /// has already dropped the published field.
        /// </summary>
        internal Bounds? ObstacleSettingsInvalidation(in FlowFieldSurfaceRequest request)
        {
            bool changed = _requests.HasLatest
                && _requests.Latest.HasSameSource(request)
                && !_requests.Latest.HasSameObstacleInputs(request);
            return changed ? request.SurfaceSettings.BakeBounds : (Bounds?)null;
        }

        private void DropPublishedField()
        {
            _committedRequest = null;
            _fieldStore.Clear();
        }

        // The session advanced its generation, so a late solve callback is dropped.
        public void CancelWork()
        {
            _solveBackend?.Cancel();
            _composeStage.Clear();
            _phase = BuildPhase.None;
            _state.DirtyFlags = FlowFieldSurfaceDirtyFlags.None;
        }

        // Drops the published field and the computed Surface, keeping the
        // latest request, registered obstacles and Modifiers. The complete
        // rebuild queries every obstacle again.
        public void DropComputedState()
        {
            DetachSolveBackend();
            _baseStage.LoadedSnapshot = null;
            DropPublishedField();

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
            DetachSolveBackend();
            _modifierComposition?.Clear();
            _modifierComposition = null;
            _goalTracker.Clear();
            _baseStage.Clear();
            _obstacleMasks.Clear();

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
        }

        private void PrepareSolveBackend()
        {
            if (_solveBackend == null && _sourceKind != FlowFieldSurfaceSourceKind.StaticSnapshot)
                _solveBackend = new FlowFieldSurfaceBaseFieldBuild();
        }

        private void DetachSolveBackend()
        {
            _solveBackend?.Dispose();
            _solveBackend = null;
        }

        private const FlowFieldSurfaceDirtyFlags BaseDirtyMask = FlowFieldSurfaceDirtyFlags.Grid
            | FlowFieldSurfaceDirtyFlags.Ground
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
