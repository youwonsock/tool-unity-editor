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

        // Both sessions share one boundary, so the Manager's events keep their
        // order and a Modifier of either mode sees the same command rules.
        private readonly FlowFieldExecutionBoundary _boundary = new FlowFieldExecutionBoundary();
        private readonly FlowFieldSession _session;
        private readonly FlowFieldVolumeSession _volumeSession;
        private float _refreshTimer;
        private bool _hasExplicitGoal;
        private Vector3 _explicitGoalWorld;
        private bool _requiresActivationRebuild;
        private FlowFieldSessionBase _attachedSession;
        [NonSerialized] private int _bakeInputGeneration;
        [NonSerialized] private int _lastBakeInputHash;
        [NonSerialized] private bool _hasBakeInputHash;

        public FlowFieldManager()
        {
            _session = new FlowFieldSession(boundary: _boundary);
            _volumeSession = new FlowFieldVolumeSession(_boundary);
            ForwardSessionEvents(_session);
            ForwardSessionEvents(_volumeSession);
        }

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
                return ModeFor(_spaceMode).Session;
            }
        }

        private ModeDriver ActiveMode => ModeFor(ActiveSession.SpaceMode);

        public bool IsInitialized => ActiveSession.IsInitialized;
        public FlowFieldRuntimeState State => ActiveSession.State;
        public bool IsFaulted => ActiveSession.IsFaulted;
        public bool IsReady => ActiveSession.IsReady;
        public bool IsRebuilding => ActiveSession.IsRebuilding;
        public string LastError => ActiveSession.LastError;
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
        internal FlowFieldSession SurfaceSession => _session;
        internal FlowFieldStaticBakeData StaticBakeData => _staticBakeData;
        internal FlowFieldVolumeBakeData VolumeStaticBakeData => _volumeStaticBakeData;
        internal Bounds BakeBoundsLocal => _bakeBoundsLocal;
        internal float CellSize => _cellSize;
        internal LayerMask ObstacleLayer => _obstacleLayer;
        internal float ObstacleCheckHeight => _obstacleCheckHeight;
        internal float ObstacleCheckCenterOffset => _obstacleCheckCenterOffset;
        internal float ObstacleClearance => _obstacleClearance;
        internal Vector3 DefaultFlowDirection => _defaultFlowDirection;
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

        private void Reset()
        {
            _bakeBoundsLocal = FlowFieldBakeBoundsUtility.DefaultLocalBounds;
            _cellSize = 0.5f;
            _groundBakeLayer = Physics.DefaultRaycastLayers;
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
            if (!Application.isPlaying || _boundary.TryDefer(ResumeAfterEnable))
                return;
            ResumeAfterEnable();
        }

        private void ResumeAfterEnable()
        {
            if (!_requiresActivationRebuild || !ActiveSession.IsInitialized)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            ActiveMode.ActivateSuspended();
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

            ActiveMode.OnSettingsEdited();
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying || !IsInitialized || IsFaulted)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            try
            {
                ActiveMode.ObserveInputs();
            }
            catch (Exception exception)
            {
                // Invalid live input is a single Session fault. Once Faulted,
                // LateUpdate stops polling and callers can explicitly retry
                // through RequestRebuild().
                ActiveSession.ReportFault(exception);
            }
        }

        // Observation follows real time, so slowing or stopping the game does
        // not delay the detection of input changes.
        private bool ConsumeRefreshTick()
        {
            _refreshTimer -= Time.unscaledDeltaTime;
            if (_refreshTimer > 0f)
                return false;
            _refreshTimer = Mathf.Max(MIN_REFRESH_RATE, _refreshRate);
            return true;
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            FlowFieldEditorVisualizationBridge.Unregister(this);
#endif
            if (!Application.isPlaying || _boundary.TryDefer(SuspendAfterDisable))
                return;
            SuspendAfterDisable();
        }

        private void SuspendAfterDisable()
        {
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            _requiresActivationRebuild = true;
            ActiveSession.Suspend();
            _refreshTimer = 0f;
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            ReleaseEditorPreview();
            FlowFieldEditorVisualizationBridge.Unregister(this);
#endif
            if (!_boundary.TryDefer(DisposeSessions))
                DisposeSessions();
        }

        private void DisposeSessions()
        {
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            _session.DisposePermanently();
            _volumeSession.Dispose();
            _attachedSession = null;
        }

        public void Init()
        {
            _boundary.ThrowIfInUserCode(nameof(Init));
            if (IsInitialized && _spaceMode != SpaceMode)
                throw new InvalidOperationException("FlowField Space Mode cannot change during an active Init session. Release and Init again.");
            if (IsFaulted)
                throw new InvalidOperationException(
                    $"{nameof(FlowFieldManager)} is faulted. Call RequestRebuild() to retry.",
                    ActiveSession.Fault);
            if (IsInitialized)
                return;
            ValidateConfiguration();
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            ActiveMode.Initialize();
        }

        public void RequestRebuild()
        {
            _boundary.ThrowIfInUserCode(nameof(RequestRebuild));
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            FlowFieldSessionBase owner = ActiveSession;
            bool retryingFault = owner.IsFaulted;
            if (retryingFault)
            {
                owner.RetryFault();
                if (_requiresActivationRebuild)
                {
                    // A fault skipped or replaced the suspension, so restore
                    // it. OnEnable cannot resume a Faulted session, which
                    // leaves the activation to a retry made while enabled.
                    owner.Suspend();
                    if (isActiveAndEnabled)
                        ActiveMode.ActivateSuspended();
                }
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
                ActiveMode.SubmitRebuild();
            }
            catch (Exception exception)
            {
                // If a retry fails during validation or request submission,
                // retain a diagnosable Faulted state instead of leaving an
                // initialized but unusable Session with no LastError.
                if (retryingFault)
                    owner.ReportFault(exception);
                throw;
            }
        }

        public void Release()
        {
            _boundary.ThrowIfInUserCode(nameof(Release));
            FlowFieldSessionBase owner = ActiveSession;
            if (!owner.IsInitialized && !owner.IsFaulted)
                return;
            using FlowFieldExecutionBoundary.OperationScope operation = _boundary.EnterOperation();
            ModeDriver mode = ActiveMode;
            DetachActiveModifiers();
            owner.Release();
            _attachedSession = null;
            _refreshTimer = 0f;
            mode.OnReleased();
        }

        private void ThrowIfLifecycleUnavailable()
        {
            if (IsFaulted)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is faulted.", ActiveSession.Fault);
            if (!IsInitialized)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is not initialized.");
        }

        private void ThrowIfAvailableForInput()
        {
            ThrowIfLifecycleUnavailable();
            if (ActiveMode.ConfigurationStale)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} configuration is stale. Call RequestRebuild() before use.");
        }

        private void ThrowIfInputAllowedForMode()
        {
            ThrowIfLifecycleUnavailable();
            // StaticBaked ignores runtime Goal/obstacle input. It must remain
            // a valid no-op even while an unrelated serialized setting is
            // awaiting an explicit rebuild.
            if (ActiveMode.ConfigurationStale && BakeMode != FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} configuration is stale. Call RequestRebuild() before use.");
        }

        private void ValidateConfiguration(bool validateStaticGoal = true)
        {
            if (!Enum.IsDefined(typeof(FlowFieldSpaceMode), _spaceMode))
                throw new ArgumentOutOfRangeException(nameof(_spaceMode));
            if (IsInitialized && SpaceMode != _spaceMode)
                throw new InvalidOperationException("FlowField Space Mode cannot change during an active Init session. Release and Init again.");
            ModeFor(_spaceMode).ValidateConfiguration(validateStaticGoal);
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
            ActiveMode.MarkGoalDirty();
            RefreshEditorPreview();
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
            _boundary.ThrowIfInUserCode(nameof(SetSpaceMode));
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
            // Removal needs no valid configuration, as with Modifiers.
            ThrowIfLifecycleUnavailable();
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
            _boundary.ThrowIfInUserCode(nameof(NotifyObstacleRegionDirty));
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
            _boundary.ThrowIfInUserCode(nameof(SetGoal));
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
            _boundary.ThrowIfInUserCode(nameof(NotifyCellsDirty));
            ThrowIfInputAllowedForMode();
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
                throw new InvalidOperationException("Cell regeneration is not allowed in StaticBaked mode.");
            ActiveMode.MarkCellsDirty();
        }

        internal void MarkConfigurationStale()
        {
            ActiveMode.MarkConfigurationStale();
        }

        // Subscribed for the Manager's whole life: a queued Released must still
        // arrive after the Manager has moved on from that session.
        private void ForwardSessionEvents(FlowFieldSessionBase session)
        {
            session.FieldCommitted += OnSessionFieldCommitted;
            session.Failed += OnSessionFailed;
            session.StateChanged += OnSessionStateChanged;
            session.EventFailed += OnSessionEventFailed;
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

    }
}
