using System;
using UnityEngine;
using VolumeFieldData = Common.FlowField.FlowFieldField<Common.FlowField.FlowFieldVolumeRequest>;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    // The paths Volume3D checks against the shared uncertain regions: a cell,
    // the step to its next cell, its escape and its Default Direction.
    internal sealed class FlowFieldVolumePathSafety
    {
        private readonly FlowFieldSession _session;

        internal FlowFieldVolumePathSafety(FlowFieldSession session) => _session = session;

        // The session owns the regions and marks them when an obstacle changes.
        private FlowFieldSafety _regions => _session.Safety;

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
            => _regions.IsCellUncertain(grid, center);
    }
}
