namespace Common.FlowField
{
    /// <summary>The axis a Volume3D display slice is taken across.</summary>
    public enum FlowFieldVolumeSliceAxis
    {
        /// <summary>One layer of cells at a fixed X.</summary>
        X = 0,
        /// <summary>One layer of cells at a fixed Y.</summary>
        Y = 1,
        /// <summary>One layer of cells at a fixed Z.</summary>
        Z = 2,
    }

    /// <summary>
    /// Selects how a Volume3D field is sampled for editor visualization.
    /// This is a display-only setting and does not change the computed grid.
    /// </summary>
    public enum FlowFieldVolumeGizmoMode
    {
        /// <summary>Cells across the whole volume, thinned out to stay under a display limit.</summary>
        FullVolume = 0,
        /// <summary>One layer of cells across the slice axis.</summary>
        Slice = 1,
    }
}
