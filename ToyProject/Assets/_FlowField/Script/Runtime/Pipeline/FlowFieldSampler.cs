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

            // The latest cells serve only when they describe this field.
            FlowFieldStagedCells staged = default;
            bool hasStaged = latest != null && latest.TryCapture(field, out staged);
            if (field.Grid.SpaceMode == FlowFieldSpaceMode.Volume3D)
                return Sample(new FlowFieldVolumeCellView(field, in staged, hasStaged, regions), worldPosition, out sample);
            return Sample(new FlowFieldSurfaceCellView(field, in staged, hasStaged, regions), worldPosition, out sample);
        }

        // Reused by every stitched read; reads run on the main thread.
        private static readonly FlowFieldStitchedViews<FlowFieldSurfaceCellView> SurfaceStitch
            = new FlowFieldStitchedViews<FlowFieldSurfaceCellView>();
        private static readonly FlowFieldStitchedViews<FlowFieldVolumeCellView> VolumeStitch
            = new FlowFieldStitchedViews<FlowFieldVolumeCellView>();

        /// <summary>
        /// <see cref="Sample"/> of a world Goal's field, whose steps may lead
        /// into the touching areas <paramref name="stitch"/> names: the rule
        /// checks those steps against the cells they land in.
        /// </summary>
        internal static FlowFieldSampleStatus SampleStitched(
            FlowFieldField field,
            Vector3 worldPosition,
            FlowFieldSafety regions,
            IFlowFieldLatestCells latest,
            IFlowFieldStitchSource stitch,
            out FlowFieldSample sample)
        {
            sample = FlowFieldSample.Stopped;
            if (field == null)
                return FlowFieldSampleStatus.NotReady;
            if (!FlowFieldGridSpace.IsFinite(worldPosition))
                return FlowFieldSampleStatus.NonFinite;
            if (!field.Grid.ContainsWorldPosition(worldPosition))
                return FlowFieldSampleStatus.OutOfGrid;
            FlowFieldStagedCells staged = default;
            bool hasStaged = latest != null && latest.TryCapture(field, out staged);
            int first = field.Grid.CellCount;
            if (field.Grid.SpaceMode == FlowFieldSpaceMode.Volume3D)
            {
                VolumeStitch.Clear();
                for (int i = 0; i < stitch.Count; i++)
                {
                    if (!stitch.TryGet(i, out FlowFieldField other, out FlowFieldSafety otherRegions,
                            out IFlowFieldLatestCells otherLatest, out int ox, out int oy, out int oz))
                        continue;
                    FlowFieldStagedCells otherStaged = default;
                    bool otherHas = otherLatest != null && otherLatest.TryCapture(other, out otherStaged);
                    VolumeStitch.Add(new FlowFieldVolumeCellView(other, in otherStaged, otherHas, otherRegions), ox, oy, oz, first);
                    first += other.Grid.CellCount;
                }
                var volume = new FlowFieldVolumeCellView(field, in staged, hasStaged, regions);
                FlowFieldSampleStatus volumeStatus = Sample(
                    new FlowFieldStitchedCellView<FlowFieldVolumeCellView>(in volume, VolumeStitch), worldPosition, out sample);
                VolumeStitch.Clear();
                return volumeStatus;
            }
            SurfaceStitch.Clear();
            for (int i = 0; i < stitch.Count; i++)
            {
                if (!stitch.TryGet(i, out FlowFieldField other, out FlowFieldSafety otherRegions,
                        out IFlowFieldLatestCells otherLatest, out int ox, out int oy, out int oz))
                    continue;
                FlowFieldStagedCells otherStaged = default;
                bool otherHas = otherLatest != null && otherLatest.TryCapture(other, out otherStaged);
                SurfaceStitch.Add(new FlowFieldSurfaceCellView(other, in otherStaged, otherHas, otherRegions), ox, oy, oz, first);
                first += other.Grid.CellCount;
            }
            var surface = new FlowFieldSurfaceCellView(field, in staged, hasStaged, regions);
            FlowFieldSampleStatus status = Sample(
                new FlowFieldStitchedCellView<FlowFieldSurfaceCellView>(in surface, SurfaceStitch), worldPosition, out sample);
            SurfaceStitch.Clear();
            return status;
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
