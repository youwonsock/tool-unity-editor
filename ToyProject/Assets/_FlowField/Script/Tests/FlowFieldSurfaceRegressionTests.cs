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
    }
}
