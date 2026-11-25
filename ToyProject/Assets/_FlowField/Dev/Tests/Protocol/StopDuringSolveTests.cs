using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Stopping (disabling the Manager) or releasing a session while its
    /// Surface2D solve runs drops that solve: the Revision stays until the
    /// restarted session's own build publishes, and that field equals a fresh
    /// build with the same inputs.
    /// </summary>
    [TestFixture, Category("Protocol")]
    internal sealed class StopDuringSolveTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator StopRuntimeDynamic() => Run(FlowFieldBakeMode.RuntimeDynamic, release: false);

        [UnityTest, Timeout(300000)]
        public IEnumerator StopBakedBase() => Run(FlowFieldBakeMode.BakedBase, release: false);

        [UnityTest, Timeout(300000)]
        public IEnumerator ReleaseRuntimeDynamic() => Run(FlowFieldBakeMode.RuntimeDynamic, release: true);

        [UnityTest, Timeout(300000)]
        public IEnumerator ReleaseBakedBase() => Run(FlowFieldBakeMode.BakedBase, release: true);

        private static IEnumerator Run(FlowFieldBakeMode bakeMode, bool release)
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled();
            FlowFieldManager manager = Manager;
            FlowFieldSession session = manager.Session;
            FlowFieldGoalRequest goal = FlowFieldGoalRequest.Position(FlowFieldProtocolScenes.SurfaceSolveGoal(manager), 0f);
            manager.SetGoal(goal);
            FlowFieldProtocolScenes.BeginSurfaceSolve(session);
            int revision = session.Revision;
            int generation = session.LifetimeGeneration;

            if (release)
                manager.Release();
            else
                manager.enabled = false;
            FlowFieldRuntimeState stopped = release ? FlowFieldRuntimeState.Released : FlowFieldRuntimeState.Suspended;
            Assert.That(session.State, Is.EqualTo(stopped), FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.LifetimeGeneration, Is.Not.EqualTo(generation), "The lifetime did not end, so a late solve callback would still count.");
            Assert.That(session.HasActiveBuild, Is.False, "The stopped session kept its build: " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.IsWaitingForExternalCompletion, Is.False, "The stopped session still waits for its solve.");
            Assert.That(FlowFieldBuildScheduler.Contains(session), Is.False, "The scheduler still pumps the stopped session.");
            Assert.That(manager.TryGetPublishedField(out _), Is.False, "The stopped session still answers reads.");
            Assert.That(session.Revision, Is.EqualTo(revision));

            // A stopped session takes no pump, and the frames give the old
            // solve every chance to report.
            session.Pump();
            yield return FlowFieldPlayScene.WaitFrames(5);
            Assert.That(session.State, Is.EqualTo(stopped), FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.Revision, Is.EqualTo(revision), "The dropped solve published.");

            if (release)
            {
                manager.Init();
                manager.SetGoal(goal);
            }
            else
            {
                manager.enabled = true;
            }
            Assert.That(manager.Session, Is.SameAs(session), "The restart replaced the session.");
            Assert.That(session.State, Is.EqualTo(FlowFieldRuntimeState.Building), FlowFieldProtocolScenes.Describe(session));
            Assert.That(session.HasActiveBuild, Is.False, "The restart carried the old build over.");
            Assert.That(FlowFieldBuildScheduler.Contains(session), Is.True, "The restarted session is not scheduled.");
            Assert.That(session.Revision, Is.EqualTo(revision), "The restart advanced the Revision.");

            // The restarted session's own build: its solve, then one publication.
            FlowFieldProtocolScenes.BeginSurfaceSolve(session);
            Assert.That(session.Revision, Is.EqualTo(revision), "The Revision advanced before the restarted build published.");
            session.Pump();
            Assert.That(session.Revision, Is.EqualTo(revision + 1),
                "The restarted build did not publish exactly once: " + FlowFieldProtocolScenes.Describe(session));
            Assert.That(manager.TryGetPublishedField(out _), Is.True, FlowFieldProtocolScenes.Describe(session));

            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            FlowFieldFieldSnapshot restarted = FlowFieldFieldSnapshot.Capture(manager);
            yield return FlowFieldProtocolScenes.SettleFresh(manager, goal);
            Assert.That(restarted.Difference(FlowFieldFieldSnapshot.Capture(manager)), Is.Empty,
                "The restarted field differs from a fresh build: " + FlowFieldPlayScene.Describe());
        }
    }
}
