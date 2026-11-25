using System.Collections;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// F1WALL: a raised Ground-layer block, 1 x 3 cells and 1.5 m high, on
    /// flat ground, with no Goal and Default Direction 15 degrees off +Z toward
    /// +X, so movers press against the block's -X side. Movers started beside
    /// it slide along it, colliding with the block, and must pass its end: rule
    /// 5 meets a step up like a wall, not a drop (a first version froze all
    /// 50). The split of passed, stopped and other walks is recorded.
    /// </summary>
    [TestFixture, Category("Safety")]
    internal sealed class RaisedBlockSlideTests : FlowFieldPlayTestBase
    {
        private const string BlockName = "FlowFieldTestRaisedBlock";
        private const int AreaWidth = 7;
        private const int AreaDepth = 9;
        private const float BlockHeight = 1.5f;
        private const int WalkSteps = 200;

        private enum Outcome
        {
            Passed,
            Stopped,
            // Off the grid, walled in, or out of steps.
            Other,
        }

        [UnityTest, Timeout(300000)]
        public IEnumerator MoversPassTheBlockEnd()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            var flat = new FlowFieldSafetyScan.SurfaceCells(manager);
            FlowFieldSafetyScan.ClearGoal(manager);
            Assert.That(TryFindFlatArea(flat, out int areaX, out int areaZ), Is.True,
                "The sample scene has no flat " + AreaWidth + " x " + AreaDepth + " area.");
            // The block takes column areaX + 3, rows areaZ + 2 to areaZ + 4.
            int blockX = areaX + 3;
            int blockZ = areaZ + 2;
            AddBlock(manager, flat, blockX, blockZ + 1);
            FlowFieldSafetyScan.SetDefaultDirection(manager, 15f);
            manager.NotifyCellsDirty();
            yield return FlowFieldSafetyScan.Settle();
            Scan(manager, blockX, blockZ);
        }

        private static void Scan(FlowFieldManager manager, int blockX, int blockZ)
        {
            var cells = new FlowFieldSafetyScan.SurfaceCells(manager);
            int blockCell = cells.Index(blockX, blockZ + 1);
            int besideCell = cells.Index(blockX - 1, blockZ + 1);
            Assert.That(cells.IsStepUp(besideCell, blockCell), Is.True,
                "The block is not raised over Max Step Height: height " + cells.Heights[blockCell].ToString("F2")
                + " beside " + cells.Heights[besideCell].ToString("F2"));

            int passed = 0;
            int stopped = 0;
            int other = 0;
            string firstStop = "none";
            for (int row = 0; row < 10; row++)
            for (int column = 0; column < 5; column++)
            {
                // Column blockX - 1, rows blockZ - 1 and blockZ, 0.05 to 0.45 cell toward the block.
                int x = blockX - 1;
                int z = blockZ - 1 + row / 5;
                Vector3 start = cells.Center(cells.Index(x, z)) + new Vector3(
                    (0.05f + 0.1f * column) * cells.CellSize, 0f, ((row % 5) * 0.2f - 0.4f) * cells.CellSize);
                switch (Walk(manager, cells, start, x, z, blockX, blockZ, out string stop))
                {
                    case Outcome.Passed:
                        passed++;
                        break;
                    case Outcome.Stopped:
                        if (stopped == 0)
                            firstStop = stop;
                        stopped++;
                        break;
                    default:
                        other++;
                        break;
                }
            }

            Assert.That(passed, Is.GreaterThan(0), "No mover passed the block's end; first stop " + firstStop);
            FlowFieldRecorded.AreEqual("passed=44 stopped=6 other=0",
                "passed=" + passed + " stopped=" + stopped + " other=" + other,
                "F1WALL RuntimeDynamic: walks");
        }

        // Slides along the samples until the walker is past the block's end
        // (row blockZ + 4), stops, leaves the grid, is walled in or runs out of steps.
        private static Outcome Walk(
            FlowFieldManager manager,
            FlowFieldSafetyScan.SurfaceCells cells,
            Vector3 position,
            int x,
            int z,
            int blockX,
            int blockZ,
            out string stop)
        {
            stop = null;
            for (int step = 0; step < WalkSteps; step++)
            {
                if (z > blockZ + 3)
                    return Outcome.Passed;
                if (!FlowFieldSafetyScan.IsMoving(manager, position, out FlowFieldSample sample))
                {
                    Vector3 local = position - cells.Center(cells.Index(x, z));
                    stop = "cell (" + (x - blockX) + "," + (z - blockZ) + ") from the block start, local ("
                        + local.x.ToString("F2") + "," + local.z.ToString("F2") + ")";
                    return Outcome.Stopped;
                }
                Vector3 direction = sample.Direction;
                direction.y = 0f;
                FlowFieldSafetyScan.SlideResult result = cells.Slide(
                    ref position, ref x, ref z, direction.normalized * FlowFieldSafetyScan.WalkStep, dropsEnd: false);
                if (result != FlowFieldSafetyScan.SlideResult.Moved)
                    return Outcome.Other;
            }
            return Outcome.Other;
        }

        // The first open, level area from cell (2, 2), as the kit searched.
        private static bool TryFindFlatArea(FlowFieldSafetyScan.SurfaceCells cells, out int areaX, out int areaZ)
        {
            for (int z = 2; z < cells.Depth - 10; z++)
            for (int x = 2; x < cells.Width - 10; x++)
            {
                if (IsFlat(cells, x, z))
                {
                    areaX = x;
                    areaZ = z;
                    return true;
                }
            }
            areaX = -1;
            areaZ = -1;
            return false;
        }

        private static bool IsFlat(FlowFieldSafetyScan.SurfaceCells cells, int x, int z)
        {
            float height = cells.Heights[cells.Index(x, z)];
            for (int offsetZ = 0; offsetZ < AreaDepth; offsetZ++)
            for (int offsetX = 0; offsetX < AreaWidth; offsetX++)
            {
                int i = cells.Index(x + offsetX, z + offsetZ);
                if (!cells.IsOpen(i) || Mathf.Abs(cells.Heights[i] - height) > 0.01f)
                    return false;
            }
            return true;
        }

        // A cube on the first Ground layer, centred on the middle cell of the
        // block. It is made in Play Mode, so it goes when Play Mode ends.
        private static void AddBlock(FlowFieldManager manager, FlowFieldSafetyScan.SurfaceCells cells, int x, int z)
        {
            int groundMask = new SerializedObject(manager).FindProperty(FlowFieldManager.PropertyNames.GroundLayer).intValue;
            int layer = 0;
            while (layer < 31 && (groundMask & (1 << layer)) == 0)
                layer++;
            int middle = cells.Index(x, z);
            Vector3 center = cells.Center(middle);
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = BlockName;
            block.layer = layer;
            block.transform.position = new Vector3(center.x, cells.Heights[middle] + 0.5f * BlockHeight, center.z);
            block.transform.localScale = new Vector3(cells.CellSize * 0.999f, BlockHeight, cells.CellSize * 2.999f);
            Physics.SyncTransforms();
        }
    }
}
