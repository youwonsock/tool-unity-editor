using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// MDSOLVE: a registered Modifier that never unregisters is destroyed while
    /// a build runs. The build leaves it out: no Fault, the registry drops it,
    /// its cells lose its effect and one new field is published. The control
    /// case destroys it while Ready, then changes the Goal.
    /// </summary>
    [TestFixture, Category("Modifiers")]
    internal sealed class MidBuildModifierDestroyTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestMdsolve";

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic() => DestroyMidBuild(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakedBase() => DestroyMidBuild(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamic() => DestroyMidBuild(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamicDestroyedWhileReady()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            yield return FlowFieldPlayScene.WaitUntilSettled(FlowFieldModifierScenarios.StartSeconds);
            FlowFieldManager manager = Manager;
            Bounds bounds = FlowFieldModifierScenarios.WorldBounds(manager);
            FlowFieldProbeModifier probe = null;
            yield return Register(manager, bounds, created => probe = created);
            int registeredRevision = manager.Revision;

            Object.DestroyImmediate(probe);
            FlowFieldModifierScenarios.SetGoal(manager, Goal(bounds));
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));

            yield return AssertLeftOut(manager, registeredRevision);
        }

        private static IEnumerator DestroyMidBuild(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldPlayScene.Enter(scene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled(FlowFieldModifierScenarios.StartSeconds);
            FlowFieldManager manager = Manager;
            Bounds bounds = FlowFieldModifierScenarios.WorldBounds(manager);
            FlowFieldProbeModifier probe = null;
            yield return Register(manager, bounds, created => probe = created);
            int registeredRevision = manager.Revision;

            // A tiny budget spreads the build over frames, so the destroy lands
            // in the first frame that has a build running.
            FlowFieldBuildSettings.BudgetMilliseconds = FlowFieldModifierScenarios.TinyBudgetMilliseconds;
            FlowFieldModifierScenarios.SetGoal(manager, Goal(bounds));
            int goalFrame = Time.frameCount;
            int deadline = goalFrame + FlowFieldModifierScenarios.BuildStartFrames;
            while (!FlowFieldModifierScenarios.BuildRunsAfter(manager, goalFrame))
            {
                FlowFieldModifierScenarios.FailIfPast(deadline, "Never saw an active build");
                yield return null;
            }
            Object.DestroyImmediate(probe);
            FlowFieldBuildSettings.BudgetMilliseconds = FlowFieldModifierScenarios.DefaultBudgetMilliseconds;

            yield return AssertLeftOut(manager, registeredRevision);
        }

        // The probe's box spans the whole height, at 40% across X and Z.
        private static IEnumerator Register(FlowFieldManager manager, Bounds bounds, System.Action<FlowFieldProbeModifier> created)
        {
            FlowFieldProbeModifier probe = FlowFieldModifierScenarios.CreateProbe(
                ProbeName,
                FlowFieldModifierScenarios.At(bounds, 0.4f, 0.4f),
                new Vector3(4f, bounds.size.y + 2f, 4f));
            Assert.That(manager.RegisterVectorModifier(probe), Is.True);
            created(probe);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            Assert.That(FlowFieldModifierScenarios.RegisteredCount(manager), Is.EqualTo(1));
            Assert.That(FlowFieldModifierScenarios.SlowedCells(manager), Is.GreaterThan(0), "The probe slowed no cell.");
        }

        private static Vector3 Goal(Bounds bounds) => FlowFieldModifierScenarios.At(bounds, 0.75f, 0.25f);

        private static IEnumerator AssertLeftOut(FlowFieldManager manager, int registeredRevision)
        {
            yield return FlowFieldPlayScene.WaitSeconds(3f);
            Assert.That(manager.IsFaulted, Is.False, FlowFieldModifierScenarios.State(manager));
            yield return FlowFieldPlayScene.WaitUntilReady();
            Assert.That(FlowFieldModifierScenarios.RegisteredCount(manager), Is.Zero, "The destroyed Modifier is still registered.");
            Assert.That(FlowFieldModifierScenarios.SlowedCells(manager), Is.Zero, "A published field still carries the destroyed Modifier.");
            Assert.That(manager.LastError, Is.Null.Or.Empty);
            Assert.That(manager.Revision, Is.EqualTo(registeredRevision + 1),
                "Expected one publish, without the destroyed Modifier: " + FlowFieldPlayScene.Describe());
        }
    }
}
