using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>How Surface2D reads a Manager configuration: its grid, checks and requests.</summary>
    internal sealed partial class FlowFieldSurfaceBuilder
    {
        // An explicit rebuild queries every obstacle again, as in Volume3D:
        // RuntimeDynamic rescans both masks, BakedBase starts again from the
        // bake and rescans what changed since. The ground is scanned again
        // only by NotifyCellsDirty or new surface settings.
        private const FlowFieldSurfaceDirtyFlags RequeryObstacleFlags
            = FlowFieldSurfaceDirtyFlags.Obstacles | FlowFieldSurfaceDirtyFlags.Escape;

        public Bounds? SubmitConfig(in FlowFieldManagerConfig config, FlowFieldConfigChange change)
        {
            switch (change)
            {
                case FlowFieldConfigChange.Rebuild:
                {
                    FlowFieldSurfaceBakeSettings settings = CreateBakeSettings(in config);
                    // A StaticBaked field takes no runtime obstacles.
                    bool requeryObstacles = _session.BakeMode != FlowFieldBakeMode.StaticBaked;
                    Bounds? invalidated = Submit(CreateRequest(
                        in config,
                        _session.BakeMode,
                        in settings,
                        requeryObstacles ? RequeryObstacleFlags : FlowFieldSurfaceDirtyFlags.None,
                        FlowFieldCellRect.Invalid,
                        requeryObstacles ? FlowFieldCellRect.Full(settings.Grid) : FlowFieldCellRect.Invalid));
                    _configOwed = false;
                    return invalidated;
                }
                case FlowFieldConfigChange.Goal:
                    _goalTracker.ResetWarning();
                    MarkDirty(FlowFieldSurfaceDirtyFlags.Goal, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
                    if (!config.HasGoal)
                        ObserveGoalRemoved();
                    // The request holds the Goal.
                    return SubmitOrOwe(in config, immediately: false);
                case FlowFieldConfigChange.Cells:
                    // Every cell again: the ground and, as a RequestRebuild
                    // does, every obstacle.
                    MarkDirty(FlowFieldSurfaceDirtyFlags.Grid | RequeryObstacleFlags,
                        FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
                    return null;
                default:
                    return SubmitSettings(in config);
            }
        }

        // The settings are valid here. Compare the request with the last
        // submitted one so a Default Direction edit rebuilds only the final
        // field and a Goal or obstacle edit reuses the Surface. StaticBaked
        // requests hold no runtime Goal or dynamic obstacle input, so those
        // edits change nothing there.
        private Bounds? SubmitSettings(in FlowFieldManagerConfig config)
        {
            FlowFieldSurfaceRequest current = CreateCurrentRequest(in config);
            if (HasSubmittedRequest && HasSameBaseInputs(current) && HasSameFinalInputs(current))
            {
                // The settings are back to the submitted request, Goal
                // included, so a change owed during this build was reverted
                // before it began: drop it, and compose with this direction.
                ObserveDefaultDirection(current.DefaultDirection);
                _configOwed = false;
                return null;
            }

            // Visible to a solve callback even before the next Submit.
            ObserveDefaultDirection(current.DefaultDirection);
            FlowFieldSurfaceDirtyFlags flags = GetInputDirtyFlags(current);
            bool gridChanged = (flags & FlowFieldSurfaceDirtyFlags.Grid) != 0;
            if (gridChanged && TryCreateLayout(in config, out _, out FlowFieldGridSpace grid))
                ObserveGrid(grid);
            Vector3 normalized = FlowFieldVectorUtility.NormalizeDefaultDirection(config.Flow.DefaultDirection);
            if ((_state.ResolvedDefaultDirection - normalized).sqrMagnitude
                > FlowFieldManagerConfig.ValueEpsilon * FlowFieldManagerConfig.ValueEpsilon)
                flags |= FlowFieldSurfaceDirtyFlags.DefaultDirection | FlowFieldSurfaceDirtyFlags.FinalRegion;
            MarkDirty(flags, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            // A new grid hides the published field at once.
            return SubmitOrOwe(in config, immediately: gridChanged);
        }

        // A running build keeps the request it started with; the next build
        // submits the newer configuration before it begins. New obstacle
        // settings are uncertain from now, not from that later submission.
        private Bounds? SubmitOrOwe(in FlowFieldManagerConfig config, bool immediately)
        {
            if (immediately || !HasActiveBuild)
            {
                _configOwed = false;
                return Submit(CreateCurrentRequest(in config));
            }
            _owedConfig = config;
            _configOwed = true;
            return ObstacleSettingsInvalidation(CreateCurrentRequest(in config));
        }

        private FlowFieldSurfaceRequest CreateCurrentRequest(in FlowFieldManagerConfig config)
            => CreateRequest(
                in config,
                _session.BakeMode,
                CreateBakeSettings(in config),
                FlowFieldSurfaceDirtyFlags.None,
                FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid);

        internal static bool TryCreateLayout(
            in FlowFieldManagerConfig config,
            out Bounds worldBounds,
            out FlowFieldGridSpace grid)
            => FlowFieldLayoutUtility.TryCreateWorldLayout(
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
            => FlowFieldSurfaceGoalResolver.Resolve(
                grid,
                config.HasGoal,
                config.GoalWorld,
                config.Flow.GoalInfluenceRadius);

        internal static FlowFieldSurfaceBakeSnapshot CreateBakeSnapshot(
            in FlowFieldManagerConfig config,
            in FlowFieldSurfaceBakeSettings settings)
        {
            FlowFieldSurfaceBakeData bakeData = config.BakeAssets.Surface;
            if (bakeData == null)
                throw new InvalidOperationException("StaticBaked and BakedBase modes require a FlowFieldSurfaceBakeData asset.");
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
            if (bakeMode == FlowFieldBakeMode.BakedBase)
            {
                return FlowFieldSurfaceRequest.ForBakedBase(
                    settings,
                    config.BakeAssets.Surface,
                    CreateBakeSnapshot(in config, in settings),
                    config.Obstacles.Layer,
                    config.Surface.ObstacleCheckHeight,
                    config.Surface.ObstacleCheckCenterOffset,
                    config.Obstacles.Clearance,
                    config.Obstacles.SweepUnregistered,
                    ResolveGoal(in config, settings.Grid),
                    config.Flow.DefaultDirection,
                    dirtyFlags,
                    dirtyFinalRegion,
                    dirtyObstacleRegion);
            }

            if (bakeMode == FlowFieldBakeMode.StaticBaked)
            {
                return FlowFieldSurfaceRequest.ForStaticSnapshot(
                    settings,
                    config.BakeAssets.Surface,
                    CreateBakeSnapshot(in config, in settings),
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
                config.Obstacles.SweepUnregistered,
                ResolveGoal(in config, settings.Grid),
                config.Flow.DefaultDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion);
        }

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
            FlowFieldSurfaceSettings surface = config.Surface;
            FlowFieldObstacleSettings obstacles = config.Obstacles;
            if (surface.GroundLayer.value == 0)
                return FlowFieldManagerConfig.Fail(out reason, "Ground Bake LayerMask must contain at least one layer.");
            if (!FlowFieldGridSpace.IsFinite(surface.MaxSlope) || surface.MaxSlope < 0f || surface.MaxSlope > 89f)
                return FlowFieldManagerConfig.Fail(out reason, "Max Slope must be between 0 and 89 degrees.");
            if (!FlowFieldGridSpace.IsFinite(surface.MaxStepHeight) || surface.MaxStepHeight < 0f)
                return FlowFieldManagerConfig.Fail(out reason, "Max Step Height must be finite and not negative.");
            if (!FlowFieldGridSpace.IsFinite(surface.ObstacleCheckHeight) || surface.ObstacleCheckHeight <= 0f)
                return FlowFieldManagerConfig.Fail(out reason, "Obstacle Check Height must be finite and positive.");
            if (!FlowFieldGridSpace.IsFinite(surface.ObstacleCheckCenterOffset))
                return FlowFieldManagerConfig.Fail(out reason, "Obstacle Check Center Offset must be finite.");

            if (!TryCreateLayout(in config, out _, out FlowFieldGridSpace layout) || !layout.IsValid)
                return FlowFieldManagerConfig.Fail(out reason, "Bake Bounds and Cell Size do not produce a valid grid.");

            if (!FlowFieldBakeModes.UsesBakeAsset(config.BakeMode))
                return true;
            FlowFieldSurfaceBakeData bakeData = config.BakeAssets.Surface;
            if (bakeData == null)
                return FlowFieldManagerConfig.Fail(out reason, $"{config.BakeMode} mode requires a FlowFieldSurfaceBakeData asset.");
            if (!bakeData.MatchesSurface(CreateBakeSettings(in config), out reason)
                || !bakeData.MatchesObstacles(
                    obstacles.Layer,
                    surface.ObstacleCheckHeight,
                    surface.ObstacleCheckCenterOffset,
                    obstacles.Clearance,
                    out reason))
                return false;
            // Only StaticBaked keeps the baked Goal; BakedBase solves its own.
            return config.BakeMode != FlowFieldBakeMode.StaticBaked
                || !validateStaticGoal
                || bakeData.MatchesGoal(config.HasGoal, config.GoalWorld, config.Flow.GoalInfluenceRadius, out reason);
        }
    }
}
