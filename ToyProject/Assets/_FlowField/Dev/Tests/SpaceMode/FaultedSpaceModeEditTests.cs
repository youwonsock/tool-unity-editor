using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// P2: a Space Mode edited, as the Inspector allows, while the Manager is
    /// faulted. The retry refuses it before anything changes, so the Fault
    /// keeps its cause and the session its mode; editing it back lets the
    /// retry recover.
    /// </summary>
    [TestFixture, Category("SpaceMode")]
    internal sealed class FaultedSpaceModeEditTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic()
        {
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            const string faulted = "state=Faulted init=False faulted=True space=Surface2D err=" + FlowFieldSpaceModeScenarios.TestFault;

            FlowFieldSpaceModeScenarios.ExpectBuildFailed(Log, FlowFieldSpaceModeScenarios.TestFault);
            manager.Session.ReportFault(new InvalidOperationException(FlowFieldSpaceModeScenarios.TestFault));
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo(faulted));

            FlowFieldSpaceModeScenarios.EditInt(manager, FlowFieldManager.PropertyNames.SpaceMode, (int)FlowFieldSpaceMode.Volume3D);
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo(faulted), "The edit changed the faulted session.");
            yield return FlowFieldPlayScene.WaitSeconds(1f);

            var thrown = Assert.Throws<InvalidOperationException>(() => manager.RequestRebuild());
            Assert.That(thrown.Message, Is.EqualTo(FlowFieldSpaceModeScenarios.SpaceModeLocked));
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo(faulted), "The refused retry changed the session.");
            yield return FlowFieldPlayScene.WaitSeconds(2f);
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo(faulted));

            FlowFieldSpaceModeScenarios.EditInt(manager, FlowFieldManager.PropertyNames.SpaceMode, (int)FlowFieldSpaceMode.Surface2D);
            Assert.DoesNotThrow(() => manager.RequestRebuild());
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo("state=Building init=True faulted=False space=Surface2D err="));
            yield return FlowFieldPlayScene.WaitUntilSettled(1f);
            Assert.That(FlowFieldSpaceModeScenarios.Report(manager), Is.EqualTo("state=Ready init=True faulted=False space=Surface2D err="));
        }
    }
}
