using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A Volume3D build whose Grid changes or whose Goal is removed is
    /// discarded at once, when the input arrives, instead of running on to be
    /// rejected as a Surface2D build is; the request builds again, and the
    /// settled field equals a fresh build.
    /// </summary>
    [TestFixture, Category("Protocol")]
    internal sealed class VolumeDiscardedBuildTests : FlowFieldPlayTestBase
    {
        // The Volume3D test scene's 20 x 12 x 20 m box at 2 m cells.
        private const float EditedCellSize = 2f;
        private const int EditedCellCount = 600;

        // A Grid edit cannot keep a BakedBase bake matching, so it runs in
        // RuntimeDynamic only.
        [UnityTest, Timeout(300000)]
        public IEnumerator GridChangeDuringBuild()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            FlowFieldSession session = manager.Session;
            FlowFieldGoalRequest goal = FlowFieldGoalRequest.Position(FlowFieldProtocolScenes.VolumeGoal(manager), 0f);
            manager.SetGoal(goal);
            FlowFieldProtocolScenes.BeginVolumeBuild(session);
            int revision = session.Revision;

            FlowFieldProtocolScenes.SetFloat(manager, FlowFieldProtocolScenes.CellSizePath, EditedCellSize);
            AssertDiscarded(session, revision, "Grid change");
            Assert.That(manager.TryGetPublishedField(out _), Is.False, "The new Grid did not hide the published field.");

            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            Assert.That(FlowFieldPlayScene.CellCount(manager), Is.EqualTo(EditedCellCount), "The field is not on the new Grid.");
            FlowFieldFieldSnapshot afterDiscard = FlowFieldFieldSnapshot.Capture(manager);
            yield return FlowFieldProtocolScenes.SettleFresh(manager, goal);
            Assert.That(afterDiscard.Difference(FlowFieldFieldSnapshot.Capture(manager)), Is.Empty,
                "The field after the discarded build differs from a fresh build: " + FlowFieldPlayScene.Describe());
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator GoalRemovedDuringBuildRuntimeDynamic() => GoalRemoved(FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator GoalRemovedDuringBuildBakedBase() => GoalRemoved(FlowFieldBakeMode.BakedBase);

        private static IEnumerator GoalRemoved(FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.VolumeScene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            FlowFieldSession session = manager.Session;
            manager.SetGoal(FlowFieldGoalRequest.Position(FlowFieldProtocolScenes.VolumeGoal(manager), 0f));
            FlowFieldProtocolScenes.BeginVolumeBuild(session);
            int revision = session.Revision;

            manager.SetGoal(FlowFieldGoalRequest.None);
            AssertDiscarded(session, revision, "Goal removal");

            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True);
            Assert.That(field.Goal.IsActive, Is.False, "The removed Goal was published.");
            FlowFieldFieldSnapshot afterDiscard = FlowFieldFieldSnapshot.Capture(manager);
            yield return FlowFieldProtocolScenes.SettleFresh(manager, FlowFieldGoalRequest.None);
            Assert.That(afterDiscard.Difference(FlowFieldFieldSnapshot.Capture(manager)), Is.Empty,
                "The field after the discarded build differs from a fresh build: " + FlowFieldPlayScene.Describe());
        }

        // Synchronously, before any pump: no build runs, the request is
        // pending again and nothing was published.
        private static void AssertDiscarded(FlowFieldSession session, int revision, string what)
        {
            Assert.That(session.HasActiveBuild, Is.False,
                "The " + what + " did not discard the running build: " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.HasPendingRequest, Is.True,
                "The request does not build again after the " + what + ": " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.State, Is.EqualTo(FlowFieldRuntimeState.Building), FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.Revision, Is.EqualTo(revision), "The discarded build published.");
            Assert.That(session.IsFaulted, Is.False, FlowFieldProtocolScenes.Describe(session));
        }
    }
}
