using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathLifecycleCleanupTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [TestCase("Same", false, false)]
        [TestCase("Other", false, false)]
        [TestCase("Free", false, false)]
        [TestCase("Same", true, false)]
        [TestCase("Other", true, false)]
        [TestCase("Free", true, false)]
        [TestCase("Same", false, true)]
        [TestCase("Other", false, true)]
        [TestCase("Free", false, true)]
        public void StopOrReleaseCallbackPreservesReplacement(string destination, bool pause, bool release)
        {
            PathData path = CreatePath();
            QueuedPathManager original = CreateManager(path);
            QueuedPathManager next = destination == "Other" ? CreateManager(path) : original;
            GameObject actor = Create("Actor");
            CharacterController controller = actor.AddComponent<CharacterController>();
            QueuedPathFollower queue = actor.AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(original, PathPlaybackRequest.Single(path));
            PathQueueRegistration oldToken = original.Register(queue);
            ulong oldId = queue.PlaybackId;
            if (pause) queue.PauseMove();
            bool replaced = false;
            ulong replacementId = 0;
            Action<EPathFollowerState> replace = state =>
            {
                if (state != EPathFollowerState.Ready || replaced) return;
                replaced = true;
                replacementId = (destination == "Free"
                    ? queue.StartFreePlayback(PathPlaybackRequest.Single(path))
                    : queue.StartQueuedPlayback(next, PathPlaybackRequest.Single(path))).PlaybackId;
            };
            queue.StateChanged += replace;
            PathCommandReceipt stopped = release ? queue.Release() : queue.StopMove();
            queue.StateChanged -= replace;
            Assert.That(replaced, Is.True);
            Assert.That(stopped.Changed, Is.True);
            Assert.That(stopped.PlaybackId, Is.Zero, "Receipt describes the stopped execution.");
            Assert.That(replacementId, Is.GreaterThan(oldId));
            Assert.That(queue.PlaybackId, Is.EqualTo(replacementId));
            Assert.That(queue.State, Is.EqualTo(EPathFollowerState.Moving));
            Assert.That(queue.IsInitialized, Is.True);
            Assert.That(queue.IsRegistered, Is.EqualTo(destination != "Free"));
            Assert.That(queue.ActiveManager, Is.EqualTo(destination == "Free" ? null : next));
            Assert.That(original.AgentCount, Is.EqualTo(destination == "Same" ? 1 : 0));
            Assert.That(next.AgentCount, Is.EqualTo(destination == "Free" ? 0 : 1));
            Assert.That(controller.enabled, Is.False);
            Assert.That(original.Unregister(oldToken), Is.False);
            queue.OnQueueDetached(oldToken, EPathQueueDetachReason.Stopped);
            queue.UnderlyingFollower.ReleaseQueueConstraint(oldToken);
            Tick(next, queue, 100);
            Assert.That(queue.GlobalNormalizedTime, Is.GreaterThan(0f));
            queue.StopMove();
            Assert.That(original.AgentCount, Is.Zero);
            Assert.That(next.AgentCount, Is.Zero);
            Assert.That(controller.enabled, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OrdinaryStopClearsRegistrationAndHoldOnce(bool pause)
        {
            PathData path = CreatePath();
            QueuedPathManager manager = CreateManager(path);
            var queue = Create("Actor").AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            queue.ForceBlock();
            if (pause) queue.PauseMove();
            int ready = 0;
            queue.StateChanged += state => { if (state == EPathFollowerState.Ready) ready++; };
            Assert.That(queue.StopMove().Changed, Is.True);
            Assert.That(queue.StopMove().Changed, Is.False);
            Assert.That(ready, Is.EqualTo(1));
            Assert.That(manager.AgentCount, Is.Zero);
            Assert.That(queue.ActiveManager, Is.Null);
            Assert.That(queue.IsBlocked, Is.False);
            Assert.That(queue.CurrentSpeedMultiplier, Is.EqualTo(1f));
        }

        [Test]
        public void ReleasedFacadeStopPreservesExternallyRestartedPlayback()
        {
            PathData path = CreatePath();
            var queue = Create("Actor").AddComponent<QueuedPathFollower>(); queue.Init(); queue.Release();
            PathFollower follower = queue.UnderlyingFollower; follower.Init();
            follower.StartPlayback(PathPlaybackRequest.Single(path));
            bool replaced = false;
            follower.StateChanged += state =>
            {
                if (state != EPathFollowerState.Ready || replaced) return;
                replaced = true;
                follower.StartPlayback(PathPlaybackRequest.Single(path));
            };
            queue.StopMove();
            Assert.That(replaced, Is.True);
            Assert.That(follower.IsMoving, Is.True);
            follower.Tick(0.2f);
            Assert.That(follower.NormalizedTime, Is.GreaterThan(0f));
        }

        [Test]
        public void ForeignOwnerCannotReleaseOrApplyCurrentConstraint()
        {
            PathData path = CreatePath();
            QueuedPathManager manager = CreateManager(path);
            var queue = Create("Actor").AddComponent<QueuedPathFollower>(); queue.Init();
            queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
            PathQueueRegistration current = manager.Register(queue);
            var foreign = new PathQueueRegistration(new object(), current.RegistrationId,
                current.PlaybackId, current.RouteRevision);
            queue.OnQueueDetached(foreign, EPathQueueDetachReason.Stopped);
            queue.UnderlyingFollower.ReleaseQueueConstraint(foreign);
            queue.UnderlyingFollower.Tick(0.2f);
            Assert.That(queue.NormalizedTime, Is.Zero, "Current initial queue hold must survive a foreign release.");
            Tick(manager, queue, 100);
            float progress = queue.NormalizedTime;
            Assert.That(progress, Is.GreaterThan(0f));
            var blocked = new PathQueueState(null, null, true, 0f, 0f,
                current.RouteRevision, current.PlaybackId, 101);
            queue.ApplyQueueState(foreign, blocked);
            queue.UnderlyingFollower.ApplyQueueConstraint(foreign,
                new PathQueueConstraint(foreign, true, 0f, 0f, 101, current.RouteRevision));
            queue.UnderlyingFollower.Tick(0.2f);
            Assert.That(queue.NormalizedTime, Is.GreaterThan(progress));
            Assert.That(queue.IsRegistered, Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PausedSettingsChangeKeepsSingleAndAggregatePolicies(bool aggregate)
        {
            PathData path = CreatePath();
            var follower = Create("Actor").AddComponent<PathFollower>(); follower.Init();
            PathPlaybackRequest request = aggregate
                ? PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(8f))
                : PathPlaybackRequest.Single(path);
            follower.StartPlayback(request); follower.Tick(0.2f); follower.PauseMove();
            ulong id = follower.PlaybackId;
            Vector3 position = follower.transform.position;
            path.ConfigureMovementSettings(PathMovementSettings.Speed(7f));
            follower.Tick(1f);
            Assert.That(follower.PlaybackId, Is.EqualTo(id));
            Assert.That(follower.transform.position, Is.EqualTo(position));
            follower.ResumeMove(); follower.Tick(0f);
            Assert.That(follower.PlaybackId, Is.EqualTo(aggregate ? id : 0UL));
            Assert.That(follower.State, Is.EqualTo(aggregate ? EPathFollowerState.Moving : EPathFollowerState.Ready));
            if (aggregate) Assert.That(follower.Duration, Is.EqualTo(8f));
        }

        private static void Tick(QueuedPathManager manager, QueuedPathFollower queue, int frame)
        {
            ((IPathRuntimeTickable)manager).Tick(0.2f, 0.2f, frame);
            ((IPathRuntimeTickable)queue).Tick(0.2f, 0.2f, frame);
            queue.UnderlyingFollower.Tick(0.2f);
        }

        private GameObject Create(string name)
        {
            var go = new GameObject(name); _objects.Add(go); return go;
        }

        private PathData CreatePath()
        {
            var path = Create("Path").AddComponent<PathData>();
            path.ConfigureMovementSettings(PathMovementSettings.Time(5f));
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 10f }); path.Init();
            return path;
        }

        private QueuedPathManager CreateManager(PathData path)
        {
            var manager = Create("Manager").AddComponent<QueuedPathManager>();
            manager.Init(); manager.ConfigureRoute(path); return manager;
        }
    }
}
