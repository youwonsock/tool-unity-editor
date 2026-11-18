using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Numeric checks and curve helpers shared by the runtime and the Editor.</summary>
    internal static class PathValueUtility
    {
        public static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        public static bool IsInRange(float value, float minimum, float maximum)
        {
            return IsFinite(value) && value >= minimum && value <= maximum;
        }

        public static bool IsNonNegativeFinite(float value)
        {
            return IsFinite(value) && value >= 0f;
        }

        public static void ThrowIfNotFinite(float value, string parameterName)
        {
            if (!IsFinite(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        public static void ThrowIfOutOfRange(float value, float minimum, float maximum, string parameterName)
        {
            if (!IsInRange(value, minimum, maximum))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        public static void ThrowIfNegative(float value, string parameterName)
        {
            if (!IsNonNegativeFinite(value))
                throw new ArgumentOutOfRangeException(parameterName);
        }

        public static void ThrowIfNotMoveValue(float value, string parameterName)
        {
            ThrowIfOutOfRange(value, PathConstants.MIN_MOVE_VALUE, PathConstants.MAX_MOVE_VALUE, parameterName);
        }

        public static AnimationCurve CloneCurve(AnimationCurve source)
        {
            if (source == null)
                return null;
            return new AnimationCurve(source.keys)
            {
                preWrapMode = source.preWrapMode,
                postWrapMode = source.postWrapMode,
            };
        }

        public static bool AreSameCurve(AnimationCurve left, AnimationCurve right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null
                || left.preWrapMode != right.preWrapMode
                || left.postWrapMode != right.postWrapMode
                || left.length != right.length)
                return false;
            for (int i = 0; i < left.length; i++)
            {
                Keyframe leftKey = left[i];
                Keyframe rightKey = right[i];
                if (!Mathf.Approximately(leftKey.time, rightKey.time)
                    || !Mathf.Approximately(leftKey.value, rightKey.value)
                    || !Mathf.Approximately(leftKey.inTangent, rightKey.inTangent)
                    || !Mathf.Approximately(leftKey.outTangent, rightKey.outTangent)
                    || !Mathf.Approximately(leftKey.inWeight, rightKey.inWeight)
                    || !Mathf.Approximately(leftKey.outWeight, rightKey.outWeight)
                    || leftKey.weightedMode != rightKey.weightedMode)
                    return false;
            }
            return true;
        }

        /// <summary>A curve with at least one key, for blends.</summary>
        public static bool IsUsableCurve(AnimationCurve curve)
        {
            return curve != null && curve.length > 0;
        }
    }
}
