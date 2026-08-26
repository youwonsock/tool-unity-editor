using UnityEngine;
using Unity.Mathematics;

namespace Supercent.Common.FlowField
{
    internal static class FlowFieldVectorUtility
    {
        internal const float DIRECTION_EPSILON_SQR = 0.00000001f;

        public static bool TrySanitize(FlowFieldVectorState candidate, out FlowFieldVectorState sanitized)
        {
            sanitized = default;
            if (!FlowFieldGridSpace.IsFinite(candidate.Direction)
                || !FlowFieldGridSpace.IsFinite(candidate.SpeedMultiplier))
                return false;

            Vector3 direction = candidate.Direction;
            direction = direction.sqrMagnitude > DIRECTION_EPSILON_SQR
                ? direction.normalized
                : Vector3.zero;

            sanitized = new FlowFieldVectorState(direction, Mathf.Max(0f, candidate.SpeedMultiplier));
            return true;
        }

        public static bool TrySanitizeOnSurface(
            FlowFieldVectorState candidate,
            Vector3 surfaceNormal,
            out FlowFieldVectorState sanitized)
        {
            sanitized = default;
            if (!FlowFieldGridSpace.IsFinite(candidate.Direction)
                || !FlowFieldGridSpace.IsFinite(candidate.SpeedMultiplier)
                || !FlowFieldGridSpace.IsFinite(surfaceNormal))
                return false;

            Vector3 direction = (Vector3)ProjectOnSurface(
                (float3)candidate.Direction,
                (float3)surfaceNormal);
            sanitized = new FlowFieldVectorState(direction, Mathf.Max(0f, candidate.SpeedMultiplier));
            return true;
        }

        public static Vector3 ProjectDefaultOnSurface(Vector3 direction, Vector3 surfaceNormal)
            => (Vector3)ProjectDefaultOnSurface((float3)direction, (float3)surfaceNormal);

        internal static float3 ProjectOnSurface(float3 direction, float3 surfaceNormal)
        {
            float normalLengthSq = math.lengthsq(surfaceNormal);
            float3 normal = normalLengthSq > DIRECTION_EPSILON_SQR
                ? math.normalize(surfaceNormal)
                : new float3(0f, 1f, 0f);
            float3 projected = direction - normal * math.dot(direction, normal);
            float directionLengthSq = math.lengthsq(projected);
            return directionLengthSq > DIRECTION_EPSILON_SQR
                ? math.normalize(projected)
                : float3.zero;
        }

        internal static float3 ProjectDefaultOnSurface(float3 direction, float3 surfaceNormal)
        {
            float3 projected = ProjectOnSurface(direction, surfaceNormal);
            if (math.lengthsq(projected) > DIRECTION_EPSILON_SQR)
                return projected;

            projected = ProjectOnSurface(new float3(0f, 0f, 1f), surfaceNormal);
            if (math.lengthsq(projected) > DIRECTION_EPSILON_SQR)
                return projected;

            return ProjectOnSurface(new float3(1f, 0f, 0f), surfaceNormal);
        }

        public static Vector3 SanitizeDefaultDirection(Vector3 direction)
        {
            if (!FlowFieldGridSpace.IsFinite(direction))
                return Vector3.forward;

            return direction.sqrMagnitude > DIRECTION_EPSILON_SQR
                ? direction.normalized
                : Vector3.forward;
        }

        internal static Vector3 SanitizeSurfaceNormal(Vector3 surfaceNormal)
        {
            if (!FlowFieldGridSpace.IsFinite(surfaceNormal)
                || surfaceNormal.sqrMagnitude <= DIRECTION_EPSILON_SQR)
                return Vector3.up;

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
            Vector3 axis = surfaceNormal.sqrMagnitude > FlowFieldVectorUtility.DIRECTION_EPSILON_SQR
                ? surfaceNormal.normalized
                : Vector3.up;
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
