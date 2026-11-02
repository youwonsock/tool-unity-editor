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
