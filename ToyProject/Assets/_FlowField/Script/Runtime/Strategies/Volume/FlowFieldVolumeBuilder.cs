using System;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldField<Common.FlowField.FlowFieldVolumeRequest>;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;

namespace Common.FlowField
{
    /// <summary>
    /// The Volume3D strategy of a <see cref="FlowFieldSession"/>: cubic cells
    /// filling the Bounds, 26-neighbour navigation on worker threads, and
    /// re-queries limited to the regions whose obstacles changed.
    /// </summary>
    internal sealed partial class FlowFieldVolumeBuilder : IFlowFieldSpaceBuilder
    {
        private const int CellsPerStep = FlowFieldVolumeSteps.CellsPerStep;
        private const int ArrayElementsPerStep = FlowFieldVolumeSteps.ArrayElementsPerStep;
        // Shared by the obstacle and Modifier steps: the cells of one step and
        // the Colliders one cell query found.
        private Collider[] _overlapBuffer;
        private readonly int[] _stepCells = new int[CellsPerStep];
        private readonly FlowFieldVolumeCellQuery _cellQuery = new FlowFieldVolumeCellQuery();
        private readonly FlowFieldSpareArrays _spares = new FlowFieldSpareArrays();
        // The uncertain regions, which the session owns and updates. The
        // builder only reads them to limit its obstacle queries.
        private FlowFieldSafety _safety => _session.Safety;

        private VolumeFieldData _committed;
        private BuildContext _build;
        private readonly FlowFieldRequestInbox<FlowFieldVolumeRequest> _requests
            = new FlowFieldRequestInbox<FlowFieldVolumeRequest>();
        // Modifier changes reuse the published navigation unless a full build is pending.
        private bool _hasPendingModifierRecompose;
        // An explicit rebuild re-queries obstacles even when only the Goal changed.
        private bool _obstacleQueryRequired;
        // The sweep was turned on or off since the last build began.
        private bool _queryEveryCell;
        // Whoever pumps directly (a bake, the Editor preview) waits for the
        // BakedBase: every region an obstacle change reached since Initialize.
        // A reloaded bake queries it again, because the bake still holds what
        // was there before the changes.
        private Bounds? _bakedBaseTouched;

        // The session that runs this builder. Lifecycle, events and the build
        // loop live there.
        private readonly FlowFieldSession _session;

