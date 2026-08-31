using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// 외부 이동 시스템이 현재 FlowField를 읽는 계약입니다.
    /// 구현체는 샘플을 Push하지 않으며 호출자가 자신의 FixedUpdate에서 Pull합니다.
    /// </summary>
    public interface IFlowFieldProvider
    {
        bool IsInitialized { get; }
        bool IsReady { get; }
        int Revision { get; }
        event Action FieldChanged;

        FlowFieldSample Sample(Vector3 worldPosition);

        FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition);
    }

    /// <summary>
    /// 외부 시스템이 FlowField 계산 입력을 변경하는 계약입니다.
    /// </summary>
    public interface IFlowFieldController
    {
        bool IsInitialized { get; }
        void Init();
        void Rebuild();
        void Release();

        void SetGoalPosition(Vector3 worldPosition);
        void SetGoalPosition(Vector3 worldPosition, float influenceRadius);
        void SetGoalTarget(Transform target);
        void SetGoalTarget(Transform target, float influenceRadius);
        void ClearGoal();

        void RegisterDynamicObstacle(Collider collider);
        void UnregisterDynamicObstacle(Collider collider);
        void NotifyObstacleRegionDirty(Bounds worldBounds);

        void RegisterVectorModifier(IFlowFieldVectorModifier modifier);
        void UnregisterVectorModifier(IFlowFieldVectorModifier modifier);
        void MarkVectorModifierDirty(IFlowFieldVectorModifier modifier);
        void MarkVectorModifierAreaDirty(IFlowFieldVectorModifier modifier);
    }
}
