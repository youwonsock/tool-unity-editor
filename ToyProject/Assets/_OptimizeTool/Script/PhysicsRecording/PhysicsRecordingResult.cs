using System;
using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording
{
    public enum PhysicsRecordingStopReason { DurationReached, Stopped, Disabled, Failed }

    /// <summary>The outcome of one take: its saved assets, or the error that prevented saving them.</summary>
    public sealed class PhysicsRecordingResult
    {
        public float Duration { get; }
        public PhysicsRecordingStopReason StopReason { get; }
        public AnimationClip Clip { get; }
        public GameObject Prefab { get; }
        /// <summary>The unreduced observations; null unless the recorder saves references.</summary>
        public PhysicsRecordingReference Reference { get; }
        public Exception Error { get; }
        public bool Succeeded => Error == null;

        private PhysicsRecordingResult(float duration, PhysicsRecordingStopReason reason, AnimationClip clip,
            GameObject prefab, PhysicsRecordingReference reference, Exception error = null)
        {
            Duration = duration;
            StopReason = reason;
            Clip = clip;
            Prefab = prefab;
            Reference = reference;
            Error = error;
        }

        internal static PhysicsRecordingResult Saved(float duration, PhysicsRecordingStopReason reason, in PhysicsRecordingAssets assets) =>
            new(duration, reason, assets.Clip, assets.Prefab, assets.Reference);

        internal static PhysicsRecordingResult Failed(float duration, PhysicsRecordingStopReason reason, Exception error) =>
            new(duration, reason, null, null, null, error);
    }
}
