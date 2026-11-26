using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Reads of the published Volume3D field: the field, its grid and info, and the bake export.</summary>
    internal sealed partial class FlowFieldVolumeBuilder
    {
        /// <summary>
        /// What a bake stores: the published request as metadata and
        /// copies of the published base field. False while nothing is published.
        /// </summary>
        internal bool TryExportBakeData(out FlowFieldVolumeBakePayload payload)
        {
            payload = default;
            if (_fields.Committed == null)
                return false;
            FlowFieldVolumeRequest request = _fields.Committed.Request;
            FlowFieldGridSpace grid = request.Grid;
            FlowFieldVolumeBakeMetadata metadata = new FlowFieldVolumeBakeMetadata(
                FlowFieldVolumeBakeData.SupportedFormatVersion,
                grid.Width,
                grid.Height,
                grid.Depth,
                grid.CellSize,
                grid.Origin,
                request.WorldBounds.center,
                request.WorldBounds.size,
                request.ObstacleLayer.value,
                request.ObstacleClearance,
                request.HasGoal,
                request.GoalWorld,
                request.GoalInfluenceRadius,
                request.HasGoal ? _fields.Committed.Goal.CellIndex : -1);
            // Static bake assets store the immutable base field. The current
            // Default Direction and registered Modifiers are runtime inputs
            // and are composed again when the snapshot is loaded.
            payload = new FlowFieldVolumeBakePayload(
                metadata,
                (bool[])_fields.Committed.Agent.Blocked.Clone(),
                (uint[])_fields.Committed.Goal.Topology.Clone(),
                (FlowFieldGoalFlags[])_fields.Committed.Goal.Flags.Clone(),
                (int[])_fields.Committed.Goal.NextCells.Clone(),
                (Vector3[])_fields.Committed.Goal.BaseDirections.Clone(),
                (float[])_fields.Committed.Goal.BaseSpeeds.Clone(),
                (Vector3[])_fields.Committed.Agent.EscapeDirections.Clone());
            return true;
        }

        public bool TryGetFieldInfo(int revision, out FlowFieldFieldInfo info)
        {
            info = default;
            if (_fields.Committed == null)
                return false;

            FlowFieldVolumeRequest request = _fields.Committed.Request;
            // A running build or a move within the Goal cell leaves the
            // published result behind the requested Goal, as on Surface2D.
            // StaticBaked requests all carry the baked Goal.
            FlowFieldVolumeRequest requested = _inputs.Requests.HasLatest ? _inputs.Requests.Latest : request;
            Vector3 resolvedGoal = default;
            bool hasResolvedGoal = _fields.Committed.Goal.CellIndex >= 0
                && _fields.Committed.Goal.CellIndex < _fields.Committed.Grid.CellCount;
            if (hasResolvedGoal)
            {
                _fields.Committed.Grid.FromFlatIndex(
                    _fields.Committed.Goal.CellIndex,
                    out int goalX,
                    out int goalY,
                    out int goalZ);
                resolvedGoal = _fields.Committed.Grid.LocalToWorldCenter(goalX, goalY, goalZ);
            }

            info = new FlowFieldFieldInfo(
                request.Grid,
                request.WorldBounds,
                FlowFieldSpaceMode.Volume3D,
                request.BakeMode,
                revision,
                requested.HasGoal,
                requested.GoalWorld,
                requested.GoalInfluenceRadius,
                hasResolvedGoal,
                resolvedGoal);
            return info.IsValid;
        }


        // The published field. A request on another grid or source drops it
        // (Submit), so it is always on the latest request's grid.
        public FlowFieldField PublishedField => _fields.Committed;

        // The latest request's grid, which the next build uses, as in Surface2D.
        public FlowFieldGridSpace LatestGrid => _inputs.Requests.HasLatest ? _inputs.Requests.Latest.Grid : default;

        // Volume3D knows nothing newer than its published field; the shared
        // rule reads that field and the notified uncertain regions.
        public IFlowFieldLatestCells LatestCells => null;
    }
}
