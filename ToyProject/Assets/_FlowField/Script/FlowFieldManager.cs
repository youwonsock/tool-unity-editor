using System;
using UnityEngine;

namespace Common.FlowField
{
    [DefaultExecutionOrder(-300)]
    public partial class FlowFieldManager : MonoBehaviour, IFlowFieldProvider, IFlowFieldController
    {
        private const float MIN_REFRESH_RATE = 0.05f;
        private const float VALUE_EPSILON = 0.0001f;
        private const int DefaultCoarseMultiplier = 4;
        private const int DefaultFineRingCoarseRadius = 1;

        [Header("Surface Bake")]
        [SerializeField, Tooltip("Manager 위치 기준 월드 축 정렬 Bake 영역입니다. XZ는 Grid, Y는 Ground Ray 범위입니다.")]
        private Bounds _bakeBoundsLocal = new Bounds(
            new Vector3(20f, 0f, 20f),
            new Vector3(40f, 10f, 40f));
        [SerializeField] private float _cellSize = 0.5f;
        [SerializeField] private LayerMask _groundBakeLayer = Physics.DefaultRaycastLayers;
        [SerializeField] private float _maxSurfaceSlope = 45f;
        [SerializeField] private float _maxStepHeight = 0.5f;
        [SerializeField] private FlowFieldSurfaceBakeData _surfaceBakeData;
        [SerializeField] private FlowFieldStaticObstacleBakeData _staticObstacleBakeData;
        [SerializeField] private FlowFieldCoarseTopologyData _coarseTopologyData;

        [Header("Hierarchical Goal")]
        [SerializeField] private int _coarseCellMultiplier = DefaultCoarseMultiplier;
        [SerializeField] private int _fineRingCoarseRadius = DefaultFineRingCoarseRadius;
        [SerializeField] private float _coarseWalkableRatio = FlowFieldCoarseTopologyData.DefaultWalkableRatio;

        [Header("Obstacles")]
        [SerializeField] private LayerMask _obstacleLayer;
        [SerializeField] private float _obstacleCheckHeight = 2f;
        [SerializeField] private float _obstacleCheckCenterOffset = 1f;
        [SerializeField, Tooltip("셀 영역 밖으로 장애물 판정을 확장하는 XZ 거리입니다.")]
        private float _obstacleClearance;
        [SerializeField, Tooltip("ON이면 obstacle layer 전수 스윕(미등록 보정). OFF면 Static bake + RegisterDynamicObstacle만 사용.")]
        private bool _enableUnregisteredObstacleSweep;
        [SerializeField] private float _refreshRate = 0.2f;

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

        private float _refreshTimer;
        private bool _hasExplicitGoal;
        private Vector3 _explicitGoalWorld;
        private bool _isReady;
        private int _revision;
        private LifecycleState _lifecycleState;
        private Exception _fault;
        private bool _requiresActivationRebuild;
        private bool _configurationStale;
        private readonly FlowFieldGoalTracker _goalTracker = new FlowFieldGoalTracker();

        public bool IsInitialized => _lifecycleState == LifecycleState.Initialized;
        public bool IsFaulted => _lifecycleState == LifecycleState.Faulted;
        public bool IsReady => _lifecycleState == LifecycleState.Initialized
            && !_configurationStale
            && _isReady;
        public int Revision => _revision;
        public event Action FieldChanged;

        internal FlowFieldSurfaceBakeData SurfaceBakeData => _surfaceBakeData;
        internal FlowFieldStaticObstacleBakeData StaticObstacleBakeData => _staticObstacleBakeData;
        internal FlowFieldCoarseTopologyData CoarseTopologyData => _coarseTopologyData;
        internal Bounds BakeBoundsLocal => _bakeBoundsLocal;
        internal float CellSize => _cellSize;
        internal int CoarseCellMultiplier => _coarseCellMultiplier;
        internal float CoarseWalkableRatio => _coarseWalkableRatio;
        internal LayerMask ObstacleLayer => _obstacleLayer;
        internal float ObstacleCheckHeight => _obstacleCheckHeight;
        internal float ObstacleCheckCenterOffset => _obstacleCheckCenterOffset;
        internal float ObstacleClearance => _obstacleClearance;

