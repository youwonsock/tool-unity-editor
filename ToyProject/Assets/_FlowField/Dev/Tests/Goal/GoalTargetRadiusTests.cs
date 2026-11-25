using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldGoalTestSupport;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// G2: a GoalTarget that stays still and changes its Influence Radius
    /// sends the new radius. The Manager's serialized radius stays as authored.
    /// </summary>
    [TestFixture, Category("Goal")]
    internal sealed class GoalTargetRadiusTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator StillTargetSendsItsNewRadius()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            float authored = SerializedRadius(manager);
            Vector3 position = WorldBounds(manager).center + new Vector3(2f, 0f, 2f);

            FlowFieldGoalTarget target = AddTarget("FlowFieldTestRadiusTarget", position, manager, 0f);
            yield return Step();
            AssertGoal(manager, position, "target reported");
            Assert.That(Info(manager).GoalInfluenceRadius, Is.EqualTo(0f), DescribeGoal(manager));

            SetTargetRadius(target, 3f);
            yield return Step();
            AssertGoal(manager, position, "after the radius edit");
            Assert.That(Info(manager).GoalInfluenceRadius, Is.EqualTo(3f), "The still target did not send its new radius: " + DescribeGoal(manager));
            Assert.That(SerializedRadius(manager), Is.EqualTo(authored), "The Manager's serialized radius must stay as authored.");
        }
    }
}
