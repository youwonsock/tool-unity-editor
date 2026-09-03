using System;
using UnityEngine;

namespace Common.TransformPath
{
    /// <summary>How a follower advances through a path.</summary>
    public enum EPathMoveType
    {
        TimeBased = 0,
        SpeedBased = 1,
    }

    public enum EPathFollowerState
    {
        Uninitialized = 0,
        Ready = 1,
        Moving = 2,
        Paused = 3,
        Completed = 4,
    }

    /// <summary>
    /// Runtime geometry build settings. Editor preview sampling is deliberately
    /// not part of the runtime path contract.
    /// </summary>
    [Serializable]
    public readonly struct PathBuildSettings
    {
        public PathData.ECurveType CurveType { get; }
        public int SegmentCount { get; }

        public PathBuildSettings(
            PathData.ECurveType curveType = PathData.ECurveType.Linear,
            int segmentCount = 500)
        {
            CurveType = curveType;
            SegmentCount = segmentCount;
        }
    }

    [Serializable]
    public readonly struct PathMovementSettings
    {
        public EPathMoveType MoveType { get; }
        public float Value { get; }
        public AnimationCurve TimeCurve { get; }

        public PathMovementSettings(
            EPathMoveType moveType = EPathMoveType.TimeBased,
            float value = 5f,
            AnimationCurve timeCurve = null)
        {
            MoveType = moveType;
            Value = value;
            TimeCurve = moveType == EPathMoveType.TimeBased
                ? timeCurve ?? AnimationCurve.Linear(0f, 0f, 1f, 1f)
                : timeCurve;
        }

        public static PathMovementSettings Time(float duration, AnimationCurve curve = null)
        {
            return new PathMovementSettings(EPathMoveType.TimeBased, duration, curve);
        }

        public static PathMovementSettings Speed(float speed)
        {
            return new PathMovementSettings(EPathMoveType.SpeedBased, speed, null);
        }
    }

    public readonly struct PathPlaybackSettings
    {
        public bool Loop { get; }

        public PathPlaybackSettings(bool loop = false)
        {
            Loop = loop;
        }
    }

    /// <summary>
    /// Serialized authoring value for one sequence segment.
    /// </summary>
    [Serializable]
    public struct PathSegmentConfig
    {
        [SerializeField] private PathData _pathData;
        [SerializeField] private bool _preservePreviousSpeed;

        public PathData PathData => _pathData;
        public bool PreservePreviousSpeed => _preservePreviousSpeed;

        public PathSegmentConfig(
            PathData pathData,
            bool preservePreviousSpeed = false)
        {
            _pathData = pathData;
            _preservePreviousSpeed = preservePreviousSpeed;
        }

        public PathSegmentConfig WithPathData(PathData pathData)
        {
            return new PathSegmentConfig(pathData, _preservePreviousSpeed);
        }
    }

    /// <summary>Legacy-free runtime descriptor used by sequence providers.</summary>
    public readonly struct PathSegmentDescriptor
    {
        public IPathProvider Provider { get; }
        public PathMovementSettings MovementSettings { get; }
        public bool PreservePreviousSpeed { get; }

        public PathSegmentDescriptor(
            IPathProvider provider,
            PathMovementSettings movementSettings,
            bool preservePreviousSpeed)
        {
            Provider = provider;
            MovementSettings = movementSettings;
            PreservePreviousSpeed = preservePreviousSpeed;
        }
    }

    public readonly struct PathQueueState
    {
        public IQueuedPathAgent Ahead { get; }
        public float? DistanceToAhead { get; }
        public bool IsBlocked { get; }
        public float SpeedMultiplier { get; }
        public float MaxGlobalNormalizedTime { get; }
        public int RouteRevision { get; }

        public PathQueueState(
            IQueuedPathAgent ahead,
            float? distanceToAhead,
            bool isBlocked,
            float speedMultiplier,
            float maxGlobalNormalizedTime,
            int routeRevision)
        {
            Ahead = ahead;
            DistanceToAhead = distanceToAhead;
            IsBlocked = isBlocked;
            SpeedMultiplier = speedMultiplier;
            MaxGlobalNormalizedTime = maxGlobalNormalizedTime;
            RouteRevision = routeRevision;
        }
    }

    public interface IPathProvider
    {
        bool IsInitialized { get; }
        bool IsReady { get; }
        int Revision { get; }
        float PathLength { get; }
        event Action PathChanged;

        Vector3 Sample(float normalizedTime);
        Vector3 SampleDistance(float distance);
    }

    public interface IPathMovementProvider : IPathProvider
    {
        PathMovementSettings MovementSettings { get; }
    }

    public interface IPathSequenceProvider : IPathProvider
    {
        int SegmentCount { get; }
        PathSegmentDescriptor GetSegment(int index);
        float GetSegmentStartDistance(int index);
        float GetSegmentLength(int index);
    }

    public interface IPathEventSource
    {
        int EventCount { get; }
        PathEventEntry GetEvent(int index);
    }

    /// <summary>
    /// The complete runtime follower contract. Single paths and sequences use
    /// the same start, seek, and lifecycle surface.
    /// </summary>
    public interface IPathFollower
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

        event Action<EPathFollowerState> StateChanged;
        event Action<int> SegmentChanged;
        event Action Completed;

        void Init();
        void Release();
        void StartMove(IPathMovementProvider provider, PathPlaybackSettings playback);
        void StartMove(IPathProvider provider, PathMovementSettings movementOverride, PathPlaybackSettings playback);
        void StartSequence(IPathSequenceProvider provider, PathPlaybackSettings playback);
        void StopMove();
        void PauseMove();
        void ResumeMove();
        void Seek(float normalizedTime);
        void SeekSegment(int segmentIndex, float localNormalizedTime);
    }

    public interface IQueuedPathAgent
    {
        IPathFollower PathFollower { get; }
        IPathProvider QueueProvider { get; }
        bool IsMoving { get; }
        float GlobalNormalizedTime { get; }
        int SnapshotRevision { get; }
        void ApplyQueueState(PathQueueState state);
    }

    public interface IPathEventReceiver
    {
        void ReceivePathEvent(string eventName, IPathFollower follower);
    }
}
