using System;
using UnityEngine;

namespace Common.FlowField
{
    [DefaultExecutionOrder(-300)]
    public partial class FlowFieldManager : MonoBehaviour, IFlowFieldProvider, IFlowFieldController, IFlowFieldBakeController
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
        [SerializeField, HideInInspector] private FlowFieldSurfaceBakeData _surfaceBakeData;
        [SerializeField, HideInInspector] private FlowFieldStaticObstacleBakeData _staticObstacleBakeData;
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

        private enum LifecycleState
        {
            Uninitialized,
            Initialized,
            Faulted,
            Released,
        }

        private readonly FlowFieldRuntimeContext _context = new FlowFieldRuntimeContext();
        private readonly FlowFieldObstaclePipeline _obstaclePipeline = new FlowFieldObstaclePipeline();
        private FlowFieldBuildPipeline _buildPipeline;
        private FlowFieldSurfaceBakeData _runtimeSurfaceBakeData;
        private FlowFieldSurfaceBakeData _editorSurfaceBakeData;
        private FlowFieldSurfaceBakeData _runtimeStaticSurfaceBakeData;
        private int _runtimeStaticSourceRevision = -1;
        private readonly FlowFieldWorkspace _committedWorkspace = new FlowFieldWorkspace();
        private FlowFieldSurfaceBakeData _committedSurfaceBakeData;
        private FlowFieldSurfaceBakeData _committedSourceSurfaceBakeData;
        private FlowFieldGridSpace _committedGrid;
        private int _committedSourceSurfaceRevision = -1;

        private float _refreshTimer;
        private bool _hasExplicitGoal;
        private Vector3 _explicitGoalWorld;
        private bool _isReady;
        private int _revision;
        private LifecycleState _lifecycleState;
        private Exception _fault;
        private bool _requiresActivationRebuild;
        private bool _configurationStale;
        private bool _isRebuilding;
        private FlowFieldBakeMode _sessionBakeMode;
        private int _inputVersion;
        private int _resolvedGoalX = -1;
        private int _resolvedGoalZ = -1;
        private readonly FlowFieldGoalTracker _goalTracker = new FlowFieldGoalTracker();

        public bool IsInitialized => _lifecycleState == LifecycleState.Initialized;
        public bool IsFaulted => _lifecycleState == LifecycleState.Faulted;
        public bool IsReady => _lifecycleState == LifecycleState.Initialized
            && !_configurationStale
            && _isReady;
        public bool IsRebuilding => _isRebuilding;
        public FlowFieldBakeMode BakeMode => IsInitialized ? _sessionBakeMode : _bakeMode;
        public int Revision => _revision;
        public event Action FieldChanged;

        internal FlowFieldSurfaceBakeData SurfaceBakeData => _surfaceBakeData;
        private FlowFieldBakeMode CurrentBakeMode
            => IsInitialized ? _sessionBakeMode : _bakeMode;

        internal FlowFieldSurfaceBakeData ActiveSurfaceBakeData
            => CurrentBakeMode == FlowFieldBakeMode.StaticBaked
                ? _runtimeStaticSurfaceBakeData != null
                    ? _runtimeStaticSurfaceBakeData
                    : _staticBakeData != null ? _staticBakeData.SurfaceBakeData : null
                : _runtimeSurfaceBakeData;
        internal FlowFieldSurfaceBakeData EditorSurfaceBakeData
        {
            get
            {
                if (CurrentBakeMode != FlowFieldBakeMode.StaticBaked)
                {
                    if (Application.isPlaying)
                        return _runtimeSurfaceBakeData ?? _surfaceBakeData;

                    // Dynamic preview follows the same downward-ray Surface
                    // baker as RuntimeDynamic. A persisted legacy asset is
                    // reused only while its complete signature still matches;
                    // otherwise a hidden transient view is rebuilt in memory.
                    try
                    {
                        FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
                        if (_surfaceBakeData != null
                            && _surfaceBakeData.HasValidData
                            && _surfaceBakeData.Matches(settings, out _))
                            return _surfaceBakeData;

                        if (_editorSurfaceBakeData != null
                            && _editorSurfaceBakeData.HasValidData
                            && _editorSurfaceBakeData.Matches(settings, out _))
                            return _editorSurfaceBakeData;

                        FlowFieldSurfaceBakeResult result = FlowFieldSurfaceBaker.Bake(settings);
                        DestroyTransientSurface(ref _editorSurfaceBakeData);
                        _editorSurfaceBakeData = ScriptableObject.CreateInstance<FlowFieldSurfaceBakeData>();
                        _editorSurfaceBakeData.name = $"{name}_EditorSurfaceBake";
                        _editorSurfaceBakeData.hideFlags = HideFlags.HideAndDontSave;
                        _editorSurfaceBakeData.Apply(settings, result);
                        return _editorSurfaceBakeData;
                    }
                    catch
                    {
                        // The inspector reports the persisted bake mismatch or
                        // missing ground. Repaint callbacks must not throw.
                        return _surfaceBakeData;
                    }
                }

                if ((_runtimeStaticSurfaceBakeData == null
                        || _staticBakeData != null
                            && _runtimeStaticSourceRevision != _staticBakeData.Revision)
                    && !Application.isPlaying
                    && _staticBakeData != null
                    && _staticBakeData.HasValidData)
                {
                    try
                    {
                        EnsureStaticSurfaceBakeData();
                    }
                    catch
                    {
                        // Inspector/gizmo validation reports the actual
                        // mismatch. Avoid throwing from a repaint callback.
                    }
                }

                return _runtimeStaticSurfaceBakeData != null
                    ? _runtimeStaticSurfaceBakeData
                    : _staticBakeData != null ? _staticBakeData.SurfaceBakeData : null;
            }
        }
        internal FlowFieldStaticObstacleBakeData StaticObstacleBakeData => _staticObstacleBakeData;
        internal FlowFieldStaticBakeData StaticBakeData => _staticBakeData;
        internal Bounds BakeBoundsLocal => _bakeBoundsLocal;
        internal float CellSize => _cellSize;
        internal int MaxGpuWaves => _maxGpuWaves;
        internal ComputeShader FrontierComputeShader => _frontierComputeShader;
        internal LayerMask ObstacleLayer => _obstacleLayer;
        internal float ObstacleCheckHeight => _obstacleCheckHeight;
        internal float ObstacleCheckCenterOffset => _obstacleCheckCenterOffset;
        internal float ObstacleClearance => _obstacleClearance;

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

