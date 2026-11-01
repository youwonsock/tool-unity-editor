using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldSurfaceLifecycleTests
    {
        private static readonly Vector3 GoalA = new Vector3(2.5f, 0f, 2.5f);
        private static readonly Vector3 GoalC = new Vector3(2.5f, 0f, 0.5f);
        private FlowFieldSession _session;
        private FlowFieldRegressionFixture.DeferredBackend _backend;
        private FlowFieldRegressionFixture.EventFailures _eventFailures;

        [SetUp]
        public void SetUp()
        {
            _backend = new FlowFieldRegressionFixture.DeferredBackend();
            _session = new FlowFieldSession(FlowFieldRegressionFixture.Surface(), _backend);
            _eventFailures = new FlowFieldRegressionFixture.EventFailures(_session);
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

        [Test]
        public void FaultedStateRestartDoesNotReportTheOldFailureToTheNewLifetime()
        {
            int failures = 0;
            bool restarted = false;
            _session.Failed += _ => failures++;
            _session.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Faulted || restarted) return;
                restarted = true;
                _session.Release();
                Initialize();
                _session.Submit(FlowFieldRegressionFixture.Request(GoalC));
            };
            _session.ReportFault(new InvalidOperationException("Old Surface fault"));
            Assert.That(restarted, Is.True);
            Assert.That(failures, Is.Zero, "The failure belongs to the lifetime released inside StateChanged.");
            FinishGoal(GoalC);
        }

        [Test]
        public void FaultedStateRetryPreservesTheNewGoalDirtyState()
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _backend.Complete(0);
            bool retried = false;
            _session.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Faulted || retried) return;
                retried = true;
                _session.RetryFault();
                _session.Submit(FlowFieldRegressionFixture.Request(GoalC));
            };
            _session.ReportFault(new InvalidOperationException("Retry Surface fault"));
            Assert.That(retried, Is.True);
            _session.Pump();
            if (_session.IsRebuilding) _backend.Complete(_backend.Calls.Count - 1);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True, _session.LastError);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(GoalC), "The accepted retry must rebuild its new goal.");
        }

        [Test]
        public void RetryBuildingSubscriberKeepsItsSubmittedRequest()
        {
            _session.ReportFault(new InvalidOperationException("Retry boundary"));
            bool submitted = false;
            _session.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Building || submitted) return;
                submitted = true;
                _session.Submit(FlowFieldRegressionFixture.Request(GoalC));
            };
            _session.RetryFault();
            Assert.That(submitted, Is.True);
            FinishGoal(GoalC);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FaultedBuildCallbackCannotCompleteTheDispatchedRetry(bool failure)
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _session.ReportFault(new InvalidOperationException("Cancel old solve"));
            _session.RetryFault();
            _session.Submit(FlowFieldRegressionFixture.Request(GoalC));
            _session.Pump();
            Assert.That(_backend.Calls.Count, Is.EqualTo(2));
            if (failure) _backend.Fail(0); else _backend.Complete(0);
            Assert.That(_session.IsRebuilding, Is.True, "The old callback must leave the retry running.");
            Assert.That(_session.Revision, Is.Zero);
            _backend.Complete(1);
            AssertGoal(GoalC);
        }

        [Test]
        public void RetryOfAFaultedSolveSolvesTheSameGoalAgain()
        {
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(_session.IsRebuilding, Is.True);
            _session.ReportFault(new InvalidOperationException("Fault during the solve"));
            _session.RetryFault();
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(_session.IsRebuilding, Is.True, "The prepared but unsolved Goal must be solved.");
            _backend.Complete(_backend.Calls.Count - 1);
            AssertGoal(GoalA);
            Assert.That(_session.Sample(new Vector3(0.5f, 0f, 0.5f)).Direction.x, Is.GreaterThan(0f));
        }

        [Test]
        public void LateFaultedSolveCannotOverwriteTheRetryWorkspace()
        {
            FlowFieldSessionRequest Request(Vector3 goal, Vector3 direction)
                => FlowFieldSessionRequest.ForSceneBuild(FlowFieldRegressionFixture.Settings,
                    1 << FlowFieldRegressionFixture.Layer, 1f, 0.5f, 0f, false,
                    FlowFieldGoalPipeline.Resolve(FlowFieldRegressionFixture.Grid, null, true, goal, 10f),
                    direction, FlowFieldDirtyFlags.None, FlowFieldCellRect.Invalid,
                    FlowFieldCellRect.Invalid);
            _session.Submit(Request(GoalA, Vector3.forward));
            _session.Pump();
            _session.ReportFault(new InvalidOperationException("Cancel old solve"));
            _session.RetryFault();
            _session.Submit(Request(GoalC, Vector3.forward));
            FinishGoal(GoalC);
            var position = new Vector3(0.5f, 0f, 0.5f);
            Vector3 expectedDirection = _session.Sample(position).Direction;
            _backend.Complete(0);
            _session.Submit(Request(GoalC, Vector3.left));
            _session.Pump();
            AssertGoal(GoalC);
            Assert.That(_session.Sample(position).Direction, Is.EqualTo(expectedDirection),
                "Final-only composition must retain the retry's navigation, even after late solver writes.");
        }

        [Test]
        public void SynchronousBackendRejectionCannotFaultTheCallbackRetry()
        {
            _session.Dispose();
            _session = new FlowFieldSession(FlowFieldRegressionFixture.Surface(), new RejectingBackend());
            _eventFailures = new FlowFieldRegressionFixture.EventFailures(_session);
            Initialize();
            bool retried = false;
            _session.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Faulted || retried) return;
                retried = true;
                _session.RetryFault();
                _session.Submit(FlowFieldRegressionFixture.Request(GoalC));
            };
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(retried, Is.True);
            Assert.That(_session.IsFaulted, Is.False, _session.LastError);
            _session.Pump();
            AssertGoal(GoalC);
        }

        [Test]
        public void DisposeCannotBeReinitializedFromItsReleasedEvent()
        {
            bool attempted = false;
            bool rejected = false;
            _session.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Released || attempted) return;
                attempted = true;
                try { Initialize(); }
                catch (ObjectDisposedException) { rejected = true; }
                _session.Dispose(); // Repeated disposal must also be harmless.
            };
            _session.Dispose();
            Assert.That(attempted, Is.True);
            Assert.That(rejected, Is.True);
            Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Released));
        }

        private void FinishGoal(Vector3 goal)
        {
            _session.Pump();
            if (_session.IsRebuilding) _backend.Complete(_backend.Calls.Count - 1);
            AssertGoal(goal);
        }

        private void AssertGoal(Vector3 goal)
        {
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(goal));
        }

        private sealed class RejectingBackend : IFlowFieldSolveBackend
        {
            private bool _rejected;

            public bool StartBfs(in FlowFieldBfsRequest request, Action<FlowFieldBfsRequest> completed,
                Action<FlowFieldBfsRequest, Exception> failed)
            {
                if (!_rejected)
                {
                    _rejected = true;
                    failed(request, new InvalidOperationException("Synchronous backend failure"));
                    return false;
                }
                FlowFieldSurfaceBuildPipeline.SolveNow(request);
                completed(request);
                return true;
            }
            public void Poll(bool wait) { }
            public void Cancel() { }
            public void Dispose() { }
        }
    }
}
