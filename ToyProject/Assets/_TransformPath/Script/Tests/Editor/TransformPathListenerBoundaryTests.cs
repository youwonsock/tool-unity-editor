using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    /// <summary>
    /// Public listeners run after the operation that raised them has returned,
    /// in order and without nesting, and a failing one changes nothing.
    /// </summary>
    public sealed class TransformPathListenerBoundaryTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object item in _objects)
                if (item != null) Object.DestroyImmediate(item);
            _objects.Clear();
        }

        [Test]
        public void ListenersRunAfterTheTickThatRaisedThem()
        {
            var seen = new List<float>();
            PathFollower follower = CreateFollower();
            follower.GetComponent<PathEventHandler>().OnEventDispatched += _ => seen.Add(follower.NormalizedTime);
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(10f, 0.1f, 0.2f)));

            follower.Tick(5f);

            Assert.That(seen, Is.EqualTo(new[] { 0.5f, 0.5f }), "Both listeners saw the finished tick, not the event positions.");
        }

        [Test]
        public void ACommandFromAListenerAppliesAfterTheTick()
        {
            var names = new List<string>();
            PathFollower follower = CreateFollower();
            var handler = follower.GetComponent<PathEventHandler>();
            handler.OnEventDispatched += name =>
            {
                names.Add(name);
                if (name == "t0.1")
                    follower.Seek(0.05f);
            };
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(10f, 0.1f, 0.2f)));

            follower.Tick(3f);
            Assert.That(names, Is.EqualTo(new[] { "t0.1", "t0.2" }), "The tick finished its events before the Seek.");
            Assert.That(follower.NormalizedTime, Is.EqualTo(0.05f).Within(0.0001f));

            follower.Tick(1f);
            Assert.That(names, Is.EqualTo(new[] { "t0.1", "t0.2", "t0.1" }), "Passing the event again runs it again.");
        }

        [Test]
        public void EveryListenerHearsWhatHappenedWhateverAnEarlierOneDid()
        {
            PathData path = CreatePath(1f);
            var queue = Create<QueuedPathFollower>("Agent");
            queue.Init();
            int laterCompleted = 0;
            int queueCompleted = 0;
            queue.Completed += () => queue.StartFreePlayback(PathPlaybackRequest.Single(CreatePath(100f)));
            queue.Completed += () => laterCompleted++;
            queue.OnCompleted += _ => queueCompleted++;
            queue.StartFreePlayback(PathPlaybackRequest.Single(path));

            queue.UnderlyingFollower.Tick(5f);

            Assert.That(laterCompleted, Is.EqualTo(1));
            Assert.That(queueCompleted, Is.EqualTo(1));
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving), "The first listener's new playback stands.");
        }

        [Test]
        public void ListenersDoNotNest()
        {
            var calls = new List<string>();
            PathFollower follower = CreateFollower();
            follower.StateChanged += state =>
            {
                calls.Add("enter:" + state);
                if (state == EPathFollowerState.Moving)
                    follower.PauseMove();
                calls.Add("exit:" + state);
            };
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(10f)));

            Assert.That(calls, Is.EqualTo(new[] { "enter:Moving", "exit:Moving", "enter:Paused", "exit:Paused" }));
        }

        [Test]
        public void AFailingListenerIsLoggedAndChangesNothing()
        {
            GameObject actor = CreateObject("Actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            var follower = actor.AddComponent<PathFollower>();
            Animator animator = actor.AddComponent<Animator>();
            var view = actor.AddComponent<PathFollowerAnimatorView>();
            typeof(PathFollowerAnimatorView).GetField("_animator", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, animator);
            follower.Init();
            PathData first = CreatePath(1f);
            PathData second = CreatePath(100f);
            var route = Create<MultiPathData>("Route");
            route.ConfigureSegments(new[]
            {
                new PathSegmentDescriptor(first, first.MovementSettings, false),
                new PathSegmentDescriptor(second, second.MovementSettings, false),
            });
            int later = 0;
            follower.SegmentChanged += _ => throw new InvalidOperationException("Listener failure");
            follower.SegmentChanged += _ => later++;
            follower.StartPlayback(PathPlaybackRequest.Sequence(route));

            LogAssert.Expect(LogType.Exception, new Regex("Listener failure"));
            Assert.DoesNotThrow(() => follower.Tick(2f));

            Assert.That(later, Is.EqualTo(1));
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(controller.enabled, Is.False);
            Assert.That(animator.speed, Is.EqualTo(1f));
        }

        [Test]
        public void AFailedCommandLeavesListenersWorking()
        {
            int notified = 0;
            PathFollower follower = CreateFollower();
            follower.StateChanged += _ => notified++;
            PathData notReady = Create<PathData>("Path");

            Assert.Throws<InvalidOperationException>(() => follower.StartPlayback(PathPlaybackRequest.Single(notReady)));
            Assert.That(notified, Is.Zero);

            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(10f)));
            Assert.That(notified, Is.EqualTo(1), "The boundary must not stay inside an operation after an exception.");
        }

        [Test]
        public void AnEventListenerSeesTheEffectOfThatEvent()
        {
            var resume = ScriptableObject.CreateInstance<PathEventSettingSO>();
            _objects.Add(resume);
            resume.UseModifyPathMoveSpeed = true;
            resume.MoveSpeedTargetValue = 2f;
            var pause = ScriptableObject.CreateInstance<PathEventSettingSO>();
            _objects.Add(pause);
            pause.EventName = "pause";
            pause.UseModifyPathMoveSpeed = true;
            pause.MoveSpeedTargetValue = 0f;
            pause.UseDelayedEvents = true;
            pause.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = 1f, EventSetting = resume });
            PathData path = CreatePath(10f);
            typeof(PathData).GetField("_pathEvents", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(path, new List<PathEventEntry> { new PathEventEntry { NormalizedTime = 0.1f, EventSetting = pause } });
            path.Rebuild();
            PathFollower follower = CreateFollower();
            var seen = new List<EPathFollowerState>();
            follower.GetComponent<PathEventHandler>().OnEventDispatched += _ => seen.Add(follower.State);
            follower.StartPlayback(PathPlaybackRequest.Single(path));

            follower.Tick(2f);

            Assert.That(seen, Is.EqualTo(new[] { EPathFollowerState.Paused }));
        }

        private GameObject CreateObject(string name)
        {
            var item = new GameObject(name);
            _objects.Add(item);
            return item;
        }

        private T Create<T>(string name) where T : Component => CreateObject(name).AddComponent<T>();

        private PathFollower CreateFollower()
        {
            var follower = Create<PathFollower>("Follower");
            follower.Init();
            return follower;
        }

        private PathData CreatePath(float length, params float[] events)
        {
            var path = Create<PathData>("Path");
            path.ConfigureMovementSettings(PathMovementSettings.Speed(1f));
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
            var entries = new List<PathEventEntry>();
            foreach (float time in events)
            {
                var setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
                _objects.Add(setting);
                setting.EventName = "t" + time;
                entries.Add(new PathEventEntry { NormalizedTime = time, EventSetting = setting });
            }
            typeof(PathData).GetField("_pathEvents", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(path, entries);
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * length });
            path.Init();
            return path;
        }
    }
}
