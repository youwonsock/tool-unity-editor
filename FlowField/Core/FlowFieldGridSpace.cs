using UnityEngine;

namespace Supercent.Common.FlowField
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
            if (!IsFinite(origin) || !IsFinite(cellSize))
                return default;

            return new FlowFieldGridSpace(
                origin,
                Mathf.Max(1, width),
                Mathf.Max(1, depth),
                Mathf.Max(FlowFieldBakeBoundsUtility.MinCellSize, cellSize));
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
            if (!IsValid || !IsFinite(world.x) || !IsFinite(world.z))
                return world;

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
}
