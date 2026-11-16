using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>How Surface2D reads a Manager configuration: its grid, checks and requests.</summary>
    internal sealed partial class FlowFieldSurfaceBuilder
    {
        private const string BakeModeLockedMessage
            = "FlowField Bake Mode cannot change during an active Init session. Release and Init again.";

        internal static bool TryCreateLayout(
            in FlowFieldManagerConfig config,
            out Bounds worldBounds,
            out FlowFieldGridSpace grid)
            => FlowFieldBakeBoundsUtility.TryCreateWorldLayout(
                config.Position,
                config.Grid.BakeBoundsLocal,
                config.Grid.CellSize,
                out worldBounds,
                out grid);

        internal static FlowFieldSurfaceBakeSettings CreateBakeSettings(in FlowFieldManagerConfig config)
        {
            if (!TryCreateLayout(in config, out Bounds worldBounds, out FlowFieldGridSpace grid))
                throw new InvalidOperationException("Bake Bounds and Cell Size do not produce a valid grid.");
            return new FlowFieldSurfaceBakeSettings(
                grid,
                worldBounds,
                config.Surface.GroundLayer,
                config.Surface.MaxSlope,
                config.Surface.MaxStepHeight);
        }

        internal static FlowFieldSurfaceGoalResolution ResolveGoal(
            in FlowFieldManagerConfig config,
            FlowFieldGridSpace grid)
            => FlowFieldSurfaceGoalPipeline.Resolve(
                grid,
                config.GoalTransform,
                config.HasExplicitGoal,
                config.ExplicitGoalWorld,
                config.Flow.GoalInfluenceRadius);

        internal static FlowFieldSurfaceBakeSnapshot CreateStaticSnapshot(
            in FlowFieldManagerConfig config,
            in FlowFieldSurfaceBakeSettings settings)
        {
            FlowFieldStaticBakeData bakeData = config.BakeAssets.Surface;
            if (bakeData == null)
                throw new InvalidOperationException("StaticBaked mode requires a FlowFieldStaticBakeData asset.");
            return bakeData.CreateSnapshot(
                settings,
                config.Obstacles.Layer,
                config.Surface.ObstacleCheckHeight,
                config.Surface.ObstacleCheckCenterOffset,
                config.Obstacles.Clearance);
        }

        /// <summary>
        /// A request from the configuration. <paramref name="bakeMode"/> is
        /// the mode the session accepted, which picks the field source.
        /// </summary>
        internal static FlowFieldSurfaceRequest CreateRequest(
            in FlowFieldManagerConfig config,
            FlowFieldBakeMode bakeMode,
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
        {
            if (bakeMode == FlowFieldBakeMode.StaticBaked)
            {
                return FlowFieldSurfaceRequest.ForStaticSnapshot(
                    settings,
                    config.BakeAssets.Surface,
                    CreateStaticSnapshot(in config, in settings),
                    config.Obstacles.Layer,
                    config.Surface.ObstacleCheckHeight,
                    config.Surface.ObstacleCheckCenterOffset,
                    config.Obstacles.Clearance,
                    config.Flow.DefaultDirection,
                    dirtyFlags);
            }

            return FlowFieldSurfaceRequest.ForSceneBuild(
                settings,
                config.Obstacles.Layer,
                config.Surface.ObstacleCheckHeight,
                config.Surface.ObstacleCheckCenterOffset,
                config.Obstacles.Clearance,
                config.EnableUnregisteredObstacleSweep,
                ResolveGoal(in config, settings.Grid),
                config.Flow.DefaultDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion);
        }

        /// <summary>
        /// Throws for the first invalid setting. <paramref name="bakeModeLocked"/>
        /// says a running session accepted a different Bake Mode.
        /// </summary>
        internal static void Validate(
            in FlowFieldManagerConfig config,
            bool bakeModeLocked,
            bool validateStaticGoal)
        {
            FlowFieldGridSettings grid = config.Grid;
            FlowFieldSurfaceSettings surface = config.Surface;
            FlowFieldObstacleSettings obstacles = config.Obstacles;
            if (!FlowFieldGridSpace.IsFinite(grid.CellSize) || grid.CellSize < FlowFieldBakeBoundsUtility.MinCellSize)
                throw new ArgumentOutOfRangeException("_cellSize", grid.CellSize, "Cell Size must be finite and positive.");
            if (!FlowFieldGridSpace.IsFinite(grid.BakeBoundsLocal.center)
                || !FlowFieldGridSpace.IsFinite(grid.BakeBoundsLocal.size)
                || grid.BakeBoundsLocal.size.x <= 0f
                || grid.BakeBoundsLocal.size.y <= 0f
                || grid.BakeBoundsLocal.size.z <= 0f)
                throw new ArgumentOutOfRangeException("_bakeBoundsLocal", "Bake Bounds must be finite and positive.");
            if (!config.HasValidDefaultDirection)
                throw new ArgumentOutOfRangeException("_defaultFlowDirection", "Default Flow Direction must be finite and non-zero.");
            if (!config.HasSupportedTransform)
                throw new ArgumentException("FlowField Manager must use an unrotated transform with unit scale.", "transform");
            if (surface.GroundLayer.value == 0)
                throw new ArgumentException("Ground Bake LayerMask must contain at least one layer.", "_groundBakeLayer");
            if (obstacles.Layer.value == 0)
                throw new ArgumentException("Obstacle LayerMask must contain at least one layer.", "_obstacleLayer");
            if (!FlowFieldGridSpace.IsFinite(surface.MaxSlope) || surface.MaxSlope < 0f || surface.MaxSlope > 89f)
                throw new ArgumentOutOfRangeException("_maxSurfaceSlope");
            if (!FlowFieldGridSpace.IsFinite(surface.MaxStepHeight) || surface.MaxStepHeight < 0f)
                throw new ArgumentOutOfRangeException("_maxStepHeight");
            if (!FlowFieldGridSpace.IsFinite(surface.ObstacleCheckHeight) || surface.ObstacleCheckHeight <= 0f)
                throw new ArgumentOutOfRangeException("_obstacleCheckHeight");
            if (!FlowFieldGridSpace.IsFinite(surface.ObstacleCheckCenterOffset))
                throw new ArgumentOutOfRangeException("_obstacleCheckCenterOffset");
            if (!FlowFieldGridSpace.IsFinite(obstacles.Clearance) || obstacles.Clearance < 0f)
                throw new ArgumentOutOfRangeException("_obstacleClearance");
            if (!FlowFieldGridSpace.IsFinite(config.RefreshRate) || config.RefreshRate < FlowFieldManagerConfig.MinRefreshRate)
                throw new ArgumentOutOfRangeException("_refreshRate");
            if (!Enum.IsDefined(typeof(FlowFieldBakeMode), config.BakeMode))
                throw new ArgumentOutOfRangeException("_bakeMode");
            if (bakeModeLocked)
                throw new InvalidOperationException(BakeModeLockedMessage);
            if (!FlowFieldGridSpace.IsFinite(config.Flow.GoalInfluenceRadius) || config.Flow.GoalInfluenceRadius < 0f)
                throw new ArgumentOutOfRangeException("_goalInfluenceRadius");

            if (!TryCreateLayout(in config, out _, out FlowFieldGridSpace layout) || !layout.IsValid)
                throw new ArgumentException("Bake Bounds and Cell Size do not produce a valid grid.", "_bakeBoundsLocal");

            if (config.BakeMode != FlowFieldBakeMode.StaticBaked)
                return;
            FlowFieldStaticBakeData bakeData = config.BakeAssets.Surface;
            if (bakeData == null)
                throw new InvalidOperationException("StaticBaked mode requires a FlowFieldStaticBakeData asset.");
            if (!bakeData.MatchesSurface(CreateBakeSettings(in config), out string surfaceMismatch))
                throw new InvalidOperationException(surfaceMismatch);
            if (!bakeData.MatchesObstacles(
                    obstacles.Layer,
                    surface.ObstacleCheckHeight,
                    surface.ObstacleCheckCenterOffset,
                    obstacles.Clearance,
                    out string obstacleMismatch))
                throw new InvalidOperationException(obstacleMismatch);
            if (validateStaticGoal
                && !bakeData.MatchesGoal(config.HasGoal, config.GoalWorld, config.Flow.GoalInfluenceRadius))
                throw new InvalidOperationException(
                    "Static Flow Bake Goal이 현재 Manager 설정과 다릅니다. ReBake가 필요합니다.");
        }

        /// <summary>The Editor's check before a bake or preview, with its reason.</summary>
        internal static bool TryValidateBake(
            in FlowFieldManagerConfig config,
            bool includeStaticGoal,
            out string reason)
        {
            reason = string.Empty;
            if (!TryCreateLayout(in config, out _, out _))
            {
                reason = "Bake Bounds 또는 Cell Size가 유효하지 않습니다.";
                return false;
            }

            if (config.BakeMode != FlowFieldBakeMode.StaticBaked)
                return CreateBakeSettings(in config).IsValid;

            FlowFieldStaticBakeData bakeData = config.BakeAssets.Surface;
            if (bakeData == null)
            {
                reason = "StaticBaked 모드에는 FlowFieldStaticBakeData가 필요합니다.";
                return false;
            }
            if (!bakeData.MatchesSurface(CreateBakeSettings(in config), out reason))
                return false;
            if (!bakeData.MatchesObstacles(
                    config.Obstacles.Layer,
                    config.Surface.ObstacleCheckHeight,
                    config.Surface.ObstacleCheckCenterOffset,
                    config.Obstacles.Clearance,
                    out reason))
                return false;
            if (includeStaticGoal
                && !bakeData.MatchesGoal(config.HasGoal, config.GoalWorld, config.Flow.GoalInfluenceRadius))
            {
                reason = "Static Flow Bake Goal이 현재 Manager 설정과 다릅니다. ReBake가 필요합니다.";
                return false;
            }
            return true;
        }
    }
}
