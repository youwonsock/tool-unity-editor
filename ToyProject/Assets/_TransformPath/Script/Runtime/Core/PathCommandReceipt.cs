namespace Common.TransformPath
{
    public readonly struct PathCommandReceipt
    {
        public ulong PlaybackId { get; }
        public ulong StateRevision { get; }
        public bool Changed { get; }
        internal ulong OwnerId { get; }

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
