using System;
using UnityEngine;
using UnityEngine.Scripting.APIUpdating;

namespace Supercent.Common.FlowField
{
    [DefaultExecutionOrder(-200)]
    [MovedFrom(true, "Supercent.XpHero.Actor.Enemy.FlowField", "Assembly-CSharp", "EnemyFlowFieldManager")]
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
        [SerializeField, Min(FlowFieldBakeBoundsUtility.MinCellSize)] private float _cellSize = 0.5f;
        [SerializeField] private LayerMask _groundBakeLayer = Physics.DefaultRaycastLayers;
        [SerializeField, Range(0f, 89f)] private float _maxSurfaceSlope = 45f;
        [SerializeField, Min(0f)] private float _maxStepHeight = 0.5f;
        [SerializeField] private FlowFieldSurfaceBakeData _surfaceBakeData;
        [SerializeField] private FlowFieldStaticObstacleBakeData _staticObstacleBakeData;
        [SerializeField] private FlowFieldCoarseTopologyData _coarseTopologyData;

        [Header("Hierarchical Goal")]
        [SerializeField, Min(2)] private int _coarseCellMultiplier = DefaultCoarseMultiplier;
        [SerializeField, Min(0)] private int _fineRingCoarseRadius = DefaultFineRingCoarseRadius;
        [SerializeField, Range(0f, 1f)] private float _coarseWalkableRatio = FlowFieldCoarseTopologyData.DefaultWalkableRatio;

        [Header("Obstacles")]
        [SerializeField] private LayerMask _obstacleLayer;
        [SerializeField, Min(0.01f)] private float _obstacleCheckHeight = 2f;
        [SerializeField] private float _obstacleCheckCenterOffset = 1f;
        [SerializeField, Min(0f), Tooltip("셀 영역 밖으로 장애물 판정을 확장하는 XZ 거리입니다.")]
        private float _obstacleClearance;
        [SerializeField, Tooltip("ON이면 obstacle layer 전수 스윕(미등록 보정). OFF면 Static bake + RegisterDynamicObstacle만 사용.")]
        private bool _enableUnregisteredObstacleSweep;
        [SerializeField, Min(MIN_REFRESH_RATE)] private float _refreshRate = 0.2f;

        [Header("Default Flow")]
        [SerializeField] private Vector3 _defaultFlowDirection = Vector3.forward;

        [Header("Goal")]
        [SerializeField] private Transform _goalTransform;
        [SerializeField, Min(0f), Tooltip("0은 Global hybrid Goal, 양수는 XYZ 구 형태의 Ranged Goal입니다.")]
        private float _goalInfluenceRadius;

        [Header("Editor Gizmos")]
        [SerializeField, Tooltip("Bake 표면, Obstacle, Goal, Modifier 영향 셀과 최종 3D 벡터를 표시합니다.")]
        private bool _showField;

        private readonly FlowFieldRuntimeContext _context = new FlowFieldRuntimeContext();
        private readonly FlowFieldObstaclePipeline _obstaclePipeline = new FlowFieldObstaclePipeline();

        private float _refreshTimer;
        private bool _hasExplicitGoal;
        private Vector3 _explicitGoalWorld;
        private bool _invalidGoalWarningIssued;
        private bool _invalidBakeWarningIssued;
        private bool _isReady;
        private int _revision;
        private readonly FlowFieldGoalTracker _goalTracker = new FlowFieldGoalTracker();

