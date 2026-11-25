using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A registered Collider without a Rigidbody, unregistered and Destroy()ed
    /// in the same frame with the sweep off, leaves no ghost block at P (kit
    /// gh, GH): the build that starts in that frame still scans it as a static
    /// obstacle, so the session reports its spot again once it has left the
    /// scene. The same holds when the Manager is faulted and retried in that
    /// frame (kit ghf) and when it is disabled and enabled around it (kit ghs,
    /// GHSUSP). A RequestRebuild afterwards finds nothing left to change.
    /// </summary>
    [TestFixture, Category("Obstacles")]
    internal sealed class UnregisterDestroyTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestGhostProbe";

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic() => SameFrame(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakedBase() => SameFrame(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamic() => SameFrame(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamicFaultedRetry()
        {
            yield return FlowFieldObstacleProbe.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickRow(manager, 1, 11, 2);
            GameObject cube = FlowFieldObstacleProbe.CreateRegisteredCube(manager, ProbeName, cells.PPosition, cells.CellSize);
            Collider collider = cube.GetComponent<Collider>();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, true, false, "registered at P");

            FlowFieldObstacleProbe.Fault(manager, Log);
            yield return null;

            // One frame, while faulted: unregister, Destroy and retry.
            Assert.That(manager.UnregisterDynamicObstacle(collider), Is.True, "The faulted Manager refused the unregister.");
            Object.Destroy(cube);
            Assert.DoesNotThrow(manager.RequestRebuild, "The retry threw.");
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, false, "after the retry");
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamicSuspended() => Suspended(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamicSuspended() => Suspended(FlowFieldPlayScene.VolumeScene);

        private static IEnumerator SameFrame(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickRow(manager, 1, 11, 2);
            GameObject cube = FlowFieldObstacleProbe.CreateRegisteredCube(manager, ProbeName, cells.PPosition, cells.CellSize);
            Collider collider = cube.GetComponent<Collider>();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, true, false, "registered at P");

            // One frame: unregister, then Destroy.
            Assert.That(manager.UnregisterDynamicObstacle(collider), Is.True, "The probe was not registered.");
            Object.Destroy(cube);
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, false, "after unregister and Destroy");
            yield return AssertRebuildChangesNothing(manager, cells);
        }

        private static IEnumerator Suspended(string scene)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickRow(manager, 1, 11, 2);
            GameObject cube = FlowFieldObstacleProbe.CreateRegisteredCube(manager, ProbeName, cells.PPosition, cells.CellSize);
            Collider collider = cube.GetComponent<Collider>();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, true, false, "registered at P");

            // One frame: disable, unregister, Destroy, enable.
            manager.enabled = false;
            bool unregistered = manager.UnregisterDynamicObstacle(collider);
            Object.Destroy(cube);
            manager.enabled = true;
            Assert.That(unregistered, Is.True, "The disabled Manager refused the unregister.");
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building), FlowFieldPlayScene.Describe());
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, false, "after the toggle, unregister and Destroy");
            yield return AssertRebuildChangesNothing(manager, cells);
        }

        private static IEnumerator AssertRebuildChangesNothing(FlowFieldManager manager, FlowFieldObstacleProbe.Cells cells)
        {
            int revision = manager.Revision;
            manager.RequestRebuild();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, false, "after RequestRebuild");
            Assert.That(manager.Revision, Is.EqualTo(revision),
                "RequestRebuild changed the field, so a ghost block had stayed: " + FlowFieldPlayScene.Describe());
        }
    }
}
