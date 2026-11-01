using System;
using System.Collections.Generic;
using UnityEngine;
using VolumeFieldData = Common.FlowField.FlowFieldVolumeFieldData;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    // Owns conservative safety state and sampling decisions. Physics observation,
    // rebuild scheduling and publication policy stay in the session.
    internal sealed class FlowFieldVolumeSafetyState
    {
        private const int MaxUncertainBounds = 64;
        // Changes no running build has observed yet.
        private readonly List<Bounds> _uncertainBounds = new List<Bounds>(8);
        // Changes the running build observes, still unsafe until it is published.
        private readonly List<Bounds> _observingBounds = new List<Bounds>(8);

        internal int UncertainRegionCount => _uncertainBounds.Count + _observingBounds.Count;

        internal void ClearRegions()
        {
            _uncertainBounds.Clear();
            _observingBounds.Clear();
        }

        /// <summary>
        /// Every build queries all obstacles after it starts, so it resolves
        /// the regions recorded so far. A discarded build keeps them here until
        /// its replacement publishes.
        /// </summary>
        internal void BeginBuild()
        {
            for (int index = 0; index < _uncertainBounds.Count; index++)
                AddBounds(_observingBounds, _uncertainBounds[index]);
            _uncertainBounds.Clear();
        }

        internal void ClearObservedRegions() => _observingBounds.Clear();

        // An obstacle mask/clearance change invalidates the complete volume.
        // Preserve its distinct policy: no dirty-region expansion.
        internal void InvalidateRegion(Bounds bounds) => _uncertainBounds.Add(bounds);

        internal void MarkDirty(Bounds bounds, float margin)
        {
            if (!FlowFieldGridSpace.IsFinite(bounds.center) || !FlowFieldGridSpace.IsFinite(bounds.size))
                throw new ArgumentOutOfRangeException(nameof(bounds));
            Bounds expanded = bounds;
            expanded.Expand(Vector3.one * (margin * 2f));
            AddBounds(_uncertainBounds, expanded);
        }

        internal bool IsLatestCellUnknown(VolumeFieldData field, Vector3 center)
        {
            if (field == null || field.Request.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            return IntersectsUncertainBounds(field.Grid, center);
        }

        internal bool IsLatestPathUnsafe(VolumeFieldData field, int current, int next)
        {
            field.Grid.FromFlatIndex(current, out int x0, out int y0, out int z0);
            field.Grid.FromFlatIndex(next, out int x1, out int y1, out int z1);
            for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
            for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
            for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
            {
                if (IsLatestCellUnsafe(field, field.Grid.ToFlatIndex(x, y, z)))
                    return true;
            }
            return false;
        }

        private bool IsLatestCellUnsafe(VolumeFieldData field, int index)
        {
            if (field.Request.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            return IntersectsUncertainBounds(field.Grid, CellCenter(field.Grid, index))
                || field.Blocked[index];
        }

        internal bool IsEscapePathUnsafe(VolumeFieldData field, int index)
        {
            Vector3 direction = field.EscapeDirections[index];
            if (direction.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                return false;
            field.Grid.FromFlatIndex(index, out int x, out int y, out int z);
            int dx = direction.x > 0.001f ? 1 : direction.x < -0.001f ? -1 : 0;
            int dy = direction.y > 0.001f ? 1 : direction.y < -0.001f ? -1 : 0;
            int dz = direction.z > 0.001f ? 1 : direction.z < -0.001f ? -1 : 0;
            if (!field.Grid.IsLocalInBounds(x + dx, y + dy, z + dz))
                return true;
            int neighbor = field.Grid.ToFlatIndex(x + dx, y + dy, z + dz);
            return IsLatestCellUnsafe(field, neighbor);
        }

        internal bool IsDefaultDirectionPathUnsafe(VolumeFieldData field, int index)
        {
            Vector3 direction = FlowFieldVectorUtility.NormalizeDefaultDirection(field.Request.DefaultDirection);
            field.Grid.FromFlatIndex(index, out int x, out int y, out int z);
            int sx = direction.x > 0.000001f ? 1 : direction.x < -0.000001f ? -1 : 0;
            int sy = direction.y > 0.000001f ? 1 : direction.y < -0.000001f ? -1 : 0;
            int sz = direction.z > 0.000001f ? 1 : direction.z < -0.000001f ? -1 : 0;
            int axisCount = (sx != 0 ? 1 : 0) + (sy != 0 ? 1 : 0) + (sz != 0 ? 1 : 0);
            if (axisCount == 0)
                return true;

            int axisMaskLimit = 1 << axisCount;
            for (int mask = 1; mask < axisMaskLimit; mask++)
            {
                int targetX = x;
                int targetY = y;
                int targetZ = z;
                int bit = 0;
                if (sx != 0)
                {
                    if ((mask & (1 << bit)) != 0)
                        targetX += sx;
                    bit++;
                }
                if (sy != 0)
                {
                    if ((mask & (1 << bit)) != 0)
                        targetY += sy;
                    bit++;
                }
                if (sz != 0 && (mask & (1 << bit)) != 0)
                    targetZ += sz;
                if (!field.Grid.IsLocalInBounds(targetX, targetY, targetZ))
                    return true;
                int target = field.Grid.ToFlatIndex(targetX, targetY, targetZ);
                if (IsLatestCellUnsafe(field, target))
                    return true;
            }
            return false;
        }

        private bool IntersectsUncertainBounds(FlowFieldGridSpace grid, Vector3 center)
            => IntersectsUncertainBounds(grid, center, _uncertainBounds)
                || IntersectsUncertainBounds(grid, center, _observingBounds);

        private static bool IntersectsUncertainBounds(FlowFieldGridSpace grid, Vector3 center,
            List<Bounds> uncertainBounds)
        {
            Bounds cell = new Bounds(center, Vector3.one * grid.CellSize);
            for (int i = 0; i < uncertainBounds.Count; i++)
                if (uncertainBounds[i].Intersects(cell))
                    return true;
            return false;
        }

        private static void AddBounds(List<Bounds> list, Bounds bounds)
        {
            for (int index = 0; index < list.Count; index++)
            {
                if (!list[index].Intersects(bounds))
                    continue;
                list[index] = UnionBounds(list[index], bounds);
                return;
            }

            if (list.Count < MaxUncertainBounds)
            {
                list.Add(bounds);
                return;
            }

            // Once the bounded dirty-list is full, retaining a conservative
            // union is safer than evicting an area and treating it as clear.
            list[0] = UnionBounds(list[0], bounds);
        }

        private static Bounds UnionBounds(Bounds left, Bounds right)
        {
            left.Encapsulate(right.min);
            left.Encapsulate(right.max);
            return left;
        }
    }
}
