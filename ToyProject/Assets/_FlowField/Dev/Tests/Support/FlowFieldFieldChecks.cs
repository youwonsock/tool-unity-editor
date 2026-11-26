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
                return;
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
    }
}
