using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The world box an obstacle change can affect. A cell's query box reaches
    /// half a cell and the Obstacle Clearance beyond the cell, so the bounds
    /// grow by one cell and the clearance on every side. The Session, the
    /// uncertain regions, both builders and the Volume3D sweep share this rule.
    /// </summary>
    internal static class FlowFieldObstacleReach
    {
        internal static float Margin(float cellSize, float clearance) => cellSize + clearance;

        internal static Bounds Expand(Bounds bounds, float margin)
        {
            bounds.Expand(2f * margin);
            return bounds;
        }

        internal static Bounds Expand(Bounds bounds, float cellSize, float clearance)
            => Expand(bounds, Margin(cellSize, clearance));

        /// <summary>The BakedBase region a reloaded bake rescans, grown to hold <paramref name="reach"/>.</summary>
        internal static Bounds Encapsulate(Bounds? region, Bounds reach)
        {
            Bounds grown = region ?? reach;
            grown.Encapsulate(reach);
            return grown;
        }
    }
}
