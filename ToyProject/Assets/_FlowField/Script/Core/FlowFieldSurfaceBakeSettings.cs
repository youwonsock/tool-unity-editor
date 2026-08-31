using UnityEngine;

namespace Common.FlowField
{
    internal readonly struct FlowFieldSurfaceBakeSettings
    {
        public FlowFieldGridSpace Grid { get; }
        public Bounds BakeBounds { get; }
        public LayerMask GroundLayer { get; }
        public float MaxSurfaceSlope { get; }
        public float MaxStepHeight { get; }

        public FlowFieldSurfaceBakeSettings(
            FlowFieldGridSpace grid,
            Bounds bakeBounds,
            LayerMask groundLayer,
            float maxSurfaceSlope,
            float maxStepHeight)
        {
            Grid = grid;
            BakeBounds = bakeBounds;
            GroundLayer = groundLayer;
            MaxSurfaceSlope = maxSurfaceSlope;
            MaxStepHeight = maxStepHeight;
        }

        public bool IsValid => Grid.IsValid
            && GroundLayer.value != 0
            && FlowFieldGridSpace.IsFinite(BakeBounds.center)
            && FlowFieldGridSpace.IsFinite(BakeBounds.size)
            && BakeBounds.size.x > 0f
            && BakeBounds.size.y >= FlowFieldBakeBoundsUtility.MinBoundsHeight
            && BakeBounds.size.z > 0f
            && Mathf.Abs(BakeBounds.size.x - Grid.WorldSizeX) <= 0.0001f
            && Mathf.Abs(BakeBounds.size.z - Grid.WorldSizeZ) <= 0.0001f
            && Mathf.Abs(BakeBounds.min.x - Grid.Origin.x) <= 0.0001f
            && Mathf.Abs(BakeBounds.min.z - Grid.Origin.z) <= 0.0001f
            && Mathf.Abs(BakeBounds.center.y - Grid.Origin.y) <= 0.0001f
            && FlowFieldGridSpace.IsFinite(MaxSurfaceSlope)
            && MaxSurfaceSlope >= 0f
            && MaxSurfaceSlope < 90f
            && FlowFieldGridSpace.IsFinite(MaxStepHeight)
            && MaxStepHeight >= 0f;
    }

    internal sealed class FlowFieldSurfaceBakeResult
    {
        public float[] SurfaceHeights { get; }
        public Vector3[] SurfaceNormals { get; }
        public byte[] CellFlags { get; }
        public byte[] NeighborMasks { get; }
        public int ValidCellCount { get; internal set; }

        public FlowFieldSurfaceBakeResult(int cellCount)
        {
            if (cellCount <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(cellCount));
            SurfaceHeights = new float[cellCount];
            SurfaceNormals = new Vector3[cellCount];
            CellFlags = new byte[cellCount];
            NeighborMasks = new byte[cellCount];
        }

        public bool IsValidFor(int cellCount)
            => cellCount > 0
                && ValidCellCount > 0
                && SurfaceHeights.Length == cellCount
                && SurfaceNormals.Length == cellCount
                && CellFlags.Length == cellCount
                && NeighborMasks.Length == cellCount;

        internal void SetSurface(int index, float height, Vector3 normal)
        {
            SurfaceHeights[index] = height;
            SurfaceNormals[index] = normal;
            CellFlags[index] = 1;
            ValidCellCount++;
        }
    }
}
