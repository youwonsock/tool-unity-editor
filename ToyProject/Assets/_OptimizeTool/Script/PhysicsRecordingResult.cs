#if UNITY_EDITOR
using System;
using UnityEngine;

namespace Common.OptimizeTool
{
    public enum PhysicsRecordingStopReason { DurationReached, Stopped, Disabled, Released, Faulted }

    /// <summary>The immutable outcome published after the clip, prefab and reference are saved, or saving fails.</summary>
    public sealed class PhysicsRecordingResult
    {
        public float Duration { get; }
        public PhysicsRecordingStopReason StopReason { get; }
        public AnimationClip Clip { get; }
        public GameObject Prefab { get; }
        public Exception Error { get; }
        public PhysicsRecordingMetrics Metrics { get; }
        public PhysicsRecordingReference Reference { get; }
        public bool Succeeded => Error == null;

        internal PhysicsRecordingResult(float duration, PhysicsRecordingStopReason reason,
            AnimationClip clip, GameObject prefab, Exception error = null, PhysicsRecordingMetrics metrics = null,
            PhysicsRecordingReference reference = null)
        {
            Duration = duration;
            StopReason = reason;
            Clip = clip;
            Prefab = prefab;
            Error = error;
            Metrics = metrics;
            Reference = reference;
        }
    }
}
#endif
