using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// A FlowFieldDynamicObstacle whose GameObject is destroyed or disabled
    /// unregisters it, and in that frame the one uncertain region is its own
    /// spot grown by the obstacle reach (cell size and Clearance), nothing
    /// elsewhere such as an empty box at the origin (kit sfd).
    /// </summary>
    [TestFixture, Category("Obstacles")]
    internal sealed class UnregisterRegionTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestUnregisterRegionProbe";

        [UnityTest, Timeout(300000)]
        public IEnumerator DestroyedGameObject() => Run(disable: false);

        [UnityTest, Timeout(300000)]
        public IEnumerator DisabledGameObject() => Run(disable: true);

        private static IEnumerator Run(bool disable)
        {
            yield return FlowFieldObstacleProbe.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Bounds world = FlowFieldObstacleProbe.WorldBounds(manager);
            var position = new Vector3(
                Mathf.Lerp(world.min.x, world.max.x, 0.8f),
                world.center.y,
                Mathf.Lerp(world.min.z, world.max.z, 0.8f));
            GameObject cube = FlowFieldObstacleProbe.CreateDynamicObstacle(manager, ProbeName, position, null);
            Collider collider = cube.GetComponent<Collider>();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            Assert.That(manager.IsDynamicObstacleRegistered(collider), Is.True,
                "The FlowFieldDynamicObstacle did not register: " + FlowFieldPlayScene.Describe());
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), FlowFieldPlayScene.Describe());
            Assert.That(FlowFieldObstacleProbe.UncertainRegions(manager), Is.Zero, "Uncertain regions before the removal.");

            FlowFieldGridSpace grid = FlowFieldObstacleProbe.Published(manager).Grid;
            Physics.SyncTransforms();
            Bounds ownSpot = FlowFieldObstacleReach.Expand(
                collider.bounds, grid.CellSize, manager.CaptureConfig().Obstacles.Clearance);
            string what = disable ? "disabled" : "destroyed";
            if (disable)
                cube.SetActive(false);
            else
                Object.DestroyImmediate(cube);

            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Building), what + ": " + FlowFieldPlayScene.Describe());
            Assert.That(FlowFieldObstacleProbe.UncertainRegions(manager), Is.EqualTo(1), what + ": uncertain regions.");
            FlowFieldObstacleProbe.AssertUncertainCellsAre(manager, grid, ownSpot, what);
        }
    }
}
