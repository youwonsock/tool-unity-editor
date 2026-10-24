using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathReentrancyTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NestedReleaseNotifiesEveryAgentEvenWhenFirstCallbackThrows(bool throwAfterRelease)
        {
            PathData path = CreatePath();
            QueuedPathManager manager = CreateManager(path);
            var first = new Agent(path);
            var second = new Agent(path);
            PathQueueRegistration firstToken = manager.Register(first);
            PathQueueRegistration secondToken = manager.Register(second);
            first.Detach = () =>
            {
                manager.Release();
                if (throwAfterRelease) throw new InvalidOperationException("detach failure");
            };

            if (throwAfterRelease)
                Assert.That(Assert.Throws<InvalidOperationException>(() => manager.Release()).Message,
                    Is.EqualTo("detach failure"));
            else
                Assert.DoesNotThrow(() => manager.Release());
            Assert.That(first.DetachCount, Is.EqualTo(1));
            Assert.That(second.DetachCount, Is.EqualTo(1));
            Assert.That(manager.IsInitialized, Is.False);
            Assert.That(manager.AgentCount, Is.Zero);
            Assert.That(manager.Unregister(firstToken), Is.False);
            Assert.That(manager.TryGetState(secondToken, out _), Is.False);

            first.Detach = null;
            manager.Init();
            manager.ConfigureRoute(path);
            PathQueueRegistration next = manager.Register(first);
            Assert.That(next.RegistrationId, Is.GreaterThan(secondToken.RegistrationId));
            Assert.That(manager.TryGetState(next, out _), Is.False);
            ((IPathRuntimeTickable)manager).Tick(0.1f, 0.1f, 10);
            Assert.That(manager.TryGetState(next, out _), Is.True);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReplacementRevalidatesManagerAndRouteAfterDetach(bool changeRoute)
        {
            PathData path = CreatePath();
            PathData other = CreatePath();
            QueuedPathManager manager = CreateManager(path);
            var agent = new Agent(path);
            PathQueueRegistration old = manager.Register(agent);
            agent.PlaybackId++;
            agent.Detach = () =>
            {
                if (changeRoute) manager.ConfigureRoute(other);
                else manager.Release();
            };

            Assert.Throws<InvalidOperationException>(() => manager.Register(agent));
            Assert.That(manager.AgentCount, Is.Zero);
            Assert.That(manager.IsInitialized, Is.EqualTo(changeRoute));
            Assert.That(manager.RouteProvider, Is.SameAs(changeRoute ? other : null));
            Assert.That(manager.TryGetState(agent, out _), Is.False);
            Assert.That(manager.Unregister(old), Is.False);
        }

        [Test]
        public void ReplacementKeepsAValidRegistrationCreatedByDetachCallback()
        {
            PathData path = CreatePath();
            QueuedPathManager manager = CreateManager(path);
            var agent = new Agent(path);
            PathQueueRegistration old = manager.Register(agent);
            agent.PlaybackId++;
            PathQueueRegistration replacement = default;
            agent.Detach = () => replacement = manager.Register(agent);
            PathQueueRegistration result = manager.Register(agent);
            Assert.That(result.Matches(replacement), Is.True);
            Assert.That(result.PlaybackId, Is.EqualTo(agent.PlaybackId));
            Assert.That(manager.AgentCount, Is.EqualTo(1));
            Assert.That(manager.Unregister(old), Is.False);
            agent.Detach = null;
        }

        [Test]
        public void RouteChangeDoesNotOverwriteReleaseFromDetachCallback()
        {
            PathData path = CreatePath();
            QueuedPathManager manager = CreateManager(path);
            var first = new Agent(path);
            var second = new Agent(path);
            manager.Register(first);
            manager.Register(second);
            first.Detach = () => manager.Release();
            Assert.DoesNotThrow(() => manager.ConfigureRoute(CreatePath()));
            Assert.That(second.DetachCount, Is.EqualTo(1));
            Assert.That(manager.RouteProvider, Is.Null);
            Assert.That(manager.IsInitialized, Is.False);
        }

        [TestCase("Single")]
        [TestCase("Aggregate")]
        [TestCase("Sequence")]
        public void CompletionReleaseStopsTickBeforeAccessingSession(string kind)
        {
            PathData path = CreatePath();
            var follower = Create("Follower").AddComponent<PathFollower>();
            follower.Init();
            int completed = 0;
            follower.Completed += () => { completed++; follower.Release(); };
            PathPlaybackRequest request = PathPlaybackRequest.Single(path);
            if (kind == "Aggregate")
                request = PathPlaybackRequest.Aggregate(path, PathMovementSettings.Time(1f));
            else if (kind == "Sequence")
            {
                var sequence = Create("Sequence").AddComponent<MultiPathData>();
                sequence.ConfigureSegments(new[] { new PathSegmentDescriptor(path, path.MovementSettings, false) });
                request = PathPlaybackRequest.Sequence(sequence);
            }
            follower.StartPlayback(request);
            Assert.DoesNotThrow(() => follower.Tick(2f));
            Assert.That(completed, Is.EqualTo(1));
            Assert.That(follower.IsInitialized, Is.False);
            Assert.That(follower.State, Is.EqualTo(EPathFollowerState.Uninitialized));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CompletionReplacementStopsOldQueueNotification(bool queuedReplacement)
        {
            PathData path = CreatePath();
            QueuedPathManager manager = CreateManager(path);
            var queue = Create("Queued follower").AddComponent<QueuedPathFollower>();
            queue.Init();
            int publicCompleted = 0;
            int queueCompleted = 0;
            queue.Completed += () =>
            {
                publicCompleted++;
                if (queuedReplacement) queue.StartQueuedPlayback(manager, PathPlaybackRequest.Single(path));
                else queue.StartFreePlayback(PathPlaybackRequest.Single(path));
            };
            queue.OnCompleted += _ => queueCompleted++;
            queue.StartFreePlayback(PathPlaybackRequest.Single(path));
            ulong oldId = queue.PlaybackId;
            queue.UnderlyingFollower.Tick(2f);
            Assert.That(publicCompleted, Is.EqualTo(1));
            Assert.That(queueCompleted, Is.Zero);
            Assert.That(queue.PlaybackId, Is.GreaterThan(oldId));
            Assert.That(queue.IsMoving, Is.True);
            Assert.That(queue.IsRegistered, Is.EqualTo(queuedReplacement));
        }

        [Test]
        public void NormalCompletionDeliversBothPublicNotificationsOnce()
        {
            var queue = Create("Queued follower").AddComponent<QueuedPathFollower>();
            queue.Init();
            var calls = new List<string>();
            queue.Completed += () => calls.Add("Completed");
            queue.OnCompleted += _ => calls.Add("OnCompleted");
            queue.StartFreePlayback(PathPlaybackRequest.Single(CreatePath()));
            queue.UnderlyingFollower.Tick(2f);
            queue.UnderlyingFollower.Tick(2f);
            Assert.That(calls, Is.EqualTo(new[] { "Completed", "OnCompleted" }));
        }

        private sealed class Agent : IQueuedPathAgent
        {
            public Agent(IPathProvider provider) { QueueProvider = provider; }
            public ulong PlaybackId { get; set; } = 1;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime => 0.5f;
            public int SnapshotRevision => QueueProvider.Revision;
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1f, true, true, true);
            public Action Detach;
            public int DetachCount;
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) { }
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason)
            {
                DetachCount++;
                Detach?.Invoke();
            }
        }

        private GameObject Create(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        private PathData CreatePath()
        {
            var path = Create("Path").AddComponent<PathData>();
            path.ConfigureMovementSettings(PathMovementSettings.Time(1f));
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 10f });
            path.Init();
            return path;
        }

        private QueuedPathManager CreateManager(PathData path)
        {
            var manager = Create("Manager").AddComponent<QueuedPathManager>();
            manager.Init();
            manager.ConfigureRoute(path);
            return manager;
        }
    }
}
