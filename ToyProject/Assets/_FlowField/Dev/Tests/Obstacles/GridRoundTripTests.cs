using System;
using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// Grid round trips G1, G2, G1 with obstacle input between. A registered
    /// obstacle that moves from P to Q outside the temporary grid G2 must end
    /// with P open and Q blocked on G1: a new grid drops the published field
    /// for good, so the old mask never shows again (kit gr, with the control
    /// that only moves). When G2 publishes before the move, the move outside
    /// it marks nothing and the return to G1 publishes once (kit gr2). New
    /// obstacle settings on the return to G1 keep the old field from reading
    /// as safe (kit f5, F5).
    /// </summary>
    [TestFixture, Category("Obstacles")]
    internal sealed class GridRoundTripTests : FlowFieldPlayTestBase
    {
        private const string ProbeName = "FlowFieldTestGridRoundTripProbe";

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleShrinkMoveAndReturn() => MoveDuringRoundTrip(FlowFieldPlayScene.SampleScene, true);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeShrinkMoveAndReturn() => MoveDuringRoundTrip(FlowFieldPlayScene.VolumeScene, true);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleMoveOnly() => MoveDuringRoundTrip(FlowFieldPlayScene.SampleScene, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeMoveOnly() => MoveDuringRoundTrip(FlowFieldPlayScene.VolumeScene, false);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleMoveWhileShrunkGridPublished() => MoveOnPublishedShrunkGrid(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeMoveWhileShrunkGridPublished() => MoveOnPublishedShrunkGrid(FlowFieldPlayScene.VolumeScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator SampleWiderGridThenNewClearance() => NewObstacleSettingsOnReturn(FlowFieldPlayScene.SampleScene);

        [UnityTest, Timeout(300000)]
        public IEnumerator VolumeWiderGridThenNewClearance() => NewObstacleSettingsOnReturn(FlowFieldPlayScene.VolumeScene);

        // kit gr: one frame shrinks to G2, moves P to Q outside it and returns to G1.
        private static IEnumerator MoveDuringRoundTrip(string scene, bool shrink)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickRow(manager, 1, 3, 2);
            GameObject cube = FlowFieldObstacleProbe.CreateRegisteredCube(manager, ProbeName, cells.PPosition, cells.CellSize);
            Collider collider = cube.GetComponent<Collider>();
            yield return FlowFieldPlayScene.WaitUntilReady();
            FlowFieldObstacleProbe.AssertSettled(manager, cells, true, false, "registered at P");

            Bounds g1 = FlowFieldObstacleProbe.BakeBounds(manager);
            Bounds g2 = ShrunkBounds(manager, g1, out _);
            if (shrink)
                FlowFieldObstacleProbe.SetBakeBounds(manager, g2);
            cube.transform.position = cells.QPosition;
            Physics.SyncTransforms();
            Assert.That(manager.NotifyDynamicObstacleMoved(collider), Is.True, "The probe was not registered.");
            int uncertainAfterMove = FlowFieldObstacleProbe.UncertainRegions(manager);
            if (shrink)
            {
                FlowFieldObstacleProbe.SetBakeBounds(manager, g1);
                Assert.That(uncertainAfterMove, Is.Zero, "A move that reaches no cell of G2 marked a region.");
                Assert.That(manager.TryGetPublishedField(out _), Is.False,
                    "The field published on the earlier G1 showed again: " + FlowFieldPlayScene.Describe());
            }
            else
            {
                Assert.That(uncertainAfterMove, Is.GreaterThan(0), "The move marked no uncertain region.");
                FlowFieldObstacleProbe.AssertCells(manager, cells, true, false, "before the move is built");
            }

            yield return FlowFieldPlayScene.WaitUntilReady();
            yield return FlowFieldObstacleProbe.PauseThenReady(1f);
            FlowFieldObstacleProbe.AssertSettled(manager, cells, false, true, "settled on G1");
        }

        // kit gr2: G2 publishes, the obstacle moves outside G2, then the grid returns to G1.
        private static IEnumerator MoveOnPublishedShrunkGrid(string scene)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldObstacleProbe.Cells cells = FlowFieldObstacleProbe.PickRow(manager, 1, 3, 2);
            GameObject cube = FlowFieldObstacleProbe.CreateRegisteredCube(manager, ProbeName, cells.PPosition, cells.CellSize);
            Collider collider = cube.GetComponent<Collider>();
            yield return FlowFieldPlayScene.WaitUntilReady();

            int fieldChanges = 0;
            Action countFieldChange = () => fieldChanges++;
            manager.FieldChanged += countFieldChange;
            try
            {
                Bounds g1 = FlowFieldObstacleProbe.BakeBounds(manager);
                int width = FlowFieldObstacleProbe.Published(manager).Grid.Width;
                Bounds g2 = ShrunkBounds(manager, g1, out int removedColumns);
                FlowFieldObstacleProbe.SetBakeBounds(manager, g2);
                Assert.That(manager.TryGetPublishedField(out _), Is.False,
                    "The G1 field still showed on G2: " + FlowFieldPlayScene.Describe());
                yield return FlowFieldPlayScene.WaitUntilReady();
                yield return FlowFieldObstacleProbe.PauseThenReady(1f);
                Assert.That(fieldChanges, Is.EqualTo(1), "FieldChanged events for the G2 field.");
                Assert.That(FlowFieldObstacleProbe.Published(manager).Grid.Width, Is.EqualTo(width - removedColumns),
                    "Width of the published G2 field.");

                cube.transform.position = cells.QPosition;
                Physics.SyncTransforms();
                Assert.That(manager.NotifyDynamicObstacleMoved(collider), Is.True, "The probe was not registered.");
                Assert.That(FlowFieldObstacleProbe.UncertainRegions(manager), Is.Zero, "A move outside G2 marked a region.");
                Assert.That(manager.State, Is.EqualTo(FlowFieldRuntimeState.Ready), "A move outside G2 started a build.");
                int changesBeforeReturn = fieldChanges;
                FlowFieldObstacleProbe.SetBakeBounds(manager, g1);
                Assert.That(manager.TryGetPublishedField(out _), Is.False,
                    "The G2 field still showed on G1: " + FlowFieldPlayScene.Describe());

                yield return FlowFieldPlayScene.WaitUntilReady();
                yield return FlowFieldObstacleProbe.PauseThenReady(1f);
                FlowFieldObstacleProbe.AssertSettled(manager, cells, false, true, "settled on G1");
                Assert.That(fieldChanges - changesBeforeReturn, Is.EqualTo(1), "FieldChanged events since the return to G1.");
            }
            finally
            {
                manager.FieldChanged -= countFieldChange;
            }
        }

        // kit f5: one frame widens to G2, then returns to G1 with a larger Clearance.
        private static IEnumerator NewObstacleSettingsOnReturn(string scene)
        {
            yield return FlowFieldObstacleProbe.Enter(scene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldField field = FlowFieldObstacleProbe.Published(manager);
            int cell = FlowFieldObstacleProbe.PickOpenCell(field, field.Agent.Blocked.Length / 2, fullSpeed: true);
            Vector3 position = field.Grid.CellCenter(cell);
            Assert.That(manager.TrySample(position, out FlowFieldSample before), Is.True, "No sample before the edits.");
            Assert.That(before.SpeedMultiplier, Is.GreaterThan(0f), "The probe cell does not move before the edits.");

            FlowFieldManagerConfig config = manager.CaptureConfig();
            Bounds g1 = config.Grid.BakeBoundsLocal;
            float cellSize = config.Grid.CellSize;
            FlowFieldObstacleProbe.SetBakeBounds(manager, new Bounds(
                g1.center + new Vector3(cellSize, 0f, 0f), g1.size + new Vector3(2f * cellSize, 0f, 0f)));
            var serialized = new SerializedObject(manager);
            serialized.FindProperty(FlowFieldManager.PropertyNames.BakeBoundsLocal).boundsValue = g1;
            serialized.FindProperty(FlowFieldObstacleProbe.ClearanceProperty).floatValue = config.Obstacles.Clearance + 0.5f;
            serialized.ApplyModifiedProperties();
            bool readable = manager.TrySample(position, out FlowFieldSample after);
            Assert.That(readable, Is.False,
                "The earlier G1 field read with the new Clearance, speed " + after.SpeedMultiplier + ": " + FlowFieldPlayScene.Describe());

            yield return FlowFieldPlayScene.WaitUntilReady();
            Assert.That(FlowFieldObstacleProbe.UncertainRegions(manager), Is.Zero, "Uncertain regions after the publish.");
            Assert.That(manager.TrySample(position, out _), Is.True, "No sample after the publish: " + FlowFieldPlayScene.Describe());
        }

        // G2: G1 without its first columns, at least 6 and half the width.
        private static Bounds ShrunkBounds(FlowFieldManager manager, Bounds g1, out int removedColumns)
        {
            int width = FlowFieldObstacleProbe.Published(manager).Grid.Width;
            float cellSize = manager.CaptureConfig().Grid.CellSize;
            removedColumns = Math.Max(6, width / 2);
            return new Bounds(
                g1.center + new Vector3(removedColumns * cellSize * 0.5f, 0f, 0f),
                g1.size - new Vector3(removedColumns * cellSize, 0f, 0f));
        }
    }
}
