using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>How Surface2D reads a Manager configuration: its grid, checks and requests.</summary>
    internal sealed partial class FlowFieldSurfaceBuilder
    {
        public void SubmitConfig(in FlowFieldManagerConfig config, FlowFieldConfigChange change)
        {
            if (!_session.IsInitialized || _session.IsFaulted)
                return;
            switch (change)
            {
                case FlowFieldConfigChange.Rebuild:
                    Submit(CreateRequest(
                        in config,
                        _session.BakeMode,
                        CreateBakeSettings(in config),
                        FlowFieldSurfaceDirtyFlags.None,
                        _state.DirtyFinalRegion,
                        _state.DirtyObstacleRegion));
                    _configOwed = false;
                    _session.AttachRuntimeScheduler();
                    break;
                case FlowFieldConfigChange.Goal:
                    _goalTracker.ResetWarning();
                    MarkDirty(FlowFieldSurfaceDirtyFlags.Goal, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
                    if (!config.HasGoal)
                        ObserveGoalRemoved();
                    // The request holds the Goal.
                    SubmitOrOwe(in config, immediately: false);
                    break;
                case FlowFieldConfigChange.Cells:
                    MarkDirty(FlowFieldSurfaceDirtyFlags.Grid, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
                    break;
                default:
                    SubmitSettings(in config);
                    break;
            }
        }

        // The settings are valid here. Compare the request with the last
        // submitted one so a Default Direction edit rebuilds only the final
        // field and a Goal or obstacle edit reuses the Surface. StaticBaked
        // requests hold no runtime Goal or dynamic obstacle input, so those
        // edits change nothing.
        private void SubmitSettings(in FlowFieldManagerConfig config)
        {
            FlowFieldSurfaceRequest current = CreateCurrentRequest(in config);
            if (HasSubmittedRequest && HasSameBaseInputs(current) && HasSameFinalInputs(current))
                return;

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
            SubmitOrOwe(in config, immediately: gridChanged);
        }

        // A running build keeps the request it started with; the next build
        // submits the newer configuration before it begins.
        private void SubmitOrOwe(in FlowFieldManagerConfig config, bool immediately)
        {
            if (immediately || !_session.IsRebuilding)
            {
                _configOwed = false;
                Submit(CreateCurrentRequest(in config));
                return;
            }
            _owedConfig = config;
            _configOwed = true;
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
                null,
                config.HasGoal,
                config.GoalWorld,
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
            config.ValidateShared(bakeModeLocked);
            FlowFieldSurfaceSettings surface = config.Surface;
            FlowFieldObstacleSettings obstacles = config.Obstacles;
            if (surface.GroundLayer.value == 0)
                throw new ArgumentException("Ground Bake LayerMask must contain at least one layer.", "_groundBakeLayer");
            if (!FlowFieldGridSpace.IsFinite(surface.MaxSlope) || surface.MaxSlope < 0f || surface.MaxSlope > 89f)
                throw new ArgumentOutOfRangeException("_maxSurfaceSlope", "Max Slope must be between 0 and 89 degrees.");
            if (!FlowFieldGridSpace.IsFinite(surface.MaxStepHeight) || surface.MaxStepHeight < 0f)
                throw new ArgumentOutOfRangeException("_maxStepHeight", "Max Step Height must be finite and not negative.");
            if (!FlowFieldGridSpace.IsFinite(surface.ObstacleCheckHeight) || surface.ObstacleCheckHeight <= 0f)
                throw new ArgumentOutOfRangeException("_obstacleCheckHeight", "Obstacle Check Height must be finite and positive.");
            if (!FlowFieldGridSpace.IsFinite(surface.ObstacleCheckCenterOffset))
                throw new ArgumentOutOfRangeException("_obstacleCheckCenterOffset", "Obstacle Check Center Offset must be finite.");

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
