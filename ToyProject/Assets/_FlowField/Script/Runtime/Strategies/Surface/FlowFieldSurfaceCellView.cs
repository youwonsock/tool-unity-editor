using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Surface2D cells the sample safety rule reads: one layer of cells
    /// (any other y is outside the grid), 8 neighbor steps, the ground's step
    /// links, and the drops and steps up between cell heights. Blocked,
    /// walkable and the escape come from the latest cells when a build in
    /// progress knows newer ones for this field. A Surface2D field always
    /// carries its Surface.
    /// </summary>
    internal readonly struct FlowFieldSurfaceCellView : IFlowFieldCellView
    {
        private readonly FlowFieldField _field;
        private readonly IFlowFieldLatestCells _latest;
        private readonly FlowFieldSafety _regions;

        // latest must describe this field, or be null.
        internal FlowFieldSurfaceCellView(FlowFieldField field, IFlowFieldLatestCells latest, FlowFieldSafety regions)
        {
            _field = field;
            _latest = latest;
            _regions = regions;
        }

        public FlowFieldField Field => _field;
        public FlowFieldGridSpace Grid => _field.Grid;
        public int StepCount => FlowFieldNeighborUtility.SurfaceCount;
        public bool HasVerticalAxis => false;

        public Vector3 Planar(Vector3 direction)
        {
            direction.y = 0f;
            return direction;
        }

        public void Locate(int index, out int x, out int y, out int z) => _field.Grid.Locate(index, out x, out y, out z);

        public bool TryCell(int x, int y, int z, out int index) => _field.Grid.TryLocal(x, y, z, out index);

        public int DirectionIndex(int dx, int dy, int dz) => FlowFieldNeighborUtility.FindDirectionIndex(dx, dz);

        public bool IsUncertain(int index)
            => _regions != null && _regions.IsCellUncertain(_field.Grid, _field.Grid.CellCenter(index));

        public bool IsBlocked(int index) => _latest != null ? _latest.IsBlocked(index) : _field.Blocked[index];

        public bool IsWalkable(int index)
            => _latest != null ? _latest.IsWalkable(index) : _field.Surface.IsSurfaceValid(index);

        // The published link: a newly blocked cell on the way is caught by
        // the cell checks around it.
        public bool HasLink(int index, int direction) => (_field.Topology[index] & (1u << direction)) != 0u;

        // A Surface2D path links only cells inside the Goal's influence, so
        // other steps read the published Surface's step links (Max Slope is a
        // per-cell validity check), both sides of a diagonal as the path does.
        public bool HasGroundLink(int index, int direction)
        {
            int mask = _field.Surface.NeighborMasks[index];
            if ((mask & (1 << direction)) == 0)
                return false;
            int dx = FlowFieldNeighborUtility.DeltaX[direction];
            int dz = FlowFieldNeighborUtility.DeltaZ[direction];
            return dx == 0 || dz == 0
                || (mask & (1 << (dx > 0 ? 0 : 1))) != 0 && (mask & (1 << (dz > 0 ? 2 : 3))) != 0;
        }

        // The published field keeps no escape directions, so without the
        // latest cells the final direction stands in.
        public Vector3 EscapeDirection(int index)
            => _latest != null
                ? _latest.EscapeDirection(index)
                : _field.EscapeDirections != null
                    ? _field.EscapeDirections[index]
                    : _field.FinalDirections[index];

        // A drop over Max Step Height is unsafe; a diagonal may also slip into
        // either cell beside it, and on through one the mover can step into.
        // A blocked cell or a step up over Max Step Height walls the mover.
        public bool IsCrossingUnsafe(int x, int y, int z, int dx, int dy, int dz, int target, out bool walled)
        {
            walled = false;
            FlowFieldGridSpace grid = _field.Grid;
            int from = grid.ToFlatIndex(x, z);
            if (IsDrop(from, target)
                || dx != 0 && dz != 0
                    && (IsDropThrough(from, grid.ToFlatIndex(x + dx, z), target)
                        || IsDropThrough(from, grid.ToFlatIndex(x, z + dz), target)))
                return true;
            walled = IsBlocked(target) || IsStepUp(from, target);
            return false;
        }

        // Along the surface at the blocked cell.
        public FlowFieldSample EscapeSample(int index, Vector3 escape, in FlowFieldSample published)
        {
            Vector3 normal = FlowFieldVectorUtility.ValidateSurfaceNormal(_field.Surface.GetSurfaceNormal(index));
            Vector3 direction = Vector3.ProjectOnPlane(escape, normal);
            direction = direction.sqrMagnitude > FlowFieldVectorUtility.DirectionEpsilonSqr
                ? direction.normalized
                : Vector3.zero;
            return new FlowFieldSample(direction, 1f, normal, hasSurface: true, hasCell: true);
        }

        // A step down by more than Max Step Height, which the ground does not
        // link: a ledge a mover cannot come back from.
        private bool IsDrop(int from, int to)
        {
            FlowFieldSurfaceData surface = _field.Surface;
            return surface.Heights[to] < surface.Heights[from] - surface.MaxStepHeight;
        }

        // A step up by more than Max Step Height: the mover meets it like a wall.
        private bool IsStepUp(int from, int to)
        {
            FlowFieldSurfaceData surface = _field.Surface;
            return surface.Heights[to] > surface.Heights[from] + surface.MaxStepHeight;
        }

        // A diagonal's drop into the cell beside it, or beyond it when the
        // mover can step into that cell.
        private bool IsDropThrough(int from, int beside, int to)
            => IsDrop(from, beside) || !IsBlocked(beside) && !IsStepUp(from, beside) && IsDrop(beside, to);
    }
}
