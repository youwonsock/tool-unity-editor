using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldSurfaceSimplificationTests
    {
        private static readonly Vector3 Position = new Vector3(0.5f, 0f, 0.5f);
        private static readonly Vector3 GoalA = new Vector3(2.5f, 0f, 2.5f);
        private static readonly Vector3 GoalB = new Vector3(0.5f, 0f, 2.5f);
        private static readonly Vector3 GoalC = new Vector3(2.5f, 0f, 0.5f);
        private FlowFieldSession _session;
        private FlowFieldRegressionFixture.DeferredBackend _backend;
        private FlowFieldRegressionFixture.EventFailures _eventFailures;
        private int _publications;

        [SetUp]
        public void SetUp()
        {
            _publications = 0;
            _backend = new FlowFieldRegressionFixture.DeferredBackend();
            _session = new FlowFieldSession(FlowFieldRegressionFixture.Surface(), _backend);
            _eventFailures = new FlowFieldRegressionFixture.EventFailures(_session);
            _session.FieldCommitted += () => _publications++;
            Initialize();
        }

        private void Initialize()
            => _session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                FlowFieldSolvePolicy.Worker);

        [TearDown]
        public void TearDown()
        {
            _session.Dispose();
            _eventFailures.AssertNone();
        }

        private static FlowFieldSessionRequest Request(Vector3? goal, Vector3 direction, float radius = 0f)
            => FlowFieldSessionRequest.ForSceneBuild(FlowFieldRegressionFixture.Settings,
                1 << FlowFieldRegressionFixture.Layer, 1f, 0.5f, 0f, false,
                goal.HasValue ? FlowFieldGoalPipeline.Resolve(FlowFieldRegressionFixture.Grid, null, true,
                    goal.Value, radius) : FlowFieldGoalResolution.None,
                direction, FlowFieldDirtyFlags.None, FlowFieldCellRect.Invalid,
                FlowFieldCellRect.Invalid);

        private void Finish()
        {
            _session.Pump();
            if (_session.IsRebuilding) _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.IsReady, Is.True, _session.LastError);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RequestsBeforePumpUseOnlyTheLatestInput(bool hasGoal)
        {
            _session.Submit(Request(hasGoal ? GoalA : (Vector3?)null, Vector3.forward));
            _session.Submit(Request(hasGoal ? GoalB : (Vector3?)null, Vector3.left));
            _session.Submit(Request(hasGoal ? GoalC : (Vector3?)null, Vector3.right));
            Assert.That(_backend.Calls, Is.Empty);
            Assert.That(_publications, Is.Zero);
            Finish();
            Assert.That(_backend.Calls.Count, Is.EqualTo(hasGoal ? 1 : 0));
            Assert.That(_session.Sample(Position).Direction, Is.EqualTo(Vector3.right));
            Assert.That(_publications, Is.EqualTo(1));
            if (hasGoal)
            {
                Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
                Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalC));
            }
        }

        [Test]
        public void ReplacementRequestsSurviveAnOldBackendCompletion()
        {
            _session.Submit(Request(null, Vector3.forward));
            Finish();
            int revision = _session.Revision;
            _session.Submit(Request(GoalA, Vector3.forward));
            _session.Pump();
            _session.Submit(Request(GoalB, Vector3.left));
            _session.Submit(Request(GoalC, Vector3.right));
            _backend.Complete(0);
            Assert.That(_session.IsFaulted, Is.False);
            // A finished solve publishes even though newer Goals arrived.
            Assert.That(_session.Revision, Is.EqualTo(revision + 1));
            Finish();
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalC));
            Assert.That(_session.Sample(Position).Direction, Is.EqualTo(Vector3.right));
            Assert.That(_publications, Is.EqualTo(3));
            _session.Pump();
            Assert.That(_publications, Is.EqualTo(3));
        }

        [TestCase("submit")]
        [TestCase("observe")]
        [TestCase("multiple")]
        public void FinalDirectionChangesKeepTheActiveGoalSolve(string mode)
        {
            _session.Submit(Request(GoalA, Vector3.forward, 0.75f));
            _session.Pump();
            if (mode == "observe") _session.ObserveDefaultDirection(Vector3.left);
            else
            {
                _session.Submit(Request(GoalA, Vector3.right, 0.75f));
                if (mode == "multiple") _session.Submit(Request(GoalA, Vector3.back, 0.75f));
                _session.Submit(Request(GoalA, Vector3.left, 0.75f));
            }
            _backend.Complete(0);
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalA));
            Assert.That(_session.Sample(Position).Direction, Is.EqualTo(Vector3.left));
            Assert.That(_backend.Calls.Count, Is.EqualTo(1));
            Assert.That(_publications, Is.EqualTo(1));
            _session.Pump();
            Assert.That(_publications, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PublicationCallbackLeavesItsRequestForTheNextPump(bool sameGoal)
        {
            bool queued = false;
            _session.FieldCommitted += () =>
            {
                if (queued) return;
                queued = true;
                _session.Submit(Request(sameGoal ? GoalA : GoalC, Vector3.forward));
            };
            _session.Submit(Request(GoalA, Vector3.forward));
            Finish();
            Assert.That(_publications, Is.EqualTo(1));
            Assert.That(_backend.Calls.Count, Is.EqualTo(1));
            Finish();
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(sameGoal ? GoalA : GoalC));
            Assert.That(_publications, Is.EqualTo(sameGoal ? 1 : 2));
            Assert.That(_backend.Calls.Count, Is.EqualTo(sameGoal ? 1 : 2));
        }

        [Test]
        public void LatestRequestSurvivesLifecycleAndLateCallbacks(
            [Values("suspend", "release", "fault")] string transition)
        {
            _session.Submit(Request(GoalA, Vector3.forward));
            _session.Pump();
            if (transition == "suspend") _session.Suspend();
            else if (transition == "release") _session.Release();
            else _session.ReportFault(new InvalidOperationException("Surface request retry"));
            if (transition == "fault") _session.RetryFault(); else Initialize();
            _session.Submit(Request(GoalB, Vector3.left));
            _session.Submit(Request(GoalC, Vector3.right));
            _backend.Complete(0, false);
            Assert.That(_publications, Is.Zero);
            Assert.That(_session.IsFaulted, Is.False);
            Finish();
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalC));
            Assert.That(_session.Sample(Position).Direction, Is.EqualTo(Vector3.right));
            Assert.That(_publications, Is.EqualTo(1));
        }

        [Test]
        public void BackendOwnershipSurvivesLifecycle(
            [Values(false, true)] bool factory,
            [Values(false, true)] bool release)
        {
            var backends = new List<OwnershipBackend>();
            Func<IFlowFieldSolveBackend> create = () =>
            {
                var value = new OwnershipBackend();
                backends.Add(value);
                return value;
            };
            var injected = factory ? null : create();
            var session = new FlowFieldSession(FlowFieldRegressionFixture.Surface(), injected,
                factory ? create : null);
            var errors = new FlowFieldRegressionFixture.EventFailures(session);
            try
            {
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                    FlowFieldSolvePolicy.Worker);
                if (release) session.Release(); else session.Suspend();
                Assert.That(backends[0].Disposals, Is.EqualTo(factory ? 1 : 0));
                session.Initialize(FlowFieldBakeMode.RuntimeDynamic, FlowFieldSessionSourceKind.SceneBuild,
                    FlowFieldSolvePolicy.Worker);
                session.Submit(Request(GoalC, Vector3.forward));
                session.Pump();
                Assert.That(session.Sample(Position).Direction, Is.EqualTo(Vector3.right));
                Assert.That(backends.Count, Is.EqualTo(factory ? 2 : 1));
            }
            finally { session.Dispose(); }
            session.Dispose();
            foreach (var backend in backends) Assert.That(backend.Disposals, Is.EqualTo(1));
            errors.AssertNone();
        }

        [Test]
        public void RepeatedLatestRequestDoesNotRepublishOrStartBfs()
        {
            _session.Submit(Request(GoalC, Vector3.right));
            Finish();
            for (int i = 0; i < 3; i++)
            {
                _session.Submit(Request(GoalC, Vector3.right));
                _session.Pump();
            }
            Assert.That(_publications, Is.EqualTo(1));
            Assert.That(_backend.Calls.Count, Is.EqualTo(1));
        }

        private sealed class OwnershipBackend : IFlowFieldSolveBackend
        {
            internal int Disposals;
            public void Poll(bool wait) { }
            public void Cancel() { }
            public bool StartBfs(in FlowFieldBfsRequest request, Action<FlowFieldBfsRequest> completed)
            {
                FlowFieldSurfaceBuildPipeline.SolveNow(request);
                completed(request);
                return true;
            }
            public void Dispose() => Disposals++;
        }
    }

    public sealed class FlowFieldModifierCaptureObservationTests
    {
    }
}
