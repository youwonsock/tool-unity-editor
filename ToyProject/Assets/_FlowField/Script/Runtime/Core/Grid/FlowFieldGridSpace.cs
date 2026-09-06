using System;
using UnityEngine;

namespace Common.FlowField
{
    public readonly struct FlowFieldGridSpace
    {
        private const float MIN_CLAMP_EPSILON = 0.00001f;

        public Vector3 Origin { get; }
        public float WorldSizeX { get; }
        public float WorldSizeY { get; }
        public float WorldSizeZ { get; }
        public int Width { get; }
        public int Height { get; }
        public int Depth { get; }
        public float CellSize { get; }
        public FlowFieldSpaceMode SpaceMode { get; }
        public int CellCount => IsValid ? (int)((long)Width * Height * Depth) : 0;
        public bool IsValid => Width > 0
            && Height > 0
            && Depth > 0
            && IsFinite(Origin)
            && IsFinite(CellSize)
            && IsFinite(WorldSizeX)
            && IsFinite(WorldSizeY)
            && IsFinite(WorldSizeZ)
            && CellSize >= FlowFieldBakeBoundsUtility.MinCellSize
            && (SpaceMode == FlowFieldSpaceMode.Volume3D
                ? FlowFieldBakeBoundsUtility.TryValidateVolumeCellCount(Width, Height, Depth, out _)
                : FlowFieldBakeBoundsUtility.TryValidateCellCount(Width, Depth, out _));

        private FlowFieldGridSpace(
            Vector3 origin,
            int width,
            int height,
            int depth,
            float cellSize,
            FlowFieldSpaceMode spaceMode)
        {
            Origin = origin;
            Width = width;
            Height = height;
            Depth = depth;
            CellSize = cellSize;
            SpaceMode = spaceMode;
            WorldSizeX = (float)((double)width * cellSize);
            WorldSizeY = (float)((double)height * cellSize);
            WorldSizeZ = (float)((double)depth * cellSize);
        }

        public static FlowFieldGridSpace FromCellGrid(Vector3 origin, int width, int depth, float cellSize)
        {
            if (!IsFinite(origin))
                throw new System.ArgumentOutOfRangeException(nameof(origin));
            if (width <= 0 || depth <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(width));
            if (!FlowFieldBakeBoundsUtility.TryValidateCellCount(width, depth, out _))
                throw new System.ArgumentOutOfRangeException(nameof(width), "Grid cell count exceeds the supported limit.");
            if (!IsFinite(cellSize) || cellSize < FlowFieldBakeBoundsUtility.MinCellSize)
                throw new System.ArgumentOutOfRangeException(nameof(cellSize));
            if (!IsFinite((float)((double)width * cellSize))
                || !IsFinite((float)((double)depth * cellSize)))
                throw new System.ArgumentOutOfRangeException(nameof(cellSize));

            return new FlowFieldGridSpace(
                origin,
                width,
                1,
                depth,
                cellSize,
                FlowFieldSpaceMode.Surface2D);
        }

        public static FlowFieldGridSpace FromVolumeCellGrid(
            Vector3 origin,
            int width,
            int height,
            int depth,
            float cellSize)
        {
            if (!IsFinite(origin))
                throw new ArgumentOutOfRangeException(nameof(origin));
            if (width <= 0 || height <= 0 || depth <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (!FlowFieldBakeBoundsUtility.TryValidateVolumeCellCount(width, height, depth, out _))
                throw new ArgumentOutOfRangeException(nameof(width), "Volume cell count exceeds the supported limit.");
            if (!IsFinite(cellSize) || cellSize < FlowFieldBakeBoundsUtility.MinCellSize)
                throw new ArgumentOutOfRangeException(nameof(cellSize));
            if (!IsFinite((float)((double)width * cellSize))
                || !IsFinite((float)((double)height * cellSize))
                || !IsFinite((float)((double)depth * cellSize)))
                throw new ArgumentOutOfRangeException(nameof(cellSize));

            return new FlowFieldGridSpace(
                origin,
                width,
                height,
                depth,
                cellSize,
                FlowFieldSpaceMode.Volume3D);
        }

        public bool ContainsWorldPosition(Vector3 world)
        {
            if (!IsValid || !IsFinite(world.x) || !IsFinite(world.y) || !IsFinite(world.z))
                return false;

            bool horizontal = (double)world.x >= Origin.x
                && (double)world.x < (double)Origin.x + WorldSizeX
                && (double)world.z >= Origin.z
                && (double)world.z < (double)Origin.z + WorldSizeZ;
            if (!horizontal)
                return false;
            return SpaceMode != FlowFieldSpaceMode.Volume3D
                || (double)world.y >= Origin.y
                    && (double)world.y < (double)Origin.y + WorldSizeY;
        }

        /// <summary>
        /// 월드 좌표를 Grid의 로컬 셀 좌표로 변환합니다. Grid 밖 좌표는
        /// 예외가 아닌 정상적인 조회 실패로 false를 반환합니다.
        /// </summary>
        /// <returns>좌표가 Grid 안이면 true, 밖이면 false입니다.</returns>
        public bool TryWorldToLocal(Vector3 world, out int localX, out int localZ)
        {
            ThrowIfVolumeForSurfaceApi();
            localX = 0;
            localZ = 0;
            if (!ContainsWorldPosition(world))
                return false;

            localX = FloorCellCoordinate((double)world.x - Origin.x, CellSize);
            localZ = FloorCellCoordinate((double)world.z - Origin.z, CellSize);
            return IsLocalInBounds(localX, localZ);
        }

        public bool TryWorldToLocal(
            Vector3 world,
            out int localX,
            out int localY,
            out int localZ)
        {
            localX = 0;
            localY = 0;
            localZ = 0;
            if (!ContainsWorldPosition(world))
                return false;

            localX = FloorCellCoordinate((double)world.x - Origin.x, CellSize);
            localY = SpaceMode == FlowFieldSpaceMode.Volume3D
                ? FloorCellCoordinate((double)world.y - Origin.y, CellSize)
                : 0;
            localZ = FloorCellCoordinate((double)world.z - Origin.z, CellSize);
            return IsLocalInBounds(localX, localY, localZ);
        }

        /// <summary>
        /// 월드 좌표를 가장 가까운 Grid 셀로 명시적으로 Clamp해 변환합니다.
        /// 유한하지 않은 좌표나 초기화되지 않은 Grid는 false라는 정상 결과입니다.
        /// </summary>
        /// <returns>Clamp 가능한 Grid면 true, 아니면 false입니다.</returns>
        public bool TryWorldToLocalClamped(Vector3 world, out int localX, out int localZ)
        {
            ThrowIfVolumeForSurfaceApi();
            localX = 0;
            localZ = 0;
            if (!IsValid || !IsFinite(world.x) || !IsFinite(world.y) || !IsFinite(world.z))
                return false;

            localX = ClampCellCoordinate((double)world.x - Origin.x, CellSize, Width);
            localZ = ClampCellCoordinate((double)world.z - Origin.z, CellSize, Depth);
            return true;
        }

        public bool TryWorldToLocalClamped(
            Vector3 world,
            out int localX,
            out int localY,
            out int localZ)
        {
            localX = 0;
            localY = 0;
            localZ = 0;
            if (!IsValid || !IsFinite(world.x) || !IsFinite(world.y) || !IsFinite(world.z))
                return false;

            localX = ClampCellCoordinate((double)world.x - Origin.x, CellSize, Width);
            localY = SpaceMode == FlowFieldSpaceMode.Volume3D
                ? ClampCellCoordinate((double)world.y - Origin.y, CellSize, Height)
                : 0;
            localZ = ClampCellCoordinate((double)world.z - Origin.z, CellSize, Depth);
            return true;
        }

        public Vector3 LocalToWorldCenter(int localX, int localZ)
        {
            ThrowIfVolumeForSurfaceApi();
            float halfCell = CellSize * 0.5f;
            return Origin + new Vector3(localX * CellSize + halfCell, 0f, localZ * CellSize + halfCell);
        }

        public Vector3 LocalToWorldCenter(int localX, int localY, int localZ)
        {
            float halfCell = CellSize * 0.5f;
            return Origin + new Vector3(
                localX * CellSize + halfCell,
                SpaceMode == FlowFieldSpaceMode.Volume3D
                    ? localY * CellSize + halfCell
                    : 0f,
                localZ * CellSize + halfCell);
        }

        public int ToFlatIndex(int localX, int localZ)
        {
            ThrowIfVolumeForSurfaceApi();
            return localZ * Width + localX;
        }

        public int ToFlatIndex(int localX, int localY, int localZ)
            => localX + Width * (localZ + Depth * localY);

        public void FromFlatIndex(int index, out int localX, out int localZ)
        {
            ThrowIfVolumeForSurfaceApi();
            localZ = index / Width;
            localX = index - localZ * Width;
        }

        public void FromFlatIndex(int index, out int localX, out int localY, out int localZ)
        {
            localY = index / (Width * Depth);
            int remainder = index - localY * Width * Depth;
            localZ = remainder / Width;
            localX = remainder - localZ * Width;
        }

        public bool IsLocalInBounds(int localX, int localZ)
        {
            ThrowIfVolumeForSurfaceApi();
            return IsLocalInBounds(localX, localZ, Width, Depth);
        }

        public bool IsLocalInBounds(int localX, int localY, int localZ)
            => localX >= 0 && localX < Width
                && localY >= 0 && localY < Height
                && localZ >= 0 && localZ < Depth;

        public Vector3 ClampWorldXZ(Vector3 world)
        {
            ThrowIfVolumeForSurfaceApi();
            if (!IsValid)
                throw new System.InvalidOperationException("Cannot clamp a position with an invalid Grid.");
            if (!IsFinite(world.x) || !IsFinite(world.z))
                throw new System.ArgumentOutOfRangeException(nameof(world));

            double epsilon = Math.Max(MIN_CLAMP_EPSILON, CellSize * 0.0001f);
            world.x = (float)Math.Max(Origin.x, Math.Min((double)Origin.x + WorldSizeX - epsilon, world.x));
            world.z = (float)Math.Max(Origin.z, Math.Min((double)Origin.z + WorldSizeZ - epsilon, world.z));
            return world;
        }

        public Vector3 ClampWorldXYZ(Vector3 world)
        {
            if (!IsValid)
                throw new InvalidOperationException("Cannot clamp a position with an invalid Grid.");
            if (!IsFinite(world.x) || !IsFinite(world.y) || !IsFinite(world.z))
                throw new ArgumentOutOfRangeException(nameof(world));

            double epsilon = Math.Max(MIN_CLAMP_EPSILON, CellSize * 0.0001f);
            world.x = (float)Math.Max(Origin.x, Math.Min((double)Origin.x + WorldSizeX - epsilon, world.x));
            world.y = (float)Math.Max(Origin.y, Math.Min((double)Origin.y + WorldSizeY - epsilon, world.y));
            world.z = (float)Math.Max(Origin.z, Math.Min((double)Origin.z + WorldSizeZ - epsilon, world.z));
            return world;
        }

        public bool MatchesBounds(FlowFieldGridSpace other)
        {
            return IsValid
                && other.IsValid
                && Width == other.Width
                && Height == other.Height
                && Depth == other.Depth
                && SpaceMode == other.SpaceMode
                && Mathf.Abs(CellSize - other.CellSize) <= 0.0001f
                && Approximately(Origin, other.Origin, 0.000001d);
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
            ThrowIfVolumeForSurfaceApi();
            minX = 1;
            maxX = 0;
            minZ = 1;
            maxZ = 0;
            if (!IsValid)
                return false;
            if (!IsFinite(worldBounds.center) || !IsFinite(worldBounds.size))
                return false;

            float gridMaxX = Origin.x + WorldSizeX;
            float gridMaxZ = Origin.z + WorldSizeZ;
            if (worldBounds.max.x < Origin.x
                || worldBounds.min.x > gridMaxX
                || worldBounds.max.z < Origin.z
                || worldBounds.min.z > gridMaxZ)
                return false;

            minX = Mathf.Clamp(
                FloorCellCoordinate((double)worldBounds.min.x - Origin.x, CellSize),
                0,
                Width - 1);
            maxX = Mathf.Clamp(
                FloorCellCoordinate((double)worldBounds.max.x - Origin.x, CellSize),
                0,
                Width - 1);
            minZ = Mathf.Clamp(
                FloorCellCoordinate((double)worldBounds.min.z - Origin.z, CellSize),
                0,
                Depth - 1);
            maxZ = Mathf.Clamp(
                FloorCellCoordinate((double)worldBounds.max.z - Origin.z, CellSize),
                0,
                Depth - 1);
            return minX <= maxX && minZ <= maxZ;
        }

        public bool TryGetOverlappingCells(
            Bounds worldBounds,
            out int minX,
            out int maxX,
            out int minY,
            out int maxY,
            out int minZ,
            out int maxZ)
        {
            minX = 1;
            maxX = 0;
            minY = 1;
            maxY = 0;
            minZ = 1;
            maxZ = 0;
            if (!IsValid || SpaceMode != FlowFieldSpaceMode.Volume3D)
                return false;
            if (!IsFinite(worldBounds.center) || !IsFinite(worldBounds.size))
                return false;

            if (worldBounds.max.x < Origin.x || worldBounds.min.x > Origin.x + WorldSizeX
                || worldBounds.max.y < Origin.y || worldBounds.min.y > Origin.y + WorldSizeY
                || worldBounds.max.z < Origin.z || worldBounds.min.z > Origin.z + WorldSizeZ)
                return false;

            minX = Mathf.Clamp(FloorCellCoordinate((double)worldBounds.min.x - Origin.x, CellSize), 0, Width - 1);
            maxX = Mathf.Clamp(FloorCellCoordinate((double)worldBounds.max.x - Origin.x, CellSize), 0, Width - 1);
            minY = Mathf.Clamp(FloorCellCoordinate((double)worldBounds.min.y - Origin.y, CellSize), 0, Height - 1);
            maxY = Mathf.Clamp(FloorCellCoordinate((double)worldBounds.max.y - Origin.y, CellSize), 0, Height - 1);
            minZ = Mathf.Clamp(FloorCellCoordinate((double)worldBounds.min.z - Origin.z, CellSize), 0, Depth - 1);
            maxZ = Mathf.Clamp(FloorCellCoordinate((double)worldBounds.max.z - Origin.z, CellSize), 0, Depth - 1);
            return minX <= maxX && minY <= maxY && minZ <= maxZ;
        }

        public static bool IsLocalInBounds(int localX, int localZ, int width, int depth)
            => localX >= 0 && localX < width && localZ >= 0 && localZ < depth;

        public static bool IsLocalInBounds(int localX, int localY, int localZ, int width, int height, int depth)
            => localX >= 0 && localX < width
                && localY >= 0 && localY < height
                && localZ >= 0 && localZ < depth;

        internal static bool IsFinite(float value)
            => !float.IsNaN(value) && !float.IsInfinity(value);

        internal static bool IsFinite(Vector3 value)
            => IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);

        internal static bool Approximately(Vector3 left, Vector3 right, double epsilonSqr)
        {
            double dx = (double)left.x - right.x;
            double dy = (double)left.y - right.y;
            double dz = (double)left.z - right.z;
            return dx * dx + dy * dy + dz * dz <= epsilonSqr;
        }

        private static int FloorCellCoordinate(double offset, float cellSize)
        {
            double value = offset / cellSize;
            if (value <= int.MinValue)
                return int.MinValue;
            if (value >= int.MaxValue)
                return int.MaxValue;
            return (int)Math.Floor(value);
        }

        private static int ClampCellCoordinate(double offset, float cellSize, int dimension)
        {
            int coordinate = FloorCellCoordinate(offset, cellSize);
            return Mathf.Clamp(coordinate, 0, dimension - 1);
        }

        private void ThrowIfVolumeForSurfaceApi()
        {
            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
                throw new InvalidOperationException(
                    "This XZ-only Grid API is unavailable in Volume3D mode; use the XYZ overload.");
        }
    }

