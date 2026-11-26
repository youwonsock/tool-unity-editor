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
        // The lowest value a field of one area holds.
        internal const int Min = Unreachable;

        // A world Goal's next step leaves the area: the code holds the step
        // direction, and the cell across the border is in a touching area.
        private const int PortalBase = -16;

        internal static int Portal(int direction) => PortalBase - direction;

        internal static bool IsPortal(int next) => next <= PortalBase && next > PortalBase - FlowFieldNeighborUtility.VolumeCount;

        internal static int PortalDirection(int next) => PortalBase - next;
    }
}
