using System;

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
        /// Whether two fields answer every read the same: the grid and each
        /// layer's content. A publication with the same content keeps the
        /// Revision, so callers see a change only when there is one.
        /// </summary>
        internal static bool HasSameContent(FlowFieldField left, FlowFieldField right)
        {
            if (ReferenceEquals(left, right))
                return true;
            return left != null && right != null
                && left.Grid.MatchesBounds(right.Grid)
                && FlowFieldSceneLayer.SameContent(left.Scene, right.Scene)
                && FlowFieldAgentLayer.SameContent(left.Agent, right.Agent)
                && FlowFieldGoalLayer.SameContent(left.Goal, right.Goal);
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
