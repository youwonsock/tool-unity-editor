using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.TransformPath.Tests
{
    /// <summary>Components wired together and ticked by the real runtime driver.</summary>
    public sealed class PathIntegrationTests : PathBoundaryIsolatedTest
    {
        private PathTestScene _scene;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Object> _assets = new List<Object>();
        private PathRuntimeDriver _driver;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            _scene = new PathTestScene();
            _driver = new PathRuntimeDriver();
            _frame = 0;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null)
                    Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            for (int i = _assets.Count - 1; i >= 0; i--)
                if (_assets[i] != null)
                    Object.DestroyImmediate(_assets[i]);
            _assets.Clear();
            _scene.Dispose();
        }

        private PathFollower CreateFollower()
        {
            var host = new GameObject("Follower");
            _objects.Add(host);
            PathFollower follower = host.AddComponent<PathFollower>();
            follower.Initialize();
            _driver.Register(follower);
            return follower;
        }

        private PathQueueMember CreateMember()
        {
            PathFollower follower = CreateFollower();
            return follower.gameObject.AddComponent<PathQueueMember>();
        }

        private QueuedPathManager CreateManager(PathProviderBehaviour route)
        {
            var host = new GameObject("Queue");
            _objects.Add(host);
            QueuedPathManager manager = host.AddComponent<QueuedPathManager>();
            manager.Initialize();
            manager.ConfigureRoute(route);
            _driver.Register(manager);
            return manager;
        }

        // The driver's frame: effects, queue, movement, with its snapshot rules.
        private void Frames(int count, float deltaTime = 1f / 60f)
        {
            for (int n = 0; n < count; n++)
            {
                _driver.Tick(deltaTime, ++_frame);
                if (_driver.Faults.Count > 0)
                    Assert.Fail($"Frame {_frame} faulted: {_driver.Faults[0]}");
            }
        }

        private PathData Line(float length, float speed) =>
            _scene.CreatePath(PathMovementSettings.Speed(speed), EPathCurveType.Linear,
                Vector3.zero, new Vector3(length, 0f, 0f));

        [Test]
        public void PausedMember_StaysAnObstacle()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember leader = CreateMember();
            PathQueueMember follower = CreateMember();
            leader.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            follower.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            leader.Follower.Seek(0.3f);
            follower.Follower.Seek(0.1f);
            Frames(30);

            leader.Follower.Pause();
            Frames(300);
            float settled = follower.Follower.Progress;
            Frames(60);

            float gap = (leader.Follower.Progress - follower.Follower.Progress) * 30f;
            Assert.AreEqual(EPathFollowerState.Paused, leader.Follower.State);
            Assert.GreaterOrEqual(gap, PathConstants.DEFAULT_QUEUE_SPACING - 0.01f);
            // Held behind the paused leader, not passing through it.
            Assert.AreEqual(settled, follower.Follower.Progress, 1e-5f);
        }

        [Test]
        public void LoopingMember_StopsAtTheSeam_ThenWaitsForTheQueue()
        {
            PathData path = Line(10f, 9f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path, true));
            member.Follower.Seek(0.95f);
            int epoch = member.Follower.MotionEpoch;
            for (int i = 0; i < 120 && member.Follower.MotionEpoch == epoch; i++)
                Frames(1);

            // The frame that crossed the seam dropped its remaining time there.
            Assert.AreEqual(epoch + 1, member.Follower.MotionEpoch);
            Assert.AreEqual(0f, member.Follower.Progress, 1e-5f);
            Frames(3);
            Assert.AreEqual(0f, member.Follower.Progress, 1e-5f);
            Frames(60);
            Assert.Greater(member.Follower.Progress, 0.05f);
        }

        [Test]
        public void SeekingMember_WaitsTheRestartDelay_ThenMovesOn()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(30);
            Assert.Greater(member.Follower.Progress, 0f);

            member.Follower.Seek(0.5f);
            // The default restart delay is 0.1 seconds, six frames.
            Frames(5);
            Assert.AreEqual(0.5f, member.Follower.Progress, 1e-5f);
            Frames(30);

            Assert.Greater(member.Follower.Progress, 0.5f);
        }

        [Test]
        public void InvalidQueuedRequest_LeavesTheMemberWhereItWas()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager first = CreateManager(path);
            var otherHost = new GameObject("Other Queue");
            _objects.Add(otherHost);
            QueuedPathManager other = otherHost.AddComponent<QueuedPathManager>();
            other.Initialize();
            _driver.Register(other);
            PathQueueMember member = CreateMember();
            member.StartQueuedPlayback(first, PathPlaybackRequest.Single(path));
            Frames(30);
            float progress = member.Follower.Progress;

            Assert.Catch<System.ArgumentException>(() => member.StartQueuedPlayback(
                other, PathPlaybackRequest.Aggregate(path, default(PathMovementSettings))));

            Assert.IsNull(other.RouteProvider);
            Assert.IsTrue(member.IsInQueue);
            Assert.AreEqual(1, first.MemberCount);
            Frames(30);
            Assert.Greater(member.Follower.Progress, progress);
        }

        [Test]
        public void DestroyedPath_StopsItsFollowers()
        {
            PathData path = Line(10f, 2f);
            PathFollower follower = CreateFollower();
            follower.Play(PathPlaybackRequest.Single(path));
            Frames(10);

            DestroyProvider(path);
            Frames(1);

            Assert.AreEqual(EPathFollowerState.Ready, follower.State);
        }

        [Test]
        public void Sequence_ComesBack_WhenItsPathRecoversUnchanged()
        {
            PathData first = Line(5f, 2f);
            PathData second = _scene.CreatePath(PathMovementSettings.Speed(2f), EPathCurveType.Linear,
                new Vector3(5f, 0f, 0f), new Vector3(9f, 0f, 0f));
            var host = new GameObject("Sequence");
            _objects.Add(host);
            MultiPathData sequence = host.AddComponent<MultiPathData>();
            sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(second) });
            Assert.IsTrue(sequence.IsReady);

            var points = new List<Transform>((IEnumerable<Transform>)GetPoints(second));
            Transform removed = points[1];
            LogAssert.Expect(LogType.Error, new Regex("could not build"));
            second.SetPathPoints(new[] { points[0] });
            Assert.IsFalse(sequence.IsReady);

            second.SetPathPoints(new[] { points[0], removed });
            Assert.IsTrue(sequence.IsReady);
        }

        [Test]
        public void SequenceQueue_KeepsMembersOnGeometryChange_ReleasesThemOnSegmentChange()
        {
            PathData first = Line(5f, 2f);
            PathData second = _scene.CreatePath(PathMovementSettings.Speed(2f), EPathCurveType.Linear,
                new Vector3(5f, 0f, 0f), new Vector3(9f, 0f, 0f));
            PathData third = _scene.CreatePath(PathMovementSettings.Speed(2f), EPathCurveType.Linear,
                new Vector3(5f, 0f, 0f), new Vector3(12f, 0f, 0f));
            var host = new GameObject("Sequence");
            _objects.Add(host);
            MultiPathData sequence = host.AddComponent<MultiPathData>();
            sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(second) });
            QueuedPathManager manager = CreateManager(sequence);
            PathQueueMember member = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            member.QueueInvalidated += (_, reason) => reasons.Add(reason);
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Sequence(sequence));
            Frames(10);

            // Only the geometry changes: the members keep their place.
            GetPoints(second)[1].position = new Vector3(10f, 0f, 0f);
            second.Rebuild();
            Frames(2);
            Assert.IsTrue(member.IsInQueue);
            Assert.IsEmpty(reasons);

            // Another path in a segment: the manager lets everyone go.
            sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(third) });
            Frames(1);
            CollectionAssert.AreEqual(new[] { EPathQueueLeaveReason.ManagerReleased }, reasons);
            Assert.IsFalse(member.IsInQueue);
        }

        [Test]
        public void PausedMember_StopsWhenItsRouteGoesAway()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            member.QueueInvalidated += (_, reason) => reasons.Add(reason);
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(10);
            member.Follower.Pause();

            DestroyProvider(path);
            Frames(1);

            CollectionAssert.AreEqual(new[] { EPathQueueLeaveReason.ProviderInvalid }, reasons);
            Assert.AreEqual(EPathFollowerState.Ready, member.Follower.State);
        }

        [Test]
        public void FollowerOnAnotherRoute_KeepsPlaying_WhenTheQueueDropsIt()
        {
            PathData path = Line(30f, 4f);
            PathData other = Line(20f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            member.QueueInvalidated += (_, reason) => reasons.Add(reason);
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(10);

            member.Follower.Play(PathPlaybackRequest.Single(other));
            Frames(1);

            CollectionAssert.AreEqual(new[] { EPathQueueLeaveReason.ProviderInvalid }, reasons);
            Assert.IsFalse(member.IsInQueue);
            Assert.AreEqual(EPathFollowerState.Moving, member.Follower.State);
            Assert.AreSame(other, member.Follower.CurrentProvider);
        }

        [Test]
        public void DefinitionChange_ReleasesOnlyMembersStillOnTheOldDefinition()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember restarted = CreateMember();
            PathQueueMember paused = CreateMember();
            var reasons = new List<string>();
            restarted.QueueInvalidated += (_, reason) => reasons.Add($"restarted {reason}");
            paused.QueueInvalidated += (_, reason) => reasons.Add($"paused {reason}");
            restarted.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            paused.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            restarted.Follower.Seek(0.4f);
            Frames(10);
            paused.Follower.Pause();

            path.MovementSettings = PathMovementSettings.Speed(6f);
            restarted.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(1);

            CollectionAssert.AreEqual(new[] { "paused ManagerReleased" }, reasons);
            Assert.IsTrue(restarted.IsInQueue);
            Assert.AreEqual(EPathFollowerState.Moving, restarted.Follower.State);
            Assert.IsFalse(paused.IsInQueue);
            Assert.AreEqual(EPathFollowerState.Ready, paused.Follower.State);
        }

        [Test]
        public void GeometryChangingEveryFrame_StillLetsTheQueueMove()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Transform end = GetPoints(path)[1];

            for (int i = 1; i <= 60; i++)
            {
                end.position = new Vector3(30f + i * 0.01f, 0f, 0f);
                path.Rebuild();
                Frames(1);
            }

            Assert.IsTrue(member.IsInQueue);
            Assert.Greater(member.Follower.Progress, 0.05f);
        }

        [Test]
        public void SpacingPolicy_DecidesTheGap_FromTheMembersOwnSpacing()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember leader = CreateMember();
            PathQueueMember follower = CreateMember();
            follower.UseManagerSpacing = false;
            follower.ActorSpacing = 1f;
            float resolvedForFollower = -1f;
            manager.SpacingPolicy = CreatePolicy("Double Gap", (member, ahead, resolved) =>
            {
                if (member == follower)
                    resolvedForFollower = resolved;
                return ahead == leader ? resolved * 2f : resolved;
            });
            leader.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            follower.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            leader.Follower.Seek(0.3f);
            follower.Follower.Seek(0.1f);
            Frames(30);

            leader.Follower.Pause();
            Frames(300);

            Assert.AreEqual(1f, resolvedForFollower);
            Assert.IsTrue(manager.TryGetMemberStatus(follower, out PathQueueStatus status));
            Assert.AreEqual(2f, status.Spacing);
            float gap = (leader.Follower.Progress - follower.Follower.Progress) * 30f;
            Assert.AreEqual(2f, gap, 0.05f);
        }

        [Test]
        public void SpacingPolicy_InvalidSpacing_StopsTheManagerFrameWithThePolicyName()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            manager.SpacingPolicy = CreatePolicy("Broken Gap", (_, __, ___) => float.NaN);
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));

            var error = Assert.Throws<System.InvalidOperationException>(
                () => manager.Tick(EPathTickPhase.Queue, 1f / 60f, ++_frame));

            StringAssert.Contains("Broken Gap", error.Message);
        }

        [Test]
        public void SpacingPolicy_CannotChangeTheLineItIsAskedAbout()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            manager.SpacingPolicy = CreatePolicy("Leaving Gap", (asked, _, resolved) =>
            {
                asked.Leave();
                return resolved;
            });
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));

            var error = Assert.Throws<System.InvalidOperationException>(
                () => manager.Tick(EPathTickPhase.Queue, 1f / 60f, ++_frame));

            StringAssert.Contains("spacing policy runs", error.Message);
            Assert.IsTrue(member.IsInQueue);
            Assert.AreEqual(1, manager.MemberCount);
            manager.SpacingPolicy = null;
            Frames(30);
            Assert.Greater(member.Follower.Progress, 0f);
        }

        [Test]
        public void CancelEventEffects_DropsTheBlendAndDelayedEvents_AndKeepsTheState()
        {
            PathData path = Line(30f, 2f);
            PathEventSettingSO faster = _scene.CreateEvent("faster");
            faster.UseSpeedChange = true;
            faster.SpeedTarget = 4f;
            faster.SpeedBlendTime = 1f;
            faster.SpeedBlendCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            faster.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = 0.5f, EventSetting = _scene.CreateEvent("late") });
            PathFollower follower = CreateFollower();
            var heard = new List<string>();
            follower.EventReached += eventName => heard.Add(eventName);
            follower.Play(PathPlaybackRequest.Single(path));
            Frames(1);
            follower.TriggerEvent(faster);
            Frames(15);
            Assert.IsTrue(follower.HasPendingEventEffects);
            float speed = follower.Speed;
            var states = new List<EPathFollowerState>();
            follower.StateChanged += state => states.Add(state);

            Assert.IsTrue(follower.CancelEventEffects());
            Assert.IsFalse(follower.HasPendingEventEffects);
            Assert.IsFalse(follower.CancelEventEffects());
            Frames(60);

            Assert.Greater(speed, 2f);
            Assert.AreEqual(speed, follower.Speed);
            Assert.AreEqual(EPathFollowerState.Moving, follower.State);
            CollectionAssert.IsEmpty(states);
            CollectionAssert.AreEqual(new[] { "faster" }, heard);
        }

        [Test]
        public void PlayOnStart_PlaysItsProvider_ButNotForAQueueMemberWithAManager()
        {
            PathData path = Line(10f, 2f);
            PathFollower solo = CreateFollower();
            SetPlayOnStart(solo, path);
            Assert.IsTrue(solo.TryPlayOnStart());
            Assert.AreEqual(EPathFollowerState.Moving, solo.State);
            Assert.AreSame(path, solo.CurrentProvider);

            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            member.Manager = manager;
            SetPlayOnStart(member.Follower, path);
            LogAssert.Expect(LogType.Warning, new Regex("skips Play On Start"));
            Assert.IsFalse(member.Follower.TryPlayOnStart());
            Assert.AreEqual(EPathFollowerState.Ready, member.Follower.State);

            var brokenHost = new GameObject("Broken Path");
            _objects.Add(brokenHost);
            PathData broken = brokenHost.AddComponent<PathData>();
            PathFollower waiting = CreateFollower();
            SetPlayOnStart(waiting, broken);
            LogAssert.Expect(LogType.Error, new Regex("could not build"));
            LogAssert.Expect(LogType.Error, new Regex("cannot play on start"));
            Assert.IsFalse(waiting.TryPlayOnStart());
            Assert.AreEqual(EPathFollowerState.Ready, waiting.State);
        }

        [Test]
        public void OverrideSegment_KeepsPlaying_WhenItsPathsOwnMovementChanges()
        {
            PathData first = Line(5f, 2f);
            PathData second = _scene.CreatePath(PathMovementSettings.Speed(2f), EPathCurveType.Linear,
                new Vector3(5f, 0f, 0f), new Vector3(9f, 0f, 0f));
            MultiPathData sequence = _scene.CreateSequence();
            sequence.SetSegments(new[]
            {
                new PathSegmentAuthoring(first, PathMovementSettings.Speed(3f)),
                new PathSegmentAuthoring(second),
            });
            PathFollower follower = CreateFollower();
            follower.Play(PathPlaybackRequest.Sequence(sequence));
            Frames(10);

            first.MovementSettings = PathMovementSettings.Speed(8f);
            Frames(1);
            Assert.AreEqual(EPathFollowerState.Moving, follower.State);

            // The second segment takes its path's movement, so a change there stops the playback.
            second.MovementSettings = PathMovementSettings.Speed(8f);
            Frames(1);
            Assert.AreEqual(EPathFollowerState.Ready, follower.State);
        }

        [Test]
        public void DisabledLineRenderer_DrawsNothing()
        {
            PathData path = Line(10f, 2f);
            var host = new GameObject("Line");
            _objects.Add(host);
            LineRenderer line = host.AddComponent<LineRenderer>();
            PathDataLineRenderer drawer = host.AddComponent<PathDataLineRenderer>();
            var serialized = new UnityEditor.SerializedObject(drawer);
            serialized.FindProperty("_pathData").objectReferenceValue = path;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            drawer.Init();
            drawer.SetVisible(true);
            Assert.IsTrue(line.enabled);
            Assert.AreEqual(path.SamplePointCount, line.positionCount);

            drawer.enabled = false;
            drawer.SetVisible(true);
            drawer.Refresh();

            Assert.IsFalse(line.enabled);
        }

        [Test]
        public void StartRightAfterTheRouteIsDestroyed_StillReleasesTheOldMembersAsProviderInvalid()
        {
            PathData path = Line(30f, 4f);
            PathData next = Line(20f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember old = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            old.QueueInvalidated += (_, reason) => reasons.Add(reason);
            old.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(5);

            DestroyProvider(path);
            // Before the manager's next Queue phase.
            PathQueueMember newcomer = CreateMember();
            newcomer.StartQueuedPlayback(manager, PathPlaybackRequest.Single(next));

            CollectionAssert.AreEqual(new[] { EPathQueueLeaveReason.ProviderInvalid }, reasons);
            Assert.AreSame(next, manager.RouteProvider);
            Assert.IsTrue(newcomer.IsInQueue);
        }

        [Test]
        public void StartOnAnotherProviderThanTheInspectorRoute_ThrowsAndChangesNothing()
        {
            PathData template = Line(30f, 4f);
            Transform[] points = { template.transform.GetChild(0), template.transform.GetChild(1) };
            PathData other = Line(20f, 4f);
            var pathHost = new GameObject("Unbuilt Route");
            _objects.Add(pathHost);
            PathData inspectorRoute = pathHost.AddComponent<PathData>();
            var host = new GameObject("Queue");
            _objects.Add(host);
            QueuedPathManager manager = host.AddComponent<QueuedPathManager>();
            var serialized = new UnityEditor.SerializedObject(manager);
            serialized.FindProperty("_routeProviderObject").objectReferenceValue = inspectorRoute;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            manager.Initialize();
            _driver.Register(manager);
            // Points arrive later and nothing has asked the route to build yet.
            inspectorRoute.SetPathPoints(points);
            Assert.IsTrue(ReferenceEquals(manager.RouteProvider, null));
            PathQueueMember member = CreateMember();

            Assert.Throws<System.InvalidOperationException>(
                () => member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(other)));

            Assert.IsTrue(ReferenceEquals(manager.RouteProvider, null));
            Assert.AreEqual(EPathFollowerState.Ready, member.Follower.State);
        }

        [Test]
        public void DestroyedRoute_StopsEveryMember_AndForgetsTheRoute()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember moving = CreateMember();
            PathQueueMember paused = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            moving.QueueInvalidated += (_, reason) => reasons.Add(reason);
            paused.QueueInvalidated += (_, reason) => reasons.Add(reason);
            moving.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            moving.Follower.Seek(0.4f);
            paused.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(10);
            paused.Follower.Pause();

            DestroyProvider(path);
            Frames(1);

            Assert.AreEqual(0, manager.MemberCount);
            Assert.IsFalse(moving.IsInQueue || paused.IsInQueue);
            Assert.AreEqual(EPathFollowerState.Ready, paused.Follower.State);
            Assert.IsTrue(ReferenceEquals(manager.RouteProvider, null));
            Assert.AreEqual(2, reasons.Count);
        }

        [Test]
        public void DisabledMember_CannotJoin()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            member.enabled = false;

            Assert.Throws<System.InvalidOperationException>(
                () => member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path)));
            Assert.AreEqual(0, manager.MemberCount);
            Assert.AreEqual(EPathFollowerState.Ready, member.Follower.State);
        }

        [Test]
        public void DestroyedMember_LeavesAndFreesItsFollower()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember gone = CreateMember();
            PathQueueMember behind = CreateMember();
            gone.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            gone.Follower.Seek(0.5f);
            behind.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            behind.Follower.Seek(0.4f);
            PathFollower freed = gone.Follower;
            gone.Hold();
            Frames(30);

            DestroyMember(gone);
            Frames(30);

            Assert.AreEqual(1, manager.MemberCount);
            Assert.Greater(freed.Progress, 0.5f);
        }

        [Test]
        public void DefinitionChangeInACallback_ReleasesEveryMemberTheSameWay()
        {
            PathData path = Line(30f, 4f);
            PathEventSettingSO mark = _scene.CreateEvent("mark");
            _scene.SetEvents(path, (0.3f, mark));
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember first = CreateMember();
            PathQueueMember second = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            first.QueueInvalidated += (_, reason) => reasons.Add(reason);
            second.QueueInvalidated += (_, reason) => reasons.Add(reason);
            first.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            first.Follower.Seek(0.29f);
            second.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(10);
            // Runs after the first follower's own move, before the second one moves.
            first.Follower.EventReached += _ => path.MovementSettings = PathMovementSettings.Speed(7f);

            Frames(30);

            CollectionAssert.AreEqual(
                new[] { EPathQueueLeaveReason.ManagerReleased, EPathQueueLeaveReason.ManagerReleased }, reasons);
        }

        [Test]
        public void PausedMember_ReportsBlocked()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            int blocked = 0;
            member.Blocked += _ => blocked++;
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(30);
            blocked = 0;

            member.Follower.Pause();
            Frames(2);

            Assert.IsTrue(member.IsBlocked);
            Assert.AreEqual(1, blocked);
        }

        [Test]
        public void ShapeThatThrowsForOneMember_StopsOnlyThatMember()
        {
            var host = new GameObject("Shape");
            _objects.Add(host);
            TestCircleShape shape = host.AddComponent<TestCircleShape>();
            shape.MovementSettings = PathMovementSettings.Speed(1f);
            QueuedPathManager manager = CreateManager(shape);
            PathQueueMember ahead = CreateMember();
            PathQueueMember behind = CreateMember();
            ahead.StartQueuedPlayback(manager, PathPlaybackRequest.Shape(shape));
            ahead.Follower.Seek(0.5f);
            behind.StartQueuedPlayback(manager, PathPlaybackRequest.Shape(shape));
            behind.Follower.Seek(0.1f);
            Frames(10);
            float length = shape.PathLength;

            // The new geometry throws only around the member ahead.
            shape.BreakBetween = new Vector2(0.45f * length, 0.6f * length);
            host.transform.position += Vector3.right;
            shape.Rebuild();
            LogAssert.Expect(LogType.Exception, new Regex("The test broke this shape"));
            _driver.Tick(1f / 60f, ++_frame);
            float progress = behind.Follower.Progress;
            Frames(10);

            Assert.AreEqual(EPathFollowerState.Ready, ahead.Follower.State);
            Assert.AreEqual(EPathFollowerState.Moving, behind.Follower.State);
            Assert.Greater(behind.Follower.Progress, progress);
            Assert.IsTrue(behind.IsInQueue);
        }

        [Test]
        public void InspectorRoute_BuildsOnTheFirstFrame_NotAtInitialize()
        {
            PathData path = _scene.CreatePath(PathMovementSettings.Speed(2f), EPathCurveType.Linear,
                Vector3.zero, new Vector3(10f, 0f, 0f));
            Transform[] points = { path.transform.GetChild(0), path.transform.GetChild(1) };
            var host = new GameObject("Queue");
            _objects.Add(host);
            var pathHost = new GameObject("Unbuilt Path");
            _objects.Add(pathHost);
            PathData unbuilt = pathHost.AddComponent<PathData>();
            QueuedPathManager manager = host.AddComponent<QueuedPathManager>();
            var serialized = new UnityEditor.SerializedObject(manager);
            serialized.FindProperty("_routeProviderObject").objectReferenceValue = unbuilt;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // No points yet: nothing is built or logged.
            manager.Initialize();
            _driver.Register(manager);
            Assert.IsNull(manager.RouteProvider);

            unbuilt.SetPathPoints(points);
            Frames(1);

            Assert.AreSame(unbuilt, manager.RouteProvider);
        }

        private static void SetPlayOnStart(PathFollower follower, PathProviderBehaviour provider)
        {
            var serialized = new UnityEditor.SerializedObject(follower);
            serialized.FindProperty("_playOnStart").boolValue = true;
            serialized.FindProperty("_playOnStartProvider").objectReferenceValue = provider;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [Test]
        public void DisabledFollower_CannotJoin()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            member.Follower.enabled = false;

            Assert.Throws<System.InvalidOperationException>(
                () => member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path)));
            Assert.AreEqual(0, manager.MemberCount);
            Assert.AreEqual(EPathFollowerState.Ready, member.Follower.State);
        }

        [Test]
        public void PolicyThatDisablesAMember_RemovesItAfterTheLine()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember leader = CreateMember();
            PathQueueMember disabled = CreateMember();
            PathQueueMember behind = CreateMember();
            leader.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            leader.Follower.Seek(0.6f);
            disabled.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            disabled.Follower.Seek(0.4f);
            behind.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            behind.Follower.Seek(0.2f);
            bool done = false;
            manager.SpacingPolicy = CreatePolicy("Disabler", (member, ahead, spacing) =>
            {
                if (!done && ReferenceEquals(member, disabled))
                {
                    done = true;
                    DisableMember(disabled);
                }
                return spacing;
            });

            Frames(2);

            Assert.IsTrue(done);
            Assert.IsFalse(disabled.IsInQueue);
            Assert.AreEqual(2, manager.MemberCount);
            Assert.AreSame(leader, manager.GetMember(0));
            Assert.AreSame(behind, manager.GetMember(1));
            Assert.IsTrue(manager.TryGetMemberStatus(behind, out PathQueueStatus status));
            Assert.AreSame(leader, status.Ahead);
        }

        [Test]
        public void PolicyThatReleasesTheManager_ReleasesTheMembersAfterTheLine()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember leader = CreateMember();
            PathQueueMember behind = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            leader.QueueInvalidated += (member, reason) => reasons.Add(reason);
            behind.QueueInvalidated += (member, reason) => reasons.Add(reason);
            leader.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            leader.Follower.Seek(0.5f);
            behind.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            bool released = false;
            // What a disable or destroy from inside the policy runs.
            manager.SpacingPolicy = CreatePolicy("Releaser", (member, ahead, spacing) =>
            {
                if (!released)
                {
                    released = true;
                    manager.Release();
                }
                return spacing;
            });

            Frames(2);

            Assert.IsTrue(released);
            Assert.IsFalse(manager.IsInitialized);
            Assert.AreEqual(0, manager.MemberCount);
            Assert.IsFalse(leader.IsInQueue || behind.IsInQueue);
            CollectionAssert.AreEqual(
                new[] { EPathQueueLeaveReason.ManagerReleased, EPathQueueLeaveReason.ManagerReleased }, reasons);
        }

        [Test]
        public void StartOnAnotherRoute_AfterTheRouteIsDestroyed_ThrowsBeforeReleasingMembers()
        {
            PathData inspectorRoute = Line(30f, 4f);
            PathData runtimeRoute = Line(30f, 4f);
            PathData other = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(runtimeRoute);
            typeof(QueuedPathManager)
                .GetField("_routeProviderObject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(manager, inspectorRoute);
            PathQueueMember queued = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            queued.QueueInvalidated += (member, reason) => reasons.Add(reason);
            queued.StartQueuedPlayback(manager, PathPlaybackRequest.Single(runtimeRoute));
            Frames(5);
            DestroyProvider(runtimeRoute);
            PathQueueMember starter = CreateMember();

            Assert.Throws<System.InvalidOperationException>(
                () => starter.StartQueuedPlayback(manager, PathPlaybackRequest.Single(other)));
            Assert.AreEqual(1, manager.MemberCount);
            Assert.IsEmpty(reasons);

            Frames(1);
            Assert.AreEqual(0, manager.MemberCount);
            CollectionAssert.AreEqual(new[] { EPathQueueLeaveReason.ProviderInvalid }, reasons);
        }

        [Test]
        public void PolicyThatDisablesAndEnablesTheManager_ReleasesTheMembers_AndStaysUsable()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember first = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            first.QueueInvalidated += (member, reason) => reasons.Add(reason);
            first.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            bool toggled = false;
            // What SetActive(false) then SetActive(true) from inside the policy runs.
            manager.SpacingPolicy = CreatePolicy("Toggler", (member, ahead, spacing) =>
            {
                if (!toggled)
                {
                    toggled = true;
                    manager.Release();
                    manager.Initialize();
                }
                return spacing;
            });

            Frames(2);

            Assert.IsTrue(toggled);
            Assert.IsTrue(manager.IsInitialized);
            Assert.AreEqual(0, manager.MemberCount);
            CollectionAssert.AreEqual(new[] { EPathQueueLeaveReason.ManagerReleased }, reasons);
            PathQueueMember next = CreateMember();
            Assert.IsTrue(next.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path)));
            Frames(1);
            Assert.IsTrue(next.IsInQueue);
        }

        [Test]
        public void MembersRestartedFromCompleted_TakeTurnsWithThoseWaitingAtTheStart()
        {
            // Six members with 1.5 m spacing do not fit on 6 m, so some wait at the start.
            PathData path = Line(6f, 4f);
            QueuedPathManager manager = CreateManager(path);
            var members = new PathQueueMember[6];
            var laps = new int[members.Length];
            for (int i = 0; i < members.Length; i++)
            {
                PathQueueMember member = CreateMember();
                int index = i;
                member.Follower.Completed += () =>
                {
                    laps[index]++;
                    member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                };
                members[i] = member;
            }
            foreach (PathQueueMember member in members)
                member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));

            Frames(1800);

            // A restart goes behind the members already waiting instead of passing them again.
            foreach (int lap in laps)
                Assert.GreaterOrEqual(lap, 3);
        }

        [Test]
        public void ManagerReleasedOrRouteDestroyedInACompletedListener_LetsTheMemberLeaveAsCompleted()
        {
            PathData released = Line(3f, 4f);
            QueuedPathManager releasedManager = CreateManager(released);
            PathQueueMember first = CreateMember();
            PathData destroyed = Line(3f, 4f);
            QueuedPathManager destroyedManager = CreateManager(destroyed);
            PathQueueMember second = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            first.QueueInvalidated += (member, reason) => reasons.Add(reason);
            second.QueueInvalidated += (member, reason) => reasons.Add(reason);
            first.Follower.Completed += () => releasedManager.Release();
            second.Follower.Completed += () => DestroyProvider(destroyed);
            first.StartQueuedPlayback(releasedManager, PathPlaybackRequest.Single(released));
            second.StartQueuedPlayback(destroyedManager, PathPlaybackRequest.Single(destroyed));

            Frames(90);

            Assert.AreEqual(EPathPlaybackEnd.Completed, first.Follower.LastPlaybackEnd);
            Assert.AreEqual(EPathPlaybackEnd.Completed, second.Follower.LastPlaybackEnd);
            Assert.IsFalse(first.IsInQueue || second.IsInQueue);
            Assert.IsEmpty(reasons);
        }

        [Test]
        public void ValueBlend_EndsAtTheSegmentEnd_SoTheNextSegmentKeepsItsOwnDuration()
        {
            PathData first = _scene.CreatePath(PathMovementSettings.Time(10f), EPathCurveType.Linear,
                Vector3.zero, new Vector3(10f, 0f, 0f));
            PathData second = _scene.CreatePath(PathMovementSettings.Time(1f), EPathCurveType.Linear,
                new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f));
            PathEventSettingSO slow = _scene.CreateEvent("Slow");
            slow.UseDurationChange = true;
            slow.DurationTarget = 20f;
            slow.DurationBlendTime = 5f;
            _scene.SetEvents(first, (0.9f, slow));
            MultiPathData sequence = _scene.CreateSequence();
            sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(second) });
            PathFollower follower = CreateFollower();
            follower.Play(PathPlaybackRequest.Sequence(sequence));

            for (int i = 0; i < 2000 && follower.SegmentIndex == 0; i++)
                Frames(1);
            Frames(1);

            Assert.AreEqual(1, follower.SegmentIndex);
            Assert.AreEqual(1f, follower.Duration, 1e-5f);
            Assert.IsFalse(follower.HasPendingEventEffects);
        }

        [Test]
        public void SeekIntoASegmentThatPreservesSpeed_TakesTheSpeedItHasNow()
        {
            PathData first = _scene.CreatePath(PathMovementSettings.Speed(2f), EPathCurveType.Linear,
                Vector3.zero, new Vector3(10f, 0f, 0f));
            PathData second = _scene.CreatePath(PathMovementSettings.Speed(5f), EPathCurveType.Linear,
                new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f));
            PathData third = _scene.CreatePath(PathMovementSettings.Speed(3f), EPathCurveType.Linear,
                new Vector3(20f, 0f, 0f), new Vector3(30f, 0f, 0f));
            MultiPathData sequence = _scene.CreateSequence();
            sequence.SetSegments(new[]
            {
                new PathSegmentAuthoring(first), new PathSegmentAuthoring(second, true), new PathSegmentAuthoring(third),
            });
            PathFollower follower = CreateFollower();
            follower.Play(PathPlaybackRequest.Sequence(sequence));

            follower.SeekSegment(1, 0.1f);
            Assert.AreEqual(2f, follower.Speed, 1e-5f);
            follower.SeekSegment(2, 0.1f);
            Assert.AreEqual(3f, follower.Speed, 1e-5f);
        }

        [Test]
        public void PausedFollower_DoesNotSampleASegmentWhosePathLeftAndFailed()
        {
            PathData first = Line(10f, 2f);
            PathData second = _scene.CreatePath(PathMovementSettings.Speed(2f), EPathCurveType.Linear,
                new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f));
            PathData replacement = _scene.CreatePath(PathMovementSettings.Speed(2f), EPathCurveType.Linear,
                new Vector3(10f, 0f, 0f), new Vector3(10f, 0f, 10f));
            MultiPathData sequence = _scene.CreateSequence();
            sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(second) });
            PathFollower follower = CreateFollower();
            follower.Play(PathPlaybackRequest.Sequence(sequence));
            follower.SeekSegment(1, 0.5f);
            follower.Pause();
            sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(replacement) });

            LogAssert.Expect(LogType.Error, new Regex("could not build"));
            second.SetPathPoints(new Transform[0]);

            Assert.IsFalse(follower.AlignRotationToPathDirection());
        }

        [Test]
        public void DisabledFollower_CannotPlay()
        {
            PathData path = Line(10f, 2f);
            PathFollower follower = CreateFollower();
            follower.enabled = false;

            Assert.Throws<System.InvalidOperationException>(() => follower.Play(PathPlaybackRequest.Single(path)));
            Assert.AreEqual(EPathFollowerState.Ready, follower.State);
        }

        [Test]
        public void CopyOfAMovingFollower_TurnsItsCharacterControllerBackOn()
        {
            PathData path = Line(2f, 4f);
            var host = new GameObject("Controlled");
            _objects.Add(host);
            CharacterController controller = host.AddComponent<CharacterController>();
            PathFollower follower = host.AddComponent<PathFollower>();
            follower.Initialize();
            _driver.Register(follower);
            follower.Play(PathPlaybackRequest.Single(path));
            Assert.IsFalse(controller.enabled);

            GameObject copy = Object.Instantiate(host);
            _objects.Add(copy);
            // Play Mode's Awake; Edit Mode does not wake a copy.
            copy.GetComponent<PathFollower>().Initialize();

            Assert.IsTrue(copy.GetComponent<CharacterController>().enabled);
            Assert.IsFalse(controller.enabled);
            Frames(60);
            Assert.AreEqual(EPathFollowerState.Completed, follower.State);
            Assert.IsTrue(controller.enabled);
        }

        [Test]
        public void FreePlaybackWithADisabledFollower_ThrowsAndKeepsTheMemberInTheQueue()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(5);
            member.Follower.enabled = false;

            Assert.Throws<System.InvalidOperationException>(
                () => member.StartFreePlayback(PathPlaybackRequest.Single(path)));
            Assert.IsTrue(member.IsInQueue);
        }

        [Test]
        public void CopyOfAMovingFollower_LeavesAControllerOutsideItAlone()
        {
            PathData path = Line(2f, 4f);
            var parent = new GameObject("Controlled Parent");
            _objects.Add(parent);
            CharacterController controller = parent.AddComponent<CharacterController>();
            var child = new GameObject("Follower");
            child.transform.SetParent(parent.transform, false);
            PathFollower follower = child.AddComponent<PathFollower>();
            typeof(PathFollower)
                .GetField("_characterController", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(follower, controller);
            follower.Initialize();
            _driver.Register(follower);
            follower.Play(PathPlaybackRequest.Single(path));
            Assert.IsFalse(controller.enabled);

            GameObject copy = Object.Instantiate(child);
            _objects.Add(copy);
            copy.GetComponent<PathFollower>().Initialize();

            // The original still moves and still holds its controller off.
            Assert.IsFalse(controller.enabled);
            Frames(60);
            Assert.IsTrue(controller.enabled);
        }

        private TestSpacingPolicy CreatePolicy(string name,
            System.Func<PathQueueMember, PathQueueMember, float, float> rule)
        {
            var policy = ScriptableObject.CreateInstance<TestSpacingPolicy>();
            policy.name = name;
            policy.Rule = rule;
            _assets.Add(policy);
            return policy;
        }

        [Test]
        public void SeekToASegmentStart_LandsInThatSegment_AndSkipsItsEventAtZero()
        {
            // With these lengths start / total comes back a hair short of segment 1's start.
            PathData first = ExactLine(27.529211f);
            PathData second = ExactLine(47.01831f);
            PathData third = ExactLine(19.679008f);
            _scene.SetEvents(second, (0f, _scene.CreateEvent("AtStart")));
            second.Rebuild();
            MultiPathData sequence = _scene.CreateSequence();
            sequence.SetSegments(new[]
            {
                new PathSegmentAuthoring(first), new PathSegmentAuthoring(second), new PathSegmentAuthoring(third),
            });
            sequence.Rebuild();
            PathFollower follower = CreateFollower();
            var log = new List<string>();
            follower.SegmentChanged += segment => log.Add($"segment {segment}");
            follower.EventReached += name => log.Add($"event {name}");
            follower.Play(PathPlaybackRequest.Sequence(sequence));
            follower.Pause();
            float start = sequence.GetSegmentStartDistance(1);
            float progress = start / sequence.PathLength;
            Assume.That(progress * sequence.PathLength, Is.LessThan(start));

            Assert.IsTrue(follower.Seek(progress));
            Assert.AreEqual(1, follower.SegmentIndex);
            Assert.AreEqual(0f, follower.SegmentProgress);
            follower.Resume();
            Frames(2);

            // The event at the target counts as passed, and the follower never entered by moving.
            CollectionAssert.IsEmpty(log);
        }

        [Test]
        public void DestroyedPathThatNeverWoke_StopsItsFollower()
        {
            PathData path = Line(10f, 1f);
            PathFollower follower = CreateFollower();
            follower.Play(PathPlaybackRequest.Single(path));
            Frames(5);

            // Edit Mode calls no OnDestroy here, as Play Mode skips it for an object never active.
            Object.DestroyImmediate(path.gameObject);
            Frames(1);

            Assert.AreEqual(EPathFollowerState.Ready, follower.State);
            Assert.AreEqual(EPathPlaybackEnd.RouteUnavailable, follower.LastPlaybackEnd);
        }

        [Test]
        public void SequencePathThatNeverWokeDestroyed_StopsAFollowerOnIt()
        {
            PathData first = Line(10f, 1f);
            PathData second = Line(10f, 1f);
            MultiPathData sequence = _scene.CreateSequence();
            sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(second) });
            sequence.Rebuild();
            PathFollower follower = CreateFollower();
            follower.Play(PathPlaybackRequest.Sequence(sequence));
            follower.SeekSegment(1, 0.5f);
            Frames(1);

            Object.DestroyImmediate(second.gameObject);
            Frames(1);

            Assert.AreEqual(EPathFollowerState.Ready, follower.State);
            Assert.AreEqual(EPathPlaybackEnd.RouteUnavailable, follower.LastPlaybackEnd);
        }

        [Test]
        public void AggregateOfASequenceWhosePathNeverWokeDestroyed_StopsWithoutAFault()
        {
            PathData first = Line(10f, 1f);
            PathData second = Line(10f, 1f);
            MultiPathData sequence = _scene.CreateSequence();
            sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(second) });
            sequence.Rebuild();
            PathFollower follower = CreateFollower();
            follower.Play(PathPlaybackRequest.Aggregate(sequence, PathMovementSettings.Speed(1f), false));
            Frames(1);

            Object.DestroyImmediate(second.gameObject);
            // Frames fails the test on any tick fault, such as sampling the destroyed path.
            Frames(1);

            Assert.IsFalse(sequence.IsReady);
            Assert.AreEqual(EPathFollowerState.Ready, follower.State);
            Assert.AreEqual(EPathPlaybackEnd.RouteUnavailable, follower.LastPlaybackEnd);
        }

        [Test]
        public void HoldMadeInCompleted_WaitsAtTheNextQueuedStart()
        {
            PathData path = Line(3f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            bool held = false;
            member.Follower.Completed += () =>
            {
                if (held)
                    return;
                held = true;
                member.Hold();
            };
            for (int i = 0; i < 300 && !held; i++)
                Frames(1);
            Frames(2);

            Assert.IsFalse(member.IsInQueue);
            Assert.IsTrue(member.IsHeld);
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(30);
            Assert.AreEqual(0f, member.Follower.Progress);
            Assert.IsTrue(member.IsBlocked);

            member.ReleaseHold();
            Frames(60);
            Assert.Greater(member.Follower.Progress, 0f);
        }

        [Test]
        public void ReleasingTheManager_ReportsProviderInvalidForAMemberAlreadyOnAnotherRoute()
        {
            PathData route = Line(30f, 3f);
            PathData other = Line(30f, 3f);
            QueuedPathManager manager = CreateManager(route);
            PathQueueMember member = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            member.QueueInvalidated += (m, reason) => reasons.Add(reason);
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(route));
            Frames(20);

            // No Queue phase between the two: the release reports what that phase would.
            member.Follower.Play(PathPlaybackRequest.Single(other));
            manager.Release();

            CollectionAssert.AreEqual(new[] { EPathQueueLeaveReason.ProviderInvalid }, reasons);
            Assert.AreSame(other, member.Follower.CurrentProvider);
            Assert.AreEqual(EPathFollowerState.Moving, member.Follower.State);
        }

        [Test]
        public void RestartInPlace_IsListedBehindTheOthersAtOnce()
        {
            PathData path = Line(3f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember lapper = CreateMember();
            PathQueueMember other = CreateMember();
            lapper.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            other.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(5);
            lapper.Follower.Seek(0.6f);
            PathQueueMember leader = null;
            lapper.Follower.Completed += () => lapper.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            lapper.Follower.Completed += () => leader ??= manager.GetMember(0);
            for (int i = 0; i < 300 && leader == null; i++)
                Frames(1);

            Assert.AreSame(other, leader);
            Assert.AreSame(lapper, manager.GetMember(1));
        }

        [Test]
        public void QueueSignals_PairBlockedAndResumed_AndLeaveEndsAWaitAtOnce()
        {
            PathData path = Line(30f, 3f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            var log = new List<string>();
            float speed = -1f;
            member.Blocked += m => log.Add("blocked");
            member.Resumed += m => log.Add("resumed");
            member.SpeedChanged += (m, multiplier) => speed = multiplier;
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(240);

            // A start waits for the first result and the restart delay, then eases up to full speed.
            CollectionAssert.AreEqual(new[] { "blocked", "resumed" }, log);
            Assert.AreEqual(1f, speed);

            log.Clear();
            member.Hold();
            CollectionAssert.IsEmpty(log, "A Hold's notice comes with the next Tick.");
            Frames(1);
            CollectionAssert.AreEqual(new[] { "blocked" }, log);
            Assert.AreEqual(0f, speed);
            member.ReleaseHold();
            Frames(240);
            CollectionAssert.AreEqual(new[] { "blocked", "resumed" }, log);
            Assert.AreEqual(1f, speed);

            member.Hold();
            Frames(1);
            log.Clear();
            member.Leave();
            CollectionAssert.AreEqual(new[] { "resumed" }, log);
            Assert.AreEqual(1f, speed);
            Assert.IsFalse(member.IsHeld);
        }

        [Test]
        public void EventReceiver_HearsAnEventBeforeEventReached()
        {
            PathData path = Line(10f, 5f);
            _scene.SetEvents(path, (0.5f, _scene.CreateEvent("Named")));
            path.Rebuild();
            var host = new GameObject("Receiving Follower");
            _objects.Add(host);
            TestEventReceiver receiver = host.AddComponent<TestEventReceiver>();
            PathFollower follower = host.AddComponent<PathFollower>();
            var serialized = new UnityEditor.SerializedObject(follower);
            serialized.FindProperty("_eventReceiver").objectReferenceValue = receiver;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            follower.Initialize();
            _driver.Register(follower);
            follower.EventReached += name => receiver.Log.Add("EventReached " + name);

            follower.Play(PathPlaybackRequest.Single(path));
            Frames(120);

            CollectionAssert.AreEqual(new[] { "receiver Named", "EventReached Named" }, receiver.Log);
        }

        [Test]
        public void Aggregate_RunsAPathsEvents_ButASequenceHasNone()
        {
            PathData path = Line(10f, 1f);
            _scene.SetEvents(path, (0.5f, _scene.CreateEvent("OnPath")));
            path.Rebuild();
            MultiPathData sequence = _scene.CreateSequence();
            sequence.SetSegments(new[] { new PathSegmentAuthoring(path) });
            sequence.Rebuild();
            PathFollower onPath = CreateFollower();
            PathFollower onSequence = CreateFollower();
            var heard = new List<string>();
            onPath.EventReached += name => heard.Add("path " + name);
            onSequence.EventReached += name => heard.Add("sequence " + name);

            onPath.Play(PathPlaybackRequest.Aggregate(path, PathMovementSettings.Speed(10f), false));
            onSequence.Play(PathPlaybackRequest.Aggregate(sequence, PathMovementSettings.Speed(10f), false));
            Frames(120);

            CollectionAssert.AreEqual(new[] { "path OnPath" }, heard);
            Assert.AreEqual(EPathFollowerState.Completed, onPath.State);
            Assert.AreEqual(EPathFollowerState.Completed, onSequence.State);
        }

        [Test]
        public void SortPathEvents_IsStable_AndClampsToTheLastPosition()
        {
            PathData path = Line(10f, 1f);
            PathEventSettingSO a = _scene.CreateEvent("a"), b = _scene.CreateEvent("b");
            PathEventSettingSO c = _scene.CreateEvent("c"), d = _scene.CreateEvent("d");
            // An event authored at 1 fails the build until the sort clamps it.
            LogAssert.Expect(LogType.Error, new Regex("outside the valid range"));
            _scene.SetEvents(path, (0.6f, a), (0.2f, b), (0.6f, c), (1f, d));

            Assert.IsTrue(path.SortPathEventsByNormalizedTime());
            var order = new List<string>();
            for (int i = 0; i < path.AuthoringEventCount; i++)
            {
                PathEventEntry entry = path.GetAuthoringEvent(i);
                order.Add($"{entry.EventSetting.EventName}@{entry.NormalizedTime:F3}");
            }
            CollectionAssert.AreEqual(new[] { "b@0.200", "a@0.600", "c@0.600", "d@0.995" }, order);
            Assert.IsFalse(path.SortPathEventsByNormalizedTime());
        }

        [Test]
        public void RotateToPathDirection_TurnsWhileMoving_AndOffKeepsTheRotation()
        {
            PathData path = _scene.CreatePath(PathMovementSettings.Speed(5f), EPathCurveType.Linear,
                Vector3.zero, new Vector3(0f, 0f, 10f));
            PathFollower turning = CreateFollower();
            PathFollower fixedRotation = CreateFollower();
            Quaternion start = Quaternion.Euler(0f, 90f, 0f);
            turning.transform.rotation = start;
            fixedRotation.transform.rotation = start;
            fixedRotation.RotateToPathDirection = false;

            turning.Play(PathPlaybackRequest.Single(path));
            fixedRotation.Play(PathPlaybackRequest.Single(path));
            Frames(5);

            Assert.AreEqual(0f, Vector3.Angle(Vector3.forward, turning.transform.forward), 0.01f);
            Assert.AreEqual(0f, Quaternion.Angle(start, fixedRotation.transform.rotation), 0.01f);
        }

        // A two-point line split into two edges, so its length is exactly the one given.
        private PathData ExactLine(float length)
        {
            PathData path = Line(length, 1f);
            path.SegmentCount = 2;
            path.Rebuild();
            return path;
        }

        [Test]
        public void MemberStoppedAtTheGap_IsBlocked_WhateverTheRouteLength()
        {
            var random = new System.Random(7);
            foreach (float length in new[] { 30f, 75f, 500f, 1000f })
            {
                foreach (bool sequence in new[] { false, true })
                {
                    for (int trial = 0; trial < 5; trial++)
                    {
                        PathProviderBehaviour route;
                        PathPlaybackRequest request;
                        if (sequence)
                        {
                            PathData first = Line(length * 0.5f, 10f);
                            PathData second = _scene.CreatePath(PathMovementSettings.Speed(10f), EPathCurveType.Linear,
                                new Vector3(length * 0.5f, 0f, 0f), new Vector3(length, 0f, 0f));
                            MultiPathData joined = _scene.CreateSequence();
                            joined.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(second) });
                            route = joined;
                            request = PathPlaybackRequest.Sequence(joined);
                        }
                        else
                        {
                            PathData path = Line(length, 10f);
                            route = path;
                            request = PathPlaybackRequest.Single(path);
                        }
                        QueuedPathManager manager = CreateManager(route);
                        PathQueueMember leader = CreateMember();
                        PathQueueMember member = CreateMember();
                        float at = 0.3f + (float)random.NextDouble() * 0.6f;
                        leader.StartQueuedPlayback(manager, request);
                        leader.Follower.Seek(at);
                        leader.Hold();
                        member.StartQueuedPlayback(manager, request);
                        member.Follower.Seek(at - 5f / length);
                        Frames(900);
                        float settled = member.Follower.Progress;
                        Frames(10);

                        string label = $"length {length}, sequence {sequence}, leader at {at}";
                        Assert.AreEqual(settled, member.Follower.Progress, 1e-7f, "Not stopped yet: " + label);
                        Assert.IsTrue(manager.TryGetMemberStatus(member, out PathQueueStatus status), label);
                        Assert.IsTrue(status.IsBlocked, "Status not blocked: " + label);
                        Assert.IsTrue(member.IsBlocked, "Member not blocked: " + label);
                    }
                }
            }
        }

        [Test]
        public void RouteAndManagerGoneTogether_MembersLeaveAsProviderInvalid()
        {
            PathData path = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember member = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            member.QueueInvalidated += (_, reason) => reasons.Add(reason);
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(10);

            // Destroyed in one frame, the route first: no Queue phase saw it go.
            DestroyProvider(path);
            manager.Release();

            CollectionAssert.AreEqual(new[] { EPathQueueLeaveReason.ProviderInvalid }, reasons);
        }

        [Test]
        public void MemberThatLeftQuietly_DoesNotReportItsOldWaitLater()
        {
            PathData path = Line(60f, 3f);
            QueuedPathManager manager = CreateManager(path);
            var log = new List<string>();
            PathQueueMember Watched(string label, float at)
            {
                PathQueueMember created = CreateMember();
                created.Blocked += _ => log.Add(label + " blocked");
                created.Resumed += _ => log.Add(label + " resumed");
                created.SpeedChanged += (_, value) => log.Add($"{label} speed {value:F2}");
                created.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                created.Follower.Seek(at);
                return created;
            }
            PathQueueMember free = Watched("free", 0.5f);
            PathQueueMember rejoined = Watched("rejoined", 0.3f);
            PathQueueMember stopped = Watched("stopped", 0.1f);
            Frames(240);
            free.Hold();
            rejoined.Hold();
            stopped.Hold();
            Frames(1);
            CollectionAssert.IsSubsetOf(new[] { "free blocked", "rejoined blocked", "stopped blocked" }, log);
            Assert.IsTrue(free.IsInQueue && rejoined.IsInQueue && stopped.IsInQueue);

            // Disabled while waiting: each leaves without a Resumed. Stopped: it leaves as Stopped.
            DisableMember(free);
            free.enabled = true;
            DisableMember(rejoined);
            rejoined.enabled = true;
            stopped.Follower.Stop();
            Frames(1);
            log.Clear();

            free.StartFreePlayback(PathPlaybackRequest.Single(path));
            stopped.Leave();
            CollectionAssert.IsEmpty(log, "A wait that ended quietly is not reported later.");

            // A new wait is reported as one.
            rejoined.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Frames(1);
            CollectionAssert.Contains(log, "rejoined blocked");
        }

        [Test]
        public void SeekInAQueueListener_WaitsBeforeMoving()
        {
            PathData path = Line(100f, 3f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember ahead = CreateMember();
            PathQueueMember member = CreateMember();
            ahead.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            ahead.Follower.Seek(0.5f);
            ahead.Hold();
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            member.Follower.Seek(0.2f);
            Frames(120);
            member.Hold();
            Frames(30);
            float sought = -1f;
            member.Resumed += m =>
            {
                if (sought >= 0f)
                    return;
                sought = 0.1f;
                m.Follower.Seek(sought);
            };

            member.ReleaseHold();
            for (int i = 0; i < 180 && sought < 0f; i++)
                Frames(1);

            // Resumed runs after the Queue phase and before the move: the Seek waits as a start would.
            Assert.AreEqual(0.1f, sought);
            Assert.AreEqual(sought, member.Follower.Progress, 1e-6f);
            Frames(1);
            Assert.AreEqual(sought, member.Follower.Progress, 1e-6f);
        }

        [Test]
        public void LineRendererWithoutAPath_ReportsItOnce()
        {
            var host = new GameObject("Line");
            _objects.Add(host);
            host.AddComponent<LineRenderer>();
            PathDataLineRenderer drawer = host.AddComponent<PathDataLineRenderer>();

            LogAssert.Expect(LogType.Error, new Regex("requires LineRenderer and PathData"));
            // What its Awake does in Play Mode, then a caller showing it.
            drawer.Init();
            drawer.Refresh();
            drawer.SetVisible(true);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void SequenceWhosePathWaitsForRuntimePoints_BuildsWhenThePathGetsThem()
        {
            var host = new GameObject("Runtime Points");
            _objects.Add(host);
            PathData waiting = host.AddComponent<PathData>();
            MultiPathData sequence = _scene.CreateSequence();
            var serialized = new UnityEditor.SerializedObject(sequence);
            SerializedPropertyArray(serialized, "_segments", 1).GetArrayElementAtIndex(0)
                .FindPropertyRelative("_path").objectReferenceValue = waiting;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            int published = 0;
            sequence.PathChanged += () => published++;

            // What Awake does in Play Mode: no build that would fail and log, only listening.
            sequence.Wake();
            Assert.IsFalse(sequence.IsReady);
            LogAssert.NoUnexpectedReceived();

            waiting.SetControlPoints(new[] { Vector3.zero, new Vector3(5f, 0f, 0f) });
            Assert.IsTrue(sequence.IsReady);
            Assert.AreEqual(5f, sequence.PathLength, 1e-4f);
            Assert.Greater(published, 0);
        }

        [Test]
        public void SequencePathThatWoke_StopsTheSequenceThroughItsOwnDestroy()
        {
            PathData first = Line(10f, 1f);
            PathData second = Line(10f, 1f);
            WakePath(first);
            WakePath(second);
            MultiPathData sequence = _scene.CreateSequence();
            sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(second) });
            PathFollower follower = CreateFollower();
            follower.Play(PathPlaybackRequest.Sequence(sequence));
            follower.SeekSegment(1, 0.5f);
            Frames(1);

            // A path that woke is not checked on each read; its OnDestroy tells the sequence.
            DestroyProvider(second);
            Frames(1);

            Assert.IsFalse(sequence.IsReady);
            Assert.AreEqual(EPathFollowerState.Ready, follower.State);
            Assert.AreEqual(EPathPlaybackEnd.RouteUnavailable, follower.LastPlaybackEnd);
        }

        [Test]
        public void PathBrokenWhileTheSequenceBuilds_LeavesTheSequenceUnready()
        {
            PathData first = Line(10f, 1f);
            WakePath(first);
            // A path not built yet: the sequence's build builds it, and it publishes then.
            var host = new GameObject("Unbuilt");
            _objects.Add(host);
            var start = new GameObject("Start").transform;
            start.SetParent(host.transform);
            start.position = new Vector3(10f, 0f, 0f);
            var end = new GameObject("End").transform;
            end.SetParent(host.transform);
            end.position = new Vector3(20f, 0f, 0f);
            PathData second = host.AddComponent<PathData>();
            var points = new UnityEditor.SerializedObject(second);
            UnityEditor.SerializedProperty list = SerializedPropertyArray(points, "_pathPoints", 2);
            list.GetArrayElementAtIndex(0).objectReferenceValue = start;
            list.GetArrayElementAtIndex(1).objectReferenceValue = end;
            points.ApplyModifiedPropertiesWithoutUndo();
            second.PathChanged += () =>
            {
                if (first != null)
                    DestroyProvider(first);
            };
            MultiPathData sequence = _scene.CreateSequence();

            LogAssert.Expect(LogType.Error, new Regex("Segment 0 path is not ready"));
            sequence.SetSegments(new[] { new PathSegmentAuthoring(first), new PathSegmentAuthoring(second) });

            Assert.IsFalse(sequence.IsReady);
        }

        [Test]
        public void ConfigureRoute_AfterTheOldRouteFailed_MembersLeaveAsProviderInvalid()
        {
            PathData old = Line(30f, 4f);
            PathData next = Line(30f, 4f);
            QueuedPathManager manager = CreateManager(old);
            PathQueueMember member = CreateMember();
            var reasons = new List<EPathQueueLeaveReason>();
            member.QueueInvalidated += (_, reason) => reasons.Add(reason);
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(old));
            Frames(10);

            // The old route fails with no Queue phase before the new one is set.
            Object.DestroyImmediate(GetPoints(old)[1].gameObject);
            LogAssert.Expect(LogType.Error, new Regex("could not build"));
            old.Rebuild();
            manager.ConfigureRoute(next);

            CollectionAssert.AreEqual(new[] { EPathQueueLeaveReason.ProviderInvalid }, reasons);
        }

        [Test]
        public void SeekAndReleaseHoldInAQueueListener_StillWaitBeforeMoving()
        {
            PathData path = Line(100f, 3f);
            QueuedPathManager manager = CreateManager(path);
            PathQueueMember ahead = CreateMember();
            PathQueueMember member = CreateMember();
            ahead.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            ahead.Follower.Seek(0.5f);
            ahead.Hold();
            member.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            member.Follower.Seek(0.2f);
            Frames(120);
            member.Hold();
            Frames(30);
            float sought = -1f;
            member.Resumed += m =>
            {
                if (sought >= 0f)
                    return;
                sought = 0.1f;
                m.Follower.Seek(sought);
                // Writes the member's limit again; it must not undo the Seek's wait.
                m.ReleaseHold();
            };

            member.ReleaseHold();
            for (int i = 0; i < 180 && sought < 0f; i++)
                Frames(1);

            Assert.AreEqual(0.1f, sought);
            Assert.AreEqual(sought, member.Follower.Progress, 1e-6f);
        }

        // What Play Mode's Awake records; Edit Mode does not run it.
        private static void WakePath(PathData path) =>
            typeof(PathData)
                .GetMethod("Awake", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(path, null);

        private static UnityEditor.SerializedProperty SerializedPropertyArray(UnityEditor.SerializedObject serialized, string name, int size)
        {
            UnityEditor.SerializedProperty property = serialized.FindProperty(name);
            property.arraySize = size;
            return property;
        }

        // Edit Mode does not call OnDestroy for a component that never woke; Play
        // Mode does, and then the component is gone, so the test does both.
        private static void DestroyProvider(PathProviderBehaviour provider)
        {
            typeof(PathProviderBehaviour)
                .GetMethod("OnDestroy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(provider, null);
            Object.DestroyImmediate(provider.gameObject);
        }

        private static void DestroyMember(PathQueueMember member)
        {
            typeof(PathQueueMember)
                .GetMethod("OnDestroy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(member, null);
            Object.DestroyImmediate(member);
        }

        // Edit Mode does not call OnDisable for a component that never woke; Play Mode does.
        private static void DisableMember(PathQueueMember member)
        {
            member.enabled = false;
            typeof(PathQueueMember)
                .GetMethod("OnDisable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(member, null);
        }

        private static IReadOnlyList<Transform> GetPoints(PathData path) => path.PathPoints;
    }
}
