using NUnit.Framework;
using UnityEngine;

namespace Common.FlowField.Tests
{
    /// <summary>
    /// V1S: the sample safety rule on a hand-built 3 x 3 x 3 Volume3D field,
    /// every link open, Default Direction +X everywhere and the corner cell
    /// (2,2,2) blocked. A centre cell a Modifier turned so that its nearest
    /// of the 26 steps is the diagonal into that corner stops, even where the
    /// direction's X axis is the largest; the unturned +X direction keeps
    /// moving with the published sample.
    /// </summary>
    [TestFixture, Category("Safety")]
    internal sealed class VolumeTurnedStepTests
    {
        private const int Size = 3;
        private const uint AllVolumeLinks = (1u << FlowFieldNeighborUtility.VolumeCount) - 1u;

        [TestCase(0.866f, 0.354f, 0.354f, true)]
        [TestCase(0.663f, 0.663f, 0.346f, true)]
        [TestCase(0.80f, 0.42f, 0.42f, true)]
        [TestCase(1f, 0f, 0f, false)]
        public void CentreHeadingTowardBlockedCorner(float x, float y, float z, bool stops)
        {
            FlowFieldGridSpace grid = FlowFieldGridSpace.FromVolumeCellGrid(Vector3.zero, Size, Size, Size, 1f);
            Assert.That(grid.TryLocal(1, 1, 1, out int centre), Is.True);
            Assert.That(grid.TryLocal(2, 2, 2, out int corner), Is.True);
            int count = grid.CellCount;
            var blocked = new bool[count];
            blocked[corner] = true;
            var topology = new uint[count];
            var nextCells = new int[count];
            var baseDirections = new Vector3[count];
            var finalDirections = new Vector3[count];
            var speeds = new float[count];
            for (int i = 0; i < count; i++)
            {
                topology[i] = AllVolumeLinks;
                nextCells[i] = FlowFieldNextCell.DefaultDirection;
                baseDirections[i] = Vector3.right;
                finalDirections[i] = Vector3.right;
                speeds[i] = 1f;
            }
            Vector3 direction = new Vector3(x, y, z).normalized;
            finalDirections[centre] = direction;
            var field = new FlowFieldField(
                grid,
                null,
                blocked,
                topology,
                new FlowFieldGoalFlags[count],
                nextCells,
                baseDirections,
                speeds,
                finalDirections,
                speeds,
                new Vector3[count],
                null,
                false,
                -1);
            var published = new FlowFieldSample(direction, 1f, Vector3.zero, false, true);

            bool overridden = FlowFieldSampleSafetyRule.Apply(
                field,
                centre,
                grid.CellCenter(centre),
                in published,
                new FlowFieldSafety(false),
                null,
                out FlowFieldSample sample);

            string what = "direction " + direction.ToString("F3") + " toward blocked corner " + corner;
            Assert.That(overridden, Is.EqualTo(stops), what);
            if (stops)
            {
                Assert.That(sample.SpeedMultiplier, Is.Zero, what);
                Assert.That(sample.Direction, Is.EqualTo(Vector3.zero), what);
            }
            else
            {
                Assert.That(sample.SpeedMultiplier, Is.EqualTo(1f), what);
                Assert.That(sample.Direction, Is.EqualTo(direction), what);
            }
        }
    }
}
