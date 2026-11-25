using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldGoalTestSupport;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// P1: a disabled GoalTarget clears only a Goal it still holds. A newer
    /// Goal a script set, or one a later target reported, stays when the
    /// earlier target is disabled; enabling the target again sets its Goal.
    /// </summary>
    [TestFixture, Category("Goal")]
    internal sealed class GoalTargetOwnershipTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator DisabledTargetKeepsANewerGoal()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Bounds bounds = WorldBounds(manager);
            Vector3 p = Point(bounds, 0.3f, 0.3f);
            Vector3 q = Point(bounds, 0.7f, 0.7f);

            FlowFieldGoalTarget a = AddTarget("FlowFieldTestTargetA", p, manager);
            yield return Step();
            AssertGoal(manager, p, "A reported");

            manager.SetGoal(FlowFieldGoalRequest.Position(q, 0f));
            AssertGoal(manager, q, "script SetGoal(Q)");
            yield return Step();
            a.enabled = false;
            yield return Step();
            AssertGoal(manager, q, "A disabled after a script set Q");

            a.enabled = true;
            yield return Step();
            AssertGoal(manager, p, "A enabled again");

            AddTarget("FlowFieldTestTargetB", q, manager);
            yield return Step();
            AssertGoal(manager, q, "B reported last");

            a.enabled = false;
            yield return Step();
            AssertGoal(manager, q, "A disabled while B holds the Goal");
        }
    }
}
