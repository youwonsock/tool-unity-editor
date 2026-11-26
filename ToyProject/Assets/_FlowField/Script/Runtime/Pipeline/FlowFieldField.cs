using System;
using Unity.Collections.LowLevel.Unsafe;

namespace Common.FlowField
{
    /// <summary>
    /// A published field, the same shape in both space modes: the grid and
    /// three layers, the scene (Surface2D's ground, null in Volume3D), the
    /// agent (what is blocked) and the Goal (the navigation toward it). Once
    /// published it never changes; a new publication replaces the whole
    /// object, and the arrays it replaced go back to
    /// <see cref="FlowFieldSpareArrays"/>. A reader outside the session may
    /// therefore use a field only until the next pump. Arrays a mode does not
    /// keep are null.
    /// </summary>
    internal class FlowFieldField
    {
        internal readonly FlowFieldGridSpace Grid;
        internal readonly FlowFieldSceneLayer Scene;
        internal readonly FlowFieldAgentLayer Agent;
        internal readonly FlowFieldGoalLayer Goal;

        internal int Capacity => Agent.Blocked.Length;

        /// <summary>
        /// Whether two fields answer every read the same: the cells, their
        /// state and the Goal. A publication with the same content keeps the
        /// Revision, so callers see a change only when there is one. The base
        /// vectors are not compared: only the final ones are read.
        /// </summary>
        internal static bool HasSameContent(FlowFieldField left, FlowFieldField right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null
                || !left.Grid.MatchesBounds(right.Grid)
                || left.Goal.IsActive != right.Goal.IsActive
                || left.Goal.CellIndex != right.Goal.CellIndex)
                return false;
            FlowFieldSurfaceData leftSurface = left.Scene?.Surface;
            FlowFieldSurfaceData rightSurface = right.Scene?.Surface;
            if (!ReferenceEquals(leftSurface, rightSurface)
                && (leftSurface == null || rightSurface == null || !leftSurface.ContentEquals(rightSurface)))
                return false;
            return SameBytes(left.Agent.Blocked, right.Agent.Blocked)
                && SameBytes(left.Goal.Topology, right.Goal.Topology)
                && SameBytes(left.Goal.Flags, right.Goal.Flags)
                && SameBytes(left.Goal.NextCells, right.Goal.NextCells)
                && SameBytes(left.Goal.FinalDirections, right.Goal.FinalDirections)
                && SameBytes(left.Goal.FinalSpeeds, right.Goal.FinalSpeeds)
                && SameBytes(left.Agent.EscapeDirections, right.Agent.EscapeDirections)
                && SameBytes(left.Goal.ModifierInfluence, right.Goal.ModifierInfluence);
        }

        // A bitwise comparison: memcmp over each array.
        private static unsafe bool SameBytes<T>(T[] left, T[] right) where T : unmanaged
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;
            if (left.Length == 0)
                return true;
            fixed (T* a = left)
            fixed (T* b = right)
                return UnsafeUtility.MemCmp(a, b, (long)left.Length * sizeof(T)) == 0;
        }

        internal FlowFieldField(
            FlowFieldGridSpace grid,
            FlowFieldSceneLayer scene,
            FlowFieldAgentLayer agent,
            FlowFieldGoalLayer goal)
        {
            Grid = grid;
            Scene = scene;
            Agent = agent ?? throw new ArgumentNullException(nameof(agent));
            Goal = goal ?? throw new ArgumentNullException(nameof(goal));
        }
    }

    /// <summary>A published field together with the request it was built for.</summary>
    internal sealed class FlowFieldField<TRequest> : FlowFieldField where TRequest : struct
    {
        internal readonly TRequest Request;

        internal FlowFieldField(
            in TRequest request,
            FlowFieldGridSpace grid,
            FlowFieldSceneLayer scene,
            FlowFieldAgentLayer agent,
            FlowFieldGoalLayer goal)
            : base(grid, scene, agent, goal)
        {
            Request = request;
        }
    }
}
