using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A FlowFieldDynamicObstacle whose Collider alone is Destroy()ed ends its
    /// registration, so the cells it blocked open with no other input (kit
    /// e9dyn, B1); the control destroys the whole GameObject. A region notice
    /// elsewhere afterwards changes nothing.
    /// </summary>
    [TestFixture, Category("Obstacles")]
    internal sealed class DestroyedColliderTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestDestroyedColliderProbe";

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakedBase() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamic() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBase() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamicWholeGameObject() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, true);

        private static IEnumerator Run(string scene, FlowFieldBakeMode bakeMode, bool wholeGameObject)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickFarRow(manager);
            GameObject cube = FlowFieldObstacleProbe.CreateDynamicObstacle(
                manager, ProbeName, cells.PPosition, FlowFieldObstacleProbe.CubeScale(manager, cells.CellSize));
            Collider collider = cube.GetComponent<Collider>();
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            Assert.That(manager.IsDynamicObstacleRegistered(collider), Is.True,
                "The FlowFieldDynamicObstacle did not register: " + FlowFieldPlayScene.Describe());
            FlowFieldObstacleProbe.AssertSettled(manager, cells, true, false, "registered at P");
            int registered = manager.Session.Obstacles.Count;

            if (wholeGameObject)
                Object.Destroy(cube);
            else
                Object.Destroy(collider);
            yield return FlowFieldObstacleProbe.PauseThenReady(3f);
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            Assert.That(collider == null, Is.True, "The Collider was not destroyed.");
            Assert.That(manager.IsDynamicObstacleRegistered(collider), Is.False, "The destroyed Collider is still registered.");
            Assert.That(manager.Session.Obstacles.Count, Is.EqualTo(registered - 1), "Registered obstacles after the destroy.");
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, false, "after the destroy");

            int revision = manager.Revision;
            manager.NotifyObstacleRegionDirty(new Bounds(cells.QPosition, Vector3.one * cells.CellSize * 0.5f));
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, false, "after a region notice at Q");
            Assert.That(manager.Revision, Is.EqualTo(revision),
                "A region notice at Q changed the field, so the destroy had left something behind: " + FlowFieldPlayScene.Describe());
        }
    }
}
