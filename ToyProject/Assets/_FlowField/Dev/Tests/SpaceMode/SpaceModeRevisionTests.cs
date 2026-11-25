using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// E4: Revision counts on across a Space Mode change (Release, the other
    /// mode, Init), so a Manager never shows one Revision for two fields. It
    /// keeps its value when the new session faults (the Volume scene has no
    /// ground for Surface2D) and the first field of each new session is the
    /// next Revision.
    /// </summary>
    [TestFixture, Category("SpaceMode")]
    internal sealed class SpaceModeRevisionTests : FlowFieldPlayTestBase
    {
        private const string NoGround = "No walkable ground collider was found within Bake Bounds.";

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeSceneThroughFault()
        {
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Volume3D));
            int before = manager.Revision;

            FlowFieldSpaceModeScenarios.ExpectBuildFailed(Log, NoGround);
            FlowFieldSpaceModeScenarios.SwitchSpaceMode(manager);
            Assert.That(manager.Revision, Is.EqualTo(before), "Revision restarted with the new session.");
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            yield return FlowFieldSpaceModeScenarios.WaitUntilFaulted(manager);
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Surface2D));
            Assert.That(manager.LastError, Is.EqualTo(NoGround));
            Assert.That(manager.Revision, Is.EqualTo(before));

            FlowFieldSpaceModeScenarios.SwitchSpaceMode(manager);
            Assert.That(manager.Revision, Is.EqualTo(before));
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Volume3D));
            Assert.That(manager.LastError, Is.Null.Or.Empty);
            Assert.That(manager.Revision, Is.EqualTo(before + 1));
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleSceneBothWays()
        {
            yield return FlowFieldSpaceModeScenarios.EnterAndSettle(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Surface2D));
            int before = manager.Revision;

            FlowFieldSpaceModeScenarios.SwitchSpaceMode(manager);
            Assert.That(manager.Revision, Is.EqualTo(before), "Revision restarted with the new session.");
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Volume3D));
            Assert.That(manager.Revision, Is.EqualTo(before + 1));

            FlowFieldSpaceModeScenarios.SwitchSpaceMode(manager);
            Assert.That(manager.Revision, Is.EqualTo(before + 1), "Revision restarted with the new session.");
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building));
            yield return FlowFieldPlayScene.WaitUntilSettled(2f);
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Surface2D));
            Assert.That(manager.Revision, Is.EqualTo(before + 2));
        }
    }
}
