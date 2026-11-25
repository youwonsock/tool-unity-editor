using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A registered obstacle teleported ten cells and unregistered in the same
    /// frame (kit sf1, SF1). The unregister also reports where a Collider still
    /// in the scene is now, so it blocks its new cell Q at once as an
    /// unregistered obstacle and P opens; a RequestRebuild afterwards finds
    /// nothing left to change.
    /// </summary>
    [TestFixture, Category("Obstacles")]
    internal sealed class TeleportUnregisterTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestTeleportProbe";

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakedBase() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamic() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);

        private static IEnumerator Run(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickRow(manager, 1, 11, 2);
            GameObject cube = FlowFieldObstacleProbe.CreateRegisteredCube(manager, ProbeName, cells.PPosition, cells.CellSize);
            Collider collider = cube.GetComponent<Collider>();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, true, false, "registered at P");

            // One frame: teleport to Q, then unregister.
            cube.transform.position = cells.QPosition;
            Physics.SyncTransforms();
            Assert.That(manager.UnregisterDynamicObstacle(collider), Is.True, "The probe was not registered.");
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, true, "moved to Q and unregistered");

            int revision = manager.Revision;
            manager.RequestRebuild();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, true, "after RequestRebuild");
            Assert.That(manager.Revision, Is.EqualTo(revision),
                "RequestRebuild changed the field, so the unregister had missed the new spot: " + FlowFieldPlayScene.Describe());
        }
    }
}
