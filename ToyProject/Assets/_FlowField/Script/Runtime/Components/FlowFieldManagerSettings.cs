using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The cells the field covers, relative to the Manager position.</summary>
    [Serializable]
    internal struct FlowFieldGridSettings
    {
        [Tooltip("Manager 위치 기준 월드 축 정렬 Bake 영역입니다. Surface2D는 XZ가 Grid, Y가 Ground Ray 범위이고, Volume3D는 전체가 셀로 채워집니다.")]
        [SerializeField] internal Bounds BakeBoundsLocal;
        [SerializeField] internal float CellSize;

        internal static FlowFieldGridSettings Default => new FlowFieldGridSettings
        {
            BakeBoundsLocal = new Bounds(new Vector3(20f, 0f, 20f), new Vector3(40f, 10f, 40f)),
            CellSize = 0.5f,
        };
    }

    /// <summary>Settings only Surface2D reads: the ground bake and its obstacle probe.</summary>
    [Serializable]
    internal struct FlowFieldSurfaceSettings
    {
        [SerializeField] internal LayerMask GroundLayer;
        [SerializeField] internal float MaxSlope;
        [SerializeField] internal float MaxStepHeight;
        [Tooltip("셀 위로 장애물을 찾는 높이입니다.")]
        [SerializeField] internal float ObstacleCheckHeight;
        [SerializeField] internal float ObstacleCheckCenterOffset;

        internal static FlowFieldSurfaceSettings Default => new FlowFieldSurfaceSettings
        {
            GroundLayer = Physics.DefaultRaycastLayers,
            MaxSlope = 45f,
            MaxStepHeight = 0.5f,
            ObstacleCheckHeight = 2f,
            ObstacleCheckCenterOffset = 1f,
        };
    }

    /// <summary>Which colliders block cells, in both modes.</summary>
    [Serializable]
    internal struct FlowFieldObstacleSettings
    {
        [SerializeField] internal LayerMask Layer;
        [Tooltip("셀 영역 밖으로 장애물 판정을 확장하는 거리입니다.")]
        [SerializeField] internal float Clearance;
    }

    /// <summary>The direction used where no Goal applies, and the Goal reach.</summary>
    [Serializable]
    internal struct FlowFieldFlowSettings
    {
        [SerializeField] internal Vector3 DefaultDirection;
        [Tooltip("0은 Global hybrid Goal, 양수는 XYZ 구 형태의 Ranged Goal입니다.")]
        [SerializeField] internal float GoalInfluenceRadius;

        internal static FlowFieldFlowSettings Default => new FlowFieldFlowSettings
        {
            DefaultDirection = Vector3.forward,
        };
    }

    /// <summary>The baked fields StaticBaked mode loads, one per space mode.</summary>
    [Serializable]
    internal struct FlowFieldBakeAssets
    {
        [Tooltip("Surface2D 정적 모드에서 사용할 Flow Field Snapshot입니다.")]
        [SerializeField] internal FlowFieldSurfaceBakeData Surface;
        [Tooltip("Volume3D 정적 모드에서 사용할 v6 Flow Field Snapshot입니다.")]
        [SerializeField] internal FlowFieldVolumeBakeData Volume;
    }
}
