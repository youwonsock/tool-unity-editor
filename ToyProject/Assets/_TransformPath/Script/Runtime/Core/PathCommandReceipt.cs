namespace Common.TransformPath
{
    public readonly struct PathCommandReceipt
    {
        public ulong PlaybackId { get; }
        public ulong StateRevision { get; }
        public bool Changed { get; }
        internal ulong OwnerId { get; }

        public PathCommandReceipt(
            ulong playbackId,
            ulong stateRevision,
            bool changed)
            : this(0UL, playbackId, stateRevision, changed)
        {
        }

        internal PathCommandReceipt(
            ulong ownerId,
            ulong playbackId,
            ulong stateRevision,
            bool changed)
        {
            OwnerId = ownerId;
            PlaybackId = playbackId;
            StateRevision = stateRevision;
            Changed = changed;
        }
    }

}
