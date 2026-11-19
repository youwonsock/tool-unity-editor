using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The contract movement systems use to read the current FlowField.
    /// It never pushes samples; callers pull them in their own FixedUpdate.
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
