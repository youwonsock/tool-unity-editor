using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A Default Direction edit made during a Surface2D solve is owed, and the
    /// solved base composes with it; reverting the edit while that composition
    /// runs must still publish the reverted direction everywhere (kit ddsync).
    /// </summary>
    /// <remarks>
    /// The kit drove the pipeline through RunPipeline by reflection with a
    /// 0.05 ms clock budget, all in one call. These tests call the session's
    /// direct Pump(budget, waitForWorker), the same pipeline without the
    /// obstacle sweep, with budgets that expire after a set number of checks,
    /// so every run stops at the same points: the solve running, then one
    /// piece of its composition.
    /// </remarks>
    [TestFixture, Category("Protocol")]
    internal sealed class DefaultDirectionSyncTests : FlowFieldPlayTestBase
    {
        private const int MaxPumps = 400;

        private enum Variant
        {
            // No edit: the field the other variants are compared with.
            Control,
            // D2 during the solve, D1 again during its composition.
            Revert,
            // D2 during the solve, kept.
            Kept,
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator RuntimeDynamicControl() => Run(FlowFieldBakeMode.RuntimeDynamic, Variant.Control);

        [UnityTest, Timeout(300000)]
        public IEnumerator RuntimeDynamicRevert() => Run(FlowFieldBakeMode.RuntimeDynamic, Variant.Revert);

        [UnityTest, Timeout(300000)]
        public IEnumerator RuntimeDynamicKept() => Run(FlowFieldBakeMode.RuntimeDynamic, Variant.Kept);

        [UnityTest, Timeout(300000)]
        public IEnumerator BakedBaseControl() => Run(FlowFieldBakeMode.BakedBase, Variant.Control);

        [UnityTest, Timeout(300000)]
        public IEnumerator BakedBaseRevert() => Run(FlowFieldBakeMode.BakedBase, Variant.Revert);

        [UnityTest, Timeout(300000)]
        public IEnumerator BakedBaseKept() => Run(FlowFieldBakeMode.BakedBase, Variant.Kept);

        private static IEnumerator Run(FlowFieldBakeMode bakeMode, Variant variant)
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            FlowFieldSession session = manager.Session;
            FlowFieldProtocolScenes.SetFloat(manager, FlowFieldProtocolScenes.GoalRadiusPath, 8f);
            FlowFieldProtocolScenes.SetVector(manager, FlowFieldProtocolScenes.DefaultDirectionPath, FlowFieldProtocolScenes.PlusZ);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);

            // From here to the end, one frame: only the direct pumps run the session.
            int revision = session.Revision;
            Vector3 goal = FlowFieldProtocolScenes.SurfaceSolveGoal(manager);
            manager.SetGoal(FlowFieldGoalRequest.Position(goal, 8f));
            FlowFieldProtocolScenes.BeginSurfaceSolve(session);
            if (variant != Variant.Control)
            {
                FlowFieldProtocolScenes.SetVector(manager, FlowFieldProtocolScenes.DefaultDirectionPath, FlowFieldProtocolScenes.PlusX);
                Assert.That(session.HasOwedInput, Is.True, "D2 is not owed: " + FlowFieldProtocolScenes.Describe(session));
                Assert.That(session.IsWaitingForExternalCompletion, Is.True,
                    "A Default Direction edit ended the solve: " + FlowFieldProtocolScenes.Describe(session));
            }

            FlowFieldProtocolScenes.FinishSolveAndComposeOnePiece(session);
            if (variant == Variant.Revert)
                FlowFieldProtocolScenes.SetVector(manager, FlowFieldProtocolScenes.DefaultDirectionPath, FlowFieldProtocolScenes.PlusZ);

            for (int pumps = 0; pumps < MaxPumps && (manager.State != FlowFieldRuntimeState.Ready || session.HasActiveBuild); pumps++)
                session.Pump(FlowFieldTestBudget.AfterChecks(0), waitForWorker: true);
            Assert.That(manager.State == FlowFieldRuntimeState.Ready && !session.HasActiveBuild, Is.True,
                "The build did not settle in " + MaxPumps + " pumps: " + FlowFieldProtocolScenes.Describe(session));

            string counts = FlowFieldProtocolScenes.DirectionCounts(manager);
            int published = session.Revision - revision;
            switch (variant)
            {
                case Variant.Control:
                    FlowFieldRecorded.AreEqual(FlowFieldProtocolScenes.ControlDirectionCounts, counts, "ddsync control directions");
                    Assert.That(published, Is.EqualTo(1), "ddsync control publications");
                    break;
                case Variant.Revert:
                    // The kit's rule: the reverted field equals the control's.
                    Assert.That(counts, Is.EqualTo(FlowFieldProtocolScenes.ControlDirectionCounts),
                        "The reverted Default Direction left D2 in the field.");
                    // The D2 composition publishes, then the revert composes D1.
                    Assert.That(published, Is.EqualTo(2), "ddsync revert publications");
                    break;
                default:
                    FlowFieldRecorded.AreEqual(FlowFieldProtocolScenes.KeptDirectionCounts, counts, "ddsync kept directions");
                    // The owed submit finds D2 already composed: no new Revision.
                    Assert.That(published, Is.EqualTo(1), "ddsync kept publications");
                    break;
            }
        }
    }
}
