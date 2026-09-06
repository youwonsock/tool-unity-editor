using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Common identity for an accepted build. Mode-specific requests still
    /// own their solver inputs, while this value is used by the shared
    /// session for source/version and publication comparisons.
    /// </summary>
    internal readonly struct FlowFieldBuildRequest
    {
        internal FlowFieldSpaceMode SpaceMode { get; }
        internal FlowFieldBakeMode BakeMode { get; }
        internal FlowFieldGridSpace Grid { get; }
        internal Bounds WorldBounds { get; }
        internal bool HasGoal { get; }
        internal Vector3 GoalWorld { get; }
        internal float GoalInfluenceRadius { get; }
        internal Object SourceAsset { get; }
        internal int SourceRevision { get; }
        internal int SourceContentGeneration { get; }

        private FlowFieldBuildRequest(
            FlowFieldSpaceMode spaceMode,
            FlowFieldBakeMode bakeMode,
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            bool hasGoal,
            Vector3 goalWorld,
            float goalInfluenceRadius,
            Object sourceAsset,
            int sourceRevision,
            int sourceContentGeneration)
        {
            SpaceMode = spaceMode;
            BakeMode = bakeMode;
            Grid = grid;
            WorldBounds = worldBounds;
            HasGoal = hasGoal;
            GoalWorld = goalWorld;
            GoalInfluenceRadius = goalInfluenceRadius;
            SourceAsset = sourceAsset;
            SourceRevision = sourceRevision;
            SourceContentGeneration = sourceContentGeneration;
        }

        internal static FlowFieldBuildRequest FromSurface(in FlowFieldSessionRequest request)
        {
            bool hasGoal = request.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot
                ? request.StaticBakeSnapshot != null && request.StaticBakeSnapshot.HasGoal
                : request.Goal.IsValid;
            Vector3 goalWorld = request.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot
                ? request.StaticBakeSnapshot != null
                    ? request.StaticBakeSnapshot.RequestedGoalWorld
                    : default
                : request.Goal.RequestedWorld;
            float radius = request.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot
                ? request.StaticBakeSnapshot != null
                    ? request.StaticBakeSnapshot.GoalInfluenceRadius
                    : 0f
                : request.Goal.InfluenceRadius;
            FlowFieldStaticBakeData source = request.StaticBakeSource;
            return new FlowFieldBuildRequest(
                FlowFieldSpaceMode.Surface2D,
                request.SourceKind == FlowFieldSessionSourceKind.StaticSnapshot
                    ? FlowFieldBakeMode.StaticBaked
                    : FlowFieldBakeMode.RuntimeDynamic,
                request.SurfaceSettings.Grid,
                request.SurfaceSettings.BakeBounds,
                hasGoal,
                goalWorld,
                radius,
                source,
                source != null ? source.Revision : -1,
                source != null ? source.ContentGeneration : -1);
        }

        internal static FlowFieldBuildRequest FromVolume(in FlowFieldVolumeRequest request)
            => new FlowFieldBuildRequest(
                FlowFieldSpaceMode.Volume3D,
                request.BakeMode,
                request.Grid,
                request.WorldBounds,
                request.HasGoal,
                request.GoalWorld,
                request.GoalInfluenceRadius,
                request.StaticBakeData,
                request.StaticBakeRevision,
                request.StaticBakeContentGeneration);

        internal bool HasSameSource(in FlowFieldBuildRequest other)
            => SpaceMode == other.SpaceMode
                && BakeMode == other.BakeMode
                && Grid.MatchesBounds(other.Grid)
                && Approximately(WorldBounds, other.WorldBounds)
                && ReferenceEquals(SourceAsset, other.SourceAsset)
                && SourceRevision == other.SourceRevision
                && SourceContentGeneration == other.SourceContentGeneration;

        private static bool Approximately(Bounds left, Bounds right)
            => FlowFieldGridSpace.Approximately(left.center, right.center, 0.00000001d)
                && FlowFieldGridSpace.Approximately(left.size, right.size, 0.00000001d);
    }
}
