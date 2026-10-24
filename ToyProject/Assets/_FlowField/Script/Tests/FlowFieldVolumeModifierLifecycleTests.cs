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
                case "cancel": _session.Cancel(); break;
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
        }

        [Test]
        public void ModifierLifetimeChangesStopTheOldOperation(
            [Values("capture", "modify")] string stage,
            [Values("release", "suspend", "dispose", "restart", "fault", "cancel")] string action,
            [Values(false, true)] bool throwAfterChange)
        {
            PublishInitial();
            int revision = _session.Revision;
            int publications = 0;
            int failures = 0;
            int calls = 0;
            bool interrupted = false;
            _session.FieldCommitted += () => publications++;
            _session.Failed += _ => failures++;
            Arm(stage, () =>
            {
                calls++;
                if (interrupted) return;
                interrupted = true;
                ChangeLifetime(action);
                if (throwAfterChange) throw new InvalidOperationException("Exception from the old modifier operation");
            });
            _session.Submit(Request());

            Assert.DoesNotThrow(() => _session.Pump(100d));
            Assert.That(calls, Is.EqualTo(1), "No further modifier calls may run after this operation is invalidated.");
            Assert.That(publications, Is.Zero);
            Assert.That(_session.Revision, Is.EqualTo(revision));
            Assert.That(failures, Is.EqualTo(action == "fault" ? 1 : 0));
            if (action == "restart")
            {
                Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Building));
                Assert.That(_session.HasPendingRequest, Is.True);
                Assert.That(_session.IsReady, Is.False);
                Assert.That(_session.TryGetLatestRequestedInput(out var latest), Is.True);
                Assert.That(latest.Grid.Origin, Is.EqualTo(Replacement.Origin));
                FlowFieldVolumeSafetyRegressionTests.Finish(_session);
                Assert.That(publications, Is.EqualTo(1));
                Assert.That(_session.TrySample(Replacement.Origin + Position, out _), Is.True);
            }
            else if (action == "suspend")
            {
                Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Suspended));
                Assert.That(_session.HasPendingRequest, Is.False);
                _session.Resume();
                _session.Submit(Request());
                FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            }
            else if (action == "fault")
            {
                Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Faulted));
                Assert.That(_session.LastError, Does.Contain("Explicit modifier interruption"));
                Assert.That(_session.HasPendingRequest, Is.False);
            }
            else if (action == "cancel")
            {
                Assert.That(_session.IsFaulted, Is.False);
                Assert.That(_session.HasPendingRequest, Is.False);
                Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(2f));
                _session.Submit(Request());
                FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            }
            else
            {
                Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Released));
                Assert.That(_session.HasPendingRequest, Is.False);
                Assert.That(_session.TryGetLatestRequestedInput(out _), Is.False);
                Assert.That(_session.IsReady, Is.False);
            }
        }

        [Test]
        public void NestedPumpDoesNotAdvanceTheSameOperation([Values("capture", "modify")] string stage)
        {
            PublishInitial();
            int revision = _session.Revision;
            int publications = 0;
            bool entered = false;
            FlowFieldRuntimeState innerState = FlowFieldRuntimeState.Uninitialized;
            int innerRevision = -1;
            _session.FieldCommitted += () => publications++;
            Arm(stage, () =>
            {
                if (entered) return;
                entered = true;
                _session.Pump(100d);
                innerState = _session.State;
                innerRevision = _session.Revision;
            });
            _session.Submit(Request());
            FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            Assert.That(entered, Is.True);
            Assert.That(innerState, Is.EqualTo(FlowFieldRuntimeState.Building));
            Assert.That(innerRevision, Is.EqualTo(revision));
            Assert.That(publications, Is.EqualTo(1));
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(2f));
        }

        [Test]
        public void CaptureRegistrationChangesRetryACompleteCurrentSet(
            [Values("remove_self", "remove_other", "add")] string change,
            [Values(false, true)] bool previouslyPublished)
        {
            var second = NewModifier(200, 3f);
            var third = NewModifier(300, 5f);
            _session.RegisterModifier(second);
            if (previouslyPublished) PublishInitial();
            int publications = 0;
            bool changed = false;
            _session.FieldCommitted += () => publications++;
            _modifier.OnCapture = () =>
            {
                if (changed) return;
                changed = true;
                if (change == "remove_self") _session.UnregisterModifier(_modifier);
                else if (change == "remove_other") _session.UnregisterModifier(second);
                else _session.RegisterModifier(third);
            };
            _session.Submit(Request());
            Assert.DoesNotThrow(() => _session.Pump(100d));
            Assert.That(changed, Is.True);
            Assert.That(publications, Is.Zero, "A partially captured set must never be published.");
            Assert.That(_session.IsFaulted, Is.False, _session.LastError);
            Assert.That(_session.HasPendingRequest, Is.True);
            FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            Assert.That(publications, Is.EqualTo(1));
            Assert.That(_session.Sample(Position).SpeedMultiplier,
                Is.EqualTo(change == "remove_self" ? 3f : change == "remove_other" ? 2f : 30f));
        }

        [Test]
        public void ExceptionFromAnInvalidatedCapturePreservesTheNewRegistrationRequest()
        {
            PublishInitial();
            bool changed = false;
            _modifier.OnCapture = () =>
            {
                if (changed) return;
                changed = true;
                _session.UnregisterModifier(_modifier);
                throw new InvalidOperationException("Exception from removed modifier capture");
            };
            _session.Submit(Request());
            Assert.DoesNotThrow(() => _session.Pump(100d));
            Assert.That(_session.IsFaulted, Is.False, _session.LastError);
            Assert.That(_session.HasPendingRequest, Is.True);
            FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            Assert.That(_session.Sample(Position).SpeedMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void NullSnapshotFromAnOldLifetimeDoesNotFaultItsReplacement([Values("release", "restart")] string action)
        {
            PublishInitial();
            _modifier.ReturnNull = true;
            _modifier.OnCapture = () => ChangeLifetime(action);
            _session.Submit(Request());
            Assert.DoesNotThrow(() => _session.Pump(100d));
            Assert.That(_session.IsFaulted, Is.False, _session.LastError);
            Assert.That(_session.State, Is.EqualTo(action == "release" ? FlowFieldRuntimeState.Released : FlowFieldRuntimeState.Building));
            if (action == "restart") FlowFieldVolumeSafetyRegressionTests.Finish(_session);
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
        public void CaptureMutationKeepsTheNewestPendingGoal()
        {
            PublishInitial();
            var latestGoal = new Vector3(0.5f, 2.5f, 2.5f);
            bool changed = false;
            _modifier.OnCapture = () =>
            {
                if (changed) return;
                changed = true;
                _session.Submit(Request(goal: latestGoal));
                _session.UnregisterModifier(_modifier);
            };
            _session.Submit(Request());
            _session.Pump(100d);
            Assert.That(_session.HasPendingRequest, Is.True);
            FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(latestGoal));
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
