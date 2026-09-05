using System;
using UnityEngine;

namespace Common.FlowField
{
    internal delegate bool FlowFieldSurfaceBakeProgress(int row, int rowCount);

    internal static class FlowFieldSurfacePipeline
    {
        internal static bool TryValidate(in FlowFieldSurfaceBakeSettings settings, out string reason)
        {
            reason = settings.IsValid ? string.Empty : "Bake Bounds, Cell Size 또는 Ground 설정이 유효하지 않습니다.";
            return settings.IsValid;
        }
    }

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

    internal static class FlowFieldSurfaceBaker
    {
        private const float NORMAL_EPSILON_SQR = 0.000001f;
        private const float BOUNDS_QUERY_EPSILON = 0.001f;

        public static FlowFieldSurfaceBakeResult Bake(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeProgress progress = null)
        {
            if (!settings.IsValid)
                throw new System.ArgumentException("Grid, Ground Layer 또는 Bake Bounds 설정이 유효하지 않습니다.", nameof(settings));

            FlowFieldGridSpace grid = settings.Grid;
            if (!FlowFieldBakeBoundsUtility.TryValidateCellCount(
                    grid.Width,
                    grid.Depth,
                    out int cellCount))
                throw new System.ArgumentOutOfRangeException(nameof(settings),
                    $"Cell Count가 상한({FlowFieldBakeBoundsUtility.MaxCellCount:N0})을 초과합니다. "
                    + $"현재 {grid.Width} × {grid.Depth} cells입니다. Bake Bounds 또는 Cell Size를 줄이세요.");

            Physics.SyncTransforms();
            FlowFieldSurfaceBakeResult result = new FlowFieldSurfaceBakeResult(cellCount);
            for (int z = 0; z < grid.Depth; z++)
            {
                if (progress != null && !progress(z, grid.Depth))
                    throw new OperationCanceledException("FlowField Surface Bake was cancelled.");
                for (int x = 0; x < grid.Width; x++)
                {
                    int index = grid.ToFlatIndex(x, z);
                    Vector3 origin = grid.LocalToWorldCenter(x, z);
                    if (!TryFindTopmostHit(settings, origin, out RaycastHit hit))
                        continue;

                    Vector3 normal = hit.normal;
                    if (!FlowFieldGridSpace.IsFinite(hit.point)
                        || !FlowFieldGridSpace.IsFinite(normal)
                        || normal.sqrMagnitude <= NORMAL_EPSILON_SQR)
                        continue;

                    normal.Normalize();
                    if (Vector3.Dot(normal, Vector3.up) <= 0f
                        || Vector3.Angle(normal, Vector3.up) > settings.MaxSurfaceSlope)
                        continue;

                    result.SetSurface(index, hit.point.y, normal);
                }
            }

            if (result.ValidCellCount <= 0)
                throw new System.InvalidOperationException("Bake Bounds 범위에서 이동 가능한 Ground Collider를 찾지 못했습니다.");

            BuildNeighborMasks(settings, result);
            return result;
        }

        private static bool TryFindTopmostHit(
            in FlowFieldSurfaceBakeSettings settings,
            Vector3 cellCenter,
            out RaycastHit topmostHit)
        {
            Bounds bounds = settings.BakeBounds;
            Vector3 origin = new Vector3(
                cellCenter.x,
                bounds.max.y + BOUNDS_QUERY_EPSILON,
                cellCenter.z);
            float distance = bounds.size.y + BOUNDS_QUERY_EPSILON * 2f;
            if (!Physics.Raycast(
                    origin,
                    Vector3.down,
                    out topmostHit,
                    distance,
                    settings.GroundLayer,
                    QueryTriggerInteraction.Ignore))
            {
                topmostHit = default;
                return false;
            }

            if (!FlowFieldGridSpace.IsFinite(topmostHit.point))
            {
                topmostHit = default;
                return false;
            }

            float height = topmostHit.point.y;
            if (height < bounds.min.y || height > bounds.max.y)
            {
                topmostHit = default;
                return false;
            }

            return true;
        }

        private static void BuildNeighborMasks(
            in FlowFieldSurfaceBakeSettings settings,
            FlowFieldSurfaceBakeResult result)
        {
            FlowFieldGridSpace grid = settings.Grid;
            for (int z = 0; z < grid.Depth; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    int index = grid.ToFlatIndex(x, z);
                    if (!IsValid(result, index))
                        continue;

                    byte mask = 0;
                    for (int directionIndex = 0; directionIndex < FlowFieldNeighborUtility.Count; directionIndex++)
                    {
                        int dx = FlowFieldNeighborUtility.DeltaX[directionIndex];
                        int dz = FlowFieldNeighborUtility.DeltaZ[directionIndex];
                        int nx = x + dx;
                        int nz = z + dz;
                        if (!grid.IsLocalInBounds(nx, nz))
                            continue;

                        int neighbor = grid.ToFlatIndex(nx, nz);
                        if (!CanConnect(result, index, neighbor, settings.MaxStepHeight))
                            continue;

                        if (FlowFieldNeighborUtility.IsDiagonal(directionIndex)
                            && !CanConnectDiagonal(
                                grid,
                                result,
                                x,
                                z,
                                dx,
                                dz,
                                settings.MaxStepHeight))
                            continue;

                        mask |= (byte)(1 << directionIndex);
                    }

                    result.NeighborMasks[index] = mask;
                }
            }
        }

        private static bool CanConnectDiagonal(
            FlowFieldGridSpace grid,
            FlowFieldSurfaceBakeResult result,
            int x,
            int z,
            int dx,
            int dz,
            float maxStepHeight)
        {
            int a = grid.ToFlatIndex(x, z);
            int b = grid.ToFlatIndex(x + dx, z);
            int c = grid.ToFlatIndex(x, z + dz);
            int d = grid.ToFlatIndex(x + dx, z + dz);
            return CanConnect(result, a, b, maxStepHeight)
                && CanConnect(result, a, c, maxStepHeight)
                && CanConnect(result, b, d, maxStepHeight)
                && CanConnect(result, c, d, maxStepHeight);
        }

        private static bool CanConnect(
            FlowFieldSurfaceBakeResult result,
            int left,
            int right,
            float maxStepHeight)
            => IsValid(result, left)
                && IsValid(result, right)
                && Mathf.Abs(result.SurfaceHeights[left] - result.SurfaceHeights[right]) <= maxStepHeight;

        private static bool IsValid(FlowFieldSurfaceBakeResult result, int index)
            => (result.CellFlags[index] & 1) != 0;
    }
}