        private void DestroyRuntimeSurfaceBakeData()
        {
            DestroyTransientSurface(ref _runtimeSurfaceBakeData);
            DestroyTransientSurface(ref _editorSurfaceBakeData);
            DestroyTransientSurface(ref _runtimeStaticSurfaceBakeData);
            _runtimeStaticSourceRevision = -1;
            DestroyCommittedSurfaceBakeData();
        }

        private void DestroyCommittedSurfaceBakeData()
        {
            DestroyTransientSurface(ref _committedSurfaceBakeData);
            _committedSourceSurfaceBakeData = null;
            _committedSourceSurfaceRevision = -1;
            _committedGrid = default;
            _committedWorkspace.Release();
        }

        private static void DestroyTransientSurface(ref FlowFieldSurfaceBakeData surface)
        {
            if (surface == null)
                return;

            if (Application.isPlaying)
                UnityEngine.Object.Destroy(surface);
            else
                UnityEngine.Object.DestroyImmediate(surface);
            surface = null;
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
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
            _refreshTimer = 0f;
            // A disabled manager invalidates its runtime field.  Once it is
            // enabled again, rebuild at this lifecycle boundary instead of
            // relying on a hidden recovery path in the next update tick.
            if (Application.isPlaying
                && _lifecycleState == LifecycleState.Initialized
                && _requiresActivationRebuild)
            {
                _requiresActivationRebuild = false;
                // OnDisable disposes the GPU solver together with its
                // readbacks.  Recreate the backend for the new activation
                // session before rebuilding the latest input.
                InitServices();
                Rebuild();
            }
        }

