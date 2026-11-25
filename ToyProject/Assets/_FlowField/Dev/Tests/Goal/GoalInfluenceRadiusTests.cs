using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldGoalTestSupport;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// SetGoal's Influence Radius belongs to its Goal. RAD: after Release and
    /// Init the Goal Transform comes back with the authored radius, not the
    /// one SetGoal used. RAD2: an invalid Goal Influence Radius edit faults
    /// at once even while SetGoal's own radius is in use, and a fixed edit and
    /// RequestRebuild recover.
    /// </summary>
    [TestFixture, Category("Goal")]
    internal sealed class GoalInfluenceRadiusTests : FlowFieldPlayTestBase
    {
        private const string InvalidRadius = "Goal Influence Radius must be finite and not negative.";

        [UnityTest, Timeout(300000)]
        public IEnumerator ReleaseRestoresTheAuthoredRadius()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Transform goal = GoalTransform(manager);
            Assert.That(goal, Is.Not.Null, "The sample needs a Goal Transform.");
            float authored = SerializedRadius(manager);
            AssertGoal(manager, goal.position, "authored Goal");
            Assert.That(Info(manager).GoalInfluenceRadius, Is.EqualTo(authored), DescribeGoal(manager));

            Vector3 point = Point(WorldBounds(manager), 0.6f, 0.6f);
            manager.SetGoal(FlowFieldGoalRequest.Position(point, 7f));
            yield return Step();
            AssertGoal(manager, point, "SetGoal with radius 7");
            Assert.That(Info(manager).GoalInfluenceRadius, Is.EqualTo(7f), DescribeGoal(manager));
            Assert.That(SerializedRadius(manager), Is.EqualTo(authored), "SetGoal must leave the serialized radius as authored.");

            manager.Release();
            manager.Init();
            yield return Step();
            AssertGoal(manager, goal.position, "Init after Release");
            Assert.That(Info(manager).GoalInfluenceRadius, Is.EqualTo(authored),
                "The Goal Transform came back with SetGoal's radius: " + DescribeGoal(manager));
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator InvalidRadiusEditFaultsAtOnce()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            manager.SetGoal(FlowFieldGoalRequest.Position(WorldBounds(manager).center, 7f));
            yield return Step();

            ExpectBuildFailed(Log, InvalidRadius);
            SetSerializedRadius(manager, -1f);
            Assert.That(manager.IsFaulted, Is.True, "The edit must fault at once: " + FlowFieldPlayScene.Describe());
            Assert.That(manager.LastError, Is.EqualTo(InvalidRadius));

            yield return FlowFieldPlayScene.WaitSeconds(1f);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Faulted), FlowFieldPlayScene.Describe());
            Assert.That(manager.LastError, Is.EqualTo(InvalidRadius));

            SetSerializedRadius(manager, 0f);
            Assert.DoesNotThrow(() => manager.RequestRebuild());
            yield return Step();
            Assert.That(manager.IsFaulted, Is.False, FlowFieldPlayScene.Describe());
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready));
            Assert.That(manager.LastError, Is.Null.Or.Empty);
        }
    }
}
