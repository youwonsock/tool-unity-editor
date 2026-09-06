using System;
using UnityEngine;

namespace Common.FlowField
{
    internal enum FlowFieldVolumeBakeValidationStatus
    {
        Pending = 0,
        Valid = 1,
        Invalid = 2,
        Cancelled = 3,
    }

    /// <summary>
    /// A non-owning view of serialized Volume3D bake data.  The validator
    /// keeps its own small state arrays, but never clones the bake payload.
    /// </summary>
    internal readonly struct FlowFieldVolumeBakeValidationView
    {
        internal readonly int FormatVersion;
        internal readonly int Revision;
        internal readonly int Width;
        internal readonly int Height;
        internal readonly int Depth;
        internal readonly float CellSize;
        internal readonly Vector3 Origin;
        internal readonly Vector3 BoundsCenter;
        internal readonly Vector3 BoundsSize;
        internal readonly int ObstacleLayerMask;
        internal readonly float ObstacleClearance;
        internal readonly bool HasGoal;
        internal readonly Vector3 RequestedGoalWorld;
        internal readonly float GoalInfluenceRadius;
        internal readonly int ResolvedGoalIndex;
        internal readonly bool[] Blocked;
        internal readonly uint[] TopologyMasks;
        internal readonly FlowFieldGoalFlags[] GoalFlags;
        internal readonly int[] NextCells;
        internal readonly Vector3[] Directions;
        internal readonly float[] Speeds;
        internal readonly Vector3[] EscapeDirections;

        internal FlowFieldVolumeBakeValidationView(
            int formatVersion,
            int revision,
            int width,
            int height,
            int depth,
            float cellSize,
            Vector3 origin,
            Vector3 boundsCenter,
            Vector3 boundsSize,
            int obstacleLayerMask,
            float obstacleClearance,
            bool hasGoal,
            Vector3 requestedGoalWorld,
            float goalInfluenceRadius,
            int resolvedGoalIndex,
            bool[] blocked,
            uint[] topologyMasks,
            FlowFieldGoalFlags[] goalFlags,
            int[] nextCells,
            Vector3[] directions,
            float[] speeds,
            Vector3[] escapeDirections)
        {
            FormatVersion = formatVersion;
            Revision = revision;
            Width = width;
            Height = height;
            Depth = depth;
            CellSize = cellSize;
            Origin = origin;
            BoundsCenter = boundsCenter;
            BoundsSize = boundsSize;
            ObstacleLayerMask = obstacleLayerMask;
            ObstacleClearance = obstacleClearance;
            HasGoal = hasGoal;
            RequestedGoalWorld = requestedGoalWorld;
            GoalInfluenceRadius = goalInfluenceRadius;
            ResolvedGoalIndex = resolvedGoalIndex;
            Blocked = blocked;
            TopologyMasks = topologyMasks;
            GoalFlags = goalFlags;
            NextCells = nextCells;
            Directions = directions;
            Speeds = speeds;
            EscapeDirections = escapeDirections;
        }
    }

    /// <summary>
    /// Cooperative structural validator shared by editor display checks and
    /// the synchronous callers that need the same payload rules.  A call to
    /// Step performs bounded cell work or bounded graph work and can resume
    /// without walking an already confirmed path again.
    /// </summary>
    internal sealed class FlowFieldVolumeBakeValidator : IDisposable
    {
        private enum Phase
        {
            Metadata,
            Cells,
            Graph,
            Complete,
        }

        private const int MinSentinel = -3;
        private const float DirectionToleranceSqr = 0.00000001f;

        private readonly FlowFieldGridSpace _expectedGrid;
        private FlowFieldVolumeBakeValidationView _view;
        private byte[] _graphState;
        private int[] _path;
        private int _cellCursor;
        private int _graphCursor;
        private int _pathCount;
        private int _currentNode = -1;
        private int _anchorCount;
        private Phase _phase;
        private FlowFieldVolumeBakeValidationStatus _status;
        private string _error;

