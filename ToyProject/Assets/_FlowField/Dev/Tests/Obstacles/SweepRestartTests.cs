using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// With the sweep on, an unregistered Collider the sweep has taken in at P
    /// moves to Q while the Manager is disabled (kit vrs) or between Release
    /// and Init (kit vrsi). The session that starts again must block Q and
    /// open P, BakedBase included, where it starts from the bake.
    /// </summary>
    [TestFixture, Category("Obstacles")]
    internal sealed class SweepRestartTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestSweepRestartProbe";

        [UnityTest, Timeout(300000)]
        public IEnumerator DisabledVolumeBakedBase() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator DisabledVolumeRuntimeDynamic() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator DisabledSampleBakedBase() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator ReleasedVolumeBakedBase() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase, true);

        [UnityTest, Timeout(300000)]
        public IEnumerator ReleasedVolumeRuntimeDynamic() => Run(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic, true);

        [UnityTest, Timeout(300000)]
        public IEnumerator ReleasedSampleBakedBase() => Run(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase, true);

        private static IEnumerator Run(string scene, FlowFieldBakeMode bakeMode, bool release)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.SetSweep(manager, true);
            yield return FlowFieldPlayScene.WaitUntilReady();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);

            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickAcross(manager);
            GameObject cube = FlowFieldObstacleProbe.CreateCube(manager, ProbeName, cells.PPosition, cells.CellSize);
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, true, false, "swept in at P");

            // One frame: stop, move to Q, start again.
            if (release)
                manager.Release();
            else
                manager.enabled = false;
            cube.transform.position = cells.QPosition;
            Physics.SyncTransforms();
            if (release)
                manager.Init();
            else
                manager.enabled = true;
            yield return FlowFieldPlayScene.WaitUntilReady();
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, true, release ? "after Release, move and Init" : "after disable, move and enable");
        }
    }
}
