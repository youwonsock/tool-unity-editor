using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// SFDRIFT: with no Goal and Default Direction turned off +Z, a moving
    /// Default Direction cell may have a sideways neighbour (its side component
    /// above 5% of the planar direction) more than Max Step Height higher or
    /// lower. Sampled 0.45 cell toward that side, where it crosses sideways
    /// next, a mover above a drop must stop (rule 5); one beside a step up may
    /// slide on. No moving cell may head forward into such a step (rule 4's
    /// nearest-step ground link). The cell counts are recorded.
    /// </summary>
    [TestFixture, Category("Safety")]
    internal sealed class SidewaysDriftTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalAngle15() => Run(FlowFieldBakeMode.RuntimeDynamic, 15f, "default=8316 moving=7408 sidewaysOverMaxStep=91 nearEdgeStepUpMoving=44 tinySideStops=0");

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalAngle15BakedBase() => Run(FlowFieldBakeMode.BakedBase, 15f, "default=8316 moving=7408 sidewaysOverMaxStep=91 nearEdgeStepUpMoving=44 tinySideStops=0");

        [UnityTest, Timeout(300000)]
        public IEnumerator NoGoalAngle0() => Run(FlowFieldBakeMode.RuntimeDynamic, 0f, "default=8316 moving=7876 sidewaysOverMaxStep=0 nearEdgeStepUpMoving=0 tinySideStops=0");

        private IEnumerator Run(FlowFieldBakeMode bakeMode, float angle, string recorded)
        {
            string scenario = "SFDRIFT " + bakeMode + ", Default Direction " + angle + ", no Goal";
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, bakeMode);
            FlowFieldManager manager = Manager;
            FlowFieldSafetyScan.ClearGoal(manager);
            FlowFieldSafetyScan.SetDefaultDirection(manager, angle);
            manager.RequestRebuild();
            yield return FlowFieldSafetyScan.Settle();
            Scan(manager, scenario, recorded);
        }

        private static void Scan(FlowFieldManager manager, string scenario, string recorded)
        {
            var cells = new FlowFieldSafetyScan.SurfaceCells(manager);
            FlowFieldField field = cells.Field;
            float edge = FlowFieldSafetyScan.NearEdgeOffset * cells.CellSize;
            var forwardSteps = new FlowFieldSafetyScan.Offenders(
                "moving cells heading forward into a step over Max Step Height");
            var nearEdgeDrops = new FlowFieldSafetyScan.Offenders(
                "near-edge samples moving sideways into a drop");
            int defaultCells = 0;
            int moving = 0;
            int sideways = 0;
            int nearEdgeStepUpMoving = 0;
            int tinySideStops = 0;
            for (int z = 0; z < cells.Depth; z++)
            for (int x = 0; x < cells.Width; x++)
            {
                int i = cells.Index(x, z);
                if (field.NextCells[i] != FlowFieldNextCell.DefaultDirection || cells.Blocked[i])
                    continue;
                defaultCells++;
                Vector3 center = cells.Center(i);
                if (!FlowFieldSafetyScan.IsMoving(manager, center, out FlowFieldSample sample))
                {
                    // A stop for a Surface projection's tiny side component.
                    Vector3 published = field.FinalDirections[i];
                    float publishedPlanar = new Vector2(published.x, published.z).magnitude;
                    float side = Mathf.Min(Mathf.Abs(published.x), Mathf.Abs(published.z));
                    if (publishedPlanar > 1e-4f && side > 1e-6f && side < 0.01f * publishedPlanar)
                        tinySideStops++;
                    continue;
                }
                moving++;
                Vector3 direction = sample.Direction;
                float planar = new Vector2(direction.x, direction.z).magnitude;
                if (planar < 1e-4f)
                    continue;
                int stepX = direction.x > 0.05f * planar ? 1 : direction.x < -0.05f * planar ? -1 : 0;
                int stepZ = direction.z > 0.05f * planar ? 1 : direction.z < -0.05f * planar ? -1 : 0;
                if (stepZ != 0 && cells.Contains(x, z + stepZ))
                {
                    int ahead = cells.Index(x, z + stepZ);
                    if (cells.IsOpen(ahead) && Mathf.Abs(cells.Heights[ahead] - cells.Heights[i]) > cells.MaxStep)
                        forwardSteps.Add(cells.Describe(x, z) + " -> (" + x + "," + (z + stepZ) + ") sample "
                            + direction.ToString("F2"));
                }
                if (stepX == 0 || !cells.Contains(x + stepX, z))
                    continue;
                int beside = cells.Index(x + stepX, z);
                if (!cells.IsOpen(beside))
                    continue;
                float rise = cells.Heights[beside] - cells.Heights[i];
                if (Mathf.Abs(rise) <= cells.MaxStep)
                    continue;
                sideways++;
                if (!FlowFieldSafetyScan.IsMoving(manager, center + new Vector3(edge * stepX, 0f, 0f)))
                    continue;
                if (rise > 0f)
                    nearEdgeStepUpMoving++;
                else
                    nearEdgeDrops.Add(cells.Describe(x, z) + " -> (" + (x + stepX) + "," + z + ") drop "
                        + rise.ToString("F2") + " sample " + direction.ToString("F2"));
            }

            FlowFieldSafetyScan.AssertNone(scenario, forwardSteps, nearEdgeDrops);
            FlowFieldRecorded.AreEqual(recorded,
                "default=" + defaultCells + " moving=" + moving + " sidewaysOverMaxStep=" + sideways
                + " nearEdgeStepUpMoving=" + nearEdgeStepUpMoving + " tinySideStops=" + tinySideStops,
                scenario + ": counts");
        }
    }
}
