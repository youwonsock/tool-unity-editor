using System;

namespace Common.FlowField
{
    [Flags]
    internal enum FlowFieldDirtyFlags : ushort
    {
        None = 0,
        Grid = 1 << 0,
        StaticObstacles = 1 << 1,
        DynamicObstacles = 1 << 2,
        Escape = 1 << 3,
        DefaultDirection = 1 << 4,
        GoalCoarse = 1 << 5,
        GoalFine = 1 << 6,
        ModifierArea = 1 << 7,
        ModifierValue = 1 << 8,
        FinalRegion = 1 << 9,
        Obstacles = StaticObstacles | DynamicObstacles,
        Goal = GoalCoarse | GoalFine,
        All = Grid
            | StaticObstacles
            | DynamicObstacles
            | Escape
            | DefaultDirection
            | GoalCoarse
            | GoalFine
            | ModifierArea
            | ModifierValue
            | FinalRegion,
    }
}
