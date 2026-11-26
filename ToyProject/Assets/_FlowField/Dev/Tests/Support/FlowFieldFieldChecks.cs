using NUnit.Framework;

namespace Common.FlowField.Tests
{
    /// <summary>Rules every published field keeps, whatever its inputs.</summary>
    internal static class FlowFieldFieldChecks
    {
        internal static void AssertInvariants(FlowFieldManager manager)
        {
            Assert.That(manager.TryGetPublishedField(out FlowFieldField field), Is.True);
            AssertInvariants(field);
        }

        internal static void AssertInvariants(FlowFieldField field)
        {
            bool surfaceMode = field.Grid.SpaceMode == FlowFieldSpaceMode.Surface2D;
            // The read path tells the modes apart by the Surface data.
            Assert.That(field.Scene != null, Is.EqualTo(surfaceMode), "Surface data must exist exactly in Surface2D.");
            if (!surfaceMode)
            {
                // Volume3D's Goal topology links every open cell: it is the ground topology.
                Assert.That(field.Agent.GroundTopology, Is.SameAs(field.Goal.Topology), "Volume3D publishes one topology.");
                return;
            }
            AssertGoalTopologyIsGroundInsideInfluence(field);
            // Surface2D links only cells that are walkable, open and inside the
            // Goal's reach, the rule the Surface2D Bake validation also checks.
            int bad = 0;
            int first = -1;
            for (int i = 0; i < field.Goal.Topology.Length; i++)
            {
                if (field.Goal.Topology[i] == 0u)
                    continue;
                bool inside = field.Scene.Surface.IsSurfaceValid(i)
                    && !field.Agent.Blocked[i]
                    && (!field.Goal.IsActive || field.Goal.Flags[i] != FlowFieldGoalFlags.None);
                if (inside)
                    continue;
                bad++;
                if (first < 0)
                    first = i;
            }
            Assert.That(bad, Is.Zero, "Surface2D Topology links " + bad + " cells outside the Goal's reach (first " + first + ").");
        }

        // A Goal link is the ground's link where the cell, its neighbor and,
        // for a diagonal, both corner cells are inside the Goal's influence
        // (a Goal flag; every open cell without an active Goal).
        private static void AssertGoalTopologyIsGroundInsideInfluence(FlowFieldField field)
        {
            FlowFieldGridSpace grid = field.Grid;
            FlowFieldGoalLayer goal = field.Goal;
            uint[] ground = field.Agent.GroundTopology;
            int bad = 0;
            int first = -1;
            for (int i = 0; i < grid.CellCount; i++)
            {
                grid.FromFlatIndex(i, out int x, out int z);
                for (int d = 0; d < FlowFieldNeighborUtility.SurfaceCount; d++)
                {
                    int dx = FlowFieldNeighborUtility.DeltaX[d];
                    int dz = FlowFieldNeighborUtility.DeltaZ[d];
                    bool expected = (ground[i] & (1u << d)) != 0u
                        && Inside(field, grid, x, z)
                        && Inside(field, grid, x + dx, z + dz)
                        && (dx == 0 || dz == 0 || Inside(field, grid, x + dx, z) && Inside(field, grid, x, z + dz));
                    bool actual = (goal.Topology[i] & (1u << d)) != 0u;
                    if (expected == actual)
                        continue;
                    bad++;
                    if (first < 0)
                        first = i;
                }
            }
            Assert.That(bad, Is.Zero,
                "Surface2D Goal topology differs from the ground topology inside the Goal's influence in " + bad
                + " links (first cell " + first + ").");
        }

        private static bool Inside(FlowFieldField field, FlowFieldGridSpace grid, int x, int z)
        {
            if (!grid.TryLocal(x, 0, z, out int index))
                return false;
            return !field.Goal.IsActive || field.Goal.Flags[index] != FlowFieldGoalFlags.None;
        }
    }
}
