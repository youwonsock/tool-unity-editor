using System.Collections.Generic;
using UnityEngine;

namespace Supercent.Common.FlowField
{
    internal sealed partial class FlowFieldModifierRegistry
    {
        internal sealed class Entry
        {
            internal readonly IFlowFieldVectorModifier Modifier;
            internal readonly long RegistrationOrder;
            internal bool[] InfluenceMask;
            internal bool[] InfluenceScratch;
            internal readonly List<int> InfluenceIndices = new List<int>(64);
            internal Collider InfluenceCollider;
            internal int Priority;
            internal int Revision;
            internal bool AreaDirty = true;
            internal bool SnapshotInitialized;
            internal bool LastEnabled;
            internal bool LastActive;
            internal bool LastTrigger;
            internal Vector3 LastPosition;
            internal Quaternion LastRotation;
            internal Vector3 LastScale;
            internal Bounds LastBounds;

#if UNITY_EDITOR
            internal bool[] EditorInfluenceMask;
            internal bool[] EditorInfluenceScratch;
#endif

            internal Entry(
                IFlowFieldVectorModifier modifier,
                long registrationOrder,
                Collider influenceCollider,
                int priority,
                int revision)
            {
                Modifier = modifier;
                RegistrationOrder = registrationOrder;
                InfluenceCollider = influenceCollider;
                Priority = priority;
                Revision = revision;
            }
        }
    }
}
