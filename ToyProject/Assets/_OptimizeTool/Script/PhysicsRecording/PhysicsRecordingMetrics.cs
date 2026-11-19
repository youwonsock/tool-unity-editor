namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>What one saved take holds, and how closely its clip plays the recorded physics steps.</summary>
    public sealed class PhysicsRecordingMetrics
    {
        /// <summary>Recorded physics steps, the observations the clip was reduced from.</summary>
        public int FrameCount { get; }
        /// <summary>Recorded Transforms, including those added during the take.</summary>
        public int TrackCount { get; }
        /// <summary>Pose keys written to the clip, summed over the animated tracks.</summary>
        public int KeyCount { get; }
        /// <summary>Tracks saved without curves because they never moved beyond the tolerances.</summary>
        public int OmittedTrackCount { get; }
        /// <summary>Largest local position error, in meters, of the saved clip at any recorded step.</summary>
        public float MaxPositionError { get; }
        /// <summary>Largest local rotation error, in degrees, of the saved clip at any recorded step.</summary>
        public float MaxRotationError { get; }

        internal PhysicsRecordingMetrics(int frameCount, int trackCount, int keyCount, int omittedTrackCount,
            float maxPositionError, float maxRotationError)
        {
            FrameCount = frameCount;
            TrackCount = trackCount;
            KeyCount = keyCount;
            OmittedTrackCount = omittedTrackCount;
            MaxPositionError = maxPositionError;
            MaxRotationError = maxRotationError;
        }
    }
}
