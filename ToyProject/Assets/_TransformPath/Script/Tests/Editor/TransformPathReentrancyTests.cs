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
