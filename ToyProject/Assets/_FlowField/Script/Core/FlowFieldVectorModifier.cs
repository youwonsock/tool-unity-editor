using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    public interface IFlowFieldVectorModifier
    {
        Collider InfluenceCollider { get; }
        int Priority { get; }
        int Revision { get; }

        FlowFieldVectorState Modify(
            in FlowFieldVectorState current,
            in FlowFieldVectorModifierContext context);
    }

    internal readonly struct FlowFieldModifierLayer
    {
        public IFlowFieldVectorModifier Modifier { get; }
        public bool[] InfluenceMask { get; }
        public IReadOnlyList<int> InfluenceIndices { get; }

        public FlowFieldModifierLayer(IFlowFieldVectorModifier modifier, bool[] influenceMask)
            : this(modifier, influenceMask, null)
        {
        }

        public FlowFieldModifierLayer(
            IFlowFieldVectorModifier modifier,
            bool[] influenceMask,
            IReadOnlyList<int> influenceIndices)
        {
            Modifier = modifier;
            InfluenceMask = influenceMask;
            InfluenceIndices = influenceIndices;
        }
    }
}
