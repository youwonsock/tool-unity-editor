using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>The cells the field covers, relative to the Manager position.</summary>
    [Serializable]
    internal struct FlowFieldGridSettings
    {
        [Tooltip("The world-aligned bake region around the Manager. In Surface2D, XZ is the grid and Y the ground ray range; Volume3D fills all of it with cells.")]
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
        [Tooltip("How high above a cell obstacles are searched.")]
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
        [Tooltip("How far obstacle detection extends beyond a cell.")]
        [SerializeField] internal float Clearance;
    }

    /// <summary>The direction used where no Goal applies, and the Goal reach.</summary>
    [Serializable]
    internal struct FlowFieldFlowSettings
    {
        [SerializeField] internal Vector3 DefaultDirection;
        [Tooltip("0 is a global hybrid Goal; a positive value is a ranged Goal within an XYZ sphere.")]
        [SerializeField] internal float GoalInfluenceRadius;

        internal static FlowFieldFlowSettings Default => new FlowFieldFlowSettings
        {
            DefaultDirection = Vector3.forward,
        };
    }

    /// <summary>The baked fields StaticBaked and BakedBase modes load, one per space mode.</summary>
    [Serializable]
    internal struct FlowFieldBakeAssets
    {
        [Tooltip("The Flow Field snapshot a StaticBaked or BakedBase Surface2D Manager uses.")]
        [SerializeField] internal FlowFieldSurfaceBakeData Surface;
        [Tooltip("The Flow Field snapshot a StaticBaked or BakedBase Volume3D Manager uses.")]
        [SerializeField] internal FlowFieldVolumeBakeData Volume;
    }
}
