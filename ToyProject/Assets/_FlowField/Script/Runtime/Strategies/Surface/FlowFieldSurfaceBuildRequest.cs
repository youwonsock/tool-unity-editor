namespace Common.FlowField
{
    /// <summary>
    /// What <see cref="FlowFieldSurfaceBaseFieldBuild.PrepareBase"/> reads. Grid
    /// and Surface come from the obstacle request, so the obstacle masks and
    /// the Goal preparation read the same ones.
    /// </summary>
    internal readonly struct FlowFieldSurfaceBuildRequest
    {
        internal FlowFieldGridSpace Grid => Obstacles.Grid;
        internal FlowFieldSurfaceData Surface => Obstacles.Surface;
        internal FlowFieldSurfaceObstacleRequest Obstacles { get; }
        internal FlowFieldSurfaceGoalResolution Goal { get; }
        internal bool SurfaceChanged { get; }

        internal FlowFieldSurfaceBuildRequest(
            FlowFieldSurfaceObstacleRequest obstacles,
            FlowFieldSurfaceGoalResolution goal,
            bool surfaceChanged = false)
        {
            Obstacles = obstacles;
            Goal = goal;
            SurfaceChanged = surfaceChanged;
        }
    }

    /// <summary>
    /// What base preparation changed. The navigation arrays stay in the
    /// builder's staging workspace; this result carries only the decisions.
    /// </summary>
    internal readonly struct FlowFieldSurfaceBuildResult
    {
        internal bool ObstacleMaskChanged { get; }
        internal FlowFieldCellRect ObstacleDirtyRegion { get; }
        internal bool HasWalkableSurface { get; }
        internal FlowFieldSurfaceGoalBuildStatus GoalStatus { get; }
        internal bool GoalChanged => GoalStatus == FlowFieldSurfaceGoalBuildStatus.Built
            || GoalStatus == FlowFieldSurfaceGoalBuildStatus.NoWalkableSurface
            || GoalStatus == FlowFieldSurfaceGoalBuildStatus.Invalid;

        internal FlowFieldSurfaceBuildResult(
            bool obstacleMaskChanged,
            FlowFieldCellRect obstacleDirtyRegion,
            bool hasWalkableSurface,
            FlowFieldSurfaceGoalBuildStatus goalStatus)
        {
            ObstacleMaskChanged = obstacleMaskChanged;
            ObstacleDirtyRegion = obstacleDirtyRegion;
            HasWalkableSurface = hasWalkableSurface;
            GoalStatus = goalStatus;
        }
    }
}
