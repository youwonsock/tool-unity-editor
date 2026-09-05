using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Unity-facing façade for the shared FlowFieldSession. The component
    /// owns serialized authoring settings and Unity lifecycle callbacks; all
    /// calculation state, versions, staging/committed fields and backend
    /// lifetime live in the common Session.
    /// </summary>
    [DefaultExecutionOrder(-300)]
    public partial class FlowFieldManager : MonoBehaviour, IFlowFieldProvider, IFlowFieldController
    {
        private const float MIN_REFRESH_RATE = 0.05f;
        private const float VALUE_EPSILON = 0.0001f;
        private const int DefaultMaxGpuWaves = 1024;

        [Header("Surface Bake")]
        [SerializeField, Tooltip("Manager 위치 기준 월드 축 정렬 Bake 영역입니다. XZ는 Grid, Y는 Ground Ray 범위입니다.")]
        private Bounds _bakeBoundsLocal = new Bounds(
            new Vector3(20f, 0f, 20f),
            new Vector3(40f, 10f, 40f));
        [SerializeField] private float _cellSize = 0.5f;
        [SerializeField] private LayerMask _groundBakeLayer = Physics.DefaultRaycastLayers;
        [SerializeField] private float _maxSurfaceSlope = 45f;
        [SerializeField] private float _maxStepHeight = 0.5f;
        [SerializeField, Tooltip("정적 모드에서 사용할 전체 Flow Field Snapshot입니다.")] private FlowFieldStaticBakeData _staticBakeData;

        [Header("GPU Frontier BFS")]
        [SerializeField] private ComputeShader _frontierComputeShader;
        [SerializeField, Min(64), Tooltip("GPU batch wave 상한입니다. Cell Count보다 큰 값은 Cell Count로 제한됩니다.")]
        private int _maxGpuWaves = DefaultMaxGpuWaves;

        [Header("Obstacles")]
        [SerializeField] private LayerMask _obstacleLayer;
        [SerializeField] private float _obstacleCheckHeight = 2f;
        [SerializeField] private float _obstacleCheckCenterOffset = 1f;
        [SerializeField, Tooltip("셀 영역 밖으로 장애물 판정을 확장하는 XZ 거리입니다.")]
        private float _obstacleClearance;
        [SerializeField, Tooltip("ON이면 obstacle layer 전수 스윕(미등록 보정). OFF면 Static bake + RegisterDynamicObstacle만 사용.")]
        private bool _enableUnregisteredObstacleSweep;
        [SerializeField] private float _refreshRate = 0.2f;

        [Header("Bake Mode")]
        [SerializeField, Tooltip("RuntimeDynamic은 런타임 Surface/장애물을 다시 계산하고, StaticBaked는 Editor에서 저장한 base field를 사용합니다.")]
        private FlowFieldBakeMode _bakeMode = FlowFieldBakeMode.RuntimeDynamic;

        [Header("Default Flow")]
        [SerializeField] private Vector3 _defaultFlowDirection = Vector3.forward;

        [Header("Goal")]
        [SerializeField] private Transform _goalTransform;
        [SerializeField, Tooltip("0은 Global hybrid Goal, 양수는 XYZ 구 형태의 Ranged Goal입니다.")]
        private float _goalInfluenceRadius;

        [Header("Editor Gizmos")]
        [SerializeField, Tooltip("Bake 표면, Obstacle, Goal, Modifier 영향 셀과 최종 3D 벡터를 표시합니다.")]
        private bool _showField;

        private readonly FlowFieldSession _session = new FlowFieldSession();
        private float _refreshTimer;
        private bool _hasExplicitGoal;
        private Vector3 _explicitGoalWorld;
        private bool _requiresActivationRebuild;
        private bool _callbacksAttached;

        public bool IsInitialized => _session.IsInitialized;
        public FlowFieldRuntimeState State => _session.RuntimeState;
        public bool IsFaulted => _session.IsFaulted;
        public bool IsReady => _session.IsReady;
        public bool IsRebuilding => _session.IsRebuilding;
        public string LastError => _session.LastError;
        public FlowFieldBakeMode BakeMode => IsInitialized ? _session.BakeMode : _bakeMode;
        public int Revision => _session.Revision;
        public event Action FieldChanged;
        public event Action<FlowFieldRuntimeState> StateChanged;

        internal FlowFieldSession Session => _session;
        internal FlowFieldBakeMode CurrentBakeMode => BakeMode;
        internal FlowFieldStaticBakeData StaticBakeData => _staticBakeData;
        internal Bounds BakeBoundsLocal => _bakeBoundsLocal;
        internal float CellSize => _cellSize;
        internal int MaxGpuWaves => _maxGpuWaves;
        internal ComputeShader FrontierComputeShader => _frontierComputeShader;
        internal LayerMask ObstacleLayer => _obstacleLayer;
        internal float ObstacleCheckHeight => _obstacleCheckHeight;
        internal float ObstacleCheckCenterOffset => _obstacleCheckCenterOffset;
        internal float ObstacleClearance => _obstacleClearance;
        internal Vector3 DefaultFlowDirection => _defaultFlowDirection;
        internal bool EnableUnregisteredObstacleSweep => _enableUnregisteredObstacleSweep;
        internal bool ShowField => _showField;

        internal FlowFieldGoalResolution ResolveConfiguredGoal(FlowFieldGridSpace grid)
            => FlowFieldGoalPipeline.Resolve(
                grid,
                _goalTransform,
                _hasExplicitGoal,
                _explicitGoalWorld,
                _goalInfluenceRadius);

        internal Vector3 ConfiguredGoalWorld
            => _goalTransform != null ? _goalTransform.position : _explicitGoalWorld;

        internal bool HasConfiguredGoal
            => _goalTransform != null || _hasExplicitGoal;

        internal float ConfiguredGoalInfluenceRadius => _goalInfluenceRadius;

        internal FlowFieldStaticBakeSnapshot CreateStaticBakeSnapshot(
            in FlowFieldSurfaceBakeSettings settings)
        {
            if (_staticBakeData == null)
                throw new InvalidOperationException("StaticBaked mode requires a FlowFieldStaticBakeData asset.");
            return _staticBakeData.CreateSnapshot(
                settings,
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance);
        }

        private void Reset()
        {
            _bakeBoundsLocal = FlowFieldBakeBoundsUtility.DefaultLocalBounds;
            _cellSize = 0.5f;
            _groundBakeLayer = Physics.DefaultRaycastLayers;
            _maxGpuWaves = DefaultMaxGpuWaves;
            _bakeMode = FlowFieldBakeMode.RuntimeDynamic;
            _enableUnregisteredObstacleSweep = false;
        }

        private void Awake()
        {
            if (Application.isPlaying)
                Init();
        }

        private void OnEnable()
        {
            _refreshTimer = 0f;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
            if (!Application.isPlaying)
                return;

            if (_requiresActivationRebuild && _session.IsInitialized)
            {
                _requiresActivationRebuild = false;
                if (_session.IsFaulted)
                {
                    _session.RetryFault();
                    AttachSessionCallbacks();
                    ReattachActiveModifiers();
                }
                else
                {
                    InitializeSession();
                }
                RequestRebuild();
            }
        }

        private void OnValidate()
        {
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
            if (!Application.isPlaying || !_session.IsInitialized)
                return;

            // OnValidate fires for every serialized property edit. Compare a
            // normalized request with the last submitted one so a default
            // direction/max-wave edit only rebuilds the final field, while a
            // real Surface/obstacle/Goal change invalidates the base build.
            // StaticBaked requests intentionally contain no runtime Goal or
            // dynamic-obstacle input, making those edits true no-ops.
            if (!TryCreateCurrentSessionRequest(out FlowFieldSessionRequest current)
                || !_session.HasSubmittedRequest)
            {
                if (!_session.ConfigurationStale)
                    _session.MarkConfigurationStale();
                return;
            }

            // Keep the latest final-only scalar visible to a solve callback
            // even when the inspector edit lands between GPU dispatch and
            // the next RequestRebuild scheduling tick.
            _session.ObserveDefaultDirection(current.DefaultDirection);

            if (!_session.HasSameBaseInputs(current))
            {
                FlowFieldDirtyFlags changedFlags = _session.GetInputDirtyFlags(current);
                // Goal and obstacle scalar edits are base-field changes, but
                // they can reuse the already-baked Surface. Only a real Grid
                // or source/signature change makes the current Surface stale
                // and requires an explicit full rebuild.
                if (BakeMode == FlowFieldBakeMode.StaticBaked
                    || (changedFlags & FlowFieldDirtyFlags.Grid) != 0)
                {
                    if (!_session.ConfigurationStale)
                        _session.MarkConfigurationStale();
                }
                else if (changedFlags != FlowFieldDirtyFlags.None)
                {
                    bool hasObstacleChange = (changedFlags
                        & (FlowFieldDirtyFlags.StaticObstacles
                            | FlowFieldDirtyFlags.DynamicObstacles
                            | FlowFieldDirtyFlags.Escape)) != 0;
                    bool hasGoalChange = (changedFlags & FlowFieldDirtyFlags.Goal) != 0;
                    bool hasGridChange = (changedFlags & FlowFieldDirtyFlags.Grid) != 0;
                    _session.MarkDirty(
                        changedFlags,
                        FlowFieldCellRect.Invalid,
                        FlowFieldCellRect.Invalid,
                        baseChange: true,
                        deferBaseVersion: hasObstacleChange && !hasGridChange,
                        deferGoalVersion: hasGoalChange && !hasGridChange);
                }
                return;
            }

            if (_session.HasSameFinalInputs(current))
                return;

            FlowFieldDirtyFlags finalFlags = FlowFieldDirtyFlags.FinalRegion;
            try
            {
                Vector3 normalized = FlowFieldVectorUtility.NormalizeDefaultDirection(_defaultFlowDirection);
                if ((_session.ResolvedDefaultDirection - normalized).sqrMagnitude > VALUE_EPSILON * VALUE_EPSILON)
                    finalFlags |= FlowFieldDirtyFlags.DefaultDirection;
            }
            catch
            {
                if (!_session.ConfigurationStale)
                    _session.MarkConfigurationStale();
                return;
            }

            if ((_session.DirtyFlags & finalFlags) != finalFlags)
                _session.MarkDirty(finalFlags, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid, baseChange: false);
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || !_session.IsInitialized || _session.IsFaulted)
                return;
            if (_session.ConfigurationStale)
                return;

            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer > 0f)
                return;
            _refreshTimer = Mathf.Max(MIN_REFRESH_RATE, _refreshRate);

            if (BakeMode == FlowFieldBakeMode.RuntimeDynamic)
            {
                DetectGridTransformChange();
                DetectGoalChange();
                bool obstacleObserved = _session.DetectObstacleTransformsChanged();
                bool probePending = _session.HasPendingObstacleProbe;
                if (!_session.IsRebuilding
                    && (_enableUnregisteredObstacleSweep || obstacleObserved || probePending))
                {
                    if (TryCreateCurrentSessionRequest(out FlowFieldSessionRequest probeRequest))
                    {
                        _session.ProbeObstacleChanges(probeRequest);
                    }
                }
                else if (_session.IsRebuilding && obstacleObserved)
                {
                    // Transform snapshots are updated immediately, but the
                    // physics probe waits until the in-flight BFS callback
                    // has returned. This keeps at most one probe and one BFS
                    // active while preserving the previous/next dirty bounds.
                    _session.RecordObstacleObservation(_session.DirtyObstacleRegion);
                }
            }

            try
            {
                _session.DetectModifierChanges();
            }
            catch (Exception exception)
            {
                // Invalid live Modifier configuration is a single Session
                // fault. Once Faulted, LateUpdate stops polling and callers
                // can explicitly retry through RequestRebuild().
                _session.ReportFault(exception);
            }
        }

        private void FixedUpdate()
        {
            if (!Application.isPlaying || !_session.IsInitialized || _session.IsFaulted)
                return;
            if (_session.ConfigurationStale || _session.IsRebuilding)
                return;
            if (_session.HasPendingRequest)
                RequestRebuild();
        }

        private void OnDisable()
        {
            if (!Application.isPlaying)
                return;
            _requiresActivationRebuild = true;
            _session.Suspend();
            _refreshTimer = 0f;
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            ReleaseEditorPreview();
#endif
            DetachSessionCallbacks();
            _session.DisposePermanently();
        }

        public void Init()
        {
            if (_session.Lifecycle == FlowFieldSessionLifecycle.Active
                || _session.Lifecycle == FlowFieldSessionLifecycle.Building)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is already initialized.");
            if (_session.IsFaulted)
            {
                ValidateConfiguration(validateStaticGoal: false);
                _session.RetryFault();
                AttachSessionCallbacks();
                RequestRebuild();
                return;
            }

            ValidateConfiguration();
            InitializeSession();
            _requiresActivationRebuild = false;
            RequestRebuild();
        }

        private void InitializeSession()
        {
            FlowFieldSessionSourceKind sourceKind = BakeMode == FlowFieldBakeMode.StaticBaked
                ? FlowFieldSessionSourceKind.StaticSnapshot
                : FlowFieldSessionSourceKind.SceneBuild;
            FlowFieldBfsBackendPolicy policy = Application.isPlaying
                ? FlowFieldBfsBackendPolicy.PreferGpu
                : FlowFieldBfsBackendPolicy.ManagedOnly;
            ComputeShader shader = _frontierComputeShader;
            AttachSessionCallbacks();
            // Attach before Initialize so consumers observe the initial
            // Uninitialized → Building transition as well as later Ready and
            // Faulted transitions. Reattaching Modifiers remains after the
            // Session has created its registry.
            _session.Initialize(_bakeMode, sourceKind, policy, shader);
            ReattachActiveModifiers();
        }

        public void RequestRebuild()
        {
            bool retryingFault = _session.IsFaulted;
            if (retryingFault)
                _session.RetryFault();
            try
            {
                ThrowIfLifecycleUnavailable();
                // Static navigation is immutable for the lifetime of a
                // Session. A Goal Transform may move for presentation or
                // gameplay reasons; runtime motion is intentionally ignored
                // by the baked base field. Initial Init still validates the
                // baked Goal signature, while later rebuilds validate only the
                // loaded snapshot settings and recompose it.
                ValidateConfiguration(validateStaticGoal: false);

                FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
                int maxWaves = Mathf.Min(settings.Grid.CellCount, Mathf.Max(64, _maxGpuWaves));
                bool accepted;
                if (BakeMode == FlowFieldBakeMode.StaticBaked)
                {
                    FlowFieldStaticBakeSnapshot snapshot = CreateStaticBakeSnapshot(settings);
                    accepted = _session.Submit(
                        FlowFieldSessionRequest.ForStaticSnapshot(
                            settings,
                            snapshot,
                            _obstacleLayer,
                            _obstacleCheckHeight,
                            _obstacleCheckCenterOffset,
                            _obstacleClearance,
                            _defaultFlowDirection,
                            FlowFieldDirtyFlags.None,
                            maxWaves,
                            $"{name}_RuntimeSurface"));
                }
                else
                {
                    FlowFieldGoalResolution goal = ResolveConfiguredGoal(settings.Grid);
                    accepted = _session.Submit(
                        FlowFieldSessionRequest.ForSceneBuild(
                            settings,
                            _obstacleLayer,
                            _obstacleCheckHeight,
                            _obstacleCheckCenterOffset,
                            _obstacleClearance,
                            _enableUnregisteredObstacleSweep,
                            goal,
                            _defaultFlowDirection,
                            FlowFieldDirtyFlags.None,
                            _session.DirtyFinalRegion,
                            _session.DirtyObstacleRegion,
                            maxWaves,
                            $"{name}_RuntimeSurface"));
                }

                // A backend can fail synchronously (for example a RequireGpu
                // probe or an injected test backend). Treat that exactly like
                // a rejected request even when the backend reported that it
                // accepted a callback before transitioning the Session.
                if (_session.IsFaulted)
                    throw new InvalidOperationException($"{nameof(FlowFieldManager)} rebuild failed.", _session.Fault);
            }
            catch (Exception exception)
            {
                // If a retry fails during validation or request submission,
                // retain a diagnosable Faulted state instead of leaving an
                // initialized but unusable Session with no LastError.
                if (retryingFault && !_session.IsFaulted)
                    _session.ReportFault(exception);
                throw;
            }
        }

        public void Release()
        {
            if (_session.Lifecycle == FlowFieldSessionLifecycle.Uninitialized
                || _session.Lifecycle == FlowFieldSessionLifecycle.Released)
                return;
            DetachActiveModifiers();
            _session.Release();
            _refreshTimer = 0f;
        }

        private void ThrowIfLifecycleUnavailable()
        {
            if (_session.IsFaulted)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is faulted.", _session.Fault);
            if (!_session.IsInitialized)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is not initialized.");
        }

        private void ThrowIfAvailableForInput()
        {
            ThrowIfLifecycleUnavailable();
            if (_session.ConfigurationStale)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} configuration is stale. Call RequestRebuild() before use.");
        }

        private void ThrowIfInputAllowedForMode()
        {
            ThrowIfLifecycleUnavailable();
            // StaticBaked ignores runtime Goal/obstacle input. It must remain
            // a valid no-op even while an unrelated serialized setting is
            // awaiting an explicit rebuild.
            if (BakeMode != FlowFieldBakeMode.StaticBaked && _session.ConfigurationStale)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} configuration is stale. Call RequestRebuild() before use.");
        }

        private void ValidateConfiguration(bool validateStaticGoal = true)
        {
            if (!FlowFieldGridSpace.IsFinite(_cellSize) || _cellSize < FlowFieldBakeBoundsUtility.MinCellSize)
                throw new ArgumentOutOfRangeException(nameof(_cellSize), _cellSize, "Cell Size must be finite and positive.");
            if (!FlowFieldGridSpace.IsFinite(_bakeBoundsLocal.center)
                || !FlowFieldGridSpace.IsFinite(_bakeBoundsLocal.size)
                || _bakeBoundsLocal.size.x <= 0f
                || _bakeBoundsLocal.size.y <= 0f
                || _bakeBoundsLocal.size.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(_bakeBoundsLocal), "Bake Bounds must be finite and positive.");
            if (!FlowFieldGridSpace.IsFinite(_defaultFlowDirection)
                || _defaultFlowDirection.sqrMagnitude <= VALUE_EPSILON)
                throw new ArgumentOutOfRangeException(nameof(_defaultFlowDirection), "Default Flow Direction must be finite and non-zero.");
            if (Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f
                || (transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
                throw new ArgumentException("FlowField Manager must use an unrotated transform with unit scale.", nameof(transform));
            if (_groundBakeLayer.value == 0)
                throw new ArgumentException("Ground Bake LayerMask must contain at least one layer.", nameof(_groundBakeLayer));
            if (_obstacleLayer.value == 0)
                throw new ArgumentException("Obstacle LayerMask must contain at least one layer.", nameof(_obstacleLayer));
            if (!FlowFieldGridSpace.IsFinite(_maxSurfaceSlope) || _maxSurfaceSlope < 0f || _maxSurfaceSlope > 89f)
                throw new ArgumentOutOfRangeException(nameof(_maxSurfaceSlope));
            if (!FlowFieldGridSpace.IsFinite(_maxStepHeight) || _maxStepHeight < 0f)
                throw new ArgumentOutOfRangeException(nameof(_maxStepHeight));
            if (!FlowFieldGridSpace.IsFinite(_obstacleCheckHeight) || _obstacleCheckHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(_obstacleCheckHeight));
            if (!FlowFieldGridSpace.IsFinite(_obstacleCheckCenterOffset))
                throw new ArgumentOutOfRangeException(nameof(_obstacleCheckCenterOffset));
            if (!FlowFieldGridSpace.IsFinite(_obstacleClearance) || _obstacleClearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(_obstacleClearance));
            if (!FlowFieldGridSpace.IsFinite(_refreshRate) || _refreshRate < MIN_REFRESH_RATE)
                throw new ArgumentOutOfRangeException(nameof(_refreshRate));
            if (_maxGpuWaves < 64)
                throw new ArgumentOutOfRangeException(nameof(_maxGpuWaves), "GPU wave limit must be at least 64.");
            if (!Enum.IsDefined(typeof(FlowFieldBakeMode), _bakeMode))
                throw new ArgumentOutOfRangeException(nameof(_bakeMode));
            if (_session.IsInitialized && BakeMode != _bakeMode)
                throw new InvalidOperationException("FlowField Bake Mode cannot change during an active Init session. Release and Init again.");
            if (!FlowFieldGridSpace.IsFinite(_goalInfluenceRadius) || _goalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(_goalInfluenceRadius));

            if (!TryGetBakeLayout(out _, out FlowFieldGridSpace grid) || !grid.IsValid)
                throw new ArgumentException("Bake Bounds and Cell Size do not produce a valid grid.", nameof(_bakeBoundsLocal));

            if (BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (_staticBakeData == null)
                    throw new InvalidOperationException("StaticBaked mode requires a FlowFieldStaticBakeData asset.");
                if (!_staticBakeData.MatchesSurface(CreateSurfaceBakeSettings(), out string surfaceMismatch))
                    throw new InvalidOperationException(surfaceMismatch);
                if (!_staticBakeData.MatchesObstacles(
                        _obstacleLayer,
                        _obstacleCheckHeight,
                        _obstacleCheckCenterOffset,
                        _obstacleClearance,
                        out string obstacleMismatch))
                    throw new InvalidOperationException(obstacleMismatch);
                if (validateStaticGoal
                    && !_staticBakeData.MatchesGoal(
                            HasConfiguredGoal,
                            ConfiguredGoalWorld,
                            _goalInfluenceRadius))
                {
                    throw new InvalidOperationException(
                        "Static Flow Bake Goal이 현재 Manager 설정과 다릅니다. ReBake가 필요합니다.");
                }
            }
        }

        private void DetectGridTransformChange()
        {
            if (!_session.StagingGrid.IsValid)
                return;
            FlowFieldGridSpace current = CreateGridSpace();
            if (_session.ObserveGrid(current))
                _session.MarkDirty(FlowFieldDirtyFlags.Grid, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid, true);
        }

        private void DetectGoalChange()
        {
            if (!_session.StagingSurfaceReady || !_session.StagingGrid.IsValid)
                return;
            if ((_session.DirtyFlags & FlowFieldDirtyFlags.Goal) != 0)
                return;
            FlowFieldGoalChangeStatus status = _session.GoalTracker.DetectChange(
                _session.StagingGrid,
                _session.StagingSurfaceReady,
                _goalTransform,
                _hasExplicitGoal,
                _explicitGoalWorld,
                _goalInfluenceRadius);
            if (status == FlowFieldGoalChangeStatus.Invalid)
                throw new InvalidOperationException("Active Goal became invalid.");
            if (status == FlowFieldGoalChangeStatus.Changed)
            {
                _session.MarkDirty(
                    FlowFieldDirtyFlags.Goal,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid,
                    baseChange: true,
                    deferGoalVersion: true);
#if UNITY_EDITOR
                InvalidateEditorPreview();
#endif
            }
        }

        private void MarkGoalDirty()
        {
            _session.GoalTracker.ResetWarning();
            _session.MarkDirty(
                FlowFieldDirtyFlags.Goal,
                FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid,
                baseChange: true,
                deferGoalVersion: true);
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        private FlowFieldGridSpace CreateGridSpace()
        {
            if (!TryGetBakeLayout(out _, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Bake Bounds and Cell Size do not produce a valid grid.");
            return grid;
        }

        #region Provider and Controller API

        public FlowFieldSample Sample(Vector3 worldPosition)
        {
            ThrowIfAvailableForInput();
            return _session.Sample(worldPosition);
        }

        public bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
            => _session.TrySample(worldPosition, out sample);

        public FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            ThrowIfAvailableForInput();
            return _session.ClampPositionToGrid(worldPosition);
        }

        public void RegisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAllowedForMode();
            if (collider == null)
                throw new ArgumentNullException(nameof(collider));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            FlowFieldCellRect dirty = _session.StagingGrid.IsValid
                ? FlowFieldCellRect.FromBounds(_session.StagingGrid, collider.bounds)
                : FlowFieldCellRect.Invalid;
            if (!_session.RegisterObstacle(collider, dirty))
                throw new InvalidOperationException("Dynamic obstacle registration failed.");
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        public void UnregisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAllowedForMode();
            if (collider == null)
                throw new ArgumentNullException(nameof(collider));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            Bounds bounds = collider.bounds;
            FlowFieldCellRect dirty = _session.StagingGrid.IsValid
                ? FlowFieldCellRect.FromBounds(_session.StagingGrid, bounds)
                : FlowFieldCellRect.Invalid;
            if (!_session.UnregisterObstacle(collider, dirty))
                throw new InvalidOperationException("Dynamic obstacle is not registered.");
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        public void NotifyObstacleRegionDirty(Bounds worldBounds)
        {
            ThrowIfInputAllowedForMode();
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center)
                || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                throw new ArgumentOutOfRangeException(nameof(worldBounds));
            if (!_session.StagingGrid.IsValid)
                throw new InvalidOperationException("FlowField grid is not initialized.");
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            _session.MarkObstacleDirty(FlowFieldCellRect.FromBounds(_session.StagingGrid, worldBounds));
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        public void SetGoalPosition(Vector3 worldPosition)
            => SetGoalPosition(worldPosition, 0f);

        public void SetGoalPosition(Vector3 worldPosition, float influenceRadius)
        {
            ThrowIfInputAllowedForMode();
            ValidateGoal(worldPosition, influenceRadius);
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            if (_goalTransform == null
                && _hasExplicitGoal
                && (_explicitGoalWorld - worldPosition).sqrMagnitude <= VALUE_EPSILON * VALUE_EPSILON
                && Mathf.Abs(_goalInfluenceRadius - influenceRadius) <= VALUE_EPSILON)
                return;
            _goalTransform = null;
            _hasExplicitGoal = true;
            _explicitGoalWorld = worldPosition;
            _goalInfluenceRadius = influenceRadius;
            MarkGoalDirty();
        }

        public void SetGoalTarget(Transform target)
            => SetGoalTarget(target, 0f);

        public void SetGoalTarget(Transform target, float influenceRadius)
        {
            ThrowIfInputAllowedForMode();
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            ValidateGoal(target.position, influenceRadius);
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            if (_goalTransform == target
                && !_hasExplicitGoal
                && Mathf.Abs(_goalInfluenceRadius - influenceRadius) <= VALUE_EPSILON)
                return;
            _goalTransform = target;
            _hasExplicitGoal = false;
            _goalInfluenceRadius = influenceRadius;
            MarkGoalDirty();
        }

        public void ClearGoal()
        {
            ThrowIfInputAllowedForMode();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            if (_goalTransform == null && !_hasExplicitGoal)
                return;
            _goalTransform = null;
            _hasExplicitGoal = false;
            MarkGoalDirty();
        }

        private static void ValidateGoal(Vector3 worldPosition, float influenceRadius)
        {
            if (!FlowFieldGoalPipeline.IsFiniteWorldXZ(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));
            if (!FlowFieldGridSpace.IsFinite(influenceRadius) || influenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(influenceRadius));
        }

        #endregion

        public void NotifySurfaceDirty()
        {
            ThrowIfInputAllowedForMode();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            _session.MarkDirty(
                FlowFieldDirtyFlags.Grid,
                FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid,
                baseChange: true,
                deferSurfaceVersion: true);
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal void MarkConfigurationStale()
        {
            if (!_session.IsInitialized)
                return;
            _session.MarkConfigurationStale();
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        #region Bake Integration

        internal FlowFieldSurfaceBakeSettings CreateSurfaceBakeSettings()
        {
            if (!TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Bake Bounds and Cell Size do not produce a valid grid.");
            return new FlowFieldSurfaceBakeSettings(
                grid,
                worldBounds,
                _groundBakeLayer,
                _maxSurfaceSlope,
                _maxStepHeight);
        }

        internal bool TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
            => FlowFieldBakeBoundsUtility.TryCreateWorldLayout(
                transform.position,
                _bakeBoundsLocal,
                _cellSize,
                out worldBounds,
                out grid);

        private bool TryCreateCurrentSessionRequest(out FlowFieldSessionRequest request)
        {
            request = default;
            try
            {
                FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
                int maxWaves = Mathf.Min(settings.Grid.CellCount, Mathf.Max(64, _maxGpuWaves));
                if (BakeMode == FlowFieldBakeMode.StaticBaked)
                {
                    FlowFieldStaticBakeSnapshot snapshot = CreateStaticBakeSnapshot(settings);
                    request = FlowFieldSessionRequest.ForStaticSnapshot(
                        settings,
                        snapshot,
                        _obstacleLayer,
                        _obstacleCheckHeight,
                        _obstacleCheckCenterOffset,
                        _obstacleClearance,
                        _defaultFlowDirection,
                        FlowFieldDirtyFlags.None,
                        maxWaves,
                        $"{name}_RuntimeSurface");
                }
                else
                {
                    request = FlowFieldSessionRequest.ForSceneBuild(
                        settings,
                        _obstacleLayer,
                        _obstacleCheckHeight,
                        _obstacleCheckCenterOffset,
                        _obstacleClearance,
                        _enableUnregisteredObstacleSweep,
                        ResolveConfiguredGoal(settings.Grid),
                        _defaultFlowDirection,
                        FlowFieldDirtyFlags.None,
                        FlowFieldCellRect.Invalid,
                        FlowFieldCellRect.Invalid,
                        maxWaves,
                        $"{name}_RuntimeSurface");
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        internal void SetBakeBoundsLocal(Bounds localBounds)
        {
            Bounds snapped = FlowFieldBakeBoundsUtility.SnapCenterAnchored(localBounds, _cellSize);
            if (FlowFieldBakeBoundsUtility.Approximately(_bakeBoundsLocal, snapped))
                return;
            _bakeBoundsLocal = snapped;
            if (_session.IsInitialized)
            {
                if (TryGetBakeLayout(out _, out FlowFieldGridSpace current))
                    _session.ObserveGrid(current);
                _session.MarkDirty(
                    FlowFieldDirtyFlags.Grid,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid,
                    baseChange: true,
                    deferSurfaceVersion: true);
            }
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal bool TryValidateSurfaceBake(out string reason)
            => TryValidateSurfaceBake(out reason, includeStaticGoal: true);

        internal bool TryValidateSurfaceBake(out string reason, bool includeStaticGoal)
        {
            reason = string.Empty;
            if (!TryGetBakeLayout(out _, out _))
            {
                reason = "Bake Bounds 또는 Cell Size가 유효하지 않습니다.";
                return false;
            }

            if (BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (_staticBakeData == null)
                {
                    reason = "StaticBaked 모드에는 FlowFieldStaticBakeData가 필요합니다.";
                    return false;
                }
                if (!_staticBakeData.MatchesSurface(CreateSurfaceBakeSettings(), out reason))
                    return false;
                if (!_staticBakeData.MatchesObstacles(
                        _obstacleLayer,
                        _obstacleCheckHeight,
                        _obstacleCheckCenterOffset,
                        _obstacleClearance,
                        out reason))
                    return false;
                if (includeStaticGoal
                    && !_staticBakeData.MatchesGoal(
                        HasConfiguredGoal,
                        ConfiguredGoalWorld,
                        _goalInfluenceRadius))
                {
                    reason = "Static Flow Bake Goal이 현재 Manager 설정과 다릅니다. ReBake가 필요합니다.";
                    return false;
                }
                return true;
            }

            return CreateSurfaceBakeSettings().IsValid;
        }

        internal void AssignStaticBakeData(FlowFieldStaticBakeData bakeData)
        {
            if (_staticBakeData == bakeData)
                return;
            _staticBakeData = bakeData;
            if (_session.IsInitialized)
                _session.MarkConfigurationStale();
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal void NotifySurfaceBakeChanged()
        {
            if (_session.IsInitialized)
                _session.MarkDirty(FlowFieldDirtyFlags.All, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid, true);
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        #endregion

        private void AttachSessionCallbacks()
        {
            if (_callbacksAttached)
                return;
            _session.FieldCommitted += OnSessionFieldCommitted;
            _session.Failed += OnSessionFailed;
            _session.StateChanged += OnSessionStateChanged;
            _callbacksAttached = true;
        }

        private void DetachSessionCallbacks()
        {
            if (!_callbacksAttached)
                return;
            _session.FieldCommitted -= OnSessionFieldCommitted;
            _session.Failed -= OnSessionFailed;
            _session.StateChanged -= OnSessionStateChanged;
            _callbacksAttached = false;
        }

        private void OnSessionFieldCommitted(bool changed)
        {
            if (changed)
                FieldChanged?.Invoke();
        }

        private void OnSessionFailed(Exception exception)
        {
            if (exception != null)
                Debug.LogError($"[{nameof(FlowFieldManager)}] FlowField build failed: {exception.Message}", this);
        }

        private void OnSessionStateChanged(FlowFieldRuntimeState state)
            => StateChanged?.Invoke(state);

        private void ReattachActiveModifiers()
        {
            FlowFieldVectorModifierVolume[] volumes = Resources.FindObjectsOfTypeAll<FlowFieldVectorModifierVolume>();
            for (int i = 0; i < volumes.Length; i++)
            {
                FlowFieldVectorModifierVolume volume = volumes[i];
                if (volume == null
                    || volume.FlowFieldManager != this
                    || !volume.gameObject.scene.IsValid())
                    continue;

                try
                {
                    volume.ReattachToConfiguredManager();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, volume);
                }
            }
        }

        private void DetachActiveModifiers()
        {
            FlowFieldVectorModifierVolume[] volumes = Resources.FindObjectsOfTypeAll<FlowFieldVectorModifierVolume>();
            for (int i = 0; i < volumes.Length; i++)
            {
                FlowFieldVectorModifierVolume volume = volumes[i];
                if (volume == null || volume.FlowFieldManager != this)
                    continue;

                volume.DetachFromFlowFieldSession(this);
            }
        }

    }
}
