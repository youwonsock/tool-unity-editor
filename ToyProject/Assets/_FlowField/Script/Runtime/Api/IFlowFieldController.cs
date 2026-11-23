using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The contract other systems use to change the FlowField inputs.
    /// </summary>
    public interface IFlowFieldController : IFlowFieldProvider
    {
        /// <summary>Starts a session. Play Mode only; Edit Mode uses Bake and the preview.</summary>
        void Init();
        void RequestRebuild();
        void Release();
        void SetSpaceMode(FlowFieldSpaceMode mode);
        void SetGoal(in FlowFieldGoalRequest request);
        void NotifyCellsDirty();

        bool RegisterDynamicObstacle(Collider collider);
        bool UnregisterDynamicObstacle(Collider collider);
        // FlowField never polls the scene: a registered obstacle that moves
        // must say so, or its cells keep their old state.
        bool NotifyDynamicObstacleMoved(Collider collider);
        void NotifyObstacleRegionDirty(Bounds worldBounds);

        bool RegisterVectorModifier(IFlowFieldVectorModifier modifier);
        bool UnregisterVectorModifier(IFlowFieldVectorModifier modifier);
        void NotifyModifierChanged(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change);
    }
}
