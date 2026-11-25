using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// R5WALL: movers sliding along raised ground (a neighbour more than Max
    /// Step Height higher) with no Goal and Default Direction turned off +Z.
    /// Each walks 0.06 m steps along its samples, colliding and sliding against
    /// step ups and blocked cells, from 5 x 5 start points in every cell beside
    /// a step up. A walker never crosses into a drop or a cell with no ground
    /// while moving. Its stops are recorded: frozen ones with no drop, hole or
    /// grid edge within a cell (Default Direction into a wall, rule 4) and what
    /// they head for, justified ones, and walkers stuck in a corner.
    /// </summary>
    [TestFixture, Category("Safety")]
    internal sealed class RaisedGroundSlideTests : FlowFieldPlayTestBase
    {
        private const int StartsPerAxis = 5;
        private const float StartSpacing = 0.18f;
        private const int WalkSteps = 80;

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalAngle15() => Run(15f, "besideStepUps=148 starts=3700 frozen=2720 (stepUp 1252, blocked 493) justified=0 stuck=0");

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalAngle30() => Run(30f, "besideStepUps=148 starts=3700 frozen=3601 (stepUp 2286, blocked 1239) justified=0 stuck=0");

        private IEnumerator Run(float angle, string recorded)
        {
            string scenario = "R5WALL Default Direction " + angle + ", no Goal";
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldSafetyScan.ClearGoal(manager);
            FlowFieldSafetyScan.SetDefaultDirection(manager, angle);
            manager.RequestRebuild();
            yield return FlowFieldSafetyScan.Settle();
            Scan(manager, scenario, recorded);
        }

        private sealed class Tally
        {
            internal int Frozen;
            internal int FrozenHeadingStepUp;
            internal int FrozenHeadingBlocked;
            internal int Justified;
            internal int Stuck;
        }

        private static void Scan(FlowFieldManager manager, string scenario, string recorded)
        {
            var cells = new FlowFieldSafetyScan.SurfaceCells(manager);
            var leaks = new FlowFieldSafetyScan.Offenders("sliding walkers crossing into a drop while moving");
            var tally = new Tally();
            int besideStepUps = 0;
            int starts = 0;
            for (int z = 0; z < cells.Depth; z++)
            for (int x = 0; x < cells.Width; x++)
            {
                int i = cells.Index(x, z);
                if (!cells.IsOpen(i) || !HasStepUpNeighbour(cells, x, z))
                    continue;
                besideStepUps++;
                Vector3 center = cells.Center(i);
                for (int startZ = 0; startZ < StartsPerAxis; startZ++)
                for (int startX = 0; startX < StartsPerAxis; startX++)
                {
                    Vector3 start = center + new Vector3(
                        (startX - 2f) * StartSpacing * cells.CellSize, 0f, (startZ - 2f) * StartSpacing * cells.CellSize);
                    starts++;
                    Walk(manager, cells, start, x, z, leaks, tally);
                }
            }

            FlowFieldSafetyScan.AssertNone(scenario, leaks);
            FlowFieldRecorded.AreEqual(recorded,
                "besideStepUps=" + besideStepUps + " starts=" + starts + " frozen=" + tally.Frozen
                + " (stepUp " + tally.FrozenHeadingStepUp + ", blocked " + tally.FrozenHeadingBlocked + ")"
                + " justified=" + tally.Justified + " stuck=" + tally.Stuck,
                scenario + ": counts");
        }

        private static void Walk(
            FlowFieldManager manager,
            FlowFieldSafetyScan.SurfaceCells cells,
            Vector3 position,
            int x,
            int z,
            FlowFieldSafetyScan.Offenders leaks,
            Tally tally)
        {
            for (int step = 0; step < WalkSteps; step++)
            {
                if (!FlowFieldSafetyScan.IsMoving(manager, position, out FlowFieldSample sample))
                {
                    if (IsHazardNear(cells, x, z))
                    {
                        tally.Justified++;
                    }
                    else
                    {
                        tally.Frozen++;
                        CountFrozenHeading(cells, x, z, sample, tally);
                    }
                    return;
                }
                Vector3 direction = sample.Direction;
                direction.y = 0f;
                if (direction.sqrMagnitude < 1e-8f)
                {
                    tally.Justified++;
                    return;
                }
                int fromX = x;
                int fromZ = z;
                FlowFieldSafetyScan.SlideResult result = cells.Slide(
                    ref position, ref x, ref z, direction.normalized * FlowFieldSafetyScan.WalkStep, dropsEnd: true);
                if (result == FlowFieldSafetyScan.SlideResult.Moved)
                    continue;
                if (result == FlowFieldSafetyScan.SlideResult.Drop)
                    leaks.Add(cells.Describe(fromX, fromZ) + " after " + step + " steps, heading " + direction.ToString("F2"));
                else if (result == FlowFieldSafetyScan.SlideResult.Walled)
                    tally.Stuck++;
                return;
            }
        }

        // The 8-neighbour a frozen walker heads for: the stopped sample's
        // direction, or its cell's published one when the sample has none.
        private static void CountFrozenHeading(
            FlowFieldSafetyScan.SurfaceCells cells, int x, int z, in FlowFieldSample sample, Tally tally)
        {
            int i = cells.Index(x, z);
            Vector3 heading = sample.Direction;
            heading.y = 0f;
            if (heading.sqrMagnitude < 1e-8f)
            {
                heading = cells.Field.FinalDirections[i];
                heading.y = 0f;
            }
            heading = heading.normalized;
            float best = -2f;
            int headX = 0;
            int headZ = 0;
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (offsetX == 0 && offsetZ == 0)
                    continue;
                float alignment = Vector3.Dot(heading, new Vector3(offsetX, 0f, offsetZ).normalized);
                if (alignment > best)
                {
                    best = alignment;
                    headX = offsetX;
                    headZ = offsetZ;
                }
            }
            if (!cells.Contains(x + headX, z + headZ))
                return;
            int target = cells.Index(x + headX, z + headZ);
            if (cells.Blocked[target])
                tally.FrozenHeadingBlocked++;
            else if (cells.IsStepUp(i, target))
                tally.FrozenHeadingStepUp++;
        }

        private static bool HasStepUpNeighbour(FlowFieldSafetyScan.SurfaceCells cells, int x, int z)
        {
            int i = cells.Index(x, z);
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (offsetX == 0 && offsetZ == 0 || !cells.Contains(x + offsetX, z + offsetZ))
                    continue;
                int t = cells.Index(x + offsetX, z + offsetZ);
                if (!cells.Blocked[t] && cells.IsStepUp(i, t))
                    return true;
            }
            return false;
        }

        // A drop, a cell with no ground or the grid edge within one cell.
        private static bool IsHazardNear(FlowFieldSafetyScan.SurfaceCells cells, int x, int z)
        {
            int i = cells.Index(x, z);
            for (int offsetZ = -1; offsetZ <= 1; offsetZ++)
            for (int offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (!cells.Contains(x + offsetX, z + offsetZ))
                    return true;
                if (cells.IsDrop(i, cells.Index(x + offsetX, z + offsetZ)))
                    return true;
            }
            return false;
        }
    }
}
