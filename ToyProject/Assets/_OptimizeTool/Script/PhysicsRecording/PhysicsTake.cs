using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>
    /// Every observed local pose of one take, in the frame-major layout the reference asset saves.
    /// It owns no scene objects; callers pass the tracked Transforms for each capture.
    /// </summary>
    internal sealed class PhysicsTake
    {
        private readonly string[] _paths;
        private readonly List<float> _times = new();
        private readonly List<Pose> _poses = new();

        /// <summary>Starts the take with the pose at time zero.</summary>
        public PhysicsTake(string[] paths, Transform[] tracks)
        {
            _paths = paths;
            AddFrame(0f);
            Store(0, tracks);
        }

        public IReadOnlyList<string> Paths => _paths;
        public int TrackCount => _paths.Length;
        public int FrameCount => _times.Count;
        public float Duration => _times[_times.Count - 1];
        public float TimeAt(int frame) => _times[frame];
        public Pose PoseAt(int track, int frame) => _poses[frame * TrackCount + track];

        /// <summary>
        /// Appends a frame. A capture at the latest time replaces that frame: a take started inside FixedUpdate
        /// first sees the pose before the step's simulation, then the simulated pose for the same time.
        /// An earlier time is ignored.
        /// </summary>
        public void Capture(float time, Transform[] tracks)
        {
            if (time > Duration) AddFrame(time);
            else if (time < Duration) return;
            Store(FrameCount - 1, tracks);
        }

        /// <summary>
        /// Ends the take exactly at the requested duration. A last step past it is interpolated, unless the step
        /// before is within <paramref name="snapTolerance"/> of it: summed fixed steps often land a hair short.
        /// </summary>
        public void Finish(float duration, float snapTolerance)
        {
            int last = FrameCount - 1;
            if (last == 0 || _times[last] <= duration) return;
            // The first frame stays at time zero, so only a later frame is snapped.
            if (last > 1 && duration - _times[last - 1] <= snapTolerance)
            {
                _times.RemoveAt(last);
                _poses.RemoveRange(last * TrackCount, TrackCount);
                _times[last - 1] = duration;
                return;
            }
            float t = Mathf.InverseLerp(_times[last - 1], _times[last], duration);
            for (int track = 0; track < TrackCount; track++)
                _poses[last * TrackCount + track] = PhysicsPoseMath.Lerp(PoseAt(track, last - 1), PoseAt(track, last), t);
            _times[last] = duration;
        }

        public void WriteTo(PhysicsRecordingReference reference, AnimationClip clip) =>
            reference.Initialize(clip, (string[])_paths.Clone(), _times.ToArray(), _poses.ToArray());

        private void AddFrame(float time)
        {
            _times.Add(time);
            for (int track = 0; track < TrackCount; track++) _poses.Add(default);
        }

        private void Store(int frame, Transform[] tracks)
        {
            for (int track = 0; track < TrackCount; track++)
            {
                Transform tf = tracks[track];
                Quaternion rotation = tf.localRotation;
                // Keep each track's rotation on one hemisphere so linear curves never take the long way round.
                if (frame > 0 && Quaternion.Dot(PoseAt(track, frame - 1).rotation, rotation) < 0f)
                    rotation = new Quaternion(-rotation.x, -rotation.y, -rotation.z, -rotation.w);
                _poses[frame * TrackCount + track] = new Pose(tf.localPosition, rotation);
            }
        }
    }
}
