using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Immutable geometry snapshot consumed by every build stage.  Runtime
    /// raycasts and serialized static snapshots both produce this same model;
    /// no calculation stage depends on a ScriptableObject.
    /// </summary>
    internal sealed class FlowFieldSurfaceData
    {
        private const byte VALID_SURFACE = 1 << 0;
        private const float NORMAL_EPSILON_SQR = 0.000001f;

        private readonly FlowFieldGridSpace _grid;
        private readonly Bounds _bakeBounds;
        private readonly LayerMask _groundLayer;
        private readonly float _maxSurfaceSlope;
        private readonly float _maxStepHeight;
        private readonly float[] _surfaceHeights;
        private readonly Vector3[] _surfaceNormals;
        private readonly byte[] _surfaceFlags;
        private readonly byte[] _neighborMasks;
        private readonly float _minHeight;
        private readonly float _maxHeight;
        private readonly bool _hasHeightRange;

        internal FlowFieldGridSpace Grid => _grid;
        internal int CellCount => _grid.CellCount;
        internal int ValidCellCount { get; }
        internal int Revision { get; }
        internal Bounds BakeBoundsWorld => _bakeBounds;
        internal LayerMask GroundLayer => _groundLayer;
        internal float MaxSurfaceSlope => _maxSurfaceSlope;
        internal float MaxStepHeight => _maxStepHeight;
        internal bool IsValid { get; }

        private FlowFieldSurfaceData(
            in FlowFieldSurfaceBakeSettings settings,
            float[] surfaceHeights,
            Vector3[] surfaceNormals,
            byte[] surfaceFlags,
            byte[] neighborMasks,
            int validCellCount,
            int revision,
            bool copyArrays)
        {
            if (!settings.IsValid)
                throw new ArgumentException("Surface settings are invalid.", nameof(settings));
            if (surfaceHeights == null
                || surfaceNormals == null
                || surfaceFlags == null
                || neighborMasks == null
                || surfaceHeights.Length != settings.Grid.CellCount
                || surfaceNormals.Length != settings.Grid.CellCount
                || surfaceFlags.Length != settings.Grid.CellCount
                || neighborMasks.Length != settings.Grid.CellCount)
                throw new ArgumentException("Surface arrays do not match the grid.");

            _grid = settings.Grid;
            _bakeBounds = settings.BakeBounds;
            _groundLayer = settings.GroundLayer;
            _maxSurfaceSlope = settings.MaxSurfaceSlope;
            _maxStepHeight = settings.MaxStepHeight;
            _surfaceHeights = copyArrays ? (float[])surfaceHeights.Clone() : surfaceHeights;
            _surfaceNormals = copyArrays ? (Vector3[])surfaceNormals.Clone() : surfaceNormals;
            _surfaceFlags = copyArrays ? (byte[])surfaceFlags.Clone() : surfaceFlags;
            _neighborMasks = copyArrays ? (byte[])neighborMasks.Clone() : neighborMasks;
            ValidCellCount = validCellCount;
            Revision = revision;
            IsValid = validCellCount > 0 && ValidateArrays(out _minHeight, out _maxHeight, out _hasHeightRange);
        }

        internal static FlowFieldSurfaceData FromRuntime(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeResult result,
            int revision)
        {
            if (result == null)
                throw new ArgumentNullException(nameof(result));
            if (!result.IsValidFor(settings.Grid.CellCount))
                throw new ArgumentException("Surface bake result does not match the grid.", nameof(result));
            return new FlowFieldSurfaceData(
                settings,
                result.SurfaceHeights,
                result.SurfaceNormals,
                result.CellFlags,
                result.NeighborMasks,
                result.ValidCellCount,
                revision,
                copyArrays: false);
        }

        internal static FlowFieldSurfaceData FromSnapshot(
            in FlowFieldSurfaceBakeSettings settings,
            float[] surfaceHeights,
            Vector3[] surfaceNormals,
            byte[] surfaceFlags,
            byte[] neighborMasks,
            int validCellCount,
            int revision)
            => new FlowFieldSurfaceData(
                settings,
                surfaceHeights,
                surfaceNormals,
                surfaceFlags,
                neighborMasks,
                validCellCount,
                revision,
                copyArrays: true);

        internal bool Matches(in FlowFieldSurfaceBakeSettings settings, out string reason)
        {
            reason = string.Empty;
            if (!settings.IsValid)
            {
                reason = "Surface settings are invalid.";
                return false;
            }
            if (!_grid.MatchesBounds(settings.Grid)
                || !FlowFieldBakeBoundsUtility.Approximately(_bakeBounds, settings.BakeBounds)
                || _groundLayer.value != settings.GroundLayer.value
                || !Approximately(_maxSurfaceSlope, settings.MaxSurfaceSlope)
                || !Approximately(_maxStepHeight, settings.MaxStepHeight))
            {
                reason = "Surface signature does not match the requested settings.";
                return false;
            }
            return IsValid;
        }

        internal bool ContentEquals(FlowFieldSurfaceData other)
        {
            if (other == null
                || !_grid.MatchesBounds(other._grid)
                || !FlowFieldBakeBoundsUtility.Approximately(_bakeBounds, other._bakeBounds)
                || _groundLayer.value != other._groundLayer.value
                || !Approximately(_maxSurfaceSlope, other._maxSurfaceSlope)
                || !Approximately(_maxStepHeight, other._maxStepHeight)
                || _surfaceHeights.Length != other._surfaceHeights.Length)
                return false;

            for (int index = 0; index < _surfaceHeights.Length; index++)
            {
                if (_surfaceFlags[index] != other._surfaceFlags[index]
                    || _neighborMasks[index] != other._neighborMasks[index]
                    || !Mathf.Approximately(_surfaceHeights[index], other._surfaceHeights[index])
                    || _surfaceNormals[index] != other._surfaceNormals[index])
                    return false;
            }
            return true;
        }

        internal bool IsSurfaceValid(int index)
            => IsValid
                && index >= 0
                && index < _surfaceFlags.Length
                && (_surfaceFlags[index] & VALID_SURFACE) != 0;

        internal Vector3 GetCellCenter(FlowFieldGridSpace grid, int index)
        {
            if (index < 0 || index >= _surfaceHeights.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            grid.FromFlatIndex(index, out int x, out int z);
            Vector3 center = grid.LocalToWorldCenter(x, z);
            center.y = _surfaceHeights[index];
            return center;
        }

        internal Vector3 GetSurfaceNormal(int index)
        {
            if (!IsSurfaceValid(index))
                return Vector3.zero;
            Vector3 normal = _surfaceNormals[index];
            if (!FlowFieldGridSpace.IsFinite(normal) || normal.sqrMagnitude <= NORMAL_EPSILON_SQR)
                throw new ArgumentException("Surface contains an invalid normal.", nameof(index));
            return normal.normalized;
        }

        internal byte GetNeighborMask(int index)
            => IsSurfaceValid(index) ? _neighborMasks[index] : (byte)0;

        internal void CopyToArrays(
            out float[] heights,
            out Vector3[] normals,
            out byte[] flags,
            out byte[] neighborMasks)
        {
            heights = (float[])_surfaceHeights.Clone();
            normals = (Vector3[])_surfaceNormals.Clone();
            flags = (byte[])_surfaceFlags.Clone();
            neighborMasks = (byte[])_neighborMasks.Clone();
        }

        internal bool HasConnection(int index, int directionIndex)
            => directionIndex >= 0
                && directionIndex < FlowFieldNeighborUtility.Count
                && (GetNeighborMask(index) & (1 << directionIndex)) != 0;

        internal bool TryGetHeightRange(out float minHeight, out float maxHeight)
        {
            minHeight = _minHeight;
            maxHeight = _maxHeight;
            return _hasHeightRange;
        }

        private bool ValidateArrays(out float minHeight, out float maxHeight, out bool hasHeightRange)
        {
            minHeight = float.PositiveInfinity;
            maxHeight = float.NegativeInfinity;
            hasHeightRange = false;
            if (_surfaceHeights.Length != CellCount
                || _surfaceNormals.Length != CellCount
                || _surfaceFlags.Length != CellCount
                || _neighborMasks.Length != CellCount)
                return false;

            int validCount = 0;
            for (int index = 0; index < CellCount; index++)
            {
                if ((_surfaceFlags[index] & ~VALID_SURFACE) != 0)
                    return false;
                if (!FlowFieldGridSpace.IsFinite(_surfaceHeights[index])
                    || !FlowFieldGridSpace.IsFinite(_surfaceNormals[index]))
                    return false;
                if (!IsSurfaceValidUnchecked(index))
                {
                    if (_neighborMasks[index] != 0)
                        return false;
                    continue;
                }

                Vector3 normal = _surfaceNormals[index];
                if (normal.sqrMagnitude <= NORMAL_EPSILON_SQR
                    || !FlowFieldGridSpace.IsFinite(normal)
                    || Vector3.Dot(normal, Vector3.up) <= 0f)
                    return false;
                validCount++;
                minHeight = Mathf.Min(minHeight, _surfaceHeights[index]);
                maxHeight = Mathf.Max(maxHeight, _surfaceHeights[index]);
            }

            if (validCount != ValidCellCount)
                return false;
            hasHeightRange = validCount > 0;
            return true;
        }

        private bool IsSurfaceValidUnchecked(int index)
            => (_surfaceFlags[index] & VALID_SURFACE) != 0;

        private static bool Approximately(float left, float right)
            => Mathf.Abs(left - right) <= 0.0001f;
    }

    internal interface IFlowFieldSurfaceSource
    {
        FlowFieldSurfaceData Build(in FlowFieldSurfaceBakeSettings settings, string name);
    }

    internal sealed class FlowFieldRaycastSurfaceSource : IFlowFieldSurfaceSource
    {
        internal static readonly FlowFieldRaycastSurfaceSource Instance = new FlowFieldRaycastSurfaceSource();
        internal int BuildCount { get; private set; }
        private int _revision;

        private FlowFieldRaycastSurfaceSource()
        {
        }

        public FlowFieldSurfaceData Build(in FlowFieldSurfaceBakeSettings settings, string name)
        {
            BuildCount++;
            FlowFieldSurfaceBakeResult result = FlowFieldSurfaceBaker.Bake(settings);
            return FlowFieldSurfaceData.FromRuntime(settings, result, ++_revision);
        }
    }

    internal sealed class FlowFieldFixedSurfaceSource : IFlowFieldSurfaceSource
    {
        private readonly FlowFieldSurfaceData _surface;
        internal int BuildCount { get; private set; }

        internal FlowFieldFixedSurfaceSource(FlowFieldSurfaceData surface)
        {
            _surface = surface ?? throw new ArgumentNullException(nameof(surface));
        }

        public FlowFieldSurfaceData Build(in FlowFieldSurfaceBakeSettings settings, string name)
        {
            BuildCount++;
            if (!_surface.Matches(settings, out string reason))
                throw new InvalidOperationException(reason);
            return _surface;
        }
    }

    internal enum FlowFieldSessionSourceKind
    {
        SceneBuild,
        StaticSnapshot,
    }

    internal enum FlowFieldSessionLifecycle
    {
        Uninitialized,
        Active,
        Building,
        Suspended,
        Faulted,
        Released,
    }

    internal enum FlowFieldBfsBackendPolicy
    {
        PreferGpu,
        ManagedOnly,
        RequireGpu,
    }

    [Flags]
    internal enum FlowFieldDirtyFlags : ushort
    {
        None = 0,
        Grid = 1 << 0,
        StaticObstacles = 1 << 1,
        DynamicObstacles = 1 << 2,
        Escape = 1 << 3,
        DefaultDirection = 1 << 4,
        Goal = 1 << 5,
        ModifierArea = 1 << 6,
        ModifierValue = 1 << 7,
        FinalRegion = 1 << 8,
        Obstacles = StaticObstacles | DynamicObstacles,
        All = Grid | StaticObstacles | DynamicObstacles | Escape
            | DefaultDirection | Goal | ModifierArea | ModifierValue | FinalRegion,
    }

    internal readonly struct FlowFieldSessionRequest
    {
        internal FlowFieldSessionSourceKind SourceKind { get; }
        internal FlowFieldSurfaceBakeSettings SurfaceSettings { get; }
        internal FlowFieldStaticBakeSnapshot StaticBakeSnapshot { get; }
        internal LayerMask ObstacleLayer { get; }
        internal float ObstacleCheckHeight { get; }
        internal float ObstacleCheckCenterOffset { get; }
        internal float ObstacleClearance { get; }
        internal bool UseUnregisteredObstacleSweep { get; }
        internal FlowFieldGoalResolution Goal { get; }
        internal Vector3 DefaultDirection { get; }
        internal FlowFieldDirtyFlags DirtyFlags { get; }
        internal FlowFieldCellRect DirtyFinalRegion { get; }
        internal FlowFieldCellRect DirtyObstacleRegion { get; }
        internal int MaxGpuWaves { get; }
        internal string SurfaceName { get; }

        private FlowFieldSessionRequest(
            FlowFieldSessionSourceKind sourceKind,
            in FlowFieldSurfaceBakeSettings surfaceSettings,
            FlowFieldStaticBakeSnapshot staticBakeSnapshot,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance,
            bool useUnregisteredObstacleSweep,
            in FlowFieldGoalResolution goal,
            Vector3 defaultDirection,
            FlowFieldDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion,
            int maxGpuWaves,
            string surfaceName)
        {
            SourceKind = sourceKind;
            SurfaceSettings = surfaceSettings;
            StaticBakeSnapshot = staticBakeSnapshot;
            ObstacleLayer = obstacleLayer;
            ObstacleCheckHeight = obstacleCheckHeight;
            ObstacleCheckCenterOffset = obstacleCheckCenterOffset;
            ObstacleClearance = obstacleClearance;
            UseUnregisteredObstacleSweep = useUnregisteredObstacleSweep;
            Goal = goal;
            DefaultDirection = defaultDirection;
            DirtyFlags = dirtyFlags;
            DirtyFinalRegion = dirtyFinalRegion;
            DirtyObstacleRegion = dirtyObstacleRegion;
            MaxGpuWaves = maxGpuWaves;
            SurfaceName = surfaceName;
        }

        internal static FlowFieldSessionRequest ForSceneBuild(
            in FlowFieldSurfaceBakeSettings settings,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance,
            bool useUnregisteredObstacleSweep,
            in FlowFieldGoalResolution goal,
            Vector3 defaultDirection,
            FlowFieldDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion,
            int maxGpuWaves,
            string surfaceName)
            => new FlowFieldSessionRequest(
                FlowFieldSessionSourceKind.SceneBuild,
                settings,
                null,
                obstacleLayer,
                obstacleCheckHeight,
                obstacleCheckCenterOffset,
                obstacleClearance,
                useUnregisteredObstacleSweep,
                goal,
                defaultDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion,
                maxGpuWaves,
                surfaceName);

        internal static FlowFieldSessionRequest ForStaticSnapshot(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldStaticBakeSnapshot staticBakeSnapshot,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance,
            Vector3 defaultDirection,
            FlowFieldDirtyFlags dirtyFlags,
            int maxGpuWaves,
            string surfaceName)
            => new FlowFieldSessionRequest(
                FlowFieldSessionSourceKind.StaticSnapshot,
                settings,
                staticBakeSnapshot,
                obstacleLayer,
                obstacleCheckHeight,
                obstacleCheckCenterOffset,
                obstacleClearance,
                false,
                FlowFieldGoalResolution.None,
                defaultDirection,
                dirtyFlags,
                FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid,
                maxGpuWaves,
                surfaceName);

        internal bool HasSameInputs(in FlowFieldSessionRequest other)
            => HasSameBaseInputs(other)
                && VectorMatch(DefaultDirection, other.DefaultDirection)
                && MaxGpuWaves == other.MaxGpuWaves;

        internal bool HasSameBaseInputs(in FlowFieldSessionRequest other)
            => SourceKind == other.SourceKind
                && SurfaceSettingsMatch(SurfaceSettings, other.SurfaceSettings)
                && ReferenceEquals(StaticBakeSnapshot, other.StaticBakeSnapshot)
                && (StaticBakeSnapshot == null
                    || StaticBakeSnapshot.Revision == other.StaticBakeSnapshot.Revision)
                && ObstacleLayer.value == other.ObstacleLayer.value
                && Approximately(ObstacleCheckHeight, other.ObstacleCheckHeight)
                && Approximately(ObstacleCheckCenterOffset, other.ObstacleCheckCenterOffset)
                && Approximately(ObstacleClearance, other.ObstacleClearance)
                && UseUnregisteredObstacleSweep == other.UseUnregisteredObstacleSweep
                && GoalMatch(Goal, other.Goal);

        internal bool HasSameFinalInputs(in FlowFieldSessionRequest other)
            => VectorMatch(DefaultDirection, other.DefaultDirection)
                && MaxGpuWaves == other.MaxGpuWaves;

        internal FlowFieldDirtyFlags GetInputDirtyFlags(in FlowFieldSessionRequest other)
        {
            FlowFieldDirtyFlags flags = FlowFieldDirtyFlags.None;
            bool surfaceChanged = !SurfaceSettingsMatch(SurfaceSettings, other.SurfaceSettings)
                || !ReferenceEquals(StaticBakeSnapshot, other.StaticBakeSnapshot)
                || StaticBakeSnapshot != null
                    && other.StaticBakeSnapshot != null
                    && StaticBakeSnapshot.Revision != other.StaticBakeSnapshot.Revision
                || SourceKind != other.SourceKind;
            if (surfaceChanged)
            {
                flags |= FlowFieldDirtyFlags.All;
            }
            else if (ObstacleLayer.value != other.ObstacleLayer.value
                || !Approximately(ObstacleCheckHeight, other.ObstacleCheckHeight)
                || !Approximately(ObstacleCheckCenterOffset, other.ObstacleCheckCenterOffset)
                || !Approximately(ObstacleClearance, other.ObstacleClearance)
                || UseUnregisteredObstacleSweep != other.UseUnregisteredObstacleSweep)
            {
                flags |= FlowFieldDirtyFlags.StaticObstacles
                    | FlowFieldDirtyFlags.DynamicObstacles
                    | FlowFieldDirtyFlags.Escape;
            }

            if (!GoalMatch(Goal, other.Goal))
                flags |= FlowFieldDirtyFlags.Goal | FlowFieldDirtyFlags.FinalRegion;
            if (!VectorMatch(DefaultDirection, other.DefaultDirection))
                flags |= FlowFieldDirtyFlags.DefaultDirection | FlowFieldDirtyFlags.FinalRegion;
            if (MaxGpuWaves != other.MaxGpuWaves)
                flags |= FlowFieldDirtyFlags.FinalRegion;
            return flags;
        }

        private static bool SurfaceSettingsMatch(
            in FlowFieldSurfaceBakeSettings left,
            in FlowFieldSurfaceBakeSettings right)
            => left.Grid.MatchesBounds(right.Grid)
                && FlowFieldBakeBoundsUtility.Approximately(left.BakeBounds, right.BakeBounds)
                && left.GroundLayer.value == right.GroundLayer.value
                && Approximately(left.MaxSurfaceSlope, right.MaxSurfaceSlope)
                && Approximately(left.MaxStepHeight, right.MaxStepHeight);

        private static bool GoalMatch(
            in FlowFieldGoalResolution left,
            in FlowFieldGoalResolution right)
            => left.HasActiveGoal == right.HasActiveGoal
                && left.IsValid == right.IsValid
                && left.LocalX == right.LocalX
                && left.LocalZ == right.LocalZ
                && left.SourceCellIndex == right.SourceCellIndex
                && Approximately(left.InfluenceRadius, right.InfluenceRadius)
                && VectorMatch(left.RequestedWorld, right.RequestedWorld);

        private static bool VectorMatch(Vector3 left, Vector3 right)
            => (left - right).sqrMagnitude <= 0.00000001f;

        private static bool Approximately(float left, float right)
            => Mathf.Abs(left - right) <= 0.0001f;
    }
}
