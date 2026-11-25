using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// The Default Direction revert of <see cref="DefaultDirectionSyncTests"/>
    /// through the runtime scheduler: D2 set during the solve and D1 again
    /// while the scheduler composes over several frames must publish D1, the
    /// same field as without the edits (kit ddrev).
    /// </summary>
    /// <remarks>
    /// The kit retried until its MCP calls happened to land in the solve and
    /// then in the composition. Here a direct pump begins the solve, so D2
    /// always arrives during it, and a tiny frame budget spreads the
    /// composition over many frames, so the test waits on the session until it
    /// composes and reverts then.
    /// </remarks>
    [TestFixture, Category("Protocol")]
    internal sealed class DefaultDirectionRevertTests : FlowFieldPlayTestBase
    {
        // The kit's budget: one or two composition pieces a frame.
        private const double TinyBudgetMilliseconds = 0.02;
        private const int MaxFrames = 600;

        [UnityTest, Timeout(300000)]
        public IEnumerator RuntimeDynamic() => Run(FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator BakedBase() => Run(FlowFieldBakeMode.BakedBase);

        private static IEnumerator Run(FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            FlowFieldSession session = manager.Session;
            FlowFieldProtocolScenes.SetFloat(manager, FlowFieldProtocolScenes.GoalRadiusPath, 8f);
            FlowFieldProtocolScenes.SetVector(manager, FlowFieldProtocolScenes.DefaultDirectionPath, FlowFieldProtocolScenes.PlusZ);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);

            FlowFieldBuildSettings.BudgetMilliseconds = TinyBudgetMilliseconds;
            Vector3 goal = FlowFieldProtocolScenes.SurfaceSolveGoal(manager);
            manager.SetGoal(FlowFieldGoalRequest.Position(goal, 8f));
            FlowFieldProtocolScenes.BeginSurfaceSolve(session);
            int revision = session.Revision;
            FlowFieldProtocolScenes.SetVector(manager, FlowFieldProtocolScenes.DefaultDirectionPath, FlowFieldProtocolScenes.PlusX);
            Assert.That(session.HasOwedInput, Is.True, "D2 is not owed: " + FlowFieldProtocolScenes.Describe(session));

            // The scheduler polls the solve, then composes a piece or two a frame.
            int frames = 0;
            while (!FlowFieldProtocolScenes.IsComposing(session))
            {
                Assert.That(session.HasActiveBuild && session.Revision == revision, Is.True,
                    "The build published before its composition was seen: " + FlowFieldProtocolScenes.Describe(session));
                Assert.That(++frames, Is.LessThan(MaxFrames),
                    "The solve did not end: " + FlowFieldProtocolScenes.Describe(session));
                yield return null;
            }
            Assert.That(session.Revision, Is.EqualTo(revision), "The composition published before the revert.");
            FlowFieldProtocolScenes.SetVector(manager, FlowFieldProtocolScenes.DefaultDirectionPath, FlowFieldProtocolScenes.PlusZ);

            FlowFieldBuildSettings.ResetToDefault();
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            Assert.That(FlowFieldProtocolScenes.DirectionCounts(manager), Is.EqualTo(FlowFieldProtocolScenes.ControlDirectionCounts),
                "The reverted Default Direction left D2 in the field: " + FlowFieldPlayScene.Describe());
        }
    }
}
