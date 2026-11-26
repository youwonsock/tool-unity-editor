using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Volume3D cells a read sees: every cell of the grid, 26 neighbor
    /// steps along all three axes, and the published field. A builder knows
    /// nothing newer than what it published; an additional Goal's read takes
    /// the default Goal's newer blocked cells and escape directions.
    /// </summary>
    internal readonly struct FlowFieldVolumeCellView : IFlowFieldCellView
    {
        private readonly FlowFieldField _field;
        private readonly FlowFieldStagedCells _latest;
        private readonly bool _hasLatest;
        private readonly FlowFieldSafety _regions;

        internal FlowFieldVolumeCellView(FlowFieldField field, FlowFieldSafety regions)
            : this(field, default, hasLatest: false, regions)
        {
        }

        // latest serves only with hasLatest, when it describes this field.
        internal FlowFieldVolumeCellView(
            FlowFieldField field,
            in FlowFieldStagedCells latest,
            bool hasLatest,
            FlowFieldSafety regions)
        {
            _field = field;
            _latest = latest;
            _hasLatest = hasLatest;
            _regions = regions;
        }

        public FlowFieldField Field => _field;
        public FlowFieldGridSpace Grid => _field.Grid;

        public bool TryLocate(Vector3 worldPosition, out int index)
        {
            index = -1;
            if (!_field.Grid.TryWorldToLocal(worldPosition, out int x, out int y, out int z))
                return false;
            index = _field.Grid.ToFlatIndex(x, y, z);
            return true;
        }

        public FlowFieldSample ReadCell(int index)
            => new FlowFieldSample(
                _field.Goal.FinalDirections[index],
                _field.Goal.FinalSpeeds[index],
                Vector3.zero,
                hasSurface: false,
                hasCell: true);

        public bool Contains(int index) => (uint)index < (uint)_field.Grid.CellCount;

        public float GroundHeight(int index) => 0f;

        public float StepHeight => float.PositiveInfinity;

        public int NextCell(int index)
        {
            int next = _field.Goal.NextCells[index];
            return FlowFieldNextCell.IsPortal(next) ? int.MaxValue : next;
        }

        public int StepCount => FlowFieldNeighborUtility.VolumeCount;
        public bool HasVerticalAxis => true;
        public Vector3 Planar(Vector3 direction) => direction;

        public void Locate(int index, out int x, out int y, out int z) => _field.Grid.Locate(index, out x, out y, out z);

        public bool TryCell(int x, int y, int z, out int index) => _field.Grid.TryLocal(x, y, z, out index);

        public int DirectionIndex(int dx, int dy, int dz) => FlowFieldNeighborUtility.FindDirectionIndex(dx, dy, dz);

        public bool IsUncertain(int index)
            => _regions != null && _regions.IsCellUncertain(_field.Grid, _field.Grid.CellCenter(index));

        public bool IsBlocked(int index) => _hasLatest ? _latest.IsBlocked(index) : _field.Agent.Blocked[index];

        // A mover can be anywhere in the volume; only blocked cells are closed.
        public bool IsWalkable(int index) => true;

        // The published link: a newly blocked cell on the way is caught by
        // the cell checks around it.
        public bool HasLink(int index, int direction) => (_field.Goal.Topology[index] & (1u << direction)) != 0u;

        // A Volume3D path links every open cell, so its links serve.
        public bool HasGroundLink(int index, int direction) => HasLink(index, direction);

        public Vector3 EscapeDirection(int index)
            => _hasLatest
                ? _latest.EscapeDirection(index)
                : _field.Agent.EscapeDirections != null
                    ? _field.Agent.EscapeDirections[index]
                    : _field.Goal.FinalDirections[index];

        // A link is missing only next to a blocked cell, which walls the mover.
        public bool IsCrossingUnsafe(int x, int y, int z, int dx, int dy, int dz, int target, out bool walled)
        {
            walled = IsBlocked(target);
            return false;
        }

        public FlowFieldSample EscapeSample(int index, Vector3 escape, in FlowFieldSample published)
            => new FlowFieldSample(escape, 1f, Vector3.zero, hasSurface: false, hasCell: true);
    }
}
