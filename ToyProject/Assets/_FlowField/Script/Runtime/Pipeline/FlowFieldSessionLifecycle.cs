using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    internal enum FlowFieldSessionLifecycle
    {
        Uninitialized,
        Active,
        Building,
        Suspended,
        Faulted,
        Released,
    }
}
