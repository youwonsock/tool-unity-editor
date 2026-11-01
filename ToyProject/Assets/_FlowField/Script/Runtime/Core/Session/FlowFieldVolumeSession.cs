using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldVolumeFieldData;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    internal sealed partial class FlowFieldVolumeSession : FlowFieldSessionBase
    {
        private const int CellsPerStep = FlowFieldVolumeNavigation.CellsPerStep;
        private const int ArrayElementsPerStep = FlowFieldVolumeNavigation.ArrayElementsPerStep;
        private const double DefaultBudgetMilliseconds = 2.0;
        private const long MemoryBudgetBytes = 512L * 1024L * 1024L;
        private readonly FlowFieldVolumeObstacleTracker _obstacles;
        private Collider[] _registeredObstacleOverlapBuffer;
        private readonly int[] _obstacleCells = new int[CellsPerStep];
        private readonly FlowFieldVolumeCellQuery _cellQuery = new FlowFieldVolumeCellQuery();
        private readonly FlowFieldVolumeSpareArrays _spares = new FlowFieldVolumeSpareArrays();
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
        private bool _suspendedByLifecycle;
        private static long _nextVisualizationResultId;
        // Whoever pumps directly (a bake, a test) waits for the worker thread.
        // The scheduler polls it, so no frame blocks on a solve.
        private bool _waitForSolve;
        private long _buildReservationBytes;
        private long _committedReservationBytes;

        private readonly Stopwatch _pumpTimer = new Stopwatch();
        private readonly Func<double> _pumpElapsedMilliseconds;

        internal FlowFieldVolumeSession(FlowFieldExecutionBoundary boundary = null)
            : base(boundary)
        {
            _obstacles = new FlowFieldVolumeObstacleTracker(MarkObstacleRegionDirty);
            _pumpElapsedMilliseconds = () => _pumpTimer.Elapsed.TotalMilliseconds;
        }

        internal override FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Volume3D;
        internal override bool IsWaitingForExternalCompletion
            => _build != null && _build.SolveRunning && !_build.SolveHandle.IsCompleted;
        internal override bool IsReady => CanPump && _committed != null;
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
        internal FlowFieldGridSpace Grid => _committed?.Grid ?? _build?.Request.Grid ?? default;
        internal override bool HasPendingRequest => _requests.Pending || _hasPendingModifierRecompose || _build != null;
        internal bool TryGetLatestRequestedInput(out FlowFieldVolumeRequest request)
        {
            request = _requests.Latest;
            return _requests.HasLatest;
        }

        internal bool ContainsModifier(IFlowFieldVectorModifier modifier)
            => _modifierRegistry.Contains(modifier);

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

        internal bool HasSpareArrays => !_spares.IsEmpty;

        internal void Initialize(FlowFieldBakeMode bakeMode)
        {
            ThrowIfDisposed();
            if (IsInitialized)
                throw new InvalidOperationException("FlowField volume session is already initialized.");
            BakeMode = bakeMode;
            Fault = null;
            _requests.Clear();
            _hasPendingModifierRecompose = false;
            _obstacleQueryRequired = false;
            _safety.ClearRegions();
            _suspendedByLifecycle = false;
            _buildReservationBytes = 0;
            _committedReservationBytes = 0;
            SetLifecycle(FlowFieldSessionLifecycle.Suspended);
        }

        internal bool Submit(in FlowFieldVolumeRequest request, bool requeryObstacles = false)
        {
            ThrowIfDisposed();
            if (!IsInitialized)
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
            // immediately.
            if (_build != null && !_build.Request.HasSameGrid(request))
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

        // Accepted input waits while the lifecycle has stopped this session.
        private void EnterBuildingUnlessStopped()
        {
            if (!IsFaulted && (State != FlowFieldRuntimeState.Suspended || !_suspendedByLifecycle))
                SetLifecycle(FlowFieldSessionLifecycle.Building);
        }

        internal void Pump(double budgetMilliseconds = DefaultBudgetMilliseconds)
        {
            if (budgetMilliseconds <= 0d)
                budgetMilliseconds = DefaultBudgetMilliseconds;
            _pumpTimer.Restart();
            FlowFieldBuildBudget budget = new FlowFieldBuildBudget(
                budgetMilliseconds,
                _pumpElapsedMilliseconds);
            _waitForSolve = true;
            try { PumpInternal(in budget); }
            finally { _waitForSolve = false; }
        }

        internal override void PumpInternal(in FlowFieldBuildBudget budget)
        {
            if (!CanPump)
                return;

            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            try
            {
                _obstacles.DetectRegisteredChanges();
                if (IsWaitingForExternalCompletion && !_waitForSolve)
                    return;

                while (!budget.IsExpired)
                {
                    if (_build == null && !TryBeginPendingBuild())
                        return;
                    if (!StepBuild(_build, budget))
                        return;

                    CommitBuild(_build);
                    if (_requests.Pending || _hasPendingModifierRecompose)
                        continue;
                    if (IsRebuilding)
                        SetLifecycle(FlowFieldSessionLifecycle.Active);
                    return;
                }
            }
            catch (Exception exception)
            {
                ReportFault(exception);
            }
        }

        private bool TryBeginPendingBuild()
        {
            if (!_requests.Pending && !_hasPendingModifierRecompose)
            {
                if (_committed != null && IsRebuilding)
                    SetLifecycle(FlowFieldSessionLifecycle.Active);
                return false;
            }

            bool recompose = !_requests.Pending;
            FlowFieldVolumeRequest request = recompose ? _committed.Request : _requests.Latest;
            _requests.Pending = false;
            _hasPendingModifierRecompose = false;
            FlowFieldModifierWorkItem[] modifierSnapshots;
            using (Boundary.EnterUserCode())
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
                    FlowFieldVolumeSpareArrays.Rent(ref _spares.FinalDirections, request.Grid.CellCount),
                    FlowFieldVolumeSpareArrays.Rent(ref _spares.FinalSpeeds, request.Grid.CellCount));
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
            AdvanceLifetimeGeneration();
            DiscardBuild();
            _requests.Pending = false;
            _hasPendingModifierRecompose = false;
        }

        internal bool RegisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAvailable();
            return _obstacles.Register(collider);
        }

        private protected override bool RegisterObstacleNow(Collider collider)
            => RegisterDynamicObstacle(collider);

        internal bool UnregisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAvailable();
            return _obstacles.Unregister(collider);
        }

        private protected override bool UnregisterObstacleNow(Collider collider)
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

        internal override void NotifyObstacleRegionDirtyCommand(Bounds worldBounds)
            => MarkObstacleRegionDirty(worldBounds);

        internal void DetectUnregisteredObstacleChanges(bool enabled)
        {
            if (!enabled
                || !IsInitialized
                || IsFaulted
                || BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            FlowFieldVolumeRequest request = _requests.HasLatest
                ? _requests.Latest
                : _build?.Request ?? _committed?.Request ?? _requests.Latest;
            if (!request.Grid.IsValid
                || !FlowFieldGridSpace.IsFinite(request.WorldBounds.center)
                || !FlowFieldGridSpace.IsFinite(request.WorldBounds.size))
                return;

            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            _obstacles.DetectUnregisteredChanges(in request);
        }

        internal void DetectModifierChanges()
        {
            bool changed;
            using (Boundary.EnterUserCode())
                changed = _modifierRegistry.DetectChanges();
            if (changed)
                QueueModifierChange();
        }

        internal bool RegisterModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            bool registered;
            using (Boundary.EnterUserCode())
                registered = _modifierRegistry.Register(modifier);
            if (!registered)
                return false;
            QueueModifierChange();
            return true;
        }

        private protected override bool RegisterModifierNow(IFlowFieldVectorModifier modifier)
            => RegisterModifier(modifier);

        internal bool UnregisterModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            if (!_modifierRegistry.Unregister(modifier))
                return false;
            QueueModifierChange();
            return true;
        }

        private protected override bool UnregisterModifierNow(IFlowFieldVectorModifier modifier)
            => UnregisterModifier(modifier);

        private protected override void NotifyModifierChangedNow(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            ThrowIfInputAvailable();
            bool valid;
            using (Boundary.EnterUserCode())
                valid = _modifierRegistry.ValidateModifierChange(modifier, change);
            if (valid && change != FlowFieldModifierChange.None)
                QueueModifierChange();
        }

        internal override void Suspend()
        {
            if (!IsInitialized)
                return;
            _suspendedByLifecycle = true;
            CancelCurrentBuild();
            SetLifecycle(FlowFieldSessionLifecycle.Suspended);
        }

        internal void Resume()
        {
            if (State != FlowFieldRuntimeState.Suspended)
                return;
            _suspendedByLifecycle = false;
            SetLifecycle(FlowFieldSessionLifecycle.Building);
        }

        internal override void Release()
        {
            if (!IsInitialized && !IsFaulted)
                return;
            CancelCurrentBuild();
            Fault = null;
            _committed = null;
            _committedReservationBytes = 0;
            _requests.HasLatest = false;
            _suspendedByLifecycle = false;
            _safety.ClearRegions();
            DetachRuntimeScheduler();
            _obstacles.Clear();
            _registeredObstacleOverlapBuffer = null;
            _cellQuery.Clear();
            _spares.Clear();
            _modifierRegistry.Clear();
            SetLifecycle(FlowFieldSessionLifecycle.Released);
        }

        internal override void RetryFault()
        {
            if (!IsFaulted)
                return;
            AdvanceLifetimeGeneration();
            Fault = null;
            // A retry only clears the failure.  The next accepted request
            // must still build and publish before the session becomes Ready;
            // never expose a transient Ready state with no valid result.
            SetLifecycle(_suspendedByLifecycle
                ? FlowFieldSessionLifecycle.Suspended
                : FlowFieldSessionLifecycle.Building);
        }

        private protected override void AbandonWorkForFault() => CancelCurrentBuild();

        private protected override void ApplyFaultedState() => SetLifecycle(FlowFieldSessionLifecycle.Faulted);

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
            if (!IsInitialized || IsFaulted)
                throw new InvalidOperationException("FlowField volume session is unavailable.", Fault);
        }

        private void ThrowIfDisposed()
        {
            if (Disposed)
                throw new ObjectDisposedException(nameof(FlowFieldVolumeSession));
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

        public override void Dispose()
        {
            if (Disposed)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = Boundary.EnterOperation();
            Disposed = true;
            DetachRuntimeScheduler();
            CancelCurrentBuild();
            _requests.HasLatest = false;
            _committed = null;
            _committedReservationBytes = 0;
            _obstacles.Clear();
            _registeredObstacleOverlapBuffer = null;
            _cellQuery.Clear();
            _spares.Clear();
            _modifierRegistry.Clear();
            _safety.ClearRegions();
            SetLifecycle(FlowFieldSessionLifecycle.Released);
            MarkBaseDisposed();
        }

        internal void Cancel()
        {
            if (Disposed)
                return;
            CancelCurrentBuild();
        }

    }

}
