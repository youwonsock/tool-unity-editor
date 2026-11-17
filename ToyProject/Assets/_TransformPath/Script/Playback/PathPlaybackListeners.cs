namespace Common.TransformPath
{
    /// <summary>The listeners a playback notifies. They run after the operation, through the boundary.</summary>
    internal sealed class PathPlaybackListeners
    {
        public readonly PathSignal<EPathFollowerState> StateChanged = new PathSignal<EPathFollowerState>();
        public readonly PathSignal<int> SegmentChanged = new PathSignal<int>();
        public readonly PathSignal Completed = new PathSignal();
        /// <summary>The follower's IPathEventReceiver, heard before <see cref="EventReached"/>.</summary>
        public readonly PathSignal<string, PathFollower> Receiver = new PathSignal<string, PathFollower>();
        public readonly PathSignal<string> EventReached = new PathSignal<string>();
        public PathFollower Owner;
    }
}
