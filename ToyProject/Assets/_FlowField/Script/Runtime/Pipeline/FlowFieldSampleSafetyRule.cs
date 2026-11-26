using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// What a mode knows about its cells that the published field may not
    /// show yet, such as the base stage of a build in progress: which cells
    /// are blocked or walkable now, and how to leave a blocked one. Links stay
    /// with the published field, because a build in progress links its cells
    /// for its own Goal, not for the steps the published field takes.
    /// </summary>
    internal interface IFlowFieldLatestCells
    {
        /// <summary>
        /// True, with the arrays to read, when these cells are the published
        /// field's cells, newer or not. One call a read; the rest reads arrays.
        /// </summary>
        bool TryCapture(FlowFieldField field, out FlowFieldStagedCells cells);
    }

    /// <summary>The latest cells of one read, as arrays a build in progress writes on this thread.</summary>
    internal readonly struct FlowFieldStagedCells
    {
        private readonly bool[] _blocked;
        private readonly FlowFieldSurfaceData _surface;
        private readonly Vector3[] _escapeDirections;

        internal FlowFieldStagedCells(bool[] blocked, FlowFieldSurfaceData surface, Vector3[] escapeDirections)
        {
            _blocked = blocked;
            _surface = surface;
            _escapeDirections = escapeDirections;
        }

        internal bool IsBlocked(int index) => _blocked[index];

        internal bool IsWalkable(int index) => _surface != null && _surface.IsSurfaceValid(index);

        internal Vector3 EscapeDirection(int index)
            => _escapeDirections != null ? _escapeDirections[index] : Vector3.zero;
    }

    /// <summary>
    /// The read-time safety rule both space modes and every bake mode share.
    /// A published field can lag behind notified obstacle changes and the
    /// latest build: a sample that would take a mover into such a change
    /// stops, and a mover inside a cell that is now blocked is sent out.
    /// What differs between the modes comes from an <see cref="IFlowFieldCellView"/>,
    /// which <see cref="FlowFieldSampler"/> picks once per read.
    /// </summary>
    internal static class FlowFieldSampleSafetyRule
    {
        private const float EscapeAxisThreshold = 0.001f;
        private const float DirectionAxisThreshold = 0.000001f;
        // How far from a mover (a fraction of the cell size) the exit check
        // also follows the faces after the first. A mover moves a fraction of
        // a cell between samples, so near a corner it crosses both faces
        // before it samples again; one moving farther than this between
        // samples can still cut a corner.
        private const float ExitLookAheadFraction = 0.25f;
        // Faces this close together are reached at once: an edge or corner.
        private const float ExitTieFraction = 0.0001f;
        // The unit vector of each neighbor step; Surface2D uses the first 8.
        private static readonly Vector3[] StepDirections = CreateStepDirections();

        /// <summary>Returns true and the sample to use when the rule overrides the published one.</summary>
        internal static bool Apply<TView>(
            in TView cells,
            int index,
            Vector3 worldPosition,
            in FlowFieldSample published,
            out FlowFieldSample sample)
            where TView : struct, IFlowFieldCellView
        {
            sample = published;
            FlowFieldField field = cells.Field;

            if (cells.IsUncertain(index) || !cells.IsWalkable(index))
            {
                sample = Stop(published);
                return true;
            }
            if (cells.IsBlocked(index))
            {
                sample = Escape(in cells, index, published);
                return true;
            }

            // The sample follows the final direction. Where a Modifier turned
            // it, the step it now heads for is checked too: the nearest of the
            // neighbor steps (8 in Surface2D, 26 in Volume3D), the step a
            // path would take that way, so a small turn along a wall keeps
            // moving. An unturned Default Direction also checks every cell
            // its axes can step into, but the ground link only on the step it
            // heads for: a Surface projection leaves tiny side components,
            // and a ledge beside the way is no reason to stop.
            int next = cells.NextCell(index);
            Vector3 baseDirection = field.Goal.BaseDirections[index];
            Vector3 direction = field.Goal.FinalDirections[index];
            bool turned = direction != baseDirection;
            bool pathStep = next >= 0 && next != index;
            bool unsafeMove = pathStep
                ? IsStepUnsafe(in cells, index, next, pathLink: true)
                    || turned && IsHeadingUnsafe(in cells, index, direction)
                : next == FlowFieldNextCell.DefaultDirection && (turned
                    ? IsHeadingUnsafe(in cells, index, direction)
                    : IsDirectionUnsafe(in cells, index, baseDirection)
                        || IsHeadingUnsafe(in cells, index, baseDirection));
            // Off the path's own step, the step this mover crosses next along
            // the sample's direction, from where it is in its cell: a mover
            // drifting sideways reaches a ledge beside the way there.
            if (!unsafeMove && (next == FlowFieldNextCell.DefaultDirection || pathStep && turned))
                unsafeMove = IsExitStepUnsafe(in cells, index, worldPosition, published.Direction);
            if (!unsafeMove)
                return false;
            sample = Stop(published);
            return true;
        }

        // Every cell of the box from this cell to the next, and the link
        // itself: the path's link for a path step, the ground's otherwise.
        private static bool IsStepUnsafe<TView>(in TView cells, int index, int next, bool pathLink)
            where TView : struct, IFlowFieldCellView
        {
            if (!cells.Contains(next))
                return true;
            cells.Locate(index, out int x0, out int y0, out int z0);
            cells.Locate(next, out int x1, out int y1, out int z1);
            int direction = cells.DirectionIndex(x1 - x0, y1 - y0, z1 - z0);
            if (direction < 0
                || !(pathLink ? cells.HasLink(index, direction) : cells.HasGroundLink(index, direction)))
                return true;
            for (int x = Mathf.Min(x0, x1); x <= Mathf.Max(x0, x1); x++)
            for (int y = Mathf.Min(y0, y1); y <= Mathf.Max(y0, y1); y++)
            for (int z = Mathf.Min(z0, z1); z <= Mathf.Max(z0, z1); z++)
            {
                if (cells.TryCell(x, y, z, out int cell) && IsUnsafe(in cells, cell))
                    return true;
            }
            return false;
        }

        // Every cell the direction can step into along its non-zero axes; a
        // direction with none has nowhere to go.
        private static bool IsDirectionUnsafe<TView>(in TView cells, int index, Vector3 direction)
            where TView : struct, IFlowFieldCellView
        {
            int sx = Sign(direction.x, DirectionAxisThreshold);
            int sy = cells.HasVerticalAxis ? Sign(direction.y, DirectionAxisThreshold) : 0;
            int sz = Sign(direction.z, DirectionAxisThreshold);
            if (sx == 0 && sy == 0 && sz == 0)
                return true;
            cells.Locate(index, out int x, out int y, out int z);
            for (int mask = 1; mask < 8; mask++)
            {
                if ((mask & 1) != 0 && sx == 0 || (mask & 2) != 0 && sy == 0 || (mask & 4) != 0 && sz == 0)
                    continue;
                int tx = x + ((mask & 1) != 0 ? sx : 0);
                int ty = y + ((mask & 2) != 0 ? sy : 0);
                int tz = z + ((mask & 4) != 0 ? sz : 0);
                if (!cells.TryCell(tx, ty, tz, out int target) || IsUnsafe(in cells, target))
                    return true;
            }
            return false;
        }

        // The nearest neighbor step to the direction, checked as a step. A
        // direction with no heading (none, or straight up or down on a
        // Surface) has nowhere to go.
        private static bool IsHeadingUnsafe<TView>(in TView cells, int index, Vector3 direction)
            where TView : struct, IFlowFieldCellView
        {
            direction = cells.Planar(direction);
            int count = cells.StepCount;
            int nearest = -1;
            float best = DirectionAxisThreshold;
            for (int i = 0; i < count; i++)
            {
                float alignment = Vector3.Dot(direction, StepDirections[i]);
                if (alignment > best)
                {
                    best = alignment;
                    nearest = i;
                }
            }
            if (nearest < 0)
                return true;
            cells.Locate(index, out int x, out int y, out int z);
            return !cells.TryCell(
                    x + FlowFieldNeighborUtility.DeltaX[nearest],
                    y + FlowFieldNeighborUtility.DeltaY[nearest],
                    z + FlowFieldNeighborUtility.DeltaZ[nearest],
                    out int target)
                || IsStepUnsafe(in cells, index, target, pathLink: false);
        }

        // The steps a mover at this position takes next in a straight line:
        // through the face, edge or corner of its cell the direction reaches
        // first, and on through every face it reaches within a quarter cell of
        // where it is, in the order it crosses them (a corner it is about to
        // cut before it samples again). Near the side of its cell even a slight
        // sideways drift crosses there next. Only what a mover cannot come back
        // from stops it: the grid edge, a notified change, no ground, or a
        // Surface2D drop over Max Step Height. A blocked cell that is no drop is
        // fine, as the escape sends a mover back out, and so is a step up over
        // Max Step Height, which the mover meets like a wall: the check ends
        // there, and a turn along either keeps moving.
        private static bool IsExitStepUnsafe<TView>(in TView cells, int index, Vector3 worldPosition, Vector3 direction)
            where TView : struct, IFlowFieldCellView
        {
            FlowFieldGridSpace grid = cells.Grid;
            direction = cells.Planar(direction);
            Vector3 offset = worldPosition - grid.CellCenter(index);
            float half = 0.5f * grid.CellSize;
            float toX = DistanceToFace(offset.x, direction.x, half);
            float toY = cells.HasVerticalAxis ? DistanceToFace(offset.y, direction.y, half) : float.PositiveInfinity;
            float toZ = DistanceToFace(offset.z, direction.z, half);
            float first = Mathf.Min(toX, Mathf.Min(toY, toZ));
            if (float.IsPositiveInfinity(first))
                return false;
            // The first crossing is always checked, the ones after it only
            // within the look-ahead.
            float reach = Mathf.Max(first, ExitLookAheadFraction * grid.CellSize);
            float tie = ExitTieFraction * grid.CellSize;
            int sx = direction.x > 0f ? 1 : -1;
            int sy = direction.y > 0f ? 1 : -1;
            int sz = direction.z > 0f ? 1 : -1;
            cells.Locate(index, out int x, out int y, out int z);
            // Each crossing is one face, or the faces reached together (an edge
            // or corner) as one diagonal step.
            float crossed = float.NegativeInfinity;
            while (true)
            {
                float next = float.PositiveInfinity;
                if (toX > crossed && toX <= reach) next = Mathf.Min(next, toX);
                if (toY > crossed && toY <= reach) next = Mathf.Min(next, toY);
                if (toZ > crossed && toZ <= reach) next = Mathf.Min(next, toZ);
                if (float.IsPositiveInfinity(next))
                    return false;
                float upTo = next + tie;
                int dx = toX > crossed && toX <= upTo ? sx : 0;
                int dy = toY > crossed && toY <= upTo ? sy : 0;
                int dz = toZ > crossed && toZ <= upTo ? sz : 0;
                if (IsCrossingUnsafe(in cells, x, y, z, dx, dy, dz, out bool walled))
                    return true;
                // A blocked cell or a step up stops the mover like a wall, so
                // it slides on from here instead.
                if (walled)
                    return false;
                x += dx;
                y += dy;
                z += dz;
                crossed = upTo;
            }
        }

        // One crossing from cell (x, y, z): the cells it passes, then what the
        // mode says about the crossing itself. Walled when the mover cannot
        // enter the cell it crosses into.
        private static bool IsCrossingUnsafe<TView>(
            in TView cells,
            int x,
            int y,
            int z,
            int dx,
            int dy,
            int dz,
            out bool walled)
            where TView : struct, IFlowFieldCellView
        {
            walled = false;
            if (!cells.TryCell(x + dx, y + dy, z + dz, out int target))
                return true;
            for (int cx = Mathf.Min(x, x + dx); cx <= Mathf.Max(x, x + dx); cx++)
            for (int cy = Mathf.Min(y, y + dy); cy <= Mathf.Max(y, y + dy); cy++)
            for (int cz = Mathf.Min(z, z + dz); cz <= Mathf.Max(z, z + dz); cz++)
            {
                if (cells.TryCell(cx, cy, cz, out int cell) && (cells.IsUncertain(cell) || !cells.IsWalkable(cell)))
                    return true;
            }
            return cells.IsCrossingUnsafe(x, y, z, dx, dy, dz, target, out walled);
        }

        // How far along the direction the face of one axis is.
        private static float DistanceToFace(float offset, float component, float half)
        {
            if (component > DirectionAxisThreshold)
                return Mathf.Max(0f, half - offset) / component;
            if (component < -DirectionAxisThreshold)
                return Mathf.Max(0f, half + offset) / -component;
            return float.PositiveInfinity;
        }

        private static Vector3[] CreateStepDirections()
        {
            var steps = new Vector3[FlowFieldNeighborUtility.VolumeCount];
            for (int i = 0; i < steps.Length; i++)
                steps[i] = new Vector3(
                    FlowFieldNeighborUtility.DeltaX[i],
                    FlowFieldNeighborUtility.DeltaY[i],
                    FlowFieldNeighborUtility.DeltaZ[i]).normalized;
            return steps;
        }

        // Only the cell the escape steps into. A mover leaving an obstacle may
        // cross more of it on the way out (a Surface2D escape points to the
        // nearest open cell, which can be several cells away), so a blocked
        // target is fine; a notified change, no ground or the grid edge is not.
        private static bool IsEscapeTargetUnsafe<TView>(in TView cells, int index, Vector3 escape)
            where TView : struct, IFlowFieldCellView
        {
            cells.Locate(index, out int x, out int y, out int z);
            return !cells.TryCell(
                    x + Sign(escape.x, EscapeAxisThreshold),
                    y + (cells.HasVerticalAxis ? Sign(escape.y, EscapeAxisThreshold) : 0),
                    z + Sign(escape.z, EscapeAxisThreshold),
                    out int target)
                || cells.IsUncertain(target)
                || !cells.IsWalkable(target);
        }

        private static FlowFieldSample Escape<TView>(in TView cells, int index, in FlowFieldSample published)
            where TView : struct, IFlowFieldCellView
        {
            Vector3 escape = cells.EscapeDirection(index);
            if (!FlowFieldGridSpace.IsFinite(escape)
                || escape.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr
                || IsEscapeTargetUnsafe(in cells, index, escape))
                return Stop(published);
            return cells.EscapeSample(index, escape, in published);
        }

        private static bool IsUnsafe<TView>(in TView cells, int index)
            where TView : struct, IFlowFieldCellView
            => cells.IsUncertain(index) || cells.IsBlocked(index) || !cells.IsWalkable(index);

        // A cell exists here, but nothing may move through it yet.
        private static FlowFieldSample Stop(in FlowFieldSample published)
            => new FlowFieldSample(Vector3.zero, 0f, published.SurfaceNormal, published.HasSurface, published.HasCell);

        private static int Sign(float value, float threshold)
            => value > threshold ? 1 : value < -threshold ? -1 : 0;
    }
}
