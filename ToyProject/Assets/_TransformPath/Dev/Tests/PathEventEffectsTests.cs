using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Common.TransformPath.Tests
{
    public sealed class PathEventEffectsTests : PathBoundaryIsolatedTest
    {
        private const float STEP = 0.25f;

        private sealed class FakeTarget : IPathEffectTarget
        {
            public readonly List<string> Log = new List<string>();

            public EPathMoveType MoveType => EPathMoveType.SpeedBased;
            public ulong PlaybackId { get; set; } = 1;
            public bool HasRoute => true;
            public float Speed { get; private set; } = 2f;
            public float Duration => 1f;

            public bool Pause()
            {
                Log.Add("pause");
                return true;
            }

            public bool Resume()
            {
                Log.Add("resume");
                return true;
            }

            public bool ApplyValue(EPathEffectChannel channel, float value)
            {
                if (Mathf.Approximately(Speed, value))
                    return false;
                Speed = value;
                return true;
            }

            public void PublishEvent(string eventName) => Log.Add(eventName);
        }

        private PathTestScene _scene;
        private FakeTarget _target;
        private PathEventEffects _effects;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            _scene = new PathTestScene();
            _target = new FakeTarget();
            _effects = new PathEventEffects(_target);
            _frame = 0;
            Tick();
        }

        [TearDown]
        public void TearDown() => _scene.Dispose();

        private void Tick() => _effects.Tick(STEP, ++_frame);

        private PathEventSettingSO Event(string name, params (float delay, PathEventSettingSO setting)[] delayed)
        {
            PathEventSettingSO setting = _scene.CreateEvent(name);
            foreach ((float delay, PathEventSettingSO next) in delayed)
                setting.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry { Delay = delay, EventSetting = next });
            return setting;
        }

        private void Run(PathEventSettingSO setting) => _effects.Run(PathEventDefinition.FromAuthoring(setting));

        [Test]
        public void DelayedEvent_RunsOnceItsDelayHasPassed()
        {
            Run(Event("a", (2f * STEP, Event("b"))));

            Tick();
            CollectionAssert.AreEqual(new[] { "a" }, _target.Log);
            Tick();

            CollectionAssert.AreEqual(new[] { "a", "b" }, _target.Log);
        }

        [TestCase("lifecycle")]
        [TestCase("speed")]
        [TestCase("duration")]
        [TestCase("delayed")]
        public void NextEventThatChangesThePlayback_CancelsWhatEarlierEventsLeftWaiting(string kind)
        {
            Run(Event("a", (2f * STEP, Event("b"))));
            Tick();

            PathEventSettingSO next = kind == "delayed" ? Event("c", (10f, Event("later"))) : Event("c");
            if (kind == "lifecycle")
                next.Lifecycle = EPathEventLifecycle.Resume;
            next.UseSpeedChange = kind == "speed";
            next.UseDurationChange = kind == "duration";
            Run(next);
            Tick();
            Tick();

            CollectionAssert.DoesNotContain(_target.Log, "b");
        }

        [Test]
        public void NextEventWithOnlyAName_KeepsWhatEarlierEventsLeftWaiting()
        {
            Run(Event("a", (2f * STEP, Event("b"))));
            Tick();

            Run(Event("c"));
            Tick();

            CollectionAssert.AreEqual(new[] { "a", "c", "b" }, _target.Log);
        }

        [Test]
        public void ZeroDelay_RunsAtOnce()
        {
            Run(Event("a", (0f, Event("now"))));

            CollectionAssert.AreEqual(new[] { "a", "now" }, _target.Log);
            Assert.IsFalse(_effects.HasPending);
        }

        [Test]
        public void ZeroDelayChildThatChangesSomething_StopsTheRest()
        {
            PathEventSettingSO pause = Event("p");
            pause.Lifecycle = EPathEventLifecycle.Pause;
            Run(Event("a", (0f, pause), (0f, Event("n")), (STEP, Event("later"))));
            Tick();
            Tick();

            CollectionAssert.AreEqual(new[] { "a", "p", "pause" }, _target.Log);
            Assert.IsFalse(_effects.HasPending);
        }

        [Test]
        public void DelayedEventThatChangesSomething_EndsThatFramesDelayedEvents()
        {
            PathEventSettingSO pause = Event("p");
            pause.Lifecycle = EPathEventLifecycle.Pause;
            Run(Event("a", (STEP, pause), (STEP, Event("n"))));

            Tick();
            CollectionAssert.AreEqual(new[] { "a", "p", "pause" }, _target.Log);
            Tick();

            CollectionAssert.AreEqual(new[] { "a", "p", "pause", "n" }, _target.Log);
        }

        [Test]
        public void SpeedBlend_ReachesItsTargetOverTheBlendTime()
        {
            PathEventSettingSO faster = Event("faster");
            faster.UseSpeedChange = true;
            faster.SpeedTarget = 4f;
            faster.SpeedBlendTime = 4f * STEP;
            faster.SpeedBlendCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            Run(faster);

            Tick();
            Tick();
            Assert.AreEqual(3f, _target.Speed, 1e-5f);
            Tick();
            Tick();

            Assert.AreEqual(4f, _target.Speed, 1e-5f);
            Assert.IsFalse(_effects.HasPending);
        }

        [Test]
        public void FinishedPlayback_AppliesTheTargetAtOnceAndSchedulesNothing()
        {
            _target.PlaybackId = 0;
            PathEventSettingSO faster = Event("faster", (STEP, Event("late")));
            faster.UseSpeedChange = true;
            faster.SpeedTarget = 4f;
            faster.SpeedBlendTime = 1f;
            faster.SpeedBlendCurve = AnimationCurve.Linear(0f, 0f, 1f, 1f);

            Run(faster);
            Assert.AreEqual(4f, _target.Speed);
            _target.PlaybackId = 1;
            Tick();
            Tick();

            CollectionAssert.AreEqual(new[] { "faster" }, _target.Log);
        }
    }
}
