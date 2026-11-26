using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The staging data of a <see cref="FlowFieldSurfaceBuilder"/>, which its
    /// stages share: the grid, the surface, the workspace and what was
    /// composed into it. The input a build owes is in
    /// <see cref="FlowFieldSurfaceInputLedger"/>.
    /// </summary>
    internal sealed class FlowFieldSurfaceState
    {
        internal FlowFieldGridSpace Grid;
        internal FlowFieldSurfaceData Surface;
        internal readonly FlowFieldSurfaceWorkspace Workspace = new FlowFieldSurfaceWorkspace();
        internal FlowFieldCellRect LastComposedRegion = FlowFieldCellRect.Invalid;
        internal Vector3 ResolvedDefaultDirection = Vector3.zero;
        internal bool SurfaceReady => Surface != null && Surface.IsValid;
        internal bool HasObstacleMask;
        internal bool BaseComposed;
    }
}