        public bool IsReady => _isReady;
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
            EnsureServices();
            _refreshTimer = 0f;
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
        }

        private void OnEnable()
        {
            EnsureServices();
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
        }

        private void OnValidate()
        {
            EnsureServices();
            _cellSize = FlowFieldBakeBoundsUtility.SanitizeCellSize(_cellSize);
            _bakeBoundsLocal = FlowFieldBakeBoundsUtility.SnapCenterAnchored(
                _bakeBoundsLocal,
                _cellSize);
            _maxSurfaceSlope = Mathf.Clamp(_maxSurfaceSlope, 0f, 89f);
            _maxStepHeight = Mathf.Max(0f, _maxStepHeight);
            _obstacleCheckHeight = Mathf.Max(0.01f, _obstacleCheckHeight);
            _obstacleClearance = Mathf.Max(0f, _obstacleClearance);
            _refreshRate = Mathf.Max(MIN_REFRESH_RATE, _refreshRate);
            _goalInfluenceRadius = FlowFieldGoalPipeline.SanitizeInfluenceRadius(_goalInfluenceRadius);
            _defaultFlowDirection = FlowFieldVectorUtility.SanitizeDefaultDirection(_defaultFlowDirection);
            _coarseCellMultiplier = Mathf.Max(2, _coarseCellMultiplier);
            _fineRingCoarseRadius = Mathf.Max(0, _fineRingCoarseRadius);
            _coarseWalkableRatio = Mathf.Clamp01(_coarseWalkableRatio);

#if UNITY_EDITOR
            InvalidateEditorPreview();
#endif

            if (Application.isPlaying)
                _context.DirtyFlags = FlowFieldDirtyFlags.All;
        }

        private void LateUpdate()
        {
            EnsureServices();
            _refreshTimer -= Time.deltaTime;
            if (_refreshTimer > 0f)
                return;

            _refreshTimer = Mathf.Max(MIN_REFRESH_RATE, _refreshRate);
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
            EnsureServices();
            if (_context.DirtyFlags != FlowFieldDirtyFlags.None)
                RebuildDirtyData();
        }

        private void EnsureServices()
        {
            if (_modifierRegistry != null)
                return;

            _modifierRegistry = new FlowFieldModifierRegistry();
            _modifierPipeline = new FlowFieldModifierPipeline(_modifierRegistry);
#if UNITY_EDITOR
            _editorPreview ??= new FlowFieldEditorPreview();
#endif
        }

        private void OnDisable()
        {
            _context.SurfaceReady = false;
            _context.HasObstacleMask = false;
            _context.Surface = null;
            _context.Workspace.ClearAll();
            _context.DirtyFlags = FlowFieldDirtyFlags.All;
            UpdateReadyState(false, resultChanged: false);
        }

        private void OnDestroy()
        {
            ClearModifierRuntimeState();
            _obstaclePipeline.ClearDynamicObstacles();
            _goalTracker.Clear();
#if UNITY_EDITOR
            DisposeEditorPreview();
#endif
            _context.Dispose();
        }

        private void RebuildDirtyData()
        {
            FlowFieldDirtyFlags pending = _context.DirtyFlags;
            _context.DirtyFlags = FlowFieldDirtyFlags.None;

            if (!TryEnsureGridAndSurface(ref pending))
            {
                FlushPendingModifierChanges();
                UpdateReadyState(false, resultChanged: false);
                return;
            }

            if ((pending & FlowFieldDirtyFlags.DefaultDirection) != 0)
            {
                _context.ResolvedDefaultDirection = FlowFieldVectorUtility.SanitizeDefaultDirection(_defaultFlowDirection);
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

        private bool TryEnsureGridAndSurface(ref FlowFieldDirtyFlags pending)
        {
            if ((pending & FlowFieldDirtyFlags.Grid) == 0 && _context.Grid.IsValid && _context.SurfaceReady)
                return true;

            FlowFieldSurfaceResult result = FlowFieldSurfacePipeline.Prepare(CreateSurfaceRequest());
            if (!result.IsReady)
            {
                DisableSurfaceField(result.Error);
                return false;
            }

            _invalidBakeWarningIssued = false;

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
            return true;
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

        private void DisableSurfaceField(string reason)
        {
            _context.SurfaceReady = false;
            _context.HasObstacleMask = false;
            _context.Surface = null;
            _context.LastSurfaceRevision = -1;
            _context.Workspace.ClearAll();
            if (_invalidBakeWarningIssued)
                return;

            _invalidBakeWarningIssued = true;
            Debug.LogError(
                $"[{nameof(FlowFieldManager)}] Surface Bake를 사용할 수 없어 Flow를 정지합니다: {reason}",
                this);
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
                WarnInvalidGoalOnce();

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
                        $"[{nameof(FlowFieldManager)}] Goal 범위에 이동 가능한 표면 셀이 없어 Default Flow를 사용합니다.",
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
            {
                WarnInvalidGoalOnce();
                return;
            }

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
            TryGetBakeLayout(out _, out FlowFieldGridSpace grid);
            return grid;
        }

        private void WarnInvalidGoalOnce()
        {
            if (_invalidGoalWarningIssued)
                return;

            _invalidGoalWarningIssued = true;
            Debug.LogWarning($"[{nameof(FlowFieldManager)}] 유효하지 않은 Goal 값은 무시합니다.", this);
        }
    }
}
