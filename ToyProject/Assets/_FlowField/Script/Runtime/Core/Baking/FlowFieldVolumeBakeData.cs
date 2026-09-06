using System;
using UnityEngine;

namespace Common.FlowField
{
    [CreateAssetMenu(
        fileName = "FlowFieldVolumeBakeData",
        menuName = "FlowField/Volume 3D Bake Data")]
    public sealed class FlowFieldVolumeBakeData : ScriptableObject
    {
        public const int CURRENT_FORMAT_VERSION = 5;

        [SerializeField] private int _formatVersion = CURRENT_FORMAT_VERSION;
        [SerializeField] private int _revision;
        [SerializeField] private Vector3 _gridOriginWorld;
        [SerializeField] private int _width;
        [SerializeField] private int _height;
        [SerializeField] private int _depth;
        [SerializeField] private float _cellSize;
        [SerializeField] private Vector3 _bakeBoundsCenterWorld;
        [SerializeField] private Vector3 _bakeBoundsSizeWorld;
        [SerializeField] private int _obstacleLayerMask;
        [SerializeField] private float _obstacleClearance;

        [SerializeField] private bool[] _blocked = Array.Empty<bool>();
        [SerializeField] private uint[] _topologyMasks = Array.Empty<uint>();
        [SerializeField] private FlowFieldGoalFlags[] _goalFlags = Array.Empty<FlowFieldGoalFlags>();
        [SerializeField] private int[] _nextCells = Array.Empty<int>();
        [SerializeField] private Vector3[] _directions = Array.Empty<Vector3>();
        [SerializeField] private float[] _speeds = Array.Empty<float>();
        [SerializeField] private Vector3[] _escapeDirections = Array.Empty<Vector3>();
        [SerializeField] private bool _hasGoal;
        [SerializeField] private Vector3 _requestedGoalWorld;
        [SerializeField] private float _goalInfluenceRadius;
        [SerializeField] private int _resolvedGoalIndex = -1;

        [NonSerialized] private int _validatedRevision = -1;
        [NonSerialized] private FlowFieldGridSpace _validatedGrid;
        [NonSerialized] private Bounds _validatedBounds;
        [NonSerialized] private int _validatedObstacleLayer;
        [NonSerialized] private float _validatedObstacleClearance;
        [NonSerialized] private int _validatedContentGeneration = -1;
        [NonSerialized] private bool _hasValidatedSignature;
        [NonSerialized] private int _contentGeneration;

        public int FormatVersion => _formatVersion;
        public int Revision => _revision;
        public int ContentGeneration => _contentGeneration;
        public bool HasGoal => _hasGoal;
        public Vector3 RequestedGoalWorld => _requestedGoalWorld;
        public float GoalInfluenceRadius => _goalInfluenceRadius;
        public int ResolvedGoalIndex => _resolvedGoalIndex;

        private void OnValidate()
        {
            unchecked { _contentGeneration++; }
            _validatedRevision = -1;
            _hasValidatedSignature = false;
        }

        private void OnEnable()
        {
            unchecked { _contentGeneration++; }
            _hasValidatedSignature = false;
        }

        /// <summary>
        /// Checks only the serialized source metadata.  This intentionally
        /// does not walk any cell or direction array so editor visualization
        /// can report a configuration mismatch while a shared structural
        /// validation job continues independently.
        /// </summary>
        internal bool MatchesMetadata(
            FlowFieldGridSpace grid,
            Bounds bounds,
            LayerMask obstacleLayer,
            float obstacleClearance,
            out string reason)
        {
            reason = string.Empty;
            if (_formatVersion != CURRENT_FORMAT_VERSION)
                return Fail(out reason, $"FlowField Volume Bake format {_formatVersion} is unsupported; ReBake is required.");
            if (_width <= 0 || _height <= 0 || _depth <= 0
                || !FlowFieldGridSpace.IsFinite(_gridOriginWorld)
                || !FlowFieldGridSpace.IsFinite(_cellSize)
                || !FlowFieldGridSpace.IsFinite(_bakeBoundsCenterWorld)
                || !FlowFieldGridSpace.IsFinite(_bakeBoundsSizeWorld)
                || _bakeBoundsSizeWorld.x <= 0f
                || _bakeBoundsSizeWorld.y <= 0f
                || _bakeBoundsSizeWorld.z <= 0f
                || !FlowFieldGridSpace.IsFinite(_obstacleClearance)
                || _obstacleClearance < 0f
                || _obstacleLayerMask == 0)
                return Fail(out reason, "Static Volume3D Bake metadata is invalid.");
            if (!grid.IsValid || grid.SpaceMode != FlowFieldSpaceMode.Volume3D
                || _width != grid.Width || _height != grid.Height || _depth != grid.Depth
                || Mathf.Abs(_cellSize - grid.CellSize) > 0.0001f
                || !FlowFieldGridSpace.Approximately(_gridOriginWorld, grid.Origin, 0.000001d)
                || !FlowFieldGridSpace.Approximately(_bakeBoundsCenterWorld, bounds.center, 0.000001d)
                || !FlowFieldGridSpace.Approximately(_bakeBoundsSizeWorld, bounds.size, 0.000001d)
                || _obstacleLayerMask != obstacleLayer.value
                || Mathf.Abs(_obstacleClearance - obstacleClearance) > 0.0001f)
                return Fail(out reason, "Static Volume3D Bake 설정이 현재 Manager 설정과 다릅니다.");
            if (!FlowFieldGridSpace.IsFinite(_requestedGoalWorld)
                || !FlowFieldGridSpace.IsFinite(_goalInfluenceRadius)
                || _goalInfluenceRadius < 0f)
                return Fail(out reason, "Static Volume3D Bake Goal 데이터가 유효하지 않습니다.");
            if (!_hasGoal && _resolvedGoalIndex != -1
                || _hasGoal && (_resolvedGoalIndex < 0 || _resolvedGoalIndex >= grid.CellCount))
                return Fail(out reason, "Static Volume3D Bake Goal index is invalid.");
            return true;
        }

