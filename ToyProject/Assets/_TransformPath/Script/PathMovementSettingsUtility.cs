using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Movement settings validation, cloning, and content comparison.</summary>
    internal static class PathMovementSettingsUtility
    {
        public const float MIN_VALUE = 0.001f;
        public const float MAX_VALUE = 9999f;

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

        public static PathMovementSettings Clone(PathMovementSettings source)
        {
            return new PathMovementSettings(
                source.MoveType,
                source.Value,
                CloneCurve(source.TimeCurve));
        }

        public static bool AreSame(PathMovementSettings left, PathMovementSettings right)
        {
            if (left.MoveType != right.MoveType
                || !Mathf.Approximately(left.Value, right.Value))
                return false;
            if (left.MoveType != EPathMoveType.TimeBased)
                return true;
            return AreSameCurve(left.TimeCurve, right.TimeCurve);
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

            Keyframe[] leftKeys = left.keys;
            Keyframe[] rightKeys = right.keys;
            for (int i = 0; i < leftKeys.Length; i++)
            {
                Keyframe leftKey = leftKeys[i];
                Keyframe rightKey = rightKeys[i];
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

        public static bool TryValidate(PathMovementSettings settings, out string error)
        {
            if (!Enum.IsDefined(typeof(EPathMoveType), settings.MoveType))
            {
                error = "MoveType is invalid.";
                return false;
            }

            if (!PathValueUtility.IsInRange(settings.Value, MIN_VALUE, MAX_VALUE))
            {
                error = "Movement value must be finite and within 0.001..9999.";
                return false;
            }

            if (settings.MoveType == EPathMoveType.TimeBased
                && !TryValidateCurve(settings.TimeCurve, out error))
                return false;

            error = null;
            return true;
        }

        public static bool TryValidateCurve(AnimationCurve curve, out string error)
        {
            if (curve == null || curve.length == 0)
            {
                error = "TimeCurve must contain at least one key.";
                return false;
            }

            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                Keyframe key = keys[i];
                if (!PathValueUtility.IsFinite(key.time)
                    || !PathValueUtility.IsFinite(key.value)
                    || !PathValueUtility.IsFinite(key.inTangent)
                    || !PathValueUtility.IsFinite(key.outTangent)
                    || !PathValueUtility.IsFinite(key.inWeight)
                    || !PathValueUtility.IsFinite(key.outWeight))
                {
                    error = "TimeCurve keys must be finite.";
                    return false;
                }
            }

            float previous = curve.Evaluate(0f);
            if (!PathValueUtility.IsFinite(previous) || Mathf.Abs(previous) > 0.001f)
            {
                error = "TimeCurve must start at 0.";
                return false;
            }

            for (int i = 1; i <= 64; i++)
            {
                float value = curve.Evaluate(i / 64f);
                if (!PathValueUtility.IsFinite(value) || value + 0.001f < previous)
                {
                    error = "TimeCurve must be finite and non-decreasing.";
                    return false;
                }
                previous = value;
            }

            if (Mathf.Abs(curve.Evaluate(1f) - 1f) > 0.001f)
            {
                error = "TimeCurve must end at 1.";
                return false;
            }

            error = null;
            return true;
        }

        public static void Validate(PathMovementSettings settings, string parameterName)
        {
            if (TryValidate(settings, out string error))
                return;

            if (!Enum.IsDefined(typeof(EPathMoveType), settings.MoveType)
                || !PathValueUtility.IsInRange(settings.Value, MIN_VALUE, MAX_VALUE))
                throw new ArgumentOutOfRangeException(parameterName, settings, error);
            throw new ArgumentException(error, parameterName);
        }
    }
}
