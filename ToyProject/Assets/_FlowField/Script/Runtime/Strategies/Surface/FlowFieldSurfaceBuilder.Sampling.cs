using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Reads of the published Surface2D field.</summary>
    internal sealed partial class FlowFieldSurfaceBuilder
    {
        public bool TryGetFieldInfo(out FlowFieldFieldInfo info)
        {
            info = default;
            if (!_fieldStore.Grid.IsValid || _fieldStore.Surface == null || !_fieldStore.Surface.IsValid)
                return false;

            bool hasRequestedGoal = false;
            Vector3 requestedGoal = default;
            float influenceRadius = 0f;
            if (_requests.HasLatest)
            {
                if (_requests.Latest.SourceKind == FlowFieldSurfaceSourceKind.StaticSnapshot)
                {
                    FlowFieldSurfaceBakeSnapshot snapshot = _requests.Latest.BakeSnapshot;
                    hasRequestedGoal = snapshot != null && snapshot.HasGoal;
                    requestedGoal = snapshot != null ? snapshot.RequestedGoalWorld : default;
                    influenceRadius = snapshot != null ? snapshot.GoalInfluenceRadius : 0f;
                }
                else
                {
                    hasRequestedGoal = _requests.Latest.Goal.IsValid;
                    requestedGoal = _requests.Latest.Goal.RequestedWorld;
                    influenceRadius = _requests.Latest.Goal.InfluenceRadius;
                }
            }

            Vector3 resolvedGoal = default;
            bool hasResolvedGoal = _fieldStore.HasActiveGoal
                && _fieldStore.ResolvedGoalIndex >= 0;
            if (hasResolvedGoal)
            {
                resolvedGoal = _fieldStore.Surface.GetCellCenter(_fieldStore.Grid, _fieldStore.ResolvedGoalIndex);
            }

            info = new FlowFieldFieldInfo(
                _fieldStore.Grid,
                _fieldStore.Surface.BakeBoundsWorld,
                FlowFieldSpaceMode.Surface2D,
                _session.BakeMode,
                _session.Revision,
                hasRequestedGoal,
                requestedGoal,
                influenceRadius,
                hasResolvedGoal,
                resolvedGoal);
            return info.IsValid;
        }

        public IFlowFieldLatestCells LatestCells => this;

        // The base stage of a build in progress already knows obstacles the
        // published field was not built with. The solve job only reads these
        // arrays, and the base stage writes them on this thread.
        bool IFlowFieldLatestCells.Describes(FlowFieldField field)
            => _state.Grid.IsValid
                && _state.Workspace != null
                && _state.Workspace.Capacity == _state.Grid.CellCount
                && field.Grid.MatchesBounds(_state.Grid)
                && ReferenceEquals(field.Surface, _state.Surface);

        bool IFlowFieldLatestCells.IsBlocked(int index) => _state.Workspace.Blocked[index];

        bool IFlowFieldLatestCells.IsWalkable(int index)
            => _state.Surface != null && _state.Surface.IsSurfaceValid(index);

        bool IFlowFieldLatestCells.HasLink(int index, int direction)
            => _state.Workspace.TopologyMasks != null
                && index < _state.Workspace.TopologyMasks.Length
                && (_state.Workspace.TopologyMasks[index] & (1 << direction)) != 0;

        Vector3 IFlowFieldLatestCells.EscapeDirection(int index)
            => _state.Workspace.EscapeDirections != null ? _state.Workspace.EscapeDirections[index] : Vector3.zero;
    }
}
