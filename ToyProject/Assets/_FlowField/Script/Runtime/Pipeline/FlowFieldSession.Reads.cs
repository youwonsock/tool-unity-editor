using UnityEngine;

namespace Common.FlowField
{
    /// <summary>
    /// The reads both space modes share: samples, clamps and field info, and how
    /// a failed read becomes an exception.
    /// </summary>
    internal sealed partial class FlowFieldSession
    {
        // The read both modes share; it never throws.
        private FlowFieldSampleStatus TrySampleStatus(Vector3 worldPosition, out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (!IsReady)
                return FlowFieldSampleStatus.NotReady;
            FlowFieldField field = _builder.PublishedField;
            FlowFieldSampleStatus status = FlowFieldSampler.Sample(field, worldPosition, out sample, out int index);
            if (status == FlowFieldSampleStatus.Sampled
                && FlowFieldSampleSafetyRule.Apply(field, index, worldPosition, in sample, Safety, _builder.LatestCells, out FlowFieldSample safe))
                sample = safe;
            return status;
        }

        internal bool TrySample(Vector3 worldPosition, out FlowFieldSample sample)
            => TrySampleStatus(worldPosition, out sample) == FlowFieldSampleStatus.Sampled;

        internal FlowFieldSample Sample(Vector3 worldPosition)
        {
            FlowFieldSampleStatus status = TrySampleStatus(worldPosition, out FlowFieldSample sample);
            if (status != FlowFieldSampleStatus.Sampled)
                throw FlowFieldFailures.ForSample(status, nameof(worldPosition));
            return sample;
        }

        // The clamp both modes share; it never throws. It uses the latest
        // grid: Surface2D grids clamp X and Z, Volume3D grids all three axes.
        private FlowFieldClampStatus TryClampStatus(Vector3 worldPosition, out FlowFieldClampResult result)
        {
            result = default;
            if (!IsInitialized)
                return FlowFieldClampStatus.NotInitialized;
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                return FlowFieldClampStatus.NonFinite;
            FlowFieldGridSpace grid = _builder.LatestGrid;
            if (!grid.IsValid)
                return FlowFieldClampStatus.NoGrid;
            bool volume = grid.SpaceMode == FlowFieldSpaceMode.Volume3D;
            Vector3 clamped = volume ? grid.ClampWorldXYZ(worldPosition) : grid.ClampWorldXZ(worldPosition);
            result = new FlowFieldClampResult(
                clamped,
                !Mathf.Approximately(worldPosition.x, clamped.x),
                volume && !Mathf.Approximately(worldPosition.y, clamped.y),
                !Mathf.Approximately(worldPosition.z, clamped.z));
            return FlowFieldClampStatus.Clamped;
        }

        internal bool TryClampPositionToGrid(Vector3 worldPosition, out FlowFieldClampResult result)
            => TryClampStatus(worldPosition, out result) == FlowFieldClampStatus.Clamped;

        /// <summary>The nearest point inside the latest grid.</summary>
        internal FlowFieldClampResult ClampPositionToGrid(Vector3 worldPosition)
        {
            FlowFieldClampStatus status = TryClampStatus(worldPosition, out FlowFieldClampResult result);
            if (status != FlowFieldClampStatus.Clamped)
                throw FlowFieldFailures.ForClamp(status, nameof(worldPosition));
            return result;
        }

        // Only a session that can sample describes its field.
        internal bool TryGetFieldInfo(out FlowFieldFieldInfo info)
        {
            info = default;
            return IsReady && _builder.TryGetFieldInfo(out info);
        }

        /// <summary>
        /// The published field, for display and bakes. It is valid until the
        /// next pump, after which its arrays may be reused.
        /// </summary>
        internal bool TryGetPublishedField(out FlowFieldField field)
        {
            field = IsReady ? _builder.PublishedField : null;
            return field != null;
        }
}
}
