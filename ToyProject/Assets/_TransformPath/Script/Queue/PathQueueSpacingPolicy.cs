using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>
    /// Decides the gap each member of a queue keeps behind the member ahead.
    /// Assign one to <see cref="QueuedPathManager.SpacingPolicy"/>; several
    /// managers may share it. The manager asks once per member every frame,
    /// leaders first, and uses the answer for the slowdown, the blocking and
    /// the overtake limit alike.
    /// </summary>
    public abstract class PathQueueSpacingPolicy : ScriptableObject
    {
        /// <summary>
        /// The gap to keep, in world units. Return a finite, non-negative
        /// value; anything else faults the manager's frame, and the manager
        /// releases every member as ManagerReleased, stopping its playback.
        /// Do not join, leave or restart queue members here: the manager is
        /// walking its line and refuses those calls.
        /// </summary>
        /// <param name="member">The member the gap is for.</param>
        /// <param name="ahead">The member ahead on the route, or null for the leader.</param>
        /// <param name="resolvedSpacing">
        /// The gap the queue would use without a policy: the manager's
        /// <see cref="QueuedPathManager.DefaultSpacing"/>, or the member's
        /// <see cref="PathQueueMember.ActorSpacing"/> when it does not use the manager's.
        /// </param>
        public abstract float GetSpacing(PathQueueMember member, PathQueueMember ahead, float resolvedSpacing);
    }
}
