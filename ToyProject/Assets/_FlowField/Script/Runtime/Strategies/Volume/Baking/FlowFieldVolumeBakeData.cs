using System;
using UnityEngine;

namespace Common.FlowField
{
    public sealed class FlowFieldVolumeBakeData : FlowFieldBakeAsset, ISerializationCallbackReceiver
    {
        /// <summary>
        /// The Volume3D bake format this FlowField reads and writes. An asset with
        /// another <see cref="FlowFieldBakeAsset.FormatVersion"/> must be baked again.
        /// </summary>
        public static int SupportedFormatVersion => 6;

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
        // v6 stores the per-cell vectors implicitly: base directions and
        // speeds follow from NextCells (plus the bake-time Default Direction
        // for cells outside the Goal influence), and an escape direction is
        // one of the six faces. They are rebuilt on load.
        [SerializeField] private Vector3 _defaultDirection;
        [SerializeField] private byte[] _escapeFaces = Array.Empty<byte>();
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
        [NonSerialized] private Vector3[] _directions = Array.Empty<Vector3>();
        [NonSerialized] private float[] _speeds = Array.Empty<float>();
        [NonSerialized] private Vector3[] _escapeDirections = Array.Empty<Vector3>();

        private protected override int CurrentFormatVersion => SupportedFormatVersion;
        public bool HasGoal => _hasGoal;
        public Vector3 RequestedGoalWorld => _requestedGoalWorld;
        public float GoalInfluenceRadius => _goalInfluenceRadius;
        public int ResolvedGoalIndex => _resolvedGoalIndex;

        private protected override void OnContentChanged()
        {
            _validatedRevision = -1;
            _hasValidatedSignature = false;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
            => RebuildDerivedArrays();

        private FlowFieldVolumeBakeMetadata Metadata => new FlowFieldVolumeBakeMetadata(
            FormatVersion,
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
            _resolvedGoalIndex);

        /// <summary>
        /// Checks only the serialized source metadata.  This intentionally
        /// does not walk any cell or direction array so editor visualization
        /// can report a configuration mismatch while a shared structural
        /// validation job continues independently.
        /// </summary>
        internal bool MatchesMetadata(
            FlowFieldGridSpace grid,
            LayerMask obstacleLayer,
            float obstacleClearance,
            out string reason)
        {
            FlowFieldVolumeBakeMetadata metadata = Metadata;
            if (!metadata.TryValidate(out reason))
                return false;
            if (!metadata.MatchesGrid(grid)
                || metadata.ObstacleLayerMask != obstacleLayer.value
                || Mathf.Abs(metadata.ObstacleClearance - obstacleClearance) > 0.0001f)
                return Fail(out reason, "Static Volume3D Bake settings differ from the Manager settings.");
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
                Metadata,
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
                && _validatedRevision == Revision
                && _validatedContentGeneration == ContentGeneration
                && _validatedGrid.MatchesBounds(grid)
                && FlowFieldLayoutUtility.Approximately(_validatedBounds, bounds)
                && _validatedObstacleLayer == obstacleLayer.value
                && Mathf.Abs(_validatedObstacleClearance - obstacleClearance) <= 0.0001f)
                return true;
            if (!MatchesMetadata(grid, obstacleLayer, obstacleClearance, out reason))
                return false;
            if (!TryGetValidationView(out FlowFieldVolumeBakeValidationView view))
                return Fail(out reason, "Static Volume3D Bake arrays are missing.");

            if (!FlowFieldVolumeBakeValidator.ValidatePayload(in view, grid, out string payloadReason))
                return Fail(out reason, payloadReason);

            RecordValidatedPayload(grid, bounds, obstacleLayer, obstacleClearance);
            return true;
        }

        /// <summary>
        /// Keeps a valid payload for this signature, so <see cref="Matches"/>
        /// does not validate it again. The Editor calls it after validating the
        /// same payload in steps, as <see cref="FlowFieldVolumeBakeValidator"/>
        /// does for Matches.
        /// </summary>
        internal void RecordValidatedPayload(
            FlowFieldGridSpace grid,
            Bounds bounds,
            LayerMask obstacleLayer,
            float obstacleClearance)
        {
            _validatedRevision = Revision;
            _validatedGrid = grid;
            _validatedBounds = bounds;
            _validatedObstacleLayer = obstacleLayer.value;
            _validatedObstacleClearance = obstacleClearance;
            _validatedContentGeneration = ContentGeneration;
            _hasValidatedSignature = true;
        }

