using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// V3D: a Noise Modifier turns Volume3D directions. No moving sample may
    /// have a nearest step of the 26 that enters a blocked or off-grid cell,
    /// without the Modifier or with it, with no Goal or with the Goal kept.
    /// Rule 4 picks the nearest of the 26 steps, not the per-axis sin 22.5
    /// degree test that is right only for 8 neighbours.
    /// </summary>
    [TestFixture, Category("Safety")]
    internal sealed class VolumeNoiseSafetyTests : FlowFieldPlayTestBase
    {
        // The 26 neighbour steps in the kit's order, which settles ties.
        private static readonly Vector3Int[] Steps = CreateSteps();

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalNoise180() => Run(keepGoal: false, angle: 180f, "no Modifier: cells=4412 moving=4024 | Noise: cells=4412 moving=3726");

        [UnityTest, Timeout(300000)]
        public IEnumerator GoalNoise180() => Run(keepGoal: true, angle: 180f, "no Modifier: cells=4411 moving=4411 | Noise: cells=4411 moving=4097");

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalNoise15() => Run(keepGoal: false, angle: 15f, "no Modifier: cells=4412 moving=4024 | Noise: cells=4412 moving=4024");

        private IEnumerator Run(bool keepGoal, float angle, string recorded)
        {
            string scenario = "V3D " + (keepGoal ? "Goal kept" : "no Goal") + ", Noise " + angle;
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.VolumeScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            Assert.That(manager.SpaceMode, Is.EqualTo(FlowFieldSpaceMode.Volume3D), FlowFieldPlayScene.Describe());
            if (!keepGoal)
                FlowFieldSafetyScan.ClearGoal(manager);
            yield return FlowFieldSafetyScan.Settle();
            string before = Scan(manager, scenario + ", no Modifier");

            FlowFieldSafetyScan.AddNoise(manager, angle);
            yield return FlowFieldSafetyScan.Settle();
            string after = Scan(manager, scenario);

            FlowFieldRecorded.AreEqual(recorded,
                "no Modifier: " + before + " | Noise: " + after,
                scenario + ": counts");
        }

        private static string Scan(FlowFieldManager manager, string scenario)
        {
            FlowFieldField field = FlowFieldSafetyScan.PublishedField(manager);
            FlowFieldGridSpace grid = field.Grid;
            var unsafeSteps = new FlowFieldSafetyScan.Offenders(
                "moving samples whose nearest step enters a blocked or off-grid cell");
            int scanned = 0;
            int moving = 0;
            for (int y = 0; y < grid.Height; y++)
            for (int z = 0; z < grid.Depth; z++)
            for (int x = 0; x < grid.Width; x++)
            {
                int i = grid.ToFlatIndex(x, y, z);
                if (field.Blocked[i] || field.NextCells[i] == i)
                    continue;
                scanned++;
                if (!FlowFieldSafetyScan.IsMoving(manager, grid.CellCenter(i), out FlowFieldSample sample))
                    continue;
                moving++;
                Vector3 direction = sample.Direction.normalized;
                Vector3Int step = Nearest(direction);
                int targetX = x + step.x;
                int targetY = y + step.y;
                int targetZ = z + step.z;
                bool offGrid = !grid.IsLocalInBounds(targetX, targetY, targetZ);
                if (!offGrid && !field.Blocked[grid.ToFlatIndex(targetX, targetY, targetZ)])
                    continue;
                unsafeSteps.Add("cell (" + x + "," + y + "," + z + ") at " + grid.CellCenter(i).ToString("F2")
                    + " direction " + direction.ToString("F2") + " step " + step
                    + (offGrid ? " off-grid" : " blocked")
                    + (field.NextCells[i] == FlowFieldNextCell.DefaultDirection ? " (Default Direction)" : " (path)"));
            }
            FlowFieldSafetyScan.AssertNone(scenario, unsafeSteps);
            return "cells=" + scanned + " moving=" + moving;
        }

        private static Vector3Int Nearest(Vector3 direction)
        {
            float best = -2f;
            Vector3Int nearest = Vector3Int.zero;
            foreach (Vector3Int step in Steps)
            {
                float alignment = Vector3.Dot(direction, ((Vector3)step).normalized);
                if (alignment > best)
                {
                    best = alignment;
                    nearest = step;
                }
            }
            return nearest;
        }

        private static Vector3Int[] CreateSteps()
        {
            var steps = new List<Vector3Int>(26);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx != 0 || dy != 0 || dz != 0)
                    steps.Add(new Vector3Int(dx, dy, dz));
            }
            return steps.ToArray();
        }
    }
}
