using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Exports and read views of the published Volume3D field for bakes and tools.</summary>
    internal sealed partial class FlowFieldVolumeBuilder
    {
        /// <summary>
        /// What a Static Bake stores: the published request as metadata and
        /// copies of the published base field. False while nothing is published.
        /// </summary>
        internal bool TryExportBakeData(out FlowFieldVolumeBakePayload payload)
        {
            payload = default;
            if (_committed == null)
                return false;
            FlowFieldVolumeRequest request = _committed.Request;
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
                request.HasGoal ? _committed.GoalIndex : -1);
            // Static bake assets store the immutable base field. The current
            // Default Direction and registered Modifiers are runtime inputs
            // and are composed again when the snapshot is loaded.
            payload = new FlowFieldVolumeBakePayload(
                metadata,
                (bool[])_committed.Blocked.Clone(),
                (uint[])_committed.Topology.Clone(),
                (FlowFieldGoalFlags[])_committed.GoalFlags.Clone(),
                (int[])_committed.NextCells.Clone(),
                (Vector3[])_committed.BaseDirections.Clone(),
                (float[])_committed.BaseSpeeds.Clone(),
                (Vector3[])_committed.EscapeDirections.Clone());
            return true;
        }

        public bool TryGetFieldInfo(out FlowFieldFieldInfo info)
        {
            info = default;
            if (_committed == null)
                return false;

            FlowFieldVolumeRequest request = _committed.Request;
            // A running build or a move within the Goal cell leaves the
            // published result behind the requested Goal, as on Surface2D.
            // StaticBaked requests all carry the baked Goal.
            FlowFieldVolumeRequest requested = _requests.HasLatest ? _requests.Latest : request;
            Vector3 resolvedGoal = default;
            bool hasResolvedGoal = _committed.GoalIndex >= 0
                && _committed.GoalIndex < _committed.Grid.CellCount;
            if (hasResolvedGoal)
            {
                _committed.Grid.FromFlatIndex(
                    _committed.GoalIndex,
                    out int goalX,
                    out int goalY,
                    out int goalZ);
                resolvedGoal = _committed.Grid.LocalToWorldCenter(goalX, goalY, goalZ);
            }

            info = new FlowFieldFieldInfo(
                request.Grid,
                request.WorldBounds,
                FlowFieldSpaceMode.Volume3D,
                request.BakeMode,
                _session.Revision,
                requested.HasGoal,
                requested.GoalWorld,
                requested.GoalInfluenceRadius,
                hasResolvedGoal,
                resolvedGoal);
            return info.IsValid;
        }
    }
}
