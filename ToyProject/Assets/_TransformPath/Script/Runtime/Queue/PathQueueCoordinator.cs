using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Pure queue math used by the Unity manager. It only consumes an agent's
    /// queue contract and route settings, so registration tests do not need a
    /// follower component.
    /// </summary>
    internal static class PathQueueCoordinator
    {
        public static float GetSpacing(PathQueueAgentSettings settings, float defaultSpacing)
        {
            return settings.UseManagerSpacing ? defaultSpacing : settings.ActorSpacing;
        }

        public static float CalculateSpeedMultiplier(
            bool enableGradualSlowdown,
            float? distance,
            float spacing,
            float slowdownStartDistance,
            float minSpeedMultiplier,
            AnimationCurve slowdownCurve)
        {
            if (!enableGradualSlowdown || !distance.HasValue)
                return 1f;
            if (distance.Value <= spacing)
                return 0f;
            if (distance.Value >= slowdownStartDistance
                || slowdownStartDistance <= spacing)
                return 1f;
            float t = Mathf.Clamp01(
                (distance.Value - spacing)
                / (slowdownStartDistance - spacing));
            float curveValue = slowdownCurve == null
                ? t
                : Mathf.Clamp01(slowdownCurve.Evaluate(t));
            return Mathf.Lerp(minSpeedMultiplier, 1f, curveValue);
        }
    }
}
