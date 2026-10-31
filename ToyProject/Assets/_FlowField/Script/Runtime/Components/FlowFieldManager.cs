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
    public partial class FlowFieldManager : MonoBehaviour, IFlowFieldController
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
        [SerializeField, Tooltip("Surface2D 정적 모드에서 사용할 Flow Field Snapshot입니다.")] private FlowFieldStaticBakeData _staticBakeData;
        [SerializeField, Tooltip("Volume3D 정적 모드에서 사용할 v6 Flow Field Snapshot입니다.")] private FlowFieldVolumeBakeData _volumeStaticBakeData;

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
        [SerializeField, Tooltip("Surface2D는 기존 바닥 Raycast, Volume3D는 전체 Bounds를 정육면체 셀로 채웁니다. Init 이후 변경하려면 Release가 필요합니다.")]
        private FlowFieldSpaceMode _spaceMode = FlowFieldSpaceMode.Surface2D;
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
        [SerializeField] private FlowFieldVolumeGizmoMode _volumeGizmoMode = FlowFieldVolumeGizmoMode.FullVolume;
        [SerializeField] private bool _showVolumeCells;
        [SerializeField] private bool _showVolumeVectors = true;
        [SerializeField] private FlowFieldVolumeSliceAxis _volumeGizmoSliceAxis = FlowFieldVolumeSliceAxis.Y;
        [SerializeField, Tooltip("-1이면 단면 중앙을 사용합니다.")] private int _volumeGizmoSlice = -1;

        private readonly FlowFieldSession _session = new FlowFieldSession();
        private readonly FlowFieldVolumeSession _volumeSession = new FlowFieldVolumeSession();
        private float _refreshTimer;
        private bool _hasExplicitGoal;
        private Vector3 _explicitGoalWorld;
        private bool _requiresActivationRebuild;
        private FlowFieldSessionBase _attachedSession;
        [NonSerialized] private int _bakeInputGeneration;
        [NonSerialized] private int _lastBakeInputHash;
        [NonSerialized] private bool _hasBakeInputHash;

        private FlowFieldSessionBase ActiveSession
        {
            get
            {
                // The serialized mode can be edited while a session is
                // active. Keep all Provider/Controller reads and commands
                // attached to the accepted session until Release; otherwise
                // an Inspector edit would make the façade query the other
                // mode's still-uninitialized session.
                if (_attachedSession != null && (_attachedSession.IsInitialized || _attachedSession.IsFaulted))
                    return _attachedSession;
                return _spaceMode == FlowFieldSpaceMode.Volume3D
                    ? (FlowFieldSessionBase)_volumeSession
                    : _session;
            }
        }

        public bool IsInitialized => ActiveSession.IsInitialized;
        public FlowFieldRuntimeState State => ActiveSession.State;
        public bool IsFaulted => ActiveSession.IsFaulted;
        public bool IsReady => ActiveSession.IsReady;
        public bool IsRebuilding => ActiveSession.IsRebuilding;
        public string LastError => ActiveSession.LastError;
        /// <summary>GPU 계산을 끄고 Managed로 전환한 이유입니다. 전환하지 않았으면 null입니다.</summary>
        public string GpuFallbackReason => ActiveSession.GpuFallbackReason;
        public FlowFieldBakeMode BakeMode => IsInitialized || IsFaulted
            ? ActiveSession.BakeMode
            : _bakeMode;
        // Once a session has accepted a mode, expose that accepted mode until
        // Release.  Inspector edits made while initialized are authoring
        // input only and must not make Provider queries describe a session
        // that is still running in the previous space.
        public FlowFieldSpaceMode SpaceMode => IsInitialized || IsFaulted
            ? ActiveSession.SpaceMode
            : _spaceMode;
        public int Revision => ActiveSession.Revision;
        public event Action FieldChanged;
        public event Action<FlowFieldRuntimeState> StateChanged;

        public bool TryGetFieldInfo(out FlowFieldFieldInfo info)
            => ActiveSession.TryGetFieldInfo(out info);

        internal FlowFieldVolumeSession VolumeSession => _volumeSession;
        internal FlowFieldStaticBakeData StaticBakeData => _staticBakeData;
        internal FlowFieldVolumeBakeData VolumeStaticBakeData => _volumeStaticBakeData;
        internal Bounds BakeBoundsLocal => _bakeBoundsLocal;
        internal float CellSize => _cellSize;
        internal int MaxGpuWaves => _maxGpuWaves;
        internal ComputeShader FrontierComputeShader => _frontierComputeShader;
        internal LayerMask ObstacleLayer => _obstacleLayer;
        internal float ObstacleCheckHeight => _obstacleCheckHeight;
        internal float ObstacleCheckCenterOffset => _obstacleCheckCenterOffset;
        internal float ObstacleClearance => _obstacleClearance;
        internal Vector3 DefaultFlowDirection => _defaultFlowDirection;
        internal int CaptureBakeInputGeneration()
        {
            int hash = ComputeBakeInputHash();
            if (!_hasBakeInputHash || hash != _lastBakeInputHash)
            {
                unchecked { _bakeInputGeneration++; }
                _lastBakeInputHash = hash;
                _hasBakeInputHash = true;
            }
            return _bakeInputGeneration;
        }
        internal FlowFieldVolumeGizmoMode VolumeGizmoMode => _volumeGizmoMode;
        internal bool ShowVolumeCells => _showVolumeCells;
        internal bool ShowVolumeVectors => _showVolumeVectors;
        internal FlowFieldVolumeSliceAxis VolumeGizmoSliceAxis => _volumeGizmoSliceAxis;
        internal int VolumeGizmoSlice => _volumeGizmoSlice;

