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
        /// <summary>The physics-free prefab that plays <see cref="Clip"/>; null for a Generic clip.</summary>
        public GameObject Prefab { get; }
        /// <summary>The unreduced observations; null unless the recorder saves references.</summary>
        public PhysicsRecordingReference Reference { get; }
        /// <summary>What the saved take holds and how closely its clip plays it; null when saving failed.</summary>
        public PhysicsRecordingMetrics Metrics { get; }
        public Exception Error { get; }
        public bool Succeeded => Error == null;

        private PhysicsRecordingResult(float duration, PhysicsRecordingStopReason reason, AnimationClip clip,
            GameObject prefab, PhysicsRecordingReference reference, PhysicsRecordingMetrics metrics, Exception error)
        {
            Duration = duration;
            StopReason = reason;
            Clip = clip;
            Prefab = prefab;
            Reference = reference;
            Metrics = metrics;
            Error = error;
        }

        internal static PhysicsRecordingResult Saved(float duration, PhysicsRecordingStopReason reason, in PhysicsRecordingAssets assets) =>
            new(duration, reason, assets.Clip, assets.Prefab, assets.Reference, assets.Metrics, null);

        internal static PhysicsRecordingResult Failed(float duration, PhysicsRecordingStopReason reason, Exception error) =>
            new(duration, reason, null, null, null, null, error);
    }
}
