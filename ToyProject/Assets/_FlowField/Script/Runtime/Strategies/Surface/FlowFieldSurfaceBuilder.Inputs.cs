using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>How Surface2D takes obstacle and Modifier input: the cells a change touches.</summary>
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
            if (_bakeMode == FlowFieldBakeMode.StaticBaked)
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


        public IFlowFieldModifierArea CreateModifierArea() => new FlowFieldSurfaceModifierArea();

        public void ApplyModifierChange(in FlowFieldModifierUpdate update)
        {
            EnsureModifierServices();
            switch (update.Kind)
            {
                case FlowFieldModifierUpdateKind.Registered:
                    // Rebuilding the area data reports the cells it touches.
                    MarkDirty(
                        FlowFieldSurfaceDirtyFlags.ModifierArea,
                        FlowFieldCellRect.Invalid,
                        FlowFieldCellRect.Invalid);
                    return;
                case FlowFieldModifierUpdateKind.Unregistered:
                {
                    // The cells it touched are composed again without it. One
                    // that touched no cell leaves nothing to compose: an
                    // invalid region would mean the whole grid.
                    FlowFieldCellRect touched = FlowFieldSurfaceModifierComposition.AreaOf(update.Entry).InfluenceRect;
                    if (touched.IsValid)
                        MarkDirty(FlowFieldSurfaceDirtyFlags.ModifierStack, touched, FlowFieldCellRect.Invalid);
                    return;
                }
            }
            bool area = (update.Requested & FlowFieldModifierChange.Area) != 0;
            FlowFieldModifierChangeResult result = update.Result;
            if (!area && !result.Any)
                return;
            if (area)
                FlowFieldSurfaceModifierComposition.AreaOf(update.Entry).AreaDirty = true;
            // A new priority order recomposes every touched cell.
            FlowFieldModifierRegistry registry = _session.Modifiers;
            FlowFieldCellRect dirty = FlowFieldCellRect.Invalid;
            for (int i = 0; i < registry.Entries.Count; i++)
            {
                FlowFieldModifierEntry entry = registry.Entries[i];
                if (result.PriorityChanged || ReferenceEquals(entry, update.Entry))
                    dirty = FlowFieldCellRect.Union(dirty, FlowFieldSurfaceModifierComposition.AreaOf(entry).InfluenceRect);
            }
            FlowFieldSurfaceDirtyFlags flags = FlowFieldSurfaceDirtyFlags.None;
            if (area)
                flags |= FlowFieldSurfaceDirtyFlags.ModifierArea;
            // New values on no touched cell change nothing. A moved area
            // composes what its new mask reaches with the latest values.
            if (result.Any && dirty.IsValid)
                flags |= FlowFieldSurfaceDirtyFlags.ModifierStack;
            MarkDirty(flags, dirty, FlowFieldCellRect.Invalid);
        }

        private void EnsureModifierServices()
        {
            if (_modifierComposition == null)
                throw new InvalidOperationException("FlowField modifier services are not initialized.");
        }
    }
}
