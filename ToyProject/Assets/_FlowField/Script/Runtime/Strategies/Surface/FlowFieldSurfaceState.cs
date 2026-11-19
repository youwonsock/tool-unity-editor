using System;
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
        internal bool BaseComposed;
        internal bool GoalRemoved;

        public void ExpandFinalDirty(FlowFieldCellRect rect)
            => DirtyFinalRegion = FlowFieldCellRect.Union(DirtyFinalRegion, rect);

        public void ExpandObstacleDirty(FlowFieldCellRect rect)
            => DirtyObstacleRegion = FlowFieldCellRect.Union(DirtyObstacleRegion, rect);

    }
}
