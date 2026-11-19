using System;
using UnityEngine;

namespace Common.FlowField
{
    public enum FlowFieldSpaceMode
    {
        Surface2D = 0,
        Volume3D = 1,
    }

    /// <summary>
    /// Selects where the navigation base field comes from.
    /// </summary>
    public enum FlowFieldBakeMode
    {
        // The surface, obstacles and Goal are computed at runtime.
        RuntimeDynamic = 0,
        // The whole baked field is loaded; the Goal and obstacles stay as baked.
        StaticBaked = 1,
        // The baked surface and obstacles are loaded; the Goal and obstacles
        // change at runtime on top of them.
        BakedBase = 2,
    }

    /// <summary>What each Bake Mode needs, so callers never list the modes.</summary>
    internal static class FlowFieldBakeModes
    {
        /// <summary>Whether the mode loads a bake asset.</summary>
        internal static bool UsesBakeAsset(FlowFieldBakeMode mode)
            => mode == FlowFieldBakeMode.StaticBaked || mode == FlowFieldBakeMode.BakedBase;
    }

    /// <summary>
    /// Observable lifecycle of a flow-field provider.  Values are explicit so
    /// serialized diagnostics and external integrations remain stable.
    /// </summary>
    public enum FlowFieldRuntimeState
    {
        Uninitialized = 0,
        Building = 1,
        Ready = 2,
        Suspended = 3,
        Faulted = 4,
        Released = 5,
    }
}
