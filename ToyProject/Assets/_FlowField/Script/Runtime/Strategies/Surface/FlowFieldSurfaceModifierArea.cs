using System.Collections.Generic;

namespace Common.FlowField
{
    /// <summary>The Surface2D cells one Modifier touches, kept between compositions.</summary>
    internal sealed class FlowFieldSurfaceModifierArea : IFlowFieldModifierArea
    {
        // Allocated on the first area rebuild.
        internal bool[] InfluenceMask;
        internal bool[] InfluenceScratch;
        internal List<int> InfluenceIndices;
        internal bool InfluenceIndicesBuilt;
        internal FlowFieldCellRect InfluenceRect = FlowFieldCellRect.Invalid;
        // The cells it touches must be found again before the next composition.
        internal bool AreaDirty = true;

        // Drops the references only: a composition that already began keeps
        // the mask and cell list it took and finishes with this Modifier. The
        // removal recomposes those cells in the next build.
        public void Release()
        {
            InfluenceMask = null;
            InfluenceScratch = null;
            InfluenceIndices = null;
            InfluenceIndicesBuilt = false;
            InfluenceRect = FlowFieldCellRect.Invalid;
        }
    }
}
