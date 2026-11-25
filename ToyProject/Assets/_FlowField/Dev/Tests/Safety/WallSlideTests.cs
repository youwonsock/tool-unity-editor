using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// WSLIDE: a mover a Noise Modifier turned slightly sideways, sampled 0.45
    /// cell toward the side it crosses next. Beside a wall (a blocked cell the
    /// ground links) or a step up it keeps moving, so it slides along (the
    /// round-5 freeze); toward a drop (an open neighbour with no ground link,
    /// not higher) it stops before the edge (rule 5). Without the Modifier and
    /// with it, Goal kept or not.
    /// </summary>
    [TestFixture, Category("Safety")]
    internal sealed class WallSlideTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator GoalNoise15() => Run(keepGoal: true, angle: 15f, "no Modifier: moving=8315 wallMoving=0 stepUpMoving=0 dropStopped=0 | Noise: moving=8315 wallMoving=105 stepUpMoving=9 dropStopped=1");

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalNoise15() => Run(keepGoal: false, angle: 15f, "no Modifier: moving=7876 wallMoving=0 stepUpMoving=0 dropStopped=0 | Noise: moving=7876 wallMoving=150 stepUpMoving=15 dropStopped=9");

        [UnityTest, Timeout(300000)]
        public IEnumerator GoalNoise180() => Run(keepGoal: true, angle: 180f, "no Modifier: moving=8315 wallMoving=0 stepUpMoving=0 dropStopped=0 | Noise: moving=7850 wallMoving=143 stepUpMoving=12 dropStopped=7");

        private IEnumerator Run(bool keepGoal, float angle, string recorded)
        {
            string scenario = "WSLIDE " + (keepGoal ? "Goal kept" : "no Goal") + ", Noise " + angle;
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
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
            var cells = new FlowFieldSafetyScan.SurfaceCells(manager);
            FlowFieldField field = cells.Field;
            byte[] groundLinks = cells.Surface.NeighborMasks;
            float edge = FlowFieldSafetyScan.NearEdgeOffset * cells.CellSize;
            var wallStops = new FlowFieldSafetyScan.Offenders("near-edge samples stopped beside a wall");
            var stepUpStops = new FlowFieldSafetyScan.Offenders("near-edge samples stopped beside a step up");
            var dropMoves = new FlowFieldSafetyScan.Offenders("near-edge samples moving toward a drop");
            int moving = 0;
            int wallMoving = 0;
            int stepUpMoving = 0;
            int dropStopped = 0;
            for (int z = 0; z < cells.Depth; z++)
            for (int x = 0; x < cells.Width; x++)
            {
                int i = cells.Index(x, z);
                if (!cells.IsOpen(i) || field.NextCells[i] < FlowFieldNextCell.DefaultDirection)
                    continue;
                Vector3 center = cells.Center(i);
                if (!FlowFieldSafetyScan.IsMoving(manager, center, out FlowFieldSample sample))
                    continue;
                moving++;
                if (!FlowFieldSafetyScan.TryCrossesSidewaysNext(sample.Direction, out int stepX, out int stepZ))
                    continue;
                int targetX = x + stepX;
                int targetZ = z + stepZ;
                if (!cells.Contains(targetX, targetZ))
                    continue;
                int t = cells.Index(targetX, targetZ);
                if (!cells.Walkable[t])
                    continue;
                int direction = FlowFieldNeighborUtility.FindDirectionIndex(stepX, stepZ);
                bool linked = (groundLinks[i] & (1 << direction)) != 0;
                Vector3 nearEdge = center + new Vector3(edge * stepX, 0f, edge * stepZ);
                bool edgeMoves = FlowFieldSafetyScan.IsMoving(manager, nearEdge);
                string where = cells.Describe(x, z) + " side (" + stepX + "," + stepZ + ") sample "
                    + sample.Direction.ToString("F2");
                if (cells.Blocked[t] && linked)
                {
                    if (edgeMoves)
                        wallMoving++;
                    else
                        wallStops.Add(where);
                }
                else if (!cells.Blocked[t] && !linked && cells.Heights[t] > cells.Heights[i])
                {
                    if (edgeMoves)
                        stepUpMoving++;
                    else
                        stepUpStops.Add(where);
                }
                else if (!cells.Blocked[t] && !linked)
                {
                    if (edgeMoves)
                        dropMoves.Add(where);
                    else
                        dropStopped++;
                }
            }
            FlowFieldSafetyScan.AssertNone(scenario, wallStops, stepUpStops, dropMoves);
            return "moving=" + moving + " wallMoving=" + wallMoving + " stepUpMoving=" + stepUpMoving
                + " dropStopped=" + dropStopped;
        }
    }
}
