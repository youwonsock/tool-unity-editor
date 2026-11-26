using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The touching areas a world Goal's read can step into: for each, its
    /// newest cells and its lattice offset from the area being read.
    /// </summary>
    internal interface IFlowFieldStitchSource
    {
        int Count { get; }

        /// <summary>
        /// The touching area's published field, uncertain regions and newer
        /// cells, and where its cell (0, 0, 0) lies in the read area's cells.
        /// False while it has no field.
        /// </summary>
        bool TryGet(
            int index,
            out FlowFieldField field,
            out FlowFieldSafety regions,
            out IFlowFieldLatestCells latest,
            out int offsetX,
            out int offsetY,
            out int offsetZ);
    }

    /// <summary>The views of the touching areas one read uses, kept so a read allocates nothing.</summary>
    internal sealed class FlowFieldStitchedViews<TView> where TView : struct, IFlowFieldCellView
    {
        internal int Count;
        internal TView[] Views = new TView[4];
        internal int[] OffsetX = new int[4];
        internal int[] OffsetY = new int[4];
        internal int[] OffsetZ = new int[4];
        // The first encoded index of each view's cells.
        internal int[] Bases = new int[4];

        internal void Clear() => Count = 0;

        internal void Add(in TView view, int offsetX, int offsetY, int offsetZ, int first)
        {
            if (Count == Views.Length)
            {
                System.Array.Resize(ref Views, Count * 2);
                System.Array.Resize(ref OffsetX, Count * 2);
                System.Array.Resize(ref OffsetY, Count * 2);
                System.Array.Resize(ref OffsetZ, Count * 2);
                System.Array.Resize(ref Bases, Count * 2);
            }
            Views[Count] = view;
            OffsetX[Count] = offsetX;
            OffsetY[Count] = offsetY;
            OffsetZ[Count] = offsetZ;
            Bases[Count] = first;
            Count++;
        }
    }

    /// <summary>
    /// A world Goal's read in one area, with the touching areas' cells beside
    /// its own: a step across the border is checked against the cells it
    /// lands in instead of stopping at the grid edge. Cells of the touching
    /// areas are indices after the area's own, in the read area's coordinates.
    /// A link out of the grid follows the rule the world Goals cross with.
    /// </summary>
    internal readonly struct FlowFieldStitchedCellView<TView> : IFlowFieldCellView
        where TView : struct, IFlowFieldCellView
    {
        private readonly TView _own;
        private readonly FlowFieldStitchedViews<TView> _others;
        private readonly int _ownCount;

        internal FlowFieldStitchedCellView(in TView own, FlowFieldStitchedViews<TView> others)
        {
            _own = own;
            _others = others;
            _ownCount = own.Grid.CellCount;
        }

        public FlowFieldField Field => _own.Field;
        public FlowFieldGridSpace Grid => _own.Grid;
        public int StepCount => _own.StepCount;
        public bool HasVerticalAxis => _own.HasVerticalAxis;
        public float StepHeight => _own.StepHeight;

        public bool Contains(int index) => index >= 0 && (index < _ownCount || Decode(index, out _, out _));

        public bool TryLocate(Vector3 worldPosition, out int index) => _own.TryLocate(worldPosition, out index);

        public FlowFieldSample ReadCell(int index) => _own.ReadCell(index);

        public Vector3 Planar(Vector3 direction) => _own.Planar(direction);

        public int DirectionIndex(int dx, int dy, int dz) => _own.DirectionIndex(dx, dy, dz);

        public FlowFieldSample EscapeSample(int index, Vector3 escape, in FlowFieldSample published)
            => _own.EscapeSample(index, escape, published);

        // A step across the border lands on the touching area's cell.
        public int NextCell(int index)
        {
            if (index >= _ownCount)
                return int.MaxValue;
            int next = _own.Field.Goal.NextCells[index];
            if (!FlowFieldNextCell.IsPortal(next))
                return next;
            int direction = FlowFieldNextCell.PortalDirection(next);
            _own.Locate(index, out int x, out int y, out int z);
            return TryCell(x + FlowFieldNeighborUtility.DeltaX[direction], y + DeltaY(direction),
                z + FlowFieldNeighborUtility.DeltaZ[direction], out int target)
                ? target
                : int.MaxValue;
        }

        public void Locate(int index, out int x, out int y, out int z)
        {
            if (index < _ownCount)
            {
                _own.Locate(index, out x, out y, out z);
                return;
            }
            Decode(index, out int slot, out int local);
            _others.Views[slot].Locate(local, out x, out y, out z);
            x += _others.OffsetX[slot];
            y += _others.OffsetY[slot];
            z += _others.OffsetZ[slot];
        }

        public bool TryCell(int x, int y, int z, out int index)
        {
            if (_own.TryCell(x, y, z, out index))
                return true;
            for (int slot = 0; slot < _others.Count; slot++)
            {
                if (_others.Views[slot].TryCell(x - _others.OffsetX[slot], y - _others.OffsetY[slot], z - _others.OffsetZ[slot], out int local))
                {
                    index = _others.Bases[slot] + local;
                    return true;
                }
            }
            index = -1;
            return false;
        }

        public bool IsUncertain(int index)
            => index < _ownCount ? _own.IsUncertain(index) : Other(index, out int local).IsUncertain(local);

        public bool IsBlocked(int index)
            => index < _ownCount ? _own.IsBlocked(index) : Other(index, out int local).IsBlocked(local);

        public bool IsWalkable(int index)
            => index < _ownCount ? _own.IsWalkable(index) : Other(index, out int local).IsWalkable(local);

        public float GroundHeight(int index)
            => index < _ownCount ? _own.GroundHeight(index) : Other(index, out int local).GroundHeight(local);

        public Vector3 EscapeDirection(int index)
            => index < _ownCount ? _own.EscapeDirection(index) : Other(index, out int local).EscapeDirection(local);

        public bool HasLink(int index, int direction)
        {
            if (index >= _ownCount)
                return Other(index, out int local).HasLink(local, direction);
            return _own.HasLink(index, direction) || CrossesBorder(index, direction);
        }

        public bool HasGroundLink(int index, int direction)
        {
            if (index >= _ownCount)
                return Other(index, out int local).HasGroundLink(local, direction);
            return _own.HasGroundLink(index, direction) || CrossesBorder(index, direction);
        }

        public bool IsCrossingUnsafe(int x, int y, int z, int dx, int dy, int dz, int target, out bool walled)
        {
            walled = false;
            bool inside = target < _ownCount
                && (HasVerticalAxis || dx == 0 || dz == 0
                    || _own.TryCell(x + dx, y, z, out _) && _own.TryCell(x, y, z + dz, out _));
            if (inside && _own.TryCell(x, y, z, out _))
                return _own.IsCrossingUnsafe(x, y, z, dx, dy, dz, target, out walled);
            if (HasVerticalAxis)
            {
                walled = IsBlocked(target);
                return false;
            }
            if (!TryCell(x, y, z, out int from))
                return true;
            if (IsDrop(from, target))
                return true;
            if (dx != 0 && dz != 0)
            {
                if (!TryCell(x + dx, y, z, out int besideX) || !TryCell(x, y, z + dz, out int besideZ))
                    return true;
                if (IsDropThrough(from, besideX, target) || IsDropThrough(from, besideZ, target))
                    return true;
            }
            walled = IsBlocked(target) || GroundHeight(target) > GroundHeight(from) + StepHeight;
            return false;
        }

        private bool IsDrop(int from, int to) => GroundHeight(to) < GroundHeight(from) - StepHeight;

        private bool IsDropThrough(int from, int beside, int to)
            => IsDrop(from, beside)
                || !IsBlocked(beside) && GroundHeight(beside) <= GroundHeight(from) + StepHeight && IsDrop(beside, to);

        // A step out of this grid into a touching area: both cells open, and
        // the cells a diagonal passes beside, as the world Goals cross.
        private bool CrossesBorder(int index, int direction)
        {
            _own.Locate(index, out int x, out int y, out int z);
            int dx = FlowFieldNeighborUtility.DeltaX[direction];
            int dy = DeltaY(direction);
            int dz = FlowFieldNeighborUtility.DeltaZ[direction];
            if (_own.TryCell(x + dx, y + dy, z + dz, out _) || !TryCell(x + dx, y + dy, z + dz, out int target))
                return false;
            if (!Open(index) || !Open(target))
                return false;
            if (HasVerticalAxis)
            {
                for (int i = FlowFieldNeighborUtility.VolumeCornerStart[direction];
                     i < FlowFieldNeighborUtility.VolumeCornerStart[direction + 1];
                     i++)
                {
                    int corner = FlowFieldNeighborUtility.VolumeCornerDirections[i];
                    if (!TryCell(x + FlowFieldNeighborUtility.DeltaX[corner], y + FlowFieldNeighborUtility.DeltaY[corner],
                            z + FlowFieldNeighborUtility.DeltaZ[corner], out int cell) || !Open(cell))
                        return false;
                }
                return true;
            }
            float step = StepHeight;
            if (!Connects(index, target, step))
                return false;
            if (dx == 0 || dz == 0)
                return true;
            return TryCell(x + dx, y, z, out int besideX)
                && TryCell(x, y, z + dz, out int besideZ)
                && Open(besideX)
                && Open(besideZ)
                && Connects(index, besideX, step)
                && Connects(index, besideZ, step)
                && Connects(besideX, target, step)
                && Connects(besideZ, target, step);
        }

        private bool Open(int index) => IsWalkable(index) && !IsBlocked(index);

        private bool Connects(int a, int b, float step) => Mathf.Abs(GroundHeight(a) - GroundHeight(b)) <= step;

        private int DeltaY(int direction) => HasVerticalAxis ? FlowFieldNeighborUtility.DeltaY[direction] : 0;

        private TView Other(int index, out int local)
        {
            Decode(index, out int slot, out local);
            return _others.Views[slot];
        }

        private bool Decode(int index, out int slot, out int local)
        {
            for (slot = _others.Count - 1; slot >= 0; slot--)
            {
                int first = _others.Bases[slot];
                if (index >= first && index - first < _others.Views[slot].Grid.CellCount)
                {
                    local = index - first;
                    return true;
                }
            }
            slot = 0;
            local = -1;
            return false;
        }
    }
}
