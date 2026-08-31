using UnityEngine;

namespace Common.FlowField
{
    internal sealed class FlowFieldRuntimeContext
    {
        public FlowFieldGridSpace Grid;
        public FlowFieldSurfaceBakeData Surface;
        public FlowFieldStaticObstacleBakeData StaticObstacles;
        public FlowFieldCoarseTopologyData CoarseTopology;
        public readonly FlowFieldWorkspace Workspace = new FlowFieldWorkspace();
        public FlowFieldDirtyFlags DirtyFlags = FlowFieldDirtyFlags.All;
        public FlowFieldCellRect DirtyFinalRegion = FlowFieldCellRect.Invalid;
        public FlowFieldCellRect DirtyObstacleRegion = FlowFieldCellRect.Invalid;
        public Vector3 ResolvedDefaultDirection = Vector3.zero;
        public bool SurfaceReady;
        public bool HasObstacleMask;
        public int LastSurfaceRevision = -1;
        public int LastStaticObstacleRevision = -1;
        public int LastCoarseRevision = -1;

        public void MarkDirty(FlowFieldDirtyFlags flags)
            => DirtyFlags |= flags;

        public void ExpandFinalDirty(FlowFieldCellRect rect)
            => DirtyFinalRegion = FlowFieldCellRect.Union(DirtyFinalRegion, rect);

        public void ExpandObstacleDirty(FlowFieldCellRect rect)
            => DirtyObstacleRegion = FlowFieldCellRect.Union(DirtyObstacleRegion, rect);

        public void Release()
            => Workspace.Release();
    }
}
