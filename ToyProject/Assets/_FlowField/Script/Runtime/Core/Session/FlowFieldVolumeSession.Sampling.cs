using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldVolumeFieldData;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    /// <summary>Reads of the published Volume3D field.</summary>
    internal sealed partial class FlowFieldVolumeSession
    {
        internal override bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsReady || !FlowFieldGridSpace.IsFinite(worldPosition))
                return false;

            VolumeFieldData field = _committed;
            // A newly accepted grid/source invalidates sampling from the
            // previous publication immediately. Ordinary Goal/obstacle
            // rebuilds keep the last same-source result available until the
            // replacement is committed.
            if (_hasLatestRequestedInput
                && !field.Request.HasSameGrid(_latestRequestedInput))
                return false;
            if (!field.Grid.ContainsWorldPosition(worldPosition)
                || !field.Grid.TryWorldToLocal(worldPosition, out int x, out int y, out int z))
                return false;

            int index = field.Grid.ToFlatIndex(x, y, z);
            Vector3 center = field.Grid.LocalToWorldCenter(x, y, z);
            if (_safety.IsLatestCellUnknown(field, center))
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                return true;
            }

            if (field.Blocked[index])
            {
                if (_safety.IsEscapePathUnsafe(field, index))
                {
                    sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                    return true;
                }
                Vector3 escape = field.EscapeDirections[index];
                sample = new FlowFieldSample(escape, escape.sqrMagnitude > 0.00000001f ? 1f : 0f, Vector3.zero, false, true);
                return true;
            }

            int next = field.NextCells[index];
            if (next >= 0 && _safety.IsLatestPathUnsafe(field, index, next))
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                return true;
            }
            if (next == -1 && _safety.IsDefaultDirectionPathUnsafe(field, index))
            {
                sample = new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);
                return true;
            }

            sample = new FlowFieldSample(
                field.FinalDirections[index],
                field.FinalSpeeds[index],
                Vector3.zero,
                false,
                true);
            return true;
        }

        internal override FlowFieldSample Sample(Vector3 worldPosition)
        {
            if (!TrySample(worldPosition, out FlowFieldSample sample))
                throw new InvalidOperationException("World position is outside the initialized FlowField volume.");
            return sample;
        }

        internal override FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            if (!IsInitialized || !Grid.IsValid)
                throw new InvalidOperationException("FlowField volume session is not initialized.");
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                throw new ArgumentOutOfRangeException(nameof(worldPosition));

            Vector3 clamped = Grid.ClampWorldXYZ(worldPosition);
            return new FlowFieldClampResult(
                clamped,
                !Mathf.Approximately(worldPosition.x, clamped.x),
                !Mathf.Approximately(worldPosition.y, clamped.y),
                !Mathf.Approximately(worldPosition.z, clamped.z));
        }
    }
}
