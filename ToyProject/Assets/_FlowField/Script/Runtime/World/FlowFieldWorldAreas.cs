using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The cells of a grid on a world's lattice: whole cells from the lattice
    /// origin. Surface2D grids are one cell high at lattice Y 0.
    /// </summary>
    internal readonly struct FlowFieldLatticeBox
    {
        internal readonly int MinX;
        internal readonly int MinY;
        internal readonly int MinZ;
        internal readonly int SizeX;
        internal readonly int SizeY;
        internal readonly int SizeZ;

        internal FlowFieldLatticeBox(int minX, int minY, int minZ, int sizeX, int sizeY, int sizeZ)
        {
            MinX = minX;
            MinY = minY;
            MinZ = minZ;
            SizeX = sizeX;
            SizeY = sizeY;
            SizeZ = sizeZ;
        }

        internal bool Contains(int x, int y, int z)
            => x >= MinX && x < MinX + SizeX && y >= MinY && y < MinY + SizeY && z >= MinZ && z < MinZ + SizeZ;

        internal bool Overlaps(in FlowFieldLatticeBox other)
            => MinX < other.MinX + other.SizeX && other.MinX < MinX + SizeX
                && MinY < other.MinY + other.SizeY && other.MinY < MinY + SizeY
                && MinZ < other.MinZ + other.SizeZ && other.MinZ < MinZ + SizeZ;

        // Within one cell on every axis: a neighbor step can cross between them.
        internal bool Touches(in FlowFieldLatticeBox other)
            => MinX <= other.MinX + other.SizeX && other.MinX <= MinX + SizeX
                && MinY <= other.MinY + other.SizeY && other.MinY <= MinY + SizeY
                && MinZ <= other.MinZ + other.SizeZ && other.MinZ <= MinZ + SizeZ
                && !Overlaps(in other);
    }

    /// <summary>
    /// A cell of an area beside a cell of a touching area: the neighbor step
    /// from <see cref="Cell"/> that lands on <see cref="Other"/>.
    /// </summary>
    internal readonly struct FlowFieldBorderPair
    {
        internal readonly int Cell;
        internal readonly int Other;
        internal readonly int Direction;

        internal FlowFieldBorderPair(int cell, int other, int direction)
        {
            Cell = cell;
            Other = other;
            Direction = direction;
        }
    }

    /// <summary>The cells where an area meets one touching area.</summary>
    internal sealed class FlowFieldWorldBorder
    {
        internal readonly FlowFieldWorldArea Other;
        internal readonly FlowFieldBorderPair[] Pairs;

        internal FlowFieldWorldBorder(FlowFieldWorldArea other, FlowFieldBorderPair[] pairs)
        {
            Other = other;
            Pairs = pairs;
        }
    }

    /// <summary>A Manager that joined a world, its grid on the lattice and its borders.</summary>
    internal sealed class FlowFieldWorldArea
    {
        internal readonly FlowFieldManager Manager;
        internal readonly FlowFieldGridSpace Grid;
        internal readonly FlowFieldLatticeBox Box;
        internal readonly List<FlowFieldWorldBorder> Borders = new List<FlowFieldWorldBorder>(4);

        internal FlowFieldWorldArea(FlowFieldManager manager, FlowFieldGridSpace grid, FlowFieldLatticeBox box)
        {
            Manager = manager;
            Grid = grid;
            Box = box;
        }

        internal bool IsVolume => Grid.SpaceMode == FlowFieldSpaceMode.Volume3D;

        // The area's cell at lattice coordinates.
        internal bool TryCell(int x, int y, int z, out int index)
        {
            index = -1;
            if (!Box.Contains(x, y, z))
                return false;
            index = IsVolume
                ? Grid.ToFlatIndex(x - Box.MinX, y - Box.MinY, z - Box.MinZ)
                : Grid.ToFlatIndex(x - Box.MinX, z - Box.MinZ);
            return true;
        }

        internal void Locate(int index, out int x, out int y, out int z)
        {
            Grid.Locate(index, out x, out y, out z);
            x += Box.MinX;
            y += Box.MinY;
            z += Box.MinZ;
        }

        internal FlowFieldWorldBorder BorderWith(FlowFieldWorldArea other)
        {
            for (int i = 0; i < Borders.Count; i++)
            {
                if (ReferenceEquals(Borders[i].Other, other))
                    return Borders[i];
            }
            return null;
        }
    }

    /// <summary>
    /// The areas of one world while Play runs: who joined, where their grids
    /// lie on the lattice and which cells meet across their borders. A Play
    /// session starts with no area, also with domain reload off.
    /// </summary>
    internal sealed class FlowFieldWorldAreas
    {
        private static readonly Dictionary<FlowFieldWorld, FlowFieldWorldAreas> Worlds
            = new Dictionary<FlowFieldWorld, FlowFieldWorldAreas>();

        private readonly List<FlowFieldWorldArea> _areas = new List<FlowFieldWorldArea>(4);
        private readonly List<FlowFieldManager> _managers = new List<FlowFieldManager>(4);

        internal readonly FlowFieldWorld World;

        private FlowFieldWorldAreas(FlowFieldWorld world)
        {
            World = world;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForPlaySession() => Worlds.Clear();

        internal static FlowFieldWorldAreas For(FlowFieldWorld world)
        {
            if (!Worlds.TryGetValue(world, out FlowFieldWorldAreas areas))
            {
                areas = new FlowFieldWorldAreas(world);
                Worlds.Add(world, areas);
            }
            return areas;
        }

        internal IReadOnlyList<FlowFieldWorldArea> All => _areas;
        internal IReadOnlyList<FlowFieldManager> Managers => _managers;

        /// <summary>Raised after an area joined or left, with that area.</summary>
        internal event Action<FlowFieldWorldArea> Changed;

        internal FlowFieldWorldArea Find(FlowFieldManager manager)
        {
            for (int i = 0; i < _areas.Count; i++)
            {
                if (ReferenceEquals(_areas[i].Manager, manager))
                    return _areas[i];
            }
            return null;
        }

        internal FlowFieldWorldArea Find(Vector3 worldPosition)
        {
            for (int i = 0; i < _areas.Count; i++)
            {
                if (_areas[i].Grid.ContainsWorldPosition(worldPosition))
                    return _areas[i];
            }
            return null;
        }

        /// <summary>
        /// The grid's cells on the world's lattice, or false with the reason a
        /// grid of this world cannot use: another Space Mode or Cell Size, or an
        /// origin off the lattice.
        /// </summary>
        internal static bool TryLatticeBox(
            FlowFieldWorld world,
            FlowFieldGridSpace grid,
            out FlowFieldLatticeBox box,
            out string reason)
        {
            box = default;
            reason = null;
            if (grid.SpaceMode != world.SpaceMode)
            {
                reason = $"The FlowField World {world.name} uses {world.SpaceMode}; this Manager uses {grid.SpaceMode}.";
                return false;
            }
            if (Mathf.Abs(grid.CellSize - world.CellSize) > FlowFieldManagerConfig.ValueEpsilon)
            {
                reason = $"The FlowField World {world.name} uses Cell Size {world.CellSize}; this Manager uses {grid.CellSize}.";
                return false;
            }
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            if (!TryLatticeCoordinate(grid.Origin.x, world.LatticeOrigin.x, world.CellSize, out int x)
                || !TryLatticeCoordinate(grid.Origin.z, world.LatticeOrigin.z, world.CellSize, out int z)
                || volume && !TryLatticeCoordinate(grid.Origin.y, world.LatticeOrigin.y, world.CellSize, out _))
            {
                reason = $"The grid does not start on the lattice of the FlowField World {world.name}; "
                    + "move the Manager or its Bake Bounds by whole cells from the Lattice Origin.";
                return false;
            }
            int y = 0;
            if (volume)
                TryLatticeCoordinate(grid.Origin.y, world.LatticeOrigin.y, world.CellSize, out y);
            box = new FlowFieldLatticeBox(x, y, z, grid.Width, volume ? grid.Height : 1, grid.Depth);
            return true;
        }

        // Within a thousandth of a cell of a lattice line.
        private static bool TryLatticeCoordinate(float position, float origin, float cellSize, out int cell)
        {
            double steps = ((double)position - origin) / cellSize;
            double rounded = Math.Round(steps);
            cell = (int)rounded;
            return Math.Abs(steps - rounded) <= 0.001d && rounded >= int.MinValue && rounded <= int.MaxValue;
        }

        /// <summary>A joined area other than <paramref name="except"/>'s whose cells overlap the box.</summary>
        internal FlowFieldWorldArea Overlapping(in FlowFieldLatticeBox box, FlowFieldManager except)
        {
            for (int i = 0; i < _areas.Count; i++)
            {
                FlowFieldWorldArea area = _areas[i];
                if (!ReferenceEquals(area.Manager, except) && area.Box.Overlaps(in box))
                    return area;
            }
            return null;
        }

        /// <summary>
        /// Joins the Manager, or moves it to a new grid, and finds the cells it
        /// meets of every touching area. The caller checked the lattice and
        /// the overlap.
        /// </summary>
        internal FlowFieldWorldArea Join(FlowFieldManager manager, FlowFieldGridSpace grid, in FlowFieldLatticeBox box)
        {
            RemoveArea(manager);
            var area = new FlowFieldWorldArea(manager, grid, box);
            for (int i = 0; i < _areas.Count; i++)
            {
                FlowFieldWorldArea other = _areas[i];
                if (!area.Box.Touches(in other.Box))
                    continue;
                FlowFieldBorderPair[] pairs = FindPairs(area, other);
                if (pairs.Length == 0)
                    continue;
                area.Borders.Add(new FlowFieldWorldBorder(other, pairs));
                other.Borders.Add(new FlowFieldWorldBorder(area, FindPairs(other, area)));
            }
            _areas.Add(area);
            _managers.Add(manager);
            Changed?.Invoke(area);
            return area;
        }

        internal void Leave(FlowFieldManager manager)
        {
            FlowFieldWorldArea area = RemoveArea(manager);
            if (area != null)
                Changed?.Invoke(area);
        }

        private FlowFieldWorldArea RemoveArea(FlowFieldManager manager)
        {
            FlowFieldWorldArea area = Find(manager);
            if (area == null)
                return null;
            _areas.Remove(area);
            _managers.Remove(manager);
            for (int i = 0; i < area.Borders.Count; i++)
            {
                List<FlowFieldWorldBorder> theirs = area.Borders[i].Other.Borders;
                for (int j = theirs.Count - 1; j >= 0; j--)
                {
                    if (ReferenceEquals(theirs[j].Other, area))
                        theirs.RemoveAt(j);
                }
            }
            area.Borders.Clear();
            return area;
        }

        // Every cell of the area on its outer faces, and each neighbor step
        // from it that lands in the other area.
        private static FlowFieldBorderPair[] FindPairs(FlowFieldWorldArea area, FlowFieldWorldArea other)
        {
            var pairs = new List<FlowFieldBorderPair>();
            int steps = area.IsVolume ? FlowFieldNeighborUtility.VolumeCount : FlowFieldNeighborUtility.SurfaceCount;
            FlowFieldGridSpace grid = area.Grid;
            for (int cell = 0; cell < grid.CellCount; cell++)
            {
                grid.Locate(cell, out int lx, out int ly, out int lz);
                bool edge = lx == 0 || lx == grid.Width - 1 || lz == 0 || lz == grid.Depth - 1
                    || area.IsVolume && (ly == 0 || ly == grid.Height - 1);
                if (!edge)
                    continue;
                int x = lx + area.Box.MinX;
                int y = ly + area.Box.MinY;
                int z = lz + area.Box.MinZ;
                for (int direction = 0; direction < steps; direction++)
                {
                    int dx = FlowFieldNeighborUtility.DeltaX[direction];
                    int dy = area.IsVolume ? FlowFieldNeighborUtility.DeltaY[direction] : 0;
                    int dz = FlowFieldNeighborUtility.DeltaZ[direction];
                    if (other.TryCell(x + dx, y + dy, z + dz, out int target))
                        pairs.Add(new FlowFieldBorderPair(cell, target, direction));
                }
            }
            return pairs.ToArray();
        }
    }
}
