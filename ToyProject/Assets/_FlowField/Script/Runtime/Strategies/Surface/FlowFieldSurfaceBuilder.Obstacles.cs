using UnityEngine;

namespace Common.FlowField
{
    /// <summary>How Surface2D rebuilds the cells an obstacle change touched.</summary>
    internal sealed partial class FlowFieldSurfaceBuilder
    {
        // Registering a Collider without a Rigidbody moves it between the static
        // and dynamic masks, so the static mask has to give up or take its cells.
        private static FlowFieldSurfaceDirtyFlags ObstacleRegistrationFlags(Collider collider)
        {
            FlowFieldSurfaceDirtyFlags flags = FlowFieldSurfaceDirtyFlags.DynamicObstacles
                | FlowFieldSurfaceDirtyFlags.Escape
                | FlowFieldSurfaceDirtyFlags.FinalRegion;
            if (collider != null && collider.attachedRigidbody == null)
                flags |= FlowFieldSurfaceDirtyFlags.StaticObstacles;
            return flags;
        }

        public void ApplyObstacleChange(in FlowFieldObstacleChange change)
        {
            if (_session.BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            // Before the first build the latest request already holds the grid.
            FlowFieldGridSpace grid = LatestGrid;
            FlowFieldCellRect dirty = FlowFieldCellRect.Invalid;
            if (grid.IsValid)
            {
                if (change.HasPrevious)
                    dirty = FlowFieldCellRect.Union(dirty, FlowFieldCellRect.FromBounds(grid, change.Previous));
                if (change.HasCurrent)
                    dirty = FlowFieldCellRect.Union(dirty, FlowFieldCellRect.FromBounds(grid, change.Current));
            }

            FlowFieldSurfaceDirtyFlags flags = change.Kind == FlowFieldObstacleChangeKind.Registered
                || change.Kind == FlowFieldObstacleChangeKind.Unregistered
                ? ObstacleRegistrationFlags(change.Collider)
                : FlowFieldSurfaceDirtyFlags.DynamicObstacles
                    | FlowFieldSurfaceDirtyFlags.Escape
                    | FlowFieldSurfaceDirtyFlags.FinalRegion;
            MarkDirty(flags, dirty, dirty);
        }
    }
}
