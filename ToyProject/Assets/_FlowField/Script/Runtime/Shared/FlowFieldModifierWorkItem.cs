using System;
using System.Buffers;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Value snapshot and live influence collider used by one Volume build.
    /// Ordering and change observations remain owned by the registry.
    /// </summary>
    internal readonly struct FlowFieldModifierWorkItem
    {
        internal IFlowFieldModifierSnapshot Snapshot { get; }
        internal Collider InfluenceCollider { get; }

        internal FlowFieldModifierWorkItem(
            IFlowFieldModifierSnapshot snapshot,
            Collider influenceCollider)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            InfluenceCollider = influenceCollider;
        }
    }
}
