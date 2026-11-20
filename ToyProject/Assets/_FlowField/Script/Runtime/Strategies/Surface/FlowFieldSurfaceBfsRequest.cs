namespace Common.FlowField
{
    /// <summary>
    /// What one Surface2D solve reads: the staging workspace the base stage
    /// prepared and its resolved Goal cell.
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
        }
    }
}