    internal static class FlowFieldBakeBoundsUtility
    {
        public const float MinCellSize = 0.01f;
        public const float MinBoundsHeight = 0.01f;
        public const int MaxSurfaceCellCount = 100000;
        public const int MaxVolumeCellCount = 1000000;
        public const int MaxCellCount = MaxSurfaceCellCount;
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

        public static Bounds SnapVolumeCenterAnchored(Bounds bounds, float cellSize)
        {
            float resolvedCellSize = ValidateCellSize(cellSize);
            if (!FlowFieldGridSpace.IsFinite(bounds.center)
                || !FlowFieldGridSpace.IsFinite(bounds.size)
                || bounds.size.x <= 0f || bounds.size.y <= 0f || bounds.size.z <= 0f)
                throw new ArgumentOutOfRangeException(nameof(bounds));
            return new Bounds(
                bounds.center,
                new Vector3(
                    SnapHorizontalSize(bounds.size.x, resolvedCellSize),
                    SnapHorizontalSize(bounds.size.y, resolvedCellSize),
                    SnapHorizontalSize(bounds.size.z, resolvedCellSize)));
        }

        public static Bounds SnapResizedKeepingOppositeFace(
            Bounds previous,
            Bounds candidate,
            float cellSize)
            => SnapResizedKeepingOppositeFace(previous, candidate, cellSize, snapY: false);

