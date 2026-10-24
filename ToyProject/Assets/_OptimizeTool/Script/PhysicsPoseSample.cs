using UnityEngine;

namespace Common.OptimizeTool
{
    /// <summary>A local-space pose at a recorded time. Shared by recording and comparison playback.</summary>
    public readonly struct PhysicsPoseSample
    {
        public float Time { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }

        public PhysicsPoseSample(float time, Vector3 position, Quaternion rotation)
        {
            Time = time;
            Position = position;
            Rotation = rotation;
        }

        public static PhysicsPoseSample Interpolate(PhysicsPoseSample from, PhysicsPoseSample to, float time)
        {
            float alpha = Mathf.InverseLerp(from.Time, to.Time, time);
            return new PhysicsPoseSample(time, Vector3.Lerp(from.Position, to.Position, alpha),
                Quaternion.Slerp(from.Rotation, to.Rotation, alpha));
        }

        /// <summary>Normalized angular distance without Quaternion.Angle's small-angle dead zone.</summary>
        public static float RotationDifference(Quaternion a, Quaternion b)
        {
            double dot = (double)a.x * b.x + (double)a.y * b.y + (double)a.z * b.z + (double)a.w * b.w;
            double aa = (double)a.x * a.x + (double)a.y * a.y + (double)a.z * a.z + (double)a.w * a.w;
            double bb = (double)b.x * b.x + (double)b.y * b.y + (double)b.z * b.z + (double)b.w * b.w;
            if (!(aa > 0d && bb > 0d)) return float.PositiveInfinity;
            return (float)(2d * System.Math.Acos(System.Math.Min(1d, System.Math.Abs(dot) / System.Math.Sqrt(aa * bb)))
                * (180d / System.Math.PI));
        }
    }
}
