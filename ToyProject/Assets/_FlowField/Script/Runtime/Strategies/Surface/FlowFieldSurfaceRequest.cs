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
        internal FlowFieldSurfaceSignature Signature { get; }
        internal FlowFieldSurfaceBakeData BakeSource { get; }
        internal FlowFieldSurfaceBakeSnapshot BakeSnapshot { get; }
        private readonly int _bakeContentGeneration;
        internal FlowFieldSurfaceBakeSettings SurfaceSettings => Signature.Surface;
        internal LayerMask ObstacleLayer => Signature.ObstacleLayer;
        internal float ObstacleCheckHeight => Signature.ObstacleCheckHeight;
        internal float ObstacleCheckCenterOffset => Signature.ObstacleCheckCenterOffset;
        internal float ObstacleClearance => Signature.ObstacleClearance;
        // Compared with the obstacle inputs, so switching the sweep rescans every obstacle.
        internal bool SweepUnregistered { get; }
        internal FlowFieldSurfaceGoalResolution Goal { get; }
        internal Vector3 DefaultDirection { get; }
        internal FlowFieldSurfaceDirtyFlags DirtyFlags { get; }
        internal FlowFieldCellRect DirtyFinalRegion { get; }
        internal FlowFieldCellRect DirtyObstacleRegion { get; }

        private FlowFieldSurfaceRequest(
            FlowFieldSurfaceSourceKind sourceKind,
            in FlowFieldSurfaceSignature signature,
            FlowFieldSurfaceBakeData bakeSource,
            FlowFieldSurfaceBakeSnapshot bakeSnapshot,
            bool sweepUnregistered,
            in FlowFieldSurfaceGoalResolution goal,
            Vector3 defaultDirection,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
        {
            SourceKind = sourceKind;
            Signature = signature;
            BakeSource = bakeSource;
            BakeSnapshot = bakeSnapshot;
            _bakeContentGeneration = bakeSource != null ? bakeSource.ContentGeneration : -1;
            SweepUnregistered = sweepUnregistered;
            Goal = goal;
            DefaultDirection = defaultDirection;
            DirtyFlags = dirtyFlags;
            DirtyFinalRegion = dirtyFinalRegion;
            DirtyObstacleRegion = dirtyObstacleRegion;
        }

        internal static FlowFieldSurfaceRequest ForSceneBuild(
            in FlowFieldSurfaceSignature signature,
            bool sweepUnregistered,
            in FlowFieldSurfaceGoalResolution goal,
            Vector3 defaultDirection,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
            => new FlowFieldSurfaceRequest(
                FlowFieldSurfaceSourceKind.SceneBuild,
                signature,
                null,
                null,
                sweepUnregistered,
                goal,
                defaultDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion);

        internal static FlowFieldSurfaceRequest ForBakedBase(
            in FlowFieldSurfaceSignature signature,
            FlowFieldSurfaceBakeData bakeSource,
            FlowFieldSurfaceBakeSnapshot bakeSnapshot,
            bool sweepUnregistered,
            in FlowFieldSurfaceGoalResolution goal,
            Vector3 defaultDirection,
            FlowFieldSurfaceDirtyFlags dirtyFlags,
            FlowFieldCellRect dirtyFinalRegion,
            FlowFieldCellRect dirtyObstacleRegion)
            => new FlowFieldSurfaceRequest(
                FlowFieldSurfaceSourceKind.BakedBase,
                signature,
                bakeSource,
                bakeSnapshot,
                sweepUnregistered,
                goal,
                defaultDirection,
                dirtyFlags,
                dirtyFinalRegion,
                dirtyObstacleRegion);

        internal static FlowFieldSurfaceRequest ForStaticSnapshot(
            in FlowFieldSurfaceSignature signature,
            FlowFieldSurfaceBakeData bakeSource,
            FlowFieldSurfaceBakeSnapshot bakeSnapshot,
            Vector3 defaultDirection,
            FlowFieldSurfaceDirtyFlags dirtyFlags)
            => new FlowFieldSurfaceRequest(
                FlowFieldSurfaceSourceKind.StaticSnapshot,
                signature,
                bakeSource,
                bakeSnapshot,
                false,
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
                && Signature.SurfaceMatches(other.Signature)
                && HasSameBake(other);

        internal bool HasSameBaseInputs(in FlowFieldSurfaceRequest other)
            => SourceKind == other.SourceKind
                && Signature.SurfaceMatches(other.Signature)
                && HasSameBake(other)
                && Signature.ObstaclesMatch(other.Signature)
                && SweepUnregistered == other.SweepUnregistered
                && GoalMatch(Goal, other.Goal);

        internal bool HasSameFinalInputs(in FlowFieldSurfaceRequest other)
            => VectorMatch(DefaultDirection, other.DefaultDirection);

        internal FlowFieldSurfaceDirtyFlags GetInputDirtyFlags(in FlowFieldSurfaceRequest other)
        {
            FlowFieldSurfaceDirtyFlags flags = FlowFieldSurfaceDirtyFlags.None;
            bool surfaceChanged = !Signature.SurfaceMatches(other.Signature)
                || !HasSameBake(other)
                || SourceKind != other.SourceKind;
            if (surfaceChanged)
            {
                flags |= FlowFieldSurfaceDirtyFlags.All;
            }
            else if (!Signature.ObstaclesMatch(other.Signature)
                || SweepUnregistered != other.SweepUnregistered)
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
