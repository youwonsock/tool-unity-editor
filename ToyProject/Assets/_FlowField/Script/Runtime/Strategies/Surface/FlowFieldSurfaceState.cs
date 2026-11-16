using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Mutable build state owned by a FlowFieldSurfaceSession.  This type deliberately
    /// contains no Unity lifecycle or callback policy; it is the data plane
    /// shared by the common runtime/editor session and the existing stage
    /// implementations.
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
        internal bool ObstacleProbePending;
        internal bool GoalRemoved;

        public void ExpandFinalDirty(FlowFieldCellRect rect)
            => DirtyFinalRegion = FlowFieldCellRect.Union(DirtyFinalRegion, rect);

        public void ExpandObstacleDirty(FlowFieldCellRect rect)
            => DirtyObstacleRegion = FlowFieldCellRect.Union(DirtyObstacleRegion, rect);

    }
}
