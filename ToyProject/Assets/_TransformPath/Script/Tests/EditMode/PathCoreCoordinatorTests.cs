using NUnit.Framework;

namespace Common.TransformPath.Tests.EditMode
{
    public sealed class PathCoreCoordinatorTests
    {
        [Test]
        public void QueueCoordinatorBlocksInsideSpacingAndSlowsOutsideIt()
        {
            PathQueueCoordinator coordinator = new PathQueueCoordinator(
                1.5f,
                true,
                3f,
                0.1f,
                null);
            FakeAgent agent = new FakeAgent
            {
                Settings = new PathQueueAgentSettings(
                    1.5f,
                    true,
                    true,
                    true),
            };

            Assert.That(
                coordinator.CalculateSpeedMultiplier(agent, 1.5f, 1.5f),
                Is.EqualTo(0f));
            Assert.That(
                coordinator.CalculateSpeedMultiplier(agent, 2.25f, 1.5f),
                Is.GreaterThan(0.1f).And.LessThan(1f));
            Assert.That(
                coordinator.CalculateSpeedMultiplier(agent, 3f, 1.5f),
                Is.EqualTo(1f));
        }

        [Test]
        public void TimeScaleOwnershipIgnoresStaleReleaseAndRestoresPair()
        {
            FakeTimeStore store = new FakeTimeStore
            {
                TimeScale = 0.75f,
                FixedDeltaTime = 0.02f,
            };
            PathTimeScaleCoordinator coordinator =
                new PathTimeScaleCoordinator(store);

            coordinator.Request(1UL, 2f, 1f, null, 10);
            coordinator.Tick(0.5f, 11);
            Assert.That(store.TimeScale, Is.EqualTo(1.375f).Within(0.0001f));

            coordinator.Request(2UL, 0.5f, 1f, null, 12);
            coordinator.Release(1UL);
            Assert.That(store.TimeScale, Is.EqualTo(1.375f).Within(0.0001f));

            coordinator.Release(2UL);
            Assert.That(store.TimeScale, Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(store.FixedDeltaTime, Is.EqualTo(0.02f).Within(0.0001f));
        }

        private sealed class FakeTimeStore : IPathTimeScaleStore
        {
            public float TimeScale { get; set; }
            public float FixedDeltaTime { get; set; }
        }

        private sealed class FakeAgent : IQueuedPathAgent
        {
            public PathQueueAgentSettings Settings;
            public ulong PlaybackId => 1UL;
            public IPathProvider QueueProvider => null;
            public bool IsMoving => true;
            public float GlobalNormalizedTime => 0.5f;
            public int SnapshotRevision => 1;
            public PathQueueAgentSettings QueueSettings => Settings;

            public void ApplyQueueState(
                PathQueueRegistration registration,
                PathQueueState state)
            {
            }

            public void OnQueueDetached(
                PathQueueRegistration registration,
                EPathQueueDetachReason reason)
            {
            }
        }
    }
}
