using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    /// <summary>Rules the other tests did not pin down.</summary>
    public sealed class TransformPathInvariantTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object item in _objects)
                if (item != null) Object.DestroyImmediate(item);
            _objects.Clear();
        }

        // One tick crosses at most 65 segments and keeps the rest for the next.
        [Test]
        public void ALongFrameCarriesItsRemainingTimeAcrossTicks()
        {
            var segments = new List<PathSegmentDescriptor>();
            for (int i = 0; i < 200; i++)
                segments.Add(new PathSegmentDescriptor(CreatePath(Vector3.right * i, Vector3.right * (i + 1)), PathMovementSettings.Speed(1f), false));
            var route = Create<MultiPathData>("Route");
            route.ConfigureSegments(segments);
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            int transitions = 0;
            follower.SegmentChanged += _ => transitions++;
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));

            follower.Tick(100.5f);
            Assert.That(transitions, NUnit.Framework.Is.EqualTo(65));
            Assert.That(follower.CurrentSegmentIndex, NUnit.Framework.Is.EqualTo(65));

            // A tick with no time of its own still spends what the last one kept.
            follower.Tick(0.00001f);
            Assert.That(follower.CurrentSegmentIndex, NUnit.Framework.Is.EqualTo(100));
            Assert.That(follower.NormalizedTime, NUnit.Framework.Is.EqualTo(0.5f).Within(0.001f));
        }

        // Sequence segments restart from their settings; a Single lap does not.
        [Test]
        public void ALapOfASinglePlaybackKeepsTheValuesSetDuringTheLastOne()
        {
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(Vector3.zero, Vector3.right), true));
            follower.SetSpeed(3f);
            follower.Tick(1f);
            Assert.That(follower.Speed, NUnit.Framework.Is.EqualTo(3f));
        }

        [Test]
        public void ALongFrameOfALoopingSinglePlaybackIsCappedLikeASequence()
        {
            int laps = 0;
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            follower.LoopBoundary += () => laps++;
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(Vector3.zero, Vector3.right), true));

            follower.Tick(100.5f);
            Assert.That(laps, NUnit.Framework.Is.EqualTo(65));
            follower.Tick(0.00001f);
            Assert.That(laps, NUnit.Framework.Is.EqualTo(100));
            Assert.That(follower.NormalizedTime, NUnit.Framework.Is.EqualTo(0.5f).Within(0.001f));
        }

        [Test]
        public void FollowersOfOneSequenceShareItsSnapshotUntilItChanges()
        {
            PathData first = CreatePath(Vector3.zero, Vector3.right);
            PathData second = CreatePath(Vector3.right, Vector3.right * 2f);
            var route = Create<MultiPathData>("Route");
            route.ConfigureSegments(new[]
            {
                new PathSegmentDescriptor(first, first.MovementSettings, false),
                new PathSegmentDescriptor(second, second.MovementSettings, false),
            });
            PathPlaybackRequest request = PathPlaybackRequest.Sequence(route);
            PathPlaybackSession one = PathPlaybackSession.CreateOrReuse(request, null);
            PathPlaybackSession other = PathPlaybackSession.CreateOrReuse(request, null);
            Assert.That(other.Snapshot, NUnit.Framework.Is.SameAs(one.Snapshot));

            first.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 3f });
            PathPlaybackSession later = PathPlaybackSession.CreateOrReuse(request, null);
            Assert.That(later.Snapshot, NUnit.Framework.Is.Not.SameAs(one.Snapshot));
            Assert.That(later.Snapshot.GetLength(0), NUnit.Framework.Is.EqualTo(3f).Within(0.001f));
            Assert.That(one.Snapshot.GetLength(0), NUnit.Framework.Is.EqualTo(1f).Within(0.001f), "A snapshot in use never changes.");
        }

        [TestCase("Single", 0.5f)]
        [TestCase("Aggregate", 0.5f)]
        [TestCase("Single", -1f)]
        public void FollowersOfOnePathShareItsEventSnapshotUntilItChanges(string kind, float eventAt)
        {
            PathData path = eventAt < 0f
                ? CreatePath(Vector3.zero, Vector3.right)
                : CreatePath(Vector3.zero, Vector3.right, eventAt);
            PathPlaybackRequest request = kind == "Single"
                ? PathPlaybackRequest.Single(path)
                : PathPlaybackRequest.Aggregate(path, path.MovementSettings);
            PathPlaybackSession one = PathPlaybackSession.CreateOrReuse(request, null);
            PathPlaybackSession other = PathPlaybackSession.CreateOrReuse(request, null);
            Assert.That(other.EventSource, NUnit.Framework.Is.SameAs(one.EventSource));
            Assert.That(one.EventSource.EventCount, NUnit.Framework.Is.EqualTo(eventAt < 0f ? 0 : 1));

            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 3f });

            PathPlaybackSession later = PathPlaybackSession.CreateOrReuse(request, null);
            if (eventAt >= 0f)
                Assert.That(later.EventSource, NUnit.Framework.Is.Not.SameAs(one.EventSource));
            Assert.That(later.EventSource.HasSameContent(one.EventSource), NUnit.Framework.Is.True);
        }

        // Each snapshot remembers the one it was last compared with. A path that
        // keeps changing must not build a chain of every snapshot it ever had.
        [Test]
        public void ReplacedSnapshotsDoNotKeepTheirPredecessorsAlive()
        {
            PathData path = CreatePath(Vector3.zero, Vector3.right);
            var route = Create<MultiPathData>("Route");
            route.ConfigureSegments(new[] { new PathSegmentDescriptor(path, path.MovementSettings, false) });
            FieldInfo comparedWith = typeof(PathSequenceSnapshot).GetField("_comparedWith", BindingFlags.NonPublic | BindingFlags.Instance);
            PathSequenceSnapshot previous = null;
            PathSequenceSnapshot replaced = null;
            for (int i = 2; i < 5; i++)
            {
                PathSequenceSnapshot.TryGetCurrent(route, out PathSequenceSnapshot current, out _);
                if (previous != null)
                    current.HasSamePlaybackDefinition(previous);
                replaced = previous;
                previous = current;
                path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * i });
            }
            PathSequenceSnapshot.TryGetCurrent(route, out _, out _);
            Assert.That(comparedWith.GetValue(previous), NUnit.Framework.Is.Null);
            Assert.That(comparedWith.GetValue(replaced), NUnit.Framework.Is.Null);
        }

        // The sample's seek buttons and curve change rely on this.
        [Test]
        public void SeekingAfterPlaybackEndedOnlyMovesTheFollower()
        {
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(Vector3.zero, Vector3.right * 10f)));
            follower.Tick(20f);
            Assert.That(follower.State, NUnit.Framework.Is.EqualTo(EPathFollowerState.Completed));

            PathCommandReceipt receipt = follower.Seek(0.5f);

            Assert.That(receipt.Changed, NUnit.Framework.Is.True);
            Assert.That(receipt.PlaybackId, NUnit.Framework.Is.EqualTo(0UL));
            Assert.That(follower.transform.position.x, NUnit.Framework.Is.EqualTo(5f).Within(0.001f));
            follower.Tick(1f);
            Assert.That(follower.State, NUnit.Framework.Is.EqualTo(EPathFollowerState.Completed));
            Assert.That(follower.transform.position.x, NUnit.Framework.Is.EqualTo(5f).Within(0.001f));
        }

        [Test]
        public void ReplacedEventSnapshotsDoNotKeepTheirPredecessorsAlive()
        {
            PathData path = CreatePath(Vector3.zero, Vector3.right, 0.5f);
            PathPlaybackRequest request = PathPlaybackRequest.Single(path);
            FieldInfo comparedWith = typeof(PathEventSourceSnapshot).GetField("_comparedWith", BindingFlags.NonPublic | BindingFlags.Instance);
            var playing = new List<PathPlaybackSession>();
            for (int i = 2; i < 5; i++)
            {
                PathPlaybackSession session = PathPlaybackSession.CreateOrReuse(request, null);
                playing.Add(session);
                path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * i });
                Assert.That(session.EvaluateRefresh(out _, out _), NUnit.Framework.Is.EqualTo(EPathSnapshotRefreshDecision.Refresh));
            }

            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 9f });
            PathPlaybackSession.CreateOrReuse(request, null);

            foreach (PathPlaybackSession session in playing)
                Assert.That(comparedWith.GetValue(session.EventSource), NUnit.Framework.Is.Null);
        }

        [Test]
        public void AFollowerTickDoesNotAllocate()
        {
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(Vector3.zero, Vector3.right * 1000f)));
            follower.Tick(0.016f);
            Assert.That(() => follower.Tick(0.016f), Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void PassingAnEventDoesNotAllocate()
        {
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(Vector3.zero, Vector3.right, 0.5f), true));
            follower.Tick(1.1f);
            Assert.That(() => follower.Tick(1f), Is.Not.AllocatingGCMemory());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AQueueFrameDoesNotAllocate(bool listening)
        {
            PathRuntimeDriver driver = CreateQueueInSlowdown(listening, out QueuedPathManager manager, out QueuedPathFollower behind);
            float before = behind.CurrentSpeedMultiplier;
            int frame = 1000;
            Assert.That(() => driver.Tick(0.016f, 0.016f, frame++), Is.Not.AllocatingGCMemory());
            Assert.That(behind.CurrentSpeedMultiplier, NUnit.Framework.Is.Not.EqualTo(before), "The speed has to change for this to mean anything.");
            Assert.That(manager.AgentCount, NUnit.Framework.Is.EqualTo(2));
        }

        // The leader pulls away inside the slowdown zone, so the agent behind
        // changes its speed multiplier every frame.
        private PathRuntimeDriver CreateQueueInSlowdown(bool listening, out QueuedPathManager manager, out QueuedPathFollower behind)
        {
            PathData path = CreatePath(Vector3.zero, Vector3.right * 1000f);
            manager = Create<QueuedPathManager>("Manager");
            manager.Init();
            manager.DefaultSpacing = 1.5f;
            manager.SlowdownStartDistance = 100f;
            manager.ConfigureRoute(path);
            var driver = new PathRuntimeDriver();
            driver.Register(manager);
            behind = null;
            int notified = 0;
            for (int i = 0; i < 2; i++)
            {
                behind = Create<QueuedPathFollower>("Agent");
                behind.Init();
                behind.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                behind.Seek(0.02f - i * 0.01f);
                if (listening)
                {
                    behind.OnSpeedChanged += (_, __) => notified++;
                    behind.OnBlocked += _ => notified++;
                    behind.OnResumed += _ => notified++;
                }
                driver.Register(behind);
                driver.Register(behind.UnderlyingFollower);
                driver.Register(behind.GetComponent<PathEventHandler>());
            }
            int frame = 1;
            for (int i = 0; i < 60; i++)
                driver.Tick(0.016f, 0.016f, frame++);
            Assert.That(driver.Faults, NUnit.Framework.Is.Empty);
            return driver;
        }

        [Test]
        public void AnEventAtTheStartFiresWhenPlaybackLeavesIt()
        {
            var names = new List<string>();
            PathFollower follower = CreateFollower(names);
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(Vector3.zero, Vector3.right * 10f, 0f, 0.5f)));
            Assert.That(names, NUnit.Framework.Is.Empty, "Nothing has been passed before the first tick.");
            for (int i = 0; i < 80; i++)
                follower.Tick(0.1f);
            Assert.That(names, NUnit.Framework.Is.EqualTo(new[] { "t0", "t0.5" }));
        }

        [Test]
        public void AnEventAtTheStartFiresOnEveryLap()
        {
            var names = new List<string>();
            PathFollower follower = CreateFollower(names);
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(Vector3.zero, Vector3.right, 0f, 0.5f), true));
            for (int i = 0; i < 25; i++)
                follower.Tick(0.1f);
            Assert.That(names.FindAll(name => name == "t0").Count, NUnit.Framework.Is.EqualTo(3));
            Assert.That(names.FindAll(name => name == "t0.5").Count, NUnit.Framework.Is.EqualTo(3));
        }

        [Test]
        public void AnEventAtTheStartOfALaterSegmentFires()
        {
            var names = new List<string>();
            PathData first = CreatePath(Vector3.zero, Vector3.right);
            PathData second = CreatePath(Vector3.right, Vector3.right * 2f, 0f, 0.5f);
            var route = Create<MultiPathData>("Route");
            route.ConfigureSegments(new[]
            {
                new PathSegmentDescriptor(first, first.MovementSettings, false),
                new PathSegmentDescriptor(second, second.MovementSettings, false),
            });
            PathFollower follower = CreateFollower(names);
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));
            for (int i = 0; i < 30; i++)
                follower.Tick(0.1f);
            Assert.That(names, NUnit.Framework.Is.EqualTo(new[] { "t0", "t0.5" }));
        }

        [Test]
        public void SeekingToAnEventSkipsIt()
        {
            var names = new List<string>();
            PathFollower follower = CreateFollower(names);
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(Vector3.zero, Vector3.right * 10f, 0f, 0.5f)));
            follower.Seek(0.5f);
            follower.Seek(0f);
            follower.Tick(0.1f);
            Assert.That(names, NUnit.Framework.Is.Empty);
        }

        [Test]
        public void EveryFaultOfAFrameIsKept()
        {
            var driver = new PathRuntimeDriver();
            driver.Register(new FaultingItem("first"));
            driver.Register(new FaultingItem("second"));
            driver.Tick(0.016f);
            Assert.That(driver.Faults.ConvertAll(fault => fault.Message), NUnit.Framework.Is.EqualTo(new[] { "first", "second" }));
            var quiet = new PathRuntimeDriver();
            quiet.Tick(0.016f);
            Assert.That(quiet.Faults, NUnit.Framework.Is.Empty);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SeekWithoutAReadyProviderLeavesThePlaybackAlone(bool sequence)
        {
            PathData path = CreatePath(Vector3.zero, Vector3.right * 10f);
            PathFollower follower = CreateFollower(new List<string>());
            MultiPathData route = null;
            if (sequence)
            {
                route = Create<MultiPathData>("Route");
                route.ConfigureSegments(new[] { new PathSegmentDescriptor(path, path.MovementSettings, false) });
            }
            follower.StartPlayback(sequence
                ? PathPlaybackRequest.Sequence(route)
                : PathPlaybackRequest.Single(path));
            follower.Tick(1f);
            follower.PauseMove();
            float before = follower.NormalizedTime;
            ulong revision = follower.StateRevision;
            path.Release();

            Assert.Throws<System.InvalidOperationException>(() => follower.Seek(0.5f));
            Assert.That(follower.NormalizedTime, NUnit.Framework.Is.EqualTo(before));
            Assert.That(follower.StateRevision, NUnit.Framework.Is.EqualTo(revision));
        }

        private PathFollower CreateFollower(List<string> names)
        {
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            follower.GetComponent<PathEventHandler>().OnEventDispatched += names.Add;
            return follower;
        }

        private sealed class FaultingItem : IPathRuntimeTickable
        {
            private readonly string _message;
            internal FaultingItem(string message) { _message = message; }
            public int RuntimeOrder => 0;
            public ulong PlaybackId => 0UL;
            public void Tick(float deltaTime, float unscaledDeltaTime, int frameId)
                => throw new System.InvalidOperationException(_message);
        }

        private T Create<T>(string name) where T : Component
        {
            var item = new GameObject(name);
            _objects.Add(item);
            return item.AddComponent<T>();
        }

        private PathData CreatePath(Vector3 start, Vector3 end, params float[] events)
        {
            var path = Create<PathData>("Path");
            path.ConfigureMovementSettings(PathMovementSettings.Speed(1f));
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 4));
            var entries = new List<PathEventEntry>();
            foreach (float time in events)
            {
                var setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
                _objects.Add(setting);
                setting.EventName = "t" + time;
                entries.Add(new PathEventEntry { NormalizedTime = time, EventSetting = setting });
            }
            typeof(PathData).GetField("_pathEvents", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(path, entries);
            path.ConfigureControlPoints(new[] { start, end });
            path.Init();
            return path;
        }
    }
}
