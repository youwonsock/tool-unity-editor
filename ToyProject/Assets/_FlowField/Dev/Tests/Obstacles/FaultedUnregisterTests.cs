using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A registered obstacle at P is moved to Q and unregistered while the
    /// Manager is faulted, then RequestRebuild retries (kit vfault, VFAULT).
    /// The retry must block Q and open P as the unfaulted path does: the
    /// builder records where a faulted change reached, so a BakedBase retry,
    /// which reloads the bake, queries it again.
    /// </summary>
    [TestFixture, Category("Obstacles")]
    internal sealed class FaultedUnregisterTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestFaultedUnregisterProbe";

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBase() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakedBase() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamic() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);

        private IEnumerator Run(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickRow(manager, 1, 11, 2);
            GameObject cube = FlowFieldObstacleProbe.CreateRegisteredCube(manager, ProbeName, cells.PPosition, cells.CellSize);
            Collider collider = cube.GetComponent<Collider>();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, true, false, "registered at P");

            FlowFieldObstacleProbe.Fault(manager, Log);
            yield return null;

            cube.transform.position = cells.QPosition;
            Physics.SyncTransforms();
            Assert.That(manager.UnregisterDynamicObstacle(collider), Is.True, "The faulted Manager refused the unregister.");
            yield return null;

            Assert.DoesNotThrow(manager.RequestRebuild, "The retry threw.");
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, true, "after the retry");
        }
    }
}
