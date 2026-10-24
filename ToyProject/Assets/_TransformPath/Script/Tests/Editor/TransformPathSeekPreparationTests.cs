using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathSeekPreparationTests
    {
        private readonly List<Object> objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = objects.Count - 1; i >= 0; i--)
                if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            objects.Clear();
        }

        [TestCase("Single", false)]
        [TestCase("Aggregate", false)]
        [TestCase("Sequence", false)]
        [TestCase("Single", true)]
        [TestCase("Aggregate", true)]
        [TestCase("Sequence", true)]
        public void EventSeekEndsOldDispatchBeforeContinuingNewPosition(string kind, bool forward)
        {
            PathData path = Path();
            Events(path, new[] { 0.4f, 0.5f }, new[] { "A", "B" });
            PathPlaybackRequest request = kind == "Single" ? PathPlaybackRequest.Single(path)
                : kind == "Aggregate" ? PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(5f))
                : PathPlaybackRequest.Sequence(Sequence(path));
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            var calls = new List<string>(); bool seek = true;
            follower.GetComponent<PathEventHandler>().OnEventDispatched += name =>
            {
                calls.Add(name);
                if (name == "A" && seek) { seek = false; follower.Seek(forward ? 0.8f : 0.1f); }
            };
            follower.StartPlayback(request);
            follower.Tick(2.5f);
            Assert.That(follower.NormalizedTime, Is.EqualTo(forward ? 0.8f : 0.1f).Within(0.00001f));
            CollectionAssert.AreEqual(new[] { "A" }, calls, "The old frame must not replay crossed events after Seek reset the cursor");
            if (!forward)
            {
                follower.Tick(0.5f);
                CollectionAssert.AreEqual(new[] { "A" }, calls);
                follower.Tick(1f);
                CollectionAssert.AreEqual(new[] { "A", "A" }, calls, "Re-crossing A on a later tick is legitimate");
            }
        }

        [Test]
        public void CrossSegmentSeekDoesNotContinueOldSegmentEventSource()
        {
            PathData first = Path(); PathData second = Path();
            Events(first, new[] { 0.4f, 0.5f }, new[] { "A", "B" });
            Events(second, new[] { 0.2f }, new[] { "C" });
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            var calls = new List<string>(); bool seek = true;
            follower.GetComponent<PathEventHandler>().OnEventDispatched += name =>
            {
                calls.Add(name);
                if (name == "A" && seek) { seek = false; follower.SeekSegment(1, 0.1f); }
            };
            follower.StartPlayback(PathPlaybackRequest.Sequence(Sequence(first, second)));
            follower.Tick(2.5f);
            Assert.That(follower.CurrentSegmentIndex, Is.EqualTo(1));
            CollectionAssert.AreEqual(new[] { "A" }, calls, "Old segment events were dispatched after SeekSegment");
            follower.Tick(0.5f);
            CollectionAssert.AreEqual(new[] { "A", "C" }, calls);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BothSeekApisDiscardPreviousTransitionBacklog(bool globalSeek)
        {
            PathData first = Path(); PathData second = Path();
            var route = Actor().AddComponent<MultiPathData>();
            route.ConfigureSegments(new[]
            {
                new PathSegmentDescriptor(first, PathMovementSettings.Time(0.001f), false),
                new PathSegmentDescriptor(second, PathMovementSettings.Time(0.001f), false)
            });
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Sequence(route, true));
            follower.Tick(0.1f);
            if (globalSeek) follower.Seek(0.1f);
            else follower.SeekSegment(0, 0.2f);
            Assert.That(follower.CurrentSegmentIndex, Is.Zero);
            Assert.That(follower.NormalizedTime, Is.EqualTo(0.2f).Within(0.0001f));
            follower.Tick(0.0001f);
            Assert.That(follower.CurrentSegmentIndex, Is.Zero, "SeekSegment carried the old capped-frame time budget into its new position");
            Assert.That(follower.NormalizedTime, Is.EqualTo(0.3f).Within(0.0001f));
        }

        private void Events(PathData path, float[] positions, string[] names)
        {
            var events = new List<PathEventEntry>();
            for (int i = 0; i < positions.Length; i++)
            {
                var setting = ScriptableObject.CreateInstance<PathEventSettingSO>(); objects.Add(setting); setting.EventName = names[i];
                events.Add(new PathEventEntry { NormalizedTime = positions[i], EventSetting = setting });
            }
            typeof(PathData).GetField("_pathEvents", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(path, events);
            path.Rebuild();
        }

        [TestCase("Single")]
        [TestCase("Aggregate")]
        [TestCase("Sequence")]
        public void NoOpSeekFromEventKeepsRemainingEventsInTheSameTick(string kind)
        {
            PathData path = Path(); Events(path, new[] { 0.4f, 0.5f }, new[] { "A", "B" });
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            var calls = new List<string>();
            follower.GetComponent<PathEventHandler>().OnEventDispatched += name =>
            {
                calls.Add(name);
                if (name == "A") Assert.That(follower.Seek(follower.GlobalNormalizedTime).Changed, Is.False);
            };
            follower.StartPlayback(Request(kind, path));
            ulong id = follower.PlaybackId; ulong revision = follower.StateRevision;
            follower.Tick(2.5f);
            CollectionAssert.AreEqual(new[] { "A", "B" }, calls);
            Assert.That(follower.PlaybackId, Is.EqualTo(id));
            Assert.That(follower.StateRevision, Is.EqualTo(revision));
        }

        [TestCase("Single")]
        [TestCase("Aggregate")]
        [TestCase("Sequence")]
        public void EventSeekCancelsOldLoopBoundaryWithoutReplacingPlayback(string kind)
        {
            PathData path = Path(); Events(path, new[] { 0.8f }, new[] { "seek" });
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            int loops = 0; int events = 0;
            follower.LoopBoundary += () => loops++;
            follower.GetComponent<PathEventHandler>().OnEventDispatched += _ => { events++; follower.Seek(0.1f); };
            follower.StartPlayback(Request(kind, path, true));
            ulong id = follower.PlaybackId;
            follower.Tick(5f);
            Assert.That(events, Is.EqualTo(1)); Assert.That(loops, Is.Zero);
            Assert.That(follower.PlaybackId, Is.EqualTo(id));
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(follower.GlobalNormalizedTime, Is.EqualTo(0.1f).Within(0.00001f));
        }

        [TestCase("Single", "Direct")]
        [TestCase("Single", "Free")]
        [TestCase("Single", "Queued")]
        [TestCase("Aggregate", "Direct")]
        [TestCase("Aggregate", "Free")]
        [TestCase("Aggregate", "Queued")]
        [TestCase("Sequence", "Direct")]
        [TestCase("Sequence", "Free")]
        [TestCase("Sequence", "Queued")]
        public void EachStartReadsProviderEventsOnceAndReusesUnchangedSnapshots(string kind, string mode)
        {
            CountingProvider provider = CountedPath();
            var request = Request(kind, provider);
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            QueuedPathFollower queue = null; QueuedPathManager manager = null;
            if (mode != "Direct") { queue = follower.gameObject.AddComponent<QueuedPathFollower>(); queue.Init(); }
            if (mode == "Queued")
            {
                manager = Actor().AddComponent<QueuedPathManager>(); manager.Init(); manager.ConfigureRoute(request.Provider);
            }
            for (int i = 0; i < 2; i++)
            {
                if (mode == "Direct") follower.StartPlayback(request);
                else if (mode == "Free") queue.StartFreePlayback(request);
                else queue.StartQueuedPlayback(manager, request);
                Assert.That(provider.Reads, Is.EqualTo(1), $"{kind}/{mode}: preparation copied the same provider events more than once");
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InvalidRequestDoesNotDetachExistingQueue(bool free)
        {
            PathData path = Path();
            var manager = Actor().AddComponent<QueuedPathManager>(); manager.Init(); manager.ConfigureRoute(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            var registration = manager.Register(queue); ulong id = queue.PlaybackId;
            if (free) Assert.Throws<System.InvalidOperationException>(() => queue.StartFreePlayback(default(PathPlaybackRequest)));
            else Assert.Throws<System.InvalidOperationException>(() => queue.StartQueuedPlayback(manager, default(PathPlaybackRequest)));
            Assert.That(queue.PlaybackId, Is.EqualTo(id)); Assert.That(queue.IsRegistered, Is.True);
            Assert.That(manager.Register(queue).RegistrationId, Is.EqualTo(registration.RegistrationId));
            Assert.That(manager.AgentCount, Is.EqualTo(1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ChangedPreparedProviderIsRevalidatedBeforeReplacingOldPlayback(bool invalid)
        {
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(Path())); ulong old = follower.PlaybackId;
            CountingProvider provider = CountedPath(); var request = PathPlaybackRequest.Single(provider);
            PathPlaybackSession candidate = follower.PreparePlayback(request);
            Assert.That(provider.Reads, Is.EqualTo(1));
            if (invalid)
            {
                provider.Ready = false;
                Assert.Throws<System.InvalidOperationException>(() => follower.StartPreparedPlayback(request, candidate));
                Assert.That(follower.PlaybackId, Is.EqualTo(old)); Assert.That(follower.IsMoving, Is.True);
            }
            else
            {
                var setting = ScriptableObject.CreateInstance<PathEventSettingSO>(); objects.Add(setting); setting.EventName = "new";
                provider.ChangeEvent(PathEventDefinition.FromAuthoring(setting));
                var calls = new List<string>(); follower.GetComponent<PathEventHandler>().OnEventDispatched += name => calls.Add(name);
                follower.StartPreparedPlayback(request, candidate);
                Assert.That(provider.Reads, Is.EqualTo(2));
                follower.Tick(2.5f);
                CollectionAssert.AreEqual(new[] { "new" }, calls);
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void QueueSeekKeepsOwnershipAndWaitsForRecalculatedConstraint(bool segmentSeek)
        {
            MultiPathData route = Sequence(Path(), Path());
            var manager = Actor().AddComponent<QueuedPathManager>(); manager.Init(); manager.ConfigureRoute(route);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Sequence(route));
            var token = manager.Register(queue); ulong id = queue.PlaybackId;
            TickQueue(manager, queue, 1, 0.2f);
            PathCommandReceipt receipt = segmentSeek ? queue.SeekSegment(0, 0.2f) : queue.Seek(0.1f);
            Assert.That(receipt.Changed, Is.True); Assert.That(queue.IsBlocked, Is.True);
            Assert.That(queue.PlaybackId, Is.EqualTo(id));
            queue.UnderlyingFollower.Tick(0.2f);
            Assert.That(queue.GlobalNormalizedTime, Is.EqualTo(0.1f).Within(0.00001f));
            TickQueue(manager, queue, 2, 0.2f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0.1f));
            Assert.That(manager.Register(queue).RegistrationId, Is.EqualTo(token.RegistrationId));
        }

        private void TickQueue(QueuedPathManager manager, QueuedPathFollower queue, int frame, float delta)
        {
            ((IPathRuntimeTickable)manager).Tick(delta, delta, frame);
            ((IPathRuntimeTickable)queue).Tick(delta, delta, frame);
            queue.UnderlyingFollower.Tick(delta);
        }

        [TestCase(0.001f)]
        [TestCase(0.002f)]
        public void DurationChangePreservesSeekProgressAtTheValidLowerBound(float duration)
        {
            PathData path = Path(); path.ConfigureMovementSettings(PathMovementSettings.Time(duration));
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            follower.Seek(0.2f); follower.SetDuration(10f); follower.Tick(0.1f);
            Assert.That(follower.NormalizedTime, Is.EqualTo(0.21f).Within(0.0001f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MinimumDurationStillProvidesNominalSpeedForTheNextSegment(bool speedBased)
        {
            PathData first = Path(); PathData second = Path();
            first.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right });
            second.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right });
            var route = Actor().AddComponent<MultiPathData>();
            route.ConfigureSegments(new[]
            {
                new PathSegmentDescriptor(first, PathMovementSettings.Time(0.001f), false),
                new PathSegmentDescriptor(second, speedBased ? PathMovementSettings.Speed(1f) : PathMovementSettings.Time(5f), true)
            });
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Sequence(route)); follower.Tick(0.001f);
            Assert.That(follower.CurrentSegmentIndex, Is.EqualTo(1));
            if (speedBased) Assert.That(follower.Speed, Is.EqualTo(1000f).Within(0.001f));
            else Assert.That(follower.Duration, Is.EqualTo(0.001f).Within(0.000001f));
        }

        private PathPlaybackRequest Request(string kind, IPathMovementProvider provider, bool loop = false)
        {
            if (kind == "Single") return PathPlaybackRequest.Single(provider, loop);
            if (kind == "Aggregate") return PathPlaybackRequest.Aggregate(provider, PathMovementSettings.Time(5f), loop);
            var route = Actor().AddComponent<MultiPathData>();
            route.ConfigureSegments(new[] { new PathSegmentDescriptor(provider, provider.MovementSettings, false) });
            return PathPlaybackRequest.Sequence(route, loop);
        }

        private CountingProvider CountedPath()
        {
            var setting = ScriptableObject.CreateInstance<PathEventSettingSO>(); objects.Add(setting); setting.EventName = "old";
            return new CountingProvider(Path(), PathEventDefinition.FromAuthoring(setting));
        }

        private sealed class CountingProvider : IPathMovementProvider, IPathEventSource
        {
            private readonly PathData path; private PathEventDefinition definition;
            public CountingProvider(PathData path, PathEventDefinition definition) { this.path = path; this.definition = definition; }
            public bool Ready = true;
            public int Reads { get; private set; }
            public bool IsInitialized => true;
            public bool IsReady => Ready;
            public int Revision { get; private set; } = 1;
            public float PathLength => path.PathLength;
            public PathMovementSettings MovementSettings => PathMovementSettings.Time(5f);
            public event System.Action PathChanged;
            public int EventCount => 1;
            public PathRuntimeEvent GetEvent(int index) { Reads++; return new PathRuntimeEvent(0.4f, definition); }
            public Vector3 Sample(float progress) => path.Sample(progress);
            public Vector3 SampleDistance(float distance) => path.SampleDistance(distance);
            public void ChangeEvent(PathEventDefinition next) { definition = next; Revision++; PathChanged?.Invoke(); }
        }

        private MultiPathData Sequence(params PathData[] paths)
        {
            var route = Actor().AddComponent<MultiPathData>();
            var segments = new PathSegmentDescriptor[paths.Length];
            for (int i = 0; i < paths.Length; i++) segments[i] = new PathSegmentDescriptor(paths[i], paths[i].MovementSettings, false);
            route.ConfigureSegments(segments); return route;
        }

        private GameObject Actor() { var obj = new GameObject("Seek preparation regression"); objects.Add(obj); return obj; }

        private PathData Path()
        {
            var path = Actor().AddComponent<PathData>();
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
            path.ConfigureMovementSettings(PathMovementSettings.Time(5f));
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 10f }); path.Init(); return path;
        }
    }
}
