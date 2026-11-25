using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// M5: a Modifier Priority edited while the Manager is faulted sends no
    /// notification, so the retry must read it again. RequestRebuild then
    /// uses the new order, refuses a duplicated Priority with a Fault naming
    /// it, and recovers once the Priority is unique again.
    /// </summary>
    [TestFixture, Category("Modifiers")]
    internal sealed class FaultedPriorityEditTests : FlowFieldPlayTestBase
    {
        private const string Prefix = "FlowFieldTestM5";
        private const string TestFault = "Test fault.";

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic() => Run(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamic() => Run(FlowFieldPlayScene.VolumeScene);

        private IEnumerator Run(string scene)
        {
            yield return FlowFieldPlayScene.Enter(scene, FlowFieldBakeMode.RuntimeDynamic);
            yield return FlowFieldPlayScene.WaitUntilSettled(FlowFieldModifierScenarios.StartSeconds);
            FlowFieldManager manager = Manager;
            Vector3 spot = FlowFieldModifierScenarios.WorldBounds(manager).center + new Vector3(10f, 0f, -20f);
            var position = new Vector3(spot.x, 1f, spot.z);
            var size = new Vector3(4f, 2f, 4f);
            FlowFieldSpeedModifier a = FlowFieldModifierScenarios.CreateSpeedModifier(manager, Prefix + "A", position, size, priority: 3001);
            FlowFieldSpeedModifier b = FlowFieldModifierScenarios.CreateSpeedModifier(manager, Prefix + "B", position, size, priority: 3002);
            Assert.That(Order(manager), Is.EqualTo("A:3001 B:3002"));
            yield return FlowFieldPlayScene.WaitUntilSettled(0.5f);

            // Faulted, A moves past B: nothing hears the edit until the retry.
            FlowFieldModifierScenarios.ExpectBuildFailed(Log, TestFault);
            manager.Session.ReportFault(new InvalidOperationException(TestFault));
            SetPriority(a, 3005);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Faulted));
            Assert.That(manager.LastError, Is.EqualTo(TestFault));
            Assert.That(Order(manager), Is.EqualTo("A:3001 B:3002"));
            manager.RequestRebuild();
            Assert.That(Order(manager), Is.EqualTo("B:3002 A:3005"), "The retry did not read the Priority edited while faulted.");
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            yield return FlowFieldPlayScene.WaitUntilSettled(0.5f);

            // Faulted, A takes B's Priority: the retry faults on the duplicate.
            const string duplicated = "Vector modifier priority 3002 is duplicated.";
            FlowFieldModifierScenarios.ExpectBuildFailed(Log, TestFault);
            manager.Session.ReportFault(new InvalidOperationException(TestFault));
            SetPriority(a, 3002);
            FlowFieldModifierScenarios.ExpectBuildFailed(Log, duplicated);
            var thrown = Assert.Throws<InvalidOperationException>(() => manager.RequestRebuild());
            Assert.That(thrown.Message, Is.EqualTo(duplicated));
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Faulted));
            Assert.That(manager.LastError, Is.EqualTo(duplicated));
            Assert.That(FlowFieldModifierScenarios.Entries(manager, Prefix), Is.EquivalentTo(new[] { "A:3002", "B:3002" }));
            yield return FlowFieldPlayScene.WaitSeconds(1f);

            SetPriority(a, 3005);
            Assert.DoesNotThrow(() => manager.RequestRebuild());
            Assert.That(Order(manager), Is.EqualTo("B:3002 A:3005"));
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            yield return FlowFieldPlayScene.WaitUntilSettled(0.5f);
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), FlowFieldModifierScenarios.State(manager));
            Assert.That(manager.LastError, Is.Null.Or.Empty);
            Assert.That(Order(manager), Is.EqualTo("B:3002 A:3005"));
            Assert.That(b.Priority, Is.EqualTo(3002));
        }

        private static string Order(FlowFieldManager manager)
            => string.Join(" ", FlowFieldModifierScenarios.Entries(manager, Prefix));

        // As the Inspector edits it: the volume's OnValidate decides what to report.
        private static void SetPriority(FlowFieldSpeedModifier modifier, int priority)
        {
            var serialized = new SerializedObject(modifier);
            serialized.FindProperty("_priority").intValue = priority;
            serialized.ApplyModifiedProperties();
        }
    }
}