        private void OnValidate()
        {
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif

            if (Application.isPlaying && _lifecycleState == LifecycleState.Initialized)
                MarkConfigurationStale();
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying)
                return;
            if (_lifecycleState != LifecycleState.Initialized)
                ThrowIfLifecycleUnavailable();
            if (_configurationStale)
                return;
            ThrowIfUnavailable();
            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer > 0f)
                return;

            _refreshTimer = _refreshRate;
            DetectGridTransformChange();
            if (CurrentBakeMode == FlowFieldBakeMode.RuntimeDynamic)
                DetectGoalChange();
            DetectModifierChanges();
            if (CurrentBakeMode != FlowFieldBakeMode.RuntimeDynamic)
                return;
            // A registered obstacle can move while a GPU wave batch is in
            // flight. Record that newer input immediately so the callback is
            // discarded instead of committing a field for the old bounds.
            if (_obstaclePipeline.DetectDynamicTransformsChanged())
            {
                _context.DirtyFlags |= FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape;
                _inputVersion++;
            }
            if (!_isRebuilding && _enableUnregisteredObstacleSweep && _context.SurfaceReady)
            {
                _context.DirtyFlags |= FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape;
                _inputVersion++;
            }
        }

        private void FixedUpdate()
        {
            if (!Application.isPlaying)
                return;
            if (_lifecycleState != LifecycleState.Initialized)
                ThrowIfLifecycleUnavailable();
            if (_configurationStale)
                return;
            ThrowIfUnavailable();
            if (_requiresActivationRebuild)
                return;
            if (_context.DirtyFlags != FlowFieldDirtyFlags.None)
                Rebuild();
        }

        private void InitServices()
        {
            if (_modifierRegistry != null || _modifierPipeline != null)
            {
                if (_modifierRegistry == null || _modifierPipeline == null)
                    throw new InvalidOperationException("FlowField modifier services are only partially initialized.");
            }

            if (_modifierRegistry == null)
                _modifierRegistry = new FlowFieldModifierRegistry();
            if (_modifierPipeline == null)
                _modifierPipeline = new FlowFieldModifierPipeline(_modifierRegistry);
            if (_buildPipeline == null && CurrentBakeMode == FlowFieldBakeMode.RuntimeDynamic)
            {
                ComputeShader shader = _frontierComputeShader;
                if (shader == null)
                    shader = Resources.Load<ComputeShader>("FlowFieldFrontier");
                _buildPipeline = new FlowFieldBuildPipeline(shader);
            }
#if UNITY_EDITOR
            if (_editorPreview == null)
            {
                _editorPreview = new FlowFieldEditorPreview();
                _editorPreview.Init();
            }
#endif
        }

        private void OnDisable()
        {
            unchecked
            {
                _inputVersion++;
            }
            _buildPipeline?.Dispose();
            _buildPipeline = null;
            DestroyRuntimeSurfaceBakeData();
            _isRebuilding = false;
            _context.SurfaceReady = false;
            _context.HasObstacleMask = false;
            _context.Surface = null;
            _context.Workspace.ClearAll();
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
            // Disabling the component ends the active runtime session. Keep
            // the next activation aligned with the serialized mode so a mode
            // change made while stopped can be applied without tripping the
            // active-session immutability guard.
            _sessionBakeMode = _bakeMode;
            _requiresActivationRebuild = true;
            UpdateReadyState(false, resultChanged: false);
        }

        private void OnDestroy()
        {
            if (_lifecycleState == LifecycleState.Initialized || _lifecycleState == LifecycleState.Faulted)
                ReleaseCore();
#if UNITY_EDITOR
            else if (_editorPreview != null)
                ReleaseEditorPreview();
#endif
        }

        public void Init()
        {
            if (_lifecycleState == LifecycleState.Initialized)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is already initialized.");
            if (_lifecycleState == LifecycleState.Faulted)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is faulted. Call Release before Init.", _fault);

            try
            {
                InitServices();
                _sessionBakeMode = _bakeMode;
                _lifecycleState = LifecycleState.Initialized;
                _fault = null;
                _configurationStale = false;
                _requiresActivationRebuild = false;
                _context.DirtyFlags = FlowFieldDirtyFlags.All;
                Rebuild();
            }
            catch (Exception exception)
            {
                _fault = exception;
                _lifecycleState = LifecycleState.Faulted;
                throw;
            }
        }

        public void Rebuild()
        {
            ThrowIfLifecycleUnavailable();
            try
            {
                ValidateConfiguration();
                RebuildDirtyData();
                _configurationStale = false;
                _requiresActivationRebuild = false;
            }
            catch (Exception exception)
            {
                if (_fault == null)
                    _fault = exception;
                _lifecycleState = LifecycleState.Faulted;
                throw;
            }
        }

        public void Release()
        {
            if (_lifecycleState == LifecycleState.Uninitialized)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} has not been initialized.");
            if (_lifecycleState == LifecycleState.Released)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} has already been released.");
            ReleaseCore();
        }

        private void ReleaseCore()
        {
            unchecked
            {
                _inputVersion++;
            }
            ClearModifierRuntimeState();
            _obstaclePipeline.ClearDynamicObstacles();
            _goalTracker.Clear();
#if UNITY_EDITOR
            ReleaseEditorPreview();
#endif
            _buildPipeline?.Dispose();
            _buildPipeline = null;
            DestroyRuntimeSurfaceBakeData();
            _context.Release();
            _isRebuilding = false;
            _isReady = false;
            _configurationStale = false;
            _lifecycleState = LifecycleState.Released;
        }

        private void ThrowIfLifecycleUnavailable()
        {
            if (_lifecycleState == LifecycleState.Faulted)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is faulted.", _fault);
            if (_lifecycleState != LifecycleState.Initialized)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is not initialized.");
        }

        private void ThrowIfUnavailable()
        {
            ThrowIfLifecycleUnavailable();
            if (_configurationStale)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} configuration is stale. Call Rebuild() before use.");
        }

        internal void MarkConfigurationStale()
        {
            if (_lifecycleState != LifecycleState.Initialized)
                return;

            _configurationStale = true;
            _isReady = false;
            _inputVersion++;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
        }

        private void ValidateConfiguration()
        {
            if (!FlowFieldGridSpace.IsFinite(_cellSize) || _cellSize < FlowFieldBakeBoundsUtility.MinCellSize)
                throw new ArgumentOutOfRangeException(nameof(_cellSize), _cellSize, "Cell Size must be finite and positive.");
            if (!FlowFieldGridSpace.IsFinite(_bakeBoundsLocal.center) || !FlowFieldGridSpace.IsFinite(_bakeBoundsLocal.size)
                || _bakeBoundsLocal.size.x <= 0f || _bakeBoundsLocal.size.y <= 0f || _bakeBoundsLocal.size.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(_bakeBoundsLocal), "Bake Bounds must be finite and positive.");
            if (!FlowFieldGridSpace.IsFinite(_defaultFlowDirection) || _defaultFlowDirection.sqrMagnitude <= VALUE_EPSILON)
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
            if (_lifecycleState == LifecycleState.Initialized && _bakeMode != _sessionBakeMode)
                throw new InvalidOperationException("FlowField Bake Mode cannot change during an active Init session. Release and Init again.");
            if (!FlowFieldGridSpace.IsFinite(_goalInfluenceRadius) || _goalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(_goalInfluenceRadius));
            if (!FlowFieldBakeBoundsUtility.TryCreateWorldLayout(transform.position, _bakeBoundsLocal, _cellSize, out _, out FlowFieldGridSpace grid)
                || !grid.IsValid)
                throw new ArgumentException("Bake Bounds and Cell Size do not produce a valid grid.", nameof(_bakeBoundsLocal));
            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (_staticBakeData == null)
                    throw new InvalidOperationException("StaticBaked mode requires a FlowFieldStaticBakeData asset.");
                FlowFieldSurfaceBakeSettings staticSurfaceSettings = CreateSurfaceBakeSettings();
                if (!_staticBakeData.MatchesSurface(staticSurfaceSettings, out string surfaceMismatch))
                    throw new InvalidOperationException(surfaceMismatch);
                if (!_staticBakeData.Matches(
                        grid,
                        _staticBakeData.SurfaceBakeData,
                        _obstacleLayer,
                        _obstacleCheckHeight,
                        _obstacleCheckCenterOffset,
                        _obstacleClearance,
                        out string mismatchReason))
                    throw new InvalidOperationException(mismatchReason);
            }
            FlowFieldSurfaceBakeData validationSurface = CurrentBakeMode == FlowFieldBakeMode.StaticBaked
                ? ActiveSurfaceBakeData
                : _surfaceBakeData;
            if (validationSurface != null && validationSurface.HasValidData)
            {
                for (int index = 0; index < grid.CellCount; index++)
                {
                    if (validationSurface.IsSurfaceValid(index))
                    {
                        Vector3 surfaceCenter = validationSurface.GetCellCenter(grid, index);
                        if (!FlowFieldGridSpace.IsFinite(surfaceCenter))
                            throw new ArgumentException("Surface bake contains a non-finite height.", nameof(validationSurface));
                        validationSurface.GetSurfaceNormal(index);
                    }
                }
            }
        }

        private void RebuildDirtyData()
        {
            if (_isRebuilding)
                return;

            FlowFieldDirtyFlags pending = _context.DirtyFlags;
            _context.DirtyFlags = FlowFieldDirtyFlags.None;

            PrepareGridAndSurface(ref pending);

            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
            {
                // StaticBaked contains the complete base field. Runtime only
                // rebuilds modifier masks/default composition; physics and BFS
                // are intentionally never queried here.
                if (_staticBakeData == null || !_staticBakeData.HasValidData)
                    throw new InvalidOperationException("Static Flow Bake Asset is missing or invalid.");
                if (pending == FlowFieldDirtyFlags.None && _isReady)
                {
                    // A no-op Rebuild must not manufacture a new revision or
                    // FieldChanged event for an identical committed snapshot.
                    UpdateReadyState(true, resultChanged: false);
                    return;
                }
                _staticBakeData.CopyToWorkspace(_context.Grid, _context.Workspace);
                _context.HasObstacleMask = true;
                _context.DirtyFinalRegion = FlowFieldCellRect.Full(_context.Grid);
                _context.ResolvedDefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(_defaultFlowDirection);
                RebuildModifierAreaData();
                FlushPendingModifierChanges();
                bool staticChanged = RebuildFinalField();
                CommitStagingWorkspace();
                UpdateReadyState(true, staticChanged);
                return;
            }

            if ((pending & FlowFieldDirtyFlags.DefaultDirection) != 0)
            {
                _context.ResolvedDefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(_defaultFlowDirection);
                MarkFinalDirtyFull(ref pending);
            }

            bool rebuildStaticObstacles = (pending & FlowFieldDirtyFlags.StaticObstacles) != 0;
            bool rebuildDynamicObstacles = (pending & FlowFieldDirtyFlags.DynamicObstacles) != 0;
            bool rebuildGoal = (pending & FlowFieldDirtyFlags.Goal) != 0;
            if (rebuildStaticObstacles || rebuildDynamicObstacles || rebuildGoal)
            {
                FlowFieldGoalResolution goalResolution = ResolveConfiguredGoal(_context.Grid);
                _resolvedGoalX = goalResolution.IsValid ? goalResolution.LocalX : -1;
                _resolvedGoalZ = goalResolution.IsValid ? goalResolution.LocalZ : -1;
                FlowFieldBuildResult prepared = FlowFieldBuildPipeline.PrepareBase(
                    new FlowFieldBuildRequest(
                        _context.Grid,
                        FlowFieldSurfaceData.From(ActiveSurfaceBakeData),
                        new FlowFieldObstacleRequest(
                            _context.Grid,
                            ActiveSurfaceBakeData,
                            null,
                            _context.Workspace,
                            _obstacleLayer,
                            _obstacleCheckHeight,
                            _obstacleCheckCenterOffset,
                            _obstacleClearance,
                            _enableUnregisteredObstacleSweep,
                            _context.DirtyObstacleRegion),
                        goalResolution,
                        pending,
                        Mathf.Min(_context.Grid.CellCount, Mathf.Max(64, _maxGpuWaves)),
                        _inputVersion),
                    _obstaclePipeline,
                    _goalTracker,
                    rebuildStaticObstacles,
                    rebuildDynamicObstacles,
                    rebuildGoal);
                // The coordinator consumes the current union of moved/dirty
                // obstacle cells. Do not carry that rectangle into a later
                // request after the overlay has been rebuilt.
                _context.DirtyObstacleRegion = FlowFieldCellRect.Invalid;

                if (rebuildStaticObstacles || rebuildDynamicObstacles)
                    _context.HasObstacleMask = true;
                if (prepared.ObstacleMaskChanged)
                {
                    if (prepared.ObstacleDirtyRegion.IsValid)
                        _context.ExpandObstacleDirty(prepared.ObstacleDirtyRegion);
                    UpdateBlockedWarning(prepared.HasWalkableSurface);
                    pending |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
                    _context.ExpandFinalDirty(FlowFieldCellRect.Full(_context.Grid));
                }
                if (rebuildGoal || prepared.ObstacleMaskChanged)
                    MarkFinalDirtyFull(ref pending);
                if (prepared.GoalStatus == FlowFieldGoalBuildStatus.NoWalkableSurface
                    && _goalTracker.TryConsumeMissingWalkableWarning())
                {
                    Debug.LogWarning(
                        $"[{nameof(FlowFieldManager)}] Goal 범위에 이동 가능한 표면 셀이 없습니다.",
                        this);
                }
            }

            if ((pending & FlowFieldDirtyFlags.ModifierArea) != 0 && RebuildModifierAreaData())
                MarkFinalDirtyFull(ref pending);

            if ((pending & FlowFieldDirtyFlags.ModifierValue) != 0)
                MarkFinalDirtyFull(ref pending);

            FlushPendingModifierChanges();
            pending |= _context.DirtyFlags;
            _context.DirtyFlags = FlowFieldDirtyFlags.None;

            bool topologyChanged = (pending & (FlowFieldDirtyFlags.Grid
                | FlowFieldDirtyFlags.StaticObstacles
                | FlowFieldDirtyFlags.DynamicObstacles
                | FlowFieldDirtyFlags.Escape
                | FlowFieldDirtyFlags.Goal)) != 0;
            if (WorkspaceHasGoal() && topologyChanged)
            {
                StartBfsSolve();
                return;
            }

            bool resultChanged = false;
            if ((pending & FlowFieldDirtyFlags.FinalRegion) != 0)
                resultChanged = RebuildFinalField();

            if (resultChanged)
                CommitStagingWorkspace();

            UpdateReadyState(
                _context.SurfaceReady
                    && _context.HasObstacleMask
                    && _context.Grid.IsValid,
                resultChanged);
        }

        private void MarkFinalDirtyFull(ref FlowFieldDirtyFlags pending)
        {
            pending |= FlowFieldDirtyFlags.FinalRegion;
            _context.ExpandFinalDirty(FlowFieldCellRect.Full(_context.Grid));
        }

        private void PrepareGridAndSurface(ref FlowFieldDirtyFlags pending)
        {
            if ((pending & FlowFieldDirtyFlags.Grid) == 0 && _context.Grid.IsValid && _context.SurfaceReady)
                return;

            FlowFieldSurfaceBakeData activeSurface;
            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
            {
                activeSurface = EnsureStaticSurfaceBakeData();
            }
            else
            {
                FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
                FlowFieldSurfaceBakeResult bakeResult = FlowFieldSurfaceBaker.Bake(settings);
                if (_runtimeSurfaceBakeData == null)
                {
                    _runtimeSurfaceBakeData = ScriptableObject.CreateInstance<FlowFieldSurfaceBakeData>();
                    _runtimeSurfaceBakeData.name = $"{name}_RuntimeSurfaceBake";
                    _runtimeSurfaceBakeData.hideFlags = HideFlags.HideAndDontSave;
                }
                _runtimeSurfaceBakeData.Apply(settings, bakeResult);
                activeSurface = _runtimeSurfaceBakeData;
            }

            FlowFieldSurfaceResult result = FlowFieldSurfacePipeline.Prepare(new FlowFieldSurfaceRequest(
                _context,
                CreateSurfaceBakeSettings(),
                activeSurface,
                null,
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance));
            if (!result.IsReady)
                throw new ArgumentException($"Surface Bake 설정이 유효하지 않습니다: {result.Error}", nameof(_surfaceBakeData));

            pending |= FlowFieldDirtyFlags.StaticObstacles
                | FlowFieldDirtyFlags.DynamicObstacles
                | FlowFieldDirtyFlags.Escape
                | FlowFieldDirtyFlags.DefaultDirection
                | FlowFieldDirtyFlags.Goal
                | FlowFieldDirtyFlags.ModifierArea
                | FlowFieldDirtyFlags.FinalRegion;
            _context.DirtyFinalRegion = FlowFieldCellRect.Full(_context.Grid);
            _context.DirtyObstacleRegion = FlowFieldCellRect.Full(_context.Grid);
            MarkAllModifierAreasDirty();
        }

        private FlowFieldSurfaceBakeData EnsureStaticSurfaceBakeData()
        {
            if (_staticBakeData == null || !_staticBakeData.HasValidData)
                return null;

            FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
            if (_runtimeStaticSurfaceBakeData != null
                && _runtimeStaticSurfaceBakeData.HasValidData
                && _runtimeStaticSourceRevision == _staticBakeData.Revision
                && _runtimeStaticSurfaceBakeData.Matches(settings, out _))
                return _runtimeStaticSurfaceBakeData;

            DestroyTransientSurface(ref _runtimeStaticSurfaceBakeData);
            _runtimeStaticSurfaceBakeData = _staticBakeData.CreateSurfaceBakeData(settings);
            _runtimeStaticSourceRevision = _staticBakeData.Revision;
            return _runtimeStaticSurfaceBakeData;
        }

        private void UpdateBlockedWarning(bool hasWalkableCell)
        {
            if (!hasWalkableCell && !_obstaclePipeline.AllBlockedWarningIssued)
            {
                _obstaclePipeline.AllBlockedWarningIssued = true;
                Debug.LogWarning($"[{nameof(FlowFieldManager)}] Every valid surface cell is blocked.", this);
            }
            else if (hasWalkableCell)
            {
                _obstaclePipeline.AllBlockedWarningIssued = false;
            }
        }

        private void UpdateReadyState(bool ready, bool resultChanged)
        {
            _isReady = ready;
            if (!resultChanged)
                return;

            unchecked
            {
                _revision++;
            }

            FieldChanged?.Invoke();
        }

        private void CommitStagingWorkspace()
        {
            FlowFieldSurfaceBakeData sourceSurface = ActiveSurfaceBakeData;
            if (sourceSurface == null || !sourceSurface.HasValidData || !_context.Grid.IsValid)
                throw new InvalidOperationException("Cannot commit a FlowField without a valid staging surface.");

            if (_committedWorkspace.Capacity != _context.Grid.CellCount)
                _committedWorkspace.Resize(_context.Grid.CellCount);

            if (_committedSurfaceBakeData == null
                || !ReferenceEquals(_committedSourceSurfaceBakeData, sourceSurface)
                || _committedSourceSurfaceRevision != sourceSurface.Revision)
            {
                DestroyTransientSurface(ref _committedSurfaceBakeData);
                _committedSurfaceBakeData = sourceSurface.CreateTransientCopy();
                _committedSourceSurfaceBakeData = sourceSurface;
                _committedSourceSurfaceRevision = sourceSurface.Revision;
            }

            _committedWorkspace.CopyFrom(_context.Workspace);
            _committedGrid = _context.Grid;
        }

        private bool WorkspaceHasGoal()
            => _context.Workspace.HasActiveGoal
                && _context.Workspace.ResolvedGoalIndex >= 0;

        private int ResolveGoalX() => _resolvedGoalX;
        private int ResolveGoalZ() => _resolvedGoalZ;

        private void StartBfsSolve()
        {
            if (!WorkspaceHasGoal())
            {
                bool noGoalChanged = RebuildFinalField();
                if (noGoalChanged)
                    CommitStagingWorkspace();
                UpdateReadyState(true, noGoalChanged);
                return;
            }

            InitServices();
            if (_buildPipeline == null)
                throw new InvalidOperationException("FlowField build pipeline is not initialized.");

            int effectiveWaves = Mathf.Min(_context.Grid.CellCount, Mathf.Max(64, _maxGpuWaves));
            FlowFieldBfsRequest request = new FlowFieldBfsRequest(
                _context.Grid,
                ActiveSurfaceBakeData,
                _context.Workspace,
                true,
                ResolveGoalX(),
                ResolveGoalZ(),
                _goalInfluenceRadius,
                _context.Workspace.ResolvedGoalIndex,
                effectiveWaves,
                _inputVersion);

            // The runner may complete synchronously when Managed BFS is used,
            // so set the flag before entering it.
            _isRebuilding = true;
            bool accepted = _buildPipeline.StartBfs(request, OnBfsCompleted, OnBfsFailed);
            if (!accepted)
            {
                _isRebuilding = false;
                throw new InvalidOperationException("FlowField BFS session could not be started.");
            }
        }

        private void OnBfsCompleted(FlowFieldBfsRequest request)
        {
            _isRebuilding = false;
            if (_lifecycleState != LifecycleState.Initialized
                || request.Version != _inputVersion
                || _context.DirtyFlags != FlowFieldDirtyFlags.None
                || !_context.Grid.MatchesBounds(request.Grid))
            {
                _context.DirtyFlags |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
                return;
            }

            bool resultChanged = RebuildFinalField();
            CommitStagingWorkspace();
            UpdateReadyState(true, resultChanged);
        }

        private void OnBfsFailed(FlowFieldBfsRequest request, Exception exception)
        {
            _isRebuilding = false;
            if (_lifecycleState != LifecycleState.Initialized)
                return;

            if (request.Version != _inputVersion
                || _context.DirtyFlags != FlowFieldDirtyFlags.None
                || !_context.Grid.MatchesBounds(request.Grid))
            {
                _context.DirtyFlags |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
                return;
            }

            _fault = exception ?? new InvalidOperationException("Managed FlowField BFS failed.");
            _lifecycleState = LifecycleState.Faulted;
            Debug.LogError($"[{nameof(FlowFieldManager)}] Managed FlowField BFS failed: {_fault.Message}", this);
        }

        private void MarkDynamicObstacleRegionDirty(FlowFieldCellRect dirtyRect)
        {
            if (dirtyRect.IsValid)
            {
                _context.ExpandObstacleDirty(dirtyRect);
                _context.ExpandFinalDirty(dirtyRect);
            }

            _context.DirtyFlags |= FlowFieldDirtyFlags.DynamicObstacles
                | FlowFieldDirtyFlags.Escape
                | FlowFieldDirtyFlags.FinalRegion;
            _inputVersion++;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        private void DetectGridTransformChange()
        {
            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            FlowFieldGridSpace current = CreateGridSpace();
            bool changed = !_context.Grid.MatchesBounds(current)
                || _runtimeSurfaceBakeData != _context.Surface
                || (_runtimeSurfaceBakeData != null && _runtimeSurfaceBakeData.Revision != _context.LastSurfaceRevision);
            if (changed)
            {
                _context.DirtyFlags |= FlowFieldDirtyFlags.Grid;
                _inputVersion++;
            }
        }

        private void DetectGoalChange()
        {
            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            FlowFieldGoalChangeStatus status = _goalTracker.DetectChange(
                _context.Grid,
                _context.SurfaceReady,
                _goalTransform,
                _hasExplicitGoal,
                _explicitGoalWorld,
                _goalInfluenceRadius);
            if (status == FlowFieldGoalChangeStatus.Invalid)
                throw new InvalidOperationException("Active Goal became invalid.");

            if (status == FlowFieldGoalChangeStatus.Changed)
            {
                _context.MarkDirty(FlowFieldDirtyFlags.Goal);
                _inputVersion++;
            }
        }

        private void MarkGoalDirty()
        {
            _context.DirtyFlags |= FlowFieldDirtyFlags.Goal;
            _inputVersion++;
            _goalTracker.ResetWarning();
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
            ThrowIfUnavailable();
            if (!_isReady)
                throw new InvalidOperationException($"{nameof(FlowFieldManager)} is not ready.");
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));
            FlowFieldGridSpace sampleGrid = _isRebuilding && _committedGrid.IsValid
                ? _committedGrid
                : _context.Grid;
            FlowFieldSurfaceBakeData sampleSurface = _isRebuilding && _committedSurfaceBakeData != null
                ? _committedSurfaceBakeData
                : ActiveSurfaceBakeData;
            FlowFieldWorkspace sampleWorkspace = _isRebuilding && _committedWorkspace.Capacity > 0
                ? _committedWorkspace
                : _context.Workspace;
            if (!sampleGrid.ContainsWorldPosition(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition), "Position is outside the FlowField grid.");

            if (!FlowFieldCellSampler.TrySample(
                sampleGrid,
                sampleSurface,
                sampleWorkspace,
                worldPosition,
                out FlowFieldSample sample))
                throw new InvalidOperationException("FlowField sampling data is inconsistent.");

            if (sampleGrid.TryWorldToLocal(worldPosition, out int sampleX, out int sampleZ))
            {
                int sampleIndex = sampleGrid.ToFlatIndex(sampleX, sampleZ);
                // During an async rebuild the staging obstacle mask is the
                // only new input allowed to affect the old committed field:
                // if it blocks the sampled cell, stop until the new field is
                // committed. Directions themselves continue to come from the
                // committed workspace below.
                if (_isRebuilding
                    && _context.Workspace.Capacity == _context.Grid.CellCount
                    && sampleIndex < _context.Workspace.Blocked.Length
                    && _context.Workspace.Blocked[sampleIndex])
                    return new FlowFieldSample(
                        Vector3.zero,
                        0f,
                        sample.SurfaceNormal,
                        sample.HasSurface);

                int next = sampleWorkspace.NextCells[sampleIndex];
                if (next >= 0)
                {
                    FlowFieldWorkspace latestWorkspace = _context.Workspace;
                    FlowFieldSurfaceBakeData latestSurface = ActiveSurfaceBakeData;
                    bool nextBlocked = next >= sampleGrid.CellCount
                        || latestWorkspace.Capacity != _context.Grid.CellCount
                        || (next < latestWorkspace.Blocked.Length && latestWorkspace.Blocked[next])
                        || latestSurface == null
                        || !latestSurface.IsSurfaceValid(next);
                    bool topologyChanged = false;
                    if (!_isRebuilding && !nextBlocked && next != sampleIndex)
                    {
                        sampleGrid.FromFlatIndex(sampleIndex, out int currentX, out int currentZ);
                        sampleGrid.FromFlatIndex(next, out int nextX, out int nextZ);
                        int directionIndex = FlowFieldNeighborUtility.FindDirectionIndex(
                            nextX - currentX,
                            nextZ - currentZ);
                        topologyChanged = directionIndex < 0
                            || latestWorkspace.TopologyMasks == null
                            || sampleIndex >= latestWorkspace.TopologyMasks.Length
                            || (latestWorkspace.TopologyMasks[sampleIndex] & (1 << directionIndex)) == 0;
                    }

                    if (nextBlocked || topologyChanged)
                        return new FlowFieldSample(
                            Vector3.zero,
                            0f,
                            sample.SurfaceNormal,
                            sample.HasSurface);
                }
            }
            return sample;
        }

        public FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            ThrowIfUnavailable();
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));

            FlowFieldGridSpace clampGrid = _isRebuilding && _committedGrid.IsValid
                ? _committedGrid
                : _context.Grid;
            if (!clampGrid.IsValid)
                throw new InvalidOperationException("FlowField grid is not initialized.");
            Vector3 clampedPosition = clampGrid.ClampWorldXZ(worldPosition);
            return new FlowFieldClampResult(
                clampedPosition,
                !Mathf.Approximately(worldPosition.x, clampedPosition.x),
                !Mathf.Approximately(worldPosition.z, clampedPosition.z));
        }

        public void RegisterDynamicObstacle(Collider collider)
        {
            ThrowIfUnavailable();
            if (collider == null)
                throw new ArgumentNullException(nameof(collider));
            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            if (!_obstaclePipeline.RegisterDynamicObstacle(collider))
                throw new InvalidOperationException("Dynamic obstacle registration failed.");

            MarkDynamicObstacleRegionDirty(FlowFieldCellRect.FromBounds(_context.Grid, collider.bounds));
        }

        public void UnregisterDynamicObstacle(Collider collider)
        {
            ThrowIfUnavailable();
            if (collider == null)
                throw new ArgumentNullException(nameof(collider));
            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            Bounds bounds = collider.bounds;
            if (!_obstaclePipeline.UnregisterDynamicObstacle(collider))
                throw new InvalidOperationException("Dynamic obstacle is not registered.");

            MarkDynamicObstacleRegionDirty(FlowFieldCellRect.FromBounds(_context.Grid, bounds));
        }

        public void NotifyObstacleRegionDirty(Bounds worldBounds)
        {
            ThrowIfUnavailable();
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center) || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                throw new ArgumentOutOfRangeException(nameof(worldBounds));
            if (!_context.Grid.IsValid)
                throw new InvalidOperationException("FlowField grid is not initialized.");

            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
                return;

            MarkDynamicObstacleRegionDirty(FlowFieldCellRect.FromBounds(_context.Grid, worldBounds));
        }

        public void SetGoalPosition(Vector3 worldPosition)
            => SetGoalPosition(worldPosition, 0f);

        public void SetGoalPosition(Vector3 worldPosition, float influenceRadius)
        {
            ThrowIfUnavailable();
            if (!FlowFieldGoalPipeline.IsFiniteWorldXZ(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));
            if (!FlowFieldGridSpace.IsFinite(influenceRadius) || influenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(influenceRadius));

            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
                return;

            float resolvedRadius = influenceRadius;
            if (_goalTransform == null
                && _hasExplicitGoal
                && Mathf.Abs(_explicitGoalWorld.x - worldPosition.x) <= VALUE_EPSILON
                && Mathf.Abs(_explicitGoalWorld.z - worldPosition.z) <= VALUE_EPSILON
                && Mathf.Abs(_goalInfluenceRadius - resolvedRadius) <= VALUE_EPSILON)
                return;

            _goalTransform = null;
            _hasExplicitGoal = true;
            _explicitGoalWorld = worldPosition;
            _goalInfluenceRadius = resolvedRadius;
            MarkGoalDirty();
        }

        public void SetGoalTarget(Transform target)
            => SetGoalTarget(target, 0f);

        public void SetGoalTarget(Transform target, float influenceRadius)
        {
            ThrowIfUnavailable();
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            if (!FlowFieldGoalPipeline.IsFiniteWorldXZ(target.position))
                throw new ArgumentOutOfRangeException(nameof(target));
            if (!FlowFieldGridSpace.IsFinite(influenceRadius) || influenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(influenceRadius));

            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
                return;

            float resolvedRadius = influenceRadius;
            if (_goalTransform == target
                && !_hasExplicitGoal
                && Mathf.Abs(_goalInfluenceRadius - resolvedRadius) <= VALUE_EPSILON)
                return;

            _goalTransform = target;
            _hasExplicitGoal = false;
            _goalInfluenceRadius = resolvedRadius;
            MarkGoalDirty();
        }

        public void ClearGoal()
        {
            ThrowIfUnavailable();
            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            if (_goalTransform == null && !_hasExplicitGoal)
                return;

            _goalTransform = null;
            _hasExplicitGoal = false;
            MarkGoalDirty();
        }

        #endregion

        public void NotifySurfaceDirty()
        {
            ThrowIfUnavailable();
            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
                return;

            _context.DirtyFlags |= FlowFieldDirtyFlags.Grid;
            _context.ExpandFinalDirty(FlowFieldCellRect.Full(_context.Grid));
            _context.ExpandObstacleDirty(FlowFieldCellRect.Full(_context.Grid));
            unchecked
            {
                _inputVersion++;
            }
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        #region Bake Integration

        private FlowFieldSurfaceRequest CreateSurfaceRequest()
            => new FlowFieldSurfaceRequest(
                _context,
                CreateSurfaceBakeSettings(),
                CurrentBakeMode == FlowFieldBakeMode.StaticBaked
                    ? (_staticBakeData != null ? _staticBakeData.SurfaceBakeData : null)
                    : Application.isPlaying ? _runtimeSurfaceBakeData : EditorSurfaceBakeData,
                // Legacy StaticObstacleBakeData is intentionally not part of
                // the new mode selection. Dynamic mode discovers static
                // colliders through BuildStatic; StaticBaked loads its complete
                // mask from FlowFieldStaticBakeData.
                null,
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance);

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

        /// <summary>
        /// 편집기에서 현재 직렬화 설정으로 Bake 레이아웃을 진단합니다.
        /// 유효하지 않은 설정은 false라는 정상적인 검증 결과입니다.
        /// </summary>
        /// <returns>유효한 레이아웃이면 true, 아니면 false입니다.</returns>
        internal bool TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
            => FlowFieldBakeBoundsUtility.TryCreateWorldLayout(
                transform.position,
                _bakeBoundsLocal,
                _cellSize,
                out worldBounds,
                out grid);

        internal void SetBakeBoundsLocal(Bounds localBounds)
        {
            Bounds snapped = FlowFieldBakeBoundsUtility.SnapCenterAnchored(localBounds, _cellSize);
            if (FlowFieldBakeBoundsUtility.Approximately(_bakeBoundsLocal, snapped))
                return;

            _bakeBoundsLocal = snapped;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
            _inputVersion++;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        /// <summary>
        /// 편집기 HelpBox에 표시할 Surface Bake 유효성 진단입니다.
        /// </summary>
        /// <returns>모든 Bake 참조가 유효하면 true, 진단 오류가 있으면 false입니다.</returns>
        internal bool TryValidateSurfaceBake(out string reason)
            => TryValidateSurfaceBake(out reason, includeStaticGoal: true);

        /// <summary>
        /// Validates the baked geometry/signature. Static editor previews can
        /// deliberately omit the current Goal comparison because their
        /// displayed Goal is the immutable one stored in the snapshot.
        /// </summary>
        internal bool TryValidateSurfaceBake(out string reason, bool includeStaticGoal)
        {
            reason = string.Empty;
            if (!TryGetBakeLayout(out _, out _))
            {
                reason = "Bake Bounds 또는 Cell Size가 유효하지 않습니다.";
                return false;
            }

            if (CurrentBakeMode == FlowFieldBakeMode.StaticBaked)
            {
                if (_staticBakeData == null)
                {
                    reason = "StaticBaked 모드에는 FlowFieldStaticBakeData가 필요합니다.";
                    return false;
                }

                if (!FlowFieldBakeBoundsUtility.TryCreateWorldLayout(
                        transform.position,
                        _bakeBoundsLocal,
                        _cellSize,
                        out _,
                        out FlowFieldGridSpace staticGrid))
                {
                    reason = "Bake Bounds 또는 Cell Size가 유효하지 않습니다.";
                    return false;
                }

                if (!_staticBakeData.MatchesSurface(CreateSurfaceBakeSettings(), out reason))
                    return false;

                if (!_staticBakeData.Matches(
                    staticGrid,
                    null,
                    _obstacleLayer,
                    _obstacleCheckHeight,
                    _obstacleCheckCenterOffset,
                    _obstacleClearance,
                    out reason))
                    return false;

                // This diagnostic is intentionally editor-facing. Runtime
                // StaticBaked sessions continue to use the baked Goal even if
                // a serialized Transform was moved after the bake.
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

            return FlowFieldSurfacePipeline.TryValidate(CreateSurfaceRequest(), out reason);
        }

        internal void AssignSurfaceBakeData(FlowFieldSurfaceBakeData bakeData)
        {
            if (_surfaceBakeData == bakeData)
                return;

            _surfaceBakeData = bakeData;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
            _inputVersion++;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal void AssignStaticObstacleBakeData(FlowFieldStaticObstacleBakeData bakeData)
        {
            if (_staticObstacleBakeData == bakeData)
                return;

            _staticObstacleBakeData = bakeData;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
            _inputVersion++;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal void AssignStaticBakeData(FlowFieldStaticBakeData bakeData)
        {
            if (_staticBakeData == bakeData)
                return;

            _staticBakeData = bakeData;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
            _inputVersion++;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal void NotifySurfaceBakeChanged()
        {
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
            _inputVersion++;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        #endregion
    }
}
