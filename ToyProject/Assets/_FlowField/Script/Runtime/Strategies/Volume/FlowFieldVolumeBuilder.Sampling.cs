using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using BuildContext = Common.FlowField.FlowFieldVolumeBuildContext;
using VolumeFieldData = Common.FlowField.FlowFieldField<Common.FlowField.FlowFieldVolumeRequest>;
using BuildPhase = Common.FlowField.FlowFieldVolumeBuildPhase;
using static Common.FlowField.FlowFieldVolumeNavigation;

namespace Common.FlowField
{
    /// <summary>Reads of the published Volume3D field.</summary>
    internal sealed partial class FlowFieldVolumeBuilder
    {
        public FlowFieldSampleStatus TrySample(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsReady)
                return FlowFieldSampleStatus.NotReady;
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                return FlowFieldSampleStatus.NonFinite;
            return FlowFieldSampler.Sample(_committed, worldPosition, this, out sample);
        }

        /// <summary>
        /// Stops a mover in a region whose obstacles changed since the build,
        /// and lets one inside a blocked cell escape unless that path is unsafe.
        /// </summary>
        bool IFlowFieldSampleSafety.TryOverride(
            FlowFieldField published,
            int index,
            Vector3 worldPosition,
            in FlowFieldSample sample,
            out FlowFieldSample result)
        {
            var field = (VolumeFieldData)published;
            result = sample;
            field.Grid.FromFlatIndex(index, out int x, out int y, out int z);
            Vector3 center = field.Grid.LocalToWorldCenter(x, y, z);
            if (_safety.IsLatestCellUnknown(field, center))
            {
                result = Stopped;
                return true;
            }

            if (field.Blocked[index])
            {
                if (_safety.IsEscapePathUnsafe(field, index))
                {
                    result = Stopped;
                    return true;
                }
                Vector3 escape = field.EscapeDirections[index];
                result = new FlowFieldSample(escape, escape.sqrMagnitude > 0.00000001f ? 1f : 0f, Vector3.zero, false, true);
                return true;
            }

            int next = field.NextCells[index];
            if (next >= 0 && _safety.IsLatestPathUnsafe(field, index, next)
                || next == -1 && _safety.IsDefaultDirectionPathUnsafe(field, index))
            {
                result = Stopped;
                return true;
            }
            return false;
        }

        // A cell exists here, but nothing may move through it yet.
        private static FlowFieldSample Stopped => new FlowFieldSample(Vector3.zero, 0f, Vector3.zero, false, true);

        public FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            if (!_session.IsInitialized || !Grid.IsValid)
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
