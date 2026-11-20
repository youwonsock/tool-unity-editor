using System;

namespace Common.FlowField
{
    /// <summary>
    /// What a Surface2D build must redo. Every flag but FinalRegion names a
    /// cause; FinalRegion is always derived from one of them, so a stage that
    /// finds its own cause changed nothing may drop FinalRegion only when no
    /// other cause is left.
    /// </summary>
    [Flags]
    internal enum FlowFieldSurfaceDirtyFlags : ushort
    {
        None = 0,
        Grid = 1 << 0,
        StaticObstacles = 1 << 1,
        DynamicObstacles = 1 << 2,
        Escape = 1 << 3,
        DefaultDirection = 1 << 4,
        Goal = 1 << 5,
        ModifierArea = 1 << 6,
        // The Modifiers over some cells changed: a value, the priority order,
        // or one left.
        ModifierStack = 1 << 7,
        FinalRegion = 1 << 8,
        // Scan the ground again. Unlike Grid the cells keep their layout, so
        // a solve or composition already running still publishes; a ground
        // that really changed then rebuilds like a new grid.
        Ground = 1 << 9,
        Obstacles = StaticObstacles | DynamicObstacles,
        All = Grid | StaticObstacles | DynamicObstacles | Escape
            | DefaultDirection | Goal | ModifierArea | ModifierStack | FinalRegion | Ground,
    }
}
