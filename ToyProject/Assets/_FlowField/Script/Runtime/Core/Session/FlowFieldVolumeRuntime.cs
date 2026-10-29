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
    internal readonly struct FlowFieldVolumeRequest
    {
        internal FlowFieldGridSpace Grid { get; }
        internal Bounds WorldBounds { get; }
        internal FlowFieldBakeMode BakeMode { get; }
        internal LayerMask ObstacleLayer { get; }
        internal float ObstacleClearance { get; }
        internal bool HasGoal { get; }
        internal Vector3 GoalWorld { get; }
        internal float GoalInfluenceRadius { get; }
        internal Vector3 DefaultDirection { get; }
        internal int MaxGpuWaves { get; }
        internal ComputeShader ComputeShader { get; }
        internal FlowFieldVolumeBakeData StaticBakeData { get; }
        internal int StaticBakeRevision { get; }
        internal int StaticBakeContentGeneration { get; }
        internal int BakeInputGeneration { get; }

        internal FlowFieldVolumeRequest(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            LayerMask obstacleLayer,
            float obstacleClearance,
            bool hasGoal,
            Vector3 goalWorld,
            float goalInfluenceRadius,
            Vector3 defaultDirection,
            int maxGpuWaves,
            ComputeShader computeShader,
            FlowFieldVolumeBakeData staticBakeData,
            int bakeInputGeneration = 0)
        {
            Grid = grid;
            WorldBounds = worldBounds;
            BakeMode = bakeMode;
            ObstacleLayer = obstacleLayer;
            ObstacleClearance = obstacleClearance;
            HasGoal = hasGoal;
            GoalWorld = goalWorld;
            GoalInfluenceRadius = goalInfluenceRadius;
            DefaultDirection = defaultDirection;
            MaxGpuWaves = maxGpuWaves;
            ComputeShader = computeShader;
            StaticBakeData = staticBakeData;
            StaticBakeRevision = staticBakeData != null ? staticBakeData.Revision : -1;
            StaticBakeContentGeneration = staticBakeData != null
                ? staticBakeData.ContentGeneration
                : -1;
            BakeInputGeneration = bakeInputGeneration;
        }

        internal bool HasSameGrid(in FlowFieldVolumeRequest other)
            => Grid.MatchesBounds(other.Grid)
                && Approximately(WorldBounds, other.WorldBounds)
                && BakeMode == other.BakeMode
                && ReferenceEquals(StaticBakeData, other.StaticBakeData)
                && StaticBakeRevision == other.StaticBakeRevision
                && StaticBakeContentGeneration == other.StaticBakeContentGeneration
                && ReferenceEquals(ComputeShader, other.ComputeShader);

        internal bool HasSameInputs(in FlowFieldVolumeRequest other)
        {
            if (!HasSameGrid(other)
                || ObstacleLayer.value != other.ObstacleLayer.value
                || Mathf.Abs(ObstacleClearance - other.ObstacleClearance) > 0.0001f)
                return false;

            if (BakeMode != FlowFieldBakeMode.StaticBaked
                && (HasGoal != other.HasGoal
                    || HasGoal && (!FlowFieldGridSpace.Approximately(
                        GoalWorld,
                        other.GoalWorld,
                        0.00000001d)
                        || Mathf.Abs(GoalInfluenceRadius - other.GoalInfluenceRadius) > 0.0001f)))
                return false;

            return FlowFieldGridSpace.Approximately(
                       DefaultDirection,
                       other.DefaultDirection,
                       0.00000001d)
                && MaxGpuWaves == other.MaxGpuWaves;
        }

        private static bool Approximately(Bounds left, Bounds right)
            => FlowFieldGridSpace.Approximately(left.center, right.center, 0.00000001d)
                && FlowFieldGridSpace.Approximately(left.size, right.size, 0.00000001d);
    }

    internal sealed class FlowFieldVolumeSession : FlowFieldSessionBase, IFlowFieldBuildOperation
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
        private FlowFieldVolumeRequest _latestRequestedInput;
        private bool _hasLatestRequestedInput;
        private FlowFieldRuntimeState _state = FlowFieldRuntimeState.Uninitialized;
        // Initialization uses Suspended as a staging state, but the first
        // accepted request must still be able to start a build. This flag
        // distinguishes that state from an explicit lifecycle suspension.
        private bool _suspendedByLifecycle;
        private FlowFieldBakeMode _bakeMode;
        private Exception _fault;
        private int _revision;
        private int _generation;
        private static long _nextVisualizationResultId;
        private bool _disposed;
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
        internal override FlowFieldBakeMode BakeMode => _bakeMode;
        internal override FlowFieldRuntimeState State => _state;
        internal override bool IsInitialized => _state == FlowFieldRuntimeState.Building
            || _state == FlowFieldRuntimeState.Ready
            || _state == FlowFieldRuntimeState.Suspended;
        internal override bool IsFaulted => _state == FlowFieldRuntimeState.Faulted;
        internal override bool IsRebuilding => _state == FlowFieldRuntimeState.Building;
        internal bool IsWaitingForExternalCompletion
            => _build != null && _build.GpuPending;
        internal bool IsBuilding
            => CanPump && (IsRebuilding || HasPendingRequest);
        internal override bool IsReady => (_state == FlowFieldRuntimeState.Building
                || _state == FlowFieldRuntimeState.Ready)
            && _committed != null
            && !IsFaulted;
        internal override string LastError => _fault?.Message;
        internal override int Revision => _revision;
        internal long TrackedMemoryBytes
        {
            get
            {
                return checked(
                    _buildReservationBytes
                    + _committedReservationBytes
                    + _safety.SnapshotBytes
                    + (_computeSolver?.AllocatedBytes ?? 0L)
                    + EstimateAuxiliaryBytes());
            }
        }
        internal FlowFieldGridSpace Grid => _committed?.Grid ?? _build?.Request.Grid ?? default;
        internal bool HasPendingRequest => _hasPendingRequest || _build != null;
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
                    return _hasPendingRequest ? 0f : 1f;
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
            _bakeMode = bakeMode;
            _fault = null;
            _gpuDisabled = false;
            _hasLatestRequestedInput = false;
            _latestRequestedInput = default;
            _hasPendingRequest = false;
            _safety.Reset();
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
                    _gpuDisabled = !_computeSolver.IsSupported;
                }
                catch
                {
                    _gpuDisabled = true;
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

        internal bool Submit(in FlowFieldVolumeRequest request)
        {
            ThrowIfDisposed();
            if (!IsInitialized)
                throw new InvalidOperationException("FlowField volume session is not initialized.");
            ValidateRequest(request);
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
                // discarded build, its late callback is ignored and the new
                // request falls back to managed BFS until the solver is
                // recreated by the next Init.
                DiscardBuild();
                if (_computeSolver?.IsRunning == true)
                    _gpuDisabled = true;
            }
            if (_committed != null
                && _committed.Request.HasSameGrid(request)
                && (_committed.Request.ObstacleLayer.value != request.ObstacleLayer.value
                    || Mathf.Abs(_committed.Request.ObstacleClearance - request.ObstacleClearance) > 0.0001f))
                _safety.InvalidateRegion(request.WorldBounds);
            _hasPendingRequest = true;
            if (!IsFaulted && (_state != FlowFieldRuntimeState.Suspended || !_suspendedByLifecycle))
                SetState(FlowFieldRuntimeState.Building);
            return true;
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
            int pumpGeneration = _generation;
            BuildContext activeBuild = _build;
            try
            {
                if (!_obstacles.DetectRegisteredChanges()
                    || !CanContinuePump(pumpGeneration, activeBuild))
                    return;

                while (!budget.IsExpired)
                {
                    if (_build == null)
                    {
                        if (!_hasPendingRequest)
                        {
                            if (_committed != null && _state == FlowFieldRuntimeState.Building)
                                SetState(FlowFieldRuntimeState.Ready);
                            return;
                        }

                        FlowFieldVolumeRequest request = _latestRequestedInput;
                        _hasPendingRequest = false;
                        if (!TryCaptureModifiers(pumpGeneration, out FlowFieldModifierWorkItem[] modifierSnapshots))
                            return;
                        long reservation = EstimateBuildBytes(request.Grid.CellCount, modifierSnapshots.Length);
                        if (!CanReserveForBuild(request, reservation))
                            throw new InvalidOperationException(
                                $"FlowField Volume3D tracked memory would exceed the "
                                + $"{MemoryBudgetBytes / (1024d * 1024d):0} MiB manager budget "
                                + $"(required {EstimateRequiredBytes(request, reservation) / (1024d * 1024d):0.0} MiB).");
                        _build = new BuildContext(
                            request,
                            pumpGeneration,
                            _safety.Epoch,
                            modifierSnapshots);
                        _buildReservationBytes = reservation;
                    }

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
                    pumpGeneration = _generation;
                    if (_hasPendingRequest)
                        continue;
                    if (_state == FlowFieldRuntimeState.Building)
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

        private bool CanPump => !_disposed
            && (_state == FlowFieldRuntimeState.Building || _state == FlowFieldRuntimeState.Ready);

        private bool CanContinuePump(int generation, BuildContext build)
            => CanPump && _generation == generation && ReferenceEquals(_build, build);

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
            // QueueCurrentRequest then has no build/publication to fall back
            // to. Retry the newest accepted input at the next owner boundary.
            if (_hasLatestRequestedInput)
                _hasPendingRequest = true;
            return false;
        }

        private void InvalidateBuildGeneration()
        {
            unchecked { _generation++; }
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
        }

        internal override bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsReady || !FlowFieldGridSpace.IsFinite(worldPosition))
                return false;

            VolumeFieldData field = _committed;
            // A newly accepted grid/source invalidates sampling from the
            // previous publication immediately. Ordinary Goal/obstacle
            // rebuilds keep the last same-source result available until the
            // replacement is committed.
            if (_hasLatestRequestedInput
                && !field.Request.HasSameGrid(_latestRequestedInput))
                return false;
            if (!field.Grid.ContainsWorldPosition(worldPosition)
                || !field.Grid.TryWorldToLocal(worldPosition, out int x, out int y, out int z))
                return false;

            int index = field.Grid.ToFlatIndex(x, y, z);
            Vector3 center = field.Grid.LocalToWorldCenter(x, y, z);
            if (_safety.IsLatestCellUnknown(field, index, center))
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                return true;
            }

            if (field.Blocked[index])
            {
                if (_safety.IsEscapePathUnsafe(field, index))
                {
                    sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                    return true;
                }
                Vector3 escape = field.EscapeDirections[index];
                sample = new FlowFieldSample(escape, escape.sqrMagnitude > 0.00000001f ? 1f : 0f, Vector3.zero, false, true);
                return true;
            }

            if (_safety.IsLatestCellBlocked(field, index))
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                return true;
            }

            int next = field.NextCells[index];
            if (next >= 0 && _safety.IsLatestPathUnsafe(field, index, next))
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                return true;
            }
            if (next == -1 && _safety.IsDefaultDirectionPathUnsafe(field, index))
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                return true;
            }

            sample = new FlowFieldSample(
                field.FinalDirections[index],
                field.FinalSpeeds[index],
                Vector3.zero,
                false,
                true);
            return true;
        }

        internal override FlowFieldSample Sample(Vector3 worldPosition)
        {
            if (!TrySample(worldPosition, out FlowFieldSample sample))
                throw new InvalidOperationException("World position is outside the initialized FlowField volume.");
            return sample;
        }

        internal override FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            if (!IsInitialized || !Grid.IsValid)
                throw new InvalidOperationException("FlowField volume session is not initialized.");
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));

            Vector3 clamped = Grid.ClampWorldXYZ(worldPosition);
            return new FlowFieldClampResult(
                clamped,
                !Mathf.Approximately(worldPosition.x, clamped.x),
                !Mathf.Approximately(worldPosition.y, clamped.y),
                !Mathf.Approximately(worldPosition.z, clamped.z));
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
                SetState(FlowFieldRuntimeState.Building);
        }

        internal override void NotifyObstacleRegionDirtyCommand(Bounds worldBounds)
            => MarkObstacleRegionDirty(worldBounds);

        internal void DetectUnregisteredObstacleChanges(bool enabled)
        {
            if (!enabled
                || !IsInitialized
                || IsFaulted
                || _bakeMode == FlowFieldBakeMode.StaticBaked)
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
                QueueCurrentRequest();
        }

        internal bool RegisterModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            if (!_modifierRegistry.Register(modifier))
                return false;
            QueueCurrentRequest();
            return true;
        }

        internal override bool RegisterModifierCommand(IFlowFieldVectorModifier modifier)
            => RegisterModifier(modifier);

        internal bool UnregisterModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            if (!_modifierRegistry.Unregister(modifier))
                return false;
            QueueCurrentRequest();
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
                QueueCurrentRequest();
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
            if (_state != FlowFieldRuntimeState.Suspended)
                return;
            _suspendedByLifecycle = false;
            SetState(FlowFieldRuntimeState.Building);
        }

        internal override void Release()
        {
            if (!IsInitialized && !IsFaulted)
                return;
            CancelCurrentBuild();
            _committed = null;
            _committedReservationBytes = 0;
            _safety.ClearSnapshot();
            _hasLatestRequestedInput = false;
            _suspendedByLifecycle = false;
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
            _fault = null;
            // A retry only clears the failure.  The next accepted request
            // must still build and publish before the session becomes Ready;
            // never expose a transient Ready state with no valid result.
            SetState(FlowFieldRuntimeState.Building);
        }

        internal override void ReportFault(Exception exception)
        {
            if (!IsInitialized || IsFaulted)
                return;
            CancelCurrentBuild();
            Exception failure = exception ?? new InvalidOperationException("FlowField volume session failed.");
            _fault = failure;
            int generation = _generation;
            SetState(FlowFieldRuntimeState.Faulted);
            if (!_disposed && _generation == generation)
                NotifyFailed(failure);
        }

        internal void ForceManagedBackend()
            => _gpuDisabled = true;

        internal bool TryExport(
            out FlowFieldVolumeRequest request,
            out bool[] blocked,
            out uint[] topology,
            out FlowFieldGoalFlags[] goalFlags,
            out int[] next,
            out Vector3[] directions,
            out float[] speeds,
            out Vector3[] escapeDirections)
        {
            return TryExport(
                out request,
                out blocked,
                out topology,
                out goalFlags,
                out next,
                out directions,
                out speeds,
                out escapeDirections,
                out _);
        }

        internal bool TryExport(
            out FlowFieldVolumeRequest request,
            out bool[] blocked,
            out uint[] topology,
            out FlowFieldGoalFlags[] goalFlags,
            out int[] next,
            out Vector3[] directions,
            out float[] speeds,
            out Vector3[] escapeDirections,
            out int resolvedGoalIndex)
        {
            request = default;
            blocked = null;
            topology = null;
            goalFlags = null;
            next = null;
            directions = null;
            speeds = null;
            escapeDirections = null;
            resolvedGoalIndex = -1;
            if (_committed == null)
                return false;
            request = _committed.Request;
            blocked = (bool[])_committed.Blocked.Clone();
            topology = (uint[])_committed.Topology.Clone();
            goalFlags = (FlowFieldGoalFlags[])_committed.GoalFlags.Clone();
            next = (int[])_committed.NextCells.Clone();
            // Static bake assets store the immutable base field. The current
            // Default Direction and registered Modifiers are runtime inputs
            // and are composed again when the snapshot is loaded.
            directions = (Vector3[])_committed.BaseDirections.Clone();
            speeds = (float[])_committed.BaseSpeeds.Clone();
            escapeDirections = (Vector3[])_committed.EscapeDirections.Clone();
            resolvedGoalIndex = _committed.GoalIndex;
            return true;
        }

        internal bool TryGetView(
            out FlowFieldGridSpace grid,
            out bool[] blocked,
            out Vector3[] directions,
            out float[] speeds)
        {
            grid = default;
            blocked = null;
            directions = null;
            speeds = null;
            if (_committed == null)
                return false;
            grid = _committed.Grid;
            blocked = _committed.Blocked;
            directions = _committed.FinalDirections;
            speeds = _committed.FinalSpeeds;
            return true;
        }

        internal override bool TryGetFieldInfo(out FlowFieldFieldInfo info)
        {
            info = default;
            if (_committed == null
                || _state == FlowFieldRuntimeState.Uninitialized
                || _state == FlowFieldRuntimeState.Released
                || _state == FlowFieldRuntimeState.Suspended
                || _state == FlowFieldRuntimeState.Faulted)
                return false;

            if (_hasLatestRequestedInput
                && !_committed.Request.HasSameGrid(_latestRequestedInput))
                return false;

            FlowFieldVolumeRequest request = _committed.Request;
            Vector3 resolvedGoal = default;
            bool hasResolvedGoal = _committed.GoalIndex >= 0
                && _committed.GoalIndex < _committed.Grid.CellCount;
            if (hasResolvedGoal)
            {
                _committed.Grid.FromFlatIndex(
                    _committed.GoalIndex,
                    out int goalX,
                    out int goalY,
                    out int goalZ);
                resolvedGoal = _committed.Grid.LocalToWorldCenter(goalX, goalY, goalZ);
            }

            info = new FlowFieldFieldInfo(
                request.Grid,
                request.WorldBounds,
                FlowFieldSpaceMode.Volume3D,
                request.BakeMode,
                _revision,
                request.HasGoal,
                request.GoalWorld,
                request.GoalInfluenceRadius,
                hasResolvedGoal,
                resolvedGoal);
            return info.IsValid;
        }

        internal bool TryGetVisualizationView(out FlowFieldReadView view)
        {
            view = default;
            if (_committed == null
                || _state == FlowFieldRuntimeState.Uninitialized
                || _state == FlowFieldRuntimeState.Released
                || _state == FlowFieldRuntimeState.Suspended
                || _state == FlowFieldRuntimeState.Faulted)
                return false;

            VolumeFieldData field = _committed;
            if (_hasLatestRequestedInput
                && !field.Request.HasSameGrid(_latestRequestedInput))
                return false;
            FlowFieldVolumeVisualizationSource source = new FlowFieldVolumeVisualizationSource(
                field.Grid,
                field.Request.WorldBounds,
                field.Request.BakeMode,
                field.Request.StaticBakeData,
                field.Request.StaticBakeRevision,
                field.ResultId);
            view = FlowFieldReadView.CreateVolume(
                source,
                field.Blocked,
                field.FinalDirections,
                field.FinalSpeeds);
            return view.IsValid;
        }

        internal bool TryGetVisualizationSource(
            out FlowFieldVolumeVisualizationSource source)
        {
            source = default;
            if (_state == FlowFieldRuntimeState.Uninitialized
                || _state == FlowFieldRuntimeState.Released
                || _state == FlowFieldRuntimeState.Suspended
                || _state == FlowFieldRuntimeState.Faulted)
                return false;

            if (_hasLatestRequestedInput)
            {
                source = new FlowFieldVolumeVisualizationSource(
                    _latestRequestedInput.Grid,
                    _latestRequestedInput.WorldBounds,
                    _latestRequestedInput.BakeMode,
                    _latestRequestedInput.StaticBakeData,
                    _latestRequestedInput.StaticBakeRevision,
                    _committed != null ? _committed.ResultId : 0L);
                return source.IsValid;
            }

            if (_committed == null)
                return false;
            source = new FlowFieldVolumeVisualizationSource(
                _committed.Grid,
                _committed.Request.WorldBounds,
                _committed.Request.BakeMode,
                _committed.Request.StaticBakeData,
                _committed.Request.StaticBakeRevision,
                _committed.ResultId);
            return source.IsValid;
        }

        private bool StepBuild(BuildContext build, in FlowFieldBuildBudget budget)
        {
            while (!budget.IsExpired)
            {
                EnsureCurrentBuild(build);
                switch (build.Phase)
                {
                    case BuildPhase.Allocate:
                        if (StepAllocate(build))
                        {
                            build.Cursor = 0;
                            build.Phase = build.Request.StaticBakeData != null
                                ? BuildPhase.StaticData
                                : BuildPhase.Obstacles;
                        }
                        break;
                    case BuildPhase.StaticData:
                        if (StepStaticData(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Modifiers;
                        }
                        break;
                    case BuildPhase.Obstacles:
                        if (StepObstacles(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Topology;
                        }
                        break;
                    case BuildPhase.Topology:
                        if (StepTopology(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Goal;
                        }
                        break;
                    case BuildPhase.Goal:
                        if (StepGoal(build))
                        {
                            build.Cursor = 0;
                            build.Phase = build.Request.HasGoal && build.GoalIndex >= 0
                                ? BuildPhase.Bfs
                                : BuildPhase.Directions;
                        }
                        break;
                    case BuildPhase.Bfs:
                        if (StepBfs(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Directions;
                        }
                        break;
                    case BuildPhase.Directions:
                        if (StepDirections(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Escape;
                        }
                        break;
                    case BuildPhase.Escape:
                        if (StepEscape(build))
                        {
                            build.Cursor = 0;
                            build.Phase = BuildPhase.Modifiers;
                        }
                        break;
                    case BuildPhase.Modifiers:
                        if (StepModifiers(build))
                            build.Phase = BuildPhase.Complete;
                        break;
                    case BuildPhase.Complete:
                        return true;
                }

                EnsureCurrentBuild(build);
            }
            return false;
        }

        private bool StepStaticData(BuildContext build)
        {
            FlowFieldVolumeBakeData data = build.Request.StaticBakeData;
            int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
            for (; build.Cursor < end; build.Cursor++)
            {
                data.CopyCellToBuild(
                    build.Cursor,
                    build.Blocked,
                    build.Topology,
                    build.GoalFlags,
                    build.Next,
                    build.Directions,
                    build.Speeds,
                    build.EscapeDirections);
            }
            if (build.Cursor < build.Count)
                return false;
            build.GoalIndex = data.ResolvedGoalIndex;
            return true;
        }

        private bool StepAllocate(BuildContext build)
        {
            // Keep large managed allocations as individually observable work
            // units. A build can therefore yield between arrays instead of
            // constructing the entire Volume3D staging graph in one frame.
            switch (build.AllocationStage)
            {
                case 0: build.Blocked = new bool[build.Count]; break;
                case 1: build.Topology = new uint[build.Count]; break;
                case 2: build.GoalFlags = new FlowFieldGoalFlags[build.Count]; break;
                case 3: build.Next = new int[build.Count]; break;
                case 4: build.Directions = new Vector3[build.Count]; break;
                case 5: build.Speeds = new float[build.Count]; break;
                case 6: build.FinalDirections = new Vector3[build.Count]; break;
                case 7: build.FinalSpeeds = new float[build.Count]; break;
                case 8: build.EscapeDirections = new Vector3[build.Count]; break;
                case 9: build.Costs = RentBuildArray(ref _spareCosts, build.Count); break;
                case 10: build.Queue = RentBuildArray(ref _spareQueue, build.Count); break;
                case 11: build.Influence = RentBuildArray(ref _spareInfluence, build.Count); break;
                case 12:
                    int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
                    for (; build.Cursor < end; build.Cursor++)
                    {
                        build.Next[build.Cursor] = -1;
                        build.Costs[build.Cursor] = Unreachable;
                    }
                    if (build.Cursor < build.Count)
                        return false;
                    build.Cursor = 0;
                    build.AllocationStage++;
                    return true;
            }

            build.AllocationStage++;
            return false;
        }

        private bool StepObstacles(BuildContext build)
        {
            if (build.Cursor == 0)
                Physics.SyncTransforms();
            int end = Math.Min(build.Cursor + CellsPerStep, build.Count);
            FlowFieldGridSpace grid = build.Request.Grid;
            Vector3 half = ObstacleHalfExtents(grid.CellSize, build.Request.ObstacleClearance);
            _cellQuery.BeginObstacleStep(
                FlowFieldVolumeCellQuery.StepBounds(grid, build.Cursor, end, half),
                build.Request.ObstacleLayer,
                _obstacles);
            for (; build.Cursor < end; build.Cursor++)
            {
                int index = build.Cursor;
                grid.FromFlatIndex(index, out int x, out int y, out int z);
                Vector3 center = grid.LocalToWorldCenter(x, y, z);
                var cellBox = new Bounds(center, half * 2f);
                build.Blocked[index] = _cellQuery.MayHitLayer(cellBox)
                    && Physics.CheckBox(
                        center,
                        half,
                        Quaternion.identity,
                        build.Request.ObstacleLayer,
                        QueryTriggerInteraction.Ignore);
                if (!build.Blocked[index])
                {
                    build.Blocked[index] = _cellQuery.IntersectsRegistered(
                        center,
                        half,
                        cellBox,
                        ref _registeredObstacleOverlapBuffer);
                }
            }
            return build.Cursor >= build.Count;
        }

        private bool StepBfs(BuildContext build)
        {
            if (!build.GpuAttempted)
            {
                build.GpuAttempted = true;
                if (TryStartGpuBfs(build))
                {
                    // AsyncGPUReadback owns the continuation. Do not spin the
                    // cooperative scheduler while the request is in flight.
                    return false;
                }
            }

            if (build.GpuPending)
                return false;

            if (build.GpuResultPending)
            {
                if (!StepGpuResult(build))
                    return false;
                if (build.Phase != BuildPhase.Bfs)
                    return false;
            }

            if (build.GpuFailed != null
                && !StepGpuFallbackReset(build))
                return false;

            return FlowFieldVolumeNavigation.StepManagedBfs(build);
        }

        private bool TryStartGpuBfs(BuildContext build)
        {
            if (_gpuDisabled || _computeSolver == null || !_computeSolver.IsSupported
                || build.Request.HasGoal == false || build.GoalIndex < 0)
                return false;

            long currentGpuBytes = _computeSolver.AllocatedBytes;
            long requestedGpuBytes = FlowFieldComputeSolver.EstimateAllocationBytes(
                build.Count,
                volume: true);
            long additionalGpuBytes = Math.Max(0L, requestedGpuBytes - currentGpuBytes);
            if (checked(TrackedMemoryBytes + additionalGpuBytes) > MemoryBudgetBytes)
            {
                // A buffer-size change is a backend choice, not a failed
                // field request. Continue with the managed solver under the
                // same staged build and input snapshot.
                _gpuDisabled = true;
                return false;
            }

            FlowFieldComputeRequest request = new FlowFieldComputeRequest(
                build.Request.Grid,
                build.Blocked,
                build.Influence,
                build.Topology,
                build.GoalIndex,
                build.Request.MaxGpuWaves);
            build.GpuPending = true;
            bool accepted;
            try
            {
                accepted = _computeSolver.Start(
                    request,
                    (completedRequest, result) => OnGpuCompleted(build, completedRequest, result),
                    (failedRequest, kind, exception) => OnGpuFailed(build, failedRequest, kind, exception));
            }
            catch
            {
                build.GpuPending = false;
                _gpuDisabled = true;
                return false;
            }

            if (!accepted)
            {
                build.GpuPending = false;
                _gpuDisabled = true;
                return false;
            }
            return true;
        }

        private void OnGpuCompleted(
            BuildContext build,
            FlowFieldComputeRequest request,
            Unity.Collections.NativeArray<GpuFlowCell> result)
        {
            if (_disposed || _build != build || build.Generation != _generation)
                return;
            // Keep the callback bounded: the persistent readback storage is
            // owned by FlowFieldComputeSolver until the next dispatch, while
            // validation and CPU fallback are resumed by the scheduler.
            build.GpuResult = result;
            build.GpuPending = false;
            build.GpuResultPending = true;
            build.GpuValidationCursor = 0;
        }

        private void OnGpuFailed(
            BuildContext build,
            FlowFieldComputeRequest request,
            FlowFieldComputeFailureKind kind,
            Exception exception)
        {
            if (_disposed || _build != build || build.Generation != _generation)
                return;
            _gpuDisabled = true;
            build.GpuPending = false;
            build.GpuFailed = exception
                ?? new InvalidOperationException($"Volume3D GPU BFS failed ({kind}).");
            build.GpuFallbackResetCursor = 0;
        }

        private bool StepGpuResult(BuildContext build)
        {
            if (!build.GpuResult.IsCreated || build.GpuResult.Length != build.Count)
            {
                HandleGpuValidationFailure(
                    build,
                    new InvalidOperationException("Volume3D GPU result length does not match the active grid."));
                return true;
            }

            int end = Math.Min(
                build.GpuValidationCursor + ArrayElementsPerStep,
                build.Count);
            try
            {
                FlowFieldVolumeGpuResultValidator.Apply(
                    build,
                    build.GpuResult,
                    build.GpuValidationCursor,
                    end);
            }
            catch (Exception exception)
            {
                HandleGpuValidationFailure(build, exception);
                return true;
            }

            build.GpuValidationCursor = end;
            if (build.GpuValidationCursor < build.Count)
                return false;

            build.GpuResultPending = false;
            build.GpuValidationCursor = 0;
            build.Cursor = 0;
            build.Phase = BuildPhase.Escape;
            return true;
        }

        private void HandleGpuValidationFailure(BuildContext build, Exception exception)
        {
            _gpuDisabled = true;
            build.GpuResultPending = false;
            build.GpuFailed = exception
                ?? new InvalidOperationException("Volume3D GPU result validation failed.");
            build.GpuFallbackResetCursor = 0;
            build.Cursor = 0;
        }

        private bool StepModifiers(BuildContext build)
        {
            if (build.Cursor == 0 && build.Modifiers.Length > 0)
                Physics.SyncTransforms();

            int end = Math.Min(build.Cursor + CellsPerStep, build.Count);
            Vector3 cellSize = Vector3.one * build.Request.Grid.CellSize;
            if (build.Modifiers.Length > 0)
            {
                _cellQuery.BeginModifierStep(
                    FlowFieldVolumeCellQuery.StepBounds(build.Request.Grid, build.Cursor, end, cellSize * 0.5f),
                    build.Modifiers);
            }
            for (; build.Cursor < end; build.Cursor++)
            {
                int index = build.Cursor;
                if (!FlowFieldVectorUtility.CanApplyModifier(
                        build.Blocked[index],
                        build.GoalFlags[index],
                        build.Directions[index]))
                {
                    build.FinalDirections[index] = build.Directions[index];
                    build.FinalSpeeds[index] = build.Speeds[index];
                    continue;
                }

                build.FinalDirections[index] = build.Directions[index];
                build.FinalSpeeds[index] = build.Speeds[index];
                build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                Vector3 center = CellCenter(build.Request.Grid, index);
                FlowFieldVectorState current = new FlowFieldVectorState(
                    build.FinalDirections[index],
                    build.FinalSpeeds[index]);
                for (int modifierIndex = 0; modifierIndex < build.Modifiers.Length; modifierIndex++)
                {
                    if (!_cellQuery.MayTouchModifier(modifierIndex, new Bounds(center, cellSize)))
                        continue;
                    FlowFieldModifierWorkItem modifier = build.Modifiers[modifierIndex];
                    Collider collider = modifier.InfluenceCollider;
                    if (!FlowFieldModifierMaskBuilder.IsUsableTrigger(collider))
                        continue;
                    int layerMask = 1 << collider.gameObject.layer;
                    if (!FlowFieldOverlapUtility.OverlapsTarget(
                            center,
                            cellSize * 0.5f,
                            layerMask,
                            collider,
                            QueryTriggerInteraction.Collide,
                            ref _registeredObstacleOverlapBuffer))
                        continue;
                    FlowFieldVectorModifierContext context = new FlowFieldVectorModifierContext(
                        index,
                        x,
                        y,
                        z,
                        center,
                        Vector3.zero,
                        build.Request.Grid,
                        (build.GoalFlags[index] & FlowFieldGoalFlags.Directed) != 0);
                    FlowFieldVectorState candidate = modifier.Snapshot.Modify(in current, in context);
                    EnsureCurrentBuild(build);
                    FlowFieldVectorUtility.ValidateVolumeModifierOutput(candidate);
                    current = candidate;
                }
                build.FinalDirections[index] = current.Direction;
                build.FinalSpeeds[index] = current.SpeedMultiplier;
            }
            return build.Cursor >= build.Count;
        }

        private bool CommitBuild(BuildContext build)
        {
            if (build == null || !CanContinuePump(build.Generation, build))
                return false;

            VolumeFieldData next = new VolumeFieldData(
                build.Request,
                build.Blocked,
                build.Topology,
                build.GoalFlags,
                build.Next,
                build.Directions,
                build.Speeds,
                build.FinalDirections,
                build.FinalSpeeds,
                build.EscapeDirections,
                build.GoalIndex,
                Interlocked.Increment(ref _nextVisualizationResultId));

            bool gridChanged = _committed != null && !_committed.Request.HasSameGrid(build.Request);
            bool safetyChangedDuringBuild = build.SafetyEpoch != _safety.Epoch;
            // Allocate and populate the replacement safety snapshot before
            // publishing any field reference. If allocation or initialization
            // fails, the previous publication remains intact.
            FlowFieldVolumeSafetyState.Snapshot nextSafety = _safety.PrepareSnapshot(
                build.Request.Grid, build.Blocked, build.SafetyEpoch);

            _committed = next;
            _revision++;
            _committedReservationBytes = EstimateCommittedBytes(build.Count);
            _safety.PublishSnapshot(in nextSafety);
            if (!_hasPendingRequest && !safetyChangedDuringBuild)
                _safety.ClearRegions();
            _build = null;
            _buildReservationBytes = 0;
            // The published field holds none of these; a completed build has
            // no GPU readback left that could still read them.
            _spareCosts = build.Costs;
            _spareQueue = build.Queue;
            _spareInfluence = build.Influence;
            if (gridChanged)
                InvalidateBuildGeneration();

            // Publish only after every private reference, revision and
            // lifecycle field has been made internally consistent. A
            // listener may Release, Suspend or submit another request.
            int callbackGeneration = _generation;
            NotifyFieldCommitted();
            return _generation != callbackGeneration;
        }

        private void QueueCurrentRequest()
        {
            // Before the first BuildContext exists, interrupted capture owns
            // the retry. Otherwise the latest input must win over the older
            // active or published input, even after pending work was canceled.
            if (!_hasPendingRequest && _committed == null && _build == null)
                return;
            if (_hasLatestRequestedInput)
                Submit(_latestRequestedInput);
            else if (_committed != null)
                Submit(_committed.Request);
            else if (_build != null)
                Submit(_build.Request);
        }

        private static Vector3 ObstacleHalfExtents(float cellSize, float clearance)
        {
            // Unity's overlap queries count a face-touching collider as an
            // overlap. Removing a tiny numerical skin preserves the intended
            // cell ownership when clearance is zero while retaining the
            // configured clearance for actual intersections.
            float half = Mathf.Max(0.000001f, cellSize * 0.5f + clearance - 0.0001f);
            return Vector3.one * half;
        }

        private void SetState(FlowFieldRuntimeState state)
        {
            if (_state == state)
                return;
            _state = state;
            NotifyStateChanged(state);
        }

        private void ThrowIfInputAvailable()
        {
            ThrowIfDisposed();
            if (!IsInitialized || IsFaulted)
                throw new InvalidOperationException("FlowField volume session is unavailable.", _fault);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
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

        private bool CanReserveForBuild(
            in FlowFieldVolumeRequest request,
            long buildReservation)
            => EstimateRequiredBytes(request, buildReservation) <= MemoryBudgetBytes;

        private long EstimateRequiredBytes(
            in FlowFieldVolumeRequest request,
            long replacementBuildReservation)
        {
            long current = TrackedMemoryBytes;
            if (_buildReservationBytes > 0)
                current -= _buildReservationBytes;

            long currentGpu = _computeSolver?.AllocatedBytes ?? 0L;
            long possibleGpu = currentGpu;
            if (!_gpuDisabled && _computeSolver != null && _computeSolver.IsSupported)
            {
                long candidateGpu = FlowFieldComputeSolver.EstimateAllocationBytes(
                    request.Grid.CellCount,
                    volume: true);
                possibleGpu = Math.Max(possibleGpu, candidateGpu);
            }

            long replacementSafetyReservation = FlowFieldVolumeSafetyState.EstimateSnapshotBytes(request.Grid.CellCount);
            return checked(
                current
                + replacementBuildReservation
                + replacementSafetyReservation
                + (possibleGpu - currentGpu));
        }

        private static long EstimateBuildBytes(int count, int modifierCount)
        {
            if (count <= 0)
                return 0;
            // The build owns twelve per-cell arrays: topology/state/output,
            // BFS costs/queue and final composition buffers. Reserve the
            // measured logical element footprint rounded up to 72 bytes per
            // cell, plus array headers and the captured modifier references.
            // The small safety margin prevents a managed-array type change
            // from turning the budget into an underestimate.
            return checked(
                72L * count
                + 32L * 13L
                + 32L
                + 8L * Math.Max(0, modifierCount));
        }

        private static long EstimateCommittedBytes(int count)
        {
            if (count <= 0)
                return 0;
            // VolumeFieldData owns nine per-cell arrays after commit. Reserve
            // the logical element footprint rounded up to 60 bytes per cell.
            return checked(60L * count + 32L * 9L + 32L);
        }

        private long EstimateAuxiliaryBytes()
        {
            long bytes = 64L;
            bytes = checked(bytes + _obstacles.SweepBufferBytes);
            bytes = checked(bytes + _cellQuery.BufferBytes);
            bytes = checked(bytes
                + 4L * (_spareCosts?.Length ?? 0)
                + 4L * (_spareQueue?.Length ?? 0)
                + (_spareInfluence?.Length ?? 0));
            if (_registeredObstacleOverlapBuffer != null)
                bytes = checked(bytes + 8L * _registeredObstacleOverlapBuffer.Length);
            bytes = checked(bytes + 8L * _modifierRegistry.Count);
            bytes = checked(bytes + 32L * _safety.UncertainRegionCount);
            return bytes;
        }

        public override void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_runtimeScheduled)
            {
                FlowFieldBuildScheduler.Unregister(this);
                _runtimeScheduled = false;
            }
            CancelCurrentBuild();
            _hasLatestRequestedInput = false;
            _committed = null;
            _committedReservationBytes = 0;
            _safety.ClearSnapshot();
            _computeSolver?.Dispose();
            _computeSolver = null;
            _obstacles.Clear();
            _registeredObstacleOverlapBuffer = null;
            _cellQuery.Clear();
            ReleaseSpareBuildArrays();
            _modifierRegistry.Clear();
            _safety.ClearRegions();
            _state = FlowFieldRuntimeState.Released;
            MarkBaseDisposed();
        }

        bool IFlowFieldBuildOperation.IsWaitingForExternalCompletion
            => IsWaitingForExternalCompletion;

        bool IFlowFieldBuildOperation.IsBuilding
            => IsBuilding;

        void IFlowFieldBuildOperation.PumpInternal(in FlowFieldBuildBudget budget)
            => PumpInternal(in budget);

        internal void Cancel()
        {
            if (_disposed)
                return;
            CancelCurrentBuild();
        }

    }

}
