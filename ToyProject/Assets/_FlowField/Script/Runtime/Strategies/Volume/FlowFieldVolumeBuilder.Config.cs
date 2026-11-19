using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>How Volume3D reads a Manager configuration: its grid, checks and requests.</summary>
    internal sealed partial class FlowFieldVolumeBuilder
    {
        internal static bool TryCreateLayout(
            in FlowFieldManagerConfig config,
            out Bounds worldBounds,
            out FlowFieldGridSpace grid)
            => FlowFieldLayoutUtility.TryCreateVolumeWorldLayout(
                config.Position,
                config.Grid.BakeBoundsLocal,
                config.Grid.CellSize,
                out worldBounds,
                out grid);

        public Bounds? SubmitConfig(in FlowFieldManagerConfig config, FlowFieldConfigChange change)
        {
            switch (change)
            {
                case FlowFieldConfigChange.Rebuild:
                    return SubmitConfigRequest(in config, force: true, requeryObstacles: true);
                // A Goal change keeps the obstacle result unless an obstacle changed.
                case FlowFieldConfigChange.Goal:
                    return SubmitConfigRequest(in config, force: true, requeryObstacles: false);
                case FlowFieldConfigChange.Cells:
                    return SubmitConfigRequest(in config, force: true, requeryObstacles: true);
                // A changed request rebuilds; a new grid hides the old result at once.
                default:
                    return SubmitConfigRequest(in config, force: false, requeryObstacles: false);
            }
        }

        private Bounds? SubmitConfigRequest(in FlowFieldManagerConfig config, bool force, bool requeryObstacles)
        {
            FlowFieldVolumeRequest request = CreateRequest(in config);
            if (!force
                && TryGetLatestRequestedInput(out FlowFieldVolumeRequest latest)
                && latest.HasSameInputs(request))
                return null;
            return Submit(request, requeryObstacles);
        }

        /// <summary>The request for the configured grid, Bake Mode and asset.</summary>
        internal static FlowFieldVolumeRequest CreateRequest(in FlowFieldManagerConfig config)
        {
            if (!TryCreateLayout(in config, out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Volume Bounds and Cell Size do not produce a valid grid.");
            return CreateRequest(
                in config,
                grid,
                worldBounds,
                config.BakeMode,
                FlowFieldBakeModes.UsesBakeAsset(config.BakeMode) ? config.BakeAssets.Volume : null);
        }

        internal static FlowFieldVolumeRequest CreateRequest(
            in FlowFieldManagerConfig config,
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData bakeData)
            => new FlowFieldVolumeRequest(
                grid,
                worldBounds,
                bakeMode,
                config.Obstacles.Layer,
                config.Obstacles.Clearance,
                config.HasGoal,
                config.GoalWorld,
                config.Flow.GoalInfluenceRadius,
                config.Flow.DefaultDirection,
                bakeData);

        /// <summary>
        /// False with the reason for the first invalid setting.
        /// <paramref name="spaceModeLocked"/> and <paramref name="bakeModeLocked"/>
        /// say a running session accepted a different Space Mode or Bake Mode.
        /// </summary>
        internal static bool TryValidate(
            in FlowFieldManagerConfig config,
            bool spaceModeLocked,
            bool bakeModeLocked,
            bool validateStaticGoal,
            out string reason)
        {
            if (!config.TryValidateShared(spaceModeLocked, bakeModeLocked, out reason))
                return false;
            FlowFieldObstacleSettings obstacles = config.Obstacles;
            if (!TryCreateLayout(in config, out Bounds volumeBounds, out FlowFieldGridSpace volumeGrid) || !volumeGrid.IsValid)
                return FlowFieldManagerConfig.Fail(out reason, "Volume Bounds and Cell Size do not produce a valid grid.");
            if (!FlowFieldBakeModes.UsesBakeAsset(config.BakeMode))
                return true;
            FlowFieldVolumeBakeData bakeData = config.BakeAssets.Volume;
            if (bakeData == null)
                return FlowFieldManagerConfig.Fail(out reason, $"Volume3D {config.BakeMode} mode requires a FlowFieldVolumeBakeData asset.");
            if (!bakeData.Matches(volumeGrid, volumeBounds, obstacles.Layer, obstacles.Clearance, out reason))
                return false;
            // Only StaticBaked keeps the baked Goal; BakedBase solves its own.
            return config.BakeMode != FlowFieldBakeMode.StaticBaked
                || !validateStaticGoal
                || bakeData.MatchesGoal(config.HasGoal, config.GoalWorld, config.Flow.GoalInfluenceRadius, out reason);
        }
    }
}