        public static Bounds SnapVolumeResizedKeepingOppositeFace(
            Bounds previous,
            Bounds candidate,
            float cellSize)
            => SnapResizedKeepingOppositeFace(previous, candidate, cellSize, snapY: true);

        private static Bounds SnapResizedKeepingOppositeFace(
            Bounds previous,
            Bounds candidate,
            float cellSize,
            bool snapY)
        {
            Bounds resolvedPrevious = snapY
                ? SnapVolumeCenterAnchored(previous, cellSize)
                : SnapCenterAnchored(previous, cellSize);
            Bounds resolvedCandidate = snapY
                ? SnapVolumeCenterAnchored(candidate, cellSize)
                : SnapCenterAnchored(candidate, cellSize);
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

            if (!TryCalculateCellCount(localBounds.size.x, cellSize, out int width)
                || !TryCalculateCellCount(localBounds.size.z, cellSize, out int depth)
                || !TryValidateCellCount(width, depth, out _))
                return false;

            Bounds snapped = new Bounds(
                localBounds.center,
                new Vector3(width * cellSize, localBounds.size.y, depth * cellSize));
            worldBounds = new Bounds(managerWorldPosition + snapped.center, snapped.size);
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center)
                || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                return false;
            Vector3 origin = new Vector3(worldBounds.min.x, worldBounds.center.y, worldBounds.min.z);
            grid = FlowFieldGridSpace.FromCellGrid(origin, width, depth, cellSize);
            return grid.IsValid;
        }

