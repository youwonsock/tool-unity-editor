using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// MDEAD: a custom Modifier destroyed, trigger and all, without
    /// UnregisterVectorModifier. Nothing faults: the next build with work
    /// prunes it and drops its effect, RequestRebuild and a late Unregister
    /// do no harm, and Release and Init start clean. In the late case its
    /// caller unregisters it before any build prunes it, and the registry
    /// still finds it by its reference.
    /// </summary>
    [TestFixture, Category("Modifiers")]
    internal sealed class DeadModifierTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestMdead";

        [UnityTest, Timeout(300000)]
        public IEnumerator DestroyedWithoutUnregister()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            yield return FlowFieldPlayScene.WaitUntilSettled(FlowFieldModifierScenarios.StartSeconds);
            FlowFieldManager manager = Manager;
            Bounds bounds = FlowFieldModifierScenarios.WorldBounds(manager);
            FlowFieldProbeModifier probe = Register(manager, bounds);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            int registeredRevision = AssertRegistered(manager);

            Object.Destroy(probe.gameObject);
            yield return FlowFieldPlayScene.WaitSeconds(1.5f);
            // Nothing was owed, so no build ran and nothing changed.
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), FlowFieldModifierScenarios.State(manager));
            Assert.That(manager.Revision, Is.EqualTo(registeredRevision));

            Assert.DoesNotThrow(() => FlowFieldModifierScenarios.SetGoal(manager, FlowFieldModifierScenarios.At(bounds, 0.7f, 0.7f)));
            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            AssertClean(manager);
            Assert.That(manager.Revision, Is.EqualTo(registeredRevision + 1));

            Assert.DoesNotThrow(() => manager.RequestRebuild());
            yield return FlowFieldPlayScene.WaitUntilSettled(1.5f);
            AssertClean(manager);
            Assert.That(manager.Revision, Is.EqualTo(registeredRevision + 1), "A rebuild of the same input published a new Revision.");

            bool unregistered = true;
            Assert.DoesNotThrow(() => unregistered = manager.UnregisterVectorModifier(probe));
            Assert.That(unregistered, Is.False, "The pruned Modifier was still registered.");
            Assert.DoesNotThrow(() => manager.RequestRebuild());
            yield return FlowFieldPlayScene.WaitUntilSettled(1.5f);
            AssertClean(manager);
            Assert.That(manager.Revision, Is.EqualTo(registeredRevision + 1));

            manager.Release();
            manager.Init();
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            AssertClean(manager);
            Assert.That(manager.Revision, Is.GreaterThan(registeredRevision + 1));
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator UnregisteredLateAfterDestroy()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            yield return FlowFieldPlayScene.WaitUntilSettled(FlowFieldModifierScenarios.StartSeconds);
            FlowFieldManager manager = Manager;
            Bounds bounds = FlowFieldModifierScenarios.WorldBounds(manager);
            FlowFieldProbeModifier probe = Register(manager, bounds);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            int registeredRevision = AssertRegistered(manager);

            Object.Destroy(probe.gameObject);
            yield return FlowFieldPlayScene.WaitSeconds(1f);
            Assert.That(FlowFieldModifierScenarios.RegisteredCount(manager), Is.EqualTo(1),
                "A build pruned the Modifier before its caller unregistered it.");

            Assert.That(manager.UnregisterVectorModifier(probe), Is.True, "The destroyed Modifier was not found by its reference.");
            Assert.That(FlowFieldModifierScenarios.RegisteredCount(manager), Is.Zero);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            AssertClean(manager);
            Assert.That(manager.Revision, Is.EqualTo(registeredRevision + 1));

            Assert.DoesNotThrow(() => FlowFieldModifierScenarios.SetGoal(manager, FlowFieldModifierScenarios.At(bounds, 0.7f, 0.7f)));
            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            AssertClean(manager);
            Assert.That(manager.Revision, Is.EqualTo(registeredRevision + 2));
        }

        // The probe's box spans the bounds' height, at 40% across X and Z.
        private static FlowFieldProbeModifier Register(FlowFieldManager manager, Bounds bounds)
        {
            FlowFieldProbeModifier probe = FlowFieldModifierScenarios.CreateProbe(
                ProbeName,
                FlowFieldModifierScenarios.At(bounds, 0.4f, 0.4f),
                new Vector3(4f, bounds.size.y, 4f));
            Assert.That(manager.RegisterVectorModifier(probe), Is.True);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            return probe;
        }

        private static int AssertRegistered(FlowFieldManager manager)
        {
            Assert.That(manager.IsFaulted, Is.False, FlowFieldModifierScenarios.State(manager));
            Assert.That(FlowFieldModifierScenarios.RegisteredCount(manager), Is.EqualTo(1));
            Assert.That(FlowFieldModifierScenarios.SlowedCells(manager), Is.GreaterThan(0), "The probe slowed no cell.");
            return manager.Revision;
        }

        private static void AssertClean(FlowFieldManager manager)
        {
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), FlowFieldModifierScenarios.State(manager));
            Assert.That(manager.IsFaulted, Is.False);
            Assert.That(manager.LastError, Is.Null.Or.Empty);
            Assert.That(FlowFieldModifierScenarios.RegisteredCount(manager), Is.Zero);
            Assert.That(FlowFieldModifierScenarios.SlowedCells(manager), Is.Zero);
        }
    }
}