        private void Reset()
        {
            _bakeBoundsLocal = FlowFieldBakeBoundsUtility.DefaultLocalBounds;
            _cellSize = 0.5f;
            _groundBakeLayer = Physics.DefaultRaycastLayers;
            _coarseCellMultiplier = DefaultCoarseMultiplier;
            _fineRingCoarseRadius = DefaultFineRingCoarseRadius;
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
            // enabled again, allow the next FixedUpdate to perform the
            // explicit rebuild requested by OnDisable.  Rebuild at this
            // lifecycle boundary instead of relying on a hidden recovery
            // path in the next update tick.
            if (Application.isPlaying
                && _lifecycleState == LifecycleState.Initialized
                && _requiresActivationRebuild)
            {
                _requiresActivationRebuild = false;
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
            DetectGoalChange();
            DetectModifierChanges();
            if (_obstaclePipeline.DetectDynamicTransformsChanged())
                _context.DirtyFlags |= FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape;
            if (_enableUnregisteredObstacleSweep && _context.SurfaceReady)
                _context.DirtyFlags |= FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Escape;
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
            _context.SurfaceReady = false;
            _context.HasObstacleMask = false;
            _context.Surface = null;
            _context.Workspace.ClearAll();
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
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
            ClearModifierRuntimeState();
            _obstaclePipeline.ClearDynamicObstacles();
            _goalTracker.Clear();
#if UNITY_EDITOR
            ReleaseEditorPreview();
#endif
            _context.Release();
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
            if (_coarseCellMultiplier < 2 || _fineRingCoarseRadius < 0
                || !FlowFieldGridSpace.IsFinite(_coarseWalkableRatio)
                || _coarseWalkableRatio < 0f || _coarseWalkableRatio > 1f)
                throw new ArgumentOutOfRangeException(nameof(_coarseCellMultiplier), "Hierarchical goal settings are out of range.");
            if (!FlowFieldGridSpace.IsFinite(_goalInfluenceRadius) || _goalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(_goalInfluenceRadius));
            if (!FlowFieldBakeBoundsUtility.TryCreateWorldLayout(transform.position, _bakeBoundsLocal, _cellSize, out _, out FlowFieldGridSpace grid)
                || !grid.IsValid)
                throw new ArgumentException("Bake Bounds and Cell Size do not produce a valid grid.", nameof(_bakeBoundsLocal));
            if (_surfaceBakeData == null)
                throw new InvalidOperationException("FlowFieldManager requires a serialized Surface Bake asset.");
            if (!_surfaceBakeData.HasValidData)
                throw new ArgumentException("Surface bake asset data is invalid.", nameof(_surfaceBakeData));
            if (_surfaceBakeData.CellCount != grid.CellCount)
                throw new ArgumentException("Surface bake cell count does not match the configured grid.", nameof(_surfaceBakeData));
            if (_staticObstacleBakeData != null)
            {
                if (!_staticObstacleBakeData.HasValidData)
                    throw new ArgumentException("Static obstacle bake asset data is invalid.", nameof(_staticObstacleBakeData));
                if (_staticObstacleBakeData.CellCount != grid.CellCount)
                    throw new ArgumentException("Static obstacle bake cell count does not match the configured grid.", nameof(_staticObstacleBakeData));
            }
            if (_coarseTopologyData != null)
            {
                if (!_coarseTopologyData.HasValidData)
                    throw new ArgumentException("Coarse topology bake asset data is invalid.", nameof(_coarseTopologyData));
                int expectedCoarseWidth = (int)(((long)grid.Width + _coarseCellMultiplier - 1L) / _coarseCellMultiplier);
                int expectedCoarseDepth = (int)(((long)grid.Depth + _coarseCellMultiplier - 1L) / _coarseCellMultiplier);
                if (_coarseTopologyData.CoarseWidth != expectedCoarseWidth
                    || _coarseTopologyData.CoarseDepth != expectedCoarseDepth
                    || _coarseTopologyData.CoarseMultiplier != _coarseCellMultiplier)
                    throw new ArgumentException("Coarse topology dimensions do not match the configured grid.", nameof(_coarseTopologyData));
            }
            if (_surfaceBakeData != null && _surfaceBakeData.HasValidData)
            {
                for (int index = 0; index < grid.CellCount; index++)
                {
                    if (_surfaceBakeData.IsSurfaceValid(index))
                    {
                        Vector3 surfaceCenter = _surfaceBakeData.GetCellCenter(grid, index);
                        if (!FlowFieldGridSpace.IsFinite(surfaceCenter))
                            throw new ArgumentException("Surface bake contains a non-finite height.", nameof(_surfaceBakeData));
                        _surfaceBakeData.GetSurfaceNormal(index);
                    }
                }
            }
        }

        private void RebuildDirtyData()
        {
            FlowFieldDirtyFlags pending = _context.DirtyFlags;
            _context.DirtyFlags = FlowFieldDirtyFlags.None;

            PrepareGridAndSurface(ref pending);

            if ((pending & FlowFieldDirtyFlags.DefaultDirection) != 0)
            {
                _context.ResolvedDefaultDirection = FlowFieldVectorUtility.NormalizeDefaultDirection(_defaultFlowDirection);
                MarkFinalDirtyFull(ref pending);
            }

            if (RebuildObstacleMasks(pending))
                CommitObstaclesAndMarkGoalDirty(ref pending);

            if ((pending & FlowFieldDirtyFlags.Goal) != 0)
            {
                RebuildGoalData();
                MarkFinalDirtyFull(ref pending);
            }

            if ((pending & FlowFieldDirtyFlags.ModifierArea) != 0 && RebuildModifierAreaData())
                MarkFinalDirtyFull(ref pending);

            if ((pending & FlowFieldDirtyFlags.ModifierValue) != 0)
                MarkFinalDirtyFull(ref pending);

            bool resultChanged = false;
            if ((pending & FlowFieldDirtyFlags.FinalRegion) != 0)
                resultChanged = RebuildFinalField();

            FlushPendingModifierChanges();
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

            FlowFieldSurfaceResult result = FlowFieldSurfacePipeline.Prepare(CreateSurfaceRequest());
            if (!result.IsReady)
                throw new ArgumentException($"Surface Bake 설정이 유효하지 않습니다: {result.Error}", nameof(_surfaceBakeData));

            // Native job storage is part of the explicit Manager rebuild contract.
            // Job execution never allocates or repairs a missing workspace.
            if (!_context.Workspace.HasNative)
                _context.Workspace.Init(_context.Grid.CellCount);

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

        private bool RebuildObstacleMasks(FlowFieldDirtyFlags pending)
        {
            bool rebuildStatic = (pending & FlowFieldDirtyFlags.StaticObstacles) != 0;
            bool rebuildDynamic = (pending & FlowFieldDirtyFlags.DynamicObstacles) != 0;
            if (!rebuildStatic && !rebuildDynamic)
                return false;

            FlowFieldObstacleRequest request = new FlowFieldObstacleRequest(
                _context.Grid,
                _surfaceBakeData,
                _staticObstacleBakeData,
                _context.Workspace,
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance,
                _enableUnregisteredObstacleSweep,
                _context.DirtyObstacleRegion);
            FlowFieldObstacleResult result = _obstaclePipeline.RebuildMasks(
                request,
                rebuildStatic,
                rebuildDynamic);
            if (result.MaskChanged && result.DirtyRegion.IsValid)
                _context.ExpandObstacleDirty(result.DirtyRegion);
            _context.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
            return result.MaskChanged;
        }

        private void CommitObstaclesAndMarkGoalDirty(ref FlowFieldDirtyFlags pending)
        {
            _obstaclePipeline.CommitCombinedAndBuildEscape(
                _context.Grid,
                _surfaceBakeData,
                _context.Workspace,
                out bool hasWalkable);
            _context.HasObstacleMask = true;
            UpdateBlockedWarning(hasWalkable);
            pending |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
            _context.ExpandFinalDirty(FlowFieldCellRect.Full(_context.Grid));
            _context.DirtyObstacleRegion = FlowFieldCellRect.Invalid;
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
            bool stateChanged = _isReady != ready;
            _isReady = ready;
            if (!stateChanged && !resultChanged)
                return;

            unchecked
            {
                _revision++;
            }

            FieldChanged?.Invoke();
        }

        private void RebuildGoalData()
        {
            FlowFieldGoalResolution resolution = FlowFieldGoalPipeline.Resolve(
                _context.Grid,
                _goalTransform,
                _hasExplicitGoal,
                _explicitGoalWorld,
                _goalInfluenceRadius);
            if (!resolution.IsValid && resolution.HasActiveGoal)
                throw new InvalidOperationException("Active Goal resolution is invalid.");

            FlowFieldGoalBuildStatus status = FlowFieldGoalPipeline.Build(
                resolution,
                _context.Grid,
                _surfaceBakeData,
                _coarseTopologyData,
                _context.Workspace,
                _fineRingCoarseRadius,
                _goalTracker);
            if (status == FlowFieldGoalBuildStatus.NoWalkableSurface)
            {
                if (_goalTracker.TryConsumeMissingWalkableWarning())
                    Debug.LogWarning(
                        $"[{nameof(FlowFieldManager)}] Goal 범위에 이동 가능한 표면 셀이 없습니다.",
                        this);
            }
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
                | FlowFieldDirtyFlags.GoalFine
                | FlowFieldDirtyFlags.FinalRegion;
        }

        private void DetectGridTransformChange()
        {
            FlowFieldGridSpace current = CreateGridSpace();
            if (!_context.Grid.MatchesBounds(current)
                || _surfaceBakeData != _context.Surface
                || (_surfaceBakeData != null && _surfaceBakeData.Revision != _context.LastSurfaceRevision)
                || (_staticObstacleBakeData != null
                    && _staticObstacleBakeData.Revision != _context.LastStaticObstacleRevision)
                || (_coarseTopologyData != null
                    && _coarseTopologyData.Revision != _context.LastCoarseRevision))
                _context.DirtyFlags |= FlowFieldDirtyFlags.Grid;
        }

        private void DetectGoalChange()
        {
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
                _context.MarkDirty(FlowFieldDirtyFlags.Goal);
        }

        private void MarkGoalDirty()
        {
            _context.DirtyFlags |= FlowFieldDirtyFlags.Goal;
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
            if (!_context.Grid.ContainsWorldPosition(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition), "Position is outside the FlowField grid.");

            if (!FlowFieldBilinearSampler.TrySample(
                _context.Grid,
                _surfaceBakeData,
                _context.Workspace,
                worldPosition,
                out FlowFieldSample sample,
                out _,
                out _,
                out _,
                out _,
                out _))
                throw new InvalidOperationException("FlowField sampling data is inconsistent.");
            return sample;
        }

        public FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            ThrowIfUnavailable();
            if (!_context.Grid.IsValid)
                throw new InvalidOperationException("FlowField grid is not initialized.");
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));

