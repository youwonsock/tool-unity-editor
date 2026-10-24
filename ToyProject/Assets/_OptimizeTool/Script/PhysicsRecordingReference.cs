using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool
{
    /// <summary>Unreduced physics observations, independent of the exported animation curves.</summary>
    public sealed class PhysicsRecordingReference : ScriptableObject
    {
        [Serializable]
        private struct Pose
        {
            public Vector3 Position;
            public Quaternion Rotation;
        }

        [SerializeField] private AnimationClip _clip;
        [SerializeField] private string[] _paths;
        [SerializeField] private float[] _times;
        // Frame-major layout: every observation has a pose for each transform path.
        [SerializeField] private Pose[] _poses;
        public AnimationClip Clip => _clip;
        public int TrackCount => _paths?.Length ?? 0;
        public int ObservationCount => _times?.Length ?? 0;
        public float Duration => ObservationCount == 0 ? 0f : _times[_times.Length - 1];
        public string Path(int track) => _paths[track];
        public float TimeAt(int observation) => _times[observation];

        public PhysicsPoseSample Sample(int track, float time)
        {
            if (track < 0 || track >= TrackCount || ObservationCount == 0)
                throw new ArgumentOutOfRangeException(nameof(track));
            FindInterval(time, out int lower, out int upper);
            return Sample(track, time, lower, upper);
        }

        internal PhysicsPoseSample Observation(int track, int observation)
        {
            Pose pose = _poses[observation * TrackCount + track];
            return new PhysicsPoseSample(_times[observation], pose.Position, pose.Rotation);
        }

        internal void SampleFrame(float time, PhysicsPoseSample[] destination)
        {
            if (destination == null || destination.Length != TrackCount)
                throw new ArgumentException("The comparison pose count changed.", nameof(destination));
            FindInterval(time, out int lower, out int upper);
            for (int track = 0; track < destination.Length; track++)
                destination[track] = Sample(track, time, lower, upper);
        }

        private void FindInterval(float time, out int lower, out int upper)
        {
            if (!IsFinite(time) || ObservationCount == 0) throw new ArgumentOutOfRangeException(nameof(time));
            int found = Array.BinarySearch(_times, time);
            upper = found >= 0 ? found : Mathf.Clamp(~found, 0, ObservationCount - 1);
            lower = found >= 0 ? found : Mathf.Max(0, upper - 1);
        }

        private PhysicsPoseSample Sample(int track, float time, int lower, int upper)
        {
            Pose a = _poses[lower * TrackCount + track];
            Pose b = _poses[upper * TrackCount + track];
            if (lower == upper) return new PhysicsPoseSample(time, a.Position, a.Rotation);
            return PhysicsPoseSample.Interpolate(
                new PhysicsPoseSample(_times[lower], a.Position, a.Rotation),
                new PhysicsPoseSample(_times[upper], b.Position, b.Rotation), time);
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
                if (!IsFinite(_times[i]) || (i == 0 ? _times[i] != 0f : _times[i] <= _times[i - 1]))
                    throw new InvalidOperationException("The recording reference observation times must start at zero and increase.");
            if (!IsFinite(expectedClip.length) || Mathf.Abs(Duration - expectedClip.length) > 0.0001f)
                throw new InvalidOperationException("The recording reference does not match the saved clip; record a new take.");
            foreach (Pose pose in _poses)
            {
                Vector3 p = pose.Position;
                Quaternion r = pose.Rotation;
                if (!IsFinite(p.x) || !IsFinite(p.y) || !IsFinite(p.z)
                    || !IsFinite(r.x) || !IsFinite(r.y) || !IsFinite(r.z) || !IsFinite(r.w)
                    || ((double)r.x * r.x + (double)r.y * r.y + (double)r.z * r.z + (double)r.w * r.w) <= 0d)
                    throw new InvalidOperationException("The recording reference contains an invalid pose.");
            }
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static string AssetPathForClip(string clipPath) =>
            System.IO.Path.ChangeExtension(clipPath, ".reference.asset");

#if UNITY_EDITOR
        internal void Initialize(AnimationClip clip, PhysicsRecordedTake take)
        {
            _clip = clip;
            _paths = new string[take.Tracks.Count];
            var observations = take.Tracks[0].Observations;
            _times = new float[observations.Count];
            _poses = new Pose[_times.Length * _paths.Length];
            for (int track = 0; track < _paths.Length; track++) _paths[track] = take.Tracks[track].Path;
            for (int frame = 0; frame < _times.Length; frame++)
            {
                _times[frame] = observations[frame].Time;
                for (int track = 0; track < _paths.Length; track++)
                {
                    PhysicsPoseSample sample = take.Tracks[track].Observations[frame];
                    _poses[frame * _paths.Length + track] = new Pose
                    { Position = sample.Position, Rotation = sample.Rotation };
                }
            }
        }
#endif
    }
}
