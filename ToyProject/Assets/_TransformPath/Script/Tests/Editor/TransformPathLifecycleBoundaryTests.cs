using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathLifecycleBoundaryTests
    {
        private readonly List<GameObject> objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = objects.Count - 1; i >= 0; i--)
                if (objects[i] != null) Object.DestroyImmediate(objects[i]);
            objects.Clear();
        }

        [Test]
        public void QueuedStartMovingCallbackPauseRemainsUnregistered()
        {
            PathData path = Path();
            QueuedPathManager manager = Manager(path);
            QueuedPathFollower queue = Actor().AddComponent<QueuedPathFollower>();
            queue.Init();
            queue.StateChanged += state => { if (state == EPathFollowerState.Moving) queue.PauseMove(); };
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Paused));
            Assert.That(queue.IsRegistered, Is.False, $"Paused agent was registered again; manager count={manager.AgentCount}");
        }

        [Test]
        public void FreeStartMovingCallbackPausePreservesQueuePauseState()
        {
            PathData path = Path();
            QueuedPathFollower queue = Actor().AddComponent<QueuedPathFollower>();
            queue.Init();
            queue.StateChanged += state => { if (state == EPathFollowerState.Moving) queue.PauseMove(); };
            queue.StartFreePlayback(PathPlaybackRequest.Single(path));
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Paused));
            Assert.That(queue.IsBlocked, Is.True, $"Pause state was overwritten; speed multiplier={queue.CurrentSpeedMultiplier}");
            Assert.That(queue.CurrentSpeedMultiplier, Is.Zero);
        }

        [Test]
        public void PauseCallbackNewPlaybackKeepsAnimatorRunning()
        {
            PathData path = Path();
            PathFollower follower = AnimatedFollower(out Animator animator);
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            ulong oldId = follower.PlaybackId;
            follower.StateChanged += state => { if (state == EPathFollowerState.Paused) follower.StartPlayback(PathPlaybackRequest.Single(path)); };
            follower.PauseMove();
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(follower.PlaybackId, Is.Not.EqualTo(oldId));
            Assert.That(animator.speed, Is.EqualTo(1f), "Old Pause command overwrote new playback animator speed");
        }

        [Test]
        public void ResumeCallbackPauseKeepsAnimatorStopped()
        {
            PathData path = Path();
            PathFollower follower = AnimatedFollower(out Animator animator);
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            follower.PauseMove();
            follower.StateChanged += state => { if (state == EPathFollowerState.Moving) follower.PauseMove(); };
            follower.ResumeMove();
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Paused));
            Assert.That(animator.speed, Is.Zero, "Old Resume command overwrote nested Pause animator speed");
        }

        [TestCase("Start")]
        [TestCase("Pause")]
        [TestCase("Resume")]
        public void MovementAdjustmentCallbackPreservesCurrentAnimatorState(string command)
        {
            PathData path = Path();
            PathFollower follower = AnimatedFollower(out Animator animator);
            if (command != "Start") follower.StartPlayback(PathPlaybackRequest.Single(path));
            if (command == "Resume") follower.PauseMove();
            System.Action<EPathFollowerState> adjust = _ => follower.SetDuration(7f);
            follower.StateChanged += adjust;
            if (command == "Start") follower.StartPlayback(PathPlaybackRequest.Single(path));
            else if (command == "Pause") follower.PauseMove();
            else follower.ResumeMove();
            Assert.That(follower.Duration, Is.EqualTo(7f));
            Assert.That(animator.speed, Is.EqualTo(command == "Pause" ? 0f : 1f));
            // Do not apply a movement command to the later Ready teardown event.
            follower.StateChanged -= adjust;
        }

        [TestCase("Single", false)]
        [TestCase("Single", true)]
        [TestCase("Aggregate", false)]
        [TestCase("Aggregate", true)]
        [TestCase("Sequence", false)]
        [TestCase("Sequence", true)]
        public void QueueBlockedConstraintSurvivesGeometryRevision(string kind, bool changeGeometry)
        {
            PathData path = Path();
            PathPlaybackRequest request = Request(kind, path);
            IPathProvider provider = request.Provider;
            QueuedPathManager manager = Manager(provider);
            var ahead = new AheadAgent(provider) { Progress = 0.8f };
            manager.Register(ahead);
            QueuedPathFollower queue = Actor().AddComponent<QueuedPathFollower>();
            queue.Init();
            queue.EnableOvertakeProtection = false;
            queue.EnableGradualSlowdown = false;
            queue.StartQueuedPlayback(manager, request);
            PathQueueRegistration registration = manager.Register(queue);
            Tick(manager, queue, 1, 0.2f);
            Assert.That(queue.IsBlocked, Is.False);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
            int originalRevision = provider.Revision;
            if (changeGeometry)
                path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 20f });
            ahead.Progress = queue.GlobalNormalizedTime + 0.01f;
            Tick(manager, queue, 2, 0f);
            Assert.That(queue.SnapshotRevision, Is.EqualTo(provider.Revision));
            Assert.That(queue.IsBlocked, Is.True);
            float before = queue.GlobalNormalizedTime;
            Tick(manager, queue, 3, 0.2f);
            Assert.That(queue.IsBlocked, Is.True);
            Assert.That(queue.GlobalNormalizedTime, Is.EqualTo(before).Within(0.00001f),
                $"Blocked queue moved after route revision {originalRevision}->{provider.Revision}; multiplier={queue.CurrentSpeedMultiplier}");
            ahead.Progress = 0.8f;
            Tick(manager, queue, 4, 0.2f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(before), "Fresh constraints must unblock the same registration");
            Assert.That(manager.Register(queue).RegistrationId, Is.EqualTo(registration.RegistrationId));
            Assert.That(manager.Unregister(registration), Is.True, "Original ownership token must remain releasable");
            Assert.That(queue.IsRegistered, Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ExternalEventOrderDoesNotTurnGeometryChangeIntoPlaybackStop(bool unsorted)
        {
            PathData path = Path();
            PathEventSettingSO setting = ScriptableObject.CreateInstance<PathEventSettingSO>();
            PathEventDefinition definition;
            try
            {
                setting.EventName = "Review event";
                definition = PathEventDefinition.FromAuthoring(setting);
            }
            finally { Object.DestroyImmediate(setting); }
            var early = new PathRuntimeEvent(0.2f, definition);
            var late = new PathRuntimeEvent(0.8f, definition);
            var provider = new ExternalEventPath(path, unsorted ? new[] { late, early } : new[] { early, late });
            PathFollower follower = Actor().AddComponent<PathFollower>();
            follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(provider));
            ulong id = follower.PlaybackId;
            follower.Tick(0.1f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 20f });
            follower.Tick(0f);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving), "Unchanged external events were classified as a definition change");
            Assert.That(follower.PlaybackId, Is.EqualTo(id));
        }

        [Test]
        public void ConstraintUsesCurrentRouteRevisionAndRejectsStaleFutureAndOlderFrames()
        {
            PathData path = Path();
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            var token = new PathQueueRegistration(new object(), 1, follower.PlaybackId, path.Revision);
            Assert.That(follower.AttachQueueConstraint(token), Is.True);
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 20f });
            follower.ApplyQueueConstraint(token, new PathQueueConstraint(token, true, 0f, 1f, 2, path.Revision));
            follower.ApplyQueueConstraint(token, new PathQueueConstraint(token, false, 1f, 1f, 3, token.RouteRevision));
            follower.ApplyQueueConstraint(token, new PathQueueConstraint(token, false, 1f, 1f, 3, path.Revision + 1));
            follower.Tick(0.2f);
            Assert.That(follower.GlobalNormalizedTime, Is.Zero, "Stale and future versions must not replace the current blocked result");
            follower.ApplyQueueConstraint(token, new PathQueueConstraint(token, false, 1f, 1f, 3, path.Revision));
            follower.ApplyQueueConstraint(token, new PathQueueConstraint(token, true, 0f, 1f, 2, path.Revision));
            follower.Tick(0.2f);
            Assert.That(follower.GlobalNormalizedTime, Is.GreaterThan(0f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void InvalidationStopsOldNotificationsAfterReplacement(bool replaceFromReady)
        {
            PathData path = Path();
            QueuedPathManager manager = Manager(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            int notifications = 0;
            if (replaceFromReady)
                queue.StateChanged += state => { if (state == EPathFollowerState.Ready) queue.StartFreePlayback(PathPlaybackRequest.Single(path)); };
            else
                queue.QueueInvalidated += (_, __) => queue.StartFreePlayback(PathPlaybackRequest.Single(path));
            queue.QueueInvalidated += (_, __) => notifications++;
            manager.Release();
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(notifications, Is.Zero, "Old invalidation must not be delivered to replacement playback");
            queue.UnderlyingFollower.Tick(0.1f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
        }

        [Test]
        public void NormalInvalidationNotifiesBothListenersOnce()
        {
            PathData path = Path();
            QueuedPathManager manager = Manager(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            var calls = new List<int>();
            queue.QueueInvalidated += (_, reason) => { Assert.That(reason, Is.EqualTo(EPathQueueDetachReason.ManagerReleased)); calls.Add(1); };
            queue.QueueInvalidated += (_, __) => calls.Add(2);
            manager.Release();
            CollectionAssert.AreEqual(new[] { 1, 2 }, calls);
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(queue.IsRegistered, Is.False);
        }

        [Test]
        public void ReplacingMovingPlaybackRegistersOnceWithoutStateChangedNotification()
        {
            PathData path = Path();
            QueuedPathManager manager = Manager(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            ulong previous = queue.PlaybackId;
            int changed = 0;
            queue.StateChanged += _ => changed++;
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Assert.That(queue.PlaybackId, Is.Not.EqualTo(previous));
            Assert.That(queue.IsRegistered, Is.True);
            Assert.That(manager.AgentCount, Is.EqualTo(1));
            Assert.That(changed, Is.Zero);
            Tick(manager, queue, 1, 0.2f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
        }

        private PathPlaybackRequest Request(string kind, PathData path)
        {
            if (kind == "Single") return PathPlaybackRequest.Single(path);
            if (kind == "Aggregate") return PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(5f));
            var route = Actor().AddComponent<MultiPathData>();
            route.ConfigureSegments(new[] { new PathSegmentDescriptor(path, path.MovementSettings, false) });
            return PathPlaybackRequest.Sequence(route);
        }

        [TestCase("stale revision")]
        [TestCase("future revision")]
        [TestCase("old frame")]
        [TestCase("other playback")]
        [TestCase("missing playback")]
        public void FacadeRejectsResultsOutsideCurrentCalculation(string invalidity)
        {
            PathData path = Path();
            QueuedPathManager manager = Manager(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            var registration = manager.Register(queue);
            Tick(manager, queue, 10, 0.2f);
            Assert.That(queue.IsBlocked, Is.False);
            int revision = path.Revision + (invalidity == "stale revision" ? -1 : invalidity == "future revision" ? 1 : 0);
            int frame = invalidity == "old frame" ? 9 : 11;
            ulong playback = invalidity == "missing playback" ? 0UL : invalidity == "other playback" ? queue.PlaybackId + 1 : queue.PlaybackId;
            queue.ApplyQueueState(registration, new PathQueueState(null, null, true, 0f, 1f, revision, playback, frame));
            ((IPathRuntimeTickable)queue).Tick(0.2f, 0.2f, 11);
            Assert.That(queue.IsBlocked, Is.False);
            Assert.That(queue.CurrentSpeedMultiplier, Is.EqualTo(1f));
        }

        private sealed class ExternalEventPath : IPathMovementProvider, IPathEventSource
        {
            private readonly PathData path;
            private readonly PathRuntimeEvent[] events;
            public ExternalEventPath(PathData path, PathRuntimeEvent[] events) { this.path = path; this.events = events; }
            public bool IsInitialized => path.IsInitialized;
            public bool IsReady => path.IsReady;
            public int Revision => path.Revision;
            public float PathLength => path.PathLength;
            public PathMovementSettings MovementSettings => path.MovementSettings;
            public event System.Action PathChanged { add { path.PathChanged += value; } remove { path.PathChanged -= value; } }
            public Vector3 Sample(float progress) => path.Sample(progress);
            public Vector3 SampleDistance(float distance) => path.SampleDistance(distance);
            public int EventCount => events.Length;
            public PathRuntimeEvent GetEvent(int index) => events[index];
        }

        private void Tick(QueuedPathManager manager, QueuedPathFollower queue, int frame, float delta)
        {
            ((IPathRuntimeTickable)manager).Tick(delta, delta, frame);
            ((IPathRuntimeTickable)queue).Tick(delta, delta, frame);
            queue.UnderlyingFollower.Tick(delta);
        }

        private GameObject Actor()
        {
            var obj = new GameObject("Lifecycle boundary actor");
            objects.Add(obj);
            return obj;
        }

        private PathData Path()
        {
            var path = Actor().AddComponent<PathData>();
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
            path.ConfigureMovementSettings(PathMovementSettings.Time(5f));
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 10f });
            path.Init();
            return path;
        }

        private QueuedPathManager Manager(IPathProvider path)
        {
            var manager = Actor().AddComponent<QueuedPathManager>();
            manager.Init();
            manager.ConfigureRoute(path);
            return manager;
        }

        private PathFollower AnimatedFollower(out Animator animator)
        {
            var obj = Actor();
            animator = obj.AddComponent<Animator>();
            var view = obj.AddComponent<PathFollowerAnimatorView>();
            typeof(PathFollowerAnimatorView).GetField("_animator", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(view, animator);
            var follower = obj.AddComponent<PathFollower>();
            follower.Init();
            return follower;
        }

        private sealed class AheadAgent : IQueuedPathAgent
        {
            public AheadAgent(IPathProvider path) { QueueProvider = path; }
            public float Progress;
            public ulong PlaybackId => 1;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime => Progress;
            public int SnapshotRevision => QueueProvider.Revision;
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1.5f, true, false, false);
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) { }
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason) { }
        }
    }
}
