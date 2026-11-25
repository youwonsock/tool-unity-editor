using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldGoalTestSupport;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// G1: Release drops a Goal SetGoal set, so the next Init reads the Goal
    /// Transform again. The field after Release and Init must equal the field
    /// with the Goal at the Goal Transform, not the one with the earlier Goal.
    /// </summary>
    [TestFixture, Category("Goal")]
    internal sealed class GoalTransformInitTests : FlowFieldPlayTestBase
    {
        private const string GoalTransformFieldHash = "1D0A6031A29E4DE7";
        private const string EarlierGoalFieldHash = "98F47F4F26BD2434";

        [UnityTest, Timeout(300000)]
        public IEnumerator InitAfterReleaseReadsTheGoalTransform()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Transform goal = GoalTransform(manager);
            Assert.That(goal, Is.Not.Null, "The Volume sample needs a Goal Transform.");
            float radius = SerializedRadius(manager);
            Vector3 p = Point(WorldBounds(manager), 0.2f, goal.position.y, 0.8f);

            manager.SetGoal(FlowFieldGoalRequest.Position(p, radius));
            yield return Settle();

            manager.Release();
            manager.Init();
            yield return Settle();
            string afterInit = FlowFieldPlayScene.Hash(manager);

            manager.SetGoal(FlowFieldGoalRequest.Position(goal.position, radius));
            yield return Settle();
            string atGoalTransform = FlowFieldPlayScene.Hash(manager);

            manager.SetGoal(FlowFieldGoalRequest.Position(p, radius));
            yield return Settle();
            string atP = FlowFieldPlayScene.Hash(manager);

            Assert.That(afterInit, Is.EqualTo(atGoalTransform), "Init after Release kept the earlier Goal instead of the Goal Transform.");
            Assert.That(atP, Is.Not.EqualTo(afterInit), "The earlier Goal must give another field, or this test checks nothing.");
            FlowFieldRecorded.AreEqual(GoalTransformFieldHash, afterInit, "g1 field hash with the Goal Transform");
            FlowFieldRecorded.AreEqual(EarlierGoalFieldHash, atP, "g1 field hash with the earlier Goal");
        }

        private static IEnumerator Settle() => FlowFieldPlayScene.WaitUntilSettled(1f);
    }
}
