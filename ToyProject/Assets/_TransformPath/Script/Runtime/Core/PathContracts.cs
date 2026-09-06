using System;
using UnityEngine;
using UnityEngine.Serialization;

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
    public enum EPathCurveType
    {
        Linear = 0,
        SplineApproximating = 1,
        SplineInterpolating = 2,
    }

    [Serializable]
    public readonly struct PathBuildSettings
    {
        public EPathCurveType CurveType { get; }
        public int SegmentCount { get; }

        public PathBuildSettings(
            EPathCurveType curveType = EPathCurveType.Linear,
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

    internal enum EPathPlaybackKind
    {
        Single,
        Aggregate,
        Sequence,
    }

    /// <summary>
    /// Runtime-only description of one playback request. The request is not
    /// serialized; authoring data remains on the referenced provider.
    /// </summary>
    public readonly struct PathPlaybackRequest
    {
        private readonly IPathProvider _provider;
        private readonly bool _loop;
        private readonly EPathPlaybackKind _kind;
        private readonly PathMovementSettings _movementOverride;

        public IPathProvider Provider => _provider;
        public bool Loop => _loop;

        internal EPathPlaybackKind Kind => _kind;
        internal PathMovementSettings MovementOverride => _movementOverride;

        private PathPlaybackRequest(
            EPathPlaybackKind kind,
            IPathProvider provider,
            bool loop,
            PathMovementSettings movementOverride)
        {
            _kind = kind;
            _provider = provider;
            _loop = loop;
            _movementOverride = movementOverride;
        }

        public static PathPlaybackRequest Single(
            IPathMovementProvider provider,
            bool loop = false)
        {
            return new PathPlaybackRequest(
                EPathPlaybackKind.Single,
                provider,
                loop,
                default(PathMovementSettings));
        }

        public static PathPlaybackRequest Aggregate(
            IPathProvider provider,
            PathMovementSettings movement,
            bool loop = false)
        {
            return new PathPlaybackRequest(
                EPathPlaybackKind.Aggregate,
                provider,
                loop,
                movement);
        }

        public static PathPlaybackRequest Sequence(
            IPathSequenceProvider provider,
            bool loop = false)
        {
            return new PathPlaybackRequest(
                EPathPlaybackKind.Sequence,
                provider,
                loop,
                default(PathMovementSettings));
        }
    }

    /// <summary>Runtime descriptor used by sequence providers.</summary>
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

    public enum EPathSegmentMovementSource
    {
        Provider = 0,
        Override = 1,
    }

    /// <summary>
    /// Inspector-facing sequence segment. The provider object is intentionally
    /// stored as a Unity component while runtime playback consumes the
    /// interface-only descriptor below.
    /// </summary>
    [Serializable]
    public struct PathSegmentAuthoring
    {
        [SerializeField, FormerlySerializedAs("_pathData")]
        private MonoBehaviour _providerObject;
        [SerializeField] private EPathSegmentMovementSource _movementSource;
        [SerializeField] private EPathMoveType _moveType;
        [SerializeField] private float _moveValue;
        [SerializeField] private AnimationCurve _timeCurve;
        [SerializeField] private bool _preservePreviousSpeed;

        public MonoBehaviour ProviderObject => _providerObject;
        public IPathProvider Provider => _providerObject as IPathProvider;
        public EPathSegmentMovementSource MovementSource => _movementSource;
        public EPathMoveType MoveType => _moveType;
        public float MoveValue => _moveValue;
        public AnimationCurve TimeCurve => _timeCurve;
        public bool PreservePreviousSpeed => _preservePreviousSpeed;

        public bool TryResolveMovementSettings(
            out PathMovementSettings settings,
            out string error)
        {
            IPathProvider provider = Provider;
            if (provider == null)
            {
                settings = default(PathMovementSettings);
                error = "A provider component is required.";
                return false;
            }

            if (_movementSource == EPathSegmentMovementSource.Provider)
            {
                if (!(provider is IPathMovementProvider movementProvider))
                {
                    settings = default(PathMovementSettings);
                    error = "Provider movement mode requires IPathMovementProvider.";
                    return false;
                }

                settings = PathMovementSettingsUtility.Clone(
                    movementProvider.MovementSettings);
                error = null;
                return true;
            }

            settings = new PathMovementSettings(
                _moveType,
                _moveValue,
                _timeCurve);
            if (!PathMovementSettingsUtility.TryValidate(settings, out error))
                return false;
            settings = PathMovementSettingsUtility.Clone(settings);
            return true;
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

}
