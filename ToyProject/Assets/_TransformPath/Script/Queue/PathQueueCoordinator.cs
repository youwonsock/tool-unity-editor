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
        /// <summary>At its gap behind the member ahead, or closer, judged as the progress cap is.</summary>
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
        // A few float steps of progress near 1. A member capped at the gap lands
        // exactly on the cap, and the progress it reads back can be that far off.
        private const float GAP_PROGRESS_TOLERANCE = 1e-6f;

        public static float CalculateSpeedMultiplier(
            bool enableGradualSlowdown,
            float? distance,
            float spacing,
            float slowdownRange,
            float minSpeedMultiplier,
            AnimationCurve slowdownCurve)
        {
            if (!enableGradualSlowdown || !distance.HasValue)
                return 1f;
            if (distance.Value <= spacing)
                return 0f;
            // The slowdown starts a range beyond the spacing, so a policy that
            // widens the spacing moves the slowdown with it.
            if (slowdownRange <= 0f || distance.Value >= spacing + slowdownRange)
                return 1f;
            float t = Mathf.Clamp01((distance.Value - spacing) / slowdownRange);
            // An empty curve, as Unity loads an unset one, slows down linearly.
            float curveValue = PathValueUtility.IsUsableCurve(slowdownCurve) ? Mathf.Clamp01(slowdownCurve.Evaluate(t)) : t;
            return Mathf.Lerp(minSpeedMultiplier, 1f, curveValue);
        }

        /// <summary>
        /// Whether a member is at its gap behind the one ahead, or closer. It is
        /// judged in progress, as <see cref="CalculateMaxProgress"/> sets the cap:
        /// a member stopped on the cap would otherwise read a distance a hair past
        /// the spacing on some route lengths and never count as blocked.
        /// </summary>
        public static bool IsAtGap(float progress, float aheadProgress, float spacing, float routeLength)
        {
            return aheadProgress - progress <= spacing / routeLength + GAP_PROGRESS_TOLERANCE;
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
