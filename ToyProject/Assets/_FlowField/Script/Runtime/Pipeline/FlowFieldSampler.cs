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
    /// Reads a published field in either space mode. It never throws; the
    /// session turns a failed status into the exception its contract names
    /// and applies <see cref="FlowFieldSampleSafetyRule"/> to the cell.
    /// </summary>
    internal static class FlowFieldSampler
    {
        internal static FlowFieldSampleStatus Sample(
            FlowFieldField field,
            Vector3 worldPosition,
            out FlowFieldSample sample,
            out int index)
        {
            sample = FlowFieldSample.Stopped;
            index = -1;
            if (field == null)
                return FlowFieldSampleStatus.NotReady;
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                return FlowFieldSampleStatus.NonFinite;

            FlowFieldGridSpace grid = field.Grid;
            if (!grid.ContainsWorldPosition(worldPosition))
                return FlowFieldSampleStatus.OutOfGrid;

            if (field.Surface != null)
            {
                if (!grid.TryWorldToLocal(worldPosition, out int x, out int z))
                    return FlowFieldSampleStatus.Inconsistent;
                index = grid.ToFlatIndex(x, z);
                sample = ReadSurfaceCell(field, index);
            }
            else
            {
                if (!grid.TryWorldToLocal(worldPosition, out int x, out int y, out int z))
                    return FlowFieldSampleStatus.OutOfGrid;
                index = grid.ToFlatIndex(x, y, z);
                sample = new FlowFieldSample(
                    field.FinalDirections[index],
                    field.FinalSpeeds[index],
                    Vector3.zero,
                    false,
                    true);
            }
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
