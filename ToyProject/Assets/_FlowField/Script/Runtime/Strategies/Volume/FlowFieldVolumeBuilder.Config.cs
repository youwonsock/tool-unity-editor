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
            => FlowFieldBakeBoundsUtility.TryCreateVolumeWorldLayout(
                config.Position,
                config.Grid.BakeBoundsLocal,
                config.Grid.CellSize,
                out worldBounds,
                out grid);

        public void SubmitConfig(in FlowFieldManagerConfig config, FlowFieldConfigChange change)
        {
            switch (change)
            {
                case FlowFieldConfigChange.Rebuild:
                    SubmitConfigRequest(in config, force: true, requeryObstacles: true);
                    break;
                // A Goal change keeps the obstacle result unless an obstacle changed.
                case FlowFieldConfigChange.Goal:
                    SubmitConfigRequest(in config, force: true, requeryObstacles: false);
                    break;
                case FlowFieldConfigChange.Cells:
                    SubmitConfigRequest(in config, force: true, requeryObstacles: true);
                    break;
                // A changed request rebuilds; a new grid hides the old result at once.
                default:
                    SubmitConfigRequest(in config, force: false, requeryObstacles: false);
                    break;
            }
        }

        private void SubmitConfigRequest(in FlowFieldManagerConfig config, bool force, bool requeryObstacles)
        {
            FlowFieldVolumeRequest request = CreateRequest(in config);
            if (!force
                && TryGetLatestRequestedInput(out FlowFieldVolumeRequest latest)
                && latest.HasSameInputs(request))
                return;
            Submit(request, requeryObstacles);
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
                config.BakeMode == FlowFieldBakeMode.StaticBaked ? config.BakeAssets.Volume : null);
        }

        // A bake passes the input generation it captured, so it can tell when
        // the settings changed under it. Runtime requests do not compare it.
        internal static FlowFieldVolumeRequest CreateRequest(
            in FlowFieldManagerConfig config,
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            FlowFieldBakeMode bakeMode,
            FlowFieldVolumeBakeData bakeData,
            int bakeInputGeneration = 0)
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
                bakeData,
                bakeInputGeneration);

        /// <summary>
        /// Throws for the first invalid setting. <paramref name="bakeModeLocked"/>
        /// says a running session accepted a different Bake Mode.
        /// </summary>
        internal static void Validate(
            in FlowFieldManagerConfig config,
            bool bakeModeLocked,
            bool validateStaticGoal)
        {
            config.ValidateShared(bakeModeLocked);
            FlowFieldObstacleSettings obstacles = config.Obstacles;
            if (!TryCreateLayout(in config, out Bounds volumeBounds, out FlowFieldGridSpace volumeGrid) || !volumeGrid.IsValid)
                throw new ArgumentException("Volume Bounds and Cell Size do not produce a valid grid.", "_bakeBoundsLocal");
            if (volumeGrid.CellCount > FlowFieldBakeBoundsUtility.MaxVolumeCellCount)
                throw new ArgumentOutOfRangeException("_bakeBoundsLocal", "Volume cell count exceeds one million cells.");
            if (config.BakeMode != FlowFieldBakeMode.StaticBaked)
                return;
            FlowFieldVolumeBakeData bakeData = config.BakeAssets.Volume;
            if (bakeData == null)
                throw new InvalidOperationException("Volume3D StaticBaked mode requires a FlowFieldVolumeBakeData asset.");
            if (!bakeData.Matches(volumeGrid, volumeBounds, obstacles.Layer, obstacles.Clearance, out string volumeMismatch))
                throw new InvalidOperationException(volumeMismatch);
            if (validateStaticGoal
                && !bakeData.MatchesGoal(config.HasGoal, config.GoalWorld, config.Flow.GoalInfluenceRadius, out string goalMismatch))
                throw new InvalidOperationException(goalMismatch);
        }

        /// <summary>The Editor's check before a bake or preview, with its reason.</summary>
        internal static bool TryValidateBake(in FlowFieldManagerConfig config, out string reason)
        {
            reason = string.Empty;
            if (!config.HasSupportedTransform)
            {
                reason = "Volume3D는 회전 없는 unit-scale Manager Transform만 지원합니다.";
                return false;
            }
            if (config.Obstacles.Layer.value == 0)
            {
                reason = "Volume3D Obstacle LayerMask가 비어 있습니다.";
                return false;
            }
            if (!config.HasValidDefaultDirection)
            {
                reason = "Volume3D Default Direction은 유한한 non-zero 벡터여야 합니다.";
                return false;
            }
            float clearance = config.Obstacles.Clearance;
            float radius = config.Flow.GoalInfluenceRadius;
            if (!FlowFieldGridSpace.IsFinite(clearance) || clearance < 0f
                || !FlowFieldGridSpace.IsFinite(radius) || radius < 0f)
            {
                reason = "Volume3D Obstacle Clearance와 Goal Influence Radius가 유효하지 않습니다.";
                return false;
            }
            if (!TryCreateLayout(in config, out Bounds worldBounds, out FlowFieldGridSpace grid) || !grid.IsValid)
            {
                reason = "Volume Bounds 또는 Cell Size가 유효하지 않습니다.";
                return false;
            }
            if (grid.CellCount > FlowFieldBakeBoundsUtility.MaxVolumeCellCount)
            {
                reason = "Volume3D 셀 수가 1,000,000개를 초과합니다.";
                return false;
            }
            if (config.BakeMode != FlowFieldBakeMode.StaticBaked)
                return true;
            FlowFieldVolumeBakeData bakeData = config.BakeAssets.Volume;
            if (bakeData == null)
            {
                reason = "Volume3D StaticBaked 모드에는 FlowFieldVolumeBakeData가 필요합니다.";
                return false;
            }
            if (!bakeData.Matches(grid, worldBounds, config.Obstacles.Layer, clearance, out reason))
                return false;
            return bakeData.MatchesGoal(config.HasGoal, config.GoalWorld, radius, out reason);
        }
    }
}
