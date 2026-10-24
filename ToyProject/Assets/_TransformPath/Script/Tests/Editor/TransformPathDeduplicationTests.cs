using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathDeduplicationTests
    {
        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [TestCase("Single", false, false)]
        [TestCase("Single", true, false)]
        [TestCase("Single", false, true)]
        [TestCase("Single", true, true)]
        [TestCase("Aggregate", false, false)]
        [TestCase("Aggregate", true, false)]
        [TestCase("Aggregate", false, true)]
        [TestCase("Aggregate", true, true)]
        [TestCase("Sequence", false, false)]
        [TestCase("Sequence", true, false)]
        [TestCase("Sequence", false, true)]
        [TestCase("Sequence", true, true)]
        public void BoundaryKeepsEventTransitionAndCompletionOrder(string kind, bool speed, bool loop)
        {
            PathMovementSettings movement = speed ? PathMovementSettings.Speed(10f) : PathMovementSettings.Time(1f);
            PathData first = CreatePath(0f, 10f, movement);
            SetEvents(first, Event(0.5f, "first"));
            PathData second = CreatePath(10f, 20f, movement);
            SetEvents(second, Event(0.5f, "second"));
            PathPlaybackRequest request = kind == "Sequence"
                ? PathPlaybackRequest.Sequence(CreateRoute(first, second), loop)
                : kind == "Aggregate" ? PathPlaybackRequest.Aggregate(first, movement, loop)
                : PathPlaybackRequest.Single(first, loop);
            GameObject actor = CreateObject("Boundary actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            PathFollower follower = actor.AddComponent<PathFollower>(); follower.Init();
            var calls = new List<string>();
            follower.GetComponent<PathEventHandler>().OnEventDispatched += name => calls.Add(name);
            follower.SegmentChanged += index => calls.Add("segment:" + index);
            follower.LoopBoundary += () =>
            {
                calls.Add("loop");
                Assert.That(follower.NormalizedTime, Is.EqualTo(kind == "Sequence" ? 0f : 1f));
                Assert.That(controller.enabled, Is.False);
            };
            follower.StateChanged += state => { if (state == EPathFollowerState.Completed) calls.Add("state:completed"); };
            follower.Completed += () =>
            {
                calls.Add("completed");
                Assert.That(follower.PlaybackId, Is.Zero);
                Assert.That(controller.enabled, Is.True);
            };
            ulong id = follower.StartPlayback(request).PlaybackId;
            follower.Tick(kind == "Sequence" ? 2.25f : 1.25f);
            var expected = new List<string> { "first" };
            if (kind == "Sequence") expected.AddRange(new[] { "segment:1", "second" });
            if (loop)
            {
                expected.Add("loop");
                if (kind == "Sequence") expected.Add("segment:0");
            }
            else expected.AddRange(new[] { "state:completed", "completed" });
            CollectionAssert.AreEqual(expected, calls);
            Assert.That(follower.State, Is.EqualTo(loop ? EPathFollowerState.Moving : EPathFollowerState.Completed));
            Assert.That(follower.PlaybackId, Is.EqualTo(loop ? id : 0UL));
            Assert.That(actor.transform.position.x, Is.EqualTo(loop ? 2.5f : kind == "Sequence" ? 20f : 10f).Within(0.001f));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void PauseFromFacadeOrUnderlyingKeepsQueueAndNotifications(bool queued, bool direct)
        {
            PathData path = CreatePath(0f, 10f, PathMovementSettings.Time(5f));
            QueuedPathManager manager = CreateObject("Manager").AddComponent<QueuedPathManager>();
            manager.Init(); manager.ConfigureRoute(path);
            GameObject actor = CreateObject("Queue actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            QueuedPathFollower queue = actor.AddComponent<QueuedPathFollower>(); queue.Init();
            if (queued) queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            else queue.StartFreePlayback(PathPlaybackRequest.Single(path));
            ulong id = queue.PlaybackId;
            int pauses = 0;
            queue.StateChanged += state =>
            {
                if (state != EPathFollowerState.Paused) return;
                pauses++;
                Assert.That(queue.IsRegistered, Is.False);
                Assert.That(queue.IsBlocked, Is.True);
                Assert.That(queue.CurrentSpeedMultiplier, Is.Zero);
            };
            PathCommandReceipt receipt = direct ? queue.UnderlyingFollower.PauseMove() : queue.PauseMove();
            Assert.That(receipt.Changed, Is.True);
            Assert.That(pauses, Is.EqualTo(1));
            Assert.That(manager.AgentCount, Is.Zero);
            Assert.That(queue.PlaybackId, Is.EqualTo(id));
            Assert.That(controller.enabled, Is.False);
            Assert.That(queue.PauseMove().Changed, Is.False);
            if (direct) queue.UnderlyingFollower.ResumeMove(); else queue.ResumeMove();
            Assert.That(queue.IsRegistered, Is.EqualTo(queued));
            Assert.That(manager.AgentCount, Is.EqualTo(queued ? 1 : 0));
            Assert.That(queue.PlaybackId, Is.EqualTo(id));
            ((IPathRuntimeTickable)manager).Tick(0.2f, 0.2f, 100);
            ((IPathRuntimeTickable)queue).Tick(0.2f, 0.2f, 100);
            queue.UnderlyingFollower.Tick(0.2f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
            queue.StopMove();
            Assert.That(controller.enabled, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PauseCallbackReplacementKeepsNewPlaybackQueueState(bool queuedReplacement)
        {
            PathData path = CreatePath(0f, 10f, PathMovementSettings.Time(5f));
            QueuedPathManager manager = CreateObject("Manager").AddComponent<QueuedPathManager>();
            manager.Init(); manager.ConfigureRoute(path);
            QueuedPathFollower queue = CreateObject("Queue actor").AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            ulong oldId = queue.PlaybackId;
            queue.StateChanged += state =>
            {
                if (state != EPathFollowerState.Paused) return;
                if (queuedReplacement) queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                else queue.StartFreePlayback(PathPlaybackRequest.Single(path));
            };
            PathCommandReceipt paused = queue.PauseMove();
            Assert.That(paused.PlaybackId, Is.EqualTo(oldId));
            Assert.That(queue.PlaybackId, Is.GreaterThan(oldId));
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(queue.IsRegistered, Is.EqualTo(queuedReplacement));
            Assert.That(manager.AgentCount, Is.EqualTo(queuedReplacement ? 1 : 0));
            Assert.That(queue.IsBlocked, Is.EqualTo(queuedReplacement));
            Assert.That(queue.CurrentSpeedMultiplier, Is.EqualTo(queuedReplacement ? 0f : 1f));
            ((IPathRuntimeTickable)manager).Tick(0.2f, 0.2f, 100);
            ((IPathRuntimeTickable)queue).Tick(0.2f, 0.2f, 100);
            queue.UnderlyingFollower.Tick(0.2f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RepeatedPreparationKeepsEqualTimeOrderAndCancelsPreviousDelays(bool sequence)
        {
            PathData path = CreatePath(0f, 10f, PathMovementSettings.Time(1f));
            PathEventEntry root = Event(0.1f, "root");
            root.EventSetting.UseDelayedEvents = true;
            root.EventSetting.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry {
                Delay = 1f, EventSetting = Event(0f, "cancelled").EventSetting
            });
            SetEvents(path, root, Event(0.2f, "equal-a"), Event(0.2f, "equal-b"));
            PathFollower follower = CreateObject("Event actor").AddComponent<PathFollower>(); follower.Init();
            PathEventHandler handler = follower.GetComponent<PathEventHandler>();
            var calls = new List<string>(); handler.OnEventDispatched += calls.Add;
            PathPlaybackRequest request = sequence ? PathPlaybackRequest.Sequence(CreateRoute(path)) : PathPlaybackRequest.Single(path);
            for (int run = 0; run < 2; run++)
            {
                follower.StartPlayback(request);
                follower.Tick(0.15f);
                follower.Tick(0.1f);
                handler.Tick(2f, 2f, int.MaxValue - 1);
            }
            CollectionAssert.AreEqual(new[] { "root", "equal-a", "equal-b", "root", "equal-a", "equal-b" }, calls);
        }

        [Test]
        public void ReinitializedFacadeReceivesCurrentPauseNotifications()
        {
            PathData path = CreatePath(0f, 10f, PathMovementSettings.Time(5f));
            QueuedPathFollower queue = CreateObject("Released facade").AddComponent<QueuedPathFollower>();
            queue.Init(); queue.StartFreePlayback(PathPlaybackRequest.Single(path)); queue.Release();
            queue.UnderlyingFollower.Init();
            queue.UnderlyingFollower.StartPlayback(PathPlaybackRequest.Single(path));
            queue.Init();
            Assert.That(queue.PauseMove().Changed, Is.True);
            Assert.That(queue.IsInitialized, Is.True);
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Paused));
            Assert.That(queue.IsBlocked, Is.True);
            Assert.That(queue.CurrentSpeedMultiplier, Is.Zero);
        }

        [Test]
        public void ManagerSettingChangesFromAgentCallbackApplyOnNextFrame()
        {
            PathData path = CreatePath(0f, 10f, PathMovementSettings.Time(5f));
            QueuedPathManager manager = CreateObject("Manager").AddComponent<QueuedPathManager>();
            manager.Init(); manager.ConfigureRoute(path);
            var ahead = new TestQueueAgent(path, 0.8f);
            var behind = new TestQueueAgent(path, 0.4f);
            ahead.Callback = () => manager.DefaultSpacing = 8f;
            manager.Register(ahead); manager.Register(behind);
            ((IPathRuntimeTickable)manager).Tick(0.1f, 0.1f, 100);
            Assert.That(behind.State.IsBlocked, Is.False);
            Assert.That(behind.State.MaxGlobalNormalizedTime, Is.EqualTo(0.65f).Within(0.00001f));
            ((IPathRuntimeTickable)manager).Tick(0.1f, 0.1f, 101);
            Assert.That(behind.State.IsBlocked, Is.True);
            Assert.That(behind.State.MaxGlobalNormalizedTime, Is.EqualTo(0f).Within(0.00001f));
        }

        [Test]
        public void EventContentComparisonKeepsPublishedRevisionAndSnapshotInAgreement()
        {
            Action<PathEventSettingSO>[] mutations =
            {
                s => s.EventName = "changed",
                s => s.UseModifyPathMoveSpeed = true,
                s => s.MoveSpeedTargetValue += 1f,
                s => s.MoveSpeedAdjustDuration += 1f,
                s => s.MoveSpeedAdjustCurve = AnimationCurve.Linear(0f, 0f, 1f, 2f),
                s => s.UseModifyPathMoveDuration = true,
                s => s.MoveDurationTargetValue += 1f,
                s => s.MoveDurationAdjustDuration += 1f,
                s => s.MoveDurationAdjustCurve.postWrapMode = WrapMode.Loop,
                s => s.UseTimeScaleAdjust = true,
                s => s.TimeScaleAdjustValue += 1f,
                s => s.TimeScaleAdjustDuration += 1f,
                s => s.TimeScaleAdjustCurve = AnimationCurve.Linear(0f, 0f, 1f, 0.5f),
                s => s.DelayedEvents[0].Delay += 1f,
                s => s.DelayedEvents[0].EventSetting.EventName = "changed child",
            };
            for (int i = 0; i < mutations.Length; i++)
            {
                PathData path = CreatePath(0f, 10f, PathMovementSettings.Time(1f));
                PathEventEntry entry = Event(0.5f, "root");
                entry.EventSetting.UseDelayedEvents = true;
                entry.EventSetting.DelayedEvents.Add(new PathEventSettingSO.DelayedEventEntry
                {
                    Delay = 1f, EventSetting = Event(0f, "child").EventSetting,
                });
                SetEvents(path, entry);
                int revision = path.Revision;
                PathEventSourceSnapshot snapshot = PathEventSourceSnapshot.Create(path);
                path.Rebuild();
                Assert.That(path.Revision, Is.EqualTo(revision), "Equivalent definitions must not invalidate playback.");
                Assert.That(snapshot.Matches(path), Is.True);
                mutations[i](entry.EventSetting);
                // Authoring mutation cannot alter already published definitions.
                Assert.That(snapshot.Matches(path), Is.True);
                path.Rebuild();
                Assert.That(path.Revision, Is.GreaterThan(revision), "Mutation " + i);
                Assert.That(snapshot.Matches(path), Is.False, "Mutation " + i);
                Assert.That(snapshot.HasSameContent(PathEventSourceSnapshot.Create(path)), Is.False);
            }
        }

        [Test]
        public void AuthoringAndExternalSourcesKeepEqualPositionOrder()
        {
            PathData path = CreatePath(0f, 10f, PathMovementSettings.Time(1f));
            SetEvents(path, Event(0.8f, "last"), Event(0.2f, "first"), Event(0.2f, "second"));
            Assert.That(path.GetEvent(0).Definition.EventName, Is.EqualTo("first"));
            Assert.That(path.GetEvent(1).Definition.EventName, Is.EqualTo("second"));
            var external = new EventSource(new[] { path.GetEvent(2), path.GetEvent(0), path.GetEvent(1) });
            PathEventSourceSnapshot snapshot = PathEventSourceSnapshot.Create(external);
            Assert.That(snapshot.Matches(path), Is.True);
            Assert.That(snapshot.HasSameContent(PathEventSourceSnapshot.Create(path)), Is.True);
            Assert.That(external.GetEvent(0).Definition.EventName, Is.EqualTo("last"), "Preparation must not mutate the provider.");
        }

        private sealed class EventSource : IPathEventSource
        {
            private readonly PathRuntimeEvent[] _events;
            public EventSource(PathRuntimeEvent[] events) { _events = events; }
            public int EventCount => _events.Length;
            public PathRuntimeEvent GetEvent(int index) => _events[index];
        }

        private sealed class TestQueueAgent : IQueuedPathAgent
        {
            public TestQueueAgent(IPathProvider provider, float progress) { QueueProvider = provider; GlobalNormalizedTime = progress; }
            public ulong PlaybackId => 1UL;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime { get; }
            public int SnapshotRevision => QueueProvider.Revision;
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1.5f, true, true, true);
            public Action Callback;
            public PathQueueState State;
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) { State = state; Callback?.Invoke(); }
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason) { }
        }

        private PathEventEntry Event(float progress, string name)
        {
            var setting = ScriptableObject.CreateInstance<PathEventSettingSO>(); _objects.Add(setting);
            setting.EventName = name;
            return new PathEventEntry { NormalizedTime = progress, EventSetting = setting };
        }

        private void SetEvents(PathData path, params PathEventEntry[] events)
        {
            typeof(PathData).GetField("_pathEvents", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(path, new List<PathEventEntry>(events));
            path.Rebuild();
        }

        private PathData CreatePath(float start, float end, PathMovementSettings movement)
        {
            var path = CreateObject("Path").AddComponent<PathData>();
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
            path.ConfigureMovementSettings(movement);
            path.ConfigureControlPoints(new[] { Vector3.right * start, Vector3.right * end }); path.Init();
            return path;
        }

        private MultiPathData CreateRoute(params PathData[] paths)
        {
            var route = CreateObject("Route").AddComponent<MultiPathData>();
            var segments = new List<PathSegmentDescriptor>();
            foreach (PathData path in paths) segments.Add(new PathSegmentDescriptor(path, path.MovementSettings, false));
            route.ConfigureSegments(segments);
            return route;
        }

        private GameObject CreateObject(string name)
        {
            var go = new GameObject(name); _objects.Add(go); return go;
        }
    }
}
