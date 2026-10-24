using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathRuntimeImprovementTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private float _timeScale;
        private float _fixedDeltaTime;

        [SetUp]
        public void SetUp()
        {
            _timeScale = Time.timeScale;
            _fixedDeltaTime = Time.fixedDeltaTime;
            Time.timeScale = 1f;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null)
                    Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
            PathTimeScaleCoordinator.Shared.Reset();
            Time.timeScale = _timeScale;
            Time.fixedDeltaTime = _fixedDeltaTime;
        }

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(5)]
        public void MultiPathSamplesEverySegmentAndClampsOutsideTheRoute(int count)
        {
            var paths = new IPathProvider[count];
            float end = 0f;
            for (int i = 0; i < count; i++)
            {
                float start = end;
                end += i + 1;
                paths[i] = CreatePath(start, end);
            }
            MultiPathData route = CreateRoute(paths);
            for (int i = -1; i <= 61; i++)
            {
                float progress = i / 60f;
                float expected = Mathf.Clamp01(progress) * end;
                Assert.That(route.SampleDistance(progress * end).x,
                    Is.EqualTo(expected).Within(0.001f), $"Distance sample {progress}");
                Assert.That(route.Sample(progress).x,
                    Is.EqualTo(expected).Within(0.001f), $"Normalized sample {progress}");
            }
        }

        [TestCase(0f, 0f)]
        [TestCase(9.99f, 9.99f)]
        [TestCase(10f, 100f)]
        [TestCase(10.01f, 100.01f)]
        [TestCase(20f, 110f)]
        [TestCase(29.99f, 119.99f)]
        [TestCase(30f, 120f)]
        public void MultiPathBoundaryBelongsToTheNextSegment(float distance, float expected)
        {
            MultiPathData route = CreateRoute(CreatePath(0f, 10f), CreatePath(100f, 120f));
            Assert.That(route.SampleDistance(distance).x, Is.EqualTo(expected).Within(0.001f));
        }

        [TestCase(EPathMoveType.TimeBased)]
        [TestCase(EPathMoveType.SpeedBased)]
        public void AggregatePlaybackMovesThroughLastSegmentAndCompletesOnce(EPathMoveType mode)
        {
            MultiPathData route = CreateRoute(CreatePath(0f, 10f), CreatePath(10f, 30f));
            PathFollower follower = CreateFollower();
            PathMovementSettings movement = mode == EPathMoveType.TimeBased
                ? PathMovementSettings.Time(3f)
                : PathMovementSettings.Speed(10f);
            int completed = 0;
            follower.Completed += () => completed++;
            follower.StartPlayback(PathPlaybackRequest.Aggregate(route, movement));
            follower.Tick(2f);
            Assert.That(follower.transform.position.x, Is.EqualTo(20f).Within(0.001f));
            follower.Tick(2f);
            follower.Tick(2f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Completed));
            Assert.That(follower.transform.position.x, Is.EqualTo(30f).Within(0.001f));
            Assert.That(completed, Is.EqualTo(1));
        }

        [Test]
        public void AggregateAndSequenceSeekAgreeInLastSegment()
        {
            MultiPathData route = CreateRoute(CreatePath(0f, 10f), CreatePath(10f, 30f));
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));
            follower.Seek(2f / 3f);
            Vector3 sequencePosition = follower.transform.position;
            Assert.That(sequencePosition.x, Is.EqualTo(20f).Within(0.001f));
            follower.StartPlayback(PathPlaybackRequest.Aggregate(route, PathMovementSettings.Time(5f)));
            follower.Seek(2f / 3f);
            Assert.That(Vector3.Distance(follower.transform.position, sequencePosition), Is.LessThan(0.001f));
        }

        [Test]
        public void NestedMultiPathSamplesChildLastSegment()
        {
            MultiPathData inner = CreateRoute(CreatePath(0f, 10f), CreatePath(10f, 30f));
            MultiPathData outer = CreateRoute(inner, CreatePath(30f, 40f));
            Assert.That(outer.SampleDistance(20f).x, Is.EqualTo(20f).Within(0.001f));
            Assert.That(outer.SampleDistance(35f).x, Is.EqualTo(35f).Within(0.001f));
        }

        [Test]
        public void DriverSkipsAnItemRemovedByAnEarlierTick()
        {
            var driver = new PathRuntimeDriver();
            var later = new Tickable(-100);
            var earlier = new Tickable(-200) { OnTick = () => driver.Unregister(later) };
            driver.Register(later);
            driver.Register(earlier);
            driver.Tick(0.1f);
            Assert.That(later.TickCount, Is.Zero);
            Assert.That(driver.LastException, Is.Null);
        }

        [Test]
        public void DriverDefersReregistrationEvenWhenPlaybackIdIsUnchanged()
        {
            var driver = new PathRuntimeDriver();
            var later = new Tickable(-100);
            bool changed = false;
            var earlier = new Tickable(-200) { OnTick = () =>
            {
                if (changed) return;
                changed = true;
                driver.Unregister(later);
                driver.Register(later);
            }};
            driver.Register(earlier);
            driver.Register(later);
            driver.Tick(0.1f);
            Assert.That(later.TickCount, Is.Zero);
            driver.Tick(0.1f);
            Assert.That(later.TickCount, Is.EqualTo(1));
        }

        [Test]
        public void DriverDefersPlaybackReplacedByAnEarlierTick()
        {
            var driver = new PathRuntimeDriver();
            var later = new Tickable(-100);
            driver.Register(new Tickable(-200) { OnTick = () => later.PlaybackId++ });
            driver.Register(later);
            driver.Tick(0.1f);
            Assert.That(later.TickCount, Is.Zero);
        }

        [Test]
        public void DriverKeepsPeerOrderIgnoresDuplicatesAndDefersNewItems()
        {
            var driver = new PathRuntimeDriver();
            var calls = new List<string>();
            var added = new Tickable(-300) { OnTick = () => calls.Add("added") };
            var second = new Tickable(-100) { OnTick = () => calls.Add("second") };
            var first = new Tickable(-100) { OnTick = () =>
            {
                calls.Add("first");
                driver.Register(second);
                driver.Register(added);
            }};
            driver.Register(first);
            driver.Register(second);
            driver.Tick(0.1f);
            CollectionAssert.AreEqual(new[] { "first", "second" }, calls);
            calls.Clear();
            driver.Tick(0.1f);
            CollectionAssert.AreEqual(new[] { "added", "first", "second" }, calls);
        }

        [Test]
        public void DriverReportsOriginalFaultAndContinuesOtherItems()
        {
            var driver = new PathRuntimeDriver();
            var failure = new InvalidOperationException("Expected tick failure");
            var failing = new Tickable(-200) { OnTick = () => throw failure, ThrowDuringCleanup = true };
            var later = new Tickable(-100);
            driver.Register(failing);
            driver.Register(later);
            driver.Tick(0.1f);
            Assert.That(driver.LastException, Is.SameAs(failure));
            Assert.That(failing.FaultPlaybackId, Is.EqualTo(1UL));
            Assert.That(later.TickCount, Is.EqualTo(1));
        }

        [TestCase("stop")]
        [TestCase("release")]
        [TestCase("complete")]
        [TestCase("replace")]
        public void PlaybackScopeClearsEffectsAndDelayedEventsOnEveryExit(string ending)
        {
            PathFollower follower = CreateFollower();
            PathData path = CreatePath(0f, 10f);
            path.ConfigureMovementSettings(PathMovementSettings.Speed(5f));
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            PathEventHandler handler = follower.GetComponent<PathEventHandler>();
            int delayedDispatches = 0;
            handler.OnEventDispatched += name => { if (name == "Delayed") delayedDispatches++; };
            InstallEffects(handler);
            Assert.That(Time.timeScale, Is.EqualTo(0.25f).Within(0.001f));
            Assert.That(handler.IsControllingSpeed, Is.True);

            if (ending == "stop") follower.StopMove();
            else if (ending == "release") follower.Release();
            else if (ending == "complete") follower.Tick(3f);
            else follower.StartPlayback(PathPlaybackRequest.Single(path));

            Assert.That(Time.timeScale, Is.EqualTo(1f).Within(0.001f));
            Assert.That(Time.fixedDeltaTime, Is.EqualTo(_fixedDeltaTime).Within(0.00001f));
            Assert.That(handler.IsControllingSpeed, Is.False);
            float speed = follower.Speed;
            handler.Tick(20f, 20f, 20);
            Assert.That(delayedDispatches, Is.Zero);
            Assert.That(follower.Speed, Is.EqualTo(speed));
        }

        [Test]
        public void CallbackFailureDisposesEffectsAndRestoresCharacterController()
        {
            PathFollower follower = CreateFollower(true);
            PathData path = CreatePath(0f, 10f);
            path.ConfigureMovementSettings(PathMovementSettings.Speed(5f));
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            InstallEffects(follower.GetComponent<PathEventHandler>());
            var failure = new InvalidOperationException("Expected listener failure");
            follower.StateChanged += state => { if (state == EPathFollowerState.Paused) throw failure; };
            Assert.That(Assert.Throws<InvalidOperationException>(() => follower.PauseMove()), Is.SameAs(failure));
            Assert.That(follower.PlaybackId, Is.Zero);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(Time.timeScale, Is.EqualTo(1f).Within(0.001f));
            Assert.That(follower.GetComponent<CharacterController>().enabled, Is.True);
        }

        [Test]
        public void StopCallbackCanStartNewPlaybackAndInstallNewEffects()
        {
            PathFollower follower = CreateFollower(true);
            PathData path = CreatePath(0f, 10f);
            PathEventHandler handler = follower.GetComponent<PathEventHandler>();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            InstallEffects(handler);
            Action<EPathFollowerState> restart = state =>
            {
                if (state != EPathFollowerState.Ready) return;
                follower.StartPlayback(PathPlaybackRequest.Single(path));
                PathEventSettingSO effect = CreateSetting();
                effect.UseTimeScaleAdjust = true;
                effect.TimeScaleAdjustValue = 0.75f;
                effect.TimeScaleAdjustDuration = 0.1f;
                handler.HandleEvent(effect);
                handler.Tick(0.2f, 0.2f, 2);
            };
            follower.StateChanged += restart;
            try
            {
                follower.StopMove();
                Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
                Assert.That(Time.timeScale, Is.EqualTo(0.75f).Within(0.001f));
                Assert.That(follower.GetComponent<CharacterController>().enabled, Is.False);
            }
            finally { follower.StateChanged -= restart; }
            follower.StopMove();
            Assert.That(Time.timeScale, Is.EqualTo(1f).Within(0.001f));
        }

        private void InstallEffects(PathEventHandler handler)
        {
            PathEventSettingSO delayed = CreateSetting();
            delayed.EventName = "Delayed";
            delayed.UseModifyPathMoveSpeed = true;
            delayed.MoveSpeedTargetValue = 12f;
            PathEventSettingSO effect = CreateSetting();
            effect.UseTimeScaleAdjust = true;
            effect.TimeScaleAdjustValue = 0.25f;
            effect.TimeScaleAdjustDuration = 0.1f;
            effect.UseModifyPathMoveSpeed = true;
            effect.MoveSpeedTargetValue = 10f;
            effect.MoveSpeedAdjustDuration = 10f;
            effect.UseDelayedEvents = true;
            effect.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = 10f, EventSetting = delayed });
            handler.HandleEvent(effect);
            handler.Tick(0.2f, 0.2f, 1);
        }

        private GameObject CreateObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        private PathData CreatePath(float start, float end)
        {
            PathData path = CreateObject("Path").AddComponent<PathData>();
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
            path.ConfigureMovementSettings(PathMovementSettings.Time(5f));
            path.ConfigureControlPoints(new[] { Vector3.right * start, Vector3.right * end });
            path.Init();
            return path;
        }

        private MultiPathData CreateRoute(params IPathProvider[] paths)
        {
            var descriptors = new PathSegmentDescriptor[paths.Length];
            for (int i = 0; i < paths.Length; i++)
                descriptors[i] = new PathSegmentDescriptor(paths[i], PathMovementSettings.Time(5f), false);
            MultiPathData route = CreateObject("Route").AddComponent<MultiPathData>();
            route.ConfigureSegments(descriptors);
            return route;
        }

        private PathFollower CreateFollower(bool withController = false)
        {
            GameObject go = CreateObject("Actor");
            if (withController) go.AddComponent<CharacterController>();
            PathFollower follower = go.AddComponent<PathFollower>();
            follower.Init();
            return follower;
        }

        private PathEventSettingSO CreateSetting()
        {
            var setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
            _objects.Add(setting);
            return setting;
        }

        private sealed class Tickable : IPathRuntimeTickable, IPathRuntimeFaultHandler
        {
            public Tickable(int order) => RuntimeOrder = order;
            public int RuntimeOrder { get; }
            public ulong PlaybackId { get; set; } = 1UL;
            public Action OnTick;
            public int TickCount;
            public ulong FaultPlaybackId;
            public bool ThrowDuringCleanup;
            public void Tick(float deltaTime, float unscaledDeltaTime, int frameId)
            {
                TickCount++;
                OnTick?.Invoke();
            }
            public void HandleRuntimeFault(ulong playbackId, Exception exception)
            {
                FaultPlaybackId = playbackId;
                if (ThrowDuringCleanup) throw new InvalidOperationException("Expected cleanup failure");
            }
        }
    }
}
