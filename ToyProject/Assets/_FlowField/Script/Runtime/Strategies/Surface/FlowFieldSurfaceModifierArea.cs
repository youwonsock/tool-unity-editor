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

        public void Release()
        {
            InfluenceMask = null;
            InfluenceScratch = null;
            InfluenceIndices?.Clear();
            InfluenceIndicesBuilt = false;
            InfluenceRect = FlowFieldCellRect.Invalid;
        }
    }

    /// <summary>What a Modifier change makes Surface2D compose again.</summary>
    internal readonly struct FlowFieldSurfaceModifierRegistryResult
    {
        public bool AreaDirty { get; }
        public bool ValueDirty { get; }
        public bool FinalDirty { get; }
        public FlowFieldCellRect DirtyRegion { get; }

        public FlowFieldSurfaceModifierRegistryResult(
            bool areaDirty,
            bool valueDirty,
            bool finalDirty,
            FlowFieldCellRect dirtyRegion)
        {
            AreaDirty = areaDirty;
            ValueDirty = valueDirty;
            FinalDirty = finalDirty;
            DirtyRegion = dirtyRegion;
        }
    }
}
