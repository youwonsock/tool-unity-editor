using UnityEngine;

namespace Common.OptimizeTool
{
    /// <summary>Local-pose math shared by recording, key reduction and comparison playback.</summary>
    public static class PhysicsPoseMath
    {
        /// <summary>Physical interpolation between two observations: linear position, spherical rotation.</summary>
        public static Pose Lerp(Pose from, Pose to, float t) =>
            new Pose(Vector3.Lerp(from.position, to.position, t), Quaternion.Slerp(from.rotation, to.rotation, t));

        /// <summary>
        /// What Legacy Animation plays between two linear keys: every curve component is interpolated on its own,
        /// then the rotation is normalized. Key reduction measures its error against this, not <see cref="Lerp"/>.
        /// </summary>
        public static Pose CurveLerp(Pose from, Pose to, float t)
        {
            Quaternion a = from.rotation, b = to.rotation;
            var rotation = new Quaternion(Mathf.LerpUnclamped(a.x, b.x, t), Mathf.LerpUnclamped(a.y, b.y, t),
                Mathf.LerpUnclamped(a.z, b.z, t), Mathf.LerpUnclamped(a.w, b.w, t));
            return new Pose(Vector3.LerpUnclamped(from.position, to.position, t), Quaternion.Normalize(rotation));
        }

        /// <summary>Angle in degrees, without Quaternion.Angle's small-angle dead zone and independent of sign.</summary>
        public static float RotationDifference(Quaternion a, Quaternion b)
        {
            double dot = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
            double aa = (double)a.x * a.x + (double)a.y * a.y + (double)a.z * a.z + (double)a.w * a.w;
            double bb = (double)b.x * b.x + (double)b.y * b.y + (double)b.z * b.z + (double)b.w * b.w;
            if (!(aa > 0d && bb > 0d)) return float.PositiveInfinity;
            return (float)(2d * System.Math.Acos(System.Math.Min(1d, System.Math.Abs(dot) / System.Math.Sqrt(aa * bb)))
                * (180d / System.Math.PI));
        }

        public static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        /// <summary>Finite position and a finite, non-zero rotation.</summary>
        public static bool IsValid(Pose pose)
        {
            Vector3 p = pose.position;
            Quaternion r = pose.rotation;
            return IsFinite(p.x) && IsFinite(p.y) && IsFinite(p.z)
                && IsFinite(r.x) && IsFinite(r.y) && IsFinite(r.z) && IsFinite(r.w)
                && (double)r.x * r.x + (double)r.y * r.y + (double)r.z * r.z + (double)r.w * r.w > 0d;
        }
    }
}