            Vector3 clampedPosition = _context.Grid.ClampWorldXZ(worldPosition);
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
            if (!_obstaclePipeline.RegisterDynamicObstacle(collider))
                throw new InvalidOperationException("Dynamic obstacle registration failed.");

            MarkDynamicObstacleRegionDirty(FlowFieldCellRect.FromBounds(_context.Grid, collider.bounds));
        }

        public void UnregisterDynamicObstacle(Collider collider)
        {
            ThrowIfUnavailable();
            if (collider == null)
                throw new ArgumentNullException(nameof(collider));
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
            if (_goalTransform == null && !_hasExplicitGoal)
                return;

            _goalTransform = null;
            _hasExplicitGoal = false;
            MarkGoalDirty();
        }

        #endregion

        #region Bake Integration

        private FlowFieldSurfaceRequest CreateSurfaceRequest()
            => new FlowFieldSurfaceRequest(
                _context,
                CreateSurfaceBakeSettings(),
                _surfaceBakeData,
                _staticObstacleBakeData,
                _coarseTopologyData,
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance,
                _coarseCellMultiplier,
                _coarseWalkableRatio);

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
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        /// <summary>
        /// 편집기 HelpBox에 표시할 Surface Bake 유효성 진단입니다.
        /// </summary>
        /// <returns>모든 Bake 참조가 유효하면 true, 진단 오류가 있으면 false입니다.</returns>
        internal bool TryValidateSurfaceBake(out string reason)
        {
            reason = string.Empty;
            if (!TryGetBakeLayout(out _, out _))
            {
                reason = "Bake Bounds 또는 Cell Size가 유효하지 않습니다.";
                return false;
            }

            return FlowFieldSurfacePipeline.TryValidate(CreateSurfaceRequest(), out reason);
        }

        internal void AssignSurfaceBakeData(FlowFieldSurfaceBakeData bakeData)
        {
            if (_surfaceBakeData == bakeData)
                return;

            _surfaceBakeData = bakeData;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
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
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal void AssignCoarseTopologyData(FlowFieldCoarseTopologyData bakeData)
        {
            if (_coarseTopologyData == bakeData)
                return;

            _coarseTopologyData = bakeData;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        internal void NotifySurfaceBakeChanged()
        {
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif
        }

        #endregion
    }
}