        internal FlowFieldVolumeBakeValidationStatus Status => _status;
        internal bool IsComplete => _status == FlowFieldVolumeBakeValidationStatus.Valid
            || _status == FlowFieldVolumeBakeValidationStatus.Invalid
            || _status == FlowFieldVolumeBakeValidationStatus.Cancelled;
        internal bool IsValid => _status == FlowFieldVolumeBakeValidationStatus.Valid;
        internal string Error => _error;
        internal float Progress
        {
            get
            {
                if (_status == FlowFieldVolumeBakeValidationStatus.Valid)
                    return 1f;
                if (_status == FlowFieldVolumeBakeValidationStatus.Invalid
                    || _status == FlowFieldVolumeBakeValidationStatus.Cancelled)
                    return Mathf.Clamp01(_phase == Phase.Metadata ? 0f : 1f);
                int count = Math.Max(1, _expectedGrid.CellCount);
                if (_phase == Phase.Metadata)
                    return 0f;
                if (_phase == Phase.Cells)
                    return 0.05f + 0.7f * _cellCursor / count;
                if (_phase == Phase.Graph)
                    return 0.75f + 0.25f * _graphCursor / count;
                return 0f;
            }
        }

        internal FlowFieldVolumeBakeValidator(
            FlowFieldVolumeBakeData asset,
            FlowFieldGridSpace expectedGrid)
        {
            _expectedGrid = expectedGrid;
            if (asset == null || !asset.TryGetValidationView(out FlowFieldVolumeBakeValidationView view))
            {
                Initialize(default, invalidReason: "Static Volume3D Bake arrays are missing.");
                return;
            }
            Initialize(view);
        }

        internal FlowFieldVolumeBakeValidator(
            in FlowFieldVolumeBakeValidationView view,
            FlowFieldGridSpace expectedGrid)
        {
            _expectedGrid = expectedGrid;
            Initialize(view);
        }

        internal static bool ValidatePayload(
            in FlowFieldVolumeBakeValidationView view,
            FlowFieldGridSpace expectedGrid,
            out string reason)
        {
            using (FlowFieldVolumeBakeValidator validator =
                new FlowFieldVolumeBakeValidator(view, expectedGrid))
            {
                while (!validator.IsComplete)
                    validator.Step(4096, 64);
                reason = validator.Error;
                return validator.IsValid;
            }
        }

        private void Initialize(
            in FlowFieldVolumeBakeValidationView view,
            string invalidReason = null)
        {
            _view = view;
            _graphState = null;
            _path = null;
            _cellCursor = 0;
            _graphCursor = 0;
            _pathCount = 0;
            _currentNode = -1;
            _anchorCount = 0;
            _phase = Phase.Metadata;
            _status = FlowFieldVolumeBakeValidationStatus.Pending;
            _error = string.Empty;

            if (!string.IsNullOrEmpty(invalidReason))
            {
                _phase = Phase.Complete;
                _status = FlowFieldVolumeBakeValidationStatus.Invalid;
                _error = invalidReason;
                return;
            }
            if (_view.Blocked == null
                || _view.TopologyMasks == null
                || _view.GoalFlags == null
                || _view.NextCells == null
                || _view.Directions == null
                || _view.Speeds == null
                || _view.EscapeDirections == null)
            {
                _phase = Phase.Complete;
                _status = FlowFieldVolumeBakeValidationStatus.Invalid;
                _error = "Static Volume3D Bake arrays are missing.";
                return;
            }

            if (_view.Blocked.Length > FlowFieldBakeBoundsUtility.MaxVolumeCellCount)
            {
                _phase = Phase.Complete;
                _status = FlowFieldVolumeBakeValidationStatus.Invalid;
                _error = "Static Volume3D Bake contains more than 1,000,000 cells.";
                return;
            }

            _graphState = new byte[_view.Blocked.Length];
            _path = new int[_view.Blocked.Length];
        }

        internal void Cancel()
        {
            if (IsComplete)
                return;
            _phase = Phase.Complete;
            _status = FlowFieldVolumeBakeValidationStatus.Cancelled;
            _error = "Static Volume3D Bake validation was cancelled.";
        }

