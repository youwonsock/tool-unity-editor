using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldVolumeObstacleRegressionTests
    {
        private FlowFieldVolumeSession _session;
        private FlowFieldRegressionFixture.EventFailures _eventFailures;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private static readonly Vector3 OldCell = new Vector3(3.5f, 1.5f, 3.5f);
        private static readonly Vector3 NewCell = new Vector3(5.5f, 1.5f, 3.5f);

        [SetUp]
        public void SetUp()
        {
            _session = new FlowFieldVolumeSession();
            _eventFailures = new FlowFieldRegressionFixture.EventFailures(_session);
            _session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
        }

        [TearDown]
        public void TearDown()
        {
            _session.Dispose();
            foreach (GameObject obj in _objects) if (obj != null) Object.DestroyImmediate(obj);
            _objects.Clear();
            _eventFailures.AssertNone();
        }

        private BoxCollider Obstacle(Vector3 position, int layer = FlowFieldRegressionFixture.Layer)
        {
            var obj = new GameObject("Obstacle observation regression");
            _objects.Add(obj);
            obj.layer = layer;
            obj.transform.position = position;
            return obj.AddComponent<BoxCollider>();
        }

        private void Build(bool sweep = false)
        {
            _session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(FlowFieldVolumeSafetyRegressionTests.Grid));
            Physics.SyncTransforms();
            if (sweep) _session.DetectUnregisteredObstacleChanges(true);
            Finish();
        }

        private void Finish() => FlowFieldVolumeSafetyRegressionTests.Finish(_session);

        private bool IsBlocked(Vector3 position)
        {
            Assert.That(_session.TryExport(out var request, out var blocked, out _, out _, out _,
                out _, out _, out _, out _), Is.True);
            Assert.That(request.Grid.TryWorldToLocal(position, out int x, out int y, out int z), Is.True);
            return blocked[request.Grid.ToFlatIndex(x, y, z)];
        }

        private void AssertStopped(Vector3 position)
        {
            Assert.That(_session.TrySample(position, out var sample), Is.True);
            Assert.That(sample.Direction, Is.EqualTo(Vector3.zero));
            Assert.That(sample.SpeedMultiplier, Is.Zero);
        }

        [Test]
        public void DuplicateRegistrationAndUnknownUnregisterDoNotQueueAnotherBuild()
        {
            var collider = Obstacle(OldCell);
            Physics.SyncTransforms();
            Assert.That(_session.RegisterDynamicObstacle(collider), Is.True);
            Build();
            int revision = _session.Revision;
            Assert.That(_session.RegisterDynamicObstacle(collider), Is.False);
            Assert.That(_session.UnregisterDynamicObstacle(Obstacle(Vector3.one * 100f)), Is.False);
            Assert.That(_session.IsRebuilding, Is.False);
            Assert.That(_session.Revision, Is.EqualTo(revision));
        }

        [Test]
        public void SweepToRegistrationTransferDoesNotInventRemovalAndStillTracksMovement()
        {
            var collider = Obstacle(OldCell);
            Build(sweep: true);
            Assert.That(_session.RegisterDynamicObstacle(collider), Is.True);
            Finish();
            int revision = _session.Revision;
            _session.DetectUnregisteredObstacleChanges(true);
            Assert.That(_session.IsRebuilding, Is.False);
            Assert.That(_session.Revision, Is.EqualTo(revision));

            collider.transform.position = NewCell;
            Physics.SyncTransforms();
            FlowFieldVolumeSafetyRegressionTests.PumpOneStep(_session);
            AssertStopped(OldCell);
            AssertStopped(NewCell);
            Finish();
            Assert.That(IsBlocked(OldCell), Is.False);
            Assert.That(IsBlocked(NewCell), Is.True);
        }

        [Test]
        public void UnregisterTransfersColliderBackToSweepWithoutLosingItsRegion()
        {
            var collider = Obstacle(OldCell);
            Physics.SyncTransforms();
            _session.RegisterDynamicObstacle(collider);
            Build();
            Assert.That(_session.UnregisterDynamicObstacle(collider), Is.True);
            _session.DetectUnregisteredObstacleChanges(true);
            Finish();
            Assert.That(IsBlocked(OldCell), Is.True);
            Object.DestroyImmediate(collider.gameObject);
            _session.DetectUnregisteredObstacleChanges(true);
            AssertStopped(OldCell);
            Finish();
            Assert.That(IsBlocked(OldCell), Is.False);
        }

        [Test]
        public void MovingUnregisteredObstacleOutsideVolumeClearsItsCachedRegion()
        {
            var collider = Obstacle(OldCell);
            Build(sweep: true);
            collider.transform.position = Vector3.one * 100f;
            _session.DetectUnregisteredObstacleChanges(true);
            AssertStopped(OldCell);
            Finish();
            Assert.That(IsBlocked(OldCell), Is.False);
            Assert.That(_session.Sample(OldCell).SpeedMultiplier, Is.GreaterThan(0f));
        }

        [Test]
        public void DisabledSweepRetainsObservationsAndReenabledSweepDetectsRemovalAndReturn()
        {
            var collider = Obstacle(OldCell);
            Build(sweep: true);
            collider.enabled = false;
            Physics.SyncTransforms();
            _session.DetectUnregisteredObstacleChanges(false);
            Assert.That(_session.IsRebuilding, Is.False);
            Assert.That(IsBlocked(OldCell), Is.True);
            _session.DetectUnregisteredObstacleChanges(true);
            AssertStopped(OldCell);
            Finish();
            Assert.That(IsBlocked(OldCell), Is.False);
            collider.enabled = true;
            _session.DetectUnregisteredObstacleChanges(true);
            AssertStopped(OldCell);
            Finish();
            Assert.That(IsBlocked(OldCell), Is.True);
        }

        [Test]
        public void RegisteredObstacleOutsideConfiguredMaskRemainsBlockingUntilUnregistered()
        {
            var collider = Obstacle(OldCell, layer: 30);
            Physics.SyncTransforms();
            _session.RegisterDynamicObstacle(collider);
            Build(sweep: true);
            Assert.That(IsBlocked(OldCell), Is.True);
            _session.UnregisterDynamicObstacle(collider);
            Finish();
            Assert.That(IsBlocked(OldCell), Is.False);
        }

        [Test]
        public void SweepGrowsBufferAndClearsFormerOccupancyAfterCompleteEmptySweep()
        {
            var colliders = new List<Collider>();
            for (int i = 0; i < 520; i++) colliders.Add(Obstacle(OldCell));
            _session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(FlowFieldVolumeSafetyRegressionTests.Grid));
            long before = _session.TrackedMemoryBytes;
            _session.DetectUnregisteredObstacleChanges(true);
            Assert.That(_session.TrackedMemoryBytes, Is.GreaterThanOrEqualTo(before + 512L * 8L));
            Finish();
            Assert.That(IsBlocked(OldCell), Is.True);
            foreach (Collider collider in colliders) collider.enabled = false;
            _session.DetectUnregisteredObstacleChanges(true);
            AssertStopped(OldCell);
            Finish();
            Assert.That(IsBlocked(OldCell), Is.False);
        }

        [Test]
        public void DestroyedObservedColliderCanBeReplacedAtTheSamePosition()
        {
            var first = Obstacle(OldCell);
            Build(sweep: true);
            Object.DestroyImmediate(first.gameObject);
            var second = Obstacle(OldCell);
            _session.DetectUnregisteredObstacleChanges(true);
            Finish();
            Assert.That(IsBlocked(OldCell), Is.True);
            Object.DestroyImmediate(second.gameObject);
            _session.DetectUnregisteredObstacleChanges(true);
            AssertStopped(OldCell);
            Finish();
            Assert.That(IsBlocked(OldCell), Is.False);
        }

        [Test]
        public void InvalidRegistrationPreservesTheLatestRequestAndPublication()
        {
            Build();
            int revision = _session.Revision;
            Assert.That(_session.TryGetLatestRequestedInput(out var before), Is.True);
            var destroyed = Obstacle(Vector3.one * 100f);
            Object.DestroyImmediate(destroyed.gameObject);
            Assert.Throws<ArgumentNullException>(() => _session.RegisterDynamicObstacle(null));
            Assert.Throws<InvalidOperationException>(() => _session.RegisterDynamicObstacle(destroyed));
            Assert.That(_session.TryGetLatestRequestedInput(out var after), Is.True);
            Assert.That(after.HasSameInputs(before), Is.True);
            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(_session.IsRebuilding, Is.False);
        }
    }
}
