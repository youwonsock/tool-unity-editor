#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool
{
    internal sealed class PhysicsRecordedTrack
    {
        public string Path { get; }
        public IReadOnlyList<PhysicsPoseSample> Keys { get; }
        public IReadOnlyList<PhysicsPoseSample> Observations { get; }
        public PhysicsRecordedTrack(string path, List<PhysicsPoseSample> keys, List<PhysicsPoseSample> observations)
        {
            Path = path;
            Keys = keys.AsReadOnly();
            Observations = observations.AsReadOnly();
        }
    }

    internal sealed class PhysicsRecordedTake
    {
        public float Duration { get; }
        public IReadOnlyList<PhysicsRecordedTrack> Tracks { get; }
        public PhysicsRecordedTake(float duration, PhysicsRecordedTrack[] tracks)
        {
            Duration = duration;
            Tracks = Array.AsReadOnly(tracks);
        }
    }

    /// <summary>Deterministic sampling over supplied local poses; owns no scene objects or editor assets.</summary>
    internal sealed class PhysicsRecordingSession
    {
        private sealed class Track
        {
            public readonly string Path;
            public readonly List<PhysicsPoseSample> Keys = new();
            public readonly List<PhysicsPoseSample> Observations = new();
            public PhysicsPoseSample Observed, Pending;
            public bool HasPending;

            public Track(string path, PhysicsPoseSample initial)
            {
                Path = path;
                Observed = initial;
                Keys.Add(initial);
                Observations.Add(initial);
            }

            public void Observe(PhysicsPoseSample sample)
            {
                int last = Observations.Count - 1;
                if (sample.Time == Observations[last].Time) Observations[last] = sample;
                else if (sample.Time > Observations[last].Time) Observations.Add(sample);
            }

            public void Record(PhysicsPoseSample sample, PhysicsRecordingSession session, bool force)
            {
                PhysicsPoseSample last = Keys[Keys.Count - 1];
                if (Quaternion.Dot(last.Rotation, sample.Rotation) < 0f)
                {
                    Quaternion r = sample.Rotation;
                    sample = new PhysicsPoseSample(sample.Time, sample.Position, new Quaternion(-r.x, -r.y, -r.z, -r.w));
                }
                bool moved = Vector3.Distance(last.Position, sample.Position) > session._positionThreshold
                    || PhysicsPoseSample.RotationDifference(last.Rotation, sample.Rotation) > session._rotationThreshold;
                if (force || moved || !session._onlyIfMoved)
                {
                    if (HasPending && moved) AddKey(Pending);
                    AddKey(sample);
                    HasPending = false;
                }
                else
                {
                    Pending = sample;
                    HasPending = true;
                }
            }

            private void AddKey(PhysicsPoseSample sample)
            {
                int last = Keys.Count - 1;
                if (sample.Time == Keys[last].Time) Keys[last] = sample;
                else if (sample.Time > Keys[last].Time) Keys.Add(sample);
            }
        }

        private readonly Track[] _tracks;
        private readonly float _duration, _interval, _positionThreshold, _rotationThreshold;
        private readonly bool _onlyIfMoved;
        private double _nextRecordTime;
        public float Elapsed { get; private set; }
        public PhysicsRecordedTake Take { get; private set; }

        public PhysicsRecordingSession(IReadOnlyList<string> paths, IReadOnlyList<PhysicsPoseSample> initial,
            float duration, float interval, bool onlyIfMoved, float positionThreshold, float rotationThreshold)
        {
            if (paths.Count == 0 || paths.Count != initial.Count) throw new ArgumentException("Pose and path counts must match.");
            _duration = duration;
            _interval = interval;
            _onlyIfMoved = onlyIfMoved;
            _positionThreshold = positionThreshold;
            _rotationThreshold = rotationThreshold;
            _nextRecordTime = interval;
            _tracks = new Track[paths.Count];
            for (int i = 0; i < paths.Count; i++) _tracks[i] = new Track(paths[i], initial[i]);
        }

        public bool Observe(float elapsed, IReadOnlyList<PhysicsPoseSample> poses)
        {
            RequireOpen(poses);
            if (elapsed <= Elapsed) return false;
            bool finished = elapsed >= _duration;
            float sampleTime = Mathf.Min(elapsed, _duration);
            bool record = finished || elapsed + 0.0000001d >= _nextRecordTime;
            for (int i = 0; i < _tracks.Length; i++)
            {
                Track track = _tracks[i];
                PhysicsPoseSample observed = poses[i];
                PhysicsPoseSample sample = finished && elapsed > _duration
                    ? PhysicsPoseSample.Interpolate(track.Observed, observed, sampleTime)
                    : new PhysicsPoseSample(sampleTime, observed.Position, observed.Rotation);
                track.Observe(sample);
                if (record)
                {
                    track.Record(sample, this, finished);
                }
                track.Observed = observed;
            }
            Elapsed = sampleTime;
            if (record) _nextRecordTime = (Math.Floor((elapsed + 0.0000001d) / _interval) + 1d) * _interval;
            if (finished) Complete();
            return finished;
        }

        public void Stop(float elapsed, IReadOnlyList<PhysicsPoseSample> poses)
        {
            RequireOpen(poses);
            Elapsed = Mathf.Clamp(elapsed, 0f, _duration);
            for (int i = 0; i < _tracks.Length; i++)
            {
                var sample = new PhysicsPoseSample(Elapsed, poses[i].Position, poses[i].Rotation);
                _tracks[i].Observe(sample);
                _tracks[i].Record(sample, this, true);
            }
            Complete();
        }

        private void RequireOpen(IReadOnlyList<PhysicsPoseSample> poses)
        {
            if (Take != null) throw new InvalidOperationException("The recording take is already complete.");
            if (poses.Count != _tracks.Length) throw new ArgumentException("The pose count changed.");
        }

        private void Complete()
        {
            var tracks = new PhysicsRecordedTrack[_tracks.Length];
            for (int i = 0; i < tracks.Length; i++)
                tracks[i] = new PhysicsRecordedTrack(_tracks[i].Path, _tracks[i].Keys, _tracks[i].Observations);
            Take = new PhysicsRecordedTake(Elapsed, tracks);
        }
    }
}
#endif