        internal FlowFieldVolumeBuilder(FlowFieldSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        public FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Volume3D;
        public bool IsWaitingForExternalCompletion
            => _build != null && _build.SolveRunning && !_build.SolveHandle.IsCompleted;
        // The published field. A request on another grid or source drops it
        // (Submit), so it is always on the latest request's grid.
        public FlowFieldField PublishedField => _committed;

        // The latest request's grid, which the next build uses, as in Surface2D.
        public FlowFieldGridSpace LatestGrid => _requests.HasLatest ? _requests.Latest.Grid : default;
        public bool HasPendingRequest => _requests.Pending || _hasPendingModifierRecompose || _build != null;
        internal bool TryGetLatestRequestedInput(out FlowFieldVolumeRequest request)
        {
            request = _requests.Latest;
            return _requests.HasLatest;
        }

        internal float BuildProgress
        {
            get
            {
                if (_build == null)
                    return _requests.Pending || _hasPendingModifierRecompose ? 0f : 1f;
                if (_build.Phase == BuildPhase.Complete)
                    return 1f;

                float phaseCount = (float)BuildPhase.Complete;
                float local = _build.Count <= 0 ? 0f : (float)_build.Cursor / _build.Count;
                return Mathf.Clamp01(((int)_build.Phase + Mathf.Clamp01(local)) / phaseCount);
            }
        }

        // The session set the bake mode and cleared the uncertain regions.
        // Running with no request yet; the first Submit starts a build.
        public void Initialize()
        {
            _requests.Clear();
            _hasPendingModifierRecompose = false;
            _obstacleQueryRequired = false;
            _queryEveryCell = false;
            _bakedBaseTouched = null;
        }

        /// <summary>
        /// Accepts a request. Returns the Bounds when new obstacle settings
        /// make every published cell uncertain.
        /// </summary>
        internal Bounds? Submit(in FlowFieldVolumeRequest request, bool requeryObstacles = false)
        {
            ThrowIfDisposed();
            if (!_session.IsInitialized)
                throw new InvalidOperationException("FlowField volume session is not initialized.");
            ValidateRequest(request);
            if (requeryObstacles)
                _obstacleQueryRequired = true;
            // Turning the sweep on or off says nothing changed, so the field
            // stays readable; the next build still queries every cell from
            // Physics, BakedBase included, so it takes in unregistered changes
            // made while nothing swept them, as Surface2D does.
            if (_requests.HasLatest && _requests.Latest.SweepUnregistered != request.SweepUnregistered)
            {
                _obstacleQueryRequired = true;
                _queryEveryCell = true;
            }
            // A new grid or source drops the published field for good: readers
            // wait for a build on this grid, even if the grid later returns to
            // the old one. Obstacle changes that reached no cell of the new
            // grid were never applied to the old field (the session skips
            // them), so it can never be shown again.
            if (_committed != null && !_committed.Request.HasSameGrid(request))
                _committed = null;
            bool goalMovedWithinCell = IsGoalMoveWithinCell(request);
            // New obstacle settings make every cell uncertain from now. The
            // comparison is with the latest accepted request on the same grid:
            // a build that began with the previous settings, the first build
            // included, may still publish.
            bool obstacleSettingsChanged = _requests.HasLatest
                && _requests.Latest.HasSameGrid(request)
                && !_requests.Latest.HasSameObstacleInputs(request);
            Bounds? invalidated = obstacleSettingsChanged ? request.WorldBounds : (Bounds?)null;
            // Keep the last accepted request independently of the committed
            // field.  A dirty notification that arrives while another Goal is
            // building must never resurrect the older committed Goal.
            _requests.Latest = request;
            _requests.HasLatest = true;
            // A grid/signature change invalidates the private staging arrays
            // immediately, and a removed Goal must not publish once more.
            if (_build != null
                && (!_build.Request.HasSameGrid(request) || _build.Request.HasGoal && !request.HasGoal))
                DiscardBuild();
            if (!goalMovedWithinCell)
                _requests.Pending = true;
            return invalidated;
        }

        /// <summary>
        /// The running or published field already answers this request; only
        /// the requested position differs. Resubmitting the same position
        /// remains an explicit request for another build.
        /// </summary>
        private bool IsGoalMoveWithinCell(in FlowFieldVolumeRequest request)
        {
            if (_obstacleQueryRequired || _requests.Pending || request.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            if (_build == null && _committed == null)
                return false;
            FlowFieldVolumeRequest current = _build != null ? _build.Request : _committed.Request;
            return !current.HasSameGoal(request) && current.HasSameResult(request);
        }

        public FlowFieldBuildPolicy BuildPolicy => new FlowFieldBuildPolicy(beginsAfterBudget: false, maxBuildsBegunPerPump: int.MaxValue);

        public bool HasActiveBuild => _build != null;

        // Volume3D knows nothing newer than its published field; the shared
        // rule reads that field and the notified uncertain regions.
        public IFlowFieldLatestCells LatestCells => null;

        // Every build but a Modifier recompose queries the obstacles.
        public bool TryBeginBuild(out FlowFieldBuildStart start)
        {
            bool started = TryBeginPendingBuild();
            start = new FlowFieldBuildStart(started && !_build.IsModifierRecompose, null);
            return started;
        }

        public FlowFieldBuildStep StepBuild(in FlowFieldBuildBudget budget, bool waitForWorker)
        {
            if (IsWaitingForExternalCompletion && !waitForWorker)
                return FlowFieldBuildStep.Waiting;
            if (StepBuild(_build, budget, waitForWorker))
                return FlowFieldBuildStep.Completed;
            return _build.SolveRunning ? FlowFieldBuildStep.Waiting : FlowFieldBuildStep.Yield;
        }

        public bool PublishBuild() => CommitBuild(_build);


        public bool HasOwedInput => _requests.Pending || _hasPendingModifierRecompose;

        private bool TryBeginPendingBuild()
        {
            if (!_requests.Pending && !_hasPendingModifierRecompose)
                return false;

            // A Modifier change recomposes the published navigation. Before
            // any field is published it builds in full like any request.
            bool recompose = !_requests.Pending && _committed != null;
            FlowFieldVolumeRequest request = recompose ? _committed.Request : _requests.Latest;
            _requests.Pending = false;
            _hasPendingModifierRecompose = false;
            FlowFieldModifierWorkItem[] modifierSnapshots;
            using (_session.Boundary.EnterUserCode())
                modifierSnapshots = _session.Modifiers.CaptureSnapshots();
            // A recompose queries no obstacles, so it resolves no safety region.
            if (recompose)
            {
                _build = new BuildContext(
                    _committed,
                    modifierSnapshots,
                    FlowFieldSpareArrays.Take(ref _spares.FinalDirections, request.Grid.CellCount),
                    FlowFieldSpareArrays.Take(ref _spares.FinalSpeeds, request.Grid.CellCount));
            }
            else
            {
                bool reuseObstacles = CanReuseObstacleQueries(request);
                bool requeryChangedCells = !reuseObstacles && CanRequeryChangedCells(request);
                bool requiresFullQuery = _obstacleQueryRequired;
                bool queryEveryCell = _queryEveryCell;
                _obstacleQueryRequired = false;
                _queryEveryCell = false;
                _build = new BuildContext(request, modifierSnapshots);
                _build.RequiresFullQuery = requiresFullQuery;
                _build.QueriesEveryCell = queryEveryCell;
                if (reuseObstacles)
                {
                    _build.ReusesObstacleQueries = true;
                    _build.Blocked = _committed.Blocked;
                    _build.Topology = _committed.Topology;
                    _build.EscapeDirections = _committed.EscapeDirections;
                }
                else if (requeryChangedCells)
                {
                    _build.QueryOnlyChangedRegions(_committed.Blocked);
                }
                else if (request.BakeMode == FlowFieldBakeMode.BakedBase && !queryEveryCell)
                {
                    // BakedBase starts again from the bake. With nothing that
                    // changed since, its obstacle results stand as baked.
                    _build.LoadsBakedObstacles = true;
                    _build.ReusesObstacleQueries = _safety.UncertainRegionCount == 0
                        && _session.Obstacles.Count == 0
                        && !_bakedBaseTouched.HasValue;
                }
            }
            return true;
        }

        /// <summary>
        /// Tracked obstacle changes report their regions, so the rest of the published
        /// mask stays correct. Only an explicit rebuild (RequestRebuild,
        /// NotifyCellsDirty) or new obstacle settings query every cell, which picks up
        /// a collider that moved without being tracked; in BakedBase it starts again
        /// from the bake.
        /// </summary>
        private bool CanRequeryChangedCells(in FlowFieldVolumeRequest request)
            => !_obstacleQueryRequired
                && _committed != null
                && request.BakeMode != FlowFieldBakeMode.StaticBaked
                && _safety.UncertainRegionCount > 0
                && _committed.Request.HasSameObstacleInputs(request);

        /// <summary>
        /// The obstacle result stands while nothing about the obstacles changed:
        /// a Goal, Default Direction or Modifier change reuses it, an explicit
        /// rebuild, a tracked change or new obstacle settings do not.
        /// </summary>
        private bool CanReuseObstacleQueries(in FlowFieldVolumeRequest request)
            => !_obstacleQueryRequired
                && _committed != null
                && request.BakeMode != FlowFieldBakeMode.StaticBaked
                && _safety.UncertainRegionCount == 0
                && _committed.Request.HasSameObstacleInputs(request);

        // A BakedBase build that starts again from the bake queries what the
        // bake cannot know: the uncertain regions, every region a change
        // reached since Initialize, and the registered obstacles.
        private Bounds[] BakedBaseRegions(BuildContext build)
        {
            var regions = new System.Collections.Generic.List<Bounds>(_safety.ObservedRegions());
            if (_bakedBaseTouched.HasValue)
                regions.Add(_bakedBaseTouched.Value);
            float margin = build.Request.Grid.CellSize + build.Request.ObstacleClearance;
            FlowFieldObstacleRegistry registry = _session.Obstacles;
            for (int i = 0; i < registry.Count; i++)
            {
                regions.Add(Reach(registry.GetBounds(i), margin));
                Collider collider = registry.GetCollider(i);
                if (collider != null)
                    regions.Add(Reach(collider.bounds, margin));
            }
            return regions.ToArray();
        }

        private static Bounds Reach(Bounds bounds, float margin)
        {
            bounds.Expand(Vector3.one * (margin * 2f));
            return bounds;
        }

        private void DiscardBuild()
        {
            BuildContext previous = _build;
            _build = null;
            // The build that replaces it still owes the full query it was asked for.
            if (previous != null && previous.RequiresFullQuery)
                _obstacleQueryRequired = true;
            if (previous != null && previous.QueriesEveryCell)
                _queryEveryCell = true;
            previous?.Dispose();
        }

        // The session advanced its generation, so the discarded build cannot publish.
        public void CancelWork()
        {
            DiscardBuild();
            _requests.Pending = false;
            _hasPendingModifierRecompose = false;
        }

        // The session marked the uncertain regions; the next build queries them.
        // BakedBase also keeps every region a change reached for a reloaded bake.
        public void ApplyObstacleChange(in FlowFieldObstacleChange change)
        {
            if (_session.BakeMode == FlowFieldBakeMode.BakedBase && _requests.HasLatest)
            {
                float margin = _requests.Latest.Grid.CellSize + _requests.Latest.ObstacleClearance;
                if (change.HasPrevious)
                    TouchBakedBase(Reach(change.Previous, margin));
                if (change.HasCurrent)
                    TouchBakedBase(Reach(change.Current, margin));
            }
            if (_requests.Pending)
                return;
            _requests.Pending = _requests.HasLatest && _requests.Latest.Grid.IsValid;
        }

        private void TouchBakedBase(Bounds reach)
        {
            Bounds touched = _bakedBaseTouched ?? reach;
            touched.Encapsulate(reach);
            _bakedBaseTouched = touched;
        }

        // Volume3D finds a Modifier's cells per build, so it keeps no area data.
        public IFlowFieldModifierArea CreateModifierArea() => null;

        public void ApplyModifierChange(in FlowFieldModifierUpdate update)
        {
            if (update.Kind == FlowFieldModifierUpdateKind.Changed && update.Requested == FlowFieldModifierChange.None)
                return;
            QueueModifierChange();
        }

        // The world may have changed while nothing observed it, so the session
        // is not Ready again until a new build publishes. The latest request
        // builds again.
        public void DropComputedState()
        {
            _committed = null;
            _requests.Pending = _requests.HasLatest;
        }

        public void ReleaseResources()
        {
            _committed = null;
            _requests.Clear();
            _bakedBaseTouched = null;
            _overlapBuffer = null;
            _cellQuery.Clear();
            _spares.Clear();
        }

        private void QueueModifierChange()
        {
            // Before the first BuildContext exists, interrupted capture owns
            // the retry. Otherwise the latest input must win over the older
            // active or published input, even after pending work was canceled.
            if (!_requests.Pending && _committed == null && _build == null)
                return;
            // Modifiers only affect the final composition. Unobserved colliders
            // wait for the next full build, as in Surface2D. When the running
            // build or, with none, the published field answers the latest
            // request, only the composition is owed, during the first build too.
            if (!_requests.Pending && _requests.HasLatest)
            {
                FlowFieldVolumeRequest next = _build != null ? _build.Request : _committed.Request;
                if (next.HasSameResult(_requests.Latest))
                {
                    _hasPendingModifierRecompose = true;
                    return;
                }
            }
            // The latest input was already accepted, and a Goal move within its
            // cell must not make Submit skip this build.
            if (_requests.HasLatest)
                _requests.Pending = true;
        }

        private static Vector3 ObstacleHalfExtents(float cellSize, float clearance)
            => Vector3.one * FlowFieldOverlapUtility.CellHalfExtent(cellSize, clearance);

        private void ThrowIfDisposed()
        {
            if (_session.Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldVolumeBuilder));
        }

        private void ValidateRequest(in FlowFieldVolumeRequest request)
        {
            if (!request.Grid.IsValid || request.Grid.SpaceMode != FlowFieldSpaceMode.Volume3D)
                throw new ArgumentException("Volume request requires a valid Volume3D grid.", nameof(request));
            if (request.ObstacleLayer.value == 0)
                throw new ArgumentException("Volume obstacle layer must contain at least one layer.", nameof(request));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleClearance) || request.ObstacleClearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleClearance));
            if (!FlowFieldVectorUtility.TryNormalizeDefaultDirection(request.DefaultDirection, out _))
                throw new ArgumentOutOfRangeException(nameof(request.DefaultDirection));
            if (request.HasGoal && !FlowFieldGridSpace.IsFinite(request.GoalWorld))
                throw new ArgumentOutOfRangeException(nameof(request.GoalWorld));
            if (!FlowFieldGridSpace.IsFinite(request.GoalInfluenceRadius) || request.GoalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(request.GoalInfluenceRadius));

            bool usesBake = FlowFieldBakeModes.UsesBakeAsset(request.BakeMode);
            if (usesBake && request.BakeData == null)
                throw new InvalidOperationException(
                    $"{request.BakeMode} Volume3D requests require a FlowFieldVolumeBakeData asset.");
            if (!usesBake && request.BakeData != null)
                throw new InvalidOperationException(
                    $"{request.BakeMode} Volume3D requests cannot carry a bake asset.");

            if (request.BakeData != null
                && !request.BakeData.Matches(
                    request.Grid,
                    request.WorldBounds,
                    request.ObstacleLayer,
                    request.ObstacleClearance,
                    out string staticReason))
                throw new InvalidOperationException(staticReason);
        }
    }

}