        internal void Step(int cellBudget = 4096, int graphBudget = 64)
        {
            if (IsComplete)
                return;
            cellBudget = Math.Max(1, cellBudget);
            graphBudget = Math.Max(1, graphBudget);

            if (_phase == Phase.Metadata)
            {
                if (!ValidateMetadata(out string metadataError))
                {
                    Fail(metadataError);
                    return;
                }
                _phase = Phase.Cells;
            }

            if (_phase == Phase.Cells)
            {
                int processed = 0;
                while (_cellCursor < _view.Blocked.Length && processed < cellBudget)
                {
                    if (!ValidateCell(_cellCursor, out string cellError))
                    {
                        Fail(cellError);
                        return;
                    }
                    _cellCursor++;
                    processed++;
                }

                if (_cellCursor < _view.Blocked.Length)
                    return;
                if (_view.HasGoal && _anchorCount != 1)
                {
                    Fail($"Static Volume3D Bake must contain exactly one Goal anchor; found {_anchorCount}.");
                    return;
                }
                if (!_view.HasGoal && _anchorCount != 0)
                {
                    Fail("Static Volume3D Bake without a Goal contains an anchor.");
                    return;
                }
                _phase = Phase.Graph;
            }

            if (_phase != Phase.Graph)
                return;

            int graphSteps = 0;
            while (_graphCursor < _view.NextCells.Length && graphSteps < graphBudget)
            {
                if (_currentNode < 0)
                {
                    while (_graphCursor < _view.NextCells.Length
                        && !IsDirectedCell(_graphCursor))
                        _graphCursor++;
                    if (_graphCursor >= _view.NextCells.Length)
                        break;
                    if (_graphState[_graphCursor] == 2)
                    {
                        _graphCursor++;
                        continue;
                    }
                    _currentNode = _graphCursor;
                    _pathCount = 0;
                }

                if (_currentNode == _view.ResolvedGoalIndex)
                {
                    ConfirmPath();
                    _graphCursor++;
                    _currentNode = -1;
                    graphSteps++;
                    continue;
                }
                if (_currentNode < 0 || _currentNode >= _view.NextCells.Length
                    || _view.Blocked[_currentNode]
                    || _view.NextCells[_currentNode] < 0)
                {
                    Fail(DescribeCell(_currentNode, "directed path does not reach the Goal anchor"));
                    return;
                }
                if (_graphState[_currentNode] == 1)
                {
                    Fail(DescribeCell(_currentNode, "directed path contains a cycle"));
                    return;
                }
                if (_graphState[_currentNode] == 2)
                {
                    ConfirmPath();
                    _graphCursor++;
                    _currentNode = -1;
                    graphSteps++;
                    continue;
                }

                _graphState[_currentNode] = 1;
                _path[_pathCount++] = _currentNode;
                _currentNode = _view.NextCells[_currentNode];
                graphSteps++;
            }

            if (_graphCursor >= _view.NextCells.Length && _currentNode < 0)
            {
                _phase = Phase.Complete;
                _status = FlowFieldVolumeBakeValidationStatus.Valid;
                _error = string.Empty;
            }
        }

        public void Dispose()
        {
            _graphState = null;
            _path = null;
        }

