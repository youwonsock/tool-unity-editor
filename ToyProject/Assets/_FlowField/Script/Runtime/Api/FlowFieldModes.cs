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
    /// Selects where the immutable navigation/base field comes from.
    /// </summary>
    public enum FlowFieldBakeMode
    {
        RuntimeDynamic = 0,
        StaticBaked = 1,
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
