using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What a mode knows about its cells that the published field may not
    /// show yet, such as the base stage of a build in progress.
    /// </summary>
    internal interface IFlowFieldLatestCells
    {
        /// <summary>True when these cells are the published field's cells, newer or not.</summary>
        bool Describes(FlowFieldField field);
        bool IsBlocked(int index);
        bool IsWalkable(int index);
        bool HasLink(int index, int direction);
        Vector3 EscapeDirection(int index);
    }

    /// <summary>
    /// The read-time safety rule both space modes and every bake mode share.
    /// A published field can lag behind notified obstacle changes and the
    /// latest build: a sample that would take a mover into such a change
    /// stops, and a mover inside a cell that is now blocked is sent out.
    /// </summary>
    internal static class FlowFieldSampleSafetyRule
    {
        private const float EscapeAxisThreshold = 0.001f;
        private const float DirectionAxisThreshold = 0.000001f;

        /// <summary>Returns true and the sample to use when the rule overrides the published one.</summary>
        internal static bool Apply(
            FlowFieldField field,
            int index,
            in FlowFieldSample published,
            FlowFieldSafety regions,
            IFlowFieldLatestCells latest,
            out FlowFieldSample sample)
        {
            sample = published;
            var cells = new Cells(field, latest != null && latest.Describes(field) ? latest : null, regions);

            if (cells.IsUncertain(index) || !cells.IsWalkable(index))
            {
                sample = Stop(published);
                return true;
            }
            if (cells.IsBlocked(index))
            {
                sample = Escape(field, index, in cells, published);
                return true;
            }

            int next = field.NextCells[index];
            bool unsafeMove = next >= 0 && next != index
                ? IsStepUnsafe(field.Grid, index, next, in cells)
                : next == -1 && IsDirectionUnsafe(
                    field.Grid, index, field.BaseDirections[index], DirectionAxisThreshold, in cells);
            if (!unsafeMove)
                return false;
            sample = Stop(published);
            return true;
        }

        // Every cell of the box from this cell to the next, and the link itself.
        private static bool IsStepUnsafe(FlowFieldGridSpace grid, int index, int next, in Cells cells)
        {
            if (next >= grid.CellCount)
                return true;
            grid.Locate(index, out int x0, out int y0, out int z0);
            grid.Locate(next, out int x1, out int y1, out int z1);
            int direction = grid.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? FlowFieldNeighborUtility.FindDirectionIndex(x1 - x0, y1 - y0, z1 - z0)
                : FlowFieldNeighborUtility.FindDirectionIndex(x1 - x0, z1 - z0);
            if (direction < 0 || !cells.HasLink(index, direction))
                return true;
            for (int x = Mathf.Min(x0, x1); x <= Mathf.Max(x0, x1); x++)
            for (int y = Mathf.Min(y0, y1); y <= Mathf.Max(y0, y1); y++)
            for (int z = Mathf.Min(z0, z1); z <= Mathf.Max(z0, z1); z++)
            {
                if (grid.TryLocal(x, y, z, out int cell) && cells.IsUnsafe(cell))
                    return true;
            }
            return false;
        }

        // Every cell the direction can step into along its non-zero axes; a
        // direction with none has nowhere to go.
        private static bool IsDirectionUnsafe(
            FlowFieldGridSpace grid,
            int index,
            Vector3 direction,
            float threshold,
            in Cells cells)
        {
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            int sx = Sign(direction.x, threshold);
            int sy = volume ? Sign(direction.y, threshold) : 0;
            int sz = Sign(direction.z, threshold);
            if (sx == 0 && sy == 0 && sz == 0)
                return true;
            grid.Locate(index, out int x, out int y, out int z);
            for (int mask = 1; mask < 8; mask++)
            {
                if ((mask & 1) != 0 && sx == 0 || (mask & 2) != 0 && sy == 0 || (mask & 4) != 0 && sz == 0)
                    continue;
                int tx = x + ((mask & 1) != 0 ? sx : 0);
                int ty = y + ((mask & 2) != 0 ? sy : 0);
                int tz = z + ((mask & 4) != 0 ? sz : 0);
                if (!grid.TryLocal(tx, ty, tz, out int target) || cells.IsUnsafe(target))
                    return true;
            }
            return false;
        }

        // Only the cell the escape steps into. A mover leaving an obstacle may
        // cross more of it on the way out (a Surface2D escape points to the
        // nearest open cell, which can be several cells away), so a blocked
        // target is fine; a notified change, no ground or the grid edge is not.
        private static bool IsEscapeTargetUnsafe(FlowFieldGridSpace grid, int index, Vector3 escape, in Cells cells)
        {
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            grid.Locate(index, out int x, out int y, out int z);
            return !grid.TryLocal(
                    x + Sign(escape.x, EscapeAxisThreshold),
                    y + (volume ? Sign(escape.y, EscapeAxisThreshold) : 0),
                    z + Sign(escape.z, EscapeAxisThreshold),
                    out int target)
                || cells.IsUncertain(target)
                || !cells.IsWalkable(target);
        }

        private static FlowFieldSample Escape(FlowFieldField field, int index, in Cells cells, in FlowFieldSample published)
        {
            Vector3 escape = cells.EscapeDirection(index);
            if (!FlowFieldGridSpace.IsFinite(escape)
                || escape.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr
                || IsEscapeTargetUnsafe(field.Grid, index, escape, in cells))
                return Stop(published);
            if (field.Surface == null)
                return new FlowFieldSample(escape, 1f, Vector3.zero, false, true);
            Vector3 normal = FlowFieldVectorUtility.ValidateSurfaceNormal(field.Surface.GetSurfaceNormal(index));
            Vector3 direction = Vector3.ProjectOnPlane(escape, normal);
            direction = direction.sqrMagnitude > FlowFieldVectorUtility.DirectionEpsilonSqr
                ? direction.normalized
                : Vector3.zero;
            return new FlowFieldSample(direction, 1f, normal, true, true);
        }

        // A cell exists here, but nothing may move through it yet.
        private static FlowFieldSample Stop(in FlowFieldSample published)
            => new FlowFieldSample(Vector3.zero, 0f, published.SurfaceNormal, published.HasSurface, published.HasCell);

        private static int Sign(float value, float threshold)
            => value > threshold ? 1 : value < -threshold ? -1 : 0;

        // The latest cells when the mode has them for this field, otherwise the
        // published field itself, plus the notified uncertain regions.
        private readonly struct Cells
        {
            private readonly FlowFieldField _field;
            private readonly IFlowFieldLatestCells _latest;
            private readonly FlowFieldSafety _regions;

            internal Cells(FlowFieldField field, IFlowFieldLatestCells latest, FlowFieldSafety regions)
            {
                _field = field;
                _latest = latest;
                _regions = regions;
            }

            internal bool IsUncertain(int index)
                => _regions != null && _regions.IsCellUncertain(_field.Grid, _field.Grid.CellCenter(index));

            internal bool IsBlocked(int index)
                => _latest != null ? _latest.IsBlocked(index) : _field.Blocked[index];

            internal bool IsWalkable(int index)
                => _latest != null
                    ? _latest.IsWalkable(index)
                    : _field.Surface == null || _field.Surface.IsSurfaceValid(index);

            internal bool HasLink(int index, int direction)
                => _latest != null
                    ? _latest.HasLink(index, direction)
                    : (_field.Topology[index] & (1u << direction)) != 0u;

            internal Vector3 EscapeDirection(int index)
                => _latest != null
                    ? _latest.EscapeDirection(index)
                    : _field.EscapeDirections != null
                        ? _field.EscapeDirections[index]
                        : _field.FinalDirections[index];

            internal bool IsUnsafe(int index)
                => IsUncertain(index) || IsBlocked(index) || !IsWalkable(index);
        }
    }
}
