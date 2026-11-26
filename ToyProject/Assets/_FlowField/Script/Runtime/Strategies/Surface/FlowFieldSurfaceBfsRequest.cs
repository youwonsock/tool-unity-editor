namespace Common.FlowField
{
    /// <summary>
    /// What one Surface2D solve reads: the staging workspace the base stage
    /// prepared and its resolved Goal cell, or a world Goal's seeds and the
    /// per-cell portal arrays filled from them.
    /// </summary>
    internal readonly struct FlowFieldSurfaceBfsRequest
    {
        internal FlowFieldGridSpace Grid { get; }
        internal FlowFieldSurfaceData Surface { get; }
        internal FlowFieldSurfaceWorkspace Workspace { get; }
        internal int GoalX { get; }
        internal int GoalZ { get; }
        internal float GoalInfluenceRadius { get; }
        internal int GoalIndex { get; }
        internal FlowFieldGoalSeeds Seeds { get; }
        internal int[] PortalDirections { get; }
        internal int[] PortalCosts { get; }
        internal float[] PortalHeights { get; }

        internal FlowFieldSurfaceBfsRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            int goalX,
            int goalZ,
            float goalInfluenceRadius,
            int goalIndex)
        {
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            GoalX = goalX;
            GoalZ = goalZ;
            GoalInfluenceRadius = goalInfluenceRadius;
            GoalIndex = goalIndex;
            Seeds = null;
            PortalDirections = null;
            PortalCosts = null;
            PortalHeights = null;
        }

        /// <summary>
        /// A world Goal's search from its seeds, with the portal arrays already
        /// filled. <paramref name="goalCell"/> is the walkable Goal cell, or -1.
        /// </summary>
        internal FlowFieldSurfaceBfsRequest(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceData surface,
            FlowFieldSurfaceWorkspace workspace,
            FlowFieldGoalSeeds seeds,
            int goalCell,
            int[] portalDirections,
            int[] portalCosts,
            float[] portalHeights)
        {
            Grid = grid;
            Surface = surface;
            Workspace = workspace;
            GoalX = seeds.GoalX;
            GoalZ = seeds.GoalZ;
            GoalInfluenceRadius = 0f;
            GoalIndex = goalCell;
            Seeds = seeds;
            PortalDirections = portalDirections;
            PortalCosts = portalCosts;
            PortalHeights = portalHeights;
        }
    }
}
