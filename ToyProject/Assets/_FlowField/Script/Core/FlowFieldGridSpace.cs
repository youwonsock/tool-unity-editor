using System;
using UnityEngine;

namespace Common.FlowField
{
    public readonly struct FlowFieldGridSpace
    {
        private const float MIN_CLAMP_EPSILON = 0.00001f;

        public Vector3 Origin { get; }
        public float WorldSizeX { get; }
        public float WorldSizeZ { get; }
        public int Width { get; }
        public int Depth { get; }
        public float CellSize { get; }
        public int CellCount => IsValid ? (int)((long)Width * Depth) : 0;
        public bool IsValid => Width > 0
            && Depth > 0
            && IsFinite(Origin)
            && IsFinite(CellSize)
            && CellSize >= FlowFieldBakeBoundsUtility.MinCellSize
            && (long)Width * Depth <= int.MaxValue;

        private FlowFieldGridSpace(Vector3 origin, int width, int depth, float cellSize)
        {
            Origin = origin;
            Width = width;
            Depth = depth;
            CellSize = cellSize;
            WorldSizeX = width * cellSize;
            WorldSizeZ = depth * cellSize;
        }

        public static FlowFieldGridSpace FromCellGrid(Vector3 origin, int width, int depth, float cellSize)
        {
            if (!IsFinite(origin))
                throw new System.ArgumentOutOfRangeException(nameof(origin));
            if (width <= 0 || depth <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(width));
            if ((long)width * depth > FlowFieldBakeBoundsUtility.MaxCellCount)
                throw new System.ArgumentOutOfRangeException(nameof(width), "Grid cell count exceeds the supported limit.");
            if (!IsFinite(cellSize) || cellSize < FlowFieldBakeBoundsUtility.MinCellSize)
                throw new System.ArgumentOutOfRangeException(nameof(cellSize));

            return new FlowFieldGridSpace(
                origin,
                width,
                depth,
                cellSize);
        }

        public bool ContainsWorldPosition(Vector3 world)
        {
            if (!IsValid || !IsFinite(world.x) || !IsFinite(world.z))
                return false;

            return world.x >= Origin.x
                && world.x < Origin.x + WorldSizeX
                && world.z >= Origin.z
                && world.z < Origin.z + WorldSizeZ;
        }

        /// <summary>
        /// 월드 좌표를 Grid의 로컬 셀 좌표로 변환합니다. Grid 밖 좌표는
        /// 예외가 아닌 정상적인 조회 실패로 false를 반환합니다.
        /// </summary>
        /// <returns>좌표가 Grid 안이면 true, 밖이면 false입니다.</returns>
        public bool TryWorldToLocal(Vector3 world, out int localX, out int localZ)
        {
            localX = 0;
            localZ = 0;
            if (!ContainsWorldPosition(world))
                return false;

            localX = Mathf.FloorToInt((world.x - Origin.x) / CellSize);
            localZ = Mathf.FloorToInt((world.z - Origin.z) / CellSize);
            return IsLocalInBounds(localX, localZ);
        }

        /// <summary>
        /// 월드 좌표를 가장 가까운 Grid 셀로 명시적으로 Clamp해 변환합니다.
        /// 유한하지 않은 좌표나 초기화되지 않은 Grid는 false라는 정상 결과입니다.
        /// </summary>
        /// <returns>Clamp 가능한 Grid면 true, 아니면 false입니다.</returns>
        public bool TryWorldToLocalClamped(Vector3 world, out int localX, out int localZ)
        {
            localX = 0;
            localZ = 0;
            if (!IsValid || !IsFinite(world.x) || !IsFinite(world.z))
                return false;

            localX = Mathf.Clamp(Mathf.FloorToInt((world.x - Origin.x) / CellSize), 0, Width - 1);
            localZ = Mathf.Clamp(Mathf.FloorToInt((world.z - Origin.z) / CellSize), 0, Depth - 1);
            return true;
        }

        public Vector3 LocalToWorldCenter(int localX, int localZ)
        {
            float halfCell = CellSize * 0.5f;
            return Origin + new Vector3(localX * CellSize + halfCell, 0f, localZ * CellSize + halfCell);
        }

        public int ToFlatIndex(int localX, int localZ) => localZ * Width + localX;

        public void FromFlatIndex(int index, out int localX, out int localZ)
        {
            localZ = index / Width;
            localX = index - localZ * Width;
        }

        public bool IsLocalInBounds(int localX, int localZ)
            => IsLocalInBounds(localX, localZ, Width, Depth);

        public Vector3 ClampWorldXZ(Vector3 world)
        {
            if (!IsValid)
                throw new System.InvalidOperationException("Cannot clamp a position with an invalid Grid.");
            if (!IsFinite(world.x) || !IsFinite(world.z))
                throw new System.ArgumentOutOfRangeException(nameof(world));

            float epsilon = Mathf.Max(MIN_CLAMP_EPSILON, CellSize * 0.0001f);
            world.x = Mathf.Clamp(world.x, Origin.x, Origin.x + WorldSizeX - epsilon);
            world.z = Mathf.Clamp(world.z, Origin.z, Origin.z + WorldSizeZ - epsilon);
            return world;
        }

        public bool MatchesBounds(FlowFieldGridSpace other)
        {
            return IsValid
                && other.IsValid
                && Width == other.Width
                && Depth == other.Depth
                && Mathf.Abs(CellSize - other.CellSize) <= 0.0001f
                && (Origin - other.Origin).sqrMagnitude <= 0.000001f;
        }

        /// <summary>
        /// Bounds와 겹치는 셀 범위를 계산합니다. Grid와 겹치지 않는 Bounds는
        /// 결과 없음으로 false를 반환합니다.
        /// </summary>
        /// <returns>겹치는 셀이 있으면 true, 없으면 false입니다.</returns>
        public bool TryGetOverlappingCells(
            Bounds worldBounds,
            out int minX,
            out int maxX,
            out int minZ,
            out int maxZ)
        {
            minX = 1;
            maxX = 0;
            minZ = 1;
            maxZ = 0;
            if (!IsValid)
                return false;

            float gridMaxX = Origin.x + WorldSizeX;
            float gridMaxZ = Origin.z + WorldSizeZ;
            if (worldBounds.max.x < Origin.x
                || worldBounds.min.x > gridMaxX
                || worldBounds.max.z < Origin.z
                || worldBounds.min.z > gridMaxZ)
                return false;

            float inverseCellSize = 1f / CellSize;
            minX = Mathf.Clamp(
                Mathf.FloorToInt((worldBounds.min.x - Origin.x) * inverseCellSize),
                0,
                Width - 1);
            maxX = Mathf.Clamp(
                Mathf.FloorToInt((worldBounds.max.x - Origin.x) * inverseCellSize),
                0,
                Width - 1);
            minZ = Mathf.Clamp(
                Mathf.FloorToInt((worldBounds.min.z - Origin.z) * inverseCellSize),
                0,
                Depth - 1);
            maxZ = Mathf.Clamp(
                Mathf.FloorToInt((worldBounds.max.z - Origin.z) * inverseCellSize),
                0,
                Depth - 1);
            return minX <= maxX && minZ <= maxZ;
        }

        public static bool IsLocalInBounds(int localX, int localZ, int width, int depth)
            => localX >= 0 && localX < width && localZ >= 0 && localZ < depth;

        internal static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    internal static class FlowFieldBakeBoundsUtility
    {
        public const float MinCellSize = 0.01f;
        public const float MinBoundsHeight = 0.01f;
        public const int MaxCellCount = 100000;
        private const float CompareEpsilon = 0.0001f;

        public static Bounds DefaultLocalBounds
            => new Bounds(new Vector3(20f, 0f, 20f), new Vector3(40f, 10f, 40f));

        public static float ValidateCellSize(float cellSize)
        {
            if (!FlowFieldGridSpace.IsFinite(cellSize) || cellSize < MinCellSize)
                throw new System.ArgumentOutOfRangeException(nameof(cellSize));
            return cellSize;
        }

        public static Bounds SnapCenterAnchored(Bounds bounds, float cellSize)
        {
            float resolvedCellSize = ValidateCellSize(cellSize);
            if (!FlowFieldGridSpace.IsFinite(bounds.center)
                || !FlowFieldGridSpace.IsFinite(bounds.size)
                || bounds.size.x <= 0f || bounds.size.y <= 0f || bounds.size.z <= 0f)
                throw new System.ArgumentOutOfRangeException(nameof(bounds));
            Vector3 center = bounds.center;
            Vector3 size = bounds.size;
            size.x = SnapHorizontalSize(size.x, resolvedCellSize);
            if (size.y < MinBoundsHeight)
                throw new System.ArgumentOutOfRangeException(nameof(bounds), "Bake Bounds height is below the minimum.");
            size.z = SnapHorizontalSize(size.z, resolvedCellSize);
            return new Bounds(center, size);
        }

        public static Bounds SnapResizedKeepingOppositeFace(
            Bounds previous,
            Bounds candidate,
            float cellSize)
        {
            Bounds resolvedPrevious = SnapCenterAnchored(previous, cellSize);
            Bounds resolvedCandidate = SnapCenterAnchored(candidate, cellSize);
            if (!FlowFieldGridSpace.IsFinite(previous.center)
                || !FlowFieldGridSpace.IsFinite(previous.size)
                || !FlowFieldGridSpace.IsFinite(candidate.center)
                || !FlowFieldGridSpace.IsFinite(candidate.size))
                throw new System.ArgumentOutOfRangeException(nameof(candidate));

            Vector3 previousMin = resolvedPrevious.min;
            Vector3 previousMax = resolvedPrevious.max;
            Vector3 candidateMin = candidate.min;
            Vector3 candidateMax = candidate.max;
            Vector3 resolvedCenter = resolvedCandidate.center;
            Vector3 resolvedSize = resolvedCandidate.size;
            resolvedCenter.x = ResolveAnchoredCenter(
                previousMin.x,
                previousMax.x,
                candidateMin.x,
                candidateMax.x,
                resolvedSize.x);
            resolvedCenter.y = ResolveAnchoredCenter(
                previousMin.y,
                previousMax.y,
                candidateMin.y,
                candidateMax.y,
                resolvedSize.y);
            resolvedCenter.z = ResolveAnchoredCenter(
                previousMin.z,
                previousMax.z,
                candidateMin.z,
                candidateMax.z,
                resolvedSize.z);
            return new Bounds(resolvedCenter, resolvedSize);
        }

        /// <summary>
        /// Bake Bounds에서 월드 Grid 레이아웃을 계산합니다. 입력이 아직 유효한지
        /// 확인하는 저수준 검사이며, 표현할 수 없는 레이아웃이면 false를 반환합니다.
        /// </summary>
        /// <returns>레이아웃이 유효하면 true, 입력이 유효하지 않으면 false입니다.</returns>
        public static bool TryCreateWorldLayout(
            Vector3 managerWorldPosition,
            Bounds localBounds,
            float cellSize,
            out Bounds worldBounds,
            out FlowFieldGridSpace grid)
        {
            worldBounds = default;
            grid = default;
            if (!FlowFieldGridSpace.IsFinite(managerWorldPosition)
                || !FlowFieldGridSpace.IsFinite(localBounds.center)
                || !FlowFieldGridSpace.IsFinite(localBounds.size)
                || !FlowFieldGridSpace.IsFinite(cellSize)
                || cellSize < MinCellSize
                || localBounds.size.x <= 0f
                || localBounds.size.y < MinBoundsHeight
                || localBounds.size.z <= 0f)
            {
                return false;
            }

            int width = CalculateCellCount(localBounds.size.x, cellSize);
            int depth = CalculateCellCount(localBounds.size.z, cellSize);
            if (!TryValidateCellCount(width, depth, out _))
                return false;

            Bounds snapped = new Bounds(
                localBounds.center,
                new Vector3(width * cellSize, localBounds.size.y, depth * cellSize));
            worldBounds = new Bounds(managerWorldPosition + snapped.center, snapped.size);
            Vector3 origin = new Vector3(worldBounds.min.x, worldBounds.center.y, worldBounds.min.z);
            grid = FlowFieldGridSpace.FromCellGrid(origin, width, depth, cellSize);
            return grid.IsValid;
        }

        /// <summary>
        /// Grid 셀 수가 양수이고 지원 상한 안에 있는지 확인합니다. 범위를 벗어난
        /// 설정을 편집기에서 진단할 수 있도록 false를 정상적인 검증 결과로 반환합니다.
        /// </summary>
        /// <returns>셀 수가 지원 범위면 true, 아니면 false입니다.</returns>
        public static bool TryValidateCellCount(
            int width,
            int depth,
            out int cellCount)
        {
            cellCount = 0;
            if (width <= 0 || depth <= 0)
                return false;

            long count = (long)width * depth;
            if (count <= 0 || count > MaxCellCount || count > int.MaxValue)
                return false;

            cellCount = (int)count;
            return true;
        }

        public static bool Approximately(Bounds left, Bounds right)
            => (left.center - right.center).sqrMagnitude <= CompareEpsilon * CompareEpsilon
                && (left.size - right.size).sqrMagnitude <= CompareEpsilon * CompareEpsilon;

        private static int CalculateCellCount(float size, float cellSize)
        {
            if (!FlowFieldGridSpace.IsFinite(size) || size <= 0f)
                throw new System.ArgumentOutOfRangeException(nameof(size));
            int count = Mathf.FloorToInt(size / cellSize + 0.5f);
            if (count <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(size), "Bounds do not contain any cells.");
            return count;
        }

        private static float SnapHorizontalSize(float size, float cellSize)
            => CalculateCellCount(size, cellSize) * cellSize;

        private static float ResolveAnchoredCenter(
            float previousMin,
            float previousMax,
            float candidateMin,
            float candidateMax,
            float snappedSize)
        {
            float minDelta = Mathf.Abs(candidateMin - previousMin);
            float maxDelta = Mathf.Abs(candidateMax - previousMax);
            return minDelta > maxDelta
                ? previousMax - snappedSize * 0.5f
                : previousMin + snappedSize * 0.5f;
        }
    }

    internal struct FlowFieldCellRect
    {
        public int MinX;
        public int MaxX;
        public int MinZ;
        public int MaxZ;
        public bool IsValid => MinX <= MaxX && MinZ <= MaxZ;

        public static FlowFieldCellRect Invalid => new FlowFieldCellRect
        {
            MinX = 1,
            MaxX = 0,
            MinZ = 1,
            MaxZ = 0,
        };

        public static FlowFieldCellRect Full(FlowFieldGridSpace grid)
        {
            if (!grid.IsValid)
                return Invalid;

            return new FlowFieldCellRect
            {
                MinX = 0,
                MaxX = grid.Width - 1,
                MinZ = 0,
                MaxZ = grid.Depth - 1,
            };
        }

        public static FlowFieldCellRect FromBounds(FlowFieldGridSpace grid, Bounds worldBounds)
        {
            if (!grid.TryGetOverlappingCells(
                    worldBounds,
                    out int minX,
                    out int maxX,
                    out int minZ,
                    out int maxZ))
                return Invalid;

            return new FlowFieldCellRect
            {
                MinX = minX,
                MaxX = maxX,
                MinZ = minZ,
                MaxZ = maxZ,
            };
        }

        public FlowFieldCellRect Expand(FlowFieldGridSpace grid, int ring)
        {
            if (!IsValid || !grid.IsValid)
                return Invalid;

            return new FlowFieldCellRect
            {
                MinX = Mathf.Max(0, MinX - ring),
                MaxX = Mathf.Min(grid.Width - 1, MaxX + ring),
                MinZ = Mathf.Max(0, MinZ - ring),
                MaxZ = Mathf.Min(grid.Depth - 1, MaxZ + ring),
            };
        }

        public static FlowFieldCellRect Union(FlowFieldCellRect left, FlowFieldCellRect right)
        {
            if (!left.IsValid)
                return right;
            if (!right.IsValid)
                return left;

            return new FlowFieldCellRect
            {
                MinX = Math.Min(left.MinX, right.MinX),
                MaxX = Math.Max(left.MaxX, right.MaxX),
                MinZ = Math.Min(left.MinZ, right.MinZ),
                MaxZ = Math.Max(left.MaxZ, right.MaxZ),
            };
        }

        public bool Overlaps(FlowFieldCellRect other)
        {
            if (!IsValid || !other.IsValid)
                return false;

            return MinX <= other.MaxX
                && MaxX >= other.MinX
                && MinZ <= other.MaxZ
                && MaxZ >= other.MinZ;
        }

        public int CellCountEstimate => IsValid
            ? (MaxX - MinX + 1) * (MaxZ - MinZ + 1)
            : 0;
    }

    internal static class FlowFieldNeighborUtility
    {
        public const int Count = 8;
        public static readonly int[] DeltaX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        public static readonly int[] DeltaZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        public static bool IsDiagonal(int directionIndex)
            => directionIndex >= 4;

        public static int FindDirectionIndex(int deltaX, int deltaZ)
        {
            for (int i = 0; i < Count; i++)
            {
                if (DeltaX[i] == deltaX && DeltaZ[i] == deltaZ)
                    return i;
            }

            return -1;
        }
    }
}
