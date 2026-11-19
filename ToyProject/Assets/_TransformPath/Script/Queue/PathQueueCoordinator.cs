using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>What the manager computed for one member in a frame.</summary>
    public readonly struct PathQueueStatus
    {
        /// <summary>The member ahead on the route, or null for the leader.</summary>
        public PathQueueMember Ahead { get; }
        public float? DistanceToAhead { get; }
        /// <summary>The gap this member keeps behind the one ahead, after the manager's spacing policy.</summary>
        public float Spacing { get; }
        public bool IsBlocked { get; }
        public float SpeedMultiplier { get; }
        public float MaxProgress { get; }

        internal PathQueueStatus(PathQueueMember ahead, float? distanceToAhead, float spacing, bool isBlocked,
            float speedMultiplier, float maxProgress)
        {
            Ahead = ahead;
            DistanceToAhead = distanceToAhead;
            Spacing = spacing;
            IsBlocked = isBlocked;
            SpeedMultiplier = speedMultiplier;
            MaxProgress = maxProgress;
        }
    }

    /// <summary>Queue math without Unity objects.</summary>
    internal static class PathQueueCoordinator
    {
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
            if (distance.Value >= slowdownStartDistance || slowdownStartDistance <= spacing)
                return 1f;
            float t = Mathf.Clamp01((distance.Value - spacing) / (slowdownStartDistance - spacing));
            float curveValue = slowdownCurve == null ? t : Mathf.Clamp01(slowdownCurve.Evaluate(t));
            return Mathf.Lerp(minSpeedMultiplier, 1f, curveValue);
        }

        /// <summary>The furthest progress a member may reach behind the one ahead.</summary>
        public static float CalculateMaxProgress(bool overtakeProtection, float? aheadProgress, float spacing, float routeLength)
        {
            return !aheadProgress.HasValue || !overtakeProtection
                ? 1f
                : Mathf.Clamp01(aheadProgress.Value - spacing / routeLength);
        }
    }

    /// <summary>
    /// How one member eases the manager's result: a manual hold, a delay before
    /// it moves off again, extra distance before a blocked member restarts, and
    /// a smoothed speed multiplier.
    /// </summary>
    internal struct PathQueueWait
    {
        public bool ManualBlock;
        public bool IsBlocked;
        public float SpeedMultiplier;
        public float ResumeDelayRemaining;

        public static PathQueueWait Free => new PathQueueWait { SpeedMultiplier = 1f };

        public void Step(float deltaTime, PathQueueStatus status,
            float spacing, float resumeDelay, float hysteresis, float smoothRate)
        {
            float target = Mathf.Clamp01(status.SpeedMultiplier);
            if (ManualBlock || status.IsBlocked)
            {
                ResumeDelayRemaining = resumeDelay;
                IsBlocked = true;
                target = 0f;
            }
            else
            {
                ResumeDelayRemaining = Mathf.Max(0f, ResumeDelayRemaining - deltaTime);
                bool enoughDistance = !status.DistanceToAhead.HasValue
                    || status.DistanceToAhead.Value >= spacing + hysteresis;
                if (IsBlocked && (ResumeDelayRemaining > 0f || !enoughDistance))
                    target = 0f;
                else
                    IsBlocked = false;
            }

            smoothRate = Mathf.Max(0f, smoothRate);
            SpeedMultiplier = smoothRate <= 0f
                ? target
                : Mathf.Lerp(SpeedMultiplier, target, Mathf.Clamp01(deltaTime * smoothRate));
            if (Mathf.Abs(SpeedMultiplier - target) <= 0.0001f)
                SpeedMultiplier = target;
        }
    }
}
