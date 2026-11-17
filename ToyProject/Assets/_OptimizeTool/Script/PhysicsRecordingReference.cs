using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool
{
    /// <summary>
    /// The unreduced local poses of a take, saved next to its clip. Comparison playback reads it in Players too,
    /// so it never needs the AssetDatabase.
    /// </summary>
    public sealed class PhysicsRecordingReference : ScriptableObject
    {
        [SerializeField] private AnimationClip _clip;
        [SerializeField] private string[] _paths;
        [SerializeField] private float[] _times;
        // Frame-major: the pose of track t at observation f is _poses[f * TrackCount + t].
        // Rotations are sign-continuous per track, which may differ in sign from the captured value.
        [SerializeField] private Pose[] _poses;

        public AnimationClip Clip => _clip;
        public int TrackCount => _paths?.Length ?? 0;
        public int ObservationCount => _times?.Length ?? 0;
        public float Duration => ObservationCount == 0 ? 0f : _times[_times.Length - 1];
        public string Path(int track) => _paths[track];
        public float TimeAt(int observation) => _times[observation];
        public Pose Observation(int track, int observation) => _poses[observation * TrackCount + track];

        public static string AssetPathForClip(string clipPath) =>
            System.IO.Path.ChangeExtension(clipPath, ".reference.asset");

        /// <summary>Writes every track's pose at time, interpolated between the surrounding observations.</summary>
        public void SampleFrame(float time, Pose[] destination)
        {
            if (destination == null || destination.Length != TrackCount)
                throw new ArgumentException("The comparison pose count changed.", nameof(destination));
            if (!PhysicsPoseMath.IsFinite(time) || ObservationCount == 0) throw new ArgumentOutOfRangeException(nameof(time));
            int found = Array.BinarySearch(_times, time);
            int upper = found >= 0 ? found : Mathf.Clamp(~found, 0, ObservationCount - 1);
            int lower = found >= 0 ? found : Mathf.Max(0, upper - 1);
            float t = lower == upper ? 0f : Mathf.InverseLerp(_times[lower], _times[upper], time);
            for (int track = 0; track < destination.Length; track++)
                destination[track] = PhysicsPoseMath.Lerp(Observation(track, lower), Observation(track, upper), t);
        }

        internal void Validate(AnimationClip expectedClip)
        {
            if (_clip == null || _clip != expectedClip || TrackCount == 0 || ObservationCount == 0
                || _poses == null || _poses.LongLength != (long)TrackCount * ObservationCount)
                throw new InvalidOperationException("The recording reference is incomplete or does not match the saved clip; record a new take.");
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in _paths)
                if (path == null || !paths.Add(path))
                    throw new InvalidOperationException("The recording reference has invalid or duplicate paths.");
            if (!paths.Contains(string.Empty))
                throw new InvalidOperationException("The recording reference has no root track.");
            for (int i = 0; i < _times.Length; i++)
                if (!PhysicsPoseMath.IsFinite(_times[i]) || (i == 0 ? _times[i] != 0f : _times[i] <= _times[i - 1]))
                    throw new InvalidOperationException("The recording reference observation times must start at zero and increase.");
            if (!PhysicsPoseMath.IsFinite(expectedClip.length) || Mathf.Abs(Duration - expectedClip.length) > 0.0001f)
                throw new InvalidOperationException("The recording reference does not match the saved clip; record a new take.");
            foreach (Pose pose in _poses)
                if (!PhysicsPoseMath.IsValid(pose))
                    throw new InvalidOperationException("The recording reference contains an invalid pose.");
        }

#if UNITY_EDITOR
        internal void Initialize(AnimationClip clip, string[] paths, float[] times, Pose[] poses)
        {
            if (poses.LongLength != (long)paths.Length * times.Length)
                throw new ArgumentException("Every observation needs a pose for each track.", nameof(poses));
            _clip = clip;
            _paths = paths;
            _times = times;
            _poses = poses;
        }
#endif
    }
}
