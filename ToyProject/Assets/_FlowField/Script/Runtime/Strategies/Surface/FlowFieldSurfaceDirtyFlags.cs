using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
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
        ModifierValue = 1 << 7,
        FinalRegion = 1 << 8,
        Obstacles = StaticObstacles | DynamicObstacles,
        All = Grid | StaticObstacles | DynamicObstacles | Escape
            | DefaultDirection | Goal | ModifierArea | ModifierValue | FinalRegion,
    }
}
