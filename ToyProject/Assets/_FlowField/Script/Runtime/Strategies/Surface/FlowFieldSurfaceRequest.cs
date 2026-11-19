using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal enum FlowFieldSurfaceSourceKind
    {
        // The surface and obstacles come from the scene (RuntimeDynamic).
        SceneBuild,
        // The whole field comes from a bake (StaticBaked).
        StaticSnapshot,
        // The surface and obstacles start from a bake; the Goal and obstacle
        // changes are runtime input (BakedBase).
        BakedBase,
    }

    internal readonly struct FlowFieldSurfaceRequest
    {
        internal FlowFieldSurfaceSourceKind SourceKind { get; }
        internal FlowFieldSurfaceBakeSettings SurfaceSettings { get; }
        internal FlowFieldSurfaceBakeData BakeSource { get; }
        internal FlowFieldSurfaceBakeSnapshot BakeSnapshot { get; }
        private readonly int _bakeContentGeneration;
        internal LayerMask ObstacleLayer { get; }
        internal float ObstacleCheckHeight { get; }
        internal float ObstacleCheckCenterOffset { get; }
        internal float ObstacleClearance { get; }
        internal FlowFieldSurfaceGoalResolution Goal { get; }
        internal Vector3 DefaultDirection { get; }
        internal FlowFieldSurfaceDirtyFlags DirtyFlags { get; }
        internal FlowFieldCellRect DirtyFinalRegion { get; }
        internal FlowFieldCellRect DirtyObstacleRegion { get; }

        private FlowFieldSurfaceRequest(
            FlowFieldSurfaceSourceKind sourceKind,
            in FlowFieldSurfaceBakeSettings surfaceSettings,
            FlowFieldSurfaceBakeData bakeSource,
            FlowFieldSurfaceBakeSnapshot bakeSnapshot,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance,
            in FlowFieldSurfaceGoalResolution goal,
            Vector3 defaultDirection,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
        {
            SourceKind = sourceKind;
            SurfaceSettings = surfaceSettings;
            BakeSource = bakeSource;
            BakeSnapshot = bakeSnapshot;
            _bakeContentGeneration = bakeSource != null ? bakeSource.ContentGeneration : -1;
            ObstacleLayer = obstacleLayer;
            ObstacleCheckHeight = obstacleCheckHeight;
            ObstacleCheckCenterOffset = obstacleCheckCenterOffset;
            ObstacleClearance = obstacleClearance;
            Goal = goal;
            DefaultDirection = defaultDirection;
            DirtyFlags = dirtyFlags;
            DirtyFinalRegion = dirtyFinalRegion;
            DirtyObstacleRegion = dirtyObstacleRegion;
        }

        internal static FlowFieldSurfaceRequest ForSceneBuild(
            in FlowFieldSurfaceBakeSettings settings,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance,
            in FlowFieldSurfaceGoalResolution goal,
            Vector3 defaultDirection,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
            => new FlowFieldSurfaceRequest(
                FlowFieldSurfaceSourceKind.SceneBuild,
                settings,
                null,
                null,
                obstacleLayer,
                obstacleCheckHeight,
                obstacleCheckCenterOffset,
                obstacleClearance,
                goal,
                defaultDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion);

        internal static FlowFieldSurfaceRequest ForBakedBase(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeData bakeSource,
            FlowFieldSurfaceBakeSnapshot bakeSnapshot,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance,
            in FlowFieldSurfaceGoalResolution goal,
            Vector3 defaultDirection,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
            => new FlowFieldSurfaceRequest(
                FlowFieldSurfaceSourceKind.BakedBase,
                settings,
                bakeSource,
                bakeSnapshot,
                obstacleLayer,
                obstacleCheckHeight,
                obstacleCheckCenterOffset,
                obstacleClearance,
                goal,
                defaultDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion);

        internal static FlowFieldSurfaceRequest ForStaticSnapshot(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeData bakeSource,
            FlowFieldSurfaceBakeSnapshot bakeSnapshot,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance,
            Vector3 defaultDirection,
            FlowFieldSurfaceDirtyFlags dirtyFlags)
            => new FlowFieldSurfaceRequest(
                FlowFieldSurfaceSourceKind.StaticSnapshot,
                settings,
                bakeSource,
                bakeSnapshot,
                obstacleLayer,
                obstacleCheckHeight,
                obstacleCheckCenterOffset,
                obstacleClearance,
                FlowFieldSurfaceGoalResolution.None,
                defaultDirection,
                dirtyFlags,
                FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid);

        internal bool HasSameInputs(in FlowFieldSurfaceRequest other)
            => HasSameBaseInputs(other)
                && VectorMatch(DefaultDirection, other.DefaultDirection);

        /// <summary>
        /// Identifies the immutable field source. Goal, obstacle and final
        /// composition changes may keep the previous publication visible, but
        /// a new grid or bake source must hide it until the replacement is
        /// committed.
        /// </summary>
        internal bool HasSameSource(in FlowFieldSurfaceRequest other)
            => SourceKind == other.SourceKind
                && SurfaceSettingsMatch(SurfaceSettings, other.SurfaceSettings)
                && HasSameBake(other);

        internal bool HasSameBaseInputs(in FlowFieldSurfaceRequest other)
            => SourceKind == other.SourceKind
                && SurfaceSettingsMatch(SurfaceSettings, other.SurfaceSettings)
                && HasSameBake(other)
                && ObstacleLayer.value == other.ObstacleLayer.value
                && FlowFieldLayoutUtility.Approximately(ObstacleCheckHeight, other.ObstacleCheckHeight)
                && FlowFieldLayoutUtility.Approximately(ObstacleCheckCenterOffset, other.ObstacleCheckCenterOffset)
                && FlowFieldLayoutUtility.Approximately(ObstacleClearance, other.ObstacleClearance)
                && GoalMatch(Goal, other.Goal);

        internal bool HasSameFinalInputs(in FlowFieldSurfaceRequest other)
            => VectorMatch(DefaultDirection, other.DefaultDirection);

        internal FlowFieldSurfaceDirtyFlags GetInputDirtyFlags(in FlowFieldSurfaceRequest other)
        {
            FlowFieldSurfaceDirtyFlags flags = FlowFieldSurfaceDirtyFlags.None;
            bool surfaceChanged = !SurfaceSettingsMatch(SurfaceSettings, other.SurfaceSettings)
                || !HasSameBake(other)
                || SourceKind != other.SourceKind;
            if (surfaceChanged)
            {
                flags |= FlowFieldSurfaceDirtyFlags.All;
            }
            else if (ObstacleLayer.value != other.ObstacleLayer.value
                || !FlowFieldLayoutUtility.Approximately(ObstacleCheckHeight, other.ObstacleCheckHeight)
                || !FlowFieldLayoutUtility.Approximately(ObstacleCheckCenterOffset, other.ObstacleCheckCenterOffset)
                || !FlowFieldLayoutUtility.Approximately(ObstacleClearance, other.ObstacleClearance))
            {
                flags |= FlowFieldSurfaceDirtyFlags.StaticObstacles
                    | FlowFieldSurfaceDirtyFlags.DynamicObstacles
                    | FlowFieldSurfaceDirtyFlags.Escape;
            }

            if (!GoalMatch(Goal, other.Goal))
                flags |= FlowFieldSurfaceDirtyFlags.Goal | FlowFieldSurfaceDirtyFlags.FinalRegion;
            if (!VectorMatch(DefaultDirection, other.DefaultDirection))
                flags |= FlowFieldSurfaceDirtyFlags.DefaultDirection | FlowFieldSurfaceDirtyFlags.FinalRegion;
            return flags;
        }

        private bool HasSameBake(in FlowFieldSurfaceRequest other)
            => ReferenceEquals(BakeSource, other.BakeSource)
                && (ReferenceEquals(BakeSource, null)
                    || (BakeSnapshot?.Revision ?? -1) == (other.BakeSnapshot?.Revision ?? -1)
                    && _bakeContentGeneration == other._bakeContentGeneration);

        private static bool SurfaceSettingsMatch(
            in FlowFieldSurfaceBakeSettings left,
            in FlowFieldSurfaceBakeSettings right)
            => left.Grid.MatchesBounds(right.Grid)
                && FlowFieldLayoutUtility.Approximately(left.BakeBounds, right.BakeBounds)
                && left.GroundLayer.value == right.GroundLayer.value
                && FlowFieldLayoutUtility.Approximately(left.MaxSurfaceSlope, right.MaxSurfaceSlope)
                && FlowFieldLayoutUtility.Approximately(left.MaxStepHeight, right.MaxStepHeight);

        private static bool GoalMatch(
            in FlowFieldSurfaceGoalResolution left,
            in FlowFieldSurfaceGoalResolution right)
            => left.HasActiveGoal == right.HasActiveGoal
                && left.IsValid == right.IsValid
                && left.LocalX == right.LocalX
                && left.LocalZ == right.LocalZ
                && left.SourceCellIndex == right.SourceCellIndex
                && FlowFieldLayoutUtility.Approximately(left.InfluenceRadius, right.InfluenceRadius)
                && VectorMatch(left.RequestedWorld, right.RequestedWorld);

        private static bool VectorMatch(Vector3 left, Vector3 right)
            => (left - right).sqrMagnitude <= 0.00000001f;
    }
}
