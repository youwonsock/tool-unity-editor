using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording.Editor
{
    /// <summary>A built clip, the tracks it leaves without curves, and the metrics of the take.</summary>
    internal sealed class PhysicsClip
    {
        public AnimationClip Clip;
        /// <summary>For each track, whether it has no curves; the saved copy must hold its first pose.</summary>
        public bool[] Omitted;
        public PhysicsRecordingMetrics Metrics;
    }

    /// <summary>Turns a take into a clip whose linear keys stay within the tolerances at every observation.</summary>
    internal static class PhysicsClipBuilder
    {
        private static readonly string[] Properties =
        {
            "localPosition.x", "localPosition.y", "localPosition.z",
            "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w"
        };

        public static PhysicsClip Build(PhysicsTake take, in PhysicsTakeSettings settings)
        {
            // Keys sit on physics steps; show a whole step rate such as 50 rather than 50.000034.
            float stepRate = 1f / Time.fixedDeltaTime;
            var clip = new AnimationClip
            {
                name = settings.ClipName,
                frameRate = Mathf.Approximately(stepRate, Mathf.Round(stepRate)) ? Mathf.Round(stepRate) : stepRate,
                legacy = settings.ClipFormat == PhysicsClipFormat.LegacyPrefab, wrapMode = WrapMode.ClampForever,
            };
            try
            {
                float positionTolerance = settings.PositionTolerance, rotationTolerance = settings.RotationTolerance;
                var omitted = new bool[take.TrackCount];
                var error = new Error();
                int root = -1, keyCount = 0;
                bool hasCurves = false;
                for (int track = 0; track < take.TrackCount; track++)
                {
                    bool isRoot = take.Paths[track].Length == 0;
                    if (isRoot) root = track;
                    // An unmoving root gets no curves, so the saved prefab can be placed anywhere.
                    if ((isRoot || settings.OmitStillTracks) && IsStill(take, track, positionTolerance, rotationTolerance))
                    {
                        omitted[track] = true;
                        continue;
                    }
                    keyCount += SetCurves(clip, take, track, ReduceKeys(take, track, positionTolerance, rotationTolerance), error);
                    hasCurves = true;
                }
                // A clip without Transform curves would not span the take; keep the root so its length matches.
                if (!hasCurves && root >= 0)
                {
                    omitted[root] = false;
                    keyCount += SetCurves(clip, take, root, ReduceKeys(take, root, positionTolerance, rotationTolerance), error);
                }
                int omittedCount = 0;
                for (int track = 0; track < take.TrackCount; track++)
                {
                    if (omitted[track])
                    {
                        omittedCount++;
                        error.MeasureStill(take, track);
                    }
                    if (take.VisibleFrom(track) > 0f) SetVisibility(clip, take, track);
                }
                return new PhysicsClip
                {
                    Clip = clip, Omitted = omitted,
                    Metrics = new PhysicsRecordingMetrics(take.FrameCount, take.TrackCount, keyCount, omittedCount,
                        error.Position, error.Rotation),
                };
            }
            catch
            {
                Object.DestroyImmediate(clip);
                throw;
            }
        }

        /// <summary>
        /// Keeps the first and last frame, then splits each span at its worst frame until linear playback
        /// of every frame in between is within both tolerances. Zero tolerances keep every frame.
        /// </summary>
        private static int[] ReduceKeys(PhysicsTake take, int track, float positionTolerance, float rotationTolerance)
        {
            int last = take.FrameCount - 1;
            var keep = new bool[last + 1];
            keep[0] = keep[last] = true;
            if (positionTolerance <= 0f && rotationTolerance <= 0f)
                for (int frame = 0; frame <= last; frame++) keep[frame] = true;
            else
            {
                var spans = new Stack<(int From, int To)>();
                spans.Push((0, last));
                while (spans.Count > 0)
                {
                    (int from, int to) = spans.Pop();
                    int worst = WorstFrame(take, track, from, to, positionTolerance, rotationTolerance);
                    if (worst < 0) continue;
                    keep[worst] = true;
                    spans.Push((from, worst));
                    spans.Push((worst, to));
                }
            }
            var keys = new List<int>();
            for (int frame = 0; frame <= last; frame++)
                if (keep[frame]) keys.Add(frame);
            return keys.ToArray();
        }

        /// <summary>The frame whose playback error most exceeds a tolerance, or -1 when the span is within both.</summary>
        private static int WorstFrame(PhysicsTake take, int track, int from, int to, float positionTolerance, float rotationTolerance)
        {
            int worst = -1;
            float worstExcess = 1f;
            for (int frame = from + 1; frame < to; frame++)
            {
                Pose played = Played(take, track, from, to, frame), observed = take.PoseAt(track, frame);
                float excess = Mathf.Max(
                    Excess(Vector3.Distance(played.position, observed.position), positionTolerance),
                    Excess(PhysicsPoseMath.RotationDifference(played.rotation, observed.rotation), rotationTolerance));
                if (excess > worstExcess)
                {
                    worstExcess = excess;
                    worst = frame;
                }
            }
            return worst;
        }

        private static float Excess(float error, float tolerance) =>
            tolerance > 0f ? error / tolerance : error > 0f ? float.PositiveInfinity : 0f;

        /// <summary>What playback shows at <paramref name="frame"/> between the keys at frames <paramref name="from"/> and <paramref name="to"/>.</summary>
        private static Pose Played(PhysicsTake take, int track, int from, int to, int frame) =>
            PhysicsPoseMath.CurveLerp(take.PoseAt(track, from), take.PoseAt(track, to),
                Mathf.InverseLerp(take.TimeAt(from), take.TimeAt(to), take.TimeAt(frame)));

        /// <summary>Every frame is within the tolerances of the first one, which is the pose the saved copy holds.</summary>
        private static bool IsStill(PhysicsTake take, int track, float positionTolerance, float rotationTolerance)
        {
            Pose first = take.PoseAt(track, 0);
            for (int frame = 1; frame < take.FrameCount; frame++)
            {
                Pose pose = take.PoseAt(track, frame);
                // Exact equality first, so zero tolerances accept a track that never moved at all.
                if (pose.position.Equals(first.position) && pose.rotation.Equals(first.rotation)) continue;
                if (Vector3.Distance(first.position, pose.position) > positionTolerance
                    || PhysicsPoseMath.RotationDifference(first.rotation, pose.rotation) > rotationTolerance)
                    return false;
            }
            return true;
        }

        /// <summary>Writes the track's curves and returns its key count.</summary>
        private static int SetCurves(AnimationClip clip, PhysicsTake take, int track, int[] keys, Error error)
        {
            var values = new float[Properties.Length][];
            for (int c = 0; c < values.Length; c++) values[c] = new float[keys.Length];
            for (int k = 0; k < keys.Length; k++)
            {
                Pose pose = take.PoseAt(track, keys[k]);
                values[0][k] = pose.position.x; values[1][k] = pose.position.y; values[2][k] = pose.position.z;
                values[3][k] = pose.rotation.x; values[4][k] = pose.rotation.y;
                values[5][k] = pose.rotation.z; values[6][k] = pose.rotation.w;
            }
            for (int c = 0; c < Properties.Length; c++)
                clip.SetCurve(take.Paths[track], typeof(Transform), Properties[c], LinearCurve(take, keys, values[c]));
            for (int k = 1; k < keys.Length; k++)
                for (int frame = keys[k - 1] + 1; frame < keys[k]; frame++)
                    error.Add(Played(take, track, keys[k - 1], keys[k], frame), take.PoseAt(track, frame));
            return keys.Length;
        }

        /// <summary>Each key's tangents are the slopes to its neighbours, so every span plays linearly.</summary>
        private static AnimationCurve LinearCurve(PhysicsTake take, int[] keys, float[] values)
        {
            int last = keys.Length - 1;
            var frames = new Keyframe[keys.Length];
            for (int k = 0; k <= last; k++)
            {
                float outSlope = k < last ? Slope(take, keys, values, k) : k > 0 ? Slope(take, keys, values, k - 1) : 0f;
                float inSlope = k > 0 ? Slope(take, keys, values, k - 1) : outSlope;
                frames[k] = new Keyframe(take.TimeAt(keys[k]), values[k], inSlope, outSlope);
            }
            return new AnimationCurve(frames);
        }

        private static float Slope(PhysicsTake take, int[] keys, float[] values, int k) =>
            (values[k + 1] - values[k]) / (take.TimeAt(keys[k + 1]) - take.TimeAt(keys[k]));

        /// <summary>
        /// Hides a track with a stepped GameObject active curve: until the time it appeared, or for the whole take when
        /// it was inactive at the start.
        /// </summary>
        private static void SetVisibility(AnimationClip clip, PhysicsTake take, int track)
        {
            float visibleFrom = take.VisibleFrom(track), end = take.Duration;
            AnimationCurve curve;
            if (float.IsPositiveInfinity(visibleFrom))
                curve = end > 0f ? new AnimationCurve(Step(0f, 0f), Step(end, 0f)) : new AnimationCurve(Step(0f, 0f));
            else curve = new AnimationCurve(Step(0f, 0f), Step(Mathf.Min(visibleFrom, end), 1f));
            clip.SetCurve(take.Paths[track], typeof(GameObject), "m_IsActive", curve);
        }

        private static Keyframe Step(float time, float value) =>
            new Keyframe(time, value, float.PositiveInfinity, float.PositiveInfinity);

        /// <summary>The largest difference between what the clip plays and what was recorded, over every frame.</summary>
        private sealed class Error
        {
            public float Position, Rotation;

            public void Add(Pose played, Pose observed)
            {
                Position = Mathf.Max(Position, Vector3.Distance(played.position, observed.position));
                Rotation = Mathf.Max(Rotation, PhysicsPoseMath.RotationDifference(played.rotation, observed.rotation));
            }

            /// <summary>A track without curves plays its first pose at every frame.</summary>
            public void MeasureStill(PhysicsTake take, int track)
            {
                for (int frame = 1; frame < take.FrameCount; frame++) Add(take.PoseAt(track, 0), take.PoseAt(track, frame));
            }
        }
    }
}
