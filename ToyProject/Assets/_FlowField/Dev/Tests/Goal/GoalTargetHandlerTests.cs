using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldGoalTestSupport;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// GTEV: a StateChanged handler that disables a GoalTarget during the
    /// target's own SetGoal. The target already holds that Goal then, so
    /// disabling it clears the Goal instead of leaving it behind.
    /// </summary>
    [TestFixture, Category("Goal")]
    internal sealed class GoalTargetHandlerTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator TargetDisabledInsideItsOwnSetGoalClearsIt()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Bounds bounds = WorldBounds(manager);
            Vector3 p = Point(bounds, 0.3f, 0.3f);
            Vector3 q = Point(bounds, 0.7f, 0.7f);

            FlowFieldGoalTarget a = AddTarget("FlowFieldTestTargetA", p, manager);
            yield return Step();
            AssertGoal(manager, p, "A reported");

            bool disabledInHandler = false;
            Action<FlowFieldRuntimeState> handler = state =>
            {
                if (state != FlowFieldRuntimeState.Building || !a.enabled)
                    return;
                a.enabled = false;
                disabledInHandler = true;
            };
            manager.StateChanged += handler;
            try
            {
                a.transform.position = q;
                yield return Step();
                yield return FlowFieldPlayScene.WaitSeconds(1f);
            }
            finally
            {
                manager.StateChanged -= handler;
            }

            Assert.That(disabledInHandler, Is.True, "The handler never saw Building after A moved: " + DescribeGoal(manager));
            Assert.That(a.enabled, Is.False);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), FlowFieldPlayScene.Describe());
            AssertNoGoal(manager, "A disabled inside its own SetGoal");
        }
    }
}
