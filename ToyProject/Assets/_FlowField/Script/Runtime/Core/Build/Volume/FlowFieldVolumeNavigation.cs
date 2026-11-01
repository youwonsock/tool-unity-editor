using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Grid arithmetic shared by the main-thread stages of a Volume3D build.</summary>
    internal static class FlowFieldVolumeNavigation
    {
        internal const int CellsPerStep = 64;
        internal const int ArrayElementsPerStep = 4096;

        internal static Vector3 CellCenter(FlowFieldGridSpace grid, int index)
        {
            grid.FromFlatIndex(index, out int x, out int y, out int z);
            return grid.LocalToWorldCenter(x, y, z);
        }
    }
}
