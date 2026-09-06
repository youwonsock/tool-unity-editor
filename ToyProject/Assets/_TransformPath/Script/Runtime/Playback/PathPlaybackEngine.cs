namespace Common.TransformPath
{
    /// <summary>
    /// Owns the identity of one playback component's executions. The identity
    /// is deliberately independent from prepared path caches so an old
    /// callback or queue registration cannot address a later execution.
    /// </summary>
    internal sealed class PathPlaybackEngine
    {
        private static ulong _nextOwnerId;
        private readonly ulong _ownerId = ++_nextOwnerId;
        private ulong _nextPlaybackId;

        public ulong OwnerId => _ownerId;
        public ulong PlaybackId { get; private set; }
        public ulong StateRevision { get; private set; }

        public void BeginPlayback()
        {
            PlaybackId = ++_nextPlaybackId;
            Touch();
        }

        public void InvalidatePlayback()
        {
            PlaybackId = 0;
            Touch();
        }

        public void Touch()
        {
            StateRevision++;
        }

        public PathCommandReceipt CreateReceipt(bool changed)
        {
            return new PathCommandReceipt(
                _ownerId,
                PlaybackId,
                StateRevision,
                changed);
        }
    }
}
