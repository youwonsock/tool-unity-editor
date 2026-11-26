using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Reads of the published Surface2D field: the field, its grid and info, and the bake export.</summary>
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
                _bakeMode,
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

        Vector3 IFlowFieldLatestCells.EscapeDirection(int index)
            => _state.Workspace.EscapeDirections != null ? _state.Workspace.EscapeDirections[index] : Vector3.zero;


        // The published field. A request on another grid or source drops it
        // (Submit), so it always comes from the latest request's source.
        public FlowFieldField PublishedField
            => _state.SurfaceReady && _fieldStore.IsValid ? _fieldStore.Field : null;

        // The latest request's grid, which the next build uses, as in Volume3D.
        public FlowFieldGridSpace LatestGrid
            => _requests.HasLatest ? _requests.Latest.SurfaceSettings.Grid : _state.Grid;

        /// <summary>
        /// What a bake stores: the published Surface and a copy of its
        /// arrays, taken at the bake's boundary (runtime sampling reads the
        /// compact store directly). False while nothing is published.
        /// </summary>
        internal bool TryExportBakeData(out FlowFieldSurfaceData surface, out FlowFieldSurfaceWorkspace workspace)
        {
            surface = _fieldStore.Surface;
            workspace = surface != null ? _fieldStore.CreateWorkspaceSnapshot() : null;
            return workspace != null;
        }
    }
}
