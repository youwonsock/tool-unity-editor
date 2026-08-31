using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldSurfaceRequest
    {
        internal FlowFieldRuntimeContext Context { get; }
        internal FlowFieldSurfaceBakeSettings Settings { get; }
        internal FlowFieldSurfaceBakeData Surface { get; }
        internal FlowFieldStaticObstacleBakeData StaticObstacles { get; }
        internal FlowFieldCoarseTopologyData CoarseTopology { get; }
        internal LayerMask ObstacleLayer { get; }
        internal float CheckHeight { get; }
        internal float CenterOffset { get; }
        internal float Clearance { get; }
        internal int CoarseMultiplier { get; }
        internal float CoarseWalkableRatio { get; }

        internal FlowFieldSurfaceRequest(
            FlowFieldRuntimeContext context,
            FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeData surface,
            FlowFieldStaticObstacleBakeData staticObstacles,
            FlowFieldCoarseTopologyData coarseTopology,
            LayerMask obstacleLayer,
            float checkHeight,
            float centerOffset,
            float clearance,
            int coarseMultiplier,
            float coarseWalkableRatio)
        {
            Context = context;
            Settings = settings;
            Surface = surface;
            StaticObstacles = staticObstacles;
            CoarseTopology = coarseTopology;
            ObstacleLayer = obstacleLayer;
            CheckHeight = checkHeight;
            CenterOffset = centerOffset;
            Clearance = clearance;
            CoarseMultiplier = coarseMultiplier;
            CoarseWalkableRatio = coarseWalkableRatio;
        }
    }

    internal readonly struct FlowFieldSurfaceResult
    {
        internal bool IsReady { get; }
        internal string Error { get; }

        private FlowFieldSurfaceResult(bool isReady, string error)
        {
            IsReady = isReady;
            Error = error;
        }

        internal static FlowFieldSurfaceResult Ready()
            => new FlowFieldSurfaceResult(true, string.Empty);

        internal static FlowFieldSurfaceResult Failed(string reason)
            => new FlowFieldSurfaceResult(false, reason);
    }

    internal static class FlowFieldSurfacePipeline
    {
        internal static FlowFieldSurfaceResult Prepare(in FlowFieldSurfaceRequest request)
        {
            if (!request.Settings.Grid.IsValid)
                return FlowFieldSurfaceResult.Failed("Grid 설정이 유효하지 않습니다.");
            if (!FlowFieldBakeBoundsUtility.TryValidateCellCount(
                    request.Settings.Grid.Width,
                    request.Settings.Grid.Depth,
                    out _))
            {
                return FlowFieldSurfaceResult.Failed(
                    $"Grid Cell 수가 상한({FlowFieldBakeBoundsUtility.MaxCellCount:N0})을 초과합니다.");
            }
            FlowFieldRuntimeContext context = request.Context;
            bool boundsChanged = !context.Grid.MatchesBounds(request.Settings.Grid);
            context.Grid = request.Settings.Grid;
            if (!TryValidate(request, out string reason))
                return FlowFieldSurfaceResult.Failed(reason);

            bool dimensionsChanged = context.Workspace.Resize(request.Settings.Grid.CellCount);
            context.SurfaceReady = true;
            context.Surface = request.Surface;
            context.LastSurfaceRevision = request.Surface.Revision;
            context.StaticObstacles = request.StaticObstacles;
            context.CoarseTopology = request.CoarseTopology;
            if (request.StaticObstacles != null)
                context.LastStaticObstacleRevision = request.StaticObstacles.Revision;
            if (request.CoarseTopology != null)
                context.LastCoarseRevision = request.CoarseTopology.Revision;
            if (dimensionsChanged || boundsChanged)
                context.HasObstacleMask = false;

            return FlowFieldSurfaceResult.Ready();
        }

        /// <summary>
        /// Surface/Obstacle/Coarse Bake 조합을 진단합니다. 편집기 HelpBox에서
        /// 첫 오류를 표시할 수 있도록 유효하지 않은 설정은 false라는 정상 결과입니다.
        /// </summary>
        /// <returns>요청이 유효하면 true, 진단할 오류가 있으면 false입니다.</returns>
        internal static bool TryValidate(in FlowFieldSurfaceRequest request, out string reason)
        {
            reason = string.Empty;
            if (!request.Settings.IsValid)
            {
                reason = "Bake Bounds, Cell Size 또는 Ground 설정이 유효하지 않습니다.";
                return false;
            }
            if (request.Surface == null)
            {
                reason = "Surface Bake Asset이 지정되지 않았습니다.";
                return false;
            }
            if (!request.Surface.Matches(request.Settings, out reason))
                return false;
            if (request.StaticObstacles != null
                && !request.StaticObstacles.Matches(
                    request.Settings.Grid,
                    request.ObstacleLayer,
                    request.CheckHeight,
                    request.CenterOffset,
                    request.Clearance,
                    out reason))
                return false;
            if (request.CoarseTopology != null
                && !request.CoarseTopology.Matches(
                    request.Settings.Grid,
                    request.CoarseMultiplier,
                    request.CoarseWalkableRatio,
                    out reason))
                return false;

            return true;
        }
    }
}
