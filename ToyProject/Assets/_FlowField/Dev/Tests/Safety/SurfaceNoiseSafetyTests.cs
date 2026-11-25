using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// E3: a Noise Modifier turns Surface2D directions, and the sample follows
    /// the turned direction. No moving sample may head (45-degree sectors) for
    /// a blocked, groundless or off-grid neighbour, without the Modifier or with
    /// it: Default Direction cells with no Goal, every cell but the anchor with
    /// the Goal kept. The counts by base direction and the moving cells, which a
    /// small angle should leave near the count without the Modifier, are
    /// recorded.
    /// </summary>
    [TestFixture, Category("Safety")]
    internal sealed class SurfaceNoiseSafetyTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalNoise15() => Run(keepGoal: false, angle: 15f, "no Modifier: cells=8316 moving=7876 unsafeByBase=0 | Noise: cells=8316 moving=7876 unsafeByBase=0");

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalNoise180() => Run(keepGoal: false, angle: 180f, "no Modifier: cells=8316 moving=7876 unsafeByBase=0 | Noise: cells=8316 moving=7525 unsafeByBase=69");

        [UnityTest, Timeout(300000)]
        public IEnumerator GoalNoise180() => Run(keepGoal: true, angle: 180f, "no Modifier: cells=8315 moving=8315 unsafeByBase=0 | Noise: cells=8315 moving=7850 unsafeByBase=0");

        private IEnumerator Run(bool keepGoal, float angle, string recorded)
        {
            string scenario = "E3 " + (keepGoal ? "Goal kept" : "no Goal") + ", Noise " + angle;
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            if (!keepGoal)
                FlowFieldSafetyScan.ClearGoal(manager);
            yield return FlowFieldSafetyScan.Settle();
            string before = Scan(manager, keepGoal, scenario + ", no Modifier");

            FlowFieldSafetyScan.AddNoise(manager, angle);
            yield return FlowFieldSafetyScan.Settle();
            string after = Scan(manager, keepGoal, scenario);

            FlowFieldRecorded.AreEqual(recorded,
                "no Modifier: " + before + " | Noise: " + after,
                scenario + ": counts");
        }

        private static string Scan(FlowFieldManager manager, bool keepGoal, string scenario)
        {
            var cells = new FlowFieldSafetyScan.SurfaceCells(manager);
            FlowFieldField field = cells.Field;
            var unsafeSamples = new FlowFieldSafetyScan.Offenders(
                "moving samples heading into a blocked, groundless or off-grid cell");
            int scanned = 0;
            int moving = 0;
            int unsafeByBase = 0;
            for (int z = 0; z < cells.Depth; z++)
            for (int x = 0; x < cells.Width; x++)
            {
                int i = cells.Index(x, z);
                int next = field.NextCells[i];
                if ((keepGoal ? next == i : next != FlowFieldNextCell.DefaultDirection) || cells.Blocked[i])
                    continue;
                scanned++;
                if (!FlowFieldSafetyScan.IsMoving(manager, cells.Center(i), out FlowFieldSample sample))
                    continue;
                moving++;
                if (HeadsUnsafe(cells, x, z, field.BaseDirections[i]))
                    unsafeByBase++;
                if (HeadsUnsafe(cells, x, z, sample.Direction))
                    unsafeSamples.Add(cells.Describe(x, z) + " base " + field.BaseDirections[i].ToString("F2")
                        + " sample " + sample.Direction.ToString("F2") + " speed " + sample.SpeedMultiplier.ToString("F2"));
            }
            FlowFieldSafetyScan.AssertNone(scenario, unsafeSamples);
            return "cells=" + scanned + " moving=" + moving + " unsafeByBase=" + unsafeByBase;
        }

        // The sector step, and each axis step it is made of.
        private static bool HeadsUnsafe(FlowFieldSafetyScan.SurfaceCells cells, int x, int z, Vector3 direction)
        {
            if (!FlowFieldSafetyScan.TrySectorStep(direction, out int stepX, out int stepZ))
                return false;
            return stepX != 0 && cells.IsUnsafe(x + stepX, z)
                || stepZ != 0 && cells.IsUnsafe(x, z + stepZ)
                || stepX != 0 && stepZ != 0 && cells.IsUnsafe(x + stepX, z + stepZ);
        }
    }
}
