using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using UnityEngine;

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
        private const int CellsPerStep = 64;
        private const int ArrayElementsPerStep = 4096;
        private const double DefaultBudgetMilliseconds = 2.0;
        private const long MemoryBudgetBytes = 512L * 1024L * 1024L;
        private const int Unreachable = int.MaxValue;
        private const int MaxUncertainBounds = 64;
        private const int MaxObstacleSweepColliders = 65536;

        private readonly List<Collider> _dynamicObstacles = new List<Collider>(16);
        // Unity overloads Collider equality after destruction. Keep the last
        // bounds beside the reference so a destroyed registered collider can
        // still invalidate its former cells.
        private readonly List<Bounds> _dynamicObstacleBounds = new List<Bounds>(16);
        private readonly Dictionary<Collider, Bounds> _dynamicBounds = new Dictionary<Collider, Bounds>();
        private readonly Dictionary<Collider, Bounds> _observedObstacleBounds = new Dictionary<Collider, Bounds>(64);
        private readonly Dictionary<Collider, ObstacleObservation> _observedObstacleStates
            = new Dictionary<Collider, ObstacleObservation>(64);
        private readonly HashSet<Collider> _seenObstacleColliders = new HashSet<Collider>();
        private Collider[] _obstacleSweepBuffer = new Collider[512];
        private Collider[] _registeredObstacleOverlapBuffer;
        private readonly List<IFlowFieldVectorModifier> _modifiers = new List<IFlowFieldVectorModifier>(16);
        private readonly Dictionary<IFlowFieldVectorModifier, ModifierObservation> _modifierObservations
            = new Dictionary<IFlowFieldVectorModifier, ModifierObservation>(16);
        private readonly List<Bounds> _uncertainBounds = new List<Bounds>(8);
        private SafetyCellState[] _safetyStates;
        private int _safetyEpoch;

        private FlowFieldComputeSolver _computeSolver;
        private bool _gpuDisabled;
        private VolumeFieldData _committed;
        private BuildContext _build;
        private FlowFieldVolumeRequest _pendingRequest;
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
        private long _registrationOrder;
        private bool _disposed;
        private bool _runtimeScheduled;
        private long _buildReservationBytes;
        private long _committedReservationBytes;
        private long _safetyReservationBytes;

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
            => IsRebuilding || HasPendingRequest;
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
                    + _safetyReservationBytes
                    + (_computeSolver?.AllocatedBytes ?? 0L)
                    + EstimateAuxiliaryBytes());
            }
        }
        internal long MemoryBudget => MemoryBudgetBytes;
        internal FlowFieldGridSpace Grid => _committed?.Grid ?? _build?.Request.Grid ?? default;
        internal bool HasPendingRequest => _hasPendingRequest || _build != null;
        internal bool TryGetLatestRequestedInput(out FlowFieldVolumeRequest request)
        {
            request = _latestRequestedInput;
            return _hasLatestRequestedInput;
        }

        internal bool ContainsModifier(IFlowFieldVectorModifier modifier)
            => modifier != null && _modifiers.Contains(modifier);

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
            _pendingRequest = default;
            _hasPendingRequest = false;
            _safetyStates = null;
            _safetyEpoch = 0;
            _uncertainBounds.Clear();
            _suspendedByLifecycle = false;
            _buildReservationBytes = 0;
            _committedReservationBytes = 0;
            _safetyReservationBytes = 0;
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
            _generation++;
            InvalidateBaseLifetime();
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
                _generation++;
                // A grid/signature change invalidates the private staging
                // arrays immediately. If an async GPU solve belongs to the
                // discarded build, its late callback is ignored and the new
                // request falls back to managed BFS until the solver is
                // recreated by the next Init.
                _build.Dispose();
                _build = null;
                _buildReservationBytes = 0;
                if (_computeSolver?.IsRunning == true)
                    _gpuDisabled = true;
            }
            if (_committed != null
                && _committed.Request.HasSameGrid(request)
                && (_committed.Request.ObstacleLayer.value != request.ObstacleLayer.value
                    || Mathf.Abs(_committed.Request.ObstacleClearance - request.ObstacleClearance) > 0.0001f))
                _uncertainBounds.Add(request.WorldBounds);
            _pendingRequest = request;
            _hasPendingRequest = true;
            if (!IsFaulted && (_state != FlowFieldRuntimeState.Suspended || !_suspendedByLifecycle))
                SetState(FlowFieldRuntimeState.Building);
            return true;
        }

        internal void Pump(double budgetMilliseconds = DefaultBudgetMilliseconds)
            => PumpInternal(budgetMilliseconds);

        internal void PumpInternal(double budgetMilliseconds = DefaultBudgetMilliseconds)
        {
            if (budgetMilliseconds <= 0d)
                budgetMilliseconds = DefaultBudgetMilliseconds;
            Stopwatch timer = Stopwatch.StartNew();
            FlowFieldBuildBudget budget = new FlowFieldBuildBudget(
                budgetMilliseconds,
                () => timer.Elapsed.TotalMilliseconds);
            PumpInternal(in budget);
        }

        private void PumpInternal(in FlowFieldBuildBudget budget)
        {
            if (_disposed || IsFaulted || _state == FlowFieldRuntimeState.Suspended
                || _state == FlowFieldRuntimeState.Released)
                return;

            DetectDynamicObstacleChanges();
            try
            {
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

                        FlowFieldVolumeRequest request = _pendingRequest;
                        _hasPendingRequest = false;
                        FlowFieldModifierWorkItem[] modifierSnapshots = CaptureModifierSnapshots();
                        long reservation = EstimateBuildBytes(request.Grid.CellCount, modifierSnapshots.Length);
                        if (!CanReserveForBuild(request, reservation))
                            throw new InvalidOperationException(
                                $"FlowField Volume3D tracked memory would exceed the "
                                + $"{MemoryBudgetBytes / (1024d * 1024d):0} MiB manager budget "
                                + $"(required {EstimateRequiredBytes(request, reservation) / (1024d * 1024d):0.0} MiB).");
                        _build = new BuildContext(
                            request,
                            _generation,
                            _safetyEpoch,
                            modifierSnapshots);
                        _buildReservationBytes = reservation;
                    }

                    if (!StepBuild(_build, budget))
                        return;

                    BuildContext completedBuild = _build;
                    bool commitCallbackChangedGeneration = CommitBuild(completedBuild);
                    if (ReferenceEquals(_build, completedBuild))
                    {
                        completedBuild.Dispose();
                        _build = null;
                    }
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
                    if (_disposed
                        || _state == FlowFieldRuntimeState.Released
                        || _state == FlowFieldRuntimeState.Suspended
                        || _state == FlowFieldRuntimeState.Faulted
                        || !IsInitialized)
                        return;
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
                _build?.Dispose();
                _build = null;
                _buildReservationBytes = 0;
                if (_hasPendingRequest)
                    SetState(FlowFieldRuntimeState.Building);
            }
            catch (Exception exception)
            {
                _build?.Dispose();
                _build = null;
                _buildReservationBytes = 0;
                _hasPendingRequest = false;
                _fault = exception;
                SetState(FlowFieldRuntimeState.Faulted);
            NotifyFailed(exception);
            }
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
            if (IsLatestCellUnknown(field, index, center))
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                return true;
            }

            if (field.Blocked[index])
            {
                if (IsEscapePathUnsafe(field, index))
                {
                    sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                    return true;
                }
                Vector3 escape = field.EscapeDirections[index];
                sample = new FlowFieldSample(escape, escape.sqrMagnitude > 0.00000001f ? 1f : 0f, Vector3.zero, false, true);
                return true;
            }

            if (IsLatestCellBlocked(field, index, center))
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                return true;
            }

            int next = field.NextCells[index];
            if (next >= 0 && IsLatestPathUnsafe(field, index, next))
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                return true;
            }
            if (next == -1 && IsDefaultDirectionPathUnsafe(field, index))
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
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (collider == null)
                throw new InvalidOperationException("Cannot register a destroyed Collider.");
            if (FindDynamicObstacleIndex(collider) >= 0)
                return false;
            Bounds current = collider.bounds;
            _observedObstacleBounds.Remove(collider);
            _observedObstacleStates.Remove(collider);
            _dynamicObstacles.Add(collider);
            _dynamicObstacleBounds.Add(current);
            _dynamicBounds[collider] = current;
            MarkObstacleRegionDirty(current);
            return true;
        }

        internal override bool RegisterObstacleCommand(Collider collider)
            => RegisterDynamicObstacle(collider);

        internal bool UnregisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAvailable();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            int obstacleIndex = FindDynamicObstacleIndex(collider);
            if (obstacleIndex < 0)
                return false;
            Bounds previous = _dynamicObstacleBounds[obstacleIndex];
            _dynamicBounds.Remove(collider);
            _observedObstacleBounds.Remove(collider);
            _observedObstacleStates.Remove(collider);
            _dynamicObstacles.RemoveAt(obstacleIndex);
            _dynamicObstacleBounds.RemoveAt(obstacleIndex);
            if (FlowFieldGridSpace.IsFinite(previous.center)
                && FlowFieldGridSpace.IsFinite(previous.size))
                MarkObstacleRegionDirty(previous);
            return true;
        }

        internal override bool UnregisterObstacleCommand(Collider collider)
            => UnregisterDynamicObstacle(collider);

        internal void MarkObstacleRegionDirty(Bounds bounds)
        {
            if (!FlowFieldGridSpace.IsFinite(bounds.center) || !FlowFieldGridSpace.IsFinite(bounds.size))
                throw new ArgumentOutOfRangeException(nameof(bounds));
            Bounds expanded = bounds;
            FlowFieldVolumeRequest request = _hasLatestRequestedInput
                ? _latestRequestedInput
                : _pendingRequest;
            float margin = request.Grid.IsValid
                ? request.Grid.CellSize + request.ObstacleClearance
                : 1f;
            expanded.Expand(Vector3.one * (margin * 2f));
            _safetyEpoch++;
            AddUncertainBounds(expanded);
            if (_hasPendingRequest)
                return;
            if (_hasLatestRequestedInput)
                _pendingRequest = _latestRequestedInput;
            _hasPendingRequest = _hasLatestRequestedInput && _pendingRequest.Grid.IsValid;
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
                : _build?.Request ?? _committed?.Request ?? _pendingRequest;
            if (!request.Grid.IsValid
                || !FlowFieldGridSpace.IsFinite(request.WorldBounds.center)
                || !FlowFieldGridSpace.IsFinite(request.WorldBounds.size))
                return;

            Physics.SyncTransforms();
            Bounds sweepBounds = request.WorldBounds;
            float sweepMargin = request.Grid.CellSize + request.ObstacleClearance;
            sweepBounds.Expand(Vector3.one * (sweepMargin * 2f));
            int count = SweepObstacles(sweepBounds, request.ObstacleLayer, out bool complete);
            if (!complete)
            {
                // A saturated query cannot prove that an old collider was
                // removed. Keep the prior observations and conservatively
                // hold the complete swept area Unknown until a full query is
                // available.
                MarkObstacleRegionDirty(sweepBounds);
                return;
            }

            _seenObstacleColliders.Clear();
            for (int index = 0; index < count; index++)
            {
                Collider collider = _obstacleSweepBuffer[index];
                if (collider == null || FindDynamicObstacleIndex(collider) >= 0)
                    continue;
                _seenObstacleColliders.Add(collider);
                Bounds current = collider.bounds;
                ObstacleObservation currentState = ObstacleObservation.From(collider);
                if (!_observedObstacleBounds.TryGetValue(collider, out Bounds previous))
                {
                    _observedObstacleBounds[collider] = current;
                    _observedObstacleStates[collider] = currentState;
                    MarkObstacleRegionDirty(current);
                    continue;
                }
                if (!_observedObstacleStates.TryGetValue(collider, out ObstacleObservation previousState)
                    || !previousState.Equals(currentState))
                {
                    MarkObstacleRegionDirty(previousState.Bounds.size.sqrMagnitude > 0f
                        ? previousState.Bounds
                        : previous);
                    MarkObstacleRegionDirty(current);
                    _observedObstacleBounds[collider] = current;
                    _observedObstacleStates[collider] = currentState;
                }
            }

            if (_observedObstacleBounds.Count == 0)
                return;
            List<Collider> removed = null;
            foreach (KeyValuePair<Collider, Bounds> pair in _observedObstacleBounds)
            {
                if (_seenObstacleColliders.Contains(pair.Key))
                    continue;
                if (removed == null)
                    removed = new List<Collider>();
                removed.Add(pair.Key);
                if (_observedObstacleStates.TryGetValue(pair.Key, out ObstacleObservation removedState))
                    MarkObstacleRegionDirty(removedState.Bounds);
                else
                    MarkObstacleRegionDirty(pair.Value);
            }
            if (removed == null)
                return;
            for (int index = 0; index < removed.Count; index++)
            {
                _observedObstacleBounds.Remove(removed[index]);
                _observedObstacleStates.Remove(removed[index]);
            }
        }

        private int SweepObstacles(Bounds bounds, LayerMask obstacleLayer, out bool complete)
        {
            while (true)
            {
                int count = Physics.OverlapBoxNonAlloc(
                    bounds.center,
                    bounds.extents,
                    _obstacleSweepBuffer,
                    Quaternion.identity,
                    obstacleLayer,
                    QueryTriggerInteraction.Ignore);
                if (count < _obstacleSweepBuffer.Length)
                {
                    complete = true;
                    return count;
                }
                if (_obstacleSweepBuffer.Length >= MaxObstacleSweepColliders)
                {
                    complete = false;
                    return count;
                }
                int nextCapacity = Math.Min(
                    MaxObstacleSweepColliders,
                    _obstacleSweepBuffer.Length * 2);
                _obstacleSweepBuffer = new Collider[nextCapacity];
            }
        }

        internal void DetectModifierChanges()
        {
            bool changed = false;
            for (int index = _modifiers.Count - 1; index >= 0; index--)
            {
                IFlowFieldVectorModifier modifier = _modifiers[index];
                if (IsMissingModifier(modifier))
                {
                    _modifierObservations.Remove(modifier);
                    _modifiers.RemoveAt(index);
                    changed = true;
                    continue;
                }

                ModifierObservation current = ModifierObservation.From(modifier);
                if (!_modifierObservations.TryGetValue(modifier, out ModifierObservation previous)
                    || !previous.Equals(current))
                {
                    _modifierObservations[modifier] = current;
                    changed = true;
                }
            }

            if (changed)
            {
                SortModifiers();
                ValidateModifierPriorities();
                QueueCurrentRequest();
            }
        }

        internal bool RegisterModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            if (modifier == null)
                throw new ArgumentNullException(nameof(modifier));
            if (modifier.InfluenceCollider == null)
                throw new InvalidOperationException("Influence Collider is required.");
            if (!modifier.InfluenceCollider.isTrigger)
                throw new ArgumentException(FlowFieldModifierMaskBuilder.TriggerRequiredMessage, nameof(modifier));
            if (modifier.InfluenceCollider is MeshCollider meshCollider && !meshCollider.convex)
                throw new ArgumentException(FlowFieldModifierMaskBuilder.ConvexMeshRequiredMessage, nameof(modifier));
            if (_modifiers.Contains(modifier))
                return false;
            for (int index = 0; index < _modifiers.Count; index++)
                if (_modifiers[index] != null && _modifiers[index].Priority == modifier.Priority)
                    throw new InvalidOperationException(
                        $"Vector modifier priority {modifier.Priority} is duplicated.");
            _modifiers.Add(modifier);
            _modifierObservations[modifier] = ModifierObservation.From(modifier);
            SortModifiers();
            QueueCurrentRequest();
            return true;
        }

        internal override bool RegisterModifierCommand(IFlowFieldVectorModifier modifier)
            => RegisterModifier(modifier);

        internal bool UnregisterModifier(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            if (!_modifiers.Remove(modifier))
                return false;
            _modifierObservations.Remove(modifier);
            QueueCurrentRequest();
            return true;
        }

        internal override bool UnregisterModifierCommand(IFlowFieldVectorModifier modifier)
            => UnregisterModifier(modifier);

        internal void MarkModifierDirty(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            ValidateModifierChange(modifier, FlowFieldModifierChange.Value | FlowFieldModifierChange.Priority);
            QueueCurrentRequest();
        }

        internal void MarkModifierAreaDirty(IFlowFieldVectorModifier modifier)
        {
            ThrowIfInputAvailable();
            ValidateModifierChange(modifier, FlowFieldModifierChange.Area);
            QueueCurrentRequest();
        }

        internal override void NotifyModifierChangedCommand(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            ThrowIfInputAvailable();
            ValidateModifierChange(modifier, change);
            if (change != FlowFieldModifierChange.None)
                QueueCurrentRequest();
        }

        internal void ValidateModifierChange(
            IFlowFieldVectorModifier modifier,
            FlowFieldModifierChange change)
        {
            ThrowIfInputAvailable();
            if (modifier == null || !_modifiers.Contains(modifier))
                throw new InvalidOperationException("Modifier is not registered.");
            if ((change & (FlowFieldModifierChange.Value
                | FlowFieldModifierChange.Area
                | FlowFieldModifierChange.Priority)) == 0)
                return;

            Collider collider = modifier.InfluenceCollider;
            if (collider == null)
                throw new InvalidOperationException("Influence Collider is required.");
            if (!collider.isTrigger)
                throw new ArgumentException(
                    FlowFieldModifierMaskBuilder.TriggerRequiredMessage,
                    nameof(modifier));
            if (collider is MeshCollider meshCollider && !meshCollider.convex)
                throw new ArgumentException(
                    FlowFieldModifierMaskBuilder.ConvexMeshRequiredMessage,
                    nameof(modifier));
            if ((change & FlowFieldModifierChange.Priority) != 0)
            {
                for (int index = 0; index < _modifiers.Count; index++)
                {
                    IFlowFieldVectorModifier other = _modifiers[index];
                    if (!ReferenceEquals(other, modifier)
                        && other != null
                        && other.Priority == modifier.Priority)
                        throw new InvalidOperationException(
                            $"Vector modifier priority {modifier.Priority} is duplicated.");
                }
            }
        }

        internal void Suspend()
        {
            if (!IsInitialized)
                return;
            _suspendedByLifecycle = true;
            _generation++;
            InvalidateBaseLifetime();
            _build?.Dispose();
            _build = null;
            _buildReservationBytes = 0;
            _hasPendingRequest = false;
            SetState(FlowFieldRuntimeState.Suspended);
        }

        internal void Resume()
        {
            if (_state != FlowFieldRuntimeState.Suspended)
                return;
            _suspendedByLifecycle = false;
            SetState(FlowFieldRuntimeState.Building);
        }

        internal void Release()
        {
            if (!IsInitialized && !IsFaulted)
                return;
            _generation++;
            _build?.Dispose();
            _build = null;
            _buildReservationBytes = 0;
            _hasPendingRequest = false;
            _committed = null;
            _committedReservationBytes = 0;
            _safetyStates = null;
            _safetyReservationBytes = 0;
            _hasLatestRequestedInput = false;
            _suspendedByLifecycle = false;
            _uncertainBounds.Clear();
            if (_runtimeScheduled)
            {
                FlowFieldBuildScheduler.Unregister(this);
                _runtimeScheduled = false;
            }
            _computeSolver?.Dispose();
            _computeSolver = null;
            _dynamicObstacles.Clear();
            _dynamicObstacleBounds.Clear();
            _dynamicBounds.Clear();
            _observedObstacleBounds.Clear();
            _seenObstacleColliders.Clear();
            _registeredObstacleOverlapBuffer = null;
            _modifiers.Clear();
            _modifierObservations.Clear();
            _observedObstacleStates.Clear();
            ClearAcceptedRequest();
            SetState(FlowFieldRuntimeState.Released);
        }

        internal void RetryFault()
        {
            if (!IsFaulted)
                return;
            _fault = null;
            // A retry only clears the failure.  The next accepted request
            // must still build and publish before the session becomes Ready;
            // never expose a transient Ready state with no valid result.
            SetState(FlowFieldRuntimeState.Building);
        }

        internal void ReportFault(Exception exception)
        {
            if (!IsInitialized || IsFaulted)
                return;
            _generation++;
            _build?.Dispose();
            _build = null;
            _buildReservationBytes = 0;
            _hasPendingRequest = false;
            _fault = exception ?? new InvalidOperationException("FlowField volume session failed.");
            SetState(FlowFieldRuntimeState.Faulted);
            NotifyFailed(_fault);
        }

        internal void SetSpaceMode(FlowFieldSpaceMode mode)
        {
            if (mode != FlowFieldSpaceMode.Volume3D)
                throw new ArgumentException("The volume session only supports Volume3D.", nameof(mode));
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
                _generation,
                field.ResultId);
            view = FlowFieldReadView.CreateVolume(
                source,
                _revision,
                field.Blocked,
                field.FinalDirections,
                field.FinalSpeeds,
                true);
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
                    _generation,
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
                _generation,
                _committed.ResultId);
            return source.IsValid;
        }

        private bool StepBuild(BuildContext build, in FlowFieldBuildBudget budget)
        {
            while (!budget.IsExpired)
            {
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

                if (build.Generation != _generation)
                    throw new OperationCanceledException("FlowField volume build generation is stale.");
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
                case 9: build.Costs = new int[build.Count]; break;
                case 10: build.Queue = new int[build.Count]; break;
                case 11: build.Influence = new bool[build.Count]; break;
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
            Vector3 half = ObstacleHalfExtents(
                build.Request.Grid.CellSize,
                build.Request.ObstacleClearance);
            for (; build.Cursor < end; build.Cursor++)
            {
                int index = build.Cursor;
                build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                Vector3 center = build.Request.Grid.LocalToWorldCenter(x, y, z);
                build.Blocked[index] = Physics.CheckBox(
                    center,
                    half,
                    Quaternion.identity,
                    build.Request.ObstacleLayer,
                    QueryTriggerInteraction.Ignore);
                if (!build.Blocked[index])
                    build.Blocked[index] = IntersectsRegisteredObstacle(center, half);
            }
            return build.Cursor >= build.Count;
        }

        private bool IntersectsRegisteredObstacle(Vector3 center, Vector3 halfExtents)
        {
            for (int index = 0; index < _dynamicObstacles.Count; index++)
            {
                Collider collider = _dynamicObstacles[index];
                if (collider == null || collider.isTrigger)
                    continue;
                int layerMask = 1 << collider.gameObject.layer;
                if (!Physics.CheckBox(
                        center,
                        halfExtents,
                        Quaternion.identity,
                        layerMask,
                        QueryTriggerInteraction.Ignore))
                    continue;
                if (FlowFieldOverlapUtility.OverlapsTarget(
                        center,
                        halfExtents,
                        layerMask,
                        collider,
                        QueryTriggerInteraction.Ignore,
                        ref _registeredObstacleOverlapBuffer))
                    return true;
            }
            return false;
        }

        private bool StepTopology(BuildContext build)
        {
            int end = Math.Min(build.Cursor + CellsPerStep, build.Count);
            for (; build.Cursor < end; build.Cursor++)
            {
                int index = build.Cursor;
                if (build.Blocked[index])
                {
                    build.Next[index] = -2;
                    build.GoalFlags[index] = FlowFieldGoalFlags.None;
                    build.Directions[index] = Vector3.zero;
                    build.Speeds[index] = 0f;
                    continue;
                }

                build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                uint mask = 0u;
                for (int direction = 0; direction < FlowFieldNeighborUtility.VolumeCount; direction++)
                {
                    if (CanTraverse(build, index, x, y, z, direction, out _))
                        mask |= 1u << direction;
                }
                build.Topology[index] = mask;
            }
            return build.Cursor >= build.Count;
        }

        private bool StepGoal(BuildContext build)
        {
            switch (build.GoalStage)
            {
                case 0:
                {
                    int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
                    for (; build.Cursor < end; build.Cursor++)
                        build.Influence[build.Cursor] = false;
                    if (build.Cursor < build.Count)
                        return false;

                    build.Cursor = 0;
                    build.GoalIndex = -1;
                    build.Head = 0;
                    build.Tail = 0;
                    if (!build.Request.HasGoal)
                    {
                        build.GoalStage = 4;
                        return true;
                    }

                    Vector3 clamped = build.Request.Grid.ClampWorldXYZ(build.Request.GoalWorld);
                    build.Request.Grid.TryWorldToLocalClamped(clamped, out int x, out int y, out int z);
                    build.RequestedGoalIndex = build.Request.Grid.ToFlatIndex(x, y, z);
                    if (!build.Blocked[build.RequestedGoalIndex])
                    {
                        build.GoalIndex = build.RequestedGoalIndex;
                        build.GoalStage = 2;
                        build.Cursor = 0;
                    }
                    else
                    {
                        build.NearestGoalDistance = long.MaxValue;
                        build.GoalStage = 1;
                    }
                    break;
                }
                case 1:
                {
                    build.Request.Grid.FromFlatIndex(
                        build.RequestedGoalIndex,
                        out int requestedX,
                        out int requestedY,
                        out int requestedZ);
                    int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
                    for (; build.Cursor < end; build.Cursor++)
                    {
                        int index = build.Cursor;
                        if (build.Blocked[index])
                            continue;
                        build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                        long dx = x - requestedX;
                        long dy = y - requestedY;
                        long dz = z - requestedZ;
                        long distance = dx * dx + dy * dy + dz * dz;
                        if (distance < build.NearestGoalDistance
                            || distance == build.NearestGoalDistance
                                && (build.GoalIndex < 0 || index < build.GoalIndex))
                        {
                            build.NearestGoalDistance = distance;
                            build.GoalIndex = index;
                        }
                    }
                    if (build.Cursor < build.Count)
                        return false;
                    build.Cursor = 0;
                    build.GoalStage = build.GoalIndex >= 0 ? 2 : 4;
                    break;
                }
                case 2:
                {
                    if (build.GoalIndex < 0)
                    {
                        build.GoalStage = 4;
                        return true;
                    }

                    build.Request.Grid.FromFlatIndex(
                        build.GoalIndex,
                        out int goalX,
                        out int goalY,
                        out int goalZ);
                    build.GoalRadiusSqr = (double)build.Request.GoalInfluenceRadius
                        * build.Request.GoalInfluenceRadius;
                    int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
                    for (; build.Cursor < end; build.Cursor++)
                    {
                        int index = build.Cursor;
                        if (build.Blocked[index])
                            continue;
                        build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                        long dx = x - goalX;
                        long dy = y - goalY;
                        long dz = z - goalZ;
                        double distanceSqr = (double)(dx * dx + dy * dy + dz * dz)
                            * build.Request.Grid.CellSize
                            * build.Request.Grid.CellSize;
                        build.Influence[index] = build.Request.GoalInfluenceRadius <= 0f
                            || distanceSqr <= build.GoalRadiusSqr;
                    }
                    if (build.Cursor < build.Count)
                        return false;

                    build.Influence[build.GoalIndex] = true;
                    build.Costs[build.GoalIndex] = 0;
                    build.Queue[build.Tail++] = build.GoalIndex;
                    build.GoalStage = 4;
                    return true;
                }
                default:
                    return true;
            }

            return false;
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

            int processed = 0;
            while (build.Head < build.Tail && processed++ < CellsPerStep)
            {
                int current = build.Queue[build.Head++];
                build.Request.Grid.FromFlatIndex(current, out int x, out int y, out int z);
                int currentCost = build.Costs[current];
                for (int direction = 0; direction < FlowFieldNeighborUtility.VolumeCount; direction++)
                {
                    if ((build.Topology[current] & (1u << direction)) == 0u)
                        continue;
                    if (!TryNeighbor(build.Request.Grid, x, y, z, direction, out int neighbor))
                        continue;
                    if (!build.Influence[neighbor])
                        continue;
                    int candidate = currentCost + 1;
                    if (candidate < build.Costs[neighbor])
                    {
                        build.Costs[neighbor] = candidate;
                        build.Queue[build.Tail++] = neighbor;
                    }
                }
            }
            return build.Head >= build.Tail;
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
                build.Request.MaxGpuWaves,
                build.Generation);
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
                ApplyGpuResult(
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

        private static void ApplyGpuResult(
            BuildContext build,
            Unity.Collections.NativeArray<GpuFlowCell> result,
            int start,
            int end)
        {
            Vector3 defaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(
                build.Request.DefaultDirection);
            for (int index = start; index < end; index++)
            {
                GpuFlowCell cell = result[index];
                if (cell.NextCell < -3 || cell.NextCell >= build.Count
                    || !FlowFieldGridSpace.IsFinite(cell.Direction))
                    throw new InvalidOperationException("Volume3D GPU result contains an invalid cell.");
                if (cell.NextCell == -2 && !build.Blocked[index]
                    || cell.NextCell == -1 && (build.Blocked[index] || build.Influence[index])
                    || cell.NextCell == -3 && (build.Blocked[index] || !build.Influence[index]))
                    throw new InvalidOperationException("Volume3D GPU result contains an inconsistent sentinel.");
                if (cell.NextCell < 0
                    && cell.Direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                    throw new InvalidOperationException("Volume3D GPU sentinel cells must have zero direction.");

                if (cell.NextCell >= 0)
                {
                    if (build.Blocked[index] || !build.Influence[index])
                        throw new InvalidOperationException("Volume3D GPU directed a blocked or out-of-influence cell.");
                    if (cell.NextCell == index)
                    {
                        if (index != build.GoalIndex
                            || cell.Direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                            throw new InvalidOperationException("Volume3D GPU produced a non-goal anchor.");
                    }
                    else
                    {
                        build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                        build.Request.Grid.FromFlatIndex(cell.NextCell, out int nx, out int ny, out int nz);
                        int direction = FlowFieldNeighborUtility.FindDirectionIndex(nx - x, ny - y, nz - z);
                        if (direction < 0
                            || (build.Topology[index] & (1u << direction)) == 0u
                            || !CanTraverse(
                                build,
                                index,
                                x,
                                y,
                                z,
                                direction,
                                out int traversable)
                            || traversable != cell.NextCell
                            || !build.Influence[cell.NextCell])
                            throw new InvalidOperationException("Volume3D GPU produced a non-topological NextCell.");
                        if (cell.Direction.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                            || Mathf.Abs(cell.Direction.magnitude - 1f) > 0.001f)
                            throw new InvalidOperationException("Volume3D GPU produced an invalid direction.");
                        Vector3 expected = DirectionBetweenCells(
                            build.Request.Grid,
                            index,
                            cell.NextCell);
                        if ((cell.Direction - expected).sqrMagnitude > 1e-10f)
                            throw new InvalidOperationException("Volume3D GPU direction differs from the deterministic CPU direction.");
                    }
                }

                build.Next[index] = cell.NextCell;
                build.Directions[index] = cell.NextCell == -1
                    ? defaultDirection
                    : cell.NextCell >= 0
                        ? cell.NextCell == index
                            ? Vector3.zero
                            : DirectionBetweenCells(
                                build.Request.Grid,
                                index,
                                cell.NextCell)
                        : Vector3.zero;
                build.Speeds[index] = build.Directions[index].sqrMagnitude > 0.00000001f ? 1f : 0f;
                build.GoalFlags[index] = cell.NextCell >= 0
                    ? FlowFieldGoalFlags.Directed
                    : cell.NextCell == -3
                        ? FlowFieldGoalFlags.Unreachable
                        : FlowFieldGoalFlags.None;
                if (cell.NextCell == index)
                    build.GoalFlags[index] |= FlowFieldGoalFlags.Anchor;
            }
        }

        private static bool StepGpuFallbackReset(BuildContext build)
        {
            int end = Math.Min(
                build.GpuFallbackResetCursor + ArrayElementsPerStep,
                build.Count);
            for (; build.GpuFallbackResetCursor < end; build.GpuFallbackResetCursor++)
                build.Costs[build.GpuFallbackResetCursor] = Unreachable;

            if (build.GpuFallbackResetCursor < build.Count)
                return false;

            build.Head = 0;
            build.Tail = 0;
            if (build.GoalIndex >= 0)
            {
                build.Costs[build.GoalIndex] = 0;
                build.Queue[build.Tail++] = build.GoalIndex;
            }
            build.GpuFailed = null;
            build.GpuFallbackResetCursor = 0;
            build.Cursor = 0;
            return true;
        }

        private bool StepDirections(BuildContext build)
        {
            int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
            Vector3 defaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(build.Request.DefaultDirection);
            for (; build.Cursor < end; build.Cursor++)
            {
                int index = build.Cursor;
                if (build.Blocked[index])
                {
                    build.Next[index] = -2;
                    build.GoalFlags[index] = FlowFieldGoalFlags.None;
                    build.Directions[index] = Vector3.zero;
                    build.Speeds[index] = 0f;
                    continue;
                }

                // The Goal itself is the only anchor. A cell whose successor
                // happens to be the Goal remains an ordinary directed cell.
                // StepTopology runs before Goal resolution, so this check
                // deliberately lives here instead of in topology creation.
                if (build.GoalIndex == index)
                {
                    build.Next[index] = index;
                    build.GoalFlags[index] = FlowFieldGoalFlags.Directed
                        | FlowFieldGoalFlags.Anchor;
                    build.Directions[index] = Vector3.zero;
                    build.Speeds[index] = 0f;
                    continue;
                }

                Vector3 direction = defaultDirection;
                int next = -1;
                bool goalCellIsUnreachable = build.GoalIndex >= 0
                    && build.Influence[index]
                    && build.Costs[index] == Unreachable;
                if (build.GoalIndex >= 0
                    && build.Influence[index]
                    && build.Costs[index] != Unreachable)
                {
                    build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                    int bestScore = int.MinValue;
                    for (int candidateDirection = 0; candidateDirection < FlowFieldNeighborUtility.VolumeCount; candidateDirection++)
                    {
                        if ((build.Topology[index] & (1u << candidateDirection)) == 0u
                            || !TryNeighbor(build.Request.Grid, x, y, z, candidateDirection, out int neighbor)
                            || build.Costs[neighbor] != build.Costs[index] - 1)
                            continue;
                        int score = AlignmentScore(x, y, z, build.Request.Grid, build.GoalIndex, candidateDirection);
                        if (next < 0 || score > bestScore || score == bestScore && neighbor < next)
                        {
                            next = neighbor;
                            bestScore = score;
                        }
                    }

                    if (next >= 0)
                        direction = DirectionBetweenCells(build.Request.Grid, index, next);
                    else
                    {
                        goalCellIsUnreachable = true;
                        direction = Vector3.zero;
                        next = -3;
                    }
                }

                if (goalCellIsUnreachable)
                {
                    direction = Vector3.zero;
                    next = -3;
                }

                build.Next[index] = next;
                build.GoalFlags[index] = next >= 0
                    ? FlowFieldGoalFlags.Directed
                    : build.GoalIndex >= 0 && build.Influence[index]
                        ? FlowFieldGoalFlags.Unreachable
                        : FlowFieldGoalFlags.None;
                build.Directions[index] = direction;
                build.Speeds[index] = direction.sqrMagnitude > 0.00000001f ? 1f : 0f;
            }
            return build.Cursor >= build.Count;
        }

        private bool StepEscape(BuildContext build)
        {
            if (build.EscapeStage == 0)
            {
                int end = Math.Min(build.Cursor + ArrayElementsPerStep, build.Count);
                for (; build.Cursor < end; build.Cursor++)
                {
                    int index = build.Cursor;
                    build.Costs[index] = build.Blocked[index] ? Unreachable : 0;
                    build.EscapeDirections[index] = Vector3.zero;
                    if (!build.Blocked[index])
                        build.Queue[build.EscapeTail++] = index;
                }
                if (build.Cursor < build.Count)
                    return false;
                build.Cursor = 0;
                build.EscapeStage = 1;
            }

            int processed = 0;
            while (build.EscapeHead < build.EscapeTail && processed++ < CellsPerStep)
            {
                int current = build.Queue[build.EscapeHead++];
                build.Request.Grid.FromFlatIndex(current, out int x, out int y, out int z);
                int nextCost = build.Costs[current] + 1;
                for (int face = 0; face < FlowFieldNeighborUtility.VolumeFaceDirections.Length; face++)
                {
                    int direction = FlowFieldNeighborUtility.VolumeFaceDirections[face];
                    if (!TryNeighbor(build.Request.Grid, x, y, z, direction, out int neighbor)
                        || !build.Blocked[neighbor]
                        || build.Costs[neighbor] != Unreachable)
                        continue;
                    build.Costs[neighbor] = nextCost;
                    build.EscapeDirections[neighbor] = DirectionBetweenCells(
                        build.Request.Grid,
                        neighbor,
                        current);
                    build.Queue[build.EscapeTail++] = neighbor;
                }
            }
            return build.EscapeHead >= build.EscapeTail;
        }

        private bool StepModifiers(BuildContext build)
        {
            if (build.Cursor == 0 && build.Modifiers.Length > 0)
                Physics.SyncTransforms();

            int end = Math.Min(build.Cursor + CellsPerStep, build.Count);
            for (; build.Cursor < end; build.Cursor++)
            {
                int index = build.Cursor;
                if (build.Blocked[index] || build.Directions[index].sqrMagnitude <= 0.00000001f)
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
                    FlowFieldModifierWorkItem modifier = build.Modifiers[modifierIndex];
                    Collider collider = modifier.InfluenceCollider;
                    if (!FlowFieldModifierMaskBuilder.IsUsableTrigger(collider))
                        continue;
                    int layerMask = 1 << collider.gameObject.layer;
                    if (!FlowFieldOverlapUtility.OverlapsTarget(
                            center,
                            Vector3.one * (build.Request.Grid.CellSize * 0.5f),
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
            if (build == null || build.Generation != _generation || !ReferenceEquals(_build, build))
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
            bool safetyChangedDuringBuild = build.SafetyEpoch != _safetyEpoch;
            // Allocate and populate the replacement safety snapshot before
            // publishing any field reference. If allocation or initialization
            // fails, the previous publication remains intact.
            SafetyCellState[] nextSafety = new SafetyCellState[build.Count];
            for (int index = 0; index < build.Count; index++)
                nextSafety[index] = build.Blocked[index]
                    ? SafetyCellState.Blocked
                    : SafetyCellState.Clear;
            if (safetyChangedDuringBuild)
            {
                for (int index = 0; index < build.Count; index++)
                {
                    build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                    if (IntersectsUncertainBounds(
                            build.Request.Grid,
                            build.Request.Grid.LocalToWorldCenter(x, y, z)))
                        nextSafety[index] = SafetyCellState.Unknown;
                }
            }

            _committed = next;
            _revision++;
            _committedReservationBytes = EstimateCommittedBytes(build.Count);
            _safetyStates = nextSafety;
            _safetyReservationBytes = EstimateSafetyBytes(build.Count);
            if (!_hasPendingRequest && !safetyChangedDuringBuild)
                _uncertainBounds.Clear();
            _build = null;
            _buildReservationBytes = 0;
            if (gridChanged)
                _generation++;

            // Publish only after every private reference, revision and
            // lifecycle field has been made internally consistent. A
            // listener may Release, Suspend or submit another request.
            int callbackGeneration = _generation;
                NotifyFieldCommitted(true);
            return _generation != callbackGeneration;
        }

        private void QueueCurrentRequest()
        {
            if (_hasPendingRequest)
            {
                // Preserve a newer Goal/obstacle request that was already
                // coalesced while a modifier change is being observed.
                Submit(_pendingRequest);
                return;
            }
            if (_committed != null)
            {
                if (_hasLatestRequestedInput)
                {
                    Submit(_latestRequestedInput);
                    return;
                }
                Submit(_committed.Request);
                return;
            }
            if (_build != null)
            {
                // A modifier can be registered while the very first build is
                // still staging. Queue the same immutable request so the
                // completed snapshot is followed by a composition using the
                // newly captured modifier list.
                Submit(_hasLatestRequestedInput ? _latestRequestedInput : _build.Request);
                return;
            }
        }

        private void DetectDynamicObstacleChanges()
        {
            for (int i = _dynamicObstacles.Count - 1; i >= 0; i--)
            {
                Collider collider = _dynamicObstacles[i];
                if (collider == null)
                {
                    Bounds removedBounds = _dynamicObstacleBounds[i];
                    if (FlowFieldGridSpace.IsFinite(removedBounds.center)
                        && FlowFieldGridSpace.IsFinite(removedBounds.size))
                        MarkObstacleRegionDirty(removedBounds);
                    _dynamicObstacles.RemoveAt(i);
                    _dynamicObstacleBounds.RemoveAt(i);
                    _dynamicBounds.Remove(collider);
                    _observedObstacleBounds.Remove(collider);
                    _observedObstacleStates.Remove(collider);
                    continue;
                }
                Bounds current = collider.bounds;
                Bounds previous = _dynamicObstacleBounds[i];
                if ((current.center - previous.center).sqrMagnitude > 0.00000001f
                    || (current.size - previous.size).sqrMagnitude > 0.00000001f)
                {
                    if (FlowFieldGridSpace.IsFinite(previous.center)
                        && FlowFieldGridSpace.IsFinite(previous.size))
                        MarkObstacleRegionDirty(previous);
                    _dynamicObstacleBounds[i] = current;
                    _dynamicBounds[collider] = current;
                    MarkObstacleRegionDirty(current);
                }
            }
        }

        private int FindDynamicObstacleIndex(Collider collider)
        {
            for (int index = 0; index < _dynamicObstacles.Count; index++)
                if (ReferenceEquals(_dynamicObstacles[index], collider))
                    return index;
            return -1;
        }

        private bool IsLatestCellUnknown(VolumeFieldData field, int index, Vector3 center)
        {
            if (field == null || field.Request.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            if (IntersectsUncertainBounds(field.Grid, center))
                return true;
            return _safetyStates != null
                && _safetyStates.Length == field.Grid.CellCount
                && _safetyStates[index] == SafetyCellState.Unknown;
        }

        private bool IsLatestCellBlocked(VolumeFieldData field, int index, Vector3 center)
        {
            if (field == null || field.Request.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            return _safetyStates != null
                && _safetyStates.Length == field.Grid.CellCount
                && _safetyStates[index] == SafetyCellState.Blocked;
        }

        private bool IsLatestPathUnsafe(VolumeFieldData field, int current, int next)
        {
            if (IsLatestCellUnsafe(field, current)
                || IsLatestCellUnsafe(field, next))
                return true;
            field.Grid.FromFlatIndex(current, out int x0, out int y0, out int z0);
            field.Grid.FromFlatIndex(next, out int x1, out int y1, out int z1);
            for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
            for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
            for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
            {
                if (x == x0 && y == y0 && z == z0 || x == x1 && y == y1 && z == z1)
                    continue;
                int intermediate = field.Grid.ToFlatIndex(x, y, z);
                if (IsLatestCellUnsafe(field, intermediate))
                    return true;
            }
            return IsLatestCellUnsafe(field, next);
        }

        private bool IsLatestCellUnsafe(VolumeFieldData field, int index)
        {
            Vector3 center = CellCenter(field.Grid, index);
            return IsLatestCellUnknown(field, index, center)
                || IsLatestCellBlocked(field, index, center);
        }

        private bool IsEscapePathUnsafe(VolumeFieldData field, int index)
        {
            Vector3 direction = field.EscapeDirections[index];
            if (direction.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                return false;
            field.Grid.FromFlatIndex(index, out int x, out int y, out int z);
            int dx = direction.x > 0.001f ? 1 : direction.x < -0.001f ? -1 : 0;
            int dy = direction.y > 0.001f ? 1 : direction.y < -0.001f ? -1 : 0;
            int dz = direction.z > 0.001f ? 1 : direction.z < -0.001f ? -1 : 0;
            if (!field.Grid.IsLocalInBounds(x + dx, y + dy, z + dz))
                return true;
            int neighbor = field.Grid.ToFlatIndex(x + dx, y + dy, z + dz);
            return IsLatestCellUnsafe(field, neighbor);
        }

        private bool IsDefaultDirectionPathUnsafe(VolumeFieldData field, int index)
        {
            Vector3 direction = FlowFieldVectorUtility.NormalizeDefaultDirection(field.Request.DefaultDirection);
            field.Grid.FromFlatIndex(index, out int x, out int y, out int z);
            int sx = direction.x > 0.000001f ? 1 : direction.x < -0.000001f ? -1 : 0;
            int sy = direction.y > 0.000001f ? 1 : direction.y < -0.000001f ? -1 : 0;
            int sz = direction.z > 0.000001f ? 1 : direction.z < -0.000001f ? -1 : 0;
            int axisCount = (sx != 0 ? 1 : 0) + (sy != 0 ? 1 : 0) + (sz != 0 ? 1 : 0);
            if (axisCount == 0)
                return true;

            int axisMaskLimit = 1 << axisCount;
            for (int mask = 1; mask < axisMaskLimit; mask++)
            {
                int targetX = x;
                int targetY = y;
                int targetZ = z;
                int bit = 0;
                if (sx != 0)
                {
                    if ((mask & (1 << bit)) != 0)
                        targetX += sx;
                    bit++;
                }
                if (sy != 0)
                {
                    if ((mask & (1 << bit)) != 0)
                        targetY += sy;
                    bit++;
                }
                if (sz != 0 && (mask & (1 << bit)) != 0)
                    targetZ += sz;
                if (!field.Grid.IsLocalInBounds(targetX, targetY, targetZ))
                    return true;
                int target = field.Grid.ToFlatIndex(targetX, targetY, targetZ);
                if (IsLatestCellUnsafe(field, target))
                    return true;
            }
            return false;
        }

        private bool IntersectsUncertainBounds(FlowFieldGridSpace grid, Vector3 center)
        {
            Bounds cell = new Bounds(center, Vector3.one * grid.CellSize);
            for (int i = 0; i < _uncertainBounds.Count; i++)
                if (_uncertainBounds[i].Intersects(cell))
                    return true;
            return false;
        }

        private void AddUncertainBounds(Bounds bounds)
        {
            for (int index = 0; index < _uncertainBounds.Count; index++)
            {
                if (!_uncertainBounds[index].Intersects(bounds))
                    continue;
                _uncertainBounds[index] = UnionBounds(_uncertainBounds[index], bounds);
                return;
            }

            if (_uncertainBounds.Count < MaxUncertainBounds)
            {
                _uncertainBounds.Add(bounds);
                return;
            }

            // Once the bounded dirty-list is full, retaining a conservative
            // union is safer than evicting an area and treating it as clear.
            _uncertainBounds[0] = UnionBounds(_uncertainBounds[0], bounds);
        }

        private static Bounds UnionBounds(Bounds left, Bounds right)
        {
            left.Encapsulate(right.min);
            left.Encapsulate(right.max);
            return left;
        }

        private static int FindNearestUnblocked(BuildContext build, int requested)
        {
            if (!build.Blocked[requested])
                return requested;
            build.Request.Grid.FromFlatIndex(requested, out int rx, out int ry, out int rz);
            int best = -1;
            long bestDistance = long.MaxValue;
            for (int index = 0; index < build.Count; index++)
            {
                if (build.Blocked[index])
                    continue;
                build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
                long dx = x - rx;
                long dy = y - ry;
                long dz = z - rz;
                long distance = dx * dx + dy * dy + dz * dz;
                if (distance < bestDistance || distance == bestDistance && index < best)
                {
                    best = index;
                    bestDistance = distance;
                }
            }
            return best;
        }

        private static bool CanTraverse(
            BuildContext build,
            int current,
            int x,
            int y,
            int z,
            int direction,
            out int neighbor)
        {
            neighbor = -1;
            int nx = x + FlowFieldNeighborUtility.DeltaX[direction];
            int ny = y + FlowFieldNeighborUtility.DeltaY[direction];
            int nz = z + FlowFieldNeighborUtility.DeltaZ[direction];
            if (!build.Request.Grid.IsLocalInBounds(nx, ny, nz))
                return false;
            neighbor = build.Request.Grid.ToFlatIndex(nx, ny, nz);
            if (build.Blocked[neighbor])
                return false;
            if (!FlowFieldNeighborUtility.IsDiagonal(direction))
                return true;

            int dx = FlowFieldNeighborUtility.DeltaX[direction];
            int dy = FlowFieldNeighborUtility.DeltaY[direction];
            int dz = FlowFieldNeighborUtility.DeltaZ[direction];
            int maxXStep = dx == 0 ? 0 : 1;
            int maxYStep = dy == 0 ? 0 : 1;
            int maxZStep = dz == 0 ? 0 : 1;
            for (int sx = 0; sx <= maxXStep; sx++)
            for (int sy = 0; sy <= maxYStep; sy++)
            for (int sz = 0; sz <= maxZStep; sz++)
            {
                if (sx == 0 && sy == 0 && sz == 0
                    || sx == maxXStep && sy == maxYStep && sz == maxZStep)
                    continue;
                int ix = x + (sx == 1 ? dx : 0);
                int iy = y + (sy == 1 ? dy : 0);
                int iz = z + (sz == 1 ? dz : 0);
                if (ix == x && iy == y && iz == z)
                    continue;
                if (!build.Request.Grid.IsLocalInBounds(ix, iy, iz)
                    || build.Blocked[build.Request.Grid.ToFlatIndex(ix, iy, iz)])
                    return false;
            }
            return true;
        }

        private static bool TryNeighbor(
            FlowFieldGridSpace grid,
            int x,
            int y,
            int z,
            int direction,
            out int index)
        {
            int nx = x + FlowFieldNeighborUtility.DeltaX[direction];
            int ny = y + FlowFieldNeighborUtility.DeltaY[direction];
            int nz = z + FlowFieldNeighborUtility.DeltaZ[direction];
            if (!grid.IsLocalInBounds(nx, ny, nz))
            {
                index = -1;
                return false;
            }
            index = grid.ToFlatIndex(nx, ny, nz);
            return true;
        }

        private static Vector3 FindEscapeDirection(BuildContext build, int index)
        {
            build.Request.Grid.FromFlatIndex(index, out int x, out int y, out int z);
            int best = -1;
            for (int face = 0; face < FlowFieldNeighborUtility.VolumeFaceDirections.Length; face++)
            {
                int direction = FlowFieldNeighborUtility.VolumeFaceDirections[face];
                if (!TryNeighbor(build.Request.Grid, x, y, z, direction, out int neighbor)
                    || build.Blocked[neighbor])
                    continue;
                best = neighbor;
                break;
            }
            return best >= 0
                ? (CellCenter(build.Request.Grid, best) - CellCenter(build.Request.Grid, index)).normalized
                : Vector3.zero;
        }

        private static int AlignmentScore(
            int x,
            int y,
            int z,
            FlowFieldGridSpace grid,
            int goalIndex,
            int direction)
        {
            grid.FromFlatIndex(goalIndex, out int gx, out int gy, out int gz);
            int dx = gx - x;
            int dy = gy - y;
            int dz = gz - z;
            int sx = FlowFieldNeighborUtility.DeltaX[direction];
            int sy = FlowFieldNeighborUtility.DeltaY[direction];
            int sz = FlowFieldNeighborUtility.DeltaZ[direction];
            return 2 * (sx * dx + sy * dy + sz * dz) - (sx * sx + sy * sy + sz * sz);
        }

        private static Vector3 CellCenter(FlowFieldGridSpace grid, int index)
        {
            grid.FromFlatIndex(index, out int x, out int y, out int z);
            return grid.LocalToWorldCenter(x, y, z);
        }

        private static Vector3 DirectionBetweenCells(
            FlowFieldGridSpace grid,
            int fromIndex,
            int toIndex)
        {
            grid.FromFlatIndex(fromIndex, out int fromX, out int fromY, out int fromZ);
            grid.FromFlatIndex(toIndex, out int toX, out int toY, out int toZ);
            return new Vector3(toX - fromX, toY - fromY, toZ - fromZ).normalized;
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

        private void SortModifiers()
            => _modifiers.Sort((left, right) => left.Priority.CompareTo(right.Priority));

        private void ValidateModifierPriorities()
        {
            for (int index = 1; index < _modifiers.Count; index++)
                if (_modifiers[index - 1].Priority == _modifiers[index].Priority)
                    throw new InvalidOperationException(
                        $"Vector modifier priority {_modifiers[index].Priority} is duplicated.");
        }

        private FlowFieldModifierWorkItem[] CaptureModifierSnapshots()
        {
            if (_modifiers.Count == 0)
                return Array.Empty<FlowFieldModifierWorkItem>();

            FlowFieldModifierWorkItem[] snapshots = new FlowFieldModifierWorkItem[_modifiers.Count];
            for (int index = 0; index < _modifiers.Count; index++)
            {
                IFlowFieldVectorModifier modifier = _modifiers[index];
                IFlowFieldModifierSnapshot snapshot = modifier.CaptureSnapshot();
                if (snapshot == null)
                    throw new InvalidOperationException(
                        $"FlowField modifier '{modifier.GetType().Name}' returned a null snapshot.");
                snapshots[index] = new FlowFieldModifierWorkItem(
                    snapshot,
                    modifier.InfluenceCollider,
                    modifier.Priority,
                    modifier.Revision);
            }
            return snapshots;
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

            long buildReservation = EstimateBuildBytes(request.Grid.CellCount, _modifiers.Count);
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

            long replacementSafetyReservation = EstimateSafetyBytes(request.Grid.CellCount);
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

        private static long EstimateSafetyBytes(int count)
            => count <= 0 ? 0 : checked(count + 32L);

        private long EstimateAuxiliaryBytes()
        {
            long bytes = 64L;
            if (_obstacleSweepBuffer != null)
                bytes = checked(bytes + 8L * _obstacleSweepBuffer.Length);
            if (_registeredObstacleOverlapBuffer != null)
                bytes = checked(bytes + 8L * _registeredObstacleOverlapBuffer.Length);
            bytes = checked(bytes + 8L * _modifiers.Count);
            bytes = checked(bytes + 32L * _uncertainBounds.Count);
            return bytes;
        }

        private static bool IsMissingModifier(IFlowFieldVectorModifier modifier)
            => modifier == null
                || modifier is UnityEngine.Object unityObject && unityObject == null;

        private readonly struct ModifierObservation : IEquatable<ModifierObservation>
        {
            private readonly Collider _collider;
            private readonly int _revision;
            private readonly int _priority;
            private readonly Bounds _bounds;
            private readonly bool _enabled;
            private readonly bool _active;
            private readonly bool _trigger;

            private ModifierObservation(
                Collider collider,
                int revision,
                int priority,
                Bounds bounds,
                bool enabled,
                bool active,
                bool trigger)
            {
                _collider = collider;
                _revision = revision;
                _priority = priority;
                _bounds = bounds;
                _enabled = enabled;
                _active = active;
                _trigger = trigger;
            }

            internal static ModifierObservation From(IFlowFieldVectorModifier modifier)
            {
                Collider collider = modifier?.InfluenceCollider;
                return new ModifierObservation(
                    collider,
                    modifier?.Revision ?? 0,
                    modifier?.Priority ?? 0,
                    collider != null ? collider.bounds : default,
                    collider != null && collider.enabled,
                    collider != null && collider.gameObject.activeInHierarchy,
                    collider != null && collider.isTrigger);
            }

            public bool Equals(ModifierObservation other)
                => _collider == other._collider
                    && _revision == other._revision
                    && _priority == other._priority
                    && _enabled == other._enabled
                    && _active == other._active
                    && _trigger == other._trigger
                    && (_bounds.center - other._bounds.center).sqrMagnitude <= 0.00000001f
                    && (_bounds.size - other._bounds.size).sqrMagnitude <= 0.00000001f;

            public override bool Equals(object obj)
                => obj is ModifierObservation other && Equals(other);

            public override int GetHashCode()
                => 0;
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
            _build?.Dispose();
            _build = null;
            _buildReservationBytes = 0;
            _committed = null;
            _committedReservationBytes = 0;
            _safetyStates = null;
            _safetyReservationBytes = 0;
            _computeSolver?.Dispose();
            _computeSolver = null;
            _dynamicObstacles.Clear();
            _dynamicObstacleBounds.Clear();
            _dynamicBounds.Clear();
            _registeredObstacleOverlapBuffer = null;
            _observedObstacleBounds.Clear();
            _observedObstacleStates.Clear();
            _seenObstacleColliders.Clear();
            _modifiers.Clear();
            _modifierObservations.Clear();
            _uncertainBounds.Clear();
            ClearAcceptedRequest();
            _state = FlowFieldRuntimeState.Released;
            MarkBaseDisposed();
        }

        FlowFieldBuildOwner IFlowFieldBuildOperation.Owner
            => FlowFieldBuildOwner.Runtime;

        FlowFieldBuildOperationState IFlowFieldBuildOperation.OperationState
            => _disposed
                ? FlowFieldBuildOperationState.Cancelled
                : IsFaulted
                    ? FlowFieldBuildOperationState.Faulted
                    : IsWaitingForExternalCompletion
                        ? FlowFieldBuildOperationState.Waiting
                        : IsBuilding
                            ? FlowFieldBuildOperationState.Running
                            : _committed != null
                                ? FlowFieldBuildOperationState.Completed
                                : FlowFieldBuildOperationState.Pending;

        bool IFlowFieldBuildOperation.IsWaitingForExternalCompletion
            => IsWaitingForExternalCompletion;

        bool IFlowFieldBuildOperation.IsBuilding
            => IsBuilding;

        float IFlowFieldBuildOperation.Progress
            => BuildProgress;

        Exception IFlowFieldBuildOperation.Failure
            => _fault;

        void IFlowFieldBuildOperation.PumpInternal(in FlowFieldBuildBudget budget)
            => PumpInternal(in budget);

        void IFlowFieldBuildOperation.Cancel()
        {
            if (_disposed)
                return;
            _generation++;
            _build?.Dispose();
            _build = null;
            _buildReservationBytes = 0;
            _hasPendingRequest = false;
        }

        void IFlowFieldBuildOperation.DisposeOperation()
            => Dispose();

        private enum BuildPhase
        {
            Allocate,
            StaticData,
            Obstacles,
            Topology,
            Goal,
            Bfs,
            Directions,
            Escape,
            Modifiers,
            Complete,
        }

        private enum SafetyCellState : byte
        {
            Unknown = 0,
            Clear = 1,
            Blocked = 2,
        }

        private readonly struct ObstacleObservation : IEquatable<ObstacleObservation>
        {
            internal readonly Bounds Bounds;
            private readonly bool _enabled;
            private readonly bool _active;
            private readonly bool _trigger;
            private readonly int _layer;

            private ObstacleObservation(
                Bounds bounds,
                bool enabled,
                bool active,
                bool trigger,
                int layer)
            {
                Bounds = bounds;
                _enabled = enabled;
                _active = active;
                _trigger = trigger;
                _layer = layer;
            }

            internal static ObstacleObservation From(Collider collider)
                => new ObstacleObservation(
                    collider != null ? collider.bounds : default,
                    collider != null && collider.enabled,
                    collider != null && collider.gameObject.activeInHierarchy,
                    collider != null && collider.isTrigger,
                    collider != null ? collider.gameObject.layer : -1);

            public bool Equals(ObstacleObservation other)
                => _enabled == other._enabled
                    && _active == other._active
                    && _trigger == other._trigger
                    && _layer == other._layer
                    && (Bounds.center - other.Bounds.center).sqrMagnitude <= 0.00000001f
                    && (Bounds.size - other.Bounds.size).sqrMagnitude <= 0.00000001f;

            public override bool Equals(object obj)
                => obj is ObstacleObservation other && Equals(other);

            public override int GetHashCode() => 0;
        }

        private sealed class BuildContext
        {
            internal readonly FlowFieldVolumeRequest Request;
            internal readonly int Generation;
            internal readonly int SafetyEpoch;
            internal readonly int Count;
            internal bool[] Blocked;
            internal uint[] Topology;
            internal FlowFieldGoalFlags[] GoalFlags;
            internal int[] Next;
            internal Vector3[] Directions;
            internal float[] Speeds;
            internal Vector3[] FinalDirections;
            internal float[] FinalSpeeds;
            internal Vector3[] EscapeDirections;
            internal int[] Costs;
            internal int[] Queue;
            internal bool[] Influence;
            internal readonly FlowFieldModifierWorkItem[] Modifiers;
            internal BuildPhase Phase;
            internal int Cursor;
            internal int Head;
            internal int Tail;
            internal int GoalIndex = -1;
            internal int RequestedGoalIndex = -1;
            internal long NearestGoalDistance = long.MaxValue;
            internal int GoalStage;
            internal double GoalRadiusSqr;
            internal int EscapeStage;
            internal int EscapeHead;
            internal int EscapeTail;
            internal bool GpuAttempted;
            internal bool GpuPending;
            internal Exception GpuFailed;
            internal Unity.Collections.NativeArray<GpuFlowCell> GpuResult;
            internal bool GpuResultPending;
            internal int GpuValidationCursor;
            internal int GpuFallbackResetCursor;
            internal int AllocationStage;

            internal BuildContext(
                in FlowFieldVolumeRequest request,
                int generation,
                int safetyEpoch,
                FlowFieldModifierWorkItem[] modifiers)
            {
                Request = request;
                Generation = generation;
                SafetyEpoch = safetyEpoch;
                Count = request.Grid.CellCount;
                Modifiers = modifiers ?? Array.Empty<FlowFieldModifierWorkItem>();
                Phase = BuildPhase.Allocate;
            }

            internal void Dispose()
            {
                // Managed staging arrays are reclaimed by the runtime once no
                // build references them. The GPU readback NativeArray belongs
                // to FlowFieldComputeSolver and is intentionally not disposed
                // here; its callback storage must remain valid until the
                // solver consumes or releases it.
                GpuResult = default;
                GpuResultPending = false;
                GpuPending = false;
            }
        }

        private sealed class VolumeFieldData
        {
            internal readonly FlowFieldVolumeRequest Request;
            internal readonly FlowFieldGridSpace Grid;
            internal readonly bool[] Blocked;
            internal readonly uint[] Topology;
            internal readonly FlowFieldGoalFlags[] GoalFlags;
            internal readonly int GoalIndex;
            internal readonly int[] NextCells;
            internal readonly Vector3[] BaseDirections;
            internal readonly float[] BaseSpeeds;
            internal readonly Vector3[] FinalDirections;
            internal readonly float[] FinalSpeeds;
            internal readonly Vector3[] EscapeDirections;
            internal readonly long ResultId;

            internal VolumeFieldData(
                in FlowFieldVolumeRequest request,
                bool[] blocked,
                uint[] topology,
                FlowFieldGoalFlags[] goalFlags,
                int[] nextCells,
                Vector3[] baseDirections,
                float[] baseSpeeds,
                Vector3[] finalDirections,
                float[] finalSpeeds,
                Vector3[] escapeDirections,
                int goalIndex,
                long resultId)
            {
                Request = request;
                Grid = request.Grid;
                // The solver/StaticBake payload has already resolved and
                // validated the goal. Do not infer it again from an Anchor;
                // an accidental non-goal anchor must never change the
                // published goal identity.
                GoalIndex = request.HasGoal ? goalIndex : -1;
                Blocked = blocked;
                Topology = topology;
                GoalFlags = goalFlags;
                NextCells = nextCells;
                BaseDirections = baseDirections;
                BaseSpeeds = baseSpeeds;
                FinalDirections = finalDirections;
                FinalSpeeds = finalSpeeds;
                EscapeDirections = escapeDirections;
                ResultId = resultId;
            }
        }
    }

}
