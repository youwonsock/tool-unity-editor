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
    }

    /// <summary>
    /// Reads a published field in either space mode and applies
    /// <see cref="FlowFieldSampleSafetyRule"/> to the cell. It picks the mode's
    /// <see cref="IFlowFieldCellView"/> once per read and never throws; the
    /// session turns a failed status into the exception its contract names.
    /// </summary>
    internal static class FlowFieldSampler
    {
        internal static FlowFieldSampleStatus Sample(
            FlowFieldField field,
            Vector3 worldPosition,
            FlowFieldSafety regions,
            IFlowFieldLatestCells latest,
            out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (field == null)
                return FlowFieldSampleStatus.NotReady;
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                return FlowFieldSampleStatus.NonFinite;
            if (!field.Grid.ContainsWorldPosition(worldPosition))
                return FlowFieldSampleStatus.OutOfGrid;

            if (field.Grid.SpaceMode == FlowFieldSpaceMode.Volume3D)
                return Sample(new FlowFieldVolumeCellView(field, regions), worldPosition, out sample);
            // The latest cells serve only when they describe this field.
            FlowFieldStagedCells staged = default;
            bool hasStaged = latest != null && latest.TryCapture(field, out staged);
            return Sample(new FlowFieldSurfaceCellView(field, in staged, hasStaged, regions), worldPosition, out sample);
        }

        /// <summary>The cell of the field a world position falls in; false outside its grid.</summary>
        internal static bool TryLocate(FlowFieldField field, Vector3 worldPosition, out int index)
        {
            index = -1;
            if (field == null
                || !FlowFieldGridSpace.IsFinite(worldPosition)
                || !field.Grid.ContainsWorldPosition(worldPosition))
                return false;
            return field.Grid.SpaceMode == FlowFieldSpaceMode.Volume3D
                ? new FlowFieldVolumeCellView(field, null).TryLocate(worldPosition, out index)
                : new FlowFieldSurfaceCellView(field, default, hasLatest: false, null).TryLocate(worldPosition, out index);
        }

        private static FlowFieldSampleStatus Sample<TView>(in TView cells, Vector3 worldPosition, out FlowFieldSample sample)
            where TView : struct, IFlowFieldCellView
        {
            if (!cells.TryLocate(worldPosition, out int index))
            {
                sample = FlowFieldSample.Stopped;
                return FlowFieldSampleStatus.OutOfGrid;
            }
            FlowFieldSample published = cells.ReadCell(index);
            sample = FlowFieldSampleSafetyRule.Apply(in cells, index, worldPosition, in published, out FlowFieldSample safe)
                ? safe
                : published;
            return FlowFieldSampleStatus.Sampled;
        }
    }
}
