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
    /// How far one segment advances in a slice of time, by Duration or by Speed,
    /// without passing a progress cap. The caller handles segment ends.
    /// </summary>
    internal static class PathProgressCalculator
    {
        private const float EPSILON = PathConstants.EPSILON;

        public static PathProgressStep AdvanceTime(float elapsed, float duration,
            in PathMovementSettings movement, float availableTime, float multiplier, float maxProgress)
        {
            duration = Mathf.Max(duration, PathConstants.MIN_MOVE_VALUE);
            if (multiplier <= EPSILON)
                return new PathProgressStep(elapsed, movement.EvaluateProgress(elapsed / duration), availableTime);
            maxProgress = Mathf.Clamp01(maxProgress);
            float maximumElapsed = movement.InverseProgress(maxProgress) * duration;
            float targetElapsed = Mathf.Min(elapsed + availableTime * multiplier, maximumElapsed);
            float consumed = Mathf.Min(availableTime, Mathf.Max(0f, targetElapsed - elapsed) / multiplier);
            float next = elapsed + consumed * multiplier;
            return new PathProgressStep(next,
                Mathf.Min(maxProgress, movement.EvaluateProgress(next / duration)), consumed);
        }

        public static PathProgressStep AdvanceDistance(float distance, float length,
            float speed, float availableTime, float multiplier, float maxProgress)
        {
            length = Mathf.Max(length, PathConstants.MIN_MOVE_VALUE);
            if (multiplier <= EPSILON)
                return new PathProgressStep(distance, Mathf.Clamp01(distance / length), availableTime);
            float effectiveSpeed = Mathf.Max(speed, PathConstants.MIN_MOVE_VALUE) * multiplier;
            float remaining = Mathf.Max(0f, length - distance);
            float consumed = Mathf.Min(availableTime,
                remaining / Mathf.Max(effectiveSpeed, PathConstants.MIN_MOVE_VALUE));
            float maximumDistance = Mathf.Clamp01(maxProgress) * length;
            consumed = Mathf.Min(consumed,
                Mathf.Max(0f, maximumDistance - distance) / Mathf.Max(effectiveSpeed, EPSILON));
            float next = distance + consumed * effectiveSpeed;
            return new PathProgressStep(next,
                Mathf.Min(Mathf.Clamp01(maxProgress), Mathf.Clamp01(next / length)), consumed);
        }
    }
}
