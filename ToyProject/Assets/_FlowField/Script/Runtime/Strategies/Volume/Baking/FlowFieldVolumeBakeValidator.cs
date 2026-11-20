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
    /// Cooperative structural validator shared by editor display checks and
    /// the synchronous callers that need the same payload rules.  A call to
    /// Step performs bounded cell work or bounded graph work and can resume
    /// without walking an already confirmed path again. It reads the payload
    /// in place and keeps only its own small state arrays.
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
        private FlowFieldVolumeBakePayload _payload;
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
                    return _phase == Phase.Metadata ? 0f : 1f;
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
            if (asset == null)
            {
                Initialize(default, invalidReason: "Static Volume3D Bake arrays are missing.");
                return;
            }
            Initialize(asset.Payload);
        }

        internal FlowFieldVolumeBakeValidator(
            in FlowFieldVolumeBakePayload payload,
            FlowFieldGridSpace expectedGrid)
        {
            _expectedGrid = expectedGrid;
            Initialize(payload);
        }

        internal static bool ValidatePayload(
            in FlowFieldVolumeBakePayload payload,
            FlowFieldGridSpace expectedGrid,
            out string reason)
        {
            using (FlowFieldVolumeBakeValidator validator =
                new FlowFieldVolumeBakeValidator(payload, expectedGrid))
            {
                while (!validator.IsComplete)
                    validator.Step(4096, 64);
                reason = validator.Error;
                return validator.IsValid;
            }
        }

        private void Initialize(
            in FlowFieldVolumeBakePayload payload,
            string invalidReason = null)
        {
            _payload = payload;
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
            if (!_payload.HasArrays)
            {
                _phase = Phase.Complete;
                _status = FlowFieldVolumeBakeValidationStatus.Invalid;
                _error = "Static Volume3D Bake arrays are missing.";
                return;
            }

            if (_payload.Blocked.Length > FlowFieldLayoutUtility.MaxVolumeCellCount)
            {
                _phase = Phase.Complete;
                _status = FlowFieldVolumeBakeValidationStatus.Invalid;
                _error = "Static Volume3D Bake contains more than 1,000,000 cells.";
                return;
            }

            _graphState = new byte[_payload.Blocked.Length];
            _path = new int[_payload.Blocked.Length];
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

            if (_phase == Phase.Metadata && !StepMetadata())
                return;
            if (_phase == Phase.Cells && !StepCells(cellBudget))
                return;
            if (_phase == Phase.Graph)
                StepGraph(graphBudget);
        }

        private bool StepMetadata()
        {
            if (!ValidateMetadata(out string metadataError))
            {
                Fail(metadataError);
                return false;
            }
            _phase = Phase.Cells;
            return true;
        }

        private bool StepCells(int cellBudget)
        {
            int processed = 0;
            while (_cellCursor < _payload.Blocked.Length && processed < cellBudget)
            {
                if (!ValidateCell(_cellCursor, out string cellError))
                {
                    Fail(cellError);
                    return false;
                }
                _cellCursor++;
                processed++;
            }

            if (_cellCursor < _payload.Blocked.Length)
                return false;
            if (_payload.Metadata.HasGoal && _anchorCount != 1)
            {
                Fail($"Static Volume3D Bake must contain exactly one Goal anchor; found {_anchorCount}.");
                return false;
            }
            if (!_payload.Metadata.HasGoal && _anchorCount != 0)
            {
                Fail("Static Volume3D Bake without a Goal contains an anchor.");
                return false;
            }
            _phase = Phase.Graph;
            return true;
        }

        private void StepGraph(int graphBudget)
        {
            int graphSteps = 0;
            while (_graphCursor < _payload.NextCells.Length && graphSteps < graphBudget)
            {
                if (_currentNode < 0)
                {
                    while (_graphCursor < _payload.NextCells.Length
                        && !IsDirectedCell(_graphCursor))
                        _graphCursor++;
                    if (_graphCursor >= _payload.NextCells.Length)
                        break;
                    if (_graphState[_graphCursor] == 2)
                    {
                        _graphCursor++;
                        continue;
                    }
                    _currentNode = _graphCursor;
                    _pathCount = 0;
                }

                if (_currentNode == _payload.Metadata.ResolvedGoalIndex)
                {
                    ConfirmPath();
                    _graphCursor++;
                    _currentNode = -1;
                    graphSteps++;
                    continue;
                }
                if (_currentNode < 0 || _currentNode >= _payload.NextCells.Length
                    || _payload.Blocked[_currentNode]
                    || _payload.NextCells[_currentNode] < 0)
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
                _currentNode = _payload.NextCells[_currentNode];
                graphSteps++;
            }

            if (_graphCursor >= _payload.NextCells.Length && _currentNode < 0)
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
            FlowFieldVolumeBakeMetadata metadata = _payload.Metadata;
            if (!metadata.TryValidate(out reason))
                return false;
            if (!metadata.MatchesGrid(_expectedGrid))
            {
                reason = "Static Volume3D Bake metadata does not match the grid.";
                return false;
            }
            int count = _expectedGrid.CellCount;
            if (!_payload.HasCellCount(count))
            {
                reason = "Static Volume3D Bake arrays do not match the grid.";
                return false;
            }
            if (metadata.HasGoal && _payload.Blocked[metadata.ResolvedGoalIndex])
            {
                reason = DescribeCell(metadata.ResolvedGoalIndex, "Goal index points to a blocked cell");
                return false;
            }
            return true;
        }

        private bool ValidateCell(int index, out string reason)
        {
            reason = string.Empty;
            bool blocked = _payload.Blocked[index];
            uint validTopologyBits = (1u << FlowFieldNeighborUtility.VolumeCount) - 1u;
            Vector3 direction = _payload.Directions[index];
            Vector3 escape = _payload.EscapeDirections[index];
            float speed = _payload.Speeds[index];
            int next = _payload.NextCells[index];
            FlowFieldGoalFlags flags = _payload.GoalFlags[index];

            if (!FlowFieldGridSpace.IsFinite(direction)
                || !FlowFieldGridSpace.IsFinite(escape)
                || !FlowFieldGridSpace.IsFinite(speed)
                || speed < 0f
                || next < MinSentinel
                || next >= _payload.Blocked.Length
                || (flags & ~(FlowFieldGoalFlags.Directed
                    | FlowFieldGoalFlags.Anchor
                    | FlowFieldGoalFlags.Unreachable)) != 0
                || !FlowFieldVectorUtility.IsUnitOrZero(escape)
                || (_payload.TopologyMasks[index] & ~validTopologyBits) != 0u)
            {
                reason = DescribeCell(index, "invalid numeric, sentinel, flags, escape direction, or topology");
                return false;
            }

            if (blocked)
            {
                if (_payload.TopologyMasks[index] != 0u
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
                    || direction.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr
                    || Mathf.Abs(direction.magnitude - 1f) > 0.001f
                    || Mathf.Abs(speed - 1f) > 0.001f)
                    reason = DescribeCell(index, "default cell is inconsistent");
                return string.IsNullOrEmpty(reason);
            }
            if (next == index)
            {
                _anchorCount++;
                if (!_payload.Metadata.HasGoal
                    || index != _payload.Metadata.ResolvedGoalIndex
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
                || (_payload.TopologyMasks[index] & (1u << directionIndex)) == 0u
                || _payload.Blocked[next]
                || !IsCornerClear(index, next))
            {
                reason = DescribeCell(index, "NextCell is not a traversable neighbour or cuts through a blocked diagonal corner");
                return false;
            }
            Vector3 expectedDirection = new Vector3(nx - x, ny - y, nz - z).normalized;
            if ((direction - expectedDirection).sqrMagnitude > DirectionToleranceSqr
                || flags != FlowFieldGoalFlags.Directed
                || direction.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr
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
                    || _payload.Blocked[_expectedGrid.ToFlatIndex(ix, iy, iz)])
                    return false;
            }
            return true;
        }

        private bool IsDirectedCell(int index)
            => !_payload.Blocked[index]
                && _payload.NextCells[index] >= 0
                && _payload.NextCells[index] != index;

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
            if (index < 0 || index >= _payload.Blocked.Length)
                return $"Static Volume3D Bake cell index {index} is invalid: {reason}.";
            _expectedGrid.FromFlatIndex(index, out int x, out int y, out int z);
            return $"Static Volume3D Bake cell {index} ({x}, {y}, {z}) is invalid: {reason}.";
        }
    }
}
