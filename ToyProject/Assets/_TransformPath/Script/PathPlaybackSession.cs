using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>Mutable playback state owned by one PathFollower.</summary>
    internal sealed class PathPlaybackSession
    {
        public readonly IPathProvider Provider;
        public readonly IPathSequenceProvider Sequence;
        public PathSequenceSnapshot Snapshot;
        public readonly PathEventCursor EventCursor;
        public int ProviderRevision;
        public PathMovementSettings? ProviderMovementSettings;

        public PathPlaybackSession(
            IPathProvider provider,
            IPathSequenceProvider sequence,
            PathSequenceSnapshot snapshot)
        {
            Provider = provider;
            Sequence = sequence;
            Snapshot = snapshot;
            EventCursor = new PathEventCursor();
            ProviderRevision = provider.Revision;
        }
    }

    internal sealed class PathEventCursor
    {
        private const float PROGRESS_EPSILON = 0.00001f;

        public int NextIndex;

        public void Reset(IPathEventSource source, float progress)
        {
            NextIndex = 0;
            if (source == null)
                return;

            while (NextIndex < source.EventCount
                && source.GetEvent(NextIndex).NormalizedTime <= progress + PROGRESS_EPSILON)
                NextIndex++;
        }

        public bool HasNext(IPathEventSource source)
        {
            return source != null && NextIndex < source.EventCount;
        }
    }
}
