using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A Surface2D build whose Grid changes or whose Goal is removed during its
    /// solve keeps running until the solve ends, is then rejected without
    /// publishing, and the next build settles Ready on a field equal to a
    /// fresh build. A region notice made during the rejected build stays
    /// uncertain until a later build publishes, and the uncertain-region count
    /// then returns to what it was before.
    /// </summary>
    [TestFixture, Category("Protocol")]
    internal sealed class SurfaceRejectedBuildTests : FlowFieldPlayTestBase
    {
        // The sample's 100 m square at 2 m cells.
        private const float EditedCellSize = 2f;
        private const int EditedCellCount = 2500;

        // A Grid edit cannot keep a BakedBase bake matching, so it runs in
        // RuntimeDynamic only.
        [UnityTest, Timeout(300000)]
        public IEnumerator GridChangeDuringSolve()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            FlowFieldSession session = manager.Session;
            FlowFieldGoalRequest goal = FlowFieldGoalRequest.Position(FlowFieldProtocolScenes.SurfaceSolveGoal(manager), 0f);
            Uncertainty uncertainty = BeginSolveWithNotice(manager, session, goal);

            FlowFieldProtocolScenes.SetFloat(manager, FlowFieldProtocolScenes.CellSizePath, EditedCellSize);
            Assert.That(session.IsWaitingForExternalCompletion, Is.True,
                "A Surface2D build ends only when its solve does: " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(manager.TryGetPublishedField(out _), Is.False, "The new Grid did not hide the published field.");
            Assert.That(session.State, Is.EqualTo(FlowFieldRuntimeState.Building), FlowFieldProtocolScenes.Describe(session));

            yield return FinishRejected(manager, session, uncertainty, "Grid change");
            Assert.That(FlowFieldPlayScene.CellCount(manager), Is.EqualTo(EditedCellCount), "The field is not on the new Grid.");
            FlowFieldFieldSnapshot afterReject = FlowFieldFieldSnapshot.Capture(manager);
            yield return FlowFieldProtocolScenes.SettleFresh(manager, goal);
            Assert.That(afterReject.Difference(FlowFieldFieldSnapshot.Capture(manager)), Is.Empty,
                "The field after the rejected build differs from a fresh build: " + FlowFieldPlayScene.Describe());
            FlowFieldRecorded.AreEqual("before=0 notified=1 rejected=1 settled=0", uncertainty.ToString(),
                "Surface2D Grid change during the solve, uncertain regions");
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator GoalRemovedDuringSolveRuntimeDynamic()
            => GoalRemoved(FlowFieldBakeMode.RuntimeDynamic, "before=0 notified=1 rejected=1 settled=0");

        [UnityTest, Timeout(300000)]
        public IEnumerator GoalRemovedDuringSolveBakedBase()
            => GoalRemoved(FlowFieldBakeMode.BakedBase, "before=0 notified=1 rejected=1 settled=0");

        private static IEnumerator GoalRemoved(FlowFieldBakeMode bakeMode, string recordedUncertainty)
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            FlowFieldSession session = manager.Session;
            FlowFieldGoalRequest goal = FlowFieldGoalRequest.Position(FlowFieldProtocolScenes.SurfaceSolveGoal(manager), 0f);
            Uncertainty uncertainty = BeginSolveWithNotice(manager, session, goal);

            manager.SetGoal(FlowFieldGoalRequest.None);
            Assert.That(session.IsWaitingForExternalCompletion, Is.True,
                "A Surface2D build ends only when its solve does: " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.HasOwedInput, Is.True, "The Goal removal is not owed: " + FlowFieldProtocolScenes.Describe(session));

            yield return FinishRejected(manager, session, uncertainty, "Goal removal");
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True);
            Assert.That(field.HasActiveGoal, Is.False, "The removed Goal was published.");
            FlowFieldFieldSnapshot afterReject = FlowFieldFieldSnapshot.Capture(manager);
            yield return FlowFieldProtocolScenes.SettleFresh(manager, FlowFieldGoalRequest.None);
            Assert.That(afterReject.Difference(FlowFieldFieldSnapshot.Capture(manager)), Is.Empty,
                "The field after the rejected build differs from a fresh build: " + FlowFieldPlayScene.Describe());
            FlowFieldRecorded.AreEqual(recordedUncertainty, uncertainty.ToString(),
                "Surface2D " + bakeMode + " Goal removal during the solve, uncertain regions");
        }

        /// <summary>The uncertain-region count at each point of the scenario.</summary>
        private sealed class Uncertainty
        {
            internal int Revision;
            internal int Before;
            internal int Notified;
            internal int Rejected;
            internal int Settled;

            public override string ToString()
                => "before=" + Before + " notified=" + Notified + " rejected=" + Rejected + " settled=" + Settled;
        }

        // A Goal solve runs, and a region notice arrives during it.
        private static Uncertainty BeginSolveWithNotice(FlowFieldManager manager, FlowFieldSession session, FlowFieldGoalRequest goal)
        {
            var uncertainty = new Uncertainty { Before = session.Safety.UncertainRegionCount };
            manager.SetGoal(goal);
            FlowFieldProtocolScenes.BeginSurfaceSolve(session);
            uncertainty.Revision = session.Revision;
            manager.NotifyObstacleRegionDirty(FlowFieldProtocolScenes.RegionAround(goal.WorldPosition));
            uncertainty.Notified = session.Safety.UncertainRegionCount;
            Assert.That(uncertainty.Notified, Is.GreaterThan(uncertainty.Before), "The notice made no region uncertain.");
            return uncertainty;
        }

        // The solve ends and is rejected, and the next build begins in the
        // same pump; then the scheduler finishes it.
        private static IEnumerator FinishRejected(
            FlowFieldManager manager,
            FlowFieldSession session,
            Uncertainty uncertainty,
            string what)
        {
            session.Pump(FlowFieldTestBudget.AfterChecks(0), waitForWorker: true);
            Assert.That(session.IsFaulted, Is.False, FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.Revision, Is.EqualTo(uncertainty.Revision),
                "The rejected build published after the " + what + ": " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.HasActiveBuild, Is.True,
                "No build replaced the rejected one: " + FlowFieldProtocolScenes.Describe(session));
            uncertainty.Rejected = session.Safety.UncertainRegionCount;
            Assert.That(uncertainty.Rejected, Is.GreaterThan(uncertainty.Before),
                "The rejected build resolved the region noticed during it.");

            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), FlowFieldPlayScene.Describe());
            Assert.That(session.Revision, Is.GreaterThan(uncertainty.Revision), "Nothing published after the rejection.");
            uncertainty.Settled = session.Safety.UncertainRegionCount;
            Assert.That(uncertainty.Settled, Is.EqualTo(uncertainty.Before),
                "The uncertain regions did not return to the count before the rejected build.");
        }
    }
}
