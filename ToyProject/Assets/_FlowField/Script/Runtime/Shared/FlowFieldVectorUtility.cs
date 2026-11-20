using UnityEngine;
using Unity.Mathematics;

namespace Common.FlowField
{
    internal static class FlowFieldVectorUtility
    {
        internal const float DirectionEpsilonSqr = 0.00000001f;

        /// <summary>A stored direction: zero, or of unit length within 1% of its square.</summary>
        internal static bool IsUnitOrZero(Vector3 value)
        {
            float sqr = value.sqrMagnitude;
            return sqr <= DirectionEpsilonSqr || Mathf.Abs(sqr - 1f) <= 0.01f;
        }

        /// <summary>
        /// Shared Surface2D/Volume3D rule: Modifiers apply only to open cells
        /// that move. Blocked, Goal anchor, unreachable and zero-direction
        /// cells keep their base vector (a zero base direction has speed 0).
        /// </summary>
        internal static bool CanApplyModifier(bool blocked, FlowFieldGoalFlags goalFlags, Vector3 baseDirection)
            => !blocked
                && (goalFlags & (FlowFieldGoalFlags.Anchor | FlowFieldGoalFlags.Unreachable)) == 0
                && baseDirection.sqrMagnitude > DirectionEpsilonSqr;

        internal static bool IsMissingModifier(IFlowFieldVectorModifier modifier)
            => modifier == null
                || modifier is UnityEngine.Object unityObject && unityObject == null;

        /// <summary>
        /// Applies one Modifier to one cell, the same in both space modes: the
        /// output must be finite, have a speed of zero or more and a unit (or
        /// zero) direction. On a surface the direction must also lie in it;
        /// Volume3D cells pass a zero normal.
        /// </summary>
        internal static FlowFieldVectorState ApplyModifier(
            IFlowFieldModifierSnapshot modifier,
            in FlowFieldVectorState current,
            in FlowFieldVectorModifierContext context)
        {
            FlowFieldVectorState candidate = modifier.Modify(in current, in context);
            ValidateModifierOutput(in candidate, context.SurfaceNormal);
            return candidate;
        }

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
            bool onSurface = surfaceNormal != Vector3.zero;
            Vector3 normal = onSurface ? ValidateSurfaceNormal(surfaceNormal) : Vector3.zero;
            if (candidate.Direction.sqrMagnitude <= DirectionEpsilonSqr)
                return;

            float directionLength = candidate.Direction.magnitude;
            if (Mathf.Abs(directionLength - 1f) > 0.001f)
                throw new System.ArgumentException("Modifier direction must be normalized.", nameof(candidate));
            if (onSurface && Mathf.Abs(Vector3.Dot(candidate.Direction, normal)) > 0.001f)
                throw new System.ArgumentException("Modifier direction must be tangent to the surface.", nameof(candidate));
        }

        public static Vector3 ProjectDefaultOnSurface(Vector3 direction, Vector3 surfaceNormal)
            => (Vector3)ProjectOnSurface((float3)direction, (float3)surfaceNormal);

        internal static float3 ProjectOnSurface(float3 direction, float3 surfaceNormal)
        {
            float normalLengthSq = math.lengthsq(surfaceNormal);
            if (normalLengthSq <= DirectionEpsilonSqr)
                return float3.zero;
            float3 normal = math.normalize(surfaceNormal);
            float3 projected = direction - normal * math.dot(direction, normal);
            float directionLengthSq = math.lengthsq(projected);
            return directionLengthSq > DirectionEpsilonSqr
                ? math.normalize(projected)
                : float3.zero;
        }

        // Two Default Directions closer than this are the same direction.
        private const float DefaultDirectionEpsilon = 0.0001f;

        /// <summary>
        /// The one Default Direction normalizer, in both space modes: a finite,
        /// non-zero direction becomes a unit vector; anything else throws.
        /// </summary>
        public static Vector3 NormalizeDefaultDirection(Vector3 direction)
        {
            if (!TryNormalizeDefaultDirection(direction, out Vector3 normalized))
                throw new System.ArgumentOutOfRangeException(nameof(direction));
            return normalized;
        }

        /// <summary><see cref="NormalizeDefaultDirection"/> for input that may be invalid.</summary>
        internal static bool TryNormalizeDefaultDirection(Vector3 direction, out Vector3 normalized)
        {
            if (!FlowFieldGridSpace.IsFinite(direction) || direction.sqrMagnitude <= DirectionEpsilonSqr)
            {
                normalized = Vector3.zero;
                return false;
            }
            normalized = direction.normalized;
            return true;
        }

        /// <summary>
        /// The one Default Direction comparison. It compares the two values as
        /// given, so pass both as configured or both normalized.
        /// </summary>
        internal static bool SameDefaultDirection(Vector3 left, Vector3 right)
            => (left - right).sqrMagnitude <= DefaultDirectionEpsilon * DefaultDirectionEpsilon;

        internal static Vector3 ValidateSurfaceNormal(Vector3 surfaceNormal)
        {
            if (!FlowFieldGridSpace.IsFinite(surfaceNormal)
                || surfaceNormal.sqrMagnitude <= DirectionEpsilonSqr)
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
            if (direction.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr || maxAngleDegrees <= 0f)
                return direction;

            ResolveSeedOffsets(seed, out float seedX, out float seedZ);
            float noise = Mathf.PerlinNoise(
                cellCenter.x * spatialFrequency + seedX,
                cellCenter.z * spatialFrequency + seedZ);
            float angle = (noise * 2f - 1f) * maxAngleDegrees;
            if (surfaceNormal.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr)
                throw new System.ArgumentOutOfRangeException(nameof(surfaceNormal));
            Vector3 axis = surfaceNormal.normalized;
            return Quaternion.AngleAxis(angle, axis) * direction;
        }

        public static Vector3 ApplyStaticRotation3D(
            Vector3 direction,
            Vector3 cellCenter,
            float maxAngleDegrees,
            float spatialFrequency,
            int seed)
        {
            if (direction.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr || maxAngleDegrees <= 0f)
                return direction;

            ResolveSeedOffsets(seed, out float seedX, out float seedZ);
            float noise = Mathf.PerlinNoise(
                cellCenter.x * spatialFrequency + seedX + cellCenter.y * 0.173f,
                cellCenter.z * spatialFrequency + seedZ + cellCenter.y * 0.317f);
            float angle = (noise * 2f - 1f) * maxAngleDegrees;
            Vector3 axis = new Vector3(
                Mathf.Sin((seed * 0.017f) + cellCenter.y),
                Mathf.Cos((seed * 0.031f) + cellCenter.x),
                Mathf.Sin((seed * 0.047f) + cellCenter.z));
            if (axis.sqrMagnitude <= FlowFieldVectorUtility.DirectionEpsilonSqr)
                axis = Vector3.up;
            return Quaternion.AngleAxis(angle, axis.normalized) * direction;
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
