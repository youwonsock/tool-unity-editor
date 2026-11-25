using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// RULE5 (CORNER): the Surface2D exit-step rule with Default Direction
    /// turned off +Z. A near-edge sample crossing sideways into a step up over
    /// Max Step Height keeps moving (the mover meets it like a wall), and one
    /// crossing into a drop stops. Movers walked in 0.06 m steps from 10 x 10
    /// start points in every ledge-adjacent cell never cross into a drop or a
    /// cell with no ground while moving, diagonally (corner cuts) or straight.
    /// The stops, and how many of them a mover following the published
    /// directions would never have needed, are recorded.
    /// </summary>
    [TestFixture, Category("Safety")]
    internal sealed class ExitStepRuleTests : FlowFieldPlayTestBase
    {
        private const int StartsPerAxis = 10;
        private const float StartSpacing = 0.098f;
        private const int WalkSteps = 60;
        private const int GhostSteps = 40;

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalAngle15() => Run(FlowFieldBakeMode.RuntimeDynamic, 15f, keepGoal: false, "stepUpMoving=44 dropStopped=47 ledgeCells=145 starts=14500 stopped=6085 noDropAhead=1539");

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalAngle15BakedBase() => Run(FlowFieldBakeMode.BakedBase, 15f, keepGoal: false, "stepUpMoving=44 dropStopped=47 ledgeCells=145 starts=14500 stopped=6085 noDropAhead=1539");

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalAngle30() => Run(FlowFieldBakeMode.RuntimeDynamic, 30f, keepGoal: false, "stepUpMoving=0 dropStopped=0 ledgeCells=145 starts=14500 stopped=6631 noDropAhead=1617");

        [UnityTest, Timeout(300000)]
        public IEnumerator GoalAngle15() => Run(FlowFieldBakeMode.RuntimeDynamic, 15f, keepGoal: true, "stepUpMoving=0 dropStopped=0 ledgeCells=145 starts=14500 stopped=0 noDropAhead=0");

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalAngle0() => Run(FlowFieldBakeMode.RuntimeDynamic, 0f, keepGoal: false, "stepUpMoving=0 dropStopped=0 ledgeCells=145 starts=14500 stopped=1000 noDropAhead=1000");

        private IEnumerator Run(FlowFieldBakeMode bakeMode, float angle, bool keepGoal, string recorded)
        {
            string scenario = "RULE5 " + bakeMode + ", Default Direction " + angle + (keepGoal ? ", Goal kept" : ", no Goal");
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, bakeMode);
            FlowFieldManager manager = Manager;
            if (!keepGoal)
                FlowFieldSafetyScan.ClearGoal(manager);
            FlowFieldSafetyScan.SetDefaultDirection(manager, angle);
            manager.RequestRebuild();
            yield return FlowFieldSafetyScan.Settle();
            Scan(manager, scenario, recorded);
        }

        private static void Scan(FlowFieldManager manager, string scenario, string recorded)
        {
            var cells = new FlowFieldSafetyScan.SurfaceCells(manager);
            float edge = FlowFieldSafetyScan.NearEdgeOffset * cells.CellSize;
            var stepUpStops = new FlowFieldSafetyScan.Offenders("near-edge samples stopped before a step up");
            var dropMoves = new FlowFieldSafetyScan.Offenders("near-edge samples moving into a drop");
            var leaks = new FlowFieldSafetyScan.Offenders("walked movers crossing into a drop while moving");
            int stepUpMoving = 0;
            int dropStopped = 0;
            int ledgeCells = 0;
            int starts = 0;
            int stopped = 0;
            int needlessStops = 0;
            for (int z = 0; z < cells.Depth; z++)
            for (int x = 0; x < cells.Width; x++)
            {
                int i = cells.Index(x, z);
                if (!cells.IsOpen(i))
                    continue;
                Vector3 center = cells.Center(i);
                if (FlowFieldSafetyScan.IsMoving(manager, center, out FlowFieldSample sample)
                    && FlowFieldSafetyScan.TryCrossesSidewaysNext(sample.Direction, out int stepX, out int stepZ)
                    && cells.Contains(x + stepX, z + stepZ))
                {
                    int t = cells.Index(x + stepX, z + stepZ);
                    if (cells.IsOpen(t))
                    {
                        float rise = cells.Heights[t] - cells.Heights[i];
                        bool edgeMoves = FlowFieldSafetyScan.IsMoving(manager, center + new Vector3(edge * stepX, 0f, edge * stepZ));
                        string where = cells.Describe(x, z) + " side (" + stepX + "," + stepZ + ") rise " + rise.ToString("F2");
                        if (rise > cells.MaxStep)
                        {
                            if (edgeMoves)
                                stepUpMoving++;
                            else
                                stepUpStops.Add(where);
                        }
                        else if (rise < -cells.MaxStep)
                        {
                            if (edgeMoves)
                                dropMoves.Add(where);
                            else
                                dropStopped++;
                        }
                    }
                }

                if (!IsLedgeAdjacent(cells, x, z))
                    continue;
                ledgeCells++;
                for (int startZ = 0; startZ < StartsPerAxis; startZ++)
                for (int startX = 0; startX < StartsPerAxis; startX++)
                {
                    float offsetX = (startX - 4.5f) * StartSpacing;
                    float offsetZ = (startZ - 4.5f) * StartSpacing;
                    Vector3 start = center + new Vector3(offsetX * cells.CellSize, 0f, offsetZ * cells.CellSize);
                    starts++;
                    Walk(manager, cells, start, x, z, new Vector2(offsetX, offsetZ), leaks, ref stopped, ref needlessStops);
                }
            }

            FlowFieldSafetyScan.AssertNone(scenario, stepUpStops, dropMoves, leaks);
            FlowFieldRecorded.AreEqual(recorded,
                "stepUpMoving=" + stepUpMoving + " dropStopped=" + dropStopped + " ledgeCells=" + ledgeCells
                + " starts=" + starts + " stopped=" + stopped + " noDropAhead=" + needlessStops,
                scenario + ": counts");
        }

        // A mover that steps 0.06 m along each sample, without colliding, until it stops.
        private static void Walk(
            FlowFieldManager manager,
            FlowFieldSafetyScan.SurfaceCells cells,
            Vector3 position,
            int x,
            int z,
            Vector2 startOffset,
            FlowFieldSafetyScan.Offenders leaks,
            ref int stopped,
            ref int needlessStops)
        {
            for (int step = 0; step < WalkSteps; step++)
            {
                if (!FlowFieldSafetyScan.IsMoving(manager, position, out FlowFieldSample sample))
                {
                    stopped++;
                    if (!GhostReachesDrop(cells, position, x, z))
                        needlessStops++;
                    return;
                }
                Vector3 direction = sample.Direction;
                direction.y = 0f;
                if (direction.sqrMagnitude < 1e-8f)
                {
                    stopped++;
                    return;
                }
                position += direction.normalized * FlowFieldSafetyScan.WalkStep;
                if (!cells.Grid.TryWorldToLocal(position, out int nextX, out int nextZ))
                    return;
                if (nextX == x && nextZ == z)
                    continue;
                int from = cells.Index(x, z);
                int to = cells.Index(nextX, nextZ);
                if (cells.IsDrop(from, to))
                {
                    leaks.Add("(" + x + "," + z + ") -> (" + nextX + "," + nextZ + ") "
                        + (cells.Walkable[to] ? "drop " + (cells.Heights[to] - cells.Heights[from]).ToString("F2") : "no ground")
                        + (nextX != x && nextZ != z ? " diagonal" : " straight") + ", start offset " + startOffset.ToString("F2"));
                    return;
                }
                x = nextX;
                z = nextZ;
            }
        }

        // Whether a mover that follows the published directions with no
        // safety rule, colliding with walls, would reach a drop or the grid
        // edge from here: if not, the stop was not needed.
        private static bool GhostReachesDrop(FlowFieldSafetyScan.SurfaceCells cells, Vector3 position, int x, int z)
        {
            for (int step = 0; step < GhostSteps; step++)
            {
                Vector3 direction = cells.Field.FinalDirections[cells.Index(x, z)];
                direction.y = 0f;
                if (direction.sqrMagnitude < 1e-8f)
                    return false;
                FlowFieldSafetyScan.SlideResult result = cells.Slide(
                    ref position, ref x, ref z, direction.normalized * FlowFieldSafetyScan.WalkStep, dropsEnd: true);
                if (result == FlowFieldSafetyScan.SlideResult.OffGrid || result == FlowFieldSafetyScan.SlideResult.Drop)
                    return true;
                if (result == FlowFieldSafetyScan.SlideResult.Walled)
                    return false;
            }
            return false;
        }

        // Any open 8-neighbour has no ground or lies more than Max Step Height below.
        private static bool IsLedgeAdjacent(FlowFieldSafetyScan.SurfaceCells cells, int x, int z)
        {
            int i = cells.Index(x, z);
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (offsetX == 0 && offsetZ == 0 || !cells.Contains(x + offsetX, z + offsetZ))
                    continue;
                if (cells.IsDrop(i, cells.Index(x + offsetX, z + offsetZ)))
                    return true;
            }
            return false;
        }
    }
}
