using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The serialized settings as a <see cref="FlowFieldManagerConfig"/>, and
    /// what the session and the Editor build from it. The mode builders own
    /// the checks and the requests; these members only pass the snapshot on.
    /// </summary>
    public partial class FlowFieldManager
    {
        internal FlowFieldStaticBakeData StaticBakeData => _bakeAssets.Surface;
        internal FlowFieldVolumeBakeData VolumeStaticBakeData => _bakeAssets.Volume;
        internal Bounds BakeBoundsLocal => _grid.BakeBoundsLocal;
        internal float CellSize => _grid.CellSize;
        internal LayerMask ObstacleLayer => _obstacles.Layer;
        internal float ObstacleCheckHeight => _surface.ObstacleCheckHeight;
        internal float ObstacleCheckCenterOffset => _surface.ObstacleCheckCenterOffset;
        internal float ObstacleClearance => _obstacles.Clearance;
        internal Vector3 DefaultFlowDirection => _flow.DefaultDirection;
        // The serialized mode, which may differ from SpaceMode while an
        // accepted session runs in the previous one.
        internal FlowFieldSpaceMode ConfiguredSpaceMode => _spaceMode;

        internal Vector3 ConfiguredGoalWorld
            => _goalTransform != null ? _goalTransform.position : _explicitGoalWorld;

        internal bool HasConfiguredGoal
            => _goalTransform != null || _hasExplicitGoal;

        internal float ConfiguredGoalInfluenceRadius => _flow.GoalInfluenceRadius;

        /// <summary>The serialized settings now, with the serialized Bake Mode.</summary>
        internal FlowFieldManagerConfig CaptureConfig() => CaptureConfig(_bakeMode);

        // Surface2D builds with the Bake Mode its session accepted.
        private FlowFieldManagerConfig CaptureConfig(FlowFieldBakeMode bakeMode)
            => new FlowFieldManagerConfig(
                _spaceMode,
                bakeMode,
                in _grid,
                in _surface,
                in _obstacles,
                in _flow,
                in _bakeAssets,
                _enableUnregisteredObstacleSweep,
                _refreshRate,
                transform,
                _goalTransform,
                _hasExplicitGoal,
                _explicitGoalWorld);

        private void ValidateConfiguration(bool validateStaticGoal = true)
        {
            if (!Enum.IsDefined(typeof(FlowFieldSpaceMode), _spaceMode))
                throw new ArgumentOutOfRangeException(nameof(_spaceMode));
            if (IsInitialized && SpaceMode != _spaceMode)
                throw new InvalidOperationException("FlowField Space Mode cannot change during an active Init session. Release and Init again.");
            FlowFieldManagerConfig config = CaptureConfig();
            bool bakeModeLocked = IsInitialized && BakeMode != _bakeMode;
            if (_spaceMode == FlowFieldSpaceMode.Volume3D)
                FlowFieldVolumeBuilder.Validate(in config, bakeModeLocked, validateStaticGoal);
            else
                FlowFieldSurfaceBuilder.Validate(in config, bakeModeLocked, validateStaticGoal);
        }

        // ----- Surface2D -----------------------------------------------------

        internal bool TryGetBakeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
        {
            FlowFieldManagerConfig config = CaptureConfig();
            return FlowFieldSurfaceBuilder.TryCreateLayout(in config, out worldBounds, out grid);
        }

        private FlowFieldGridSpace CreateGridSpace()
        {
            if (!TryGetBakeLayout(out _, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Bake Bounds and Cell Size do not produce a valid grid.");
            return grid;
        }

        internal FlowFieldSurfaceBakeSettings CreateSurfaceBakeSettings()
        {
            FlowFieldManagerConfig config = CaptureConfig();
            return FlowFieldSurfaceBuilder.CreateBakeSettings(in config);
        }

        internal FlowFieldSurfaceGoalResolution ResolveConfiguredGoal(FlowFieldGridSpace grid)
        {
            FlowFieldManagerConfig config = CaptureConfig();
            return FlowFieldSurfaceBuilder.ResolveGoal(in config, grid);
        }

        internal FlowFieldSurfaceRequest CreateSurfaceRequest(
            FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
        {
            FlowFieldManagerConfig config = CaptureConfig();
            return FlowFieldSurfaceBuilder.CreateRequest(
                in config,
                BakeMode,
                in settings,
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

        internal bool TryValidateSurfaceBake(out string reason)
            => TryValidateSurfaceBake(out reason, includeStaticGoal: true);

        internal bool TryValidateSurfaceBake(out string reason, bool includeStaticGoal)
        {
            if (_spaceMode == FlowFieldSpaceMode.Volume3D)
                return TryValidateVolumeBake(out reason);
            FlowFieldManagerConfig config = CaptureConfig(BakeMode);
            return FlowFieldSurfaceBuilder.TryValidateBake(in config, includeStaticGoal, out reason);
        }

        // ----- Volume3D ------------------------------------------------------

        internal bool TryGetVolumeLayout(out Bounds worldBounds, out FlowFieldGridSpace grid)
        {
            FlowFieldManagerConfig config = CaptureConfig();
            return FlowFieldVolumeBuilder.TryCreateLayout(in config, out worldBounds, out grid);
        }

        internal FlowFieldVolumeRequest CreateVolumeRequest(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData bakeData,
            int bakeInputGeneration = 0)
        {
            FlowFieldManagerConfig config = CaptureConfig();
            return FlowFieldVolumeBuilder.CreateRequest(
                in config,
                grid,
                worldBounds,
                bakeMode,
                bakeData,
                bakeInputGeneration);
        }

        private void QueueVolumeRequest(bool force = false, bool requeryObstacles = false)
        {
            FlowFieldSession session = SessionFor(FlowFieldSpaceMode.Volume3D);
            if (session == null || !session.IsInitialized || session.IsFaulted)
                return;
            FlowFieldManagerConfig config = CaptureConfig();
            FlowFieldVolumeRequest request = FlowFieldVolumeBuilder.CreateRequest(in config);
            if (!force
                && session.Volume.TryGetLatestRequestedInput(out FlowFieldVolumeRequest latest)
                && latest.HasSameInputs(request))
                return;
            // The session owns the accepted input, including a newer input
            // submitted from its synchronous state-change callback.
            session.Volume.Submit(request, requeryObstacles);
        }

        internal bool TryValidateVolumeBake(out string reason)
        {
            FlowFieldManagerConfig config = CaptureConfig();
            return FlowFieldVolumeBuilder.TryValidateBake(in config, out reason);
        }

        // ----- Bake inputs, changed by the Editor ------------------------------

        internal void SetBakeBoundsLocal(Bounds localBounds)
        {
            Bounds snapped = ModeFor(_spaceMode).SnapBakeBounds(localBounds);
            if (FlowFieldBakeBoundsUtility.Approximately(_grid.BakeBoundsLocal, snapped))
                return;
            _grid.BakeBoundsLocal = snapped;
            OnSettingsChanged();
        }

        internal void AssignStaticBakeData(FlowFieldStaticBakeData bakeData)
        {
            if (_bakeAssets.Surface == bakeData)
                return;
            _bakeAssets.Surface = bakeData;
            OnSettingsChanged();
        }

        internal void AssignVolumeBakeData(FlowFieldVolumeBakeData bakeData)
        {
            if (_bakeAssets.Volume == bakeData)
                return;
            _bakeAssets.Volume = bakeData;
            OnSettingsChanged();
        }

        /// <summary>A signature of every setting a bake depends on.</summary>
        internal int ComputeBakeInputHash()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + (int)_spaceMode;
                hash = hash * 31 + (int)_bakeMode;
                hash = hash * 31 + _grid.BakeBoundsLocal.center.GetHashCode();
                hash = hash * 31 + _grid.BakeBoundsLocal.size.GetHashCode();
                hash = hash * 31 + _grid.CellSize.GetHashCode();
                hash = hash * 31 + _surface.GroundLayer.value;
                hash = hash * 31 + _surface.MaxSlope.GetHashCode();
                hash = hash * 31 + _surface.MaxStepHeight.GetHashCode();
                hash = hash * 31 + _obstacles.Layer.value;
                hash = hash * 31 + _surface.ObstacleCheckHeight.GetHashCode();
                hash = hash * 31 + _surface.ObstacleCheckCenterOffset.GetHashCode();
                hash = hash * 31 + _obstacles.Clearance.GetHashCode();
                hash = hash * 31 + _flow.DefaultDirection.GetHashCode();
                hash = hash * 31 + (_goalTransform != null
                    ? _goalTransform.GetInstanceID()
                    : 0);
                if (_goalTransform != null)
                    hash = hash * 31 + _goalTransform.position.GetHashCode();
                hash = hash * 31 + (_hasExplicitGoal ? 1 : 0);
                hash = hash * 31 + _explicitGoalWorld.GetHashCode();
                hash = hash * 31 + _flow.GoalInfluenceRadius.GetHashCode();
                hash = hash * 31 + transform.position.GetHashCode();
                hash = hash * 31 + transform.rotation.GetHashCode();
                hash = hash * 31 + transform.lossyScale.GetHashCode();
                return hash;
            }
        }
    }
}
