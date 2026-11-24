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
        /// <summary>True when these cells are the published field's cells, newer or not.</summary>
        bool Describes(FlowFieldField field);
        bool IsBlocked(int index);
        bool IsWalkable(int index);
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
        // How far from a mover (a fraction of the cell size) the exit check
        // also follows the faces after the first. A mover moves a fraction of
        // a cell between samples, so near a corner it crosses both faces
        // before it samples again; one moving farther than this between
        // samples can still cut a corner.
        private const float ExitLookAheadFraction = 0.25f;
        // Faces this close together are reached at once: an edge or corner.
        private const float ExitTieFraction = 0.0001f;
        // The unit vector of each neighbour step; Surface2D uses the first 8.
        private static readonly Vector3[] StepDirections = CreateStepDirections();

        /// <summary>Returns true and the sample to use when the rule overrides the published one.</summary>
        internal static bool Apply(
            FlowFieldField field,
            int index,
            Vector3 worldPosition,
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

            // The sample follows the final direction. Where a Modifier turned
            // it, the step it now heads for is checked too: the nearest of the
            // neighbour steps (8 in Surface2D, 26 in Volume3D), the step a
            // path would take that way, so a small turn along a wall keeps
            // moving. An unturned Default Direction also checks every cell
            // its axes can step into, but the ground link only on the step it
            // heads for: a Surface projection leaves tiny side components,
            // and a ledge beside the way is no reason to stop.
            int next = field.NextCells[index];
            Vector3 baseDirection = field.BaseDirections[index];
            Vector3 direction = field.FinalDirections[index];
            bool turned = direction != baseDirection;
            bool pathStep = next >= 0 && next != index;
            bool unsafeMove = pathStep
                ? IsStepUnsafe(field.Grid, index, next, pathLink: true, in cells)
                    || turned && IsHeadingUnsafe(field.Grid, index, direction, in cells)
                : next == -1 && (turned
                    ? IsHeadingUnsafe(field.Grid, index, direction, in cells)
                    : IsDirectionUnsafe(field.Grid, index, baseDirection, in cells)
                        || IsHeadingUnsafe(field.Grid, index, baseDirection, in cells));
            // Off the path's own step, the step this mover crosses next along
            // the sample's direction, from where it is in its cell: a mover
            // drifting sideways reaches a ledge beside the way there.
            if (!unsafeMove && (next == -1 || pathStep && turned))
                unsafeMove = IsExitStepUnsafe(field.Grid, index, worldPosition, published.Direction, in cells);
            if (!unsafeMove)
                return false;
            sample = Stop(published);
            return true;
        }

        // Every cell of the box from this cell to the next, and the link
        // itself: the path's link for a path step, the ground's otherwise.
        private static bool IsStepUnsafe(FlowFieldGridSpace grid, int index, int next, bool pathLink, in Cells cells)
        {
            if (next >= grid.CellCount)
                return true;
            grid.Locate(index, out int x0, out int y0, out int z0);
            grid.Locate(next, out int x1, out int y1, out int z1);
            int direction = grid.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? FlowFieldNeighborUtility.FindDirectionIndex(x1 - x0, y1 - y0, z1 - z0)
                : FlowFieldNeighborUtility.FindDirectionIndex(x1 - x0, z1 - z0);
            if (direction < 0
                || !(pathLink ? cells.HasLink(index, direction) : cells.HasGroundLink(index, direction)))
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
            in Cells cells)
        {
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            int sx = Sign(direction.x, DirectionAxisThreshold);
            int sy = volume ? Sign(direction.y, DirectionAxisThreshold) : 0;
            int sz = Sign(direction.z, DirectionAxisThreshold);
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

        // The nearest neighbour step to the direction, checked as a step. A
        // direction with no heading (none, or straight up or down on a
        // Surface) has nowhere to go.
        private static bool IsHeadingUnsafe(FlowFieldGridSpace grid, int index, Vector3 direction, in Cells cells)
        {
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            if (!volume)
                direction.y = 0f;
            int count = volume ? FlowFieldNeighborUtility.VolumeCount : FlowFieldNeighborUtility.SurfaceCount;
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
            grid.Locate(index, out int x, out int y, out int z);
            return !grid.TryLocal(
                    x + FlowFieldNeighborUtility.DeltaX[nearest],
                    y + FlowFieldNeighborUtility.DeltaY[nearest],
                    z + FlowFieldNeighborUtility.DeltaZ[nearest],
                    out int target)
                || IsStepUnsafe(grid, index, target, pathLink: false, in cells);
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
        private static bool IsExitStepUnsafe(
            FlowFieldGridSpace grid,
            int index,
            Vector3 worldPosition,
            Vector3 direction,
            in Cells cells)
        {
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            if (!volume)
                direction.y = 0f;
            Vector3 offset = worldPosition - grid.CellCenter(index);
            float half = 0.5f * grid.CellSize;
            float toX = DistanceToFace(offset.x, direction.x, half);
            float toY = volume ? DistanceToFace(offset.y, direction.y, half) : float.PositiveInfinity;
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
            grid.Locate(index, out int x, out int y, out int z);
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
                if (IsCrossingUnsafe(grid, volume, x, y, z, dx, dy, dz, in cells, out bool walled))
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

        // One crossing from cell (x, y, z): the cells it passes and, on a
        // Surface2D, whether it drops. A diagonal may slip into either cell
        // beside it, and on through one the mover can step into. Walled when
        // the mover cannot enter the cell it crosses into.
        private static bool IsCrossingUnsafe(
            FlowFieldGridSpace grid,
            bool volume,
            int x,
            int y,
            int z,
            int dx,
            int dy,
            int dz,
            in Cells cells,
            out bool walled)
        {
            walled = false;
            if (!grid.TryLocal(x + dx, y + dy, z + dz, out int target))
                return true;
            for (int cx = Mathf.Min(x, x + dx); cx <= Mathf.Max(x, x + dx); cx++)
            for (int cy = Mathf.Min(y, y + dy); cy <= Mathf.Max(y, y + dy); cy++)
            for (int cz = Mathf.Min(z, z + dz); cz <= Mathf.Max(z, z + dz); cz++)
            {
                if (grid.TryLocal(cx, cy, cz, out int cell) && (cells.IsUncertain(cell) || !cells.IsWalkable(cell)))
                    return true;
            }
            // A Volume3D link is missing only next to a blocked cell.
            if (volume)
            {
                walled = cells.IsBlocked(target);
                return false;
            }
            int from = grid.ToFlatIndex(x, z);
            if (cells.IsDrop(from, target)
                || dx != 0 && dz != 0
                    && (cells.IsDropThrough(from, grid.ToFlatIndex(x + dx, z), target)
                        || cells.IsDropThrough(from, grid.ToFlatIndex(x, z + dz), target)))
                return true;
            walled = cells.IsBlocked(target) || cells.IsStepUp(from, target);
            return false;
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

            // The published link: a newly blocked cell on the way is caught by
            // the cell checks around it.
            internal bool HasLink(int index, int direction)
                => (_field.Topology[index] & (1u << direction)) != 0u;

            // The link the ground allows whatever the Goal. A Surface2D path
            // links only cells inside the Goal's influence, so other steps
            // read the published Surface's step links (Max Slope is a per-cell
            // validity check), both sides of a diagonal as the path does. A
            // Volume3D path links every open cell, so its links serve.
            internal bool HasGroundLink(int index, int direction)
            {
                FlowFieldSurfaceData surface = _field.Surface;
                if (surface == null)
                    return HasLink(index, direction);
                int mask = surface.NeighborMasks[index];
                if ((mask & (1 << direction)) == 0)
                    return false;
                int dx = FlowFieldNeighborUtility.DeltaX[direction];
                int dz = FlowFieldNeighborUtility.DeltaZ[direction];
                return dx == 0 || dz == 0
                    || (mask & (1 << (dx > 0 ? 0 : 1))) != 0 && (mask & (1 << (dz > 0 ? 2 : 3))) != 0;
            }

            // A Surface2D step down by more than Max Step Height, which the
            // ground does not link: a ledge a mover cannot come back from.
            internal bool IsDrop(int from, int to)
            {
                FlowFieldSurfaceData surface = _field.Surface;
                return surface != null && surface.Heights[to] < surface.Heights[from] - surface.MaxStepHeight;
            }

            // A Surface2D step up by more than Max Step Height: the mover meets
            // it like a wall.
            internal bool IsStepUp(int from, int to)
            {
                FlowFieldSurfaceData surface = _field.Surface;
                return surface != null && surface.Heights[to] > surface.Heights[from] + surface.MaxStepHeight;
            }

            // A diagonal's drop into the cell beside it, or beyond it when the
            // mover can step into that cell.
            internal bool IsDropThrough(int from, int beside, int to)
                => IsDrop(from, beside) || !IsBlocked(beside) && !IsStepUp(from, beside) && IsDrop(beside, to);

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
