using UnityEngine;

namespace Common.TransformPath.Samples
{
    /// <summary>
    /// A queue spacing policy that widens the gap right behind the leader and
    /// keeps every other member's own spacing. The sample's queue lane uses it.
    /// </summary>
    [CreateAssetMenu(menuName = "TransformPath/Samples/Gap Policy", fileName = "TransformPathGapPolicy")]
    public sealed class TransformPathGapPolicy : PathQueueSpacingPolicy
    {
        [Tooltip("The gap behind the leader, as a multiple of the spacing the member would keep anyway.")]
        [SerializeField, Min(1f)] private float _leaderGapScale = 2f;

        public float LeaderGapScale => _leaderGapScale;

        public override float GetSpacing(PathQueueMember member, PathQueueMember ahead, float resolvedSpacing)
        {
            return IsLeader(ahead) ? resolvedSpacing * _leaderGapScale : resolvedSpacing;
        }

        // The manager orders its line before it asks, so the first member is the leader.
        private static bool IsLeader(PathQueueMember member)
        {
            QueuedPathManager manager = member == null ? null : member.ActiveManager;
            return manager != null && manager.MemberCount > 0 && manager.GetMember(0) == member;
        }
    }
}
