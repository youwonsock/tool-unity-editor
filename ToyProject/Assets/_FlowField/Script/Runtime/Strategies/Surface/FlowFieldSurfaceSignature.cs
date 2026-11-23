using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The settings a Surface2D field is built from: the surface part (grid,
    /// Bake Bounds, Ground Layer, Max Slope, Max Step Height) and the obstacle
    /// part (Layer, Check Height, Check Center Offset, Clearance). Requests,
    /// bake assets, bake snapshots and surface data compare their settings
    /// through it, with one tolerance: the grid's own
    /// <see cref="FlowFieldGridSpace.MatchesBounds"/> and
    /// <see cref="FlowFieldLayoutUtility.Approximately(float, float)"/> for the rest.
    /// </summary>
    internal readonly struct FlowFieldSurfaceSignature
    {
        internal FlowFieldSurfaceBakeSettings Surface { get; }
        internal LayerMask ObstacleLayer { get; }
        internal float ObstacleCheckHeight { get; }
        internal float ObstacleCheckCenterOffset { get; }
        internal float ObstacleClearance { get; }

        internal FlowFieldSurfaceSignature(
            in FlowFieldSurfaceBakeSettings surface,
            LayerMask obstacleLayer,
            float obstacleCheckHeight,
            float obstacleCheckCenterOffset,
            float obstacleClearance)
        {
            Surface = surface;
            ObstacleLayer = obstacleLayer;
            ObstacleCheckHeight = obstacleCheckHeight;
            ObstacleCheckCenterOffset = obstacleCheckCenterOffset;
            ObstacleClearance = obstacleClearance;
        }

        internal bool SurfaceMatches(in FlowFieldSurfaceSignature other)
            => SurfaceMatches(Surface, other.Surface);

        internal bool ObstaclesMatch(in FlowFieldSurfaceSignature other)
            => ObstacleLayer.value == other.ObstacleLayer.value
                && FlowFieldLayoutUtility.Approximately(ObstacleCheckHeight, other.ObstacleCheckHeight)
                && FlowFieldLayoutUtility.Approximately(ObstacleCheckCenterOffset, other.ObstacleCheckCenterOffset)
                && FlowFieldLayoutUtility.Approximately(ObstacleClearance, other.ObstacleClearance);

        // The vertical check box, which Modifier influence masks use as well as obstacles.
        internal bool CheckVolumeMatches(in FlowFieldSurfaceSignature other)
            => FlowFieldLayoutUtility.Approximately(ObstacleCheckHeight, other.ObstacleCheckHeight)
                && FlowFieldLayoutUtility.Approximately(ObstacleCheckCenterOffset, other.ObstacleCheckCenterOffset);

        /// <summary>The surface part alone, for data that has no obstacle settings.</summary>
        internal static bool SurfaceMatches(
            in FlowFieldSurfaceBakeSettings left,
            in FlowFieldSurfaceBakeSettings right)
            => left.Grid.MatchesBounds(right.Grid)
                && FlowFieldLayoutUtility.Approximately(left.BakeBounds, right.BakeBounds)
                && left.GroundLayer.value == right.GroundLayer.value
                && FlowFieldLayoutUtility.Approximately(left.MaxSurfaceSlope, right.MaxSurfaceSlope)
                && FlowFieldLayoutUtility.Approximately(left.MaxStepHeight, right.MaxStepHeight);
    }
}
