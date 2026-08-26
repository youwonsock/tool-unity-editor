using UnityEngine;

namespace Supercent.Common.FlowField
{
    public readonly struct FlowFieldSample
    {
        public Vector3 Direction { get; }
        public float SpeedMultiplier { get; }
        public Vector3 SurfaceNormal { get; }
        public bool HasSurface { get; }

        public FlowFieldSample(
            Vector3 direction,
            float speedMultiplier,
            Vector3 surfaceNormal,
            bool hasSurface)
        {
            Direction = direction;
            SpeedMultiplier = speedMultiplier;
            SurfaceNormal = surfaceNormal;
            HasSurface = hasSurface;
        }

        internal static FlowFieldSample Stopped
            => new FlowFieldSample(Vector3.zero, 0f, Vector3.up, false);
    }

    public readonly struct FlowFieldVectorState
    {
        public Vector3 Direction { get; }
        public float SpeedMultiplier { get; }

        public FlowFieldVectorState(Vector3 direction, float speedMultiplier)
        {
            Direction = direction;
            SpeedMultiplier = speedMultiplier;
        }

        internal static FlowFieldVectorState Stopped => new FlowFieldVectorState(Vector3.zero, 1f);
    }

    public readonly struct FlowFieldVectorModifierContext
    {
        public int CellIndex { get; }
        public int CellX { get; }
        public int CellZ { get; }
        public Vector3 CellCenter { get; }
        public Vector3 SurfaceNormal { get; }
        public FlowFieldGridSpace GridSpace { get; }
        public bool IsGoalDirected { get; }

        internal FlowFieldVectorModifierContext(
            int cellIndex,
            int cellX,
            int cellZ,
            Vector3 cellCenter,
            Vector3 surfaceNormal,
            FlowFieldGridSpace gridSpace,
            bool isGoalDirected)
        {
            CellIndex = cellIndex;
            CellX = cellX;
            CellZ = cellZ;
            CellCenter = cellCenter;
            SurfaceNormal = surfaceNormal;
            GridSpace = gridSpace;
            IsGoalDirected = isGoalDirected;
        }
    }
}
