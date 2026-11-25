using System;

namespace Common.FlowField
{
    /// <summary>
    /// Argument checks shared by the Surface2D obstacle and modifier mask builders.
    /// </summary>
    internal static class FlowFieldSurfaceMaskArguments
    {
        internal static void ValidateTarget(FlowFieldGridSpace grid, FlowFieldSurfaceData surface, bool[] destination)
        {
            if (!grid.IsValid)
                throw new ArgumentException("Grid is invalid.", nameof(grid));
            if (!surface.IsValid)
                throw new ArgumentException("Surface data is invalid.", nameof(surface));
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));
            if (destination.Length != grid.CellCount)
                throw new ArgumentException("Destination length must match the grid cell count.", nameof(destination));
        }

        internal static void ValidateObstacleBox(float checkHeight, float centerOffset, float clearance)
        {
            if (!FlowFieldGridSpace.IsFinite(checkHeight) || checkHeight <= 0f)
                throw new ArgumentOutOfRangeException(nameof(checkHeight));
            if (!FlowFieldGridSpace.IsFinite(centerOffset))
                throw new ArgumentOutOfRangeException(nameof(centerOffset));
            if (!FlowFieldGridSpace.IsFinite(clearance) || clearance < 0f)
                throw new ArgumentOutOfRangeException(nameof(clearance));
        }
    }
}
