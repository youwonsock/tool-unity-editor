using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// How one path or segment moves: a Duration with a time curve, or a Speed.
    /// The time curve is copied once on construction and never handed out, so a
    /// value can be shared by every follower without defensive copies.
    /// </summary>
    public readonly struct PathMovementSettings
    {
        private readonly AnimationCurve _timeCurve;
        // The default curve and its copies map time to progress one to one, so
        // neither direction needs the curve.
        private readonly bool _linearTime;

        public EPathMoveType MoveType { get; }

        /// <summary>The Duration in seconds when TimeBased, the Speed in units per second when SpeedBased.</summary>
        public float Value { get; }

        public PathMovementSettings(
            EPathMoveType moveType,
            float value,
            AnimationCurve timeCurve = null)
        {
            MoveType = moveType;
            Value = value;
            // Unity loads an unset curve field as an empty curve. Both mean linear.
            _timeCurve = moveType != EPathMoveType.TimeBased
                ? null
                : timeCurve == null || timeCurve.length == 0
                    ? AnimationCurve.Linear(0f, 0f, 1f, 1f)
                    : PathValueUtility.CloneCurve(timeCurve);
            _linearTime = _timeCurve != null && IsIdentity(_timeCurve);
        }

        public static PathMovementSettings Time(float duration, AnimationCurve curve = null)
        {
            return new PathMovementSettings(EPathMoveType.TimeBased, duration, curve);
        }

        public static PathMovementSettings Speed(float speed)
        {
            return new PathMovementSettings(EPathMoveType.SpeedBased, speed);
        }

        /// <summary>A copy of the time curve, or null when SpeedBased.</summary>
        public AnimationCurve CopyTimeCurve() => PathValueUtility.CloneCurve(_timeCurve);

        /// <summary>Path progress after a raw time fraction of 0..1.</summary>
        internal float EvaluateProgress(float rawProgress)
        {
            float raw = Mathf.Clamp01(rawProgress);
            return Mathf.Clamp01(_timeCurve == null || _linearTime ? raw : _timeCurve.Evaluate(raw));
        }

        /// <summary>The raw time fraction at which the time curve reaches a progress.</summary>
        internal float InverseProgress(float progress)
        {
            float target = Mathf.Clamp01(progress);
            if (target <= 0f || target >= 1f || _timeCurve == null || _linearTime)
                return target;
            float low = 0f;
            float high = 1f;
            for (int i = 0; i < 32; i++)
            {
                float middle = (low + high) * 0.5f;
                if (Mathf.Clamp01(_timeCurve.Evaluate(middle)) >= target) high = middle;
                else low = middle;
            }
            return high;
        }

        // (0, 0) to (1, 1) with unit tangents and no weights: the Hermite curve is the line itself.
        private static bool IsIdentity(AnimationCurve curve)
        {
            if (curve.length != 2)
                return false;
            Keyframe start = curve[0];
            Keyframe end = curve[1];
            return start.time == 0f && start.value == 0f && end.time == 1f && end.value == 1f
                && Mathf.Approximately(start.outTangent, 1f) && Mathf.Approximately(end.inTangent, 1f)
                && (start.weightedMode & WeightedMode.Out) == 0
                && (end.weightedMode & WeightedMode.In) == 0;
        }

        internal bool IsSameAs(PathMovementSettings other)
        {
            if (MoveType != other.MoveType || !Mathf.Approximately(Value, other.Value))
                return false;
            return MoveType != EPathMoveType.TimeBased
                || PathValueUtility.AreSameCurve(_timeCurve, other._timeCurve);
        }

        internal bool TryValidate(out string error) => TryValidate(out error, out _);

        /// <summary>Throws ArgumentOutOfRangeException for the mode or the value, ArgumentException for the curve.</summary>
        internal void Validate(string parameterName)
        {
            if (TryValidate(out string error, out bool outOfRange))
                return;
            if (outOfRange)
                throw new ArgumentOutOfRangeException(parameterName, error);
            throw new ArgumentException(error, parameterName);
        }

        private bool TryValidate(out string error, out bool outOfRange)
        {
            outOfRange = true;
            if (!Enum.IsDefined(typeof(EPathMoveType), MoveType))
            {
                error = "MoveType is invalid.";
                return false;
            }
            if (!PathValueUtility.IsInRange(Value, PathConstants.MIN_MOVE_VALUE, PathConstants.MAX_MOVE_VALUE))
            {
                error = $"Movement value must be finite and within {PathConstants.MIN_MOVE_VALUE}..{PathConstants.MAX_MOVE_VALUE}.";
                return false;
            }
            outOfRange = false;
            if (MoveType == EPathMoveType.TimeBased)
                return TryValidateTimeCurve(_timeCurve, out error);
            error = null;
            return true;
        }

        /// <summary>A time curve must run from 0 to 1 without going back.</summary>
        internal static bool TryValidateTimeCurve(AnimationCurve curve, out string error)
        {
            if (curve == null || curve.length == 0)
            {
                error = "TimeCurve must contain at least one key.";
                return false;
            }
            for (int i = 0; i < curve.length; i++)
            {
                Keyframe key = curve[i];
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
    }
}
