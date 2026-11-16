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
            if (!IsReady || !_fieldStore.Grid.IsValid || _fieldStore.Surface == null || !_fieldStore.Surface.IsValid)
                return false;

            bool hasRequestedGoal = false;
            Vector3 requestedGoal = default;
            float influenceRadius = 0f;
            if (_requests.HasLatest)
            {
                if (_requests.Latest.SourceKind == FlowFieldSurfaceSourceKind.StaticSnapshot)
                {
                    FlowFieldSurfaceBakeSnapshot snapshot = _requests.Latest.StaticBakeSnapshot;
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

        public bool TryGetPublishedField(out FlowFieldField field)
        {
            field = null;
            if (!_session.IsInitialized || !_state.Grid.IsValid || !IsReady)
                return false;
            field = _fieldStore.Field;
            return field != null;
        }

        /// <summary>
        /// The whole read without exceptions, so TrySample, which movers call
        /// every physics step, never pays for one on an ordinary miss.
        /// </summary>
        public FlowFieldSampleStatus TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsReady)
                return FlowFieldSampleStatus.NotReady;
            return FlowFieldSampler.Sample(_fieldStore.Field, worldPosition, this, out sample);
        }

        /// <summary>
        /// The staging workspace already knows about obstacles the published
        /// field was not built with. A mover inside one escapes it, and one
        /// whose next step a change has blocked stops.
        /// </summary>
        bool IFlowFieldSampleSafety.TryOverride(
            FlowFieldField field,
            int sampleIndex,
            Vector3 worldPosition,
            in FlowFieldSample published,
            out FlowFieldSample sample)
        {
            sample = published;
            FlowFieldGridSpace sampleGrid = field.Grid;
            int latestX = 0;
            int latestZ = 0;
            bool latestLayoutMatchesSample = _state.Grid.IsValid
                && _state.Workspace != null
                && _state.Workspace.Capacity == _state.Grid.CellCount
                && sampleGrid.MatchesBounds(_state.Grid)
                && ReferenceEquals(field.Surface, _state.Surface)
                && _state.Grid.TryWorldToLocal(worldPosition, out latestX, out latestZ);
            if (!latestLayoutMatchesSample)
                return false;

            int latestIndex = _state.Grid.ToFlatIndex(latestX, latestZ);
            FlowFieldSurfaceWorkspace latestWorkspace = _state.Workspace;
            FlowFieldSurfaceData latestSurface = _state.Surface;
            bool obstacleProbeActive = _session.IsRebuilding || _obstaclePipeline.HasStagedDynamicProbe;
            bool latestBlocked = latestWorkspace.Blocked[latestIndex];
            bool stagedObstacleBlocksSample = _obstaclePipeline.HasStagedDynamicProbe
                && latestWorkspace.ObstacleScratch[latestIndex];
            if (obstacleProbeActive && latestBlocked)
            {
                sample = FlowFieldSurfaceCellSampler.TrySampleEscape(
                        _state.Grid,
                        latestSurface,
                        latestWorkspace,
                        worldPosition,
                        out FlowFieldSample escapeSample)
                    ? escapeSample
                    : Stop(published);
                return true;
            }
            if (obstacleProbeActive && stagedObstacleBlocksSample)
            {
                sample = Stop(published);
                return true;
            }

            int next = field.NextCells[sampleIndex];
            if (next < 0)
                return false;
            bool nextBlocked = next >= sampleGrid.CellCount
                || latestWorkspace.Blocked[next]
                || _obstaclePipeline.HasStagedDynamicProbe
                    && latestWorkspace.ObstacleScratch[next]
                || latestSurface == null
                || !latestSurface.IsSurfaceValid(next);
            bool topologyChanged = false;
            if (!_session.IsRebuilding && !nextBlocked && next != sampleIndex)
            {
                sampleGrid.FromFlatIndex(sampleIndex, out int currentX, out int currentZ);
                sampleGrid.FromFlatIndex(next, out int nextX, out int nextZ);
                int directionIndex = FlowFieldNeighborUtility.FindDirectionIndex(nextX - currentX, nextZ - currentZ);
                topologyChanged = directionIndex < 0
                    || latestWorkspace.TopologyMasks == null
                    || sampleIndex >= latestWorkspace.TopologyMasks.Length
                    || (latestWorkspace.TopologyMasks[sampleIndex] & (1 << directionIndex)) == 0;
            }
            if (!nextBlocked && !topologyChanged)
                return false;
            sample = Stop(published);
            return true;
        }

        private static FlowFieldSample Stop(in FlowFieldSample sample)
            => new FlowFieldSample(Vector3.zero, 0f, sample.SurfaceNormal, sample.HasSurface, sample.HasCell);

        public FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            if (!_session.IsInitialized)
                throw new InvalidOperationException("FlowField session is not initialized.");
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));
            FlowFieldGridSpace grid = _session.IsRebuilding && _fieldStore.Grid.IsValid ? _fieldStore.Grid : _state.Grid;
            if (!grid.IsValid)
                throw new InvalidOperationException("FlowField grid is not initialized.");
            Vector3 clamped = grid.ClampWorldXZ(worldPosition);
            return new FlowFieldClampResult(
                clamped,
                !Mathf.Approximately(worldPosition.x, clamped.x),
                false,
                !Mathf.Approximately(worldPosition.z, clamped.z));
        }
    }
}
