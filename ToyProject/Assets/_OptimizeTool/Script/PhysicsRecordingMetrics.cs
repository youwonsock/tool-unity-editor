#if UNITY_EDITOR
using System.Diagnostics;

namespace Common.OptimizeTool
{
    /// <summary>Observed editor recording/export costs. Timings do not imply a runtime performance guarantee.</summary>
    public sealed class PhysicsRecordingMetrics
    {
        public int TransformCount { get; }
        public int ObservationCount { get; }
        public long StoredPoseCount { get; }
        public long BackupBytes { get; }
        public double HierarchyValidationMilliseconds { get; }
        public double SamplingMilliseconds { get; }
        public double CurveBuildMilliseconds { get; }
        public double PrefabBuildMilliseconds { get; }
        public double AssetSaveMilliseconds { get; }
        public double TotalExportMilliseconds { get; }

        internal PhysicsRecordingMetrics(PhysicsRecordingMeasurements source, PhysicsRecordedTake take)
        {
            TransformCount = source.TransformCount;
            ObservationCount = source.ObservationCount;
            if (take != null)
                foreach (PhysicsRecordedTrack track in take.Tracks) StoredPoseCount += track.Keys.Count;
            BackupBytes = source.BackupBytes;
            double factor = 1000d / Stopwatch.Frequency;
            HierarchyValidationMilliseconds = source.ValidationTicks * factor;
            SamplingMilliseconds = source.SamplingTicks * factor;
            CurveBuildMilliseconds = source.CurveTicks * factor;
            PrefabBuildMilliseconds = source.PrefabTicks * factor;
            AssetSaveMilliseconds = source.SaveTicks * factor;
            TotalExportMilliseconds = source.ExportTicks * factor;
        }
    }

    internal sealed class PhysicsRecordingMeasurements
    {
        public int TransformCount, ObservationCount;
        public long ValidationTicks, SamplingTicks, CurveTicks, PrefabTicks, SaveTicks, ExportTicks, BackupBytes;
        public PhysicsRecordingMetrics Snapshot(PhysicsRecordedTake take) => new(this, take);
    }
}
#endif
