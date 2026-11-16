using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldField<Common.FlowField.FlowFieldVolumeRequest>;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    /// <summary>
    /// The Volume3D strategy of a <see cref="FlowFieldSession"/>: cubic cells
    /// filling the Bounds, 26-neighbour navigation on worker threads, and
    /// re-queries limited to the regions whose obstacles changed.
    /// </summary>
    internal sealed partial class FlowFieldVolumeBuilder : IFlowFieldSpaceBuilder, IFlowFieldSampleSafety
    {
        private const int CellsPerStep = FlowFieldVolumeNavigation.CellsPerStep;
        private const int ArrayElementsPerStep = FlowFieldVolumeNavigation.ArrayElementsPerStep;
        private const double DefaultBudgetMilliseconds = 2.0;
        private const long MemoryBudgetBytes = 512L * 1024L * 1024L;
        private readonly FlowFieldVolumeObstacleTracker _obstacles;
        private Collider[] _registeredObstacleOverlapBuffer;
        private readonly int[] _obstacleCells = new int[CellsPerStep];
        private readonly FlowFieldVolumeCellQuery _cellQuery = new FlowFieldVolumeCellQuery();
        private readonly FlowFieldSpareArrays _spares = new FlowFieldSpareArrays();
        private readonly FlowFieldVolumeModifierRegistry _modifierRegistry = new FlowFieldVolumeModifierRegistry();
        private readonly FlowFieldVolumeSafetyState _safety = new FlowFieldVolumeSafetyState();

        private VolumeFieldData _committed;
        private BuildContext _build;
        private readonly FlowFieldRequestInbox<FlowFieldVolumeRequest> _requests
            = new FlowFieldRequestInbox<FlowFieldVolumeRequest>();
        // Modifier changes reuse the published navigation unless a full build is pending.
        private bool _hasPendingModifierRecompose;
        // An explicit rebuild re-queries obstacles even when only the Goal changed.
        private bool _obstacleQueryRequired;
        // Initialization uses Suspended as a staging state, but the first
        // accepted request must still be able to start a build. This flag
        // distinguishes that state from an explicit lifecycle suspension.
        // Whoever pumps directly (a bake, a test) waits for the worker thread.
        // The scheduler polls it, so no frame blocks on a solve.
        private bool _waitForSolve;
        private long _buildReservationBytes;
        private long _committedReservationBytes;

        private readonly Stopwatch _pumpTimer = new Stopwatch();
        private readonly Func<double> _pumpElapsedMilliseconds;

        // The session that runs this builder. Lifecycle, events and the build
        // loop live there.
        private readonly FlowFieldSession _session;

        internal FlowFieldVolumeBuilder(FlowFieldSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _obstacles = new FlowFieldVolumeObstacleTracker(MarkObstacleRegionDirty);
            _pumpElapsedMilliseconds = () => _pumpTimer.Elapsed.TotalMilliseconds;
        }

        public FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Volume3D;
        public bool IsWaitingForExternalCompletion
            => _build != null && _build.SolveRunning && !_build.SolveHandle.IsCompleted;
        public bool IsReady => _session.CanPump && _committed != null;
        internal long TrackedMemoryBytes
        {
            get
            {
                return checked(
                    _buildReservationBytes
                    + _committedReservationBytes
                    + EstimateAuxiliaryBytes());
            }
        }
        internal FlowFieldGridSpace Grid
            => _committed?.Grid ?? _build?.Request.Grid ?? (_requests.HasLatest ? _requests.Latest.Grid : default);
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

        internal void Initialize(FlowFieldBakeMode bakeMode)
        {
            ThrowIfDisposed();
            if (_session.IsInitialized)
                throw new InvalidOperationException("FlowField volume session is already initialized.");
            _session.BakeMode = bakeMode;
            _session.Fault = null;
            _requests.Clear();
            _hasPendingModifierRecompose = false;
            _obstacleQueryRequired = false;
            _safety.ClearRegions();
            _buildReservationBytes = 0;
            _committedReservationBytes = 0;
            // Running with no request yet; the first Submit starts a build.
            _session.SetLifecycle(FlowFieldSessionLifecycle.Active);
        }

        internal bool Submit(in FlowFieldVolumeRequest request, bool requeryObstacles = false)
        {
            ThrowIfDisposed();
            if (!_session.IsInitialized)
                throw new InvalidOperationException("FlowField volume session is not initialized.");
            ValidateRequest(request);
            if (requeryObstacles)
                _obstacleQueryRequired = true;
            bool goalMovedWithinCell = IsGoalMoveWithinCell(request);
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
            if (_committed != null
                && _committed.Request.HasSameGrid(request)
                && (_committed.Request.ObstacleLayer.value != request.ObstacleLayer.value
                    || Mathf.Abs(_committed.Request.ObstacleClearance - request.ObstacleClearance) > 0.0001f))
                _safety.InvalidateRegion(request.WorldBounds);
            if (goalMovedWithinCell)
                return true;
            _requests.Pending = true;
            EnterBuildingUnlessStopped();
            return true;
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

        // Accepted input waits while the session is stopped.
        private void EnterBuildingUnlessStopped()
        {
            if (!_session.IsFaulted && _session.Lifecycle != FlowFieldSessionLifecycle.Suspended)
                _session.SetLifecycle(FlowFieldSessionLifecycle.Building);
        }

        internal void Pump(double budgetMilliseconds = DefaultBudgetMilliseconds)
        {
            if (budgetMilliseconds <= 0d)
                budgetMilliseconds = DefaultBudgetMilliseconds;
            _pumpTimer.Restart();
            FlowFieldBuildBudget budget = new FlowFieldBuildBudget(
                budgetMilliseconds,
                _pumpElapsedMilliseconds);
            _session.RunPipeline(in budget, scheduled: false, waitForWorker: true);
        }

        public void BeforePump() => _obstacles.DetectRegisteredChanges();

        public bool BeginsAfterBudget => false;

        public int MaxBuildsBegunPerPump => int.MaxValue;

        public bool HasActiveBuild => _build != null;

        public bool TryBeginBuild(bool scheduled) => TryBeginPendingBuild();

        public FlowFieldBuildStep StepBuild(in FlowFieldBuildBudget budget, bool waitForWorker)
        {
            if (IsWaitingForExternalCompletion && !waitForWorker)
                return FlowFieldBuildStep.Waiting;
            _waitForSolve = waitForWorker;
            try
            {
                if (StepBuild(_build, budget))
                    return FlowFieldBuildStep.Completed;
                return _build.SolveRunning ? FlowFieldBuildStep.Waiting : FlowFieldBuildStep.Yield;
            }
            finally
            {
                _waitForSolve = false;
            }
        }

        public void PublishBuild() => CommitBuild(_build);

        // The next build may already be owed, so the session reports Ready
        // only once nothing is.
        public bool StaysBuildingWhileOwed => true;

        public bool HasOwedInput => _requests.Pending || _hasPendingModifierRecompose;

        private bool TryBeginPendingBuild()
        {
            if (!_requests.Pending && !_hasPendingModifierRecompose)
            {
                if (_committed != null && _session.IsRebuilding)
                    _session.SetLifecycle(FlowFieldSessionLifecycle.Active);
                return false;
            }

            bool recompose = !_requests.Pending;
            FlowFieldVolumeRequest request = recompose ? _committed.Request : _requests.Latest;
            _requests.Pending = false;
            _hasPendingModifierRecompose = false;
            FlowFieldModifierWorkItem[] modifierSnapshots;
            using (_session.Boundary.EnterUserCode())
                modifierSnapshots = _modifierRegistry.CaptureSnapshots();
            long reservation = EstimateBuildBytes(request.Grid.CellCount, modifierSnapshots.Length);
            if (!CanReserveForBuild(request, reservation))
                throw new InvalidOperationException(
                    $"FlowField Volume3D tracked memory would exceed the "
                    + $"{MemoryBudgetBytes / (1024d * 1024d):0} MiB manager budget "
                    + $"(required {EstimateRequiredBytes(request, reservation) / (1024d * 1024d):0.0} MiB).");
            // A recompose queries no obstacles, so it resolves no safety region.
            if (recompose)
            {
                _build = new BuildContext(
                    _committed,
                    modifierSnapshots,
                    FlowFieldSpareArrays.Rent(ref _spares.FinalDirections, request.Grid.CellCount),
                    FlowFieldSpareArrays.Rent(ref _spares.FinalSpeeds, request.Grid.CellCount));
            }
            else
            {
                bool reuseObstacles = CanReuseObstacleQueries(request);
                bool requeryChangedCells = !reuseObstacles && CanRequeryChangedCells(request);
                _obstacleQueryRequired = false;
                _safety.BeginBuild();
                _build = new BuildContext(request, modifierSnapshots);
                if (reuseObstacles)
                {
                    _build.ReusesObstacleQueries = true;
                    _build.Blocked = _committed.Blocked;
                    _build.Topology = _committed.Topology;
                    _build.EscapeDirections = _committed.EscapeDirections;
                }
                else if (requeryChangedCells)
                {
                    _build.QueryOnlyChangedRegions(_committed.Blocked, _safety.ObservedRegions());
                }
            }
            _buildReservationBytes = reservation;
            return true;
        }

        /// <summary>
        /// Tracked obstacle changes report their regions, so the rest of the published
        /// mask stays correct. An explicit rebuild or a resubmitted request still
        /// queries every cell, which picks up a collider that moved without being tracked.
        /// </summary>
        private bool CanRequeryChangedCells(in FlowFieldVolumeRequest request)
            => !_obstacleQueryRequired
                && _committed != null
                && request.StaticBakeData == null
                && _safety.UncertainRegionCount > 0
                && _committed.Request.HasSameObstacleInputs(request);

        /// <summary>A Goal change keeps the obstacle result unless a tracked obstacle changed.</summary>
        private bool CanReuseObstacleQueries(in FlowFieldVolumeRequest request)
            => !_obstacleQueryRequired
                && _committed != null
                && request.StaticBakeData == null
                && _safety.UncertainRegionCount == 0
                && _committed.Request.HasSameObstacleInputs(request)
                && !_committed.Request.HasSameGoal(request);

        private void DiscardBuild()
        {
            BuildContext previous = _build;
            _build = null;
            _buildReservationBytes = 0;
            previous?.Dispose();
        }

        private void CancelCurrentBuild()
        {
            _session.AdvanceLifetimeGeneration();
            DiscardBuild();
            _requests.Pending = false;
            _hasPendingModifierRecompose = false;
        }

        internal bool RegisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAvailable();
            return _obstacles.Register(collider);
        }

        public bool RegisterObstacleNow(Collider collider)
            => RegisterDynamicObstacle(collider);

        internal bool UnregisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAvailable();
            return _obstacles.Unregister(collider);
        }

        public bool UnregisterObstacleNow(Collider collider)
            => UnregisterDynamicObstacle(collider);

        internal void MarkObstacleRegionDirty(Bounds bounds)
        {
            if (!FlowFieldGridSpace.IsFinite(bounds.center) || !FlowFieldGridSpace.IsFinite(bounds.size))
                throw new ArgumentOutOfRangeException(nameof(bounds));
            FlowFieldVolumeRequest request = _requests.Latest;
            float margin = request.Grid.IsValid
                ? request.Grid.CellSize + request.ObstacleClearance
                : 1f;
            _safety.MarkDirty(bounds, margin);
            if (_requests.Pending)
                return;
            _requests.Pending = _requests.HasLatest && request.Grid.IsValid;
            if (_requests.Pending)
                EnterBuildingUnlessStopped();
        }

        public void NotifyObstacleRegionDirtyCommand(Bounds worldBounds)
            => MarkObstacleRegionDirty(worldBounds);

        internal void DetectUnregisteredObstacleChanges(bool enabled)
        {
            if (!enabled
                || !_session.IsInitialized
                || _session.IsFaulted
                || _session.BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            FlowFieldVolumeRequest request = _requests.HasLatest
                ? _requests.Latest
                : _build?.Request ?? _committed?.Request ?? _requests.Latest;
            if (!request.Grid.IsValid
                || !FlowFieldGridSpace.IsFinite(request.WorldBounds.center)
                || !FlowFieldGridSpace.IsFinite(request.WorldBounds.size))
                return;

            using FlowFieldExecutionBoundary.OperationScope operation = _session.Boundary.EnterOperation();
            _obstacles.DetectUnregisteredChanges(in request);
        }

        internal void DetectModifierChanges()
        {
            bool changed;
            using (_session.Boundary.EnterUserCode())
                changed = _modifierRegistry.DetectChanges();
            if (changed)
                QueueModifierChange();
        }

        internal bool RegisterModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            bool registered;
            using (_session.Boundary.EnterUserCode())
                registered = _modifierRegistry.Register(modifier);
            if (!registered)
                return false;
            QueueModifierChange();
            return true;
        }

        public bool RegisterModifierNow(IFlowFieldVectorModifier modifier)
            => RegisterModifier(modifier);

        internal bool UnregisterModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfDisposed();
            if (!_session.IsInitialized && !_session.IsFaulted)
                throw new InvalidOperationException("FlowField volume session is unavailable.");
            if (!_modifierRegistry.Unregister(modifier))
                return false;
            // A retry builds everything again.
            if (!_session.IsFaulted)
                QueueModifierChange();
            return true;
        }

        public bool UnregisterModifierNow(IFlowFieldVectorModifier modifier)
            => UnregisterModifier(modifier);

        public void NotifyModifierChangedNow(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            ThrowIfInputAvailable();
            bool valid;
            using (_session.Boundary.EnterUserCode())
                valid = _modifierRegistry.ValidateModifierChange(modifier, change);
            if (valid && change != FlowFieldModifierChange.None)
                QueueModifierChange();
        }

        public void Stop()
        {
            if (_session.Lifecycle != FlowFieldSessionLifecycle.Active
                && _session.Lifecycle != FlowFieldSessionLifecycle.Building)
                return;
            CancelCurrentBuild();
            DropCommittedField();
            _session.SetLifecycle(FlowFieldSessionLifecycle.Suspended);
        }

        // The world may have changed while nothing observed it, so the session
        // is not Ready again until a new build publishes.
        private void DropCommittedField()
        {
            _committed = null;
            _committedReservationBytes = 0;
        }

        public void Release()
        {
            if (!_session.IsInitialized && !_session.IsFaulted)
                return;
            CancelCurrentBuild();
            _session.Fault = null;
            DropCommittedField();
            _requests.Clear();
            _safety.ClearRegions();
            _session.DetachRuntimeScheduler();
            _obstacles.Clear();
            _registeredObstacleOverlapBuffer = null;
            _cellQuery.Clear();
            _spares.Clear();
            _modifierRegistry.Clear();
            _session.SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        /// <summary>
        /// Starts a stopped or faulted session again from nothing. Registered
        /// obstacles and Modifiers stay; the latest request builds again.
        /// </summary>
        public void Restart()
        {
            ThrowIfDisposed();
            if (_session.Lifecycle != FlowFieldSessionLifecycle.Suspended && !_session.IsFaulted)
                return;
            CancelCurrentBuild();
            _session.Fault = null;
            DropCommittedField();
            _requests.Pending = _requests.HasLatest;
            _session.SetLifecycle(FlowFieldSessionLifecycle.Building);
        }

        public void AbandonWorkForFault() => CancelCurrentBuild();

        public void ApplyFaultedState() => _session.SetLifecycle(FlowFieldSessionLifecycle.Faulted);

        private void QueueModifierChange()
        {
            // Before the first BuildContext exists, interrupted capture owns
            // the retry. Otherwise the latest input must win over the older
            // active or published input, even after pending work was canceled.
            if (!_requests.Pending && _committed == null && _build == null)
                return;
            // Modifiers only affect the final composition. Unobserved colliders
            // wait for the next full build, as Surface2D already does.
            if (!_requests.Pending
                && _committed != null
                && _requests.HasLatest
                && _committed.Request.HasSameResult(_requests.Latest))
            {
                _hasPendingModifierRecompose = true;
                EnterBuildingUnlessStopped();
                return;
            }
            if (_requests.HasLatest)
            {
                // The latest input was already accepted, and a Goal move within
                // its cell must not make Submit skip this build.
                _requests.Pending = true;
                EnterBuildingUnlessStopped();
            }
            else if (_committed != null)
                Submit(_committed.Request);
            else if (_build != null)
                Submit(_build.Request);
        }

        private static Vector3 ObstacleHalfExtents(float cellSize, float clearance)
            => Vector3.one * FlowFieldOverlapUtility.CellHalfExtent(cellSize, clearance);

        private void ThrowIfInputAvailable()
        {
            ThrowIfDisposed();
            if (!_session.IsInitialized || _session.IsFaulted)
                throw new InvalidOperationException("FlowField volume session is unavailable.", _session.Fault);
        }

        private void ThrowIfDisposed()
        {
            if (_session.Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldVolumeBuilder));
        }

        private void ValidateRequest(in FlowFieldVolumeRequest request)
        {
            if (!request.Grid.IsValid || request.Grid.SpaceMode != FlowFieldSpaceMode.Volume3D)
                throw new ArgumentException("Volume request requires a valid Volume3D grid.", nameof(request));
            if ((long)request.Grid.CellCount > FlowFieldBakeBoundsUtility.MaxVolumeCellCount)
                throw new ArgumentOutOfRangeException(nameof(request), "Volume cell count exceeds one million cells.");
            if (request.ObstacleLayer.value == 0)
                throw new ArgumentException("Volume obstacle layer must contain at least one layer.", nameof(request));
            if (!FlowFieldGridSpace.IsFinite(request.ObstacleClearance) || request.ObstacleClearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(request.ObstacleClearance));
            if (!FlowFieldGridSpace.IsFinite(request.DefaultDirection)
                || request.DefaultDirection.sqrMagnitude <= 0.00000001f)
                throw new ArgumentOutOfRangeException(nameof(request.DefaultDirection));
            if (request.HasGoal && !FlowFieldGridSpace.IsFinite(request.GoalWorld))
                throw new ArgumentOutOfRangeException(nameof(request.GoalWorld));
            if (!FlowFieldGridSpace.IsFinite(request.GoalInfluenceRadius) || request.GoalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(request.GoalInfluenceRadius));

            if (request.BakeMode == FlowFieldBakeMode.StaticBaked && request.StaticBakeData == null)
                throw new InvalidOperationException(
                    "StaticBaked Volume3D requests require a FlowFieldVolumeBakeData asset.");
            if (request.BakeMode != FlowFieldBakeMode.StaticBaked && request.StaticBakeData != null)
                throw new InvalidOperationException(
                    "RuntimeDynamic Volume3D requests cannot carry a static bake asset.");

            long buildReservation = EstimateBuildBytes(request.Grid.CellCount, _modifierRegistry.Count);
            long estimate = EstimateRequiredBytes(request, buildReservation);
            if (estimate > MemoryBudgetBytes)
                throw new InvalidOperationException(
                    $"FlowField Volume3D tracked memory {estimate / (1024d * 1024d):0.0} MiB "
                    + "exceeds the 512 MiB manager budget.");
            if (request.StaticBakeData != null
                && !request.StaticBakeData.Matches(
                    request.Grid,
                    request.WorldBounds,
                    request.ObstacleLayer,
                    request.ObstacleClearance,
                    out string staticReason))
                throw new InvalidOperationException(staticReason);
        }

        public void Dispose()
        {
            if (_session.Disposed)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = _session.Boundary.EnterOperation();
            _session.Disposed = true;
            _session.DetachRuntimeScheduler();
            CancelCurrentBuild();
            _requests.Clear();
            DropCommittedField();
            _obstacles.Clear();
            _registeredObstacleOverlapBuffer = null;
            _cellQuery.Clear();
            _spares.Clear();
            _modifierRegistry.Clear();
            _safety.ClearRegions();
            _session.SetLifecycle(FlowFieldSessionLifecycle.Released);
            _session.MarkBaseDisposed();
        }
    }

}
