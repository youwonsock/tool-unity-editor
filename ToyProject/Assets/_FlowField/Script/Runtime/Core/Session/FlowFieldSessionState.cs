using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Mutable build state owned by a FlowFieldSession.  This type deliberately
    /// contains no Unity lifecycle or callback policy; it is the data plane
    /// shared by the common runtime/editor session and the existing stage
    /// implementations.
    /// </summary>
    internal sealed class FlowFieldSessionState
    {
        internal FlowFieldGridSpace Grid;
        internal FlowFieldSurfaceData Surface;
        internal readonly FlowFieldWorkspace Workspace = new FlowFieldWorkspace();
        internal FlowFieldDirtyFlags DirtyFlags = FlowFieldDirtyFlags.All;
        internal FlowFieldCellRect DirtyFinalRegion = FlowFieldCellRect.Invalid;
        internal FlowFieldCellRect DirtyObstacleRegion = FlowFieldCellRect.Invalid;
        internal FlowFieldCellRect LastComposedRegion = FlowFieldCellRect.Invalid;
        internal Vector3 ResolvedDefaultDirection = Vector3.zero;
        internal bool SurfaceReady => Surface != null && Surface.IsValid;
        internal bool HasObstacleMask;
        internal bool BaseComposed;
        internal bool ConfigurationStale;
        internal bool ObstacleProbePending;
        // A Grid change marked by the owner before its validated Submit.
        internal bool GridMarkedUnsubmitted;
        internal bool GoalRemoved;

        public void ExpandFinalDirty(FlowFieldCellRect rect)
            => DirtyFinalRegion = FlowFieldCellRect.Union(DirtyFinalRegion, rect);

        public void ExpandObstacleDirty(FlowFieldCellRect rect)
            => DirtyObstacleRegion = FlowFieldCellRect.Union(DirtyObstacleRegion, rect);

    }
}
