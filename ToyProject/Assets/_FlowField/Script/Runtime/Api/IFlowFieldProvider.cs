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
        FlowFieldRuntimeState State { get; }
        bool IsReady { get; }
        bool IsRebuilding { get; }
        bool IsFaulted { get; }
        string LastError { get; }
        int Revision { get; }
        FlowFieldSpaceMode SpaceMode { get; }
        FlowFieldBakeMode BakeMode { get; }
        event Action FieldChanged;
        event Action<FlowFieldRuntimeState> StateChanged;

        bool TrySample(Vector3 worldPosition, out FlowFieldSample sample);
        FlowFieldSample Sample(Vector3 worldPosition);

        FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition);
        bool TryGetFieldInfo(out FlowFieldFieldInfo info);
    }

}