        private bool ValidateMetadata(out string reason)
        {
            reason = string.Empty;
            if (_view.FormatVersion != FlowFieldVolumeBakeData.CURRENT_FORMAT_VERSION)
            {
                reason = $"FlowField Volume Bake format {_view.FormatVersion} is unsupported; ReBake is required.";
                return false;
            }
            if (!_expectedGrid.IsValid
                || _expectedGrid.SpaceMode != FlowFieldSpaceMode.Volume3D
                || _view.Width != _expectedGrid.Width
                || _view.Height != _expectedGrid.Height
                || _view.Depth != _expectedGrid.Depth
                || !FlowFieldGridSpace.IsFinite(_view.Origin)
                || !FlowFieldGridSpace.IsFinite(_view.CellSize)
                || _view.CellSize < FlowFieldBakeBoundsUtility.MinCellSize
                || !FlowFieldGridSpace.Approximately(_view.Origin, _expectedGrid.Origin, 0.000001d)
                || Mathf.Abs(_view.CellSize - _expectedGrid.CellSize) > 0.0001f)
            {
                reason = "Static Volume3D Bake metadata does not match the display grid.";
                return false;
            }
            if (_view.BoundsSize.x <= 0f || _view.BoundsSize.y <= 0f || _view.BoundsSize.z <= 0f
                || !FlowFieldGridSpace.IsFinite(_view.BoundsCenter)
                || !FlowFieldGridSpace.IsFinite(_view.BoundsSize)
                || !FlowFieldGridSpace.IsFinite(_view.ObstacleClearance)
                || _view.ObstacleClearance < 0f
                || _view.ObstacleLayerMask == 0
                || !FlowFieldGridSpace.IsFinite(_view.RequestedGoalWorld)
                || !FlowFieldGridSpace.IsFinite(_view.GoalInfluenceRadius)
                || _view.GoalInfluenceRadius < 0f)
            {
                reason = "Static Volume3D Bake metadata is invalid.";
                return false;
            }
            Bounds expectedBounds = new Bounds(
                _expectedGrid.Origin
                    + new Vector3(
                        _expectedGrid.WorldSizeX,
                        _expectedGrid.WorldSizeY,
                        _expectedGrid.WorldSizeZ) * 0.5f,
                new Vector3(
                    _expectedGrid.WorldSizeX,
                    _expectedGrid.WorldSizeY,
                    _expectedGrid.WorldSizeZ));
            if (!FlowFieldBakeBoundsUtility.Approximately(
                    expectedBounds,
                    new Bounds(_view.BoundsCenter, _view.BoundsSize)))
            {
                reason = "Static Volume3D Bake Bounds do not match the display grid.";
                return false;
            }
            int count = _expectedGrid.CellCount;
            if (_view.Blocked.Length != count
                || _view.TopologyMasks.Length != count
                || _view.GoalFlags.Length != count
                || _view.NextCells.Length != count
                || _view.Directions.Length != count
                || _view.Speeds.Length != count
                || _view.EscapeDirections.Length != count)
            {
                reason = "Static Volume3D Bake arrays do not match the display grid.";
                return false;
            }
            if ((!_view.HasGoal && _view.ResolvedGoalIndex != -1)
                || (_view.HasGoal
                    && (_view.ResolvedGoalIndex < 0 || _view.ResolvedGoalIndex >= count)))
            {
                reason = "Static Volume3D Bake Goal index is invalid.";
                return false;
            }
            if (_view.HasGoal && _view.Blocked[_view.ResolvedGoalIndex])
            {
                reason = DescribeCell(_view.ResolvedGoalIndex, "Goal index points to a blocked cell");
                return false;
            }
            return true;
        }

        private bool ValidateCell(int index, out string reason)
        {
            reason = string.Empty;
            bool blocked = _view.Blocked[index];
            uint validTopologyBits = (1u << FlowFieldNeighborUtility.VolumeCount) - 1u;
            Vector3 direction = _view.Directions[index];
            Vector3 escape = _view.EscapeDirections[index];
            float speed = _view.Speeds[index];
            int next = _view.NextCells[index];
            FlowFieldGoalFlags flags = _view.GoalFlags[index];

            if (!FlowFieldGridSpace.IsFinite(direction)
                || !FlowFieldGridSpace.IsFinite(escape)
                || !FlowFieldGridSpace.IsFinite(speed)
                || speed < 0f
                || next < MinSentinel
                || next >= _view.Blocked.Length
                || (flags & ~(FlowFieldGoalFlags.Directed
                    | FlowFieldGoalFlags.Anchor
                    | FlowFieldGoalFlags.Unreachable)) != 0
                || !NormalizedOrZero(escape)
                || (_view.TopologyMasks[index] & ~validTopologyBits) != 0u)
            {
                reason = DescribeCell(index, "invalid numeric, sentinel, flags, escape direction, or topology");
                return false;
            }

            if (blocked)
            {
                if (_view.TopologyMasks[index] != 0u
                    || flags != FlowFieldGoalFlags.None
                    || next != -2
                    || direction != Vector3.zero
                    || speed != 0f)
                    reason = DescribeCell(index, "blocked cell contains navigable output");
                return string.IsNullOrEmpty(reason);
            }

            if (next == -2)
            {
                reason = DescribeCell(index, "open cell uses the blocked sentinel");
                return false;
            }
            if (next == -3)
            {
                if (flags != FlowFieldGoalFlags.Unreachable
                    || direction != Vector3.zero
                    || speed != 0f)
                    reason = DescribeCell(index, "unreachable cell is not stopped");
                return string.IsNullOrEmpty(reason);
            }
            if (next == -1)
            {
                if (flags != FlowFieldGoalFlags.None
                    || direction.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                    || Mathf.Abs(direction.magnitude - 1f) > 0.001f
                    || Mathf.Abs(speed - 1f) > 0.001f)
                    reason = DescribeCell(index, "default cell is inconsistent");
                return string.IsNullOrEmpty(reason);
            }
            if (next == index)
            {
                _anchorCount++;
                if (!_view.HasGoal
                    || index != _view.ResolvedGoalIndex
                    || direction != Vector3.zero
                    || speed != 0f
                    || flags != (FlowFieldGoalFlags.Directed | FlowFieldGoalFlags.Anchor))
                    reason = DescribeCell(index, "non-goal anchor");
                return string.IsNullOrEmpty(reason);
            }

            FlowFieldGridSpace expectedGrid = _expectedGrid;
            expectedGrid.FromFlatIndex(index, out int x, out int y, out int z);
            expectedGrid.FromFlatIndex(next, out int nx, out int ny, out int nz);
            int directionIndex = FlowFieldNeighborUtility.FindDirectionIndex(nx - x, ny - y, nz - z);
            if (directionIndex < 0
                || (_view.TopologyMasks[index] & (1u << directionIndex)) == 0u
                || _view.Blocked[next]
                || !IsCornerClear(index, next))
            {
                reason = DescribeCell(index, "NextCell is not a traversable neighbour or cuts through a blocked diagonal corner");
                return false;
            }
            Vector3 expectedDirection = new Vector3(nx - x, ny - y, nz - z).normalized;
            if ((direction - expectedDirection).sqrMagnitude > DirectionToleranceSqr
                || flags != FlowFieldGoalFlags.Directed
                || direction.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                || Mathf.Abs(direction.magnitude - 1f) > 0.001f
                || Mathf.Abs(speed - 1f) > 0.001f)
            {
                reason = DescribeCell(index, "directed cell is inconsistent with NextCell");
                return false;
            }
            return true;
        }

