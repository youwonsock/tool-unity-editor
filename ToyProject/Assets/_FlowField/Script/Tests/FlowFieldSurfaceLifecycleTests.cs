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
                FlowFieldBfsBackendPolicy.PreferGpu, null);
        [TearDown]
        public void TearDown()
        {
            _session.Dispose();
            _eventFailures.AssertNone();
        }

        [TestCase("release")]
        [TestCase("suspend")]
        [TestCase("dispose")]
        [TestCase("restart")]
        public void BuildingStateSubscriberEndsTheOldBuild(string transition)
        {
            // Initialize already publishes Building before a result exists.
            // Publish once so the tested transition is Ready -> Building.
            _session.Submit(FlowFieldRegressionFixture.Request());
            _session.Pump();
            Assert.That(_session.IsReady, Is.True, _session.LastError);
            bool interrupted = false;
            _session.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Building || interrupted) return;
                interrupted = true;
                if (transition == "suspend") _session.Suspend();
                else if (transition == "dispose") _session.Dispose();
                else
                {
                    _session.Release();
                    if (transition == "restart")
                    {
                        Initialize();
                        _session.Submit(FlowFieldRegressionFixture.Request(GoalC));
                    }
                }
            };
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            Assert.That(interrupted, Is.True);
            var expected = transition == "suspend" ? FlowFieldRuntimeState.Suspended
                : transition == "restart" ? FlowFieldRuntimeState.Building : FlowFieldRuntimeState.Released;
            Assert.That(_session.State, Is.EqualTo(expected), "The old Pump must preserve the callback's lifecycle decision.");
            Assert.That(_backend.Calls, Is.Empty, "The cancelled build must not dispatch into a new lifetime.");
            if (transition == "restart") FinishGoal(GoalC);
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

        [TestCase("release")]
        [TestCase("suspend")]
        [TestCase("restart")]
        [TestCase("fault")]
        public void ReadyStateSubscriberCancelsTheRemainingPublication(string transition)
        {
            bool interrupted = false;
            int publications = 0;
            _session.FieldCommitted += () => publications++;
            _session.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Ready || interrupted) return;
                interrupted = true;
                if (transition == "suspend") _session.Suspend();
                else if (transition == "fault") _session.ReportFault(new InvalidOperationException("Ready boundary"));
                else
                {
                    _session.Release();
                    if (transition == "restart")
                    {
                        Initialize();
                        _session.Submit(FlowFieldRegressionFixture.Request(GoalC));
                    }
                }
            };
            _session.Submit(FlowFieldRegressionFixture.Request());
            _session.Pump();
            Assert.That(interrupted, Is.True);
            Assert.That(publications, Is.Zero, "An interrupted publication must not emit its remaining event.");
            Assert.That(_session.Revision, Is.Zero);
            if (transition == "restart")
            {
                FinishGoal(GoalC);
                Assert.That(publications, Is.EqualTo(1));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PublicationSubscriberCannotRecursivelyPumpTheNextRequest(bool fromReadyState)
        {
            bool submitted = false;
            bool nestedPump = true;
            int publications = 0;
            Action submit = () =>
            {
                if (submitted) return;
                submitted = true;
                _session.Submit(FlowFieldRegressionFixture.Request(GoalC));
                nestedPump = _session.Pump();
            };
            _session.StateChanged += state => { if (fromReadyState && state == FlowFieldRuntimeState.Ready) submit(); };
            _session.FieldCommitted += () => { publications++; if (!fromReadyState) submit(); };
            _session.Submit(FlowFieldRegressionFixture.Request());
            _session.Pump();
            Assert.That(submitted, Is.True);
            Assert.That(nestedPump, Is.False);
            Assert.That(_backend.Calls, Is.Empty);
            Assert.That(publications, Is.EqualTo(1));
            FinishGoal(GoalC);
            Assert.That(publications, Is.EqualTo(2));
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
                    FlowFieldCellRect.Invalid, 1024);
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
        public void AsyncPublicationRestartDefersItsNestedPump()
        {
            bool restarted = false;
            bool nestedPump = true;
            _session.FieldCommitted += () =>
            {
                if (restarted) return;
                restarted = true;
                _session.Release();
                Initialize();
                _session.Submit(FlowFieldRegressionFixture.Request(GoalC));
                nestedPump = _session.Pump();
            };
            _session.Submit(FlowFieldRegressionFixture.Request(GoalA));
            _session.Pump();
            _backend.Complete(0);
            Assert.That(restarted, Is.True);
            Assert.That(nestedPump, Is.False);
            Assert.That(_backend.Calls.Count, Is.EqualTo(1));
            FinishGoal(GoalC);
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
            _session.Pump(); // PreferGpu retries with the managed backend.
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

        [UnityTest]
        public IEnumerator CancelledGpuResultCannotWriteStaging()
            => VerifyCancelledGpuWrite(width: 3);

        [UnityTest]
        public IEnumerator CancelledGpuWaveLimitCannotStartManagedFallback()
            => VerifyCancelledGpuWrite(width: 80);

        private static IEnumerator VerifyCancelledGpuWrite(int width)
        {
            var shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/_FlowField/Script/Runtime/Core/Solvers/FlowFieldFrontier.compute");
            using (var backend = new FlowFieldSurfaceBuildPipeline(shader))
            {
                if (!backend.SupportsGpu) Assert.Ignore("Compute shaders and async readback are required.");
                var grid = FlowFieldGridSpace.FromCellGrid(Vector3.zero, width, 1, 1f);
                var settings = new FlowFieldSurfaceBakeSettings(grid,
                    new Bounds(new Vector3(width * 0.5f, 0f, 0.5f), new Vector3(width, 4f, 1f)),
                    1 << FlowFieldRegressionFixture.Layer, 45f, 0.5f);
                var baked = new FlowFieldSurfaceBakeResult(grid.CellCount);
                for (int i = 0; i < grid.CellCount; i++) baked.SetSurface(i, 0f, Vector3.up);
                for (int i = 0; i < grid.CellCount; i++)
                    FlowFieldSurfaceBaker.BuildNeighborMask(settings, baked, i, i, 0);
                var surface = FlowFieldSurfaceData.FromRuntime(settings, baked, 1);
                var workspace = new FlowFieldWorkspace();
                workspace.Resize(grid.CellCount);
                try
                {
                    Assert.That(FlowFieldSolver.PrepareGoal(grid, surface, workspace, width - 1, 0, 0f, out int goal), Is.True);
                    var expectedNext = (int[])workspace.NextCells.Clone();
                    var expectedDirections = (Vector3[])workspace.GoalDirections.Clone();
                    bool current = true;
                    int checks = 0;
                    int notifications = 0;
                    var request = new FlowFieldBfsRequest(grid, surface, workspace, true,
                        width - 1, 0, 0f, goal, 64, () => { checks++; return current; });
                    Assert.That(backend.StartBfs(request, _ => notifications++, (_, __) => notifications++), Is.True);
                    Assert.That(checks, Is.EqualTo(1), "The build must reach the asynchronous GPU boundary.");
                    current = false;
                    double deadline = EditorApplication.timeSinceStartup + 30d;
                    while (checks == 1 && EditorApplication.timeSinceStartup < deadline) yield return null;
                    Assert.That(checks, Is.GreaterThan(1), "GPU completion did not check request ownership.");
                    Assert.That(notifications, Is.Zero);
                    Assert.That(workspace.NextCells, Is.EqualTo(expectedNext));
                    Assert.That(workspace.GoalDirections, Is.EqualTo(expectedDirections));
                    Assert.That(backend.GpuDisabled, Is.False, "A cancelled result must not downgrade the next build.");
                }
                finally { workspace.Release(); }
            }
        }

        private sealed class RejectingBackend : IFlowFieldGpuBackend
        {
            public bool SupportsGpu => true;
            public bool StartBfs(in FlowFieldBfsRequest request, Action<FlowFieldBfsRequest> completed,
                Action<FlowFieldBfsRequest, Exception> failed, bool allowManagedFallback = true)
            {
                failed(request, new InvalidOperationException("Synchronous backend failure"));
                return false;
            }
            public void Dispose() { }
        }
    }
}
