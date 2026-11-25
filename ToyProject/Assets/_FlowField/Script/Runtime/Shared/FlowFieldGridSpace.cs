using System;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The cell layout of a field: an axis-aligned grid of cells of one size from
    /// <see cref="Origin"/>. A Surface2D grid is one cell high and indexes cells by X and Z; its
    /// two-coordinate members throw on a Volume3D grid. Methods without a bounds check say so.
    /// </summary>
    public readonly struct FlowFieldGridSpace
    {
        private const float MinClampEpsilon = 0.00001f;

        /// <summary>The world position of the grid's minimum corner.</summary>
        public Vector3 Origin { get; }
        /// <summary>The extent along X in world units: Width × CellSize.</summary>
        public float WorldSizeX { get; }
        /// <summary>The extent along Y in world units: Height × CellSize, one cell on Surface2D.</summary>
        public float WorldSizeY { get; }
        /// <summary>The extent along Z in world units: Depth × CellSize.</summary>
        public float WorldSizeZ { get; }
        /// <summary>The number of cells along X.</summary>
        public int Width { get; }
        /// <summary>The number of cells along Y; 1 on Surface2D.</summary>
        public int Height { get; }
        /// <summary>The number of cells along Z.</summary>
        public int Depth { get; }
        /// <summary>The edge length of a cell in world units.</summary>
        public float CellSize { get; }
        /// <summary>The space mode the grid was made for.</summary>
        public FlowFieldSpaceMode SpaceMode { get; }
        // Stored because the field loops read both on every cell.
        /// <summary>Width × Height × Depth; 0 for an invalid grid.</summary>
        public int CellCount { get; }
        /// <summary>
        /// Whether the grid has cells: positive counts within the mode's cell limit, a finite origin
        /// and a cell size of at least 0.01 world units. A default value is invalid.
        /// </summary>
        public bool IsValid { get; }

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
            IsValid = Width > 0
                && Height > 0
                && Depth > 0
                && IsFinite(Origin)
                && IsFinite(CellSize)
                && IsFinite(WorldSizeX)
                && IsFinite(WorldSizeY)
                && IsFinite(WorldSizeZ)
                && CellSize >= FlowFieldLayoutUtility.MinCellSize
                && (SpaceMode == FlowFieldSpaceMode.Volume3D
                    ? FlowFieldLayoutUtility.TryValidateVolumeCellCount(Width, Height, Depth, out _)
                    : FlowFieldLayoutUtility.TryValidateCellCount(Width, Depth, out _));
            CellCount = IsValid ? (int)((long)Width * Height * Depth) : 0;
        }

        /// <summary>
        /// A Surface2D grid of <paramref name="width"/> × <paramref name="depth"/> cells, one cell high.
        /// </summary>
        /// <exception cref="System.ArgumentOutOfRangeException">
        /// <paramref name="origin"/> is not finite, the counts are not positive or exceed 100,000
        /// cells, <paramref name="cellSize"/> is not finite or below 0.01, or the world size overflows.
        /// </exception>
        public static FlowFieldGridSpace FromCellGrid(Vector3 origin, int width, int depth, float cellSize)
        {
            if (!IsFinite(origin))
                throw new System.ArgumentOutOfRangeException(nameof(origin));
            if (width <= 0 || depth <= 0)
                throw new System.ArgumentOutOfRangeException(nameof(width));
            if (!FlowFieldLayoutUtility.TryValidateCellCount(width, depth, out _))
                throw new System.ArgumentOutOfRangeException(nameof(width), "Grid cell count exceeds the supported limit.");
            if (!IsFinite(cellSize) || cellSize < FlowFieldLayoutUtility.MinCellSize)
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

        /// <summary>
        /// A Volume3D grid of <paramref name="width"/> × <paramref name="height"/> × <paramref name="depth"/>
        /// cubic cells.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="origin"/> is not finite, the counts are not positive or exceed 1,000,000
        /// cells, <paramref name="cellSize"/> is not finite or below 0.01, or the world size overflows.
        /// </exception>
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
            if (!FlowFieldLayoutUtility.TryValidateVolumeCellCount(width, height, depth, out _))
                throw new ArgumentOutOfRangeException(nameof(width), "Volume cell count exceeds the supported limit.");
            if (!IsFinite(cellSize) || cellSize < FlowFieldLayoutUtility.MinCellSize)
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

        /// <summary>
        /// Whether the position lies in the grid: from <see cref="Origin"/> inclusive to the far edge
        /// exclusive on each axis; Surface2D ignores Y. False for an invalid grid or a non-finite position.
        /// </summary>
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
        /// Converts a world position to local cell coordinates. A position outside
        /// the grid is an ordinary miss and returns false, not an exception.
        /// </summary>
        /// <returns>True inside the grid, false outside.</returns>
        /// <exception cref="InvalidOperationException">The grid is Volume3D; use the XYZ overload.</exception>
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

        /// <summary>
        /// Converts a world position to local cell coordinates in either mode; localY is 0 on
        /// Surface2D. A position outside the grid returns false, not an exception.
        /// </summary>
        /// <returns>True inside the grid, false outside.</returns>
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
        /// Converts a world position to the nearest grid cell, clamping it.
        /// A non-finite position or an uninitialized grid returns false.
        /// </summary>
        /// <returns>True when the grid can clamp, otherwise false.</returns>
        /// <exception cref="InvalidOperationException">The grid is Volume3D; use the XYZ overload.</exception>
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

        /// <summary>
        /// Converts a world position to the nearest cell in either mode, clamping each axis; localY
        /// is 0 on Surface2D.
        /// </summary>
        /// <returns>False for a non-finite position or an invalid grid.</returns>
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

        /// <summary>The world center of a cell, at the origin's height. No bounds check.</summary>
        /// <exception cref="InvalidOperationException">The grid is Volume3D; use the XYZ overload.</exception>
        public Vector3 LocalToWorldCenter(int localX, int localZ)
        {
            ThrowIfVolumeForSurfaceApi();
            float halfCell = CellSize * 0.5f;
            return Origin + new Vector3(localX * CellSize + halfCell, 0f, localZ * CellSize + halfCell);
        }

        /// <summary>
        /// The world center of a cell; on Surface2D at the origin's height, ignoring
        /// <paramref name="localY"/>. No bounds check.
        /// </summary>
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

        /// <summary>The flat index localZ × Width + localX. No bounds check.</summary>
        /// <exception cref="InvalidOperationException">The grid is Volume3D; use the XYZ overload.</exception>
        public int ToFlatIndex(int localX, int localZ)
        {
            ThrowIfVolumeForSurfaceApi();
            return localZ * Width + localX;
        }

        /// <summary>
        /// The flat index localX + Width × (localZ + Depth × localY); with localY 0 it matches the
        /// two-coordinate overload. No bounds check.
        /// </summary>
        public int ToFlatIndex(int localX, int localY, int localZ)
            => localX + Width * (localZ + Depth * localY);

        /// <summary>
        /// The cell of a flat index; the inverse of the two-coordinate ToFlatIndex. No bounds check.
        /// </summary>
        /// <exception cref="InvalidOperationException">The grid is Volume3D; use the XYZ overload.</exception>
        public void FromFlatIndex(int index, out int localX, out int localZ)
        {
            ThrowIfVolumeForSurfaceApi();
            localZ = index / Width;
            localX = index - localZ * Width;
        }

        /// <summary>The cell of a flat index; the inverse of the XYZ ToFlatIndex. No bounds check.</summary>
        public void FromFlatIndex(int index, out int localX, out int localY, out int localZ)
            => FromFlatIndex(index, Width, Depth, out localX, out localY, out localZ);

        /// <summary>The cell of a flat index in a Volume3D layout of this width and depth.</summary>
        internal static void FromFlatIndex(
            int index,
            int width,
            int depth,
            out int localX,
            out int localY,
            out int localZ)
        {
            localY = index / (width * depth);
            int remainder = index - localY * width * depth;
            localZ = remainder / width;
            localX = remainder - localZ * width;
        }

        // ----- Mode-neutral cell access: Surface2D cells have localY 0 -------

        /// <summary>The local coordinates of a flat index; localY is 0 on a Surface2D grid.</summary>
        internal void Locate(int index, out int localX, out int localY, out int localZ)
        {
            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                FromFlatIndex(index, out localX, out localY, out localZ);
                return;
            }
            localY = 0;
            FromFlatIndex(index, out localX, out localZ);
        }

        /// <summary>The flat index of local coordinates, or false outside the grid.</summary>
        internal bool TryLocal(int localX, int localY, int localZ, out int index)
        {
            index = -1;
            if (SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                if (!IsLocalInBounds(localX, localY, localZ, Width, Height, Depth))
                    return false;
                index = ToFlatIndex(localX, localY, localZ);
                return true;
            }
            if (localY != 0 || !IsLocalInBounds(localX, localZ, Width, Depth))
                return false;
            index = ToFlatIndex(localX, localZ);
            return true;
        }

        /// <summary>The world center of a cell; a Surface2D cell sits at the grid's origin height.</summary>
        internal Vector3 CellCenter(int index)
        {
            Locate(index, out int localX, out int localY, out int localZ);
            return SpaceMode == FlowFieldSpaceMode.Volume3D
                ? LocalToWorldCenter(localX, localY, localZ)
                : LocalToWorldCenter(localX, localZ);
        }

        /// <summary>Whether the coordinates name a cell of this grid.</summary>
        /// <exception cref="InvalidOperationException">The grid is Volume3D; use the XYZ overload.</exception>
        public bool IsLocalInBounds(int localX, int localZ)
        {
            ThrowIfVolumeForSurfaceApi();
            return IsLocalInBounds(localX, localZ, Width, Depth);
        }

        /// <summary>Whether the coordinates name a cell of this grid; localY must be 0 on Surface2D.</summary>
        public bool IsLocalInBounds(int localX, int localY, int localZ)
            => localX >= 0 && localX < Width
                && localY >= 0 && localY < Height
                && localZ >= 0 && localZ < Depth;

        /// <summary>
        /// Clamps X and Z into the grid and keeps Y. The far edge belongs to no cell, so a clamped
        /// axis stops just short of it and the result lies inside the grid.
        /// </summary>
        /// <exception cref="InvalidOperationException">The grid is Volume3D or invalid.</exception>
        /// <exception cref="ArgumentOutOfRangeException">X or Z is not finite.</exception>
        public Vector3 ClampWorldXZ(Vector3 world)
        {
            ThrowIfVolumeForSurfaceApi();
            if (!IsValid)
                throw new System.InvalidOperationException("Cannot clamp a position with an invalid Grid.");
            if (!IsFinite(world.x) || !IsFinite(world.z))
                throw new System.ArgumentOutOfRangeException(nameof(world));

            double epsilon = Math.Max(MinClampEpsilon, CellSize * 0.0001f);
            world.x = ClampAxis(world.x, Origin.x, WorldSizeX, epsilon);
            world.z = ClampAxis(world.z, Origin.z, WorldSizeZ, epsilon);
            return world;
        }

        /// <summary>
        /// Clamps all three axes into the grid, as <see cref="ClampWorldXZ"/> does; on Surface2D Y is
        /// clamped into the one-cell height.
        /// </summary>
        /// <exception cref="InvalidOperationException">The grid is invalid.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The position is not finite.</exception>
        public Vector3 ClampWorldXYZ(Vector3 world)
        {
            if (!IsValid)
                throw new InvalidOperationException("Cannot clamp a position with an invalid Grid.");
            if (!IsFinite(world.x) || !IsFinite(world.y) || !IsFinite(world.z))
                throw new ArgumentOutOfRangeException(nameof(world));

            double epsilon = Math.Max(MinClampEpsilon, CellSize * 0.0001f);
            world.x = ClampAxis(world.x, Origin.x, WorldSizeX, epsilon);
            world.y = ClampAxis(world.y, Origin.y, WorldSizeY, epsilon);
            world.z = ClampAxis(world.z, Origin.z, WorldSizeZ, epsilon);
            return world;
        }

        // The upper edge belongs to no cell, and rounding back to float can land
        // on it at large coordinates. Step down to the float below it.
        private static float ClampAxis(float value, float origin, float size, double epsilon)
        {
            double upper = (double)origin + size;
            float clamped = (float)Math.Max(origin, Math.Min(upper - epsilon, value));
            if (clamped < upper)
                return clamped;
            if (clamped == 0f)
                return -float.Epsilon;
            int bits = Unity.Mathematics.math.asint(clamped);
            return Unity.Mathematics.math.asfloat(clamped > 0f ? bits - 1 : bits + 1);
        }

        /// <summary>
        /// Whether both grids are valid with the same mode, cell counts, cell size (within 0.0001)
        /// and origin (within 0.001 world units).
        /// </summary>
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
        /// Computes the cell range the Bounds overlap. Bounds that miss the grid
        /// have no range and return false.
        /// </summary>
        /// <returns>True when some cells overlap, otherwise false.</returns>
        /// <exception cref="InvalidOperationException">The grid is Volume3D; use the XYZ overload.</exception>
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

        /// <summary>
        /// Computes the inclusive cell range the Bounds overlap on all three axes, clamped to the grid.
        /// </summary>
        /// <returns>
        /// True when some cells overlap; false on a Surface2D or invalid grid, for non-finite Bounds
        /// and for Bounds that miss the grid.
        /// </returns>
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

        /// <summary>
        /// Whether the coordinates lie in a <paramref name="width"/> × <paramref name="depth"/> layout.
        /// </summary>
        public static bool IsLocalInBounds(int localX, int localZ, int width, int depth)
            => localX >= 0 && localX < width && localZ >= 0 && localZ < depth;

        /// <summary>
        /// Whether the coordinates lie in a <paramref name="width"/> × <paramref name="height"/> ×
        /// <paramref name="depth"/> layout.
        /// </summary>
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
}
