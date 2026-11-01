using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldVolumeObstacleTrackerTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Bounds> _dirty = new List<Bounds>();
        private static readonly Bounds Sweep = new Bounds(new Vector3(4f, 2f, 4f), new Vector3(10f, 6f, 10f));

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject obj in _objects) if (obj != null) Object.DestroyImmediate(obj);
            _objects.Clear();
            _dirty.Clear();
        }

        private BoxCollider Obstacle(Vector3 position)
        {
            var obj = new GameObject("Tracker reconciliation regression");
            _objects.Add(obj);
            obj.transform.position = position;
            var collider = obj.AddComponent<BoxCollider>();
            Physics.SyncTransforms();
            return collider;
        }

        [Test]
        public void IncompleteSweepRetainsDestroyedColliderObservationUntilCompleteResult()
        {
            var tracker = new FlowFieldVolumeObstacleTracker(_dirty.Add);
            var collider = Obstacle(Vector3.one * 1.5f);
            Bounds previous = collider.bounds;
            tracker.ApplySweep(Sweep, new Collider[] { collider }, 1, complete: true);
            _dirty.Clear();
            Object.DestroyImmediate(collider.gameObject);

            // An incomplete result cannot prove that a previous collider was
            // removed, including one that now compares equal to Unity null.
            tracker.ApplySweep(Sweep, Array.Empty<Collider>(), 0, complete: false);
            Assert.That(_dirty, Is.EqualTo(new[] { Sweep }));
            _dirty.Clear();
            tracker.ApplySweep(Sweep, Array.Empty<Collider>(), 0, complete: true);
            Assert.That(_dirty, Is.EqualTo(new[] { previous }));
            _dirty.Clear();
            tracker.ApplySweep(Sweep, Array.Empty<Collider>(), 0, complete: true);
            Assert.That(_dirty, Is.Empty);
        }

        [Test]
        public void ClearRetiresRegistrationAndSweepObservationsWithoutReportingPhantomRemovals()
        {
            var tracker = new FlowFieldVolumeObstacleTracker(_dirty.Add);
            var observed = Obstacle(Vector3.one * 1.5f);
            var registered = Obstacle(Vector3.one * 3.5f);
            tracker.ApplySweep(Sweep, new Collider[] { observed }, 1, complete: true);
            Assert.That(tracker.Register(registered), Is.True);
            long bytes = tracker.SweepBufferBytes;
            _dirty.Clear();
            tracker.Clear();
            Assert.That(tracker.RegisteredCount, Is.Zero);
            Assert.That(tracker.SweepBufferBytes, Is.EqualTo(bytes));
            tracker.ApplySweep(Sweep, Array.Empty<Collider>(), 0, complete: true);
            Assert.That(_dirty, Is.Empty);
            Assert.That(tracker.Unregister(registered), Is.False);
            Assert.That(tracker.Register(registered), Is.True);
            Assert.That(tracker.GetRegisteredObstacle(0), Is.SameAs(registered));
        }
    }
}
