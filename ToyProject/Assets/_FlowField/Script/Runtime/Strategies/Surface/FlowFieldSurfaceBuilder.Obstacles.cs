using UnityEngine;

namespace Common.FlowField
{
    /// <summary>How Surface2D rebuilds the cells an obstacle change touched.</summary>
    internal sealed partial class FlowFieldSurfaceBuilder
    {
        // The static mask rescans the region of a change when it can hold the
        // obstacle: a registration of a Collider without a Rigidbody moves it
        // between the static and dynamic masks, a region notification can
        // move any Collider, and in BakedBase the static mask starts as the
        // bake, which still holds every obstacle where it was baked.
        private FlowFieldSurfaceDirtyFlags ObstacleChangeFlags(in FlowFieldObstacleChange change)
        {
            FlowFieldSurfaceDirtyFlags flags = FlowFieldSurfaceDirtyFlags.DynamicObstacles
                | FlowFieldSurfaceDirtyFlags.Escape
                | FlowFieldSurfaceDirtyFlags.FinalRegion;
            bool registration = change.Kind == FlowFieldObstacleChangeKind.Registered
                || change.Kind == FlowFieldObstacleChangeKind.Unregistered;
            if (_sourceKind == FlowFieldSurfaceSourceKind.BakedBase
                || change.Kind == FlowFieldObstacleChangeKind.RegionDirty
                || registration && change.Collider != null && change.Collider.attachedRigidbody == null)
                flags |= FlowFieldSurfaceDirtyFlags.StaticObstacles;
            return flags;
        }

        public void ApplyObstacleChange(in FlowFieldObstacleChange change)
        {
            if (_session.BakeMode == FlowFieldBakeMode.StaticBaked)
                return;
            // Before the first build the latest request already holds the grid.
            FlowFieldGridSpace grid = LatestGrid;
            float clearance = _requests.HasLatest ? _requests.Latest.ObstacleClearance : 0f;
            FlowFieldCellRect dirty = FlowFieldCellRect.Invalid;
            if (grid.IsValid)
            {
                if (change.HasPrevious)
                    dirty = FlowFieldCellRect.Union(dirty, Touch(grid, change.Previous, clearance));
                if (change.HasCurrent)
                    dirty = FlowFieldCellRect.Union(dirty, Touch(grid, change.Current, clearance));
            }
            MarkDirty(ObstacleChangeFlags(in change), dirty, dirty);
        }

        // The cells the bounds can block, recorded for a BakedBase reload.
        private FlowFieldCellRect Touch(FlowFieldGridSpace grid, Bounds bounds, float clearance)
        {
            Bounds reach = FlowFieldObstacleReach.Expand(bounds, grid.CellSize, clearance);
            if (_sourceKind == FlowFieldSurfaceSourceKind.BakedBase)
                _state.BakedBaseTouched = FlowFieldObstacleReach.Encapsulate(_state.BakedBaseTouched, reach);
            return FlowFieldCellRect.FromBounds(grid, reach);
        }
    }
}
