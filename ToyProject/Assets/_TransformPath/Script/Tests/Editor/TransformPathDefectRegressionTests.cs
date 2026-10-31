using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathDefectRegressionTests
    {
        private readonly List<Object> _objects = new List<Object>();
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            // Scheduled effects start at Time.frameCount outside a driver tick.
            _frame = Time.frameCount + 10;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null)
                    Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [TestCase(0.001f)]
        [TestCase(2f)]
        [TestCase(3f)]
        [TestCase(10f)]
        [TestCase(1000f)]
        public void RotationFollowsThePathAtAnyLength(float length)
        {
            PathData path = CreatePath(PathMovementSettings.Time(5f), Vector3.right * length);
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            Assert.That(Vector3.Dot(follower.transform.forward, Vector3.right), Is.GreaterThan(0.99f));

            follower.transform.rotation = Quaternion.identity;
            Assert.That(follower.AlignRotationToPathDirection(), Is.True);
            Assert.That(Vector3.Dot(follower.transform.forward, Vector3.right), Is.GreaterThan(0.99f));
        }

        [Test]
        public void RotationFollowsShortSequenceSegments()
        {
            PathData first = CreatePath(PathMovementSettings.Time(1f), Vector3.right * 2f);
            PathData second = CreatePath(PathMovementSettings.Time(1f), Vector3.forward * 2f);
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Sequence(CreateSequence(first, second)));
            Assert.That(Vector3.Dot(follower.transform.forward, Vector3.right), Is.GreaterThan(0.99f));
            follower.SeekSegment(1, 0.5f);
            Assert.That(Vector3.Dot(follower.transform.forward, Vector3.forward), Is.GreaterThan(0.99f));
        }

        [Test]
        public void RotationHoldsAtThePathEnd()
        {
            PathData path = CreatePath(PathMovementSettings.Speed(1f), Vector3.right * 2f);
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            follower.Seek(1f);
            Assert.That(Vector3.Dot(follower.transform.forward, Vector3.right), Is.GreaterThan(0.99f));
            Assert.That(follower.AlignRotationToPathDirection(), Is.False);
        }

        [Test]
        public void FreePlaybackPauseAndResumeClearTheQueueHold()
        {
            QueuedPathFollower actor = Actor();
            actor.StartFreePlayback(PathPlaybackRequest.Single(CreatePath(PathMovementSettings.Time(5f))));
            actor.PauseMove();
            Assert.That(actor.IsBlocked, Is.True);
            actor.ResumeMove();
            Assert.That(actor.IsBlocked, Is.False);
            Assert.That(actor.IsActuallyMoving, Is.True);
            Assert.That(actor.CurrentSpeedMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void UnderlyingStopWhilePausedClearsTheQueueHold()
        {
            QueuedPathFollower actor = Actor();
            actor.StartFreePlayback(PathPlaybackRequest.Single(CreatePath(PathMovementSettings.Time(5f))));
            actor.PauseMove();
            actor.UnderlyingFollower.StopMove();
            Assert.That(actor.IsBlocked, Is.False);
            Assert.That(actor.CurrentSpeedMultiplier, Is.EqualTo(1f));
        }

        // The second actor is behind (-1) or just past (+0.5) the paused first actor.
        [TestCase(-1f)]
        [TestCase(0.5f)]
        public void ResumingInsideTheSpacingWaitsInsteadOfEndingPlayback(float secondRelativeToFirst)
        {
            PathData path = CreatePath(PathMovementSettings.Speed(10f), Vector3.right * 100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower first = Actor(), second = Actor();
            PathRuntimeDriver driver = Driver(manager, first, second);
            first.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Run(driver, () => first.GlobalNormalizedTime >= 0.1f);
            first.PauseMove();
            second.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Run(driver, () => second.GlobalNormalizedTime * 100f
                >= first.GlobalNormalizedTime * 100f + secondRelativeToFirst);
            int invalidations = 0;
            first.QueueInvalidated += (_, __) => invalidations++;
            second.QueueInvalidated += (_, __) => invalidations++;

            first.ResumeMove();
            QueuedPathFollower rear = secondRelativeToFirst < 0f ? second : first;
            float held = rear.GlobalNormalizedTime;
            for (int i = 0; i < 3; i++)
                Step(driver);

            Assert.That(invalidations, Is.Zero);
            Assert.That(rear.IsBlocked, Is.True);
            Assert.That(rear.GlobalNormalizedTime, Is.EqualTo(held));
            RunBothToCompletion(driver, first, second);
        }

        [Test]
        public void RaisingTheSpacingWaitsInsteadOfEndingPlayback()
        {
            PathData path = CreatePath(PathMovementSettings.Speed(10f), Vector3.right * 100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower first = Actor(), second = Actor();
            PathRuntimeDriver driver = Driver(manager, first, second);
            StartWithGap(driver, manager, path, first, second);
            int invalidations = 0;
            second.QueueInvalidated += (_, __) => invalidations++;

            manager.DefaultSpacing = 8f;
            for (int i = 0; i < 3; i++)
                Step(driver);

            Assert.That(invalidations, Is.Zero);
            Assert.That(second.IsBlocked, Is.True);
            RunBothToCompletion(driver, first, second);
        }

        [Test]
        public void ShorteningTheRouteWaitsInsteadOfEndingPlayback()
        {
            PathData path = CreatePath(PathMovementSettings.Speed(10f), Vector3.right * 100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower first = Actor(), second = Actor();
            PathRuntimeDriver driver = Driver(manager, first, second);
            StartWithGap(driver, manager, path, first, second);
            int invalidations = 0;
            second.QueueInvalidated += (_, __) => invalidations++;

            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 20f });
            for (int i = 0; i < 3; i++)
                Step(driver);

            Assert.That(invalidations, Is.Zero);
            Assert.That(second.IsMoving, Is.True);
            RunBothToCompletion(driver, first, second);
        }

        [Test]
        public void ProgressPastTheRequestedCapStillEndsPlayback()
        {
            PathData path = CreatePath(PathMovementSettings.Speed(10f), Vector3.right * 100f);
            QueuedPathManager manager = CreateManager(path);
            QueuedPathFollower actor = Actor();
            PathRuntimeDriver driver = Driver(manager, actor);
            EPathQueueDetachReason? reason = null;
            actor.QueueInvalidated += (_, value) => reason = value;
            actor.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path), 0.5f);
            Step(driver);
            actor.Seek(0.75f);
            for (int i = 0; i < 3; i++)
                Step(driver);
            Assert.That(reason, Is.EqualTo(EPathQueueDetachReason.ConstraintViolation));
            Assert.That(actor.IsMoving, Is.False);
        }

        [Test]
        public void UnderlyingListenerFailureLeavesNoGhostInTheQueue()
        {
            PathData firstPath = CreatePath(PathMovementSettings.Speed(10f));
            PathData secondPath = CreatePath(PathMovementSettings.Speed(10f));
            MultiPathData sequence = CreateSequence(firstPath, secondPath);
            QueuedPathManager manager = CreateManager(sequence);
            QueuedPathFollower first = Actor(), second = Actor();
            PathRuntimeDriver driver = Driver(manager, first, second);
            first.StartQueuedPlayback(manager, PathPlaybackRequest.Sequence(sequence));
            first.UnderlyingFollower.SegmentChanged += _ => throw new InvalidOperationException("Listener failure");
            Run(driver, () => first.GlobalNormalizedTime >= 0.2f);
            second.StartQueuedPlayback(manager, PathPlaybackRequest.Sequence(sequence));
            Exception fault = null;
            Run(driver, () =>
            {
                fault ??= driver.LastException;
                return fault != null;
            });

            Assert.That(first.IsMoving, Is.False);
            Assert.That(first.IsRegistered, Is.False);
            Assert.That(first.IsBlocked, Is.False);
            Assert.That(manager.AgentCount, Is.EqualTo(1));
            Run(driver, () => second.State == EPathFollowerState.Completed);
        }

        [Test]
        public void StaleRegistrationIsNotTreatedAsTheActorAhead()
        {
            PathData path = CreatePath(PathMovementSettings.Speed(10f), Vector3.right * 100f);
            QueuedPathManager manager = CreateManager(path);
            var stale = new StaleAgent(path) { GlobalNormalizedTime = 0.5f };
            manager.Register(stale);
            stale.PlaybackId = 2;
            QueuedPathFollower actor = Actor();
            PathRuntimeDriver driver = Driver(manager, actor);
            actor.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Step(driver);
            Assert.That(manager.TryGetState(actor, out PathQueueState state), Is.True);
            Assert.That(state.Ahead, Is.Null);
            Assert.That(state.IsBlocked, Is.False);
        }

        [Test]
        public void AnimatorSpeedRecoversAfterListenerFailure()
        {
            GameObject actor = CreateObject("Actor");
            PathFollower follower = actor.AddComponent<PathFollower>();
            Animator animator = actor.AddComponent<Animator>();
            PathFollowerAnimatorView view = actor.AddComponent<PathFollowerAnimatorView>();
            SetField(view, "_animator", animator);
            follower.Init();
            PathData firstPath = CreatePath(PathMovementSettings.Speed(10f));
            PathData secondPath = CreatePath(PathMovementSettings.Speed(10f));
            follower.SegmentChanged += _ => throw new InvalidOperationException("Listener failure");
            follower.StartPlayback(PathPlaybackRequest.Sequence(CreateSequence(firstPath, secondPath)));

            Assert.Throws<InvalidOperationException>(() => follower.Tick(2f));

            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(animator.speed, Is.EqualTo(1f));
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public void MovementAdjustmentEndsAtASegmentOfAnotherMoveType(bool speedFirst, bool seek)
        {
            PathData firstPath = CreatePath(
                speedFirst ? PathMovementSettings.Speed(10f) : PathMovementSettings.Time(0.1f), Vector3.right);
            PathData secondPath = CreatePath(
                speedFirst ? PathMovementSettings.Time(5f) : PathMovementSettings.Speed(10f));
            PathFollower follower = CreateFollower();
            PathEventHandler handler = follower.GetComponent<PathEventHandler>();
            var driver = new PathRuntimeDriver();
            driver.Register(handler);
            driver.Register(follower);
            follower.StartPlayback(PathPlaybackRequest.Sequence(CreateSequence(firstPath, secondPath)));
            handler.HandleEvent(Adjustment(speedFirst), follower);
            Assert.That(handler.IsControllingSpeed, Is.True);

            if (seek)
                follower.SeekSegment(1, 0.1f);
            for (int i = 0; i < 40; i++)
            {
                Step(driver);
                Assert.That(driver.LastException, Is.Null);
            }

            Assert.That(follower.CurrentSegmentIndex, Is.EqualTo(1));
            Assert.That(follower.IsMoving, Is.True);
            Assert.That(handler.IsControllingSpeed, Is.False);
        }

        [Test]
        public void MovementAdjustmentContinuesAcrossSegmentsOfTheSameMoveType()
        {
            PathData firstPath = CreatePath(PathMovementSettings.Speed(10f), Vector3.right);
            PathData secondPath = CreatePath(PathMovementSettings.Speed(10f));
            PathFollower follower = CreateFollower();
            PathEventHandler handler = follower.GetComponent<PathEventHandler>();
            var driver = new PathRuntimeDriver();
            driver.Register(handler);
            driver.Register(follower);
            follower.StartPlayback(PathPlaybackRequest.Sequence(CreateSequence(firstPath, secondPath)));
            handler.HandleEvent(Adjustment(true), follower);
            for (int i = 0; i < 10; i++)
                Step(driver);
            Assert.That(follower.CurrentSegmentIndex, Is.EqualTo(1));
            Assert.That(handler.IsControllingSpeed, Is.True);
        }

        [Test]
        public void ZeroTimeScaleEventCannotWaitForDelayedEvents()
        {
            PathFollower follower = CreateFollower();
            follower.StartPlayback(PathPlaybackRequest.Single(CreatePath(PathMovementSettings.Time(5f))));
            var delayed = new PathEventDefinition("", false, 0f, 0f, null, false, 0f, 0f, null, false, 0f, 0f, null);
            AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            var definition = new PathEventDefinition("", false, 0f, 0f, null, false, 0f, 0f, null,
                true, 0f, 0.1f, curve, new[] { new PathDelayedEventDefinition(1f, delayed) });
            ArgumentException exception = Assert.Throws<ArgumentException>(() =>
                follower.GetComponent<PathEventHandler>().HandleEvent(definition, follower));
            Assert.That(exception.Message, Does.Contain("time scale"));
        }

        private static PathEventDefinition Adjustment(bool speed)
        {
            AnimationCurve curve = AnimationCurve.Linear(0f, 0f, 1f, 1f);
            return new PathEventDefinition("", speed, 20f, 100f, curve, !speed, 2f, 100f, curve, false, 0f, 0f, null);
        }

        private void StartWithGap(PathRuntimeDriver driver, QueuedPathManager manager, PathData path,
            QueuedPathFollower first, QueuedPathFollower second)
        {
            first.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Run(driver, () => first.GlobalNormalizedTime >= 0.05f);
            second.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            for (int i = 0; i < 3; i++)
                Step(driver);
        }

        private void RunBothToCompletion(PathRuntimeDriver driver, QueuedPathFollower first, QueuedPathFollower second)
        {
            Run(driver, () => first.State == EPathFollowerState.Completed
                && second.State == EPathFollowerState.Completed);
            Assert.That(driver.LastException, Is.Null);
        }

        private PathRuntimeDriver Driver(QueuedPathManager manager, params QueuedPathFollower[] actors)
        {
            var driver = new PathRuntimeDriver();
            driver.Register(manager);
            foreach (QueuedPathFollower actor in actors)
            {
                driver.Register(actor);
                driver.Register(actor.UnderlyingFollower);
            }
            return driver;
        }

        private void Run(PathRuntimeDriver driver, Func<bool> until)
        {
            for (int i = 0; i < 5000 && !until(); i++)
                Step(driver);
            Assert.That(until(), Is.True, "The expected state was not reached.");
        }

        private void Step(PathRuntimeDriver driver)
        {
            driver.Tick(0.02f, 0.02f, ++_frame);
        }

        private QueuedPathFollower Actor()
        {
            GameObject actor = CreateObject("Queue Actor");
            actor.AddComponent<PathFollower>();
            QueuedPathFollower queued = actor.AddComponent<QueuedPathFollower>();
            SetField(queued, "_resumeMovementDelay", 0f);
            SetField(queued, "_speedSmoothRate", 0f);
            SetField(queued, "_unblockHysteresis", 0f);
            return queued;
        }

        private GameObject CreateObject(string name)
        {
            var result = new GameObject(name);
            _objects.Add(result);
            return result;
        }

        private PathData CreatePath(PathMovementSettings movement, Vector3? end = null)
        {
            PathData path = CreateObject("Path").AddComponent<PathData>();
            path.ConfigureMovementSettings(movement);
            path.ConfigureControlPoints(new[] { Vector3.zero, end ?? Vector3.right * 10f });
            path.Init();
            Assert.That(path.IsReady, Is.True);
            return path;
        }

        private MultiPathData CreateSequence(params PathData[] paths)
        {
            MultiPathData sequence = CreateObject("Sequence").AddComponent<MultiPathData>();
            var segments = new PathSegmentDescriptor[paths.Length];
            for (int i = 0; i < paths.Length; i++)
                segments[i] = new PathSegmentDescriptor(paths[i], paths[i].MovementSettings, false);
            sequence.ConfigureSegments(segments);
            return sequence;
        }

        private PathFollower CreateFollower()
        {
            PathFollower follower = CreateObject("Actor").AddComponent<PathFollower>();
            follower.Init();
            return follower;
        }

        private QueuedPathManager CreateManager(IPathProvider route)
        {
            QueuedPathManager manager = CreateObject("Manager").AddComponent<QueuedPathManager>();
            manager.Init();
            manager.ConfigureRoute(route);
            return manager;
        }

        private static void SetField(object target, string name, object value)
        {
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
        }

        private sealed class StaleAgent : IQueuedPathAgent
        {
            public StaleAgent(IPathProvider provider) => QueueProvider = provider;
            public ulong PlaybackId { get; set; } = 1;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime { get; set; }
            public int SnapshotRevision => QueueProvider.Revision;
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1.5f, true, true, true);
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) { }
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason) { }
        }
    }
}
