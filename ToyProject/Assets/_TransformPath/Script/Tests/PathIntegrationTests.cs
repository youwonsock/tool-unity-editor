using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.TransformPath.Tests
{
    /// <summary>Components wired together and ticked in the driver's phase order.</summary>
    public sealed class PathIntegrationTests
    {
        private PathTestScene _scene;
        private readonly List<GameObject> _objects = new List<GameObject>();
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

        private QueuedPathManager CreateManager(IPathProvider route)
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
            int laps = member.Follower.LoopCount;
            for (int i = 0; i < 120 && member.Follower.LoopCount == laps; i++)
                Frames(1);

            // The frame that crossed the seam dropped its remaining time there.
            Assert.AreEqual(laps + 1, member.Follower.LoopCount);
            Assert.AreEqual(0f, member.Follower.Progress, 1e-5f);
            Frames(3);
            Assert.AreEqual(0f, member.Follower.Progress, 1e-5f);
            Frames(60);
            Assert.Greater(member.Follower.Progress, 0.05f);
        }

        [Test]
        public void DestroyedPath_StopsItsFollowers()
        {
            PathData path = Line(10f, 2f);
            PathFollower follower = CreateFollower();
            follower.Play(PathPlaybackRequest.Single(path));
            Frames(10);

            // Edit Mode does not call OnDestroy for a component that never woke; Play Mode does.
            typeof(PathProviderBehaviour)
                .GetMethod("OnDestroy", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Invoke(path, null);
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
        public void Events_CannotBeChangedFromOutside()
        {
            PathData path = Line(10f, 2f);
            _scene.SetEvents(path, (0.5f, _scene.CreateEvent("e")));

            Assert.IsNull(path.Events as PathRuntimeEvent[]);
            Assert.AreEqual(1, path.Events.Count);
        }

        private static IReadOnlyList<Transform> GetPoints(PathData path) => path.PathPoints;
    }
}
