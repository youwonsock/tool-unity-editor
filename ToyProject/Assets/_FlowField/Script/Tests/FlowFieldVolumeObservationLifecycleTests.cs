using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldVolumeObservationLifecycleTests
    {
        private FlowFieldVolumeSession _session;
        private FlowFieldRegressionFixture.EventFailures _eventFailures;
        private GameObject _obstacle;
        private static readonly Vector3 OldCell = new Vector3(3.5f, 1.5f, 3.5f);
        private static readonly FlowFieldGridSpace Replacement = FlowFieldGridSpace.FromVolumeCellGrid(
            new Vector3(100f, 0f, 0f), 4, 4, 4, 1f);

        [SetUp]
        public void SetUp()
        {
            _session = new FlowFieldVolumeSession();
            _eventFailures = new FlowFieldRegressionFixture.EventFailures(_session);
            _session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            _obstacle = new GameObject("Observation lifecycle regression");
            _obstacle.layer = FlowFieldRegressionFixture.Layer;
            _obstacle.transform.position = OldCell;
            _obstacle.AddComponent<BoxCollider>();
            Physics.SyncTransforms();
        }

        [TearDown]
        public void TearDown()
        {
            _session.Dispose();
            if (_obstacle != null) Object.DestroyImmediate(_obstacle);
            _eventFailures.AssertNone();
        }

        [TestCase(false, "release")]
        [TestCase(true, "release")]
        [TestCase(false, "suspend")]
        [TestCase(true, "suspend")]
        [TestCase(false, "dispose")]
        [TestCase(true, "dispose")]
        [TestCase(false, "restart")]
        [TestCase(true, "restart")]
        [TestCase(false, "fault")]
        [TestCase(true, "fault")]
        public void RegisteredObservationRespectsLifetimeChangesFromStateCallback(bool destroy, string action)
            => CheckObservation(registered: true, destroy: destroy, action: action);

        [TestCase(false, "release")]
        [TestCase(true, "release")]
        [TestCase(false, "suspend")]
        [TestCase(true, "suspend")]
        [TestCase(false, "dispose")]
        [TestCase(true, "dispose")]
        [TestCase(false, "restart")]
        [TestCase(true, "restart")]
        [TestCase(false, "fault")]
        [TestCase(true, "fault")]
        public void UnregisteredObservationRespectsLifetimeChangesFromStateCallback(bool destroy, string action)
            => CheckObservation(registered: false, destroy: destroy, action: action);

        private void CheckObservation(bool registered, bool destroy, string action)
        {
            if (registered) _session.RegisterDynamicObstacle(_obstacle.GetComponent<Collider>());
            _session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(FlowFieldVolumeSafetyRegressionTests.Grid));
            if (!registered) _session.DetectUnregisteredObstacleChanges(true);
            FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            int callbacks = 0;
            int publications = 0;
            _session.FieldCommitted += () => publications++;
            _session.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Building || callbacks != 0) return;
                callbacks++;
                switch (action)
                {
                    case "release": _session.Release(); break;
                    case "suspend": _session.Suspend(); break;
                    case "dispose": _session.Dispose(); break;
                    case "restart":
                        _session.Release();
                        _session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                        _session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(Replacement,
                            goal: Replacement.Origin + Vector3.one * 2.5f));
                        break;
                    case "fault": _session.ReportFault(new InvalidOperationException("Expected lifecycle interruption")); break;
                }
            };

            if (destroy) Object.DestroyImmediate(_obstacle);
            else _obstacle.transform.position += Vector3.right * 2f;
            Physics.SyncTransforms();
            Assert.DoesNotThrow(() =>
            {
                if (registered) _session.Pump(10d);
                else _session.DetectUnregisteredObstacleChanges(true);
            });
            Assert.That(callbacks, Is.EqualTo(1));
            Assert.That(publications, Is.Zero, "The interrupted call must not consume a new lifetime's pending build.");

            if (action == "restart")
            {
                Assert.That(_session.IsReady, Is.False);
                Assert.That(_session.TryGetLatestRequestedInput(out var request), Is.True);
                Assert.That(request.Grid.Origin, Is.EqualTo(Replacement.Origin));
                FlowFieldVolumeSafetyRegressionTests.Finish(_session);
                Assert.That(publications, Is.EqualTo(1));
                Assert.That(_session.TrySample(Replacement.Origin + Vector3.one * 0.5f, out _), Is.True);
            }
            else if (action == "suspend")
            {
                Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Suspended));
                Assert.That(_session.IsRebuilding, Is.False);
                Assert.That(_session.HasPendingRequest, Is.False);
                _session.Resume();
                _session.Submit(FlowFieldVolumeSafetyRegressionTests.Request(FlowFieldVolumeSafetyRegressionTests.Grid));
                FlowFieldVolumeSafetyRegressionTests.Finish(_session);
                Assert.That(_session.IsReady, Is.True);
            }
            else if (action == "fault")
            {
                Assert.That(_session.IsFaulted, Is.True);
                Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Faulted));
                Assert.That(_session.HasPendingRequest, Is.False);
                Assert.That(_session.LastError, Does.Contain("Expected lifecycle interruption"));
            }
            else
            {
                Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Released));
                Assert.That(_session.HasPendingRequest, Is.False);
                Assert.That(_session.IsReady, Is.False);
                Assert.That(_session.TryGetLatestRequestedInput(out _), Is.False);
            }
        }
    }
}
