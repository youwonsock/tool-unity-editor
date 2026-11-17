namespace Common.FlowField
{
    /// <summary>Reads of the published Volume3D field.</summary>
    internal sealed partial class FlowFieldVolumeBuilder
    {
        // Volume3D knows nothing newer than its published field; the shared
        // rule reads that field and the notified uncertain regions.
        public IFlowFieldLatestCells LatestCells => null;
    }
}
