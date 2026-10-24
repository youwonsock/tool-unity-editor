using UnityEngine;

namespace Common.TransformPath
{
    internal readonly struct PathProgressStep
    {
        public float Position { get; }
        public float Progress { get; }
        public float ConsumedTime { get; }

        public PathProgressStep(float position, float progress, float consumedTime)
        {
            Position = position;
            Progress = progress;
            ConsumedTime = consumedTime;
        }
    }

    /// <summary>
    /// Component-free segment math shared by Single/Aggregate and Sequence.
    /// The caller owns boundary policy and dispatches callbacks between steps.
    /// </summary>
    internal static class PathProgressCalculator
    {
        private const float EPSILON = 0.00001f;

        public static PathProgressStep AdvanceTime(float elapsed, float duration,
            AnimationCurve curve, float availableTime, float multiplier, float maxProgress)
        {
            duration = Mathf.Max(duration, PathMovementSettingsUtility.MIN_VALUE);
            if (multiplier <= EPSILON)
                return new PathProgressStep(elapsed, EvaluateTimeProgress(elapsed / duration, curve), availableTime);
            maxProgress = Mathf.Clamp01(maxProgress);
            float maximumElapsed = InverseTimeProgress(maxProgress, curve) * duration;
            float targetElapsed = Mathf.Min(elapsed + availableTime * multiplier, maximumElapsed);
            float consumed = Mathf.Min(availableTime, Mathf.Max(0f, targetElapsed - elapsed) / multiplier);
            float next = elapsed + consumed * multiplier;
            return new PathProgressStep(next,
                Mathf.Min(maxProgress, EvaluateTimeProgress(next / duration, curve)), consumed);
        }

        public static PathProgressStep AdvanceDistance(float distance, float length,
            float speed, float availableTime, float multiplier, float maxProgress)
        {
            length = Mathf.Max(length, PathMovementSettingsUtility.MIN_VALUE);
            if (multiplier <= EPSILON)
                return new PathProgressStep(distance, Mathf.Clamp01(distance / length), availableTime);
            float effectiveSpeed = Mathf.Max(speed, PathMovementSettingsUtility.MIN_VALUE) * multiplier;
            float remaining = Mathf.Max(0f, length - distance);
            float consumed = Mathf.Min(availableTime,
                remaining / Mathf.Max(effectiveSpeed, PathMovementSettingsUtility.MIN_VALUE));
            float maximumDistance = Mathf.Clamp01(maxProgress) * length;
            consumed = Mathf.Min(consumed,
                Mathf.Max(0f, maximumDistance - distance) / Mathf.Max(effectiveSpeed, EPSILON));
            float next = distance + consumed * effectiveSpeed;
            return new PathProgressStep(next,
                Mathf.Min(Mathf.Clamp01(maxProgress), Mathf.Clamp01(next / length)), consumed);
        }

        public static float EvaluateTimeProgress(float rawProgress, AnimationCurve curve)
        {
            return Mathf.Clamp01(curve == null
                ? rawProgress
                : curve.Evaluate(Mathf.Clamp01(rawProgress)));
        }

        public static float InverseTimeProgress(float progress, AnimationCurve curve)
        {
            float target = Mathf.Clamp01(progress);
            if (target <= 0f || target >= 1f || curve == null)
                return target;
            float low = 0f;
            float high = 1f;
            for (int i = 0; i < 32; i++)
            {
                float middle = (low + high) * 0.5f;
                float value = Mathf.Clamp01(curve.Evaluate(middle));
                if (value >= target) high = middle;
                else low = middle;
            }
            return high;
        }
    }
}
