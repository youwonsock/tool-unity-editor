using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A registration command that runs at once must not pay for the closure only
    /// a deferred one needs. The session does nothing itself, because registering
    /// allocates in the real ones, so an allocation can only come from the command.
    /// </summary>
    public sealed class FlowFieldCommandAllocationTests
    {
        [Test]
        public void CommandsOutsideModifierCodeDoNotAllocate(
            [Values("register modifier", "unregister modifier", "notify modifier", "register obstacle", "unregister obstacle")]
            string command)
        {
            var session = new IdleSession();
            Run(session, command);
            Assert.That(() => Run(session, command), Is.Not.AllocatingGCMemory());
            Assert.That(session.Calls, Is.EqualTo(2));
        }

        private static void Run(FlowFieldSessionBase session, string command)
        {
            switch (command)
            {
                case "register modifier": session.RegisterModifierCommand(null); break;
                case "unregister modifier": session.UnregisterModifierCommand(null); break;
                case "notify modifier": session.NotifyModifierChangedCommand(null, FlowFieldModifierChange.None); break;
                case "register obstacle": session.RegisterObstacleCommand(null); break;
                case "unregister obstacle": session.UnregisterObstacleCommand(null); break;
            }
        }

        private sealed class IdleSession : FlowFieldSessionBase
        {
            internal int Calls;

            internal IdleSession() : base(null) { }

            private protected override bool RegisterModifierNow(IFlowFieldVectorModifier modifier) => Called();
            private protected override bool UnregisterModifierNow(IFlowFieldVectorModifier modifier) => Called();
            private protected override void NotifyModifierChangedNow(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change) => Called();
            private protected override bool RegisterObstacleNow(Collider collider) => Called();
            private protected override bool UnregisterObstacleNow(Collider collider) => Called();

            private bool Called()
            {
                Calls++;
                return true;
            }

            internal override FlowFieldSpaceMode SpaceMode => default;
            internal override bool IsReady => false;
            internal override bool HasPendingRequest => false;
            internal override bool IsWaitingForExternalCompletion => false;
            internal override bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
            {
                sample = default;
                return false;
            }
            internal override FlowFieldSample Sample(Vector3 worldPosition) => default;
            internal override FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition) => default;
            internal override bool TryGetFieldInfo(out FlowFieldFieldInfo info)
            {
                info = default;
                return false;
            }
            internal override void NotifyObstacleRegionDirtyCommand(Bounds worldBounds) { }
            internal override void PumpInternal(in FlowFieldBuildBudget budget) { }
            internal override void Suspend() { }
            internal override void Release() { }
            internal override void RetryFault() { }
            private protected override void AbandonWorkForFault() { }
            private protected override void ApplyFaultedState() { }
            public override void Dispose() { }
        }
    }
}
