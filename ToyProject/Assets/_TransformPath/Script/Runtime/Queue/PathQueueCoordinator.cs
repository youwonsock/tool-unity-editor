using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Pure queue math used by the Unity manager. It only consumes an agent's
    /// queue contract and route settings, so registration tests do not need a
    /// follower component.
    /// </summary>
    internal sealed class PathQueueCoordinator
    {
        public float DefaultSpacing { get; set; }
        public bool EnableGradualSlowdown { get; set; }
        public float SlowdownStartDistance { get; set; }
        public float MinSpeedMultiplier { get; set; }
        public AnimationCurve SlowdownCurve { get; set; }

        public PathQueueCoordinator(
            float defaultSpacing,
            bool enableGradualSlowdown,
            float slowdownStartDistance,
            float minSpeedMultiplier,
            AnimationCurve slowdownCurve)
        {
            DefaultSpacing = defaultSpacing;
            EnableGradualSlowdown = enableGradualSlowdown;
            SlowdownStartDistance = slowdownStartDistance;
            MinSpeedMultiplier = minSpeedMultiplier;
            SlowdownCurve = slowdownCurve;
        }

        public float GetSpacing(IQueuedPathAgent agent)
        {
            return agent.QueueSettings.UseManagerSpacing
                ? DefaultSpacing
                : agent.QueueSettings.ActorSpacing;
        }

        public float CalculateSpeedMultiplier(
            IQueuedPathAgent agent,
            float? distance,
            float spacing)
        {
            if (!EnableGradualSlowdown
                || !agent.QueueSettings.EnableGradualSlowdown
                || !distance.HasValue)
                return 1f;
            if (distance.Value <= spacing)
                return 0f;
            if (distance.Value >= SlowdownStartDistance
                || SlowdownStartDistance <= spacing)
                return 1f;
            float t = Mathf.Clamp01(
                (distance.Value - spacing)
                / (SlowdownStartDistance - spacing));
            float curveValue = SlowdownCurve == null
                ? t
                : Mathf.Clamp01(SlowdownCurve.Evaluate(t));
            return Mathf.Lerp(MinSpeedMultiplier, 1f, curveValue);
        }
    }
}