        public static bool TryCreateVolumeWorldLayout(
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
                || localBounds.size.y <= 0f
                || localBounds.size.z <= 0f)
                return false;

            if (!TryCalculateVolumeAxisCellCount(localBounds.size.x, cellSize, out int width)
                || !TryCalculateVolumeAxisCellCount(localBounds.size.y, cellSize, out int height)
                || !TryCalculateVolumeAxisCellCount(localBounds.size.z, cellSize, out int depth)
                || !TryValidateVolumeCellCount(width, height, depth, out _))
                return false;

            Bounds snapped = new Bounds(
                localBounds.center,
                new Vector3(width * cellSize, height * cellSize, depth * cellSize));
            worldBounds = new Bounds(managerWorldPosition + snapped.center, snapped.size);
            if (!FlowFieldGridSpace.IsFinite(worldBounds.center)
                || !FlowFieldGridSpace.IsFinite(worldBounds.size))
                return false;
            grid = FlowFieldGridSpace.FromVolumeCellGrid(
                worldBounds.min,
                width,
                height,
                depth,
                cellSize);
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
            if (count <= 0 || count > MaxSurfaceCellCount || count > int.MaxValue)
                return false;

            cellCount = (int)count;
            return true;
        }

        public static bool TryValidateVolumeCellCount(
            int width,
            int height,
            int depth,
            out int cellCount)
        {
            cellCount = 0;
            if (width <= 0 || height <= 0 || depth <= 0)
                return false;

            long count;
            try
            {
                count = checked((long)width * height * depth);
            }
            catch (OverflowException)
            {
                return false;
            }
            if (count <= 0 || count > MaxVolumeCellCount || count > int.MaxValue)
                return false;

            cellCount = (int)count;
            return true;
        }

        public static bool Approximately(Bounds left, Bounds right)
            => FlowFieldGridSpace.Approximately(
                    left.center,
                    right.center,
                    CompareEpsilon * CompareEpsilon)
                && FlowFieldGridSpace.Approximately(
                    left.size,
                    right.size,
                    CompareEpsilon * CompareEpsilon);

        private static int CalculateCellCount(float size, float cellSize)
        {
            if (!TryCalculateCellCount(size, cellSize, out int count))
                throw new System.ArgumentOutOfRangeException(nameof(size));
            return count;
        }

        private static bool TryCalculateCellCount(float size, float cellSize, out int count)
        {
            count = 0;
            if (!FlowFieldGridSpace.IsFinite(size)
                || size <= 0f
                || !FlowFieldGridSpace.IsFinite(cellSize)
                || cellSize <= 0f)
                return false;

            double rounded = Math.Floor((double)size / cellSize + 0.5d);
            if (rounded <= 0d || rounded > int.MaxValue)
                return false;
            count = (int)rounded;
            return true;
        }

        private static bool TryCalculateVolumeAxisCellCount(
            float size,
            float cellSize,
            out int count)
        {
            count = 0;
            if (!FlowFieldGridSpace.IsFinite(size)
                || size <= 0f
                || !FlowFieldGridSpace.IsFinite(cellSize)
                || cellSize <= 0f)
                return false;

            double rounded = Math.Floor((double)size / cellSize + 0.5d);
            rounded = Math.Max(1d, rounded);
            if (rounded > int.MaxValue)
                return false;
            count = (int)rounded;
            return true;
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
        public int MinY;
        public int MaxY;
        public int MinZ;
        public int MaxZ;
        public bool IsValid => MinX <= MaxX && MinY <= MaxY && MinZ <= MaxZ;

        public static FlowFieldCellRect Invalid => new FlowFieldCellRect
        {
            MinX = 1,
            MaxX = 0,
            MinY = 1,
            MaxY = 0,
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
                MinY = 0,
                MaxY = grid.Height - 1,
                MinZ = 0,
                MaxZ = grid.Depth - 1,
            };
        }

        public static FlowFieldCellRect FromBounds(FlowFieldGridSpace grid, Bounds worldBounds)
        {
            if (grid.SpaceMode == FlowFieldSpaceMode.Volume3D)
                return FromBounds(grid, worldBounds, includeY: true);

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
                MinY = 0,
                MaxY = grid.Height - 1,
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
                MinY = Mathf.Max(0, MinY - ring),
                MaxY = Mathf.Min(grid.Height - 1, MaxY + ring),
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
                MinY = Math.Min(left.MinY, right.MinY),
                MaxY = Math.Max(left.MaxY, right.MaxY),
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
                && MinY <= other.MaxY
                && MaxY >= other.MinY
                && MinZ <= other.MaxZ
                && MaxZ >= other.MinZ;
        }

        public int CellCountEstimate => IsValid
            ? (MaxX - MinX + 1) * (MaxY - MinY + 1) * (MaxZ - MinZ + 1)
            : 0;

        public static FlowFieldCellRect FromBounds(
            FlowFieldGridSpace grid,
            Bounds worldBounds,
            bool includeY)
        {
            if (!includeY || grid.SpaceMode != FlowFieldSpaceMode.Volume3D)
                return FromBounds(grid, worldBounds);
            if (!grid.TryGetOverlappingCells(
                    worldBounds,
                    out int minX,
                    out int maxX,
                    out int minY,
                    out int maxY,
                    out int minZ,
                    out int maxZ))
                return Invalid;
            return new FlowFieldCellRect
            {
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY,
                MinZ = minZ,
                MaxZ = maxZ,
            };
        }
    }

    internal static class FlowFieldNeighborUtility
    {
        public const int SurfaceCount = 8;
        public const int Count = SurfaceCount;
        public const int VolumeCount = 26;
        public static readonly int[] VolumeFaceDirections = { 0, 1, 2, 3, 8, 9 };
        public static readonly int[] DeltaX =
        {
            1, -1, 0, 0, 1, 1, -1, -1,
            0, 0,
            1, -1, 1, -1,
            0, 0, 0, 0,
            1, 1, 1, 1, -1, -1, -1, -1,
        };
        public static readonly int[] DeltaY =
        {
            0, 0, 0, 0, 0, 0, 0, 0,
            1, -1,
            1, 1, -1, -1,
            1, -1, 1, -1,
            1, 1, -1, -1, 1, 1, -1, -1,
        };
        public static readonly int[] DeltaZ =
        {
            0, 0, 1, -1, 1, -1, 1, -1,
            0, 0,
            0, 0, 0, 0,
            1, 1, -1, -1,
            1, -1, 1, -1, 1, -1, 1, -1,
        };

        public static bool IsDiagonal(int directionIndex)
            => Math.Abs(DeltaX[directionIndex])
                + Math.Abs(DeltaY[directionIndex])
                + Math.Abs(DeltaZ[directionIndex]) > 1;

        public static int FindDirectionIndex(int deltaX, int deltaZ)
        {
            for (int i = 0; i < Count; i++)
            {
                if (DeltaX[i] == deltaX && DeltaZ[i] == deltaZ && DeltaY[i] == 0)
                    return i;
            }

            return -1;
        }

        public static int FindDirectionIndex(int deltaX, int deltaY, int deltaZ)
        {
            for (int i = 0; i < VolumeCount; i++)
                if (DeltaX[i] == deltaX && DeltaY[i] == deltaY && DeltaZ[i] == deltaZ)
                    return i;
            return -1;
        }
    }
}
