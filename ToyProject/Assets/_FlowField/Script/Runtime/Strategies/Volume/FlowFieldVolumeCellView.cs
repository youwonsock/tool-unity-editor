using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The Volume3D cells the sample safety rule reads: every cell of the
    /// grid, 26 neighbor steps along all three axes, and the published field
    /// itself, since Volume3D knows nothing newer than what it published.
    /// </summary>
    internal readonly struct FlowFieldVolumeCellView : IFlowFieldCellView
    {
        private readonly FlowFieldField _field;
        private readonly FlowFieldSafety _regions;

        internal FlowFieldVolumeCellView(FlowFieldField field, FlowFieldSafety regions)
        {
            _field = field;
            _regions = regions;
        }

        public FlowFieldField Field => _field;
        public FlowFieldGridSpace Grid => _field.Grid;
        public int StepCount => FlowFieldNeighborUtility.VolumeCount;
        public bool HasVerticalAxis => true;
        public Vector3 Planar(Vector3 direction) => direction;

        public void Locate(int index, out int x, out int y, out int z) => _field.Grid.Locate(index, out x, out y, out z);

        public bool TryCell(int x, int y, int z, out int index) => _field.Grid.TryLocal(x, y, z, out index);

        public int DirectionIndex(int dx, int dy, int dz) => FlowFieldNeighborUtility.FindDirectionIndex(dx, dy, dz);

        public bool IsUncertain(int index)
            => _regions != null && _regions.IsCellUncertain(_field.Grid, _field.Grid.CellCenter(index));

        public bool IsBlocked(int index) => _field.Blocked[index];

        // A mover can be anywhere in the volume; only blocked cells are closed.
        public bool IsWalkable(int index) => true;

        // The published link: a newly blocked cell on the way is caught by
        // the cell checks around it.
        public bool HasLink(int index, int direction) => (_field.Topology[index] & (1u << direction)) != 0u;

        // A Volume3D path links every open cell, so its links serve.
        public bool HasGroundLink(int index, int direction) => HasLink(index, direction);

        public Vector3 EscapeDirection(int index)
            => _field.EscapeDirections != null ? _field.EscapeDirections[index] : _field.FinalDirections[index];

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
