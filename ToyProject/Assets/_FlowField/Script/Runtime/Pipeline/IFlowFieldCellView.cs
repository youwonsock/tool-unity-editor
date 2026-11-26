using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The cells of one published field as the sample safety rule reads them,
    /// one implementation per space mode. The rule picks the view once per
    /// read and asks it everything that differs between the modes: the axes,
    /// the neighbor steps, which cells exist next to a cell, what blocks or
    /// links them, and how a crossing or an escape ends.
    /// </summary>
    internal interface IFlowFieldCellView
    {
        FlowFieldField Field { get; }
        FlowFieldGridSpace Grid { get; }
        // Neighbor steps: 8 in Surface2D, 26 in Volume3D.
        int StepCount { get; }
        // Volume3D moves along y; a Surface2D direction ignores it.
        bool HasVerticalAxis { get; }
        // The direction with what this mode cannot move along removed.
        Vector3 Planar(Vector3 direction);
        void Locate(int index, out int x, out int y, out int z);
        // The cell at these coordinates, or false when there is none.
        bool TryCell(int x, int y, int z, out int index);
        // The neighbor step for this offset, or -1.
        int DirectionIndex(int dx, int dy, int dz);
        // A notified change that the field has not published yet touches it.
        bool IsUncertain(int index);
        bool IsBlocked(int index);
        // There is ground to move on.
        bool IsWalkable(int index);
        // The published path's link.
        bool HasLink(int index, int direction);
        // The link the ground allows whatever the Goal.
        bool HasGroundLink(int index, int direction);
        Vector3 EscapeDirection(int index);
        // After the cells a crossing passes were checked: whether the
        // crossing into target is one a mover cannot come back from, and
        // walled when the mover cannot enter target.
        bool IsCrossingUnsafe(int x, int y, int z, int dx, int dy, int dz, int target, out bool walled);
        // The sample that sends a mover in a blocked cell out along escape.
        FlowFieldSample EscapeSample(int index, Vector3 escape, in FlowFieldSample published);
    }
}