        private bool IsCornerClear(int from, int to)
        {
            _expectedGrid.FromFlatIndex(from, out int x, out int y, out int z);
            _expectedGrid.FromFlatIndex(to, out int nx, out int ny, out int nz);
            int dx = nx - x;
            int dy = ny - y;
            int dz = nz - z;
            if (Math.Abs(dx) + Math.Abs(dy) + Math.Abs(dz) <= 1)
                return true;

            int xSteps = dx == 0 ? 0 : 1;
            int ySteps = dy == 0 ? 0 : 1;
            int zSteps = dz == 0 ? 0 : 1;
            for (int sx = 0; sx <= xSteps; sx++)
            for (int sy = 0; sy <= ySteps; sy++)
            for (int sz = 0; sz <= zSteps; sz++)
            {
                bool isStart = sx == 0 && sy == 0 && sz == 0;
                bool isEnd = sx == xSteps && sy == ySteps && sz == zSteps;
                if (isStart || isEnd)
                    continue;

                int ix = x + (sx == 1 ? dx : 0);
                int iy = y + (sy == 1 ? dy : 0);
                int iz = z + (sz == 1 ? dz : 0);
                if (!_expectedGrid.IsLocalInBounds(ix, iy, iz)
                    || _view.Blocked[_expectedGrid.ToFlatIndex(ix, iy, iz)])
                    return false;
            }
            return true;
        }

        private bool IsDirectedCell(int index)
            => !_view.Blocked[index]
                && _view.NextCells[index] >= 0
                && _view.NextCells[index] != index;

        private void ConfirmPath()
        {
            for (int index = 0; index < _pathCount; index++)
                _graphState[_path[index]] = 2;
            _pathCount = 0;
        }

        private void Fail(string reason)
        {
            _phase = Phase.Complete;
            _status = FlowFieldVolumeBakeValidationStatus.Invalid;
            _error = string.IsNullOrEmpty(reason) ? "Static Volume3D Bake is invalid." : reason;
        }

        private string DescribeCell(int index, string reason)
        {
            if (index < 0 || index >= _view.Blocked.Length)
                return $"Static Volume3D Bake cell index {index} is invalid: {reason}.";
            _expectedGrid.FromFlatIndex(index, out int x, out int y, out int z);
            return $"Static Volume3D Bake cell {index} ({x}, {y}, {z}) is invalid: {reason}.";
        }

        private static bool NormalizedOrZero(Vector3 direction)
            => direction == Vector3.zero
                || Mathf.Abs(direction.magnitude - 1f) <= 0.001f;
    }
}
