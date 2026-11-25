using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// With the sweep on and running every frame, an unregistered Collider the
    /// sweep has taken in at P is moved to Q and registered in the same frame
    /// (kit swreg, SWREG). The sweep reports the spot it last saw when the
    /// Collider becomes registered, so P opens and Q is blocked. The control
    /// only moves it and leaves the sweep to find the move.
    /// </summary>
    [TestFixture, Category("Obstacles")]
    internal sealed class SweepRegisterTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestSweepRegisterProbe";

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamic() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, true);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBase() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, true);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, true);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamicMoveOnly() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBaseMoveOnly() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamicMoveOnly() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic, false);

        private static IEnumerator Run(string scene, FlowFieldBakeMode bakeMode, bool register)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.SetSweep(manager, true, 0f);
            yield return FlowFieldPlayScene.WaitUntilReady();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);

            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickAcross(manager);
            GameObject cube = FlowFieldObstacleProbe.CreateCube(manager, ProbeName, cells.PPosition, cells.CellSize);
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, true, false, "swept in at P");

            // One frame: move to Q, then register (or not, in the control).
            cube.transform.position = cells.QPosition;
            Physics.SyncTransforms();
            if (register)
                Assert.That(manager.RegisterDynamicObstacle(cube.GetComponent<Collider>()), Is.True,
                    "The swept Collider was already registered.");
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, true, register ? "moved and registered" : "moved");
        }
    }
}