        internal bool MatchesGoal(
            bool hasGoal,
            Vector3 requestedGoalWorld,
            float influenceRadius,
            out string reason)
        {
            reason = string.Empty;
            if (_hasGoal != hasGoal)
                return Fail(out reason, "Whether the Static Volume3D Bake has a Goal differs from the Manager settings.");
            if (!_hasGoal)
                return true;
            if (!FlowFieldGridSpace.Approximately(requestedGoalWorld, _requestedGoalWorld, 0.00000001d)
                || Mathf.Abs(influenceRadius - _goalInfluenceRadius) > 0.0001f)
                return Fail(out reason, "Static Volume3D Bake Goal differs from the Manager settings. ReBake is required.");
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

        /// <summary>
        /// Copies the baked obstacle results of cells [start, start + count),
        /// which follow from the obstacles alone, for a BakedBase build.
        /// </summary>
        internal void CopyObstaclesToBuild(
            int start,
            int count,
            bool[] blocked,
            uint[] topology,
            Vector3[] escapeDirections)
        {
            Array.Copy(_blocked, start, blocked, start, count);
            Array.Copy(_topologyMasks, start, topology, start, count);
            Array.Copy(_escapeDirections, start, escapeDirections, start, count);
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
            FlowFieldVolumeBakeMetadata metadata = Metadata;
            if (!metadata.TryValidate(out _)
                || !metadata.MatchesGrid(grid)
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
            FlowFieldVolumeBakeMetadata metadata = new FlowFieldVolumeBakeMetadata(
                SupportedFormatVersion,
                grid.Width,
                grid.Height,
                grid.Depth,
                grid.CellSize,
                grid.Origin,
                bounds.center,
                bounds.size,
                obstacleLayer.value,
                obstacleClearance,
                hasGoal,
                requestedGoalWorld,
                goalInfluenceRadius,
                hasGoal ? resolvedGoalIndex : -1);

            // Validate the complete incoming payload, metadata included, before
            // changing any serialized field. A rejected bake must leave the
            // previous Asset contents, GUID and Revision untouched.
            FlowFieldVolumeBakeValidationView view = new FlowFieldVolumeBakeValidationView(
                metadata,
                blocked,
                topology,
                goalFlags,
                next,
                directions,
                speeds,
                escapeDirections);
            if (!FlowFieldVolumeBakeValidator.ValidatePayload(in view, grid, out string reason))
                throw new InvalidOperationException(reason);
            if (!TryEncodeVectors(
                    grid,
                    blocked,
                    next,
                    directions,
                    speeds,
                    escapeDirections,
                    out Vector3 defaultDirection,
                    out byte[] escapeFaces,
                    out reason))
                throw new InvalidOperationException(reason);

            _gridOriginWorld = metadata.Origin;
            _width = metadata.Width;
            _height = metadata.Height;
            _depth = metadata.Depth;
            _cellSize = metadata.CellSize;
            _bakeBoundsCenterWorld = metadata.BoundsCenter;
            _bakeBoundsSizeWorld = metadata.BoundsSize;
            _obstacleLayerMask = metadata.ObstacleLayerMask;
            _obstacleClearance = metadata.ObstacleClearance;
            _hasGoal = metadata.HasGoal;
            _requestedGoalWorld = metadata.RequestedGoalWorld;
            _goalInfluenceRadius = metadata.GoalInfluenceRadius;
            _resolvedGoalIndex = metadata.ResolvedGoalIndex;
            _blocked = (bool[])blocked.Clone();
            _topologyMasks = (uint[])topology.Clone();
            _goalFlags = (FlowFieldGoalFlags[])goalFlags.Clone();
            _nextCells = (int[])next.Clone();
            _defaultDirection = defaultDirection;
            _escapeFaces = escapeFaces;
            RebuildDerivedArrays();
            MarkBaked();
            RecordValidatedPayload(grid, bounds, obstacleLayer, obstacleClearance);
        }

        private void RebuildDerivedArrays()
        {
            int count = _nextCells?.Length ?? 0;
            if (_blocked == null || _blocked.Length != count
                || _escapeFaces == null || _escapeFaces.Length != count
                || (long)_width * _height * _depth != count)
            {
                // Inconsistent serialized data: leave no view so validation rejects it.
                _directions = null;
                _speeds = null;
                _escapeDirections = null;
                return;
            }
            _directions = new Vector3[count];
            _speeds = new float[count];
            _escapeDirections = new Vector3[count];
            for (int index = 0; index < count; index++)
            {
                _directions[index] = DeriveDirection(index, _width, _depth, _blocked, _nextCells, _defaultDirection, out float speed);
                _speeds[index] = speed;
                _escapeDirections[index] = DecodeEscape(_escapeFaces[index]);
            }
        }

        /// <summary>
        /// Same values the build writes: (next - cell).normalized for a directed
        /// cell, the Default Direction outside the Goal influence, zero otherwise.
        /// </summary>
        private static Vector3 DeriveDirection(
            int index,
            int width,
            int depth,
            bool[] blocked,
            int[] next,
            Vector3 defaultDirection,
            out float speed)
        {
            int target = next[index];
            speed = 0f;
            if (blocked[index] || target == index || target < -1 || target >= next.Length)
                return Vector3.zero;
            if (target == -1)
            {
                speed = defaultDirection.sqrMagnitude > FlowFieldVectorUtility.DirectionEpsilonSqr ? 1f : 0f;
                return defaultDirection;
            }
            int layer = width * depth;
            int y = index / layer;
            int z = (index - y * layer) / width;
            int x = index - y * layer - z * width;
            int ny = target / layer;
            int nz = (target - ny * layer) / width;
            int nx = target - ny * layer - nz * width;
            speed = 1f;
            return new Vector3(nx - x, ny - y, nz - z).normalized;
        }

        private static Vector3 DecodeEscape(byte face)
        {
            if (face == 0)
                return Vector3.zero;
            if (face > FlowFieldNeighborUtility.VolumeFaceDirections.Length)
                return new Vector3(float.NaN, float.NaN, float.NaN);
            int direction = FlowFieldNeighborUtility.VolumeFaceDirections[face - 1];
            return new Vector3(
                FlowFieldNeighborUtility.DeltaX[direction],
                FlowFieldNeighborUtility.DeltaY[direction],
                FlowFieldNeighborUtility.DeltaZ[direction]);
        }

        /// <summary>
        /// Encodes the incoming vectors into the v6 form and confirms that
        /// rebuilding reproduces them exactly; otherwise the bake is rejected.
        /// </summary>
        private static bool TryEncodeVectors(
            FlowFieldGridSpace grid,
            bool[] blocked,
            int[] next,
            Vector3[] directions,
            float[] speeds,
            Vector3[] escapeDirections,
            out Vector3 defaultDirection,
            out byte[] escapeFaces,
            out string reason)
        {
            int count = grid.CellCount;
            defaultDirection = Vector3.zero;
            escapeFaces = new byte[count];
            reason = string.Empty;
            for (int index = 0; index < count; index++)
            {
                if (!blocked[index] && next[index] == -1)
                {
                    defaultDirection = directions[index];
                    break;
                }
            }
            for (int index = 0; index < count; index++)
            {
                Vector3 escape = escapeDirections[index];
                if (escape == Vector3.zero)
                    continue;
                for (int face = 0; face < FlowFieldNeighborUtility.VolumeFaceDirections.Length; face++)
                {
                    if ((DecodeEscape((byte)(face + 1)) - escape).sqrMagnitude <= 0.000001f)
                    {
                        escapeFaces[index] = (byte)(face + 1);
                        break;
                    }
                }
                if (escapeFaces[index] == 0)
                    return Fail(out reason, $"Volume3D bake escape direction at cell {index} is not a face direction.");
            }
            for (int index = 0; index < count; index++)
            {
                Vector3 derived = DeriveDirection(index, grid.Width, grid.Depth, blocked, next, defaultDirection, out float speed);
                if ((derived - directions[index]).sqrMagnitude > 1e-12f
                    || Mathf.Abs(speed - speeds[index]) > 0.0001f
                    || (DecodeEscape(escapeFaces[index]) - escapeDirections[index]).sqrMagnitude > 0.000001f)
                    return Fail(out reason, $"Volume3D bake cell {index} cannot be stored in the v6 format.");
            }
            return true;
        }
    }
}
