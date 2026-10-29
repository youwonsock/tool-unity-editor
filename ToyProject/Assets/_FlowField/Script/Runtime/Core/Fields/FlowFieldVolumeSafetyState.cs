using System;
using System.Collections.Generic;
using UnityEngine;
using VolumeFieldData = Common.FlowField.FlowFieldVolumeFieldData;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    // Owns conservative safety state and sampling decisions. Physics observation,
    // rebuild scheduling, GPU lifetimes and publication policy stay in the session.
    internal sealed class FlowFieldVolumeSafetyState
    {
        private const int MaxUncertainBounds = 64;
        private readonly List<Bounds> _uncertainBounds = new List<Bounds>(8);
        private Snapshot _snapshot;
        private SafetyCellState[] _spareStates;

        internal int Epoch { get; private set; }
        internal int UncertainRegionCount => _uncertainBounds.Count;
        internal long SnapshotBytes => EstimateSnapshotBytes(_snapshot.Count);

        internal void Reset()
        {
            ClearSnapshot();
            Epoch = 0;
            ClearRegions();
        }

        internal void ClearSnapshot()
        {
            _snapshot = default;
            _spareStates = null;
        }
        internal void ClearRegions() => _uncertainBounds.Clear();

        // An obstacle mask/clearance change invalidates the complete volume.
        // Preserve its distinct policy: no dirty-region expansion or epoch change.
        internal void InvalidateRegion(Bounds bounds) => _uncertainBounds.Add(bounds);

        internal void MarkDirty(Bounds bounds, float margin)
        {
            if (!FlowFieldGridSpace.IsFinite(bounds.center) || !FlowFieldGridSpace.IsFinite(bounds.size))
                throw new ArgumentOutOfRangeException(nameof(bounds));
            Bounds expanded = bounds;
            expanded.Expand(Vector3.one * (margin * 2f));
            Epoch++;
            AddUncertainBounds(expanded);
        }

        internal Snapshot PrepareSnapshot(FlowFieldGridSpace grid, bool[] blocked, int buildSafetyEpoch)
        {
            SafetyCellState[] reuse = _spareStates;
            _spareStates = null;
            return new Snapshot(grid, blocked, buildSafetyEpoch != Epoch, _uncertainBounds, reuse);
        }

        /// <summary>
        /// Replaces the published snapshot. The replaced array is kept for the
        /// next PrepareSnapshot; nothing reads it after publication.
        /// </summary>
        internal void PublishSnapshot(in Snapshot snapshot)
        {
            _spareStates = _snapshot.States;
            _snapshot = snapshot;
        }

        internal static long EstimateSnapshotBytes(int count)
            => count <= 0 ? 0 : checked(count + 32L);

        internal bool IsLatestCellUnknown(VolumeFieldData field, int index, Vector3 center)
        {
            if (field == null || field.Request.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            if (IntersectsUncertainBounds(field.Grid, center))
                return true;
            return _snapshot.Count == field.Grid.CellCount
                && _snapshot.IsUnknown(index);
        }

        internal bool IsLatestCellBlocked(VolumeFieldData field, int index)
        {
            if (field == null || field.Request.BakeMode == FlowFieldBakeMode.StaticBaked)
                return false;
            return _snapshot.Count == field.Grid.CellCount
                && _snapshot.IsBlocked(index);
        }

        internal bool IsLatestPathUnsafe(VolumeFieldData field, int current, int next)
        {
            if (IsLatestCellUnsafe(field, current)
                || IsLatestCellUnsafe(field, next))
                return true;
            field.Grid.FromFlatIndex(current, out int x0, out int y0, out int z0);
            field.Grid.FromFlatIndex(next, out int x1, out int y1, out int z1);
            for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
            for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
            for (int z = Math.Min(z0, z1); z <= Math.Max(z0, z1); z++)
            {
                if (x == x0 && y == y0 && z == z0 || x == x1 && y == y1 && z == z1)
                    continue;
                int intermediate = field.Grid.ToFlatIndex(x, y, z);
                if (IsLatestCellUnsafe(field, intermediate))
                    return true;
            }
            return IsLatestCellUnsafe(field, next);
        }

        private bool IsLatestCellUnsafe(VolumeFieldData field, int index)
        {
            Vector3 center = CellCenter(field.Grid, index);
            return IsLatestCellUnknown(field, index, center)
                || IsLatestCellBlocked(field, index);
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
            => IntersectsUncertainBounds(grid, center, _uncertainBounds);

        private static bool IntersectsUncertainBounds(FlowFieldGridSpace grid, Vector3 center,
            List<Bounds> uncertainBounds)
        {
            Bounds cell = new Bounds(center, Vector3.one * grid.CellSize);
            for (int i = 0; i < uncertainBounds.Count; i++)
                if (uncertainBounds[i].Intersects(cell))
                    return true;
            return false;
        }

        private void AddUncertainBounds(Bounds bounds)
        {
            for (int index = 0; index < _uncertainBounds.Count; index++)
            {
                if (!_uncertainBounds[index].Intersects(bounds))
                    continue;
                _uncertainBounds[index] = UnionBounds(_uncertainBounds[index], bounds);
                return;
            }

            if (_uncertainBounds.Count < MaxUncertainBounds)
            {
                _uncertainBounds.Add(bounds);
                return;
            }

            // Once the bounded dirty-list is full, retaining a conservative
            // union is safer than evicting an area and treating it as clear.
            _uncertainBounds[0] = UnionBounds(_uncertainBounds[0], bounds);
        }

        private static Bounds UnionBounds(Bounds left, Bounds right)
        {
            left.Encapsulate(right.min);
            left.Encapsulate(right.max);
            return left;
        }

        internal enum SafetyCellState : byte
        {
            Unknown = 0,
            Clear = 1,
            Blocked = 2,
        }

        // Build the replacement completely before the session publishes its field.
        // The backing array stays private, and publication only copies this value.
        internal readonly struct Snapshot
        {
            private readonly SafetyCellState[] _states;

            internal int Count => _states?.Length ?? 0;
            internal SafetyCellState[] States => _states;
            internal bool IsUnknown(int index) => _states[index] == SafetyCellState.Unknown;
            internal bool IsBlocked(int index) => _states[index] == SafetyCellState.Blocked;

            internal Snapshot(FlowFieldGridSpace grid, bool[] blocked, bool safetyChangedDuringBuild,
                List<Bounds> uncertainBounds, SafetyCellState[] reuse = null)
            {
                // Every entry is written below, so a reused array needs no clear.
                _states = reuse != null && reuse.Length == grid.CellCount
                    ? reuse
                    : new SafetyCellState[grid.CellCount];
                for (int index = 0; index < _states.Length; index++)
                    _states[index] = blocked[index] ? SafetyCellState.Blocked : SafetyCellState.Clear;
                if (!safetyChangedDuringBuild)
                    return;
                for (int index = 0; index < _states.Length; index++)
                {
                    grid.FromFlatIndex(index, out int x, out int y, out int z);
                    if (IntersectsUncertainBounds(grid, grid.LocalToWorldCenter(x, y, z), uncertainBounds))
                        _states[index] = SafetyCellState.Unknown;
                }
            }
        }
    }
}
