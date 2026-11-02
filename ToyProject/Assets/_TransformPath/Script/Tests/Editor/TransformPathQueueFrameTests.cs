using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Common.TransformPath.Tests
{
    public sealed class TransformPathQueueFrameTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [Test]
        public void StateQueriesFollowRegistrationLifetimeAndEntryReuse()
        {
            PathData path = CreatePath();
            QueuedPathManager manager = CreateManager(path);
            var first = new Agent(path, 0.8f);
            PathQueueRegistration oldToken = manager.Register(first);
            Assert.That(manager.TryGetState(first, out _), Is.False);
            Assert.That(manager.TryGetState(oldToken, out _), Is.False);
            first.OnApply = () =>
            {
                Assert.That(manager.TryGetState(first, out PathQueueState state), Is.True);
                Assert.That(state.FrameId, Is.EqualTo(100));
                Assert.That(manager.TryGetState(oldToken, out _), Is.True);
            };
            Tick(manager, 100);
            manager.Register(first);
            Assert.That(manager.TryGetState(first, out _), Is.True, "Duplicate registration keeps its last result.");
            manager.Unregister(oldToken);
            Assert.That(manager.TryGetState(first, out _), Is.False);
            var next = new Agent(path, 0.4f);
            PathQueueRegistration nextToken = manager.Register(next);
            Assert.That(manager.TryGetState(next, out _), Is.False, "A pooled entry must not expose the previous agent's state.");
            Assert.That(manager.TryGetState(nextToken, out _), Is.False);
            Tick(manager, 101);
            var foreign = new PathQueueRegistration(new object(), nextToken.RegistrationId,
                nextToken.PlaybackId, nextToken.RouteRevision);
            Assert.That(manager.TryGetState(foreign, out _), Is.False);
            Assert.That(manager.TryGetState(oldToken, out _), Is.False);
            Assert.That(manager.TryGetState(nextToken, out PathQueueState current), Is.True);
            Assert.That(current.FrameId, Is.EqualTo(101));
            manager.Release();
            Assert.That(manager.TryGetState(next, out _), Is.False);
            Assert.That(manager.TryGetState(nextToken, out _), Is.False);
        }

        [Test]
        public void SelfDetachDoesNotRestorePublishedStateAfterCallback()
        {
            PathData path = CreatePath();
            QueuedPathManager manager = CreateManager(path);
            var agent = new Agent(path, 0.5f);
            PathQueueRegistration token = manager.Register(agent);
            agent.OnApply = () => manager.Unregister(token);
            Tick(manager, 100);
            Assert.That(agent.ApplyCount, Is.EqualTo(1));
            Assert.That(manager.AgentCount, Is.Zero);
            Assert.That(manager.TryGetState(agent, out _), Is.False);
            Tick(manager, 101);
            Assert.That(agent.ApplyCount, Is.EqualTo(1));
        }

        private sealed class Agent : IQueuedPathAgent
        {
            public Agent(IPathProvider path, float progress) { QueueProvider = path; GlobalNormalizedTime = progress; }
            public ulong PlaybackId { get; set; } = 1;
            public IPathProvider QueueProvider { get; }
            public bool IsMoving => true;
            public float GlobalNormalizedTime { get; }
            public int SnapshotRevision => QueueProvider.Revision;
            public PathQueueAgentSettings QueueSettings => new PathQueueAgentSettings(1f, true, true, true);
            public Action OnApply;
            public int ApplyCount;
            public void ApplyQueueState(PathQueueRegistration registration, PathQueueState state) { ApplyCount++; OnApply?.Invoke(); }
            public void OnQueueDetached(PathQueueRegistration registration, EPathQueueDetachReason reason) { }
        }

        private static void Tick(QueuedPathManager manager, int frame) =>
            ((IPathRuntimeTickable)manager).Tick(0.1f, 0.1f, frame);

        private GameObject Create(string name)
        {
            var go = new GameObject(name); _objects.Add(go); return go;
        }

        private PathData CreatePath()
        {
            var path = Create("Frame test path").AddComponent<PathData>();
            path.ConfigureMovementSettings(PathMovementSettings.Time(5f));
            path.ConfigureControlPoints(new[] { Vector3.zero, Vector3.right * 10f }); path.Init(); return path;
        }

        private QueuedPathManager CreateManager(PathData path)
        {
            var manager = Create("Frame test manager").AddComponent<QueuedPathManager>();
            manager.Init(); manager.ConfigureRoute(path); return manager;
        }
    }
}
