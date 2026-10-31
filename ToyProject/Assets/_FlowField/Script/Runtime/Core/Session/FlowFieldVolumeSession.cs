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
    internal sealed partial class FlowFieldVolumeSession : FlowFieldSessionBase, IFlowFieldBuildOperation
    {
        private const int CellsPerStep = FlowFieldVolumeNavigation.CellsPerStep;
        private const int ArrayElementsPerStep = FlowFieldVolumeNavigation.ArrayElementsPerStep;
        private const double DefaultBudgetMilliseconds = 2.0;
        private const long MemoryBudgetBytes = 512L * 1024L * 1024L;
        private const int Unreachable = FlowFieldVolumeNavigation.Unreachable;
        private readonly FlowFieldVolumeObstacleTracker _obstacles;
        private Collider[] _registeredObstacleOverlapBuffer;
        private readonly FlowFieldVolumeCellQuery _cellQuery = new FlowFieldVolumeCellQuery();
        // Build-only search arrays of the last committed build, reused by the
        // next build of the same size (published arrays are never reused).
        private int[] _spareCosts;
        private int[] _spareQueue;
        private bool[] _spareInfluence;
        private readonly FlowFieldVolumeModifierRegistry _modifierRegistry = new FlowFieldVolumeModifierRegistry();
        private readonly FlowFieldVolumeSafetyState _safety = new FlowFieldVolumeSafetyState();

        private FlowFieldComputeSolver _computeSolver;
        private bool _gpuDisabled;
        private VolumeFieldData _committed;
        private BuildContext _build;
        // The latest accepted input is also the next build input when pending.
        // A running BuildContext and published FieldData retain their own inputs.
        private bool _hasPendingRequest;
        // Modifier changes reuse the published navigation unless a full build is pending.
        private bool _hasPendingModifierRecompose;
        // An explicit rebuild re-queries obstacles even when only the Goal changed.
        private bool _obstacleQueryRequired;
        private FlowFieldVolumeRequest _latestRequestedInput;
        private bool _hasLatestRequestedInput;
        // Initialization uses Suspended as a staging state, but the first
        // accepted request must still be able to start a build. This flag
        // distinguishes that state from an explicit lifecycle suspension.
        private bool _suspendedByLifecycle;
        private static long _nextVisualizationResultId;
        private bool _pumping;
        private bool _runtimeScheduled;
        private long _buildReservationBytes;
        private long _committedReservationBytes;

        private readonly Stopwatch _pumpTimer = new Stopwatch();
        private readonly Func<double> _pumpElapsedMilliseconds;

        internal FlowFieldVolumeSession()
        {
            _obstacles = new FlowFieldVolumeObstacleTracker(MarkObstacleRegionDirty);
            _pumpElapsedMilliseconds = () => _pumpTimer.Elapsed.TotalMilliseconds;
        }

        internal override FlowFieldSpaceMode SpaceMode => FlowFieldSpaceMode.Volume3D;
        internal override bool IsInitialized => State == FlowFieldRuntimeState.Building
            || State == FlowFieldRuntimeState.Ready
            || State == FlowFieldRuntimeState.Suspended;
        internal override bool IsFaulted => State == FlowFieldRuntimeState.Faulted;
        internal override bool IsRebuilding => State == FlowFieldRuntimeState.Building;
        internal bool IsWaitingForExternalCompletion
            => _build != null && _build.GpuPending;
        internal bool IsBuilding
            => CanPump && (IsRebuilding || HasPendingRequest);
        internal override bool IsReady => (State == FlowFieldRuntimeState.Building
                || State == FlowFieldRuntimeState.Ready)
            && _committed != null
            && !IsFaulted;
        internal long TrackedMemoryBytes
        {
            get
            {
                return checked(
                    _buildReservationBytes
                    + _committedReservationBytes
                    + (_computeSolver?.AllocatedBytes ?? 0L)
                    + EstimateAuxiliaryBytes());
            }
        }
        internal FlowFieldGridSpace Grid => _committed?.Grid ?? _build?.Request.Grid ?? default;
        internal bool HasPendingRequest => _hasPendingRequest || _hasPendingModifierRecompose || _build != null;
        internal bool TryGetLatestRequestedInput(out FlowFieldVolumeRequest request)
        {
            request = _latestRequestedInput;
            return _hasLatestRequestedInput;
        }

        internal bool ContainsModifier(IFlowFieldVectorModifier modifier)
            => _modifierRegistry.Contains(modifier);

        internal float BuildProgress
        {
            get
            {
                if (_build == null)
                    return _hasPendingRequest || _hasPendingModifierRecompose ? 0f : 1f;
                if (_build.Phase == BuildPhase.Complete)
                    return 1f;

                const float phaseCount = 9f;
                float local = _build.Phase == BuildPhase.Bfs
                    ? _build.Count <= 0 ? 0f : (float)_build.Head / _build.Count
                    : _build.Phase == BuildPhase.Escape
                        ? _build.Count <= 0 ? 0f : (float)_build.Cursor / _build.Count
                        : _build.Count <= 0 ? 0f : (float)_build.Cursor / _build.Count;
                return Mathf.Clamp01(((int)_build.Phase + Mathf.Clamp01(local)) / phaseCount);
            }
        }

        internal void Initialize(FlowFieldBakeMode bakeMode, ComputeShader computeShader = null)
        {
            ThrowIfDisposed();
            if (IsInitialized)
                throw new InvalidOperationException("FlowField volume session is already initialized.");
            BakeMode = bakeMode;
            Fault = null;
            _gpuDisabled = false;
            GpuFallbackReason = null;
            _hasLatestRequestedInput = false;
            _latestRequestedInput = default;
            _hasPendingRequest = false;
            _hasPendingModifierRecompose = false;
            _obstacleQueryRequired = false;
            _safety.ClearRegions();
            _suspendedByLifecycle = false;
            _buildReservationBytes = 0;
            _committedReservationBytes = 0;
            _computeSolver?.Dispose();
            _computeSolver = null;
            if (computeShader != null)
            {
                try
                {
                    _computeSolver = new FlowFieldComputeSolver(computeShader);
                    if (!_computeSolver.IsSupported)
                        DisableGpu("Compute Shader 또는 AsyncGPUReadback을 지원하지 않습니다.");
                }
                catch (Exception exception)
                {
                    DisableGpu(exception.Message);
                    _computeSolver = null;
                }
            }
            InvalidateBuildGeneration();
            SetState(FlowFieldRuntimeState.Suspended);
        }

        internal void AttachRuntimeScheduler()
        {
            ThrowIfDisposed();
            if (_runtimeScheduled)
                return;
            FlowFieldBuildScheduler.Register(this);
            _runtimeScheduled = true;
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
            _latestRequestedInput = request;
            _hasLatestRequestedInput = true;
            if (_build != null && !_build.Request.HasSameGrid(request))
            {
                InvalidateBuildGeneration();
                // A grid/signature change invalidates the private staging
                // arrays immediately. If an async GPU solve belongs to the
                // discarded build, its late callback is ignored.
                DiscardBuild();
            }
            if (_committed != null
                && _committed.Request.HasSameGrid(request)
                && (_committed.Request.ObstacleLayer.value != request.ObstacleLayer.value
                    || Mathf.Abs(_committed.Request.ObstacleClearance - request.ObstacleClearance) > 0.0001f))
                _safety.InvalidateRegion(request.WorldBounds);
            if (goalMovedWithinCell)
                return true;
            _hasPendingRequest = true;
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
            if (_obstacleQueryRequired || _hasPendingRequest || request.BakeMode == FlowFieldBakeMode.StaticBaked)
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
                SetState(FlowFieldRuntimeState.Building);
        }

        internal void Pump(double budgetMilliseconds = DefaultBudgetMilliseconds)
        {
            // A nested Pump would return from PumpInternal anyway; returning
            // first keeps it from restarting the outer pump's timer.
            if (_pumping)
                return;
            if (budgetMilliseconds <= 0d)
                budgetMilliseconds = DefaultBudgetMilliseconds;
            _pumpTimer.Restart();
            FlowFieldBuildBudget budget = new FlowFieldBuildBudget(
                budgetMilliseconds,
                _pumpElapsedMilliseconds);
            PumpInternal(in budget);
        }

        private void PumpInternal(in FlowFieldBuildBudget budget)
        {
            if (_pumping || !CanPump)
                return;

            _pumping = true;
            int pumpGeneration = OperationGeneration;
            BuildContext activeBuild = _build;
            try
            {
                if (!_obstacles.DetectRegisteredChanges()
                    || !CanContinuePump(pumpGeneration, activeBuild))
                    return;
                if (IsWaitingForExternalCompletion)
                    return;

                while (!budget.IsExpired)
                {
                    if (_build == null && !TryBeginPendingBuild(pumpGeneration))
                        return;

                    activeBuild = _build;
                    if (!StepBuild(activeBuild, budget))
                        return;

                    BuildContext completedBuild = activeBuild;
                    bool commitCallbackChangedGeneration = CommitBuild(completedBuild);
                    if (ReferenceEquals(_build, completedBuild))
                    {
                        DiscardBuild();
                    }
                    activeBuild = null;
                    // CommitBuild normally clears _build before invoking
                    // user callbacks. A callback is nevertheless allowed to
                    // Release and immediately Init/submit a new build. In
                    // that case the old pump must not finish the new build's
                    // lifecycle transition or consume its pending request.
                    if (_build != null || commitCallbackChangedGeneration)
                        return;
                    // Commit callbacks are allowed to Release, Suspend or
                    // submit a new request. Never let the normal completion
                    // path overwrite that lifecycle decision with Ready.
                    if (!CanPump)
                        return;
                    // A successful grid replacement can advance the generation
                    // itself. Only that completed transition may be adopted;
                    // callback-driven changes have already returned above.
                    pumpGeneration = OperationGeneration;
                    if (_hasPendingRequest || _hasPendingModifierRecompose)
                        continue;
                    if (State == FlowFieldRuntimeState.Building)
                        SetState(FlowFieldRuntimeState.Ready);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                // A lifecycle or grid-signature generation change invalidates
                // only the private staging build. The latest coalesced request
                // remains pending and must continue without Faulting the
                // provider or publishing the stale result.
                if (!CanContinuePump(pumpGeneration, activeBuild))
                    return;
                DiscardBuild();
                if (_hasPendingRequest)
                    SetState(FlowFieldRuntimeState.Building);
            }
            catch (Exception exception)
            {
                // An exception thrown after Release/Init, Suspend or Cancel
                // still belongs to the old operation. It must not discard a
                // newer request or overwrite the chosen lifecycle state.
                if (CanContinuePump(pumpGeneration, activeBuild))
                    ReportFault(exception);
            }
            finally
            {
                _pumping = false;
            }
        }

        private bool TryBeginPendingBuild(int pumpGeneration)
        {
            if (!_hasPendingRequest && !_hasPendingModifierRecompose)
            {
                if (_committed != null && State == FlowFieldRuntimeState.Building)
                    SetState(FlowFieldRuntimeState.Ready);
                return false;
            }

            bool recompose = !_hasPendingRequest;
            FlowFieldVolumeRequest request = recompose ? _committed.Request : _latestRequestedInput;
            _hasPendingRequest = false;
            _hasPendingModifierRecompose = false;
            if (!TryCaptureModifiers(pumpGeneration, out FlowFieldModifierWorkItem[] modifierSnapshots))
                return false;
            long reservation = EstimateBuildBytes(request.Grid.CellCount, modifierSnapshots.Length);
            if (!CanReserveForBuild(request, reservation))
                throw new InvalidOperationException(
                    $"FlowField Volume3D tracked memory would exceed the "
                    + $"{MemoryBudgetBytes / (1024d * 1024d):0} MiB manager budget "
                    + $"(required {EstimateRequiredBytes(request, reservation) / (1024d * 1024d):0.0} MiB).");
            // A recompose queries no obstacles, so it resolves no safety region.
            if (recompose)
            {
                _build = new BuildContext(_committed, pumpGeneration, modifierSnapshots);
            }
            else
            {
                bool reuseObstacles = CanReuseObstacleQueries(request);
                _obstacleQueryRequired = false;
                _safety.BeginBuild();
                _build = new BuildContext(request, pumpGeneration, modifierSnapshots);
                if (reuseObstacles)
                {
                    _build.ReusesObstacleQueries = true;
                    _build.Blocked = _committed.Blocked;
                    _build.Topology = _committed.Topology;
                }
            }
            _buildReservationBytes = reservation;
            return true;
        }

        /// <summary>
        /// A Goal change keeps the obstacle result unless a tracked obstacle
        /// changed. Unobserved colliders wait for the next full build.
        /// </summary>
        private bool CanReuseObstacleQueries(in FlowFieldVolumeRequest request)
            => !_obstacleQueryRequired
                && _committed != null
                && request.StaticBakeData == null
                && _safety.UncertainRegionCount == 0
                && _committed.Request.HasSameObstacleInputs(request)
                && !_committed.Request.HasSameGoal(request);

        private bool CanPump => !Disposed
            && (State == FlowFieldRuntimeState.Building || State == FlowFieldRuntimeState.Ready);

        private bool CanContinuePump(int generation, BuildContext build)
            => CanPump && OperationGeneration == generation && ReferenceEquals(_build, build);

        private void EnsureCurrentBuild(BuildContext build)
        {
            if (build == null || !CanContinuePump(build.Generation, build))
                throw new OperationCanceledException("FlowField volume build is no longer current.");
        }

        private bool TryCaptureModifiers(int generation, out FlowFieldModifierWorkItem[] snapshots)
        {
            int captureVersion = _modifierRegistry.CaptureVersion;
            try
            {
                snapshots = _modifierRegistry.CaptureSnapshots();
            }
            catch (Exception) when (!CanContinuePump(generation, null)
                || captureVersion != _modifierRegistry.CaptureVersion)
            {
                snapshots = null;
            }

            if (!CanContinuePump(generation, null))
                return false;
            if (captureVersion == _modifierRegistry.CaptureVersion)
                return true;

            // Registration may change before the first BuildContext exists.
            // QueueModifierChange then has no build/publication to fall back
            // to. Retry the newest accepted input at the next owner boundary.
            if (_hasLatestRequestedInput)
                _hasPendingRequest = true;
            return false;
        }

        private void InvalidateBuildGeneration()
        {
            AdvanceOperationGeneration();
            _obstacles.InvalidateObservations();
            _modifierRegistry.InvalidateCaptures();
        }

        /// <summary>
        /// Takes the spare array when it has the requested size and clears it,
        /// which leaves it in the same state as a new allocation.
        /// </summary>
        private static T[] RentBuildArray<T>(ref T[] spare, int count)
        {
            T[] array = spare;
            spare = null;
            if (array == null || array.Length != count)
                return new T[count];
            Array.Clear(array, 0, count);
            return array;
        }

        private void ReleaseSpareBuildArrays()
        {
            _spareCosts = null;
            _spareQueue = null;
            _spareInfluence = null;
        }

        private void DiscardBuild()
        {
            BuildContext previous = _build;
            _build = null;
            _buildReservationBytes = 0;
            previous?.Dispose();
        }

        private void CancelCurrentBuild()
        {
            InvalidateBuildGeneration();
            DiscardBuild();
            _hasPendingRequest = false;
            _hasPendingModifierRecompose = false;
        }

        internal bool RegisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAvailable();
            return _obstacles.Register(collider);
        }

        internal override bool RegisterObstacleCommand(Collider collider)
            => RegisterDynamicObstacle(collider);

        internal bool UnregisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAvailable();
            return _obstacles.Unregister(collider);
        }

        internal override bool UnregisterObstacleCommand(Collider collider)
            => UnregisterDynamicObstacle(collider);

        internal void MarkObstacleRegionDirty(Bounds bounds)
        {
            if (!FlowFieldGridSpace.IsFinite(bounds.center) || !FlowFieldGridSpace.IsFinite(bounds.size))
                throw new ArgumentOutOfRangeException(nameof(bounds));
            FlowFieldVolumeRequest request = _latestRequestedInput;
            float margin = request.Grid.IsValid
                ? request.Grid.CellSize + request.ObstacleClearance
                : 1f;
            _safety.MarkDirty(bounds, margin);
            if (_hasPendingRequest)
                return;
            _hasPendingRequest = _hasLatestRequestedInput && request.Grid.IsValid;
            if (_hasPendingRequest)
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
            FlowFieldVolumeRequest request = _hasLatestRequestedInput
                ? _latestRequestedInput
                : _build?.Request ?? _committed?.Request ?? _latestRequestedInput;
            if (!request.Grid.IsValid
                || !FlowFieldGridSpace.IsFinite(request.WorldBounds.center)
                || !FlowFieldGridSpace.IsFinite(request.WorldBounds.size))
                return;

            _obstacles.DetectUnregisteredChanges(in request);
        }

        internal void DetectModifierChanges()
        {
            if (_modifierRegistry.DetectChanges())
                QueueModifierChange();
        }

        internal bool RegisterModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            if (!_modifierRegistry.Register(modifier))
                return false;
            QueueModifierChange();
            return true;
        }

        internal override bool RegisterModifierCommand(IFlowFieldVectorModifier modifier)
            => RegisterModifier(modifier);

        internal bool UnregisterModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            if (!_modifierRegistry.Unregister(modifier))
                return false;
            QueueModifierChange();
            return true;
        }

        internal override bool UnregisterModifierCommand(IFlowFieldVectorModifier modifier)
            => UnregisterModifier(modifier);

        internal override void NotifyModifierChangedCommand(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            ThrowIfInputAvailable();
            if (_modifierRegistry.ValidateModifierChange(modifier, change)
                && change != FlowFieldModifierChange.None)
                QueueModifierChange();
        }

        internal override void Suspend()
        {
            if (!IsInitialized)
                return;
            _suspendedByLifecycle = true;
            CancelCurrentBuild();
            SetState(FlowFieldRuntimeState.Suspended);
        }

        internal void Resume()
        {
            if (State != FlowFieldRuntimeState.Suspended)
                return;
            _suspendedByLifecycle = false;
            SetState(FlowFieldRuntimeState.Building);
        }

        internal override void Release()
        {
            if (!IsInitialized && !IsFaulted)
                return;
            CancelCurrentBuild();
            Fault = null;
            _committed = null;
            _committedReservationBytes = 0;
            _hasLatestRequestedInput = false;
            _suspendedByLifecycle = false;
            GpuFallbackReason = null;
            _safety.ClearRegions();
            if (_runtimeScheduled)
            {
                FlowFieldBuildScheduler.Unregister(this);
                _runtimeScheduled = false;
            }
            _computeSolver?.Dispose();
            _computeSolver = null;
            _obstacles.Clear();
            _registeredObstacleOverlapBuffer = null;
            _cellQuery.Clear();
            ReleaseSpareBuildArrays();
            _modifierRegistry.Clear();
            SetState(FlowFieldRuntimeState.Released);
        }

        internal override void RetryFault()
        {
            if (!IsFaulted)
                return;
            Fault = null;
            // A retry only clears the failure.  The next accepted request
            // must still build and publish before the session becomes Ready;
            // never expose a transient Ready state with no valid result.
            SetState(_suspendedByLifecycle
                ? FlowFieldRuntimeState.Suspended
                : FlowFieldRuntimeState.Building);
        }

        private protected override void AbandonWorkForFault() => CancelCurrentBuild();

        private protected override void ApplyFaultedState() => SetState(FlowFieldRuntimeState.Faulted);

        // A bake session stays on the deterministic Managed path by design, so
        // this records no fallback reason.
        internal void UseManagedBackendForBake()
            => _gpuDisabled = true;

        private void QueueModifierChange()
        {
            // Before the first BuildContext exists, interrupted capture owns
            // the retry. Otherwise the latest input must win over the older
            // active or published input, even after pending work was canceled.
            if (!_hasPendingRequest && _committed == null && _build == null)
                return;
            // Modifiers only affect the final composition. Unobserved colliders
            // wait for the next full build, as Surface2D already does.
            if (!_hasPendingRequest
                && _committed != null
                && _hasLatestRequestedInput
                && _committed.Request.HasSameResult(_latestRequestedInput))
            {
                _hasPendingModifierRecompose = true;
                EnterBuildingUnlessStopped();
                return;
            }
            if (_hasLatestRequestedInput)
            {
                // The latest input was already accepted, and a Goal move within
                // its cell must not make Submit skip this build.
                _hasPendingRequest = true;
                EnterBuildingUnlessStopped();
            }
            else if (_committed != null)
                Submit(_committed.Request);
            else if (_build != null)
                Submit(_build.Request);
        }

        private static Vector3 ObstacleHalfExtents(float cellSize, float clearance)
            => Vector3.one * FlowFieldOverlapUtility.CellHalfExtent(cellSize, clearance);

        private void SetState(FlowFieldRuntimeState state) => PublishState(state);

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
            Disposed = true;
            if (_runtimeScheduled)
            {
                FlowFieldBuildScheduler.Unregister(this);
                _runtimeScheduled = false;
            }
            CancelCurrentBuild();
            _hasLatestRequestedInput = false;
            _committed = null;
            _committedReservationBytes = 0;
            _computeSolver?.Dispose();
            _computeSolver = null;
            _obstacles.Clear();
            _registeredObstacleOverlapBuffer = null;
            _cellQuery.Clear();
            ReleaseSpareBuildArrays();
            _modifierRegistry.Clear();
            _safety.ClearRegions();
            MarkBaseDisposed();
            PublishState(FlowFieldRuntimeState.Released);
        }

        bool IFlowFieldBuildOperation.IsWaitingForExternalCompletion
            => IsWaitingForExternalCompletion;

        bool IFlowFieldBuildOperation.IsBuilding
            => IsBuilding;

        void IFlowFieldBuildOperation.PumpInternal(in FlowFieldBuildBudget budget)
            => PumpInternal(in budget);

        internal void Cancel()
        {
            if (Disposed)
                return;
            CancelCurrentBuild();
        }

    }

}
