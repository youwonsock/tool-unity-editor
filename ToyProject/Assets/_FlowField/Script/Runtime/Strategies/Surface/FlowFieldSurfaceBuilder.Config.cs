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
                    FlowFieldSurfaceSignature signature = CreateSignature(in config);
                    // A StaticBaked field takes no runtime obstacles.
                    bool requeryObstacles = _session.BakeMode != FlowFieldBakeMode.StaticBaked;
                    Bounds? invalidated = Submit(CreateRequest(
                        in config,
                        _session.BakeMode,
                        in signature,
                        requeryObstacles ? RequeryObstacleFlags : FlowFieldSurfaceDirtyFlags.None,
                        FlowFieldCellRect.Invalid,
                        requeryObstacles ? FlowFieldCellRect.Full(signature.Surface.Grid) : FlowFieldCellRect.Invalid));
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
                    // does, every obstacle. The grid stays, so work already
                    // running still publishes, as in Volume3D.
                    MarkDirty(FlowFieldSurfaceDirtyFlags.Ground | RequeryObstacleFlags,
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
            FlowFieldSurfaceDirtyFlags flags = InputDirtyFlags(current);
            MarkDirty(flags, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            if (_requests.HasLatest && current.HasSameInputs(_requests.Latest))
            {
                // The settings are back to the submitted request, Goal
                // included, so a change owed during this build was reverted
                // before it began: drop it. A direction a solve already
                // composed is composed again (the flags above).
                _configOwed = false;
                return null;
            }
            // A new grid drops the published field at once.
            return SubmitOrOwe(in config, immediately: (flags & FlowFieldSurfaceDirtyFlags.Grid) != 0);
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
                CreateSignature(in config),
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
                throw new InvalidOperationException(FlowFieldManagerConfig.InvalidGridMessage);
            return new FlowFieldSurfaceBakeSettings(
                grid,
                worldBounds,
                config.Surface.GroundLayer,
                config.Surface.MaxSlope,
                config.Surface.MaxStepHeight);
        }

        /// <summary>The surface and obstacle settings the configuration builds and bakes with.</summary>
        internal static FlowFieldSurfaceSignature CreateSignature(in FlowFieldManagerConfig config)
            => new FlowFieldSurfaceSignature(
                CreateBakeSettings(in config),
                config.Obstacles.Layer,
                config.Surface.ObstacleCheckHeight,
                config.Surface.ObstacleCheckCenterOffset,
                config.Obstacles.Clearance);

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
            in FlowFieldSurfaceSignature signature)
        {
            FlowFieldSurfaceBakeData bakeData = config.BakeAssets.Surface;
            if (bakeData == null)
                throw new InvalidOperationException("StaticBaked and BakedBase modes require a FlowFieldSurfaceBakeData asset.");
            return bakeData.CreateSnapshot(in signature);
        }

        /// <summary>
        /// A request from the configuration. <paramref name="bakeMode"/> is
        /// the mode the session accepted, which picks the field source.
        /// </summary>
        internal static FlowFieldSurfaceRequest CreateRequest(
            in FlowFieldManagerConfig config,
            FlowFieldBakeMode bakeMode,
            in FlowFieldSurfaceSignature signature,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
        {
            if (bakeMode == FlowFieldBakeMode.BakedBase)
            {
                return FlowFieldSurfaceRequest.ForBakedBase(
                    signature,
                    config.BakeAssets.Surface,
                    CreateBakeSnapshot(in config, in signature),
                    config.Obstacles.SweepUnregistered,
                    ResolveGoal(in config, signature.Surface.Grid),
                    config.Flow.DefaultDirection,
                    dirtyFlags,
                    dirtyFinalRegion,
                    dirtyObstacleRegion);
            }

            if (bakeMode == FlowFieldBakeMode.StaticBaked)
            {
                return FlowFieldSurfaceRequest.ForStaticSnapshot(
                    signature,
                    config.BakeAssets.Surface,
                    CreateBakeSnapshot(in config, in signature),
                    config.Flow.DefaultDirection,
                    dirtyFlags);
            }

            return FlowFieldSurfaceRequest.ForSceneBuild(
                signature,
                config.Obstacles.SweepUnregistered,
                ResolveGoal(in config, signature.Surface.Grid),
                config.Flow.DefaultDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion);
        }

        /// <summary>
        /// False with the reason for the first invalid setting.
        /// <paramref name="bakeModeLocked"/> says a running session accepted a
        /// different Bake Mode.
        /// </summary>
        internal static bool TryValidate(
            in FlowFieldManagerConfig config,
            bool bakeModeLocked,
            bool validateStaticGoal,
            out string reason)
        {
            if (!config.TryValidateShared(bakeModeLocked, out reason))
                return false;
            FlowFieldSurfaceSettings surface = config.Surface;
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
                return FlowFieldManagerConfig.Fail(out reason, FlowFieldManagerConfig.InvalidGridMessage);

            if (!FlowFieldBakeModes.UsesBakeAsset(config.BakeMode))
                return true;
            FlowFieldSurfaceBakeData bakeData = config.BakeAssets.Surface;
            if (bakeData == null)
                return FlowFieldManagerConfig.Fail(out reason, $"Surface2D {config.BakeMode} mode requires a FlowFieldSurfaceBakeData asset.");
            if (!bakeData.Matches(CreateSignature(in config), out reason))
                return false;
            // Only StaticBaked keeps the baked Goal; BakedBase solves its own.
            return config.BakeMode != FlowFieldBakeMode.StaticBaked
                || !validateStaticGoal
                || bakeData.MatchesGoal(config.HasGoal, config.GoalWorld, config.Flow.GoalInfluenceRadius, out reason);
        }
    }
}
