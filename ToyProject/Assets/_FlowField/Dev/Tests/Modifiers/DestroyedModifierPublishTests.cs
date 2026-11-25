using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// V1: a Modifier that never unregisters is destroyed mid-build, its
    /// trigger left in place. The build leaves it out, so no published field
    /// carries its effect, not even for a frame, and exactly one field is
    /// published. A built-in Modifier destroyed the same way unregisters in
    /// OnDisable; the build it began may still finish with it, so only the
    /// end state is checked: clean, with nothing registered and no Fault.
    /// </summary>
    [TestFixture, Category("Modifiers")]
    internal sealed class DestroyedModifierPublishTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestE9vdead";
        private const int PublishFrames = 1500;

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamic() => NeverUnregistered(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBase() => NeverUnregistered(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic() => NeverUnregistered(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamicBuiltIn() => BuiltIn(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamicBuiltIn() => BuiltIn(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);

        private static IEnumerator NeverUnregistered(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldPlayScene.Enter(scene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled(FlowFieldModifierScenarios.StartSeconds);
            FlowFieldManager manager = Manager;
            Bounds bounds = FlowFieldModifierScenarios.WorldBounds(manager);
            FlowFieldProbeModifier probe = FlowFieldModifierScenarios.CreateProbe(
                ProbeName, FlowFieldModifierScenarios.At(bounds, 0.4f, 0.4f), new Vector3(4f, 4f, 4f));
            Assert.That(manager.RegisterVectorModifier(probe), Is.True);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            Assert.That(FlowFieldModifierScenarios.RegisteredCount(manager), Is.EqualTo(1));
            Assert.That(FlowFieldModifierScenarios.SlowedCells(manager), Is.GreaterThan(0), "The probe slowed no cell.");
            int registeredRevision = manager.Revision;

            FlowFieldBuildSettings.BudgetMilliseconds = FlowFieldModifierScenarios.TinyBudgetMilliseconds;
            FlowFieldModifierScenarios.SetGoal(manager, Goal(bounds));
            int goalFrame = Time.frameCount;
            int deadline = goalFrame + FlowFieldModifierScenarios.BuildStartFrames;
            while (!FlowFieldModifierScenarios.BuildRunsAfter(manager, goalFrame))
            {
                FlowFieldModifierScenarios.FailIfPast(deadline, "Never saw an active build");
                yield return null;
            }
            // The component only: its trigger stays usable.
            Object.DestroyImmediate(probe);

            // Each FieldChanged sees the field its pump ended with; the
            // per-frame read with the tiny budget sees every publish.
            var changed = new List<int>();
            System.Action onFieldChanged = () => changed.Add(FlowFieldModifierScenarios.SlowedCells(manager));
            manager.FieldChanged += onFieldChanged;
            var published = new List<int>();
            int lastRevision = registeredRevision;
            int publishDeadline = Time.frameCount + PublishFrames;
            while (true)
            {
                if (manager.Revision != lastRevision)
                {
                    lastRevision = manager.Revision;
                    published.Add(FlowFieldModifierScenarios.SlowedCells(manager));
                }
                if (published.Count > 0 && manager.State == FlowFieldRuntimeState.Ready)
                    break;
                Assert.That(manager.IsFaulted, Is.False, FlowFieldModifierScenarios.State(manager));
                FlowFieldModifierScenarios.FailIfPast(publishDeadline, "No field was published after the destroy");
                yield return null;
            }
            FlowFieldBuildSettings.BudgetMilliseconds = FlowFieldModifierScenarios.DefaultBudgetMilliseconds;
            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            manager.FieldChanged -= onFieldChanged;

            Assert.That(published, Is.All.EqualTo(0), "Slowed cells per publish after the destroy: " + string.Join(",", published));
            Assert.That(changed, Is.All.EqualTo(0), "Slowed cells per FieldChanged after the destroy: " + string.Join(",", changed));
            Assert.That(manager.Revision, Is.EqualTo(registeredRevision + 1), "Expected exactly one publish: " + FlowFieldPlayScene.Describe());
            AssertClean(manager);
            Assert.That(FlowFieldModifierScenarios.SlowedCells(manager), Is.Zero);
        }

        private static IEnumerator BuiltIn(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldPlayScene.Enter(scene, bakeMode);
            yield return FlowFieldPlayScene.WaitUntilSettled(FlowFieldModifierScenarios.StartSeconds);
            FlowFieldManager manager = Manager;
            Bounds bounds = FlowFieldModifierScenarios.WorldBounds(manager);
            FlowFieldSpeedModifier modifier = FlowFieldModifierScenarios.CreateSpeedModifier(
                manager, ProbeName, FlowFieldModifierScenarios.At(bounds, 0.4f, 0.4f), new Vector3(4f, 4f, 4f));
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            Assert.That(FlowFieldModifierScenarios.RegisteredCount(manager), Is.EqualTo(1));
            Assert.That(FlowFieldModifierScenarios.FasterCells(manager), Is.GreaterThan(0), "The Speed Modifier changed no cell.");
            int registeredRevision = manager.Revision;

            FlowFieldBuildSettings.BudgetMilliseconds = FlowFieldModifierScenarios.TinyBudgetMilliseconds;
            FlowFieldModifierScenarios.SetGoal(manager, Goal(bounds));
            int goalFrame = Time.frameCount;
            int deadline = goalFrame + FlowFieldModifierScenarios.BuildStartFrames;
            while (!FlowFieldModifierScenarios.BuildRunsAfter(manager, goalFrame))
            {
                FlowFieldModifierScenarios.FailIfPast(deadline, "Never saw an active build");
                yield return null;
            }
            // OnDisable unregisters it before it goes.
            Object.DestroyImmediate(modifier);
            Assert.That(FlowFieldModifierScenarios.RegisteredCount(manager), Is.Zero);

            int publishDeadline = Time.frameCount + PublishFrames;
            while (manager.Revision == registeredRevision || manager.State != FlowFieldRuntimeState.Ready)
            {
                Assert.That(manager.IsFaulted, Is.False, FlowFieldModifierScenarios.State(manager));
                FlowFieldModifierScenarios.FailIfPast(publishDeadline, "No field was published after the destroy");
                yield return null;
            }
            FlowFieldBuildSettings.BudgetMilliseconds = FlowFieldModifierScenarios.DefaultBudgetMilliseconds;
            yield return FlowFieldPlayScene.WaitUntilSettled(2f);

            AssertClean(manager);
            Assert.That(FlowFieldModifierScenarios.FasterCells(manager), Is.Zero);
        }

        private static Vector3 Goal(Bounds bounds) => FlowFieldModifierScenarios.At(bounds, 0.75f, 0.25f);

        private static void AssertClean(FlowFieldManager manager)
        {
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), FlowFieldModifierScenarios.State(manager));
            Assert.That(manager.IsFaulted, Is.False);
            Assert.That(manager.LastError, Is.Null.Or.Empty);
            Assert.That(FlowFieldModifierScenarios.RegisteredCount(manager), Is.Zero);
        }
    }
}
