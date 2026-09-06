using System;

namespace Common.TransformPath
{
    internal interface IPathPlaybackIdentity
    {
        ulong PlaybackOwnerId { get; }
    }

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

    public interface IPathPlaybackState
    {
        bool IsInitialized { get; }
        IPathProvider CurrentProvider { get; }
        IPathSequenceProvider CurrentSequence { get; }
        EPathFollowerState State { get; }
        bool IsMoving { get; }
        float NormalizedTime { get; }
        float GlobalNormalizedTime { get; }
        int CurrentSegmentIndex { get; }
        EPathMoveType MoveType { get; }
        float Speed { get; }
        float Duration { get; }
        ulong PlaybackId { get; }
        ulong StateRevision { get; }
        int SnapshotRevision { get; }

        event Action<EPathFollowerState> StateChanged;
        event Action<int> SegmentChanged;
        event Action Completed;
    }

    public interface IPathPlaybackControl
    {
        PathCommandReceipt StartPlayback(PathPlaybackRequest request);
        PathCommandReceipt StopMove();
        PathCommandReceipt PauseMove();
        PathCommandReceipt ResumeMove();
        PathCommandReceipt Seek(float normalizedTime);
        PathCommandReceipt SeekSegment(int segmentIndex, float localNormalizedTime);
    }

    public interface IPathMovementControl
    {
        PathCommandReceipt SetSpeed(float speed);
        PathCommandReceipt SetDuration(float duration);
    }

    public interface IPathLifecycle
    {
        PathCommandReceipt Init();
        PathCommandReceipt Release();
    }

    public interface IPathFollower :
        IPathPlaybackState,
        IPathPlaybackControl,
        IPathMovementControl,
        IPathLifecycle
    {
    }
}
