using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The registered dynamic obstacles as a builder reads them; only the session changes them.</summary>
    internal interface IFlowFieldObstacleView
    {
        int Count { get; }
        Collider GetCollider(int index);
        // Where the Collider was last reported: registration or a notified move.
        Bounds GetBounds(int index);
        bool Contains(Collider collider);
    }

    /// <summary>The registered Modifiers as a builder reads them; only the session changes them.</summary>
    internal interface IFlowFieldModifierView
    {
        // In Priority order.
        IReadOnlyList<FlowFieldModifierEntry> Entries { get; }
        bool Contains(IFlowFieldVectorModifier modifier);
        // Every Modifier's snapshot and Influence Collider, in Priority order.
        FlowFieldModifierWorkItem[] CaptureSnapshots();
    }

    /// <summary>The uncertain regions as a builder reads them to limit its obstacle queries.</summary>
    internal interface IFlowFieldUncertainRegions
    {
        int UncertainRegionCount { get; }
        // The regions the running build has taken in and still has to publish.
        Bounds[] ObservedRegions();
    }

    /// <summary>
    /// What a space builder reads from the session that runs it: the shared
    /// registrations, the uncertain regions, whether the obstacle sweep runs,
    /// and the scope that marks a call into Modifier code. The session keeps
    /// the lifecycle, the Revision and every change to these; a builder holds
    /// no reference to the session itself.
    /// </summary>
    internal interface IFlowFieldBuildHost
    {
        IFlowFieldObstacleView Obstacles { get; }
        IFlowFieldModifierView Modifiers { get; }
        IFlowFieldUncertainRegions UncertainRegions { get; }
        bool SweepsObstacles { get; }
        // Commands issued from inside wait until the scope ends.
        FlowFieldExecutionBoundary.OperationScope EnterUserCode();
    }
}