        internal bool TryGetValidationView(out FlowFieldVolumeBakeValidationView view)
        {
            view = default;
            if (_blocked == null || _topologyMasks == null || _goalFlags == null
                || _nextCells == null || _directions == null || _speeds == null
                || _escapeDirections == null)
                return false;

            view = new FlowFieldVolumeBakeValidationView(
                _formatVersion,
                _revision,
                _width,
                _height,
                _depth,
                _cellSize,
                _gridOriginWorld,
                _bakeBoundsCenterWorld,
                _bakeBoundsSizeWorld,
                _obstacleLayerMask,
                _obstacleClearance,
                _hasGoal,
                _requestedGoalWorld,
                _goalInfluenceRadius,
                _resolvedGoalIndex,
                _blocked,
                _topologyMasks,
                _goalFlags,
                _nextCells,
                _directions,
                _speeds,
                _escapeDirections);
            return true;
        }

        internal bool Matches(
            FlowFieldGridSpace grid,
            Bounds bounds,
            LayerMask obstacleLayer,
            float obstacleClearance,
            out string reason)
        {
            reason = string.Empty;
            if (_hasValidatedSignature
                && _validatedRevision == _revision
                && _validatedContentGeneration == _contentGeneration
                && _validatedGrid.MatchesBounds(grid)
                && FlowFieldBakeBoundsUtility.Approximately(_validatedBounds, bounds)
                && _validatedObstacleLayer == obstacleLayer.value
                && Mathf.Abs(_validatedObstacleClearance - obstacleClearance) <= 0.0001f)
                return true;
            if (!MatchesMetadata(grid, bounds, obstacleLayer, obstacleClearance, out reason))
                return false;
            if (!TryGetValidationView(out FlowFieldVolumeBakeValidationView view))
                return Fail(out reason, "Static Volume3D Bake arrays are missing.");

            using (FlowFieldVolumeBakeValidator validator =
                new FlowFieldVolumeBakeValidator(view, grid))
            {
                while (!validator.IsComplete)
                    validator.Step(4096, 64);
                if (!validator.IsValid)
                    return Fail(out reason, validator.Error);
            }

            _validatedRevision = _revision;
            _validatedGrid = grid;
            _validatedBounds = bounds;
            _validatedObstacleLayer = obstacleLayer.value;
            _validatedObstacleClearance = obstacleClearance;
            _validatedContentGeneration = _contentGeneration;
            _hasValidatedSignature = true;
            return true;
        }

        internal bool MatchesGoal(
            bool hasGoal,
            Vector3 requestedGoalWorld,
            float influenceRadius,
            out string reason)
        {
            reason = string.Empty;
            if (_hasGoal != hasGoal)
                return Fail(out reason, "Static Volume3D Bake Goal 활성 상태가 현재 Manager 설정과 다릅니다.");
            if (!_hasGoal)
                return true;
            if (!FlowFieldGridSpace.Approximately(requestedGoalWorld, _requestedGoalWorld, 0.00000001d)
                || Mathf.Abs(influenceRadius - _goalInfluenceRadius) > 0.0001f)
                return Fail(out reason, "Static Volume3D Bake Goal이 현재 Manager 설정과 다릅니다. ReBake가 필요합니다.");
            return true;
        }

