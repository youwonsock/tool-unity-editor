namespace Common.FlowField
{
    /// <summary>
    /// The next-cell values of a cell that does not step to another cell. Both
    /// space modes, their solves and both bake assets use them. A cell whose
    /// next cell is itself is the Goal anchor.
    /// </summary>
    internal static class FlowFieldNextCell
    {
        // No Goal, or outside its influence: the cell follows Default Direction.
        internal const int DefaultDirection = -1;
        // A blocked cell, or a Surface2D cell without walkable ground.
        internal const int Blocked = -2;
        // Inside the Goal's influence with no path to the Goal: the cell stops.
        internal const int Unreachable = -3;
        // The lowest valid value.
        internal const int Min = Unreachable;
    }
}
