using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The grids and session requests built from the serialized settings.</summary>
    public partial class FlowFieldManager
    {
        private FlowFieldGridSpace CreateGridSpace()
        {
            if (!TryGetBakeLayout(out _, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Bake Bounds and Cell Size do not produce a valid grid.");
            return grid;
        }

        internal bool TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
            => FlowFieldBakeBoundsUtility.TryCreateVolumeWorldLayout(
                transform.position,
                _bakeBoundsLocal,
                _cellSize,
                out worldBounds,
                out grid);

        private FlowFieldVolumeRequest CreateVolumeRequest()
        {
            if (!TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Volume Bounds and Cell Size do not produce a valid grid.");
            return CreateVolumeRequest(
                grid,
                worldBounds,
                _bakeMode,
                _bakeMode == FlowFieldBakeMode.StaticBaked ? _volumeStaticBakeData : null);
        }

        // A bake passes the input generation it captured, so it can tell when
        // the settings changed under it. Runtime requests do not compare it.
        internal FlowFieldVolumeRequest CreateVolumeRequest(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData bakeData,
            int bakeInputGeneration = 0)
            => new FlowFieldVolumeRequest(
                grid,
                worldBounds,
                bakeMode,
                _obstacleLayer,
                _obstacleClearance,
                HasConfiguredGoal,
                ConfiguredGoalWorld,
                _goalInfluenceRadius,
                _defaultFlowDirection,
                bakeData,
                bakeInputGeneration);

        private void QueueVolumeRequest(bool force = false, bool requeryObstacles = false)
        {
            if (!_volumeSession.IsInitialized || _volumeSession.IsFaulted)
                return;
            FlowFieldVolumeRequest request = CreateVolumeRequest();
            if (!force
                && _volumeSession.Volume.TryGetLatestRequestedInput(out FlowFieldVolumeRequest latest)
                && latest.HasSameInputs(request))
                return;
            // The session owns the accepted input, including a newer input
            // submitted from its synchronous state-change callback.
            _volumeSession.Volume.Submit(request, requeryObstacles);
        }

        internal FlowFieldSurfaceBakeSettings CreateSurfaceBakeSettings()
        {
            if (!TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Bake Bounds and Cell Size do not produce a valid grid.");
            return new FlowFieldSurfaceBakeSettings(
                grid,
                worldBounds,
                _groundBakeLayer,
                _maxSurfaceSlope,
                _maxStepHeight);
        }

        internal bool TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
            => FlowFieldBakeBoundsUtility.TryCreateWorldLayout(
                transform.position,
                _bakeBoundsLocal,
                _cellSize,
                out worldBounds,
                out grid);

        internal FlowFieldSurfaceRequest CreateSurfaceRequest(
            FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
        {
            if (BakeMode == FlowFieldBakeMode.StaticBaked)
            {
                return FlowFieldSurfaceRequest.ForStaticSnapshot(
                    settings,
                    _staticBakeData,
                    CreateStaticBakeSnapshot(settings),
                    _obstacleLayer,
                    _obstacleCheckHeight,
                    _obstacleCheckCenterOffset,
                    _obstacleClearance,
                    _defaultFlowDirection,
                    dirtyFlags);
            }

            return FlowFieldSurfaceRequest.ForSceneBuild(
                settings,
                _obstacleLayer,
                _obstacleCheckHeight,
                _obstacleCheckCenterOffset,
                _obstacleClearance,
                _enableUnregisteredObstacleSweep,
                ResolveConfiguredGoal(settings.Grid),
                _defaultFlowDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion);
        }

        private bool TryCreateCurrentSurfaceRequest(out FlowFieldSurfaceRequest request)
        {
            request = default;
            try
            {
                FlowFieldSurfaceBakeSettings settings = CreateSurfaceBakeSettings();
                request = CreateSurfaceRequest(
                    settings,
                    FlowFieldSurfaceDirtyFlags.None,
                    FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
