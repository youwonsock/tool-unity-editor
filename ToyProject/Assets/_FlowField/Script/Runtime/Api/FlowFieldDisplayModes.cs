using System;
using UnityEngine;

namespace Common.FlowField
{
    public enum FlowFieldVolumeSliceAxis
    {
        X = 0,
        Y = 1,
        Z = 2,
    }

    /// <summary>
    /// Selects how a Volume3D field is sampled for editor visualization.
    /// This is a display-only setting and does not change the computed grid.
    /// </summary>
    public enum FlowFieldVolumeGizmoMode
    {
        FullVolume = 0,
        Slice = 1,
    }
}
