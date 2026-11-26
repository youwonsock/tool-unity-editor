using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// SF3: with no Goal, a Default Direction sample whose 45-degree heading
    /// enters an open, walkable neighbour with no published link (a ledge, or a
    /// step over Max Step Height) must not move. Rule 4 checks the ground link
    /// of the nearest step, as a path step checks its own link.
    /// </summary>
    [TestFixture, Category("Safety")]
    internal sealed class DefaultDirectionLinkTests : FlowFieldPlayTestBase
    {
        [UnityTest, Timeout(300000)]
        public IEnumerator SampleNoGoal()
        {
            yield return FlowFieldPlayScene.Enter(FlowFieldPlayScene.SampleScene, FlowFieldBakeMode.RuntimeDynamic);
            FlowFieldManager manager = Manager;
            FlowFieldSafetyScan.ClearGoal(manager);
            yield return FlowFieldSafetyScan.Settle();
            Scan(manager);
        }

        private static void Scan(FlowFieldManager manager)
        {
            var cells = new FlowFieldSafetyScan.SurfaceCells(manager);
            FlowFieldField field = cells.Field;
            var moversWithoutLink = new FlowFieldSafetyScan.Offenders(
                "moving Default Direction samples heading for an open neighbour with no link");
            int defaultCells = 0;
            int moving = 0;
            int missingLinks = 0;
            for (int z = 0; z < cells.Depth; z++)
            for (int x = 0; x < cells.Width; x++)
            {
                int i = cells.Index(x, z);
                if (field.Goal.NextCells[i] != FlowFieldNextCell.DefaultDirection || cells.Blocked[i])
                    continue;
                defaultCells++;
                bool moves = FlowFieldSafetyScan.IsMoving(manager, cells.Center(i), out FlowFieldSample sample);
                if (moves)
                    moving++;
                Vector3 direction = moves ? sample.Direction : field.Goal.FinalDirections[i];
                if (!FlowFieldSafetyScan.TrySectorStep(direction, out int stepX, out int stepZ))
                    continue;
                int targetX = x + stepX;
                int targetZ = z + stepZ;
                if (!cells.Contains(targetX, targetZ) || !cells.IsOpen(cells.Index(targetX, targetZ)))
                    continue;
                int link = FlowFieldNeighborUtility.FindDirectionIndex(stepX, stepZ);
                if ((field.Goal.Topology[i] & (1u << link)) != 0u)
                    continue;
                missingLinks++;
                if (moves)
                    moversWithoutLink.Add(cells.Describe(x, z) + " -> (" + targetX + "," + targetZ + ") speed "
                        + sample.SpeedMultiplier.ToString("F2"));
            }

            FlowFieldSafetyScan.AssertNone("SF3 sample, no Goal", moversWithoutLink);
            FlowFieldRecorded.AreEqual("default=8316 moving=7876 missingLinks=48",
                "default=" + defaultCells + " moving=" + moving + " missingLinks=" + missingLinks,
                "SF3 sample, no Goal: counts");
        }
    }
}