        internal void CopyCellToBuild(
            int index,
            bool[] blocked,
            uint[] topology,
            FlowFieldGoalFlags[] goalFlags,
            int[] next,
            Vector3[] directions,
            float[] speeds,
            Vector3[] escapeDirections)
        {
            blocked[index] = _blocked[index];
            topology[index] = _topologyMasks[index];
            goalFlags[index] = _goalFlags[index];
            next[index] = _nextCells[index];
            directions[index] = _directions[index];
            speeds[index] = _speeds[index];
            escapeDirections[index] = _escapeDirections[index];
        }

        internal bool TryGetView(
            FlowFieldGridSpace grid,
            out bool[] blocked,
            out Vector3[] directions,
            out float[] speeds)
        {
            blocked = null;
            directions = null;
            speeds = null;
            if (_formatVersion != CURRENT_FORMAT_VERSION
                || !grid.IsValid
                || grid.SpaceMode != FlowFieldSpaceMode.Volume3D
                || _width != grid.Width || _height != grid.Height || _depth != grid.Depth
                || !FlowFieldGridSpace.IsFinite(_gridOriginWorld)
                || !FlowFieldGridSpace.IsFinite(_cellSize)
                || Mathf.Abs(_cellSize - grid.CellSize) > 0.0001f
                || (_gridOriginWorld - grid.Origin).sqrMagnitude > 0.000001f
                || !FlowFieldGridSpace.IsFinite(_bakeBoundsCenterWorld)
                || !FlowFieldGridSpace.IsFinite(_bakeBoundsSizeWorld)
                || _bakeBoundsSizeWorld.x <= 0f
                || _bakeBoundsSizeWorld.y <= 0f
                || _bakeBoundsSizeWorld.z <= 0f
                || !FlowFieldBakeBoundsUtility.Approximately(
                    new Bounds(
                        grid.Origin + new Vector3(grid.WorldSizeX, grid.WorldSizeY, grid.WorldSizeZ) * 0.5f,
                        new Vector3(grid.WorldSizeX, grid.WorldSizeY, grid.WorldSizeZ)),
                    new Bounds(_bakeBoundsCenterWorld, _bakeBoundsSizeWorld))
                || _blocked == null || _blocked.Length != grid.CellCount
                || _topologyMasks == null || _topologyMasks.Length != grid.CellCount
                || _goalFlags == null || _goalFlags.Length != grid.CellCount
                || _nextCells == null || _nextCells.Length != grid.CellCount
                || _directions == null || _directions.Length != grid.CellCount
                || _speeds == null || _speeds.Length != grid.CellCount
                || _escapeDirections == null || _escapeDirections.Length != grid.CellCount)
                return false;
            blocked = _blocked;
            directions = _directions;
            speeds = _speeds;
            return true;
        }

