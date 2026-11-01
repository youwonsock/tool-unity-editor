using System;
using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldSurfaceModifierLifecycleTests
    {
        [Test]
        public void RetryOfAFaultedAreaPassSolvesTheSameGoal()
        {
            var owner = new GameObject("Surface area fault") { layer = FlowFieldRegressionFixture.Layer };
            owner.transform.position = new Vector3(1.5f, 0f, 1.5f);
            var collider = owner.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = new Vector3(3f, 2f, 3f);
            var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            var errors = new FlowFieldRegressionFixture.EventFailures(session);
            var modifier = new Modifier { InfluenceCollider = collider, Speed = 1f };
            var goal = new Vector3(2.5f, 0f, 2.5f);
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                    FlowFieldSolvePolicy.Inline);
                session.RegisterModifierCommand(modifier);
                modifier.ThrowFromCollider = true;
                session.Submit(FlowFieldRegressionFixture.Request(goal));
                session.Pump();
                Assert.That(session.IsFaulted, Is.True);
                modifier.ThrowFromCollider = false;
                session.RetryFault();
                session.Submit(FlowFieldRegressionFixture.Request(goal));
                session.Pump();
                Assert.That(session.IsReady, Is.True, session.LastError);
                Assert.That(session.Sample(new Vector3(0.5f, 0f, 0.5f)).Direction.x, Is.GreaterThan(0f));
            }
            finally
            {
                session.Dispose();
                UnityEngine.Object.DestroyImmediate(owner);
                errors.AssertNone();
            }
        }

        [Test]
        public void StaticSnapshotIdentityReloadsChangedDataEvenWithTheSameRevision()
        {
            using var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            var errors = new FlowFieldRegressionFixture.EventFailures(session);
            session.Initialize(FlowFieldBakeMode.StaticBaked, FlowFieldSessionSourceKind.StaticSnapshot,
                FlowFieldSolvePolicy.Inline);
            var first = CreateStaticSnapshot();
            var replacement = CreateStaticSnapshot(blockedIndex: 0);
            Assert.That(replacement.Revision, Is.EqualTo(first.Revision));
            int publications = 0;
            session.FieldCommitted += () => publications++;
            session.Submit(StaticRequest(first, Vector3.forward));
            session.Pump();
            Assert.That(session.Sample(new Vector3(0.5f, 0f, 0.5f)).SpeedMultiplier, Is.EqualTo(1f));
            session.Submit(StaticRequest(replacement, Vector3.forward));
            Assert.That(session.Sample(new Vector3(0.5f, 0f, 0.5f)).SpeedMultiplier, Is.EqualTo(1f),
                "A compatible source keeps the committed sample until the replacement is pumped.");
            session.Pump();
            Assert.That(session.IsReady, Is.True, session.LastError);
            Assert.That(session.Sample(new Vector3(0.5f, 0f, 0.5f)).SpeedMultiplier, Is.Zero);
            Assert.That(publications, Is.EqualTo(2));
            session.Submit(StaticRequest(replacement, Vector3.forward));
            session.Pump();
            Assert.That(publications, Is.EqualTo(2), "An unchanged immutable snapshot must not republish.");
            session.Submit(StaticRequest(replacement, Vector3.right));
            session.Pump();
            Assert.That(session.Sample(new Vector3(0.5f, 0f, 0.5f)).SpeedMultiplier, Is.Zero);
            Assert.That(session.Sample(new Vector3(1.5f, 0f, 0.5f)).Direction, Is.EqualTo(Vector3.right));
            errors.AssertNone();
        }

        private static FlowFieldStaticBakeSnapshot CreateStaticSnapshot(int blockedIndex = -1)
        {
            using var source = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            source.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldSolvePolicy.Inline);
            source.Submit(FlowFieldRegressionFixture.Request());
            source.Pump();
            Assert.That(source.IsReady, Is.True, source.LastError);
            var workspace = source.CommittedWorkspace;
            if (blockedIndex >= 0) workspace.Blocked[blockedIndex] = true;
            return new FlowFieldStaticBakeSnapshot(FlowFieldRegressionFixture.Settings, 1 << FlowFieldRegressionFixture.Layer,
                1f, 0.5f, 0f, 1, false, Vector3.zero, 0f, -1, source.CommittedSurface,
                workspace.Blocked, workspace.EscapeDirections, workspace.TopologyMasks, workspace.GoalFlags,
                workspace.GoalDirections, workspace.NextCells);
        }

        private static FlowFieldSessionRequest StaticRequest(FlowFieldStaticBakeSnapshot snapshot, Vector3 direction)
            => FlowFieldSessionRequest.ForStaticSnapshot(FlowFieldRegressionFixture.Settings, null, snapshot,
                1 << FlowFieldRegressionFixture.Layer, 1f, 0.5f, 0f, direction, FlowFieldDirtyFlags.None);

        [Test]
        public void AreaAndObstacleChangesDuringASolveBuildTheMaskOnce()
        {
            var owner = new GameObject("Surface area rebuild");
            owner.layer = FlowFieldRegressionFixture.Layer;
            owner.transform.position = new Vector3(1.5f, 0f, 1.5f);
            var collider = owner.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = new Vector3(3f, 2f, 3f);
            var backend = new FlowFieldRegressionFixture.DeferredBackend();
            var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface(), backend);
            var modifier = new Modifier { InfluenceCollider = collider };
            FlowFieldSessionRequest request = FlowFieldRegressionFixture.Request(new Vector3(2.5f, 0f, 2.5f));
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                    FlowFieldSolvePolicy.Worker);
                session.RegisterModifierCommand(modifier);
                session.Submit(request);
                session.Pump();
                backend.Complete(0);
                Assert.That(session.IsReady, Is.True, session.LastError);

                session.Submit(FlowFieldRegressionFixture.Request(new Vector3(2.5f, 0f, 0.5f)));
                session.Pump();
                Assert.That(backend.Calls.Count, Is.EqualTo(2));
                session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Area);
                session.NotifyObstacleRegionDirtyCommand(new Bounds(Vector3.one, Vector3.one));
                int readsBefore = modifier.ColliderReads;
                int solved = 1;
                for (int i = 0; i < 16; i++)
                {
                    while (solved < backend.Calls.Count)
                        backend.Complete(solved++);
                    if (session.HasPendingRequest)
                        session.Submit(FlowFieldRegressionFixture.Request(new Vector3(2.5f, 0f, 0.5f)));
                    session.Pump();
                }
                Assert.That(session.IsReady, Is.True, session.LastError);
                Assert.That(modifier.ColliderReads - readsBefore, Is.EqualTo(1));
            }
            finally
            {
                session.DisposePermanently();
                UnityEngine.Object.DestroyImmediate(owner);
            }
        }

        private sealed class Modifier : IFlowFieldVectorModifier, IFlowFieldModifierSnapshot
        {
            private Collider _collider;
            public Collider InfluenceCollider
            {
                get
                {
                    ColliderReads++;
                    return ThrowFromCollider ? throw new InvalidOperationException("Expected area failure") : _collider;
                }
                set => _collider = value;
            }
            public int Priority => 0;
            public int Revision => 0;
            public FlowFieldModifierObservation Observe() => new FlowFieldModifierObservation(InfluenceCollider, Priority, Revision);
            internal bool ThrowFromCollider;
            internal int ColliderReads;
            internal float Speed = 2f;
            internal Action OnCapture;
            internal Action OnModify;
            public IFlowFieldModifierSnapshot CaptureSnapshot() { OnCapture?.Invoke(); return this; }
            public FlowFieldVectorState Modify(in FlowFieldVectorState current, in FlowFieldVectorModifierContext context)
            {
                OnModify?.Invoke();
                return new FlowFieldVectorState(current.Direction, current.SpeedMultiplier * Speed);
            }
        }
    }
}
