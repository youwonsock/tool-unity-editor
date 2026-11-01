using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.FlowField.Tests
{
    public sealed class FlowFieldRequestOwnershipTests
    {
        private static readonly FlowFieldGridSpace Grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, 3, 3, 3, 1f);
        private FlowFieldVolumeSession _session;
        private FlowFieldRegressionFixture.EventFailures _events;
        private GameObject _area;

        [SetUp]
        public void SetUp()
        {
            _session = new FlowFieldVolumeSession();
            _events = new FlowFieldRegressionFixture.EventFailures(_session);
            _session.Initialize(FlowFieldBakeMode.RuntimeDynamic);
        }

        [TearDown]
        public void TearDown()
        {
            _session.Dispose();
            if (_area != null) Object.DestroyImmediate(_area);
            _events.AssertNone();
        }

        private static FlowFieldVolumeRequest Request(FlowFieldGridSpace grid, Vector3? goal = null)
            => FlowFieldVolumeSafetyRegressionTests.Request(grid, goal: goal ?? grid.Origin + Vector3.one * 2.5f);

        [Test]
        public void DirtyInputAfterCancellationUsesTheLatestAcceptedRequest(
            [Values("suspend", "fault")] string transition,
            [Values("obstacle", "modifier")] string trigger,
            [Values(false, true)] bool replaceGrid)
        {
            _session.Submit(Request(Grid));
            FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            FlowFieldGridSpace latestGrid = replaceGrid
                ? FlowFieldGridSpace.FromVolumeCellGrid(new Vector3(100f, 0f, 0f), 4, 4, 4, 1f)
                : Grid;
            Vector3 goal = latestGrid.Origin + new Vector3(0.5f, 2.5f, 2.5f);
            _session.Submit(Request(latestGrid, goal));
            if (transition == "suspend") _session.Suspend();
            else _session.ReportFault(new InvalidOperationException("Expected request cancellation"));
            Assert.That(_session.HasPendingRequest, Is.False);
            Assert.That(_session.TryGetLatestRequestedInput(out var latest), Is.True);
            Assert.That(latest.GoalWorld, Is.EqualTo(goal));
            if (transition == "suspend") _session.Resume();
            if (transition == "fault") _session.RetryFault();

            if (trigger == "obstacle")
                _session.NotifyObstacleRegionDirtyCommand(new Bounds(latestGrid.Origin + Vector3.one * 1.5f, Vector3.one));
            else
            {
                _area = new GameObject("Request ownership modifier");
                _area.layer = FlowFieldRegressionFixture.Layer;
                _area.transform.position = latestGrid.Origin + Vector3.one * 1.5f;
                var collider = _area.AddComponent<BoxCollider>();
                collider.isTrigger = true;
                collider.size = Vector3.one * 4f;
                Physics.SyncTransforms();
                _session.RegisterModifier(new FlowFieldRegressionFixture.Modifier { InfluenceCollider = collider, Priority = 100 });
            }
            Assert.That(_session.HasPendingRequest, Is.True);
            FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            Assert.That(_session.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.Grid.Origin, Is.EqualTo(latestGrid.Origin));
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(goal));
            Assert.That(_session.TrySample(latestGrid.Origin + Vector3.one * 0.5f, out _), Is.True);
        }

        [Test]
        public void RejectedInputCannotReplaceTheAcceptedRequest([Values(false, true)] bool published)
        {
            _session.Submit(Request(Grid));
            if (published) FlowFieldVolumeSafetyRegressionTests.Finish(_session);
            bool pending = _session.HasPendingRequest;
            Assert.Catch<ArgumentException>(() => _session.Submit(Request(Grid, new Vector3(float.NaN, 0f, 0f))));
            Assert.That(_session.IsFaulted, Is.False);
            Assert.That(_session.HasPendingRequest, Is.EqualTo(pending));
            Assert.That(_session.TryGetLatestRequestedInput(out var latest), Is.True);
            Assert.That(latest.GoalWorld, Is.EqualTo(Vector3.one * 2.5f));
            FlowFieldVolumeSafetyRegressionTests.Finish(_session);
        }

        [TestCase("release")]
        [TestCase("dispose")]
        public void SchedulerContinuesAfterPublicationUnregistersItsCurrentSession(string action)
        {
            using (var other = new FlowFieldVolumeSession())
            {
                var otherEvents = new FlowFieldRegressionFixture.EventFailures(other);
                other.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                int before = FlowFieldBuildScheduler.Count;
                _session.AttachRuntimeScheduler();
                other.AttachRuntimeScheduler();
                _session.FieldCommitted += () =>
                {
                    if (action == "release") _session.Release();
                    else _session.Dispose();
                };
                _session.Submit(Request(Grid));
                other.Submit(Request(Grid));
                FlowFieldRegressionFixture.PumpSchedulerUntil(() => other.IsReady);
                Assert.That(_session.State, Is.EqualTo(FlowFieldRuntimeState.Released));
                Assert.That(other.IsReady, Is.True, other.LastError);
                Assert.That(other.Revision, Is.EqualTo(1));
                Assert.That(FlowFieldBuildScheduler.Count, Is.EqualTo(before + 1));
                otherEvents.AssertNone();
            }
        }

        [Test]
        public void NestedSchedulerPumpDoesNotAdvanceAnotherSessionInsidePublication()
        {
            using (var other = new FlowFieldVolumeSession())
            {
                var otherEvents = new FlowFieldRegressionFixture.EventFailures(other);
                other.Initialize(FlowFieldBakeMode.RuntimeDynamic);
                _session.AttachRuntimeScheduler();
                other.AttachRuntimeScheduler();
                int beforeNested = -1;
                int afterNested = -1;
                _session.FieldCommitted += () =>
                {
                    beforeNested = other.Revision;
                    FlowFieldBuildScheduler.PumpAll(100d);
                    afterNested = other.Revision;
                };
                _session.Submit(Request(Grid));
                other.Submit(Request(Grid));
                FlowFieldRegressionFixture.PumpSchedulerUntil(() => _session.IsReady && other.IsReady);
                Assert.That(beforeNested, Is.GreaterThanOrEqualTo(0));
                Assert.That(afterNested, Is.EqualTo(beforeNested));
                Assert.That(_session.Revision, Is.EqualTo(1));
                Assert.That(other.Revision, Is.EqualTo(1));
                otherEvents.AssertNone();
            }
        }
    }

    public sealed class FlowFieldManagerRequestOwnershipTests
    {
        private GameObject _root;
        private FlowFieldManager _manager;
        private FlowFieldRegressionFixture.EventFailures _events;

        private void CreateManager()
        {
            _root = new GameObject("Manager request ownership regression");
            _root.SetActive(false);
            _manager = _root.AddComponent<FlowFieldManager>();
            using (var settings = new SerializedObject(_manager))
            {
                settings.FindProperty("_spaceMode").enumValueIndex = (int)FlowFieldSpaceMode.Volume3D;
                settings.FindProperty("_bakeMode").enumValueIndex = (int)FlowFieldBakeMode.RuntimeDynamic;
                settings.FindProperty("_bakeBoundsLocal").boundsValue = new Bounds(Vector3.one * 1.5f, Vector3.one * 3f);
                settings.FindProperty("_cellSize").floatValue = 1f;
                settings.FindProperty("_obstacleLayer").intValue = 1 << FlowFieldRegressionFixture.Layer;
                settings.FindProperty("_showField").boolValue = false;
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            _events = new FlowFieldRegressionFixture.EventFailures(_manager.VolumeSession);
            _root.SetActive(true);
        }

        private IEnumerator WaitForPublication(int previousRevision)
        {
            float deadline = Time.realtimeSinceStartup + 10f;
            while (_manager.Revision <= previousRevision && !_manager.IsFaulted && Time.realtimeSinceStartup < deadline)
                yield return null;
            Assert.That(_manager.IsReady, Is.True, _manager.LastError);
            Assert.That(_manager.Revision, Is.EqualTo(previousRevision + 1));
        }

        private IEnumerator AssertStable(int revision)
        {
            for (int i = 0; i < 5; i++) yield return null;
            Assert.That(_manager.IsFaulted, Is.False, _manager.LastError);
            Assert.That(_manager.Revision, Is.EqualTo(revision), "Unchanged automatic observations must not publish again.");
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            string callbackFailure = null;
            try { _events?.AssertNone(); }
            catch (AssertionException exception) { callbackFailure = exception.Message; }
            if (Application.isPlaying) yield return new ExitPlayMode();
            Assert.That(callbackFailure, Is.Null);
        }

        [UnityTest]
        public IEnumerator AutomaticDeduplicationPreservesExplicitRebuildAndReinitialization()
        {
            yield return new EnterPlayMode();
            CreateManager();
            yield return WaitForPublication(0);
            yield return AssertStable(1);
            _manager.RequestRebuild();
            yield return WaitForPublication(1);
            yield return AssertStable(2);
            _manager.Release();
            Assert.That(_manager.IsReady, Is.False);
            _manager.Init();
            yield return WaitForPublication(2);
            yield return AssertStable(3);
        }

        [UnityTest]
        public IEnumerator RepeatedInitializationKeepsExactlyOneManagerEventSubscription()
        {
            yield return new EnterPlayMode();
            CreateManager();
            int publications = 0;
            _manager.FieldChanged += () => publications++;
            yield return WaitForPublication(0);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                _manager.Init();
                yield return AssertStable(cycle + 1);
                Assert.That(publications, Is.EqualTo(cycle + 1));
                _manager.Release();
                _manager.Init();
                yield return WaitForPublication(cycle + 1);
                Assert.That(publications, Is.EqualTo(cycle + 2));
            }
            _manager.enabled = false;
            _manager.enabled = true;
            yield return WaitForPublication(4);
            Assert.That(publications, Is.EqualTo(5));
        }

        [UnityTest]
        public IEnumerator RejectedGoalDoesNotReplaceTheCurrentConfiguration()
        {
            yield return new EnterPlayMode();
            CreateManager();
            yield return WaitForPublication(0);
            Vector3 first = Vector3.one * 2.5f;
            _manager.SetGoal(FlowFieldGoalRequest.Position(first));
            yield return WaitForPublication(1);
            Assert.Catch<ArgumentException>(() => _manager.SetGoal(FlowFieldGoalRequest.Position(new Vector3(float.NaN, 0f, 0f))));
            yield return AssertStable(2);
            Assert.That(_manager.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(first));
            Vector3 next = new Vector3(0.5f, 2.5f, 2.5f);
            _manager.SetGoal(FlowFieldGoalRequest.Position(next));
            yield return WaitForPublication(2);
            Assert.That(_manager.TryGetFieldInfo(out info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(next));
        }

        [UnityTest]
        public IEnumerator DisableEnableRebuildsTheAcceptedConfigurationOnce()
        {
            yield return new EnterPlayMode();
            CreateManager();
            yield return WaitForPublication(0);
            _manager.enabled = false;
            Assert.That(_manager.State, Is.EqualTo(FlowFieldRuntimeState.Suspended));
            Assert.That(_manager.VolumeSession.HasPendingRequest, Is.False);
            _manager.enabled = true;
            yield return WaitForPublication(1);
            yield return AssertStable(2);
        }

        [UnityTest]
        public IEnumerator ReentrantGoalChangeDoesNotCauseADuplicateAutomaticPublication()
        {
            yield return new EnterPlayMode();
            CreateManager();
            yield return WaitForPublication(0);
            Vector3 newestGoal = new Vector3(0.5f, 2.5f, 2.5f);
            bool redirected = false;
            _manager.StateChanged += state =>
            {
                if (state != FlowFieldRuntimeState.Building || redirected) return;
                redirected = true;
                _manager.SetGoal(FlowFieldGoalRequest.Position(newestGoal));
            };
            _manager.SetGoal(FlowFieldGoalRequest.Position(Vector3.one * 2.5f));
            FlowFieldVolumeSafetyRegressionTests.Finish(_manager.VolumeSession);
            Assert.That(redirected, Is.True);
            Assert.That(_manager.Revision, Is.EqualTo(2));
            Assert.That(_manager.TryGetFieldInfo(out var info), Is.True);
            Assert.That(info.ResolvedGoalWorld, Is.EqualTo(newestGoal));
            yield return AssertStable(2);
        }
    }
}
