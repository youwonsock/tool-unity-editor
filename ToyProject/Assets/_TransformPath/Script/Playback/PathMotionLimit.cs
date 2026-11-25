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
        /// <summary>
        /// A jump (a loop seam, or a Seek) drops the rest of that frame's time and
        /// blocks, so the queue answers for the new position first.
        /// </summary>
        public readonly bool WaitOnDiscontinuity;

        public PathMotionLimit(bool blocked, float speedMultiplier, float maxProgress, bool waitOnDiscontinuity)
        {
            Blocked = blocked;
            SpeedMultiplier = Mathf.Clamp01(speedMultiplier);
            MaxProgress = Mathf.Clamp01(maxProgress);
            WaitOnDiscontinuity = waitOnDiscontinuity;
        }
    }
}
