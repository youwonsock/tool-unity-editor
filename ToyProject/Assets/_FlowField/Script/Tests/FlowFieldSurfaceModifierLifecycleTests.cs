using System;
using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldSurfaceModifierLifecycleTests
    {
        [TestCase("capture", "none")]
        [TestCase("modify", "none")]
        [TestCase("capture", "fault")]
        [TestCase("modify", "fault")]
        [TestCase("capture", "retry")]
        [TestCase("modify", "retry")]
        public void ModifierLifecycleBoundaryStopsOldPublication(string phase, string transition)
        {
            var owner = new GameObject("Surface modifier review");
            owner.layer = FlowFieldRegressionFixture.Layer;
            owner.transform.position = new Vector3(1.5f, 0f, 1.5f);
            var collider = owner.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = new Vector3(3f, 2f, 3f);
            var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            var errors = new FlowFieldRegressionFixture.EventFailures(session);
            var modifier = new Modifier { InfluenceCollider = collider };
            int publications = 0;
            bool entered = false;
            session.FieldCommitted += () => publications++;
            Action action = () =>
            {
                if (entered) return;
                entered = true;
                if (transition == "none") return;
                session.ReportFault(new InvalidOperationException("Surface modifier lifecycle boundary"));
                if (transition == "retry")
                {
                    session.RetryFault();
                    session.Submit(FlowFieldRegressionFixture.Request(new Vector3(2.5f, 0f, 0.5f)));
                }
            };
            if (phase == "capture") modifier.OnCapture = action; else modifier.OnModify = action;
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                    FlowFieldBfsBackendPolicy.ManagedOnly, null);
                session.RegisterModifierCommand(modifier);
                session.Submit(FlowFieldRegressionFixture.Request());
                session.Pump();
                Assert.That(entered, Is.True);
                Assert.That(publications, Is.EqualTo(transition == "none" ? 1 : 0),
                    "The cancelled Surface composition must not publish after its modifier changes lifetime.");
                if (transition == "retry")
                {
                    session.Pump();
                    Assert.That(session.TryGetFieldInfo(out var info), Is.True, session.LastError);
                    Assert.That(info.ResolvedGoalWorld, Is.EqualTo(new Vector3(2.5f, 0f, 0.5f)));
                }
            }
            finally
            {
                session.Dispose();
                UnityEngine.Object.DestroyImmediate(owner);
                errors.AssertNone();
            }
        }

        [Test]
        public void CompositionBoundaryPreservesLifetimeAndNextRequest(
            [Values("scene", "static", "bfs")] string path,
            [Values("capture", "modify")] string phase,
            [Values("none", "fault", "retry", "suspend", "resume", "release", "restart", "dispose", "retry-throw", "restart-throw")] string transition)
        {
            var owner = new GameObject("Surface composition lifecycle");
            owner.layer = FlowFieldRegressionFixture.Layer;
            owner.transform.position = new Vector3(1.5f, 0f, 1.5f);
            var collider = owner.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = new Vector3(3f, 2f, 3f);
            var backend = new FlowFieldRegressionFixture.DeferredBackend();
            var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface(), backend);
            var errors = new FlowFieldRegressionFixture.EventFailures(session);
            var modifier = new Modifier { InfluenceCollider = collider };
            var snapshot = CreateStaticSnapshot();
            bool isStatic = path == "static";
            bool isBfs = path == "bfs";
            var goal = new Vector3(2.5f, 0f, 2.5f);
            var nextGoal = new Vector3(2.5f, 0f, 0.5f);
            FlowFieldSessionRequest Request(bool next) => isStatic
                ? StaticRequest(snapshot, next ? Vector3.right : Vector3.forward)
                : FlowFieldRegressionFixture.Request(isBfs ? (Vector3?)(next ? nextGoal : goal) : null,
                    next ? Vector3.right : Vector3.forward);
            void Initialize() => session.Initialize(
                isStatic ? FlowFieldBakeMode.StaticBaked : FlowFieldBakeMode.RuntimeDynamic,
                isStatic ? FlowFieldSessionSourceKind.StaticSnapshot : FlowFieldSessionSourceKind.SceneBuild,
                isBfs ? FlowFieldBfsBackendPolicy.RequireGpu : FlowFieldBfsBackendPolicy.ManagedOnly, null);
            int publications = 0, failures = 0, captures = 0, modifications = 0;
            int nestedPublications = -1;
            bool entered = false;
            session.FieldCommitted += () => publications++;
            session.Failed += _ => failures++;
            Action transitionAction = () =>
            {
                if (entered) return;
                entered = true;
                if (transition == "none") return;
                bool retry = transition.StartsWith("retry");
                bool restart = transition.StartsWith("restart");
                if (transition == "fault" || retry)
                {
                    session.ReportFault(new InvalidOperationException("Expected modifier fault"));
                    if (retry) session.RetryFault();
                }
                else if (transition == "suspend" || transition == "resume")
                {
                    session.Suspend();
                    if (transition == "resume") Initialize();
                }
                else if (transition == "dispose") session.Dispose();
                else
                {
                    session.Release();
                    if (restart)
                    {
                        Initialize();
                        session.RegisterModifierCommand(modifier);
                    }
                }
                if (retry || restart || transition == "resume")
                {
                    session.Submit(Request(true));
                    session.Pump(); // Accepted work must wait until the old callback unwinds.
                    nestedPublications = publications;
                }
                if (transition.EndsWith("-throw"))
                    throw new InvalidOperationException("Exception from the cancelled modifier");
            };
            try
            {
                Initialize();
                session.RegisterModifierCommand(modifier);
                session.Submit(Request(false));
                if (isBfs)
                {
                    session.Pump();
                    Assert.That(backend.Calls.Count, Is.EqualTo(1));
                    Assert.That(publications, Is.Zero);
                }
                modifier.OnCapture = () => { captures++; if (phase == "capture") transitionAction(); };
                modifier.OnModify = () => { modifications++; if (phase == "modify") transitionAction(); };
                if (isBfs) backend.Complete(0); else session.Pump();
                Assert.That(entered, Is.True, "The intended composition boundary must execute.");
                Assert.That(publications, Is.EqualTo(transition == "none" ? 1 : 0));
                Assert.That(failures, Is.EqualTo(transition == "fault" || transition.StartsWith("retry") ? 1 : 0),
                    "A late exception must not fault the replacement lifetime.");
                if (transition == "none")
                {
                    Assert.That(session.IsReady, Is.True, session.LastError);
                    Assert.That(session.Sample(new Vector3(0.5f, 0f, 0.5f)).SpeedMultiplier, Is.EqualTo(2f));
                    return;
                }
                Assert.That(captures, Is.EqualTo(1));
                Assert.That(modifications, Is.EqualTo(phase == "modify" ? 1 : 0),
                    "Cancellation must stop before modifying another cell.");
                Assert.That(session.IsReady, Is.False);
                if (transition.StartsWith("retry") || transition.StartsWith("restart") || transition == "resume")
                {
                    Assert.That(nestedPublications, Is.Zero);
                    session.Pump();
                    if (isBfs) backend.Complete(backend.Calls.Count - 1);
                    Assert.That(publications, Is.EqualTo(1), "The next accepted request must publish exactly once.");
                    Assert.That(session.IsReady, Is.True, session.LastError);
                    Assert.That(session.Sample(new Vector3(0.5f, 0f, 0.5f)).SpeedMultiplier, Is.EqualTo(2f));
                    if (isBfs)
                    {
                        Assert.That(session.TryGetFieldInfo(out var info), Is.True);
                        Assert.That(info.ResolvedGoalWorld, Is.EqualTo(nextGoal));
                    }
                    else Assert.That(session.Sample(new Vector3(0.5f, 0f, 0.5f)).Direction, Is.EqualTo(Vector3.right));
                }
                else
                {
                    var expected = transition == "fault" ? FlowFieldRuntimeState.Faulted
                        : transition == "suspend" ? FlowFieldRuntimeState.Suspended : FlowFieldRuntimeState.Released;
                    Assert.That(session.State, Is.EqualTo(expected));
                    session.Pump();
                    Assert.That(publications, Is.Zero);
                }
                if (isStatic) Assert.That(backend.Calls, Is.Empty, "StaticBaked must never dispatch BFS.");
            }
            finally
            {
                session.Dispose();
                UnityEngine.Object.DestroyImmediate(owner);
                errors.AssertNone();
            }
        }

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
                    FlowFieldBfsBackendPolicy.ManagedOnly, null);
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
        public void CancelledModifyDoesNotWriteEvenWhenItsReturnedValueIsInvalid(
            [Values(false, true)] bool regional,
            [Values(false, true)] bool indexed)
        {
            using var source = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            source.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldBfsBackendPolicy.ManagedOnly, null);
            source.Submit(FlowFieldRegressionFixture.Request());
            source.Pump();
            var workspace = source.CommittedWorkspace;
            bool current = true;
            int calls = 0, laterCalls = 0;
            var modifier = new Modifier { OnModify = () => { calls++; current = false; }, Speed = float.NaN };
            var later = new Modifier { OnModify = () => laterCalls++ };
            var mask = new bool[FlowFieldRegressionFixture.Grid.CellCount];
            for (int i = 0; i < mask.Length; i++) mask[i] = true;
            var layers = new[]
            {
                new FlowFieldModifierLayer(modifier, mask, indexed ? new[] { 0, 1, 2 } : null),
                new FlowFieldModifierLayer(later, mask)
            };
            bool completed = regional
                ? FlowFieldFinalFieldComposer.ApplyModifiers(FlowFieldRegressionFixture.Grid, source.CommittedSurface,
                    workspace, layers, FlowFieldCellRect.Full(FlowFieldRegressionFixture.Grid), () => current)
                : FlowFieldFinalFieldComposer.Compose(FlowFieldRegressionFixture.Grid, source.CommittedSurface,
                    workspace, Vector3.forward, layers, () => current);
            Assert.That(completed, Is.False);
            Assert.That(calls, Is.EqualTo(1));
            Assert.That(laterCalls, Is.Zero);
            for (int i = 0; i < mask.Length; i++)
            {
                Assert.That(workspace.FinalDirections[i], Is.EqualTo(Vector3.forward));
                Assert.That(workspace.FinalSpeedMultipliers[i], Is.EqualTo(1f));
                Assert.That(workspace.ModifierInfluence[i], Is.False);
            }
        }

        [Test]
        public void StaticSnapshotIdentityReloadsChangedDataEvenWithTheSameRevision()
        {
            using var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface());
            var errors = new FlowFieldRegressionFixture.EventFailures(session);
            session.Initialize(FlowFieldBakeMode.StaticBaked, FlowFieldSessionSourceKind.StaticSnapshot,
                FlowFieldBfsBackendPolicy.ManagedOnly, null);
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
                FlowFieldBfsBackendPolicy.ManagedOnly, null);
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
                1 << FlowFieldRegressionFixture.Layer, 1f, 0.5f, 0f, direction, FlowFieldDirtyFlags.None, 1024);

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
                    FlowFieldBfsBackendPolicy.RequireGpu, null);
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
