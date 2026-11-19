using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.TransformPath.Tests
{
    /// <summary>Components wired together and ticked in the driver's phase order.</summary>
    public sealed class PathIntegrationTests : PathBoundaryIsolatedTest
    {
        private PathTestScene _scene;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Object> _assets = new List<Object>();
        private readonly List<PathFollower> _followers = new List<PathFollower>();
        private QueuedPathManager _manager;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            _scene = new PathTestScene();
            _followers.Clear();
            _manager = null;
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
            _followers.Add(follower);
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
            _manager = host.AddComponent<QueuedPathManager>();
            _manager.Initialize();
            _manager.ConfigureRoute(route);
            return _manager;
        }

        // Effects, queue, movement: the driver's order.
        private void Frames(int count, float deltaTime = 1f / 60f)
        {
            for (int n = 0; n < count; n++)
            {
                _frame++;
                foreach (PathFollower follower in _followers)
                    follower.Tick(EPathTickPhase.Effects, deltaTime, _frame);
                _manager?.Tick(EPathTickPhase.Queue, deltaTime, _frame);
                foreach (PathFollower follower in _followers)
                    follower.Tick(EPathTickPhase.Movement, deltaTime, _frame);
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

        private static void SetPlayOnStart(PathFollower follower, PathProviderBehaviour provider)
        {
            var serialized = new UnityEditor.SerializedObject(follower);
            serialized.FindProperty("_playOnStart").boolValue = true;
            serialized.FindProperty("_playOnStartProvider").objectReferenceValue = provider;
            serialized.ApplyModifiedPropertiesWithoutUndo();
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

        // Edit Mode does not call OnDestroy for a component that never woke; Play Mode does.
        private static void DestroyProvider(PathProviderBehaviour provider) =>
            typeof(PathProviderBehaviour)
                .GetMethod("OnDestroy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(provider, null);

        private static IReadOnlyList<Transform> GetPoints(PathData path) => path.PathPoints;
    }
}
