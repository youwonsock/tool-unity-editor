using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldGoalTestSupport;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The additional-Goal API of a running Manager: its checks, a Goal at the
    /// default Goal's position that samples the same, nearest, a move that
    /// advances the Revision, removal, Release, and StaticBaked's refusal.
    /// </summary>
    [TestFixture, Category("Goal")]
    internal sealed class AdditionalGoalManagerTests : FlowFieldPlayTestBase
    {
        private const float Radius = 2f;
        private const int Samples = 32;

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBase() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator StaticBakedRefusesAdditionalGoals()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.StaticBaked);
            FlowFieldManager manager = Manager;
            var refused = Assert.Throws<InvalidOperationException>(
                () => manager.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(WorldBounds(manager).center)));
            Assert.That(refused.Message, Is.EqualTo("Adding a Goal is not allowed in StaticBaked mode."));
        }

        private IEnumerator Run(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldPlayScene.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            Bounds bounds = WorldBounds(manager);

            Assert.Throws<ArgumentException>(() => manager.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.None));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => manager.AddGoal((FlowFieldGoalKind)7, FlowFieldGoalRequest.Position(bounds.center)));
            Assert.That(manager.IsGoalReady(default), Is.False);
            Assert.That(manager.SetGoal(default(FlowFieldGoalHandle), FlowFieldGoalRequest.Position(bounds.center)), Is.False);
            Assert.That(manager.RemoveGoal(default), Is.False);
            Assert.That(manager.TrySample(bounds.center, default(FlowFieldGoalHandle), out _), Is.False);

            Vector3 goal = Point(bounds, 0.3f, 0.7f);
            manager.SetGoal(FlowFieldGoalRequest.Position(goal, Radius));
            FlowFieldGoalHandle handle = manager.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(goal, Radius));
            Assert.That(handle.IsValid, Is.True);
            Assert.That(manager.IsGoalReady(handle), Is.False, "A Goal builds after the default Goal's field.");
            yield return FlowFieldPlayScene.WaitUntilReady();
            AssertGoal(manager, goal, "the default Goal");
            Assert.That(manager.IsGoalReady(handle), Is.True, "Ready waits for the additional Goals.");

            var random = new System.Random(3);
            for (int i = 0; i < Samples; i++)
            {
                Vector3 position = StormTests.Pick(bounds, random);
                bool sampled = manager.TrySample(position, out FlowFieldSample expected);
                Assert.That(manager.TrySample(position, handle, out FlowFieldSample sample), Is.EqualTo(sampled));
                Assert.That(sample, Is.EqualTo(expected), "at " + position.ToString("F2"));
            }
            Assert.That(manager.TrySampleNearestShared(goal, out _, out FlowFieldGoalHandle nearest), Is.True);
            Assert.That(nearest, Is.EqualTo(handle));

            int revision = manager.Revision;
            Assert.That(manager.SetGoal(handle, FlowFieldGoalRequest.Position(Point(bounds, 0.7f, 0.3f), Radius)), Is.True);
            yield return FlowFieldPlayScene.WaitUntilReady();
            Assert.That(manager.Revision, Is.GreaterThan(revision), "A moved Goal's new field is a new Revision.");

            Assert.That(manager.RemoveGoal(handle), Is.True);
            Assert.That(manager.IsGoalReady(handle), Is.False);
            Assert.That(manager.TrySample(goal, handle, out _), Is.False);
            Assert.That(manager.RemoveGoal(handle), Is.False);

            FlowFieldGoalHandle group = manager.AddGoal(FlowFieldGoalKind.Group, FlowFieldGoalRequest.Position(goal));
            Assert.That(group, Is.Not.EqualTo(handle), "A handle never names another Goal.");
            manager.Release();
            Assert.That(manager.IsGoalReady(group), Is.False);
            Assert.Throws<InvalidOperationException>(
                () => manager.AddGoal(FlowFieldGoalKind.Shared, FlowFieldGoalRequest.Position(goal)));
            manager.Init();
            yield return FlowFieldPlayScene.WaitUntilReady();
            Assert.That(manager.IsGoalReady(group), Is.False, "Release removes every additional Goal.");
            Assert.That(manager.SetGoal(group, FlowFieldGoalRequest.Position(goal)), Is.False);
        }
    }
}
