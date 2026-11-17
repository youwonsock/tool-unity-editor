namespace Common.FlowField
{
    /// <summary>Step sizes the main-thread stages of a Volume3D build share.</summary>
    internal static class FlowFieldVolumeNavigation
    {
        internal const int CellsPerStep = 64;
        internal const int ArrayElementsPerStep = 4096;
    }
}
