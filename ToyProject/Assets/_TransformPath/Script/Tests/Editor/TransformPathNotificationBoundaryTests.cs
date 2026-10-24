using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathNotificationBoundaryTests
    {
        private readonly List<GameObject> objects = new List<GameObject>();
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
        public void StartingValueAdjustmentStillAppliesTheInitialPose(string kind, bool adjust)
        {
            PathData path = Path();
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            follower.transform.position = new Vector3(-9f, 2f, 4f);
            if (adjust)
                follower.StateChanged += state => { if (state == EPathFollowerState.Moving) follower.SetDuration(7f); };
            PathPlaybackRequest request;
            if (kind == "Single") request = PathPlaybackRequest.Single(path);
            else if (kind == "Aggregate") request = PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(5f));
            else
            {
                var route = Actor().AddComponent<MultiPathData>();
                route.ConfigureSegments(new[] { new PathSegmentDescriptor(path, path.MovementSettings, false) });
                request = PathPlaybackRequest.Sequence(route);
            }
            follower.StartPlayback(request);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(follower.NormalizedTime, Is.Zero);
            Assert.That(Vector3.Distance(follower.transform.position, path.Sample(0f)), Is.LessThan(0.0001f),
                $"Start returned with progress 0 but actor pose {follower.transform.position} instead of the route start");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResumedNotificationDoesNotContinueAfterTheFirstListenerBlocksAgain(bool blockAgain)
        {
            PathData path = Path();
            var manager = Actor().AddComponent<QueuedPathManager>(); manager.Init(); manager.ConfigureRoute(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            int secondCalls = 0; bool secondSawBlocked = false;
            queue.OnResumed += q => { if (blockAgain) q.ForceBlock(); };
            queue.OnResumed += q => { secondCalls++; secondSawBlocked = q.IsBlocked; };
            ((IPathRuntimeTickable)manager).Tick(0.2f, 0.2f, 1);
            ((IPathRuntimeTickable)queue).Tick(0.2f, 0.2f, 1);
            Assert.That(secondCalls, Is.EqualTo(blockAgain ? 0 : 1),
                $"Later OnResumed listener ran while blocked={secondSawBlocked}; source state revision did not change");
        }

        private GameObject Actor() { var obj = new GameObject("Notification boundary regression"); objects.Add(obj); return obj; }

        [TestCase("seek")]
        [TestCase("replace")]
        [TestCase("pause")]
        [TestCase("stop")]
        [TestCase("rotation")]
        public void StartingCallbackCommandsRetainTheirPoseAndLifecycle(string command)
        {
            PathData path = Path(); PathData other = Path();
            other.ConfigureControlPoints(new[] { Vector3.right * 100f, Vector3.right * 110f });
            var follower = Actor().AddComponent<PathFollower>(); follower.Init();
            Vector3 previous = new Vector3(-9f, 2f, 4f); follower.transform.position = previous;
            follower.transform.rotation = Quaternion.identity;
            follower.StateChanged += state =>
            {
                if (state != EPathFollowerState.Moving) return;
                if (command == "seek") follower.Seek(0.3f);
                else if (command == "replace") follower.StartPlayback(PathPlaybackRequest.Single(other));
                else if (command == "pause") follower.PauseMove();
                else if (command == "stop") follower.StopMove();
                else follower.SetRotateToPathDirection(false);
            };
            PathCommandReceipt receipt = follower.StartPlayback(PathPlaybackRequest.Single(path));
            Vector3 expected = command == "seek" ? path.Sample(0.3f)
                : command == "replace" ? other.Sample(0f)
                : command == "rotation" ? path.Sample(0f) : previous;
            Assert.That(Vector3.Distance(follower.transform.position, expected), Is.LessThan(0.0001f));
            Assert.That(follower.State, Is.EqualTo(command == "pause" ? EPathFollowerState.Paused
                : command == "stop" ? EPathFollowerState.Ready : EPathFollowerState.Moving));
            if (command == "replace") Assert.That(follower.PlaybackId, Is.Not.EqualTo(receipt.PlaybackId));
            if (command == "rotation") Assert.That(Quaternion.Angle(follower.transform.rotation, Quaternion.identity), Is.LessThan(0.001f));
        }

        [Test]
        public void SpeedChangeFromMovingCallbackStillAppliesInitialPosition()
        {
            PathData path = Path(); path.ConfigureMovementSettings(PathMovementSettings.Speed(2f));
            var follower = Actor().AddComponent<PathFollower>(); follower.Init(); follower.transform.position = Vector3.left * 20f;
            follower.StateChanged += state => { if (state == EPathFollowerState.Moving) follower.SetSpeed(3f); };
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            Assert.That(follower.Speed, Is.EqualTo(3f));
            Assert.That(Vector3.Distance(follower.transform.position, path.Sample(0f)), Is.LessThan(0.0001f));
        }

        [TestCase("block")]
        [TestCase("detach")]
        [TestCase("state")]
        [TestCase("restart")]
        [TestCase("observe")]
        public void QueueCommandsEndTheOldResumedBatch(string command)
        {
            QueuedPathFollower queue = Queue(out QueuedPathManager manager, out PathData path);
            PathQueueRegistration token = manager.Register(queue);
            int later = 0; ulong sourceRevision = queue.StateRevision;
            queue.OnResumed += q =>
            {
                if (command == "block") q.ForceBlock();
                else if (command == "detach") q.DetachQueue();
                else if (command == "state") q.ApplyQueueState(token,
                    new PathQueueState(null, null, false, 1f, 0.7f, path.Revision, q.PlaybackId, 2));
                else if (command == "restart") q.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            };
            queue.OnResumed += _ => later++;
            Tick(manager, queue, 1);
            Assert.That(later, Is.EqualTo(command == "observe" ? 1 : 0));
            if (command != "restart") Assert.That(queue.StateRevision, Is.EqualTo(sourceRevision), "Queue notification cancellation must not change public playback StateRevision");
            if (command == "detach") { Assert.That(queue.IsRegistered, Is.False); Assert.That(queue.IsMoving, Is.True); }
            if (command == "restart") Assert.That(manager.AgentCount, Is.EqualTo(1));
        }

        [Test]
        public void ReblockedResumedBatchSkipsOldSpeedNotificationButNextFramesNotify()
        {
            QueuedPathFollower queue = Queue(out QueuedPathManager manager, out _);
            typeof(QueuedPathFollower).GetField("_speedSmoothRate", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(queue, 1f);
            bool once = true; int later = 0; var speeds = new List<float>();
            queue.OnResumed += q => { if (once) { once = false; q.ForceBlock(); } };
            queue.OnResumed += _ => later++;
            queue.OnSpeedChanged += (_, speed) => speeds.Add(speed);
            Tick(manager, queue, 1);
            Assert.That(later, Is.Zero); Assert.That(speeds, Is.Empty); Assert.That(queue.IsBlocked, Is.True);
            Tick(manager, queue, 2);
            CollectionAssert.AreEqual(new[] { 0f }, speeds);
            queue.ForceUnblock(); Tick(manager, queue, 3);
            Assert.That(later, Is.EqualTo(1)); Assert.That(queue.IsBlocked, Is.False);
            Assert.That(speeds.Count, Is.EqualTo(2)); Assert.That(speeds[1], Is.EqualTo(0.2f).Within(0.0001f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ManualHoldFromBlockedListenerStopsOnlyThatBatch(bool hold)
        {
            QueuedPathFollower queue = Queue(out QueuedPathManager manager, out PathData path);
            queue.EnableOvertakeProtection = false; Tick(manager, queue, 1);
            int later = 0;
            queue.OnBlocked += q => { if (hold) q.ForceBlock(); };
            queue.OnBlocked += _ => later++;
            manager.Register(new AheadAgent(path)); Tick(manager, queue, 2);
            Assert.That(queue.IsBlocked, Is.True);
            Assert.That(later, Is.EqualTo(hold ? 0 : 1));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ForceBlockFromSpeedListenerStopsTheRemainingSpeedListeners(bool block)
        {
            QueuedPathFollower queue = Queue(out QueuedPathManager manager, out _);
            typeof(QueuedPathFollower).GetField("_speedSmoothRate", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(queue, 1f);
            int later = 0; float first = 0f;
            queue.OnSpeedChanged += (q, speed) => { first = speed; if (block) q.ForceBlock(); };
            queue.OnSpeedChanged += (_, __) => later++;
            Tick(manager, queue, 1);
            Assert.That(first, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(later, Is.EqualTo(block ? 0 : 1));
        }

        [Test]
        public void ForceUnblockFromSpeedListenerEndsOldBatchAndAllowsNextResume()
        {
            QueuedPathFollower queue = Queue(out QueuedPathManager manager, out _); queue.ForceBlock();
            bool once = true; int later = 0; float laterSpeed = -1f;
            queue.OnSpeedChanged += (q, _) => { if (once) { once = false; q.ForceUnblock(); } };
            queue.OnSpeedChanged += (_, speed) => { later++; laterSpeed = speed; };
            Tick(manager, queue, 1); Assert.That(later, Is.Zero);
            Tick(manager, queue, 2); Assert.That(later, Is.EqualTo(1)); Assert.That(laterSpeed, Is.EqualTo(1f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void QueueCommandFromCompletedStopsOldCompletionGroups(bool block)
        {
            QueuedPathFollower queue = Queue(out QueuedPathManager manager, out _);
            int later = 0; int queueCompleted = 0;
            queue.Completed += () => { if (block) queue.ForceBlock(); };
            queue.Completed += () => later++;
            queue.OnCompleted += _ => queueCompleted++;
            Tick(manager, queue, 1); queue.UnderlyingFollower.Tick(5f);
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Completed));
            Assert.That(later, Is.EqualTo(block ? 0 : 1)); Assert.That(queueCompleted, Is.EqualTo(block ? 0 : 1));
        }

        [TestCase("listener")]
        [TestCase("ready")]
        [TestCase("observe")]
        public void QueueCommandStopsOldInvalidationNotifications(string command)
        {
            QueuedPathFollower queue = Queue(out QueuedPathManager manager, out _);
            int later = 0;
            if (command == "ready") queue.StateChanged += state => { if (state == EPathFollowerState.Ready) queue.ForceBlock(); };
            queue.QueueInvalidated += (q, _) => { if (command == "listener") q.ForceBlock(); };
            queue.QueueInvalidated += (_, __) => later++;
            manager.Release();
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Ready));
            Assert.That(later, Is.EqualTo(command == "observe" ? 1 : 0));
        }

        private QueuedPathFollower Queue(out QueuedPathManager manager, out PathData path)
        {
            path = Path(); manager = Actor().AddComponent<QueuedPathManager>(); manager.Init(); manager.ConfigureRoute(path);
            var queue = Actor().AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path)); return queue;
        }

        [Test]
        public void ReplacedReleaseKeepsTheNewPlaybackAndDetachKeepsFreeMovement()
        {
            QueuedPathFollower queue = Queue(out QueuedPathManager manager, out PathData path);
            ulong first = queue.PlaybackId;
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            Assert.That(queue.PlaybackId, Is.Not.EqualTo(first)); Assert.That(manager.AgentCount, Is.EqualTo(1));
            queue.StartFreePlayback(PathPlaybackRequest.Single(path));
            Assert.That(queue.IsMoving, Is.True); Assert.That(queue.IsRegistered, Is.False); Assert.That(manager.AgentCount, Is.Zero);
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path)); ulong queued = queue.PlaybackId;
            queue.DetachQueue();
            Assert.That(queue.PlaybackId, Is.EqualTo(queued)); Assert.That(queue.IsMoving, Is.True);
            Assert.That(queue.IsRegistered, Is.False); Assert.That(manager.AgentCount, Is.Zero);
        }

        [Test]
        public void LoopStillReRegistersWithoutStoppingThePlayback()
        {
            QueuedPathFollower queue = Queue(out QueuedPathManager manager, out PathData path);
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path, true));
            ulong id = queue.PlaybackId; PathQueueRegistration old = manager.Register(queue);
            Tick(manager, queue, 1); queue.UnderlyingFollower.Tick(5f);
            Assert.That(queue.PlaybackId, Is.EqualTo(id)); Assert.That(queue.IsMoving, Is.True);
            Assert.That(queue.IsRegistered, Is.True); Assert.That(manager.AgentCount, Is.EqualTo(1));
            Assert.That(manager.Register(queue).RegistrationId, Is.Not.EqualTo(old.RegistrationId));
            Tick(manager, queue, 2); queue.UnderlyingFollower.Tick(0.1f);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
        }

        private void Tick(QueuedPathManager manager, QueuedPathFollower queue, int frame)
        {
            ((IPathRuntimeTickable)manager).Tick(0.2f, 0.2f, frame);
            ((IPathRuntimeTickable)queue).Tick(0.2f, 0.2f, frame);
        }

        private sealed class AheadAgent : IQueuedPathAgent
        {
            public AheadAgent(IPathProvider provider) { QueueProvider = provider; }
            public ulong PlaybackId => 1;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime => 0.1f;
            public int SnapshotRevision => QueueProvider.Revision;
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1.5f, true, true, false);
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) { }
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason) { }
        }
        private PathData Path()
        {
            var path = Actor().AddComponent<PathData>();
            path.ConfigureBuildSettings(new PathBuildSettings(EPathCurveType.Linear, 10));
            path.ConfigureMovementSettings(PathMovementSettings.Time(5f));
            path.ConfigureControlPoints(new[] { Vector3.right * 10f, Vector3.right * 20f }); path.Init(); return path;
        }
    }
}
