using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static Common.FlowField.Tests.FlowFieldGoalTestSupport;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// E1: a GoalTarget sets its Goal again after Release and Init in one
    /// frame, clears it when disabled while the Manager is faulted (so the
    /// retry leaves it out), and moves it when its Manager field is cleared
    /// and set again.
    /// </summary>
    [TestFixture, Category("Goal")]
    internal sealed class GoalTargetLifecycleTests : FlowFieldPlayTestBase
    {
        private const string TestFault = "Test fault.";

        [UnityTest, Timeout(300000)]
        public IEnumerator TargetFollowsReleaseFaultAndManagerField()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Vector3 p = Point(WorldBounds(manager), 0.3f, 0.3f);

            FlowFieldGoalTarget target = AddTarget("FlowFieldTestGoalTarget", p, manager);
            yield return Step();
            AssertGoal(manager, p, "with the target");

            // Release drops the Goal and Init reads the Goal Transform; the
            // target must see it lost its Goal although no frame passed.
            manager.Release();
            manager.Init();
            yield return Step();
            AssertGoal(manager, p, "after Release and Init in one frame");

            ExpectBuildFailed(Log, TestFault);
            manager.Session.ReportFault(new InvalidOperationException(TestFault));
            target.enabled = false;
            yield return WaitPlayerFrames(2);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Faulted), FlowFieldPlayScene.Describe());

            manager.RequestRebuild();
            yield return Step();
            AssertNoGoal(manager, "retry after the target was disabled while faulted");

            target.enabled = true;
            yield return Step();
            AssertGoal(manager, p, "target enabled again");

            SetTargetManager(target, null);
            yield return Step();
            AssertNoGoal(manager, "Manager field cleared");

            SetTargetManager(target, manager);
            yield return Step();
            AssertGoal(manager, p, "Manager field set again");
        }
    }
}
