using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>
    /// Every recorded local pose of one take, and when each track's GameObject was turned on or off. It owns no scene
    /// objects; callers pass the tracked Transforms, in track order, for each step. Time zero is the first recorded step.
    /// </summary>
    internal sealed class PhysicsTake
    {
        /// <summary>The physics step at which a track was first seen with a new active state.</summary>
        public readonly struct ActiveChange
        {
            public readonly float Time;
            public readonly bool Active;

            public ActiveChange(float time, bool active)
            {
                Time = time;
                Active = active;
            }
        }

        private readonly List<string> _paths = new();
        private readonly List<bool> _startsActive = new();
        private readonly List<bool> _lastActive = new();
        private readonly List<List<ActiveChange>> _activeChanges = new();
        private readonly List<List<Pose>> _poses = new();
        private readonly List<float> _times = new();
        private readonly List<Pose> _held = new();
        private float _heldTime;
        private bool _hasHeld;

        /// <summary>A take of these tracks; their start active states come from the first step's observation.</summary>
        public PhysicsTake(IReadOnlyList<string> paths)
        {
            foreach (string path in paths) AddTrackLists(path, true, new List<Pose>(), default);
        }

        public IReadOnlyList<string> Paths => _paths;
        public int TrackCount => _paths.Count;
        public int FrameCount => _times.Count;
        /// <summary>The time of the last recorded step; 0 before the first.</summary>
        public float Duration => _times.Count == 0 ? 0f : _times[_times.Count - 1];
        public float TimeAt(int frame) => _times[frame];
        public Pose PoseAt(int track, int frame) => _poses[track][frame];

        /// <summary>
        /// The track's GameObject active state at time zero. A track added during the take starts inactive and turns on
        /// at the step it appeared. The root's own state is not recorded, so it always starts active.
        /// </summary>
        public bool StartsActive(int track) => _startsActive[track];

        /// <summary>The steps, in time order, at which the track's GameObject was turned on or off.</summary>
        public IReadOnlyList<ActiveChange> ActiveChanges(int track) => _activeChanges[track];

        /// <summary>Whether a step at <paramref name="time"/> is recorded; the first step always is.</summary>
        public bool IsDue(float time, float interval, float tolerance) =>
            _times.Count == 0 || time - Duration >= interval - tolerance;

        /// <summary>
        /// Adds a track that appeared at <paramref name="time"/>. Earlier frames, and a held step, get its current pose,
        /// so every frame keeps one pose per track. Unless it appeared at the first step, it is hidden until then.
        /// </summary>
        public void AddTrack(string path, Transform tf, float time)
        {
            var pose = new Pose(tf.localPosition, tf.localRotation);
            var poses = new List<Pose>(_times.Count + 1);
            for (int frame = 0; frame < _times.Count; frame++) poses.Add(pose);
            bool first = _times.Count == 0;
            AddTrackLists(path, first, poses, pose);
            if (!first) SetActive(_paths.Count - 1, time, tf.gameObject.activeSelf);
        }

        /// <summary>
        /// Notes every track whose GameObject was turned on or off since the last step; call it once per physics step,
        /// before <see cref="Capture"/> or <see cref="Hold"/>. The first step sets the start states. Steps that are not
        /// recorded as frames still count, so a change keeps the time of the step that saw it.
        /// </summary>
        public void ObserveActive(float time, IReadOnlyList<Transform> tracks)
        {
            bool first = _times.Count == 0;
            for (int track = 0; track < TrackCount; track++)
            {
                // The root's Animation would stop with its own GameObject, so the root's state is not recorded.
                if (_paths[track].Length == 0) continue;
                bool active = tracks[track].gameObject.activeSelf;
                if (first) _startsActive[track] = _lastActive[track] = active;
                else SetActive(track, time, active);
            }
        }

        /// <summary>Records the step at <paramref name="time"/>, after every frame so far.</summary>
        public void Capture(float time, IReadOnlyList<Transform> tracks)
        {
            _times.Add(time);
            for (int track = 0; track < TrackCount; track++)
                _poses[track].Add(Continuous(track, tracks[track].localPosition, tracks[track].localRotation));
            _hasHeld = false;
        }

        /// <summary>Keeps the step at <paramref name="time"/> without recording it, until a later step replaces it.</summary>
        public void Hold(float time, IReadOnlyList<Transform> tracks)
        {
            _heldTime = time;
            for (int track = 0; track < TrackCount; track++)
                _held[track] = Continuous(track, tracks[track].localPosition, tracks[track].localRotation);
            _hasHeld = true;
        }

        /// <summary>Records the held step, the last one before a stop or the end, so the take ends on its true pose.</summary>
        public void CommitHeld()
        {
            if (!_hasHeld) return;
            _times.Add(_heldTime);
            for (int track = 0; track < TrackCount; track++) _poses[track].Add(_held[track]);
            _hasHeld = false;
        }

        /// <summary>
        /// Ends the take exactly at the requested duration. A last step past it is interpolated, unless the step
        /// before is within <paramref name="snapTolerance"/> of it: summed fixed steps often land a hair short.
        /// </summary>
        public void Finish(float duration, float snapTolerance)
        {
            int last = _times.Count - 1;
            if (last <= 0 || _times[last] <= duration) return;
            // A change seen at the step past the end happened after the end.
            foreach (List<ActiveChange> changes in _activeChanges) changes.RemoveAll(change => change.Time > duration);
            // The first frame stays at time zero, so only a later frame is snapped.
            if (last > 1 && duration - _times[last - 1] <= snapTolerance)
            {
                _times.RemoveAt(last);
                foreach (List<Pose> poses in _poses) poses.RemoveAt(last);
                _times[last - 1] = duration;
                return;
            }
            float t = Mathf.InverseLerp(_times[last - 1], _times[last], duration);
            foreach (List<Pose> poses in _poses) poses[last] = PhysicsPoseMath.Lerp(poses[last - 1], poses[last], t);
            _times[last] = duration;
        }

        /// <summary>Writes the frame-major layout the reference saves.</summary>
        public void WriteTo(PhysicsRecordingReference reference, AnimationClip clip)
        {
            var poses = new Pose[_times.Count * TrackCount];
            for (int track = 0; track < TrackCount; track++)
                for (int frame = 0; frame < _times.Count; frame++)
                    poses[frame * TrackCount + track] = _poses[track][frame];
            reference.Initialize(clip, _paths.ToArray(), _times.ToArray(), poses);
        }

        private void AddTrackLists(string path, bool startsActive, List<Pose> poses, Pose held)
        {
            _paths.Add(path);
            _startsActive.Add(startsActive);
            _lastActive.Add(startsActive);
            _activeChanges.Add(new List<ActiveChange>());
            _poses.Add(poses);
            _held.Add(held);
        }

        private void SetActive(int track, float time, bool active)
        {
            if (active == _lastActive[track]) return;
            _lastActive[track] = active;
            _activeChanges[track].Add(new ActiveChange(time, active));
        }

        /// <summary>
        /// The pose with its rotation on the hemisphere of the track's last recorded rotation, so linear curves never
        /// take the long way round.
        /// </summary>
        private Pose Continuous(int track, Vector3 position, Quaternion rotation)
        {
            List<Pose> poses = _poses[track];
            if (poses.Count > 0 && Quaternion.Dot(poses[poses.Count - 1].rotation, rotation) < 0f)
                rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
            return new Pose(position, rotation);
        }
    }
}
