using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Reads of the published Surface2D field.</summary>
    internal sealed partial class FlowFieldSession
    {
        internal override bool TryGetFieldInfo(out FlowFieldFieldInfo info)
        {
            info = default;
            if (!IsReady || !_fieldStore.Grid.IsValid || _fieldStore.Surface == null || !_fieldStore.Surface.IsValid)
                return false;

            bool hasRequestedGoal = false;
            Vector3 requestedGoal = default;
            float influenceRadius = 0f;
            if (_requests.HasLatest)
            {
                if (_requests.Latest.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot)
                {
                    FlowFieldStaticBakeSnapshot snapshot = _requests.Latest.StaticBakeSnapshot;
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
                BakeMode,
                Revision,
                hasRequestedGoal,
                requestedGoal,
                influenceRadius,
                hasResolvedGoal,
                resolvedGoal);
            return info.IsValid;
        }

        internal override FlowFieldSample Sample(Vector3 worldPosition)
        {
            if (!IsReady)
                throw new InvalidOperationException("FlowField session is not ready.");
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));

            FlowFieldGridSpace sampleGrid = _fieldStore.Grid;
            FlowFieldSurfaceData sampleSurface = _fieldStore.Surface;
            if (!sampleGrid.ContainsWorldPosition(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition), "Position is outside the FlowField grid.");
            if (!_fieldStore.TrySample(worldPosition, out FlowFieldSample sample))
                throw new InvalidOperationException("FlowField sampling data is inconsistent.");

            if (sampleGrid.TryWorldToLocal(worldPosition, out int sampleX, out int sampleZ))
            {
                int sampleIndex = sampleGrid.ToFlatIndex(sampleX, sampleZ);
                int latestX = 0;
                int latestZ = 0;
                bool latestLayoutMatchesSample = _state.Grid.IsValid
                    && _state.Workspace != null
                    && _state.Workspace.Capacity == _state.Grid.CellCount
                    && sampleGrid.MatchesBounds(_state.Grid)
                    && ReferenceEquals(sampleSurface, _state.Surface)
                    && _state.Grid.TryWorldToLocal(worldPosition, out latestX, out latestZ);
                if (latestLayoutMatchesSample)
                {
                    int latestIndex = _state.Grid.ToFlatIndex(latestX, latestZ);
                    FlowFieldWorkspace latestWorkspace = _state.Workspace;
                    FlowFieldSurfaceData latestSurface = _state.Surface;
                    bool obstacleProbeActive = IsRebuilding || _obstaclePipeline.HasStagedDynamicProbe;
                    bool latestBlocked = latestWorkspace.Blocked[latestIndex];
                    bool stagedObstacleBlocksSample = _obstaclePipeline.HasStagedDynamicProbe
                        && latestWorkspace.ObstacleScratch[latestIndex];
                    if (obstacleProbeActive && latestBlocked)
                    {
                        if (FlowFieldCellSampler.TrySampleEscape(
                                _state.Grid,
                                latestSurface,
                                latestWorkspace,
                                worldPosition,
                                out FlowFieldSample escapeSample))
                            return escapeSample;
                        return new FlowFieldSample(
                            Vector3.zero,
                            0f,
                            sample.SurfaceNormal,
                            sample.HasSurface,
                            sample.HasCell);
                    }
                    if (obstacleProbeActive && stagedObstacleBlocksSample)
                        return new FlowFieldSample(
                            Vector3.zero,
                            0f,
                            sample.SurfaceNormal,
                            sample.HasSurface,
                            sample.HasCell);

                    int next = _fieldStore.NextCells[sampleIndex];
                    if (next >= 0)
                    {
                        bool nextBlocked = next >= sampleGrid.CellCount
                            || latestWorkspace.Blocked[next]
                            || _obstaclePipeline.HasStagedDynamicProbe
                                && latestWorkspace.ObstacleScratch[next]
                            || latestSurface == null
                            || !latestSurface.IsSurfaceValid(next);
                        bool topologyChanged = false;
                        if (!IsRebuilding && !nextBlocked && next != sampleIndex)
                        {
                            sampleGrid.FromFlatIndex(sampleIndex, out int currentX, out int currentZ);
                            sampleGrid.FromFlatIndex(next, out int nextX, out int nextZ);
                            int directionIndex = FlowFieldNeighborUtility.FindDirectionIndex(nextX - currentX, nextZ - currentZ);
                            topologyChanged = directionIndex < 0
                                || latestWorkspace.TopologyMasks == null
                                || sampleIndex >= latestWorkspace.TopologyMasks.Length
                                || (latestWorkspace.TopologyMasks[sampleIndex] & (1 << directionIndex)) == 0;
                        }
                        if (nextBlocked || topologyChanged)
                            return new FlowFieldSample(
                                Vector3.zero,
                                0f,
                                sample.SurfaceNormal,
                                sample.HasSurface,
                                sample.HasCell);
                    }
                }
            }
            return sample;
        }

        internal override bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsReady || !FlowFieldGridSpace.IsFinite(worldPosition))
                return false;
            FlowFieldGridSpace grid = _fieldStore.Grid.IsValid ? _fieldStore.Grid : _state.Grid;
            if (!grid.IsValid || !grid.ContainsWorldPosition(worldPosition))
                return false;
            try
            {
                sample = Sample(worldPosition);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        internal override FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("FlowField session is not initialized.");
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));
            FlowFieldGridSpace grid = IsRebuilding && _fieldStore.Grid.IsValid ? _fieldStore.Grid : _state.Grid;
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
