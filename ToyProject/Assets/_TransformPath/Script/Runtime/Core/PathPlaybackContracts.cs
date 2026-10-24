using System;

namespace Common.TransformPath
{
    internal interface IPathPlaybackIdentity
    {
        ulong PlaybackOwnerId { get; }
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

    /// <summary>Playback commands with a shared read-only state contract.</summary>
    public interface IPathFollower : IPathPlaybackState
    {
        PathCommandReceipt Init();
        PathCommandReceipt Release();
        PathCommandReceipt StartPlayback(PathPlaybackRequest request);
        PathCommandReceipt StopMove();
        PathCommandReceipt PauseMove();
        PathCommandReceipt ResumeMove();
        PathCommandReceipt Seek(float normalizedTime);
        PathCommandReceipt SeekSegment(int segmentIndex, float localNormalizedTime);
        PathCommandReceipt SetSpeed(float speed);
        PathCommandReceipt SetDuration(float duration);
    }
}
