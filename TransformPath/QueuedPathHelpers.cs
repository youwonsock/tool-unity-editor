namespace Supercent.Common.TransformPath
{
    internal static class QueuedPathSpacingHelper
    {
        public static float GetEffectiveSpacing(QueuedPathFollower follower, QueuedPathManager manager, float managerDefaultSpacing)
        {
            if (follower != null && follower.UseManagerSpacing && manager != null)
                return managerDefaultSpacing;

            return follower != null ? follower.ActorSpacing : managerDefaultSpacing;
        }
    }

    internal static class QueuedPathBlockingHelper
    {
        public static bool ShouldStartBlocking(float distance, float spacing)
            => distance >= 0f && distance <= spacing;

        public static bool ShouldEndBlocking(float distance, float spacing, float hysteresis, float blockTimer)
            => blockTimer <= 0f && distance > spacing + hysteresis;
    }
}