        internal void Apply(
            FlowFieldGridSpace grid,
            Bounds bounds,
            LayerMask obstacleLayer,
            float obstacleClearance,
            bool hasGoal,
            Vector3 requestedGoalWorld,
            float goalInfluenceRadius,
            int resolvedGoalIndex,
            bool[] blocked,
            uint[] topology,
            FlowFieldGoalFlags[] goalFlags,
            int[] next,
            Vector3[] directions,
            float[] speeds,
            Vector3[] escapeDirections)
        {
            if (grid.SpaceMode != FlowFieldSpaceMode.Volume3D || grid.CellCount <= 0)
                throw new ArgumentException("A valid Volume3D grid is required.", nameof(grid));
            if (!FlowFieldGridSpace.IsFinite(bounds.center)
                || !FlowFieldGridSpace.IsFinite(bounds.size)
                || bounds.size.x <= 0f
                || bounds.size.y <= 0f
                || bounds.size.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(bounds));
            Bounds expectedBounds = new Bounds(
                grid.Origin + new Vector3(grid.WorldSizeX, grid.WorldSizeY, grid.WorldSizeZ) * 0.5f,
                new Vector3(grid.WorldSizeX, grid.WorldSizeY, grid.WorldSizeZ));
            if (!FlowFieldBakeBoundsUtility.Approximately(expectedBounds, bounds))
                throw new ArgumentException("Bake Bounds do not match the Volume3D grid.", nameof(bounds));
            if (obstacleLayer.value == 0
                || !FlowFieldGridSpace.IsFinite(obstacleClearance)
                || obstacleClearance < 0f
                || !FlowFieldGridSpace.IsFinite(requestedGoalWorld)
                || !FlowFieldGridSpace.IsFinite(goalInfluenceRadius)
                || goalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(obstacleClearance));
            int count = grid.CellCount;
            if (blocked == null || blocked.Length != count
                || topology == null || topology.Length != count
                || goalFlags == null || goalFlags.Length != count
                || next == null || next.Length != count
                || directions == null || directions.Length != count
                || speeds == null || speeds.Length != count
                || escapeDirections == null || escapeDirections.Length != count)
                throw new ArgumentException("Volume3D bake arrays must match the grid cell count.");
            if (!FlowFieldGridSpace.IsFinite(requestedGoalWorld)
                || !FlowFieldGridSpace.IsFinite(goalInfluenceRadius)
                || goalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(goalInfluenceRadius));
            if (hasGoal && (resolvedGoalIndex < 0 || resolvedGoalIndex >= count || blocked[resolvedGoalIndex]))
                throw new InvalidOperationException(
                    "Cannot bake a Volume3D Goal without a valid unblocked resolved Goal cell.");
            if (!hasGoal)
                resolvedGoalIndex = -1;

            // Validate the complete incoming payload before changing any
            // serialized field. A rejected bake must leave the previous Asset
            // contents, GUID and Revision untouched.
            if (!ValidateIncomingArrays(
                     grid,
                     hasGoal,
                     resolvedGoalIndex,
                     requestedGoalWorld,
                     goalInfluenceRadius,
                     blocked,
                    topology,
                    goalFlags,
                    next,
                    directions,
                    speeds,
                    escapeDirections,
                    out string reason))
                throw new InvalidOperationException(reason);

            _formatVersion = CURRENT_FORMAT_VERSION;
            _gridOriginWorld = grid.Origin;
            _width = grid.Width;
            _height = grid.Height;
            _depth = grid.Depth;
            _cellSize = grid.CellSize;
            _bakeBoundsCenterWorld = bounds.center;
            _bakeBoundsSizeWorld = bounds.size;
            _obstacleLayerMask = obstacleLayer.value;
            _obstacleClearance = obstacleClearance;
            _hasGoal = hasGoal;
            _requestedGoalWorld = requestedGoalWorld;
            _goalInfluenceRadius = goalInfluenceRadius;
            _resolvedGoalIndex = resolvedGoalIndex;
            _blocked = (bool[])blocked.Clone();
            _topologyMasks = (uint[])topology.Clone();
            _goalFlags = (FlowFieldGoalFlags[])goalFlags.Clone();
            _nextCells = (int[])next.Clone();
            _directions = (Vector3[])directions.Clone();
            _speeds = (float[])speeds.Clone();
            _escapeDirections = (Vector3[])escapeDirections.Clone();
            _revision++;
            unchecked { _contentGeneration++; }
            _validatedRevision = _revision;
            _validatedGrid = grid;
            _validatedBounds = bounds;
            _validatedObstacleLayer = obstacleLayer.value;
            _validatedObstacleClearance = obstacleClearance;
            _validatedContentGeneration = _contentGeneration;
            _hasValidatedSignature = true;
        }

        private static bool ValidateIncomingArrays(
            FlowFieldGridSpace grid,
            bool hasGoal,
            int resolvedGoalIndex,
            Vector3 requestedGoalWorld,
            float goalInfluenceRadius,
            bool[] blocked,
            uint[] topology,
            FlowFieldGoalFlags[] goalFlags,
            int[] next,
            Vector3[] directions,
            float[] speeds,
            Vector3[] escapeDirections,
            out string reason)
        {
            Bounds bounds = new Bounds(
                grid.Origin
                    + new Vector3(grid.WorldSizeX, grid.WorldSizeY, grid.WorldSizeZ) * 0.5f,
                new Vector3(grid.WorldSizeX, grid.WorldSizeY, grid.WorldSizeZ));
            FlowFieldVolumeBakeValidationView view = new FlowFieldVolumeBakeValidationView(
                FlowFieldVolumeBakeData.CURRENT_FORMAT_VERSION,
                0,
                grid.Width,
                grid.Height,
                grid.Depth,
                grid.CellSize,
                grid.Origin,
                bounds.center,
                bounds.size,
                1,
                0f,
                hasGoal,
                requestedGoalWorld,
                goalInfluenceRadius,
                resolvedGoalIndex,
                blocked,
                topology,
                goalFlags,
                next,
                directions,
                speeds,
                escapeDirections);
            return FlowFieldVolumeBakeValidator.ValidatePayload(in view, grid, out reason);
        }

        private static bool Fail(out string reason, string message)
        {
            reason = message;
            return false;
        }

    }
}
