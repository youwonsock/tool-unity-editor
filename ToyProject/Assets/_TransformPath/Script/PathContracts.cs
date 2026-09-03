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
    public readonly struct PathMoveSettings
    {
        public EPathMoveType MoveType { get; }
        public float Value { get; }
        public AnimationCurve TimeCurve { get; }
        public bool Loop { get; }
        public bool PreserveSpeedBetweenSegments { get; }

        public PathMoveSettings(
            EPathMoveType moveType = EPathMoveType.TimeBased,
            float value = 1f,
            AnimationCurve timeCurve = null,
            bool loop = false,
            bool preserveSpeedBetweenSegments = false)
        {
            MoveType = moveType;
            Value = value;
            TimeCurve = timeCurve ?? AnimationCurve.Linear(0f, 0f, 1f, 1f);
            Loop = loop;
            PreserveSpeedBetweenSegments = preserveSpeedBetweenSegments;
        }

        public static PathMoveSettings Time(float duration, AnimationCurve curve = null, bool loop = false)
        {
            return new PathMoveSettings(EPathMoveType.TimeBased, duration, curve, loop);
        }

        public static PathMoveSettings Speed(float speed, AnimationCurve curve = null, bool loop = false)
        {
            return new PathMoveSettings(EPathMoveType.SpeedBased, speed, curve, loop);
        }
    }

    public readonly struct PathSequenceSettings
    {
        public bool Loop { get; }
        public bool PreserveSpeedBetweenSegments { get; }

        public PathSequenceSettings(bool loop = false, bool preserveSpeedBetweenSegments = false)
        {
            Loop = loop;
            PreserveSpeedBetweenSegments = preserveSpeedBetweenSegments;
        }
    }

    /// <summary>
    /// Serialized authoring value for one sequence segment.
    /// </summary>
    [Serializable]
    public struct PathSegmentConfig
    {
        [SerializeField] private PathData _pathData;
        [SerializeField] private EPathMoveType _moveType;
        [SerializeField] private float _value;
        [SerializeField] private AnimationCurve _timeCurve;

        public PathData PathData => _pathData;
        public EPathMoveType MoveType => _moveType;
        public float Value => _value;
        public AnimationCurve TimeCurve => _timeCurve;

        public PathSegmentConfig(
            PathData pathData,
            EPathMoveType moveType = EPathMoveType.TimeBased,
            float value = 1f,
            AnimationCurve timeCurve = null)
        {
            _pathData = pathData;
            _moveType = moveType;
            _value = value;
            _timeCurve = timeCurve ?? AnimationCurve.Linear(0f, 0f, 1f, 1f);
        }

        public PathSegmentConfig WithPathData(PathData pathData)
        {
            return new PathSegmentConfig(pathData, _moveType, _value, _timeCurve);
        }
    }

    /// <summary>Legacy-free runtime descriptor used by sequence providers.</summary>
    public readonly struct PathSegmentDescriptor
    {
        public IPathProvider Provider { get; }
        public EPathMoveType MoveType { get; }
        public float Value { get; }
        public AnimationCurve TimeCurve { get; }

        public PathSegmentDescriptor(
            IPathProvider provider,
            EPathMoveType moveType,
            float value,
            AnimationCurve timeCurve)
        {
            Provider = provider;
            MoveType = moveType;
            Value = value;
            TimeCurve = timeCurve;
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
        void StartMove(IPathProvider provider, PathMoveSettings settings);
        void StartSequence(IPathSequenceProvider provider, PathSequenceSettings settings);
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
