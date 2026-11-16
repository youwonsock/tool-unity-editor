using System;
using Unity.Jobs;
using UnityEngine;

namespace Common.FlowField
{
    [Flags]
    internal enum FlowFieldGoalFlags : byte
    {
        None = 0,
        Directed = 1 << 0,
        Anchor = 1 << 1,
        Unreachable = 1 << 2,
    }
}
