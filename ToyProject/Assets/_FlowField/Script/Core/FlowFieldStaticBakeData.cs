using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Immutable base-field snapshot produced by the shared editor bake
    /// pipeline.  The geometry arrays are authoritative; the legacy Surface
    /// asset reference is retained only as a compatibility breadcrumb for
    /// older scenes.  Runtime StaticBaked sessions reconstruct the same
    /// internal surface model from this asset and never need to query Physics.
    /// </summary>
    public sealed class FlowFieldStaticBakeData : ScriptableObject
    {
        // Direction predecessor selection is part of the serialized base
        // field, so changing its deterministic tie-break requires a ReBake.
        internal const int CURRENT_FORMAT_VERSION = 3;
        private const float SIGNATURE_EPSILON = 0.0001f;
        private const int MIN_NEXT_SENTINEL = -3;

        [SerializeField] private int _formatVersion;
        [SerializeField] private int _revision;
        [SerializeField] private FlowFieldSurfaceBakeData _surfaceBakeData;
        [SerializeField] private int _surfaceRevision = -1;
        // The static snapshot keeps a copy of the baked geometry so the asset
        // is self-describing. The legacy SurfaceBakeData reference is retained
        // for compatibility and for the shared runtime surface view.
        [SerializeField] private float[] _surfaceHeights = Array.Empty<float>();
        [SerializeField] private Vector3[] _surfaceNormals = Array.Empty<Vector3>();
        [SerializeField] private byte[] _surfaceFlags = Array.Empty<byte>();
        [SerializeField] private byte[] _surfaceNeighborMasks = Array.Empty<byte>();
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
        [SerializeField] private bool _hasGoal;
        [SerializeField] private Vector3 _requestedGoalWorld;
        [SerializeField] private float _goalInfluenceRadius;
        [SerializeField] private int _resolvedGoalIndex = -1;
        [SerializeField] private bool[] _blocked = Array.Empty<bool>();
        [SerializeField] private Vector3[] _escapeDirections = Array.Empty<Vector3>();
        [SerializeField] private byte[] _topologyMasks = Array.Empty<byte>();
        [SerializeField] private Vector3[] _goalDirections = Array.Empty<Vector3>();
        [SerializeField] private int[] _nextCells = Array.Empty<int>();

        internal FlowFieldSurfaceBakeData SurfaceBakeData => _surfaceBakeData;
        public int FormatVersion => _formatVersion;
        public int Revision => _revision;
        public bool HasGoal => _hasGoal;
        public Vector3 RequestedGoalWorld => _requestedGoalWorld;
        public float GoalInfluenceRadius => _goalInfluenceRadius;
        public int ResolvedGoalIndex => _resolvedGoalIndex;
        internal bool[] Blocked => _blocked;
        internal Vector3[] EscapeDirections => _escapeDirections;
        internal byte[] TopologyMasks => _topologyMasks;
        internal Vector3[] GoalDirections => _goalDirections;
        internal int[] NextCells => _nextCells;

        public bool HasValidData
        {
            get
            {
                if (_formatVersion != CURRENT_FORMAT_VERSION
                    || !FlowFieldBakeBoundsUtility.TryValidateCellCount(_width, _depth, out int expectedCount)
                    || !FlowFieldGridSpace.IsFinite(_gridOriginWorld)
                    || !FlowFieldGridSpace.IsFinite(_cellSize)
                    || _cellSize < FlowFieldBakeBoundsUtility.MinCellSize
                    || _groundLayerMask == 0
                    || !FlowFieldGridSpace.IsFinite(_bakeBoundsCenterWorld)
                    || !FlowFieldGridSpace.IsFinite(_bakeBoundsSizeWorld)
                    || _bakeBoundsSizeWorld.x <= 0f
                    || _bakeBoundsSizeWorld.y < FlowFieldBakeBoundsUtility.MinBoundsHeight
                    || _bakeBoundsSizeWorld.z <= 0f
                    || !FlowFieldGridSpace.IsFinite(_maxSurfaceSlope)
                    || _maxSurfaceSlope < 0f
                    || _maxSurfaceSlope >= 90f
                    || !FlowFieldGridSpace.IsFinite(_maxStepHeight)
                    || _maxStepHeight < 0f
                    || _obstacleLayerMask == 0
                    || !FlowFieldGridSpace.IsFinite(_obstacleCheckHeight)
                    || _obstacleCheckHeight <= 0f
                    || !FlowFieldGridSpace.IsFinite(_obstacleCheckCenterOffset)
                    || !FlowFieldGridSpace.IsFinite(_obstacleClearance)
                    || _obstacleClearance < 0f
                    || !_hasGoal && _resolvedGoalIndex != -1
                    || _hasGoal && (_resolvedGoalIndex < 0 || _resolvedGoalIndex >= expectedCount)
                    || !FlowFieldGridSpace.IsFinite(_requestedGoalWorld)
                    || !FlowFieldGridSpace.IsFinite(_goalInfluenceRadius)
                    || _goalInfluenceRadius < 0f
                    || _blocked == null || _blocked.Length != expectedCount
                    || _escapeDirections == null || _escapeDirections.Length != expectedCount
                    || _topologyMasks == null || _topologyMasks.Length != expectedCount
                    || _goalDirections == null || _goalDirections.Length != expectedCount
                    || _nextCells == null || _nextCells.Length != expectedCount)
                    return false;

                if (_surfaceHeights == null || _surfaceHeights.Length != expectedCount
                    || _surfaceNormals == null || _surfaceNormals.Length != expectedCount
                    || _surfaceFlags == null || _surfaceFlags.Length != expectedCount
                    || _surfaceNeighborMasks == null || _surfaceNeighborMasks.Length != expectedCount)
                    return false;

                for (int index = 0; index < expectedCount; index++)
                {
                    if (!FlowFieldGridSpace.IsFinite(_surfaceHeights[index])
                        || !FlowFieldGridSpace.IsFinite(_surfaceNormals[index])
                        || !FlowFieldGridSpace.IsFinite(_escapeDirections[index])
                        || !FlowFieldGridSpace.IsFinite(_goalDirections[index]))
                        return false;

                    if ((_surfaceFlags[index] & 1) != 0
                        && (_surfaceNormals[index].sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                            || !IsApproximatelyUnit(_surfaceNormals[index])))
                        return false;

                    if ((_surfaceFlags[index] & ~1) != 0
                        || (_surfaceFlags[index] == 0 && _surfaceNeighborMasks[index] != 0))
                        return false;

                    if (!IsNormalizedOrZero(_escapeDirections[index])
                        || !IsNormalizedOrZero(_goalDirections[index]))
                        return false;

                    int next = _nextCells[index];
                    if (next < MIN_NEXT_SENTINEL || next >= expectedCount)
                        return false;
                }

                return IsConsistentSnapshot(expectedCount);
            }
        }

        private bool IsConsistentSnapshot(int expectedCount)
        {
            int anchorCount = 0;
            for (int index = 0; index < expectedCount; index++)
            {
                bool valid = (_surfaceFlags[index] & 1) != 0;
                bool blocked = _blocked[index];
                int next = _nextCells[index];

                if (!valid || blocked)
                {
                    if (next != -2 || _topologyMasks[index] != 0)
                        return false;
                    continue;
                }

                if (_hasGoal && next == -3)
                {
                    // Unreachable cells are still inside the goal influence
                    // and therefore must remain walkable. Their topology is
                    // retained in the snapshot for diagnostics and for a
                    // subsequent shared composition pass.
                    if (_goalDirections[index] != Vector3.zero)
                        return false;
                    continue;
                }

                if (next == -1)
                {
                    // A goal influence radius may leave valid cells outside
                    // the goal domain.  Those cells intentionally carry the
                    // no-direction sentinel even when the snapshot has a
                    // goal; their topology is zero because the shared BFS
                    // only builds edges inside the influence mask.  A
                    // no-goal snapshot retains its surface topology for
                    // diagnostics, so only enforce the zero mask in the
                    // goal case.
                    if (_goalDirections[index] != Vector3.zero)
                        return false;
                    if (_hasGoal && _topologyMasks[index] != 0)
                        return false;
                    continue;
                }

                if (next < 0 || next >= expectedCount)
                    return false;

                if (!_hasGoal)
                    return false;

                if (next == index)
                {
                    if (index != _resolvedGoalIndex || _goalDirections[index] != Vector3.zero)
                        return false;
                    anchorCount++;
                    continue;
                }

                if (!IsValidWalkable(next))
                    return false;

                int direction = FindDirection(index, next);
                if (direction < 0 || (_topologyMasks[index] & (1 << direction)) == 0)
                    return false;
                if (_goalDirections[index].sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                    return false;
            }

            return !_hasGoal
                ? _resolvedGoalIndex == -1
                : anchorCount == 1;
        }

        private bool IsValidWalkable(int index)
            => index >= 0
                && index < _surfaceFlags.Length
                && (_surfaceFlags[index] & 1) != 0
                && !_blocked[index];

        private int FindDirection(int from, int to)
        {
            int fromX = from % _width;
            int fromZ = from / _width;
            int toX = to % _width;
            int toZ = to / _width;
            int dx = toX - fromX;
            int dz = toZ - fromZ;
            for (int direction = 0; direction < FlowFieldNeighborUtility.Count; direction++)
            {
                if (FlowFieldNeighborUtility.DeltaX[direction] == dx
                    && FlowFieldNeighborUtility.DeltaZ[direction] == dz)
                    return direction;
            }

            return -1;
        }

        private static bool IsApproximatelyUnit(Vector3 value)
            => Mathf.Abs(value.sqrMagnitude - 1f) <= 0.01f;

        private static bool IsNormalizedOrZero(Vector3 value)
            => value.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                || IsApproximatelyUnit(value);

        internal bool Matches(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            out string reason)
        {
            reason = string.Empty;
            if (!HasValidData)
            {
                reason = "Static Flow Bake Asset에 유효한 데이터가 없습니다.";
                return false;
            }
            if (!grid.IsValid
                || _width != grid.Width
                || _depth != grid.Depth
                || Mathf.Abs(_cellSize - grid.CellSize) > SIGNATURE_EPSILON
                || (_gridOriginWorld - grid.Origin).sqrMagnitude > SIGNATURE_EPSILON * SIGNATURE_EPSILON)
            {
                reason = "Static Flow Bake Grid가 현재 설정과 다릅니다.";
                return false;
            }
            if (_obstacleLayerMask != obstacleLayer.value
                || Mathf.Abs(_obstacleCheckHeight - checkHeight) > SIGNATURE_EPSILON
                || Mathf.Abs(_obstacleCheckCenterOffset - centerOffset) > SIGNATURE_EPSILON
                || Mathf.Abs(_obstacleClearance - clearance) > SIGNATURE_EPSILON)
            {
                reason = "Static Flow Bake 장애물 설정이 현재 설정과 다릅니다.";
                return false;
            }

            return true;
        }

        internal bool MatchesSurface(
            in FlowFieldSurfaceBakeSettings settings,
            out string reason)
        {
            reason = string.Empty;
            if (!HasValidData)
            {
                reason = "Static Flow Bake Asset에 유효한 데이터가 없습니다.";
                return false;
            }

            FlowFieldGridSpace grid = settings.Grid;
            if (!settings.IsValid
                || _width != grid.Width
                || _depth != grid.Depth
                || Mathf.Abs(_cellSize - grid.CellSize) > SIGNATURE_EPSILON
                || (_gridOriginWorld - grid.Origin).sqrMagnitude > SIGNATURE_EPSILON * SIGNATURE_EPSILON
                || _groundLayerMask != settings.GroundLayer.value
                || (_bakeBoundsCenterWorld - settings.BakeBounds.center).sqrMagnitude > SIGNATURE_EPSILON * SIGNATURE_EPSILON
                || (_bakeBoundsSizeWorld - settings.BakeBounds.size).sqrMagnitude > SIGNATURE_EPSILON * SIGNATURE_EPSILON
                || Mathf.Abs(_maxSurfaceSlope - settings.MaxSurfaceSlope) > SIGNATURE_EPSILON
                || Mathf.Abs(_maxStepHeight - settings.MaxStepHeight) > SIGNATURE_EPSILON)
            {
                reason = "Static Flow Bake Surface 설정이 현재 설정과 다릅니다.";
                return false;
            }

            return true;
        }

        internal bool MatchesGoal(bool hasGoal, Vector3 requestedGoalWorld, float influenceRadius)
        {
            if (hasGoal != _hasGoal)
                return false;
            if (!hasGoal)
                return true;
            return (requestedGoalWorld - _requestedGoalWorld).sqrMagnitude <= SIGNATURE_EPSILON * SIGNATURE_EPSILON
                && Mathf.Abs(influenceRadius - _goalInfluenceRadius) <= SIGNATURE_EPSILON;
        }

        internal void CopyToWorkspace(
            FlowFieldGridSpace grid,
            FlowFieldWorkspace workspace)
        {
            if (!HasValidData)
                throw new InvalidOperationException("Static Flow Bake Asset is invalid.");
            if (workspace == null || workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Static Flow Bake workspace capacity does not match the grid.", nameof(workspace));

            Array.Copy(_blocked, workspace.StaticBlocked, _blocked.Length);
            Array.Clear(workspace.DynamicBlocked, 0, workspace.DynamicBlocked.Length);
            workspace.RebuildCombinedBlocked();
            Array.Copy(_escapeDirections, workspace.EscapeDirections, _escapeDirections.Length);
            Array.Copy(_topologyMasks, workspace.TopologyMasks, _topologyMasks.Length);
            Array.Copy(_goalDirections, workspace.GoalDirections, _goalDirections.Length);
            Array.Copy(_nextCells, workspace.NextCells, _nextCells.Length);
            workspace.LoadBakedGoal(_hasGoal, _resolvedGoalIndex, _nextCells);
        }

        /// <summary>
        /// Creates the transient calculation view consumed by the shared
        /// solver/mask/composer code.  This is deliberately not an asset and
        /// is destroyed with the owning Manager/session.
        /// </summary>
        internal FlowFieldSurfaceBakeData CreateSurfaceBakeData(
            in FlowFieldSurfaceBakeSettings settings)
        {
            if (!MatchesSurface(settings, out string reason))
                throw new InvalidOperationException(reason);

            int count = settings.Grid.CellCount;
            var result = new FlowFieldSurfaceBakeResult(count);
            for (int index = 0; index < count; index++)
            {
                if ((_surfaceFlags[index] & 1) == 0)
                    continue;

                result.SetSurface(index, _surfaceHeights[index], _surfaceNormals[index]);
                result.NeighborMasks[index] = _surfaceNeighborMasks[index];
            }

            var surface = ScriptableObject.CreateInstance<FlowFieldSurfaceBakeData>();
            surface.name = $"{name}_RuntimeSurface";
            surface.hideFlags = HideFlags.HideAndDontSave;
            surface.Apply(settings, result);
            return surface;
        }

        internal void Apply(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeData surface,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool hasGoal,
            Vector3 requestedGoalWorld,
            float goalInfluenceRadius,
            int resolvedGoalIndex,
            FlowFieldWorkspace workspace)
        {
            if (surface == null)
                throw new ArgumentNullException(nameof(surface));

            FlowFieldSurfaceBakeSettings settings = new FlowFieldSurfaceBakeSettings(
                grid,
                surface.BakeBoundsWorld,
                surface.GroundLayer,
                surface.MaxSurfaceSlope,
                surface.MaxStepHeight);
            Apply(
                settings,
                surface,
                obstacleLayer,
                checkHeight,
                centerOffset,
                clearance,
                hasGoal,
                requestedGoalWorld,
                goalInfluenceRadius,
                resolvedGoalIndex,
                workspace);
        }

        internal void Apply(
            in FlowFieldSurfaceBakeSettings surfaceSettings,
            FlowFieldSurfaceBakeData surface,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            bool hasGoal,
            Vector3 requestedGoalWorld,
            float goalInfluenceRadius,
            int resolvedGoalIndex,
            FlowFieldWorkspace workspace)
        {
            FlowFieldGridSpace grid = surfaceSettings.Grid;
            if (!surfaceSettings.IsValid)
                throw new ArgumentException("Static Flow Bake Surface settings are invalid.", nameof(surfaceSettings));
            if (surface == null || !surface.HasValidData)
                throw new ArgumentException("Static Flow Bake requires a valid Surface Asset.", nameof(surface));
            if (workspace == null || workspace.Capacity != grid.CellCount)
                throw new ArgumentException("Static Flow Bake workspace capacity does not match the grid.", nameof(workspace));
            if (!FlowFieldGridSpace.IsFinite(requestedGoalWorld)
                || !FlowFieldGridSpace.IsFinite(goalInfluenceRadius)
                || goalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException(nameof(goalInfluenceRadius));
            if (hasGoal && (resolvedGoalIndex < 0 || resolvedGoalIndex >= grid.CellCount))
                throw new ArgumentOutOfRangeException(nameof(resolvedGoalIndex));

            _formatVersion = CURRENT_FORMAT_VERSION;
            _revision++;
            _surfaceBakeData = surface;
            _surfaceRevision = surface.Revision;
            CopySurfaceSnapshot(surface, grid, out _surfaceHeights, out _surfaceNormals, out _surfaceFlags, out _surfaceNeighborMasks);
            _gridOriginWorld = grid.Origin;
            _width = grid.Width;
            _depth = grid.Depth;
            _cellSize = grid.CellSize;
            _groundLayerMask = surfaceSettings.GroundLayer.value;
            _bakeBoundsCenterWorld = surfaceSettings.BakeBounds.center;
            _bakeBoundsSizeWorld = surfaceSettings.BakeBounds.size;
            _maxSurfaceSlope = surfaceSettings.MaxSurfaceSlope;
            _maxStepHeight = surfaceSettings.MaxStepHeight;
            _obstacleLayerMask = obstacleLayer.value;
            _obstacleCheckHeight = checkHeight;
            _obstacleCheckCenterOffset = centerOffset;
            _obstacleClearance = clearance;
            _hasGoal = hasGoal;
            _requestedGoalWorld = requestedGoalWorld;
            _goalInfluenceRadius = goalInfluenceRadius;
            _resolvedGoalIndex = hasGoal ? resolvedGoalIndex : -1;
            _blocked = Clone(workspace.Blocked);
            _escapeDirections = Clone(workspace.EscapeDirections);
            _topologyMasks = Clone(workspace.TopologyMasks);
            _goalDirections = Clone(workspace.GoalDirections);
            _nextCells = Clone(workspace.NextCells);
        }

        private static T[] Clone<T>(T[] source)
        {
            if (source == null)
                return Array.Empty<T>();
            var clone = new T[source.Length];
            Array.Copy(source, clone, source.Length);
            return clone;
        }

        private static void CopySurfaceSnapshot(
            FlowFieldSurfaceBakeData surface,
            FlowFieldGridSpace grid,
            out float[] heights,
            out Vector3[] normals,
            out byte[] flags,
            out byte[] neighborMasks)
        {
            int count = grid.CellCount;
            heights = new float[count];
            normals = new Vector3[count];
            flags = new byte[count];
            neighborMasks = new byte[count];
            for (int index = 0; index < count; index++)
            {
                if (!surface.IsSurfaceValid(index))
                    continue;
                Vector3 center = surface.GetCellCenter(grid, index);
                heights[index] = center.y;
                normals[index] = surface.GetSurfaceNormal(index);
                flags[index] = 1;
                neighborMasks[index] = surface.GetNeighborMask(index);
            }
        }
    }
}
