using UnityEngine;

namespace Common.FlowField
{
    /// <summary>Why a read of the published field produced no sample.</summary>
    internal enum FlowFieldSampleStatus
    {
        Sampled,
        NotReady,
        NonFinite,
        OutOfGrid,
        Inconsistent,
    }

    /// <summary>
    /// What the latest, not yet published state says about a cell. A session
    /// uses it to stop a mover the published field would still send into a
    /// change it has not rebuilt for.
    /// </summary>
    internal interface IFlowFieldSampleSafety
    {
        /// <summary>Returns true and the sample to use when the latest state overrides the published one.</summary>
        bool TryOverride(
            FlowFieldField field,
            int index,
            Vector3 worldPosition,
            in FlowFieldSample published,
            out FlowFieldSample sample);
    }

    /// <summary>
    /// Reads a published field in either space mode. It never throws; the
    /// session turns a failed status into the exception its contract names.
    /// </summary>
    internal static class FlowFieldSampler
    {
        internal static FlowFieldSampleStatus Sample(
            FlowFieldField field,
            Vector3 worldPosition,
            IFlowFieldSampleSafety safety,
            out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (field == null)
                return FlowFieldSampleStatus.NotReady;
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                return FlowFieldSampleStatus.NonFinite;

            FlowFieldGridSpace grid = field.Grid;
            if (!grid.ContainsWorldPosition(worldPosition))
                return FlowFieldSampleStatus.OutOfGrid;

            int index;
            FlowFieldSample published;
            if (field.Surface != null)
            {
                if (!grid.TryWorldToLocal(worldPosition, out int x, out int z))
                    return FlowFieldSampleStatus.Inconsistent;
                index = grid.ToFlatIndex(x, z);
                published = ReadSurfaceCell(field, index);
            }
            else
            {
                if (!grid.TryWorldToLocal(worldPosition, out int x, out int y, out int z))
                    return FlowFieldSampleStatus.OutOfGrid;
                index = grid.ToFlatIndex(x, y, z);
                published = new FlowFieldSample(
                    field.FinalDirections[index],
                    field.FinalSpeeds[index],
                    Vector3.zero,
                    false,
                    true);
            }

            sample = safety != null && safety.TryOverride(field, index, worldPosition, published, out FlowFieldSample latest)
                ? latest
                : published;
            return FlowFieldSampleStatus.Sampled;
        }

        // A cell without a surface has nothing to follow. Elsewhere the
        // direction lies in the surface plane.
        private static FlowFieldSample ReadSurfaceCell(FlowFieldField field, int index)
        {
            FlowFieldSurfaceData surface = field.Surface;
            if (!surface.IsSurfaceValid(index))
                return FlowFieldSample.Stopped;
            Vector3 normal = surface.GetSurfaceNormal(index);
            Vector3 direction = field.FinalDirections[index];
            if (direction.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                direction = Vector3.ProjectOnPlane(direction, normal).normalized;
            return new FlowFieldSample(direction, field.FinalSpeeds[index], normal, true, true);
        }
    }
}
