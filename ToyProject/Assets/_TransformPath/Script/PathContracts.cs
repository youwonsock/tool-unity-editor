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

    internal static class PathMovementSettingsUtility
    {
        internal const float MinValue = 0.001f;
        internal const float MaxValue = 9999f;

        internal static AnimationCurve CloneCurve(AnimationCurve source)
        {
            if (source == null)
                return null;

            AnimationCurve clone = new AnimationCurve(source.keys)
            {
                preWrapMode = source.preWrapMode,
                postWrapMode = source.postWrapMode,
            };
            return clone;
        }

        internal static PathMovementSettings Clone(PathMovementSettings source)
        {
            return new PathMovementSettings(
                source.MoveType,
                source.Value,
                CloneCurve(source.TimeCurve));
        }

        internal static bool AreSame(PathMovementSettings left, PathMovementSettings right)
        {
            if (left.MoveType != right.MoveType
                || !Mathf.Approximately(left.Value, right.Value))
                return false;
            if (left.MoveType != EPathMoveType.TimeBased)
                return true;
            return AreSameCurve(left.TimeCurve, right.TimeCurve);
        }

        internal static bool AreSameCurve(AnimationCurve left, AnimationCurve right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null
                || left.preWrapMode != right.preWrapMode
                || left.postWrapMode != right.postWrapMode
                || left.length != right.length)
                return false;

            Keyframe[] leftKeys = left.keys;
            Keyframe[] rightKeys = right.keys;
            for (int i = 0; i < leftKeys.Length; i++)
            {
                Keyframe a = leftKeys[i];
                Keyframe b = rightKeys[i];
                if (!Mathf.Approximately(a.time, b.time)
                    || !Mathf.Approximately(a.value, b.value)
                    || !Mathf.Approximately(a.inTangent, b.inTangent)
                    || !Mathf.Approximately(a.outTangent, b.outTangent)
                    || !Mathf.Approximately(a.inWeight, b.inWeight)
                    || !Mathf.Approximately(a.outWeight, b.outWeight)
                    || a.weightedMode != b.weightedMode)
                    return false;
            }
            return true;
        }

        internal static void Validate(PathMovementSettings settings, string parameterName)
        {
            if (!Enum.IsDefined(typeof(EPathMoveType), settings.MoveType))
                throw new ArgumentOutOfRangeException(parameterName, "MoveType is invalid.");
            if (!IsFinite(settings.Value)
                || settings.Value < MinValue
                || settings.Value > MaxValue)
                throw new ArgumentOutOfRangeException(parameterName, "Movement value must be finite and within 0.001..9999.");
            if (settings.MoveType == EPathMoveType.TimeBased)
                ValidateCurve(settings.TimeCurve, parameterName);
        }

        internal static void ValidateCurve(AnimationCurve curve, string parameterName)
        {
            if (curve == null || curve.length == 0)
                throw new ArgumentException("TimeCurve must contain at least one key.", parameterName);

            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                if (!IsFinite(keys[i].time)
                    || !IsFinite(keys[i].value)
                    || !IsFinite(keys[i].inTangent)
                    || !IsFinite(keys[i].outTangent)
                    || !IsFinite(keys[i].inWeight)
                    || !IsFinite(keys[i].outWeight))
                    throw new ArgumentException("TimeCurve keys must be finite.", parameterName);
            }

            float previous = curve.Evaluate(0f);
            if (!IsFinite(previous) || Mathf.Abs(previous) > 0.001f)
                throw new ArgumentException("TimeCurve must start at 0.", parameterName);
            for (int i = 1; i <= 64; i++)
            {
                float value = curve.Evaluate(i / 64f);
                if (!IsFinite(value) || value + 0.001f < previous)
                    throw new ArgumentException("TimeCurve must be finite and non-decreasing.", parameterName);
                previous = value;
            }
            if (Mathf.Abs(curve.Evaluate(1f) - 1f) > 0.001f)
                throw new ArgumentException("TimeCurve must end at 1.", parameterName);
        }

        internal static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
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
