using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// SMD: a serialized Space Mode edit while the Manager is disabled waits
    /// for Release, as it does while the Manager runs: enabling it builds on
    /// in the session's mode, without a Fault. A Bake Mode edit made with it
    /// still faults at once, keeps the Fault through enabling, and a retry
    /// after editing both back recovers.
    /// </summary>
    [TestFixture, Category("SpaceMode")]
    internal sealed class DisabledSpaceModeEditTests : FlowFieldPlayTestBase
    {
        private const string BakeModeLocked = "FlowField Bake Mode can only change after Release and before Init.";

        [UnityTest, Timeout(300000)]
        public IEnumerator SpaceModeOnly()
        {
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            manager.enabled = false;
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo("state=Suspended init=True faulted=False space=Surface2D err="));
            yield return FlowFieldPlayScene.WaitSeconds(0.5f);

            int oldSpace = FlowFieldSpaceModeScenarios.SerializedInt(manager, FlowFieldManager.PropertyNames.SpaceMode);
            FlowFieldSpaceModeScenarios.EditInt(manager, FlowFieldManager.PropertyNames.SpaceMode, FlowFieldSpaceModeScenarios.OtherSpaceMode(oldSpace));
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo("state=Suspended init=True faulted=False space=Surface2D err="));

            Assert.DoesNotThrow(() => manager.enabled = true);
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo("state=Building init=True faulted=False space=Surface2D err="));
            yield return FlowFieldPlayScene.WaitSeconds(1.5f);
            yield return FlowFieldPlayScene.WaitUntilReady();
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo("state=Ready init=True faulted=False space=Surface2D err="),
                "A running session must ignore the edit until Release.");

            FlowFieldSpaceModeScenarios.EditInt(manager, FlowFieldManager.PropertyNames.SpaceMode, oldSpace);
            Assert.That(manager.IsFaulted, Is.False);
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo("state=Ready init=True faulted=False space=Surface2D err="));
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator SpaceModeAndBakeMode()
        {
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            manager.enabled = false;
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo("state=Suspended init=True faulted=False space=Surface2D err="));
            yield return FlowFieldPlayScene.WaitSeconds(0.5f);

            const string faulted = "state=Faulted init=False faulted=True space=Surface2D err=" + BakeModeLocked;
            int oldSpace = FlowFieldSpaceModeScenarios.SerializedInt(manager, FlowFieldManager.PropertyNames.SpaceMode);
            int oldBake = FlowFieldSpaceModeScenarios.SerializedInt(manager, FlowFieldManager.PropertyNames.BakeMode);
            FlowFieldSpaceModeScenarios.ExpectBuildFailed(Log, BakeModeLocked);
            EditModes(manager, FlowFieldSpaceModeScenarios.OtherSpaceMode(oldSpace), (int)FlowFieldBakeMode.StaticBaked);
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo(faulted));

            Assert.DoesNotThrow(() => manager.enabled = true);
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo(faulted), "Enabling left the Fault.");
            yield return FlowFieldPlayScene.WaitSeconds(1.5f);
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo(faulted));

            EditModes(manager, oldSpace, oldBake);
            Assert.DoesNotThrow(() => manager.RequestRebuild());
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo("state=Building init=True faulted=False space=Surface2D err="));
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo("state=Ready init=True faulted=False space=Surface2D err="));
        }

        // Both in one Inspector edit.
        private static void EditModes(FlowFieldManager manager, int spaceMode, int bakeMode)
        {
            var serialized = new UnityEditor.SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.SpaceMode).intValue = spaceMode;
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeMode).intValue = bakeMode;
            serialized.ApplyModifiedProperties();
        }
    }
}
