using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// Value snapshot and live influence collider used by one Volume build.
    /// Ordering and change observations remain owned by the registry.
    /// </summary>
    internal readonly struct FlowFieldModifierWorkItem
    {
        internal IFlowFieldVectorModifier Modifier { get; }
        internal IFlowFieldModifierSnapshot Snapshot { get; }
        internal Collider InfluenceCollider { get; }

        internal FlowFieldModifierWorkItem(
            IFlowFieldVectorModifier modifier,
            IFlowFieldModifierSnapshot snapshot,
            Collider influenceCollider)
        {
            Modifier = modifier;
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            InfluenceCollider = influenceCollider;
        }

        /// <summary>Destroyed since the build took it.</summary>
        internal bool IsDestroyed => FlowFieldVectorUtility.IsMissingModifier(Modifier);
    }
}
