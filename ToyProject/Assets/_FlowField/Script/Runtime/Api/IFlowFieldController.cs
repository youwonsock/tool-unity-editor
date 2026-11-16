using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// 외부 시스템이 FlowField 계산 입력을 변경하는 계약입니다.
    /// </summary>
    public interface IFlowFieldController : IFlowFieldProvider
    {
        void Init();
        void RequestRebuild();
        void Release();
        void SetSpaceMode(FlowFieldSpaceMode mode);
        void SetGoal(in FlowFieldGoalRequest request);
        void NotifyCellsDirty();

        bool RegisterDynamicObstacle(Collider collider);
        bool UnregisterDynamicObstacle(Collider collider);
        void NotifyObstacleRegionDirty(Bounds worldBounds);

        bool RegisterVectorModifier(IFlowFieldVectorModifier modifier);
        bool UnregisterVectorModifier(IFlowFieldVectorModifier modifier);
        void NotifyModifierChanged(IFlowFieldVectorModifier modifier, FlowFieldModifierChange change);
    }
}
