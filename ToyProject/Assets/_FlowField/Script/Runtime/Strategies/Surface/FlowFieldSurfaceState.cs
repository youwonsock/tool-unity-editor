using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The mutable build state of a <see cref="FlowFieldSurfaceBuilder"/>,
    /// which its stages share. It holds no lifecycle or callback policy.
    /// </summary>
    internal sealed class FlowFieldSurfaceState
    {
        internal FlowFieldGridSpace Grid;
        internal FlowFieldSurfaceData Surface;
        internal readonly FlowFieldSurfaceWorkspace Workspace = new FlowFieldSurfaceWorkspace();
        internal FlowFieldSurfaceDirtyFlags DirtyFlags = FlowFieldSurfaceDirtyFlags.All;
        internal FlowFieldCellRect DirtyFinalRegion = FlowFieldCellRect.Invalid;
        internal FlowFieldCellRect DirtyObstacleRegion = FlowFieldCellRect.Invalid;
        internal FlowFieldCellRect LastComposedRegion = FlowFieldCellRect.Invalid;
        internal Vector3 ResolvedDefaultDirection = Vector3.zero;
        internal bool SurfaceReady => Surface != null && Surface.IsValid;
        internal bool HasObstacleMask;
        // BakedBase: every region an obstacle change reached since Initialize.
        // A reloaded bake scans these again, because the bake still holds
        // what was there before the changes.
        internal Bounds? BakedBaseTouched;
        internal bool BaseComposed;
        // The owner removed the runtime Goal, so a build still composing for
        // the old one must not publish. A StaticBaked field keeps its baked
        // Goal and never sets it.
        internal bool GoalRemoved;

        public void ExpandFinalDirty(FlowFieldCellRect rect)
            => DirtyFinalRegion = FlowFieldCellRect.Union(DirtyFinalRegion, rect);

        public void ExpandObstacleDirty(FlowFieldCellRect rect)
            => DirtyObstacleRegion = FlowFieldCellRect.Union(DirtyObstacleRegion, rect);

    }
}
