using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldSurfaceRegressionTests
    {
        private FlowFieldSession _session;
        private FlowFieldRegressionFixture.EventFailures _eventFailures;
        private FlowFieldRegressionFixture.DeferredBackend _backend;
        private FlowFieldRegressionFixture.CountingSurfaceSource _source;
        private GameObject _colliderObject;
        private static readonly Vector3 OriginCell = new Vector3(0.5f, 0f, 0.5f);
        private static readonly Vector3 GoalA = new Vector3(2.5f, 0f, 2.5f);
        private static readonly Vector3 GoalB = new Vector3(0.5f, 0f, 2.5f);

        [SetUp]
        public void SetUp()
        {
            _backend = new FlowFieldRegressionFixture.DeferredBackend();
            _source = FlowFieldRegressionFixture.Surface();
            _session = new FlowFieldSession(_source, _backend);
            _eventFailures = new FlowFieldRegressionFixture.EventFailures(_session);
            _session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldBfsBackendPolicy.PreferGpu, null);
        }

        [TearDown]
        public void TearDown()
        {
            _session?.Dispose();
            if (_colliderObject != null) Object.DestroyImmediate(_colliderObject);
            _eventFailures?.AssertNone();
        }

        private void Build(Vector3? goal = null)
        {
            _session.Submit(FlowFieldRegressionFixture.Request(goal));
            _session.Pump();
            if (_session.IsRebuilding) _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.IsReady, Is.True, _session.LastError);
        }

        [Test]
        public void SubmissionDoesNotCalculateUntilOwnerPumps()
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            Assert.That(_source.BuildCount, Is.Zero);
            Assert.That(_backend.Calls, Is.Empty);
            _session.Pump();
            Assert.That(_backend.Calls.Count, Is.EqualTo(1));
        }

        [Test]
        public void InitialFieldIsUnavailableUntilBfsCompletes()
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(_session.IsRebuilding, Is.True);
            Assert.That(_session.TrySample(OriginCell, out _), Is.False);
            _backend.Complete(0);
            Assert.That(_session.TrySample(OriginCell, out var sample), Is.True);
            Assert.That(sample.Direction.x, Is.GreaterThan(0f));
            Assert.That(sample.Direction.z, Is.GreaterThan(0f));
        }

        [Test]
        public void CompatiblePublicationRemainsReadableWhileGoalRebuilds()
        {
            Build();
            int revision = _session.Revision;
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(_session.IsRebuilding && _session.IsReady, Is.True);
            Assert.That(_session.TrySample(OriginCell, out var previous), Is.True);
            Assert.That(previous.Direction, Is.EqualTo(Vector3.forward));
            Assert.That(_session.Revision, Is.EqualTo(revision));
            _backend.Complete(0);
            Assert.That(_session.Revision, Is.GreaterThan(revision));
        }

        [Test]
        public void SupersededGoalResultCannotPublishOverLatestRequest()
        {
            Build();
            int revision = _session.Revision;
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            _backend.Complete(0);
            Assert.That(_session.Revision, Is.EqualTo(revision));
            _session.Pump();
            _backend.Complete(1);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalB));
            Assert.That(_session.Sample(OriginCell).Direction, Is.EqualTo(Vector3.forward));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReleasedOrSuspendedSessionIgnoresLateCompletion(bool release)
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            if (release) _session.Release(); else _session.Suspend();
            int revision = _session.Revision;
            int publications = 0;
            _session.FieldCommitted += () => publications++;
            Assert.DoesNotThrow(() => _backend.Complete(0, false));
            Assert.That(_session.IsReady, Is.False);
            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(publications, Is.Zero);
        }

        [Test]
        public void StaleBackendFailureCannotFaultTheNewerGoalRequest()
        {
            Build();
            int revision = _session.Revision;
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            _backend.Fail(0);
            Assert.That(_session.IsFaulted, Is.False);
            Assert.That(_session.Revision, Is.EqualTo(revision));
            _session.Pump();
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalB));
        }

        [Test]
        public void PublicationListenerCanReleaseWithoutOldCompletionReactivatingSession()
        {
            _session.FieldCommitted += () => _session.Release();
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _backend.Complete(0);
            Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Released));
            Assert.That(_session.TrySample(OriginCell, out _), Is.False);
        }

        [Test]
        public void UnrecoverableBackendFailureFaultsWithoutPublishingPartialData()
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _backend.Fail(0);
            Assert.That(_session.IsFaulted, Is.True);
            Assert.That(_session.IsReady, Is.False);
            Assert.That(_session.Revision, Is.Zero);
            Assert.That(_session.LastError, Does.Contain("Simulated GPU failure"));
        }

        [Test]
        public void IdenticalGoalDoesNotRebuildOrRepublish()
        {
            Build(GoalA);
            int revision = _session.Revision;
            int publications = 0;
            _session.FieldCommitted += () => publications++;
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(_backend.Calls.Count, Is.EqualTo(1));
            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(publications, Is.Zero);
        }

        [Test]
        public void ModifierValueChangeReusesNavigationAndChangesOnlySpeed()
        {
            Build(GoalA);
            var before = _session.Sample(OriginCell);
            _colliderObject = new GameObject("Modifier regression");
            _colliderObject.layer = FlowFieldRegressionFixture.Layer;
            var collider = _colliderObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.center = new Vector3(1.5f, 0.5f, 1.5f);
            collider.size = new Vector3(3f, 2f, 3f);
            var modifier = new FlowFieldRegressionFixture.Modifier { InfluenceCollider = collider, Priority = 100 };
            _session.RegisterModifierCommand(modifier);
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(_session.Sample(OriginCell).SpeedMultiplier, Is.EqualTo(2f));
            modifier.Speed = 3f;
            modifier.Revision++;
            _session.NotifyModifierChangedCommand(modifier, FlowFieldModifierChange.Value);
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            var after = _session.Sample(OriginCell);
            Assert.That(after.SpeedMultiplier, Is.EqualTo(3f));
            Assert.That(after.Direction, Is.EqualTo(before.Direction));
            Assert.That(_source.BuildCount, Is.EqualTo(1));
            Assert.That(_backend.Calls.Count, Is.EqualTo(1));
        }

        [Test]
        public void DefaultDirectionObservedDuringSolveDoesNotInvalidateGoal()
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _session.ObserveDefaultDirection(Vector3.left);
            _backend.Complete(0);
            Assert.That(_session.IsReady, Is.True);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalA));
        }

        [Test]
        public void PendingSolveDoesNotComposeModifiersUntilItCompletes()
        {
            Build(GoalA);
            _colliderObject = new GameObject("Compose count regression") { layer = FlowFieldRegressionFixture.Layer };
            var collider = _colliderObject.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.center = new Vector3(1.5f, 0.5f, 1.5f);
            collider.size = new Vector3(3f, 2f, 3f);
            var modifier = new CountingModifier { InfluenceCollider = collider, Priority = 100 };
            _session.RegisterModifierCommand(modifier);
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(modifier.Calls, Is.GreaterThan(0));

            modifier.Calls = 0;
            _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            _session.Pump();
            Assert.That(_session.IsRebuilding, Is.True);
            Assert.That(modifier.Calls, Is.Zero, "The field is composed once, after the solve.");
            _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(modifier.Calls, Is.GreaterThan(0));
            Assert.That(_session.Sample(OriginCell).SpeedMultiplier, Is.EqualTo(2f));
        }

        [Test]
        public void GoalChangePublishesWhenAnObstacleMovesDuringTheSolve()
        {
            Build(GoalA);
            RegisterObstacle(new Vector3(1.5f, 100f, 1.5f));
            RunOwnerTicks(GoalA);
            int revision = _session.Revision;

            StartSolve(GoalB);
            ObserveObstacleMove(new Vector3(1.5f, 101f, 1.5f));
            _backend.Complete(_backend.Calls.Count - 1);

            Assert.That(_session.Revision, Is.GreaterThan(revision));
            AssertResolvedGoal(GoalB);
            Assert.That(_session.HasPendingObstacleProbe, Is.True);

            int solves = _backend.Calls.Count;
            RunOwnerTicks(GoalB);
            AssertResolvedGoal(GoalB);
            Assert.That(_session.HasPendingObstacleProbe, Is.False);
            Assert.That(_backend.Calls.Count, Is.EqualTo(solves), "The same mask needs no further solve.");
        }

        [Test]
        public void ContinuouslyMovingObstacleDoesNotStarvePublication()
        {
            Vector3 first = new Vector3(2.5f, 0.5f, 0.5f);
            Vector3 second = new Vector3(1.5f, 0.5f, 1.5f);
            Build(GoalA);
            RegisterObstacle(first);
            RunOwnerTicks(GoalA);
            int revision = _session.Revision;

            _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            int solves = 0;
            Vector3 latest = first;
            for (int cycle = 0; cycle < 6; cycle++)
            {
                _session.Pump();
                Assert.That(_session.IsRebuilding, Is.True, $"Cycle {cycle} did not start a solve.");
                solves++;
                latest = cycle % 2 == 0 ? second : first;
                ObserveObstacleMove(latest);
                _backend.Complete(_backend.Calls.Count - 1);
                Assert.That(_session.IsFaulted, Is.False, _session.LastError);
                AssertResolvedGoal(GoalB);
                _session.ProbeObstacleChanges(FlowFieldRegressionFixture.Request(GoalB));
                _session.Submit(FlowFieldRegressionFixture.Request(GoalB));
            }

            Assert.That(solves, Is.EqualTo(6));
            Assert.That(_session.Revision, Is.GreaterThan(revision));
            RunOwnerTicks(GoalB);
            bool[] blocked = _session.CommittedWorkspace.Blocked;
            Assert.That(blocked[FlowFieldRegressionFixture.Grid.ToFlatIndex((int)latest.x, (int)latest.z)], Is.True);
            AssertResolvedGoal(GoalB);
        }

        [Test]
        public void GoalRejectedByAGridRequestIsSolvedAgain()
        {
            Build(GoalA);
            StartSolve(GoalB);
            _session.MarkDirty(
                FlowFieldDirtyFlags.Grid,
                FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid,
                deferSurfaceVersion: true);
            _backend.Complete(_backend.Calls.Count - 1);
            AssertResolvedGoal(GoalA);

            Assert.That(_session.HasPendingRequest, Is.True);
            RunOwnerTicks(GoalB);
            AssertResolvedGoal(GoalB);
        }

        [Test]
        public void FailedSolveWithAnObservedObstacleRetriesTheGoal()
        {
            Build(GoalA);
            RegisterObstacle(new Vector3(1.5f, 100f, 1.5f));
            RunOwnerTicks(GoalA);

            StartSolve(GoalB);
            ObserveObstacleMove(new Vector3(1.5f, 101f, 1.5f));
            _backend.Fail(_backend.Calls.Count - 1);
            Assert.That(_session.IsFaulted, Is.False, _session.LastError);
            Assert.That(_session.GpuFallbackReason, Does.Contain("Simulated GPU failure"));
            AssertResolvedGoal(GoalA);

            RunOwnerTicks(GoalB);
            AssertResolvedGoal(GoalB);
        }

        private void RegisterObstacle(Vector3 position)
        {
            _colliderObject = new GameObject("Moving obstacle regression");
            _colliderObject.layer = FlowFieldRegressionFixture.Layer;
            _colliderObject.transform.position = position;
            _colliderObject.transform.localScale = Vector3.one * 0.5f;
            BoxCollider collider = _colliderObject.AddComponent<BoxCollider>();
            Physics.SyncTransforms();
            _session.RegisterObstacle(collider, FlowFieldCellRect.Invalid);
        }

        private void StartSolve(Vector3 goal)
        {
            _session.Submit(FlowFieldRegressionFixture.Request(goal));
            _session.Pump();
            Assert.That(_session.IsRebuilding, Is.True);
        }

        // The same order as Manager.LateUpdate while a solve is in flight.
        private void ObserveObstacleMove(Vector3 position)
        {
            _colliderObject.transform.position = position;
            Physics.SyncTransforms();
            Assert.That(_session.DetectObstacleTransformsChanged(), Is.True);
            _session.RecordObstacleObservation(_session.DirtyObstacleRegion);
        }

        // Manager.LateUpdate probes and Manager.FixedUpdate submits and pumps.
        private void RunOwnerTicks(Vector3 goal)
        {
            for (int tick = 0; tick < 8; tick++)
            {
                if (_session.HasPendingObstacleProbe)
                    _session.ProbeObstacleChanges(FlowFieldRegressionFixture.Request(goal));
                if (!_session.HasPendingRequest)
                    return;
                _session.Submit(FlowFieldRegressionFixture.Request(goal));
                _session.Pump();
                if (_session.IsRebuilding)
                    _backend.Complete(_backend.Calls.Count - 1);
            }
            Assert.Fail("The session did not settle.");
        }

        private void AssertResolvedGoal(Vector3 goal)
        {
            Assert.That(_session.TryGetFieldInfo(out FlowFieldFieldInfo info), Is.True);
            Assert.That(info.HasResolvedGoal, Is.True);
            Assert.That(info.ResolvedGoalWorld.x, Is.EqualTo(goal.x).Within(0.01f));
            Assert.That(info.ResolvedGoalWorld.z, Is.EqualTo(goal.z).Within(0.01f));
        }

        private sealed class CountingModifier : IFlowFieldVectorModifier
        {
            public Collider InfluenceCollider { get; set; }
            public int Priority { get; set; }
            public int Revision { get; set; }
            internal int Calls;
            public IFlowFieldModifierSnapshot CaptureSnapshot() => new Snapshot(this);

            private sealed class Snapshot : IFlowFieldModifierSnapshot
            {
                private readonly CountingModifier _owner;
                internal Snapshot(CountingModifier owner) { _owner = owner; }
                public FlowFieldVectorState Modify(in FlowFieldVectorState current, in FlowFieldVectorModifierContext context)
                {
                    _owner.Calls++;
                    return new FlowFieldVectorState(current.Direction, current.SpeedMultiplier * 2f);
                }
            }
        }
    }
}
