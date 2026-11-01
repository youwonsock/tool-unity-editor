using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldSchedulerSubmitTests
    {
        private static readonly Vector3 OriginCell = new Vector3(0.5f, 0f, 0.5f);
        private static readonly Vector3 GoalA = new Vector3(2.5f, 0f, 2.5f);
        private static readonly Vector3 GoalB = new Vector3(0.5f, 0f, 2.5f);
        private static readonly FlowFieldGridSpace VolumeGrid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 3, 3, 3, 1f);

        private FlowFieldSession _session;
        private FlowFieldRegressionFixture.DeferredBackend _backend;
        private FlowFieldRegressionFixture.CountingSurfaceSource _source;
        private GameObject _obstacle;
        private GameObject _area;

        private IFlowFieldBuildOperation Operation => _session;

        [SetUp]
        public void SetUp()
        {
            _backend = new FlowFieldRegressionFixture.DeferredBackend();
            _source = FlowFieldRegressionFixture.Surface();
            _session = new FlowFieldSession(_source, _backend);
            _session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldSolvePolicy.Worker);
        }

        [TearDown]
        public void TearDown()
        {
            _session.Dispose();
            if (_obstacle != null) Object.DestroyImmediate(_obstacle);
            if (_area != null) Object.DestroyImmediate(_area);
        }

        private void Build(Vector3? goal = null)
        {
            _session.Submit(FlowFieldRegressionFixture.Request(goal));
            _session.Pump();
            if (_session.IsRebuilding) _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.IsReady, Is.True, _session.LastError);
        }

        private void Pump() => Operation.PumpInternal(FlowFieldBuildBudget.Unlimited);

        // The Modifier makes the result differ, so publishing it would raise the
        // Revision. A budget that is already spent leaves the composition unfinished.
        private void BeginComposition(Vector3? goal)
        {
            Build(goal);
            _area = new GameObject("Composition area");
            _area.layer = 27;
            _area.transform.position = new Vector3(1.5f, 0f, 1.5f);
            var collider = _area.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.size = Vector3.one * 8f;
            Physics.SyncTransforms();
            _session.RegisterModifierCommand(new FlowFieldRegressionFixture.Modifier
            {
                InfluenceCollider = collider,
                Priority = 1,
            });
            _session.Submit(FlowFieldRegressionFixture.Request(goal));
            var spent = new FlowFieldBuildBudget(0d, () => 1d);
            Operation.PumpInternal(in spent);
            Assert.That(_session.IsRebuilding, Is.True, "The composition must still be running.");
        }

        [Test]
        public void TheSchedulerDoesNotBuildAStaleConfiguration()
        {
            Build(GoalA);
            int surfaces = _source.BuildCount;
            _session.MarkConfigurationStale();

            Pump();

            Assert.That(_session.ConfigurationStale, Is.True);
            Assert.That(_session.IsReady, Is.False);
            Assert.That(_source.BuildCount, Is.EqualTo(surfaces));
        }

        [Test]
        public void AStaleConfigurationBuildsAfterItsSubmit()
        {
            Build(GoalA);
            _session.MarkConfigurationStale();
            Pump();

            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            Pump();
            if (_session.IsRebuilding) _backend.Complete(_backend.Calls.Count - 1);

            Assert.That(_session.ConfigurationStale, Is.False);
            Assert.That(_session.IsReady, Is.True, _session.LastError);
        }

        [Test]
        public void TheSchedulerHoldsDirtyInputUntilItsSubmit()
        {
            Build(GoalA);
            int solves = _backend.Calls.Count;
            _session.MarkDirty(FlowFieldDirtyFlags.Goal, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);

            Pump();
            Assert.That(_backend.Calls.Count, Is.EqualTo(solves));
            Assert.That(_session.DirtyFlags & FlowFieldDirtyFlags.Goal, Is.Not.EqualTo(FlowFieldDirtyFlags.None));

            _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            Pump();
            Assert.That(_backend.Calls.Count, Is.EqualTo(solves + 1));
        }

        [Test]
        public void AGoalChangedDuringASolveSurvivesUntilItsSubmit()
        {
            Build(GoalA);
            _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            Pump();
            int solves = _backend.Calls.Count;
            // What the Manager records while a solve keeps it from submitting.
            _session.MarkDirty(FlowFieldDirtyFlags.Goal, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);

            _backend.Complete(solves - 1);
            Pump();
            Assert.That(_session.DirtyFlags & FlowFieldDirtyFlags.Goal, Is.Not.EqualTo(FlowFieldDirtyFlags.None),
                "A build of the older request must not consume the newer Goal's dirty flag.");

            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            Pump();
            Assert.That(_backend.Calls.Count, Is.EqualTo(solves + 1));
        }

        [Test]
        public void DirectPumpIsNotHeld()
        {
            Build(GoalA);
            int solves = _backend.Calls.Count;
            _session.MarkDirty(FlowFieldDirtyFlags.DynamicObstacles | FlowFieldDirtyFlags.Goal,
                FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            Assert.That(_session.HasPendingRequest, Is.True);

            _session.Pump();

            Assert.That(_session.DirtyFlags, Is.EqualTo(FlowFieldDirtyFlags.None));
            Assert.That(_backend.Calls.Count, Is.GreaterThanOrEqualTo(solves));
        }

        [Test]
        public void HeldWorkDoesNotSpendTheFrameBudget()
        {
            Build(GoalA);
            _session.AttachRuntimeScheduler();
            _session.MarkConfigurationStale();
            Assert.That(Operation.IsWaitingForExternalCompletion, Is.True);

            var timer = Stopwatch.StartNew();
            FlowFieldBuildScheduler.PumpAll(200d);
            timer.Stop();

            Assert.That(timer.Elapsed.TotalMilliseconds, Is.LessThan(100d));
        }

        [Test]
        public void ConfigurationGoingStaleDuringACompositionDiscardsIt()
        {
            BeginComposition(GoalA);
            int revision = _session.Revision;
            _session.MarkConfigurationStale();

            Pump();

            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(_session.ConfigurationStale, Is.True);
            Assert.That(_session.IsReady, Is.False);
            Assert.That(_session.IsRebuilding, Is.False);
        }

        [Test]
        public void AGridChangeDuringACompositionDiscardsIt()
        {
            BeginComposition(GoalA);
            int revision = _session.Revision;
            _session.MarkDirty(FlowFieldDirtyFlags.Grid, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);

            Pump();

            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(_session.IsRebuilding, Is.False);
        }

        [Test]
        public void AGoalRemovedDuringACompositionDiscardsIt()
        {
            BeginComposition(GoalA);
            int revision = _session.Revision;
            _session.MarkDirty(FlowFieldDirtyFlags.Goal, FlowFieldCellRect.Invalid, FlowFieldCellRect.Invalid);
            _session.ObserveGoalRemoved();

            Pump();

            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(_session.IsRebuilding, Is.False);
        }

        [Test]
        public void ACompositionWithoutAGoalStillPublishes()
        {
            BeginComposition(null);
            int revision = _session.Revision;

            Pump();

            Assert.That(_session.Revision, Is.EqualTo(revision + 1));
            Assert.That(_session.Sample(OriginCell).SpeedMultiplier, Is.EqualTo(2f));
        }

        [Test]
        public void AViewedBlockedArrayIsNotReusedThroughASharingField()
        {
            using var volume = new FlowFieldVolumeSession();
            volume.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            void Finish(Vector3 goal, bool requery)
            {
                volume.Submit(VolumeRequest(goal), requery);
                for (int i = 0; i < 100 && volume.IsRebuilding; i++) volume.Pump(10d);
                Assert.That(volume.IsReady, Is.True, volume.LastError);
            }

            Finish(new Vector3(0.5f, 0.5f, 0.5f), false);
            Assert.That(volume.TryGetView(out _, out bool[] viewed, out _, out _), Is.True);
            // The next field shares the viewed Blocked array, and nobody viewed that field.
            Finish(new Vector3(2.5f, 0.5f, 0.5f), false);
            Finish(new Vector3(0.5f, 2.5f, 0.5f), true);
            _obstacle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _obstacle.layer = FlowFieldRegressionFixture.Layer;
            _obstacle.transform.position = Vector3.one * 1.5f;
            Physics.SyncTransforms();
            Finish(new Vector3(0.5f, 0.5f, 2.5f), true);

            var committed = (FlowFieldVolumeFieldData)typeof(FlowFieldVolumeSession)
                .GetField("_committed", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(volume);
            Assert.That(committed.Blocked, Is.Not.SameAs(viewed));
            Assert.That(viewed, Has.None.True, "A viewed array must keep the values it was handed out with.");
        }

        [Test]
        public void ARegistrationDroppedByAFaultIsReported()
        {
            using var volume = new FlowFieldVolumeSession();
            volume.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            _area = new GameObject("Dropped registration area");
            _area.layer = FlowFieldRegressionFixture.Layer;
            var collider = _area.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            var dropped = new FlowFieldRegressionFixture.Modifier { InfluenceCollider = collider, Priority = 2 };
            var failing = new FailingCaptureModifier
            {
                Collider = collider,
                OnCapture = () =>
                {
                    Assert.That(volume.RegisterModifierCommand(dropped), Is.True);
                    throw new InvalidOperationException("Intentional failure");
                },
            };
            Assert.That(volume.RegisterModifier(failing), Is.True);
            LogAssert.Expect(LogType.Warning, new Regex("Modifier 등록"));

            volume.Submit(VolumeRequest(new Vector3(0.5f, 0.5f, 0.5f)));
            volume.Pump(10d);

            Assert.That(volume.IsFaulted, Is.True);
            Assert.That(volume.ContainsModifier(dropped), Is.False);
        }

        private static FlowFieldVolumeRequest VolumeRequest(Vector3 goal) => new FlowFieldVolumeRequest(
            VolumeGrid, new Bounds(Vector3.one * 1.5f, Vector3.one * 3f), FlowFieldBakeMode.RuntimeDynamic,
            1 << FlowFieldRegressionFixture.Layer, 0f, true, goal, 0f, Vector3.forward, null);

        private sealed class FailingCaptureModifier : IFlowFieldVectorModifier
        {
            internal Collider Collider;
            internal Action OnCapture;

            public FlowFieldModifierObservation Observe() => new FlowFieldModifierObservation(Collider, 1, 0);

            public IFlowFieldModifierSnapshot CaptureSnapshot()
            {
                OnCapture?.Invoke();
                return null;
            }
        }
    }
}
