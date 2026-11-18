using System.Collections.Generic;
using UnityEngine;

namespace Common.OptimizeTool.PhysicsRecording
{
    /// <summary>Turns a take into a Legacy clip whose linear keys stay within the tolerances at every observation.</summary>
    internal static class PhysicsClipBuilder
    {
        private static readonly string[] Properties =
        {
            "localPosition.x", "localPosition.y", "localPosition.z",
            "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w"
        };

        public static AnimationClip Build(PhysicsTake take, string name, float positionTolerance, float rotationTolerance)
        {
            // Keys sit on physics steps; show a whole step rate such as 50 rather than 50.000034.
            float stepRate = 1f / Time.fixedDeltaTime;
            var clip = new AnimationClip
            {
                name = name, frameRate = Mathf.Approximately(stepRate, Mathf.Round(stepRate)) ? Mathf.Round(stepRate) : stepRate,
                legacy = true, wrapMode = WrapMode.ClampForever,
            };
            try
            {
                int stillRoot = -1;
                int[] stillRootKeys = null;
                bool hasCurves = false;
                for (int track = 0; track < take.TrackCount; track++)
                {
                    int[] keys = ReduceKeys(take, track, positionTolerance, rotationTolerance);
                    // An unmoving root gets no curves, so the saved prefab can be placed anywhere.
                    if (take.Paths[track].Length == 0
                        && IsStill(take, track, keys, positionTolerance, rotationTolerance))
                    {
                        stillRoot = track;
                        stillRootKeys = keys;
                        continue;
                    }
                    SetCurves(clip, take, track, keys);
                    hasCurves = true;
                }
                // A clip without curves would have no length; keep the root so the clip spans the take.
                if (!hasCurves && stillRoot >= 0)
                    SetCurves(clip, take, stillRoot, stillRootKeys);
                return clip;
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
            Pose a = take.PoseAt(track, from), b = take.PoseAt(track, to);
            float start = take.TimeAt(from), end = take.TimeAt(to);
            int worst = -1;
            float worstExcess = 1f;
            for (int frame = from + 1; frame < to; frame++)
            {
                Pose played = PhysicsPoseMath.CurveLerp(a, b, Mathf.InverseLerp(start, end, take.TimeAt(frame)));
                Pose observed = take.PoseAt(track, frame);
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

        private static bool IsStill(PhysicsTake take, int track, int[] keys, float positionTolerance, float rotationTolerance)
        {
            if (keys.Length > 2) return false;
            Pose first = take.PoseAt(track, keys[0]), last = take.PoseAt(track, keys[keys.Length - 1]);
            return Vector3.Distance(first.position, last.position) <= positionTolerance
                && PhysicsPoseMath.RotationDifference(first.rotation, last.rotation) <= rotationTolerance;
        }

        private static void SetCurves(AnimationClip clip, PhysicsTake take, int track, int[] keys)
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
    }
}
