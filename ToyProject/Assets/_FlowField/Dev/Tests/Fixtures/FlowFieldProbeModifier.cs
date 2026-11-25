using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A custom Modifier for the tests: it halves the speed inside its trigger
    /// and never unregisters itself, so a test decides when it goes away.
    /// </summary>
    public sealed class FlowFieldProbeModifier : MonoBehaviour, IFlowFieldVectorModifier
    {
        public Collider Influence;
        public int Priority = 4001;

        public FlowFieldModifierObservation Observe() => new FlowFieldModifierObservation(Influence, Priority, 0);

        public IFlowFieldModifierSnapshot CaptureSnapshot() => new Snapshot();

        private sealed class Snapshot : IFlowFieldModifierSnapshot
        {
            public FlowFieldVectorState Modify(in FlowFieldVectorState current, in FlowFieldVectorModifierContext context)
                => new FlowFieldVectorState(current.Direction, current.SpeedMultiplier * 0.5f);
        }
    }
}
