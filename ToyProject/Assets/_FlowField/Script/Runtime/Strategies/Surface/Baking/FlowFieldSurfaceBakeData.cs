using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Surface2D bake StaticBaked and BakedBase Managers load. It stores
    /// the surface and the base navigation; Default Direction and Modifiers
    /// stay runtime inputs and are composed after loading, and BakedBase
    /// solves its own Goal on the baked surface and obstacles.
    /// </summary>
    public sealed class FlowFieldSurfaceBakeData : FlowFieldBakeAsset, ISerializationCallbackReceiver
    {
        /// <summary>
        /// The Surface2D bake format this FlowField reads and writes. An asset with
        /// another <see cref="FlowFieldBakeAsset.FormatVersion"/> must be baked again.
        /// </summary>
        public static int SupportedFormatVersion => 6;
        private const float Epsilon = 0.0001f;
        private const int MinSentinel = -3;

        [SerializeField] private Vector3 _gridOriginWorld;
        [SerializeField] private int _width;
        [SerializeField] private int _depth;
        [SerializeField] private float _cellSize;
        [SerializeField] private int _groundLayerMask;
        [SerializeField] private Vector3 _bakeBoundsCenterWorld;
        [SerializeField] private Vector3 _bakeBoundsSizeWorld;
        [SerializeField] private float _maxSurfaceSlope;
        [SerializeField] private float _maxStepHeight;
        [SerializeField] private int _obstacleLayerMask;
        [SerializeField] private float _obstacleCheckHeight;
        [SerializeField] private float _obstacleCheckCenterOffset;
        [SerializeField] private float _obstacleClearance;

        // v6 stores the float and Vector3 arrays packed as bytes (see
        // FlowFieldBakeBlob); the runtime arrays below are unpacked on load.
        [SerializeField] private byte[] _surfaceHeightData = Array.Empty<byte>();
        [SerializeField] private byte[] _surfaceNormalData = Array.Empty<byte>();
        [SerializeField] private byte[] _surfaceFlags = Array.Empty<byte>();
        [SerializeField] private byte[] _surfaceNeighborMasks = Array.Empty<byte>();
        [SerializeField] private bool[] _blocked = Array.Empty<bool>();
        [SerializeField] private byte[] _escapeDirectionData = Array.Empty<byte>();
        [SerializeField] private byte[] _topologyMasks = Array.Empty<byte>();
        [SerializeField] private FlowFieldGoalFlags[] _goalFlags = Array.Empty<FlowFieldGoalFlags>();
        [SerializeField] private byte[] _goalDirectionData = Array.Empty<byte>();
        [SerializeField] private int[] _nextCells = Array.Empty<int>();

        [NonSerialized] private float[] _surfaceHeights = Array.Empty<float>();
        [NonSerialized] private Vector3[] _surfaceNormals = Array.Empty<Vector3>();
        [NonSerialized] private Vector3[] _escapeDirections = Array.Empty<Vector3>();
        [NonSerialized] private Vector3[] _goalDirections = Array.Empty<Vector3>();

        [SerializeField] private bool _hasGoal;
        [SerializeField] private Vector3 _requestedGoalWorld;
        [SerializeField] private float _goalInfluenceRadius;
        [SerializeField] private int _resolvedGoalIndex = -1;

        [NonSerialized] private FlowFieldSurfaceBakeSnapshot _snapshotCache;
        [NonSerialized] private int _snapshotCacheRevision = -1;
        [NonSerialized] private int _validatedRevision = -1;
        [NonSerialized] private int _validatedContentGeneration = -1;
        [NonSerialized] private bool _validationResult;
        [NonSerialized] private string _validationReason = string.Empty;

        private protected override int CurrentFormatVersion => SupportedFormatVersion;
        public bool HasGoal => _hasGoal;
        public Vector3 RequestedGoalWorld => _requestedGoalWorld;
        public float GoalInfluenceRadius => _goalInfluenceRadius;
        public int ResolvedGoalIndex => _resolvedGoalIndex;

        // Serialized inspector edits can change a valid snapshot without going
        // through Apply(). The immutable runtime cache never outlives that
        // change; the next request validates and rebuilds the view.
        private protected override void OnContentChanged()
        {
            _snapshotCache = null;
            _snapshotCacheRevision = -1;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
            _surfaceHeightData = FlowFieldBakeBlob.Pack(_surfaceHeights);
            _surfaceNormalData = FlowFieldBakeBlob.Pack(_surfaceNormals);
            _escapeDirectionData = FlowFieldBakeBlob.Pack(_escapeDirections);
            _goalDirectionData = FlowFieldBakeBlob.Pack(_goalDirections);
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            _surfaceHeights = FlowFieldBakeBlob.Unpack<float>(_surfaceHeightData);
            _surfaceNormals = FlowFieldBakeBlob.Unpack<Vector3>(_surfaceNormalData);
            _escapeDirections = FlowFieldBakeBlob.Unpack<Vector3>(_escapeDirectionData);
            _goalDirections = FlowFieldBakeBlob.Unpack<Vector3>(_goalDirectionData);
            MarkContentChanged();
        }

        public bool HasValidData
        {
            get
            {
                return Validate(out _);
            }
        }

        // Requests, Gizmos and the Inspector all ask, and the full check costs
        // milliseconds at the largest grids, so an unchanged asset answers once.
        internal bool Validate(out string reason)
        {
            if (_validatedRevision != Revision || _validatedContentGeneration != ContentGeneration)
            {
                _validationResult = ValidateContents(out _validationReason);
                _validatedRevision = Revision;
                _validatedContentGeneration = ContentGeneration;
            }
            reason = _validationReason;
            return _validationResult;
        }

        private bool ValidateContents(out string reason)
        {
            reason = string.Empty;
            if (!HasCurrentFormat)
                return Fail(out reason, $"FlowField Static Bake format {FormatVersion} is unsupported; ReBake is required.");
            if (!FlowFieldLayoutUtility.TryValidateCellCount(_width, _depth, out int count)
                || !FlowFieldGridSpace.IsFinite(_gridOriginWorld)
                || !FlowFieldGridSpace.IsFinite(_cellSize)
                || _cellSize < FlowFieldLayoutUtility.MinCellSize
                || !FlowFieldGridSpace.IsFinite(_width * _cellSize)
                || !FlowFieldGridSpace.IsFinite(_depth * _cellSize)
                || _groundLayerMask == 0
                || !FlowFieldGridSpace.IsFinite(_bakeBoundsCenterWorld)
                || !FlowFieldGridSpace.IsFinite(_bakeBoundsSizeWorld)
                || _bakeBoundsSizeWorld.x <= 0f
                || _bakeBoundsSizeWorld.y < FlowFieldLayoutUtility.MinBoundsHeight
                || _bakeBoundsSizeWorld.z <= 0f
                || !FlowFieldGridSpace.IsFinite(_maxSurfaceSlope)
                || _maxSurfaceSlope < 0f || _maxSurfaceSlope >= 90f
                || !FlowFieldGridSpace.IsFinite(_maxStepHeight) || _maxStepHeight < 0f
                || _obstacleLayerMask == 0
                || !FlowFieldGridSpace.IsFinite(_obstacleCheckHeight) || _obstacleCheckHeight <= 0f
                || !FlowFieldGridSpace.IsFinite(_obstacleCheckCenterOffset)
                || !FlowFieldGridSpace.IsFinite(_obstacleClearance) || _obstacleClearance < 0f
                || !_hasGoal && _resolvedGoalIndex != -1
                || _hasGoal && (_resolvedGoalIndex < 0 || _resolvedGoalIndex >= count)
                || !FlowFieldGridSpace.IsFinite(_requestedGoalWorld)
                || !FlowFieldGridSpace.IsFinite(_goalInfluenceRadius) || _goalInfluenceRadius < 0f)
                return Fail(out reason, "FlowField Static Bake header is invalid.");

            if (!ArrayMatches(count))
                return Fail(out reason, "FlowField Static Bake arrays do not match the grid.");

            for (int i = 0; i < count; i++)
            {
                if (!FlowFieldGridSpace.IsFinite(_surfaceHeights[i])
                    || !FlowFieldGridSpace.IsFinite(_surfaceNormals[i])
                    || !FlowFieldGridSpace.IsFinite(_escapeDirections[i])
                    || !FlowFieldGridSpace.IsFinite(_goalDirections[i]))
                    return Fail(out reason, "FlowField Static Bake contains a non-finite value.");
                bool valid = (_surfaceFlags[i] & 1) != 0;
                if ((_surfaceFlags[i] & ~1) != 0
                    || (!valid && _surfaceNeighborMasks[i] != 0)
                    || valid && (_surfaceNormals[i].sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr
                        || !ApproximatelyUnit(_surfaceNormals[i])))
                    return Fail(out reason, "FlowField Static Bake contains an invalid Surface cell.");
                if (!FlowFieldVectorUtility.IsUnitOrZero(_escapeDirections[i]) || !FlowFieldVectorUtility.IsUnitOrZero(_goalDirections[i]))
                    return Fail(out reason, "FlowField Static Bake contains an invalid direction.");
                if ((_goalFlags[i] & ~(FlowFieldGoalFlags.Directed
                    | FlowFieldGoalFlags.Anchor
                    | FlowFieldGoalFlags.Unreachable)) != 0)
                    return Fail(out reason, "FlowField Static Bake contains invalid Goal flags.");
                if (_nextCells[i] < MinSentinel || _nextCells[i] >= count)
                    return Fail(out reason, "FlowField Static Bake contains an invalid NextCell sentinel.");
                if (!valid || _blocked[i])
                {
                    if (_nextCells[i] != -2 || _topologyMasks[i] != 0
                        || _goalFlags[i] != FlowFieldGoalFlags.None
                        || _goalDirections[i] != Vector3.zero)
                        return Fail(out reason, "Blocked or invalid cells must use the -2 sentinel.");
                }
                if (valid && !HasValidSurfaceMask(i))
                    return Fail(out reason, "FlowField Static Bake Surface connectivity is inconsistent.");
            }

            if (!IsConsistentSnapshot(count))
                return Fail(out reason, "FlowField Static Bake topology and NextCell data are inconsistent.");
            return true;
        }

        /// <summary>
        /// Creates (and then reuses) the immutable runtime view after the
        /// serialized asset has passed its complete validation/signature check.
        /// The cache is invalidated whenever Apply writes a new revision, so a
        /// Goal/default-direction-only request does not clone the asset again.
        /// </summary>
        internal FlowFieldSurfaceBakeSnapshot CreateSnapshot(in FlowFieldSurfaceSignature signature)
        {
            if (!Matches(signature, out string reason))
                throw new InvalidOperationException(reason);

            if (_snapshotCache != null
                && _snapshotCacheRevision == Revision
                && _snapshotCache.Matches(signature, out _))
                return _snapshotCache;

            FlowFieldSurfaceData surface = CreateSurfaceData(signature.Surface);
            _snapshotCache = new FlowFieldSurfaceBakeSnapshot(
                signature,
                Revision,
                _hasGoal,
                _requestedGoalWorld,
                _goalInfluenceRadius,
                _resolvedGoalIndex,
                surface,
                _blocked,
                _escapeDirections,
                _topologyMasks,
                _goalFlags,
                _goalDirections,
                _nextCells);
            _snapshotCacheRevision = Revision;
            return _snapshotCache;
        }

        private bool ArrayMatches(int count)
            => _surfaceHeights != null && _surfaceHeights.Length == count
                && _surfaceNormals != null && _surfaceNormals.Length == count
                && _surfaceFlags != null && _surfaceFlags.Length == count
                && _surfaceNeighborMasks != null && _surfaceNeighborMasks.Length == count
                && _blocked != null && _blocked.Length == count
                && _escapeDirections != null && _escapeDirections.Length == count
                && _topologyMasks != null && _topologyMasks.Length == count
                && _goalFlags != null && _goalFlags.Length == count
                && _goalDirections != null && _goalDirections.Length == count
                && _nextCells != null && _nextCells.Length == count;

        private bool IsConsistentSnapshot(int count)
        {
            int anchors = 0;
            for (int i = 0; i < count; i++)
            {
                bool valid = (_surfaceFlags[i] & 1) != 0;
                bool traversable = valid
                    && !_blocked[i]
                    && (!_hasGoal || _goalFlags[i] != FlowFieldGoalFlags.None);
                if (!traversable && _topologyMasks[i] != 0)
                    return false;
                if (traversable)
                {
                    int x = i % _width;
                    int z = i / _width;
                    for (int topologyDirection = 0; topologyDirection < FlowFieldNeighborUtility.SurfaceCount; topologyDirection++)
                    {
                        bool expected = CanTraverseSnapshot(i, x, z, topologyDirection);
                        bool actual = (_topologyMasks[i] & (1 << topologyDirection)) != 0;
                        if (expected != actual)
                            return false;
                    }
                }
                if (!valid || _blocked[i])
                    continue;
                int next = _nextCells[i];
                if (!_hasGoal)
                {
                    if (next != -1
                        || _goalFlags[i] != FlowFieldGoalFlags.None
                        || _goalDirections[i] != Vector3.zero)
                        return false;
                    continue;
                }
                if (next == -3)
                {
                    if (_goalFlags[i] != FlowFieldGoalFlags.Unreachable
                        || _goalDirections[i] != Vector3.zero)
                        return false;
                    continue;
                }
                if (next == -1)
                {
                    if (_goalFlags[i] != FlowFieldGoalFlags.None
                        || _goalDirections[i] != Vector3.zero)
                        return false;
                    continue;
                }
                if (next < 0 || next >= count || _blocked[next] || (_surfaceFlags[next] & 1) == 0)
                    return false;
                if (next == i)
                {
                    if (i != _resolvedGoalIndex
                        || _goalFlags[i] != (FlowFieldGoalFlags.Directed | FlowFieldGoalFlags.Anchor)
                        || _goalDirections[i] != Vector3.zero)
                        return false;
                    anchors++;
                    continue;
                }
                int direction = FindDirection(i, next);
                if (direction < 0 || (_topologyMasks[i] & (1 << direction)) == 0
                    || _goalFlags[i] != FlowFieldGoalFlags.Directed
                    || _goalDirections[i].sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr
                    || !ApproximatelyUnit(_goalDirections[i]))
                    return false;

                // A normalized vector alone is not enough to prove that a
                // baked result belongs to its NextCell. Reject snapshots that
                // were edited or truncated into a plausible-looking but
                // semantically wrong direction.
                if (Vector3.Distance(_goalDirections[i], ComputeGoalDirection(i, next)) > 0.01f)
                    return false;
            }
            return !_hasGoal ? _resolvedGoalIndex == -1 : anchors == 1;
        }

        private bool HasValidSurfaceMask(int index)
        {
            int x = index % _width;
            int z = index / _width;
            for (int direction = 0; direction < FlowFieldNeighborUtility.SurfaceCount; direction++)
            {
                if ((_surfaceNeighborMasks[index] & (1 << direction)) == 0)
                    continue;
                int dx = FlowFieldNeighborUtility.DeltaX[direction];
                int dz = FlowFieldNeighborUtility.DeltaZ[direction];
                int nx = x + dx;
                int nz = z + dz;
                if (nx < 0 || nz < 0 || nx >= _width || nz >= _depth)
                    return false;
                int neighbor = nz * _width + nx;
                if (!CanConnectSnapshot(index, neighbor))
                    return false;
                if (FlowFieldNeighborUtility.IsDiagonal(direction)
                    && !CanConnectDiagonalSnapshot(x, z, dx, dz))
                    return false;
            }
            return true;
        }

        private bool CanTraverseSnapshot(int index, int x, int z, int direction)
        {
            if ((_surfaceNeighborMasks[index] & (1 << direction)) == 0)
                return false;
            int dx = FlowFieldNeighborUtility.DeltaX[direction];
            int dz = FlowFieldNeighborUtility.DeltaZ[direction];
            int nx = x + dx;
            int nz = z + dz;
            if (nx < 0 || nz < 0 || nx >= _width || nz >= _depth)
                return false;
            int neighbor = nz * _width + nx;
            if (!IsSnapshotTraversable(neighbor))
                return false;
            if (!FlowFieldNeighborUtility.IsDiagonal(direction))
                return true;

            int first = dx > 0 ? 0 : 1;
            int second = dz > 0 ? 2 : 3;
            int orthogonalX = z * _width + x + dx;
            int orthogonalZ = (z + dz) * _width + x;
            return (_surfaceNeighborMasks[index] & (1 << first)) != 0
                && (_surfaceNeighborMasks[index] & (1 << second)) != 0
                && IsSnapshotTraversable(orthogonalX)
                && IsSnapshotTraversable(orthogonalZ);
        }

        private bool IsSnapshotTraversable(int index)
            => index >= 0
                && index < _surfaceHeights.Length
                && (_surfaceFlags[index] & 1) != 0
                && !_blocked[index]
                && (!_hasGoal || _goalFlags[index] != FlowFieldGoalFlags.None);

        private bool CanConnectDiagonalSnapshot(int x, int z, int dx, int dz)
        {
            int a = z * _width + x;
            int b = z * _width + x + dx;
            int c = (z + dz) * _width + x;
            int d = (z + dz) * _width + x + dx;
            return CanConnectSnapshot(a, b)
                && CanConnectSnapshot(a, c)
                && CanConnectSnapshot(b, d)
                && CanConnectSnapshot(c, d);
        }

        private bool CanConnectSnapshot(int left, int right)
            => left >= 0
                && right >= 0
                && left < _surfaceHeights.Length
                && right < _surfaceHeights.Length
                && (_surfaceFlags[left] & 1) != 0
                && (_surfaceFlags[right] & 1) != 0
                && Mathf.Abs(_surfaceHeights[left] - _surfaceHeights[right]) <= _maxStepHeight;

        private int FindDirection(int from, int to)
        {
            int fromX = from % _width;
            int fromZ = from / _width;
            int dx = to % _width - fromX;
            int dz = to / _width - fromZ;
            for (int i = 0; i < FlowFieldNeighborUtility.SurfaceCount; i++)
                if (FlowFieldNeighborUtility.DeltaX[i] == dx && FlowFieldNeighborUtility.DeltaZ[i] == dz)
                    return i;
            return -1;
        }

        private Vector3 ComputeGoalDirection(int from, int to)
        {
            Vector3 current = CellCenter(from);
            Vector3 next = CellCenter(to);
            Vector3 normal = _surfaceNormals[from].normalized;
            Vector3 projected = Vector3.ProjectOnPlane(next - current, normal);
            return projected.sqrMagnitude > FlowFieldVectorUtility.DirectionEpsilonSqr
                ? projected.normalized
                : Vector3.zero;
        }

        private Vector3 CellCenter(int index)
        {
            int x = index % _width;
            int z = index / _width;
            return new Vector3(
                _gridOriginWorld.x + (x + 0.5f) * _cellSize,
                _surfaceHeights[index],
                _gridOriginWorld.z + (z + 0.5f) * _cellSize);
        }

        private static bool ApproximatelyUnit(Vector3 value)
            => Mathf.Abs(value.sqrMagnitude - 1f) <= 0.01f;

        /// <summary>
        /// True when the asset is valid and was baked with these settings;
        /// otherwise the reason, the surface part first.
        /// </summary>
        internal bool Matches(in FlowFieldSurfaceSignature signature, out string reason)
        {
            if (!Validate(out reason))
                return false;
            FlowFieldSurfaceSignature baked = BakedSignature();
            if (!signature.Surface.IsValid || !baked.SurfaceMatches(signature))
                return Fail(out reason, "Static Flow Bake Surface settings differ from the Manager settings.");
            if (!baked.ObstaclesMatch(signature))
                return Fail(out reason, "Static Flow Bake obstacle settings differ from the Manager settings.");
            return true;
        }

        // The settings the header records. Only a validated header has them:
        // FromCellGrid throws on an invalid one.
        private FlowFieldSurfaceSignature BakedSignature()
            => new FlowFieldSurfaceSignature(
                new FlowFieldSurfaceBakeSettings(
                    FlowFieldGridSpace.FromCellGrid(_gridOriginWorld, _width, _depth, _cellSize),
                    new Bounds(_bakeBoundsCenterWorld, _bakeBoundsSizeWorld),
                    _groundLayerMask,
                    _maxSurfaceSlope,
                    _maxStepHeight),
                _obstacleLayerMask,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance);

        internal bool MatchesGoal(bool hasGoal, Vector3 requestedGoalWorld, float influenceRadius, out string reason)
        {
            reason = string.Empty;
            if (hasGoal == _hasGoal && (!hasGoal
                    || (requestedGoalWorld - _requestedGoalWorld).sqrMagnitude <= Epsilon * Epsilon
                        && Mathf.Abs(influenceRadius - _goalInfluenceRadius) <= Epsilon))
                return true;
            return Fail(out reason, "Static Flow Bake Goal differs from the Manager settings. ReBake is required.");
        }

        // Called once Matches accepted the settings.
        private FlowFieldSurfaceData CreateSurfaceData(in FlowFieldSurfaceBakeSettings settings)
        {
            int valid = 0;
            for (int i = 0; i < _surfaceFlags.Length; i++)
                if ((_surfaceFlags[i] & 1) != 0) valid++;
            return FlowFieldSurfaceData.FromSnapshot(
                settings,
                _surfaceHeights,
                _surfaceNormals,
                _surfaceFlags,
                _surfaceNeighborMasks,
                valid,
                Revision);
        }

        private void AssignHeader(
            in FlowFieldSurfaceSignature signature,
            bool hasGoal,
            Vector3 requestedGoalWorld,
            float goalInfluenceRadius,
            int resolvedGoalIndex)
        {
            FlowFieldSurfaceBakeSettings settings = signature.Surface;
            _gridOriginWorld = settings.Grid.Origin;
            _width = settings.Grid.Width;
            _depth = settings.Grid.Depth;
            _cellSize = settings.Grid.CellSize;
            _groundLayerMask = settings.GroundLayer.value;
            _bakeBoundsCenterWorld = settings.BakeBounds.center;
            _bakeBoundsSizeWorld = settings.BakeBounds.size;
            _maxSurfaceSlope = settings.MaxSurfaceSlope;
            _maxStepHeight = settings.MaxStepHeight;
            _obstacleLayerMask = signature.ObstacleLayer.value;
            _obstacleCheckHeight = signature.ObstacleCheckHeight;
            _obstacleCheckCenterOffset = signature.ObstacleCheckCenterOffset;
            _obstacleClearance = signature.ObstacleClearance;
            _hasGoal = hasGoal;
            _requestedGoalWorld = requestedGoalWorld;
            _goalInfluenceRadius = goalInfluenceRadius;
            _resolvedGoalIndex = hasGoal ? resolvedGoalIndex : -1;
        }

        internal void Apply(
            in FlowFieldSurfaceSignature signature,
            FlowFieldSurfaceData surface,
            bool hasGoal,
            Vector3 requestedGoalWorld,
            float goalInfluenceRadius,
            int resolvedGoalIndex,
            FlowFieldSurfaceWorkspace workspace)
        {
            FlowFieldSurfaceBakeSettings settings = signature.Surface;
            if (!settings.IsValid || surface == null || !surface.IsValid)
                throw new ArgumentException("Static Flow Bake input is invalid.");
            if (workspace == null || workspace.Capacity != settings.Grid.CellCount)
                throw new ArgumentException("Static Flow Bake workspace capacity does not match the grid.", nameof(workspace));
            if (!FlowFieldGridSpace.IsFinite(requestedGoalWorld)
                || !FlowFieldGridSpace.IsFinite(goalInfluenceRadius) || goalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(goalInfluenceRadius));
            if (signature.ObstacleLayer.value == 0
                || !FlowFieldGridSpace.IsFinite(signature.ObstacleCheckHeight) || signature.ObstacleCheckHeight <= 0f
                || !FlowFieldGridSpace.IsFinite(signature.ObstacleCheckCenterOffset)
                || !FlowFieldGridSpace.IsFinite(signature.ObstacleClearance) || signature.ObstacleClearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(signature));
            if (hasGoal && (resolvedGoalIndex < 0 || resolvedGoalIndex >= settings.Grid.CellCount))
                throw new ArgumentOutOfRangeException(nameof(resolvedGoalIndex));

            surface.CopyToArrays(
                out float[] surfaceHeights,
                out Vector3[] surfaceNormals,
                out byte[] surfaceFlags,
                out byte[] surfaceNeighborMasks);

            // Validate a complete candidate before touching this Asset. The
            // candidate borrows the incoming arrays, so an invalid payload
            // cannot partially replace serialized metadata or increment the
            // Asset revision. Only the successful commit below clones data
            // into the persistent Asset.
            FlowFieldSurfaceBakeData candidate = ScriptableObject.CreateInstance<FlowFieldSurfaceBakeData>();
            try
            {
                // The candidate is checked as the bake it would become: current format.
                candidate.MarkBaked();
                candidate.AssignHeader(signature, hasGoal, requestedGoalWorld, goalInfluenceRadius, resolvedGoalIndex);
                candidate._surfaceHeights = surfaceHeights;
                candidate._surfaceNormals = surfaceNormals;
                candidate._surfaceFlags = surfaceFlags;
                candidate._surfaceNeighborMasks = surfaceNeighborMasks;
                candidate._blocked = workspace.Blocked;
                candidate._escapeDirections = workspace.EscapeDirections;
                candidate._topologyMasks = workspace.TopologyMasks;
                candidate._goalFlags = workspace.GoalFlags;
                candidate._goalDirections = workspace.GoalDirections;
                candidate._nextCells = workspace.NextCells;

                if (!candidate.Validate(out string reason))
                    throw new InvalidOperationException(reason);

                _snapshotCache = null;
                _snapshotCacheRevision = -1;
                MarkBaked();
                AssignHeader(signature, hasGoal, requestedGoalWorld, goalInfluenceRadius, resolvedGoalIndex);
                _surfaceHeights = surfaceHeights;
                _surfaceNormals = surfaceNormals;
                _surfaceFlags = surfaceFlags;
                _surfaceNeighborMasks = surfaceNeighborMasks;
                _blocked = FlowFieldSurfaceBakeSnapshot.Clone(workspace.Blocked);
                _escapeDirections = FlowFieldSurfaceBakeSnapshot.Clone(workspace.EscapeDirections);
                _topologyMasks = FlowFieldSurfaceBakeSnapshot.Clone(workspace.TopologyMasks);
                _goalFlags = FlowFieldSurfaceBakeSnapshot.Clone(workspace.GoalFlags);
                _goalDirections = FlowFieldSurfaceBakeSnapshot.Clone(workspace.GoalDirections);
                _nextCells = FlowFieldSurfaceBakeSnapshot.Clone(workspace.NextCells);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(candidate);
            }
        }
    }
}
