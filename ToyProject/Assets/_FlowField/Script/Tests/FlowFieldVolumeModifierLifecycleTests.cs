using System;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldVolumeModifierLifecycleTests
    {
        private static readonly FlowFieldGridSpace Grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 3, 3, 3, 1f);
        private static readonly FlowFieldGridSpace Replacement = FlowFieldGridSpace.FromVolumeCellGrid(new Vector3(100f, 0f, 0f), 3, 3, 3, 1f);
        private static readonly Vector3 Position = Vector3.one * 0.5f;
        private FlowFieldVolumeSession _session;
        private FlowFieldRegressionFixture.EventFailures _eventFailures;
        private GameObject _area;
        private Modifier _modifier;

        [SetUp]
        public void SetUp()
        {
            _session = new FlowFieldVolumeSession();
            _eventFailures = new FlowFieldRegressionFixture.EventFailures(_session);
            _session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
            _area = new GameObject("Modifier lifecycle regression");
            _area.layer = FlowFieldRegressionFixture.Layer;
            var collider = _area.AddComponent<BoxCollider>();
            collider.isTrigger = true;
            collider.center = Vector3.one * 1.5f;
            collider.size = Vector3.one * 4f;
            Physics.SyncTransforms();
            _modifier = NewModifier(100, 2f);
            _session.RegisterModifier(_modifier);
        }

        [TearDown]
        public void TearDown()
        {
            _session.Dispose();
            if (_area != null) Object.DestroyImmediate(_area);
            _eventFailures.AssertNone();
        }

        private Modifier NewModifier(int priority, float speed)
            => new Modifier { InfluenceCollider = _area.GetComponent<Collider>(), Priority = priority, Speed = speed };

        private static FlowFieldVolumeRequest Request(FlowFieldGridSpace? grid = null, Vector3? goal = null)
        {
            FlowFieldGridSpace value = grid ?? Grid;
            return FlowFieldVolumeSafetyRegressionTests.Request(value, goal: goal ?? value.Origin + Vector3.one * 2.5f);
        }

        private void PublishInitial()
        {
            _session.Submit(Request());
            FlowFieldVolumeSafetyRegressionTests.Finish(_session);
        }

        private void Arm(string stage, Action callback)
        {
            if (stage == "capture") _modifier.OnCapture = callback;
            else _modifier.OnModify = callback;
        }

        private void ChangeLifetime(string action)
        {
            switch (action)
            {
                case "release": _session.Release(); break;
                case "suspend": _session.Suspend(); break;
                case "dispose": _session.Dispose(); break;
                case "restart":
                    _session.Release();
                    _session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                    _session.Submit(Request(Replacement));
                    break;
                case "fault": _session.ReportFault(new InvalidOperationException("Explicit modifier interruption")); break;
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        [Test]
        public void ActiveModifierExceptionsStillFaultExactlyOnce([Values("capture", "modify")] string stage)
        {
            PublishInitial();
            int failures = 0;
            int publications = 0;
            int revision = _session.Revision;
            _session.Failed += _ => failures++;
            _session.FieldCommitted += () => publications++;
            Arm(stage, () => throw new InvalidOperationException("Active modifier failure"));
            _session.Submit(Request());
            Assert.DoesNotThrow(() => _session.Pump(100d));
            Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Faulted));
            Assert.That(_session.LastError, Does.Contain("Active modifier failure"));
            Assert.That(_session.HasPendingRequest, Is.False);
            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(failures, Is.EqualTo(1));
            Assert.That(publications, Is.Zero);
        }

        [Test]
        public void FaultStateCallbackCannotReportAnOldFailureIntoTheNewLifetime([Values("capture", "modify", "report")] string stage)
        {
            PublishInitial();
            int failures = 0;
            bool restarted = false;
            _session.Failed += _ => failures++;
            _session.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Faulted || restarted) return;
                restarted = true;
                ChangeLifetime("restart");
            };
            if (stage == "report") _session.ReportFault(new InvalidOperationException("Old explicit fault"));
            else
            {
                Arm(stage, () => throw new InvalidOperationException("Old modifier fault"));
                _session.Submit(Request());
                _session.Pump(100d);
            }
            Assert.That(restarted, Is.True);
            Assert.That(failures, Is.Zero);
            Assert.That(_session.IsFaulted, Is.False);
            Assert.That(_session.HasPendingRequest, Is.True);
            FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            Assert.That(_session.TrySample(Replacement.Origin + Position, out _), Is.True);
        }

        [Test]
        public void PumpGuardIsPerSession()
        {
            using (var other = new FlowFieldVolumeSession())
            {
                var otherFailures = new FlowFieldRegressionFixture.EventFailures(other);
                other.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                other.Submit(Request(Replacement));
                _modifier.OnCapture = () => FlowFieldVolumeSafetyRegressionTests.Finish(other);
                _session.Submit(Request());
                FlowFieldVolumeSafetyRegressionTests.Finish(_session);
                Assert.That(other.IsReady, Is.True);
                otherFailures.AssertNone();
            }
        }

        private sealed class Modifier : IFlowFieldVectorModifier
        {
            public Collider InfluenceCollider { get; set; }
            public int Priority { get; set; }
            public int Revision => 0;
            public FlowFieldModifierObservation Observe() => new FlowFieldModifierObservation(InfluenceCollider, Priority, Revision);
            internal float Speed;
            internal bool ReturnNull;
            internal Action OnCapture;
            internal Action OnModify;

            public IFlowFieldModifierSnapshot CaptureSnapshot()
            {
                OnCapture?.Invoke();
                return ReturnNull ? null : new Snapshot(Speed, OnModify);
            }

            private sealed class Snapshot : IFlowFieldModifierSnapshot
            {
                private readonly float _speed;
                private readonly Action _onModify;
                internal Snapshot(float speed, Action onModify) { _speed = speed; _onModify = onModify; }
                public FlowFieldVectorState Modify(in FlowFieldVectorState current, in FlowFieldVectorModifierContext context)
                {
                    _onModify?.Invoke();
                    return new FlowFieldVectorState(current.Direction, current.SpeedMultiplier * _speed);
                }
            }
        }
    }
}
