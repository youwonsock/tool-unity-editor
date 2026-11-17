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
            if (!_session.IsReady || !_fieldStore.Grid.IsValid || _fieldStore.Surface == null || !_fieldStore.Surface.IsValid)
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

            // A notified obstacle change stops movers in its region until a
            // build that queried it is published, as in Volume3D.
            if (_safety.IsCellUncertain(sampleGrid, CellCenter(sampleGrid, sampleIndex)))
            {
                sample = Stop(published);
                return true;
            }

            int latestIndex = _state.Grid.ToFlatIndex(latestX, latestZ);
            FlowFieldSurfaceWorkspace latestWorkspace = _state.Workspace;
            FlowFieldSurfaceData latestSurface = _state.Surface;
            bool latestBlocked = latestWorkspace.Blocked[latestIndex];
            if (_session.IsRebuilding && latestBlocked)
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
            int next = field.NextCells[sampleIndex];
            if (next < 0)
                return false;
            bool nextBlocked = next >= sampleGrid.CellCount
                || latestWorkspace.Blocked[next]
                || _safety.IsCellUncertain(sampleGrid, CellCenter(sampleGrid, next))
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

        private static Vector3 CellCenter(FlowFieldGridSpace grid, int index)
        {
            grid.FromFlatIndex(index, out int x, out int z);
            return grid.LocalToWorldCenter(x, z);
        }

        private static FlowFieldSample Stop(in FlowFieldSample sample)
            => new FlowFieldSample(Vector3.zero, 0f, sample.SurfaceNormal, sample.HasSurface, sample.HasCell);
    }
}
