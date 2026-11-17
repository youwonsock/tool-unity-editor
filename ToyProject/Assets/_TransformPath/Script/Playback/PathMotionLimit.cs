using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>What a queue currently allows one follower.</summary>
    internal readonly struct PathMotionLimit
    {
        public static readonly PathMotionLimit None = new PathMotionLimit(false, 1f, 1f, false);

        public readonly bool Blocked;
        public readonly float SpeedMultiplier;
        public readonly float MaxProgress;
        /// <summary>A queue member stops at a loop seam and waits for the queue before its next lap.</summary>
        public readonly bool HoldAtLoopSeam;

        public PathMotionLimit(bool blocked, float speedMultiplier, float maxProgress, bool holdAtLoopSeam)
        {
            Blocked = blocked;
            SpeedMultiplier = Mathf.Clamp01(speedMultiplier);
            MaxProgress = Mathf.Clamp01(maxProgress);
            HoldAtLoopSeam = holdAtLoopSeam;
        }
    }
}
