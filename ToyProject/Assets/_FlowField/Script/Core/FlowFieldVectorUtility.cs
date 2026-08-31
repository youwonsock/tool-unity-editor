using UnityEngine;
using Unity.Mathematics;

namespace Common.FlowField
{
    internal static class FlowFieldVectorUtility
    {
        internal const float DIRECTION_EPSILON_SQR = 0.00000001f;

        internal static void ValidateModifierOutput(
            in FlowFieldVectorState candidate,
            Vector3 surfaceNormal)
        {
            if (!FlowFieldGridSpace.IsFinite(candidate.Direction)
                || !FlowFieldGridSpace.IsFinite(candidate.SpeedMultiplier)
                || !FlowFieldGridSpace.IsFinite(surfaceNormal))
                throw new System.ArgumentOutOfRangeException(nameof(candidate));
            if (candidate.SpeedMultiplier < 0f)
                throw new System.ArgumentOutOfRangeException(nameof(candidate.SpeedMultiplier));
            Vector3 normal = ValidateSurfaceNormal(surfaceNormal);
            if (candidate.Direction.sqrMagnitude <= DIRECTION_EPSILON_SQR)
                return;

            float directionLength = candidate.Direction.magnitude;
            if (Mathf.Abs(directionLength - 1f) > 0.001f)
                throw new System.ArgumentException("Modifier direction must be normalized.", nameof(candidate));
            if (Mathf.Abs(Vector3.Dot(candidate.Direction, normal)) > 0.001f)
                throw new System.ArgumentException("Modifier direction must be tangent to the surface.", nameof(candidate));
        }

        public static Vector3 ProjectDefaultOnSurface(Vector3 direction, Vector3 surfaceNormal)
            => (Vector3)ProjectDefaultOnSurface((float3)direction, (float3)surfaceNormal);

        internal static float3 ProjectOnSurface(float3 direction, float3 surfaceNormal)
        {
            float normalLengthSq = math.lengthsq(surfaceNormal);
            if (normalLengthSq <= DIRECTION_EPSILON_SQR)
                return float3.zero;
            float3 normal = math.normalize(surfaceNormal);
            float3 projected = direction - normal * math.dot(direction, normal);
            float directionLengthSq = math.lengthsq(projected);
            return directionLengthSq > DIRECTION_EPSILON_SQR
                ? math.normalize(projected)
                : float3.zero;
        }

        internal static float3 ProjectDefaultOnSurface(float3 direction, float3 surfaceNormal)
            => ProjectOnSurface(direction, surfaceNormal);

        public static Vector3 NormalizeDefaultDirection(Vector3 direction)
        {
            if (!FlowFieldGridSpace.IsFinite(direction))
                throw new System.ArgumentOutOfRangeException(nameof(direction));
            if (direction.sqrMagnitude <= DIRECTION_EPSILON_SQR)
                throw new System.ArgumentOutOfRangeException(nameof(direction));

            return direction.normalized;
        }

        internal static Vector3 ValidateSurfaceNormal(Vector3 surfaceNormal)
        {
            if (!FlowFieldGridSpace.IsFinite(surfaceNormal)
                || surfaceNormal.sqrMagnitude <= DIRECTION_EPSILON_SQR)
                throw new System.ArgumentOutOfRangeException(nameof(surfaceNormal));

            return surfaceNormal.normalized;
        }
    }

    internal static class FlowFieldNoiseUtility
    {
        public static Vector3 ApplyStaticRotation(
            Vector3 direction,
            Vector3 cellCenter,
            Vector3 surfaceNormal,
            float maxAngleDegrees,
            float spatialFrequency,
            int seed)
        {
            if (direction.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR || maxAngleDegrees <= 0f)
                return direction;

            ResolveSeedOffsets(seed, out float seedX, out float seedZ);
            float noise = Mathf.PerlinNoise(
                cellCenter.x * spatialFrequency + seedX,
                cellCenter.z * spatialFrequency + seedZ);
            float angle = (noise * 2f - 1f) * maxAngleDegrees;
            if (surfaceNormal.sqrMagnitude <= FlowFieldVectorUtility.DIRECTION_EPSILON_SQR)
                throw new System.ArgumentOutOfRangeException(nameof(surfaceNormal));
            Vector3 axis = surfaceNormal.normalized;
            return Quaternion.AngleAxis(angle, axis) * direction;
        }

        private static void ResolveSeedOffsets(int seed, out float offsetX, out float offsetZ)
        {
            uint value = unchecked((uint)seed);
            value ^= value >> 16;
            value *= 0x7feb352d;
            value ^= value >> 15;
            value *= 0x846ca68b;
            value ^= value >> 16;

            offsetX = (value & 0xffffu) * 0.0137f + 17.17f;
            offsetZ = (value >> 16) * 0.0173f + 53.31f;
        }
    }

}
