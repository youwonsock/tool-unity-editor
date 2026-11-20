using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The header of a Volume3D bake: its format, grid, Bounds, obstacle
    /// settings and Goal. The asset, the bake writer and the validator all
    /// check it with <see cref="TryValidate"/> and <see cref="MatchesGrid"/>,
    /// so they share one set of rules and tolerances.
    /// </summary>
    internal readonly struct FlowFieldVolumeBakeMetadata
    {
        internal readonly int FormatVersion;
        internal readonly int Width;
        internal readonly int Height;
        internal readonly int Depth;
        internal readonly float CellSize;
        internal readonly Vector3 Origin;
        internal readonly Vector3 BoundsCenter;
        internal readonly Vector3 BoundsSize;
        internal readonly int ObstacleLayerMask;
        internal readonly float ObstacleClearance;
        internal readonly bool HasGoal;
        internal readonly Vector3 RequestedGoalWorld;
        internal readonly float GoalInfluenceRadius;
        internal readonly int ResolvedGoalIndex;

        internal FlowFieldVolumeBakeMetadata(
            int formatVersion,
            int width,
            int height,
            int depth,
            float cellSize,
            Vector3 origin,
            Vector3 boundsCenter,
            Vector3 boundsSize,
            int obstacleLayerMask,
            float obstacleClearance,
            bool hasGoal,
            Vector3 requestedGoalWorld,
            float goalInfluenceRadius,
            int resolvedGoalIndex)
        {
            FormatVersion = formatVersion;
            Width = width;
            Height = height;
            Depth = depth;
            CellSize = cellSize;
            Origin = origin;
            BoundsCenter = boundsCenter;
            BoundsSize = boundsSize;
            ObstacleLayerMask = obstacleLayerMask;
            ObstacleClearance = obstacleClearance;
            HasGoal = hasGoal;
            RequestedGoalWorld = requestedGoalWorld;
            GoalInfluenceRadius = goalInfluenceRadius;
            ResolvedGoalIndex = resolvedGoalIndex;
        }

        /// <summary>
        /// Whether the metadata is usable on its own: the current format,
        /// positive cell counts and sizes, finite values, an obstacle layer,
        /// a non-negative clearance, finite Goal data and a Goal index inside
        /// its own cells (-1 without a Goal).
        /// </summary>
        internal bool TryValidate(out string reason)
        {
            if (FormatVersion != FlowFieldVolumeBakeData.SupportedFormatVersion)
                return Fail(out reason, $"FlowField Volume Bake format {FormatVersion} is unsupported; ReBake is required.");
            if (Width <= 0 || Height <= 0 || Depth <= 0
                || !FlowFieldGridSpace.IsFinite(CellSize)
                || CellSize < FlowFieldLayoutUtility.MinCellSize
                || !FlowFieldGridSpace.IsFinite(Origin)
                || !FlowFieldGridSpace.IsFinite(BoundsCenter)
                || !FlowFieldGridSpace.IsFinite(BoundsSize)
                || BoundsSize.x <= 0f
                || BoundsSize.y <= 0f
                || BoundsSize.z <= 0f
                || ObstacleLayerMask == 0
                || !FlowFieldGridSpace.IsFinite(ObstacleClearance)
                || ObstacleClearance < 0f)
                return Fail(out reason, "Static Volume3D Bake metadata is invalid.");
            if (!FlowFieldGridSpace.IsFinite(RequestedGoalWorld)
                || !FlowFieldGridSpace.IsFinite(GoalInfluenceRadius)
                || GoalInfluenceRadius < 0f)
                return Fail(out reason, "Static Volume3D Bake Goal data is invalid.");
            long cellCount = (long)Width * Height * Depth;
            if (HasGoal
                    ? ResolvedGoalIndex < 0 || ResolvedGoalIndex >= cellCount
                    : ResolvedGoalIndex != -1)
                return Fail(out reason, "Static Volume3D Bake Goal index is invalid.");
            reason = string.Empty;
            return true;
        }

        /// <summary>
        /// Whether the bake was made on this grid: the same cell counts, the
        /// cell size within 0.0001, and the origin and Bounds within 0.001 of
        /// the grid's own. A NaN never matches.
        /// </summary>
        internal bool MatchesGrid(FlowFieldGridSpace grid)
        {
            Bounds gridBounds = GridBounds(grid);
            return grid.IsValid
                && grid.SpaceMode == FlowFieldSpaceMode.Volume3D
                && Width == grid.Width
                && Height == grid.Height
                && Depth == grid.Depth
                && Mathf.Abs(CellSize - grid.CellSize) <= 0.0001f
                && FlowFieldGridSpace.Approximately(Origin, grid.Origin, 0.000001d)
                && FlowFieldGridSpace.Approximately(BoundsCenter, gridBounds.center, 0.000001d)
                && FlowFieldGridSpace.Approximately(BoundsSize, gridBounds.size, 0.000001d);
        }

        /// <summary>The world Bounds the cells of a Volume3D grid cover.</summary>
        internal static Bounds GridBounds(FlowFieldGridSpace grid)
        {
            Vector3 size = new Vector3(grid.WorldSizeX, grid.WorldSizeY, grid.WorldSizeZ);
            return new Bounds(grid.Origin + size * 0.5f, size);
        }

        private static bool Fail(out string reason, string message)
        {
            reason = message;
            return false;
        }
    }
}