#if UNITY_EDITOR
        internal bool TryGetVolumeVisualizationView(out FlowFieldReadView view)
            => _volumeSession.TryGetVisualizationView(out view);

        internal bool TryGetVolumeVisualizationSource(
            out FlowFieldVolumeVisualizationSource source)
            => _volumeSession.TryGetVisualizationSource(out source);
#endif

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
            _spaceMode = FlowFieldSpaceMode.Surface2D;
            _bakeMode = FlowFieldBakeMode.RuntimeDynamic;
            _volumeGizmoMode = FlowFieldVolumeGizmoMode.FullVolume;
            _showVolumeCells = false;
            _showVolumeVectors = true;
            _volumeGizmoSliceAxis = FlowFieldVolumeSliceAxis.Y;
            _volumeGizmoSlice = -1;
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
            FlowFieldEditorVisualizationBridge.Register(this);
#endif
            if (!Application.isPlaying || !_requiresActivationRebuild)
                return;

            FlowFieldSessionBase owner = ActiveSession;
            if (!owner.IsInitialized)
                return;
            int version = BeginLifecycleOperation();
            _requiresActivationRebuild = false;
            if (owner.SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                _volumeSession.Resume();
                if (!OwnsLifecycleOperation(owner, version))
                    return;
            }
            else if (!InitializeSession(version))
                return;
            RequestRebuild();
        }

        private void OnValidate()
        {
            CaptureBakeInputGeneration();
#if UNITY_EDITOR
            InvalidateEditorPreview();
            FlowFieldEditorVisualizationBridge.RequestRefresh(this);
#endif
            if (!Application.isPlaying || !IsInitialized || _spaceMode != SpaceMode)
                return;

            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                if (!_volumeSession.IsInitialized || _volumeSession.IsFaulted)
                    return;
                try
                {
                    if (!_volumeSession.IsRebuilding || _volumeSession.HasPendingRequest)
                        QueueVolumeRequest();
                }
                catch (Exception exception)
                {
                    _volumeSession.ReportFault(exception);
                }
                return;
            }

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
                _session.MarkDirty(finalFlags, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || !IsInitialized || IsFaulted)
                return;
            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                try
                {
                    _volumeSession.DetectUnregisteredObstacleChanges(_enableUnregisteredObstacleSweep);
                    _volumeSession.DetectModifierChanges();
                    QueueVolumeRequest();
                }
                catch (Exception exception)
                {
                    _volumeSession.ReportFault(exception);
                }
                return;
            }
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
            if (!Application.isPlaying || !IsInitialized || IsFaulted)
                return;
            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
                return;
            if (_session.RequiresExplicitRebuild || _session.IsRebuilding)
                return;
            if (_session.HasPendingRequest
                && TryCreateCurrentSessionRequest(out FlowFieldSessionRequest request))
            {
                _session.Submit(request);
                _session.Pump();
            }
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            FlowFieldEditorVisualizationBridge.Unregister(this);
#endif
            if (!Application.isPlaying)
                return;
            BeginLifecycleOperation();
            _requiresActivationRebuild = true;
            ActiveSession.Suspend();
            _refreshTimer = 0f;
        }

        private void OnDestroy()
        {
            BeginLifecycleOperation();
#if UNITY_EDITOR
            ReleaseEditorPreview();
            FlowFieldEditorVisualizationBridge.Unregister(this);
#endif
            DetachSessionCallbacks();
            _session.DisposePermanently();
            _volumeSession.Dispose();
        }

        public void Init()
        {
            if (IsInitialized && _spaceMode != SpaceMode)
                throw new InvalidOperationException("FlowField Space Mode cannot change during an active Init session. Release and Init again.");
            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                if (_volumeSession.IsFaulted)
                    throw new InvalidOperationException(
                        $"{nameof(FlowFieldManager)} is faulted. Call RequestRebuild() to retry.",
                        new InvalidOperationException(_volumeSession.LastError));
                if (_volumeSession.IsInitialized
                    && _volumeSession.State != FlowFieldRuntimeState.Released)
                    return;
                ValidateConfiguration();
                int version = BeginLifecycleOperation();
                // Attach before Initialize so consumers observe the complete
                // lifecycle, including the first Uninitialized → Suspended →
                // Ready transition and any immediate fault.
                AttachSessionCallbacks(_volumeSession);
                _volumeSession.Initialize(_bakeMode, _frontierComputeShader);
                if (!OwnsLifecycleOperation(_volumeSession, version))
                    return;
                _requiresActivationRebuild = !isActiveAndEnabled;
                if (_requiresActivationRebuild)
                    _volumeSession.Suspend();
                if (!OwnsLifecycleOperation(_volumeSession, version))
                    return;
                RequestRebuild();
                if (OwnsLifecycleOperation(_volumeSession, version))
                    ReattachActiveModifiers();
                return;
            }

            if (_session.Lifecycle == FlowFieldSessionLifecycle.Active
                || _session.Lifecycle == FlowFieldSessionLifecycle.Building
                || _session.Lifecycle == FlowFieldSessionLifecycle.Suspended)
                return;
            if (_session.IsFaulted)
                throw new InvalidOperationException(
                    $"{nameof(FlowFieldManager)} is faulted. Call RequestRebuild() to retry.",
                    _session.Fault);

            ValidateConfiguration();
            int surfaceVersion = BeginLifecycleOperation();
            if (!InitializeSession(surfaceVersion))
                return;
            _requiresActivationRebuild = !isActiveAndEnabled;
            if (_requiresActivationRebuild)
                _session.Suspend();
            if (OwnsLifecycleOperation(_session, surfaceVersion))
                RequestRebuild();
        }

        private bool InitializeSession(int version)
        {
            FlowFieldSessionSourceKind sourceKind = BakeMode == FlowFieldBakeMode.StaticBaked
                ? FlowFieldSessionSourceKind.StaticSnapshot
                : FlowFieldSessionSourceKind.SceneBuild;
            FlowFieldBfsBackendPolicy policy = Application.isPlaying
                ? FlowFieldBfsBackendPolicy.PreferGpu
                : FlowFieldBfsBackendPolicy.ManagedOnly;
            ComputeShader shader = _frontierComputeShader;
            AttachSessionCallbacks(_session);
            // Attach before Initialize so consumers observe the initial
            // Uninitialized → Building transition as well as later Ready and
            // Faulted transitions. Reattaching Modifiers remains after the
            // Session has created its registry.
            _session.Initialize(_bakeMode, sourceKind, policy, shader);
            if (!OwnsLifecycleOperation(_session, version))
                return false;
            ReattachActiveModifiers();
            return OwnsLifecycleOperation(_session, version);
        }

        public void RequestRebuild()
        {
            int version = _lifecycleVersion;
            FlowFieldSessionBase owner = ActiveSession;
            bool retryingFault = owner.IsFaulted;
            if (retryingFault)
            {
                owner.RetryFault();
                if (!OwnsLifecycleOperation(owner, version))
                    return;
            }
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
                if (owner.SpaceMode == FlowFieldSpaceMode.Volume3D)
                {
                    QueueVolumeRequest(force: true, requeryObstacles: true);
                    if (OwnsLifecycleOperation(_volumeSession, version))
                        _volumeSession.AttachRuntimeScheduler();
                }
                else
                {
                    SubmitSurfaceRebuild(version);
                }
            }
            catch (Exception exception)
            {
                // If a retry fails during validation or request submission,
                // retain a diagnosable Faulted state instead of leaving an
                // initialized but unusable Session with no LastError.
                if (retryingFault && OwnsLifecycleOperation(owner, version))
                    owner.ReportFault(exception);
                throw;
            }
        }

        private void SubmitSurfaceRebuild(int version)
        {
            FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
            _session.Submit(CreateSurfaceRequest(
                settings,
                FlowFieldDirtyFlags.None,
                _session.DirtyFinalRegion,
                _session.DirtyObstacleRegion));

            // A backend can fail synchronously (for example a RequireGpu
            // probe or an injected test backend). Treat that exactly like
            // a rejected request even when the backend reported that it
            // accepted a callback before transitioning the Session.
            if (version == _lifecycleVersion && ReferenceEquals(_attachedSession, _session) && _session.IsFaulted)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} rebuild failed.", _session.Fault);
        }

        public void Release()
        {
            FlowFieldSessionBase owner = ActiveSession;
            if (!owner.IsInitialized && !owner.IsFaulted)
                return;
            BeginLifecycleOperation();
            DetachActiveModifiers();
            owner.Release();
            DetachReleasedSession(owner);
            _refreshTimer = 0f;
#if UNITY_EDITOR
            // The Volume3D gizmo mesh is cached per result and must drop the released field.
            if (owner.SpaceMode == FlowFieldSpaceMode.Volume3D)
                FlowFieldEditorVisualizationBridge.RequestRefresh(this);
#endif
        }

        private void ThrowIfLifecycleUnavailable()
        {
            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                if (_volumeSession.IsFaulted)
                    throw new InvalidOperationException($"{nameof(FlowFieldManager)} is faulted.",
                        new InvalidOperationException(_volumeSession.LastError));
                if (!_volumeSession.IsInitialized)
                    throw new InvalidOperationException($"{nameof(FlowFieldManager)} is not initialized.");
                return;
            }
            if (_session.IsFaulted)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is faulted.", _session.Fault);
            if (!_session.IsInitialized)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is not initialized.");
        }

        private void ThrowIfAvailableForInput()
        {
            ThrowIfLifecycleUnavailable();
            if (SpaceMode != FlowFieldSpaceMode.Volume3D && _session.ConfigurationStale)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} configuration is stale. Call RequestRebuild() before use.");
        }

        private void ThrowIfInputAllowedForMode()
        {
            ThrowIfLifecycleUnavailable();
            // StaticBaked ignores runtime Goal/obstacle input. It must remain
            // a valid no-op even while an unrelated serialized setting is
            // awaiting an explicit rebuild.
            if (SpaceMode != FlowFieldSpaceMode.Volume3D
                && BakeMode != FlowFieldBakeMode.StaticBaked
                && _session.ConfigurationStale)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} configuration is stale. Call RequestRebuild() before use.");
        }

        private void ValidateConfiguration(bool validateStaticGoal = true)
        {
            if (!Enum.IsDefined(typeof(FlowFieldSpaceMode), _spaceMode))
                throw new ArgumentOutOfRangeException(nameof(_spaceMode));
            if ((_session.IsInitialized && _spaceMode != FlowFieldSpaceMode.Surface2D)
                || (_volumeSession.IsInitialized && _spaceMode != FlowFieldSpaceMode.Volume3D))
                throw new InvalidOperationException("FlowField Space Mode cannot change during an active Init session. Release and Init again.");

            if (_spaceMode == FlowFieldSpaceMode.Volume3D)
                ValidateVolumeConfiguration(validateStaticGoal);
            else
                ValidateSurfaceConfiguration(validateStaticGoal);
        }

        private void ValidateVolumeConfiguration(bool validateStaticGoal)
        {
            if (!FlowFieldGridSpace.IsFinite(_cellSize) || _cellSize < FlowFieldBakeBoundsUtility.MinCellSize)
                throw new ArgumentOutOfRangeException(nameof(_cellSize));
            if (!FlowFieldGridSpace.IsFinite(_bakeBoundsLocal.center)
                || !FlowFieldGridSpace.IsFinite(_bakeBoundsLocal.size)
                || _bakeBoundsLocal.size.x <= 0f
                || _bakeBoundsLocal.size.y <= 0f
                || _bakeBoundsLocal.size.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(_bakeBoundsLocal));
            if (!FlowFieldGridSpace.IsFinite(_defaultFlowDirection)
                || _defaultFlowDirection.sqrMagnitude <= VALUE_EPSILON)
                throw new ArgumentOutOfRangeException(nameof(_defaultFlowDirection));
            if (Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f
                || (transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
                throw new ArgumentException("FlowField Manager must use an unrotated transform with unit scale in Volume3D mode.", nameof(transform));
            if (_obstacleLayer.value == 0)
                throw new ArgumentException("Obstacle LayerMask must contain at least one layer.", nameof(_obstacleLayer));
            if (!FlowFieldGridSpace.IsFinite(_obstacleClearance) || _obstacleClearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(_obstacleClearance));
            if (!FlowFieldGridSpace.IsFinite(_goalInfluenceRadius) || _goalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(_goalInfluenceRadius));
            if (!Enum.IsDefined(typeof(FlowFieldBakeMode), _bakeMode))
                throw new ArgumentOutOfRangeException(nameof(_bakeMode));
            if (_maxGpuWaves < 64)
                throw new ArgumentOutOfRangeException(nameof(_maxGpuWaves), "GPU wave limit must be at least 64.");
            if (!TryGetVolumeLayout(out Bounds volumeBounds, out FlowFieldGridSpace volumeGrid) || !volumeGrid.IsValid)
                throw new ArgumentException("Volume Bounds and Cell Size do not produce a valid grid.", nameof(_bakeBoundsLocal));
            if (volumeGrid.CellCount > FlowFieldBakeBoundsUtility.MaxVolumeCellCount)
                throw new ArgumentOutOfRangeException(nameof(_bakeBoundsLocal), "Volume cell count exceeds one million cells.");
            if (_volumeSession.IsInitialized && BakeMode != _bakeMode)
                throw new InvalidOperationException("FlowField Bake Mode cannot change during an active Init session. Release and Init again.");
            if (_bakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (_volumeStaticBakeData == null)
                    throw new InvalidOperationException("Volume3D StaticBaked mode requires a FlowFieldVolumeBakeData asset.");
                if (!_volumeStaticBakeData.Matches(
                        volumeGrid,
                        volumeBounds,
                        _obstacleLayer,
                        _obstacleClearance,
                        out string volumeMismatch))
                    throw new InvalidOperationException(volumeMismatch);
                if (validateStaticGoal
                    && !_volumeStaticBakeData.MatchesGoal(
                        HasConfiguredGoal,
                        ConfiguredGoalWorld,
                        _goalInfluenceRadius,
                        out string volumeGoalMismatch))
                    throw new InvalidOperationException(volumeGoalMismatch);
            }
        }

        private void ValidateSurfaceConfiguration(bool validateStaticGoal)
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
                _session.MarkDirty(FlowFieldDirtyFlags.Grid, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
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
                    deferGoalVersion: true);
                RefreshEditorPreview();
            }
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void RefreshEditorPreview()
        {
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        private void MarkGoalDirty()
        {
            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                // A Goal change keeps the obstacle result unless a tracked obstacle moved.
                if (_volumeSession.IsInitialized)
                    QueueVolumeRequest(force: true);
                RefreshEditorPreview();
                return;
            }
            _session.GoalTracker.ResetWarning();
            _session.MarkDirty(
                FlowFieldDirtyFlags.Goal,
                FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid,
                deferGoalVersion: true);
            RefreshEditorPreview();
        }

        private FlowFieldGridSpace CreateGridSpace()
        {
            if (!TryGetBakeLayout(out _, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Bake Bounds and Cell Size do not produce a valid grid.");
            return grid;
        }

        internal bool TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
            => FlowFieldBakeBoundsUtility.TryCreateVolumeWorldLayout(
                transform.position,
                _bakeBoundsLocal,
                _cellSize,
                out worldBounds,
                out grid);

        private FlowFieldVolumeRequest CreateVolumeRequest()
        {
            if (!TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Volume Bounds and Cell Size do not produce a valid grid.");
            return CreateVolumeRequest(
                grid,
                worldBounds,
                _bakeMode,
                _bakeMode == FlowFieldBakeMode.StaticBaked ? _volumeStaticBakeData : null);
        }

        private FlowFieldVolumeRequest CreateVolumeRequest(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData bakeData)
            => new FlowFieldVolumeRequest(
                grid,
                worldBounds,
                bakeMode,
                _obstacleLayer,
                _obstacleClearance,
                HasConfiguredGoal,
                ConfiguredGoalWorld,
                _goalInfluenceRadius,
                _defaultFlowDirection,
                _maxGpuWaves,
                _frontierComputeShader,
                bakeData,
                CaptureBakeInputGeneration());

        private void QueueVolumeRequest(bool force = false, bool requeryObstacles = false)
        {
            if (!_volumeSession.IsInitialized || _volumeSession.IsFaulted)
                return;
            FlowFieldVolumeRequest request = CreateVolumeRequest();
            if (!force
                && _volumeSession.TryGetLatestRequestedInput(out FlowFieldVolumeRequest latest)
                && latest.HasSameInputs(request))
                return;
            // The session owns the accepted input, including a newer input
            // submitted from its synchronous state-change callback.
            _volumeSession.Submit(request, requeryObstacles);
        }

        /// <summary>
        /// Creates an isolated managed Volume3D bake session. The caller owns
        /// and must dispose the returned session; it may pump it from either
        /// the Editor update loop or a test-controlled clock.
        /// </summary>
        internal FlowFieldVolumeSession CreateVolumeBakeSessionForEditor(
            out FlowFieldVolumeRequest request)
        {
            request = default;
            if (_spaceMode != FlowFieldSpaceMode.Volume3D)
                throw new InvalidOperationException("Volume bake requires Volume3D mode.");
            if (!TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Volume Bounds and Cell Size do not produce a valid grid.");

            request = CreateVolumeRequest(grid, worldBounds, FlowFieldBakeMode.RuntimeDynamic, null);

            FlowFieldVolumeSession session = new FlowFieldVolumeSession();
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, null);
                session.ForceManagedBackend();
                session.Submit(request);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }

        #region Provider and Controller API

        public FlowFieldSample Sample(Vector3 worldPosition)
        {
            ThrowIfAvailableForInput();
            return ActiveSession.Sample(worldPosition);
        }

        public bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
            => ActiveSession.TrySample(worldPosition, out sample);

        public FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            ThrowIfAvailableForInput();
            return ActiveSession.ClampPositionToGrid(worldPosition);
        }

        public void SetSpaceMode(FlowFieldSpaceMode mode)
        {
            if (!Enum.IsDefined(typeof(FlowFieldSpaceMode), mode))
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (IsInitialized || IsFaulted)
                throw new InvalidOperationException("FlowField Space Mode can only change after Release and before Init.");
            _spaceMode = mode;
        }

        public bool RegisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAllowedForMode();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (collider == null)
                throw new InvalidOperationException("Cannot register a destroyed Collider.");
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException("Dynamic obstacle registration is not allowed in StaticBaked mode.");
            bool added = ActiveSession.RegisterObstacleCommand(collider);
#if UNITY_EDITOR
            if (added)
                InvalidateEditorPreview();
#endif
            return added;
        }

        public bool UnregisterDynamicObstacle(Collider collider)
        {
            ThrowIfInputAllowedForMode();
            if (ReferenceEquals(collider, null))
                throw new ArgumentNullException(nameof(collider));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            bool removed = ActiveSession.UnregisterObstacleCommand(collider);
#if UNITY_EDITOR
            if (removed)
                InvalidateEditorPreview();
#endif
            return removed;
        }

        public void NotifyObstacleRegionDirty(Bounds worldBounds)
        {
            ThrowIfInputAllowedForMode();
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center)
                || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                throw new ArgumentOutOfRangeException(nameof(worldBounds));
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException("Obstacle changes are not allowed in StaticBaked mode.");
            ActiveSession.NotifyObstacleRegionDirtyCommand(worldBounds);
            RefreshEditorPreview();
        }

        public void SetGoal(in FlowFieldGoalRequest request)
        {
            ThrowIfInputAllowedForMode();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException("Runtime Goal changes are not allowed in StaticBaked mode.");

            if (!request.HasGoal)
            {
                if (_goalTransform == null && !_hasExplicitGoal)
                    return;
                _goalTransform = null;
                _hasExplicitGoal = false;
                _explicitGoalWorld = default;
                MarkGoalDirty();
                return;
            }

            if (_goalTransform == null
                && _hasExplicitGoal
                && FlowFieldGridSpace.Approximately(
                    _explicitGoalWorld,
                    request.WorldPosition,
                    0.00000001d)
                && Mathf.Abs(_goalInfluenceRadius - request.InfluenceRadius) <= VALUE_EPSILON)
                return;

            _goalTransform = null;
            _hasExplicitGoal = true;
            _explicitGoalWorld = request.WorldPosition;
            _goalInfluenceRadius = request.InfluenceRadius;
            MarkGoalDirty();
        }

        #endregion

        public void NotifyCellsDirty()
        {
            ThrowIfInputAllowedForMode();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException("Cell regeneration is not allowed in StaticBaked mode.");
            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                QueueVolumeRequest(force: true, requeryObstacles: true);
                return;
            }
            _session.MarkDirty(
                FlowFieldDirtyFlags.Grid,
                FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid,
                deferSurfaceVersion: true);
            RefreshEditorPreview();
        }

        internal void MarkConfigurationStale()
        {
            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                if (_volumeSession.IsInitialized)
                    QueueVolumeRequest(force: true, requeryObstacles: true);
                RefreshEditorPreview();
                return;
            }
            if (!_session.IsInitialized)
                return;
            _session.MarkConfigurationStale();
            RefreshEditorPreview();
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

        private FlowFieldSessionRequest CreateSurfaceRequest(
            FlowFieldSurfaceBakeSettings settings,
            FlowFieldDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
        {
            int maxWaves = FlowFieldComputeSolver.ClampMaxWaves(settings.Grid.CellCount, _maxGpuWaves);
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                return FlowFieldSessionRequest.ForStaticSnapshot(
                    settings,
                    _staticBakeData,
                    CreateStaticBakeSnapshot(settings),
                    _obstacleLayer,
                    _obstacleCheckHeight,
                    _obstacleCheckCenterOffset,
                    _obstacleClearance,
                    _defaultFlowDirection,
                    dirtyFlags,
                    maxWaves);
            }

            return FlowFieldSessionRequest.ForSceneBuild(
                settings,
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance,
                _enableUnregisteredObstacleSweep,
                ResolveConfiguredGoal(settings.Grid),
                _defaultFlowDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion,
                maxWaves);
        }

        private bool TryCreateCurrentSessionRequest(out FlowFieldSessionRequest request)
        {
            request = default;
            try
            {
                FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
                request = CreateSurfaceRequest(
                    settings,
                    FlowFieldDirtyFlags.None,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal void SetBakeBoundsLocal(Bounds localBounds)
        {
            Bounds snapped = _spaceMode == FlowFieldSpaceMode.Volume3D
                ? FlowFieldBakeBoundsUtility.SnapVolumeCenterAnchored(localBounds, _cellSize)
                : FlowFieldBakeBoundsUtility.SnapCenterAnchored(localBounds, _cellSize);
            if (FlowFieldBakeBoundsUtility.Approximately(_bakeBoundsLocal, snapped))
                return;
            _bakeBoundsLocal = snapped;
            if (_spaceMode == FlowFieldSpaceMode.Volume3D)
            {
                if (_volumeSession.IsInitialized)
                    QueueVolumeRequest(force: true, requeryObstacles: true);
                RefreshEditorPreview();
                return;
            }
            if (_session.IsInitialized)
            {
                if (TryGetBakeLayout(out _, out FlowFieldGridSpace current))
                    _session.ObserveGrid(current);
                _session.MarkDirty(
                    FlowFieldDirtyFlags.Grid,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid,
                    deferSurfaceVersion: true);
            }
            RefreshEditorPreview();
        }

        internal bool TryValidateSurfaceBake(out string reason)
            => TryValidateSurfaceBake(out reason, includeStaticGoal: true);

        internal bool TryValidateSurfaceBake(out string reason, bool includeStaticGoal)
        {
            if (_spaceMode == FlowFieldSpaceMode.Volume3D)
                return TryValidateVolumeBake(out reason);
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

        internal bool TryValidateVolumeBake(out string reason)
        {
            reason = string.Empty;
            if (Quaternion.Angle(transform.rotation, Quaternion.identity) > 0.01f
                || (transform.lossyScale - Vector3.one).sqrMagnitude > 0.0001f)
            {
                reason = "Volume3D는 회전 없는 unit-scale Manager Transform만 지원합니다.";
                return false;
            }
            if (_obstacleLayer.value == 0)
            {
                reason = "Volume3D Obstacle LayerMask가 비어 있습니다.";
                return false;
            }
            if (!FlowFieldGridSpace.IsFinite(_defaultFlowDirection)
                || _defaultFlowDirection.sqrMagnitude <= VALUE_EPSILON)
            {
                reason = "Volume3D Default Direction은 유한한 non-zero 벡터여야 합니다.";
                return false;
            }
            if (!FlowFieldGridSpace.IsFinite(_obstacleClearance) || _obstacleClearance < 0f
                || !FlowFieldGridSpace.IsFinite(_goalInfluenceRadius) || _goalInfluenceRadius < 0f)
            {
                reason = "Volume3D Obstacle Clearance와 Goal Influence Radius가 유효하지 않습니다.";
                return false;
            }
            if (!TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid) || !grid.IsValid)
            {
                reason = "Volume Bounds 또는 Cell Size가 유효하지 않습니다.";
                return false;
            }
            if (grid.CellCount > FlowFieldBakeBoundsUtility.MaxVolumeCellCount)
            {
                reason = "Volume3D 셀 수가 1,000,000개를 초과합니다.";
                return false;
            }
            if (_bakeMode != FlowFieldBakeMode.StaticBaked)
                return true;
            if (_volumeStaticBakeData == null)
            {
                reason = "Volume3D StaticBaked 모드에는 FlowFieldVolumeBakeData가 필요합니다.";
                return false;
            }
            if (!_volumeStaticBakeData.Matches(
                grid,
                worldBounds,
                _obstacleLayer,
                _obstacleClearance,
                out reason))
                return false;
            return _volumeStaticBakeData.MatchesGoal(
                HasConfiguredGoal,
                ConfiguredGoalWorld,
                _goalInfluenceRadius,
                out reason);
        }

        internal void AssignStaticBakeData(FlowFieldStaticBakeData bakeData)
        {
            if (_staticBakeData == bakeData)
                return;
            _staticBakeData = bakeData;
            if (_session.IsInitialized)
                _session.MarkConfigurationStale();
            RefreshEditorPreview();
        }

        internal void AssignVolumeBakeData(FlowFieldVolumeBakeData bakeData)
        {
            if (_volumeStaticBakeData == bakeData)
                return;
            _volumeStaticBakeData = bakeData;
            if (_volumeSession.IsInitialized)
                QueueVolumeRequest(force: true, requeryObstacles: true);
            RefreshEditorPreview();
        }

        #endregion

        // This is the facade's synchronous initialization/activation attempt,
        // not the Volume build generation (which also changes on valid commits).
        private int _lifecycleVersion;

        private int BeginLifecycleOperation()
        {
            unchecked { return ++_lifecycleVersion; }
        }

        private bool OwnsLifecycleOperation(FlowFieldSessionBase owner, int version)
            => version == _lifecycleVersion && ReferenceEquals(_attachedSession, owner) && owner.IsInitialized;

        private void AttachSessionCallbacks(FlowFieldSessionBase session)
        {
            if (ReferenceEquals(_attachedSession, session))
                return;
            DetachSessionCallbacks();
            _attachedSession = session;
            _attachedSession.FieldCommitted += OnSessionFieldCommitted;
            _attachedSession.Failed += OnSessionFailed;
            _attachedSession.StateChanged += OnSessionStateChanged;
            _attachedSession.EventFailed += OnSessionEventFailed;
        }

        private void DetachReleasedSession(FlowFieldSessionBase session)
        {
            if (ReferenceEquals(_attachedSession, session)
                && session.State == FlowFieldRuntimeState.Released)
                DetachSessionCallbacks();
        }

        private void DetachSessionCallbacks()
        {
            if (_attachedSession == null)
                return;
            _attachedSession.FieldCommitted -= OnSessionFieldCommitted;
            _attachedSession.Failed -= OnSessionFailed;
            _attachedSession.StateChanged -= OnSessionStateChanged;
            _attachedSession.EventFailed -= OnSessionEventFailed;
            _attachedSession = null;
        }

        private void OnSessionFieldCommitted()
            => FieldChanged?.Invoke();

        private void OnSessionFailed(Exception exception)
        {
            if (exception != null)
                Debug.LogError($"[{nameof(FlowFieldManager)}] FlowField build failed: {exception.Message}", this);
        }

        private void OnSessionStateChanged(FlowFieldRuntimeState state)
            => StateChanged?.Invoke(state);

        private void OnSessionEventFailed(Exception exception)
        {
            if (exception != null)
                Debug.LogException(exception, this);
        }

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

        private int ComputeBakeInputHash()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (int)_spaceMode;
                hash = hash * 31 + (int)_bakeMode;
                hash = hash * 31 + _bakeBoundsLocal.center.GetHashCode();
                hash = hash * 31 + _bakeBoundsLocal.size.GetHashCode();
                hash = hash * 31 + _cellSize.GetHashCode();
                hash = hash * 31 + _groundBakeLayer.value;
                hash = hash * 31 + _maxSurfaceSlope.GetHashCode();
                hash = hash * 31 + _maxStepHeight.GetHashCode();
                hash = hash * 31 + _obstacleLayer.value;
                hash = hash * 31 + _obstacleCheckHeight.GetHashCode();
                hash = hash * 31 + _obstacleCheckCenterOffset.GetHashCode();
                hash = hash * 31 + _obstacleClearance.GetHashCode();
                hash = hash * 31 + _defaultFlowDirection.GetHashCode();
                hash = hash * 31 + _maxGpuWaves;
                hash = hash * 31 + (_frontierComputeShader != null
                    ? _frontierComputeShader.GetInstanceID()
                    : 0);
                hash = hash * 31 + (_goalTransform != null
                    ? _goalTransform.GetInstanceID()
                    : 0);
                if (_goalTransform != null)
                    hash = hash * 31 + _goalTransform.position.GetHashCode();
                hash = hash * 31 + (_hasExplicitGoal ? 1 : 0);
                hash = hash * 31 + _explicitGoalWorld.GetHashCode();
                hash = hash * 31 + _goalInfluenceRadius.GetHashCode();
                hash = hash * 31 + transform.position.GetHashCode();
                hash = hash * 31 + transform.rotation.GetHashCode();
                hash = hash * 31 + transform.lossyScale.GetHashCode();
                return hash;
            }
        }

    }
}
