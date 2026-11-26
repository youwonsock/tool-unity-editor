using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Turning the unregistered sweep on brings in an unregistered Collider
    /// that appeared while it was off (kit v6, V6). In a Volume3D BakedBase
    /// field, turning it on and then off each starts a build that queries
    /// every cell, so the Collider stays in the field; what a later
    /// RequestRebuild keeps is recorded, because it reloads the bake and
    /// queries only what changed since Init (kit vso, VSO, the BakedBase
    /// limit TechnicalNotes describes for unregistered obstacles).
    /// </summary>
    [TestFixture, Category("Obstacles")]
    internal sealed class SweepToggleTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestSweepToggleProbe";
        private const string RecordedAfterRebuild = "P=False Q=False uncertain=0 state=Ready revisionChanged=True";

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeRuntimeDynamic() => SweepOn(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBase() => SweepOn(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleRuntimeDynamic() => SweepOn(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleBakedBase() => SweepOn(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.BakedBase);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeBakedBaseOnOffThenRebuild()
        {
            yield return FlowFieldObstacleProbe.Enter(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.BakedBase);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickAcross(manager);
            FlowFieldObstacleProbe.CreateCube(manager, ProbeName, cells.PPosition, cells.CellSize);
            FlowFieldObstacleProbe.AssertCells(manager, cells, false, false, "placed with the sweep off");
            yield return null;

            FlowFieldObstacleProbe.SetSweep(manager, true);
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertCells(manager, cells, true, false, "sweep on");

            FlowFieldObstacleProbe.SetSweep(manager, false);
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertCells(manager, cells, true, false, "sweep off");

            int revision = manager.Revision;
            manager.RequestRebuild();
            yield return FlowFieldObstacleProbe.PauseThenReady(2f);
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldRecorded.AreEqual(RecordedAfterRebuild,
                FlowFieldObstacleProbe.Report(manager, cells) + " revisionChanged=" + (manager.Revision != revision),
                "vso after RequestRebuild");
        }

        private static IEnumerator SweepOn(string scene, FlowFieldBakeMode bakeMode)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, bakeMode);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.SetSweep(manager, false);
            yield return FlowFieldPlayScene.WaitUntilReady();
            yield return FlowFieldObstacleProbe.PauseThenReady(0.5f);

            FlowFieldField field = FlowFieldObstacleProbe.Published(manager);
            int cell = FlowFieldObstacleProbe.PickOpenCell(field, field.Agent.Blocked.Length / 3, fullSpeed: false);
            FlowFieldObstacleProbe.CreateCube(
                manager, ProbeName, FlowFieldObstacleProbe.Position(manager, field.Grid, cell), field.Grid.CellSize);
            FlowFieldObstacleProbe.AssertBlocked(manager, cell, false, "placed with the sweep off");
            yield return FlowFieldObstacleProbe.PauseThenReady(1.5f);
            FlowFieldObstacleProbe.AssertBlocked(manager, cell, false, "later, with the sweep still off");

            FlowFieldObstacleProbe.SetSweep(manager, true);
            yield return FlowFieldPlayScene.WaitUntilReady();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertBlocked(manager, cell, true, "sweep on");
            Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), FlowFieldPlayScene.Describe());
            Assert.That(FlowFieldObstacleProbe.UncertainRegions(manager), Is.Zero, "Uncertain regions after the sweep settled.");
        }
    }
}
